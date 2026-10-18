using System;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.Crafting;
using Game.Systems.Creatures;
using Game.Systems.AI;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.Manufacturing {
    [HarmonyPatch]
    internal static class ManufacturingHooks {
        private const int HaulIngredientsJob = 105668070;

        [ThreadStatic]
        private static bool materialShortageCycle;

        [HarmonyPatch(typeof(CraftingSys), "OnRegisterCrafter")]
        [HarmonyPostfix]
        private static void CrafterRegistered(MultiCrafterComp crafter, bool added) {
            SessionLog.Guard("manufacturing.register", () => SessionLog.Building("Manufacturing", crafter?.Tile,
                (added ? "registered " : "unregistered ") + SessionLog.Order(crafter?.CurrentOrder)));
        }

        [HarmonyPatch(typeof(CraftingSys), "ProcessCrafter")]
        [HarmonyPrefix]
        private static void BeforeProcess(MultiCrafterComp crafter, out CrafterState __state) {
            var order = crafter?.CurrentOrder;
            __state = new CrafterState {
                Order = order,
                State = order?.State.ToString(),
                ProgressBucket = order == null ? -1 : (int)(order.Progress * 10f)
            };
        }

        [HarmonyPatch(typeof(CraftingSys), "ProcessCrafter")]
        [HarmonyPostfix]
        private static void AfterProcess(MultiCrafterComp crafter, CrafterState __state) {
            SessionLog.Guard("manufacturing.process", () => {
                var order = crafter?.CurrentOrder;
                int bucket = order == null ? -1 : (int)(order.Progress * 10f);
                if (order == __state.Order && order?.State.ToString() == __state.State
                        && bucket == __state.ProgressBucket) { return; }
                SessionLog.Building("Manufacturing", crafter?.Tile,
                    "process previousState=" + __state.State
                    + " previousProgress10=" + __state.ProgressBucket
                    + " currentProgress10=" + bucket + " " + SessionLog.Order(order));
            });
        }

        private struct CrafterState {
            internal CraftingOrder Order;
            internal string State;
            internal int ProgressBucket;
        }

        [HarmonyPatch(typeof(CraftingSys), "OnCreateCraftingOrder")]
        [HarmonyPostfix]
        private static void OrderCreated(CraftingOrder __result) {
            SessionLog.Guard("manufacturing.create", () => SessionLog.Building("Manufacturing", __result?.Crafter?.Tile,
                "order_created " + SessionLog.Order(__result)));
        }

        [HarmonyPatch(typeof(CraftingSys), "OnRemoveCraftingOrder")]
        [HarmonyPostfix]
        private static void OrderRemoved(CraftingOrder order, bool __result) {
            SessionLog.Guard("manufacturing.remove", () => SessionLog.Building("Manufacturing", order?.Crafter?.Tile,
                "order_removed success=" + __result + " " + SessionLog.Order(order)));
        }

        [HarmonyPatch(typeof(MultiCrafterComp), nameof(MultiCrafterComp.CycleNextOrder))]
        [HarmonyPrefix]
        private static void BeforeOrderCycle(MultiCrafterComp __instance, out CycleState __state) {
            var order = __instance?.CurrentOrder;
            __state = new CycleState {
                Order = order,
                State = order?.State,
                Progress = order?.Progress ?? 0f,
                Produced = order?.Demand?.AmountProduced ?? 0
            };
        }

        [HarmonyPatch(typeof(MultiCrafterComp), nameof(MultiCrafterComp.CycleNextOrder))]
        [HarmonyPostfix]
        private static void OrderCycled(MultiCrafterComp __instance, string why, CycleState __state) {
            SessionLog.Guard("manufacturing.cycle", () => {
                var current = __instance?.CurrentOrder;
                if (materialShortageCycle && why == "Goal failed") { return; }
                if (ReferenceEquals(__state.Order, current)
                        && __state.State == current?.State
                        && __state.Progress == (current?.Progress ?? 0f)
                        && __state.Produced == (current?.Demand?.AmountProduced ?? 0)) { return; }
                SessionLog.Building("Manufacturing", __instance?.Tile,
                    "order_changed reason=" + why
                    + " previousCraftable=" + SessionLog.CraftableId(__state.Order)
                    + " previousState=" + __state.State
                    + " current=" + SessionLog.Order(current));
            });
        }

        private struct CycleState {
            internal CraftingOrder Order;
            internal CraftingOrderState? State;
            internal float Progress;
            internal int Produced;
        }

        [HarmonyPatch(typeof(MultiCrafterComp), nameof(MultiCrafterComp.ProvideRequestedMat))]
        [HarmonyPrefix]
        private static void BeforeInput(UnstoredMatComp unstoredMat, out int __state) {
            __state = unstoredMat?.StackSize ?? 0;
        }

        [HarmonyPatch(typeof(MultiCrafterComp), nameof(MultiCrafterComp.ProvideRequestedMat))]
        [HarmonyPostfix]
        private static void AfterInput(MultiCrafterComp __instance, UnstoredMatComp unstoredMat,
                Being worker, int __state, bool __result) {
            SessionLog.Guard("manufacturing.input", () => SessionLog.Building("Manufacturing", __instance.Tile,
                "input material=" + unstoredMat?.Type + " taken=" + (__state - (unstoredMat?.StackSize ?? 0))
                + " worker=" + SessionLog.Entity(worker) + " fulfilled=" + __result
                + " " + SessionLog.Order(__instance.CurrentOrder)));
        }

        [HarmonyPatch(typeof(CraftingSys), "SpawnCraftable")]
        [HarmonyPrefix]
        private static void BeforeOutput(CraftingOrder order, out int __state) {
            __state = order?.Demand?.AmountProduced ?? 0;
        }

        [HarmonyPatch(typeof(CraftingSys), "SpawnCraftable")]
        [HarmonyPostfix]
        private static void AfterOutput(CraftingOrder order, int __state) {
            SessionLog.Guard("manufacturing.output", () => SessionLog.Building("Manufacturing", order?.Crafter?.Tile,
                "output quantity=" + ((order?.Demand?.AmountProduced ?? 0) - __state)
                + " " + SessionLog.Order(order)));
        }

        [HarmonyPatch(typeof(MultiCrafterComp), nameof(MultiCrafterComp.OnGoalChange))]
        [HarmonyPrefix]
        private static void BeforeGoalChanged(MultiCrafterComp __instance, AIGoal goal,
                AIGoalState from, AIGoalState to, out bool __state) {
            __state = IsConfirmedMaterialShortage(__instance, goal);
            materialShortageCycle = __state &&
                (AIGoal.IsFailed(to) || AIGoal.IsCancelled(to));
        }

        [HarmonyPatch(typeof(MultiCrafterComp), nameof(MultiCrafterComp.OnGoalChange))]
        [HarmonyPostfix]
        private static void GoalChanged(MultiCrafterComp __instance, AIGoal goal,
                AIGoalState from, AIGoalState to, bool __state) {
            try {
                SessionLog.Guard("manufacturing.goal", () => {
                    if (__state && IsShortageRetryTransition(from, to)) { return; }
                    SessionLog.Building("Manufacturing", __instance.Tile,
                        "task_state " + from + "->" + to + " " + SessionLog.Goal(goal));
                });
            } finally {
                materialShortageCycle = false;
            }
        }

        private static bool IsConfirmedMaterialShortage(MultiCrafterComp crafter, AIGoal goal) {
            if (goal == null || goal.JobIdH != HaulIngredientsJob
                    || string.IsNullOrEmpty(goal.FailReason)
                    || !goal.FailReason.StartsWith(T.AdRejLackingMat, StringComparison.Ordinal)) {
                return false;
            }
            var order = crafter?.CurrentOrder;
            return order?.NextMissingMat != null || order?.Deficit?.HasDeficit == true;
        }

        private static bool IsShortageRetryTransition(AIGoalState from, AIGoalState to) {
            return (AIGoal.IsFailed(from) && to == AIGoalState.Available)
                || (from == AIGoalState.Available && AIGoal.IsFailed(to));
        }
    }
}
