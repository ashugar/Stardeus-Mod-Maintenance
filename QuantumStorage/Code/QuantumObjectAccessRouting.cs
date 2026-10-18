using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Items;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.QuantumStorage
{
    internal static class QuantumObjectAccessRouting
    {
        [ThreadStatic]
        private static AIAgentComp currentAgent;

        private static bool loggedFailure;

        internal static AIAgentComp PushAgent(AIAgentComp agent)
        {
            AIAgentComp previous = currentAgent;
            currentAgent = agent;
            return previous;
        }

        internal static void RestoreAgent(AIAgentComp previous)
        {
            currentAgent = previous;
        }

        internal static bool TryResolveCurrentAgentAccessPoint(
            ObjData data,
            GameState state,
            out ObjStorageComp storage)
        {
            return TryResolveAgentAccessPoint(data, state, currentAgent, currentAgent?.PosIdx ?? -1, out storage);
        }

        internal static QuantumObjectStorageRouteState RedirectStorageOwner(
            ObjData data,
            GameState state,
            AIAgentComp agent,
            int anchorPos)
        {
            QuantumObjectStorageRouteState routeState = default;
            if (!TryResolveAgentAccessPoint(data, state, agent, anchorPos, out ObjStorageComp storage) ||
                storage.EntityId == data.StorageId)
            {
                return routeState;
            }

            routeState.Data = data;
            routeState.PreviousStorageId = data.StorageId;
            routeState.RelayRoute = Relays.RelayObjectCore.BeginRoute(data, state);
            data.StorageId = storage.EntityId;
            return routeState;
        }

        internal static void RestoreStorageOwner(QuantumObjectStorageRouteState routeState)
        {
            if (routeState.Data != null && routeState.PreviousStorageId != 0 &&
                routeState.Data.State == ObjState.Stored && routeState.Data.StorageId != 0)
            {
                routeState.Data.StorageId = routeState.PreviousStorageId;
            }
            if (routeState.RelayRoute) { Relays.RelayObjectCore.EndRoute(routeState.Data); }
        }

        private static bool TryResolveAgentAccessPoint(
            ObjData data,
            GameState state,
            AIAgentComp agent,
            int anchorPos,
            out ObjStorageComp storage)
        {
            storage = null;
            if (data == null || state == null || agent == null || data.State != ObjState.Stored)
            {
                return false;
            }

            try
            {
                if (!EntityUtils.IsWithinBounds(anchorPos))
                {
                    anchorPos = agent.PosIdx;
                }
                QuantumObjectStorageNetworkSys system = QuantumObjectStorageNetworkSys.Get(state);
                if (system != null && system.TrySelectNearestStoredObjectAccessPoint(
                    data,
                    null,
                    anchorPos,
                    candidate => IsUsableByAgent(state, agent, candidate),
                    out storage)) { return true; }
                return Relays.StorageRelaySys.Get(state)?.TrySelectObjectAccess(data, anchorPos,
                    candidate => IsUsableByAgent(state, agent, candidate), out storage) == true;
            }
            catch (Exception ex)
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    D.LogEx(ex, "Quantum Storage nearest object endpoint routing failed; Core's serialized owner endpoint remains in use.");
                }
                storage = null;
                return false;
            }
        }

        private static bool IsUsableByAgent(
            GameState state,
            AIAgentComp agent,
            ObjStorageComp storage)
        {
            if (storage == null || !EntityUtils.IsWithinBounds(storage.InPort) ||
                agent.BB.IsBlacklisted2(storage.EntityId) || !agent.IsFeelingSafeAt(storage.InPort))
            {
                return false;
            }

            if (!agent.Motion.IsGrounded)
            {
                return true;
            }

            int islandId = state.Sys.Areas.IslandAt(agent.PosIdx);
            return state.Sys.Areas.CanReachGroundedIslandToPos(islandId, storage.InPort);
        }
    }

    internal struct QuantumObjectStorageRouteState
    {
        internal ObjData Data;
        internal int PreviousStorageId;
        internal bool RelayRoute;
    }

    [HarmonyPatch]
    internal static class QuantumObjectPositionRoutingContextPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(AIActionFindEquipment), nameof(AIActionFindEquipment.Execute));
            yield return AccessTools.DeclaredMethod(typeof(AIActionFindBestEquipment), nameof(AIActionFindBestEquipment.Execute));
            yield return AccessTools.DeclaredMethod(typeof(AIActionEquipItem), nameof(AIActionEquipItem.Execute));
        }

        private static void Prefix(AIAgentComp agent, out AIAgentComp __state)
        {
            __state = QuantumObjectAccessRouting.PushAgent(agent);
        }

        private static Exception Finalizer(Exception __exception, AIAgentComp __state)
        {
            QuantumObjectAccessRouting.RestoreAgent(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(ObjData), nameof(ObjData.PosIdx))]
    internal static class ObjDataNearestQuantumAccessPointPatch
    {
        private static bool Prefix(
            ObjData __instance,
            GameState s,
            ref ObjStorageComp storage,
            ref int __result)
        {
            if (!QuantumObjectAccessRouting.TryResolveCurrentAgentAccessPoint(
                    __instance,
                    s,
                    out ObjStorageComp accessPoint))
            {
                return true;
            }

            storage = accessPoint;
            __result = accessPoint.InPort;
            return false;
        }
    }

    [HarmonyPatch(typeof(AIActionLoadObj), "TryGetObj")]
    internal static class AIActionLoadObjNearestQuantumAccessPointPatch
    {
        private static void Prefix(
            GameState __0,
            AIAgentComp __1,
            ObjData __2,
            AIGoal __3,
            out QuantumObjectStorageRouteState __state)
        {
            int anchorPos = __1?.PosIdx ?? -1;
            if (__3?.Target is ILoadsObj loader && EntityUtils.IsWithinBounds(loader.LoadObjSpotIdx))
            {
                anchorPos = loader.LoadObjSpotIdx;
            }
            __state = QuantumObjectAccessRouting.RedirectStorageOwner(__2, __0, __1, anchorPos);
        }

        private static Exception Finalizer(
            Exception __exception,
            QuantumObjectStorageRouteState __state)
        {
            QuantumObjectAccessRouting.RestoreStorageOwner(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(AIActionSurgery_GoTo), "TryGetPart")]
    internal static class AIActionSurgeryNearestQuantumAccessPointPatch
    {
        private static void Prefix(
            GameState __0,
            AIAgentComp __1,
            ObjData __2,
            out QuantumObjectStorageRouteState __state)
        {
            __state = QuantumObjectAccessRouting.RedirectStorageOwner(
                __2,
                __0,
                __1,
                __1?.PosIdx ?? -1);
        }

        private static Exception Finalizer(
            Exception __exception,
            QuantumObjectStorageRouteState __state)
        {
            QuantumObjectAccessRouting.RestoreStorageOwner(__state);
            return __exception;
        }
    }
}
