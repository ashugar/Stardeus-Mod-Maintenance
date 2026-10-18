using System.Reflection;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Creatures;
using Game.Systems.Path;
using HarmonyLib;

namespace ExpandedTraitsMod {
    [HarmonyPatch(typeof(FoodPreference), nameof(FoodPreference.ApplyMentalEffect), new[] {
        typeof(AIAgentComp), typeof(MatType), typeof(long)
    })]
    internal static class SurvivalMealMoodPatch {
        private static bool Prefix(MatType foodType) => foodType?.Id != MatId.SurvivalMeal;
    }

    [HarmonyPatch(typeof(FoodPreference), nameof(FoodPreference.IsPreferred), new[] {
        typeof(MatType), typeof(bool)
    })]
    internal static class SurvivalMealPreferencePatch {
        private static void Postfix(MatType type, ref bool __result) {
            if (type?.Id == MatId.SurvivalMeal) __result = false;
        }
    }

    [HarmonyPatch]
    internal static class FoodTierSelectionPatch {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(AIActionFindFood),
            "FindFood", new[] {
                typeof(GameState), typeof(AIAgentComp), typeof(AIGoal), typeof(long)
            });

        private static bool Prefix(GameState S, AIAgentComp agent, AIGoal goal,
                ref AIActionResult __result) {
            if (agent?.BeingData == null
                    || !FoodPreference.TryGetPreferredVegan(agent.BeingData, out bool vegan)
                    || agent.BB.Int1 != MatGroupIdH.Food) return true;
            bool starving = agent.BeingData.HasCondition(ConditionIdH.Starvation)
                && agent.BeingData.StatsBeing.Hunger.Value <= 0.001f;
            var inventory = S.Sys.Inventory;
            var preferred = inventory.FindClosestReachableStorageByGroup(
                MatGroupIdH.Food, agent.Being, agent.BB.Blacklist,
                storage => FoodPreference.StorageHasPreferredFood(storage, MatGroupIdH.Food, vegan),
                !starving, false);
            if (preferred != null) return true;

            var neutral = inventory.FindClosestReachableStorageByGroup(
                MatGroupIdH.Food, agent.Being, agent.BB.Blacklist,
                storage => HasMaterial(storage, MatGroupIdH.Food, MatId.SurvivalMeal),
                !starving, false);
            if (neutral != null) {
                __result = ReserveAndTravel(neutral, MatGroupIdH.Food, MatId.SurvivalMeal,
                    agent, goal, !starving);
                return false;
            }
            if (starving || agent.BeingData.StatsBeing.Hunger.Value < 0.1f) return true;

            // Keep disliked food out of this search. Core will then attempt cooking and
            // report hunger normally; the blacklist is cleared when this goal deactivates.
            while (true) {
                var disliked = inventory.FindClosestReachableStorageByGroup(
                    MatGroupIdH.Food, agent.Being, agent.BB.Blacklist, null, true, false);
                if (disliked == null) break;
                agent.BB.AddToBlacklist(disliked.EntityId);
            }
            return true;
        }

        private static bool HasMaterial(IMatStorage storage, int group, string materialId) {
            var options = Caches.MatList;
            options.Clear();
            if (!storage.AvailableForGroup(group, options)) return false;
            foreach (Mat option in options) if (option.Type?.Id == materialId) return true;
            return false;
        }

        internal static AIActionResult ReserveAndTravel(IMatStorage storage, int group,
                string materialId, AIAgentComp agent, AIGoal goal, bool safeOnly) {
            var options = Caches.MatList;
            options.Clear();
            if (!storage.AvailableForGroup(group, options)) {
                agent.BB.AddToBlacklist(storage.EntityId);
                return AIActionResult.Create(AIActionState.InProgress);
            }
            MatType type = null;
            foreach (Mat option in options) {
                if (option.Type?.Id == materialId) { type = option.Type; break; }
            }
            if (type == null || storage.Reserve(type, agent.Being, 1, goal) == 0) {
                agent.BB.AddToBlacklist(storage.EntityId);
                return AIActionResult.Create(AIActionState.InProgress);
            }
            agent.BB.TargetEntity = storage.Entity;
            agent.BB.TargetMatType = type;
            agent.BB.MoveTargetPos = storage.OutPort;
            var flags = PathReqFlags.AdjOnTop | PathReqFlags.AdjNearby | PathReqFlags.TempDestination;
            if (!safeOnly) flags |= PathReqFlags.UnsafeOK;
            if (!agent.Motion.TryGoTo(storage.OutPort, flags, out agent.BB.MoveProgress)) {
                storage.CancelReservationFor(agent.Being, goal);
                agent.BB.AddToBlacklist(storage.EntityId);
                agent.BB.MoveProgress = null;
            }
            return AIActionResult.Create(AIActionState.InProgress);
        }
    }

    [HarmonyPatch]
    internal static class CoffeePriorityPatch {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(AIActionFindDrink),
            "FindDrink", new[] {
                typeof(GameState), typeof(AIAgentComp), typeof(AIGoal), typeof(long)
            });
        private static bool Prefix(GameState S, AIAgentComp agent, AIGoal goal,
                ref AIActionResult __result) {
            if (agent?.BeingData?.Mind?.Traits?.HasTrait(ExpandedTraitIds.CoffeeAddictH) != true
                    || agent.BB.Int1 != MatGroupIdH.Drink) return true;
            var coffee = S.Sys.Inventory.FindClosestReachableStorageByGroup(
                MatGroupIdH.Drink, agent.Being, agent.BB.Blacklist,
                storage => HasCoffee(storage), true, false);
            if (coffee == null) return true;
            __result = FoodTierSelectionPatch.ReserveAndTravel(coffee, MatGroupIdH.Drink,
                MatId.Coffee, agent, goal, true);
            return false;
        }

        private static bool HasCoffee(IMatStorage storage) {
            var options = Caches.MatList;
            options.Clear();
            if (!storage.AvailableForGroup(MatGroupIdH.Drink, options)) return false;
            foreach (Mat option in options) if (option.Type?.Id == MatId.Coffee) return true;
            return false;
        }
    }
}
