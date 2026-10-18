using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Input;
using Game.Systems.Creatures;
using Game.Systems.Inventory;
using Game.Systems.Trade;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.QuantumStorage.Relays
{
    internal static class RelayCore
    {
        [ThreadStatic] internal static int LocalDepth;
        [ThreadStatic] private static int ownedDepth;
        private static readonly MethodInfo add = AccessTools.DeclaredMethod(typeof(MatStorageComp), "AddMat", new[] { typeof(MatType), typeof(int) });
        private static readonly MethodInfo remove = AccessTools.DeclaredMethod(typeof(MatStorageComp), "RemoveMat", new[] { typeof(MatType), typeof(int) });
        private static readonly FieldInfo reservationStorage = AccessTools.Field(typeof(MatStorageReservations), "storage");

        internal sealed class Scope : IDisposable
        {
            private readonly bool owned;
            private bool disposed;
            internal Scope(bool owned) { this.owned = owned; if (owned) { ownedDepth++; } else { LocalDepth++; } }
            public void Dispose() { if (disposed) { return; } disposed = true; if (owned) { ownedDepth--; } else { LocalDepth--; } }
        }
        internal static Scope Local() => new Scope(false);
        internal static void Add(MatStorageComp s, MatType t, int n) => add.Invoke(s, new object[] { t, n });
        internal static void Remove(MatStorageComp s, MatType t, int n) => remove.Invoke(s, new object[] { t, n });

        // Explicit signatures throughout. All targets are checked before any patch
        // is installed; the owning system rolls back this group alone on failure.
        internal static List<(MethodBase target, string prefix, string postfix, string finalizer)> Bindings()
        {
            if (add == null || remove == null || reservationStorage == null) { throw new MissingMemberException("Relay Core storage binding"); }
            var targets = new List<(MethodBase target, string prefix, string postfix, string finalizer)>();
            void Method(Type t, string name, Type[] args, string pre = null, string post = null, string fin = null)
            {
                var m = AccessTools.DeclaredMethod(t, name, args);
                if (m == null) { throw new MissingMethodException(t.FullName, name); }
                targets.Add((m, pre, post, fin));
            }
            void Getter(string name, string post)
            {
                var m = AccessTools.PropertyGetter(typeof(MatStorageComp), name);
                if (m == null) { throw new MissingMethodException(name); }
                targets.Add((m, null, post, null));
            }
            var storage = typeof(MatStorageComp);
            var empty = Type.EmptyTypes;
            var material = new[] { typeof(MatType) };
            var transfer = new[] { typeof(MatType), typeof(Being), typeof(int), typeof(object) };
            Getter("Contents", nameof(Contents));
            Getter("SortedContents", nameof(Sorted));
            Getter("MaxCapacity", nameof(Capacity)); Getter("UsedCapacity", nameof(Used));
            Getter("AvailableCapacity", nameof(Free)); Getter("FillPercent", nameof(Fill));
            Getter("IsFull", nameof(Full)); Getter("IsEmpty", nameof(Empty));
            Method(storage, "Available", material, post: nameof(Available));
            Method(storage, "AvailableWithoutReservation", material, post: nameof(WithoutReservation));
            Method(storage, "AvailableForGroup", new[] { typeof(int), typeof(List<Mat>) }, post: nameof(GroupOptions));
            Method(storage, "AvailableForGroup", new[] { typeof(int) }, post: nameof(HasGroup));
            Method(storage, "Store", transfer, nameof(Store));
            Method(storage, "Reserve", transfer, nameof(Reserve), fin: nameof(EndStorageScope));
            Method(storage, "Take", transfer, nameof(Take), fin: nameof(EndStorageScope));
            Method(storage, "TakeAll", empty, nameof(TakeAll));
            Method(storage, "Eject", new[] { typeof(MatType), typeof(bool), typeof(int) }, nameof(Eject));
            Method(storage, "OnRemove", empty, nameof(Removing), fin: nameof(EndScope));
            Method(storage, "OnExplode", empty, nameof(Explode));
            Method(storage, "DestroyContents", empty, nameof(LocalStart), fin: nameof(EndScope));
            Method(storage, "RelocateTo", new[] { typeof(Entity) }, nameof(Relocate), fin: nameof(EndScope));
            Method(storage, "AddMat", new[] { typeof(MatType), typeof(int) }, post: nameof(Mutation));
            Method(storage, "RemoveMat", new[] { typeof(MatType), typeof(int) }, post: nameof(Mutation));
            Method(storage, "ClearMats", empty, post: nameof(Mutation));
            Method(storage, "AvailableForNanobots", material, post: nameof(NativeNanobotAvailable));
            Method(typeof(MatStorageReservations), "Remove", new[] { typeof(Being), typeof(MatType) }, post: nameof(ReservationChanged));
            Method(typeof(MatStorageReservations), "RemoveAll", new[] { typeof(Being) }, post: nameof(ReservationChanged));
            targets.Add((AccessTools.PropertyGetter(typeof(MatStorageReservations), "AllReservations"), null, nameof(ReservationSnapshot), null));
            Method(storage, "ShowCustomTooltip", new[] { typeof(string), typeof(InfoItem) }, pre: nameof(Tooltip));
            Method(typeof(MatStorageTab), "LoadTopControls", new[] { typeof(WGroup) }, post: nameof(Header));
            Method(typeof(MatStorageTab), "BuildContentsList", empty, post: nameof(ContentsButtons));
            Method(typeof(PendingReplacementComp), "AfterReplace", new[] { typeof(Tile), typeof(Tile) }, post: nameof(Replaced));
            Method(typeof(InventorySys), "HasEnough", new[] { typeof(MatType), typeof(int), typeof(int), typeof(Being) }, nameof(HasEnough));
            Method(typeof(InventorySys), "CountByGroup", new[] { typeof(int) }, nameof(CountGroup));
            Method(typeof(InventorySys), "TrySell", new[] { typeof(MatType), typeof(int), typeof(bool) }, nameof(OwnedStart), fin: nameof(EndScope));
            var counts = typeof(Dictionary<MatType, int>);
            Method(typeof(InventorySys), "LoadRemainingMaterials", new[] { counts }, nameof(OwnedStart), fin: nameof(EndScope));
            Method(typeof(InventorySys), "LoadRemainingMaterials", new[] { counts, counts }, nameof(OwnedStart), fin: nameof(EndScope));
            Method(typeof(InventorySys), "LoadRemainingMaterials", new[] { counts, typeof(string[]), typeof(string[]), typeof(bool) }, nameof(OwnedStart), fin: nameof(EndScope));
            Method(typeof(TradeSys), "LoadOurItems", new[] { typeof(Trader), typeof(List<TradeableItem>) }, nameof(OwnedStart), fin: nameof(EndScope));
            // The existing Quantum patch remains responsible for Core's warning
            // IL shape and notification lifecycle. Amend only its computed totals.
            Method(typeof(QuantumFullStorageWarning), "CorrectTotals", new[] { typeof(GameState), typeof(List<IMatStorage>), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() }, post: nameof(WarningTotals));
            return targets;
        }
        internal static void Install(Harmony harmony)
        {
            foreach (var t in Bindings())
            {
                HarmonyMethod H(string name) => name == null ? null : new HarmonyMethod(typeof(RelayCore), name) { priority = HarmonyLib.Priority.First };
                harmony.Patch(t.target, H(t.prefix), H(t.postfix), null, H(t.finalizer));
            }
        }

        private static bool Is(MatStorageComp storage) => StorageRelaySys.IsRelay(storage);
        private static StorageRelaySys Sys(MatStorageComp s) => StorageRelaySys.Get(s.S);
        private static RelayGroup Network(MatStorageComp s) => Sys(s)?.Group(s);
        private static int LocalCount(MatStorageComp s, MatType type)
        {
            var d = StorageRelaySys.LocalContents(s);
            return type != null && d != null && d.TryGetValue(type, out int n) ? n : 0;
        }
        private static void OwnedStart(out Scope __state) => __state = new Scope(true);
        private static void LocalStart(MatStorageComp __instance, out Scope __state) => __state = Is(__instance) ? Local() : null;
        private static void Removing(MatStorageComp __instance, out Scope __state)
        {
            __state = null;
            if (!Is(__instance)) { return; }
            Sys(__instance)?.CaptureUpgrade(__instance);
            __state = Local();
        }
        private static void Replaced(Tile oldTile, Tile t) => StorageRelaySys.Get(t?.S)?.CompleteUpgrade(oldTile, t);
        private static void EndScope(Scope __state) => __state?.Dispose();
        private static void EndStorageScope(MatStorageComp __instance, Scope __state)
        {
            __state?.Dispose();
            if (__state != null && LocalDepth == 0) { Sys(__instance)?.Changed(__instance); }
        }
        private static void Mutation(MatStorageComp __instance) { if (Is(__instance)) { Sys(__instance)?.Changed(__instance); } }
        private static void ReservationChanged(MatStorageReservations __instance)
        { if (reservationStorage.GetValue(__instance) is MatStorageComp s && Is(s)) { Sys(s)?.Changed(s); } }
        private static void ReservationSnapshot(MatStorageReservations __instance, ref IEnumerable<MatReservation> __result)
        {
            // Core TakeAll fails the owners while iterating this collection.
            // Snapshot enumeration only; Core still removes each real reservation.
            if (LocalDepth > 0 && reservationStorage.GetValue(__instance) is MatStorageComp s && Is(s)) { __result = __result.ToArray(); }
        }

        private static void Contents(MatStorageComp __instance, ref Dictionary<MatType, int> __result)
        {
            if (!Is(__instance) || LocalDepth > 0 || ownedDepth > 0) { return; }
            var n = Network(__instance);
            if (n != null) { __result = n.Contents; }
        }
        private static void Sorted(MatStorageComp __instance, ref IOrderedEnumerable<KeyValuePair<MatType, int>> __result)
        {
            if (!Is(__instance) || LocalDepth > 0) { return; }
            var d = __instance.Contents;
            if (d == null) { return; }
            switch (MatStorageComp.Sort)
            {
                case MatStorageComp.SortMode.Alphabetic: __result = d.OrderBy(p => p.Key.NameT); break;
                case MatStorageComp.SortMode.CountsAsc: __result = d.OrderBy(p => p.Value); break;
                default: __result = d.OrderByDescending(p => p.Value); break;
            }
        }
        private static void Capacity(MatStorageComp __instance, ref int __result)
        {
            if (!Is(__instance)) { return; }
            __result = LocalDepth > 0 ? StorageRelaySys.LocalCapacity(__instance) : Network(__instance)?.Capacity ?? StorageRelaySys.LocalCapacity(__instance);
        }
        private static void Used(MatStorageComp __instance, ref int __result)
        { if (Is(__instance) && LocalDepth == 0) { __result = Network(__instance)?.Used ?? StorageRelaySys.LocalUsed(__instance); } }
        private static void Free(MatStorageComp __instance, ref int __result)
        {
            if (!Is(__instance)) { return; }
            if (LocalDepth > 0) { __result = Math.Max(0, StorageRelaySys.LocalCapacity(__instance) - StorageRelaySys.LocalUsed(__instance)); return; }
            var n = Network(__instance); __result = n == null ? 0 : Math.Max(0, n.Capacity - n.Used);
        }
        private static void Fill(MatStorageComp __instance, ref float __result)
        { if (Is(__instance)) { __result = Math.Min(1f, __instance.UsedCapacity / (float)Math.Max(1, __instance.MaxCapacity)); } }
        private static void Full(MatStorageComp __instance, ref bool __result)
        { if (Is(__instance)) { __result = __instance.AvailableCapacity <= 0; } }
        private static void Empty(MatStorageComp __instance, ref bool __result)
        { if (Is(__instance)) { __result = __instance.UsedCapacity == 0; } }
        private static void Available(MatStorageComp __instance, MatType type, ref int __result)
        {
            if (!Is(__instance)) { return; }
            __result = LocalDepth > 0 ? Math.Max(0, LocalCount(__instance, type) - StorageRelaySys.Pinned(__instance, type)) : StorageRelaySys.Available(Network(__instance), type);
        }
        private static void WithoutReservation(MatStorageComp __instance, MatType type, ref int __result)
        {
            if (!Is(__instance) || LocalDepth > 0) { return; }
            var n = Network(__instance);
            __result = 0;
            if (n == null || type == null) { return; }
            if (ownedDepth > 0) { __result = LocalCount(__instance, type); }
            else { n.Contents.TryGetValue(type, out __result); }
        }
        private static void NativeNanobotAvailable(MatStorageComp __instance, MatType type, ref int __result)
        {
            if (!Is(__instance)) { return; }
            // Retain native material-Hive behaviour only. No remote Relay stock
            // is promoted into Quantum's extra object/TACC/boundary routes.
            __result = Network(__instance) == null ? 0 : Math.Max(0, LocalCount(__instance, type) - StorageRelaySys.Pinned(__instance, type));
        }
        private static void GroupOptions(MatStorageComp __instance, int groupId, List<Mat> mats, ref bool __result)
        {
            if (!Is(__instance)) { return; }
            __result = false; mats?.Clear();
            if (mats == null || !__instance.AcceptsGroup(groupId)) { return; }
            var d = __instance.Contents;
            if (d == null) { return; }
            foreach (var p in d)
            {
                if (p.Key.GroupH != groupId) { continue; }
                int amount = __instance.Available(p.Key);
                if (amount > 0) { mats.Add(new Mat { Type = p.Key, StackSize = amount, MaxStackSize = amount }); }
            }
            __result = mats.Count > 0;
        }
        private static void HasGroup(MatStorageComp __instance, int groupId, ref bool __result)
        {
            if (!Is(__instance)) { return; }
            var mats = new List<Mat>(); GroupOptions(__instance, groupId, mats, ref __result);
        }
        private static bool Store(MatStorageComp __instance, MatType type, Being who, int amount, object ctx, ref int __result)
        {
            if (!Is(__instance) || LocalDepth > 0) { return true; }
            __result = Sys(__instance)?.Store(__instance, type, who, amount, ctx) ?? amount;
            return false;
        }
        private static bool Reserve(MatStorageComp __instance, MatType type, Being who, ref int wantedAmount, ref int __result, out Scope __state)
        {
            __state = null;
            if (!Is(__instance) || LocalDepth > 0) { return true; }
            __result = 0;
            if (who == null || type == null || Network(__instance) == null) { return false; }
            var existing = __instance.Reservations.GetReservation(who, type);
            if (existing != null) { __result = existing.Amount; return false; }
            wantedAmount = Sys(__instance)?.PrepareReservation(__instance, type, wantedAmount) ?? 0;
            if (wantedAmount <= 0) { return false; }
            __state = Local();
            // Core creates MatReservation, associates it with the endpoint and
            // worker blackboard, and performs all normal task bookkeeping.
            return true;
        }
        private static bool Take(MatStorageComp __instance, MatType type, Being who, int wantedAmount, object ctx, ref int __result, out Scope __state)
        {
            __state = null;
            if (!Is(__instance) || LocalDepth > 0) { return true; }
            __result = 0;
            if (Network(__instance) == null) { return false; }
            if (who == null) { __result = Sys(__instance).TakeUnreserved(__instance, type, wantedAmount, ctx); return false; }
            __state = Local(); return true;
        }
        private static bool TakeAll(MatStorageComp __instance, ref List<Mat> __result)
        {
            if (!Is(__instance) || LocalDepth > 0) { return true; }
            __result = Sys(__instance)?.TakeAll(__instance); return false;
        }
        private static bool Eject(MatStorageComp __instance, MatType m, bool ignoreReservations, int amount, ref UnstoredMatComp __result)
        {
            if (!Is(__instance) || LocalDepth > 0) { return true; }
            __result = null;
            var n = Network(__instance);
            if (n == null || m == null) { return false; }
            int available = StorageRelaySys.Available(n, m);
            // Reserved stock is only forcibly ejected by Core's Eject All path,
            // which also fails its owning goals. Single-type ejection is safe.
            int took = Sys(__instance).TakeUnreserved(__instance, m, amount > 0 ? Math.Min(amount, available) : available, null);
            if (took > 0) { __result = EntityUtils.SpawnRawMaterial(m, took, __instance.Tile.Transform.AnyPortOutFirst, 0.25f, true); }
            return false;
        }
        private static bool Explode(MatStorageComp __instance) => !Is(__instance); // removal ejects the local share, once
        private static bool Relocate(MatStorageComp __instance, Entity target, out Scope __state)
        {
            __state = null;
            if (!Is(__instance)) { return true; }
            if (target.TryGetComponent<MatStorageComp>(out var dest) && QuantumStorageNetworkSys.TryGetNode(dest, out _))
            {
                // Never let Core overwrite a shared Quantum dictionary. Only
                // this Relay's share moves; existing Quantum contents stay intact.
                List<Mat> mats;
                using (Local()) { mats = __instance.TakeAll(); }
                QuantumStorageNetworkSys.Get(dest.S)?.Attach(dest);
                if (mats != null)
                foreach (var mat in mats)
                {
                    int rest = dest.Store(mat.Type, null, mat.StackSize, __instance);
                    if (rest > 0) { EntityUtils.SpawnRawMaterial(mat.Type, rest, dest.Tile.Transform.AnyPortOutFirst, 0.25f, true); }
                }
                return false;
            }
            __state = Local(); return true;
        }
        internal static string CapacityLabel(int used, int capacity)
        {
            // AvailableCapacity == 0 also gates OFFLINE access. That is not a
            // full inventory. Only the display uses this physical fullness test;
            // Core's existing intake/reservation/hauling guards stay unchanged.
            return string.Format(T.StorageCapacityBar_V2, used, capacity) +
                (used >= capacity ? " (" + Texts.Red(T.InfoStorageFull) + ")" : "");
        }

        private static bool Tooltip(MatStorageComp __instance, InfoItem item, ref string __result)
        {
            if (!Is(__instance) || item.Type != InfoItemType.Bar) { return true; }
            var sys = Sys(__instance);
            // Also catches electrical disconnection while paused. Never read
            // the old InfoBar.Info or rebalance stock to produce a tooltip.
            var group = sys?.Group(__instance, true);
            int used = group?.Used ?? StorageRelaySys.LocalUsed(__instance);
            int capacity = group?.Capacity ?? StorageRelaySys.LocalCapacity(__instance);
            var text = new StringBuilder(CapacityLabel(used, capacity));
            var local = StorageRelaySys.LocalContents(__instance);
            if (local != null)
            {
                // Preserve Core's useful top-ten LOCAL item provenance list.
                foreach (var mat in local.OrderByDescending(m => m.Value).ThenBy(m => m.Key.NameT).Take(10))
                { text.AppendFormat("\n   {0} {1}: x{2}", GlyphMap.ArrowRight, mat.Key.Def.CodexLink, mat.Value); }
                if (local.Count > 10) { text.Append("\n" + Texts.Grey($"   {GlyphMap.ArrowRight} +{local.Count - 10}")); }
            }
            if (sys != null) { text.Append("\n" + sys.Status(__instance)); }
            __result = text.ToString();
            return false;
        }
        private static void Header(MatStorageTab __instance, WGroup group, MatStorageComp ___storage)
        {
            if (!Is(___storage)) { return; }
            StatusHeader(__instance, group, () => Sys(___storage)?.Status(___storage), ___storage.OnChange);
        }
        internal static void StatusHeader(BaseEntityTab tab, WGroup group, Func<string> status, BCallback changed)
        {
            var life = (Lifetime)AccessTools.Field(typeof(BaseEntityTab), "lifetime").GetValue(tab);
            var block = UISpawn.Block(life, group, 1);
            var text = UISpawn.Widget<WIconText>(life, UIPrefabId.WContentText, block.Container, 0);
            void Refresh() => text.Load(IconId.CInfo, status() ?? "Relay support unavailable.");
            Refresh(); changed.Bind(life, Refresh, false);
        }
        private static void ContentsButtons(MatStorageComp ___storage, WGroup ___contentsBlock)
        {
            if (!Is(___storage) || ___contentsBlock == null || LocalDepth > 0) { return; }
            bool online = Network(___storage) != null;
            foreach (var button in ___contentsBlock.Container.GetComponentsInChildren<WButtonText>(true)) { button.SetInteractable(online); }
            foreach (var button in ___contentsBlock.Container.GetComponentsInChildren<WButtonIconText>(true)) { button.SetInteractable(online); }
        }

        internal static object Key(GameState state, IMatStorage storage)
        {
            if (storage is not MatStorageComp s) { return storage; }
            if (Is(s)) { return (object)Network(s) ?? s; }
            if (QuantumStorageNetworkSys.Get(state)?.TryGetNetwork(s, out var q) == true) { return q; }
            return s;
        }
        private static bool HasEnough(InventorySys __instance, MatType type, int amount, int islandId, Being being, ref bool __result)
        {
            if (StorageRelaySys.Get(__instance.S)?.HasNodes != true) { return true; }
            long count = 0; var seen = new HashSet<object>();
            BeingSafety safety = being?.Data.Safety;
            bool ignoreDanger = safety == null || safety.IgnoreDanger;
            foreach (var s in __instance.Storages)
            {
                if (s.StorageType == StorageType.StoreOnly ||
                    (islandId != -1 && !__instance.S.Sys.Areas.CanReachGroundedIslandToPos(islandId, s.OutPort)) ||
                    (!ignoreDanger && !safety.IsActuallySafeAt(s.OutPort)) || !seen.Add(Key(__instance.S, s))) { continue; }
                count += s.Available(type);
                if (count >= amount) { __result = true; return false; }
            }
            __result = false; return false;
        }
        private static bool CountGroup(InventorySys __instance, int group, ref int __result)
        {
            if (StorageRelaySys.Get(__instance.S)?.HasNodes != true) { return true; }
            __result = 0; var seen = new HashSet<object>(); var mats = new List<Mat>();
            var stores = __instance.StoragesByGroup(group);
            if (stores != null)
            foreach (var s in stores)
            { if (seen.Add(Key(__instance.S, s)) && s.AvailableForGroup(group, mats)) { __result += mats.Count; } }
            return false;
        }
        private static void WarningTotals(GameState state, List<IMatStorage> storages, ref int capacity, ref int free)
        {
            if (StorageRelaySys.Get(state)?.HasNodes != true || storages == null) { return; }
            long c = 0, f = 0; var seen = new HashSet<object>();
            foreach (var s in storages)
            {
                if (s is not MatStorageComp m || m.StorageType == StorageType.TakeOnly || !seen.Add(Key(state, m))) { continue; }
                if (Is(m) && Network(m) == null) { continue; }
                c += m.MaxCapacity; f += Math.Max(0, m.AvailableCapacity);
            }
            capacity = (int)Math.Min(int.MaxValue, c); free = (int)Math.Min(int.MaxValue, f);
        }
    }
}
