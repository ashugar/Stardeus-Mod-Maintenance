using Game.Constants;
using Game.Components;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Crafting;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AutoFactory
{
    public static class AutoFactoryRecipeRegistry
    {
        internal const string NanoFabricatorType = "AutoFactory";
        internal const string NanoFoundryType = "AutoFoundry";
        internal const string NanoCloningType = "AutoCloning";
        internal const string NanoFabricatorDefId = "Objects/Devices/NanoFabricator";
        internal const string NanoFoundryDefId = "Objects/Devices/NanoFoundry";
        internal const string NanoCloningDefId = "Objects/Devices/NanoCloningPod";
        private const string RetiredNanoFabricatorType = "AutoFactoryRetired";
        private const string RetiredNanoFoundryType = "AutoFoundryRetired";
        private const string FabricatorRecipePrefix = "AutoFactory/";
        private const string FoundryRecipePrefix = "AutoFoundry/";
        private const string CloningRecipePrefix = "AutoCloning/";
        private const float EnergyMultiplier = 1.10f;
        private const float MinimumProductionTimeHours = 0.10f;
        private const float NanoFabricatorSpeedFactor = 1.00f;
        private const float NanoFoundrySpeedFactor = 0.50f;
        // CraftingSys processes active crafters six times per in-game hour.
        // Charging 1/96 per productive step therefore consumes exactly 1/16,
        // or 0.0625, Nanobots per productive hour for every managed facility.
        private const float NanobotFuelPerProductiveStep = 1f / 96f;
        private const double ThroughputTieRelativeTolerance = 0.000001d;

        private static readonly HashSet<string> ExcludedSourceRecipeTypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                // Converter recipes exchange one manufactured material for
                // another. Auto Factory deliberately does not clone this
                // recipe family, even when a Converter route would otherwise
                // rank as throughput-optimal.
                "Converter"
            };

        private static readonly HashSet<string> CloningPodRecipeTypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                // Core exposes these recipe families exclusively through the
                // Cloning Pod. Preserve that biological-production boundary
                // even when an output, such as Stem Cells, Crawler Goo, or
                // Protein, belongs to an otherwise eligible inventory group.
                "Human",
                "Animal",
                "Organics1",
                "Organics2",
                "Organics3"
            };

        private static readonly HashSet<string> FabricatorBeingRecipeTypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                // Core's Fabricator currently exposes its constructible being
                // products through Robotics2 and Robotics3. Restrict the new
                // being exception to those native Fabricator families so a
                // being made by another specialized facility is not admitted
                // merely because its product definition is a Being.
                "Robotics2",
                "Robotics3"
            };

        private static readonly HashSet<string> ExcludedInventoryGroups =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Ore",
                "Plants",
                "FoodIngredients",
                "Food",
                "Datoids",
                "Plates"
            };

        private static readonly HashSet<string> ExcludedProducts =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Phasium"
            };
        private static readonly HashSet<int> WeaponProductIds = new();
        private static readonly HashSet<int> RoboticBodyPartProductIds = new();
        private static readonly HashSet<int> CloningObjectProductIds = new();
        private static readonly HashSet<int> BeingProductIds = new();
        private static readonly HashSet<int> NanoCloningProductIds = new();
        private static readonly Dictionary<int, bool> FuelBlockedStates =
            new Dictionary<int, bool>();
        private static readonly HashSet<string> ManagedRecipeTypes = new(
            StringComparer.Ordinal)
        {
            NanoFabricatorType,
            NanoFoundryType,
            NanoCloningType
        };
        private static readonly Dictionary<(Def Product, string RecipeType),
            Craftable[]> ManagedRecipesByProductAndType = new();
        private static bool managedRecipeLookupReady;

        // Purpose: Registers all eligible Auto Factory recipe clones and compatibility aliases.
        internal static void RegisterRecipes()
        {
            managedRecipeLookupReady = false;
            ManagedRecipesByProductAndType.Clear();
            try
            {
                InjectEligibleRecipes();
                RebuildManagedRecipeLookup();
            }
            catch (Exception exception)
            {
                managedRecipeLookupReady = false;
                ManagedRecipesByProductAndType.Clear();
                D.LogEx(exception, "AutoFactory failed to register managed facility recipes");
            }
        }

        // Purpose: Clears per-facility fuel-block logging state for a new game session.
        internal static void ResetRuntimeTraceState()
        {
            FuelBlockedStates.Clear();
        }

        // Purpose: Moves fuel-block trace state when a managed facility is relocated.
        internal static void TransferRuntimeTraceState(int sourceId, int targetId)
        {
            if (FuelBlockedStates.TryGetValue(sourceId, out var blocked))
            {
                FuelBlockedStates.Remove(sourceId);
                FuelBlockedStates[targetId] = blocked;
            }
        }

        // Purpose: Removes fuel-block trace state for a facility that no longer exists.
        internal static void ForgetRuntimeTraceState(int factoryId)
        {
            FuelBlockedStates.Remove(factoryId);
        }

        // Purpose: Maps a managed crafter to its private facility recipe family.
        internal static string RecipeTypeFor(MultiCrafterComp crafter)
        {
            return crafter?.Tile?.Definition?.Id switch
            {
                NanoFoundryDefId => NanoFoundryType,
                NanoCloningDefId => NanoCloningType,
                _ => NanoFabricatorType
            };
        }

        // Purpose: Finds the first managed recipe for a material and optional facility type.
        internal static Craftable FindRecipe(MatType material,
            MultiCrafterComp crafter = null)
        {
            return FindRecipe(material?.Def, crafter);
        }

        // Purpose: Finds the first managed recipe for a product and optional facility type.
        internal static Craftable FindRecipe(Def product,
            MultiCrafterComp crafter = null)
        {
            return FindRecipes(product, crafter).FirstOrDefault();
        }

        // Purpose: Returns every equal-best managed route for a product and optional facility.
        internal static Craftable[] FindRecipes(Def product,
            MultiCrafterComp crafter = null)
        {
            return FindRecipesForType(product, crafter == null
                ? NanoFabricatorType : RecipeTypeFor(crafter));
        }

        // Purpose: Finds any managed facility recipe capable of producing a material.
        internal static Craftable FindAnyRecipe(MatType material)
        {
            return FindAnyRecipe(material?.Def);
        }

        // Purpose: Finds a deterministic managed recipe across all facility families.
        internal static Craftable FindAnyRecipe(Def product)
        {
            return product == null
                ? null
                : FindRecipesForType(product, NanoFabricatorType)
                    .Concat(FindRecipesForType(product, NanoFoundryType))
                    .Concat(FindRecipesForType(product, NanoCloningType))
                    .OrderByDescending(recipe => recipe.OutputMultiplier)
                    .ThenBy(recipe => recipe.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
        }

        // Purpose: Finds the first managed recipe for a material in one recipe family.
        internal static Craftable FindRecipeForType(MatType material,
            string recipeType)
        {
            return FindRecipeForType(material?.Def, recipeType);
        }

        // Purpose: Finds the first managed recipe for a product in one recipe family.
        internal static Craftable FindRecipeForType(Def product,
            string recipeType)
        {
            return FindRecipesForType(product, recipeType).FirstOrDefault();
        }

        // Purpose: Returns all equal-best routes for a product in one recipe family.
        internal static Craftable[] FindRecipesForType(Def product,
            string recipeType)
        {
            if (product == null || string.IsNullOrEmpty(recipeType))
            {
                return Array.Empty<Craftable>();
            }

            if (managedRecipeLookupReady
                && ManagedRecipeTypes.Contains(recipeType))
            {
                return ManagedRecipesByProductAndType.TryGetValue(
                    (product, recipeType), out var recipes)
                    ? recipes : Array.Empty<Craftable>();
            }

            return Craftable.All
                .Where(craftable => craftable?.Type == recipeType
                    && craftable.ProductDef == product)
                .OrderBy(craftable => craftable.Id, StringComparer.Ordinal)
                .ToArray();
        }

        // Purpose: Indexes immutable managed recipe families once after clone registration.
        private static void RebuildManagedRecipeLookup()
        {
            ManagedRecipesByProductAndType.Clear();
            foreach (var group in Craftable.All
                         .Where(craftable => craftable?.ProductDef != null
                             && ManagedRecipeTypes.Contains(craftable.Type))
                         .GroupBy(craftable =>
                             (craftable.ProductDef, craftable.Type)))
            {
                ManagedRecipesByProductAndType[group.Key] = group
                    .OrderBy(craftable => craftable.Id,
                        StringComparer.Ordinal)
                    .ToArray();
            }
            managedRecipeLookupReady = true;
        }

        // Purpose: Verifies that a managed recipe has a supported product and complete ingredients.
        internal static bool IsUsableNanoRecipe(Craftable craftable)
        {
            return (craftable?.Type == NanoFabricatorType
                    || craftable?.Type == NanoFoundryType
                    || craftable?.Type == NanoCloningType)
                && craftable.ProductDef != null
                && (craftable.ProductDef.MatType != null
                    || WeaponProductIds.Contains(craftable.ProductDef.IdH)
                    || RoboticBodyPartProductIds.Contains(
                        craftable.ProductDef.IdH)
                    || CloningObjectProductIds.Contains(craftable.ProductDef.IdH)
                    || BeingProductIds.Contains(craftable.ProductDef.IdH))
                && craftable.Ingredients != null
                && craftable.Ingredients.All(ingredient =>
                    ingredient.Type != null);
        }

        // Purpose: Identifies hidden load-only recipe aliases retired by current policy.
        internal static bool IsRetiredNanoRecipe(Craftable craftable)
        {
            return craftable?.Type == RetiredNanoFabricatorType
                || craftable?.Type == RetiredNanoFoundryType;
        }

        // Purpose: Identifies products exposed through the Nano Cloning Pod family.
        internal static bool IsNanoCloningProduct(Def product)
        {
            return product != null
                && NanoCloningProductIds.Contains(product.IdH);
        }

        // Purpose: Identifies a robotic body-part product cloned for the Nano Fabricator.
        internal static bool IsNanoFabricatorRoboticBodyPartProduct(Def product)
        {
            return product != null
                && RoboticBodyPartProductIds.Contains(product.IdH);
        }

        // Purpose: Builds throughput-ranked recipe families and hidden save-compatibility aliases.
        private static void InjectEligibleRecipes()
        {
            var craftableType = typeof(Craftable);
            var allField = craftableType.GetField("all", BindingFlags.Static | BindingFlags.NonPublic);
            var byDefField = craftableType.GetField("byDef", BindingFlags.Static | BindingFlags.NonPublic);
            var orderedField = craftableType.GetField("allOrdered", BindingFlags.Static | BindingFlags.NonPublic);

            var all = allField?.GetValue(null) as Dictionary<int, Craftable>;
            var byDef = byDefField?.GetValue(null) as Dictionary<Def, List<Craftable>>;
            if (all == null || byDef == null)
            {
                D.Err("AutoFactory could not access the Stardeus recipe registry");
                return;
            }

            WeaponProductIds.Clear();
            RoboticBodyPartProductIds.Clear();
            CloningObjectProductIds.Clear();
            BeingProductIds.Clear();
            NanoCloningProductIds.Clear();
            var eligibleCloningSources = Craftable.All
                .Where(source => source?.ProductDef != null
                    && CloningPodRecipeTypes.Contains(source.Type)
                    && source.Ingredients != null
                    && source.Ingredients.All(ingredient =>
                        ingredient.Type != null))
                .ToArray();
            var cloningPodProducts = Craftable.All
                .Where(source => source?.ProductDef != null
                    && CloningPodRecipeTypes.Contains(source.Type))
                .Select(source => source.ProductDef)
                .ToHashSet();
            var previouslyClonedFabricatorSources = Craftable.All
                .Where(IsPreviouslyClonedFabricatorSource)
                .ToArray();
            var eligibleFabricatorSources = Craftable.All
                .Where(IsEligible)
                // The facility boundary is product-wide: a future alternate
                // route must not re-admit something craftable in Cloning Pod.
                .Where(source => !cloningPodProducts.Contains(
                    source.ProductDef))
                .ToArray();
            var fabricatorSources = SelectHighestThroughputRoutes(
                    eligibleFabricatorSources, NanoFabricatorType)
                .ToArray();
            foreach (var nonMaterialProduct in fabricatorSources
                         .Where(source => source.ProductDef?.MatType == null)
                         .Select(source => source.ProductDef))
            {
                if (nonMaterialProduct.IsBeing)
                {
                    BeingProductIds.Add(nonMaterialProduct.IdH);
                }
                else if (IsRoboticBodyPartProduct(nonMaterialProduct))
                {
                    RoboticBodyPartProductIds.Add(nonMaterialProduct.IdH);
                }
                else
                {
                    WeaponProductIds.Add(nonMaterialProduct.IdH);
                }
            }
            var eligibleFoundrySources = Craftable.All
                .Where(source => source != null
                    && (source.Type == "Ore" || source.Type == "Burnable")
                    && source.ProductDef?.MatType != null
                    && source.Ingredients != null
                    && source.Ingredients.All(ingredient => ingredient.Type != null))
                .ToArray();
            var foundrySources = SelectHighestThroughputRoutes(
                    eligibleFoundrySources, NanoFoundryType)
                .ToArray();
            var cloningSources = SelectHighestThroughputRoutes(
                    eligibleCloningSources, NanoCloningType)
                .ToArray();
            foreach (var cloningProduct in cloningSources
                         .Select(source => source.ProductDef)
                         .Where(product => product != null))
            {
                NanoCloningProductIds.Add(cloningProduct.IdH);
            }
            foreach (var nonMaterialProduct in cloningSources
                         .Where(source => source.ProductDef?.MatType == null)
                         .Select(source => source.ProductDef))
            {
                if (nonMaterialProduct.IsBeing)
                {
                    BeingProductIds.Add(nonMaterialProduct.IdH);
                }
                else
                {
                    CloningObjectProductIds.Add(nonMaterialProduct.IdH);
                }
            }

            var added = 0;
            foreach (var source in fabricatorSources)
            {
                var clone = CloneRecipe(source, NanoFabricatorType,
                    FabricatorRecipePrefix);
                if (all.ContainsKey(clone.IdH))
                {
                    continue;
                }

                all.Add(clone.IdH, clone);
                if (!byDef.TryGetValue(clone.ProductDef, out var productRecipes))
                {
                    productRecipes = new List<Craftable>();
                    byDef.Add(clone.ProductDef, productRecipes);
                }

                productRecipes.Add(clone);
                clone.ProductDef.Recipes = productRecipes;
                added++;
            }

            var foundryAdded = 0;
            foreach (var source in foundrySources)
            {
                var clone = CloneRecipe(source, NanoFoundryType,
                    FoundryRecipePrefix);
                if (all.ContainsKey(clone.IdH))
                {
                    continue;
                }
                all.Add(clone.IdH, clone);
                if (!byDef.TryGetValue(clone.ProductDef, out var productRecipes))
                {
                    productRecipes = new List<Craftable>();
                    byDef.Add(clone.ProductDef, productRecipes);
                }
                productRecipes.Add(clone);
                clone.ProductDef.Recipes = productRecipes;
                foundryAdded++;
            }

            var cloningAdded = 0;
            foreach (var source in cloningSources)
            {
                var clone = CloneRecipe(source, NanoCloningType,
                    CloningRecipePrefix);
                if (all.ContainsKey(clone.IdH))
                {
                    continue;
                }
                all.Add(clone.IdH, clone);
                if (!byDef.TryGetValue(clone.ProductDef, out var productRecipes))
                {
                    productRecipes = new List<Craftable>();
                    byDef.Add(clone.ProductDef, productRecipes);
                }
                productRecipes.Add(clone);
                clone.ProductDef.Recipes = productRecipes;
                cloningAdded++;
            }

            // Stardeus saves crafting demands by recipe Id. Keep retired clone
            // Ids resolvable without advertising them to either managed
            // crafter: these aliases are deliberately omitted from byDef and
            // use non-compatible recipe types. AutoFactorySys cancels any
            // restored retired order explicitly during reconciliation.
            var retiredAliasesAdded = RegisterRetiredRecipeAliases(all,
                previouslyClonedFabricatorSources,
                fabricatorSources,
                NanoFabricatorType,
                RetiredNanoFabricatorType,
                FabricatorRecipePrefix)
                + RegisterRetiredRecipeAliases(all,
                    eligibleFoundrySources,
                    foundrySources,
                    NanoFoundryType,
                    RetiredNanoFoundryType,
                    FoundryRecipePrefix);

            orderedField?.SetValue(null, null);
            D.Log("AutoFactory registered {0} throughput-optimal Nano Fabricator recipes, including {1} Weapons products and {2} robotic body-part products; excluded {3} lower-throughput routes, all Converter sources, and all {4} products craftable in Cloning Pod; excluded inventory groups: Ore, Plants, FoodIngredients, Food, Datoids, Plates; excluded products: Phasium",
                added, WeaponProductIds.Count,
                RoboticBodyPartProductIds.Count,
                eligibleFabricatorSources.Length - fabricatorSources.Length,
                cloningPodProducts.Count);
            D.Log("AutoFactory registered {0} throughput-optimal Nano Foundry recipes from the Furnace Ore/Burnable pool; excluded {1} lower-throughput routes",
                foundryAdded,
                eligibleFoundrySources.Length - foundrySources.Length);
            D.Log("AutoFactory registered {0} throughput-optimal Nano Cloning Pod recipes from the Core Cloning Pod families, including {1} being products and {2} non-material cloning products; excluded {3} lower-throughput routes",
                cloningAdded,
                cloningSources.Where(source =>
                        source.ProductDef?.IsBeing == true)
                    .Select(source => source.ProductDef)
                    .Distinct()
                    .Count(),
                CloningObjectProductIds.Count,
                eligibleCloningSources.Length - cloningSources.Length);
            D.Log("AutoFactory registered {0} hidden retired-recipe aliases for save compatibility; retired routes are not available for new orders",
                retiredAliasesAdded);
        }

        // Purpose: Applies source-family exclusions to a potential Nano Fabricator route.
        private static bool IsEligible(Craftable source)
        {
            return IsPotentialFabricatorSource(source)
                && !ExcludedSourceRecipeTypes.Contains(source.Type)
                && !CloningPodRecipeTypes.Contains(source.Type);
        }

        // Purpose: Tests the product and ingredient boundary shared by current and legacy routes.
        private static bool IsPotentialFabricatorSource(Craftable source)
        {
            if (source == null
                || source.Type == NanoFabricatorType
                || source.Type == NanoFoundryType
                || source.Type == NanoCloningType
                || source.Type == RetiredNanoFabricatorType
                || source.Type == RetiredNanoFoundryType
                || source.ProductDef == null)
            {
                return false;
            }

            if (source.Ingredients == null
                || source.Ingredients.Any(ingredient => ingredient.Type == null))
            {
                return false;
            }

            var material = source.ProductDef.MatType;
            if (material == null)
            {
                // Core and installed content can add Robot or Android recipes
                // to ordinary Fabricator families. Discover them from the
                // product definition so Auto Factory does not hard-code a
                // species or another mod's recipe identifier.
                if (source.ProductDef.IsBeing)
                {
                    return FabricatorBeingRecipeTypes.Contains(source.Type);
                }
                // Core represents body parts as object products rather than
                // materials. Admit only non-biological BodyPartDefs from the
                // Robotics recipe families so Cloning Pod biology remains in
                // the Nano Cloning Pod while robotic parts can be fabricated.
                if (IsRoboticBodyPartProduct(source.ProductDef))
                {
                    return source.Type?.StartsWith("Robotics",
                        StringComparison.Ordinal) == true;
                }
                // Handheld weapons and C42 are object products rather than
                // MatTypes. Core groups every supported weapon recipe under a
                // tiered Weapons* crafter type, including future tiers.
                return source.Type?.StartsWith("Weapons",
                    StringComparison.Ordinal) == true;
            }
            return !ExcludedInventoryGroups.Contains(material.GroupId)
                && !ExcludedProducts.Contains(material.Id);
        }

        // Purpose: Uses Core's body-part metadata to distinguish robotic parts from biology.
        private static bool IsRoboticBodyPartProduct(Def product)
        {
            return product != null
                && BodyPartDef.TryForDef(product.IdH, out var bodyPart)
                && !bodyPart.IsBiological;
        }

        // Purpose: Reconstructs the pre-throughput recipe set needed for retired save aliases.
        private static bool IsPreviouslyClonedFabricatorSource(
            Craftable source)
        {
            if (!IsPotentialFabricatorSource(source))
            {
                return false;
            }
            if (source.ProductDef?.MatType?.Id != "Plastic")
            {
                return true;
            }

            // Before throughput ranking, Plastic's Converter route was the
            // sole alternate source filtered at registration time.
            var ingredientIds = source.Ingredients
                .Where(ingredient => ingredient.Type != null)
                .Select(ingredient => ingredient.Type.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return ingredientIds.Length == 2
                && ingredientIds.Contains("Carbon")
                && ingredientIds.Contains("Petroleum");
        }

        // Purpose: Registers hidden aliases so saves can resolve recipe IDs retired by ranking.
        private static int RegisterRetiredRecipeAliases(
            Dictionary<int, Craftable> all,
            IEnumerable<Craftable> priorSources,
            IEnumerable<Craftable> selectedSources,
            string activeRecipeType,
            string retiredRecipeType,
            string recipePrefix)
        {
            var selectedIds = new HashSet<string>(selectedSources
                .Select(source => source.Id), StringComparer.Ordinal);
            var added = 0;
            foreach (var source in priorSources
                         .Where(source => !selectedIds.Contains(source.Id))
                         .OrderBy(source => source.Id, StringComparer.Ordinal))
            {
                var alias = CloneRecipe(source, activeRecipeType,
                    recipePrefix);
                if (all.ContainsKey(alias.IdH))
                {
                    continue;
                }
                alias.Type = retiredRecipeType;
                all.Add(alias.IdH, alias);
                added++;
            }
            return added;
        }

        // Purpose: Keeps only the highest-output-per-hour route or equal-best ties per product.
        private static IEnumerable<Craftable> SelectHighestThroughputRoutes(
            IEnumerable<Craftable> sources,
            string recipeType)
        {
            foreach (var productGroup in sources
                         .Where(source => source?.ProductDef != null)
                         .GroupBy(source => source.ProductDef))
            {
                var bestThroughput = productGroup.Max(source =>
                    ThroughputPerHour(source, recipeType));
                foreach (var source in productGroup
                             .Where(source => IsThroughputTie(
                                 ThroughputPerHour(source, recipeType),
                                 bestThroughput))
                             .OrderBy(source => source.Id,
                                 StringComparer.Ordinal))
                {
                    yield return source;
                }
            }
        }

        // Purpose: Calculates adjusted batch output per production hour for route ranking.
        private static double ThroughputPerHour(Craftable source,
            string recipeType)
        {
            var output = Math.Max(1, source?.OutputMultiplier ?? 1);
            return output / (double)AdjustedProductionTime(source, recipeType);
        }

        // Purpose: Compares route throughput with a small relative floating-point tolerance.
        private static bool IsThroughputTie(double left, double right)
        {
            var scale = Math.Max(1d,
                Math.Max(Math.Abs(left), Math.Abs(right)));
            return Math.Abs(left - right)
                <= ThroughputTieRelativeTolerance * scale;
        }

        // Purpose: Copies a source recipe into an Auto Factory family with adjusted costs and time.
        private static Craftable CloneRecipe(Craftable source,
            string recipeType, string recipePrefix)
        {
            var clone = new Craftable
            {
                Id = recipePrefix + source.Id,
                Type = recipeType,
                ColorGroupId = source.ColorGroupId,
                OutputMultiplier = source.OutputMultiplier,
                Ingredients = source.Ingredients,
                DisposableOutput = source.DisposableOutput,
                ProductDefId = source.ProductDefId,
                ProductionTimeHours = AdjustedProductionTime(source,
                    recipeType),
                EnergyCost = source.EnergyCost * EnergyMultiplier,
                IgnoreMarket = source.IgnoreMarket,
                IgnoreEnergyOutput = source.IgnoreEnergyOutput,
                ListOrder = source.ListOrder
            };
            clone.IdH = Hashes.S(clone.Id);
            return clone;
        }

        // Purpose: Applies facility speed, product tier scaling, and the minimum time floor.
        private static float AdjustedProductionTime(Craftable source,
            string recipeType)
        {
            var tier = Math.Max(1, source?.ProductDef?.Tier ?? 1);
            var speedFactor = recipeType == NanoFoundryType
                ? NanoFoundrySpeedFactor
                : NanoFabricatorSpeedFactor;
            var multiplier = 1f - speedFactor / (tier + 2f);
            return Math.Max(MinimumProductionTimeHours,
                (source?.ProductionTimeHours ?? MinimumProductionTimeHours)
                * multiplier);
        }

        private struct FuelState
        {
            public RefillableStorageComp Fuel;
            public CraftingOrder Order;
            public float ProgressBefore;
            public int ProducedBefore;
            public bool Applies;
        }

        [HarmonyPatch(typeof(CraftingSys), "ProcessCrafter")]
        private static class NanoFabricatorFuelPatch
        {
            // Purpose: Blocks managed production without fuel and captures state for fuel charging.
            private static bool Prefix(MultiCrafterComp crafter, ref FuelState __state)
            {
                __state = default;
                if (crafter?.Tile?.Definition?.Id != NanoFabricatorDefId
                    && crafter?.Tile?.Definition?.Id != NanoFoundryDefId
                    && crafter?.Tile?.Definition?.Id != NanoCloningDefId)
                {
                    return true;
                }

                if (!crafter.Entity.TryGetComponent(out RefillableStorageComp fuel))
                {
                    D.Err("AutoFactory managed facility is missing its Nanobots fuel storage");
                    crafter.ENode?.SetConsumerWantedInput(0f);
                    return false;
                }

                __state.Fuel = fuel;
                __state.Order = crafter.CurrentOrder;
                __state.ProgressBefore = crafter.CurrentOrder?.Progress ?? 0f;
                __state.ProducedBefore = crafter.TotalProduced;
                __state.Applies = true;

                var demand = crafter.CurrentOrder?.Demand;
                if (demand == null)
                {
                    FuelBlockedStates.Remove(crafter.Tile.Id);
                    return true;
                }

                // Fuel starvation must not hide the recipe's downstream
                // material demand. Returning false below skips the vanilla
                // ProcessCrafter body, including its normal ingredient check.
                // Keep the native deficit holder current so other automation
                // can produce unavailable ingredients while Nanobots are being
                // refilled. Native ingredient hauling resumes after fuel is
                // available and ProcessCrafter is allowed to continue.
                var order = crafter.CurrentOrder;
                if (order?.Ingredients == null
                    || order.Ingredients.Any(ingredient =>
                        ingredient.Type == null)
                    || !IsUsableNanoRecipe(order.Demand?.Craftable))
                {
                    LogActivity(crafter,
                        "Cancelled invalid restored order: its recipe or ingredient state is incomplete.",
                        AutoFactoryLogCategory.Cancellation);
                    crafter.UI.StopProducing(order);
                    crafter.ENode?.SetConsumerWantedInput(0f);
                    return false;
                }
                var previousDeficit = order.Deficit?.HasDeficit == true
                    ? order.Deficit.DeficitType
                    : null;
                order.CheckIngredientAvailability(crafter.S);
                var currentDeficit = order.Deficit?.HasDeficit == true
                    ? order.Deficit.DeficitType
                    : null;
                if (currentDeficit != previousDeficit)
                {
                    if (currentDeficit != null)
                    {
                        LogActivity(crafter, "Requested unavailable material: "
                            + currentDeficit.NameT + ".",
                            AutoFactoryLogCategory.Cancellation);
                    }
                    else if (previousDeficit != null)
                    {
                        LogActivity(crafter, "Material shortage cleared: "
                            + previousDeficit.NameT + ".",
                            AutoFactoryLogCategory.Completion);
                    }
                }

                // Use the same per-step amount for both the fuel gate and the
                // completed-progress charge so production never advances on a
                // quantity that the postfix cannot consume.
                const float requiredFuel = NanobotFuelPerProductiveStep;
                var fuelBlocked = fuel.CurrentStock < requiredFuel;
                if (!FuelBlockedStates.TryGetValue(crafter.Tile.Id,
                        out var wasFuelBlocked)
                    || wasFuelBlocked != fuelBlocked)
                {
                    FuelBlockedStates[crafter.Tile.Id] = fuelBlocked;
                    if (fuelBlocked)
                    {
                        LogActivity(crafter,
                            "Production blocked: insufficient Nanobots fuel.",
                            AutoFactoryLogCategory.Cancellation);
                    }
                    else if (wasFuelBlocked)
                    {
                        LogActivity(crafter,
                            "Nanobots fuel restored; production may resume.",
                            AutoFactoryLogCategory.Completion);
                    }
                }

                if (fuel.CurrentStock >= requiredFuel)
                {
                    fuel.ClearDeficitFlag();
                    // RefillableStorage normally starts a top-up goal once the
                    // tank falls below half of its configured target. That goal
                    // occupies the same entity goal slot used by ingredient
                    // hauling and blocks MultiCrafter processing even though
                    // ample working fuel remains. Production takes precedence
                    // over a nonessential top-up; genuinely insufficient fuel
                    // still follows the refill path below.
                    if (fuel.TryGetGoal(out var refillGoal))
                    {
                        AIGoal.Cancel(refillGoal, G.Ticks);
                    }
                    return true;
                }

                fuel.NotifyLack();
                fuel.CreateOrUpdateRefillGoal();
                crafter.CheckOperator(false);
                crafter.ENode?.SetConsumerWantedInput(0f);
                return false;
            }

            // Purpose: Charges Nanobot fuel only for production progress completed by the tick.
            private static void Postfix(MultiCrafterComp crafter, FuelState __state)
            {
                if (!__state.Applies || __state.Fuel == null)
                {
                    return;
                }

                var produced = crafter.TotalProduced > __state.ProducedBefore;
                var progressed = __state.Order != null
                    && __state.Order.Progress > __state.ProgressBefore;
                if (!produced && !progressed)
                {
                    return;
                }

                const float fuelPerProductiveStep = NanobotFuelPerProductiveStep;
                if (!__state.Fuel.TryConsume(fuelPerProductiveStep))
                {
                    D.Err("AutoFactory managed facility advanced production without sufficient Nanobots fuel");
                }
            }
        }

        // Purpose: Routes recipe and fuel messages through Auto Factory's categorized activity log.
        private static void LogActivity(MultiCrafterComp crafter, string message,
            AutoFactoryLogCategory category = AutoFactoryLogCategory.Information)
        {
            AutoFactorySys.LogActivity(crafter, message, category);
        }

        [HarmonyPatch(typeof(MultiCrafterUI), nameof(MultiCrafterUI.GetCraftableItems))]
        private static class NanoFabricatorCraftableListPatch
        {
            // Purpose: Restricts managed New Order lists to usable recipes for the current facility.
            private static void Postfix(
                MultiCrafterComp ___crafter,
                CrafterConfig ___Settings,
                ref List<Craftable> __result)
            {
                if (___crafter?.Tile?.Definition?.Id != NanoFabricatorDefId
                    && ___crafter?.Tile?.Definition?.Id != NanoFoundryDefId
                    && ___crafter?.Tile?.Definition?.Id != NanoCloningDefId)
                {
                    return;
                }

                __result ??= new List<Craftable>();
                if (__result.Count == 0)
                {
                    Craftable.ForCrafter(___Settings.CompatibleTypes, __result);
                }

                // Keep broad compatible-type discovery constrained to the
                // recipes cloned and validated by this registry. This includes
                // supported Weapons, Cloning Pod objects, and being products,
                // while excluding every unrelated broad-compatible recipe.
                __result.RemoveAll(craftable => !IsUsableNanoRecipe(craftable));
                var expectedType = RecipeTypeFor(___crafter);
                __result.RemoveAll(craftable => craftable.Type != expectedType);
            }
        }
    }
}
