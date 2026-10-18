using System;
using System.Collections.Generic;
using System.Linq;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Items;
using Game.Utils;
using HarmonyLib;

namespace Game.Systems.QuantumStorage.Relays
{
    public sealed partial class StorageRelaySys
    {
        private readonly Dictionary<int, ObjStorageComp> objectNodes = new Dictionary<int, ObjStorageComp>();
        private readonly Dictionary<int, RelayGroup> objectGroups = new Dictionary<int, RelayGroup>();
        private readonly Dictionary<int, List<ObjData>> objectIndex = new Dictionary<int, List<ObjData>>();
        private bool objectIndexDirty = true, objectDisplayRefresh, objectsReady;
        internal int ObjectBulkDepth;
        private static readonly System.Reflection.FieldInfo objectUsed = AccessTools.Field(typeof(ObjStorageComp), "usedCapacity");
        private static readonly System.Reflection.MethodInfo objectStats = AccessTools.DeclaredMethod(typeof(ObjStorageComp), "UpdateStats", Type.EmptyTypes);

        internal static bool IsRelay(ObjStorageComp storage) => storage?.Entity != null && storage.Entity.TryGetComponent<StorageRelayNodeComp>(out _);
        internal static int LocalCapacity(ObjStorageComp storage) => storage.Entity.GetComponent<StorageRelayNodeComp>().Capacity;
        internal ObjStorageComp ObjectNode(int id) => objectNodes.TryGetValue(id, out var s) ? s : null;

        private void IndexObjects()
        {
            if (!ready || !objectsReady || !objectIndexDirty) { return; }
            objectIndex.Clear();
            foreach (int id in objectNodes.Keys) { objectIndex[id] = new List<ObjData>(); }
            var all = S.Query.GetAllObjs.Ask();
            if (all != null)
            foreach (var obj in all.Values)
            {
                if (obj.State == ObjState.Stored && objectIndex.TryGetValue(RelayObjectCore.Owner(obj), out var list)) { list.Add(obj); }
            }
            objectIndexDirty = false;
        }

        internal IReadOnlyList<ObjData> OwnedObjects(int id)
        {
            IndexObjects();
            return objectIndex.TryGetValue(id, out var list) ? (IReadOnlyList<ObjData>)list : Array.Empty<ObjData>();
        }

        internal void ObjectChanged()
        {
            objectIndexDirty = true;
            foreach (var g in objectGroups.Values.Distinct()) { g.Dirty = g.NeedsBalance = true; }
        }

        internal RelayGroup ObjectGroup(ObjStorageComp storage, bool force = false)
        {
            if (!Operational || !ready || !objectsReady || RelayObjectCore.LocalDepth > 0 || storage == null) { return null; }
            EnsureTopology(force);
            if (!Active(storage.Entity.GetComponent<StorageRelayNodeComp>())) { EnsureTopology(true); return null; }
            if (!objectGroups.TryGetValue(storage.EntityId, out var group)) { return null; }
            RefreshObjects(group);
            return group;
        }

        internal bool ConnectedObjects(int ownerId, ObjStorageComp access)
        {
            if (!Operational || !ready || !objectsReady || access == null) { return false; }
            EnsureTopology(true);
            return objectGroups.TryGetValue(ownerId, out var a) && objectGroups.TryGetValue(access.EntityId, out var b) && ReferenceEquals(a, b);
        }

        private void RefreshObjects(RelayGroup group)
        {
            if (!group.Dirty) { return; }
            IndexObjects();
            group.Objects = group.Nodes.SelectMany(n => OwnedObjects(n.EntityId)).ToList();
            group.Used = group.Objects.Count;
            group.Capacity = checked(group.Nodes.Sum(n => n.Capacity));
            group.Dirty = false;
        }

        internal void RefreshObjectDisplays()
        {
            if (!ready || !objectsReady || objectDisplayRefresh || ObjectBulkDepth > 0 || RelayObjectCore.LocalDepth > 0) { return; }
            objectDisplayRefresh = true;
            try
            {
                IndexObjects();
                foreach (var group in objectGroups.Values.Distinct()) { RefreshObjects(group); }
                foreach (var storage in objectNodes.Values.ToArray())
                {
                    objectGroups.TryGetValue(storage.EntityId, out var group);
                    storage.MaxCapacity = group?.Capacity ?? LocalCapacity(storage);
                    // Core's private counter ALWAYS means local ownership. The
                    // getter supplies a connected view, never a second saved copy.
                    objectUsed.SetValue(storage, OwnedObjects(storage.EntityId).Count);
                    objectStats.Invoke(storage, null);
                }
            }
            finally { objectDisplayRefresh = false; }
        }

