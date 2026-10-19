using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutoUtilitiesModFramework;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Items;
using Game.Utils;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.AutoHauling {
    /// <summary>
    /// Finds safe loose cargo and asks Core to create its ordinary Store goal.
    /// Destination selection and the complete goal lifecycle remain vanilla.
    /// </summary>
    public sealed class AutoHaulingSys : GameSystem, ISaveable {
        public const string SysId = "AutoHaulingSys";
        public const string ConsumerId = "AutoHauling";
        public const string ModVersion = "1.1.6";
        public const string RequiredUtilitiesVersion = "1.4.0";

        private const int SaveSchemaVersion = 3;
        private const long ScanIntervalTicks = 300;
        // Core's first counted colony-goal retry cannot occur until more than
        // 32 ticks after failure. Polling at 30 preserves per-attempt capture.
        private const long TrackedGoalReconcileIntervalTicks =
            Consts.TicksPer30Minutes;
        private const int MaxFailureRecords = 50;
        private const string HullpiercerDefinitionId =
            "Obj/Containers/Hullpiercer";
        private const string StorageCapsuleDefinitionId =
            "Obj/Containers/StorageCapsule";
        private const string ArtilleryFungusDefinitionId =
            "Obj/Plants/ArtilleryFungus";

        private static readonly int StorableObjComponentH =
            Hashes.S(ComponentId.StorableObj);
        private static readonly int ObjQualityComponentH =
            Hashes.S(ComponentId.ObjQuality);
        private static readonly int GroupPropertyH = Hashes.S("Group");
        private static readonly CultureInfo Invariant =
            CultureInfo.InvariantCulture;

        private readonly HashSet<string> disabledCategoryIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> enabledItemOverrides =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> disabledItemOverrides =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<uint, TrackedHaulGoal> trackedGoals =
            new Dictionary<uint, TrackedHaulGoal>();
        private readonly List<TrackedHaulGoal> trackedGoalSnapshot =
            new List<TrackedHaulGoal>();
        private readonly List<AIGoal> staleStoreGoalSnapshot =
            new List<AIGoal>();
        private readonly Dictionary<uint, byte> staleStorePriorities =
            new Dictionary<uint, byte>();
        private readonly List<HaulFailureRecord> failureHistory =
            new List<HaulFailureRecord>();
        private readonly IntelligentRecyclingBridge intelligentRecycling =
            new IntelligentRecyclingBridge();
        private readonly PartRestorationUnitBridge partRestorationUnit =
            new PartRestorationUnitBridge();

        private ComponentData saveData;
        private int loadedSettingsGameId = -1;
        private GameState loadedState;
        private ObjsSys loadedObjsSystem;
        private long nextScanTick;
        private long nextTrackedGoalReconcileTick;
        private bool trackedGoalsReady;
        private bool frameworkReady;
        // Schema-3 saves keep this retired preference for rollback only.
        // Core now owns all worker bidding; nothing reads it for gameplay.
        private bool legacyIdleHaulingBidAssistEnabled;
        private IShortcutHandle allowForbiddenShortcut;
        private AutoHaulingPanel panel;

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        public bool Enabled { get; private set; }
        public bool StaleStoreRefreshEnabled { get; private set; } = true;
        public bool FrameworkReady => frameworkReady;
        public int ActiveTrackedTasks => trackedGoals.Count;
        internal IReadOnlyList<HaulFailureRecord> FailureHistory => failureHistory;

        public long LastScanTick { get; private set; }
        public int LastScanned { get; private set; }
        public int LastQueued { get; private set; }
        public int LastBlockedByTask { get; private set; }
        public int LastForbidden { get; private set; }
        public int LastNoStorage { get; private set; }
        public int LastDisabled { get; private set; }
        public int LastProtected { get; private set; }
        public int LastUnavailable { get; private set; }
        public int LastRejected { get; private set; }
        public int LastHeldByIntelligentRecycling { get; private set; }
        public int LastHeldByPartRestorationUnit { get; private set; }
        public int LastStaleStoreTasksFound { get; private set; }
        public int LastStaleStoreTasksCancelled { get; private set; }
        public int LastStaleStoreTasksRequeued { get; private set; }

        internal ForbiddenLooseSummary LastManualAllow { get; private set; }
        public long LastManualAllowTick { get; private set; }
        internal bool RefreshIntelligentRecyclingCompatibility(
                out string status) {
            bool ready = intelligentRecycling.RefreshAvailability();
            status = intelligentRecycling.StatusText;
            return ready;
        }

        [SelfInit]
        private static void Register() {
            GameSystems.Register(SysId, () => new AutoHaulingSys());
        }

        protected override void OnInitialize() {
            string consumerVersion = typeof(AutoHaulingSys).Assembly
                .GetName().Version.ToString(3);
            if (!AutoUtilitiesApi.TryRegisterConsumer(
                    ConsumerId,
                    "Auto Hauling",
                    consumerVersion,
                    RequiredUtilitiesVersion)
                    || !AutoUtilitiesApi.IsFeatureAvailable(
                        ConsumerId, RequiredUtilitiesVersion)) {
                D.Err("[Auto Hauling] AUMF " + RequiredUtilitiesVersion
                    + " registration failed. "
                    + "Automation will remain inactive for this session.");
                frameworkReady = false;
                return;
            }

            try {
                allowForbiddenShortcut = AutoUtilitiesApi.RegisterShortcut(
                    new ShortcutRegistration {
                        ConsumerId = ConsumerId,
                        Id = "AllowForbiddenLooseCargo",
                        DisplayName = "allow Forbidden loose cargo",
                        PreferenceKey = "autohauling.allowforbidden.shortcut",
                        GameState = S,
                        OnPressed = AllowForbiddenLooseCargo
                    });
                panel = new AutoHaulingPanel(this, allowForbiddenShortcut);
                frameworkReady = true;
            } catch (Exception ex) {
                frameworkReady = false;
                panel?.Dispose();
                panel = null;
                allowForbiddenShortcut?.Dispose();
                allowForbiddenShortcut = null;
                D.LogEx(ex, "[Auto Hauling] Failed to create the AUMF "
                    + "control panel or shortcut. Automation will remain "
                    + "inactive.");
                return;
            }

            S.Sig.AfterLoadState.AddListener(OnAfterLoadState);
        }

        private void OnAfterLoadState(GameState state) {
            DetachLoadedState();
            if (state == null) {
                panel?.RefreshStatus();
                return;
            }

            // Save data can be loaded before Core has fully restored its AI
            // goals and responders. Bind the new state now, then perform one
            // deferred reconciliation after Core completes the load update.
            if (loadedSettingsGameId != state.GameId) {
                ResetToDefaults();
                loadedSettingsGameId = state.GameId;
            }

            loadedState = state;
            loadedObjsSystem = ResolveObjsSystem(state);
            panel?.Close();
            nextScanTick = state.Ticks + ScanIntervalTicks;
            nextTrackedGoalReconcileTick =
                state.Ticks + TrackedGoalReconcileIntervalTicks;
            trackedGoalsReady = false;
            intelligentRecycling.RefreshAvailability();
            partRestorationUnit.RefreshAvailability();
            state.Clock.OnTick.AddListener(OnTick);
            state.Sig.AIRemoveGoal.AddListener(OnGoalRemoved);
            state.Clock.OnNextUpdate(() => {
                if (loadedState != state) {
                    return;
                }
                trackedGoalsReady = true;
                ReconcileTrackedGoals();
                nextTrackedGoalReconcileTick =
                    state.Ticks + TrackedGoalReconcileIntervalTicks;
                panel?.RefreshStatus();
            });
            panel?.RefreshStatus();
            D.Warn("[Auto Hauling] Initialized. Master automation is {0}.",
                Enabled ? "enabled" : "disabled");
        }

        private void DetachLoadedState() {
            if (loadedState != null) {
                loadedState.Clock.OnTick.RemoveListener(OnTick);
                loadedState.Sig.AIRemoveGoal.RemoveListener(OnGoalRemoved);
            }
            loadedState = null;
            loadedObjsSystem = null;
            trackedGoalSnapshot.Clear();
            nextTrackedGoalReconcileTick = 0L;
            trackedGoalsReady = false;
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
            // Failure diagnostics and cargo discovery have independent
            // cadences: tracked goals need a short safety poll, while the much
            // larger cargo scan remains on its established 300-tick interval.
            if (trackedGoalsReady
                    && trackedGoals.Count > 0
                    && ticks >= nextTrackedGoalReconcileTick) {
                nextTrackedGoalReconcileTick =
                    ticks + TrackedGoalReconcileIntervalTicks;
                ReconcileTrackedGoals();
            }
            if (!frameworkReady || !Enabled || ticks < nextScanTick) {
                return;
            }
            nextScanTick = ticks + ScanIntervalTicks;
            ScanLooseCargo();
        }

        public void SetEnabled(bool value) {
            if (value && !frameworkReady) {
                D.Err("[Auto Hauling] Cannot enable automation because the "
                    + "AUMF control surface is unavailable.");
                return;
            }
            Enabled = value;
            if (value && loadedState != null) {
                nextScanTick = Math.Min(nextScanTick, loadedState.Ticks + 1L);
            }
            panel?.RefreshAll();
        }

        public void SetStaleStoreRefreshEnabled(bool value) {
            StaleStoreRefreshEnabled = value;
            SchedulePromptScan();
            panel?.RefreshAll();
        }

        internal List<HaulItemOption> GetItemOptions() {
            Dictionary<string, HaulItemOption> options =
                new Dictionary<string, HaulItemOption>(StringComparer.Ordinal);

            foreach (MatType material in MatType.Known.Values) {
                HaulItemOption option = BuildMaterialOption(material);
                if (option != null) {
                    options[option.ItemKey] = option;
                }
            }

            foreach (Def def in The.Defs.Defs.Values) {
                HaulItemOption option = BuildObjectOption(def, null);
                if (option != null) {
                    options[option.ItemKey] = option;
                }
            }

            return options.Values
                .OrderBy(option => option.Domain)
                .ThenBy(option => option.CategoryName,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => option.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => option.ItemId, StringComparer.Ordinal)
                .ToList();
        }

        internal bool IsItemEnabled(HaulItemOption option) {
            if (option == null) {
                return false;
            }
            if (enabledItemOverrides.Contains(option.ItemKey)) {
                return true;
            }
            if (disabledItemOverrides.Contains(option.ItemKey)) {
                return false;
            }
            return !disabledCategoryIds.Contains(option.CategoryKey);
        }

        internal void SetCategoryEnabled(string categoryKey, bool value) {
            if (string.IsNullOrEmpty(categoryKey)) {
                return;
            }
            if (value) {
                disabledCategoryIds.Remove(categoryKey);
            } else {
                disabledCategoryIds.Add(categoryKey);
            }

            foreach (HaulItemOption option in GetItemOptions()) {
                if (!string.Equals(option.CategoryKey, categoryKey,
                        StringComparison.Ordinal)) {
                    continue;
                }
                enabledItemOverrides.Remove(option.ItemKey);
                disabledItemOverrides.Remove(option.ItemKey);
            }
            SchedulePromptScan();
            panel?.RefreshAll();
        }

        internal void SetItemEnabled(HaulItemOption option, bool value) {
            if (option == null) {
                return;
            }
            enabledItemOverrides.Remove(option.ItemKey);
            disabledItemOverrides.Remove(option.ItemKey);
            bool categoryDefault =
                !disabledCategoryIds.Contains(option.CategoryKey);
            if (value != categoryDefault) {
                if (value) {
                    enabledItemOverrides.Add(option.ItemKey);
                } else {
                    disabledItemOverrides.Add(option.ItemKey);
                }
            }
            SchedulePromptScan();
            panel?.RefreshAll();
        }

        private void SchedulePromptScan() {
            if (Enabled && loadedState != null) {
                nextScanTick = Math.Min(nextScanTick, loadedState.Ticks + 1L);
            }
        }

        internal ForbiddenLooseSummary PreviewForbiddenLooseCargo() {
            return ProcessForbiddenLooseCargo(clear: false);
        }

        internal void AllowForbiddenLooseCargo() {
            if (loadedState == null) {
                return;
            }
            LastManualAllow = ProcessForbiddenLooseCargo(clear: true);
            LastManualAllowTick = loadedState.Ticks;
            SchedulePromptScan();
            panel?.RefreshAll();
        }

        private ForbiddenLooseSummary ProcessForbiddenLooseCargo(bool clear) {
            ForbiddenLooseSummary result = new ForbiddenLooseSummary();
            if (loadedState == null) {
                return result;
            }

            // This is a deliberate one-shot global action, so it uses Core's
            // complete object index rather than the recurring scan's active
            // list. The same pass powers the preview and the actual override.
            Dictionary<uint, ObjData> allObjects = S.Query.GetAllObjs.Ask();
            if (allObjects == null) {
                return result;
            }

            foreach (ObjData data in allObjects.Values) {
                if (!IsActiveLooseData(data)
                        || data.Obj.IsCarriedByAny) {
                    continue;
                }

                bool forbidden;
                bool usePile;
                if (data.Obj.Pile != null && !data.Obj.Pile.TreatAsObj) {
                    forbidden = data.Obj.Pile.IsForbidden;
                    usePile = true;
                } else if (data.Obj.StorableObj != null) {
                    forbidden = data.Obj.StorableObj.IsForbidden;
                    usePile = false;
                } else {
                    continue;
                }
                if (!forbidden) {
                    continue;
                }

                result.Allowed++;
                if (!clear) {
                    continue;
                }
                if (usePile) {
                    data.Obj.Pile.IsForbidden = false;
                } else {
                    data.Obj.StorableObj.IsForbidden = false;
                }
            }
            return result;
        }

        private void ScanLooseCargo() {
            ResetLastScan();
            GameState state = loadedState;
            if (state == null) {
                panel?.RefreshStatus();
                return;
            }
            LastScanTick = state.Ticks;
            bool intelligentRecyclingReady =
                intelligentRecycling.RefreshAvailability();
            bool partRestorationReady =
                partRestorationUnit.RefreshAvailability();
            List<ObjData> activeObjects = loadedObjsSystem?.ActiveObjs;
            if (activeObjects == null) {
                loadedObjsSystem = ResolveObjsSystem(loadedState);
                activeObjects = loadedObjsSystem?.ActiveObjs;
            }
            if (activeObjects == null) {
                panel?.RefreshStatus();
                return;
            }

            // Always clear cross-pass scratch state. This keeps a prior
            // interrupted scan from carrying a stale priority into a later
            // scan where stale-task refresh may have been disabled.
            staleStoreGoalSnapshot.Clear();
            staleStorePriorities.Clear();
            if (StaleStoreRefreshEnabled) {
                RefreshStaleStoreTasks(
                    LastScanTick,
                    intelligentRecyclingReady,
                    partRestorationReady);
            }

            for (int i = 0; i < activeObjects.Count; i++) {
                ObjData data = activeObjects[i];
                if (data == null || data.Id == 0u
                        || !IsActiveLooseData(data)) {
                    continue;
                }

                if (GetHardExclusion(data) != HardExclusion.None) {
                    LastProtected++;
                    continue;
                }

                if (!TryResolveCandidate(data, out HaulItemOption option,
                        out UnstoredMatComp pile,
                        out StorableObjComp storable)) {
                    continue;
                }
                LastScanned++;

                // Structural exclusions run first, followed by cooperative
                // ownership, saved settings, transient task/availability
                // gates, and finally Core's ordinary Store-order helper.
                bool qualityBearing = IsQualityBearing(data);
                if (ShouldHoldForPartRestoration(
                        data, partRestorationReady)) {
                    LastHeldByPartRestorationUnit++;
                    continue;
                }
                if (qualityBearing && pile != null) {
                    // Intelligent Recycling's cooperative contract is per
                    // ObjData/storable object, not a material-pile policy.
                    LastProtected++;
                    continue;
                }
                if (qualityBearing
                        && (!intelligentRecyclingReady
                            || !intelligentRecycling.TryAllowsAutoHauling(
                                data.Id))) {
                    LastHeldByIntelligentRecycling++;
                    continue;
                }

                if (!IsItemEnabled(option)) {
                    LastDisabled++;
                    continue;
                }
                if (data.Obj.Goal != null) {
                    LastBlockedByTask++;
                    continue;
                }
                if (data.Obj.IsCarriedByAny || !data.IsAvailable) {
                    LastUnavailable++;
                    continue;
                }

                if (pile != null) {
                    if (pile.IsForbidden) {
                        LastForbidden++;
                        continue;
                    }
                    // Direct TryOrderStore does not apply Core's automatic
                    // unreachable-pile gate. Respect it here without clearing
                    // the marker or treating it as a player Forbidden flag.
                    if (pile.IsMarkedUnreachable) {
                        LastUnavailable++;
                        continue;
                    }
                    if (!pile.HasCompatibleStorage()) {
                        LastNoStorage++;
                        continue;
                    }
                    if (!pile.TryOrderStore()) {
                        RecordOrderRejection(data, option,
                            "Stardeus rejected the material Store order after "
                            + "the eligibility checks passed.");
                        continue;
                    }
                } else {
                    if (storable.IsForbidden) {
                        LastForbidden++;
                        continue;
                    }
                    // TryOrderStoring can divert unusable items to disposal.
                    // This guard is a hard hauling-only boundary.
                    if (!storable.IsItemUsable) {
                        LastUnavailable++;
                        continue;
                    }
                    if (!storable.HasCompatibleStorage()) {
                        LastNoStorage++;
                        continue;
                    }
                    if (!storable.TryOrderStoring(
                            byPlayer: false,
                            checkAvailableStorage: true,
                            out string failReason)) {
                        RecordOrderRejection(data, option,
                            string.IsNullOrWhiteSpace(failReason)
                                ? "Stardeus rejected the object Store order "
                                    + "after the eligibility checks passed."
                                : failReason);
                        continue;
                    }
                }

                AIGoal goal = data.Obj.Goal;
                if (goal == null) {
                    RecordOrderRejection(data, option,
                        "Stardeus reported a successful Store order but no "
                        + "vanilla goal was attached to the item.");
                    continue;
                }
                if (staleStorePriorities.TryGetValue(
                        data.Id, out byte priorPriority)) {
                    goal.SetPriority(LastScanTick, priorPriority);
                    LastStaleStoreTasksRequeued++;
                }
                LastQueued++;
                TrackGoal(data.Id, data, option, goal);
            }

            staleStorePriorities.Clear();
            panel?.RefreshStatus();
        }

        private void RefreshStaleStoreTasks(
                long ticks,
                bool intelligentRecyclingReady,
                bool partRestorationReady) {
            // Snapshot first because cancelling mutates Core's colony-goal
            // dictionary. Every candidate is then revalidated immediately
            // before cancellation to avoid acting on state that changed while
            // the snapshot was being assembled.
            staleStoreGoalSnapshot.Clear();
            staleStorePriorities.Clear();
            Dictionary<uint, AIGoal> colonyGoals =
                loadedState?.Sys?.AI?.ColonyGoals;
            if (colonyGoals == null || colonyGoals.Count == 0) {
                return;
            }

            foreach (AIGoal goal in colonyGoals.Values) {
                if (TryResolveStaleStoreTask(
                        goal,
                        ticks,
                        intelligentRecyclingReady,
                        partRestorationReady,
                        out _,
                        out _,
                        out _)) {
                    staleStoreGoalSnapshot.Add(goal);
                }
            }

            LastStaleStoreTasksFound = staleStoreGoalSnapshot.Count;
            foreach (AIGoal goal in staleStoreGoalSnapshot) {
                if (!TryResolveStaleStoreTask(
                        goal,
                        ticks,
                        intelligentRecyclingReady,
                        partRestorationReady,
                        out ObjData data,
                        out UnstoredMatComp pile,
                        out _)) {
                    continue;
                }

                // Core deliberately marks a material pile Forbidden whenever
                // its Store task is cancelled. This refresh owns that exact
                // cancellation, so restore only the pre-existing allowed
                // state rather than broadening the manual Forbidden action.
                bool restoreAllowedPile = pile != null
                    && !pile.IsForbidden;
                staleStorePriorities[data.Id] = goal.GoalPriority;
                AIGoal.Cancel(
                    goal,
                    ticks,
                    "Auto Hauling refreshed this Store task after it waited "
                        + "more than two in-game days for a worker.");
                if (restoreAllowedPile
                        && data.Obj?.Pile == pile
                        && data.Obj.IsActive
                        && pile.IsForbidden) {
                    pile.IsForbidden = false;
                }
                LastStaleStoreTasksCancelled++;
            }
            staleStoreGoalSnapshot.Clear();
        }

        private bool TryResolveStaleStoreTask(
                AIGoal goal,
                long ticks,
                bool intelligentRecyclingReady,
                bool partRestorationReady,
                out ObjData data,
                out UnstoredMatComp pile,
                out StorableObjComp storable) {
            // Match Core's exact loose-cargo Store shapes. A Hauling skill by
            // itself is not enough: the task must still be unassigned,
            // continuously bidding beyond the threshold, unrestricted, and
            // safe to recreate through the normal Store path.
            data = null;
            pile = null;
            storable = null;
            if (goal == null
                    || goal.JobIdH != JobTypeIdH.Hauling
                    || goal.State != AIGoalState.Bidding
                    || goal.Agent != null
                    || goal.BestBid.IsValid
                    || goal.GoalPriority == 0
                    || goal.IsNanobotWorkable
                    || (goal.AllowedMindIds != null
                        && goal.AllowedMindIds.Length > 0)
                    || goal.StateStartTick <= 0L
                    || ticks - goal.StateStartTick <= Consts.TicksPer2Days
                    || !goal.IsAvailabilityGateOpen(loadedState)) {
                return false;
            }

            if (goal.Target is UnstoredMatComp targetPile
                    && !targetPile.TreatAsObj) {
                data = targetPile.Obj?.Data;
            } else if (goal.Target is StorableObjComp targetStorable) {
                data = targetStorable.Obj?.Data;
            } else {
                return false;
            }

            if (!IsActiveLooseData(data)
                    || data.Obj.Goal != goal
                    || GetHardExclusion(data) != HardExclusion.None
                    || !TryResolveCandidate(
                        data, out HaulItemOption option,
                        out pile, out storable)
                    || !IsItemEnabled(option)
                    || data.Obj.IsCarriedByAny
                    || !data.IsAvailable) {
                return false;
            }

            if (ShouldHoldForPartRestoration(
                    data, partRestorationReady)) {
                return false;
            }

            bool qualityBearing = IsQualityBearing(data);
            if (qualityBearing
                    && (!intelligentRecyclingReady
                        || !intelligentRecycling.TryAllowsAutoHauling(
                            data.Id))) {
                return false;
            }

            if (pile != null) {
                // Refresh must not undo Core's unreachable suppression by
                // cancelling and recreating work that the scan would refuse.
                return !pile.IsForbidden
                    && !pile.IsMarkedUnreachable
                    && pile.HasCompatibleStorage();
            }
            return storable != null
                && !storable.IsForbidden
                && storable.IsItemUsable
                && storable.HasCompatibleStorage();
        }

        private void ResetLastScan() {
            LastScanned = 0;
            LastQueued = 0;
            LastBlockedByTask = 0;
            LastForbidden = 0;
            LastNoStorage = 0;
            LastDisabled = 0;
            LastProtected = 0;
            LastUnavailable = 0;
            LastRejected = 0;
            LastHeldByIntelligentRecycling = 0;
            LastHeldByPartRestorationUnit = 0;
            LastStaleStoreTasksFound = 0;
            LastStaleStoreTasksCancelled = 0;
            LastStaleStoreTasksRequeued = 0;
        }

        private static bool IsActiveLooseData(ObjData data) {
            return data != null
                && data.State == ObjState.Active
                && data.Obj != null
                && data.Obj.IsActive
                && data.Def != null;
        }

        private static HardExclusion GetHardExclusion(ObjData data) {
            // These are structural safety rules, not user preferences. They
            // are enforced below the UI and cannot be overridden by category
            // settings or automatic stale-task refresh.
            if (data?.Def == null || data.Obj == null) {
                return HardExclusion.BeingOrMind;
            }
            bool qualityBearing = IsQualityBearing(data);
            if (data.Def.IsBeing
                    || data.Mind != null
                    || data.MindId != 0u
                    || (data.BodyPart != null && !qualityBearing)) {
                return HardExclusion.BeingOrMind;
            }
            if (qualityBearing && data.Obj.Pile != null
                    && !data.Obj.Pile.TreatAsObj) {
                return HardExclusion.MaterialQuality;
            }
            if (IsIntelligentRecyclingDefinition(data.Def.Id)) {
                return HardExclusion.IntelligentRecycling;
            }
            return HardExclusion.None;
        }

        private bool ShouldHoldForPartRestoration(
                ObjData data, bool apiReady) {
            if (data?.BodyPart == null || !partRestorationUnit.IsInstalled) {
                return false;
            }

            // PRU's contract grants external automation only through its
            // authoritative release property. Once the optional mod is known
            // to be installed, an incompatible or failed query is no
            // permission to create or replace work for that body part.
            return !apiReady
                || !partRestorationUnit.TryIsReleasedForExternalAutomation(
                    data.Id, out bool released)
                || !released;
        }

        private static bool IsQualityBearing(ObjData data) {
            return data?.Def != null
                && data.Obj != null
                && (data.Def.HasComponent(ObjQualityComponentH)
                    || data.Obj.TryGetComponent<ObjQualityComp>(out _));
        }

        private static bool IsIntelligentRecyclingDefinition(string id) {
            return string.Equals(id, HullpiercerDefinitionId,
                    StringComparison.Ordinal)
                || string.Equals(id, StorageCapsuleDefinitionId,
                    StringComparison.Ordinal)
                || string.Equals(id, ArtilleryFungusDefinitionId,
                    StringComparison.Ordinal);
        }

        private static bool TryResolveCandidate(
                ObjData data,
                out HaulItemOption option,
                out UnstoredMatComp pile,
                out StorableObjComp storable) {
            option = null;
            pile = null;
            storable = null;

            if (data.Obj.Pile != null && !data.Obj.Pile.TreatAsObj) {
                option = BuildMaterialOption(data.Obj.Pile.Type);
                if (option == null) {
                    return false;
                }
                pile = data.Obj.Pile;
                return true;
            }

            if (data.Obj.StorableObj == null) {
                return false;
            }
            option = data.Def.MatType != null
                ? BuildMaterialOption(data.Def.MatType)
                : BuildObjectOption(data.Def, data.Obj.StorableObj.Group);
            if (option == null) {
                return false;
            }
            storable = data.Obj.StorableObj;
            return true;
        }

        private static HaulItemOption BuildMaterialOption(MatType material) {
            if (material == null || string.IsNullOrEmpty(material.Id)
                    || material.Def == null
                    || material.Def.IsBeing
                    || material.Def.HasComponent(ObjQualityComponentH)
                    || IsIntelligentRecyclingDefinition(material.Def.Id)) {
                return null;
            }
            string categoryId = !string.IsNullOrWhiteSpace(material.GroupId)
                ? material.GroupId
                : !string.IsNullOrWhiteSpace(material.Group)
                    ? material.Group
                    : "Other";
            string categoryName = !string.IsNullOrWhiteSpace(
                    material.GroupNameKey)
                ? material.GroupNameT
                : categoryId;
            if (string.IsNullOrWhiteSpace(categoryName)) {
                categoryName = categoryId;
            }
            return new HaulItemOption {
                Domain = HaulDomain.Material,
                ItemKey = "mat:" + material.Id,
                ItemId = material.Id,
                Name = string.IsNullOrWhiteSpace(material.NameT)
                    ? material.Id
                    : material.NameT,
                CategoryKey = "mat:" + categoryId,
                CategoryName = categoryName,
                Icon = !string.IsNullOrWhiteSpace(material.IconId)
                    ? material.IconId
                    : !string.IsNullOrWhiteSpace(material.Def.Preview)
                        ? material.Def.Preview
                        : IconId.CStore,
                QualityBearing = false
            };
        }

        private static HaulItemOption BuildObjectOption(
                Def def, string runtimeGroup) {
            if (def == null
                    || def.IsAbstract
                    || def.IsTemplate
                    || def.IsBeing
                    || def.MatType != null
                    || string.IsNullOrEmpty(def.Id)
                    || IsIntelligentRecyclingDefinition(def.Id)
                    || !def.HasComponent(StorableObjComponentH)) {
                return null;
            }
            string group = runtimeGroup;
            if (string.IsNullOrWhiteSpace(group)) {
                ComponentConfig config = def.ComponentConfigFor(
                    StorableObjComponentH, warn: false);
                group = config?.GetString(GroupPropertyH, null);
            }
            if (string.IsNullOrWhiteSpace(group)) {
                group = "Other";
            }
            return new HaulItemOption {
                Domain = HaulDomain.Object,
                ItemKey = "obj:" + def.Id,
                ItemId = def.Id,
                Name = string.IsNullOrWhiteSpace(def.NameT) ? def.Id : def.NameT,
                CategoryKey = "obj:" + group,
                CategoryName = group,
                Icon = string.IsNullOrWhiteSpace(def.Preview)
                    ? IconId.CStore
                    : def.Preview,
                QualityBearing = def.HasComponent(ObjQualityComponentH)
            };
        }

        private void TrackGoal(uint objectId, ObjData data,
                HaulItemOption option, AIGoal goal) {
            bool wasEmpty = trackedGoals.Count == 0;
            trackedGoals[goal.Id] = new TrackedHaulGoal {
                GoalId = goal.Id,
                ObjectId = objectId,
                LastFailedTotal = goal.TimesFailedTotal,
                ItemName = string.IsNullOrWhiteSpace(data.ListName)
                    ? option.Name
                    : data.ListName,
                ItemId = option.ItemId,
                Position = FormatPosition(data.Obj.GetPosition())
            };
            if (wasEmpty) {
                long ticks = loadedState?.Ticks ?? G.Ticks;
                nextTrackedGoalReconcileTick =
                    ticks + TrackedGoalReconcileIntervalTicks;
            }
        }

        private void RecordOrderRejection(ObjData data, HaulItemOption option,
                string reason) {
            LastRejected++;
            RecordFailure(new HaulFailureRecord {
                Tick = loadedState?.Ticks ?? G.Ticks,
                GoalId = 0u,
                ItemName = string.IsNullOrWhiteSpace(data.ListName)
                    ? option.Name
                    : data.ListName,
                ItemId = option.ItemId,
                Position = FormatPosition(data.Obj.GetPosition()),
                Reason = reason,
                State = "Order rejected",
                Attempt = 1,
                Retrying = false
            });
        }

        private void ReconcileTrackedGoals() {
            // Core's removal signal handles ordinary completion/cancellation
            // immediately. This bounded safety pass catches state/failure
            // changes that can happen before final removal; no suitable
            // production goal-state callback exists for those transitions.
            if (trackedGoals.Count == 0) {
                trackedGoalSnapshot.Clear();
                return;
            }
            bool changed = false;
            trackedGoalSnapshot.Clear();
            trackedGoalSnapshot.AddRange(trackedGoals.Values);
            foreach (TrackedHaulGoal tracked in trackedGoalSnapshot) {
                AIGoal goal = S.Query.FindAIGoal.Ask(
                    tracked.GoalId, false);
                if (goal == null
                        || !GoalTargetsObject(goal, tracked.ObjectId)) {
                    trackedGoals.Remove(tracked.GoalId);
                    changed = true;
                    continue;
                }
                if (CaptureGoalFailure(tracked, goal)) {
                    changed = true;
                }
            }
            trackedGoalSnapshot.Clear();
            if (changed) {
                panel?.RefreshStatus();
            }
        }

        private void OnGoalRemoved(AIGoal goal) {
            if (goal == null
                    || !trackedGoals.TryGetValue(
                        goal.Id, out TrackedHaulGoal tracked)) {
                return;
            }
            if (!GoalTargetsObject(goal, tracked.ObjectId)) {
                trackedGoals.Remove(goal.Id);
                panel?.RefreshStatus();
                return;
            }
            bool recorded = CaptureGoalFailure(tracked, goal);
            if (!recorded && tracked.LastFailedTotal == 0
                    && IsUserVisibleFailureState(goal.State)) {
                RecordGoalFailure(tracked, goal, 1);
            }
            trackedGoals.Remove(goal.Id);
            panel?.RefreshStatus();
        }

        private bool CaptureGoalFailure(
                TrackedHaulGoal tracked, AIGoal goal) {
            // TimesFailedTotal is monotonic for a goal. Persisting the last
            // observed total prevents both the short poll and removal signal
            // from recording the same Core attempt twice.
            if (goal.TimesFailedTotal <= tracked.LastFailedTotal) {
                return false;
            }
            tracked.LastFailedTotal = goal.TimesFailedTotal;
            if (goal.State == AIGoalState.Cancelled
                    || goal.State == AIGoalState.Interrupted) {
                // Core groups Interrupted with some failure helpers, but a
                // player or competing direct order can legitimately interrupt
                // work. Do not present that as a haul failure.
                return true;
            }
            RecordGoalFailure(tracked, goal, goal.TimesFailedTotal);
            return true;
        }

        private static bool GoalTargetsObject(AIGoal goal, uint objectId) {
            if (goal == null || objectId == 0u) {
                return false;
            }
            if (goal.Target is UnstoredMatComp pile) {
                return pile.Obj?.Data?.Id == objectId;
            }
            if (goal.Target is StorableObjComp storable) {
                return storable.Obj?.Data?.Id == objectId;
            }
            return false;
        }

        private void RecordGoalFailure(
                TrackedHaulGoal tracked, AIGoal goal, int attempt) {
            string reason = string.IsNullOrWhiteSpace(goal.FailReason)
                ? "Stardeus supplied no failure reason (state: "
                    + goal.State + ")."
                : goal.FailReason;
            bool terminal = goal.State == AIGoalState.Completed
                || goal.State == AIGoalState.Cancelled
                || goal.State == AIGoalState.Interrupted
                || goal.State == AIGoalState.Error
                || goal.State == AIGoalState.ErrorLooping;
            bool retrying = !goal.NonRetryable && !terminal;
            RecordFailure(new HaulFailureRecord {
                Tick = loadedState?.Ticks ?? G.Ticks,
                GoalId = goal.Id,
                ItemName = tracked.ItemName,
                ItemId = tracked.ItemId,
                Position = tracked.Position,
                Reason = reason,
                State = goal.State.ToString(),
                Attempt = Math.Max(1, attempt),
                Retrying = retrying
            });
        }

        private static bool IsUserVisibleFailureState(AIGoalState state) {
            return state == AIGoalState.Failed
                || state == AIGoalState.CoolingDown
                || state == AIGoalState.Error
                || state == AIGoalState.ErrorLooping;
        }

        private void RecordFailure(HaulFailureRecord record) {
            // In-game diagnostics only. Vanilla retry cycles can generate
            // thousands of entries, so never mirror this history to D.* or
            // Player.log.
            HaulFailureRecord last = failureHistory.Count == 0
                ? null
                : failureHistory[failureHistory.Count - 1];
            bool repeated = last != null
                && last.GoalId == record.GoalId
                && string.Equals(last.ItemId, record.ItemId,
                    StringComparison.Ordinal)
                && string.Equals(last.Position, record.Position,
                    StringComparison.Ordinal)
                && string.Equals(last.Reason, record.Reason,
                    StringComparison.Ordinal);
            if (repeated) {
                last.Tick = record.Tick;
                last.State = record.State;
                last.Attempt = Math.Max(last.Attempt, record.Attempt);
                last.Retrying = record.Retrying;
                if (last.RepeatCount < int.MaxValue) {
                    last.RepeatCount++;
                }
            } else {
                failureHistory.Add(record);
                while (failureHistory.Count > MaxFailureRecords) {
                    failureHistory.RemoveAt(0);
                }
            }
            panel?.RefreshStatus();
        }

        internal void ClearFailureHistory() {
            failureHistory.Clear();
            panel?.RefreshAll();
        }

        private static string FormatPosition(Vector2 position) {
            return "(" + position.x.ToString("0.0", Invariant)
                + ", " + position.y.ToString("0.0", Invariant) + ")";
        }

        public ComponentData Save() {
            // Core's save container supports primitive parallel arrays. Sort
            // tracked goals by ID so every array preserves the same stable
            // index relationship across saves.
            saveData ??= new ComponentData(0, SysId);
            saveData.SetInt("SaveSchemaVersion", SaveSchemaVersion);
            saveData.SetBool("Enabled", Enabled);
            saveData.SetBool("StaleStoreRefreshEnabled",
                StaleStoreRefreshEnabled);
            // Preserve the old preference without activating a private auction.
            saveData.SetBool("IdleHaulingBidAssistEnabled",
                legacyIdleHaulingBidAssistEnabled);
            saveData.SetStringSet("DisabledCategoryIds",
                disabledCategoryIds.OrderBy(value => value).ToArray());
            saveData.SetStringSet("EnabledItemOverrides",
                enabledItemOverrides.OrderBy(value => value).ToArray());
            saveData.SetStringSet("DisabledItemOverrides",
                disabledItemOverrides.OrderBy(value => value).ToArray());

            saveData.SetLong("LastScanTick", LastScanTick);
            saveData.SetInt("LastScanned", LastScanned);
            saveData.SetInt("LastQueued", LastQueued);
            saveData.SetInt("LastBlockedByTask", LastBlockedByTask);
            saveData.SetInt("LastForbidden", LastForbidden);
            saveData.SetInt("LastNoStorage", LastNoStorage);
            saveData.SetInt("LastDisabled", LastDisabled);
            saveData.SetInt("LastProtected", LastProtected);
            saveData.SetInt("LastUnavailable", LastUnavailable);
            saveData.SetInt("LastRejected", LastRejected);
            saveData.SetInt("LastHeldByIntelligentRecycling",
                LastHeldByIntelligentRecycling);
            saveData.SetInt("LastHeldByPartRestorationUnit",
                LastHeldByPartRestorationUnit);
            saveData.SetInt("LastStaleStoreTasksFound",
                LastStaleStoreTasksFound);
            saveData.SetInt("LastStaleStoreTasksCancelled",
                LastStaleStoreTasksCancelled);
            saveData.SetInt("LastStaleStoreTasksRequeued",
                LastStaleStoreTasksRequeued);
            // Retired assist counters already present in saveData are left
            // untouched so a rollback can still display its historical data.

            saveData.SetLong("LastManualAllowTick", LastManualAllowTick);
            saveData.SetInt("LastManualAllowed", LastManualAllow.Allowed);

            TrackedHaulGoal[] tracked = trackedGoals.Values
                .OrderBy(value => value.GoalId).ToArray();
            saveData.SetIntSet("TrackedGoalIds", tracked
                .Select(value => unchecked((int) value.GoalId)).ToArray());
            saveData.SetIntSet("TrackedObjectIds", tracked
                .Select(value => unchecked((int) value.ObjectId)).ToArray());
            saveData.SetIntSet("TrackedFailureTotals", tracked
                .Select(value => (int) value.LastFailedTotal).ToArray());
            saveData.SetStringSet("TrackedItemNames", tracked
                .Select(value => value.ItemName ?? string.Empty).ToArray());
            saveData.SetStringSet("TrackedItemIds", tracked
                .Select(value => value.ItemId ?? string.Empty).ToArray());
            saveData.SetStringSet("TrackedPositions", tracked
                .Select(value => value.Position ?? string.Empty).ToArray());
            saveData.SetStringSet("FailureHistory", failureHistory
                .Select(record => record.Serialize()).ToArray());
            return saveData;
        }

        public void Load(ComponentData data) {
            if (data == null) {
                ResetToDefaults();
                return;
            }
            // Keep the original container, including retired diagnostic keys.
            saveData = data;
            loadedSettingsGameId = S.GameId;
            Enabled = data.GetBool("Enabled", false);
            int loadedSchemaVersion = data.GetInt("SaveSchemaVersion", 0);
            StaleStoreRefreshEnabled = loadedSchemaVersion >= 2
                && data.GetBool("StaleStoreRefreshEnabled", true);
            legacyIdleHaulingBidAssistEnabled = loadedSchemaVersion >= 3
                && data.GetBool("IdleHaulingBidAssistEnabled", false);

            LoadSet(data.GetStringSet("DisabledCategoryIds", null),
                disabledCategoryIds);
            LoadSet(data.GetStringSet("EnabledItemOverrides", null),
                enabledItemOverrides);
            LoadSet(data.GetStringSet("DisabledItemOverrides", null),
                disabledItemOverrides);

            // Every newly added field has a conservative default, retaining
            // compatibility with saves from all earlier schemas.
            LastScanTick = Math.Max(0L, data.GetLong("LastScanTick", 0L));
            LastScanned = Math.Max(0, data.GetInt("LastScanned", 0));
            LastQueued = Math.Max(0, data.GetInt("LastQueued", 0));
            LastBlockedByTask = Math.Max(0,
                data.GetInt("LastBlockedByTask", 0));
            LastForbidden = Math.Max(0, data.GetInt("LastForbidden", 0));
            LastNoStorage = Math.Max(0, data.GetInt("LastNoStorage", 0));
            LastDisabled = Math.Max(0, data.GetInt("LastDisabled", 0));
            LastProtected = Math.Max(0, data.GetInt("LastProtected", 0));
            LastUnavailable = Math.Max(0,
                data.GetInt("LastUnavailable", 0));
            LastRejected = Math.Max(0, data.GetInt("LastRejected", 0));
            LastHeldByIntelligentRecycling = Math.Max(0,
                data.GetInt("LastHeldByIntelligentRecycling", 0));
            LastHeldByPartRestorationUnit = Math.Max(0,
                data.GetInt("LastHeldByPartRestorationUnit", 0));
            LastStaleStoreTasksFound = Math.Max(0,
                data.GetInt("LastStaleStoreTasksFound", 0));
            LastStaleStoreTasksCancelled = Math.Max(0,
                data.GetInt("LastStaleStoreTasksCancelled", 0));
            LastStaleStoreTasksRequeued = Math.Max(0,
                data.GetInt("LastStaleStoreTasksRequeued", 0));

            LastManualAllowTick = data.GetLong("LastManualAllowTick", 0L);
            LastManualAllow = new ForbiddenLooseSummary {
                Allowed = Math.Max(0, data.GetInt("LastManualAllowed", 0))
            };

            LoadTrackedGoals(data);
            failureHistory.Clear();
            string[] serializedFailures =
                data.GetStringSet("FailureHistory", null);
            if (serializedFailures != null) {
                foreach (string serialized in serializedFailures) {
                    if (HaulFailureRecord.TryDeserialize(
                            serialized, out HaulFailureRecord record)) {
                        failureHistory.Add(record);
                    }
                }
            }
            while (failureHistory.Count > MaxFailureRecords) {
                failureHistory.RemoveAt(0);
            }
        }

        private void LoadTrackedGoals(ComponentData data) {
            // Parallel-array length mismatches can occur in old or partially
            // written data. Restore only the common complete prefix.
            trackedGoals.Clear();
            int[] goalIds = data.GetIntSet("TrackedGoalIds", null);
            int[] objectIds = data.GetIntSet("TrackedObjectIds", null);
            int[] failureTotals =
                data.GetIntSet("TrackedFailureTotals", null);
            string[] itemNames =
                data.GetStringSet("TrackedItemNames", null);
            string[] itemIds = data.GetStringSet("TrackedItemIds", null);
            string[] positions = data.GetStringSet("TrackedPositions", null);
            if (goalIds == null || objectIds == null
                    || failureTotals == null || itemNames == null
                    || itemIds == null || positions == null) {
                return;
            }
            int count = new[] {
                goalIds.Length,
                objectIds.Length,
                failureTotals.Length,
                itemNames.Length,
                itemIds.Length,
                positions.Length
            }.Min();
            for (int i = 0; i < count; i++) {
                uint goalId = unchecked((uint) goalIds[i]);
                if (goalId == 0u) {
                    continue;
                }
                trackedGoals[goalId] = new TrackedHaulGoal {
                    GoalId = goalId,
                    ObjectId = unchecked((uint) objectIds[i]),
                    LastFailedTotal = (ushort) Math.Max(0,
                        Math.Min(ushort.MaxValue, failureTotals[i])),
                    ItemName = itemNames[i],
                    ItemId = itemIds[i],
                    Position = positions[i]
                };
            }
        }

        private static void LoadSet(
                string[] values, HashSet<string> destination) {
            destination.Clear();
            if (values == null) {
                return;
            }
            foreach (string value in values) {
                if (!string.IsNullOrEmpty(value)) {
                    destination.Add(value);
                }
            }
        }

        private void ResetToDefaults() {
            saveData = null;
            loadedSettingsGameId = -1;
            Enabled = false;
            StaleStoreRefreshEnabled = true;
            legacyIdleHaulingBidAssistEnabled = false;
            disabledCategoryIds.Clear();
            enabledItemOverrides.Clear();
            disabledItemOverrides.Clear();
            trackedGoals.Clear();
            trackedGoalSnapshot.Clear();
            staleStoreGoalSnapshot.Clear();
            staleStorePriorities.Clear();
            failureHistory.Clear();
            LastScanTick = 0L;
            nextScanTick = 0L;
            nextTrackedGoalReconcileTick = 0L;
            ResetLastScan();
            LastManualAllow = new ForbiddenLooseSummary();
            LastManualAllowTick = 0L;
        }

        public override void Unload() {
            S?.Sig?.AfterLoadState.RemoveListener(OnAfterLoadState);
            DetachLoadedState();
            panel?.Dispose();
            panel = null;
            allowForbiddenShortcut?.Dispose();
            allowForbiddenShortcut = null;
            frameworkReady = false;
            trackedGoals.Clear();
            trackedGoalSnapshot.Clear();
            staleStoreGoalSnapshot.Clear();
            staleStorePriorities.Clear();
            failureHistory.Clear();
        }
    }
}
