using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Research;
using Game.Systems.AI;
using Game.Systems.Conditions;
using Game.Systems.Creatures;
using Game.Systems.Drama;
using Game.Systems.Mental;
using Game.Systems.Story;
using Game.Systems.Traits;
using Game.Utils;
using HarmonyLib;
using KL.Randomness;
using KL.Utils;

namespace ExpandedTraitsMod {
    [HarmonyPatch]
    internal static class IntrovertSocialOpportunityPatch {
        private sealed class State {
            internal HashSet<int> Ids;
            internal bool Hidden;
        }
        private static readonly FieldInfo TraitIdsField = AccessTools.Field(typeof(BeingTraits), "traitIds");
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(DramaSys),
            "FindSocialOpportunity", new[] { typeof(BeingData), typeof(int) });

        private static bool Prefix(BeingData being, ref SocialOpportunity __result, out State __state) {
            __state = null;
            BeingTraits traits = being?.Mind?.Traits;
            if (traits == null || !traits.HasTrait(ExpandedTraitIds.IntrovertH)) return true;
            Being actor = being.Being;
            if (actor == null || !actor.S.Rng.Chance(0.35f)) {
                __result = null;
                return false;
            }
            var ids = TraitIdsField?.GetValue(traits) as HashSet<int>;
            if (ids == null) return true;
            __state = new State { Ids = ids, Hidden = ids.Remove(ExpandedTraitIds.IntrovertH) };
            return true;
        }

