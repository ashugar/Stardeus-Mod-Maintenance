using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.QuantumStorage.Relays
{
    internal sealed class RelayGroup
    {
        internal readonly List<StorageRelayNodeComp> Nodes = new List<StorageRelayNodeComp>();
        internal bool Dirty = true, NeedsBalance = true;
        internal Dictionary<MatType, int> Contents = new Dictionary<MatType, int>();
        internal int Used, Capacity;
        internal bool IsObject;
        internal List<Game.Systems.Items.ObjData> Objects = new List<Game.Systems.Items.ObjData>();
    }

    public sealed partial class StorageRelaySys : GameSystem, ISaveable
    {
        public const string SysId = "StorageRelaySys";
        internal const string PatchId = "jleas.quantumstorage.relays";
        private readonly Dictionary<MatStorageComp, StorageRelayNodeComp> nodes = new Dictionary<MatStorageComp, StorageRelayNodeComp>();
        private readonly Dictionary<MatStorageComp, RelayGroup> groups = new Dictionary<MatStorageComp, RelayGroup>();
        private List<RelayVertex> lastVertices = new List<RelayVertex>();
        private long lastTick = long.MinValue;
        private bool ready, rebuilding;
        private Harmony harmony;
        private int balanceCursor;
        internal bool Operational { get; private set; }
        internal bool HasNodes => nodes.Count > 0;
        public override string Id => SysId;

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register() => GameSystems.Register(SysId, () => new StorageRelaySys());

        protected override void OnInitialize()
        {
            // Separate patch transaction: a Relay compatibility failure cannot
            // unpatch the established Quantum material/object save recovery.
            harmony = new Harmony(PatchId);
            try
            {
                RelayCore.Install(harmony);
                RelayObjectCore.Install(harmony);
                Operational = true;
                S.Sig.AfterLoadState.AddListener(AfterLoad);
                D.Log("Quantum Storage: ten Relay types initialized; shared 12-tile topology, fixed local capacity, native material and object save records.");
            }
            catch (Exception ex)
            {
                harmony.UnpatchAll(PatchId);
                Operational = false;
                D.LogEx(ex, "Relay initialization failed. Existing Quantum networks are unchanged; do not use Relay buildings in this session.");
            }
        }

        private void AfterLoad(GameState state)
        {
            ready = true;
            state.Clock.OnTick.AddListener(OnTick);
            // Core restores ObjData and query responders during AfterLoadState.
            // Do not depend on listener registration order or zero local counters
            // before its object restoration/validation has completed.
            Ready.When(Game.CodeGen.WhenH.ObjsLoaded, () =>
            {
                objectsReady = true; ObjectChanged(); InvalidateTopology(); EnsureTopology(true);
            });
            EnsureTopology(true);
        }

        public override void Unload()
        {
            Operational = ready = false;
            objectsReady = false;
            harmony?.UnpatchAll(PatchId);
            nodes.Clear(); groups.Clear(); lastVertices.Clear();
            objectNodes.Clear(); objectGroups.Clear(); objectIndex.Clear();
            upgrades.Clear(); upgradeData = null;
        }

        public static StorageRelaySys Get(GameState state)
        {
            if (state?.Sys?.Known != null && state.Sys.Known.TryGetValue(SysId, out var sys)) { return sys as StorageRelaySys; }
            return null;
        }

        internal static bool IsRelay(MatStorageComp storage) => storage?.Entity != null && storage.Entity.TryGetComponent<StorageRelayNodeComp>(out _);
        internal static MatStorageComp Storage(StorageRelayNodeComp node) => node.Entity.GetComponent<MatStorageComp>();
        internal static bool Active(StorageRelayNodeComp node) => node?.Tile != null && node.Tile.IsActive && node.Tile.IsConstructed &&
            node.Tile.Reinstallable?.IsMidRelocation != true && node.Tile.ENode?.IsPowered == true && !node.Tile.ENode.HasDeficit && node.IsUsable;

        internal void InvalidateTopology() => lastTick = long.MinValue;

        internal void Attach(StorageRelayNodeComp node)
        {
            if (node.Entity.TryGetComponent<MatStorageComp>(out var storage)) { nodes[storage] = node; }
            else if (node.Entity.TryGetComponent<ObjStorageComp>(out var objects))
            { objectNodes[objects.EntityId] = objects; objectIndexDirty = true; }
            lastTick = long.MinValue;
        }

        internal void Detach(StorageRelayNodeComp node)
        {
            if (node.Entity.TryGetComponent<MatStorageComp>(out var storage))
            { nodes.Remove(storage); groups.Remove(storage); }
            else
            { objectNodes.Remove(node.EntityId); objectGroups.Remove(node.EntityId); ObjectChanged(); }
            lastTick = long.MinValue;
        }

        internal RelayGroup Group(MatStorageComp storage, bool force = false)
        {
            if (!Operational || !ready || RelayCore.LocalDepth > 0) { return null; }
            EnsureTopology(force);
            if (!groups.TryGetValue(storage, out var group)) { return null; }
            // Read views refresh at most once per clock tick. Every operation
            // that touches remote stock forces a fresh topology check, including
            // bridge changes within that same tick. Local power is always checked.
            if (!nodes.TryGetValue(storage, out var node) || !Active(node))
            {
                EnsureTopology(true);
                if (!groups.TryGetValue(storage, out group)) { return null; }
            }
            Refresh(group);
            return group;
        }

        private void EnsureTopology(bool force)
        {
            if (!ready || rebuilding || (!force && lastTick == G.Ticks)) { return; }
            lastTick = G.Ticks;
            var ordered = nodes.Values.Concat(objectsReady ? objectNodes.Values.Select(s => s.Entity.GetComponent<StorageRelayNodeComp>()) : Enumerable.Empty<StorageRelayNodeComp>()).OrderBy(n => n.EntityId).ToList();
            var vertices = ordered.Select(n => new RelayVertex { Id = n.EntityId, Kind = (n.Entity.TryGetComponent<ObjStorageComp>(out _) ? "obj:" : "mat:") + n.NetworkId,
                X = (int)n.Tile.Position.x, Y = (int)n.Tile.Position.y, Range = n.Range, Active = Active(n) }).ToList();
            bool changed = vertices.Count != lastVertices.Count;
            for (int i = 0; !changed && i < vertices.Count; i++)
            {
                var a = vertices[i]; var b = lastVertices[i];
                changed = a.Id != b.Id || a.X != b.X || a.Y != b.Y || a.Kind != b.Kind || a.Range != b.Range || a.Active != b.Active;
            }
            if (!changed) { return; }
            rebuilding = true;
            try
            {
                // Never redistribute the old group at this point: the unit that
                // just went offline keeps precisely its last locally owned share.
                groups.Clear(); objectGroups.Clear();
                foreach (var component in RelayPolicy.Components(vertices))
                {
                    var group = new RelayGroup { IsObject = ordered[component[0]].Entity.TryGetComponent<ObjStorageComp>(out _) };
                    foreach (int i in component)
                    {
                        group.Nodes.Add(ordered[i]);
                        if (group.IsObject) { objectGroups[ordered[i].EntityId] = group; }
                        else { groups[Storage(ordered[i])] = group; }
                    }
                    if (group.IsObject) { RefreshObjects(group); } else { Refresh(group); }
                }
                lastVertices = vertices;
                // Core's bar.Info is a separate cache from the storage panel.
                // Refresh every endpoint, including the offline bridge that no
                // longer belongs to a group and will never be balanced on tick.
                foreach (var s in nodes.Keys) { QuantumStorageReflection.RefreshDisplay(s); }
                RefreshObjectDisplays();
            }
            finally { rebuilding = false; }
        }

        internal static Dictionary<MatType, int> LocalContents(MatStorageComp storage) => QuantumStorageReflection.GetContents(storage);
        internal static int LocalUsed(MatStorageComp storage) => LocalContents(storage)?.Values.Sum() ?? 0;
        internal static int LocalCapacity(MatStorageComp storage) => storage.Entity.GetComponent<StorageRelayNodeComp>().Capacity;
        internal static int Pinned(MatStorageComp storage, MatType type)
        {
            // Read real Core records, including Core's in-place reservation updates.
            // No replacement reservation book and no reassignment of Storage/Being.
            long sum = 0;
            foreach (var res in storage.Reservations.AllReservations)
            { if (res.Type == type && res.Amount > 0) { sum += res.Amount; } }
            return (int)Math.Min(int.MaxValue, sum);
        }

        private static Dictionary<MatType, int> Pins(MatStorageComp storage)
        {
            var result = new Dictionary<MatType, int>();
            foreach (var res in storage.Reservations.AllReservations)
            {
                if (res.Type == null || res.Amount <= 0) { continue; }
                result.TryGetValue(res.Type, out int amount);
                result[res.Type] = amount + res.Amount;
            }
            return result;
        }

        internal static void Refresh(RelayGroup group)
        {
            if (!group.Dirty) { return; }
            var totals = new Dictionary<MatType, int>();
            group.Used = group.Capacity = 0;
            foreach (var node in group.Nodes)
            {
                group.Capacity = checked(group.Capacity + node.Capacity);
                var contents = LocalContents(Storage(node));
                if (contents == null) { continue; }
                foreach (var p in contents)
                {
                    if (p.Value <= 0) { continue; }
                    totals.TryGetValue(p.Key, out int count);
                    totals[p.Key] = checked(count + p.Value);
                    group.Used = checked(group.Used + p.Value);
                }
            }
            group.Contents = totals;
            group.Dirty = false;
        }

        internal static int Available(RelayGroup group, MatType type)
        {
            if (group == null || type == null) { return 0; }
            Refresh(group);
            group.Contents.TryGetValue(type, out int count);
            foreach (var node in group.Nodes) { count -= Pinned(Storage(node), type); }
            return Math.Max(0, count);
        }

        internal void Changed(MatStorageComp storage)
        {
            if (groups.TryGetValue(storage, out var group)) { group.Dirty = group.NeedsBalance = true; }
        }

        private void OnTick(long ticks)
        {
            RecoverUpgrades();
            EnsureTopology(false);
            var work = groups.Values.Concat(objectGroups.Values).Distinct().Where(g => g.NeedsBalance).ToArray();
            if (work.Length == 0) { return; }
            // At most one dirty network per game tick. No all-goal / all-hauler scan.
            var group = work[(balanceCursor++ & int.MaxValue) % work.Length];
            if (group.IsObject) { BalanceObjects(group); RefreshObjectDisplays(); }
            else
            {
                Balance(group);
                foreach (var n in group.Nodes) { QuantumStorageReflection.RefreshDisplay(Storage(n)); }
            }
        }

        internal bool Balance(RelayGroup group, MatStorageComp claimant = null, MatType type = null, int amount = 0)
        {
            if (group == null) { return false; }
            var bins = group.Nodes.Select(n => {
                var s = Storage(n);
                return new RelayBin<MatType> { Id = n.EntityId, Capacity = n.Capacity,
                    Contents = LocalContents(s) ?? new Dictionary<MatType, int>(), Pinned = Pins(s) };
            }).ToList();
            int claim = claimant == null ? -1 : group.Nodes.FindIndex(n => Storage(n) == claimant);
            if (!RelayPolicy.TryBalance(bins, Comparer<MatType>.Create((a,b) => string.CompareOrdinal(a.Id,b.Id)), out var plan, claim, type, amount))
            {
                // Overfull or inconsistent old Core reservation: preserve the exact
                // local stock and let Core release the reservation normally.
                group.NeedsBalance = false;
                return false;
            }
            var before = bins.Select(b => new Dictionary<MatType, int>(b.Contents)).ToArray();
            try
            {
                using (RelayCore.Local())
                {
                // Private Core mutators keep its usedCapacity cache authoritative.
                // No Store/Take task side effects during bookkeeping redistribution.
                for (int i = 0; i < bins.Count; i++)
                {
                    var storage = Storage(group.Nodes[i]);
                    foreach (var p in bins[i].Contents.ToArray())
                    {
                        int wanted = plan[i].TryGetValue(p.Key, out int n) ? n : 0;
                        if (p.Value > wanted) { RelayCore.Remove(storage, p.Key, p.Value - wanted); }
                    }
                }
                for (int i = 0; i < bins.Count; i++)
                {
                    var storage = Storage(group.Nodes[i]);
                    foreach (var p in plan[i])
                    {
                        var contents = LocalContents(storage);
                        int have = contents != null && contents.TryGetValue(p.Key, out int n) ? n : 0;
                        if (p.Value > have) { RelayCore.Add(storage, p.Key, p.Value - have); }
                    }
                }
                }
            }
            catch (Exception ex)
            {
                // Protect every local save record if a future Core mutation hook
                // throws mid-transfer. Reservation objects are retained verbatim.
                for (int i = 0; i < bins.Count; i++)
                {
                    var s = Storage(group.Nodes[i]);
                    QuantumStorageReflection.Bind(s, before[i], s.Reservations);
                    QuantumStorageReflection.SetUsedCapacity(s, before[i].Values.Sum());
                }
                group.Dirty = true; group.NeedsBalance = false;
                D.LogEx(ex, "Relay rebalance rolled back; all local inventory shares and Core reservation books were preserved.");
                return false;
            }
            group.Dirty = true; group.NeedsBalance = false; Refresh(group);
            return true;
        }

        internal int Store(MatStorageComp entry, MatType type, Being who, int amount, object ctx)
        {
            var group = Group(entry, true);
            if (group == null || type == null || amount < 1 || !entry.AcceptsType(type)) { return amount; }
            int remaining = amount;
            using (RelayCore.Local())
            {
                foreach (var node in group.Nodes.OrderBy(n => n.EntityId == entry.EntityId ? 0 : 1))
                {
                    var s = Storage(node);
                    int chunk = Math.Min(remaining, Math.Max(0, node.Capacity - LocalUsed(s)));
                    if (chunk <= 0) { continue; }
                    remaining -= chunk - s.Store(type, who, chunk, ctx);
                    if (remaining == 0) { break; }
                }
            }
            Changed(entry);
            return remaining;
        }

        internal int PrepareReservation(MatStorageComp entry, MatType type, int wanted)
        {
            var group = Group(entry, true);
            if (group == null || type == null || wanted <= 0) { return 0; }
            int spaceForPins = Math.Max(0, LocalCapacity(entry) - Pins(entry).Values.Sum());
            int amount = Math.Min(wanted, Math.Min(spaceForPins, Available(group, type)));
            return amount > 0 && Balance(group, entry, type, amount) ? amount : 0;
        }

        internal int TakeUnreserved(MatStorageComp entry, MatType type, int wanted, object ctx)
        {
            var group = Group(entry, true);
            if (group == null || type == null || wanted < 1) { return 0; }
            int remaining = wanted;
            using (RelayCore.Local())
            {
                foreach (var node in group.Nodes.OrderBy(n => n.EntityId == entry.EntityId ? 0 : 1))
                {
                    var s = Storage(node); var contents = LocalContents(s);
                    int count = contents != null && contents.TryGetValue(type, out int n) ? n : 0;
                    int chunk = Math.Min(remaining, Math.Max(0, count - Pinned(s, type)));
                    if (chunk > 0) { remaining -= s.Take(type, null, chunk, ctx); }
                    if (remaining == 0) { break; }
                }
            }
            Changed(entry);
            return wanted - remaining;
        }

        internal List<Mat> TakeAll(MatStorageComp entry)
        {
            var group = Group(entry, true);
            if (group == null) { return null; }
            var result = new List<Mat>();
            using (RelayCore.Local())
            {
                foreach (var node in group.Nodes)
                {
                    // Core TakeAll owns cancellation of reservations and goals.
                    var mats = Storage(node).TakeAll();
                    if (mats != null) { result.AddRange(mats); }
                }
            }
            Changed(entry);
            return result;
        }

        internal string Status(MatStorageComp storage)
        {
            var group = Group(storage);
            return Status(LocalUsed(storage), LocalCapacity(storage), group);
        }

        internal static string Status(int localUsed, int localCapacity, RelayGroup group)
        {
            return "Stored in this Relay: " + localUsed + " / " + localCapacity +
                "\n" + (group == null ? "Offline — local contents retained; no inventory access or links." :
                "Connected Relays: " + group.Nodes.Count + " | Available network space: " + Math.Max(0, group.Capacity - group.Used) + " / " + group.Capacity) +
                "\nLink range: 12 tiles (walls do not block links). Eject All affects only the active connected network.";
        }
    }
}
