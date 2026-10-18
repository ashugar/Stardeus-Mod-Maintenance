using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Game;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.AI.Combat;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Diplomacy;
using Game.Systems.Items;
using Game.Systems.Story;
using Game.Utils;
using HarmonyLib;
using KL.Randomness;
using KL.Utils;

namespace ProstheticsAndroidsExpanded.Systems
{
    /// <summary>
    /// Lets Androids participate in the small number of population sources
    /// where Core still directly asks for a Human instead of using a faction
    /// or quest species pool.
    /// </summary>
    public sealed class AndroidPopulationIntegrationSys : GameSystem
    {
        public const string SysId = "AndroidPopulationIntegrationSys";
        private const string HarmonyId =
            "ProstheticsAndroidsExpanded.AndroidPopulationIntegration";

        private Harmony harmony;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        [SelfInit(SelfInit.Stage.CoreDefs, 1)]
        private static void Register()
        {
            GameSystems.Register(
                SysId,
                () => new AndroidPopulationIntegrationSys());
        }

        protected override void OnInitialize()
        {
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(AndroidPopulationIntegrationSys).Assembly);
        }

        public override void Unload()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }
    }

    internal static class AndroidPopulationChoice
    {
        private const string AndroidSpeciesId = "Androids";
        private const float AndroidChance = 0.10f;

        public static Species HumanOrAndroid(int requestedSpeciesId)
        {
            if (requestedSpeciesId == Hashes.S("Human") &&
                Rng.Unseeded.Chance(AndroidChance))
            {
                var android = Species.Get(Hashes.S(AndroidSpeciesId));
                if (android != null)
                {
                    return android;
                }
            }
            return Species.Get(requestedSpeciesId);
        }

        public static Species HumanOrAndroid(Species requestedSpecies, Rng rng)
        {
            if (requestedSpecies?.Id == "Human" &&
                rng != null &&
                rng.Chance(AndroidChance))
            {
                var android = Species.Get(Hashes.S(AndroidSpeciesId));
                if (android != null)
                {
                    return android;
                }
            }
            return requestedSpecies;
        }

        public static void ReserveAndroidShare(Dictionary<Species, float> pool)
        {
            if (pool == null)
            {
                return;
            }
            var android = Species.Get(Hashes.S(AndroidSpeciesId));
            if (android == null)
            {
                return;
            }

            var totalWeight = 0f;
            var nonAndroidWeight = 0f;
            foreach (var entry in pool)
            {
                if (entry.Value <= 0f)
                {
                    continue;
                }
                totalWeight += entry.Value;
                if (entry.Key != android)
                {
                    nonAndroidWeight += entry.Value;
                }
            }
            if (totalWeight <= 0f || nonAndroidWeight <= 0f)
            {
                return;
            }
            if (pool.TryGetValue(android, out var existingAndroidWeight) &&
                existingAndroidWeight / totalWeight >= AndroidChance)
            {
                return;
            }

            var nonAndroidScale =
                totalWeight * (1f - AndroidChance) / nonAndroidWeight;
            var species = new List<Species>(pool.Keys);
            foreach (var entry in species)
            {
                if (entry != android)
                {
                    pool[entry] *= nonAndroidScale;
                }
            }
            pool[android] = totalWeight * AndroidChance;
        }
    }

    /// <summary>
    /// These two events still contain one intentional direct Human lookup.
    /// The other population sources moved to ManifestSys, SurvivorSpecies,
    /// and RaiderSpecies in rc332 and are patched at those shared selectors.
    /// </summary>
    [HarmonyPatch]
    internal static class AndroidPopulationSingleHumanCallPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(
                typeof(StasisPodFriendlyCrewStoryEvent),
                "OnStart");
            yield return AccessTools.Method(
                typeof(SpawnSurvivorsOnShuttleLandStoryEvent),
                "OnTick");
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var speciesGet = AccessTools.Method(
                typeof(Species),
                nameof(Species.Get),
                new[] { typeof(int) });
            var replacement = AccessTools.Method(
                typeof(AndroidPopulationChoice),
                nameof(AndroidPopulationChoice.HumanOrAndroid),
                new[] { typeof(int) });

            var replacementCount = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(speciesGet))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replacementCount++;
                }
                yield return instruction;
            }
            if (replacementCount != 1)
            {
                D.Err("PAE expected one direct Human species lookup but replaced {0}",
                    replacementCount);
            }
        }
    }

    /// <summary>
    /// Stasis manifests now create and bind every sleeper before anyone wakes.
    /// Change only the species argument for newly generated Human sleepers;
    /// preset sleepers and existing bound BeingData never pass through here.
    /// </summary>
    [HarmonyPatch(typeof(ManifestSys), "Generate")]
    internal static class AndroidManifestSleeperPatch
    {
        private static void Prefix(ref Species __1, Rng __2)
        {
            __1 = AndroidPopulationChoice.HumanOrAndroid(__1, __2);
        }
    }

    /// <summary>
    /// Rescue camps and peaceful derelicts now obtain their species from this
    /// shared pool. Reserve the established 10% Android share while keeping
    /// the total weight and relative balance of the remaining species intact.
    /// </summary>
    [HarmonyPatch(typeof(SurvivorSpecies), nameof(SurvivorSpecies.WeightedFor))]
    internal static class AndroidSurvivorSpeciesPatch
    {
        private static void Postfix(Dictionary<Species, float> __result)
        {
            AndroidPopulationChoice.ReserveAndroidShare(__result);
        }
    }

    internal static class AndroidSpecialPopulationScope
    {
        [ThreadStatic]
        private static int depth;

        public static bool IsActive => depth > 0;
        public static void Enter() => depth++;
        public static void Exit()
        {
            if (depth > 0)
            {
                depth--;
            }
        }
    }

    /// <summary>
    /// These sources now build faction pools through RaiderSpecies. Mark only
    /// their synchronous selection call so Android eligibility is restored
    /// without adding Androids to unrelated Virus raids or faction content.
    /// </summary>
    [HarmonyPatch]
    internal static class AndroidSpecialPopulationScopePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(
                typeof(DerelictShipWithSurvivorsStoryEvent),
                "AddPostLootStages");
            yield return AccessTools.Method(
                typeof(DerelictShipVirusType1StoryStoryEvent),
                "AddPostLootStages");
            yield return AccessTools.Method(
                typeof(DerelictShipVirusType2StoryStoryEvent),
                "AddPostLootStages");
            yield return AccessTools.Method(
                typeof(DerelictShipTraitorStoryStoryEvent),
                "AddPostLootStages");
            yield return AccessTools.Method(
                typeof(GiftGen),
                "TryItem_Being_N2P");
        }

        private static void Prefix()
        {
            AndroidSpecialPopulationScope.Enter();
        }

        private static Exception Finalizer(Exception __exception)
        {
            AndroidSpecialPopulationScope.Exit();
            return __exception;
        }
    }

    /// <summary>
    /// CombatRaid contains both an explicit ForceHumans branch and a Human
    /// emergency fallback. Only the second Species.Get(Human) call is changed.
    /// </summary>
    [HarmonyPatch(typeof(CombatRaid), "SpawnRaiders")]
    internal static class AndroidCombatRaidFallbackPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var speciesGet = AccessTools.Method(
                typeof(Species),
                nameof(Species.Get),
                new[] { typeof(int) });
            var replacement = AccessTools.Method(
                typeof(AndroidPopulationChoice),
                nameof(AndroidPopulationChoice.HumanOrAndroid),
                new[] { typeof(int) });
            var codes = new List<CodeInstruction>(instructions);
            var humanGetCount = 0;
            var replacementCount = 0;

            for (var i = 0; i < codes.Count; i++)
            {
                var instruction = codes[i];
                if (instruction.Calls(speciesGet) &&
                    i > 0 &&
                    codes[i - 1].LoadsConstant(Hashes.S("Human")))
                {
                    humanGetCount++;
                    if (humanGetCount == 2)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                        replacementCount++;
                    }
                }
                yield return instruction;
            }
            if (replacementCount != 1)
            {
                D.Err(
                    "PAE expected one CombatRaid Human fallback replacement, found {0}",
                    replacementCount);
            }
        }
    }

    // rc290 moved faction raid composition out of faction JSON and into
    // RaiderSpecies. Append the same relative Android weight after Core has
    // built its pool; forced-Human and bots-only pools never call this hook.
    [HarmonyPatch(typeof(RaiderSpecies), nameof(RaiderSpecies.WeightedFor))]
    internal static class AndroidFactionRaiderSpeciesPatch
    {
        private static void Postfix(
            Game.Data.Factions.FactionDef fac,
            Dictionary<Species, float> into)
        {
            if (into == null)
            {
                return;
            }
            if (AndroidSpecialPopulationScope.IsActive)
            {
                AndroidPopulationChoice.ReserveAndroidShare(into);
                return;
            }
            if (fac == null ||
                (fac.Id != "Adventurer" && fac.Id != "OldEarth" &&
                 fac.Id != "Pirate" && fac.Id != "Scavenger"))
            {
                return;
            }
            var android = Species.Get(Hashes.S("Androids"));
            if (android != null)
            {
                into[android] = 0.05f;
            }
        }
    }

    /// <summary>
    /// Core's automatic bulk surgery picker assumes that a species is either
    /// wholly biological or wholly robotic. Androids deliberately combine a
    /// biological species with compatible robotic parts, so use the same
    /// compatibility rules exposed by the manual surgery UI while retaining
    /// every other vanilla availability and quality check.
    /// </summary>
    [HarmonyPatch(typeof(ObjsSys), "BestInstallableBodyPart")]
    internal static class AndroidBestInstallableBodyPartPatch
    {
        private const string AndroidSpeciesId = "Androids";

        private static bool Prefix(
            ObjsSys __instance,
            BodyPartSlot slot,
            ref ObjData __result)
        {
            if (slot?.Body?.Owner?.Species?.Id != AndroidSpeciesId)
            {
                return true;
            }

            ObjData bestPart = null;
            foreach (var candidate in __instance.All.Values)
            {
                if (candidate.BodyPart == null)
                {
                    continue;
                }
                if (!candidate.IsAvailable)
                {
                    continue;
                }
                if (!candidate.BodyPartState.IsUsableSelf)
                {
                    continue;
                }
                if (candidate.BodyPartState.WoundSeverity >
                    Severity.Critical)
                {
                    continue;
                }
                if (!candidate.BodyPart.IsCompatibleWith(
                        slot,
                        out _))
                {
                    continue;
                }
                if (candidate.Mind != null &&
                    candidate.Mind.Faction?.IsPlayer == false)
                {
                    continue;
                }

                bestPart = bestPart == null
                    ? candidate
                    : slot.BetterPart(bestPart, candidate);
            }

            __result = bestPart;
            return false;
        }
    }
}
