using System;
using System.Collections.Generic;

namespace MarketBalancing {
    /// <summary>One priced ingredient in a production batch.</summary>
    internal sealed class Ingredient {
        internal readonly string Item;
        internal readonly int Quantity;
        internal Ingredient(string item, int quantity) { Item = item; Quantity = quantity; }
    }

    /// <summary>A recipe expressed in market item IDs and native batch quantities.</summary>
    internal sealed class Recipe {
        internal readonly string Id;
        internal readonly string Product;
        internal readonly int Output;
        internal readonly List<Ingredient> Ingredients;
        internal Recipe(string id, string product, int output, params Ingredient[] ingredients) {
            Id = id; Product = product; Output = output;
            Ingredients = new List<Ingredient>(ingredients);
        }
    }

    internal sealed class FloorResult {
        internal readonly Dictionary<string, float> Prices = new Dictionary<string, float>(StringComparer.Ordinal);
        internal readonly List<string> CyclicRecipes = new List<string>();
        internal readonly List<string> InvalidRecipes = new List<string>();
        internal readonly List<string> OverflowRecipes = new List<string>();
        internal int EligibleRecipes;
    }

    /// <summary>Computes increase-only base values without touching game objects or saves.</summary>
    internal static class PriceFloor {
        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static FloorResult Calculate(IDictionary<string, float> baseline, IEnumerable<Recipe> recipes) {
            var result = new FloorResult();
            foreach (var pair in baseline) {
                if (pair.Key != null && IsFinite(pair.Value) && pair.Value >= 0f) {
                    result.Prices.Add(pair.Key, pair.Value);
                }
            }
            var valid = new List<Recipe>();
            var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var id in result.Prices.Keys) { graph.Add(id, new HashSet<string>(StringComparer.Ordinal)); }
            foreach (var recipe in recipes) {
                if (!IsValid(recipe, result.Prices)) {
                    result.InvalidRecipes.Add(recipe?.Id ?? "<null>");
                    continue;
                }
                valid.Add(recipe);
                foreach (var ingredient in recipe.Ingredients) { graph[recipe.Product].Add(ingredient.Item); }
            }

            // A recipe closing a dependency loop cannot safely participate in a general
            // increase-only floor. Drop those recipes, retaining independent recipes for
            // the same products and all downstream production.
            var accepted = new Dictionary<string, List<Recipe>>(StringComparer.Ordinal);
            foreach (var recipe in valid) {
                bool cyclic = false;
                foreach (var ingredient in recipe.Ingredients) {
                    if (HasPath(ingredient.Item, recipe.Product, graph)) { cyclic = true; break; }
                }
                if (cyclic) { result.CyclicRecipes.Add(recipe.Id); continue; }
                if (!accepted.TryGetValue(recipe.Product, out var list)) {
                    list = new List<Recipe>(); accepted.Add(recipe.Product, list);
                }
                list.Add(recipe);
                result.EligibleRecipes++;
            }

            var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var consumers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var id in result.Prices.Keys) {
                dependencies.Add(id, new HashSet<string>(StringComparer.Ordinal));
                consumers.Add(id, new HashSet<string>(StringComparer.Ordinal));
            }
            foreach (var group in accepted) {
                foreach (var recipe in group.Value) {
                    foreach (var ingredient in recipe.Ingredients) {
                        dependencies[group.Key].Add(ingredient.Item);
                        consumers[ingredient.Item].Add(group.Key);
                    }
                }
            }
            var ready = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in dependencies) { if (entry.Value.Count == 0) { ready.Add(entry.Key); } }
            while (ready.Count > 0) {
                var id = ready.Min; ready.Remove(id);
                if (accepted.TryGetValue(id, out var list)) {
                    foreach (var recipe in list) {
                        double total = 0d;
                        foreach (var ingredient in recipe.Ingredients) {
                            total += (double)result.Prices[ingredient.Item] * ingredient.Quantity;
                        }
                        double floor = total / recipe.Output;
                        if (floor > float.MaxValue || double.IsInfinity(floor)) {
                            result.OverflowRecipes.Add(recipe.Id); continue;
                        }
                        if (floor > result.Prices[id]) { result.Prices[id] = RoundUp(floor); }
                    }
                }
                foreach (var consumer in consumers[id]) {
                    dependencies[consumer].Remove(id);
                    if (dependencies[consumer].Count == 0) { ready.Add(consumer); }
                }
            }
            return result;
        }

        private static bool IsValid(Recipe recipe, Dictionary<string, float> prices) {
            if (recipe?.Product == null || !prices.ContainsKey(recipe.Product)
                    || recipe.Output < 1 || recipe.Ingredients.Count == 0) { return false; }
            foreach (var ingredient in recipe.Ingredients) {
                if (ingredient?.Item == null || ingredient.Quantity < 1 || !prices.ContainsKey(ingredient.Item)) {
                    return false;
                }
            }
            return true;
        }

        private static bool HasPath(string start, string target, Dictionary<string, HashSet<string>> graph) {
            var pending = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Add(start);
            while (pending.Count > 0) {
                var node = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                if (node == target) { return true; }
                if (!visited.Add(node)) { continue; }
                foreach (var next in graph[node]) { pending.Add(next); }
            }
            return false;
        }

        // Core stores base values as floats. Round toward positive infinity so a
        // fractional output batch never falls just below its ingredient floor.
        private static float RoundUp(double value) {
            float rounded = (float)value;
            if ((double)rounded < value) {
                int bits = BitConverter.ToInt32(BitConverter.GetBytes(rounded), 0);
                rounded = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0);
            }
            return rounded;
        }
    }
}
