using Game.Systems;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Crafting;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Systems.Mental;
using Game.Systems.UIUX;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Data.Factions;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AutoUtilitiesModFramework;

namespace AutoFactory
{
    internal enum AutoFactoryScope
    {
        Global,
        Room
    }

    internal enum AutoFactoryLogCategory
    {
        Information,
        Completion,
        Cancellation,
        Issue,
        Manual,
        CrossFacility
    }

    [Flags]
    internal enum PlanningDirtyOrigin
    {
        None = 0,
        Initialization = 1 << 0,
        ScopeChanged = 1 << 1,
        AutomationToggled = 1 << 2,
        ProductionTargetChanged = 1 << 3,
        ProductionPriorityChanged = 1 << 4,
        ProductionTargetsCleared = 1 << 5,
        SaveLoaded = 1 << 6,
        FacilityRelocated = 1 << 7,
        FacilityRegistered = 1 << 8,
        FacilityRemoved = 1 << 9,
        DeficitChanged = 1 << 10,
        MaterialInventoryChanged = 1 << 11,
        ObjectInventoryChanged = 1 << 12,
        BeingInventoryChanged = 1 << 13,
        OrderCompleted = 1 << 14,
        ManualOrder = 1 << 15,
        OrderStopped = 1 << 16,
        ExceptionRetry = 1 << 17,
        Fallback = 1 << 18,
        GoalProbeContinuation = 1 << 19,
        ResearchChanged = 1 << 20,
        ProductionGoalTableLoaded = 1 << 21
    }

    public sealed class AutoFactorySys : GameSystem, ISaveable
    {
        public const string SysId = "AutoFactorySys";
        public const string ConsumerId = "AutoFactory";
        public const string RequiredUtilitiesVersion = "1.4.0";
        private const string HarmonyId = "AutoFactory.Runtime";
        private const int DefaultFactoryPriority = 6;
        private const int MaximumFactoryPriority = 6;
        private const int MaximumProductionGoalPriority = 100;
        private const int GlobalFabricatorDomain = int.MinValue;
        private const int GlobalFoundryDomain = int.MinValue + 1;
        private const int GlobalCloningDomain = int.MinValue + 2;
        // One in-game hour is Core's shortest named interval in the proposed
        // 60-300 tick fallback range. Signals remain next-tick responsive;
        // this pass only catches state transitions with no dependable signal.
        private const long FallbackReconciliationTicks = Consts.TicksPerHour;
        // Purpose: Emits compact phase/origin summaries once per in-game hour
        // so short performance captures include actionable phase timings.
        private const long PlanningDiagnosticsReportTicks = Consts.TicksPerHour;
        // Failed goal routes are deliberately retried in small batches. This
        // bounds recursive dependency discovery without allowing a cached
        // negative result to suppress work indefinitely.
        private const int MaximumGoalRouteProbesPerPass = 8;
        private static Harmony harmony;
        internal static AutoFactorySys Instance { get; private set; }
        private static readonly HashSet<string> ManagedFactoryIds = new(
            StringComparer.Ordinal)
        {
            "Objects/Devices/NanoFabricator",
            "Objects/Devices/NanoFoundry",
            "Objects/Devices/NanoCloningPod"
        };
        private readonly List<MultiCrafterComp> managedFactories = new();
        private readonly List<MultiCrafterComp> automatedFactories = new();
        private readonly HashSet<MatDeficitEventHolder> activeDeficits = new();
        private readonly Dictionary<MultiCrafterComp, CraftingOrder> automatedOrders = new();
        private readonly Dictionary<MatType, int> pendingDeliveries = new();
        private readonly Dictionary<CraftingOrder, Def> fallbackOrderRoots = new();
        private readonly Dictionary<Def, int> pendingObjectDeliveries = new();
        private readonly Dictionary<Def, int> pendingBeingDeliveries = new();
        private readonly HashSet<CraftingOrder> productionGoalOrders = new();
        private readonly HashSet<CraftingOrder> manualOrders = new();
        private readonly Dictionary<CraftingOrder, CrossFacilityDependency>
            crossFacilityOrders = new();
        private readonly Dictionary<MatType, int> lastInventoryCounts = new();
        private readonly Dictionary<Def, int> lastObjectInventoryCounts = new();
        private readonly Dictionary<Def, int> lastBeingCounts = new();
        private readonly HashSet<Def> nonDeliveryBeingCountChanges = new();
        private readonly Dictionary<string, int> productionTargets =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> productionPriorities =
            new(StringComparer.Ordinal);
        private readonly HashSet<int> roomScopedFactoryIds = new();
        private readonly HashSet<int> defaultLabelInitializedFactoryIds = new();
        private readonly Dictionary<int, bool> roomEnclosureStates = new();
        private readonly Dictionary<int, FactoryOrderTrackingState>
            pendingFactoryOrderTracking = new();
        private readonly Dictionary<(int RoomId, MatType Material), int>
            roomPendingDeliveries = new();
        private readonly Dictionary<(int RoomId, MatType Material), int>
            lastRoomInventoryCounts = new();
        private readonly HashSet<MatType> planningRelevantMaterials = new();
        private readonly HashSet<Def> planningRelevantObjectProducts = new();
        private readonly HashSet<Def> mindHolderObjectProducts = new();
        private readonly HashSet<Def> planningRelevantBeingProducts = new();
        private readonly List<Def> cachedEligibleTargetProducts = new();
        private readonly HashSet<MatType> cachedEligibleTargetMaterials = new();
        // Derived planning caches stay session-local. Save data remains the
        // authority for settings and order ownership, while these collections
        // can always be rebuilt by the independent fallback reconciliation.
        private readonly Dictionary<int, List<MultiCrafterComp>>
            cachedDomains = new();
        private readonly Dictionary<int, Dictionary<MatType, int>>
            cachedWantedByDomain = new();
        private readonly HashSet<MatType> dirtyDemandMaterials = new();
        private readonly Dictionary<Def, int> cachedGlobalGoalProducts = new();
        private readonly List<Def> cachedOrderedGoalProducts = new();
        // Maps every recipe-chain input back to the goal roots that depend on
        // it. Inventory signals can therefore invalidate only affected
        // negative route results instead of reopening every unmet goal.
        private readonly Dictionary<Def, HashSet<Def>>
            goalRootsByDependencyProduct = new();
        private readonly Dictionary<Def, GoalRouteFailure>
            unavailableGoalRoutes = new();
        private readonly HashSet<Def> pendingGoalRouteProbes = new();
        // Product and facility worksets collect every invalidation that arrives
        // before the next safe clock tick. They narrow cache maintenance without
        // changing Core's final crafting-order authority.
        private readonly HashSet<Def> dirtyPlanningProducts = new();
        private readonly HashSet<MultiCrafterComp> dirtyPlanningFactories = new();
        private readonly Dictionary<PlanningDirtyOrigin, long>
            planningDirtyOriginCounts = new();
        private readonly DiagnosticTiming onTickTiming = new("OnTick");
        private readonly DiagnosticTiming maintenanceTiming = new("Maintenance");
        private readonly DiagnosticTiming beingCountTiming = new("BeingCounts");
        private readonly DiagnosticTiming roomAccountingTickTiming =
            new("RoomAccountingTick");
        private readonly DiagnosticTiming rebalanceTiming = new("Rebalance");
        private readonly DiagnosticTiming priorityTiming = new("Priority");
        private readonly DiagnosticTiming materialSignalTiming =
            new("MatSignal");
        private readonly DiagnosticTiming materialRoomAccountingTiming =
            new("MatRoomAccounting");
        private readonly DiagnosticTiming objectSignalTiming =
            new("ObjSignal");
        private readonly DiagnosticTiming plannerTrackingTiming =
            new("PlannerTracking");
        private readonly DiagnosticTiming plannerFactoryOrdersTiming =
            new("PlannerFactoryOrders");
        private readonly DiagnosticTiming plannerDisabledTiming =
            new("PlannerDisabled");
        private readonly DiagnosticTiming plannerDemandTopologyTiming =
            new("PlannerDemandTopology");
        private readonly DiagnosticTiming plannerDomainCacheTiming =
            new("PlannerDomains");
        private readonly DiagnosticTiming plannerDemandCacheTiming =
            new("PlannerDemandCache");
        private readonly DiagnosticTiming plannerDemandCacheFullTiming =
            new("PlannerDemandFull");
        private readonly DiagnosticTiming plannerDemandCachePartialTiming =
            new("PlannerDemandPartial");
        private readonly DiagnosticTiming plannerGoalCacheTiming =
            new("PlannerGoalCache");
        private readonly DiagnosticTiming plannerValidationTiming =
            new("PlannerValidation");
        private readonly DiagnosticTiming plannerSurplusTiming =
            new("PlannerSurplus");
        private readonly DiagnosticTiming plannerShortageSchedulingTiming =
            new("PlannerShortages");
        private readonly DiagnosticTiming plannerShortageDomainsTiming =
            new("PlannerShortageDomains");
        private readonly DiagnosticTiming plannerShortageCrossFacilityTiming =
            new("PlannerShortageCross");
        private readonly DiagnosticTiming plannerGoalSchedulingTiming =
            new("PlannerGoals");
        private AutoFactoryUi ui;
        private ProductionGoalTableStore productionGoalTables;
        private string loadedProductionGoalTableName;
        private bool enabled = true;
        private bool isRebalancing;
        // Wake-up latch only; product/facility worksets and cache flags define
        // the actual reconciliation scope.
        private bool planningPassPending = true;
        private bool factoryMaintenanceDirty = true;
        private bool domainTopologyDirty = true;
        private bool demandTopologyDirty = true;
        private bool goalCacheDirty = true;
        private bool goalOrderDirty = true;
        private bool inventoryUiRefreshPending;
        private PlanningDirtyOrigin pendingPlanningDirtyOrigins =
            PlanningDirtyOrigin.Initialization;
        private long lastFullReconciliationTick = long.MinValue;
        private long lastPlanningDiagnosticsTick = long.MinValue;
        private long planningPasses;
        private long dirtyPlanningPasses;
        private long fallbackPlanningPasses;
        private long partialPlanningPasses;
        private long domainCacheRebuilds;
        private long demandCacheRebuilds;
        private long demandCachePartialRefreshes;
        private long goalCacheFullRebuilds;
        private long goalCachePartialRefreshes;
        private long goalRouteProbes;
        private long goalRouteSuccesses;
        private long goalRouteFailures;
        private long goalRouteCacheSkips;
        private long goalRouteInvalidations;
        private long goalRouteContinuationPasses;
        private int maximumPendingGoalRouteProbes;
        private long inventoryUiRefreshes;
        private int maximumDirtyProducts;
        private int maximumDirtyDemandMaterials;
        private int maximumDirtyFactories;
        private long failedPlanningPasses;
        private long ignoredNestedDirtySignals;
        private long materialSignals;
        private long materialDirtySignals;
        private long materialChangedValues;
        private int maximumMaterialChangesPerSignal;
        private long objectSignals;
        private long objectDirtySignals;
        private long objectChangedValues;
        private int maximumObjectChangesPerSignal;
        private int factoryPriority = DefaultFactoryPriority;
        private int nextNanoFabricatorLabel = 1;
        private int nextNanoFoundryLabel = 1;
        private int nextNanoCloningPodLabel = 1;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        // Purpose: Registers Auto Factory's game system with the Core system registry.
        [SelfInit(SelfInit.Stage.CoreDefs, 100)]
        private static void Register()
        {
            GameSystems.Register(SysId, () => new AutoFactorySys());
        }

        // Purpose: Initializes patches, recipes, signals, framework integration, and session state.
        protected override void OnInitialize()
        {
            try
            {
                Instance = this;
                AutoFactoryRecipeRegistry.ResetRuntimeTraceState();
                if (harmony == null)
                {
                    harmony = new Harmony(HarmonyId);
                    harmony.PatchAll(typeof(AutoFactorySys).Assembly);
                }
                AutoFactoryRecipeRegistry.RegisterRecipes();
                CacheEligibleTargetProducts();
                CachePlanningRelevantInventory();
                productionGoalTables = new ProductionGoalTableStore();
                RecordPlanningDirtyOrigin(PlanningDirtyOrigin.Initialization);
                S.Sig.RegisterCrafter.AddListener(OnRegisterCrafter);
                S.Sig.TileRelocated.AddListener(OnTileRelocated);
                S.Sig.MatDeficitChanged.AddListener(OnMatDeficitChanged);
                S.Sig.CrafterProducedItem.AddListener(OnCrafterProducedItem);
                S.Sig.MatCountChanged.AddListener(OnMatCountChanged);
                S.Sig.ObjCountChanged.AddListener(OnObjCountChanged);
                S.Sig.FactionRelationChanged.AddListener(
                    OnFactionRelationChanged);
                S.Sig.BeingAdded.AddListener(OnBeingAdded);
                S.Sig.BeingDied.AddListener(OnBeingDied);
                S.Sig.MindInstalled.AddListener(OnMindInstalled);
                S.Sig.MindRemoved.AddListener(OnMindRemoved);
                S.Sig.EntityRemoved.AddListener(OnEntityRemoved);
                S.Sig.ResearchChanged.AddListener(OnResearchChanged);
                S.Clock.OnTick.AddListener(OnTick);

                var consumerVersion = typeof(AutoFactorySys).Assembly
                    .GetName().Version.ToString(3);
                if (AutoUtilitiesApi.TryRegisterConsumer(
                    ConsumerId,
                    "Auto Factory",
                    consumerVersion,
                    RequiredUtilitiesVersion))
                {
                    ui = new AutoFactoryUi(this);
                }
            }
            catch (Exception exception)
            {
                D.LogEx(exception, "AutoFactory runtime initialization failed");
            }
        }

        // Purpose: Detaches signals, UI, patches, and cached state when the system unloads.
        public override void Unload()
        {
            S?.Sig?.RegisterCrafter.RemoveListener(OnRegisterCrafter);
            S?.Sig?.TileRelocated.RemoveListener(OnTileRelocated);
            S?.Sig?.MatDeficitChanged.RemoveListener(OnMatDeficitChanged);
            S?.Sig?.CrafterProducedItem.RemoveListener(OnCrafterProducedItem);
            S?.Sig?.MatCountChanged.RemoveListener(OnMatCountChanged);
            S?.Sig?.ObjCountChanged.RemoveListener(OnObjCountChanged);
            S?.Sig?.FactionRelationChanged.RemoveListener(
                OnFactionRelationChanged);
            S?.Sig?.BeingAdded.RemoveListener(OnBeingAdded);
            S?.Sig?.BeingDied.RemoveListener(OnBeingDied);
            S?.Sig?.MindInstalled.RemoveListener(OnMindInstalled);
            S?.Sig?.MindRemoved.RemoveListener(OnMindRemoved);
            S?.Sig?.EntityRemoved.RemoveListener(OnEntityRemoved);
            S?.Sig?.ResearchChanged.RemoveListener(OnResearchChanged);
            S?.Clock?.OnTick.RemoveListener(OnTick);
            ui?.Dispose();
            ui = null;
            managedFactories.Clear();
            automatedFactories.Clear();
            activeDeficits.Clear();
            automatedOrders.Clear();
            pendingDeliveries.Clear();
            pendingObjectDeliveries.Clear();
            pendingBeingDeliveries.Clear();
            fallbackOrderRoots.Clear();
            productionGoalOrders.Clear();
            manualOrders.Clear();
            crossFacilityOrders.Clear();
            lastInventoryCounts.Clear();
            lastObjectInventoryCounts.Clear();
            lastBeingCounts.Clear();
            nonDeliveryBeingCountChanges.Clear();
            productionTargets.Clear();
            productionPriorities.Clear();
            productionGoalTables = null;
            loadedProductionGoalTableName = null;
            roomScopedFactoryIds.Clear();
            defaultLabelInitializedFactoryIds.Clear();
            roomEnclosureStates.Clear();
            pendingFactoryOrderTracking.Clear();
            roomPendingDeliveries.Clear();
            lastRoomInventoryCounts.Clear();
            planningRelevantMaterials.Clear();
            planningRelevantObjectProducts.Clear();
            mindHolderObjectProducts.Clear();
            planningRelevantBeingProducts.Clear();
            cachedEligibleTargetProducts.Clear();
            cachedEligibleTargetMaterials.Clear();
            cachedDomains.Clear();
            cachedWantedByDomain.Clear();
            dirtyDemandMaterials.Clear();
            cachedGlobalGoalProducts.Clear();
            cachedOrderedGoalProducts.Clear();
            goalRootsByDependencyProduct.Clear();
            unavailableGoalRoutes.Clear();
            pendingGoalRouteProbes.Clear();
            dirtyPlanningProducts.Clear();
            dirtyPlanningFactories.Clear();
            isRebalancing = false;
            planningPassPending = true;
            factoryMaintenanceDirty = true;
            domainTopologyDirty = true;
            demandTopologyDirty = true;
            goalCacheDirty = true;
            goalOrderDirty = true;
            inventoryUiRefreshPending = false;
            pendingPlanningDirtyOrigins = PlanningDirtyOrigin.Initialization;
            lastFullReconciliationTick = long.MinValue;
            ResetPlanningDiagnostics();
            AutoFactoryRecipeRegistry.ResetRuntimeTraceState();
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
        }

        internal bool Enabled => enabled;
        internal GameState ShortcutGameState => S;
        internal int FactoryPriority => factoryPriority;
        internal int NanoFabricatorCount => automatedFactories.Count(crafter =>
            crafter?.Tile?.IsConstructed == true && IsNanoFabricator(crafter));
        internal int NanoFoundryCount => automatedFactories.Count(crafter =>
            crafter?.Tile?.IsConstructed == true && IsNanoFoundry(crafter));
        internal int NanoCloningPodCount => automatedFactories.Count(crafter =>
            crafter?.Tile?.IsConstructed == true && IsNanoCloningPod(crafter));
        internal int ActiveOrderCount => automatedOrders.Count;

        // Purpose: Returns the saved Global or Room automation scope for a managed factory.
        internal AutoFactoryScope GetFactoryScope(MultiCrafterComp crafter)
        {
            return crafter?.Entity != null
                && roomScopedFactoryIds.Contains(crafter.Entity.Id)
                ? AutoFactoryScope.Room
                : AutoFactoryScope.Global;
        }

        // Purpose: Resolves the current enclosed-room identifier for a factory tile.
        internal int GetFactoryRoomId(MultiCrafterComp crafter)
        {
            var tile = crafter?.Tile;
            var areas = crafter?.S?.Sys?.Areas;
            return tile == null
                || areas == null
                || !areas.AreaTypes.IsCreated
                || tile.PosIdx < 0
                || tile.PosIdx >= areas.AreaTypes.Length
                ? 0
                : areas.At(tile.PosIdx);
        }

        // Purpose: Changes a factory's scope and schedules next-tick reconciliation.
        internal void SetFactoryScope(MultiCrafterComp crafter, AutoFactoryScope scope)
        {
            if (SetFactoryScopes(new[] { crafter }, scope) > 0)
            {
                RefreshSelectedCrafterPanel(crafter);
            }
        }

        // Purpose: Applies one saved scope to multiple managed factories with one reconciliation pass.
        internal int SetFactoryScopes(IEnumerable<MultiCrafterComp> crafters,
            AutoFactoryScope scope)
        {
            if (crafters == null)
            {
                return 0;
            }

            var changedCount = 0;
            var visited = new HashSet<int>();
            foreach (var crafter in crafters)
            {
                if (!IsManagedFactory(crafter) || crafter.Entity == null
                    || !crafter.Entity.IsActive
                    || !visited.Add(crafter.Entity.Id))
                {
                    continue;
                }

                var changed = scope == AutoFactoryScope.Room
                    ? roomScopedFactoryIds.Add(crafter.Entity.Id)
                    : roomScopedFactoryIds.Remove(crafter.Entity.Id);
                if (!changed)
                {
                    continue;
                }

                if (scope == AutoFactoryScope.Room)
                {
                    roomEnclosureStates[crafter.Entity.Id] =
                        GetFactoryRoomId(crafter) > 0;
                }
                else
                {
                    roomEnclosureStates.Remove(crafter.Entity.Id);
                }

                LogActivity(crafter, scope == AutoFactoryScope.Room
                        ? "Automation scope changed to Room. Local lacking-material demand and its intermediary chain take priority, followed by unmet Global Production Goals. Ingredients may be hauled from anywhere on the ship."
                        : "Automation scope changed to Global. Ship-wide demand, inventory, and ingredients will be used.",
                    AutoFactoryLogCategory.Manual);
                TrackDirtyFactory(crafter);
                changedCount++;
            }

            if (changedCount == 0)
            {
                return 0;
            }

            RefreshRoomInventoryCounts();
            MarkProductionPlanningDirty(PlanningDirtyOrigin.ScopeChanged);
            ui?.RefreshState();
            return changedCount;
        }

        // Purpose: Returns the sorted products that can be configured as Production Goals.
        internal IReadOnlyList<Def> EligibleTargetProducts()
        {
            if (cachedEligibleTargetProducts.Count == 0)
            {
                CacheEligibleTargetProducts();
            }
            return cachedEligibleTargetProducts;
        }

        // Purpose: Caches the session-stable Production Goal product catalog.
        private void CacheEligibleTargetProducts()
        {
            cachedEligibleTargetProducts.Clear();
            cachedEligibleTargetProducts.AddRange(Craftable.All
                .Where(recipe => recipe?.Type == AutoFactoryRecipeRegistry.NanoFabricatorType
                    || recipe?.Type == AutoFactoryRecipeRegistry.NanoFoundryType
                    || recipe?.Type == AutoFactoryRecipeRegistry.NanoCloningType)
                .Where(AutoFactoryRecipeRegistry.IsUsableNanoRecipe)
                .Select(recipe => recipe.ProductDef)
                .Where(product => product != null)
                .Distinct()
                .OrderBy(TargetGroup)
                .ThenBy(product => product.NameT)
                .ToArray());
            cachedEligibleTargetMaterials.Clear();
            cachedEligibleTargetMaterials.UnionWith(cachedEligibleTargetProducts
                .Select(product => product.MatType)
                .Where(material => material != null));
        }

