using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TraitAndClothingCompatibility;

internal static class Program {
    private static int assertions;

    private static int Main(string[] args) {
        try {
            if (args.Length < 1 || args.Length > 2)
                throw new ArgumentException("Provide the fingerprint resource path, optionally preceded by an explicit Newtonsoft.Json DLL path.");
            if (args.Length == 2) {
                if (!Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]))
                    throw new ArgumentException("The explicit Newtonsoft.Json DLL path must exist.");
                Assembly.LoadFrom(args[0]);
            }
            return Run(args[args.Length - 1]);
        } catch (Exception error) {
            Console.Error.WriteLine("FAIL synthetic fingerprint tests: " + error.GetType().Name);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string referencePath) {
        Assert((string)OptionalTraitFingerprint.Project(new JValue("abc")) ==
            "TACC.String.SHA256.v1:BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", "known digest");
        JObject original = JObject.Parse("{\"Text\":\"Synthetic reference\",\"Nested\":{\"Items\":[\"one\",null,true,1,1.0]}}");
        JToken untouched = original.DeepClone(), projected = OptionalTraitFingerprint.Project(original);
        Assert(JToken.DeepEquals(original, untouched), "input untouched");
        Assert(projected["Nested"]["Items"][3].Type == JTokenType.Integer, "integer type retained");
        Assert(projected["Nested"]["Items"][4].Type == JTokenType.Float, "float type retained");
        Assert(projected["Nested"]["Items"][1].Type == JTokenType.Null && (bool)projected["Nested"]["Items"][2], "null and boolean retained");
        Assert((string)projected["Text"] != "Synthetic reference", "authored string replaced");
        Assert(JToken.DeepEquals(OptionalTraitFingerprint.Project(JObject.Parse("{\"Value\":\"a\\u0062c\"}")),
            OptionalTraitFingerprint.Project(JObject.Parse("{\"Value\":\"abc\"}"))), "decoded escapes agree");
        Assert(!JToken.DeepEquals(OptionalTraitFingerprint.Project(new JValue("\u00e9")),
            OptionalTraitFingerprint.Project(new JValue("e\u0301"))), "Unicode normalization not introduced");
        bool refused = false;
        try { OptionalTraitFingerprint.Project(new JValue(new string((char)0xd800, 1))); }
        catch (EncoderFallbackException) { refused = true; }
        Assert(refused, "unpaired surrogate refused");
        Assert(OptionalTraitCompatibility.IsReviewedExpandedTraitsDllHash(
            OptionalTraitCompatibility.ExpandedTraits013DllHash), "reviewed Expanded Traits 0.1.3 DLL hash accepted");
        Assert(OptionalTraitCompatibility.IsReviewedExpandedTraitsDllHash(
            OptionalTraitCompatibility.ExpandedTraits014DllHash), "reviewed Expanded Traits 0.1.4 DLL hash accepted");
        Assert(!OptionalTraitCompatibility.IsReviewedExpandedTraitsDllHash(
            new string('0', 64)), "unknown Expanded Traits DLL hash refused");

        string[] staticIds = { "AnimalPerson", "Cautious", "Claustrophobic", "ColdAdapted", "Daredevil", "Empath", "Extrovert", "HeatAdapted", "HeavySleeper", "Introvert", "IronStomach", "LightSleeper", "SensitiveStomach" };
        JObject resource = JObject.Parse(File.ReadAllText(referencePath));
        Assert(resource.Properties().Count() == staticIds.Length, "shipped reference entry count");
        foreach (JProperty entry in resource.Properties()) {
            Assert(staticIds.Contains(entry.Name, StringComparer.Ordinal) && entry.Value.Type == JTokenType.String, "shipped reference dictionary format");
            JObject snapshot = JObject.Parse((string)entry.Value);
            foreach (JToken token in snapshot.Descendants().Where(t => t.Type == JTokenType.String)) {
                string fingerprint = (string)token;
                Assert(fingerprint.StartsWith(OptionalTraitFingerprint.StringPrefix, StringComparison.Ordinal)
                    && fingerprint.Length == OptionalTraitFingerprint.StringPrefix.Length + 64
                    && fingerprint.Substring(OptionalTraitFingerprint.StringPrefix.Length).All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F'), "shipped reference string is a SHA256 fingerprint");
            }
        }

        string baseline = "{\"Id\":\"Cautious\",\"NameT\":\"Synthetic name\",\"DescT\":\"Synthetic description\",\"CompatibleSpecies\":[\"Synthetic species\"],\"Rarity\":0.2,\"Nested\":{\"Text\":\"abc\",\"Zero\":0.0,\"Order\":[\"first\",\"second\"]},\"Empty\":null}";
        var references = Reference("Cautious", baseline);
        Assert(Accepts("Cautious", baseline, references), "reference accepted");
        Assert(!Accepts("Cautious", baseline, references, false), "unreviewed version refused");
        Assert(!Accepts("Cautious", baseline, new Dictionary<string, string>()), "missing reference refused");
        Assert(!Accepts("Cautious", baseline, new Dictionary<string, string> { ["Cautious"] = baseline }), "plaintext reference refused");
        Assert(!OptionalTraitCompatibility.Accepts(null, new Dictionary<string, string>(), references, true), "null change refused");
        Assert(!OptionalTraitCompatibility.Accepts(new TraitChange { Id = "Cautious" }, new Dictionary<string, string>(), references, true), "missing current refused");
        Variant(baseline, references, v => v["CompatibleSpecies"] = new JArray("Other synthetic species"), true, "ignored species");
        Variant(baseline, references, v => v.Remove("CompatibleSpecies"), true, "ignored missing species");
        Variant(baseline, references, v => v["NameT"] = "Synthetic NAME", false, "case-sensitive strings");
        Variant(baseline, references, v => v["DescT"] = "Different synthetic description", false, "changed authored field");
        Variant(baseline, references, v => v["Nested"]["Zero"] = 1e-16, true, "close-float tolerance");
        Variant(baseline, references, v => v["Nested"]["Zero"] = 1e-8, false, "numerical change");
        Variant(baseline, references, v => v["Nested"]["Zero"] = 0, false, "integer versus float");
        Variant(baseline, references, v => v["Nested"]["Zero"] = -0.0, true, "signed zero");
        Variant(baseline, references, v => v["Nested"]["Order"] = new JArray("second", "first"), false, "array order");
        Variant(baseline, references, v => v.Remove("Empty"), false, "missing versus null");
        Variant(baseline, references, v => v["Additional"] = JValue.CreateNull(), false, "extra null field");
        Variant(baseline, references, v => v["Rarity"] = 0.3, false, "ordinary static rarity");
        JObject reordered = JObject.Parse(baseline);
        JToken text = reordered["Nested"]["Text"]; text.Parent.Remove(); ((JObject)reordered["Nested"]).Add("Text", text);
        JToken name = reordered["NameT"]; name.Parent.Remove(); reordered.Add("NameT", name);
        Assert(Accepts("Cautious", reordered.ToString(Formatting.None), references), "root and nested property order");

        foreach (string id in staticIds) {
            JObject value = JObject.Parse(baseline); value["Id"] = id;
            if (id == "AnimalPerson") value["Rarity"] = 1d / 3d;
            string snapshot = value.ToString(Formatting.None);
            Assert(Accepts(id, snapshot, Reference(id, snapshot)), "recognized static ID: " + id);
        }
        JObject animal = JObject.Parse(baseline); animal["Id"] = "AnimalPerson";
        var animalReference = Reference("AnimalPerson", animal.ToString(Formatting.None));
        animal["Rarity"] = 1d / 3d;
        Assert(Accepts("AnimalPerson", animal.ToString(Formatting.None), animalReference), "AnimalPerson dynamic rarity");
        animal["Rarity"] = 0d;
        Assert(Accepts("AnimalPerson", animal.ToString(Formatting.None), animalReference), "AnimalPerson zero rarity");
        animal["Rarity"] = 0.2;
        Assert(!Accepts("AnimalPerson", animal.ToString(Formatting.None), animalReference), "AnimalPerson rarity gate");
        foreach (string id in new[] { "CatPerson", "DogPerson" }) {
            JObject previous = JObject.Parse(baseline); previous["Id"] = id;
            JObject current = (JObject)previous.DeepClone(); current["Rarity"] = 1d / 3d;
            Assert(Accepts(id, current.ToString(Formatting.None), new Dictionary<string, string>(), true, previous.ToString(Formatting.None)), "native pet plaintext comparison: " + id);
            current["NameT"] = "Another synthetic name";
            Assert(!Accepts(id, current.ToString(Formatting.None), new Dictionary<string, string>(), true, previous.ToString(Formatting.None)), "native pet static change: " + id);
        }
        string generatedId = OptionalTraitCompatibility.GeneratedPetPrefix + "Synthetic";
        string generated = new JObject { ["Id"] = generatedId, ["TypeId"] = "Mental", ["Flags"] = 0, ["Rarity"] = 0.25 }.ToString(Formatting.None);
        Assert(Accepts(generatedId, generated, new Dictionary<string, string>()), "generated pet structural path");
        JObject changedGenerated = JObject.Parse(generated); changedGenerated["Flags"] = 1;
        Assert(!Accepts(generatedId, changedGenerated.ToString(Formatting.None), new Dictionary<string, string>()), "generated pet flags checked");
        Console.WriteLine("PASS synthetic optional-trait fingerprint tests: " + assertions + " assertions.");
        return 0;
    }

    private static Dictionary<string, string> Reference(string id, string snapshot) =>
        new Dictionary<string, string> { [id] = OptionalTraitFingerprint.Project(JObject.Parse(snapshot)).ToString(Formatting.None) };

    private static bool Accepts(string id, string current, IDictionary<string, string> references, bool reviewed = true, string previous = null) =>
        OptionalTraitCompatibility.Accepts(new TraitChange { Id = id, Current = current, Previous = previous },
            new Dictionary<string, string> { [id] = current }, references, reviewed);

    private static void Variant(string baseline, IDictionary<string, string> references, Action<JObject> mutate, bool expected, string name) {
        JObject value = JObject.Parse(baseline); mutate(value);
        string current = value.ToString(Formatting.None);
        JObject before = JObject.Parse(baseline), after = JObject.Parse(current);
        before.Remove("CompatibleSpecies"); after.Remove("CompatibleSpecies");
        Assert(JToken.DeepEquals(before, after) == expected, "legacy premise: " + name);
        Assert(Accepts("Cautious", current, references) == expected, name);
    }

    private static void Assert(bool value, string name) {
        if (!value) throw new InvalidOperationException(name);
        assertions++;
    }
}
