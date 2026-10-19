using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

static partial class Program
{
    sealed class PanelTestRow
    {
        internal string Key;
        internal int Count;
        internal bool Valid = true;
    }

    sealed class UnreviewedCallbacks
    {
        public void WithParameter(int count) { }
        public int WithReturnValue() => 0;
        public void Generic<T>() { }
    }

    static void MaterialPanelTests()
    {
        int firstCheck = checks;
        var patch = ModAssembly.GetType("Game.Systems.QuantumStorage.QuantumMaterialPanelPatch", true);
        var coreMethod = AccessTools.DeclaredMethod(typeof(Game.Systems.UIUX.MatStorageTab), "BuildContentsList", Type.EmptyTypes);
        string reviewed = (string)patch.GetField("ReviewedBodyHash", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
        string actual = (string)AccessTools.Method(patch, "BodyHash").Invoke(null, new object[] { coreMethod });
        Check(reviewed == actual, "exact installed Core material list body is reviewed");
        Check((bool)AccessTools.Method(patch, "Prepare").Invoke(null, null), "optional UI patch enables on installed Core");
        Check((bool)AccessTools.Method(patch, "IsReviewedBody").Invoke(null, new object[] { coreMethod }),
            "production body guard accepts the reviewed installed method");
        Check(!(bool)AccessTools.Method(patch, "IsReviewedBody").Invoke(null, new object[] { null }),
            "missing Core method fails closed");
        Check(!(bool)AccessTools.Method(patch, "IsReviewedBody").Invoke(null,
            new object[] { AccessTools.Method(typeof(Program), "PrintUiHash") }),
            "production body guard rejects an unreviewed implementation");
        Check((bool)AccessTools.Method(patch, "IsReviewedContract").Invoke(null, new object[] { coreMethod }),
            "production guard accepts the reviewed root and complete native action contract");
        Check(!(bool)AccessTools.Method(patch, "IsReviewedContract").Invoke(null, new object[] { null }),
            "missing root cannot enable the callback contract");
        var callbacks = PanelTypes(typeof(Game.Systems.UIUX.MatStorageTab))
            .SelectMany(t => t.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.Name.StartsWith("<BuildContentsList>", StringComparison.Ordinal)).ToArray();
        Check(callbacks.Length == 3, "all three native contents actions are guarded");
        Check(callbacks.All(m => !m.IsStatic && !m.IsGenericMethod && m.ReturnType == typeof(void) &&
            m.GetParameters().Length == 0), "reviewed native callback signatures are exact instance actions");
        var fingerprints = callbacks.Select(m => new KeyValuePair<string, string>(
            m.DeclaringType.FullName + "::" + m.Name,
            (string)AccessTools.Method(patch, "BodyHash").Invoke(null, new object[] { m }))).ToArray();
        bool Matches(IEnumerable<KeyValuePair<string, string>> candidate) =>
            (bool)AccessTools.Method(patch, "MatchesReviewedCallbacks").Invoke(null, new object[] { candidate });
        Check(Matches(fingerprints), "current callback identities and bodies match the production guard");
        Check(Matches(fingerprints.Reverse()), "reflection enumeration order does not affect the contract");
        for (int i = 0; i < fingerprints.Length; i++)
        {
            var changedCallback = fingerprints.ToArray();
            changedCallback[i] = new(fingerprints[i].Key, "UNREVIEWED CALLBACK BODY");
            Check(!Matches(changedCallback), "callback-only body change fails closed while the root is unchanged");
            Check(!Matches(fingerprints.Where((_, index) => index != i)), "missing native action fails closed");
            changedCallback[i] = new(fingerprints[i].Key + "(changed signature)", fingerprints[i].Value);
            Check(!Matches(changedCallback), "changed callback identity fails closed");
        }
        Check(!Matches(null), "missing callback discovery fails closed");
        Check(!Matches(Array.Empty<KeyValuePair<string, string>>()), "empty callback discovery fails closed");
        Check(!Matches(fingerprints.Append(fingerprints[0])), "duplicate callback discovery fails closed");
        Check(!Matches(fingerprints.Append(new("unknown callback", fingerprints[0].Value))),
            "additional unreviewed action fails closed");
        Check(!Matches(fingerprints.Append(new(null, fingerprints[0].Value))), "invalid callback signature fails closed");
        Check(AccessTools.Method(patch, "CallbackKey").Invoke(null,
            new object[] { AccessTools.Method(typeof(Program), "PrintUiHash") }) == null,
            "static methods cannot masquerade as reviewed instance action callbacks");
        foreach (string name in new[] { "WithParameter", "WithReturnValue", "Generic" })
            Check(AccessTools.Method(patch, "CallbackKey").Invoke(null,
                new object[] { typeof(UnreviewedCallbacks).GetMethod(name) }) == null,
                "changed action signature fails closed: " + name);
        MaterialPanelCoreContractTests();
        Check((string)AccessTools.Method(patch, "BodyHash").Invoke(null, new object[] { AccessTools.Method(typeof(Program), "PrintUiHash") }) != reviewed,
            "unreviewed method body does not match UI gate");

        var source = Enumerable.Range(0, 1000).ToDictionary(i => $"Material {i:D4}", i => i + 1);
        var sorted = source.OrderBy(p => p.Key).ToList();
        var rows = sorted.Select(p => new PanelTestRow { Key = p.Key, Count = p.Value }).ToList();
        var originalRows = rows.ToArray();
        var cache = new PanelRows<string, PanelTestRow>(sorted, rows);
        int textWrites = 0, moves = 0;
        bool Update(IList<KeyValuePair<string,int>> current) => cache.TryUpdate(current, r => r.Valid,
            (r, count) => { textWrites++; r.Count = count; },
            (r, index) => { moves++; rows.Remove(r); rows.Insert(index, r); });
        Check(Update(sorted), "initial identical notification reuses all 1000 rows");
        Check(textWrites == 0 && moves == 0, "identical notification performs no UI writes");

        var rng = new Random(733);
        for (int i = 0; i < 10000; i++)
        {
            int index = rng.Next(sorted.Count);
            var key = sorted[index].Key;
            source[key]++;
            sorted[index] = new KeyValuePair<string, int>(key, source[key]);
            Check(Update(sorted), "quantity-only update is reusable");
        }
        Check(textWrites == 10000 && moves == 0, "10000 quantity updates write only 10000 changed labels, not 10 million rows");
        Check(rows.SequenceEqual(originalRows), "every original row and its native action identity is retained");
        Check(rows.All(r => r.Count == source[r.Key]), "all displayed quantities match the full source after deposits");

        source[rows[0].Key] = 600000;
        source[rows[1].Key] = 0;
        source[rows[2].Key] = int.MaxValue;
        sorted = source.OrderBy(p => p.Key).ToList();
        Check(Update(sorted), "large stacks and zero quantities remain reusable");
        Check(rows[0].Count == 600000 && rows[1].Count == 0 && rows[2].Count == int.MaxValue,
            "large-stack, zero, and maximum integer labels retain exact quantities");
        for (int i = 0; i < 100; i++)
        {
            foreach (var key in source.Keys.Take(20).ToArray()) { source[key] = rng.Next(10); }
            sorted = i % 3 == 0 ? source.OrderBy(p => p.Key).ToList() : i % 3 == 1 ?
                source.OrderByDescending(p => p.Value).ToList() : source.OrderBy(p => p.Value).ToList();
            Check(Update(sorted), "native name/count ordering is accepted without row recreation");
            Check(rows.Select(r => r.Key).SequenceEqual(sorted.Select(p => p.Key)), "all ties and sort positions preserved exactly");
            Check(rows.All(r => r.Count == source[r.Key]), "quantity counts remain current while resorting");
        }
        int beforeWrites = textWrites, beforeMoves = moves;
        Check(!Update(sorted.Take(sorted.Count - 1).ToList()), "removed material requires full native rebuild");
        Check(!Update(sorted.Append(new KeyValuePair<string,int>("new", 1)).ToList()), "new material requires full native rebuild");
        var replaced = sorted.ToList(); replaced[0] = new KeyValuePair<string,int>("replacement", 1);
        Check(!Update(replaced), "same-sized replacement type requires rebuild");
        var duplicate = sorted.ToList(); duplicate[0] = duplicate[1];
        Check(!Update(duplicate), "duplicate identity fails closed to native rebuild");
        rows.Last().Valid = false;
        var changed = sorted.Select(p => new KeyValuePair<string,int>(p.Key, p.Value + 1)).ToList();
        Check(!Update(changed), "disposed row requires rebuild even when other quantities changed");
        Check(textWrites == beforeWrites && moves == beforeMoves, "rejected reuse never partially edits the UI");

        var empty = new PanelRows<string, PanelTestRow>(new List<KeyValuePair<string,int>>(), new List<PanelTestRow>());
        Check(empty.TryUpdate(new List<KeyValuePair<string,int>>(), _ => false, (_,_) => throw new Exception(), (_,_) => throw new Exception()),
            "empty native state is reused without any action changes");
        Check(!empty.TryUpdate(new List<KeyValuePair<string,int>> { new("first", 1) }, _ => true, (_,_) => {}, (_,_) => {}),
            "empty-to-stock transition invokes native full list construction");
        Console.WriteLine($"PASS: {checks-firstCheck} material-panel checks; 1000 rows retained, 10000 quantity notifications -> 10000 label writes / zero row replacements. Unity rendering and click interaction require in-game validation.");
    }

    static IEnumerable<Type> PanelTypes(Type type)
    {
        yield return type;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var child in PanelTypes(nested)) yield return child;
    }
}