        private static void Postfix(State __state) => Restore(__state);
        private static Exception Finalizer(Exception __exception, State __state) {
            Restore(__state);
            return __exception;
        }
        private static void Restore(State state) {
            if (state?.Hidden == true) state.Ids.Add(ExpandedTraitIds.IntrovertH);
        }
    }

    [HarmonyPatch]
    internal static class SocialInteractionTraitPatch {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(DramaSys),
            "RegisterSocialInteraction", new[] { typeof(SocialInteraction) });

        private static void Prefix(SocialInteraction soc) {
            if (soc == null) return;
            for (int i = 0; i < soc.InvolvedMinds.Length; i++) {
                Mind person = soc.InvolvedMinds[i];
                if (person?.Owner == null || !person.Species.IsHumanoid) continue;
                for (int j = 0; j < soc.InvolvedMinds.Length; j++) {
                    if (i == j) continue;
                    Mind animal = soc.InvolvedMinds[j];
                    if (!IsFriendlyPet(animal)) continue;
                    if (TraitDefinitionRegistry.AffinityValue(person.Traits, animal.Species) > 0) {
                        soc.AttitudeMul_Pet = Math.Max(soc.AttitudeMul_Pet, 1.25f);
                    }
                }
            }
        }

        private static void Postfix(DramaSys __instance, SocialInteraction soc, bool __result) {
            if (!__result || soc == null) return;
            GameState state = A.S;
            long ticks = state.Ticks;
            bool storm = ExpandedTraitsSystem.IsPsychicStorm(state);
            for (int i = 0; i < soc.InvolvedMinds.Length; i++) {
                Mind person = soc.InvolvedMinds[i];
                if (person?.Owner == null || person.Health == null) continue;
                int petMood = 0;
                for (int j = 0; j < soc.InvolvedMinds.Length; j++) {
                    if (i == j) continue;
                    Mind animal = soc.InvolvedMinds[j];
                    if (!IsFriendlyPet(animal)) continue;
                    petMood = Math.Max(petMood,
                        TraitDefinitionRegistry.AffinityValue(person.Traits, animal.Species));
                }
                if (petMood > 0) {
                    MoodService.Add(state, person.Owner, ticks, Consts.TicksPer4Hours,
                        "Enjoyed animal companionship", petMood, IconId.CMoodGood);
                }
                if (person.Traits.HasTrait(ExpandedTraitIds.ExtrovertH)) {
                    MoodService.Add(state, person.Owner, ticks, Consts.TicksPer4Hours,
                        "Socially fulfilled", 3, IconId.CWave);
                }
                if (storm && person.Traits.HasTrait(ExpandedTraitIds.EmpathH)) {
                    for (int j = 0; j < soc.InvolvedMinds.Length; j++) {
                        if (i == j) continue;
                        Mind other = soc.InvolvedMinds[j];
                        if (other?.Owner == null || other.Health == null) continue;
                        MoodService.Add(state, other.Owner, ticks, Consts.TicksPer2Hours,
                            "Empathic reassurance", 2,
                            IconId.CBrainWaves);
                        ValidationDiagnostics.Log(
                            "Empath reassurance applied during psychic storm: empath={0}; recipient={1}; mood=+2; durationTicks={2}.",
                            person, other, Consts.TicksPer2Hours);
                    }
                }
            }
        }

        private static bool IsFriendlyPet(Mind mind) => mind?.Owner != null
            && mind.Owner.IsFriendly && TraitDefinitionRegistry.IsPetSpecies(mind.Owner.Species);

    }

    [HarmonyPatch]
    internal static class EmpathPsychicStormPatch {
        private static IEnumerable<MethodBase> TargetMethods() {
            Type[] signature = { typeof(GameState), typeof(StoryEventDef) };
            yield return AccessTools.Method(typeof(BaseSpaceStormStoryEvent),
                nameof(BaseSpaceStormStoryEvent.StartStormEffects), signature);
            yield return AccessTools.Method(typeof(BaseSpaceStormStoryEvent),
                nameof(BaseSpaceStormStoryEvent.TickStormEffects), signature);
            yield return AccessTools.Method(typeof(BaseSpaceStormStoryEvent),
                nameof(BaseSpaceStormStoryEvent.StopStormEffects), signature);
        }

        private static void Postfix(BaseSpaceStormStoryEvent __instance,
                MethodBase __originalMethod, GameState s) {
            if (!(__instance is PsychicStormStoryEvent) || s == null) return;
            string phase = __originalMethod.Name == nameof(BaseSpaceStormStoryEvent.StartStormEffects)
                ? "start"
                : __originalMethod.Name == nameof(BaseSpaceStormStoryEvent.StopStormEffects)
                    ? "stop" : "hourly tick";
            if (phase == "stop") {
                int empathMinds = 0;
                int remainingEffects = 0;
                var minds = s.Sys.Get<BeingsSys>(BeingsSys.SysId)?.Minds;
                if (minds != null) {
                    foreach (Mind mind in minds.Values) {
                        if (mind?.Traits?.HasTrait(ExpandedTraitIds.EmpathH) != true) continue;
                        empathMinds++;
                        if (mind.Health?.PermanentEffect(MoodSourceIdH.PsychicStorm) != null) {
                            remainingEffects++;
                        }
                    }
                }
                ValidationDiagnostics.Log(
                    "Psychic storm cleanup observed: empathMinds={0}; remainingPsychicStormEffects={1}.",
                    empathMinds, remainingEffects);
                return;
            }
            if (phase == "hourly tick"
                    && s.Ticks % Consts.TicksPerHour != 17) return;
            int applied = 0;
            foreach (Being being in s.Beings.Alive) {
                BeingData data = being?.Data;
                if (data?.Mind?.Traits?.HasTrait(ExpandedTraitIds.EmpathH) != true) continue;
                MentalHealth health = data.Mind.Health;
                if (health == null) continue;
                int baseMood = __instance.MoodEffect + 4;
                health.SetPermanentEffect(s, MoodSourceIdH.PsychicStorm,
                    __instance.Icon, __instance.Title, baseMood);
                MentalEffect effect = health.PermanentEffect(MoodSourceIdH.PsychicStorm);
                applied++;
                ValidationDiagnostics.Log(
                    "Empath psychic-storm mood applied: mind={0}; phase={1}; baseMood={2}; effectiveMood={3}.",
                    data.Mind, phase, baseMood, effect?.Effect ?? 0);
            }
            ValidationDiagnostics.Log(
                "Psychic storm hook observed: phase={0}; empathEffectsApplied={1}.",
                phase, applied);
        }
    }

    [HarmonyPatch(typeof(MentalHealth), nameof(MentalHealth.AddEffect), new[] {
        typeof(GameState), typeof(MentalEffect), typeof(float), typeof(bool)
    })]
    internal static class SleepMoodSeverityPatch {
        private static void Prefix(MentalHealth __instance, MentalEffect effect) {
            BeingTraits traits = __instance?.Owner?.Traits;
            if (traits == null || effect == null) return;
            bool disturbed = effect.Reason == T.SleepDisturbedByNoise;
            bool interrupted = effect.Reason == T.SleepInterrupted || effect.Reason == T.SleptTooShort;
            if (!disturbed && !interrupted) return;
            if (traits.HasTrait(ExpandedTraitIds.LightSleeperH)) {
                effect.Effect = (sbyte)(disturbed ? -5 : -10);
            } else if (traits.HasTrait(ExpandedTraitIds.HeavySleeperH)) {
                effect.Effect = (sbyte)(disturbed ? -1 : -4);
            }
        }
    }

    [HarmonyPatch]
    internal static class SleepNoiseChancePatch {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(BeingCluster),
            "DisturbSleeping", new[] { typeof(GameState), typeof(long) });
        private static bool Prefix(BeingCluster __instance, GameState s, long ticks) {
            foreach (BeingData being in __instance.FriendlyAsleep) {
                if (being.Species.IsAnimal || being.Mind.Health == null) continue;
                float chance = being.Being.Graphics.IsHidden ? 0.03f : 0.5f;
                if (being.Mind.Traits.HasTrait(ExpandedTraitIds.LightSleeperH)) chance = Math.Min(1f, chance * 1.75f);
                else if (being.Mind.Traits.HasTrait(ExpandedTraitIds.HeavySleeperH)) chance *= 0.25f;
                if (!Rng.UChance(chance)) continue;
                var effect = MentalEffect.CreateWithIcon(being.Being?.PosIdx ?? -1,
                    IconId.CSoundWarning, ticks, Consts.TicksPer6Hours,
                    T.SleepDisturbedByNoise, -3);
                s.Sig.AddMentalEffectWithLog.Send(being, effect, 0f, false);
                being.Mind.Health.Comp?.DisplayMoodBubble(effect);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Condition), nameof(Condition.Advance), new[] {
        typeof(GameState), typeof(long), typeof(int), typeof(BeingData), typeof(sbyte),
        typeof(float), typeof(float), typeof(bool), typeof(Severity)
    })]
    internal static class FoodPoisoningProgressPatch {
        private static void Prefix(int condId, BeingData data, sbyte direction,
                ref float developmentRate) {
            if (condId != ConditionIdH.FoodPoisoning || direction != 1 || data?.Mind?.Traits == null) return;
            if (data.Mind.Traits.HasTrait(ExpandedTraitIds.IronStomachH)) developmentRate *= 0.5f;
            else if (data.Mind.Traits.HasTrait(ExpandedTraitIds.SensitiveStomachH)) developmentRate *= 1.5f;
        }
    }

    [HarmonyPatch(typeof(ConditionState), nameof(ConditionState.CreateFor), new[] {
        typeof(GameState), typeof(int), typeof(BeingData), typeof(Severity),
        typeof(bool), typeof(bool)
    })]
    internal static class FoodPoisoningSeverityPatch {
        private static void Prefix(int condIdH, BeingData data, ref Severity sev) {
            if (condIdH != ConditionIdH.FoodPoisoning || data?.Mind?.Traits == null) return;
            if (data.Mind.Traits.HasTrait(ExpandedTraitIds.IronStomachH) && sev > Severity.Mild) sev--;
            else if (data.Mind.Traits.HasTrait(ExpandedTraitIds.SensitiveStomachH) && sev < Severity.Critical) sev++;
        }
    }

    [HarmonyPatch(typeof(AIAction), nameof(AIAction.AddMentalEffect), new[] {
        typeof(AIAgentComp), typeof(long), typeof(int), typeof(string), typeof(int)
    })]
    internal static class CoffeeAbsencePatch {
        private static bool Prefix(AIAgentComp agent, string msg) =>
            msg != T.NoCoffee || agent?.BeingData?.Mind?.Traits?.HasTrait(ExpandedTraitIds.CoffeeAddictH) == true;
    }

    [HarmonyPatch(typeof(AIActionConsumeDrink), nameof(AIActionConsumeDrink.Execute), new[] {
        typeof(GameState), typeof(AIAgentComp), typeof(AIGoal), typeof(long)
    })]
    internal static class CoffeePositivePatch {
        private static void Postfix(AIAgentComp agent, long ticks, AIActionResult __result) {
            if (__result.State != AIActionState.Success
                    || agent?.BeingData?.Mind?.Traits?.HasTrait(ExpandedTraitIds.CoffeeAddictH) != true
                    || agent.BB.TargetEntity?.TryGetComponent<Game.Components.UnstoredMatComp>(out var mat) != true
                    || mat.Type?.Id != MatId.Coffee) return;
            AIAction.AddMentalEffect(agent, ticks, Consts.TicksPer3Hours, mat.Type.NameT, 10);
        }
    }
}
