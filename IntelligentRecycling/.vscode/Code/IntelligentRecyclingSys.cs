using System;
using System.Collections.Generic;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Systems.IntelligentRecycling {
    /// <summary>
    /// Creates vanilla disposal goals for eligible loose objects whose quality is
    /// at or below the configured threshold of a compatible disposer.
    /// </summary>
    public sealed partial class IntelligentRecyclingSys : GameSystem, ISaveable {
        public const string SysId = "IntelligentRecyclingSys";
        public const string ModId = "IntelligentRecycling";
        public const string DisplayName = "Intelligent Recycling";
        public const string ModVersion = "1.3.3";
        private const string ConsumerId = ModId;
        internal const string ArtilleryFungusDefinitionId =
            "Obj/Plants/ArtilleryFungus";
        public const string RequiredUtilitiesVersion = "1.4.0";
        private const string HarmonyId = "jleas.stardeus.intelligentrecycling.events";
        internal const long ScanIntervalTicks = 300;
        internal const int SaveSchemaVersion = 13;
        internal const float DefaultQualityThreshold = 0.5f;
        private readonly List<DisposerComp> disposers = new();
        private readonly List<StasisPodComp> stasisPods = new();
        private readonly List<BreachCapsuleComp> breachCapsules = new();
        private readonly List<ShuttleComp> shuttles = new();
        private readonly Dictionary<uint, uint> trackedGoalsByObjectId = new();
        private readonly Dictionary<uint, uint> trackedGoalsByBeingId = new();
        private readonly Dictionary<int, uint> trackedGoalsByStasisPodId = new();
        private readonly Dictionary<int, uint> trackedGoalsByBreachCapsuleId = new();
        private readonly Dictionary<string, LostShuttleReplacementRecord>
            lostShuttleReplacements = new();
        private readonly HashSet<int> suppressedLostShuttleEntityIds = new();
        private readonly HashSet<uint> pendingObjectScanIds = new();
        private readonly HashSet<uint> processingObjectScanIds = new();
        private readonly HashSet<string> disabledEquipmentSlotIds = new();
        private readonly HashSet<string> disabledBiologicalSlotIds = new();
        private readonly HashSet<string> disabledRoboticSlotIds = new();
        private readonly PartRestorationUnitBridge partRestorationUnit = new();
        private ComponentData saveData;
        private int loadedSettingsGameId = -1;
        private GameState loadedState;
        private ObjsSys loadedObjsSystem;
        private long nextScanTick;
        private long lastScanTick;
        private bool scanRequested;
        private bool trackedGoalsReady;
        private bool thresholdSettingsLoadedFromSave;
        private bool thresholdAuthorityReady;
        private bool synchronizingProcessorThresholds;
        private ITopBarWidgetHandle topWidget;
        private IShortcutHandle panelShortcut;
        private IntelligentRecyclingPanel panel;
        private static IntelligentRecyclingSys activeInstance;
        private static long apiRevision;
        private Harmony harmony;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        public bool Enabled = true;
        public bool RecycleBiologicalParts = true;
        public bool RecycleRoboticParts = true;
        public bool RecycleEquipment = false;
        public bool ReplaceStorageHaulTasks = false;
        public bool RecycleHullpiercers = true;
        public bool RecycleOpenedStorageCapsules = true;
        public bool RecycleOpenedSafeBoxes = true;
        public bool DeconstructStasisPods = true;
        public bool DeconstructBreachCapsulePorts = true;
        public bool RecycleArtilleryFungus = true;
        public bool DisposeDeadCrawlers = true;
        public bool ReplaceLostShuttles = false;
        public float BiologicalQualityThreshold { get; private set; } =
            DefaultQualityThreshold;
        public float RoboticQualityThreshold { get; private set; } =
            DefaultQualityThreshold;

        public int BiologicalTasksQueued { get; private set; }
        public int RoboticTasksQueued { get; private set; }
        public int EquipmentTasksQueued { get; private set; }
        public int BiologicalBodyPartTasksQueued { get; private set; }
        public int RoboticBodyPartTasksQueued { get; private set; }
        public int EquipmentItemTasksQueued { get; private set; }
        public int HullpiercerTasksQueued { get; private set; }
        public int StorageCapsuleTasksQueued { get; private set; }
        public int SafeBoxTasksQueued { get; private set; }
        public int ArtilleryFungusTasksQueued { get; private set; }
        public int DeadCrawlerTasksQueued { get; private set; }
        public int StasisPodTasksQueued { get; private set; }
        public int BreachCapsulePortTasksQueued { get; private set; }
        public int LostShuttleReplacementsQueued { get; private set; }
        public int ActiveTrackedTasks => trackedGoalsByObjectId.Count
            + trackedGoalsByBeingId.Count
            + trackedGoalsByStasisPodId.Count
            + trackedGoalsByBreachCapsuleId.Count
            + lostShuttleReplacements.Count;
        public int LastQueued { get; private set; }
        public int LastAboveThreshold { get; private set; }
        public int LastNoCompatibleProcessor { get; private set; }
        public int LastIgnoredByQuality => LastAboveThreshold
            + LastNoCompatibleProcessor;
        public int LegacyLastIgnoredByQuality { get; private set; }
        public int LastProtectedMindParts { get; private set; }
        public int LastBlockedByTask { get; private set; }
        public int LastEligibleLoose { get; private set; }
        public int LastForbidden { get; private set; }
        public int LastReserved { get; private set; }
        public int LastParentDisabled { get; private set; }
        public int LastCategoryDisabled { get; private set; }
        public int LastStoredOrNotLoose { get; private set; }
        public int LastCarried { get; private set; }
        public int LastInactiveOrInvalid { get; private set; }
        public int LastUnsupportedTarget { get; private set; }
        public int LastSpecialConditionNotReady { get; private set; }
        public int LastVanillaOrderRejected { get; private set; }

        [SelfInit]
        private static void Register() {
            GameSystems.Register(SysId, () => new IntelligentRecyclingSys());
        }

        protected override void OnInitialize() {
            if (!AutoUtilitiesApi.TryRegisterConsumer(
                    ConsumerId, DisplayName,
                    typeof(IntelligentRecyclingSys).Assembly.GetName().Version
                        .ToString(3),
                    RequiredUtilitiesVersion)) {
                return;
            }

            activeInstance = this;
            MarkApiRevision();
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(IntelligentRecyclingSys).Assembly);
            panelShortcut = AutoUtilitiesApi.RegisterShortcut(
                new ShortcutRegistration {
                    ConsumerId = ConsumerId,
                    Id = "TogglePanel",
                    DisplayName = "open or close Intelligent Recycling",
                    PreferenceKey = "intelligentrecycling.ui.shortcut",
                    GameState = S,
                    OnPressed = TogglePanel
                });
            panel = new IntelligentRecyclingPanel(this, panelShortcut);
            panelShortcut.StateChanged += OnShortcutStateChanged;
            topWidget = AutoUtilitiesApi.RegisterTopBarWidget(
                new TopBarWidgetRegistration {
                    Id = "IntelligentRecycling.Status",
                    Icon = () => IconId.CRecycling,
                    Tint = () => Enabled ? Color.green : Color.gray,
                    Tooltip = BuildWidgetTooltip,
                    OnClick = TogglePanel,
                    Width = 32f,
                    Order = 300
                });
            S.Sig.AfterLoadState.AddListener(OnAfterLoadState);
        }

        private void OnAfterLoadState(GameState state) {
            if (loadedState != null) {
                loadedState.Clock.OnTick.RemoveListener(OnTick);
                loadedState.Sig.ConstructionCompleted.RemoveListener(OnConstructionCompleted);
                loadedState.Sig.BeingDied.RemoveListener(OnBeingDied);
            }
            if (loadedSettingsGameId != state.GameId) {
                ResetToDefaults();
            }
            loadedState = state;
            partRestorationUnit.RefreshAvailability();
            loadedObjsSystem = ResolveObjsSystem(state);
            InitializeThresholdAuthority();
            InvalidateProcessorContext();
            panel?.Close();
            nextScanTick = state.Ticks + ScanIntervalTicks;
            lastScanTick = 0;
            trackedGoalsReady = false;
            pendingObjectScanIds.Clear();
            processingObjectScanIds.Clear();
            state.Clock.OnTick.AddListener(OnTick);
            state.Sig.ConstructionCompleted.AddListener(OnConstructionCompleted);
            state.Sig.BeingDied.AddListener(OnBeingDied);
            state.Clock.OnNextUpdate(() => {
                if (loadedState != state) {
                    return;
                }
                trackedGoalsReady = true;
                ReconcileTrackedGoals();
                MarkApiRevision();
            });
            RefreshTopWidget();
            MarkApiRevision();
            D.Warn("[Intelligent Recycling] Automation initialized.");
        }

        private static ObjsSys ResolveObjsSystem(GameState state) {
            if (state?.Sys?.Known == null
                    || !state.Sys.Known.TryGetValue(
                        ObjsSys.SysId, out GameSystem system)) {
                return null;
            }
            return system as ObjsSys;
        }

        private void OnTick(long ticks) {
            // Event patches only request work. Scans are consumed here so a
            // paused game remains idle and several events collapse into one pass.
            if (trackedGoalsReady) {
                ReconcileTrackedGoals();
            }
            if (!Enabled) {
                return;
            }
            bool fullScan = scanRequested || ticks >= nextScanTick;
            if (!fullScan && pendingObjectScanIds.Count == 0) {
                return;
            }
            if (fullScan) {
                scanRequested = false;
                nextScanTick = ticks + ScanIntervalTicks;
                pendingObjectScanIds.Clear();
                ScanLooseObjects(null);
                return;
            }

            processingObjectScanIds.Clear();
            processingObjectScanIds.UnionWith(pendingObjectScanIds);
            pendingObjectScanIds.Clear();
            try {
                ScanLooseObjects(processingObjectScanIds);
            } finally {
                processingObjectScanIds.Clear();
            }
        }

        private void RequestScan() {
            scanRequested = true;
            InvalidateProcessorContext();
            MarkApiRevision();
        }

        private void RequestObjectScan(Obj obj) {
            ObjData data = obj?.Data;
            if (data == null || data.Id == 0u) {
                RequestScan();
                return;
            }
            if (pendingObjectScanIds.Add(data.Id)) {
                MarkApiRevision();
            }
        }

        internal static void RequestEventScan() {
            activeInstance?.RequestScan();
        }

        internal static void RequestEventScan(Obj obj) {
            activeInstance?.RequestObjectScan(obj);
        }

        internal static void NotifyProcessorThresholdChanged(
                DisposerComp disposer) {
            activeInstance?.OnProcessorThresholdChanged(disposer);
        }

        private void OnConstructionCompleted(Tile tile) {
            CompleteLostShuttleReplacement(tile);
            if (tile != null
                    && tile.TryGetComponent<DisposerComp>(out DisposerComp disposer)) {
                if (thresholdAuthorityReady) {
                    ApplyManagedThreshold(disposer, refreshFacilityUi: false);
                }
                RequestScan();
            }
        }

        private void OnBeingDied(BeingData being) {
            if (DisposeDeadCrawlers
                    && being?.Species?.IdH == SpeciesIdH.Crawler) {
                RequestScan();
            }
        }

        private string BuildWidgetTooltip() {
            string status = Enabled ? "ON" : "OFF";
            return $"Intelligent Recycling: {status}\n"
                + WidgetStat("Active tracked tasks:", ActiveTrackedTasks)
                + WidgetStat("Eligible loose items checked:", LastEligibleLoose)
                + WidgetStat("Queued by the last scan:", LastQueued)
                + WidgetStat("Above processor threshold:", LastAboveThreshold)
                + WidgetStat("No compatible processor:", LastNoCompatibleProcessor)
                + WidgetStat("Protected Brain/Core:", LastProtectedMindParts)
                + WidgetStat("Busy or already assigned:", LastBlockedByTask)
                + "Click for settings.";
        }

        private static string WidgetStat(string label, int value) {
            return $"{label}<pos=270>{value}\n";
        }

        internal void SetEnabled(bool value) {
            Enabled = value;
            if (value) {
                RequestScan();
            }
            RefreshTopWidget();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetBiological(bool value) {
            RecycleBiologicalParts = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetRobotic(bool value) {
            RecycleRoboticParts = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetEquipment(bool value) {
            RecycleEquipment = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetBiologicalQualityThreshold(float value) {
            SetManagedThreshold(
                IntelligentRecyclingProcessorFamily.Biological,
                value,
                refreshPanel: false,
                source: null);
        }

        internal void SetRoboticQualityThreshold(float value) {
            SetManagedThreshold(
                IntelligentRecyclingProcessorFamily.Robotic,
                value,
                refreshPanel: false,
                source: null);
        }

        private void InitializeThresholdAuthority() {
            thresholdAuthorityReady = false;
            if (!thresholdSettingsLoadedFromSave) {
                AdoptExistingEffectiveThresholds();
            }
            thresholdSettingsLoadedFromSave = true;
            BiologicalQualityThreshold = NormalizeThreshold(
                BiologicalQualityThreshold);
            RoboticQualityThreshold = NormalizeThreshold(
                RoboticQualityThreshold);
            thresholdAuthorityReady = true;
            SynchronizeAllProcessorThresholds(refreshFacilityUi: false);
        }

        private void AdoptExistingEffectiveThresholds() {
            float biological = -1f;
            float robotic = -1f;
            disposers.Clear();
            S.Components.FindAll(disposers);
            foreach (DisposerComp disposer in disposers) {
                if (disposer?.Tile == null || !disposer.Tile.IsConstructed
                        || !IsFinite(disposer.PartQualityThreshold)) {
                    continue;
                }
                if (disposer.SlotDesignation
                        == SlotDesignationH.Disposer_Organic) {
                    biological = Math.Max(
                        biological, disposer.PartQualityThreshold);
                } else if (disposer.SlotDesignation
                        == SlotDesignationH.Disposer_Robotic) {
                    robotic = Math.Max(robotic, disposer.PartQualityThreshold);
                }
            }
            BiologicalQualityThreshold = biological >= 0f
                ? biological
                : DefaultQualityThreshold;
            RoboticQualityThreshold = robotic >= 0f
                ? robotic
                : DefaultQualityThreshold;
        }

        private void OnProcessorThresholdChanged(DisposerComp disposer) {
            if (disposer == null || synchronizingProcessorThresholds) {
                return;
            }
            if (!thresholdAuthorityReady) {
                RequestScan();
                return;
            }
            IntelligentRecyclingProcessorFamily family;
            if (disposer.SlotDesignation
                    == SlotDesignationH.Disposer_Organic) {
                family = IntelligentRecyclingProcessorFamily.Biological;
            } else if (disposer.SlotDesignation
                    == SlotDesignationH.Disposer_Robotic) {
                family = IntelligentRecyclingProcessorFamily.Robotic;
            } else {
                RequestScan();
                return;
            }
            bool sourceIsFinite = IsFinite(disposer.PartQualityThreshold);
            float value = sourceIsFinite
                ? disposer.PartQualityThreshold
                : family == IntelligentRecyclingProcessorFamily.Biological
                    ? BiologicalQualityThreshold
                    : RoboticQualityThreshold;
            SetManagedThreshold(
                family,
                value,
                refreshPanel: true,
                source: sourceIsFinite ? disposer : null);
        }

        private void SetManagedThreshold(
                IntelligentRecyclingProcessorFamily family,
                float value,
                bool refreshPanel,
                DisposerComp source) {
            float normalized = NormalizeThreshold(value);
            if (family == IntelligentRecyclingProcessorFamily.Biological) {
                BiologicalQualityThreshold = normalized;
            } else if (family
                    == IntelligentRecyclingProcessorFamily.Robotic) {
                RoboticQualityThreshold = normalized;
            } else {
                return;
            }
            SynchronizeProcessorThresholds(
                family, normalized, source, refreshFacilityUi: true);
            RequestScan();
            if (refreshPanel) {
                panel?.Refresh();
            }
            MarkApiRevision();
        }

        private void SynchronizeAllProcessorThresholds(bool refreshFacilityUi) {
            SynchronizeProcessorThresholds(
                IntelligentRecyclingProcessorFamily.Biological,
                BiologicalQualityThreshold,
                source: null,
                refreshFacilityUi);
            SynchronizeProcessorThresholds(
                IntelligentRecyclingProcessorFamily.Robotic,
                RoboticQualityThreshold,
                source: null,
                refreshFacilityUi);
            InvalidateProcessorContext();
            MarkApiRevision();
        }

        private void SynchronizeProcessorThresholds(
                IntelligentRecyclingProcessorFamily family,
                float threshold,
                DisposerComp source,
                bool refreshFacilityUi) {
            if (!thresholdAuthorityReady) {
                return;
            }
            synchronizingProcessorThresholds = true;
            try {
                disposers.Clear();
                S.Components.FindAll(disposers);
                foreach (DisposerComp disposer in disposers) {
                    if (disposer?.Tile == null || disposer == source
                            || !MatchesProcessorFamily(disposer, family)) {
                        continue;
                    }
                    bool changed = !IsFinite(disposer.PartQualityThreshold)
                        || disposer.PartQualityThreshold != threshold;
                    if (!changed) {
                        continue;
                    }
                    disposer.PartQualityThreshold = threshold;
                    if (refreshFacilityUi) {
                        disposer.DisposerStateChangedB.Invoke();
                    }
                }
            } finally {
                synchronizingProcessorThresholds = false;
            }
            InvalidateProcessorContext();
        }

        private void ApplyManagedThreshold(DisposerComp disposer,
                bool refreshFacilityUi) {
            IntelligentRecyclingProcessorFamily family;
            if (disposer.SlotDesignation
                    == SlotDesignationH.Disposer_Organic) {
                family = IntelligentRecyclingProcessorFamily.Biological;
            } else if (disposer.SlotDesignation
                    == SlotDesignationH.Disposer_Robotic) {
                family = IntelligentRecyclingProcessorFamily.Robotic;
            } else {
                return;
            }
            float threshold = family
                    == IntelligentRecyclingProcessorFamily.Biological
                ? BiologicalQualityThreshold
                : RoboticQualityThreshold;
            if (!IsFinite(disposer.PartQualityThreshold)
                    || disposer.PartQualityThreshold != threshold) {
                synchronizingProcessorThresholds = true;
                try {
                    disposer.PartQualityThreshold = threshold;
                    if (refreshFacilityUi) {
                        disposer.DisposerStateChangedB.Invoke();
                    }
                } finally {
                    synchronizingProcessorThresholds = false;
                }
            }
            InvalidateProcessorContext();
            MarkApiRevision();
        }

        private static bool MatchesProcessorFamily(DisposerComp disposer,
                IntelligentRecyclingProcessorFamily family) {
            return family == IntelligentRecyclingProcessorFamily.Biological
                ? disposer.SlotDesignation == SlotDesignationH.Disposer_Organic
                : family == IntelligentRecyclingProcessorFamily.Robotic
                    && disposer.SlotDesignation
                        == SlotDesignationH.Disposer_Robotic;
        }

        private static float NormalizeThreshold(float value) {
            return IsFinite(value)
                ? Mathf.Clamp01(value)
                : DefaultQualityThreshold;
        }

        internal List<KeyValuePair<string, string>> GetEquipmentCategories() {
            // Build from the loaded registries instead of Core constants so
            // compatible content mods appear automatically.
            Dictionary<string, string> categories = new();
            foreach (InventoryItemDef item in InventoryItemDef.All.Values) {
                if (item?.SlotType == null || string.IsNullOrEmpty(item.SlotId)) {
                    continue;
                }
                categories[item.SlotId] = item.SlotType.Name ?? item.SlotId;
            }
            return SortCategories(categories);
        }

        internal sealed class BodyPartCategoryInfo {
            public string Id;
            public string Name;
            public string BiologicalIcon;
            public string RoboticIcon;
        }

        internal List<BodyPartCategoryInfo> GetBodyPartCategories() {
            Dictionary<string, BodyPartCategoryInfo> categories = new();
            foreach (BodyPartDef part in BodyPartDef.All.Values) {
                if (part?.SlotType == null || string.IsNullOrEmpty(part.SlotTypeId)
                        || IsProtectedCategoryId(part.SlotTypeId)) {
                    continue;
                }
                if (!categories.TryGetValue(part.SlotTypeId, out BodyPartCategoryInfo info)) {
                    info = new BodyPartCategoryInfo {
                        Id = part.SlotTypeId,
                        Name = part.SlotType.NameT ?? part.SlotTypeId
                    };
                    categories.Add(part.SlotTypeId, info);
                }
                if (part.IsBiological) {
                    // Vanilla's Body UI also renders BodyPartDef.IconId. Keeping
                    // that source makes these controls visually consistent.
                    info.BiologicalIcon ??= part.IconId;
                } else {
                    info.RoboticIcon ??= part.IconId;
                }
            }
            List<BodyPartCategoryInfo> result = new(categories.Values);
            result.Sort((a, b) => string.Compare(a.Name, b.Name,
                StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        private static List<KeyValuePair<string, string>> SortCategories(
                Dictionary<string, string> categories) {
            List<KeyValuePair<string, string>> result = new(categories);
            result.Sort((a, b) => string.Compare(
                a.Value,
                b.Value,
                StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        internal bool IsEquipmentCategoryEnabled(string slotId) {
            return !string.IsNullOrEmpty(slotId)
                && !disabledEquipmentSlotIds.Contains(slotId);
        }

        internal bool IsBiologicalCategoryEnabled(string slotTypeId) {
            return !string.IsNullOrEmpty(slotTypeId)
                && !IsProtectedCategoryId(slotTypeId)
                && !disabledBiologicalSlotIds.Contains(slotTypeId);
        }

        internal bool IsRoboticCategoryEnabled(string slotTypeId) {
            return !string.IsNullOrEmpty(slotTypeId)
                && !IsProtectedCategoryId(slotTypeId)
                && !disabledRoboticSlotIds.Contains(slotTypeId);
        }

        internal void SetEquipmentCategory(string slotId, bool value) {
            SetCategory(disabledEquipmentSlotIds, slotId, value);
        }

        internal void SetBiologicalCategory(string slotTypeId, bool value) {
            if (IsProtectedCategoryId(slotTypeId)) {
                disabledBiologicalSlotIds.Add(slotTypeId);
                return;
            }
            SetCategory(disabledBiologicalSlotIds, slotTypeId, value);
        }

        internal void SetRoboticCategory(string slotTypeId, bool value) {
            if (IsProtectedCategoryId(slotTypeId)) {
                disabledRoboticSlotIds.Add(slotTypeId);
                return;
            }
            SetCategory(disabledRoboticSlotIds, slotTypeId, value);
        }

        private static bool IsProtectedCategoryId(string id) {
            return string.Equals(id, "Brain", StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, "Core", StringComparison.OrdinalIgnoreCase);
        }

        private void SetCategory(HashSet<string> disabled, string id, bool value) {
            if (string.IsNullOrEmpty(id)) {
                return;
            }
            if (value) {
                disabled.Remove(id);
            } else {
                disabled.Add(id);
            }
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetReplaceStorageHaulTasks(bool value) {
            ReplaceStorageHaulTasks = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetHullpiercers(bool value) {
            RecycleHullpiercers = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetOpenedStorageCapsules(bool value) {
            RecycleOpenedStorageCapsules = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetOpenedSafeBoxes(bool value) {
            RecycleOpenedSafeBoxes = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetStasisPods(bool value) {
            DeconstructStasisPods = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetBreachCapsulePorts(bool value) {
            DeconstructBreachCapsulePorts = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetArtilleryFungus(bool value) {
            RecycleArtilleryFungus = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetDeadCrawlers(bool value) {
            DisposeDeadCrawlers = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void SetReplaceLostShuttles(bool value) {
            if (value && !ReplaceLostShuttles) {
                suppressedLostShuttleEntityIds.Clear();
            }
            ReplaceLostShuttles = value;
            RequestScan();
            panel?.Refresh();
            MarkApiRevision();
        }

        internal void ResetQueuedStatistics() {
            BiologicalTasksQueued = 0;
            RoboticTasksQueued = 0;
            EquipmentTasksQueued = 0;
            BiologicalBodyPartTasksQueued = 0;
            RoboticBodyPartTasksQueued = 0;
            EquipmentItemTasksQueued = 0;
            HullpiercerTasksQueued = 0;
            StorageCapsuleTasksQueued = 0;
            SafeBoxTasksQueued = 0;
            ArtilleryFungusTasksQueued = 0;
            DeadCrawlerTasksQueued = 0;
            StasisPodTasksQueued = 0;
            BreachCapsulePortTasksQueued = 0;
            LostShuttleReplacementsQueued = 0;
            panel?.RefreshStatistics();
            MarkApiRevision();
        }

        private void RefreshTopWidget() {
            topWidget?.Refresh();
        }

        private void TogglePanel() {
            panel?.Toggle();
            MarkApiRevision();
        }

        private void OnShortcutStateChanged() {
            MarkApiRevision();
        }

        private void ScanLooseObjects(HashSet<uint> targetedObjectIds) {
            LastQueued = 0;
            LastAboveThreshold = 0;
            LastNoCompatibleProcessor = 0;
            LegacyLastIgnoredByQuality = 0;
            LastProtectedMindParts = 0;
            LastBlockedByTask = 0;
            LastEligibleLoose = 0;
            LastForbidden = 0;
            LastReserved = 0;
            LastParentDisabled = 0;
            LastCategoryDisabled = 0;
            LastStoredOrNotLoose = 0;
            LastCarried = 0;
            LastInactiveOrInvalid = 0;
            LastUnsupportedTarget = 0;
            LastSpecialConditionNotReady = 0;
            LastVanillaOrderRejected = 0;

            ProcessorContext processors = BuildProcessorContext();
            bool partRestorationReady =
                partRestorationUnit.RefreshAvailability();

            List<ObjData> activeObjects = loadedObjsSystem?.ActiveObjs;
            if (activeObjects == null) {
                loadedObjsSystem = ResolveObjsSystem(loadedState);
                activeObjects = loadedObjsSystem?.ActiveObjs;
            }

            if (activeObjects != null) {
                for (int i = 0; i < activeObjects.Count; i++) {
                    ObjData data = activeObjects[i];
                    if (targetedObjectIds != null
                            && (data == null
                                || !targetedObjectIds.Contains(data.Id))) {
                        continue;
                    }
                    EvaluateAndQueueObject(
                        data, processors, partRestorationReady);
                }
            }

            if (targetedObjectIds == null) {
                ScanStasisPods();
                ScanBreachCapsulePorts();
                ScanDeadCrawlers(processors.BiologicalThreshold);
                ScanLostShuttles();
            }

            lastScanTick = loadedState?.Ticks ?? 0;
            MarkApiRevision();
            RefreshTopWidget();
            panel?.RefreshStatistics();
        }

        private void EvaluateAndQueueObject(ObjData data,
                ProcessorContext processors, bool partRestorationReady) {
            if (data == null || data.Id == 0u) {
                LastInactiveOrInvalid++;
                return;
            }

            uint objectId = data.Id;
            ObjectEvaluation evaluation = EvaluateObject(data, processors);
            IntelligentRecyclingObjectSnapshotData snapshot =
                evaluation.Snapshot;

            if (!evaluation.Recognized || evaluation.Disposable == null) {
                if (snapshot.HasObjQuality) {
                    LastUnsupportedTarget++;
                }
                return;
            }
            if (!snapshot.Active) {
                LastInactiveOrInvalid++;
                if (snapshot.Stored || snapshot.InDevice) {
                    LastStoredOrNotLoose++;
                }
                return;
            }
            if (snapshot.Carried) {
                LastCarried++;
                LastStoredOrNotLoose++;
                return;
            }
            if (partRestorationReady
                    && partRestorationUnit.IsOwned(objectId)) {
                LastBlockedByTask++;
                return;
            }
            if (!snapshot.ParentSettingEnabled) {
                LastParentDisabled++;
                return;
            }
            if (!snapshot.CategorySettingEnabled) {
                LastCategoryDisabled++;
                return;
            }
            if (snapshot.SpecialTarget
                    && !snapshot.SpecialReadyForProcessing) {
                LastSpecialConditionNotReady++;
                return;
            }

            LastEligibleLoose++;
            if (!IsTransientQueueStateClear(snapshot, true)) {
                if (snapshot.PermanentlyProtected) {
                    LastProtectedMindParts++;
                } else if (snapshot.Forbidden) {
                    LastForbidden++;
                } else if (snapshot.Reserved) {
                    LastReserved++;
                } else {
                    LastInactiveOrInvalid++;
                }
                return;
            }
            if (!snapshot.EffectiveThresholdAvailable) {
                LastNoCompatibleProcessor++;
                return;
            }
            if (!snapshot.SpecialTarget
                    && snapshot.QualityRelation
                        == IntelligentRecyclingQualityRelation.AboveThreshold) {
                LastAboveThreshold++;
                return;
            }

            if (evaluation.Obj.Goal != null
                    && (!ReplaceStorageHaulTasks
                        || !TryCancelUnassignedStorageGoal(evaluation.Obj))) {
                LastBlockedByTask++;
                return;
            }

            if (evaluation.Disposable.TryOrderDispose(false, out _)) {
                LastQueued++;
                RecordQueuedCategory(data, evaluation.Disposable);
                if (evaluation.Obj.Goal != null) {
                    trackedGoalsByObjectId[objectId] = evaluation.Obj.Goal.Id;
                }
            } else {
                LastVanillaOrderRejected++;
            }
        }

        private void ReconcileTrackedGoals() {
            if (trackedGoalsByObjectId.Count == 0
                    && trackedGoalsByBeingId.Count == 0
                    && trackedGoalsByStasisPodId.Count == 0
                    && trackedGoalsByBreachCapsuleId.Count == 0
                    && lostShuttleReplacements.Count == 0) {
                return;
            }

            Dictionary<uint, ObjData> allObjects = S.Query.GetAllObjs.Ask();
            List<uint> completed = null;
            foreach (KeyValuePair<uint, uint> tracked in trackedGoalsByObjectId) {
                if (!allObjects.TryGetValue(tracked.Key, out ObjData data)
                        || data?.Obj == null
                        || data.Obj.Goal == null
                        || data.Obj.Goal.Id != tracked.Value) {
                    completed ??= new List<uint>();
                    completed.Add(tracked.Key);
                }
            }

            bool changed = completed != null;
            if (changed) {
                foreach (uint objectId in completed) {
                    trackedGoalsByObjectId.Remove(objectId);
                }
            }
            changed |= ReconcileTrackedCrawlers();
            changed |= ReconcileTrackedStasisPods();
            changed |= ReconcileTrackedBreachCapsules();
            changed |= ReconcileLostShuttleReplacements();
            if (!changed) {
                return;
            }
            MarkApiRevision();
            RefreshTopWidget();
            panel?.RefreshStatistics();
        }

        private void ScanDeadCrawlers(float biologicalThreshold) {
            if (!DisposeDeadCrawlers) {
                return;
            }
            Dictionary<uint, BeingData> allBeings = S.Query.AllBeingData.Ask();
            foreach (KeyValuePair<uint, BeingData> pair in allBeings) {
                BeingData data = pair.Value;
                Being being = data?.Being;
                if (data?.Species?.IdH != SpeciesIdH.Crawler
                        || !data.HasStateFlag(BeingState.Dead)
                        || being == null || !being.IsActive) {
                    continue;
                }

                LastEligibleLoose++;
                if (biologicalThreshold < 0f) {
                    LastNoCompatibleProcessor++;
                    continue;
                }
                if (being.Goal != null) {
                    LastBlockedByTask++;
                    continue;
                }
                if (!being.TryGetComponent<DisposableComp>(out var disposable)
                        || !disposable.IsBiological
                        || !disposable.TryOrderDispose(false, out _)) {
                    continue;
                }

                LastQueued++;
                BiologicalTasksQueued++;
                DeadCrawlerTasksQueued++;
                if (being.Goal != null) {
                    trackedGoalsByBeingId[pair.Key] = being.Goal.Id;
                }
            }
        }

        private bool ReconcileTrackedCrawlers() {
            if (trackedGoalsByBeingId.Count == 0) {
                return false;
            }
            Dictionary<uint, BeingData> allBeings = S.Query.AllBeingData.Ask();
            List<uint> completed = null;
            foreach (KeyValuePair<uint, uint> tracked in trackedGoalsByBeingId) {
                if (!allBeings.TryGetValue(tracked.Key, out BeingData data)
                        || data?.Being == null
                        || data.Being.Goal == null
                        || data.Being.Goal.Id != tracked.Value) {
                    completed ??= new List<uint>();
                    completed.Add(tracked.Key);
                }
            }
            if (completed == null) {
                return false;
            }
            foreach (uint beingId in completed) {
                trackedGoalsByBeingId.Remove(beingId);
            }
            return true;
        }

        private bool ReconcileTrackedStasisPods() {
            if (trackedGoalsByStasisPodId.Count == 0) {
                return false;
            }
            stasisPods.Clear();
            S.Components.FindAll(stasisPods);
            HashSet<int> active = new();
            foreach (StasisPodComp pod in stasisPods) {
                if (pod?.Tile?.Goal != null
                        && trackedGoalsByStasisPodId.TryGetValue(
                            pod.EntityId, out uint goalId)
                        && pod.Tile.Goal.Id == goalId) {
                    if (!IsStasisPodReadyForDeconstruction(pod)) {
                        AIGoal.Cancel(pod.Tile.Goal, G.Ticks,
                            "Stasis Pod is still Waiting");
                        D.Warn("[Intelligent Recycling] Canceled its tracked "
                            + "Stasis Pod deconstruction at {0} because Core's "
                            + "Waiting lifecycle is not complete.",
                            pod.Tile.PosIdx);
                        continue;
                    }
                    active.Add(pod.EntityId);
                }
            }
            List<int> completed = null;
            foreach (int podId in trackedGoalsByStasisPodId.Keys) {
                if (!active.Contains(podId)) {
                    completed ??= new List<int>();
                    completed.Add(podId);
                }
            }
            if (completed == null) {
                return false;
            }
            foreach (int podId in completed) {
                trackedGoalsByStasisPodId.Remove(podId);
            }
            return true;
        }

        private void ScanStasisPods() {
            if (!DeconstructStasisPods) {
                return;
            }
            stasisPods.Clear();
            S.Components.FindAll(stasisPods);
            foreach (StasisPodComp pod in stasisPods) {
                Tile tile = pod?.Tile;
                if (tile == null || !tile.IsActive || !tile.IsConstructed
                        || !IsStasisPodReadyForDeconstruction(pod)) {
                    continue;
                }
                if (tile.TryGetComponent<StasisPodMerchantComp>(out var merchant)
                        && (merchant.CanTrade || merchant.IsTraderActive)) {
                    continue;
                }

                LastEligibleLoose++;
                if (tile.Goal != null || !tile.Constructable.CanDeconstruct()) {
                    LastBlockedByTask++;
                    continue;
                }
                AIGoal goal = tile.Constructable.CreateDeconstructGoal();
                if (goal == null) {
                    continue;
                }
                LastQueued++;
                EquipmentTasksQueued++;
                StasisPodTasksQueued++;
                trackedGoalsByStasisPodId[pod.EntityId] = goal.Id;
            }
        }

        private bool IsStasisPodReadyForDeconstruction(StasisPodComp pod) {
            if (pod?.IsOpened != true) {
                return false;
            }
            if (pod.LandingTime <= 0) {
                return true;
            }
            return loadedState != null
                && loadedState.Ticks >= pod.LandingTime + Consts.TicksPer2Hours;
        }

        private bool ReconcileTrackedBreachCapsules() {
            if (trackedGoalsByBreachCapsuleId.Count == 0) {
                return false;
            }
            breachCapsules.Clear();
            S.Components.FindAll(breachCapsules);
            HashSet<int> active = new();
            foreach (BreachCapsuleComp capsule in breachCapsules) {
                if (capsule?.Tile?.Goal != null
                        && trackedGoalsByBreachCapsuleId.TryGetValue(
                            capsule.EntityId, out uint goalId)
                        && capsule.Tile.Goal.Id == goalId) {
                    if (!IsBreachCapsuleReadyForDeconstruction(capsule)) {
                        AIGoal.Cancel(capsule.Tile.Goal, G.Ticks,
                            "Breach Capsule raid is not resolved");
                        D.Warn("[Intelligent Recycling] Canceled its tracked "
                            + "Breach Capsule deconstruction at {0} because "
                            + "Core has not released or resolved its raid.",
                            capsule.Tile.PosIdx);
                        continue;
                    }
                    active.Add(capsule.EntityId);
                }
            }
            List<int> completed = null;
            foreach (int capsuleId in trackedGoalsByBreachCapsuleId.Keys) {
                if (!active.Contains(capsuleId)) {
                    completed ??= new List<int>();
                    completed.Add(capsuleId);
                }
            }
            if (completed == null) {
                return false;
            }
            foreach (int capsuleId in completed) {
                trackedGoalsByBreachCapsuleId.Remove(capsuleId);
            }
            return true;
        }

        private void ScanBreachCapsulePorts() {
            if (!DeconstructBreachCapsulePorts) {
                return;
            }
            breachCapsules.Clear();
            S.Components.FindAll(breachCapsules);
            foreach (BreachCapsuleComp capsule in breachCapsules) {
                Tile tile = capsule?.Tile;
                if (tile == null || !tile.IsActive || !tile.IsConstructed
                        || !IsBreachCapsuleReadyForDeconstruction(capsule)) {
                    continue;
                }

                LastEligibleLoose++;
                if (tile.Goal != null || !tile.Constructable.CanDeconstruct()) {
                    LastBlockedByTask++;
                    continue;
                }
                AIGoal goal = tile.Constructable.CreateDeconstructGoal();
                if (goal == null) {
                    continue;
                }
                LastQueued++;
                EquipmentTasksQueued++;
                BreachCapsulePortTasksQueued++;
                trackedGoalsByBreachCapsuleId[capsule.EntityId] = goal.Id;
            }
        }

        private static bool IsBreachCapsuleReadyForDeconstruction(
                BreachCapsuleComp capsule) {
            return capsule?.IsOpened == true && capsule.IsRaidStarted;
        }

        private bool IsEnabledCategory(ObjData data, DisposableComp disposable) {
            string definitionId = data.Def?.Id;
            if (string.Equals(definitionId, DefIds.ObjContainersHullpiercer,
                    StringComparison.Ordinal)) {
                return RecycleHullpiercers
                    && (!data.Obj.TryGetComponent<HullpiercerComp>(out var hullpiercer)
                        || !hullpiercer.IsMidFlight);
            }
            if (string.Equals(definitionId, DefIds.ObjContainersStorageCapsule,
                    StringComparison.Ordinal)) {
                return RecycleOpenedStorageCapsules
                    && data.Obj.TryGetComponent<StorageCapsuleComp>(out var capsule)
                    && capsule.IsOpen;
            }
            if (string.Equals(definitionId, DefIds.ObjContainersQuestPackage,
                    StringComparison.Ordinal)) {
                return RecycleOpenedSafeBoxes
                    && data.Obj.TryGetComponent<QuestPackageComp>(out var safeBox)
                    && safeBox.IsOpen;
            }
            if (string.Equals(definitionId, ArtilleryFungusDefinitionId,
                    StringComparison.Ordinal)) {
                return RecycleArtilleryFungus;
            }
            BodyPartDef part = data.BodyPart;
            if (part != null) {
                return part.IsBiological
                    ? RecycleBiologicalParts
                        && IsBiologicalCategoryEnabled(part.SlotTypeId)
                    : RecycleRoboticParts
                        && IsRoboticCategoryEnabled(part.SlotTypeId);
            }
            if (data.Item != null) {
                return RecycleEquipment
                    && IsEquipmentCategoryEnabled(data.Item.SlotId);
            }
            return false;
        }

        private void RecordQueuedCategory(ObjData data, DisposableComp disposable) {
            if (string.Equals(data.Def?.Id, ArtilleryFungusDefinitionId,
                    StringComparison.Ordinal)) {
                BiologicalTasksQueued++;
                ArtilleryFungusTasksQueued++;
            } else if (data.BodyPart != null) {
                if (disposable.IsBiological) {
                    BiologicalTasksQueued++;
                    BiologicalBodyPartTasksQueued++;
                } else {
                    RoboticTasksQueued++;
                    RoboticBodyPartTasksQueued++;
                }
            } else if (string.Equals(data.Def?.Id,
                    DefIds.ObjContainersHullpiercer,
                    StringComparison.Ordinal)) {
                EquipmentTasksQueued++;
                HullpiercerTasksQueued++;
            } else if (string.Equals(data.Def?.Id,
                    DefIds.ObjContainersStorageCapsule,
                    StringComparison.Ordinal)) {
                EquipmentTasksQueued++;
                StorageCapsuleTasksQueued++;
            } else if (string.Equals(data.Def?.Id,
                    DefIds.ObjContainersQuestPackage,
                    StringComparison.Ordinal)) {
                EquipmentTasksQueued++;
                SafeBoxTasksQueued++;
            } else if (data.Item != null) {
                EquipmentTasksQueued++;
                EquipmentItemTasksQueued++;
            }
        }

        private static bool IsUnconditionalProcessingTarget(ObjData data) {
            return IsSpecialContainerTarget(data)
                || string.Equals(data?.Def?.Id, ArtilleryFungusDefinitionId,
                    StringComparison.Ordinal);
        }

        private static bool IsSpecialContainerTarget(ObjData data) {
            string id = data?.Def?.Id;
            return string.Equals(id, DefIds.ObjContainersHullpiercer,
                    StringComparison.Ordinal)
                || string.Equals(id, DefIds.ObjContainersStorageCapsule,
                    StringComparison.Ordinal)
                || string.Equals(id, DefIds.ObjContainersQuestPackage,
                    StringComparison.Ordinal);
        }

        private static bool IsProtectedMindPart(ObjData data) {
            if (data.BodyPart == null) {
                return false;
            }
            if (data.BodyPart.HoldsMind || data.Mind != null || data.MindId != 0) {
                return true;
            }

            // Safety net for blank/dumb Core variants that intentionally have no
            // mind attached. Definition IDs are stable and are not localized.
            string id = data.BodyPart.Id ?? data.Def?.Id;
            if (string.IsNullOrEmpty(id)) {
                return false;
            }
            return id.IndexOf("brain", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("core", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsHaulingForbidden(Obj obj) {
            return obj.TryGetComponent<StorableObjComp>(out var storable)
                && storable.IsForbidden;
        }

        private static bool TryCancelUnassignedStorageGoal(Obj obj) {
            if (!obj.TryGetComponent<StorableObjComp>(out var storable)) {
                return false;
            }

            AIGoal goal = obj.Goal;
            if (goal == null
                    || goal.Target != storable
                    || goal.JobIdH != JobTypeIdH.Hauling
                    || !IsUnassignedStorageGoalState(goal.State)) {
                return false;
            }

            AIGoal.Cancel(goal, G.Ticks);
            return obj.Goal == null;
        }

        private static bool IsUnassignedStorageGoalState(AIGoalState state) {
            return state == AIGoalState.Available
                || state == AIGoalState.Pending
                || state == AIGoalState.Bidding;
        }

        public ComponentData Save() {
            saveData ??= new ComponentData(0, SysId);
            saveData.SetInt("SaveSchemaVersion", SaveSchemaVersion);
            saveData.SetBool("Enabled", Enabled);
            saveData.SetBool("RecycleBiologicalParts", RecycleBiologicalParts);
            saveData.SetBool("RecycleRoboticParts", RecycleRoboticParts);
            saveData.SetBool("RecycleEquipment", RecycleEquipment);
            saveData.SetBool("ReplaceStorageHaulTasks", ReplaceStorageHaulTasks);
            saveData.SetBool("RecycleHullpiercers", RecycleHullpiercers);
            saveData.SetBool("RecycleOpenedStorageCapsules",
                RecycleOpenedStorageCapsules);
            saveData.SetBool("RecycleOpenedSafeBoxes",
                RecycleOpenedSafeBoxes);
            saveData.SetBool("DeconstructStasisPods", DeconstructStasisPods);
            saveData.SetBool("DeconstructBreachCapsulePorts",
                DeconstructBreachCapsulePorts);
            saveData.SetBool("RecycleArtilleryFungus", RecycleArtilleryFungus);
            saveData.SetBool("DisposeDeadCrawlers", DisposeDeadCrawlers);
            saveData.SetBool("ReplaceLostShuttles", ReplaceLostShuttles);
            saveData.SetFloat("BiologicalQualityThreshold",
                BiologicalQualityThreshold);
            saveData.SetFloat("RoboticQualityThreshold",
                RoboticQualityThreshold);
            saveData.SetString(
                "DisabledEquipmentSlotIds",
                string.Join("|", disabledEquipmentSlotIds));
            saveData.SetString("DisabledBiologicalSlotIds",
                string.Join("|", disabledBiologicalSlotIds));
            saveData.SetString("DisabledRoboticSlotIds",
                string.Join("|", disabledRoboticSlotIds));
            saveData.SetInt("BiologicalTasksQueued", BiologicalTasksQueued);
            saveData.SetInt("RoboticTasksQueued", RoboticTasksQueued);
            saveData.SetInt("EquipmentTasksQueued", EquipmentTasksQueued);
            saveData.SetInt("BiologicalBodyPartTasksQueued",
                BiologicalBodyPartTasksQueued);
            saveData.SetInt("RoboticBodyPartTasksQueued",
                RoboticBodyPartTasksQueued);
            saveData.SetInt("EquipmentItemTasksQueued",
                EquipmentItemTasksQueued);
            saveData.SetInt("HullpiercerTasksQueued", HullpiercerTasksQueued);
            saveData.SetInt("StorageCapsuleTasksQueued",
                StorageCapsuleTasksQueued);
            saveData.SetInt("SafeBoxTasksQueued", SafeBoxTasksQueued);
            saveData.SetInt("ArtilleryFungusTasksQueued",
                ArtilleryFungusTasksQueued);
            saveData.SetInt("DeadCrawlerTasksQueued", DeadCrawlerTasksQueued);
            saveData.SetInt("StasisPodTasksQueued", StasisPodTasksQueued);
            saveData.SetInt("BreachCapsulePortTasksQueued",
                BreachCapsulePortTasksQueued);
            saveData.SetInt("LostShuttleReplacementsQueued",
                LostShuttleReplacementsQueued);
            saveData.SetInt("LastQueued", LastQueued);
            saveData.SetInt("LastIgnoredByQuality", LastIgnoredByQuality);
            saveData.SetInt("LastAboveThreshold", LastAboveThreshold);
            saveData.SetInt("LastNoCompatibleProcessor",
                LastNoCompatibleProcessor);
            saveData.SetInt("LegacyLastIgnoredByQuality",
                LegacyLastIgnoredByQuality);
            saveData.SetInt("LastProtectedMindParts", LastProtectedMindParts);
            saveData.SetInt("LastBlockedByTask", LastBlockedByTask);
            saveData.SetInt("LastEligibleLoose", LastEligibleLoose);
            saveData.SetInt("LastForbidden", LastForbidden);
            saveData.SetInt("LastReserved", LastReserved);
            saveData.SetInt("LastParentDisabled", LastParentDisabled);
            saveData.SetInt("LastCategoryDisabled", LastCategoryDisabled);
            saveData.SetInt("LastStoredOrNotLoose", LastStoredOrNotLoose);
            saveData.SetInt("LastCarried", LastCarried);
            saveData.SetInt("LastInactiveOrInvalid", LastInactiveOrInvalid);
            saveData.SetInt("LastUnsupportedTarget", LastUnsupportedTarget);
            saveData.SetInt("LastSpecialConditionNotReady",
                LastSpecialConditionNotReady);
            saveData.SetInt("LastVanillaOrderRejected",
                LastVanillaOrderRejected);
            saveData.SetLong("LastScanTick", lastScanTick);
            int[] objectIds = new int[trackedGoalsByObjectId.Count];
            int[] goalIds = new int[trackedGoalsByObjectId.Count];
            int trackedIndex = 0;
            foreach (KeyValuePair<uint, uint> tracked in trackedGoalsByObjectId) {
                objectIds[trackedIndex] = unchecked((int) tracked.Key);
                goalIds[trackedIndex] = unchecked((int) tracked.Value);
                trackedIndex++;
            }
            saveData.SetIntSet("TrackedObjectIds", objectIds);
            saveData.SetIntSet("TrackedGoalIds", goalIds);
            int[] beingIds = new int[trackedGoalsByBeingId.Count];
            int[] beingGoalIds = new int[trackedGoalsByBeingId.Count];
            int beingIndex = 0;
            foreach (KeyValuePair<uint, uint> tracked in trackedGoalsByBeingId) {
                beingIds[beingIndex] = unchecked((int) tracked.Key);
                beingGoalIds[beingIndex] = unchecked((int) tracked.Value);
                beingIndex++;
            }
            saveData.SetIntSet("TrackedBeingIds", beingIds);
            saveData.SetIntSet("TrackedBeingGoalIds", beingGoalIds);
            int[] stasisPodIds = new int[trackedGoalsByStasisPodId.Count];
            int[] stasisGoalIds = new int[trackedGoalsByStasisPodId.Count];
            int stasisIndex = 0;
            foreach (KeyValuePair<int, uint> tracked in trackedGoalsByStasisPodId) {
                stasisPodIds[stasisIndex] = tracked.Key;
                stasisGoalIds[stasisIndex] = unchecked((int) tracked.Value);
                stasisIndex++;
            }
            saveData.SetIntSet("TrackedStasisPodIds", stasisPodIds);
            saveData.SetIntSet("TrackedStasisGoalIds", stasisGoalIds);
            int[] breachCapsuleIds = new int[trackedGoalsByBreachCapsuleId.Count];
            int[] breachGoalIds = new int[trackedGoalsByBreachCapsuleId.Count];
            int breachIndex = 0;
            foreach (KeyValuePair<int, uint> tracked
                    in trackedGoalsByBreachCapsuleId) {
                breachCapsuleIds[breachIndex] = tracked.Key;
                breachGoalIds[breachIndex] = unchecked((int) tracked.Value);
                breachIndex++;
            }
            saveData.SetIntSet("TrackedBreachCapsuleIds", breachCapsuleIds);
            saveData.SetIntSet("TrackedBreachGoalIds", breachGoalIds);
            SaveLostShuttleReplacementState(saveData);
            return saveData;
        }

        public void Load(ComponentData data) {
            saveData = data;
            loadedSettingsGameId = S.GameId;
            int schemaVersion = Math.Max(0,
                data.GetInt("SaveSchemaVersion", 0));
            Enabled = data.GetBool("Enabled", true);
            RecycleBiologicalParts = data.GetBool("RecycleBiologicalParts", true);
            RecycleRoboticParts = data.GetBool("RecycleRoboticParts", true);
            RecycleEquipment = data.GetBool("RecycleEquipment", false);
            ReplaceStorageHaulTasks = data.GetBool("ReplaceStorageHaulTasks", false);
            RecycleHullpiercers = data.GetBool("RecycleHullpiercers", true);
            RecycleOpenedStorageCapsules = data.GetBool(
                "RecycleOpenedStorageCapsules", true);
            RecycleOpenedSafeBoxes = data.GetBool(
                "RecycleOpenedSafeBoxes", true);
            DeconstructStasisPods = data.GetBool("DeconstructStasisPods", true);
            DeconstructBreachCapsulePorts = data.GetBool(
                "DeconstructBreachCapsulePorts", true);
            RecycleArtilleryFungus = data.GetBool("RecycleArtilleryFungus", true);
            DisposeDeadCrawlers = data.GetBool("DisposeDeadCrawlers", true);
            ReplaceLostShuttles = data.GetBool("ReplaceLostShuttles", false);
            thresholdSettingsLoadedFromSave = schemaVersion >= 11;
            BiologicalQualityThreshold = thresholdSettingsLoadedFromSave
                ? NormalizeThreshold(data.GetFloat(
                    "BiologicalQualityThreshold", DefaultQualityThreshold))
                : DefaultQualityThreshold;
            RoboticQualityThreshold = thresholdSettingsLoadedFromSave
                ? NormalizeThreshold(data.GetFloat(
                    "RoboticQualityThreshold", DefaultQualityThreshold))
                : DefaultQualityThreshold;
            LoadDisabledCategories(
                data.GetString("DisabledEquipmentSlotIds", null),
                disabledEquipmentSlotIds);
            // 0.8.x stored one shared body-slot set. If the new independent
            // sets are absent, seed both from that legacy value.
            string legacyBodySlots = data.GetString("DisabledBodyPartSlotIds", null);
            string biologicalSlots = data.GetString("DisabledBiologicalSlotIds", null);
            string roboticSlots = data.GetString("DisabledRoboticSlotIds", null);
            LoadDisabledCategories(biologicalSlots ?? legacyBodySlots,
                disabledBiologicalSlotIds);
            LoadDisabledCategories(roboticSlots ?? legacyBodySlots,
                disabledRoboticSlotIds);
            EnforceProtectedCategories();
            BiologicalTasksQueued = Math.Max(0, data.GetInt("BiologicalTasksQueued", 0));
            RoboticTasksQueued = Math.Max(0, data.GetInt("RoboticTasksQueued", 0));
            EquipmentTasksQueued = Math.Max(0, data.GetInt("EquipmentTasksQueued", 0));
            BiologicalBodyPartTasksQueued = Math.Max(0,
                data.GetInt("BiologicalBodyPartTasksQueued", 0));
            RoboticBodyPartTasksQueued = Math.Max(0,
                data.GetInt("RoboticBodyPartTasksQueued", 0));
            EquipmentItemTasksQueued = Math.Max(0,
                data.GetInt("EquipmentItemTasksQueued", 0));
            HullpiercerTasksQueued = Math.Max(0,
                data.GetInt("HullpiercerTasksQueued", 0));
            StorageCapsuleTasksQueued = Math.Max(0,
                data.GetInt("StorageCapsuleTasksQueued", 0));
            SafeBoxTasksQueued = Math.Max(0,
                data.GetInt("SafeBoxTasksQueued", 0));
            ArtilleryFungusTasksQueued = Math.Max(0,
                data.GetInt("ArtilleryFungusTasksQueued", 0));
            DeadCrawlerTasksQueued = Math.Max(0,
                data.GetInt("DeadCrawlerTasksQueued", 0));
            StasisPodTasksQueued = Math.Max(0,
                data.GetInt("StasisPodTasksQueued", 0));
            BreachCapsulePortTasksQueued = Math.Max(0,
                data.GetInt("BreachCapsulePortTasksQueued", 0));
            LostShuttleReplacementsQueued = Math.Max(0,
                data.GetInt("LostShuttleReplacementsQueued", 0));
            LastQueued = Math.Max(0, data.GetInt("LastQueued", 0));
            if (schemaVersion >= 10) {
                LastAboveThreshold = Math.Max(0,
                    data.GetInt("LastAboveThreshold", 0));
                LastNoCompatibleProcessor = Math.Max(0,
                    data.GetInt("LastNoCompatibleProcessor", 0));
                LegacyLastIgnoredByQuality = Math.Max(0,
                    data.GetInt("LegacyLastIgnoredByQuality", 0));
            } else {
                LastAboveThreshold = 0;
                LastNoCompatibleProcessor = 0;
                LegacyLastIgnoredByQuality = Math.Max(0,
                    data.GetInt("LastIgnoredByQuality", 0));
            }
            LastProtectedMindParts = Math.Max(0, data.GetInt("LastProtectedMindParts", 0));
            LastBlockedByTask = Math.Max(0, data.GetInt("LastBlockedByTask", 0));
            LastEligibleLoose = Math.Max(0, data.GetInt("LastEligibleLoose", 0));
            LastForbidden = Math.Max(0, data.GetInt("LastForbidden", 0));
            LastReserved = Math.Max(0, data.GetInt("LastReserved", 0));
            LastParentDisabled = Math.Max(0,
                data.GetInt("LastParentDisabled", 0));
            LastCategoryDisabled = Math.Max(0,
                data.GetInt("LastCategoryDisabled", 0));
            LastStoredOrNotLoose = Math.Max(0,
                data.GetInt("LastStoredOrNotLoose", 0));
            LastCarried = Math.Max(0, data.GetInt("LastCarried", 0));
            LastInactiveOrInvalid = Math.Max(0,
                data.GetInt("LastInactiveOrInvalid", 0));
            LastUnsupportedTarget = Math.Max(0,
                data.GetInt("LastUnsupportedTarget", 0));
            LastSpecialConditionNotReady = Math.Max(0,
                data.GetInt("LastSpecialConditionNotReady", 0));
            LastVanillaOrderRejected = Math.Max(0,
                data.GetInt("LastVanillaOrderRejected", 0));
            lastScanTick = Math.Max(0L, data.GetLong("LastScanTick", 0L));
            trackedGoalsByObjectId.Clear();
            int[] objectIds = data.GetIntSet("TrackedObjectIds", null);
            int[] goalIds = data.GetIntSet("TrackedGoalIds", null);
            if (objectIds != null && goalIds != null) {
                int count = Math.Min(objectIds.Length, goalIds.Length);
                for (int i = 0; i < count; i++) {
                    trackedGoalsByObjectId[unchecked((uint) objectIds[i])] =
                        unchecked((uint) goalIds[i]);
                }
            }
            trackedGoalsByBeingId.Clear();
            int[] beingIds = data.GetIntSet("TrackedBeingIds", null);
            int[] beingGoalIds = data.GetIntSet("TrackedBeingGoalIds", null);
            if (beingIds != null && beingGoalIds != null) {
                int count = Math.Min(beingIds.Length, beingGoalIds.Length);
                for (int i = 0; i < count; i++) {
                    trackedGoalsByBeingId[unchecked((uint) beingIds[i])] =
                        unchecked((uint) beingGoalIds[i]);
                }
            }
            trackedGoalsByStasisPodId.Clear();
            int[] stasisPodIds = data.GetIntSet("TrackedStasisPodIds", null);
            int[] stasisGoalIds = data.GetIntSet("TrackedStasisGoalIds", null);
            if (stasisPodIds != null && stasisGoalIds != null) {
                int count = Math.Min(stasisPodIds.Length, stasisGoalIds.Length);
                for (int i = 0; i < count; i++) {
                    trackedGoalsByStasisPodId[stasisPodIds[i]] =
                        unchecked((uint) stasisGoalIds[i]);
                }
            }
            trackedGoalsByBreachCapsuleId.Clear();
            int[] breachCapsuleIds = data.GetIntSet(
                "TrackedBreachCapsuleIds", null);
            int[] breachGoalIds = data.GetIntSet("TrackedBreachGoalIds", null);
            if (breachCapsuleIds != null && breachGoalIds != null) {
                int count = Math.Min(breachCapsuleIds.Length, breachGoalIds.Length);
                for (int i = 0; i < count; i++) {
                    trackedGoalsByBreachCapsuleId[breachCapsuleIds[i]] =
                        unchecked((uint) breachGoalIds[i]);
                }
            }
            LoadLostShuttleReplacementState(data, schemaVersion);
            MarkApiRevision();
        }

        private static void LoadDisabledCategories(
                string serialized,
                HashSet<string> destination) {
            destination.Clear();
            if (string.IsNullOrEmpty(serialized)) {
                return;
            }
            string[] ids = serialized.Split(new[] { '|' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string id in ids) {
                destination.Add(id);
            }
        }

        private void EnforceProtectedCategories() {
            // This is deliberately enforced below the UI. Hidden controls or
            // old/tampered save data must never make Brain/Core disposable.
            disabledBiologicalSlotIds.Add("Brain");
            disabledBiologicalSlotIds.Add("Core");
            disabledRoboticSlotIds.Add("Brain");
            disabledRoboticSlotIds.Add("Core");
        }

        private void ResetToDefaults() {
            saveData = null;
            loadedSettingsGameId = -1;
            Enabled = true;
            RecycleBiologicalParts = true;
            RecycleRoboticParts = true;
            RecycleEquipment = false;
            ReplaceStorageHaulTasks = false;
            RecycleHullpiercers = true;
            RecycleOpenedStorageCapsules = true;
            RecycleOpenedSafeBoxes = true;
            DeconstructStasisPods = true;
            DeconstructBreachCapsulePorts = true;
            RecycleArtilleryFungus = true;
            DisposeDeadCrawlers = true;
            ReplaceLostShuttles = false;
            BiologicalQualityThreshold = DefaultQualityThreshold;
            RoboticQualityThreshold = DefaultQualityThreshold;
            thresholdSettingsLoadedFromSave = false;
            thresholdAuthorityReady = false;
            synchronizingProcessorThresholds = false;
            disabledEquipmentSlotIds.Clear();
            disabledBiologicalSlotIds.Clear();
            disabledRoboticSlotIds.Clear();
            EnforceProtectedCategories();
            BiologicalTasksQueued = 0;
            RoboticTasksQueued = 0;
            EquipmentTasksQueued = 0;
            BiologicalBodyPartTasksQueued = 0;
            RoboticBodyPartTasksQueued = 0;
            EquipmentItemTasksQueued = 0;
            HullpiercerTasksQueued = 0;
            StorageCapsuleTasksQueued = 0;
            SafeBoxTasksQueued = 0;
            ArtilleryFungusTasksQueued = 0;
            DeadCrawlerTasksQueued = 0;
            StasisPodTasksQueued = 0;
            BreachCapsulePortTasksQueued = 0;
            LostShuttleReplacementsQueued = 0;
            LastQueued = 0;
            LastAboveThreshold = 0;
            LastNoCompatibleProcessor = 0;
            LegacyLastIgnoredByQuality = 0;
            LastProtectedMindParts = 0;
            LastBlockedByTask = 0;
            LastEligibleLoose = 0;
            LastForbidden = 0;
            LastReserved = 0;
            LastParentDisabled = 0;
            LastCategoryDisabled = 0;
            LastStoredOrNotLoose = 0;
            LastCarried = 0;
            LastInactiveOrInvalid = 0;
            LastUnsupportedTarget = 0;
            LastSpecialConditionNotReady = 0;
            LastVanillaOrderRejected = 0;
            lastScanTick = 0;
            trackedGoalsByObjectId.Clear();
            trackedGoalsByBeingId.Clear();
            trackedGoalsByStasisPodId.Clear();
            trackedGoalsByBreachCapsuleId.Clear();
            lostShuttleReplacements.Clear();
            suppressedLostShuttleEntityIds.Clear();
            MarkApiRevision();
        }

        public override void Unload() {
            S.Sig.AfterLoadState.RemoveListener(OnAfterLoadState);
            if (loadedState != null) {
                loadedState.Clock.OnTick.RemoveListener(OnTick);
                loadedState.Sig.ConstructionCompleted.RemoveListener(OnConstructionCompleted);
                loadedState.Sig.BeingDied.RemoveListener(OnBeingDied);
                loadedState = null;
                loadedObjsSystem = null;
                trackedGoalsReady = false;
                thresholdAuthorityReady = false;
            }
            panel?.Dispose();
            panel = null;
            topWidget?.Dispose();
            topWidget = null;
            if (panelShortcut != null) {
                panelShortcut.StateChanged -= OnShortcutStateChanged;
            }
            panelShortcut?.Dispose();
            panelShortcut = null;
            disposers.Clear();
            stasisPods.Clear();
            breachCapsules.Clear();
            shuttles.Clear();
            InvalidateProcessorContext();
            trackedGoalsByObjectId.Clear();
            trackedGoalsByBeingId.Clear();
            trackedGoalsByStasisPodId.Clear();
            trackedGoalsByBreachCapsuleId.Clear();
            lostShuttleReplacements.Clear();
            suppressedLostShuttleEntityIds.Clear();
            if (activeInstance == this) {
                activeInstance = null;
                MarkApiRevision();
            }
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
        }
    }
}
