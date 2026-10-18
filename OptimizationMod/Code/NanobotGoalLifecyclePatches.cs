using Game.Systems.AI;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Feeds newly accepted colony goals into the Nanobot dirty index. Core
    /// rc316 owns native AI priority bucketing, so no separate AI bucket cache
    /// or priority-change hooks are installed by Optimization Mod.
    /// </summary>
    internal static class NanobotGoalAddedToIndexPatch {
        internal static void Postfix(AISys __instance, AIGoal goal) {
            NanobotDirtyGoalIndex.GoalAdded(__instance, goal);
        }
    }
}
