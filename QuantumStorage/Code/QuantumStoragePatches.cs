using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Story.Events;
using Game.Systems.Inventory;
using Game.Systems.Items;
using Game.Systems.Trade;
using HarmonyLib;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.QuantumStorage
{
    internal struct QuantumDamageState
    {
        public QuantumStorageNetworkSys MaterialSystem;
        public QuantumStorageNetwork MaterialNetwork;
        public MatStorageComp MaterialSource;
        public QuantumObjectStorageNetworkSys ObjectSystem;
        public QuantumObjectStorageNetwork ObjectNetwork;
        public ObjStorageComp ObjectSource;
        public float HealthBefore;
        public float IncomingDamage;
        public bool WasOnFire;
    }

    // Core's three-argument compatibility overload forwards here. Patch the
    // implementation once so both callers work without duplicate feedback.
    [HarmonyPatch(typeof(DamageableComp), nameof(DamageableComp.TakeDamage),
        new Type[] { typeof(float), typeof(DamageType), typeof(bool), typeof(bool) })]
    internal static class DamageableTakeDamagePatch
    {
        private static void Prefix(DamageableComp __instance, float amount, DamageType type, ref QuantumDamageState __state)
        {
            __state = default;
            if (amount <= 0f || __instance.IsDestroyed || (type != DamageType.Heat && type != DamageType.Explosive))
            {
                return;
            }

            if (__instance.Entity == null)
            {
                return;
            }

            QuantumStorageNetworkSys materialSystem = QuantumStorageNetworkSys.Get(__instance.S);
            if (materialSystem != null && !materialSystem.IsApplyingFeedback &&
                __instance.Entity.TryGetComponent(out MatStorageComp materialSource) && materialSource.IsUsable &&
                materialSystem.TryGetNetwork(materialSource, out QuantumStorageNetwork materialNetwork))
            {
                __state = new QuantumDamageState
                {
                    MaterialSystem = materialSystem,
                    MaterialNetwork = materialNetwork,
                    MaterialSource = materialSource,
                    HealthBefore = __instance.Health,
                    IncomingDamage = amount,
                    WasOnFire = materialSource.Entity.Flammable?.IsOnFire == true
                };
                return;
            }

            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(__instance.S);
            if (objectSystem != null && !objectSystem.IsApplyingFeedback &&
                __instance.Entity.TryGetComponent(out ObjStorageComp objectSource) &&
                objectSource.Tile.ENode?.IsPowerable == true &&
                objectSystem.TryGetNetwork(objectSource, out QuantumObjectStorageNetwork objectNetwork))
            {
                __state = new QuantumDamageState
                {
                    ObjectSystem = objectSystem,
                    ObjectNetwork = objectNetwork,
                    ObjectSource = objectSource,
                    HealthBefore = __instance.Health,
                    IncomingDamage = amount,
                    WasOnFire = objectSource.Entity.Flammable?.IsOnFire == true
                };
            }
        }

        private static void Postfix(DamageableComp __instance, DamageType type, QuantumDamageState __state)
        {
            if (__instance.Health >= __state.HealthBefore)
            {
                return;
            }

            if (__state.MaterialSystem != null)
            {
                try
                {
                    __state.MaterialSystem.HandleDamageFeedback(
                        __state.MaterialNetwork,
                        __state.MaterialSource,
                        __state.IncomingDamage,
                        type,
                        __state.WasOnFire);
                }
                catch (Exception ex)
                {
                    D.LogEx(ex, "Quantum Storage feedback handling failed; the original damage remains unchanged.");
                }

                return;
            }

            if (__state.ObjectSystem != null)
            {
                try
                {
                    __state.ObjectSystem.HandleDamageFeedback(
                        __state.ObjectNetwork,
                        __state.ObjectSource,
                        __state.IncomingDamage,
                        type,
                        __state.WasOnFire);
                }
                catch (Exception ex)
                {
                    D.LogEx(ex, "Quantum object-storage feedback handling failed; the original damage remains unchanged.");
                }
            }
        }
    }

    [HarmonyPatch(typeof(FlammableComp), nameof(FlammableComp.Update))]
    internal static class FlammableUpdatePatch
    {
        private static void Postfix(FlammableComp __instance)
        {
            if (__instance.IsOnFire || __instance.Entity == null)
            {
                return;
            }

            if (__instance.Entity.TryGetComponent(out MatStorageComp materialStorage) &&
                QuantumStorageNetworkSys.TryGetNode(materialStorage, out _))
            {
                QuantumStorageNetworkSys.Get(__instance.S)?.EndFireIncident(materialStorage.EntityId);
                return;
            }

            if (__instance.Entity.TryGetComponent(out ObjStorageComp objectStorage) &&
                QuantumObjectStorageNetworkSys.TryGetNode(objectStorage, out _))
            {
                QuantumObjectStorageNetworkSys.Get(__instance.S)?.EndFireIncident(objectStorage.EntityId);
            }
        }
    }

    [HarmonyPatch(typeof(ObjStorageComp), nameof(ObjStorageComp.OnLateReady))]
    internal static class ObjStorageOnLateReadyPatch
    {
        private const int ObjsReady = -1611921394;

        private static void Postfix(ObjStorageComp __instance, bool wasLoaded)
        {
            Ready.When(ObjsReady, () => QuantumObjectStorageNetworkSys.Get(__instance.S)?.Attach(__instance));
        }
    }

    [HarmonyPatch(typeof(ObjStorageComp), nameof(ObjStorageComp.Receive))]
    internal static class ObjStorageReceivePatch
    {
        private const int ConstructionFinishedMessage = -1901802648;
        private const int ObjsReady = -1611921394;

        private static void Postfix(ObjStorageComp __instance, int message)
        {
            if (message == ConstructionFinishedMessage)
            {
                Ready.When(ObjsReady, () => QuantumObjectStorageNetworkSys.Get(__instance.S)?.Attach(__instance));
            }
        }
    }

    [HarmonyPatch(typeof(ObjStorageComp), nameof(ObjStorageComp.OnRemove))]
    internal static class ObjStorageOnRemovePatch
    {
        private static void Prefix(ObjStorageComp __instance, out QuantumObjectStorageNetworkSys __state)
        {
            __state = QuantumObjectStorageNetworkSys.Get(__instance.S);
            __state?.PrepareRemove(__instance);
        }

        private static void Postfix(ObjStorageComp __instance, QuantumObjectStorageNetworkSys __state)
        {
            __state?.CompleteRemove(__instance);
        }

        private static Exception Finalizer(Exception __exception, ObjStorageComp __instance, QuantumObjectStorageNetworkSys __state)
        {
            if (__exception != null) { __state?.RefreshFor(__instance); }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ObjStorageComp), nameof(ObjStorageComp.RelocateTo))]
    internal static class ObjStorageRelocatePatch
    {
        private static bool Prefix(ObjStorageComp __instance, Entity target, out QuantumObjectStorageNetworkSys __state)
        {
            __state = QuantumObjectStorageNetworkSys.Get(__instance.S);
            return __state == null || !__state.Relocate(__instance, target);
        }

        private static Exception Finalizer(Exception __exception, ObjStorageComp __instance, Entity target, QuantumObjectStorageNetworkSys __state)
        {
            if (__exception != null)
            {
                __state?.RefreshFor(__instance);
                if (target != null && target.TryGetComponent(out ObjStorageComp other)) { __state?.RefreshFor(other); }
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ObjStorageComp), nameof(ObjStorageComp.OnSave))]
    internal static class ObjStorageOnSavePatch
    {
        private static void Postfix(ObjStorageComp __instance)
        {
            QuantumObjectStorageNetworkSys system = QuantumObjectStorageNetworkSys.Get(__instance.S);
            if (system != null && system.TryGetNetwork(__instance, out _) && __instance.Data != null)
            {
                __instance.Data.SetInt("Used", system.CountOwnedBy(__instance.EntityId));
            }
        }
    }

    [HarmonyPatch(typeof(ObjsSys), "FindStoredObjData")]
    internal static class ObjsFindStoredObjDataPatch
    {
        private static bool Prefix(ObjsSys __instance, int storageId, ObjData[] res, string searchStr, ObjData.SortMode sortMode, ref int __result)
        {
            QuantumObjectStorageNetworkSys system = QuantumObjectStorageNetworkSys.Get(__instance.S);
            if (system == null || !system.TryGetNetwork(storageId, out QuantumObjectStorageNetwork network))
            {
                return true;
            }

            int count = 0;
            string searchQuery = Strings.ToSearchString(searchStr, allowNull: true);
            IEnumerable<ObjData> objs = sortMode == ObjData.SortMode.Unsorted
                ? __instance.All.Values
                : ObjData.SortedBy(__instance.S, __instance.All.Values, sortMode);
            foreach (ObjData objData in objs)
            {
                if (network.ContainsStorageId(objData.StorageId) && objData.MatchesSearchQuery(searchQuery))
                {
                    res[count++] = objData;
                    if (count == res.Length)
                    {
                        break;
                    }
                }
            }

            __result = count;
            return false;
        }
    }

    internal sealed class QuantumObjectMutationState
    {
        public QuantumObjectStorageNetworkSys System;
        public QuantumObjectStorageNetwork Network;
        public QuantumObjectStorageNetwork Destination;
        public ObjData Record;
        public int PreviousStorageId;
        public int RequestedStorageId;
        public bool RestoreOwnerOnFailure;
        private bool completed;

        internal void Complete(Exception error = null)
        {
            if (completed) { return; }
            completed = true;
            // Core can return false AFTER ejecting or destroying the record.
            // Restore only a still-stored temporary access-point redirection.
            if (RestoreOwnerOnFailure && Record.State == ObjState.Stored && PreviousStorageId != 0 &&
                Record.StorageId == RequestedStorageId) { Record.StorageId = PreviousStorageId; }
            System.CompleteObjectMutation(this, error != null);
        }
    }

    internal sealed class QuantumObjectBulkMutationState
    {
        private bool completed;

        internal QuantumObjectStorageNetworkSys System;
        internal QuantumObjectStorageNetwork Network;

        internal void Complete(Exception error = null)
        {
            if (completed)
            {
                return;
            }

            completed = true;
            try { if (error != null) { System?.Refresh(Network); } }
            finally { System?.EndBulkMutation(Network); }
        }
    }

    [HarmonyPatch]
    internal static class ObjStorageEjectAllPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(ObjStorageComp), nameof(ObjStorageComp.EjectAll), Type.EmptyTypes);
            yield return AccessTools.DeclaredMethod(typeof(ObjStorageComp), nameof(ObjStorageComp.DisposeByQuality), new[] { typeof(float) });
            yield return AccessTools.DeclaredMethod(typeof(ObjStorageComp), nameof(ObjStorageComp.DisposeWhere), new[] { typeof(Func<ObjData, bool>), typeof(string) });
        }

        private static void Prefix(ObjStorageComp __instance, out QuantumObjectBulkMutationState __state)
        {
            __state = null;
            QuantumObjectStorageNetworkSys system = QuantumObjectStorageNetworkSys.Get(__instance.S);
            QuantumObjectStorageNetwork network = system?.BeginBulkMutation(__instance);
            if (network != null)
            {
                __state = new QuantumObjectBulkMutationState
                {
                    System = system,
                    Network = network
                };
            }
        }

        private static void Postfix(QuantumObjectBulkMutationState __state)
        {
            __state?.Complete();
        }

        private static Exception Finalizer(Exception __exception, QuantumObjectBulkMutationState __state)
        {
            __state?.Complete(__exception);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ObjStorageComp), nameof(ObjStorageComp.ForEachStored), new[] { typeof(Action<ObjData>), typeof(string), typeof(ObjData.SortMode?) })]
    internal static class ObjStorageCountBeforeEnumerationPatch
    {
        private static void Prefix(ObjStorageComp __instance) =>
            QuantumObjectStorageNetworkSys.Get(__instance.S)?.ReconcileForEnumeration(__instance);
    }

    [HarmonyPatch(typeof(ObjStorage), nameof(ObjStorage.ValidateAll), new[] { typeof(Dictionary<uint, ObjData>) })]
    internal static class ObjStorageCountAfterValidationPatch
    {
        private static void Postfix(GameState ___S) => QuantumObjectStorageNetworkSys.Get(___S)?.ReconcileCounts();
    }

    [HarmonyPatch(typeof(ObjStorage), "StoreObj", new[] { typeof(ObjData), typeof(ObjStorageComp) })]
    internal static class ObjStorageStoreObjPatch
    {
        private static void Prefix(ObjData objData, ObjStorageComp storage, out QuantumObjectMutationState __state) =>
            __state = QuantumObjectStorageNetworkSys.Get(storage?.S ?? A.S)?.BeginObjectMutation(objData, storage);

        private static void Postfix(QuantumObjectMutationState __state) => __state?.Complete();

        private static Exception Finalizer(Exception __exception, QuantumObjectMutationState __state)
        { __state?.Complete(__exception); return __exception; }
    }

    [HarmonyPatch(typeof(ObjStorage), "UnstoreObj", new[] { typeof(ObjData), typeof(ObjStorageComp) })]
    internal static class ObjStorageUnstoreObjPatch
    {
        private static void Prefix(ObjData objData, ObjStorageComp storage, out QuantumObjectMutationState __state)
        {
            QuantumObjectStorageNetworkSys system = QuantumObjectStorageNetworkSys.Get(storage?.S ?? A.S);
            __state = system?.BeginObjectMutation(objData, storage);
            if (__state == null) { return; }
            __state.RestoreOwnerOnFailure = true;
            system.PrepareCrossEndpointUnstore(objData, storage);
        }

        private static void Postfix(QuantumObjectMutationState __state) => __state?.Complete();

        private static Exception Finalizer(Exception __exception, QuantumObjectMutationState __state)
        { __state?.Complete(__exception); return __exception; }
    }

    [HarmonyPatch(typeof(ObjStorage), "DestroyStoredObj", new[] { typeof(ObjData) })]
    internal static class ObjStorageDestroyStoredObjPatch
    {
        private static void Prefix(ObjData data, GameState ___S, out QuantumObjectMutationState __state)
            => __state = QuantumObjectStorageNetworkSys.Get(___S)?.BeginObjectMutation(data);

        private static void Postfix(QuantumObjectMutationState __state) => __state?.Complete();

        private static Exception Finalizer(Exception __exception, QuantumObjectMutationState __state)
        { __state?.Complete(__exception); return __exception; }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.OnLateReady))]
    internal static class MatStorageOnLateReadyPatch
    {
        private static void Postfix(MatStorageComp __instance)
        {
            QuantumStorageNetworkSys.Get(__instance.S)?.Attach(__instance);
        }
    }

    [HarmonyPatch]
    internal static class MatStorageContentsMutationPatch
    {
        private static readonly string[] MutationMethods = { "AddMat", "RemoveMat", "ClearMats" };

        private static bool Prepare()
        {
            return QuantumStorageReflection.HasUsedCapacityCache;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            for (int i = 0; i < MutationMethods.Length; i++)
            {
                MethodInfo method = AccessTools.DeclaredMethod(typeof(MatStorageComp), MutationMethods[i]);
                if (method != null)
                {
                    yield return method;
                }
            }
        }

        private static void Postfix(MatStorageComp __instance)
        {
            try
            {
                QuantumStorageNetworkSys.Get(__instance.S)?.HandleStorageContentsChanged(__instance);
            }
            catch (Exception ex)
            {
                D.LogEx(ex, "Quantum Storage failed to synchronize Core's cached used capacity; the shared material pool remains unchanged.");
            }
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.Receive))]
    internal static class MatStorageReceivePatch
    {
        private const int ConstructionFinishedMessage = -1901802648;

        private static void Postfix(MatStorageComp __instance, int message)
        {
            if (message == ConstructionFinishedMessage)
            {
                QuantumStorageNetworkSys.Get(__instance.S)?.Attach(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.OnRemove))]
    internal static class MatStorageOnRemovePatch
    {
        private static bool Prefix(MatStorageComp __instance)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            return system == null || !system.HandleRemove(__instance);
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.RelocateTo))]
    internal static class MatStorageRelocatePatch
    {
        private static bool Prefix(MatStorageComp __instance)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            return system == null || !system.TryGetNetwork(__instance, out _);
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.OnExplode))]
    internal static class MatStorageOnExplodePatch
    {
        private static bool Prefix(MatStorageComp __instance)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            return system == null || !system.TryGetNetwork(__instance, out _) || system.IsLastNode(__instance);
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.OnSave))]
    internal static class MatStorageOnSavePatch
    {
        private static void Postfix(MatStorageComp __instance)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            if (system != null && system.TryGetNetwork(__instance, out _) && !system.IsReporter(__instance) && __instance.Data != null)
            {
                __instance.Data.SetMats("RawMats", null);
            }
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.AppendMatCounts))]
    internal static class MatStorageAppendMatCountsPatch
    {
        private static bool Prefix(MatStorageComp __instance)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            return system == null || !system.TryGetNetwork(__instance, out _) || system.IsReporter(__instance);
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.Available))]
    internal static class MatStorageAvailablePatch
    {
        private static void Postfix(MatStorageComp __instance, MatType type, ref int __result)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            if (system != null && system.TryGetNetwork(__instance, out QuantumStorageNetwork network))
            {
                __result = network.Available(type);
            }
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.AvailableForGroup), new Type[] { typeof(int), typeof(List<Mat>) })]
    internal static class MatStorageGroupOptionsPatch
    {
        private static void Postfix(MatStorageComp __instance, int groupId, List<Mat> mats, ref bool __result)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            if (system == null || !system.TryGetNetwork(__instance, out QuantumStorageNetwork network))
            {
                return;
            }

            // Core can return zero-sized and duplicate options. Preserve a rejected
            // query, including its group restriction, and clear any reused-list data.
            if (!__result || !__instance.AcceptsGroup(groupId) || mats == null)
            {
                mats?.Clear();
                __result = false;
                return;
            }

            __result = network.SanitizeGroupOptions(groupId, mats);
        }
    }

    [HarmonyPatch(typeof(InventorySys), nameof(InventorySys.MarkUnreachableByFlying))]
    internal static class InventoryMarkUnreachableByFlyingPatch
    {
        private static bool Prefix(InventorySys __instance, MatType mat, Being worker)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            if (system == null || !system.HasAttachedNetworks)
            {
                return true;
            }

            if (mat == null || worker == null || worker.Motion.IsGrounded)
            {
                return false;
            }

            List<IMatStorage> storages = __instance.Storages;
            for (int i = storages.Count - 1; i >= 0; i--)
            {
                IMatStorage storage = storages[i];
                if (!storage.IsPublic || storage.StorageType == StorageType.StoreOnly || !storage.Entity.IsActive)
                {
                    continue;
                }

                if (storage is MatStorageComp quantumStorage &&
                    system.TryGetNetwork(quantumStorage, out QuantumStorageNetwork network))
                {
                    if (network.Available(mat) < 1)
                    {
                        continue;
                    }
                }
                else if (storage.AvailableWithoutReservation(mat) < 1)
                {
                    continue;
                }

                storage.MarkAsUnreachableByFlying(worker);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.AvailableCapacity), MethodType.Getter)]
    internal static class MatStorageAvailableCapacityPatch
    {
        private static void Postfix(MatStorageComp __instance, ref int __result)
        {
            if (__result < 0 && QuantumStorageNetworkSys.TryGetNode(__instance, out _))
            {
                __result = 0;
            }
        }
    }

    [HarmonyPatch(typeof(MatStorageComp), nameof(MatStorageComp.FillPercent), MethodType.Getter)]
    internal static class MatStorageFillPercentPatch
    {
        private static void Postfix(MatStorageComp __instance, ref float __result)
        {
            if (QuantumStorageNetworkSys.TryGetNode(__instance, out _))
            {
                __result = float.IsNaN(__result) ? 0f : Mathf.Clamp01(__result);
            }
        }
    }

    [HarmonyPatch(typeof(InventorySys), nameof(InventorySys.HasEnough))]
    internal static class InventoryHasEnoughPatch
    {
        private static bool Prefix(InventorySys __instance, MatType type, int amount, int islandId, Being being, ref bool __result)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            if (system == null || !system.HasAttachedNetworks)
            {
                return true;
            }

            __result = system.HasEnough(__instance, type, amount, islandId, being);
            return false;
        }
    }

    [HarmonyPatch(typeof(InventorySys), nameof(InventorySys.CountByGroup))]
    internal static class InventoryCountByGroupPatch
    {
        private static bool Prefix(InventorySys __instance, int group, ref int __result)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            if (system == null || !system.HasAttachedNetworks)
            {
                return true;
            }

            __result = system.CountByGroup(__instance, group);
            return false;
        }
    }

    [HarmonyPatch(typeof(TradeSys), "LoadOurItems")]
    internal static class TradeSysLoadOurItemsPatch
    {
        private static void Postfix(TradeSys __instance, List<TradeableItem> items)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(__instance.S);
            if (system != null && system.HasAttachedNetworks)
            {
                system.CorrectTradeMaterialCounts(__instance.S.Sys.Inventory, items);
            }
        }
    }

    [HarmonyPatch(typeof(ColonizePlanetCutscene), "RemoveWealth")]
    internal static class ColonizePlanetRemoveWealthPatch
    {
        private static bool Prefix(GameState ___S, ref IEnumerator __result)
        {
            QuantumStorageNetworkSys system = QuantumStorageNetworkSys.Get(___S);
            if (system == null || !system.HasAttachedNetworks)
            {
                return true;
            }

            __result = system.RemoveWealthDeduplicated();
            return false;
        }
    }
}