        // Purpose: Provides the stable inventory category used to group Production Goals.
        internal static string TargetGroup(Def product)
        {
            if (product?.MatType?.GroupId is string materialGroup)
            {
                return materialGroup;
            }
            if (product?.IsBeing == true)
            {
                return "Beings";
            }
            if (AutoFactoryRecipeRegistry
                .IsNanoFabricatorRoboticBodyPartProduct(product))
            {
                return "Robotic Body Parts";
            }
            return AutoFactoryRecipeRegistry.IsNanoCloningProduct(product)
                ? "Cloning" : "Weapons";
        }

        // Purpose: Provides the stable persisted identifier for a target product.
        internal static string TargetKey(Def product)
        {
            return product?.MatType?.Id ?? product?.Id;
        }

        // Purpose: Reads the configured Production Goal for an object or material definition.
        internal int GetProductionTarget(Def product)
        {
            var key = TargetKey(product);
            return key != null
                && productionTargets.TryGetValue(key, out var target)
                ? target
                : 0;
        }

        // Purpose: Resolves a material to its definition before reading its Production Goal.
        internal int GetProductionTarget(MatType material)
        {
            return GetProductionTarget(material?.Def);
        }

        // Purpose: Reads a Production Goal's scheduler priority, where zero is default.
        internal int GetProductionPriority(Def product)
        {
            var key = TargetKey(product);
            return key != null
                && productionPriorities.TryGetValue(key, out var priority)
                ? priority
                : 0;
        }

        // Purpose: Returns the latest ship-wide owned count for a target product.
        internal int GetInventoryCount(Def product)
        {
            if (product?.MatType is MatType material)
            {
                return lastInventoryCounts.TryGetValue(material, out var count)
                    ? count : 0;
            }
            if (product == null)
            {
                return 0;
            }
            if (product.IsBeing)
            {
                if (lastBeingCounts.TryGetValue(product, out var beingCount))
                {
                    return beingCount;
                }
                return CountOwnedBeingProduct(product);
            }
            if (lastObjectInventoryCounts.TryGetValue(product, out var objectCount))
            {
                return objectCount;
            }
            if (mindHolderObjectProducts.Contains(product))
            {
                return CountOwnedMindHolderProduct(product);
            }
            return S?.Sys?.Inventory == null
                ? 0 : S.Sys.Inventory.CountOfProduct(product);
        }

        // Purpose: Counts a Brain/Core product while excluding objects whose
        // attached Mind belongs to a faction hostile toward the player.
        private int CountOwnedMindHolderProduct(Def product)
        {
            if (product == null || !mindHolderObjectProducts.Contains(product)
                || S == null)
            {
                return 0;
            }

            var allObjects = S.Query.GetAllObjs.Ask();
            if (allObjects == null)
            {
                return 0;
            }
            var allBeings = S.Query.AllBeingData.Ask();
            var count = 0;
            foreach (var obj in allObjects.Values)
            {
                if (obj?.Def != product
                    || !IsCoreCountedObject(obj, allBeings)
                    || IsHostileMind(obj.Mind))
                {
                    continue;
                }
                count++;
            }
            return count;
        }

        // Purpose: Mirrors the object states included by Core's ObjsSys count.
        private static bool IsCoreCountedObject(ObjData obj,
            Dictionary<uint, BeingData> allBeings)
        {
            if (obj == null || obj.State == ObjState.Destroyed
                || obj.State == ObjState.InDevice)
            {
                return false;
            }
            if (obj.State != ObjState.Equipped
                || allBeings == null
                || !allBeings.TryGetValue(obj.BeingDataId, out var being))
            {
                return true;
            }
            return being?.Body != null
                && !being.Body.HasFlag(BodyFlags.Missing)
                && being.IsPlayer;
        }

        // Purpose: Uses Core diplomacy so neutral/allied/player/blank Minds
        // count, while born-hostile, anti-player, and enemy Minds do not.
        private bool IsHostileMind(Mind mind)
        {
            return mind?.Faction != null
                && S?.Sys?.Diplomacy?.IsHostileTowardsPlayer(mind.Faction)
                    == true;
        }

        // Purpose: Preserves Core active counts for biological beings while treating
        // robotic products as coreless chassis plus live player-faction robots.
        private int CountOwnedBeingProduct(Def product)
        {
            if (product?.IsBeing != true)
            {
                return 0;
            }

            var species = Species.ForDef(product.IdH);
            if (species?.IsBiological != false)
            {
                return S?.Beings == null ? 0 : S.Beings.CountByDef(product);
            }

            if (S == null)
            {
                return 0;
            }
            var all = S.Query.AllBeingData.Ask();
            if (all == null)
            {
                return 0;
            }

            var count = 0;
            foreach (var being in all.Values)
            {
                if (being?.Species?.Def != product
                    || !IsAvailableRoboticBeingRecord(being))
                {
                    continue;
                }

                var coreSlot = being.Body.MindSlot;
                var isCoreless = coreSlot == null || coreSlot.IsPartRemoved
                    || coreSlot.InstalledDef == null;
                if (isCoreless || IsLivePlayerBeing(being))
                {
                    count++;
                }
            }
            return count;
        }

        // Purpose: Excludes robotic bodies that are no longer available to ship inventory.
        private static bool IsAvailableRoboticBeingRecord(BeingData being)
        {
            return being?.Species?.IsBiological == false
                && being.Body != null
                && !being.Body.HasFlag(BodyFlags.Missing)
                && !being.HasStateFlag(BeingState.Expedition)
                && !being.HasStateFlag(BeingState.InColony);
        }

        // Purpose: Identifies active, living members of the player's faction.
        private static bool IsLivePlayerBeing(BeingData being)
        {
            return being?.Being?.IsActive == true
                && being.Being.IsAlive
                && being.IsPlayer;
        }

        // Purpose: Resolves a material to its definition before reading its owned count.
        internal int GetInventoryCount(MatType material)
        {
            return GetInventoryCount(material?.Def);
        }

        internal int GlobalNanobotsCount => lastInventoryCounts
            .Where(pair => pair.Key?.Id == "Nanobots")
            .Select(pair => pair.Value)
            .FirstOrDefault();

        internal int ConfiguredTargetCount => EligibleTargetProducts().Count(product =>
            GetProductionTarget(product) > 0);

        internal int UnmetTargetCount => EligibleTargetProducts().Count(product =>
            GetProductionTarget(product) > GetInventoryCount(product)
                + PendingForProduct(product));

        internal string LoadedProductionGoalTableName =>
            loadedProductionGoalTableName;

        internal IReadOnlyList<ProductionGoalTableSummary>
            ProductionGoalTableSummaries => productionGoalTables?.Summaries
                ?? Array.Empty<ProductionGoalTableSummary>();

        // Purpose: Enables or disables automation and schedules next-tick reconciliation.
        internal void SetEnabled(bool value)
        {
            if (enabled == value)
            {
                return;
            }
            enabled = value;
            MarkProductionPlanningDirty(PlanningDirtyOrigin.AutomationToggled);
            ui?.RefreshState();
        }

        // Purpose: Saves and applies the shared task priority to every managed factory.
        internal void SetFactoryPriority(int value)
        {
            value = Math.Max(1, Math.Min(MaximumFactoryPriority, value));
            if (factoryPriority == value)
            {
                return;
            }

            factoryPriority = value;
            ApplyFactoryPriorityToAll();
            foreach (var crafter in managedFactories.Where(crafter => crafter != null))
            {
                LogActivity(crafter,
                    "Global Auto Factory priority changed to "
                    + factoryPriority + "/" + MaximumFactoryPriority + ".");
            }
            ui?.RefreshState();
        }

        // Purpose: Sets one Production Goal and schedules next-tick reconciliation.
        internal void SetProductionTarget(Def product, int target)
        {
            var key = TargetKey(product);
            if (key == null)
            {
                return;
            }
            target = Math.Max(0, target);
            if (target == 0)
            {
                productionTargets.Remove(key);
            }
            else
            {
                productionTargets[key] = target;
            }
            TrackDirtyProduct(product);
            MarkProductionPlanningDirty(
                PlanningDirtyOrigin.ProductionTargetChanged);
            ui?.RefreshState();
        }

        // Purpose: Sets one Production Goal's 0-100 scheduler priority.
        internal void SetProductionPriority(Def product, int priority)
        {
            var key = TargetKey(product);
            if (key == null)
            {
                return;
            }
            priority = Math.Max(0,
                Math.Min(MaximumProductionGoalPriority, priority));
            if (priority == GetProductionPriority(product))
            {
                // Rebuild the field even when the normalized value is unchanged
                // so entries such as -1 or 101 visibly return to 0 or 100.
                ui?.RefreshState();
                return;
            }
            if (priority == 0)
            {
                productionPriorities.Remove(key);
            }
            else
            {
                productionPriorities[key] = priority;
            }
            TrackDirtyProduct(product);
            MarkProductionPlanningDirty(
                PlanningDirtyOrigin.ProductionPriorityChanged);
            ui?.RefreshState();
        }

        // Purpose: Disables all Production Goals and unloads the selected table
        // while leaving the globally saved table and factory automation intact.
        internal void ClearProductionTargets()
        {
            productionTargets.Clear();
            productionPriorities.Clear();
            loadedProductionGoalTableName = null;
            goalCacheDirty = true;
            goalOrderDirty = true;
            MarkProductionPlanningDirty(
                PlanningDirtyOrigin.ProductionTargetsCleared);
            ui?.RefreshState();
        }

        // Purpose: Creates and selects a globally reusable snapshot of all
        // nonzero Production Goal and Priority values.
        internal bool TryCreateProductionGoalTable(string name,
            out string message)
        {
            if (productionGoalTables == null)
            {
                message = "Production Goals Tables are not available yet.";
                return false;
            }
            if (!productionGoalTables.TryCreate(name,
                    CaptureProductionGoalTableValues(), out var savedName,
                    out message))
            {
                return false;
            }

            loadedProductionGoalTableName = savedName;
            message = "Created and loaded Production Goals Table \""
                + savedName + "\".";
            return true;
        }

        // Purpose: Replaces the selected global table with the current grid.
        internal bool TrySaveLoadedProductionGoalTable(out string message)
        {
            if (productionGoalTables == null
                || string.IsNullOrWhiteSpace(loadedProductionGoalTableName))
            {
                message = "No Production Goals Table is loaded. Enter a name "
                    + "and choose Create Production Goals Table.";
                return false;
            }
            if (!productionGoalTables.TryUpdate(loadedProductionGoalTableName,
                    CaptureProductionGoalTableValues(), out message))
            {
                if (!productionGoalTables.Contains(
                        loadedProductionGoalTableName))
                {
                    loadedProductionGoalTableName = null;
                }
                return false;
            }

            message = "Saved current goals and priorities to \""
                + loadedProductionGoalTableName + "\".";
            return true;
        }

