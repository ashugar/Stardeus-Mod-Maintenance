using System;
using System.Collections.Generic;
using System.Linq;
using Game.CodeGen;
using Game.Constants;
using Game.Systems.Creatures;
using Game.Systems.Traits;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace ExpandedTraitsMod {
    internal static class TraitDefinitionRegistry {
        private const float PetFamilyWeight = 1f;
        private static readonly HashSet<int> Generated = new HashSet<int>();
        private static readonly HashSet<int> ActiveGenerated = new HashSet<int>();
        private static readonly int CatH = Hashes.S("Cat");
        private static readonly int DogH = Hashes.S("Dog");
        private static readonly int CrawlerH = Hashes.S("Crawler");
        private static readonly int CatPersonH = Hashes.S("CatPerson");
        private static readonly int DogPersonH = Hashes.S("DogPerson");
        private static bool registering;

        internal static void RegisterLoadedPets() {
            if (registering || TraitDef.All.Count == 0 || Species.Known.Count == 0) return;
            registering = true;
            try {
                foreach (Species species in Species.Known.Values.OrderBy(s => s.Id, StringComparer.Ordinal)) {
                    if (!IsPetSpecies(species)) continue;
                    if (species.IdH == CatH || species.IdH == DogH) continue;
                    EnsureGenerated(ExpandedTraitIds.PetTraitId(species.Id), species);
                }
                WidenModTraitCompatibility();
                NormalizePetFamilyRarity();
            } finally {
                registering = false;
            }
        }

        internal static bool IsPetSpecies(Species species) =>
            species != null && (species.IsPet || species.IdH == CrawlerH);

        internal static TraitDef EnsureGenerated(string id, Species species = null) {
            if (!ExpandedTraitIds.IsGeneratedPetTrait(id)) return null;
            int idH = Hashes.S(id);
            if (TraitDef.All.Count == 0) TraitDef.LoadKnown();
            if (species == null && Species.Known.Count != 0) {
                string speciesId = id.Substring(ExpandedTraitIds.PetPrefix.Length);
                species = Species.Known.Values.FirstOrDefault(s =>
                    string.Equals(s.Id, speciesId, StringComparison.Ordinal));
            }
            bool active = IsPetSpecies(species);
            if (TraitDef.All.TryGetValue(idH, out TraitDef existing)) {
                Generated.Add(idH);
                if (active) ActivateGenerated(existing, species);
                return existing;
            }
            TraitType mental = TraitType.Get("Mental");
            if (mental == null) return null;
            string fallback = id.Substring(ExpandedTraitIds.PetPrefix.Length).Replace('_', ' ');
            string petName = species?.Name ?? fallback;
            var def = new TraitDef {
                Id = id,
                IdH = idH,
                NameT = petName + " Person",
                DescT = active
                    ? "Especially enjoys friendly " + petName
                        + " companions. This definition is generated from the species loaded with the game."
                    : "Affinity retained for save compatibility. Its species is not currently loaded.",
                IconId = PetIcon(species),
                TypeId = "Mental",
                Type = mental,
                CompatibleSpecies = CompatibleHumanoids(),
                Rarity = active ? 0.1f : 0f,
                StatSources = Array.Empty<Game.Systems.Stats.StatSourceData>(),
                StatModifiers = Array.Empty<(int, Game.Systems.Stats.StatModifier)>(),
                Flags = TraitFlags.None
            };
            TraitDef.All[idH] = def;
            Generated.Add(idH);
            if (active) ActivateGenerated(def, species);
            return def;
        }

        private static void ActivateGenerated(TraitDef trait, Species species) {
            if (trait == null || !IsPetSpecies(species)) return;
            TraitType mental = TraitType.Get("Mental");
            if (mental == null) return;
            string petName = species.Name ?? species.Id.Replace('_', ' ');
            trait.NameT = petName + " Person";
            trait.DescT = "Especially enjoys friendly " + petName
                + " companions. This definition is generated from the species loaded with the game.";
            trait.IconId = PetIcon(species);
            trait.TypeId = "Mental";
            trait.Type = mental;
            trait.CompatibleSpecies = CompatibleHumanoids();
            trait.Rarity = 0.1f;
            ActiveGenerated.Add(trait.IdH);
            if (!TraitDef.ByType.TryGetValue(mental, out List<TraitDef> list)) {
                list = new List<TraitDef>();
                TraitDef.ByType[mental] = list;
            }
            if (!list.Contains(trait)) list.Add(trait);
        }

        internal static bool IsSpecificPetTrait(TraitDef trait) => trait != null &&
            (trait.IdH == CatPersonH || trait.IdH == DogPersonH
                || Generated.Contains(trait.IdH) || ExpandedTraitIds.IsGeneratedPetTrait(trait.Id));

        internal static Species SpeciesFor(TraitDef trait) {
            if (trait == null) return null;
            if (trait.IdH == CatPersonH) return Species.Known.TryGetValue(CatH, out Species cat) ? cat : null;
            if (trait.IdH == DogPersonH) return Species.Known.TryGetValue(DogH, out Species dog) ? dog : null;
            if (!ExpandedTraitIds.IsGeneratedPetTrait(trait.Id)) return null;
            string id = trait.Id.Substring(ExpandedTraitIds.PetPrefix.Length);
            return Species.Known.Values.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        }

        internal static int AffinityValue(BeingTraits traits, Species petSpecies) {
            if (traits == null || petSpecies == null) return 0;
            foreach (TraitSlot slot in traits.Slots) {
                TraitDef trait = slot.Trait;
                if (!IsSpecificPetTrait(trait)) continue;
                if (SpeciesFor(trait)?.IdH == petSpecies.IdH) return 4;
            }
            return traits.HasTrait(ExpandedTraitIds.AnimalPersonH) ? 2 : 0;
        }

        internal static bool HasAnyPetTrait(BeingTraits traits) {
            if (traits == null) return false;
            foreach (TraitSlot slot in traits.Slots) {
                TraitDef trait = slot.Trait;
                if (trait?.IdH == ExpandedTraitIds.AnimalPersonH || IsSpecificPetTrait(trait)) {
                    return true;
                }
            }
            return false;
        }

        internal static IReadOnlyList<TraitDef> PetTraits() {
            var list = new List<TraitDef>();
            AddIfFound(list, CatPersonH);
            AddIfFound(list, DogPersonH);
            AddIfFound(list, ExpandedTraitIds.AnimalPersonH);
            foreach (TraitDef trait in ActiveGenerated
                    .Select(id => TraitDef.All.TryGetValue(id, out TraitDef found) ? found : null)
                    .Where(trait => trait != null)
                    .OrderBy(trait => trait.Id, StringComparer.Ordinal)) {
                if (!list.Contains(trait)) list.Add(trait);
            }
            return list;
        }

        private static void AddIfFound(List<TraitDef> list, int id) {
            if (TraitDef.All.TryGetValue(id, out TraitDef trait) && !list.Contains(trait)) list.Add(trait);
        }

        private static void NormalizePetFamilyRarity() {
            IReadOnlyList<TraitDef> traits = PetTraits();
            if (traits.Count == 0) return;
            float each = PetFamilyWeight / traits.Count;
            foreach (TraitDef trait in traits) trait.Rarity = each;
        }

        private static void WidenModTraitCompatibility() {
            string[] compatible = CompatibleHumanoids();
            int[] ids = {
                ExpandedTraitIds.AnimalPersonH, ExpandedTraitIds.IntrovertH,
                ExpandedTraitIds.ExtrovertH, ExpandedTraitIds.EmpathH,
                ExpandedTraitIds.LightSleeperH, ExpandedTraitIds.HeavySleeperH,
                ExpandedTraitIds.IronStomachH, ExpandedTraitIds.SensitiveStomachH,
                ExpandedTraitIds.ClaustrophobicH, ExpandedTraitIds.DaredevilH,
                ExpandedTraitIds.CautiousH, ExpandedTraitIds.HeatAdaptedH,
                ExpandedTraitIds.ColdAdaptedH
            };
            foreach (int id in ids) {
                if (TraitDef.All.TryGetValue(id, out TraitDef trait)) trait.CompatibleSpecies = compatible;
            }
            foreach (int id in Generated) {
                if (TraitDef.All.TryGetValue(id, out TraitDef trait)) trait.CompatibleSpecies = compatible;
            }
        }

        private static string[] CompatibleHumanoids() {
            string[] found = Species.Known.Values
                .Where(s => s.IsHumanoid && s.GrantsMentalHealth)
                .Select(s => s.Id).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
            return found.Length == 0 ? new[] { "Human" } : found;
        }

        private static string PetIcon(Species species) {
            if (species?.IdH == DogH) return "Icons/Color/PreviewDog01";
            return species?.Def?.PreviewIcon ?? "Icons/Color/PreviewCat01";
        }
    }

    [HarmonyPatch(typeof(TraitDef), nameof(TraitDef.LoadKnown), new Type[] { })]
    internal static class TraitDefsLoadedPatch {
        private static void Postfix() => TraitDefinitionRegistry.RegisterLoadedPets();
    }

    [HarmonyPatch]
    internal static class SpeciesLoadedPatch {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Species), "LoadKnown", Type.EmptyTypes);
        private static void Postfix() => TraitDefinitionRegistry.RegisterLoadedPets();
    }

    [HarmonyPatch(typeof(TraitDef), nameof(TraitDef.Get), new[] { typeof(string), typeof(int) })]
    internal static class MissingGeneratedTraitPatch {
        private static bool Prefix(string type, ref TraitDef __result) {
            if (!ExpandedTraitIds.IsGeneratedPetTrait(type)) return true;
            __result = TraitDefinitionRegistry.EnsureGenerated(type);
            return __result == null;
        }
    }
}
