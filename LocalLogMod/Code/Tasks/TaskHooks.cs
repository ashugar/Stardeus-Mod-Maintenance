using System;
using Game.Constants;
using Game.Systems.AI;
using HarmonyLib;
using LocalLogMod.Logging;

namespace LocalLogMod.Tasks {
    [HarmonyPatch]
    internal static class TaskHooks {
        private const int HaulIngredientsJob = 105668070;

        [HarmonyPatch(typeof(AIGoal), nameof(AIGoal.Create))]
        [HarmonyPostfix]
        private static void Created(AIGoal __result) {
            SessionLog.Guard("tasks.create", () => SessionLog.Write("Tasks", "created " + SessionLog.Goal(__result)));
        }

        [HarmonyPatch(typeof(AIGoal), nameof(AIGoal.CreatePersonal))]
        [HarmonyPostfix]
        private static void PersonalCreated(AIGoal __result) {
            SessionLog.Guard("tasks.personal", () => SessionLog.Write("Tasks", "personal_created " + SessionLog.Goal(__result)));
        }

        [HarmonyPatch(typeof(AIGoal), nameof(AIGoal.CreateCombat))]
        [HarmonyPostfix]
        private static void CombatCreated(AIGoal __result) {
            SessionLog.Guard("tasks.combat", () => SessionLog.Write("Tasks", "combat_created " + SessionLog.Goal(__result)));
        }

        [HarmonyPatch(typeof(AIGoal), nameof(AIGoal.DirectOrder))]
        [HarmonyPostfix]
        private static void DirectCreated(AIGoal __result) {
            SessionLog.Guard("tasks.direct", () => SessionLog.Write("Tasks", "direct_order_created " + SessionLog.Goal(__result)));
        }

        [HarmonyPatch(typeof(AISys), "OnAddGoal")]
        [HarmonyPostfix]
        private static void Registered(AIGoal goal) {
            SessionLog.Guard("tasks.register", () => SessionLog.Write("Tasks", "registered " + SessionLog.Goal(goal)));
        }

        [HarmonyPatch(typeof(AISys), "OnRemoveGoal")]
        [HarmonyPrefix]
        private static void Unregistered(AIGoal goal) {
            SessionLog.Guard("tasks.unregister", () => SessionLog.Write("Tasks", "unregistered " + SessionLog.Goal(goal)));
        }

        [HarmonyPatch(typeof(AIAgentComp), nameof(AIAgentComp.SetGoal))]
        [HarmonyPostfix]
        private static void WorkerGoalSet(AIAgentComp __instance, bool __result) {
            SessionLog.Guard("tasks.worker.set", () => SessionLog.Write("Tasks",
                "worker_goal_set worker=" + SessionLog.Entity(__instance.Being)
                + " success=" + __result + " " + SessionLog.Goal(__instance.Goal)));
        }

        [HarmonyPatch(typeof(AIAgentComp), nameof(AIAgentComp.ClearGoal))]
        [HarmonyPrefix]
        private static void WorkerGoalCleared(AIAgentComp __instance) {
            SessionLog.Guard("tasks.worker.clear", () => SessionLog.Write("Tasks",
                "worker_goal_cleared worker=" + SessionLog.Entity(__instance.Being)
                + " " + SessionLog.Goal(__instance.Goal)));
        }

        [HarmonyPatch(typeof(AIGoal), "set_FailReason")]
        [HarmonyPrefix]
        private static void BeforeFailReason(AIGoal __instance, out string __state) {
            __state = __instance.FailReason;
        }

        [HarmonyPatch(typeof(AIGoal), "set_FailReason")]
        [HarmonyPostfix]
        private static void AfterFailReason(AIGoal __instance, string __state) {
            SessionLog.Guard("tasks.reason", () => {
                if (__state != __instance.FailReason) {
                    if (IsKnownMaterialShortage(__instance)) { return; }
                    SessionLog.Write("Tasks", "failure_reason previous=" + __state
                        + " current=" + __instance.FailReason + " " + SessionLog.Goal(__instance));
                }
            });
        }

        [HarmonyPatch(typeof(AIGoal), nameof(AIGoal.ChangeStateTo))]
        [HarmonyPrefix]
        private static void BeforeState(AIGoal __instance, out AIGoalState __state) {
            __state = __instance.State;
        }

        [HarmonyPatch(typeof(AIGoal), nameof(AIGoal.ChangeStateTo))]
        [HarmonyPostfix]
        private static void AfterState(AIGoal __instance, AIGoalState __state) {
            SessionLog.Guard("tasks.state", () => {
                if (__state != __instance.State) {
                    if (IsKnownMaterialShortage(__instance)
                            && IsShortageRetryTransition(__state, __instance.State)) { return; }
                    SessionLog.Write("Tasks", "state " + __state + "->" + __instance.State + " " + SessionLog.Goal(__instance));
                }
            });
        }

        [HarmonyPatch(typeof(AIGoal), nameof(AIGoal.SetAgent))]
        [HarmonyPostfix]
        private static void Assigned(AIGoal __instance) {
            SessionLog.Guard("tasks.assign", () => SessionLog.Write("Tasks", "assigned " + SessionLog.Goal(__instance)));
        }

        [HarmonyPatch(typeof(AIGoal), nameof(AIGoal.SetPriority))]
        [HarmonyPrefix]
        private static void BeforePriority(AIGoal __instance, out byte __state) {
            __state = __instance.GoalPriority;
        }

        [HarmonyPatch(typeof(AIGoal), nameof(AIGoal.SetPriority))]
        [HarmonyPostfix]
        private static void AfterPriority(AIGoal __instance, byte __state) {
            SessionLog.Guard("tasks.priority", () => {
                if (__state != __instance.GoalPriority) {
                    SessionLog.Write("Tasks", "priority " + __state + "->" + __instance.GoalPriority + " " + SessionLog.Goal(__instance));
                }
            });
        }

        private static bool IsKnownMaterialShortage(AIGoal goal) {
            return goal != null && goal.JobIdH == HaulIngredientsJob
                && !string.IsNullOrEmpty(goal.FailReason)
                && goal.FailReason.StartsWith(T.AdRejLackingMat, StringComparison.Ordinal);
        }

        private static bool IsShortageRetryTransition(AIGoalState from, AIGoalState to) {
            return (AIGoal.IsFailed(from) && to == AIGoalState.Available)
                || (from == AIGoalState.Available && AIGoal.IsFailed(to));
        }
    }
}
