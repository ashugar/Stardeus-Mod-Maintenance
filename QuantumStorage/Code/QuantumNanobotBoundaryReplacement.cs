using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Components;
using Game.Data;
using HarmonyLib;
using KL.Grid;

namespace Game.Systems.QuantumStorage
{
    internal static class QuantumNanobotBoundaryReplacement
    {
        public static void CollectReachableAreaKeys(
            GameState state,
            int targetPos,
            IComponent boundaryTarget,
            List<int> keys)
        {
            if (!TryGetBoundaryFloorReplacement(state, targetPos, boundaryTarget, out _))
            {
                return;
            }

            ForEachAdjacentAccessArea(state, targetPos, areaKey =>
            {
                if (!keys.Contains(areaKey))
                {
                    keys.Add(areaKey);
                }
            });
        }

        public static bool CanUseAdjacentRoom(
            GameState state,
            int sourcePos,
            int targetPos,
            IComponent boundaryTarget)
        {
            if (!TryGetBoundaryFloorReplacement(state, targetPos, boundaryTarget, out _) ||
                !EntityUtils.IsWithinBounds(sourcePos))
            {
                return false;
            }

            return HasAdjacentMatchingArea(state, sourcePos, targetPos);
        }

        private static bool TryGetBoundaryFloorReplacement(
            GameState state,
            int targetPos,
            IComponent boundaryTarget,
            out PendingReplacementComp replacement)
        {
            replacement = boundaryTarget as PendingReplacementComp;
            if (state?.Sys?.Areas == null || replacement?.Entity is not Tile floor ||
                !EntityUtils.IsWithinBounds(targetPos) || floor.PosIdx != targetPos ||
                floor.Definition.LayerId != 0)
            {
                return false;
            }

            Tile boundary = EntityUtils.GetTileInLayer(targetPos, 1);
            return boundary != null && boundary.IsConstructed;
        }

        private static bool HasAdjacentMatchingArea(GameState state, int sourcePos, int targetPos)
        {
            return IsMatchingAdjacentRoom(state, sourcePos, Pos.N(targetPos)) ||
                IsMatchingAdjacentRoom(state, sourcePos, Pos.E(targetPos)) ||
                IsMatchingAdjacentRoom(state, sourcePos, Pos.S(targetPos)) ||
                IsMatchingAdjacentRoom(state, sourcePos, Pos.W(targetPos)) ||
                IsMatchingAdjacentRoom(state, sourcePos, Pos.NE(targetPos)) ||
                IsMatchingAdjacentRoom(state, sourcePos, Pos.NW(targetPos)) ||
                IsMatchingAdjacentRoom(state, sourcePos, Pos.SE(targetPos)) ||
                IsMatchingAdjacentRoom(state, sourcePos, Pos.SW(targetPos));
        }

        private static bool IsMatchingAdjacentRoom(GameState state, int sourcePos, int adjacentPos)
        {
            return IsWalkableAdjacentFloor(state, adjacentPos) &&
                state.Sys.Areas.IsSameAreaOrOutside(sourcePos, adjacentPos);
        }

        private static void ForEachAdjacentAccessArea(GameState state, int targetPos, Action<int> action)
        {
            // Replacement work uses AdjNearby, which permits a worker to stand on any of
            // the eight surrounding tiles. Wall-ring corners often have only a diagonal
            // room tile available because both cardinal neighbors are occupied by walls.
            VisitAdjacentAccessArea(state, Pos.N(targetPos), action);
            VisitAdjacentAccessArea(state, Pos.E(targetPos), action);
            VisitAdjacentAccessArea(state, Pos.S(targetPos), action);
            VisitAdjacentAccessArea(state, Pos.W(targetPos), action);
            VisitAdjacentAccessArea(state, Pos.NE(targetPos), action);
            VisitAdjacentAccessArea(state, Pos.NW(targetPos), action);
            VisitAdjacentAccessArea(state, Pos.SE(targetPos), action);
            VisitAdjacentAccessArea(state, Pos.SW(targetPos), action);
        }

        private static void VisitAdjacentAccessArea(GameState state, int adjacentPos, Action<int> action)
        {
            if (!IsWalkableAdjacentFloor(state, adjacentPos))
            {
                return;
            }

            int areaId = state.Sys.Areas.At(adjacentPos);
            action(areaId > 0 || areaId == -1 ? areaId : 0);
        }

        private static bool IsWalkableAdjacentFloor(GameState state, int adjacentPos)
        {
            return EntityUtils.IsWithinBounds(adjacentPos) &&
                EntityUtils.IsSectionFloorAt(adjacentPos) &&
                state.Sys.Path.IsWalkable(adjacentPos);
        }

    }
    [HarmonyPatch]
    internal static class NanobotReachabilityCollectAreaKeysQuantumReplacementPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Game.Systems.Nanobots.NanobotReachability");
            return type == null
                ? null
                : AccessTools.Method(type, "CollectReachableAreaKeys", new[]
                {
                    typeof(GameState),
                    typeof(int),
                    typeof(IComponent),
                    typeof(List<int>)
                });
        }

        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        private static void Postfix(GameState state, int targetPos, IComponent boundaryTarget, List<int> keys)
        {
            QuantumNanobotBoundaryReplacement.CollectReachableAreaKeys(state, targetPos, boundaryTarget, keys);
        }
    }

    [HarmonyPatch]
    internal static class NanobotReachabilitySameAreaQuantumReplacementPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName("Game.Systems.Nanobots.NanobotReachability");
            return type == null
                ? null
                : AccessTools.Method(type, "IsSameAreaOrOutside", new[]
                {
                    typeof(GameState),
                    typeof(int),
                    typeof(int),
                    typeof(IComponent)
                });
        }

        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        private static void Postfix(
            GameState state,
            int sourcePos,
            int targetPos,
            IComponent boundaryTarget,
            ref bool __result)
        {
            if (!__result)
            {
                __result = QuantumNanobotBoundaryReplacement.CanUseAdjacentRoom(
                    state,
                    sourcePos,
                    targetPos,
                    boundaryTarget);
            }

        }
    }
}
