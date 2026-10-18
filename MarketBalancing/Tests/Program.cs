using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Game.Data;
using Game.Data.Economy;
using Game.Systems.Crafting;
using MarketBalancing;

internal static class Program {
    private static int passed;
    private static void Check(bool condition, string message) {
        if (!condition) { throw new Exception("FAIL: " + message); }
        passed++;
    }
    private static Dictionary<string, float> Prices(params (string Id, float Value)[] values) =>
        values.ToDictionary(x => x.Id, x => x.Value, StringComparer.Ordinal);
    private static Ingredient I(string id, int quantity = 1) => new(id, quantity);
    private static Recipe R(string id, string product, int output, params Ingredient[] ingredients) => new(id, product, output, ingredients);

    private static void Main(string[] args) {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        MathTests();
        AdapterTests();
        if (args.Length > 0) { AuditCore(args[0], args.Length > 1 ? args[1] : null); }
        Console.WriteLine($"PASS_COUNT={passed}; FAIL_COUNT=0");
    }

    private static void MathTests() {
        var baseline = Prices(("water", 30), ("meat", 89), ("meal", 50), ("expensive", 500));
        var recipe = R("survival", "meal", 1, I("water"), I("meat"));
        var result = PriceFloor.Calculate(baseline, new[] { recipe, R("premium", "expensive", 1, I("meat")) });
        Check(result.Prices["meal"] == 119, "survival meal covers both ingredients");
        Check(result.Prices["water"] == 30 && result.Prices["meat"] == 89, "raw values preserved");
        Check(result.Prices["expensive"] == 500, "higher price preserved");
        Check(baseline["meal"] == 50, "calculation leaves baseline untouched");

        var chainPrices = Prices(("raw", 100), ("middle", 1), ("final", 1));
        var chain = new[] { R("last", "final", 2, I("middle", 3)), R("first", "middle", 4, I("raw", 2)) };
        result = PriceFloor.Calculate(chainPrices, chain);
        Check(result.Prices["middle"] == 50 && result.Prices["final"] == 75, "batch quantities and reversed chain");
        var reversed = PriceFloor.Calculate(chainPrices, chain.Reverse());
        Check(result.Prices.All(p => reversed.Prices[p.Key] == p.Value), "recipe order independent");
        var repeated = PriceFloor.Calculate(result.Prices, chain);
        Check(result.Prices.All(p => repeated.Prices[p.Key] == p.Value), "repeated calculation stable");

        result = PriceFloor.Calculate(Prices(("a", 30), ("b", 89), ("p", 1)),
            new[] { R("cheap", "p", 1, I("a", 2)), R("dear", "p", 1, I("b")) });
        Check(result.Prices["p"] == 89, "all alternative production recipes covered by value");
        result = PriceFloor.Calculate(Prices(("a", 7), ("p", 1)), new[] { R("duplicates", "p", 1, I("a"), I("a", 2)) });
        Check(result.Prices["p"] == 21, "duplicate ingredient rows summed");
        result = PriceFloor.Calculate(Prices(("a", 1), ("p", 0)), new[] { R("fraction", "p", 3, I("a")) });
        Check((double)result.Prices["p"] * 3 >= 1, "fractional floor never rounds below ingredients");

        var cycles = new[] { R("ab", "a", 1, I("b", 3)), R("ba", "b", 1, I("a", 3)),
            R("outside", "a", 1, I("raw")), R("downstream", "p", 1, I("a", 2)), R("self", "self", 1, I("self")) };
        result = PriceFloor.Calculate(Prices(("raw", 100), ("a", 2), ("b", 3), ("p", 1), ("self", 10)), cycles);
        Check(result.CyclicRecipes.Count == 3, "mutual and self cycles detected");
        Check(result.Prices["a"] == 100 && result.Prices["b"] == 3 && result.Prices["p"] == 200, "cycles isolated; alternate and downstream production retained");

        result = PriceFloor.Calculate(Prices(("a", 1), ("p", 0), ("nan", float.NaN), ("inf", float.PositiveInfinity), ("neg", -1)),
            new[] { R("missing", "p", 1, I("unknown")), R("empty", "p", 1), R("badq", "p", 1, I("a", 0)), R("badout", "p", 0, I("a")) });
        Check(result.InvalidRecipes.Count == 4 && result.Prices["p"] == 0, "malformed recipes skipped");
        Check(result.Prices.Count == 2, "nonfinite and negative prices excluded");
        result = PriceFloor.Calculate(Prices(("a", float.MaxValue), ("p", 1)), new[] { R("overflow", "p", 1, I("a", 2)) });
        Check(result.OverflowRecipes.Count == 1 && result.Prices["p"] == 1, "overflow rejected without infinite price");

        // Independent generated DAGs verify the actual invariant across varied recipes,
        // large quantities, fractional batches, and randomized input order.
        var random = new Random(215);
        for (int trial = 0; trial < 30; trial++) {
            var values = new Dictionary<string, float>(); var rs = new List<Recipe>();
            for (int n = 0; n < 30; n++) {
                values.Add($"v{n}", (float)(random.NextDouble() * 500));
                if (n > 0) {
                    rs.Add(R($"r{n}", $"v{n}", random.Next(1, 20), I($"v{random.Next(n)}", random.Next(1, 12)), I($"v{random.Next(n)}", random.Next(1, 12))));
                }
            }
            result = PriceFloor.Calculate(values, rs.OrderBy(_ => random.Next()));
            Check(rs.All(r => (double)result.Prices[r.Product] * r.Output >= r.Ingredients.Sum(i => (double)result.Prices[i.Item] * i.Quantity)), "generated DAG floors " + trial);
            Check(values.All(p => result.Prices[p.Key] >= p.Value), "generated DAG increase only " + trial);
        }
    }

