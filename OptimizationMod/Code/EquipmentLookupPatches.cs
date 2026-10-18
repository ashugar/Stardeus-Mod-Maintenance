using System;
using System.Collections.Generic;
using Game.Systems.AI;
using Game.Systems.Items;
using Game.Systems.Policies;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Builds a lightweight, lifecycle-maintained index containing only object
    /// records that can structurally be equipment. Dynamic vanilla conditions
    /// such as availability, reservations, goals, compatibility, and blacklists
    /// are still evaluated for every query.
    /// </summary>
    internal static class EquipmentLookupIndex {
        private static readonly Dictionary<InventorySlotType, List<ObjData>> ByType =
            new Dictionary<InventorySlotType, List<ObjData>>();
        private static readonly Dictionary<int, List<ObjData>> ByTypeId =
            new Dictionary<int, List<ObjData>>();
        private static readonly List<ObjData> AllEquipment = new List<ObjData>();
        private static readonly HashSet<uint> EquipmentIds = new HashSet<uint>();
        private static ObjsSys cachedSystem;
        private static int cachedObjectCount = -1;
        private static uint cachedLastObjectId;

        internal static void Reset() {
            cachedSystem = null;
            cachedObjectCount = -1;
            cachedLastObjectId = 0;
            ByType.Clear();
            ByTypeId.Clear();
            AllEquipment.Clear();
            EquipmentIds.Clear();
        }

        internal static void Ensure(ObjsSys system) {
            if (ReferenceEquals(cachedSystem, system)
                    && cachedObjectCount == system.All.Count
                    && cachedLastObjectId == ObjData.LastId) {
                return;
            }

            cachedSystem = system;
            cachedObjectCount = system.All.Count;
            cachedLastObjectId = ObjData.LastId;
            ByType.Clear();
            ByTypeId.Clear();
            AllEquipment.Clear();
            EquipmentIds.Clear();

            foreach (ObjData obj in system.All.Values) {
                InventorySlotType slotType = obj.Item?.SlotType;
                if (slotType == null) { continue; }
                AddToIndices(obj, slotType);
            }

        }

        internal static void AddPersistentObject(ObjsSys system, ObjData obj) {
            if (!ReferenceEquals(cachedSystem, system) || obj == null) { return; }

            InventorySlotType slotType = obj.Item?.SlotType;
            bool equipmentAdded = slotType != null && EquipmentIds.Add(obj.Id);
            if (equipmentAdded) {
                AddToIndices(obj, slotType);
            }

            cachedObjectCount = system.All.Count;
            cachedLastObjectId = ObjData.LastId;
        }

        internal static void RemovePersistentObject(ObjsSys system, ObjData obj) {
            if (!ReferenceEquals(cachedSystem, system) || obj == null) { return; }

            InventorySlotType slotType = obj.Item?.SlotType;
            bool equipmentRemoved = slotType != null && EquipmentIds.Remove(obj.Id);
            if (equipmentRemoved) {
                AllEquipment.Remove(obj);
                if (ByType.TryGetValue(slotType, out List<ObjData> typeList)) {
                    typeList.Remove(obj);
                }
                if (ByTypeId.TryGetValue(slotType.IdH, out List<ObjData> idList)) {
                    idList.Remove(obj);
                }
            }

            cachedObjectCount = system.All.Count;
            cachedLastObjectId = ObjData.LastId;
        }

        internal static List<ObjData> ForType(InventorySlotType type) {
            return type != null && ByType.TryGetValue(type, out List<ObjData> list)
                ? list
                : null;
        }

        internal static List<ObjData> ForTypeId(int typeId) {
            if (typeId == 0) { return AllEquipment; }
            return ByTypeId.TryGetValue(typeId, out List<ObjData> list)
                ? list
                : null;
        }

        private static void AddToIndices(
                ObjData obj, InventorySlotType slotType) {
            EquipmentIds.Add(obj.Id);
            AllEquipment.Add(obj);
            if (!ByType.TryGetValue(slotType, out List<ObjData> typeList)) {
                typeList = new List<ObjData>();
                ByType.Add(slotType, typeList);
            }
            typeList.Add(obj);

            if (!ByTypeId.TryGetValue(slotType.IdH, out List<ObjData> idList)) {
                idList = new List<ObjData>();
                ByTypeId.Add(slotType.IdH, idList);
            }
            idList.Add(obj);
        }
    }

    internal static class PersistentObjectAddedPatch {
        internal static void Postfix(
                ObjsSys __instance,
                ObjData data,
                bool __result) {
            if (__result) {
                EquipmentLookupIndex.AddPersistentObject(__instance, data);
            }
        }
    }

    internal static class PersistentObjectRemovedPatch {
        internal static void Postfix(
                ObjsSys __instance,
                ObjData data,
                ObjState state,
                bool __result) {
            if (__result && state == ObjState.Destroyed) {
                EquipmentLookupIndex.RemovePersistentObject(__instance, data);
            }
        }
    }

    internal static class BestAvailableEquipmentPatch {
        internal static bool Prefix(
                ObjsSys __instance,
                InventoryItemSlot slot,
                HashSet<uint> blacklist,
                HashSet<int> storageBlacklist,
                ref ObjData __result) {
            if (__instance == null || slot?.Inventory?.Owner == null) {
                return true;
            }

            try {
                EquipmentLookupIndex.Ensure(__instance);
                List<ObjData> candidates = EquipmentLookupIndex.ForType(slot.Type);
                EquipmentPolicyFilter filter = EquipmentPolicyFilter.For(
                    __instance.S.Query.EquipmentPolicyFor.Ask(
                        slot.Inventory.Owner, slot.Type),
                    slot.Inventory.Owner);
                __result = FindBest(
                    slot, candidates,
                    blacklist, storageBlacklist, filter);
                return false;
            } catch (Exception ex) {
                D.Err("Optimization Mod best-equipment query failed open: {0}", ex);
                return true;
            }
        }

        private static ObjData FindBest(
                InventoryItemSlot slot,
                List<ObjData> candidates,
                HashSet<uint> blacklist,
                HashSet<int> storageBlacklist,
                EquipmentPolicyFilter filter) {
            ObjData bestItem = filter.Allows(slot.ObjData)
                ? slot.ObjData
                : null;
            if (candidates != null) {
                for (int i = 0; i < candidates.Count; i++) {
                    ObjData candidate = candidates[i];
                    if (!IsEligible(slot, candidate, blacklist,
                            storageBlacklist, filter)) {
                        continue;
                    }
                    if (bestItem == null) {
                        bestItem = candidate;
                    } else {
                        // Core owns value, quality, origin, wearer-mood, and any
                        // future tie-breaking behavior.
                        bestItem = slot.BetterItem(bestItem, candidate);
                    }
                }
            }
            if (ReferenceEquals(bestItem, slot.ObjData)) {
                return null;
            }
            return bestItem;
        }

        private static bool IsEligible(
                InventoryItemSlot slot,
                ObjData candidate,
                HashSet<uint> blacklist,
                HashSet<int> storageBlacklist,
                EquipmentPolicyFilter filter) {
            if (!candidate.IsAvailable) {
                return false;
            }
            if (candidate.Item == null || candidate.Item.SlotType != slot.Type) {
                return false;
            }
            if (candidate.Obj?.Goal?.IsByPlayer == true) {
                return false;
            }
            if (!candidate.Item.IsCompatibleWith(slot.Inventory.Owner)) {
                return false;
            }
            if (!filter.Allows(candidate)) {
                return false;
            }
            if (blacklist?.Contains(candidate.Id) == true) {
                return false;
            }
            if (candidate.State == ObjState.Stored
                    && storageBlacklist?.Contains(candidate.StorageId) == true) {
                return false;
            }
            return true;
        }
    }

    internal static class FindCompatibleEquipmentPatch {
        internal static bool Prefix(
                ObjsSys __instance,
                AIAgentComp agent,
                int invSlotId,
                ObjData[] res,
                HashSet<uint> blacklist,
                HashSet<int> storageBlacklist,
                ref int __result) {
            if (__instance == null || agent?.BeingData == null || res == null) {
                return true;
            }

            try {
                EquipmentLookupIndex.Ensure(__instance);
                List<ObjData> candidates = EquipmentLookupIndex.ForTypeId(invSlotId);
                InventorySlotType slotType = invSlotId == 0
                    ? null
                    : InventorySlotType.FastGet(invSlotId);
                EquipmentPolicyFilter filter = EquipmentPolicyFilter.For(
                    __instance.S.Query.EquipmentPolicyFor.Ask(
                        agent.BeingData, slotType),
                    agent.BeingData);
                int found = 0;

                if (candidates != null) {
                    for (int i = 0; i < candidates.Count; i++) {
                        ObjData objData = candidates[i];
                        var item = objData.Item;
                        if (item == null) { continue; }
                        if (!objData.IsAvailable) { continue; }
                        if (invSlotId != 0 && item.SlotType.IdH != invSlotId) {
                            continue;
                        }
                        if (objData.Obj?.Goal?.IsByPlayer == true) { continue; }
                        if (blacklist?.Contains(objData.Id) == true) { continue; }
                        if (objData.State == ObjState.Stored
                                && storageBlacklist?.Contains(objData.StorageId) == true) {
                            continue;
                        }
                        if (!filter.Allows(objData)) { continue; }
                        if (!item.IsCompatibleWith(agent.BeingData)) { continue; }

                        if (found < res.Length) {
                            res[found++] = objData;
                        }
                        if (found == res.Length) { break; }
                    }
                }

                __result = found;
                return false;
            } catch (Exception ex) {
                D.Err("Optimization Mod compatible-equipment query failed open: {0}", ex);
                return true;
            }
        }
    }

    internal static class FindCompatibleEquipmentUiPatch {
        internal static bool Prefix(
                ObjsSys __instance,
                AIAgentComp agent,
                int invSlotId,
                List<ObjData> res,
                ref int __result) {
            if (__instance == null || agent?.BeingData == null || res == null) {
                return true;
            }

            int initialCount = res.Count;
            try {
                EquipmentLookupIndex.Ensure(__instance);
                List<ObjData> candidates = EquipmentLookupIndex.ForTypeId(invSlotId);
                int found = 0;

                if (candidates != null) {
                    for (int i = 0; i < candidates.Count; i++) {
                        ObjData objData = candidates[i];
                        var item = objData.Item;
                        if (item == null) { continue; }
                        if (!objData.IsAvailable) { continue; }
                        if (invSlotId != 0 && item.SlotType.IdH != invSlotId) {
                            continue;
                        }
                        if (!item.IsCompatibleWith(agent.BeingData)) { continue; }
                        res.Add(objData);
                        found++;
                    }
                }

                __result = found;
                return false;
            } catch (Exception ex) {
                if (res.Count > initialCount) {
                    res.RemoveRange(initialCount, res.Count - initialCount);
                }
                D.Err("Optimization Mod equipment-UI query failed open: {0}", ex);
                return true;
            }
        }
    }
}
