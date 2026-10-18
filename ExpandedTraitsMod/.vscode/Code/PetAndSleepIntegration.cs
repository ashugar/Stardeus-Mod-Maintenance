using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.AI;
using Game.Systems.Conditions;
using Game.Systems.Creatures;
using Game.Utils;
using HarmonyLib;
using KL.Grid;
using KL.Utils;

namespace ExpandedTraitsMod {
    [HarmonyPatch(typeof(Def), nameof(Def.AfterLoad), new Type[] { })]
    internal static class FriendlyCrawlerDefinitionPatch {
        private const string CrawlerDefinition = "Beings/Crawler01";

        private static void Prefix(Def __instance) {
            if (__instance?.Id != CrawlerDefinition) return;
            if (__instance.Components?.Any(c => c?.Component == "Pettable") == true) return;
            int oldLength = __instance.Components?.Length ?? 0;
            Array.Resize(ref __instance.Components, oldLength + 1);
            __instance.Components[oldLength] = new ComponentConfig { Component = "Pettable" };
        }
    }

    [HarmonyPatch]
    internal static class FriendlyCrawlerPettingPatch {
        private static readonly int CrawlerH = Hashes.S("Crawler");
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(AIActionPetAnimal), "CanPet", new[] {
                typeof(GameState), typeof(int), typeof(int), typeof(bool),
                typeof(PettableComp), typeof(float).MakeByRefType()
            });

        private static bool Prefix(GameState s, int agentPos, int agentIslandId,
                bool isG, PettableComp pet, ref float w, ref bool __result) {
            Being crawler = pet?.Being;
            if (crawler?.Data?.Species?.IdH != CrawlerH || !crawler.Data.IsFriendly) return true;
            if (!crawler.IsActive || crawler.IsDead || crawler.Motion.IsImmobilized
                    || crawler.Graphics.IsHidden) {
                w = 0f;
                __result = false;
                return false;
            }
            int petPos = crawler.PosIdx;
            if (isG && !s.Sys.Areas.CanReachGroundedIslandToPos(agentIslandId, petPos)) {
                w = 0f;
                __result = false;
                return false;
            }
            float fun = (1f - crawler.Data.StatsBeing.Fun.Value) * 100f;
            w = Pos.Distance(agentPos, petPos) + fun * fun;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class HeavySleeperSirenPatch {
        private static readonly int SirenGoalH = Hashes.S("EmergencySiren");
        private static readonly Dictionary<uint, long> AlarmStarted = new();

        internal static void Reset() => AlarmStarted.Clear();

        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(SecuritySys), "HailColonistsToSirens",
                new[] { typeof(long), typeof(bool) });

        private static void Postfix(long ticks, bool sirensActive) {
            if (!sirensActive) {
                AlarmStarted.Clear();
                return;
            }
            GameState state = A.S;
            foreach (Being being in state.Beings.Alive) {
                BeingData data = being?.Data;
                if (data?.Mind?.Traits?.HasTrait(ExpandedTraitIds.HeavySleeperH) != true
                        || !data.HasCondition(ConditionIdH.Sleeping)
                        || !data.Safety.IsActuallySafeAt(being.PosIdx)) {
                    if (data != null) AlarmStarted.Remove(data.Id);
                    continue;
                }
                if (!AlarmStarted.TryGetValue(data.Id, out long started)) {
                    AlarmStarted[data.Id] = ticks;
                    started = ticks;
                }
                if (ticks - started >= Consts.TicksPer30Minutes) continue;
                if (!being.AIAgent.BB.TryNeedGoal(SirenGoalH, out AIGoal goal)) continue;
                if (AIGoal.IsActive(goal)) AIGoal.Cancel(goal, ticks);
                being.AIAgent.BB.NeedGoals.Remove(SirenGoalH);
            }
        }
    }
}