    private static MarketItem AddItem(string id, float value) {
        var def = new Def { Id = id }; def.MatType = new MatType { Def = def };
        var item = new MarketItem { Id = id, IdH = MarketItem.CFDs.Count + 1, Def = def, Value = value };
        MarketItem.CFDs.Add(item.IdH, item); return item;
    }
    private static Mat Mat(MarketItem item, int quantity = 1) => new() { Type = item.Def.MatType, StackSize = quantity };
    private static Craftable AddCraft(string id, MarketItem product, string type, params Mat[] inputs) {
        var c = new Craftable { Id = id, ProductDefId = product.Id, Type = type, OutputMultiplier = 1, Ingredients = inputs };
        Craftable.All.Add(c); return c;
    }
    private static void Clear() { MarketBalance.Restore(); MarketItem.CFDs.Clear(); Craftable.All.Clear(); }

    private static void AdapterTests() {
        Clear();
        var water = AddItem("water", 30); var meat = AddItem("meat", 89); var meal = AddItem("meal", 50);
        AddCraft("meal", meal, "Food", Mat(water), Mat(meat)).IgnoreMarket = true;
        MarketBalance.Apply(); Check(meal.Value == 119, "recipe IgnoreMarket included");
        MarketBalance.Restore(); Check(meal.Value == 50, "unload restores original base value");
        MarketBalance.Apply(); Check(meal.Value == 119, "second session has no compounding");
        meal.Value = 777; MarketBalance.Restore(); Check(meal.Value == 777, "later other-mod price preserved");
        meal.Value = 50; MarketBalance.Apply();
        var replacement = new MarketItem { Id = meal.Id, IdH = meal.IdH, Def = meal.Def, Value = 333 };
        MarketItem.CFDs[meal.IdH] = replacement; MarketBalance.Restore();
        Check(replacement.Value == 333, "replacement registry object preserved");

        Clear(); var a = AddItem("a", 20); var b = AddItem("b", 1);
        AddCraft("convert", b, "Converter", Mat(a, 10)); MarketBalance.Apply();
        Check(b.Value == 1, "lossy converter excluded");
        AddCraft("production", b, "Oil", Mat(a, 2)).OutputMultiplier = 4;
        MarketBalance.Apply(); Check(b.Value == 10, "ordinary route to converter product remains eligible");
        Clear(); a = AddItem("a", 20); b = AddItem("b", 1);
        var untraded = new MatType { Def = new Def { Id = "untraded" }, IgnoreMarket = true };
        AddCraft("ignored-material", b, "Food", Mat(a), new Mat { Type = untraded, StackSize = 1 });
        MarketBalance.Apply(); Check(b.Value == 20, "explicitly nonmarket material excluded from valuation");
        Clear(); a = AddItem("a", 20); b = AddItem("b", 1);
        var missing = new MatType { Def = new Def { Id = "missing" } };
        AddCraft("missing", b, "Food", Mat(a), new Mat { Type = missing, StackSize = 1 });
        MarketBalance.Apply(); Check(b.Value == 1, "missing tradable ingredient skips whole recipe");
        Clear(); a = AddItem("a", 20); b = AddItem("b", 1);
        AddCraft("malformed", b, "Food", new Mat { StackSize = 1 });
        MarketBalance.Apply(); Check(b.Value == 1, "unresolved ingredient refused");
        Clear(); a = AddItem("a", 20); b = AddItem("b", 1); b.Def.IgnoreMarket = true;
        AddCraft("hidden-product", b, "Food", Mat(a)); MarketBalance.Apply();
        Check(b.Value == 1, "nonmarket product untouched");
        Clear();
    }