        // Purpose: Replaces the current grid with one globally saved table.
        internal bool TryLoadProductionGoalTable(string name,
            out string message)
        {
            if (productionGoalTables == null
                || !productionGoalTables.TryGet(name, out var table))
            {
                message = "Production Goals Table \"" + name
                    + "\" is no longer available.";
                if (string.Equals(loadedProductionGoalTableName, name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    loadedProductionGoalTableName = null;
                }
                return false;
            }

            productionTargets.Clear();
            productionPriorities.Clear();
            var eligibleById = EligibleTargetProducts()
                .Where(product => TargetKey(product) != null)
                .ToDictionary(TargetKey, product => product,
                    StringComparer.Ordinal);
            foreach (var pair in table.Values)
            {
                if (!eligibleById.ContainsKey(pair.Key))
                {
                    continue;
                }
                if (pair.Value.Goal > 0)
                {
                    productionTargets[pair.Key] = pair.Value.Goal;
                }
                if (pair.Value.Priority > 0)
                {
                    productionPriorities[pair.Key] = pair.Value.Priority;
                }
            }
            foreach (var product in eligibleById.Values)
            {
                TrackDirtyProduct(product);
            }
            loadedProductionGoalTableName = table.Name;
            MarkProductionPlanningDirty(
                PlanningDirtyOrigin.ProductionGoalTableLoaded);
            message = "Loaded \"" + table.Name + "\" and replaced the "
                + "current goals and priorities.";
            return true;
        }

        // Purpose: Deletes one globally saved table while preserving the
        // current Goal/Priority grid and unloading a matching selection.
        internal bool TryDeleteProductionGoalTable(string name,
            out string message)
        {
            if (productionGoalTables == null)
            {
                message = "Production Goals Tables are not available yet.";
                return false;
            }
            if (!productionGoalTables.TryDelete(name,
                    out var deletedName, out message))
            {
                return false;
            }

            var wasLoaded = string.Equals(loadedProductionGoalTableName,
                deletedName, StringComparison.OrdinalIgnoreCase);
            if (wasLoaded)
            {
                loadedProductionGoalTableName = null;
            }
            message = "Deleted saved Production Goals Table \""
                + deletedName + "\". Current goals and priorities were "
                + "preserved" + (wasLoaded ? ", and the table was unloaded."
                    : ".");
            return true;
        }

        // Purpose: Captures one sparse, stable-keyed table from the current grid.
        private IReadOnlyDictionary<string, ProductionGoalTableValue>
            CaptureProductionGoalTableValues()
        {
            var values = new Dictionary<string, ProductionGoalTableValue>(
                StringComparer.Ordinal);
            foreach (var product in EligibleTargetProducts())
            {
                var key = TargetKey(product);
                var goal = GetProductionTarget(product);
                var priority = GetProductionPriority(product);
                if (key != null && (goal > 0 || priority > 0))
                {
                    values[key] = new ProductionGoalTableValue(goal, priority);
                }
            }
            return values;
        }

        // Purpose: Serializes settings, scopes, goals, counts, and in-flight order ownership.
        public ComponentData Save()
        {
            var data = ComponentData.Create(SysId);
            data.SetBool("Enabled", enabled);
            data.SetInt("FactoryPriority", factoryPriority);
            var activeFactoryIds = managedFactories
                .Where(crafter => crafter?.Entity != null)
                .Select(crafter => crafter.Entity.Id)
                .ToHashSet();
            data.SetStringSet("RoomScopedFactories", roomScopedFactoryIds
                .Where(activeFactoryIds.Contains)
                .Select(id => id.ToString()).ToArray());
            data.SetStringSet("DefaultLabelsInitialized",
                defaultLabelInitializedFactoryIds
                    .Where(activeFactoryIds.Contains)
                    .Select(id => id.ToString()).ToArray());
            data.SetInt("NextNanoFabricatorLabel", nextNanoFabricatorLabel);
            data.SetInt("NextNanoFoundryLabel", nextNanoFoundryLabel);
            data.SetInt("NextNanoCloningPodLabel", nextNanoCloningPodLabel);
            var eligibleIds = new HashSet<string>(EligibleTargetProducts()
                .Select(TargetKey), StringComparer.Ordinal);
            data.SetStringSet("ProductionTargets", productionTargets
                .Where(pair => pair.Value > 0 && eligibleIds.Contains(pair.Key))
                .Select(pair => pair.Key + "|" + pair.Value)
                .ToArray());
            data.SetStringSet("ProductionPriorities", productionPriorities
                .Where(pair => pair.Value > 0 && eligibleIds.Contains(pair.Key))
                .Select(pair => pair.Key + "|" + pair.Value)
                .ToArray());
            data.SetString("LoadedProductionGoalTable",
                loadedProductionGoalTableName);
            data.SetStringSet("OrderTracking", managedFactories
                .Where(crafter => crafter?.Entity?.IsActive == true)
                .Select(CaptureFactoryOrderTracking)
                .Where(state => state != null)
                .Select(state => state.Serialize())
                .ToArray());
            data.SetStringSet("PendingDeliveries", pendingDeliveries
                .Where(pair => pair.Key != null && pair.Value > 0)
                .Select(pair => pair.Key.Id + "|" + pair.Value)
                .ToArray());
            data.SetStringSet("PendingObjectDeliveries", pendingObjectDeliveries
                .Where(pair => pair.Key != null && pair.Value > 0)
                .Select(pair => pair.Key.Id + "|" + pair.Value)
                .ToArray());
            data.SetStringSet("PendingBeingDeliveries", pendingBeingDeliveries
                .Where(pair => pair.Key?.IsBeing == true && pair.Value > 0)
                .Select(pair => pair.Key.Id + "|" + pair.Value)
                .ToArray());
            data.SetStringSet("InventoryCounts", lastInventoryCounts
                .Where(pair => pair.Key != null && pair.Value >= 0)
                .Select(pair => pair.Key.Id + "|" + pair.Value)
                .ToArray());
            data.SetStringSet("ObjectInventoryCounts", lastObjectInventoryCounts
                .Where(pair => pair.Key != null && pair.Value >= 0)
                .Select(pair => pair.Key.Id + "|" + pair.Value)
                .ToArray());
            data.SetStringSet("BeingCounts", lastBeingCounts
                .Where(pair => pair.Key?.IsBeing == true && pair.Value >= 0)
                .Select(pair => pair.Key.Id + "|" + pair.Value)
                .ToArray());
            data.SetStringSet("RoomPendingDeliveries", roomPendingDeliveries
                .Where(pair => pair.Key.RoomId > 0
                    && pair.Key.Material != null && pair.Value > 0)
                .Select(pair => pair.Key.RoomId + "|"
                    + pair.Key.Material.Id + "|" + pair.Value)
                .ToArray());
            data.SetStringSet("RoomInventoryCounts", lastRoomInventoryCounts
                .Where(pair => pair.Key.RoomId > 0
                    && pair.Key.Material != null && pair.Value >= 0)
                .Select(pair => pair.Key.RoomId + "|"
                    + pair.Key.Material.Id + "|" + pair.Value)
                .ToArray());
            return data;
        }

        // Purpose: Restores persisted state and defers entity-bound order reconciliation.
        public void Load(ComponentData data)
        {
            if (data == null)
            {
                return;
            }
            enabled = !data.TryGetBool("Enabled", out var savedEnabled)
                || savedEnabled;
            factoryPriority = Math.Max(1, Math.Min(MaximumFactoryPriority,
                data.GetInt("FactoryPriority", DefaultFactoryPriority)));
            roomScopedFactoryIds.Clear();
            foreach (var savedId in data.GetStringSet(
                         "RoomScopedFactories", Array.Empty<string>()))
            {
                if (int.TryParse(savedId, out var id) && id > 0)
                {
                    roomScopedFactoryIds.Add(id);
                }
            }
            defaultLabelInitializedFactoryIds.Clear();
            foreach (var savedId in data.GetStringSet(
                         "DefaultLabelsInitialized", Array.Empty<string>()))
            {
                if (int.TryParse(savedId, out var id) && id > 0)
                {
                    defaultLabelInitializedFactoryIds.Add(id);
                }
            }
            nextNanoFabricatorLabel = Math.Max(1,
                data.GetInt("NextNanoFabricatorLabel", 1));
            nextNanoFoundryLabel = Math.Max(1,
                data.GetInt("NextNanoFoundryLabel", 1));
            nextNanoCloningPodLabel = Math.Max(1,
                data.GetInt("NextNanoCloningPodLabel", 1));
            productionTargets.Clear();
            foreach (var saved in data.GetStringSet(
                "ProductionTargets", Array.Empty<string>()))
            {
                var separator = saved.LastIndexOf('|');
                if (separator <= 0
                    || !int.TryParse(saved.Substring(separator + 1), out var target)
                    || target <= 0)
                {
                    continue;
                }
                productionTargets[saved.Substring(0, separator)] = target;
            }
            var eligibleIds = new HashSet<string>(EligibleTargetProducts()
                .Select(TargetKey), StringComparer.Ordinal);
            foreach (var productId in productionTargets.Keys
                         .Where(productId => !eligibleIds.Contains(productId))
                         .ToArray())
            {
                productionTargets.Remove(productId);
            }
            productionPriorities.Clear();
            foreach (var saved in data.GetStringSet(
                         "ProductionPriorities", Array.Empty<string>()))
            {
                var separator = saved.LastIndexOf('|');
                if (separator <= 0
                    || !int.TryParse(saved.Substring(separator + 1),
                        out var priority)
                    || priority <= 0)
                {
                    continue;
                }
                productionPriorities[saved.Substring(0, separator)] =
                    Math.Max(1,
                        Math.Min(MaximumProductionGoalPriority, priority));
            }
            foreach (var productId in productionPriorities.Keys
                         .Where(productId => !eligibleIds.Contains(productId))
                         .ToArray())
            {
                productionPriorities.Remove(productId);
            }
            loadedProductionGoalTableName = data.GetString(
                "LoadedProductionGoalTable", null);
            if (productionGoalTables?.Contains(
                    loadedProductionGoalTableName) != true)
            {
                loadedProductionGoalTableName = null;
            }
            pendingDeliveries.Clear();
            foreach (var saved in data.GetStringSet(
                         "PendingDeliveries", Array.Empty<string>()))
            {
                if (TryDeserializeMaterialCount(saved, positiveOnly: true,
                        out var material, out var count))
                {
                    pendingDeliveries[material] = count;
                }
            }
            pendingObjectDeliveries.Clear();
            foreach (var saved in data.GetStringSet(
                         "PendingObjectDeliveries", Array.Empty<string>()))
            {
                if (TryDeserializeProductCount(saved, positiveOnly: true,
                        out var product, out var count)
                    && product.MatType == null && !product.IsBeing)
                {
                    pendingObjectDeliveries[product] = count;
                }
            }
            pendingBeingDeliveries.Clear();
            foreach (var saved in data.GetStringSet(
                         "PendingBeingDeliveries", Array.Empty<string>()))
            {
                if (TryDeserializeProductCount(saved, positiveOnly: true,
                        out var product, out var count)
                    && product.IsBeing)
                {
                    pendingBeingDeliveries[product] = count;
                }
            }
            lastInventoryCounts.Clear();
            foreach (var saved in data.GetStringSet(
                         "InventoryCounts", Array.Empty<string>()))
            {
                if (TryDeserializeMaterialCount(saved, positiveOnly: false,
                        out var material, out var count))
                {
                    lastInventoryCounts[material] = count;
                }
            }
            lastObjectInventoryCounts.Clear();
            foreach (var saved in data.GetStringSet(
                         "ObjectInventoryCounts", Array.Empty<string>()))
            {
                if (TryDeserializeProductCount(saved, positiveOnly: false,
                        out var product, out var count)
                    && product.MatType == null && !product.IsBeing)
                {
                    lastObjectInventoryCounts[product] = count;
                }
            }
            lastBeingCounts.Clear();
            foreach (var saved in data.GetStringSet(
                         "BeingCounts", Array.Empty<string>()))
            {
                if (TryDeserializeProductCount(saved, positiveOnly: false,
                        out var product, out var count)
                    && product.IsBeing)
                {
                    lastBeingCounts[product] = count;
                }
            }
            roomPendingDeliveries.Clear();
            foreach (var saved in data.GetStringSet(
                         "RoomPendingDeliveries", Array.Empty<string>()))
            {
                if (TryDeserializeRoomMaterialCount(saved, positiveOnly: true,
                        out var key, out var count))
                {
                    roomPendingDeliveries[key] = count;
                }
            }
            lastRoomInventoryCounts.Clear();
            foreach (var saved in data.GetStringSet(
                         "RoomInventoryCounts", Array.Empty<string>()))
            {
                if (TryDeserializeRoomMaterialCount(saved, positiveOnly: false,
                        out var key, out var count))
                {
                    lastRoomInventoryCounts[key] = count;
                }
            }
            pendingFactoryOrderTracking.Clear();
            foreach (var saved in data.GetStringSet(
                         "OrderTracking", Array.Empty<string>()))
            {
                var state = FactoryOrderTrackingState.Deserialize(saved);
                if (state != null)
                {
                    pendingFactoryOrderTracking[state.FactoryId] = state;
                }
            }
            ApplyFactoryPriorityToAll();
            InvalidateAllPlannerCaches();
            MarkFactoryMaintenanceDirty(PlanningDirtyOrigin.SaveLoaded);
            // CraftingSys registers its per-crafter order responder and restores
            // saved orders only after system data has loaded. The ordinary clock
            // reconciliation restores Auto Factory ownership once both the native
            // order list and AreaSys room arrays are ready.
            ui?.RefreshState();
        }

        // Purpose: Parses a persisted material/count entry with the requested count rule.
        private static bool TryDeserializeMaterialCount(string saved,
            bool positiveOnly, out MatType material, out int count)
        {
            material = null;
            count = 0;
            var fields = saved?.Split('|');
            return fields?.Length == 2
                && MatType.Known.TryGetValue(fields[0], out material)
                && int.TryParse(fields[1], out count)
                && (positiveOnly ? count > 0 : count >= 0);
        }

        // Purpose: Parses a persisted room/material/count entry with the requested count rule.
        private static bool TryDeserializeRoomMaterialCount(string saved,
            bool positiveOnly, out (int RoomId, MatType Material) key,
            out int count)
        {
            key = default;
            count = 0;
            var fields = saved?.Split('|');
            if (fields?.Length != 3
                || !int.TryParse(fields[0], out var roomId)
                || roomId <= 0
                || !MatType.Known.TryGetValue(fields[1], out var material)
                || !int.TryParse(fields[2], out count)
                || (positiveOnly ? count <= 0 : count < 0))
            {
                return false;
            }
            key = (roomId, material);
            return true;
        }

        // Purpose: Parses a persisted object-product/count entry and resolves its definition.
        private bool TryDeserializeProductCount(string saved,
            bool positiveOnly, out Def product, out int count)
        {
            product = null;
            count = 0;
            var separator = saved?.LastIndexOf('|') ?? -1;
            if (separator <= 0
                || !int.TryParse(saved.Substring(separator + 1), out count)
                || (positiveOnly ? count <= 0 : count < 0))
            {
                return false;
            }
            product = FindProductByKey(saved.Substring(0, separator));
            return product != null;
        }

        // Purpose: Resolves a persisted target key to its current product definition.
        private Def FindProductByKey(string key)
        {
            return string.IsNullOrWhiteSpace(key) ? null
                : EligibleTargetProducts().FirstOrDefault(product =>
                    string.Equals(TargetKey(product), key,
                        StringComparison.Ordinal)
                    || string.Equals(product.Id, key,
                        StringComparison.Ordinal));
        }

        private sealed class FactoryOrderTrackingState
        {
            public int FactoryId;
            public string RecipeId;
            public bool Manual;
            public string FallbackRootId;
            public bool ProductionGoal;
            public int CrossRequesterId;
            public string CrossRootId;
            public string CrossMaterialId;
            public bool CrossProductionGoal;
            public bool IsRelocation;

            // Purpose: Serializes one factory's in-flight ownership and handoff metadata.
            public string Serialize()
            {
                return string.Join("|", FactoryId, RecipeId,
                    Manual ? "M" : "A", FallbackRootId ?? string.Empty,
                    ProductionGoal ? "1" : "0", CrossRequesterId,
                    CrossRootId ?? string.Empty,
                    CrossMaterialId ?? string.Empty,
                    CrossProductionGoal ? "1" : "0");
            }

            // Purpose: Reconstructs validated in-flight ownership metadata from save data.
            public static FactoryOrderTrackingState Deserialize(string saved)
            {
                var fields = saved?.Split('|');
                if (fields == null || fields.Length != 9
                    || !int.TryParse(fields[0], out var factoryId)
                    || factoryId <= 0
                    || string.IsNullOrWhiteSpace(fields[1])
                    || (fields[2] != "M" && fields[2] != "A")
                    || !int.TryParse(fields[5], out var requesterId))
                {
                    return null;
                }
                return new FactoryOrderTrackingState
                {
                    FactoryId = factoryId,
                    RecipeId = fields[1],
                    Manual = fields[2] == "M",
                    FallbackRootId = fields[3],
                    ProductionGoal = fields[4] == "1",
                    CrossRequesterId = requesterId,
                    CrossRootId = fields[6],
                    CrossMaterialId = fields[7],
                    CrossProductionGoal = fields[8] == "1"
                };
            }
        }

        // Purpose: Snapshots the ownership classification of a factory's active order.
        private FactoryOrderTrackingState CaptureFactoryOrderTracking(
            MultiCrafterComp crafter)
        {
            if (crafter?.Entity == null)
            {
                return null;
            }

            var order = manualOrders.FirstOrDefault(candidate =>
                candidate?.Crafter == crafter
                && crafter.Orders?.Contains(candidate) == true);
            var isManual = order != null;
            if (order == null)
            {
                automatedOrders.TryGetValue(crafter, out order);
            }
            var recipeId = order?.Demand?.Craftable?.Id;
            if (order == null || string.IsNullOrWhiteSpace(recipeId)
                || crafter.Orders?.Contains(order) != true)
            {
                return null;
            }

            fallbackOrderRoots.TryGetValue(order, out var fallbackRoot);
            crossFacilityOrders.TryGetValue(order, out var cross);
            return new FactoryOrderTrackingState
            {
                FactoryId = crafter.Entity.Id,
                RecipeId = recipeId,
                Manual = isManual,
                FallbackRootId = TargetKey(fallbackRoot),
                ProductionGoal = productionGoalOrders.Contains(order),
                CrossRequesterId = cross?.Requester?.Entity?.Id ?? 0,
                CrossRootId = TargetKey(cross?.Root),
                CrossMaterialId = cross?.Material?.Id,
                CrossProductionGoal = cross?.ProductionGoal == true
            };
        }

        // Purpose: Reattaches all deferred order metadata after native orders are available.
        private void RestorePendingFactoryOrderTracking()
        {
            foreach (var crafter in managedFactories.ToArray())
            {
                RestorePendingFactoryOrderTracking(crafter);
            }
        }

        // Purpose: Reattaches deferred order metadata for one reconstructed factory.
        private void RestorePendingFactoryOrderTracking(MultiCrafterComp crafter)
        {
            if (crafter?.Entity == null
                || !pendingFactoryOrderTracking.TryGetValue(
                    crafter.Entity.Id, out var state))
            {
                return;
            }
            var order = crafter.Orders?.FirstOrDefault(candidate =>
                candidate?.Demand?.Craftable?.Id == state.RecipeId);
            if (order == null)
            {
                return;
            }

            // Core restores the native order list separately from this
            // ownership metadata. An existing order means the facility is
            // configured even if a prior empty-order transition left the
            // native warning latched.
            crafter.UI?.MarkConfigured();

            automatedOrders.Remove(crafter);
            manualOrders.RemoveWhere(candidate => candidate?.Crafter == crafter);
            if (state.Manual)
            {
                manualOrders.Add(order);
            }
            else
            {
                automatedOrders[crafter] = order;
            }

            if (!string.IsNullOrWhiteSpace(state.FallbackRootId)
                && FindProductByKey(state.FallbackRootId) is Def fallbackRoot)
            {
                fallbackOrderRoots[order] = fallbackRoot;
            }
            if (state.ProductionGoal)
            {
                productionGoalOrders.Add(order);
            }
            if (state.CrossRequesterId > 0
                && S.Components.Find<MultiCrafterComp>(state.CrossRequesterId)
                    is MultiCrafterComp requester
                && FindProductByKey(state.CrossRootId) is Def crossRoot
                && MatType.Known.TryGetValue(
                    state.CrossMaterialId ?? string.Empty, out var crossMaterial))
            {
                crossFacilityOrders[order] = new CrossFacilityDependency
                {
                    Requester = requester,
                    Root = crossRoot,
                    Material = crossMaterial,
                    ProviderRecipeType = order.Demand?.Craftable?.Type,
                    ProductionGoal = state.CrossProductionGoal
                };
            }

            pendingFactoryOrderTracking.Remove(crafter.Entity.Id);
            if (state.IsRelocation)
            {
                LogActivity(crafter,
                    "Preserved automation scope and in-flight order state after relocation.",
                    AutoFactoryLogCategory.Information);
            }
        }

        // Purpose: Transfers scope, labels, tracing, and order ownership across relocation.
        private void OnTileRelocated(int sourceId, int targetId)
        {
            var source = managedFactories.FirstOrDefault(crafter =>
                crafter?.Entity?.Id == sourceId);
            var target = S.Components.Find<MultiCrafterComp>(targetId);
            if (!IsManagedFactory(source) || !IsManagedFactory(target))
            {
                return;
            }

            AutoFactoryRecipeRegistry.TransferRuntimeTraceState(sourceId,
                targetId);

            if (roomScopedFactoryIds.Remove(sourceId))
            {
                roomScopedFactoryIds.Add(targetId);
            }
            if (defaultLabelInitializedFactoryIds.Remove(sourceId))
            {
                defaultLabelInitializedFactoryIds.Add(targetId);
            }
            if (roomEnclosureStates.TryGetValue(sourceId, out var enclosed))
            {
                roomEnclosureStates.Remove(sourceId);
                roomEnclosureStates[targetId] = enclosed;
            }

            var state = CaptureFactoryOrderTracking(source);
            if (state != null)
            {
                state.FactoryId = targetId;
                state.IsRelocation = true;
                pendingFactoryOrderTracking[targetId] = state;
            }
            foreach (var dependency in crossFacilityOrders.Values
                         .Where(dependency => dependency?.Requester == source)
                         .Distinct())
            {
                dependency.Requester = target;
            }
            foreach (var pending in pendingFactoryOrderTracking.Values.Where(
                         pending => pending.CrossRequesterId == sourceId))
            {
                pending.CrossRequesterId = targetId;
            }
            TrackDirtyFactory(source);
            TrackDirtyFactory(target);
            MarkFactoryMaintenanceDirty(PlanningDirtyOrigin.FacilityRelocated);
        }

        // Purpose: Adds or removes managed factories as Core registers their components.
        private void OnRegisterCrafter(MultiCrafterComp crafter, bool added)
        {
            if (!IsManagedFactory(crafter))
            {
                return;
            }

            if (added)
            {
                if (!managedFactories.Contains(crafter))
                {
                    managedFactories.Add(crafter);
                }
                TrackDirtyFactory(crafter);
                ApplyFactoryPriority(crafter);
            }
            else
            {
                TrackDirtyFactory(crafter);
                managedFactories.Remove(crafter);
                CleanupRemovedFactoryTracking(crafter);
                MarkFactoryMaintenanceDirty(PlanningDirtyOrigin.FacilityRemoved);
                return;
            }

            if (!IsAutomatedFactory(crafter))
            {
                return;
            }

            if (added)
            {
                if (!automatedFactories.Contains(crafter))
                {
                    automatedFactories.Add(crafter);
                }
                RefreshCraftableCache(crafter);
                LogActivity(crafter,
                    "Automation monitoring started. Production and refill priorities set to "
                    + factoryPriority + "/" + MaximumFactoryPriority + ".");

                // Never inspect or schedule native orders from component
                // registration. During save reconstruction CraftingSys has not
                // registered GetCraftingOrdersFor or restored its saved orders.
                // The ordinary clock reconciliation performs pending ownership
                // restoration and pre-0.2 Custom-order cleanup after Core and
                // AreaSys have both finished loading.
                MarkFactoryMaintenanceDirty(
                    PlanningDirtyOrigin.FacilityRegistered);
            }
        }

        // Purpose: Purges every tracking record owned by a permanently removed factory.
        private void CleanupRemovedFactoryTracking(MultiCrafterComp crafter)
        {
            var id = crafter?.Entity?.Id ?? 0;
            automatedOrders.Remove(crafter);
            automatedFactories.Remove(crafter);
            foreach (var order in manualOrders.Where(order =>
                         order?.Crafter == crafter).ToArray())
            {
                manualOrders.Remove(order);
            }
            foreach (var order in fallbackOrderRoots.Keys.Where(order =>
                         order?.Crafter == crafter).ToArray())
            {
                fallbackOrderRoots.Remove(order);
            }
            productionGoalOrders.RemoveWhere(order => order?.Crafter == crafter);
            foreach (var order in crossFacilityOrders.Keys.Where(order =>
                         order?.Crafter == crafter
                         || crossFacilityOrders[order].Requester == crafter)
                         .ToArray())
            {
                crossFacilityOrders.Remove(order);
            }
            if (crafter?.Entity != null)
            {
                roomScopedFactoryIds.Remove(id);
                defaultLabelInitializedFactoryIds.Remove(id);
                roomEnclosureStates.Remove(id);
                pendingFactoryOrderTracking.Remove(id);
                AutoFactoryRecipeRegistry.ForgetRuntimeTraceState(id);
            }
            TrackDirtyFactory(crafter);
        }

        // Purpose: Coalesces dirty work into partial passes while preserving an independent full pass.
        private void OnTick(long ticks)
        {
            var tickStarted = Stopwatch.GetTimestamp();
            try
            {
                if (S?.IsLoaded != true)
                {
                    return;
                }

                FlushPendingInventoryUi();
                var fallbackDue = lastFullReconciliationTick == long.MinValue
                    || ticks < lastFullReconciliationTick
                    || ticks - lastFullReconciliationTick
                        >= FallbackReconciliationTicks;
                var dirtyDue = planningPassPending;
                var runPlanning = dirtyDue || fallbackDue;
                var runMaintenance = factoryMaintenanceDirty
                    || fallbackDue;
                if (!runMaintenance && !runPlanning)
                {
                    return;
                }

                if (runMaintenance)
                {
                    var phaseStarted = Stopwatch.GetTimestamp();
                    factoryMaintenanceDirty = false;
                    EnsureManagedFactoriesConfigured();
                    EnsureDefaultFactoryLabels();
                    maintenanceTiming.Record(phaseStarted);
                }
                if (runPlanning)
                {
                    planningPasses++;
                    if (dirtyDue)
                    {
                        dirtyPlanningPasses++;
                    }
                    if (fallbackDue)
                    {
                        fallbackPlanningPasses++;
                        InvalidateAllPlannerCaches(
                            preserveGoalRouteFailures: true);
                        RecordPlanningDirtyOrigin(
                            PlanningDirtyOrigin.Fallback);
                    }
                    else
                    {
                        partialPlanningPasses++;
                    }
                    planningPassPending = false;
                    pendingPlanningDirtyOrigins = PlanningDirtyOrigin.None;
                    try
                    {
                        var phaseStarted = Stopwatch.GetTimestamp();
                        RefreshBeingCounts();
                        beingCountTiming.Record(phaseStarted);

                        phaseStarted = Stopwatch.GetTimestamp();
                        RefreshRoomPendingDeliveryAccounting();
                        roomAccountingTickTiming.Record(phaseStarted);

                        phaseStarted = Stopwatch.GetTimestamp();
                        RebalanceOrders(fallbackDue);
                        rebalanceTiming.Record(phaseStarted);
                        if (fallbackDue)
                        {
                            lastFullReconciliationTick = ticks;
                        }
                    }
                    catch
                    {
                        // Preserve recovery behavior if a transient Core lifecycle
                        // transition interrupts a pass before it completes.
                        failedPlanningPasses++;
                        InvalidateAllPlannerCaches();
                        MarkProductionPlanningDirty(
                            PlanningDirtyOrigin.ExceptionRetry);
                        throw;
                    }
                }
                if (factoryPriority == MaximumFactoryPriority
                    && (runMaintenance || runPlanning))
                {
                    var phaseStarted = Stopwatch.GetTimestamp();
                    ApplyFactoryPriorityForPlanning(fallbackDue);
                    priorityTiming.Record(phaseStarted);
                }
                dirtyPlanningFactories.Clear();
            }
            finally
            {
                FlushPendingInventoryUi();
                onTickTiming.Record(tickStarted);
                MaybeReportPlanningDiagnostics(ticks);
            }
        }

        // Purpose: Defers a full planning pass unless reconciliation is already in progress.
        internal void MarkProductionPlanningDirty(PlanningDirtyOrigin origin)
        {
            RecordPlanningDirtyOrigin(origin);
            if ((origin & (PlanningDirtyOrigin.ScopeChanged
                    | PlanningDirtyOrigin.FacilityRelocated
                    | PlanningDirtyOrigin.FacilityRegistered
                    | PlanningDirtyOrigin.FacilityRemoved)) != 0)
            {
                domainTopologyDirty = true;
                demandTopologyDirty = true;
                InvalidateAllGoalRouteFailures();
            }
            if ((origin & (PlanningDirtyOrigin.ProductionTargetsCleared
                    | PlanningDirtyOrigin.ProductionGoalTableLoaded
                    | PlanningDirtyOrigin.SaveLoaded
                    | PlanningDirtyOrigin.Initialization
                    | PlanningDirtyOrigin.ExceptionRetry
                    | PlanningDirtyOrigin.ResearchChanged)) != 0)
            {
                InvalidateAllPlannerCaches();
            }
            if ((origin & PlanningDirtyOrigin.ProductionPriorityChanged) != 0)
            {
                goalOrderDirty = true;
            }
            if ((origin & PlanningDirtyOrigin.AutomationToggled) != 0)
            {
                InvalidateAllGoalRouteFailures();
            }
            // StopProducing is also called by RebalanceOrdersCore. Ignore only
            // that self-generated removal signal; inventory or lifecycle work
            // arriving during a pass remains queued for the following tick.
            if (isRebalancing && origin == PlanningDirtyOrigin.OrderStopped)
            {
                ignoredNestedDirtySignals++;
                return;
            }
            pendingPlanningDirtyOrigins |= origin;
            planningPassPending = true;
        }

        // Purpose: Adds one product to the next partial goal-cache refresh.
        private void TrackDirtyProduct(Def product,
            bool routeAvailabilityMayIncrease = true)
        {
            if (product != null)
            {
                dirtyPlanningProducts.Add(product);
                if (routeAvailabilityMayIncrease)
                {
                    InvalidateGoalRouteFailures(product);
                }
                maximumDirtyProducts = Math.Max(maximumDirtyProducts,
                    dirtyPlanningProducts.Count);
            }
        }

        // Purpose: Adds one material to the next incremental demand-cache refresh.
        private void TrackDirtyDemandMaterial(MatType material)
        {
            if (material == null)
            {
                return;
            }
            dirtyDemandMaterials.Add(material);
            maximumDirtyDemandMaterials = Math.Max(
                maximumDirtyDemandMaterials, dirtyDemandMaterials.Count);
        }

        // Purpose: Adds one facility to event-scoped maintenance work.
        private void TrackDirtyFactory(MultiCrafterComp crafter)
        {
            if (crafter != null)
            {
                dirtyPlanningFactories.Add(crafter);
                maximumDirtyFactories = Math.Max(maximumDirtyFactories,
                    dirtyPlanningFactories.Count);
            }
        }

        // Purpose: Invalidates every derived planner cache for a bounded safety pass.
        private void InvalidateAllPlannerCaches(
            bool preserveGoalRouteFailures = false)
        {
            domainTopologyDirty = true;
            demandTopologyDirty = true;
            goalCacheDirty = true;
            goalOrderDirty = true;
            dirtyDemandMaterials.Clear();
            if (!preserveGoalRouteFailures)
            {
                InvalidateAllGoalRouteFailures();
            }
        }

        // Purpose: Drops negative route results affected by one changed product.
        private void InvalidateGoalRouteFailures(Def product)
        {
            if (product == null)
            {
                return;
            }
            var affectedRoots = new HashSet<Def> { product };
            if (goalRootsByDependencyProduct.TryGetValue(product,
                    out var dependentRoots))
            {
                affectedRoots.UnionWith(dependentRoots);
            }
            foreach (var root in affectedRoots)
            {
                if (unavailableGoalRoutes.Remove(root))
                {
                    goalRouteInvalidations++;
                }
                pendingGoalRouteProbes.Remove(root);
            }
        }

        // Purpose: Clears session-local route failures after structural or lifecycle changes.
        private void InvalidateAllGoalRouteFailures()
        {
            goalRouteInvalidations += unavailableGoalRoutes.Count;
            unavailableGoalRoutes.Clear();
            pendingGoalRouteProbes.Clear();
        }

        // Purpose: Queues cached failures for bounded safety rechecks after a full pass.
        private void QueueAllGoalRouteProbes()
        {
            pendingGoalRouteProbes.UnionWith(unavailableGoalRoutes.Keys.Where(
                cachedGlobalGoalProducts.ContainsKey));
            maximumPendingGoalRouteProbes = Math.Max(
                maximumPendingGoalRouteProbes,
                pendingGoalRouteProbes.Count);
        }

        // Purpose: Schedules label/priority maintenance and production reconciliation together.
        private void MarkFactoryMaintenanceDirty(PlanningDirtyOrigin origin)
        {
            factoryMaintenanceDirty = true;
            MarkProductionPlanningDirty(origin);
        }

        // Purpose: Counts every dirty source, including signals ignored during reconciliation.
        private void RecordPlanningDirtyOrigin(PlanningDirtyOrigin origin)
        {
            if (origin == PlanningDirtyOrigin.None)
            {
                return;
            }
            planningDirtyOriginCounts.TryGetValue(origin, out var count);
            planningDirtyOriginCounts[origin] = count + 1;
        }

        // Purpose: Writes one compact hourly diagnostic summary instead of per-tick log spam.
        private void MaybeReportPlanningDiagnostics(long ticks)
        {
            if (lastPlanningDiagnosticsTick == long.MinValue
                || ticks < lastPlanningDiagnosticsTick)
            {
                lastPlanningDiagnosticsTick = ticks;
                return;
            }
            if (ticks - lastPlanningDiagnosticsTick
                < PlanningDiagnosticsReportTicks)
            {
                return;
            }

            var dirtyOrigins = planningDirtyOriginCounts.Count == 0
                ? "none"
                : string.Join(",", planningDirtyOriginCounts
                    .OrderBy(pair => pair.Key)
                    .Select(pair => pair.Key + "=" + pair.Value));
            // KL.Utils.D.Log is editor-only and is removed from release builds.
            // Unity's runtime logger keeps this aggregated diagnostic available
            // in Player.log without changing planner behavior.
            UnityEngine.Debug.Log(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "AutoFactory planner diagnostics: ticks={0}; passes={1} dirty={2} fallback={3} failed={4}; ignoredNestedDirty={5}; pending={6}; origins=[{7}]; signals mat={8}/{9} changedValues={10} maxChanged={11}, obj={12}/{13} changedValues={14} maxChanged={15}; state factories={16} activeOrders={17} deficits={18} goals={19} relevant={20}m/{21}o/{22}b",
                ticks - lastPlanningDiagnosticsTick,
                planningPasses,
                dirtyPlanningPasses,
                fallbackPlanningPasses,
                failedPlanningPasses,
                ignoredNestedDirtySignals,
                pendingPlanningDirtyOrigins,
                dirtyOrigins,
                materialSignals,
                materialDirtySignals,
                materialChangedValues,
                maximumMaterialChangesPerSignal,
                objectSignals,
                objectDirtySignals,
                objectChangedValues,
                maximumObjectChangesPerSignal,
                automatedFactories.Count,
                automatedOrders.Count,
                activeDeficits.Count,
                productionTargets.Count,
                planningRelevantMaterials.Count,
                planningRelevantObjectProducts.Count,
                planningRelevantBeingProducts.Count));
            UnityEngine.Debug.Log(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "AutoFactory planner cache diagnostics: partial={0} full={1}; rebuilds domains={2} demandFull={3} demandPartial={4} goalsFull={5} goalsPartial={6}; worksets maxProducts={7} maxDemandMaterials={8} maxFactories={9} pendingProducts={10} pendingDemandMaterials={11} pendingFactories={12}; goalRoutes cached={13} pending={14} maxPending={15} probes={16} successes={17} failures={18} skips={19} invalidations={20} continuations={21}; uiInventoryRefreshes={22}; dirtyFlags domains={23} demandFull={24} goals={25} order={26}",
                partialPlanningPasses,
                fallbackPlanningPasses,
                domainCacheRebuilds,
                demandCacheRebuilds,
                demandCachePartialRefreshes,
                goalCacheFullRebuilds,
                goalCachePartialRefreshes,
                maximumDirtyProducts,
                maximumDirtyDemandMaterials,
                maximumDirtyFactories,
                dirtyPlanningProducts.Count,
                dirtyDemandMaterials.Count,
                dirtyPlanningFactories.Count,
                unavailableGoalRoutes.Count,
                pendingGoalRouteProbes.Count,
                maximumPendingGoalRouteProbes,
                goalRouteProbes,
                goalRouteSuccesses,
                goalRouteFailures,
                goalRouteCacheSkips,
                goalRouteInvalidations,
                goalRouteContinuationPasses,
                inventoryUiRefreshes,
                domainTopologyDirty,
                demandTopologyDirty,
                goalCacheDirty,
                goalOrderDirty));
            UnityEngine.Debug.Log(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "AutoFactory planner phase diagnostics: {0}",
                string.Join("; ", new[]
                {
                    onTickTiming.SnapshotAndReset(),
                    maintenanceTiming.SnapshotAndReset(),
                    beingCountTiming.SnapshotAndReset(),
                    roomAccountingTickTiming.SnapshotAndReset(),
                    rebalanceTiming.SnapshotAndReset(),
                    priorityTiming.SnapshotAndReset(),
                    materialSignalTiming.SnapshotAndReset(),
                    materialRoomAccountingTiming.SnapshotAndReset(),
                    objectSignalTiming.SnapshotAndReset(),
                    plannerTrackingTiming.SnapshotAndReset(),
                    plannerFactoryOrdersTiming.SnapshotAndReset(),
                    plannerDisabledTiming.SnapshotAndReset(),
                    plannerDemandTopologyTiming.SnapshotAndReset(),
                    plannerDomainCacheTiming.SnapshotAndReset(),
                    plannerDemandCacheTiming.SnapshotAndReset(),
                    plannerDemandCacheFullTiming.SnapshotAndReset(),
                    plannerDemandCachePartialTiming.SnapshotAndReset(),
                    plannerGoalCacheTiming.SnapshotAndReset(),
                    plannerValidationTiming.SnapshotAndReset(),
                    plannerSurplusTiming.SnapshotAndReset(),
                    plannerShortageSchedulingTiming.SnapshotAndReset(),
                    plannerShortageDomainsTiming.SnapshotAndReset(),
                    plannerShortageCrossFacilityTiming.SnapshotAndReset(),
                    plannerGoalSchedulingTiming.SnapshotAndReset()
                })));

