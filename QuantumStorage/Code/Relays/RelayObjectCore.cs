using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Input;
using Game.Systems.AI;
using Game.Systems.Creatures.Surgeries;
using Game.Systems.Items;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.QuantumStorage.Relays
{
    // One Core adapter for clothing, weapons, organic and robotic parts.
    // No building IDs, faction shortcuts, object clones, or custom jobs here.
    internal static class RelayObjectCore
    {
        [ThreadStatic] private static Dictionary<ObjData, int> routes;
        [ThreadStatic] internal static int LocalDepth;
        private sealed class NativeScope : IDisposable
        {
            private bool disposed;
            internal NativeScope() { LocalDepth++; }
            public void Dispose() { if (!disposed) { disposed = true; LocalDepth--; } }
        }
        internal static int RouteDepth => routes?.Count ?? 0;
        internal static int Owner(ObjData obj) => obj != null && routes != null && routes.TryGetValue(obj, out int owner) ? owner : obj?.StorageId ?? 0;
        internal static bool BeginRoute(ObjData obj, GameState state)
        {
            if (StorageRelaySys.Get(state)?.ObjectNode(Owner(obj)) == null) { return false; }
            routes ??= new Dictionary<ObjData, int>();
            if (routes.ContainsKey(obj)) { return false; }
            routes.Add(obj, obj.StorageId); return true;
        }
        internal static void EndRoute(ObjData obj) => routes?.Remove(obj);

        internal static List<(MethodBase target, string prefix, string postfix, string finalizer)> Bindings()
        {
            var result = new List<(MethodBase, string, string, string)>();
            void Method(Type type, string name, Type[] args, string pre = null, string post = null, string fin = null)
            {
                var m = AccessTools.DeclaredMethod(type, name, args);
                if (m == null) { throw new MissingMethodException(type.FullName, name); }
                result.Add((m, pre, post, fin));
            }
            void Getter(Type type, string name, string post)
            {
                var m = AccessTools.PropertyGetter(type, name);
                if (m == null) { throw new MissingMethodException(type.FullName, name); }
                result.Add((m, null, post, null));
            }
            var comp = typeof(ObjStorageComp); var raw = typeof(ObjStorage); var empty = Type.EmptyTypes;
            Getter(comp, "UsedCapacity", nameof(Used));
            Method(comp, "UpdateCapacity", empty, post: nameof(Full));
            Method(comp, "UpdateStats", empty, pre: nameof(Stats));
            Method(comp, "OnSave", empty, post: nameof(Saved));
            Method(comp, "OnRemove", empty, nameof(Removing), fin: nameof(EndMutation));
            Method(comp, "RelocateTo", new[] { typeof(Entity) }, nameof(Relocate));
            Method(comp, "EjectAll", empty, nameof(Bulk), fin: nameof(EndBulk));
            Method(comp, "DisposeByQuality", new[] { typeof(float) }, nameof(Bulk), fin: nameof(EndBulk));
            Method(comp, "DisposeWhere", new[] { typeof(Func<ObjData, bool>), typeof(string) }, nameof(Bulk), fin: nameof(EndBulk));
            Method(comp, "Eject", new[] { typeof(ObjData), typeof(bool) }, nameof(Eject));
            Method(raw, "StoreObj", new[] { typeof(ObjData), comp }, nameof(Store), fin: nameof(EndMutation));
            Method(raw, "UnstoreObj", new[] { typeof(ObjData), comp }, nameof(Unstore), fin: nameof(EndMutation));
            Method(raw, "DestroyStoredObj", new[] { typeof(ObjData) }, nameof(Destroy), fin: nameof(EndMutation));
            Method(raw, "ValidateAll", new[] { typeof(Dictionary<uint, ObjData>) }, nameof(Validate), fin: nameof(EndMutation));
            Method(typeof(ObjsSys), "FindStoredObjData", new[] { typeof(int), typeof(ObjData[]), typeof(string), typeof(ObjData.SortMode) }, nameof(Find));
            Getter(typeof(ObjData), "IsAvailable", nameof(Available));
            Getter(typeof(ObjData), "IsAvailableIgnoreReservation", nameof(Available));
            Getter(typeof(ObjData), "CanSell", nameof(Available));
            Method(typeof(ObjData), "TryReserve", new[] { typeof(AIGoal) }, nameof(Reserve));
            Method(typeof(ObjData), "TryReserveForOperation", new[] { typeof(SurgicalOperation) }, nameof(Reserve));
            Method(typeof(ObjData), "TryUnreserve", new[] { typeof(AIGoal) }, post: nameof(Released));
            Method(typeof(ObjData), "TryUnreserveForOperation", new[] { typeof(SurgicalOperation) }, post: nameof(Released));
            Method(typeof(ObjDataData), "Serialize", new[] { typeof(ObjData) }, post: nameof(SavedObject));
            Method(comp, "ShowCustomTooltip", new[] { typeof(string), typeof(InfoItem) }, nameof(Tooltip));
            Method(typeof(ObjStorageTab), "LoadTopControls", new[] { typeof(WGroup) }, post: nameof(Header));
            Method(typeof(ObjStorageTab), "BuildContentsList", empty, post: nameof(Buttons));
            return result;
        }

        internal static void Install(Harmony harmony)
        {
            foreach (var binding in Bindings())
            {
                HarmonyMethod H(string name) => name == null ? null : new HarmonyMethod(typeof(RelayObjectCore), name) { priority = HarmonyLib.Priority.First };
                harmony.Patch(binding.target, H(binding.prefix), H(binding.postfix), null, H(binding.finalizer));
            }
        }

        private static StorageRelaySys Sys(ObjStorageComp s) => StorageRelaySys.Get(s?.S ?? A.S);
        private static bool Is(ObjStorageComp s) => StorageRelaySys.IsRelay(s);

        // The physical counter is touched only by Core, or reconciled from Core's
        // one authoritative ObjData set. Never persist the connected view.
        private static void Used(ObjStorageComp __instance, ref int __result)
        {
            if (!Is(__instance) || LocalDepth > 0) { return; }
            var sys = Sys(__instance);
            var group = sys?.ObjectGroup(__instance);
            if (group != null) { __result = group.Used; }
        }
        private static void Full(ObjStorageComp __instance)
        {
            if (Is(__instance) && LocalDepth == 0 && Sys(__instance) is StorageRelaySys sys)
            { __instance.IsFull = sys.ObjectIsFull(__instance); }
        }
        private static bool Stats(ObjStorageComp __instance) => !Is(__instance) || LocalDepth == 0;
        private static void Saved(ObjStorageComp __instance)
        {
            if (Is(__instance) && __instance.Data != null && Sys(__instance) is StorageRelaySys sys)
            { __instance.Data.SetInt("Used", sys.OwnedObjects(__instance.EntityId).Count); }
        }
        private static void SavedObject(ObjData data, ObjDataData __result)
        {
            // Defensive against a save callback inside a temporary access route.
            if (__result != null && data?.State == ObjState.Stored && routes?.ContainsKey(data) == true)
            { __result.StorageId = Owner(data); }
        }

        private sealed class Mutation : IDisposable
        {
            internal readonly StorageRelaySys System;
            internal ObjData Data;
            internal int Previous;
            private readonly NativeScope local;
            internal Mutation(StorageRelaySys system, ObjData data = null)
            { System = system; Data = data; Previous = data?.StorageId ?? 0; local = new NativeScope(); }
            public void Dispose()
            {
                // Failed ejection (including an exception before Core changed
                // State) must not permanently move the item's saved owner.
                if (Data?.State == ObjState.Stored && Previous != 0) { Data.StorageId = Previous; }
                local.Dispose();
                System?.ObjectChanged(); System?.RefreshObjectDisplays();
            }
        }
        private static void EndMutation(Mutation __state) => __state?.Dispose();

        private static bool Store(ObjData objData, ref ObjStorageComp storage, ref bool __result, out Mutation __state)
        {
            __state = null;
            if (!Is(storage)) { return true; }
            var sys = Sys(storage);
            var owner = sys?.DepositOwner(storage, objData);
            if (owner == null) { __result = false; return false; }
            storage = owner;
            __state = new Mutation(sys);
            // Network admission was checked above; the selected share has room.
            storage.IsFull = false;
            return true;
        }

        private static bool Unstore(ObjData objData, ref ObjStorageComp storage, ref bool __result, out Mutation __state)
        {
            __state = null;
            var sys = Sys(storage);
            var owner = sys?.ObjectNode(Owner(objData));
            if (owner == null && !Is(storage)) { return true; }
            storage ??= owner;
            if (objData == null || storage == null || (LocalDepth == 0 && sys?.ConnectedObjects(Owner(objData), storage) != true))
            { __result = false; return false; }
            __state = new Mutation(sys, objData);
            objData.StorageId = storage.EntityId;
            StorageRelaySys.EnsureNativeEjectCounter(storage);
            return true;
        }

        private static void Destroy(ObjData data, out Mutation __state)
        {
            var sys = StorageRelaySys.Get(A.S);
            __state = sys?.ObjectNode(Owner(data)) != null ? new Mutation(sys) : null;
        }
        private static void Removing(ObjStorageComp __instance, out Mutation __state)
        {
            __state = null;
            if (!Is(__instance)) { return; }
            var sys = Sys(__instance);
            sys?.CaptureObjectUpgrade(__instance);
            __state = new Mutation(sys);
            // Core's removal signal iterates StorageId directly and ejects only
            // this share, including sealed/offline objects. Do not rehome peers.
        }
        private static void Validate(out Mutation __state) => __state = new Mutation(StorageRelaySys.Get(A.S));

        private static bool Relocate(ObjStorageComp __instance, Entity target)
        {
            if (!Is(__instance) || target == null || !target.TryGetComponent<ObjStorageComp>(out var dest)) { return true; }
            var all = __instance.S.Query.GetAllObjs.Ask();
            foreach (var obj in all.Values)
            { if (obj.State == ObjState.Stored && obj.StorageId == __instance.EntityId) { obj.StorageId = dest.EntityId; } }
            using (new NativeScope())
            {
                __instance.UsedCapacity = 0;
                dest.UsedCapacity = all.Values.Count(o => o.State == ObjState.Stored && o.StorageId == dest.EntityId);
            }
            var sys = Sys(__instance); sys?.ObjectChanged(); sys?.InvalidateTopology(); sys?.RefreshObjectDisplays();
            QuantumObjectStorageNetworkSys.Get(dest.S)?.RefreshFor(dest);
            return false;
        }

        private static bool Find(ObjsSys __instance, int storageId, ObjData[] res, string searchStr, ObjData.SortMode sortMode, ref int __result)
        {
            if (LocalDepth > 0) { return true; }
            var sys = StorageRelaySys.Get(__instance.S);
            var storage = sys?.ObjectNode(storageId);
            if (storage == null) { return true; }
            var group = sys.ObjectGroup(storage, true);
            IEnumerable<ObjData> records = group?.Objects ?? sys.OwnedObjects(storageId);
            if (sortMode != ObjData.SortMode.Unsorted) { records = ObjData.SortedBy(__instance.S, records, sortMode); }
            string query = Strings.ToSearchString(searchStr, allowNull: true);
            __result = 0;
            foreach (var obj in records)
            {
                if (__result == res.Length) { break; }
                if (obj.MatchesSearchQuery(query)) { res[__result++] = obj; }
            }
            return false;
        }

        private static void Available(ObjData __instance, ref bool __result)
        {
            if (__result && LocalDepth == 0 && StorageRelaySys.Get(A.S)?.CanUseObject(__instance) == false) { __result = false; }
        }
        private static bool Reserve(ObjData __instance, ref bool __result)
        {
            if (StorageRelaySys.Get(A.S)?.CanUseObject(__instance) != false) { return true; }
            __result = false; return false;
        }
        private static void Released(ObjData __instance)
        {
            var sys = StorageRelaySys.Get(A.S);
            if (sys?.ObjectNode(Owner(__instance)) != null) { sys.ObjectChanged(); }
        }

        private static bool Bulk(ObjStorageComp __instance, out StorageRelaySys __state)
        {
            __state = null;
            if (!Is(__instance)) { return true; }
            var sys = Sys(__instance);
            if (sys?.ObjectGroup(__instance, true) == null) { return false; }
            __state = sys; sys.ObjectBulkDepth++; return true;
        }
        private static void EndBulk(StorageRelaySys __state)
        {
            if (__state == null) { return; }
            __state.ObjectBulkDepth--; __state.ObjectChanged(); __state.RefreshObjectDisplays();
        }
        private static bool Eject(ObjStorageComp __instance) => !Is(__instance) || LocalDepth > 0 ||
            Sys(__instance)?.ConnectedObjects(__instance.EntityId, __instance) == true;

        private static bool Tooltip(ObjStorageComp __instance, InfoItem item, ref string __result)
        {
            if (!Is(__instance) || item.Type != InfoItemType.Bar) { return true; }
            var sys = Sys(__instance); if (sys == null) { return true; }
            var group = sys.ObjectGroup(__instance, true);
            var local = sys.OwnedObjects(__instance.EntityId);
            var text = new StringBuilder(RelayCore.CapacityLabel(group?.Used ?? local.Count, group?.Capacity ?? StorageRelaySys.LocalCapacity(__instance)));
            var kinds = local.GroupBy(o => o.Def).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Id).ToList();
            foreach (var k in kinds.Take(10)) { text.AppendFormat("\n   {0} {1}: x{2}", GlyphMap.ArrowRight, k.Key.CodexLink, k.Count()); }
            if (kinds.Count > 10) { text.Append("\n   +" + (kinds.Count - 10)); }
            text.Append("\n" + sys.Status(__instance)); __result = text.ToString(); return false;
        }
        private static void Header(ObjStorageTab __instance, WGroup group, ObjStorageComp ___storage)
        {
            if (!Is(___storage)) { return; }
            RelayCore.StatusHeader(__instance, group, () => Sys(___storage)?.Status(___storage), ___storage.OnChange);
        }
        private static void Buttons(ObjStorageComp ___storage, WGroup ___contentsBlock)
        {
            if (!Is(___storage) || ___contentsBlock == null) { return; }
            bool online = Sys(___storage)?.ObjectGroup(___storage) != null;
            foreach (var button in ___contentsBlock.Container.GetComponentsInChildren<WButtonText>(true)) { button.SetInteractable(online); }
            foreach (var button in ___contentsBlock.Container.GetComponentsInChildren<WButtonIconText2>(true)) { button.SetInteractable(online); }
            foreach (var button in ___contentsBlock.Container.GetComponentsInChildren<WButtonIconText2Progress>(true)) { button.Item.SetInteractable(online); }
        }
    }
}
