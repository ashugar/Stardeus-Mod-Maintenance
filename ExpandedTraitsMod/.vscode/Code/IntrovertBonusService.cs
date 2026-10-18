using System.Collections.Generic;
using Game.Data;
using Game.Systems.Creatures;
using Game.Systems.Mental;
using Game.Systems.Traits;
using HarmonyLib;

namespace ExpandedTraitsMod {
    internal static class IntrovertBonusService {
        internal static void Reconcile(GameState state) {
            if (state?.Beings?.Alive == null) return;
            foreach (Being being in state.Beings.Alive) EnsureFor(being?.Data?.Mind?.Traits);
        }

        internal static void EnsureFor(BeingTraits traits) {
            if (traits?.Owner == null || traits.Owner.Id == 0 || traits.Slots.Count >= 4
                    || !traits.HasTrait(ExpandedTraitIds.IntrovertH)) return;
            if (TraitDefinitionRegistry.HasAnyPetTrait(traits)) return;
            IReadOnlyList<TraitDef> petTraits = TraitDefinitionRegistry.PetTraits();
            uint seed = traits.Owner.Id * 2654435761u;
            if ((seed & 1u) != 0u || petTraits.Count == 0) return;
            int index = (int)((seed >> 1) % (uint)petTraits.Count);
            TraitDef selected = petTraits[index];
            if (selected?.IsAvailableForSpecies(traits.Owner.Species) == true) traits.AddTrait(selected);
        }
    }

    [HarmonyPatch]
    internal static class IntrovertBonusAfterMindRegistrationPatch {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(BeingsSys), "RegisterMind", new[] { typeof(Mind) });

        private static void Postfix(Mind __0, bool __result) {
            if (!__result) return;
            IntrovertBonusService.EnsureFor(__0?.Traits);
            AppearanceSocialSkillService.EnsureFor(__0);
        }
    }
}