            planningDirtyOriginCounts.Clear();
            planningPasses = 0;
            dirtyPlanningPasses = 0;
            fallbackPlanningPasses = 0;
            partialPlanningPasses = 0;
            domainCacheRebuilds = 0;
            demandCacheRebuilds = 0;
            demandCachePartialRefreshes = 0;
            goalCacheFullRebuilds = 0;
            goalCachePartialRefreshes = 0;
            goalRouteProbes = 0;
            goalRouteSuccesses = 0;
            goalRouteFailures = 0;
            goalRouteCacheSkips = 0;
            goalRouteInvalidations = 0;
            goalRouteContinuationPasses = 0;
            maximumPendingGoalRouteProbes = pendingGoalRouteProbes.Count;
            inventoryUiRefreshes = 0;
            maximumDirtyProducts = dirtyPlanningProducts.Count;
            maximumDirtyDemandMaterials = dirtyDemandMaterials.Count;
            maximumDirtyFactories = dirtyPlanningFactories.Count;
            failedPlanningPasses = 0;
            ignoredNestedDirtySignals = 0;
            materialSignals = 0;
            materialDirtySignals = 0;
            materialChangedValues = 0;
            maximumMaterialChangesPerSignal = 0;
            objectSignals = 0;
            objectDirtySignals = 0;
            objectChangedValues = 0;
            maximumObjectChangesPerSignal = 0;
            lastPlanningDiagnosticsTick = ticks;
        }

        // Purpose: Clears diagnostic windows without touching planner-owned game state.
        private void ResetPlanningDiagnostics()
        {
            planningDirtyOriginCounts.Clear();
            planningPasses = 0;
            dirtyPlanningPasses = 0;
            fallbackPlanningPasses = 0;
            partialPlanningPasses = 0;
            domainCacheRebuilds = 0;
            demandCacheRebuilds = 0;
            demandCachePartialRefreshes = 0;
            goalCacheFullRebuilds = 0;
            goalCachePartialRefreshes = 0;
            goalRouteProbes = 0;
            goalRouteSuccesses = 0;
            goalRouteFailures = 0;
            goalRouteCacheSkips = 0;
            goalRouteInvalidations = 0;
            goalRouteContinuationPasses = 0;
            maximumPendingGoalRouteProbes = 0;
            inventoryUiRefreshes = 0;
            maximumDirtyProducts = 0;
            maximumDirtyDemandMaterials = 0;
            maximumDirtyFactories = 0;
            failedPlanningPasses = 0;
            ignoredNestedDirtySignals = 0;
            materialSignals = 0;
            materialDirtySignals = 0;
            materialChangedValues = 0;
            maximumMaterialChangesPerSignal = 0;
            objectSignals = 0;
            objectDirtySignals = 0;
            objectChangedValues = 0;
            maximumObjectChangesPerSignal = 0;
            lastPlanningDiagnosticsTick = long.MinValue;
            foreach (var timing in new[]
            {
                onTickTiming,
                maintenanceTiming,
                beingCountTiming,
                roomAccountingTickTiming,
                rebalanceTiming,
                priorityTiming,
                materialSignalTiming,
                materialRoomAccountingTiming,
                objectSignalTiming,
                plannerTrackingTiming,
                plannerFactoryOrdersTiming,
                plannerDisabledTiming,
                plannerDemandTopologyTiming,
                plannerDomainCacheTiming,
                plannerDemandCacheTiming,
                plannerDemandCacheFullTiming,
                plannerDemandCachePartialTiming,
                plannerGoalCacheTiming,
                plannerValidationTiming,
                plannerSurplusTiming,
                plannerShortageSchedulingTiming,
                plannerShortageDomainsTiming,
                plannerShortageCrossFacilityTiming,
                plannerGoalSchedulingTiming
            })
            {
                timing.Reset();
            }
        }

        // Purpose: Caches products and ingredients whose inventory can change a plan.
        private void CachePlanningRelevantInventory()
        {
            planningRelevantMaterials.Clear();
            planningRelevantObjectProducts.Clear();
            mindHolderObjectProducts.Clear();
            planningRelevantBeingProducts.Clear();
            foreach (var recipe in Craftable.All.Where(
                         AutoFactoryRecipeRegistry.IsUsableNanoRecipe))
            {
                if (recipe.ProductDef?.MatType is MatType productMaterial)
                {
                    planningRelevantMaterials.Add(productMaterial);
                }
                else if (recipe.ProductDef?.IsBeing == true)
                {
                    planningRelevantBeingProducts.Add(recipe.ProductDef);
                }
                else if (recipe.ProductDef != null)
                {
                    planningRelevantObjectProducts.Add(recipe.ProductDef);
                    if (BodyPartDef.TryForDef(recipe.ProductDef.IdH,
                            out var bodyPart)
                        && bodyPart.HoldsMind)
                    {
                        mindHolderObjectProducts.Add(recipe.ProductDef);
                    }
                }

                foreach (var ingredient in recipe.Ingredients
                             ?? Array.Empty<Mat>())
                {
                    if (ingredient.Type != null)
                    {
                        planningRelevantMaterials.Add(ingredient.Type);
                    }
                }
            }
            CacheGoalDependencyIndex();
        }

        // Purpose: Builds a reverse recipe graph for targeted negative-route invalidation.
        private void CacheGoalDependencyIndex()
        {
            goalRootsByDependencyProduct.Clear();
            var recipesByProduct = Craftable.All
                .Where(AutoFactoryRecipeRegistry.IsUsableNanoRecipe)
                .Where(recipe => recipe.ProductDef != null)
                .GroupBy(recipe => recipe.ProductDef)
                .ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var root in EligibleTargetProducts())
            {
                var dependencies = new HashSet<Def> { root };
                CollectGoalDependencyProducts(root, recipesByProduct,
                    dependencies, new HashSet<Def>());
                foreach (var dependency in dependencies)
                {
                    if (!goalRootsByDependencyProduct.TryGetValue(dependency,
                            out var roots))
                    {
                        roots = new HashSet<Def>();
                        goalRootsByDependencyProduct[dependency] = roots;
                    }
                    roots.Add(root);
                }
            }
        }

        // Purpose: Recursively collects every managed ingredient that can feed one goal root.
        private static void CollectGoalDependencyProducts(Def product,
            Dictionary<Def, Craftable[]> recipesByProduct,
            HashSet<Def> dependencies,
            HashSet<Def> visited)
        {
            if (product == null || !visited.Add(product)
                || !recipesByProduct.TryGetValue(product, out var recipes))
            {
                return;
            }
            foreach (var ingredient in recipes
                         .SelectMany(recipe => recipe.Ingredients
                             ?? Array.Empty<Mat>())
                         .Where(ingredient => ingredient.Type?.Def != null))
            {
                var dependency = ingredient.Type.Def;
                dependencies.Add(dependency);
                CollectGoalDependencyProducts(dependency, recipesByProduct,
                    dependencies, visited);
            }
        }

        // Purpose: Counts material values changed since the previous planning snapshot.
        private int CountChangedPlanningMaterials(
            Dictionary<MatType, int> counts)
        {
            if (planningRelevantMaterials.Count == 0)
            {
                return 1;
            }
            var changed = 0;
            foreach (var material in planningRelevantMaterials)
            {
                lastInventoryCounts.TryGetValue(material, out var previous);
                counts.TryGetValue(material, out var current);
                if (previous != current)
                {
                    changed++;
                    TrackDirtyProduct(material.Def,
                        routeAvailabilityMayIncrease: current > previous);
                }
            }
            return changed;
        }

        // Purpose: Counts object-product values changed since the previous snapshot.
        private int CountChangedPlanningObjectProducts(
            Dictionary<Def, int> counts)
        {
            if (planningRelevantObjectProducts.Count == 0)
            {
                return 0;
            }
            var changed = 0;
            foreach (var product in planningRelevantObjectProducts)
            {
                lastObjectInventoryCounts.TryGetValue(product,
                    out var previous);
                counts.TryGetValue(product, out var current);
                if (previous != current)
                {
                    changed++;
                    TrackDirtyProduct(product,
                        routeAvailabilityMayIncrease: current > previous);
                }
            }
            return changed;
        }

        // Purpose: Replaces Core's aggregate count only for mind-holding body
        // parts, retaining every ordinary object count unchanged.
        private Dictionary<Def, int> BuildOwnedObjectProductCounts(
            Dictionary<Def, int> coreCounts)
        {
            var counts = new Dictionary<Def, int>();
            foreach (var product in planningRelevantObjectProducts)
            {
                var count = 0;
                coreCounts?.TryGetValue(product, out count);
                counts[product] = count;
            }
            if (mindHolderObjectProducts.Count == 0 || S == null)
            {
                return counts;
            }

            var allObjects = S.Query.GetAllObjs.Ask();
            var allBeings = S.Query.AllBeingData.Ask();
            if (allObjects == null)
            {
                return counts;
            }
            foreach (var product in mindHolderObjectProducts)
            {
                counts[product] = 0;
            }
            foreach (var obj in allObjects.Values)
            {
                var product = obj?.Def;
                if (product == null
                    || !mindHolderObjectProducts.Contains(product)
                    || !IsCoreCountedObject(obj, allBeings)
                    || IsHostileMind(obj.Mind))
                {
                    continue;
                }
                counts[product]++;
            }
            return counts;
        }

        // Purpose: Refreshes biological active counts and available robotic chassis counts.
        private void RefreshBeingCounts()
        {
            var changed = false;
            foreach (var product in planningRelevantBeingProducts)
            {
                lastBeingCounts.TryGetValue(product, out var previous);
                var current = CountOwnedBeingProduct(product);
                var isNonDeliveryChange = nonDeliveryBeingCountChanges
                    .Remove(product);
                if (previous == current && lastBeingCounts.ContainsKey(product))
                {
                    continue;
                }

                changed = true;
                TrackDirtyProduct(product);
                var delivered = isNonDeliveryChange
                    ? 0 : Math.Max(0, current - previous);
                if (delivered > 0
                    && pendingBeingDeliveries.TryGetValue(product,
                        out var pending))
                {
                    var remaining = pending - delivered;
                    if (remaining > 0)
                    {
                        pendingBeingDeliveries[product] = remaining;
                    }
                    else
                    {
                        pendingBeingDeliveries.Remove(product);
                    }
                }
                lastBeingCounts[product] = current;
            }
            if (changed)
            {
                inventoryUiRefreshPending = true;
            }
        }

        // Purpose: Refreshes visible owned counts at most once per game tick.
        private void FlushPendingInventoryUi()
        {
            if (!inventoryUiRefreshPending)
            {
                return;
            }
            inventoryUiRefreshPending = false;
            inventoryUiRefreshes++;
            ui?.OnInventoryChanged();
        }

        // Purpose: Reapplies the configured task priority to every managed factory.
        private void ApplyFactoryPriorityToAll()
        {
            foreach (var crafter in managedFactories.ToArray())
            {
                if (crafter == null)
                {
                    managedFactories.Remove(crafter);
                    continue;
                }
                ApplyFactoryPriority(crafter);
            }
        }

        // Purpose: Keeps automation-owned facilities configured while they wait idle.
        private void EnsureManagedFactoriesConfigured()
        {
            foreach (var crafter in managedFactories.Where(crafter =>
                         crafter?.Settings != null).ToArray())
            {
                // Core treats an empty native crafting-order list as
                // Unconfigured unless this supported crafter setting is on.
                // Idle Nano facilities are still configured because Auto
                // Factory owns when their next order should be created.
                crafter.Settings.IsIgnoreUnconfiguredOn = true;
                crafter.UI?.MarkConfigured();
            }
        }

        // Purpose: Reapplies boosted priority only to invalidated factories unless safety-pass due.
        private void ApplyFactoryPriorityForPlanning(bool forceAll)
        {
            if (forceAll)
            {
                ApplyFactoryPriorityToAll();
                return;
            }
            foreach (var crafter in dirtyPlanningFactories.ToArray())
            {
                if (crafter != null && managedFactories.Contains(crafter))
                {
                    ApplyFactoryPriority(crafter);
                }
            }
        }

        // Purpose: Applies task, production-goal, and refill priority to one factory.
        private void ApplyFactoryPriority(MultiCrafterComp crafter)
        {
            if (crafter == null)
            {
                return;
            }

            var nativePriority = Math.Min(factoryPriority, 5);
            crafter.Settings.TaskPriority.Set(nativePriority);
            crafter.Operatable?.SetPriority(nativePriority);
            if (crafter.TryGetGoal(out var productionGoal))
            {
                ApplyGoalPriority(productionGoal, nativePriority);
            }

            if (crafter.Entity.TryGetComponent(out RefillableStorageComp fuel))
            {
                while (fuel.RefillPriority != nativePriority)
                {
                    fuel.AdjustRefillPriority(
                        fuel.RefillPriority < nativePriority ? 1 : -1);
                }
                if (fuel.TryGetGoal(out var refillGoal))
                {
                    ApplyGoalPriority(refillGoal, nativePriority);
                }
            }
        }

        // Purpose: Applies normal or boosted priority semantics to a Core AI goal.
        private void ApplyGoalPriority(AIGoal goal, int nativePriority)
        {
            if (goal == null)
            {
                return;
            }

            goal.SetPriority(G.Ticks, nativePriority);
            if (factoryPriority == MaximumFactoryPriority)
            {
                goal.Boost();
            }
            else
            {
                goal.BoostedUntil = 0;
            }
        }

        // Purpose: Identifies any facility whose scope and priority Auto Factory manages.
        internal static bool IsManagedFactory(MultiCrafterComp crafter)
        {
            return crafter?.Tile?.Definition?.Id is string id
                && ManagedFactoryIds.Contains(id);
        }

        // Purpose: Identifies the Nano Fabricator facility definition.
        internal static bool IsNanoFabricator(MultiCrafterComp crafter)
        {
            return crafter?.Tile?.Definition?.Id
                == AutoFactoryRecipeRegistry.NanoFabricatorDefId;
        }

        // Purpose: Identifies the Nano Foundry facility definition.
        internal static bool IsNanoFoundry(MultiCrafterComp crafter)
        {
            return crafter?.Tile?.Definition?.Id
                == AutoFactoryRecipeRegistry.NanoFoundryDefId;
        }

        // Purpose: Identifies the Nano Cloning Pod facility definition.
        internal static bool IsNanoCloningPod(MultiCrafterComp crafter)
        {
            return crafter?.Tile?.Definition?.Id
                == AutoFactoryRecipeRegistry.NanoCloningDefId;
        }

        // Purpose: Identifies facilities eligible for automated production scheduling.
        internal static bool IsAutomatedFactory(MultiCrafterComp crafter)
        {
            return IsNanoFabricator(crafter)
                || IsNanoFoundry(crafter)
                || IsNanoCloningPod(crafter);
        }

        // Purpose: Reopens goal routes immediately when Core research availability changes.
        private void OnResearchChanged(Def _)
        {
            MarkProductionPlanningDirty(PlanningDirtyOrigin.ResearchChanged);
        }

        // Purpose: Tracks active material shortages that may require automated production.
        private void OnMatDeficitChanged(MatDeficitEventHolder deficit, bool added)
        {
            if (deficit == null)
            {
                return;
            }

            if (!added)
            {
                if (activeDeficits.Remove(deficit))
                {
                    if (!IsAutomatableDeficit(deficit))
                    {
                        return;
                    }
                    TrackDirtyDemandMaterial(deficit.DeficitType);
                    MarkProductionPlanningDirty(
                        PlanningDirtyOrigin.DeficitChanged);
                }
                return;
            }

            if (!activeDeficits.Add(deficit) || !IsAutomatableDeficit(deficit))
            {
                return;
            }

            TrackDirtyDemandMaterial(deficit.DeficitType);
            // The next clock tick runs after Core finishes this synchronous
            // callback; repeated deficit signals before then collapse to one pass.
            MarkProductionPlanningDirty(PlanningDirtyOrigin.DeficitChanged);
        }

        // Purpose: Updates material inventory and resolves fulfilled pending deliveries.
        private void OnMatCountChanged(Dictionary<MatType, int> counts)
        {
            var signalStarted = Stopwatch.GetTimestamp();
            materialSignals++;
            try
            {
                counts ??= new Dictionary<MatType, int>();
                var changedValues = CountChangedPlanningMaterials(counts);
                if (changedValues > 0)
                {
                    materialDirtySignals++;
                    materialChangedValues += changedValues;
                    maximumMaterialChangesPerSignal = Math.Max(
                        maximumMaterialChangesPerSignal, changedValues);
                }
                foreach (var pair in pendingDeliveries.ToArray())
                {
                    lastInventoryCounts.TryGetValue(pair.Key, out var previous);
                    counts.TryGetValue(pair.Key, out var current);
                    var delivered = Math.Max(0, current - previous);
                    if (delivered <= 0)
                    {
                        continue;
                    }
                    var remaining = pair.Value - delivered;
                    if (remaining > 0)
                    {
                        pendingDeliveries[pair.Key] = remaining;
                    }
                    else
                    {
                        pendingDeliveries.Remove(pair.Key);
                    }
                    TrackDirtyProduct(pair.Key.Def);
                    TrackDirtyDemandMaterial(pair.Key);
                }

                lastInventoryCounts.Clear();
                foreach (var material in planningRelevantMaterials)
                {
                    counts.TryGetValue(material, out var count);
                    lastInventoryCounts[material] = count;
                }

                inventoryUiRefreshPending = true;
                if (changedValues > 0)
                {
                    MarkProductionPlanningDirty(
                        PlanningDirtyOrigin.MaterialInventoryChanged);
                }
            }
            finally
            {
                materialSignalTiming.Record(signalStarted);
            }
        }

        // Purpose: Updates object-product inventory and resolves fulfilled pending deliveries.
        private void OnObjCountChanged(Dictionary<Def, int> counts,
            Dictionary<Def, int> equippedCounts)
        {
            var signalStarted = Stopwatch.GetTimestamp();
            objectSignals++;
            try
            {
                counts ??= new Dictionary<Def, int>();
                var ownedCounts = BuildOwnedObjectProductCounts(counts);
                var changedValues = CountChangedPlanningObjectProducts(
                    ownedCounts);
                if (changedValues > 0)
                {
                    objectDirtySignals++;
                    objectChangedValues += changedValues;
                    maximumObjectChangesPerSignal = Math.Max(
                        maximumObjectChangesPerSignal, changedValues);
                }
                foreach (var pair in pendingObjectDeliveries.ToArray())
                {
                    lastObjectInventoryCounts.TryGetValue(pair.Key,
                        out var previous);
                    ownedCounts.TryGetValue(pair.Key, out var current);
                    var delivered = Math.Max(0, current - previous);
                    if (delivered <= 0)
                    {
                        continue;
                    }
                    var remaining = pair.Value - delivered;
                    if (remaining > 0)
                    {
                        pendingObjectDeliveries[pair.Key] = remaining;
                    }
                    else
                    {
                        pendingObjectDeliveries.Remove(pair.Key);
                    }
                }

                lastObjectInventoryCounts.Clear();
                foreach (var product in planningRelevantObjectProducts)
                {
                    ownedCounts.TryGetValue(product, out var count);
                    lastObjectInventoryCounts[product] = count;
                }
                inventoryUiRefreshPending = true;
                if (changedValues > 0)
                {
                    MarkProductionPlanningDirty(
                        PlanningDirtyOrigin.ObjectInventoryChanged);
                }
            }
            finally
            {
                objectSignalTiming.Record(signalStarted);
            }
        }

        // Purpose: Re-evaluates Brain/Core ownership when diplomacy changes
        // whether a stored Mind is hostile toward the player.
        private void OnFactionRelationChanged(FactionDef first,
            FactionDef second)
        {
            if (mindHolderObjectProducts.Count == 0
                || (first != null && second != null
                    && !first.IsPlayer && !second.IsPlayer))
            {
                return;
            }

            var ownedCounts = BuildOwnedObjectProductCounts(null);
            var changed = false;
            foreach (var product in mindHolderObjectProducts)
            {
                lastObjectInventoryCounts.TryGetValue(product,
                    out var previous);
                ownedCounts.TryGetValue(product, out var current);
                if (current == previous
                    && lastObjectInventoryCounts.ContainsKey(product))
                {
                    continue;
                }
                lastObjectInventoryCounts[product] = current;
                TrackDirtyProduct(product);
                changed = true;
            }
            if (!changed)
            {
                return;
            }
            inventoryUiRefreshPending = true;
            MarkProductionPlanningDirty(
                PlanningDirtyOrigin.ObjectInventoryChanged);
        }

        // Purpose: Schedules a count refresh when a supported being enters the active world.
        private void OnBeingAdded(Being being)
        {
            if (being?.Definition != null
                && planningRelevantBeingProducts.Contains(being.Definition))
            {
                TrackDirtyProduct(being.Definition);
                MarkProductionPlanningDirty(
                    PlanningDirtyOrigin.BeingInventoryChanged);
            }
        }

        // Purpose: Schedules a Core-parity owned-count refresh after a supported being dies.
        private void OnBeingDied(BeingData being)
        {
            var product = being?.Species?.Def;
            if (product != null
                && planningRelevantBeingProducts.Contains(product))
            {
                TrackDirtyProduct(product);
                MarkProductionPlanningDirty(
                    PlanningDirtyOrigin.BeingInventoryChanged);
            }
        }

        // Purpose: Reconciles robotic ownership as soon as a Core is installed.
        private void OnMindInstalled(Mind _, ObjData part, BeingData being)
        {
            OnRoboticCoreInventoryChanged(being);
            OnMindHolderObjectStateChanged(part);
        }

        // Purpose: Reconciles robotic ownership as soon as its Core is removed.
        private void OnMindRemoved(Mind _, BeingData being, ObjData part)
        {
            OnRoboticCoreInventoryChanged(being);
            OnMindHolderObjectStateChanged(part);
        }

        // Purpose: Refreshes a Brain/Core goal after its Mind is installed or
        // removed, without treating the disposition change as crafted output.
        private void OnMindHolderObjectStateChanged(ObjData part)
        {
            var product = part?.Def;
            if (product == null || !mindHolderObjectProducts.Contains(product))
            {
                return;
            }
            TrackDirtyProduct(product);
            inventoryUiRefreshPending = true;
            MarkProductionPlanningDirty(
                PlanningDirtyOrigin.ObjectInventoryChanged);
        }

        // Purpose: Recounts after Core finishes the slot change without claiming delivery credit.
        private void OnRoboticCoreInventoryChanged(BeingData being)
        {
            var product = being?.Species?.Def;
            if (product == null || !IsAvailableRoboticBeingRecord(being)
                || !planningRelevantBeingProducts.Contains(product))
            {
                return;
            }

            nonDeliveryBeingCountChanges.Add(product);
            TrackDirtyProduct(product);
            MarkProductionPlanningDirty(
                PlanningDirtyOrigin.BeingInventoryChanged);
        }

        // Purpose: Schedules a count refresh when a supported being entity is removed.
        private void OnEntityRemoved(Entity entity)
        {
            if (entity?.Definition?.IsBeing == true
                && planningRelevantBeingProducts.Contains(entity.Definition))
            {
                TrackDirtyProduct(entity.Definition);
                MarkProductionPlanningDirty(
                    PlanningDirtyOrigin.BeingInventoryChanged);
            }
        }

        // Purpose: Completes tracked orders, records delivery credit, and returns control to automation.
        private void OnCrafterProducedItem(CraftingOrder order, int quantity)
        {
            if (order == null || quantity < 1)
            {
                return;
            }

            var crafter = order.Crafter;
            var isManual = manualOrders.Contains(order);
            if (!IsAutomatedFactory(crafter)
                || order.Demand?.Craftable?.Type
                    != AutoFactoryRecipeRegistry.RecipeTypeFor(crafter)
                || (!isManual && order.Demand.Type != CraftingDemandType.One))
            {
                return;
            }

            var product = order.Demand?.Craftable?.ProductDef;
            var requestedProduct = RequestedProduct(order);
            var material = product?.MatType;
            if (material != null)
            {
                LogActivity(crafter, isManual
                        ? "Completed manual order " + material.NameT + " x"
                            + quantity + "; returning control to automation."
                        : "Completed " + material.NameT + " x"
                            + quantity + "; clearing automated order.",
                    AutoFactoryLogCategory.Completion);
                pendingDeliveries.TryGetValue(material, out var pending);
                pendingDeliveries[material] = pending + quantity;
                TrackDirtyDemandMaterial(material);
                if (GetFactoryScope(crafter) == AutoFactoryScope.Room)
                {
                    var roomId = GetFactoryRoomId(crafter);
                    if (roomId > 0)
                    {
                        var key = (roomId, material);
                        roomPendingDeliveries.TryGetValue(key, out var roomPending);
                        roomPendingDeliveries[key] = roomPending + quantity;
                    }
                }
            }
            else if (product != null)
            {
                LogActivity(crafter, isManual
                        ? "Completed manual order " + product.NameT + " x"
                            + quantity + "; returning control to automation."
                        : "Completed " + product.NameT + " x"
                            + quantity + "; clearing automated order.",
                    AutoFactoryLogCategory.Completion);
                if (product.IsBeing)
                {
                    pendingBeingDeliveries.TryGetValue(product,
                        out var pending);
                    pendingBeingDeliveries[product] = pending + quantity;
                }
                else
                {
                    pendingObjectDeliveries.TryGetValue(product,
                        out var pending);
                    pendingObjectDeliveries[product] = pending + quantity;
                }
            }

            if (crafter.Orders?.Contains(order) == true)
            {
                crafter.UI.StopProducing(order);
            }
            automatedOrders.Remove(crafter);
            manualOrders.Remove(order);
            fallbackOrderRoots.Remove(order);
            productionGoalOrders.Remove(order);
            TrackDirtyProduct(product);
            TrackDirtyProduct(requestedProduct);
            TrackDirtyFactory(crafter);
            inventoryUiRefreshPending = true;
            // The next clock tick schedules the replacement order after
            // CraftingSys finishes this production callback.
            MarkProductionPlanningDirty(PlanningDirtyOrigin.OrderCompleted);
            RefreshSelectedCrafterPanel(crafter);
        }

        // Purpose: Gives a player-selected Produce One order temporary ownership of a factory.
        internal void RegisterManualOrder(MultiCrafterComp crafter,
            CraftingOrder order)
        {
            if (!IsAutomatedFactory(crafter)
                || order?.Demand?.Craftable?.Type
                    != AutoFactoryRecipeRegistry.RecipeTypeFor(crafter))
            {
                return;
            }

            foreach (var existing in crafter.Orders?
                         .Where(candidate => candidate != null && candidate != order)
                         .ToArray() ?? Array.Empty<CraftingOrder>())
            {
                if (automatedOrders.TryGetValue(crafter, out var automated)
                    && existing == automated)
                {
                    LogActivity(crafter,
                        "Cancelled automated order " + ProductName(existing)
                        + " because the player selected a manual order.",
                        AutoFactoryLogCategory.Manual);
                }
                crafter.UI.StopProducing(existing);
                fallbackOrderRoots.Remove(existing);
                productionGoalOrders.Remove(existing);
                manualOrders.Remove(existing);
            }

            automatedOrders.Remove(crafter);
            order.Demand.Type = CraftingDemandType.One;
            order.Demand.AmountWanted = 1;
            order.Demand.AmountProduced = 0;
            order.Demand.AmountProducedThis = 0;
            manualOrders.Add(order);
            TrackDirtyFactory(crafter);
            LogActivity(crafter,
                "Player queued manual Produce One order: "
                + ProductName(order) + ". Automation will resume after completion.",
                AutoFactoryLogCategory.Manual);
            MarkProductionPlanningDirty(PlanningDirtyOrigin.ManualOrder);
            RefreshSelectedCrafterPanel(crafter);
        }

        // Purpose: Records the product and facility invalidated by an order removal.
        internal void RegisterStoppedOrder(MultiCrafterComp crafter,
            CraftingOrder order)
        {
            if (!IsAutomatedFactory(crafter))
            {
                return;
            }
            TrackDirtyFactory(crafter);
            TrackDirtyProduct(RequestedProduct(order));
            MarkProductionPlanningDirty(PlanningDirtyOrigin.OrderStopped);
        }

        // Purpose: Reloads the selected factory panel after an order transition completes.
        internal void RefreshSelectedCrafterPanel(MultiCrafterComp crafter)
        {
            if (crafter?.Entity == null)
            {
                return;
            }

            // ProductionTab creates its order rows only while loading the tab.
            // Refresh on the next frame, after removal and requeue are complete,
            // and only when this particular Nano Fabricator is selected.
            Coroutines.OnNextFrame(() =>
                S.UIQuery.ReloadEntityIfSelected.Ask(crafter.Entity));
        }

        // Purpose: Rejects self-fuel shortages and deficits with no eligible Nano recipe.
        private bool IsAutomatableDeficit(MatDeficitEventHolder deficit)
        {
            if (deficit?.DeficitType == null)
            {
                return false;
            }

            // Do not let a Nano Fabricator attempt to manufacture the fuel that
            // its own refill storage is currently missing.
            if (deficit.Owner is RefillableStorageComp refill
                && ManagedFactoryIds.Contains(refill.Tile?.Definition?.Id))
            {
                return false;
            }

            return IsProductUnlockedForAutomation(deficit.DeficitType.Def)
                && AutoFactoryRecipeRegistry.FindAnyRecipe(
                    deficit.DeficitType) != null;
        }

        // Purpose: Runs order reconciliation through a guard against nested signal callbacks.
        private void RebalanceOrders(bool forceFull)
        {
            if (isRebalancing)
            {
                return;
            }

            isRebalancing = true;
            try
            {
                RebalanceOrdersCore(forceFull);
            }
            finally
            {
                isRebalancing = false;
            }
        }

        // Purpose: Reconciles manual, automated, retired, cross-facility, and goal orders.
        private void RebalanceOrdersCore(bool forceFull)
        {
            var plannerPhaseStarted = Stopwatch.GetTimestamp();
            RestorePendingFactoryOrderTracking();
            productionGoalOrders.RemoveWhere(order => order?.Crafter == null
                || order.Crafter.Orders?.Contains(order) != true);
            foreach (var order in crossFacilityOrders.Keys.Where(order =>
                         order?.Crafter == null
                         || order.Crafter.Orders?.Contains(order) != true).ToArray())
            {
                crossFacilityOrders.Remove(order);
            }

            if (TrackRoomEnclosureChanges())
            {
                domainTopologyDirty = true;
                demandTopologyDirty = true;
                InvalidateAllGoalRouteFailures();
            }
            plannerTrackingTiming.Record(plannerPhaseStarted);

            plannerPhaseStarted = Stopwatch.GetTimestamp();
            var factoryOrderCandidates = forceFull
                ? automatedFactories.ToArray()
                : dirtyPlanningFactories
                    .Where(automatedFactories.Contains)
                    .ToArray();
            foreach (var crafter in factoryOrderCandidates)
            {
                var retiredOrders = crafter?.Orders?
                    .Where(order => AutoFactoryRecipeRegistry
                        .IsRetiredNanoRecipe(order?.Demand?.Craftable))
                    .ToArray();
                if (retiredOrders != null)
                {
                    foreach (var retiredOrder in retiredOrders)
                    {
                        LogActivity(crafter,
                            "Cancelled retired lower-throughput or Converter order after the recipe-policy upgrade: "
                            + ProductName(retiredOrder) + ".",
                            AutoFactoryLogCategory.Cancellation);
                        crafter.UI.StopProducing(retiredOrder);
                        manualOrders.Remove(retiredOrder);
                        fallbackOrderRoots.Remove(retiredOrder);
                        productionGoalOrders.Remove(retiredOrder);
                        crossFacilityOrders.Remove(retiredOrder);
                        if (automatedOrders.TryGetValue(crafter,
                                out var trackedRetired)
                            && trackedRetired == retiredOrder)
                        {
                            automatedOrders.Remove(crafter);
                        }
                    }
                }

                var autoFactoryOrders = crafter?.Orders?
                    .Where(order => order?.Demand?.Craftable?.Type
                        == AutoFactoryRecipeRegistry.RecipeTypeFor(crafter))
                    .ToArray();
                if (autoFactoryOrders == null)
                {
                    continue;
                }
                if (autoFactoryOrders.Length > 0)
                {
                    // Auto Factory creates orders through Core's authoritative
                    // query rather than MultiCrafterUI.StartProducing, so clear
                    // only the native stale warning once an order truly exists.
                    crafter.UI?.MarkConfigured();
                }

                manualOrders.RemoveWhere(order => order?.Crafter == crafter
                    && crafter.Orders?.Contains(order) != true);

                foreach (var order in autoFactoryOrders.Where(order =>
                             order.Demand.Type == CraftingDemandType.Custom
                             || (order.Demand.Type == CraftingDemandType.One
                                 && order.State == CraftingOrderState.ProducedEnough)))
                {
                    LogActivity(crafter, "Cleared completed or legacy order: "
                        + ProductName(order) + ".",
                        AutoFactoryLogCategory.Completion);
                    crafter.UI.StopProducing(order);
                    fallbackOrderRoots.Remove(order);
                    productionGoalOrders.Remove(order);
                    crossFacilityOrders.Remove(order);
                }

                var activeOrders = autoFactoryOrders
                    .Where(order => !manualOrders.Contains(order))
                    .Where(order => order.Demand.Type == CraftingDemandType.One
                        && order.State != CraftingOrderState.ProducedEnough
                        && crafter.Orders?.Contains(order) == true)
                    .ToArray();
                automatedOrders.TryGetValue(crafter, out var trackedOrder);
                var retainedOrder = activeOrders.Contains(trackedOrder)
                    ? trackedOrder
                    : activeOrders.Contains(crafter.CurrentOrder)
                        ? crafter.CurrentOrder
                        : activeOrders.FirstOrDefault();

                // The game's save owns the order list, while this tracking map
                // is rebuilt at runtime. Enforce the one-order invariant during
                // every reconciliation pass, even when the crafter lacks fuel
                // and therefore cannot emit a production-completion signal.
                foreach (var surplusOrder in activeOrders.Where(order =>
                             order != retainedOrder))
                {
                    LogActivity(crafter, "Removed surplus queued order: "
                        + ProductName(surplusOrder) + ".",
                        AutoFactoryLogCategory.Cancellation);
                    crafter.UI.StopProducing(surplusOrder);
                    fallbackOrderRoots.Remove(surplusOrder);
                    productionGoalOrders.Remove(surplusOrder);
                    crossFacilityOrders.Remove(surplusOrder);
                }

                if (retainedOrder != null)
                {
                    automatedOrders[crafter] = retainedOrder;
                }
                else
                {
                    automatedOrders.Remove(crafter);
                }
            }
            plannerFactoryOrdersTiming.Record(plannerPhaseStarted);

            if (!enabled)
            {
                plannerPhaseStarted = Stopwatch.GetTimestamp();
                foreach (var pair in automatedOrders.ToArray())
                {
                    if (pair.Key?.Orders?.Contains(pair.Value) == true)
                    {
                        LogActivity(pair.Key,
                            "Cancelled automated order because automation was disabled: "
                            + ProductName(pair.Value) + ".",
                            AutoFactoryLogCategory.Cancellation);
                        pair.Key.UI.StopProducing(pair.Value);
                    }
                    fallbackOrderRoots.Remove(pair.Value);
                    productionGoalOrders.Remove(pair.Value);
                    crossFacilityOrders.Remove(pair.Value);
                }
                automatedOrders.Clear();
                plannerDisabledTiming.Record(plannerPhaseStarted);
                return;
            }

            plannerPhaseStarted = Stopwatch.GetTimestamp();
            RefreshPlannerCaches(forceFull);
            var domains = cachedDomains;
            var wantedByDomain = cachedWantedByDomain;
            var globalGoalProducts = cachedGlobalGoalProducts;
            plannerDemandTopologyTiming.Record(plannerPhaseStarted);

            plannerPhaseStarted = Stopwatch.GetTimestamp();
            foreach (var pair in automatedOrders.ToArray())
            {
                var crafter = pair.Key;
                var order = pair.Value;
                var product = order?.Demand?.Craftable?.ProductDef;
                var material = product?.MatType;
                var requestedProduct = RequestedProduct(order);
                var requestedMaterial = requestedProduct?.MatType;
                var researchValid = IsRecipeUnlockedForAutomation(
                        order?.Demand?.Craftable)
                    && IsProductUnlockedForAutomation(requestedProduct);
                var domain = crafter == null ? 0 : ScopeDomainKey(crafter);
                wantedByDomain.TryGetValue(domain, out var wantedMaterials);
                var demandValid = requestedMaterial != null
                    && wantedMaterials?.ContainsKey(requestedMaterial) == true;
                var goalValid = requestedProduct != null
                    && IsAutomationLocationValid(crafter)
                    && globalGoalProducts.ContainsKey(requestedProduct)
                    && AutoFactoryRecipeRegistry.FindRecipe(requestedProduct, crafter)
                        != null;
                var crossValid = order != null
                    && crossFacilityOrders.TryGetValue(order,
                        out var crossDependency)
                    && IsCrossFacilityDemandStillValid(crossDependency,
                        wantedByDomain, globalGoalProducts);
                var valid = crafter != null
                    && automatedFactories.Contains(crafter)
                    && IsAutomationLocationValid(crafter)
                    && order != null
                    && crafter.Orders?.Contains(order) == true
                    && product != null
                    && researchValid
                    && (demandValid || goalValid || crossValid);
                if (valid)
                {
                    if (crossValid)
                    {
                        continue;
                    }
                    var wasProductionGoal = productionGoalOrders.Contains(order);
                    if (wasProductionGoal && !goalValid && demandValid)
                    {
                        productionGoalOrders.Remove(order);
                    }
                    else if (!wasProductionGoal && !demandValid && goalValid)
                    {
                        productionGoalOrders.Add(order);
                    }
                    continue;
                }

                if (crafter?.Orders?.Contains(order) == true)
                {
                    LogActivity(crafter,
                        !IsAutomationLocationValid(crafter)
                            ? "Cancelled " + ProductName(order)
                                + ": Room-scoped automation is paused because "
                                + "the factory is not inside an enclosed room."
                            : CancellationReason(crafter, order),
                        AutoFactoryLogCategory.Cancellation);
                    crafter.UI.StopProducing(order);
                }
                automatedOrders.Remove(crafter);
                if (order != null)
                {
                    fallbackOrderRoots.Remove(order);
                    productionGoalOrders.Remove(order);
                    crossFacilityOrders.Remove(order);
                }
            }
            plannerValidationTiming.Record(plannerPhaseStarted);

            plannerPhaseStarted = Stopwatch.GetTimestamp();
            foreach (var domain in domains)
            {
                var wantedMaterials = wantedByDomain[domain.Key];
                foreach (var materialGroup in automatedOrders
                             .Where(pair => ScopeDomainKey(pair.Key) == domain.Key)
                             .Where(pair => !productionGoalOrders.Contains(pair.Value))
                             .Where(pair => pair.Value?.Demand?.Craftable?.ProductDef?.MatType != null)
                             .Where(pair => !fallbackOrderRoots.ContainsKey(pair.Value))
                             .GroupBy(pair => pair.Value.Demand.Craftable.ProductDef.MatType)
                             .ToArray())
                {
                    var allowed = wantedMaterials.TryGetValue(
                        materialGroup.Key, out var wanted) ? wanted : 0;
                    foreach (var surplus in materialGroup.Skip(allowed).ToArray())
                    {
                        if (surplus.Key?.Orders?.Contains(surplus.Value) == true)
                        {
                            LogActivity(surplus.Key,
                                "Cancelled surplus demand for "
                                + ProductName(surplus.Value) + ".",
                                AutoFactoryLogCategory.Cancellation);
                            surplus.Key.UI.StopProducing(surplus.Value);
                        }
                        automatedOrders.Remove(surplus.Key);
                        productionGoalOrders.Remove(surplus.Value);
                        crossFacilityOrders.Remove(surplus.Value);
                    }
                }
            }

            foreach (var productGroup in automatedOrders
                         .Where(pair => productionGoalOrders.Contains(pair.Value))
                         .Where(pair => RequestedProduct(pair.Value) != null)
                         .GroupBy(pair => RequestedProduct(pair.Value))
                         .ToArray())
            {
                var allowed = globalGoalProducts.TryGetValue(
                    productGroup.Key, out var wanted) ? wanted : 0;
                foreach (var surplus in productGroup.Skip(allowed).ToArray())
                {
                    if (surplus.Key?.Orders?.Contains(surplus.Value) == true)
                    {
                        LogActivity(surplus.Key,
                            "Cancelled surplus global production-goal demand for "
                            + ProductName(surplus.Value) + ".",
                            AutoFactoryLogCategory.Cancellation);
                        surplus.Key.UI.StopProducing(surplus.Value);
                    }
                    automatedOrders.Remove(surplus.Key);
                    productionGoalOrders.Remove(surplus.Value);
                    crossFacilityOrders.Remove(surplus.Value);
                }
            }
            plannerSurplusTiming.Record(plannerPhaseStarted);

            plannerPhaseStarted = Stopwatch.GetTimestamp();
            var shortageDomainsStarted = plannerPhaseStarted;
            foreach (var domain in domains.OrderBy(pair =>
                         IsGlobalDomain(pair.Key) ? 1 : 0))
            {
                ScheduleDomain(domain.Value, wantedByDomain[domain.Key]);
            }
            plannerShortageDomainsTiming.Record(shortageDomainsStarted);

            // Lacking-material dependencies retain absolute precedence over
            // every Production Goal, including priority-100 goals.
            var shortageCrossFacilityStarted = Stopwatch.GetTimestamp();
            ScheduleCrossFacilityDependencies(domains, wantedByDomain,
                new Dictionary<Def, int>());
            plannerShortageCrossFacilityTiming.Record(
                shortageCrossFacilityStarted);
            plannerShortageSchedulingTiming.Record(plannerPhaseStarted);

            // Process one goal root at a time so its direct, local intermediary,
            // and cross-facility routes all inherit the same priority. This
            // prevents a lower-priority dependency from occupying a provider
            // before a higher-priority root has had an opportunity to progress.
            plannerPhaseStarted = Stopwatch.GetTimestamp();
            var goalRandom = new Random(unchecked((int)G.Ticks));
            var goalAvailableMaterials = new Dictionary<MatType, int>();
            LoadAvailableMaterials(goalAvailableMaterials);
            var allCrafters = domains.Values.SelectMany(value => value).ToList();
            var freeGoalCrafters = allCrafters
                .Where(IsAutomationLocationValid)
                .Where(crafter => !automatedOrders.ContainsKey(crafter))
                .Where(crafter => crafter.Orders == null
                    || crafter.Orders.Count == 0)
                .ToList();
            var remainingGoalRouteProbes = MaximumGoalRouteProbesPerPass;
            var deferredGoalRouteProbe = false;
            foreach (var root in GoalSchedulingOrder(goalRandom))
            {
                if (freeGoalCrafters.Count == 0)
                {
                    break;
                }
                if (!globalGoalProducts.TryGetValue(root, out var wanted))
                {
                    continue;
                }
                var availability = GoalFacilityAvailability.Capture(
                    freeGoalCrafters, ScopeDomainKey);
                if (!NeedsGoalRouteProbe(root, availability))
                {
                    goalRouteCacheSkips++;
                    continue;
                }
                if (remainingGoalRouteProbes <= 0)
                {
                    deferredGoalRouteProbe = true;
                    continue;
                }
                remainingGoalRouteProbes--;
                goalRouteProbes++;
                pendingGoalRouteProbes.Remove(root);
                unavailableGoalRoutes.Remove(root);
                var freeBefore = freeGoalCrafters.Count;
                var oneGoal = new Dictionary<Def, int>
                {
                    [root] = wanted
                };
                ScheduleCrossFacilityDependencies(domains, wantedByDomain,
                    oneGoal, includeLackingMaterialRequests: false,
                    goalAvailableMaterials, goalRandom, freeGoalCrafters);
                ScheduleGlobalGoals(allCrafters, oneGoal,
                    goalAvailableMaterials, goalRandom, freeGoalCrafters);
                if (freeGoalCrafters.Count < freeBefore)
                {
                    goalRouteSuccesses++;
                    continue;
                }
                goalRouteFailures++;
                unavailableGoalRoutes[root] = new GoalRouteFailure(
                    availability);
            }
            if (deferredGoalRouteProbe && freeGoalCrafters.Count > 0)
            {
                goalRouteContinuationPasses++;
                MarkProductionPlanningDirty(
                    PlanningDirtyOrigin.GoalProbeContinuation);
            }
            plannerGoalSchedulingTiming.Record(plannerPhaseStarted);
        }

        // Purpose: Decides whether a cached unavailable goal has a meaningful retry reason.
        private bool NeedsGoalRouteProbe(Def root,
            GoalFacilityAvailability availability)
        {
            if (!unavailableGoalRoutes.TryGetValue(root, out var failure)
                || pendingGoalRouteProbes.Contains(root))
            {
                return true;
            }
            return availability.HasMeaningfulChangeSince(
                failure.Availability);
        }

        // Purpose: Assigns direct or intermediary material work within one scope/facility domain.
        private void ScheduleDomain(List<MultiCrafterComp> domainCrafters,
            Dictionary<MatType, int> wantedMaterials,
            Func<MultiCrafterComp, bool> belongsToPool = null,
            bool productionGoal = false)
        {
            if (wantedMaterials.Count == 0)
            {
                return;
            }

            var freeCrafters = domainCrafters
                .Where(candidate => !automatedOrders.ContainsKey(candidate))
                .Where(candidate => candidate.Orders == null || candidate.Orders.Count == 0)
                .ToList();
            if (freeCrafters.Count == 0)
            {
                return;
            }

            var domainKey = ScopeDomainKey(domainCrafters[0]);
            belongsToPool ??= candidate => ScopeDomainKey(candidate) == domainKey;
            var scheduledCounts = automatedOrders
                .Where(pair => belongsToPool(pair.Key))
                .Select(pair => RequestedProduct(pair.Value)?.MatType)
                .Where(material => material != null)
                .GroupBy(material => material)
                .ToDictionary(group => group.Key, group => group.Count());
            var covered = scheduledCounts.Keys.ToHashSet();
            var availableMaterials = new Dictionary<MatType, int>();
            LoadAvailableMaterials(availableMaterials);
            var random = new Random(unchecked((int)G.Ticks));
            var representative = domainCrafters[0];
            var hasDirectWorkableRecipe = wantedMaterials.Keys.Any(material =>
                AutoFactoryRecipeRegistry.FindRecipes(material.Def,
                        representative)
                    .Any(recipe => IsRecipeUnlockedForAutomation(recipe)
                        && HasAllIngredients(recipe, availableMaterials)));

            foreach (var crafter in freeCrafters.ToArray())
            {
                var uniqueCandidates = wantedMaterials.Keys
                    .Where(material => !covered.Contains(material))
                    .Select(material => new
                    {
                        Material = material,
                        Recipe = FindAvailableRecipe(material.Def,
                            representative, availableMaterials, random)
                    })
                    .Where(candidate => candidate.Recipe != null)
                    .ToList();
                if (uniqueCandidates.Count == 0)
                {
                    break;
                }
                var selected = uniqueCandidates[random.Next(
                    uniqueCandidates.Count)];
                if (CreateOneOrder(crafter, selected.Material.Def,
                        selected.Recipe,
                        productionGoal: productionGoal))
                {
                    ReserveIngredients(selected.Recipe, availableMaterials);
                    covered.Add(selected.Material);
                    scheduledCounts.TryGetValue(selected.Material,
                        out var scheduled);
                    scheduledCounts[selected.Material] = scheduled + 1;
                    freeCrafters.Remove(crafter);
                }
            }

            if (!hasDirectWorkableRecipe)
            {
                foreach (var crafter in freeCrafters.ToArray())
                {
                    var fallback = FindFirstWorkableIntermediate(
                        wantedMaterials.Keys.Select(material => material.Def),
                        availableMaterials,
                        representative,
                        random);
                    if (fallback.Material == null || fallback.Recipe == null)
                    {
                        break;
                    }

                    if (CreateOneOrder(crafter, fallback.Material.Def,
                            fallback.Recipe,
                            fallback.Root,
                            productionGoal))
                    {
                        ReserveIngredients(fallback.Recipe, availableMaterials);
                        var rootMaterial = fallback.Root?.MatType;
                        if (rootMaterial != null)
                        {
                            scheduledCounts.TryGetValue(rootMaterial,
                                out var scheduled);
                            scheduledCounts[rootMaterial] = scheduled + 1;
                        }
                        freeCrafters.Remove(crafter);
                    }
                }
            }

            // Every unique deficit is covered. Extra stations may double up,
            // bounded by the number of active deficit holders for that material.
            foreach (var crafter in freeCrafters)
            {
                var candidates = wantedMaterials
                    .Where(pair =>
                    {
                        scheduledCounts.TryGetValue(pair.Key,
                            out var scheduled);
                        return scheduled < pair.Value;
                    })
                    .Select(pair => new
                    {
                        Material = pair.Key,
                        Recipe = FindAvailableRecipe(pair.Key.Def,
                            representative, availableMaterials, random)
                    })
                    .Where(candidate => candidate.Recipe != null)
                    .ToList();
                if (candidates.Count == 0)
                {
                    break;
                }
                var selected = candidates[random.Next(candidates.Count)];
                if (CreateOneOrder(crafter, selected.Material.Def,
                        selected.Recipe,
                        productionGoal: productionGoal))
                {
                    ReserveIngredients(selected.Recipe, availableMaterials);
                    scheduledCounts.TryGetValue(selected.Material,
                        out var scheduled);
                    scheduledCounts[selected.Material] = scheduled + 1;
                }
            }
        }

        // Purpose: Assigns remaining global Production Goals across all eligible free factories.
        private void ScheduleGlobalGoals(List<MultiCrafterComp> crafters,
            Dictionary<Def, int> wantedProducts,
            Dictionary<MatType, int> availableMaterials = null,
            Random random = null,
            List<MultiCrafterComp> sharedFreeCrafters = null)
        {
            if (wantedProducts.Count == 0)
            {
                return;
            }
            var freeCrafters = sharedFreeCrafters ?? crafters
                .Where(IsAutomationLocationValid)
                .Where(crafter => !automatedOrders.ContainsKey(crafter))
                .Where(crafter => crafter.Orders == null
                    || crafter.Orders.Count == 0)
                .ToList();
            if (availableMaterials == null)
            {
                availableMaterials = new Dictionary<MatType, int>();
                LoadAvailableMaterials(availableMaterials);
            }
            random ??= new Random(unchecked((int)G.Ticks));

            foreach (var wantedProduct in wantedProducts
                         .OrderByDescending(pair =>
                             GetProductionPriority(pair.Key))
                         .ThenBy(_ => random.Next()))
            {
                var root = wantedProduct.Key;
                if (!IsProductUnlockedForAutomation(root))
                {
                    continue;
                }
                var remaining = wantedProduct.Value
                    - automatedOrders.Count(order =>
                        RequestedProduct(order.Value) == root);
                if (remaining <= 0)
                {
                    continue;
                }
                foreach (var crafter in freeCrafters.ToArray())
                {
                    if (AutoFactoryRecipeRegistry.FindRecipe(root, crafter)
                        == null)
                    {
                        continue;
                    }
                    var recipe = FindAvailableRecipe(root, crafter,
                        availableMaterials, random);
                    if (recipe != null
                        && CreateOneOrder(crafter, root, recipe,
                            productionGoal: true))
                    {
                        ReserveIngredients(recipe, availableMaterials);
                        freeCrafters.Remove(crafter);
                        if (--remaining <= 0)
                        {
                            break;
                        }
                        continue;
                    }

                    // Priority applies to the requested goal root, not merely
                    // to immediately craftable final products. Try that root's
                    // intermediary chain before allowing a lower-priority goal
                    // to use the same free facility.
                    var fallback = FindFirstWorkableIntermediate(
                        new[] { root }, availableMaterials, crafter, random);
                    if (fallback.Material != null && fallback.Recipe != null
                        && CreateOneOrder(crafter, fallback.Material.Def,
                            fallback.Recipe, fallback.Root,
                            productionGoal: true))
                    {
                        ReserveIngredients(fallback.Recipe, availableMaterials);
                        freeCrafters.Remove(crafter);
                        if (--remaining <= 0)
                        {
                            break;
                        }
                    }
                }
            }
        }

        // Purpose: Maps a factory to a stable Global/Room and facility-family scheduler domain.
        private int ScopeDomainKey(MultiCrafterComp crafter)
        {
            var family = IsNanoFoundry(crafter) ? 1
                : IsNanoCloningPod(crafter) ? 2 : 0;
            if (GetFactoryScope(crafter) != AutoFactoryScope.Room)
            {
                return family switch
                {
                    1 => GlobalFoundryDomain,
                    2 => GlobalCloningDomain,
                    _ => GlobalFabricatorDomain
                };
            }
            return GetFactoryRoomId(crafter) * 3 + family;
        }

        // Purpose: Identifies scheduler domains that use ship-wide demand.
        private static bool IsGlobalDomain(int domain)
        {
            return domain == GlobalFabricatorDomain
                || domain == GlobalFoundryDomain
                || domain == GlobalCloningDomain;
        }

        // Purpose: Rejects Room-scoped factories that are not currently enclosed.
        private bool IsAutomationLocationValid(MultiCrafterComp crafter)
        {
            return crafter != null
                && (GetFactoryScope(crafter) == AutoFactoryScope.Global
                    || GetFactoryRoomId(crafter) > 0);
        }

        // Purpose: Detects enclosure changes and pauses or resumes Room-scoped automation.
        private bool TrackRoomEnclosureChanges()
        {
            var changed = false;
            foreach (var crafter in automatedFactories
                         .Where(candidate => candidate?.Entity != null
                             && GetFactoryScope(candidate)
                                 == AutoFactoryScope.Room))
            {
                var id = crafter.Entity.Id;
                var enclosed = GetFactoryRoomId(crafter) > 0;
                if (!roomEnclosureStates.TryGetValue(id, out var previous))
                {
                    roomEnclosureStates[id] = enclosed;
                    continue;
                }
                if (previous == enclosed)
                {
                    continue;
                }

                roomEnclosureStates[id] = enclosed;
                changed = true;
                TrackDirtyFactory(crafter);
                LogActivity(crafter,
                    enclosed
                        ? "Enclosed room restored. Room-scoped automation may resume."
                        : "Room enclosure lost. Room-scoped automation is paused until the factory is enclosed again.",
                    enclosed
                        ? AutoFactoryLogCategory.Completion
                        : AutoFactoryLogCategory.Issue);
                RefreshSelectedCrafterPanel(crafter);
            }

            var activeIds = new HashSet<int>(automatedFactories
                .Where(candidate => candidate?.Entity != null
                    && GetFactoryScope(candidate) == AutoFactoryScope.Room)
                .Select(candidate => candidate.Entity.Id));
            foreach (var id in roomEnclosureStates.Keys
                         .Where(id => !activeIds.Contains(id)).ToArray())
            {
                roomEnclosureStates.Remove(id);
                changed = true;
            }
            return changed;
        }

        // Purpose: Refreshes only invalidated derived planning data, or all data on safety passes.
        private void RefreshPlannerCaches(bool forceFull)
        {
            if (forceFull)
            {
                InvalidateAllPlannerCaches(
                    preserveGoalRouteFailures: true);
            }

            if (domainTopologyDirty)
            {
                var cacheStarted = Stopwatch.GetTimestamp();
                cachedDomains.Clear();
                foreach (var group in automatedFactories
                             .Where(crafter => crafter?.Tile?.IsConstructed
                                 == true)
                             .Where(IsAutomationLocationValid)
                             .GroupBy(ScopeDomainKey)
                             .OrderBy(group => IsGlobalDomain(group.Key)
                                 ? 1 : 0))
                {
                    cachedDomains[group.Key] = group.ToList();
                }
                domainTopologyDirty = false;
                demandTopologyDirty = true;
                domainCacheRebuilds++;
                plannerDomainCacheTiming.Record(cacheStarted);
            }

            if (demandTopologyDirty)
            {
                var cacheStarted = Stopwatch.GetTimestamp();
                var deficits = CaptureDemandDeficits();
                cachedWantedByDomain.Clear();
                foreach (var domain in cachedDomains)
                {
                    cachedWantedByDomain[domain.Key] =
                        BuildWantedMaterials(domain.Value[0], deficits);
                }
                demandTopologyDirty = false;
                dirtyDemandMaterials.Clear();
                demandCacheRebuilds++;
                plannerDemandCacheTiming.Record(cacheStarted);
                plannerDemandCacheFullTiming.Record(cacheStarted);
            }
            else if (dirtyDemandMaterials.Count > 0)
            {
                var cacheStarted = Stopwatch.GetTimestamp();
                var deficits = CaptureDemandDeficits(dirtyDemandMaterials);
                foreach (var material in dirtyDemandMaterials.ToArray())
                {
                    RefreshWantedMaterial(material, deficits);
                }
                dirtyDemandMaterials.Clear();
                demandCachePartialRefreshes++;
                plannerDemandCacheTiming.Record(cacheStarted);
                plannerDemandCachePartialTiming.Record(cacheStarted);
            }

            var goalCacheStarted = Stopwatch.GetTimestamp();
            RefreshGoalCache(forceFull);
            if (forceFull)
            {
                QueueAllGoalRouteProbes();
            }
            plannerGoalCacheTiming.Record(goalCacheStarted);
        }

        // Purpose: Updates unmet Production Goal counts from the dirty product workset.
        private void RefreshGoalCache(bool forceFull)
        {
            if (forceFull || goalCacheDirty)
            {
                cachedGlobalGoalProducts.Clear();
                foreach (var product in EligibleTargetProducts())
                {
                    UpdateCachedGoalProduct(product);
                }
                goalCacheDirty = false;
                goalOrderDirty = true;
                goalCacheFullRebuilds++;
            }
            else if (dirtyPlanningProducts.Count > 0)
            {
                foreach (var product in dirtyPlanningProducts.ToArray())
                {
                    UpdateCachedGoalProduct(product);
                }
                goalCachePartialRefreshes++;
            }

            dirtyPlanningProducts.Clear();
            PruneGoalRouteState();
            if (!goalOrderDirty)
            {
                return;
            }
            cachedOrderedGoalProducts.Clear();
            cachedOrderedGoalProducts.AddRange(cachedGlobalGoalProducts.Keys
                .OrderByDescending(GetProductionPriority)
                .ThenBy(TargetKey, StringComparer.Ordinal));
            goalOrderDirty = false;
        }

        // Purpose: Recomputes one configured goal and updates index membership.
        private void UpdateCachedGoalProduct(Def product)
        {
            if (product == null)
            {
                return;
            }
            var target = GetProductionTarget(product);
            var recipe = target > 0
                && IsProductUnlockedForAutomation(product)
                ? AutoFactoryRecipeRegistry.FindAnyRecipe(product) : null;
            var output = Math.Max(1, recipe?.OutputMultiplier ?? 1);
            var shortfall = target <= 0 ? 0 : Math.Max(0,
                target - GetInventoryCount(product) - PendingForProduct(product));
            var wanted = (shortfall + output - 1) / output;
            var wasPresent = cachedGlobalGoalProducts.ContainsKey(product);
            if (wanted > 0 && recipe != null)
            {
                cachedGlobalGoalProducts[product] = wanted;
                if (!wasPresent)
                {
                    ForgetGoalRouteState(product);
                    goalOrderDirty = true;
                }
            }
            else if (cachedGlobalGoalProducts.Remove(product))
            {
                ForgetGoalRouteState(product);
                goalOrderDirty = true;
            }
        }

        // Purpose: Removes negative-route state for roots that are no longer unmet goals.
        private void PruneGoalRouteState()
        {
            foreach (var root in unavailableGoalRoutes.Keys.Where(root =>
                         !cachedGlobalGoalProducts.ContainsKey(root)).ToArray())
            {
                ForgetGoalRouteState(root);
            }
            pendingGoalRouteProbes.RemoveWhere(root =>
                !cachedGlobalGoalProducts.ContainsKey(root));
        }

        // Purpose: Forgets one goal's cached failure without affecting dependent roots.
        private void ForgetGoalRouteState(Def root)
        {
            if (root != null && unavailableGoalRoutes.Remove(root))
            {
                goalRouteInvalidations++;
            }
            if (root != null)
            {
                pendingGoalRouteProbes.Remove(root);
            }
        }

        // Purpose: Preserves priority order while randomizing equal-priority goals per pass.
        private IEnumerable<Def> GoalSchedulingOrder(Random random)
        {
            foreach (var priorityGroup in cachedOrderedGoalProducts
                         .GroupBy(GetProductionPriority))
            {
                foreach (var product in priorityGroup
                             .OrderBy(_ => random.Next()))
                {
                    yield return product;
                }
            }
        }

        // Purpose: Aggregates active deficit holders once for every demand-cache refresh.
        private DemandDeficitSnapshot CaptureDemandDeficits(
            ISet<MatType> includedMaterials = null)
        {
            var result = new DemandDeficitSnapshot();
            foreach (var deficit in activeDeficits)
            {
                if (deficit?.DeficitType == null
                    || (includedMaterials != null
                        && !includedMaterials.Contains(deficit.DeficitType))
                    || !IsAutomatableDeficit(deficit))
                {
                    continue;
                }
                result.AddGlobal(deficit.DeficitType);
                if (deficit.Entity != null)
                {
                    var roomId = S.Sys.Areas.At(deficit.Entity.PosIdx);
                    if (roomId > 0)
                    {
                        result.AddRoom(roomId, deficit.DeficitType);
                    }
                }
            }
            return result;
        }

        // Purpose: Recomputes one changed material across existing scheduler domains.
        private void RefreshWantedMaterial(MatType material,
            DemandDeficitSnapshot deficits)
        {
            if (material == null)
            {
                return;
            }

            var isEligibleTarget = cachedEligibleTargetMaterials.Contains(
                material);
            foreach (var domain in cachedDomains)
            {
                if (!cachedWantedByDomain.TryGetValue(domain.Key,
                        out var wantedMaterials))
                {
                    wantedMaterials = new Dictionary<MatType, int>();
                    cachedWantedByDomain[domain.Key] = wantedMaterials;
                }
                var crafter = domain.Value.FirstOrDefault();
                if (!isEligibleTarget || crafter == null)
                {
                    wantedMaterials.Remove(material);
                    continue;
                }

                var roomScoped = GetFactoryScope(crafter)
                    == AutoFactoryScope.Room;
                var roomId = roomScoped ? GetFactoryRoomId(crafter) : 0;
                var recipe = roomScoped && roomId <= 0
                    ? null : AutoFactoryRecipeRegistry.FindRecipe(
                        material, crafter);
                if (recipe == null)
                {
                    wantedMaterials.Remove(material);
                    continue;
                }

                var lackingOrders = deficits.Count(roomScoped, roomId,
                    material);
                var pending = PendingForScope(roomScoped, roomId, material);
                var output = Math.Max(1, recipe.OutputMultiplier);
                lackingOrders = Math.Max(0, lackingOrders
                    - (pending + output - 1) / output);
                if (lackingOrders > 0)
                {
                    wantedMaterials[material] = lackingOrders;
                }
                else
                {
                    wantedMaterials.Remove(material);
                }
            }
        }

        // Purpose: Calculates outstanding material-demand slots for one factory's scope.
        private Dictionary<MatType, int> BuildWantedMaterials(
            MultiCrafterComp crafter, DemandDeficitSnapshot deficits)
        {
            var roomScoped = GetFactoryScope(crafter) == AutoFactoryScope.Room;
            var roomId = roomScoped ? GetFactoryRoomId(crafter) : 0;
            if (roomScoped && roomId <= 0)
            {
                return new Dictionary<MatType, int>();
            }

            var wantedMaterials = new Dictionary<MatType, int>();
            foreach (var material in cachedEligibleTargetMaterials)
            {
                var recipe = AutoFactoryRecipeRegistry.FindRecipe(material,
                    crafter);
                if (recipe == null)
                {
                    continue;
                }
                var lackingOrders = deficits.Count(roomScoped, roomId,
                    material);
                var pending = PendingForScope(roomScoped, roomId, material);
                var output = Math.Max(1, recipe?.OutputMultiplier ?? 1);
                lackingOrders = Math.Max(0, lackingOrders
                    - (pending + output - 1) / output);
                if (lackingOrders > 0)
                {
                    wantedMaterials[material] = lackingOrders;
                }
            }
            return wantedMaterials;
        }

        // Purpose: Returns undelivered production credit for any goal product.
        private int PendingForProduct(Def product)
        {
            if (product?.MatType is MatType material)
            {
                return PendingForScope(false, 0, material);
            }
            if (product?.IsBeing == true)
            {
                return pendingBeingDeliveries.TryGetValue(product,
                    out var beingPending) ? beingPending : 0;
            }
            return product != null
                && pendingObjectDeliveries.TryGetValue(product, out var pending)
                ? pending : 0;
        }

        // Purpose: Returns undelivered material credit for the requested Global or Room scope.
        private int PendingForScope(bool roomScoped, int roomId, MatType material)
        {
            if (!roomScoped)
            {
                return pendingDeliveries.TryGetValue(material, out var pending)
                    ? pending : 0;
            }
            return roomPendingDeliveries.TryGetValue(
                (roomId, material), out var roomPending) ? roomPending : 0;
        }

        // Purpose: Loads ship-wide unreserved ingredients for availability-aware recipe selection.
        private void LoadAvailableMaterials(Dictionary<MatType, int> result)
        {
            // Scope controls which lacking-material demand a factory owns.
            // Ingredients remain ship-wide so the native hauling system can
            // supply a Room-scoped factory from storage outside its room.
            S.Sys.Inventory.LoadRemainingMaterials(result);
        }

        // Purpose: Counts available material stored inside one enclosed room.
        private int GetRoomInventoryCount(int roomId, MatType material)
        {
            var inventory = S?.Sys?.Inventory;
            var areas = S?.Sys?.Areas;
            var storages = inventory?.Storages;
            if (roomId <= 0 || material == null
                || storages == null
                || areas == null
                || !areas.AreaTypes.IsCreated)
            {
                return 0;
            }
            var count = 0;
            foreach (var storage in storages)
            {
                // A storage can be detached while Core rebuilds inventory state.
                // Snapshot Entity once so a second lifecycle-sensitive lookup
                // cannot turn null between the guard and the position read.
                var entity = storage?.Entity;
                var posIdx = entity?.PosIdx ?? -1;
                if (posIdx >= 0 && posIdx < areas.AreaTypes.Length
                    && areas.At(posIdx) == roomId)
                {
                    count += storage.AvailableWithoutReservation(material);
                }
            }
            return count;
        }

        // Purpose: Resolves room-local deliveries and refreshes their inventory baseline.
        private bool RefreshRoomPendingDeliveryAccounting()
        {
            // Core can send inventory signals while AfterLoadState is still
            // rebuilding storage entities. Preserve the saved room baseline
            // until the state is fully loaded so reconstructed stock cannot be
            // mistaken for a new delivery and room lookup remains lifecycle-safe.
            if (S?.IsLoaded != true)
            {
                return false;
            }

            var changed = false;
            foreach (var pair in roomPendingDeliveries.ToArray())
            {
                lastRoomInventoryCounts.TryGetValue(pair.Key,
                    out var previous);
                var current = GetRoomInventoryCount(pair.Key.RoomId,
                    pair.Key.Material);
                var delivered = Math.Max(0, current - previous);
                if (delivered <= 0)
                {
                    continue;
                }
                var remaining = pair.Value - delivered;
                if (remaining > 0)
                {
                    roomPendingDeliveries[pair.Key] = remaining;
                }
                else
                {
                    roomPendingDeliveries.Remove(pair.Key);
                }
                changed = true;
                TrackDirtyProduct(pair.Key.Material.Def);
                TrackDirtyDemandMaterial(pair.Key.Material);
            }
            RefreshRoomInventoryCounts();
            return changed;
        }

        // Purpose: Refreshes the room-local inventory baseline used by pending-delivery accounting.
        private void RefreshRoomInventoryCounts()
        {
            var areas = S?.Sys?.Areas;
            if (S?.IsLoaded != true
                || S?.Sys?.Inventory?.Storages == null
                || areas == null
                || !areas.AreaTypes.IsCreated)
            {
                return;
            }
            lastRoomInventoryCounts.Clear();
            foreach (var roomId in automatedFactories
                         .Where(crafter => GetFactoryScope(crafter)
                             == AutoFactoryScope.Room)
                         .Select(GetFactoryRoomId)
                         .Where(id => id > 0)
                         .Distinct())
            {
                foreach (var material in EligibleTargetProducts()
                             .Select(product => product.MatType)
                             .Where(material => material != null))
                {
                    lastRoomInventoryCounts[(roomId, material)] =
                        GetRoomInventoryCount(roomId, material);
                }
            }
        }

        // Purpose: Finds an ingredient-available eligible recipe for a product and factory type.
        private Craftable FindAvailableRecipe(
            Def product,
            MultiCrafterComp crafter,
            Dictionary<MatType, int> availableMaterials,
            Random random)
        {
            return SelectAvailableRecipe(
                AutoFactoryRecipeRegistry.FindRecipes(product, crafter),
                availableMaterials,
                random);
        }

        // Purpose: Finds an ingredient-available eligible recipe for an explicit recipe type.
        private Craftable FindAvailableRecipeForType(
            Def product,
            string recipeType,
            Dictionary<MatType, int> availableMaterials,
            Random random)
        {
            return SelectAvailableRecipe(
                AutoFactoryRecipeRegistry.FindRecipesForType(product,
                    recipeType),
                availableMaterials,
                random);
        }

        // Purpose: Selects among equal-best routes whose full ingredient sets are available.
        private Craftable SelectAvailableRecipe(
            IEnumerable<Craftable> recipes,
            Dictionary<MatType, int> availableMaterials,
            Random random)
        {
            var availableRecipes = recipes
                .Where(IsRecipeUnlockedForAutomation)
                .Where(recipe => HasAllIngredients(recipe,
                    availableMaterials))
                .ToArray();
            if (availableRecipes.Length == 0)
            {
                return null;
            }
            return availableRecipes.Length == 1 || random == null
                ? availableRecipes[0]
                : availableRecipes[random.Next(availableRecipes.Length)];
        }

        // Purpose: Randomizes equivalent route traversal when a scheduler randomizer is available.
        private static IEnumerable<Craftable> ShuffledRecipes(
            IEnumerable<Craftable> recipes,
            Random random)
        {
            return random == null
                ? recipes
                : recipes.OrderBy(_ => random.Next());
        }

        // Purpose: Verifies that the availability snapshot covers every grouped ingredient.
        private static bool HasAllIngredients(
            Craftable recipe,
            Dictionary<MatType, int> availableMaterials)
        {
            if (recipe?.Ingredients == null)
            {
                return false;
            }

            foreach (var required in recipe.Ingredients
                .Where(ingredient => ingredient.Type != null)
                .GroupBy(ingredient => ingredient.Type)
                .Select(group => new
                {
                    Type = group.Key,
                    Amount = group.Sum(ingredient => ingredient.MaxStackSize)
                }))
            {
                if (!availableMaterials.TryGetValue(required.Type, out var available)
                    || available < required.Amount)
                {
                    return false;
                }
            }
            return true;
        }

        // Purpose: Reserves an assigned recipe's ingredients in the current scheduling snapshot.
        private static void ReserveIngredients(
            Craftable recipe,
            Dictionary<MatType, int> availableMaterials)
        {
            foreach (var required in recipe.Ingredients
                .Where(ingredient => ingredient.Type != null)
                .GroupBy(ingredient => ingredient.Type)
                .Select(group => new
                {
                    Type = group.Key,
                    Amount = group.Sum(ingredient => ingredient.MaxStackSize)
                }))
            {
                if (availableMaterials.TryGetValue(required.Type, out var available))
                {
                    availableMaterials[required.Type] = Math.Max(0,
                        available - required.Amount);
                }
            }
        }

        private sealed class DiagnosticTiming
        {
            private readonly string name;
            private long calls;
            private long totalTimestampTicks;
            private long maximumTimestampTicks;

            // Purpose: Names one measured Auto Factory phase in aggregate reports.
            public DiagnosticTiming(string name)
            {
                this.name = name;
            }

            // Purpose: Records one elapsed interval from a Stopwatch timestamp.
            public void Record(long started)
            {
                var elapsed = Math.Max(0,
                    Stopwatch.GetTimestamp() - started);
                calls++;
                totalTimestampTicks += elapsed;
                maximumTimestampTicks = Math.Max(maximumTimestampTicks,
                    elapsed);
            }

            // Purpose: Formats and resets one reporting window.
            public string SnapshotAndReset()
            {
                var totalMilliseconds = ToMilliseconds(totalTimestampTicks);
                var averageMilliseconds = calls == 0
                    ? 0d : totalMilliseconds / calls;
                var maximumMilliseconds = ToMilliseconds(
                    maximumTimestampTicks);
                var result = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0} calls={1} total={2:F3}ms avg={3:F3}ms max={4:F3}ms",
                    name,
                    calls,
                    totalMilliseconds,
                    averageMilliseconds,
                    maximumMilliseconds);
                Reset();
                return result;
            }

            // Purpose: Clears accumulated timing without changing game state.
            public void Reset()
            {
                calls = 0;
                totalTimestampTicks = 0;
                maximumTimestampTicks = 0;
            }

            // Purpose: Converts Stopwatch timestamp units to milliseconds.
            private static double ToMilliseconds(long timestampTicks)
            {
                return timestampTicks * 1000d / Stopwatch.Frequency;
            }
        }

        private readonly struct GoalFacilityAvailability
        {
            public readonly int Fabricators;
            public readonly int Foundries;
            public readonly int CloningPods;
            public readonly long DomainSignature;

            private GoalFacilityAvailability(int fabricators, int foundries,
                int cloningPods, long domainSignature)
            {
                Fabricators = fabricators;
                Foundries = foundries;
                CloningPods = cloningPods;
                DomainSignature = domainSignature;
            }

            // Purpose: Captures free capacity by recipe family, not facility identity.
            public static GoalFacilityAvailability Capture(
                IEnumerable<MultiCrafterComp> crafters,
                Func<MultiCrafterComp, int> domainKey)
            {
                var snapshot = crafters.ToArray();
                var fabricators = 0;
                var foundries = 0;
                var cloningPods = 0;
                foreach (var crafter in snapshot)
                {
                    if (IsNanoFoundry(crafter))
                    {
                        foundries++;
                    }
                    else if (IsNanoCloningPod(crafter))
                    {
                        cloningPods++;
                    }
                    else if (IsNanoFabricator(crafter))
                    {
                        fabricators++;
                    }
                }
                long domainSignature = 17;
                foreach (var domain in snapshot.GroupBy(domainKey)
                             .OrderBy(group => group.Key))
                {
                    unchecked
                    {
                        domainSignature = domainSignature * 31 + domain.Key;
                        domainSignature = domainSignature * 31
                            + domain.Count();
                    }
                }
                return new GoalFacilityAvailability(fabricators, foundries,
                    cloningPods, domainSignature);
            }

            // Purpose: Retries when free family or Room/Global capability changed.
            public bool HasMeaningfulChangeSince(
                GoalFacilityAvailability previous)
            {
                return Fabricators > previous.Fabricators
                    || Foundries > previous.Foundries
                    || CloningPods > previous.CloningPods
                    || DomainSignature != previous.DomainSignature;
            }
        }

        private sealed class GoalRouteFailure
        {
            public GoalFacilityAvailability Availability { get; }

            public GoalRouteFailure(GoalFacilityAvailability availability)
            {
                Availability = availability;
            }
        }

        private sealed class CrossFacilityDependency
        {
            public MultiCrafterComp Requester;
            public Def Root;
            public MatType Material;
            public string ProviderRecipeType;
            public bool ProductionGoal;
        }

        private sealed class ExternalFacilityBoundary
        {
            public MatType Material;
            public string ProviderRecipeType;
        }

        private sealed class DemandDeficitSnapshot
        {
            private readonly Dictionary<MatType, int> globalCounts = new();
            private readonly Dictionary<(int RoomId, MatType Material), int>
                roomCounts = new();

            // Purpose: Records one automatable ship-wide deficit holder.
            public void AddGlobal(MatType material)
            {
                if (material == null)
                {
                    return;
                }
                globalCounts.TryGetValue(material, out var count);
                globalCounts[material] = count + 1;
            }

            // Purpose: Records one automatable deficit holder inside an enclosed room.
            public void AddRoom(int roomId, MatType material)
            {
                if (roomId <= 0 || material == null)
                {
                    return;
                }
                var key = (roomId, material);
                roomCounts.TryGetValue(key, out var count);
                roomCounts[key] = count + 1;
            }

            // Purpose: Returns the exact Global or Room-scoped deficit count.
            public int Count(bool roomScoped, int roomId, MatType material)
            {
                if (material == null)
                {
                    return 0;
                }
                if (roomScoped)
                {
                    return roomCounts.TryGetValue((roomId, material),
                        out var roomCount) ? roomCount : 0;
                }
                return globalCounts.TryGetValue(material, out var globalCount)
                    ? globalCount : 0;
            }
        }

        // Purpose: Hands missing intermediaries to compatible idle managed facilities.
        private void ScheduleCrossFacilityDependencies(
            Dictionary<int, List<MultiCrafterComp>> domains,
            Dictionary<int, Dictionary<MatType, int>> wantedByDomain,
            Dictionary<Def, int> globalGoalProducts,
            bool includeLackingMaterialRequests = true,
            Dictionary<MatType, int> available = null,
            Random random = null,
            List<MultiCrafterComp> sharedFreeProviders = null)
        {
            if (available == null)
            {
                available = new Dictionary<MatType, int>();
                LoadAvailableMaterials(available);
            }
            random ??= new Random(unchecked((int)G.Ticks));
            var freeProviders = sharedFreeProviders
                ?? domains.Values.SelectMany(value => value)
                    .Where(IsAutomationLocationValid)
                    .Where(crafter => !automatedOrders.ContainsKey(crafter))
                    .Where(crafter => crafter.Orders == null
                        || crafter.Orders.Count == 0)
                    .ToList();
            if (freeProviders.Count == 0)
            {
                return;
            }

            var requests = new List<CrossFacilityDependency>();
            foreach (var domain in domains.Where(pair =>
                         includeLackingMaterialRequests
                         && pair.Value.Any(crafter => IsNanoFabricator(crafter)
                             || IsNanoCloningPod(crafter))))
            {
                var requester = domain.Value.First(crafter =>
                    IsNanoFabricator(crafter) || IsNanoCloningPod(crafter));
                foreach (var root in wantedByDomain[domain.Key].Keys)
                {
                    AddCrossFacilityRequest(requests, requester, root.Def,
                        productionGoal: false, available, random);
                }
            }
            foreach (var root in globalGoalProducts.Keys
                         .OrderByDescending(GetProductionPriority)
                         .ThenBy(_ => random.Next()))
            {
                var requester = domains.Values.SelectMany(value => value)
                    .Where(IsAutomationLocationValid)
                    .Where(crafter => IsNanoFabricator(crafter)
                        || IsNanoCloningPod(crafter))
                    .FirstOrDefault(crafter => AutoFactoryRecipeRegistry
                        .FindRecipe(root, crafter) != null);
                if (requester != null)
                {
                    AddCrossFacilityRequest(requests, requester, root,
                        productionGoal: true, available, random);
                }
            }

            foreach (var request in requests)
            {
                if (freeProviders.Count == 0)
                {
                    break;
                }
                var accepting = SelectAcceptingProvider(request.Requester,
                    request.ProviderRecipeType, freeProviders);
                if (accepting == null)
                {
                    continue;
                }
                var recipe = FindAvailableRecipeForType(
                    request.Material.Def,
                    request.ProviderRecipeType,
                    available,
                    random);
                if (recipe == null)
                {
                    continue;
                }
                if (CreateCrossFacilityOrder(accepting, request, recipe))
                {
                    ReserveIngredients(recipe, available);
                    freeProviders.Remove(accepting);
                }
            }
        }

        // Purpose: Adds one deduplicated cross-facility dependency request for a root product.
        private void AddCrossFacilityRequest(
            List<CrossFacilityDependency> requests,
            MultiCrafterComp requester,
            Def root,
            bool productionGoal,
            Dictionary<MatType, int> available,
            Random random)
        {
            if (!IsProductUnlockedForAutomation(root))
            {
                return;
            }
            var visited = new HashSet<MatType>();
            if (root?.MatType != null)
            {
                visited.Add(root.MatType);
            }
            var candidate = FindExternalFacilityBoundary(root, root,
                AutoFactoryRecipeRegistry.RecipeTypeFor(requester),
                available, visited, random);
            if (candidate == null
                || requests.Any(existing => existing.Material
                        == candidate.Material
                    && existing.ProviderRecipeType
                        == candidate.ProviderRecipeType))
            {
                return;
            }
            requests.Add(new CrossFacilityDependency
            {
                Requester = requester,
                Root = root,
                Material = candidate.Material,
                ProviderRecipeType = candidate.ProviderRecipeType,
                ProductionGoal = productionGoal
            });
        }

        // Purpose: Walks one facility recipe chain to its first workable external boundary.
        private ExternalFacilityBoundary FindExternalFacilityBoundary(
            Def root, Def product, string requesterRecipeType,
            Dictionary<MatType, int> available,
            HashSet<MatType> visited, Random random)
        {
            if (!IsProductUnlockedForAutomation(product))
            {
                return null;
            }
            var recipes = AutoFactoryRecipeRegistry.FindRecipesForType(product,
                    requesterRecipeType)
                .Where(IsRecipeUnlockedForAutomation)
                .ToArray();
            if (recipes.Length == 0)
            {
                return null;
            }
            foreach (var recipe in ShuffledRecipes(recipes, random))
            {
                if (recipe?.Ingredients == null)
                {
                    continue;
                }
                foreach (var requirement in recipe.Ingredients
                             .Where(ingredient => ingredient.Type != null)
                             .GroupBy(ingredient => ingredient.Type)
                             .Select(group => new
                             {
                                 Type = group.Key,
                                 Amount = group.Sum(item => item.MaxStackSize)
                             }))
                {
                    available.TryGetValue(requirement.Type, out var owned);
                    pendingDeliveries.TryGetValue(requirement.Type,
                        out var pending);
                    if (owned + pending >= requirement.Amount)
                    {
                        continue;
                    }
                    var branchVisited = new HashSet<MatType>(visited);
                    if (!branchVisited.Add(requirement.Type))
                    {
                        continue;
                    }
                    foreach (var providerType in ExternalProviderTypesFor(
                                 requesterRecipeType))
                    {
                        var providerRecipes = AutoFactoryRecipeRegistry
                            .FindRecipesForType(requirement.Type.Def,
                                providerType);
                        if (providerRecipes.Any(providerRecipe =>
                                IsRecipeUnlockedForAutomation(providerRecipe)
                                && HasAllIngredients(providerRecipe, available)))
                        {
                            return new ExternalFacilityBoundary
                            {
                                Material = requirement.Type,
                                ProviderRecipeType = providerType
                            };
                        }
                    }
                    var nested = FindExternalFacilityBoundary(root,
                        requirement.Type.Def,
                        requesterRecipeType,
                        available,
                        branchVisited,
                        random);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }
            return null;
        }

        // Purpose: Lists provider families allowed to satisfy a requester's missing input.
        private static IEnumerable<string> ExternalProviderTypesFor(
            string requesterRecipeType)
        {
            if (requesterRecipeType == AutoFactoryRecipeRegistry
                    .NanoFabricatorType)
            {
                yield return AutoFactoryRecipeRegistry.NanoFoundryType;
                yield break;
            }
            if (requesterRecipeType == AutoFactoryRecipeRegistry
                    .NanoCloningType)
            {
                yield return AutoFactoryRecipeRegistry.NanoFabricatorType;
                yield return AutoFactoryRecipeRegistry.NanoFoundryType;
            }
        }

        // Purpose: Chooses a compatible provider while honoring Room preference when possible.
        private MultiCrafterComp SelectAcceptingProvider(
            MultiCrafterComp requester, string providerRecipeType,
            List<MultiCrafterComp> candidates)
        {
            var compatible = candidates.Where(candidate =>
                AutoFactoryRecipeRegistry.RecipeTypeFor(candidate)
                    == providerRecipeType).ToList();
            if (GetFactoryScope(requester) == AutoFactoryScope.Room)
            {
                var room = GetFactoryRoomId(requester);
                return compatible.FirstOrDefault(provider =>
                           GetFactoryScope(provider) == AutoFactoryScope.Room
                           && GetFactoryRoomId(provider) == room)
                    ?? compatible.FirstOrDefault(provider =>
                        GetFactoryScope(provider) == AutoFactoryScope.Global);
            }
            return compatible.FirstOrDefault(provider =>
                       GetFactoryScope(provider) == AutoFactoryScope.Global)
                ?? compatible.FirstOrDefault();
        }

        // Purpose: Creates and records a one-cycle order owned by another facility's chain.
        private bool CreateCrossFacilityOrder(MultiCrafterComp accepting,
            CrossFacilityDependency dependency,
            Craftable recipe)
        {
            if (accepting == null
                || recipe == null
                || recipe.Type != dependency?.ProviderRecipeType
                || AutoFactoryRecipeRegistry.RecipeTypeFor(accepting)
                    != dependency.ProviderRecipeType
                || recipe.ProductDef != dependency?.Material?.Def
                || !IsRecipeUnlockedForAutomation(recipe)
                || !IsProductUnlockedForAutomation(dependency.Root))
            {
                return false;
            }
            var demand = CraftingDemand.CreateFor(recipe);
            demand.Type = CraftingDemandType.One;
            demand.AmountWanted = 1;
            demand.AmountProduced = 0;
            demand.AmountProducedThis = 0;
            var order = S.Query.CreateCraftingOrder.Ask(accepting, demand);
            if (order == null)
            {
                return false;
            }
            // CreateCraftingOrder is Core's authoritative order path, but unlike
            // MultiCrafterUI.StartProducing it does not clear Unconfigured.
            accepting.UI?.MarkConfigured();
            automatedOrders[accepting] = order;
            TrackDirtyFactory(accepting);
            fallbackOrderRoots[order] = dependency.Root;
            crossFacilityOrders[order] = dependency;
            var requesterName = FactoryDisplayName(dependency.Requester);
            var acceptingName = FactoryDisplayName(accepting);
            LogActivity(dependency.Requester,
                "Handed off " + dependency.Material.NameT + " for "
                + dependency.Root.NameT + " to " + acceptingName + ".",
                AutoFactoryLogCategory.CrossFacility);
            LogActivity(accepting,
                "Accepted " + dependency.Material.NameT + " hand-off from "
                + requesterName + " for " + dependency.Root.NameT + ".",
                AutoFactoryLogCategory.CrossFacility);
            return true;
        }

        // Purpose: Confirms that a tracked handoff still serves active shortage or goal demand.
        private bool IsCrossFacilityDemandStillValid(
            CrossFacilityDependency dependency,
            Dictionary<int, Dictionary<MatType, int>> wantedByDomain,
            Dictionary<Def, int> globalGoalProducts)
        {
            if (dependency?.Requester == null
                || !automatedFactories.Contains(dependency.Requester)
                || !IsAutomationLocationValid(dependency.Requester))
            {
                return false;
            }
            if (dependency.ProductionGoal)
            {
                return globalGoalProducts.ContainsKey(dependency.Root);
            }
            var domain = ScopeDomainKey(dependency.Requester);
            return wantedByDomain.TryGetValue(domain, out var wanted)
                && dependency.Root?.MatType != null
                && wanted.ContainsKey(dependency.Root.MatType);
        }

        private sealed class IntermediateCandidate
        {
            public Def Root;
            public MatType Material;
            public Craftable Recipe;
        }

        // Purpose: Searches root products for the first ingredient intermediate craftable now.
        private IntermediateCandidate FindFirstWorkableIntermediate(
            IEnumerable<Def> rootProducts,
            Dictionary<MatType, int> availableMaterials,
            MultiCrafterComp crafter,
            Random random)
        {
            foreach (var root in rootProducts)
            {
                var visited = new HashSet<MatType>();
                if (root?.MatType != null)
                {
                    visited.Add(root.MatType);
                }
                var candidate = FindFirstWorkableIntermediate(
                    root,
                    root,
                    availableMaterials,
                    visited,
                    crafter,
                    random);
                if (candidate != null)
                {
                    return candidate;
                }
            }
            return new IntermediateCandidate();
        }

        // Purpose: Recursively walks one recipe chain to find a craftable missing intermediate.
        private IntermediateCandidate FindFirstWorkableIntermediate(
            Def root,
            Def product,
            Dictionary<MatType, int> availableMaterials,
            HashSet<MatType> visited,
            MultiCrafterComp crafter,
            Random random)
        {
            if (!IsProductUnlockedForAutomation(product))
            {
                return null;
            }
            var productRecipes = AutoFactoryRecipeRegistry.FindRecipes(product,
                    crafter)
                .Where(IsRecipeUnlockedForAutomation)
                .ToArray();
            if (productRecipes.Length == 0)
            {
                return null;
            }

            foreach (var productRecipe in ShuffledRecipes(productRecipes,
                         random))
            {
                if (productRecipe?.Ingredients == null)
                {
                    continue;
                }
                var requirements = productRecipe.Ingredients
                    .Where(ingredient => ingredient.Type != null)
                    .GroupBy(ingredient => ingredient.Type)
                    .Select(group => new
                    {
                        Type = group.Key,
                        Amount = group.Sum(ingredient =>
                            ingredient.MaxStackSize)
                    });

                foreach (var requirement in requirements)
                {
                    availableMaterials.TryGetValue(requirement.Type,
                        out var available);
                    pendingDeliveries.TryGetValue(requirement.Type,
                        out var pending);
                    if (available + pending >= requirement.Amount)
                    {
                        continue;
                    }
                    var branchVisited = new HashSet<MatType>(visited);
                    if (!branchVisited.Add(requirement.Type))
                    {
                        continue;
                    }

                    var ingredientRecipes = AutoFactoryRecipeRegistry
                        .FindRecipes(requirement.Type.Def, crafter);
                    if (ingredientRecipes.Length == 0)
                    {
                        continue;
                    }
                    var ingredientRecipe = SelectAvailableRecipe(
                        ingredientRecipes,
                        availableMaterials,
                        random);
                    if (ingredientRecipe != null)
                    {
                        return new IntermediateCandidate
                        {
                            Root = root,
                            Material = requirement.Type,
                            Recipe = ingredientRecipe
                        };
                    }

                    var nested = FindFirstWorkableIntermediate(
                        root,
                        requirement.Type.Def,
                        availableMaterials,
                        branchVisited,
                        crafter,
                        random);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }
            return null;
        }

        // Purpose: Creates and classifies one automated direct, fallback, or Production Goal order.
        private bool CreateOneOrder(
            MultiCrafterComp crafter,
            Def product,
            Craftable recipe,
            Def fallbackRoot = null,
            bool productionGoal = false)
        {
            if (crafter == null
                || recipe == null
                || recipe.ProductDef != product
                || recipe.Type != AutoFactoryRecipeRegistry.RecipeTypeFor(crafter)
                || !IsRecipeUnlockedForAutomation(recipe)
                || (fallbackRoot != null
                    && !IsProductUnlockedForAutomation(fallbackRoot)))
            {
                return false;
            }
            var demand = CraftingDemand.CreateFor(recipe);
            demand.Type = CraftingDemandType.One;
            demand.AmountWanted = 1;
            demand.AmountProduced = 0;
            demand.AmountProducedThis = 0;
            var created = S.Query.CreateCraftingOrder.Ask(crafter, demand);
            if (created != null)
            {
                // Keep Core's warning lifecycle aligned with the real order
                // created through its lower-level authoritative query.
                crafter.UI?.MarkConfigured();
                automatedOrders[crafter] = created;
                TrackDirtyFactory(crafter);
                if (fallbackRoot != null)
                {
                    fallbackOrderRoots[created] = fallbackRoot;
                }
                if (productionGoal)
                {
                    productionGoalOrders.Add(created);
                }
                if (fallbackRoot == null)
                {
                    LogActivity(crafter,
                        DirectQueueReason(crafter, product, productionGoal));
                }
                else
                {
                    LogActivity(crafter,
                        IntermediaryQueueReason(crafter, product.MatType,
                            fallbackRoot, productionGoal));
                }
                return true;
            }
            return false;
        }

        // Purpose: Returns a readable material or object name for an order log message.
        private static string ProductName(CraftingOrder order)
        {
            return order?.Demand?.Craftable?.ProductDef?.MatType?.NameT
                ?? order?.Demand?.Craftable?.ProductDef?.NameT
                ?? "unknown product";
        }

        // Purpose: Returns the root requested product behind a direct or intermediary order.
        private Def RequestedProduct(CraftingOrder order)
        {
            return order != null
                && fallbackOrderRoots.TryGetValue(order, out var root)
                ? root
                : order?.Demand?.Craftable?.ProductDef;
        }

        // Purpose: Mirrors Core's native production research gate for one product.
        private bool IsProductUnlockedForAutomation(Def product)
        {
            return product != null
                && S?.Research?.IsDefUnlocked(product) == true;
        }

        // Purpose: Mirrors Core's native production research gate for one recipe.
        private bool IsRecipeUnlockedForAutomation(Craftable recipe)
        {
            return IsProductUnlockedForAutomation(recipe?.ProductDef);
        }

        // Purpose: Builds the scope-aware log explanation for a direct automated order.
        private string DirectQueueReason(MultiCrafterComp crafter,
            Def product, bool productionGoal)
        {
            var scope = GetFactoryScope(crafter);
            var roomId = scope == AutoFactoryScope.Room
                ? GetFactoryRoomId(crafter) : 0;
            var target = GetProductionTarget(product);
            if (productionGoal && target > 0)
            {
                var owned = GetInventoryCount(product);
                return "Queued " + product.NameT + " for production goal; owned "
                    + owned + ", target " + target + ", priority "
                    + GetProductionPriority(product) + " globally.";
            }

            var material = product?.MatType;
            var requests = activeDeficits.Count(deficit =>
                IsAutomatableDeficit(deficit) && deficit.DeficitType == material
                && (scope == AutoFactoryScope.Global
                    || (deficit.Entity != null
                        && S.Sys.Areas.At(deficit.Entity.PosIdx) == roomId)));
            return "Queued " + material.NameT + " for "
                + (scope == AutoFactoryScope.Room
                    ? "Room #" + roomId + " lacking-material demand"
                    : "global lacking-material demand")
                + (requests > 0 ? " (" + requests + " active request"
                    + (requests == 1 ? string.Empty : "s") + ")" : string.Empty)
                + ".";
        }

        // Purpose: Builds the log explanation for an intermediary automated order.
        private string IntermediaryQueueReason(MultiCrafterComp crafter,
            MatType material, Def root, bool productionGoal)
        {
            var scope = GetFactoryScope(crafter);
            var roomId = scope == AutoFactoryScope.Room
                ? GetFactoryRoomId(crafter) : 0;
            var target = GetProductionTarget(root);
            if (productionGoal && target > 0)
            {
                var owned = GetInventoryCount(root);
                return "Queued intermediary " + material.NameT + " for "
                    + root.NameT + " production goal; owned "
                    + owned + ", target " + target + ", priority "
                    + GetProductionPriority(root) + " globally.";
            }
            return "Queued intermediary " + material.NameT
                + " because lacking product " + root.NameT
                + " cannot yet be crafted.";
        }

        // Purpose: Builds the goal- or scope-aware explanation for an automated cancellation.
        private string CancellationReason(MultiCrafterComp crafter,
            CraftingOrder order)
        {
            var product = order?.Demand?.Craftable?.ProductDef;
            var requested = RequestedProduct(order);

            if (!IsProductUnlockedForAutomation(product)
                || !IsProductUnlockedForAutomation(requested))
            {
                var locked = !IsProductUnlockedForAutomation(requested)
                    ? requested : product;
                return "Cancelled " + ProductName(order) + ": "
                    + (locked?.NameT ?? "product")
                    + " is still locked by research.";
            }

            var target = GetProductionTarget(requested);
            if (productionGoalOrders.Contains(order) && target > 0)
            {
                var pending = PendingForProduct(requested);
                var owned = GetInventoryCount(requested);
                return "Cancelled " + ProductName(order) + ": " + requested.NameT
                    + " production goal is satisfied (owned "
                    + owned + ", pending " + pending
                    + ", target " + target + ").";
            }
            return "Cancelled " + ProductName(order) + ": no remaining "
                + (GetFactoryScope(crafter) == AutoFactoryScope.Room
                    ? "room demand" : "global demand")
                + (requested != null && requested != product
                    ? " for lacking product " + requested.NameT
                    : string.Empty) + ".";
        }

        // Purpose: Assigns unique default labels once to newly constructed managed factories.
        private void EnsureDefaultFactoryLabels()
        {
            foreach (var crafter in managedFactories.Where(crafter =>
                         crafter?.Entity != null
                         && crafter.Tile?.IsConstructed == true
                         && !defaultLabelInitializedFactoryIds.Contains(
                             crafter.Entity.Id)).ToArray())
            {
                var label = crafter.Tile.Label;
                if (label != null && string.IsNullOrWhiteSpace(label.Label))
                {
                    var prefix = IsNanoFoundry(crafter)
                        ? "Nano Foundry "
                        : IsNanoCloningPod(crafter)
                            ? "Nano Cloning Pod " : "Nano Fabricator ";
                    var number = IsNanoFoundry(crafter)
                        ? nextNanoFoundryLabel++
                        : IsNanoCloningPod(crafter)
                            ? nextNanoCloningPodLabel++
                            : nextNanoFabricatorLabel++;
                    while (managedFactories.Any(existing =>
                               existing?.Tile?.Label?.Label == prefix + number))
                    {
                        number = IsNanoFoundry(crafter)
                            ? nextNanoFoundryLabel++
                            : IsNanoCloningPod(crafter)
                                ? nextNanoCloningPodLabel++
                                : nextNanoFabricatorLabel++;
                    }
                    label.SetLabel(prefix + number);
                    crafter.S?.Sig?.LabelChanged.Send(label);
                }
                defaultLabelInitializedFactoryIds.Add(crafter.Entity.Id);
            }
        }

        // Purpose: Returns a factory's custom label or translated definition name for logs.
        private static string FactoryDisplayName(MultiCrafterComp crafter)
        {
            var label = crafter?.Tile?.Label?.Label;
            if (!string.IsNullOrWhiteSpace(label))
            {
                return label;
            }
            return crafter?.Tile?.Definition?.NameT ?? "Auto Factory facility";
        }

        // Purpose: Writes a color-coded Auto Factory activity entry to the facility log.
        internal static void LogActivity(MultiCrafterComp crafter, string message,
            AutoFactoryLogCategory category = AutoFactoryLogCategory.Information)
        {
            if (crafter?.Tile != null && !string.IsNullOrWhiteSpace(message))
            {
                var line = "Auto Factory: " + message;
                line = category switch
                {
                    AutoFactoryLogCategory.Completion => Texts.Green(line),
                    AutoFactoryLogCategory.Cancellation => Texts.Yellow(line),
                    AutoFactoryLogCategory.Issue => Texts.Red(line),
                    AutoFactoryLogCategory.Manual => Texts.WithColor(line, "#C084FC"),
                    AutoFactoryLogCategory.CrossFacility => Texts.WithColor(line, "#FF9F43"),
                    _ => Texts.Blue(line)
                };
                crafter.S?.Sys?.Log?.AddLine(crafter.Tile,
                    line, canRepeat: true);
            }
        }

        // Purpose: Rebuilds a factory's visible order list from current eligible Nano recipes.
        private static void RefreshCraftableCache(MultiCrafterComp crafter)
        {
            if (crafter?.UI == null)
            {
                return;
            }

            var recipes = new List<Craftable>();
            Craftable.ForCrafter(crafter.Settings.CompatibleTypes, recipes);
            recipes.RemoveAll(recipe =>
                !AutoFactoryRecipeRegistry.IsUsableNanoRecipe(recipe)
                || recipe.Type
                    != AutoFactoryRecipeRegistry.RecipeTypeFor(crafter));
            var field = AccessTools.Field(typeof(MultiCrafterUI), "allCraftables");
            field?.SetValue(crafter.UI, recipes);
        }
    }

    [HarmonyPatch(typeof(MultiCrafterUI),
        nameof(MultiCrafterUI.SetAutoCraftingAmountFor))]
    internal static class NanoFabricatorManualOrderPatch
    {
        // Purpose: Reclassifies a newly queued player order as a one-cycle manual override.
        private static void Postfix(CraftingDemand item,
            MultiCrafterComp ___crafter)
        {
            if (!AutoFactorySys.IsAutomatedFactory(___crafter)
                || item?.Craftable?.Type
                    != AutoFactoryRecipeRegistry.RecipeTypeFor(___crafter))
            {
                return;
            }

            var order = ___crafter.Orders?.LastOrDefault(candidate =>
                ReferenceEquals(candidate?.Demand, item));
            AutoFactorySys.Instance?.RegisterManualOrder(___crafter, order);
        }
    }

    [HarmonyPatch(typeof(MultiCrafterUI), nameof(MultiCrafterUI.StopProducing))]
    internal static class NanoFactoryOrderStoppedPatch
    {
        // Purpose: Reconciles after a player or Core removes a managed factory order.
        private static void Postfix(MultiCrafterComp ___crafter,
            CraftingOrder __0)
        {
            if (AutoFactorySys.IsAutomatedFactory(___crafter))
            {
                AutoFactorySys.Instance?.RegisterStoppedOrder(___crafter,
                    __0);
            }
        }
    }

    [HarmonyPatch(typeof(ProductionTab), "ToggleIngredients")]
    internal static class StaleNanoFabricatorOrderDetailsPatch
    {
        // Purpose: Prevents stale removed-order details from opening and refreshes the panel.
        private static bool Prefix(CraftingOrder order, ref bool __result)
        {
            var crafter = order?.Crafter;
            if (!AutoFactorySys.IsAutomatedFactory(crafter)
                || (order.Ingredients != null
                    && crafter.Orders?.Contains(order) == true))
            {
                return true;
            }

            __result = false;
            AutoFactorySys.Instance?.RefreshSelectedCrafterPanel(crafter);
            return false;
        }
    }
}
