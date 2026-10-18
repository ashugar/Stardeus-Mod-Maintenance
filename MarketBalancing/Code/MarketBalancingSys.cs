using System;
using System.Collections.Generic;
using Game.CodeGen;
using Game.Data.Economy;
using Game.Systems;
using Game.Systems.Crafting;
using Game.Utils;
using HarmonyLib;
using KL.Utils;

namespace MarketBalancing {
    /// <summary>Installs session hooks before EconomySys loads the market.</summary>
    public sealed class MarketBalancingSys : GameSystem {
        private const string HarmonyId = "jleas.stardeus.marketbalancing";
        private Harmony harmony;
        public override string Id => "MarketBalancingSys";

        [SelfInit]
        private static void Register() { GameSystems.Register("MarketBalancingSys", () => new MarketBalancingSys()); }

        protected override void OnInitialize() {
            harmony = new Harmony(HarmonyId);
            try {
                harmony.PatchAll(typeof(MarketBalancingSys).Assembly);
                D.Warn("[Market Balancing] 0.1.0 initialized; base-value floors run after Core validation.");
            } catch {
                harmony.UnpatchAll(HarmonyId);
                harmony = null;
                throw;
            }
        }

        public override void Unload() {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            MarketBalance.Restore();
        }
    }

    /// <summary>Owns only the base-value changes made by this mod in the current session.</summary>
    internal static class MarketBalance {
        private sealed class Change {
            internal MarketItem Item;
            internal float Before;
            internal float After;
        }
        private static readonly List<Change> changes = new List<Change>();

        // Restore before Core's next validation as well as on unload. Otherwise Core's
        // profit-margin validator could compound our equal-cost floors on repeated loads.
        internal static void Restore() {
            foreach (var change in changes) {
                // Preserve a later edit by another mod or a replaced market registry.
                if (MarketItem.CFDs.TryGetValue(change.Item.IdH, out var current)
                        && ReferenceEquals(current, change.Item) && current.Value == change.After) {
                    current.Value = change.Before;
                }
            }
            changes.Clear();
        }

        internal static void Apply() {
            var prices = new Dictionary<string, float>(StringComparer.Ordinal);
            var items = new Dictionary<string, MarketItem>(StringComparer.Ordinal);
            foreach (var item in MarketItem.CFDs.Values) {
                if (item?.Id == null || item.Def == null || item.Def.IgnoreMarket
                        || item.Def.MatType?.IgnoreMarket == true || !PriceFloor.IsFinite(item.Value)
                        || item.Value < 0f) { continue; }
                prices[item.Id] = item.Value;
                items[item.Id] = item;
            }

            int converters = 0, unpriced = 0, malformed = 0;
            var recipes = new List<Recipe>();
            foreach (var craftable in Craftable.All) {
                // Reciprocal, deliberately lossy conversions have no finite joint price floor.
                if (string.Equals(craftable.Type, "Converter", StringComparison.Ordinal)) { converters++; continue; }
                if (craftable.Ingredients == null || craftable.Ingredients.Length == 0
                        || craftable.ProductDefId == null || !prices.ContainsKey(craftable.ProductDefId)) { continue; }
                var ingredients = new List<Ingredient>();
                bool invalid = false, missing = false;
                foreach (var mat in craftable.Ingredients) {
                    if (mat.Type?.Def == null || mat.StackSize < 1) { invalid = true; break; }
                    if (mat.Type.IgnoreMarket || mat.Type.Def.IgnoreMarket) { continue; }
                    if (!prices.ContainsKey(mat.Type.Def.Id)) { missing = true; break; }
                    ingredients.Add(new Ingredient(mat.Type.Def.Id, mat.StackSize));
                }
                if (invalid) { malformed++; continue; }
                if (missing) { unpriced++; continue; }
                if (ingredients.Count == 0) { continue; }
                // IgnoreMarket on the RECIPE is intentionally included: it excludes meals,
                // ammunition and many other manufactured goods from vanilla validation.
                recipes.Add(new Recipe(craftable.Id, craftable.ProductDefId,
                    Math.Max(1, craftable.OutputMultiplier), ingredients.ToArray()));
            }

            // Compute the entire plan before mutating any native base values.
            var result = PriceFloor.Calculate(prices, recipes);
            int changed = 0;
            foreach (var entry in result.Prices) {
                var item = items[entry.Key];
                if (entry.Value <= item.Value) { continue; }
                changes.Add(new Change { Item = item, Before = item.Value, After = entry.Value });
                item.Value = entry.Value;
                changed++;
            }
            D.Warn("[Market Balancing] Applied {0} base-value increases across {1} production recipes. "
                + "Skipped: {2} converters, {3} unpriced, {4} malformed, {5} cyclic, {6} overflow.",
                changed, result.EligibleRecipes, converters, unpriced,
                malformed + result.InvalidRecipes.Count, result.CyclicRecipes.Count, result.OverflowRecipes.Count);
            foreach (var change in changes) {
                D.Log("[Market Balancing] {0}: {1} -> {2}", change.Item.Id, change.Before, change.After);
            }
            if (result.CyclicRecipes.Count > 0) {
                D.Warn("[Market Balancing] Cyclic recipes excluded: {0}", string.Join(", ", result.CyclicRecipes));
            }
            if (result.OverflowRecipes.Count > 0) {
                D.Warn("[Market Balancing] Price overflow; recipes excluded: {0}", string.Join(", ", result.OverflowRecipes));
            }
        }
    }

    [HarmonyPatch(typeof(EconomySys), "InitializeMarket", new Type[0])]
    internal static class BeforeMarketInitialize {
        [HarmonyPrefix]
        private static void Prefix() { MarketBalance.Restore(); }
    }

    // Core calls this immediately after its own validator and before prewarming
    // prices or computing net worth. Saved market modifiers remain owned by Core.
    [HarmonyPatch(typeof(EconomySys), "ApplyMarketItems", new Type[0])]
    internal static class BeforeMarketApply {
        [HarmonyPrefix]
        private static void Prefix() {
            try { MarketBalance.Apply(); }
            catch (Exception ex) {
                MarketBalance.Restore();
                D.LogEx(ex, "[Market Balancing] Price calculation failed; restored this mod's changes.");
            }
        }
    }
}