        internal bool ObjectIsFull(ObjStorageComp storage)
        {
            var group = ObjectGroup(storage);
            return group == null || group.Used >= group.Capacity;
        }

        internal string Status(ObjStorageComp storage) => Status(OwnedObjects(storage.EntityId).Count, LocalCapacity(storage), ObjectGroup(storage));

        private bool BalanceObjects(RelayGroup group)
        {
            if (RelayObjectCore.RouteDepth > 0) { return false; }
            RefreshObjects(group);
            var bins = group.Nodes.Select(n => new RelayBin<string> { Id = n.EntityId, Capacity = n.Capacity }).ToList();
            var records = group.Objects.OrderBy(o => o.Id).Select(o => new RelayRecord<ObjData>
            { Record = o, Owner = o.StorageId, Kind = o.Def.Id, Pinned = o.Reservation != null &&
                (o.Reservation.IsActive || o.Reservation.LoadGoalId != 0 || o.Reservation.LoadSurgeryId != 0) }).ToList();
            // Do not retry an inconsistent/overfull plan until inventory changes.
            group.NeedsBalance = false;
            if (!RelayObjectPolicy.TryPlan(bins, records, out var owners)) { return false; }
            bool moved = false;
            foreach (var p in owners)
            {
                if (p.Key.StorageId == p.Value) { continue; }
                p.Key.StorageId = p.Value; moved = true;
            }
            if (moved)
            {
                objectIndexDirty = true; group.Dirty = true;
                RefreshObjects(group);
            }
            // Core may release a reservation in-place during validation. Recheck
            // only this small group, not all tasks or all objects in the world.
            group.NeedsBalance = records.Any(r => r.Pinned);
            return true;
        }

        internal bool CanUseObject(ObjData data)
        {
            if (!Operational || !ready || !objectsReady || data?.State != ObjState.Stored) { return true; }
            var owner = ObjectNode(RelayObjectCore.Owner(data));
            // Availability depends on the physical owner's power, not whether it
            // has peers. No graph construction per object in Core's hot selectors.
            return owner == null || Active(owner.Entity.GetComponent<StorageRelayNodeComp>());
        }

        internal bool TrySelectObjectAccess(ObjData data, int anchor, Predicate<ObjStorageComp> allowed, out ObjStorageComp storage)
        {
            storage = null;
            if (data?.State != ObjState.Stored) { return false; }
            var owner = ObjectNode(RelayObjectCore.Owner(data));
            var group = owner == null ? null : ObjectGroup(owner);
            if (group == null) { return false; }
            storage = group.Nodes.Where(Active).Select(n => ObjectNode(n.EntityId)).Where(s => s != null && allowed(s))
                .OrderBy(s => KL.Grid.Pos.SqrDistance(anchor, s.InPort)).ThenBy(s => s.EntityId).FirstOrDefault();
            return storage != null;
        }

        internal ObjStorageComp DepositOwner(ObjStorageComp entry, ObjData data)
        {
            var group = ObjectGroup(entry, true);
            if (group == null || group.Used >= group.Capacity || data == null || data.State != ObjState.Active ||
                !data.IsStorable(out int kind) || !entry.AcceptsGroup(kind) || entry.HaulTarget?.Accepts(data) == false) { return null; }
            // Agent adjacency was checked at the real entry by Core. StorageId
            // selects a local share behind that entry, not a physical teleport job.
            return group.Nodes.OrderBy(n => n.EntityId == entry.EntityId ? 0 : 1).ThenBy(n => n.EntityId)
                .Where(n => OwnedObjects(n.EntityId).Count < n.Capacity).Select(n => ObjectNode(n.EntityId)).FirstOrDefault();
        }

        internal static void EnsureNativeEjectCounter(ObjStorageComp storage)
        {
            // A remote endpoint can have zero locally owned records. Core ejects
            // through it and decrements its counter; reconcile from actual owners
            // after the synchronous mutation instead of reporting a negative count.
            if ((int)objectUsed.GetValue(storage) < 1) { objectUsed.SetValue(storage, 1); }
        }
    }
}
