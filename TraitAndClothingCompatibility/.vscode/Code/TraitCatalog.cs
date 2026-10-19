using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Game.Components;
using Game.Systems.AI;
using Game.Constants;
using Game.Data;
using Game.Systems.Items;
using Game.Systems.Mental;
using Game.Systems.Policies;
using Game.Systems.Traits;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TraitAndClothingCompatibility {
    internal static class TraitCatalog {
        internal const string TestedCoreVersion = "v0.15.0-rc337.3219";
        internal const string TestedGameHash = "38985F083AF23E557664230BC835BBF8BED3A4256F4F346C32E8428D68B33FA4";
        internal const string TestedConstantsHash = "F49F4FAEF233ED15DEA4BB3061E196ACD1C16E193FC49B3FB20C6F3A5F599909";
        internal static readonly HashSet<string> ShippedIds = new HashSet<string>(new[] {
            "Ablutophobic", "AlwaysHungry", "Antisocial", "Astrophile", "Astrophobe", "BadAppetite",
            "Beautiful", "Bookworm", "BWaveResistent", "Carnivore", "CatPerson", "Charismatic",
            "CleaningOcd", "Clumsy", "CoffeeAddict", "Curious", "DogPerson", "Enlightened", "Fast",
            "Fastidious", "FastMetabolism", "Gigolo", "Handsome", "Hooker", "Insomniac", "Lazy",
            "LongSleeper", "Minimalist", "OpenEyedSleeper", "Optimist", "Pacifist", "Paranoid",
            "Pessimist", "Psychopath", "Sedentary", "ShadowDweller", "ShortSleeper", "Showerer",
            "Shy", "Slow", "SlowMetabolism", "SlowPooper", "Sociopath", "Stalker", "Stoic", "Technophile",
            "Technophobe", "Thrifty", "Ugly", "Vegan", "Wimp", "Workaholic"
        }, StringComparer.Ordinal);

        // Exact optional-mod hooks reviewed by version and module MVID. Rebuilt or changed
        // modules get new snapshots and become unvalidated again until separately reviewed.
        internal static readonly HashSet<string> ValidatedPatches = new HashSet<string>(new[] {
            "Game.Systems.AI.AIActionFindBestEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): finalizer / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Finalizer / 21cff6f4-a4cf-4638-9a2b-12e0444ea3db / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindBestEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): prefix / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Prefix / 21cff6f4-a4cf-4638-9a2b-12e0444ea3db / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): finalizer / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Finalizer / 21cff6f4-a4cf-4638-9a2b-12e0444ea3db / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): prefix / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Prefix / 21cff6f4-a4cf-4638-9a2b-12e0444ea3db / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindBestEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): finalizer / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Finalizer / e7abb8be-08ae-47a0-92bb-626504e51949 / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindBestEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): prefix / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Prefix / e7abb8be-08ae-47a0-92bb-626504e51949 / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): finalizer / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Finalizer / e7abb8be-08ae-47a0-92bb-626504e51949 / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): prefix / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Prefix / e7abb8be-08ae-47a0-92bb-626504e51949 / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindBestEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): finalizer / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Finalizer / 5eb2c05e-1725-4d0b-ae5c-6768b6f15319 / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindBestEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): prefix / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Prefix / 5eb2c05e-1725-4d0b-ae5c-6768b6f15319 / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): finalizer / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Finalizer / 5eb2c05e-1725-4d0b-ae5c-6768b6f15319 / priority=400 / before= / after=",
            "Game.Systems.AI.AIActionFindEquipment.Game.Systems.AI.AIActionResult Execute(Game.Data.GameState, Game.Systems.AI.AIAgentComp, Game.Systems.AI.AIGoal, Int64): prefix / jleas.quantumstorage / Game.Systems.QuantumStorage.QuantumObjectPositionRoutingContextPatch.Prefix / 5eb2c05e-1725-4d0b-ae5c-6768b6f15319 / priority=400 / before= / after=",
            "Game.Systems.Items.InventoryItemSlot.Game.Systems.Items.ObjData BetterItem(Game.Systems.Items.ObjData, Game.Systems.Items.ObjData): prefix / ExpandedTraitsMod.Runtime.Bootstrap / ExpandedTraitsMod.MinimalistEquipmentPatch.Prefix / 9dc01d16-8daa-4e5f-a1be-a5b5be1b9858 / priority=400 / before= / after=",
            "Game.Systems.Mental.MentalHealth.Void AddEffect(Game.Data.GameState, Game.Systems.Mental.MentalEffect, Single, Boolean): prefix / ExpandedTraitsMod.Runtime.Bootstrap / ExpandedTraitsMod.SleepMoodSeverityPatch.Prefix / 9dc01d16-8daa-4e5f-a1be-a5b5be1b9858 / priority=400 / before= / after=",
            "Game.Systems.Items.InventoryItemSlot.Game.Systems.Items.ObjData BetterItem(Game.Systems.Items.ObjData, Game.Systems.Items.ObjData): prefix / ExpandedTraitsMod.Runtime.Bootstrap / ExpandedTraitsMod.MinimalistEquipmentPatch.Prefix / 95e164e0-26dd-451e-984e-11e28c262fd0 / priority=400 / before= / after=",
            "Game.Systems.Mental.MentalHealth.Void AddEffect(Game.Data.GameState, Game.Systems.Mental.MentalEffect, Single, Boolean): prefix / ExpandedTraitsMod.Runtime.Bootstrap / ExpandedTraitsMod.SleepMoodSeverityPatch.Prefix / 95e164e0-26dd-451e-984e-11e28c262fd0 / priority=400 / before= / after="
        }, StringComparer.Ordinal);

        private static readonly string[] Fields = {
            "Id", "NameT", "DescT", "IconId", "TypeId", "BodyTypes", "CompatibleSpecies", "CompatibleSex",
            "AddsAbilities", "AddsNeeds", "TraitFlagIds", "Rarity", "IncompatibleTraitIds", "IncapableJobTypes",
            "LovesJobTypes", "ProvidesStats", "AddsStatModifiers", "AddsAttackMethods", "AdjustsSkills", "Modifiers"
        };

        internal static Dictionary<string, string> ReferenceSnapshot() {
            return ReadReference("BaselineTraits.json");
        }

        internal static Dictionary<string, string> ReferenceExpandedTraits() => ReadReference("BaselineExpandedTraits.json");

        internal static Dictionary<string, string> ReferenceEquipment() => ReadReference("BaselineEquipment.json");

        private static Dictionary<string, string> ReadReference(string name) {
            using var stream = typeof(TraitCatalog).Assembly.GetManifestResourceStream("TACC." + name);
            if (stream == null) throw new InvalidOperationException("TACC's validated reference is missing: " + name);
            using var reader = new StreamReader(stream);
            var result = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
            if (result == null || result.Count == 0) throw new InvalidOperationException("TACC's validated reference is empty: " + name);
            return result;
        }

        internal static Dictionary<string, string> Snapshot() {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var trait in TraitDef.All.Values) {
                if (trait == null || string.IsNullOrEmpty(trait.Id)) continue;
                var obj = new JObject();
                foreach (string name in Fields) {
                    FieldInfo field = typeof(TraitDef).GetField(name, BindingFlags.Instance | BindingFlags.Public);
                    if (field != null) obj[name] = Project(field.GetValue(trait), 0);
                }
                // Runtime-derived flags can change without a corresponding text field edit.
                obj["Flags"] = (int)trait.Flags;
                result[trait.Id] = obj.ToString(Formatting.None);
            }
            return result;
        }

        private static JToken Project(object value, int depth) {
            if (value == null) return JValue.CreateNull();
            Type type = value.GetType();
            if (type.IsEnum) return new JValue(value.ToString());
            if (value is string || type.IsPrimitive || value is decimal) return JToken.FromObject(value);
            if (depth > 8) return new JValue(type.FullName);
            if (value is IEnumerable sequence) {
                var array = new JArray();
                foreach (object item in sequence) array.Add(Project(item, depth + 1));
                return array;
            }
            var obj = new JObject();
            foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .OrderBy(f => f.Name, StringComparer.Ordinal)) {
                if (f.Name == "Job") continue; // Parsed SkillAdjustment.Job points into the game registry.
                // Core sets this flag on a successful config read; it is not a trait/effect value.
                // Exclude only this reviewed field, preserving all actual property values and other types.
                if (f.DeclaringType == typeof(SerializableProperty) && f.Name == nameof(SerializableProperty.IsUsed)) continue;
                obj[f.Name] = Project(f.GetValue(value), depth + 1);
            }
            return obj;
        }

        internal static string Hash(string input) {
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? "")));
        }

        internal static string GameHash() => AssemblyHash(typeof(ItemOwnership).Assembly);

        private static string AssemblyHash(Assembly assembly) {
            using var stream = File.OpenRead(assembly.Location);
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

        internal static Dictionary<string, string> ReferenceCode() => new Dictionary<string, string> {
            ["Game.dll"] = TestedGameHash, ["Game.Constants.dll"] = TestedConstantsHash
        };

        internal static Dictionary<string, string> CodeSnapshot() => new Dictionary<string, string> {
            ["Game.dll"] = GameHash(), ["Game.Constants.dll"] = AssemblyHash(typeof(Tunables).Assembly)
        };

        internal static Dictionary<string, string> EquipmentSnapshot() {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            // Reflect constants rather than referring to them directly: C# otherwise inlines build-time values.
            foreach (Type type in new[] { typeof(ItemOwnership), typeof(WornItemMood) }) {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .Where(f => f.IsLiteral))
                    result[type.Name + "." + field.Name] = ValueText(field.GetRawConstantValue());
            }
            foreach (var field in typeof(Tunables).GetFields(BindingFlags.Public | BindingFlags.Static)) {
                if (new[] { "Tattered", "SecondHand", "DeadMans", "WornItem", "WornGear", "ClothingMood" }
                        .Any(term => field.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
                    result["Tunables." + field.Name] = ValueText(field.GetValue(null));
            }
            return result;
        }

        private static string ValueText(object value) => value is float number
            ? number.ToString("R", CultureInfo.InvariantCulture) : Project(value, 0).ToString(Formatting.None);

        internal static List<DiagnosticEntry> FindUnvalidated(IDictionary<string, string> referenceTraits,
                IDictionary<string, string> traits, IDictionary<string, string> referenceEquipment,
                IDictionary<string, string> equipment, IDictionary<string, string> referenceCode,
                IDictionary<string, string> code, string patches) {
            var findings = new List<DiagnosticEntry>();
            bool optionalTraitsReviewed = OptionalTraitCompatibility.ReviewedVersionPresent();
            Dictionary<string, string> optionalReference = ReferenceExpandedTraits();
            foreach (var change in TraitChangeTracker.Compare(referenceTraits, traits, ShippedIds, false)) {
                if (OptionalTraitCompatibility.Accepts(change, traits, optionalReference, optionalTraitsReviewed)) continue;
                string kind = change.Previous == null ? "Unknown trait" : "Unvalidated trait change";
                string detail = change.Previous == null
                    ? "This trait is unknown to TACC. " + Describe(change.Current)
                    : change.Current == null ? "This validated trait is missing from the loaded registry. " + Describe(change.Previous)
                    : "This trait differs from TACC's validated reference. " + ChangedFields(change.Previous, change.Current);
                findings.Add(Finding("trait", change.Id, change.Previous, change.Current, kind, detail));
            }
            AddValueFindings(findings, "Equipment effect", referenceEquipment, equipment);
            AddValueFindings(findings, "Equipment effect code", referenceCode, code);
            // Only exact reviewed snapshots are accepted. Owner, target, patch method, module MVID,
            // priority, or ordering changes make the patch unknown again.
            foreach (string patch in (patches ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                if (ValidatedPatches.Contains(patch)) continue;
                findings.Add(Finding("patch", patch, null, patch, "Unknown equipment-effect patch",
                    "This patch is unknown to TACC; its effect on clothing behavior has not been validated. " + patch));
            }
            return findings;
        }

        private static void AddValueFindings(List<DiagnosticEntry> findings, string scope,
                IDictionary<string, string> reference, IDictionary<string, string> current) {
            foreach (var change in TraitChangeTracker.Compare(reference, current, ShippedIds, false)) {
                string detail = change.Previous == null ? "This equipment-effect entry is unknown to TACC."
                    : "This equipment-effect entry differs from TACC's validated reference; the change is unknown to this mod.";
                detail += " Validated: " + (change.Previous ?? "<not validated>") + ". Observed: " + (change.Current ?? "<missing>") + ".";
                findings.Add(Finding(scope, change.Id, change.Previous, change.Current,
                    change.Previous == null ? "Unknown equipment effect" : "Unvalidated " + scope.ToLowerInvariant(), detail));
            }
        }

        private static DiagnosticEntry Finding(string scope, string subject, string before, string after, string kind, string detail) =>
            new DiagnosticEntry {
                FindingKey = scope + ":" + Hash(subject + "\0" + before + "\0" + after),
                Kind = kind, Subject = subject,
                Detail = detail + "\nThis remains unvalidated until a TACC update includes a reviewed comparison. Clearing the log does not validate it."
            };

        internal static string ChangedFields(string before, string after) {
            if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after)) return "Definition catalog entry changed.";
            var a = JObject.Parse(before);
            var b = JObject.Parse(after);
            var names = a.Properties().Select(p => p.Name).Union(b.Properties().Select(p => p.Name))
                .Where(name => !JToken.DeepEquals(a[name], b[name])).ToList();
            return "Changed fields: " + string.Join(", ", names) + ".\n"
                + string.Join("\n", names.Select(name => name + ": " + Compact(a[name]) + " → " + Compact(b[name])));
        }

        internal static string Describe(string snapshot) {
            if (string.IsNullOrEmpty(snapshot)) return "";
            var obj = JObject.Parse(snapshot);
            return "Name: " + (string)obj["NameT"] + ". Description: " + (string)obj["DescT"]
                + ". Type: " + (string)obj["TypeId"] + ". Rarity: " + Compact(obj["Rarity"]) + ".";
        }

        private static string Compact(JToken token) {
            string text = token?.ToString(Formatting.None) ?? "null";
            return text.Length <= 220 ? text : text.Substring(0, 220) + "…";
        }

        internal static IEnumerable<MethodInfo> InspectedMethods() {
            const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;
            return typeof(ItemOwnership).GetMethods(declared).Concat(typeof(WornItemMood).GetMethods(declared))
                .Concat(typeof(AINeedEquipment).GetMethods(declared))
                .Concat(typeof(EquipmentPolicyFilter).GetMethods(declared))
                .Concat(typeof(MentalHealth).GetMethods(declared).Where(m => m.Name == "AddEffect" || m.Name == "TryNewFor"
                    || m.Name == "SetPermanentEffect" || m.Name == "RemovePermanentEffect" || m.Name == "PermanentEffect"))
                .Concat(typeof(MentalEffect).GetMethods(declared).Where(m => m.Name == "Create" || m.Name == "CreateWithIcon"
                    || m.Name == "CreatePermanent"))
                .Concat(new[] {
                    AccessTools.Method(typeof(InventoryItemSlot), nameof(InventoryItemSlot.BetterItem)),
                    AccessTools.Method(typeof(AIActionFindEquipment), nameof(AIActionFindEquipment.Execute)),
                    AccessTools.Method(typeof(AIActionFindBestEquipment), nameof(AIActionFindBestEquipment.Execute)),
                    AccessTools.PropertyGetter(typeof(StorableObjComp), nameof(StorableObjComp.IsForbidden)),
                    AccessTools.PropertyGetter(typeof(ObjData), "IsTattered")
                });
        }

        internal static string PatchSnapshot() {
            var parts = new List<string>();
            foreach (MethodInfo method in InspectedMethods()) {
                if (method == null) continue;
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                var groups = new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers };
                string[] kinds = { "prefix", "postfix", "transpiler", "finalizer" };
                for (int i = 0; i < groups.Length; i++) foreach (var patch in groups[i]) {
                    var p = patch.PatchMethod;
                    if (patch.owner == TaccSystem.HarmonyId) continue;
                    parts.Add(method.DeclaringType.FullName + "." + method + ": " + kinds[i] + " / " + patch.owner
                        + " / " + p?.DeclaringType?.FullName + "." + p?.Name + " / " + p?.Module.ModuleVersionId
                        + " / priority=" + patch.priority + " / before=" + string.Join(",", patch.before ?? Array.Empty<string>())
                        + " / after=" + string.Join(",", patch.after ?? Array.Empty<string>()));
                }
            }
            return string.Join("\n", parts.OrderBy(p => p, StringComparer.Ordinal));
        }
    }
}
