using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TraitAndClothingCompatibility {
    // Optional integrations remain dormant unless the exact reviewed mod data is loaded.
    internal static class OptionalTraitCompatibility {
        internal const string ExpandedTraitsAssembly = "ExpandedTraitsMod";
        internal const string ExpandedTraitsDllHash = "FB8C13C813F491B980402BB416806C1139E23525AA8028D4B9E1280973DC1130";
        internal const string ExpandedTraitsDefinitionsHash = "94A39E354BE0C29C0A3B9EF31B6DB12C27D644A8C4F3B3107CA144177F690DE2";
        internal const string GeneratedPetPrefix = "ExpandedTraits.PetAffinity.";

        private static readonly HashSet<string> StaticIds = new HashSet<string>(new[] {
            "AnimalPerson", "Cautious", "Claustrophobic", "ColdAdapted", "Daredevil", "Empath",
            "Extrovert", "HeatAdapted", "HeavySleeper", "Introvert", "IronStomach", "LightSleeper",
            "SensitiveStomach"
        }, StringComparer.Ordinal);

        private static readonly string[] EmptyGeneratedFields = {
            "BodyTypes", "CompatibleSex", "AddsAbilities", "AddsNeeds", "TraitFlagIds",
            "IncompatibleTraitIds", "IncapableJobTypes", "LovesJobTypes", "ProvidesStats",
            "AddsStatModifiers", "AddsAttackMethods", "AdjustsSkills", "Modifiers"
        };

        // Tests can exercise both paths without loading or taking a dependency on Expanded Traits.
        private static bool? reviewedVersionOverride;

        internal static void SetReviewedVersionOverrideForTests(bool? value) => reviewedVersionOverride = value;

        internal static bool Accepts(TraitChange change, IDictionary<string, string> current,
                IDictionary<string, string> optionalReference, bool reviewedVersionPresent) {
            if (change == null || change.Current == null || !reviewedVersionPresent) return false;
            if (change.Id == "CatPerson" || change.Id == "DogPerson") {
                return SameExcept(change.Previous, change.Current, "Rarity")
                    && HasReviewedPetRarity(change.Current, current);
            }
            if (StaticIds.Contains(change.Id)) {
                if (!optionalReference.TryGetValue(change.Id, out string baseline)) return false;
                string[] ignored = change.Id == "AnimalPerson"
                    ? new[] { "CompatibleSpecies", "Rarity" }
                    : new[] { "CompatibleSpecies" };
                return SameExcept(baseline, change.Current, ignored)
                    && (change.Id != "AnimalPerson" || HasReviewedPetRarity(change.Current, current));
            }
            return change.Id.StartsWith(GeneratedPetPrefix, StringComparison.Ordinal)
                && ValidGeneratedPet(change.Id, change.Current, current);
        }

        internal static bool ReviewedVersionPresent() {
            if (reviewedVersionOverride.HasValue) return reviewedVersionOverride.Value;
            try {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, ExpandedTraitsAssembly, StringComparison.Ordinal));
                if (assembly == null || string.IsNullOrEmpty(assembly.Location)
                        || FileHash(assembly.Location) != ExpandedTraitsDllHash) return false;
                string root = Directory.GetParent(Path.GetDirectoryName(assembly.Location))?.FullName;
                string definitions = root == null ? null : Path.Combine(root, "Config", "Traits", "Defs");
                return DefinitionHash(definitions) == ExpandedTraitsDefinitionsHash;
            } catch {
                return false;
            }
        }

        private static bool ValidGeneratedPet(string id, string snapshot,
                IDictionary<string, string> current) {
            if (id.Length == GeneratedPetPrefix.Length) return false;
            JObject value;
            try { value = JObject.Parse(snapshot); } catch { return false; }
            if ((string)value["Id"] != id || (string)value["TypeId"] != "Mental"
                    || (int?)value["Flags"] != 0 || !HasReviewedPetRarity(snapshot, current)) return false;
            return EmptyGeneratedFields.All(name => NullOrEmpty(value[name]));
        }

        private static bool HasReviewedPetRarity(string snapshot, IDictionary<string, string> current) {
            if (!TryRarity(snapshot, out double rarity)) return false;
            int activeGenerated = current.Where(pair => pair.Key.StartsWith(GeneratedPetPrefix, StringComparison.Ordinal))
                .Count(pair => TryRarity(pair.Value, out double value) && value > 0d);
            int familyCount = 3 + activeGenerated;
            double expected = 1d / familyCount;
            return Math.Abs(rarity) < 0.000001d || Math.Abs(rarity - expected) < 0.000001d;
        }

        private static bool TryRarity(string snapshot, out double rarity) {
            rarity = 0d;
            try {
                JToken token = JObject.Parse(snapshot)["Rarity"];
                if (token == null || token.Type == JTokenType.Null) return false;
                rarity = token.Value<double>();
                return true;
            } catch { return false; }
        }

        private static bool SameExcept(string before, string after, params string[] ignored) {
            if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after)) return false;
            try {
                var a = JObject.Parse(before); var b = JObject.Parse(after);
                foreach (string name in ignored) { a.Remove(name); b.Remove(name); }
                return JToken.DeepEquals(a, b);
            } catch { return false; }
        }

        private static bool NullOrEmpty(JToken value) => value == null || value.Type == JTokenType.Null
            || (value is JArray array && array.Count == 0);

        private static string FileHash(string path) {
            using var stream = File.OpenRead(path); using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static string DefinitionHash(string path) {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
            using var data = new MemoryStream();
            foreach (string file in Directory.GetFiles(path, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal)) {
                byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(file)); data.Write(name, 0, name.Length); data.WriteByte(0);
                byte[] bytes = File.ReadAllBytes(file); byte[] length = BitConverter.GetBytes(bytes.Length);
                data.Write(length, 0, length.Length); data.Write(bytes, 0, bytes.Length);
            }
            using var sha = SHA256.Create(); return Hex(sha.ComputeHash(data.ToArray()));
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
    }
}