    private static JsonElement Read(string path) {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.Clone();
    }
    private static string S(JsonElement d, string key, string fallback = null) => d.TryGetProperty(key, out var x) ? x.GetString() : fallback;
    private static bool B(JsonElement d, string key) => d.TryGetProperty(key, out var x) && x.GetBoolean();
    private static int N(JsonElement d, string key, int fallback = 0) => d.TryGetProperty(key, out var x) ? x.GetInt32() : fallback;
    private static IEnumerable<string> Files(string core, string folder) => Directory.GetFiles(Path.Combine(core, folder), "*.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal);

    private static void AuditCore(string core, string reportPath) {
        Clear();
        var materialMap = new Dictionary<string, MatType>();
        foreach (var path in Files(core, "Config/Materials")) {
            var d = Read(path); var id = S(d, "Id");
            materialMap[id] = new MatType { IgnoreMarket = B(d, "IgnoreMarket"), Def = new Def { Id = S(d, "DefId", "Obj/Materials/" + id) } };
        }
        var byId = new Dictionary<string, MarketItem>();
        foreach (var path in Files(core, "Config/Economy/MarketItems")) {
            var d = Read(path); if (S(d, "Type") != "CFD") { continue; }
            byId.Add(S(d, "Id"), AddItem(S(d, "Id"), d.GetProperty("Value").GetSingle()));
        }
        foreach (var m in materialMap.Values) {
            var defPath = Path.Combine(core, "Definitions", m.Def.Id.Replace('/', Path.DirectorySeparatorChar) + ".json");
            Check(File.Exists(defPath), "material definition available " + m.Def.Id);
            if (byId.TryGetValue(m.Def.Id, out var mi)) { m.Def = mi.Def; mi.Def.MatType = m; }
        }
        foreach (var path in Files(core, "Config/Craftable")) {
            var d = Read(path); var inputs = new List<Mat>();
            if (d.TryGetProperty("Ingredients", out var array)) {
                foreach (var ing in array.EnumerateArray()) {
                    materialMap.TryGetValue(S(ing, "TypeId"), out var mt);
                    inputs.Add(new Mat { Type = mt, StackSize = N(ing, "StackSize") });
                }
            }
            Craftable.All.Add(new Craftable { Id = Path.GetRelativePath(Path.Combine(core, "Config/Craftable"), path),
                ProductDefId = S(d, "ProductDefId"), Type = S(d, "Type"), IgnoreMarket = B(d, "IgnoreMarket"),
                OutputMultiplier = Math.Max(1, N(d, "OutputMultiplier", 1)), Ingredients = inputs.ToArray() });
        }
        var before = byId.ToDictionary(x => x.Key, x => x.Value.Value);
        MarketBalance.Apply();
        Check(byId["Obj/Meals/SurvivalMeal"].Value >= byId["Obj/Materials/Protein"].Value + byId["Obj/Materials/Water"].Value, "installed Core survival recipe covered");
        int eligible = 0;
        foreach (var c in Craftable.All) {
            if (c.Type == "Converter" || c.Ingredients.Length == 0 || !byId.TryGetValue(c.ProductDefId, out var output)) { continue; }
            if (c.Ingredients.Any(m => m.Type == null || m.StackSize < 1 || (!m.Type.IgnoreMarket && !byId.ContainsKey(m.Type.Def.Id)))) { continue; }
            var cost = c.Ingredients.Where(m => !m.Type.IgnoreMarket && !m.Type.Def.IgnoreMarket).Sum(m => (double)byId[m.Type.Def.Id].Value * m.StackSize);
            if (cost == 0) { continue; }
            Check((double)output.Value * c.OutputMultiplier >= cost, "installed Core floor " + c.Id); eligible++;
        }
        var changed = byId.Where(x => x.Value.Value != before[x.Key]).OrderBy(x => x.Key).Select(x => new { Item = x.Key, Before = before[x.Key], After = x.Value.Value }).ToArray();
        var after = byId.ToDictionary(x => x.Key, x => x.Value.Value);
        MarketBalance.Restore(); Check(byId.All(x => x.Value.Value == before[x.Key]), "Core snapshot restored");
        MarketBalance.Apply(); Check(byId.All(x => x.Value.Value == after[x.Key]), "Core snapshot repeated load stable");
        var report = new { Scope = "Static Core JSON snapshot and adapter stand-ins; before native Core price validation, not gameplay or loaded mod definitions.",
            Core = core, CoreMarketItems = byId.Count, CoreRecipes = Craftable.All.Count, EligibleRecipes = eligible,
            IncreasedItems = changed.Length, Changes = changed };
        if (reportPath != null) { File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); }
        Console.WriteLine($"CORE_SNAPSHOT: {byId.Count} market items, {Craftable.All.Count} recipes, {eligible} eligible, {changed.Length} increases");
        foreach (var c in changed.Take(12)) { Console.WriteLine($"  {c.Item}: {c.Before} -> {c.After}"); }
        Clear();
    }
}
