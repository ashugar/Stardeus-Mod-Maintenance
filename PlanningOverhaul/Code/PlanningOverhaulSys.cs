using System;
using System.Collections.Generic;
using System.Linq;
using Stopwatch = System.Diagnostics.Stopwatch;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Commands;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Tools;
using Game.Utils;
using HarmonyLib;
using KL.Clock;
using KL.Grid;
using KL.Utils;
using UnityEngine;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys : GameSystem, ISaveable
{
    public const string SysId = "PlanningOverhaulSys";
    public const string ConsumerId = "PlanningOverhaul";
    public const string RequiredUtilitiesVersion = "1.4.0";
    private const string HarmonyId = "jleas.stardeus.planningoverhaul";
    private const int SaveSchemaVersion = 3;
    private const long AutomationIntervalTicks = 24;
    private const long MaterialWaitRecheckIntervalTicks =
        AutomationIntervalTicks * 10;
    private const long StructuralBatchBudgetMilliseconds = 6;
    private const int StructuralOrdersPerTick = 24;
    private const int StructuralCandidatesPerTick = 64;
    private const long StructuralBatchUiIntervalTicks = 15;
    private const long SlowOperationMilliseconds = 25;
    private const float PerfLogCooldownSeconds = 3f;

    private readonly List<PlanRoom> rooms = new();
    private readonly List<PlanEntry> entries = new();
    private readonly PlanningOverhaulPlanIndex planIndex = new();
    private readonly Dictionary<int, int> ownedOverlay = new();
    private readonly Dictionary<int, int> roomColorOrdinals = new();
    private readonly Dictionary<string, int> buildingColorOrdinals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> upgradeCompatibilityCounts = new(StringComparer.Ordinal);
    private readonly HashSet<int> stagingBlockedRooms = new();
    private readonly HashSet<int> broaderRelocationBlockedRooms = new();
    private readonly HashSet<int> floorFrontierBlockedRooms = new();
    private HashSet<PlanEntry> activePulseAllowedEntries;
    private readonly HashSet<PlanEntry> activePulseMaterialLimitedEntries = new();
    private readonly Dictionary<MatType, int> activePulseExistingMaterialCommitments = new();
    private readonly Dictionary<MatType, int> activePulseMaterialCaps = new();
    private readonly Dictionary<MatType, int> activePulseCommittedCosts = new();
    private readonly List<PlanEntry> activePulseQueue = new();
    private readonly HashSet<PlanEntry> activePulsePendingEntries = new();
    private readonly HashSet<PlanEntry> activePulseMainEntries = new();
    private readonly List<PlanEntry> activePulseReadyFloors = new();
    private readonly HashSet<PlanEntry> activePulseReadyFloorEntries = new();
    private readonly int[] footprintBuffer = new int[4096];
    private PlanningOverhaulUi ui;
    private GameState loadedState;
    private Harmony harmony;
    private int currentRoomId;
    private int nextRoomId = 1;
    private int manualPulseRoomId;
    private int activePulseRoomId;
    private int continuousRoomId;
    private int continuousRoomCursorId;
    private int stagingValidationPasses;
    private int floorFrontierValidationPasses;
    private bool activePulseIsManual;
    private bool activePulseQueueInitialized;
    private bool activePulseYieldsWhenQueueDrains;
    private int activePulseQueueTotal;
    private int activePulseQueuedOrders;
    private int activePulseDeferredWithoutProgress;
    private int activePulseQueueHead;
    private int activePulseReadyFloorHead;
    private ClockSpeed speedBeforePlacement = ClockSpeed.Stopped;
    private bool restoreSpeedAfterPlacement;
    private long nextAutomationTick;
    private long nextGoalPriorityTick;
    private long nextStructuralBatchUiTick;
    private long nextStagingValidationTick;
    private long nextFloorFrontierValidationTick;
    private float nextAutomationPerfLogTime;
    private float nextOverlayPerfLogTime;
    private float nextPreviewPerfLogTime;
    private float nextUpgradePerfLogTime;
    private float nextPlacementPerfLogTime;
    private float nextShipCapturePerfLogTime;
    private float nextUiBuildPerfLogTime;
    private float nextSavePerfLogTime;
    private float nextEquipmentStatePerfLogTime;
    private float nextEquipmentSourcePerfLogTime;
    private float nextStructuralStatusPerfLogTime;
    private IReadOnlyList<Tile> upgradeCompatibilitySelection;
    private EquipmentConstructionPreview continuousConstructionPreview;
    private bool batchingUpgradeReplacement;
    private int suppressedUpgradeSelectionRefreshes;
    private string blueprintName = "Construction Plan";
    private string lastStatus = "Create a room or begin placing buildings.";
    private PlanColorKey planColorKey = PlanColorKey.Room;

    public static PlanningOverhaulSys Instance { get; private set; }
    public override string Id => SysId;
    public override bool SkipInSandbox => false;

    internal IReadOnlyList<PlanRoom> Rooms => rooms;
    internal IReadOnlyList<PlanEntry> Entries => entries;
    internal PlanRoom CurrentRoom => rooms.FirstOrDefault(room => room.Id == currentRoomId);
    internal int CurrentRoomId => currentRoomId;
    internal int ContinuousRoomId => continuousRoomId;
    internal bool Continuous { get; private set; }
    internal bool IsGamePaused => S?.Clock == null
        || S.Clock.Speed == ClockSpeed.Stopped || S.Clock.IsForceStopped;
    internal bool CaptureActive { get; private set; }
    internal string CaptureDefId { get; private set; }
    internal string BlueprintName => blueprintName;
    internal string CurrentMapSize => S.GridWidth + " × " + S.GridHeight;
    internal string LastStatus => !string.IsNullOrWhiteSpace(lastStatus) ? lastStatus
        : Continuous ? "Continuous construction is on. Checking the Construction Plan."
        : "Continuous construction is off.";
    internal bool ColorByBuildingType => planColorKey == PlanColorKey.BuildingType;
    internal string ColorKeyName => ColorByBuildingType ? "Building type" : "Room";
    internal EquipmentConstructionPreview ContinuousConstructionPreview =>
        continuousConstructionPreview;
    private int ActivePulseQueueCount => activePulsePendingEntries.Count;

    [SelfInit]
    private static void Register()
    {
        GameSystems.Register(SysId, () => new PlanningOverhaulSys());
    }

    protected override void OnInitialize()
    {
        string version = typeof(PlanningOverhaulSys).Assembly.GetName()
            .Version.ToString(3);
        if (!PlanningOverhaulLog.TryStartSession(version,
                out string logError))
            UnityEngine.Debug.LogWarning("[Planning Overhaul] Could not create "
                + "PlanningOverhaul.log; private diagnostics are disabled: "
                + logError);
        Instance = this;
        EnsureDefaultRoom();
        harmony = new Harmony(HarmonyId);
        harmony.PatchAll(typeof(PlanningOverhaulSys).Assembly);
        PlanningOverhaulLog.Info("Version " + version + " active; when Auto Factory is loaded, its Nano Fabricator, Nano Foundry, and Nano Cloning Pod production queues are excluded from blueprint capture and restoration because Auto Factory owns those queues dynamically; existing PO4 production metadata for those definitions is ignored only while Auto Factory is present; the optional check has no compile-time Auto Factory dependency; Continuous construction completes a non-wrapping room rotation before separate production, Transporter, upgrade, and planter configuration passes; every configuration status remains visible for a full 24-tick interval; final configuration waits report category counts and concrete first waits; planter crop and Auto Harvest settings use Core's planter controls; planters without saved metadata are reported without blocking completion; configuration never determines room completeness; Core's null crafting-order list is treated as empty; room checks run every 24 game ticks; planned floors require a cardinally adjacent completed floor and disconnected plans halt with an actionable status; research-locked structural and equipment phases yield their room while later rooms continue; moves into staging are admitted one at a time; moves to ready final blueprint destinations share a priority-5 pool capped at half the living player-faction crew, rounded down with a minimum of one; positive player-selected priorities on active plan construction remain unchanged; material batches subtract outstanding plan demand and material waits recheck on a bounded interval; and paused room selection drives Status and the Material gate.");
        PlanningOverhaulLog.Info("Rooms waiting only on active Core plan tasks yield to later rooms; active room construction blocks another construction batch there but still permits eligible equipment moves; a complete pass with no new work or observed task-state change shows one recovery warning per unchanged task fingerprint.");
        PlanningOverhaulLog.Info("Nanobot Hives and runtime-detected Quantum Storage endpoints are prioritized after connected power infrastructure in enclosed rooms. Exterior uses a floor-frontier policy: every non-floor entry becomes locally eligible on its completed planned floor, with no room-wide power or atmosphere gate. Optional Exterior Hive and raw-material Quantum Storage pairs extend greedy Nanobot coverage from established Hives across safe powered floor cells without occupying planned building footprints; Quantum Storage remains an optional runtime integration and support never blocks a room.");

        if (AutoUtilitiesApi.TryRegisterConsumer(
                ConsumerId,
                "Planning Overhaul",
                typeof(PlanningOverhaulSys).Assembly.GetName().Version.ToString(3),
                RequiredUtilitiesVersion))
        {
            ui = new PlanningOverhaulUi(this);
        }
        S.Sig.AfterLoadState.AddListener(OnLoadState);
    }

    private void OnLoadState(GameState state)
    {
        DetachLoadedState();
        loadedState = state;
        state.Clock.OnTick.AddListener(OnTick);
        AttachPlanningState(state);
        ResetActiveWorkRotation(clearWarning: true);
        ResetProtectedValidation();
        CancelPreviouslyQueuedProtectedPlanWork();
        EnsureDefaultRoom();
        RebuildOverlay();
        ui?.Refresh();
    }

    private void OnTick(long ticks)
    {
        // Status sampling is bounded independently of the admission pass. Do
        // not move full-plan inspections above this interval gate: Core ticks
        // much more often than new planner work can usefully be admitted.
        UpdateStructuralConstructionStatus(allowRoomEvaluation: false);
        if (ticks < nextAutomationTick) return;
        nextAutomationTick = ticks + AutomationIntervalTicks;

        Stopwatch perf = Stopwatch.StartNew();
        long activePulseStateMs = 0;
        long issuedPriorityMs = 0;
        long relocationPriorityMs = 0;
        long activePulseMs = 0;
        long stagedReturnMs = 0;
        long equipmentStateMs = 0;
        long conformanceMs = 0;
        long equipmentMoveMs = 0;
        long equipmentBuildMs = 0;
        long roomSearchMs = 0;
        long pulsePreviewMs = 0;
        long uiRefreshMs = 0;
        int equipmentEntryCount = 0;
        bool waitingForEquipmentRelocation = false;
        bool completedPostRotationConfiguration = false;
        string outcome = "idle";
        try
        {
            long phaseStart = perf.ElapsedMilliseconds;
            UpdateActivePulseState();
            activePulseStateMs = perf.ElapsedMilliseconds - phaseStart;

            if (ticks >= nextGoalPriorityTick)
            {
                nextGoalPriorityTick = ticks + AutomationIntervalTicks;
                phaseStart = perf.ElapsedMilliseconds;
                ApplyIssuedGoalPriorities(ticks);
                issuedPriorityMs = perf.ElapsedMilliseconds - phaseStart;

                phaseStart = perf.ElapsedMilliseconds;
                ApplyRelocationGoalPriorities(ticks);
                relocationPriorityMs = perf.ElapsedMilliseconds - phaseStart;
            }

            phaseStart = perf.ElapsedMilliseconds;
            if (TryAdvanceEquipmentReconciliation(
                    out string reconciliationOutcome))
            {
                stagedReturnMs = perf.ElapsedMilliseconds - phaseStart;
                outcome = reconciliationOutcome;
                ui?.RefreshAutomation();
                return;
            }
            stagedReturnMs = perf.ElapsedMilliseconds - phaseStart;

            if (activePulseRoomId != 0 && (Continuous || activePulseIsManual))
            {
                int pulseRoomId = activePulseRoomId;
                ClearContinuousConstructionPreview();
                outcome = "advance-active-pulse";
                bool materialLimitedSubmission = activePulseYieldsWhenQueueDrains;
                if (Continuous && continuousRoomId == 0)
                    continuousRoomId = activePulseRoomId;
                phaseStart = perf.ElapsedMilliseconds;
                bool relocationBlocked = AdvanceActivePulse(
                    out bool structuralPhaseCompleted,
                    out bool broaderRelocationBlock);
                activePulseMs = perf.ElapsedMilliseconds - phaseStart;
                if (relocationBlocked && Continuous)
                {
                    outcome = "structural-relocation-blocked";
                    EquipmentStageState blockedState = BuildEquipmentStageState();
                    MarkRoomStagingBlocked(continuousRoomId, blockedState, ticks,
                        broaderRelocationBlock);
                }
                else if (Continuous && !materialLimitedSubmission && continuousRoomId != 0)
                {
                    RecordAutomationProgress(continuousRoomId);
                }
                if (!structuralPhaseCompleted
                        && (ActivePulseQueueCount == 0
                            || ticks >= nextStructuralBatchUiTick))
                {
                    nextStructuralBatchUiTick = ticks + StructuralBatchUiIntervalTicks;
                    phaseStart = perf.ElapsedMilliseconds;
                    ui?.RefreshAutomation();
                    uiRefreshMs = perf.ElapsedMilliseconds - phaseStart;
                }
                if (!structuralPhaseCompleted && Continuous
                        && IsExteriorRoom(pulseRoomId))
                {
                    PlanRoom exteriorRoom = rooms.FirstOrDefault(room =>
                        room.Id == pulseRoomId);
                    if (TryAdvanceExteriorFrontierWork(exteriorRoom,
                            null, out string exteriorOutcome))
                        outcome = exteriorOutcome;
                    else if (!string.IsNullOrWhiteSpace(exteriorOutcome))
                        outcome = exteriorOutcome;
                }
                if (!structuralPhaseCompleted) return;
            }
            if (!Continuous)
            {
                continuousRoomId = 0;
                ResetPostRotationConfigurationPass();
                ClearContinuousConstructionPreview();
                phaseStart = perf.ElapsedMilliseconds;
                bool returnedManualBuilding = TryReturnStagedBuildings(0);
                stagedReturnMs = perf.ElapsedMilliseconds - phaseStart;
                if (returnedManualBuilding)
                {
                    outcome = "return-manual-staged-building";
                    ui?.RefreshAutomation();
                    return;
                }
                outcome = "construction-off";
                return;
            }

            completedPostRotationConfiguration =
                postRotationConfigurationCompletedPendingStatus;

            if (postRotationConfigurationPhase != PostRotationConfigurationPhase.None)
            {
                completedPostRotationConfiguration =
                    AdvancePostRotationConfigurationPass(out outcome);
                continuousRoomId = 0;
                ClearContinuousConstructionPreview();
                if (completedPostRotationConfiguration)
                {
                    continuousRoomCursorId = 0;
                    postRotationConfigurationCompletedPendingStatus = true;
                }
                ui?.RefreshAutomation();
                return;
            }

            if (stagingValidationPasses >= 2 && ticks < nextStagingValidationTick)
            {
                outcome = "staging-full-halt";
                continuousRoomId = 0;
                ClearContinuousConstructionPreview();
                return;
            }
            if (IsProtectedValidationWaiting(ticks))
            {
                outcome = "protected-building-halt";
                continuousRoomId = 0;
                ClearContinuousConstructionPreview();
                return;
            }
            if (IsFloorFrontierValidationWaiting(ticks))
            {
                outcome = "floor-frontier-halt";
                continuousRoomId = 0;
                ClearContinuousConstructionPreview();
                return;
            }

            phaseStart = perf.ElapsedMilliseconds;
            EquipmentStageState equipmentStageState = BuildEquipmentStageState();
            equipmentStateMs = perf.ElapsedMilliseconds - phaseStart;

            phaseStart = perf.ElapsedMilliseconds;
            if (protectedValidationPasses >= 2)
            {
                protectedValidationPasses = 1;
                protectedBuildingWaits.Clear();
            }
            if (stagingValidationPasses >= 2)
            {
                stagingValidationPasses = 1;
                stagingBlockedRooms.Clear();
                broaderRelocationBlockedRooms.Clear();
            }
            if (floorFrontierValidationPasses >= 2)
            {
                floorFrontierValidationPasses = 1;
                floorFrontierBlockedRooms.Clear();
            }

            RefreshMaterialWaits(equipmentStageState, ticks);
            RefreshResearchWaits(equipmentStageState, ticks);
            RefreshActiveWorkWaits(equipmentStageState);
            if (TryHoldForCompletedMaterialRotation(equipmentStageState))
            {
                outcome = "material-rotation-halt";
                ui?.RefreshAutomation();
                return;
            }
            if (TryHoldForCompletedResearchRotation(equipmentStageState))
            {
                outcome = "research-rotation-halt";
                ui?.RefreshAutomation();
                return;
            }

            var excludedRooms = new HashSet<int>(stagingBlockedRooms);
            excludedRooms.UnionWith(materialWaits.Keys);
            excludedRooms.UnionWith(researchWaits.Keys);
            excludedRooms.UnionWith(protectedBuildingWaits.Keys);
            excludedRooms.UnionWith(floorFrontierBlockedRooms);
            excludedRooms.UnionWith(activePlanWorkWaits.Keys);
            PlanRoom next = completedPostRotationConfiguration
                ? null
                : FindNextIncompleteRoom(equipmentStageState,
                    excludedRooms, wrap: false);
            roomSearchMs = perf.ElapsedMilliseconds - phaseStart;
            if (next == null)
            {
                ClearContinuousConstructionPreview();
                if (!completedPostRotationConfiguration)
                {
                    BeginPostRotationConfigurationPass();
                    AdvancePostRotationConfigurationPass(out outcome);
                    ui?.RefreshAutomation();
                    return;
                }
                postRotationConfigurationCompletedPendingStatus = false;
                PlanRoom anyIncomplete = FindNextIncompleteRoom(equipmentStageState,
                    null);
                if (anyIncomplete != null && protectedBuildingWaits.Count > 0)
                {
                    FinishProtectedRoomRotation(equipmentStageState, ticks);
                    outcome = protectedValidationPasses >= 2
                        ? "protected-building-halt" : "protected-building-validation";
                    ui?.RefreshAutomation();
                    return;
                }
                if (anyIncomplete != null
                    && BeginMaterialRotationWait(equipmentStageState))
                {
                    outcome = "material-rotation-halt";
                }
                else if (anyIncomplete != null
                        && BeginResearchRotationWait(equipmentStageState))
                {
                    outcome = "research-rotation-halt";
                }
                else if (anyIncomplete != null && stagingBlockedRooms.Count > 0)
                {
                    BeginOrFinishStagingValidation(equipmentStageState, ticks);
                    outcome = stagingValidationPasses >= 2
                        ? "staging-full-halt" : "begin-staging-validation";
                }
                else if (anyIncomplete != null
                         && floorFrontierBlockedRooms.Count > 0)
                {
                    BeginOrFinishFloorFrontierValidation(
                        equipmentStageState, ticks);
                    outcome = floorFrontierValidationPasses >= 2
                        ? "floor-frontier-halt"
                        : "begin-floor-frontier-validation";
                }
                else if (anyIncomplete != null
                        && FinishActiveWorkRotation(equipmentStageState,
                            out bool activeWorkWarning))
                {
                    outcome = activeWorkWarning
                        ? "active-work-no-progress-warning"
                        : "active-work-progress-rescan";
                }
                else if (anyIncomplete == null)
                {
                    PruneTrackedPlanWork();
                    if (relocationTargets.Count > 0 || planConstructionRooms.Count > 0
                            || planDeconstructionRooms.Count > 0)
                    {
                        lastStatus = DescribePendingPlanWork();
                        outcome = "plan-final-jobs";
                    }
                    else if (HasPendingPlannedConfiguration())
                    {
                        lastStatus = AppendPlanterMetadataWarning(
                            "The room rotation and its four configuration passes are complete. "
                            + DescribePendingConfigurationCycle()
                            + " Continuous construction will retry them after the next room rotation.");
                        outcome = "configuration-cycle-pending";
                    }
                    else
                    {
                        SetContinuous(false);
                        lastStatus = planterMetadataMissingCount > 0
                            ? AppendPlanterMetadataWarning(
                                "Construction Plan complete. Every room matches its planned structure, equipment, production settings, Transporter links, and installed upgrades. Continuous construction has been disabled.")
                            : "Construction Plan complete. Every room matches its planned structure, equipment, production settings, Transporter links, installed upgrades, and planter settings. Continuous construction has been disabled.";
                        if (equipmentStageState.RetainedProtectedBuildings.Count > 0)
                            lastStatus += " Protected originals retained outside the blueprint: "
                                + SummarizeTiles(equipmentStageState.RetainedProtectedBuildings) + ".";
                        The.Dialog.ShowSimple(IconId.CWarning, lastStatus);
                        outcome = "plan-complete";
                    }
                }
                else
                {
                    lastStatus = anyIncomplete.Name + " still needs work. Continuous construction will recheck this room.";
                    outcome = "plan-incomplete";
                }
                ui?.RefreshAutomation();
                return;
            }
            continuousRoomId = next.Id;
            continuousRoomCursorId = next.Id;
            ClearContinuousConstructionPreview();

            if (!equipmentStageState.IsStructureFinished(next.Id))
            {
                outcome = "evaluate-room-shell";
                phaseStart = perf.ElapsedMilliseconds;
                PulsePreview preview = BuildPulsePreview(next.Id,
                    assessRelocations: false);
                pulsePreviewMs = perf.ElapsedMilliseconds - phaseStart;
                if (preview.InProgress > 0)
                {
                    int restoredGoals = RestoreMissingStructuralGoals(
                        preview.InProgressEntries);
                    string activeHeading = next.Name + " shell has "
                        + preview.InProgress + " structural construction ghost"
                        + (preview.InProgress == 1 ? string.Empty : "s")
                        + " pending. "
                        + (restoredGoals > 0
                            ? "Restored " + restoredGoals
                                + " missing native construction job"
                                + (restoredGoals == 1 ? string.Empty : "s") + ". "
                            : string.Empty);
                    bool startedAdditionalWork = preview.Pending.Count > 0
                        && StartAffordableContinuousStructure(preview);
                    if (startedAdditionalWork)
                    {
                        string batchStatus = lastStatus;
                        SetStructuralConstructionStatus(next,
                            activeHeading + batchStatus,
                            preview.InProgressEntries);
                        if (activePulseRoomId == 0) continuousRoomId = 0;
                        outcome = "structural-active-and-new-orders";
                    }
                    else
                    {
                        bool hasViableNativeWork = restoredGoals > 0
                            || RoomHasActiveStructuralJob(next.Id);
                        if (hasViableNativeWork)
                        {
                            if (restoredGoals > 0)
                                RecordAutomationProgress(next.Id);
                            YieldRoomForActivePlanWork(next.Id, activeHeading);
                            SetStructuralConstructionStatus(next,
                                activeHeading
                                    + "Active structural work is already in Core's task list, so later rooms will be checked before this room is revisited.",
                                preview.InProgressEntries);
                            outcome = restoredGoals > 0
                                ? "structural-goals-restored-and-yielded"
                                : "structural-work-yielded";
                        }
                        else
                        {
                            bool waitingForFloorFrontier = preview.Pending
                                .Concat(preview.InProgressEntries)
                                .Any(entry => entry.Layer == WorldLayer.Floor
                                    && !HasConstructedFloorNeighbor(entry));
                            if (waitingForFloorFrontier)
                                MarkRoomFloorFrontierBlocked(next.Id);
                            SetStructuralConstructionStatus(next,
                                activeHeading
                                    + "No viable native structural task remains, so the next room will be checked.",
                                preview.InProgressEntries);
                            continuousRoomId = 0;
                            outcome = "structural-orphan-yielded";
                        }
                    }
                }
                else if (preview.CanStart)
                {
                    ExecutePulse(preview, false);
                    RecordAutomationProgress(next.Id);
                }
                else
                {
                    bool materialOnlyBlock = preview.Pending.Count > 0
                        && preview.InProgress == 0
                        && preview.Locked == 0
                        && preview.Blocked == 0
                        && preview.Materials.Any(item => item.Missing > 0);
                    if (preview.Locked > 0)
                    {
                        YieldRoomForResearch(next, "shell",
                            preview.LockedEntries);
                        outcome = "structural-research-yield";
                    }
                    else if (materialOnlyBlock)
                    {
                        StartAffordableContinuousStructure(preview);
                        outcome = "structural-material-yield";
                    }
                    else
                        lastStatus = next.Name + ": "
                            + PulseBlockReason(preview, true);
                }
                if (!IsRoomStructureFinished(next.Id))
                {
                    if (next.IsExterior)
                    {
                        TryAdvanceExteriorFrontierWork(next,
                            equipmentStageState,
                            out string exteriorOutcome);
                        if (!string.IsNullOrWhiteSpace(exteriorOutcome))
                            outcome = exteriorOutcome;
                    }
                    phaseStart = perf.ElapsedMilliseconds;
                    ui?.RefreshAutomation();
                    uiRefreshMs = perf.ElapsedMilliseconds - phaseStart;
                    return;
                }

                if (activePulseRoomId == next.Id)
                    ClearActivePulseSubmission(clearRoom: true);
                if (structuralStatusRoom?.Id == next.Id)
                    ClearStructuralConstructionStatus();
                equipmentStageState.StructureFinished[next.Id] = true;
                continuousRoomId = next.Id;
                lastStatus = next.Name
                    + " completed its structural Pulse Wave. Advancing to its next construction phase.";
                outcome = "structural-phase-complete";
            }

            phaseStart = perf.ElapsedMilliseconds;
            bool conformanceHandled = TryConformUnplannedBuildings(
                equipmentStageState, next.Id, out bool conformanceProgress,
                out bool conformanceRelocationBlocked,
                out bool conformanceBroaderRelocationBlock,
                out bool conformancePoweredDestinationWait,
                out string protectedWait);
            conformanceMs = perf.ElapsedMilliseconds - phaseStart;
            if (conformanceHandled)
            {
                if (conformanceProgress) RecordAutomationProgress(next.Id);
                if (!conformanceRelocationBlocked && protectedWait == null
                        && !conformancePoweredDestinationWait)
                {
                    if (!conformanceProgress && RoomHasTrackedPlanWork(next.Id))
                    {
                        YieldRoomForActivePlanWork(next.Id, lastStatus);
                        outcome = "conform-active-work-yielded";
                    }
                    else outcome = "conform-active-room";
                    ui?.RefreshAutomation();
                    return;
                }
            }
            bool roomRelocationBlocked = conformanceRelocationBlocked;

            phaseStart = perf.ElapsedMilliseconds;
            bool returnedStagedBuilding = TryReturnStagedBuildings(next.Id,
                equipmentStageState);
            stagedReturnMs = perf.ElapsedMilliseconds - phaseStart;
            if (returnedStagedBuilding)
            {
                RecordAutomationProgress(next.Id);
                outcome = "return-staged-building-to-active-room";
                ui?.RefreshAutomation();
                return;
            }

            phaseStart = perf.ElapsedMilliseconds;
            bool supportHandled = TryAdvanceOptionalConstructionSupport(next,
                equipmentStageState, out bool supportQueued);
            if (supportHandled && supportQueued)
            {
                RecordAutomationProgress(next.Id);
                outcome = "construct-optional-power-support";
                ui?.RefreshAutomation();
                return;
            }

            phaseStart = perf.ElapsedMilliseconds;
            PlanEntry[] nextEquipmentStageEntries = NextEquipmentStageEntries(
                equipmentStageState, next.Id);
            if (conformancePoweredDestinationWait)
                nextEquipmentStageEntries = nextEquipmentStageEntries
                    .Where(entry => EquipmentPhase(entry) == 0).ToArray();
            equipmentEntryCount = nextEquipmentStageEntries.Length;
            bool movedAvailableBuilding = TryMoveAvailableBuildingsIntoPlan(
                equipmentStageState, nextEquipmentStageEntries,
                out HashSet<PlanEntry> relocationWaits,
                out waitingForEquipmentRelocation,
                out int stagingBlockedEntries,
                out bool equipmentRelocationProgress);
            equipmentMoveMs = perf.ElapsedMilliseconds - phaseStart;
            if (movedAvailableBuilding)
            {
                if (equipmentRelocationProgress)
                {
                    RecordAutomationProgress(next.Id);
                    outcome = "move-available-building-into-active-room";
                    ui?.RefreshAutomation();
                    return;
                }
            }

            if (RoomHasActivePlanConstruction(next.Id))
            {
                YieldRoomForActivePlanWork(next.Id,
                    DescribeActivePlanConstructionWait(next.Id));
                outcome = "active-equipment-construction-yielded";
                ui?.RefreshAutomation();
                return;
            }

            PlanEntry[] constructionEntries = nextEquipmentStageEntries
                .Where(entry => !relocationWaits.Contains(entry)
                    && RoomAllowsEquipmentPhase(entry, equipmentStageState))
                .ToArray();
            bool waitingOnlyForPowerNetwork = ShouldYieldForPowerNetwork(
                equipmentStageState, next.Id, nextEquipmentStageEntries.Length);
            phaseStart = perf.ElapsedMilliseconds;
            bool materialBlocked = false;
            bool researchBlocked = false;
            bool advancedEquipmentStage = constructionEntries.Length > 0
                && TryBuildPlannedEquipmentStage(constructionEntries,
                    out materialBlocked, out researchBlocked);
            equipmentBuildMs = perf.ElapsedMilliseconds - phaseStart;
            if (advancedEquipmentStage)
            {
                if (!materialBlocked) RecordAutomationProgress(next.Id);
                outcome = materialBlocked
                    ? "partial-equipment-material-yield"
                    : "construct-active-room-equipment";
                ui?.RefreshAutomation();
                return;
            }
            if (materialBlocked)
            {
                outcome = "equipment-material-yield";
                ui?.RefreshAutomation();
                return;
            }
            if (researchBlocked)
            {
                outcome = "equipment-research-yield";
                ui?.RefreshAutomation();
                return;
            }

            if (!RoomHasTrackedPlanWork(next.Id)
                    && (protectedWait != null || nextEquipmentStageEntries.Any(entry =>
                        ProtectedLocationFor(entry.DefId) != ProtectedBuildingLocation.None
                        || BlockersFor(entry).Any(IsProtectedBuilding))))
            {
                YieldRoomForProtectedBuildings(next.Id, protectedWait ?? lastStatus);
                outcome = "protected-building-room-yield";
                ui?.RefreshAutomation();
                return;
            }

            if (conformancePoweredDestinationWait)
            {
                if (RoomHasTrackedPlanWork(next.Id))
                    YieldRoomForActivePlanWork(next.Id, lastStatus);
                else
                    continuousRoomId = 0;
                outcome = "powered-relocation-destination-yielded";
                ui?.RefreshAutomation();
                return;
            }

            if (roomRelocationBlocked)
            {
                MarkRoomStagingBlocked(next.Id, equipmentStageState, ticks,
                    conformanceBroaderRelocationBlock);
                outcome = conformanceBroaderRelocationBlock
                    ? "room-clearing-relocation-blocked"
                    : "room-clearing-staging-full";
                ui?.RefreshAutomation();
                return;
            }
            if (relocationWaits.Count > 0)
            {
                bool stagingOnly = stagingBlockedEntries > 0
                    && relocationWaits.Count == stagingBlockedEntries;
                MarkRoomStagingBlocked(next.Id, equipmentStageState, ticks,
                    broaderRelocationBlock: !stagingOnly);
                outcome = stagingOnly
                    ? "equipment-staging-full"
                    : "equipment-relocation-blocked";
                ui?.RefreshAutomation();
                return;
            }

            if (RoomHasIncompleteEquipment(next.Id))
            {
                if (RoomHasTrackedPlanWork(next.Id))
                {
                    YieldRoomForActivePlanWork(next.Id, lastStatus);
                    outcome = "active-equipment-work-yielded";
                    ui?.RefreshAutomation();
                    return;
                }
                if (!waitingForEquipmentRelocation && constructionEntries.Length == 0)
                    lastStatus = DescribeEquipmentWait(next.Id);
                if (waitingOnlyForPowerNetwork)
                {
                    continuousRoomId = 0;
                    outcome = "power-network-wait-yielded";
                }
                else outcome = "wait-active-room-equipment";
                ui?.RefreshAutomation();
                return;
            }

            RecordAutomationProgress(next.Id);
            lastStatus = next.Name + " is complete. Advancing to the next room.";
            outcome = "active-room-complete";
            ui?.RefreshAutomation();
            uiRefreshMs = perf.ElapsedMilliseconds - phaseStart;
        }
        finally
        {
            UpdateStructuralConstructionStatus(allowRoomEvaluation: true);
            RefreshOptionalAutoFactoryMaterialDemand();
            ReportSlowOperation("AutomationTick", perf.ElapsedMilliseconds,
                "outcome=" + outcome
                + "; activeState=" + activePulseStateMs
                + "; issuedPriorities=" + issuedPriorityMs
                + "; relocationPriorities=" + relocationPriorityMs
                + "; activePulse=" + activePulseMs
                + "; stagedReturn=" + stagedReturnMs
                + "; equipmentState=" + equipmentStateMs
                + "; conformance=" + conformanceMs
                + "; equipmentMove=" + equipmentMoveMs
                + "; equipmentBuild=" + equipmentBuildMs
                + "; equipmentEntries=" + equipmentEntryCount
                + "; equipmentRelocationWait=" + waitingForEquipmentRelocation
                + "; roomSearch=" + roomSearchMs
                + "; pulsePreview=" + pulsePreviewMs
                + "; uiRefresh=" + uiRefreshMs
                + "; status=" + lastStatus,
                ref nextAutomationPerfLogTime);
        }
    }

    private void ReportSlowOperation(string operation, long elapsedMilliseconds,
        string detail, ref float nextAllowedTime)
    {
        if (elapsedMilliseconds < SlowOperationMilliseconds) return;
        float now = Time.realtimeSinceStartup;
        if (now < nextAllowedTime) return;
        nextAllowedTime = now + PerfLogCooldownSeconds;
        PlanningOverhaulLog.Performance(operation, elapsedMilliseconds,
            "entries=" + entries.Count
            + "; rooms=" + rooms.Count
            + "; continuous=" + Continuous
            + "; activePulseRoom=" + activePulseRoomId
            + "; " + detail);
    }

    internal void ReportUiBuild(string tab, long elapsedMilliseconds,
        long allocatedBytes)
    {
        ReportSlowOperation("BuildPanel", elapsedMilliseconds,
            "tab=" + tab + "; allocatedBytes=" + allocatedBytes,
            ref nextUiBuildPerfLogTime);
    }

    internal void ToggleUi()
    {
        if (ui == null) return;
        if (!ui.IsOpen) EnsurePlanOverlayVisible();
        ui.Toggle();
    }

    internal void SetContinuous(bool value)
    {
        if (Continuous == value) return;
        Continuous = value;
        ClearStructuralConstructionStatus();
        ClearContinuousConstructionPreview();
        ClearAllMaterialWaits();
        ClearAllResearchWaits();
        ResetProtectedValidation();
        ResetFloorFrontierValidation();
        ResetPostRotationConfigurationPass();
        ResetActiveWorkRotation(clearWarning: true);
        ResetExteriorFrontierAdmissionGate();
        if (value)
        {
            ResetConstructionSupportEvaluation();
            continuousRoomId = 0;
            continuousRoomCursorId = 0;
            manualPulseRoomId = 0;
            activePulseIsManual = false;
            ResetStagingValidation();
            lastStatus = "Continuous construction is on. Each room follows structure, clearing, power, optional construction support, atmosphere, and equipment. After each full room rotation, separate passes restore production settings, Transporter links, installed upgrades, and planter crop and Auto Harvest settings.";
        }
        else
        {
            continuousRoomId = 0;
            continuousRoomCursorId = 0;
            ResetStagingValidation();
            lastStatus = "Continuous construction is off. Unclaimed plan work is paused; Pulse Wave is available.";
        }
        ApplyIssuedGoalPriorities(G.Ticks);
        ApplyRelocationGoalPriorities(G.Ticks);
        if (value) nextAutomationTick = G.Ticks;
        ui?.Refresh();
    }

    internal IReadOnlyList<Def> GetCatalog(string query, int maximum)
    {
        string needle = (query ?? string.Empty).Trim();
        return The.Defs.Defs.Values
            .Where(ToolPlaceTiles.CanShowInBuildMenu)
            .Where(def => needle.Length == 0
                || (def.NameT ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || def.Id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(def => S.Research.IsDefUnlocked(def) ? 0 : 1)
            .ThenBy(def => def.NameT, StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .ToArray();
    }

    internal IReadOnlyList<BuildCatalogCategory> GetCatalogCategories(
        string query)
    {
        string needle = (query ?? string.Empty).Trim();
        ToolPlaceTiles buildTool = The.Toolbox.Get<ToolPlaceTiles>(
            ToolPlaceTiles.ToolId);
        var categories = new List<BuildCatalogCategory>();
        foreach (IGrouping<string, Def> group in The.Defs.Defs.Values
            .Where(ToolPlaceTiles.CanShowInBuildMenu)
            .Where(def => needle.Length == 0
                || (def.NameT ?? string.Empty).IndexOf(needle,
                    StringComparison.OrdinalIgnoreCase) >= 0
                || def.Id.IndexOf(needle,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            .GroupBy(BuildCatalogCategoryId, StringComparer.Ordinal))
        {
            string nameKey = Texts.RemapToolMenuName(
                "menu." + group.Key.ToLowerInvariant());
            var category = new BuildCatalogCategory
            {
                Id = group.Key,
                Name = nameKey.TOrNull() ?? T.MenuUngrouped,
                Order = buildTool?.OrderForSubmenu(group.Key) ?? int.MaxValue
            };
            category.Buildings.AddRange(group
                .OrderBy(def => S.Research.IsDefUnlocked(def) ? 0 : 1)
                .ThenBy(def => def.NameT, StringComparer.OrdinalIgnoreCase)
                .ThenBy(def => def.Id, StringComparer.Ordinal));
            categories.Add(category);
        }
        return categories
            .OrderBy(category => category.Order)
            .ThenBy(category => category.Name,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(category => category.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildCatalogCategoryId(Def def)
    {
        string path = string.IsNullOrWhiteSpace(def.ToolPath)
            ? def.Id : def.ToolPath;
        int separator = path.IndexOf('/');
        string category = separator < 0 ? path : path.Substring(0, separator);
        return string.IsNullOrWhiteSpace(category)
            ? "Ungrouped" : category.Trim();
    }

    internal int CatalogCount(string query)
    {
        string needle = (query ?? string.Empty).Trim();
        return The.Defs.Defs.Values.Count(def => ToolPlaceTiles.CanShowInBuildMenu(def)
            && (needle.Length == 0
                || (def.NameT ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || def.Id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    internal Tile[] GetUpgradeSelection()
    {
        if (S == null) return Array.Empty<Tile>();
        List<Tile> selected = S.Query.GetSelectedTiles.Ask();
        if (selected == null) return Array.Empty<Tile>();
        return selected.Select(EntityUtils.ParentObj)
            .Where(tile => tile?.IsActive == true)
            .Distinct()
            .ToArray();
    }

    internal bool IsSupportedUpgradeSelection(IReadOnlyList<Tile> selected)
    {
        if (selected == null || selected.Count == 0) return false;
        int layer = selected[0].Definition.LayerId;
        return (layer == WorldLayer.Floor || layer == WorldLayer.Walls)
            && selected.All(tile => tile?.IsActive == true
                && tile.IsConstructed
                && tile.Definition.LayerId == layer);
    }

    internal IReadOnlyList<Def> GetUpgradeCandidates(IReadOnlyList<Tile> selected,
        int maximum)
    {
        return GetUpgradeCandidates(selected, string.Empty, maximum);
    }

    internal IReadOnlyList<Def> GetUpgradeCandidates(IReadOnlyList<Tile> selected,
        string query, int maximum)
    {
        upgradeCompatibilitySelection = selected;
        upgradeCompatibilityCounts.Clear();
        if (!IsSupportedUpgradeSelection(selected)) return Array.Empty<Def>();
        int layer = selected[0].Definition.LayerId;
        string needle = (query ?? string.Empty).Trim();
        var candidates = new List<Def>();
        foreach (Def def in The.Defs.Defs.Values
            .Where(ToolPlaceTiles.CanShowInBuildMenu)
            .Where(def => def.LayerId == layer && S.Research.IsDefUnlocked(def))
            .Where(def => needle.Length == 0
                || def.NameT.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || def.Id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(def => def.NameT, StringComparer.OrdinalIgnoreCase)
            .ThenBy(def => def.Id, StringComparer.Ordinal))
        {
            int compatible = CalculateCompatibleUpgradeCount(def, selected);
            upgradeCompatibilityCounts[def.Id] = compatible;
            if (compatible <= 0) continue;
            candidates.Add(def);
            if (candidates.Count >= maximum) break;
        }
        return candidates;
    }

    internal int CompatibleUpgradeCount(Def replacement,
        IReadOnlyList<Tile> selected)
    {
        if (ReferenceEquals(selected, upgradeCompatibilitySelection)
                && replacement != null
                && upgradeCompatibilityCounts.TryGetValue(replacement.Id,
                    out int compatible))
            return compatible;
        return CalculateCompatibleUpgradeCount(replacement, selected);
    }

    private int CalculateCompatibleUpgradeCount(Def replacement,
        IReadOnlyList<Tile> selected)
    {
        if (replacement == null || selected == null) return 0;
        int count = 0;
        for (int i = 0; i < selected.Count; i++)
            if (CanQueueUpgrade(selected[i], replacement, out _)) count++;
        return count;
    }

    private bool CanQueueUpgrade(Tile source, Def replacement,
        out Tile replacementTile)
    {
        replacementTile = null;
        if (source?.IsActive != true || !source.IsConstructed
                || source.Goal != null || source.PendingReplacement?.IsPending == true
                || replacement == null
                || replacement.LayerId != source.Definition.LayerId
                || (replacement.LayerId != WorldLayer.Floor
                    && replacement.LayerId != WorldLayer.Walls)) return false;
        replacementTile = EntityUtils.CreatePrototype<Tile>(replacement);
        if (replacementTile?.Constructable == null) return false;
        replacementTile.Transform.Rotation = source.Transform.Rotation;
        return CanPlannerPlaceTile(source.PosIdx, replacementTile, replacement.LayerId);
    }

    internal void ReplaceUpgradeSelection(Def replacement)
    {
        Tile[] selected = GetUpgradeSelection();
        if (!IsSupportedUpgradeSelection(selected))
        {
            lastStatus = "Select only constructed walls or only constructed floors before choosing a replacement.";
            ui?.Refresh();
            return;
        }
        if (replacement == null || replacement.LayerId != selected[0].Definition.LayerId
                || !S.Research.IsDefUnlocked(replacement)
                || !ToolPlaceTiles.CanShowInBuildMenu(replacement))
        {
            lastStatus = "The chosen replacement is no longer available for this selection.";
            ui?.Refresh();
            return;
        }

        Stopwatch perf = Stopwatch.StartNew();
        int queued = 0;
        int skipped = 0;
        suppressedUpgradeSelectionRefreshes = 0;
        batchingUpgradeReplacement = true;
        try
        {
            foreach (Tile source in selected)
            {
                if (!CanQueueUpgrade(source, replacement, out Tile replacementTile))
                {
                    skipped++;
                    continue;
                }
                var command = new CmdPlaceTile(source.PosIdx, replacementTile,
                    warn: false, instant: A.InstantBuild);
                command.Execute(S);
                if (command.IsSuccess) queued++;
                else skipped++;
            }
        }
        finally
        {
            batchingUpgradeReplacement = false;
        }
        lastStatus = replacement.NameT + ": " + queued + " native replacement task"
            + (queued == 1 ? string.Empty : "s") + " queued"
            + (skipped > 0 ? "; " + skipped + " skipped because the location or current task was incompatible" : string.Empty)
            + ".";
        ui?.Refresh();
        ReportSlowOperation("ReplaceUpgradeSelection", perf.ElapsedMilliseconds,
            "selected=" + selected.Length + "; queued=" + queued
            + "; skipped=" + skipped + "; suppressedSelectionRefreshes="
            + suppressedUpgradeSelectionRefreshes,
            ref nextUpgradePerfLogTime);
    }

    internal void BeginPlacement(Def def)
    {
        if (def == null || CurrentRoom == null) return;
        EnsurePlanOverlayVisible();
        bool wasAlreadyCapturing = CaptureActive;
        ToolPlaceTiles.StartBuilding(def.Id);
        // StartBuilding may deactivate a previously selected placement tool.
        // Arm capture only after that lifecycle has completed.
        CaptureActive = true;
        CaptureDefId = def.Id;
        PauseForPlacement(wasAlreadyCapturing);
        if (ghostEntryByPosition.Count == 0 && entries.Count > 0)
            RefreshGhosts(true);
        else
        {
            RestoreGhostAt(lastGhostHoverPosition);
            lastGhostHoverPosition = Pos.Invalid;
        }
        lastStatus = "Planning " + def.NameT + " for " + CurrentRoom.Name
            + ". The game is paused while this building is selected. Click or drag on the map; no resources will be spent.";
    }

    private void PauseForPlacement(bool wasAlreadyCapturing)
    {
        if (S?.Clock == null) return;
        if (!wasAlreadyCapturing)
        {
            speedBeforePlacement = S.Clock.Speed;
            restoreSpeedAfterPlacement = speedBeforePlacement != ClockSpeed.Stopped;
        }
        S.Clock.SetSpeed(ClockSpeed.Stopped);
    }

    internal void EndCapture(string reason = null)
    {
        if (!CaptureActive) return;
        ClockSpeed resumeSpeed = speedBeforePlacement;
        bool shouldRestoreSpeed = restoreSpeedAfterPlacement;
        CaptureActive = false;
        CaptureDefId = null;
        speedBeforePlacement = ClockSpeed.Stopped;
        restoreSpeedAfterPlacement = false;
        if (shouldRestoreSpeed && S?.Clock != null)
            S.Clock.SetSpeed(resumeSpeed);
        if (!string.IsNullOrWhiteSpace(reason)) lastStatus = reason;
        RestoreGhostAt(lastGhostHoverPosition);
        lastGhostHoverPosition = Pos.Invalid;
        RefreshDirtyRoomNameLabels();
        ui?.Refresh();
    }

    internal void CapturePlacement(ToolPlaceTiles tool, IEnumerable<int> positions)
    {
        if (!CaptureActive || tool == null || positions == null) return;
        // CaptureDefId is the catalog choice that armed planning. Prefer it
        // over pooled ToolPlaceTiles state so the saved definition and its
        // semantic floor/upper layer always agree.
        Def def = The.Defs.TryGet(CaptureDefId) ?? tool.SelectedDef;
        if (def == null)
        {
            lastStatus = "The selected building definition is no longer available.";
            return;
        }

        Stopwatch perf = Stopwatch.StartNew();
        var candidates = new List<PlanEntry>();
        var replacedEntries = new HashSet<PlanEntry>();
        var supersededCandidates = new HashSet<PlanEntry>();
        var candidateByFootprint = new Dictionary<int, PlanEntry>();
        foreach (int position in positions.Distinct())
        {
            if (position <= 0 || S?.Map?.Plan?.Data == null
                    || position >= S.Map.Plan.Data.Length) continue;
            var candidate = new PlanEntry
            {
                RoomId = currentRoomId,
                Position = position,
                Rotation = (int)tool.SelectedRotation,
                Layer = def.LayerId,
                DefId = def.Id
            };
            int[] footprint = EntryFootprint(candidate);
            foreach (int footprintPosition in footprint)
            {
                if (planEntriesByFootprint.TryGetValue(footprintPosition,
                        out List<PlanEntry> existingEntries))
                    foreach (PlanEntry existing in existingEntries)
                        if (SamePlanningLayer(existing, candidate))
                            replacedEntries.Add(existing);
                if (candidateByFootprint.TryGetValue(footprintPosition,
                        out PlanEntry earlierCandidate)
                        && SamePlanningLayer(earlierCandidate, candidate))
                    supersededCandidates.Add(earlierCandidate);
            }
            foreach (int footprintPosition in footprint)
                candidateByFootprint[footprintPosition] = candidate;
            candidates.Add(candidate);
        }

        int replaced = entries.RemoveAll(replacedEntries.Contains)
            + supersededCandidates.Count;
        candidates.RemoveAll(supersededCandidates.Contains);
        entries.AddRange(candidates);
        int added = candidates.Count;
        ApplyIncrementalPlanChange(replacedEntries, candidates);
        lastStatus = def.NameT + ": " + added + " planned"
            + (replaced > 0 ? ", " + replaced + " updated" : string.Empty)
            + " in " + CurrentRoom.Name + ".";
        ui?.Refresh();
        ReportSlowOperation("CapturePlacement", perf.ElapsedMilliseconds,
            "definition=" + def.Id + "; added=" + added
            + "; replaced=" + replaced,
            ref nextPlacementPerfLogTime);
    }

    internal void AddRoom(string requestedName)
    {
        string name = string.IsNullOrWhiteSpace(requestedName)
            ? "Room " + (rooms.Count + 1)
            : requestedName.Trim();
        var room = new PlanRoom
        {
            Id = nextRoomId++,
            Order = rooms.Count == 0 ? 0 : rooms.Max(item => item.Order) + 1,
            Name = name
        };
        int previousRoomId = currentRoomId;
        rooms.Add(room);
        currentRoomId = room.Id;
        RefreshRoomColorOrdinals();
        lastStatus = name + " was added and selected.";
        RefreshRoomNameLabel(previousRoomId);
        RefreshRoomNameLabel(currentRoomId);
        ui?.Refresh();
    }

    internal void SelectRoom(int roomId)
    {
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == roomId);
        if (room == null) return;
        int previousRoomId = currentRoomId;
        currentRoomId = roomId;
        lastStatus = room.Name + " is the active planning room.";
        EnsurePlanOverlayVisible();
        RefreshRoomNameLabel(previousRoomId);
        RefreshRoomNameLabel(currentRoomId);
        FocusRoom(roomId);
        ui?.Refresh();
    }

    private void FocusRoom(int roomId)
    {
        var positions = new HashSet<int>();
        foreach (PlanEntry entry in planIndex.EntriesForRoom(roomId))
            foreach (int position in EntryFootprint(entry))
                if (position > 0 && position < S.GridSize) positions.Add(position);
        if (positions.Count == 0) return;
        PosRect bounds = Pos.Encapsulate(positions);
        S.Sig.CameraFocus.Send(bounds.Center(), bounds.Extents, 0f);
    }

    internal void RenameCurrentRoom(string name)
    {
        if (CurrentRoom == null || string.IsNullOrWhiteSpace(name)) return;
        CurrentRoom.Name = name.Trim();
        lastStatus = "Room renamed to " + CurrentRoom.Name + ".";
        RefreshRoomNameLabel(currentRoomId);
        ui?.Refresh();
    }

    internal void DeleteCurrentRoom()
    {
        PlanRoom room = CurrentRoom;
        if (room == null) return;
        PruneTrackedPlanWork();
        UpdateActivePulseState();
        if (activePulseRoomId == room.Id)
        {
            lastStatus = room.Name
                + " cannot be deleted while its Pulse Wave is active.";
            ui?.Refresh();
            return;
        }
        if (relocationTargets.Values.Any(work => work.RoomId == room.Id)
                || planConstructionRooms.Values.Any(roomId => roomId == room.Id)
                || planDeconstructionRooms.Values.Any(roomId => roomId == room.Id))
        {
            lastStatus = room.Name
                + " cannot be deleted while one of its construction, relocation, or deconstruction jobs is active.";
            ui?.Refresh();
            return;
        }
        bool hasConstruction = planIndex.EntriesForRoom(room.Id)
            .Where(entry => entry.IssuedByPlan)
            .Any(entry =>
            {
                Tile tile = ExistingAt(entry);
                return tile?.IsConstructed == false
                    || tile?.PendingReplacement?.IsPending == true;
            });
        if (hasConstruction)
        {
            lastStatus = room.Name
                + " cannot be deleted while its construction orders are active.";
            ui?.Refresh();
            return;
        }

        List<PlanRoom> ordered = rooms.OrderBy(item => item.Order).ToList();
        int removedIndex = Math.Max(0, ordered.FindIndex(item => item.Id == room.Id));
        PlanEntry[] removedPlanEntries = planIndex.EntriesForRoom(room.Id).ToArray();
        int removedEntries = entries.RemoveAll(entry => entry.RoomId == room.Id);
        ApplyIncrementalPlanChange(removedPlanEntries, null);
        foreach (int entityId in stagedBuildings
                     .Where(item => item.Value.RoomId == room.Id)
                     .Select(item => item.Key).ToArray())
            stagedBuildings.Remove(entityId);
        ReleaseConstructionSupportOwnership(room.Id);
        rooms.Remove(room);
        exteriorRoomIds.Remove(room.Id);
        ordered = rooms.OrderBy(item => item.Order).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        currentRoomId = ordered.Count == 0
            ? 0 : ordered[Math.Min(removedIndex, ordered.Count - 1)].Id;
        if (manualPulseRoomId == room.Id) manualPulseRoomId = 0;
        EnsureDefaultRoom();
        RefreshRoomColorOrdinals();
        dirtyRoomNameLabels.Add(room.Id);
        dirtyRoomNameLabels.Add(currentRoomId);
        RefreshDirtyRoomNameLabels();
        lastStatus = room.Name + " was deleted with " + removedEntries
            + " planning entries. Existing completed buildings were preserved.";
        ui?.Refresh();
    }

    internal void DeleteAllRoomsAndTasks()
    {
        The.Toolbox.Clear();
        Continuous = false;
        ClearAllMaterialWaits();
        ClearAllResearchWaits();
        ResetProtectedValidation();
        ResetFloorFrontierValidation();
        continuousRoomId = 0;
        continuousRoomCursorId = 0;
        planterMetadataMissingCount = 0;
        ResetPostRotationConfigurationPass();
        ResetActiveWorkRotation(clearWarning: true);
        ClearActivePulseSubmission(clearRoom: true);

        int cancelledTasks = CancelAllTrackedPlanWork();
        ReleaseConstructionSupportOwnership();
        int deletedRooms = rooms.Count;
        int deletedEntries = entries.Count;
        rooms.Clear();
        exteriorRoomIds.Clear();
        entries.Clear();
        currentRoomId = 0;
        nextRoomId = 1;
        EnsureDefaultRoom();
        RebuildOverlay();
        lastStatus = "Deleted " + deletedRooms + " room"
            + (deletedRooms == 1 ? string.Empty : "s") + " and "
            + deletedEntries + " planning entr"
            + (deletedEntries == 1 ? "y" : "ies") + "; cancelled "
            + cancelledTasks + " active Planning Overhaul task"
            + (cancelledTasks == 1 ? string.Empty : "s")
            + ". A new empty Room 1 is ready. Staging designations were preserved.";
        ui?.Refresh();
    }

    internal void ClearBlueprintPlan()
    {
        The.Toolbox.Clear();
        int removed = entries.Count;
        ClearBlueprintOverlayMarks();
        ClearOwnedGhosts(true);
        ApplySnapshot(new PlanningBlueprintSnapshot
        {
            MapWidth = S.GridWidth, MapHeight = S.GridHeight
        }, false);
        lastStatus = "Cleared the blueprint from the plan: " + removed
            + " building entries and all staging designations removed. Continuous construction "
            + "is off. Existing buildings and native jobs were preserved.";
        ui?.Refresh();
    }

    private void ClearBlueprintOverlayMarks()
    {
        if (S?.Map?.Plan?.Data == null) return;
        int[] planData = S.Map.Plan.Data;
        var blueprintPositions = new HashSet<int>(planEntriesByFootprint.Keys);
        foreach (StagingAreaType type in Enum.GetValues(typeof(StagingAreaType)))
            blueprintPositions.UnionWith(StagingPositions(type));

        bool changed = false;
        for (int position = 1; position < planData.Length; position++)
        {
            int brush = planData[position];
            if (!ShouldClearBlueprintMark(brush,
                    blueprintPositions.Contains(position)))
                continue;
            planData[position] = 0;
            changed = true;
        }
        ownedOverlay.Clear();
        if (changed) S.Sig.UpdatePlan.Send();
    }

    private static bool ShouldClearBlueprintMark(int brush,
        bool blueprintPosition) => brush != 0 && (blueprintPosition
            || PlanningColorPalette.IsPlanningOverhaulBrush(brush));

    internal void UndoLastEntry()
    {
        PlanEntry entry = entries.LastOrDefault(item => item.RoomId == currentRoomId);
        if (entry == null)
        {
            lastStatus = "There is no planned building to undo in this room.";
            return;
        }
        entries.Remove(entry);
        ApplyIncrementalPlanChange(new[] { entry }, null);
        RefreshDirtyRoomNameLabels();
        lastStatus = "Removed the last planned building from " + CurrentRoom.Name + ".";
        ui?.Refresh();
    }

    internal void ClearCurrentRoom()
    {
        PlanEntry[] removedEntries = planIndex.EntriesForRoom(currentRoomId)
            .Where(entry => !entry.IssuedByPlan).ToArray();
        var removedSet = new HashSet<PlanEntry>(removedEntries);
        int removed = entries.RemoveAll(removedSet.Contains);
        ApplyIncrementalPlanChange(removedEntries, null);
        RefreshDirtyRoomNameLabels();
        lastStatus = removed + " unissued plan entries cleared from " + CurrentRoom.Name + ".";
        ui?.Refresh();
    }

    internal int EntryCount(int roomId) => planIndex.EntryCount(roomId);
    internal int StructuralCount(int roomId) => RoomStructuralEntries(roomId).Count;
    internal int EquipmentCount(int roomId) => RoomPostFloorEntries(roomId).Count;

    internal PulsePreview BuildPulsePreview(int roomId,
        bool assessRelocations = true)
    {
        Stopwatch perf = Stopwatch.StartNew();
        try
        {
        var preview = new PulsePreview
        {
            Room = rooms.FirstOrDefault(room => room.Id == roomId)
        };
        if (preview.Room == null || S?.Map?.Grids == null) return preview;

        var costs = new Dictionary<MatType, int>();
        foreach (PlanEntry entry in RoomStructuralEntries(roomId))
        {
            preview.StructuralTotal++;
            Tile existing = ExistingAt(entry);
            if (existing != null)
            {
                if (string.Equals(existing.DefinitionId, entry.DefId, StringComparison.Ordinal))
                {
                    if (existing.IsConstructed) preview.StructuralFinished++;
                    else
                    {
                        preview.InProgress++;
                        preview.InProgressEntries.Add(entry);
                    }
                }
                else if (entry.IssuedByPlan
                         && existing.PendingReplacement?.IsPending == true
                         && string.Equals(existing.PendingReplacement.TargetDef?.Id,
                             entry.DefId, StringComparison.Ordinal))
                {
                    preview.InProgress++;
                    preview.InProgressEntries.Add(entry);
                    continue;
                }
                else
                {
                    // Core supports native same-layer replacement for floors,
                    // walls, and doors. Keep it pending so the Pulse can use it.
                }
                if (string.Equals(existing.DefinitionId, entry.DefId,
                        StringComparison.Ordinal)) continue;
            }

            Def def = The.Defs.TryGet(entry.DefId);
            if (def == null)
            {
                preview.Blocked++;
                preview.BlockedEntries.Add(entry);
                preview.BlockedDetails.Add(EntryName(entry) + " at "
                    + PositionText(entry.Position) + ": building definition is unavailable");
                continue;
            }
            planIndex.SetIssued(entry, false);
            preview.Pending.Add(entry);
            if (!S.Research.IsDefUnlocked(def))
            {
                preview.Locked++;
                preview.LockedEntries.Add(entry);
            }
            if (def.ConstructedMats == null) continue;
            foreach (Mat material in def.ConstructedMats)
            {
                MatType type = material.Type;
                if (type == null && material.TypeId != null)
                    MatType.Known.TryGetValue(material.TypeId, out type);
                if (type == null || material.StackSize <= 0) continue;
                costs[type] = costs.TryGetValue(type, out int old)
                    ? old + material.StackSize : material.StackSize;
            }
        }

        if (assessRelocations) AssessRelocationsForPulse(preview);

        foreach (KeyValuePair<MatType, int> item in costs.OrderBy(item => item.Key.NameT))
        {
            int required = item.Value + ((item.Value + 9) / 10);
            preview.Materials.Add(new MaterialRequirement
            {
                Type = item.Key,
                Cost = item.Value,
                RequiredToStart = required,
                Available = S.Sys.Inventory.AvailableCount(item.Key)
            });
        }
        return preview;
        }
        finally
        {
            ReportSlowOperation("BuildPulsePreview", perf.ElapsedMilliseconds,
                "roomId=" + roomId + "; assessRelocations="
                    + assessRelocations, ref nextPreviewPerfLogTime);
        }
    }

    internal void StartManualPulse()
    {
        if (Continuous)
        {
            lastStatus = "Turn Continuous construction off before starting a manual Pulse Wave.";
            ui?.Refresh();
            return;
        }
        PulsePreview preview = BuildPulsePreview(currentRoomId);
        if (!preview.CanStart)
        {
            lastStatus = PulseBlockReason(preview, false);
            ui?.Refresh();
            return;
        }
        ExecutePulse(preview, true);
        ui?.Refresh();
    }

    private void ExecutePulse(PulsePreview preview, bool manual)
    {
        PulsePreview fresh = manual
            ? BuildPulsePreview(preview.Room.Id)
            : preview;
        if (!fresh.CanStart)
        {
            lastStatus = PulseBlockReason(fresh, false);
            return;
        }

        activePulseRoomId = preview.Room.Id;
        activePulseIsManual = manual;
        if (manual) manualPulseRoomId = preview.Room.Id;
        BeginActivePulseSubmission(fresh.Pending, null,
            yieldsWhenQueueDrains: false);
        AdvanceActivePulse(out _, out _);
    }

    private bool StartAffordableContinuousStructure(PulsePreview preview)
    {
        PlanEntry[] candidates = preview.Pending
            .Where(entry =>
            {
                Def def = The.Defs.TryGet(entry.DefId);
                return def != null && S.Research.IsDefUnlocked(def);
            })
            .Where(entry => entry.Layer == WorldLayer.Floor
                ? HasConstructedFloorNeighbor(entry)
                : HasConstructedWallSupport(entry))
            .OrderBy(entry => entry.Layer == WorldLayer.Floor ? 0 : 1)
            .ThenBy(entry => entry.Position)
            .ToArray();
        if (candidates.Length == 0)
        {
            ClearRoomMaterialWait(preview.Room.Id);
            bool waitingForFloorFrontier = preview.Pending.Any(entry =>
                entry.Layer == WorldLayer.Floor
                && !HasConstructedFloorNeighbor(entry));
            if (preview.InProgress == 0 && waitingForFloorFrontier)
            {
                MarkRoomFloorFrontierBlocked(preview.Room.Id);
                continuousRoomId = 0;
                ScheduleStructuralBatchContinuation();
            }
            lastStatus = preview.Room.Name + (waitingForFloorFrontier
                ? " shell has no unfinished planned floor cardinally adjacent "
                    + "to a completed floor. No floor construction was queued. "
                : " shell is waiting for completed floor support. ")
                + "Later structural rooms will be checked before this room is retried.";
            return false;
        }
        PlanEntry[] affordable = SelectAffordableConstructionEntries(candidates,
            candidates.Length, out PlanEntry[] waiting,
            out Dictionary<MatType, int> committedCosts);
        List<MaterialRequirement> requirements = BuildMaterialRequirements(
            waiting, committedCosts);

        int issued = 0;
        if (affordable.Length > 0)
        {
            activePulseAllowedEntries = new HashSet<PlanEntry>(affordable);
            activePulseRoomId = preview.Room.Id;
            activePulseIsManual = false;
            BeginActivePulseSubmission(affordable,
                activePulseAllowedEntries,
                yieldsWhenQueueDrains: true);
            AdvanceActivePulse(out _, out _);
            issued = affordable.Count(entry => entry.IssuedByPlan);
            RecordAutomationProgress(preview.Room.Id);
        }

        bool signaled = SetRoomMaterialWait(preview.Room, "shell",
            waiting.FirstOrDefault()?.Position
                ?? preview.Pending.FirstOrDefault()?.Position ?? 0,
            requirements, waiting,
            RoomHasTrackedPlanWork(preview.Room.Id));
        int retained = ActivePulseQueueCount;
        lastStatus = preview.Room.Name + " shell queued " + issued
            + " affordable structural order"
            + (issued == 1 ? string.Empty : "s") + ". "
            + (retained > 0
                ? retained + " additional affordable structural entries will "
                    + "be queued in small consecutive-tick batches. "
                : string.Empty)
            + (waiting.Length > 0
                ? "Material-limited work is waiting for "
                    + MaterialRequirementsSummary(requirements) + "; "
                : "Further structural work will be reconsidered after the "
                    + "current floor frontier advances; ")
            + "this room will remain active while submitted structural work advances its floor frontier."
            + AutoFactorySignalSuffix(signaled);
        return true;
    }

    internal string PulseBlockReason(PulsePreview preview, bool continuous)
    {
        if (preview.Room == null) return "Select a room first.";
        if (preview.StructuralTotal == 0) return preview.Room.Name + " has no planned floors, walls, or doors.";
        if (preview.InProgress > 0) return preview.Room.Name
            + " has structural construction in progress: "
            + SummarizePlanEntries(preview.InProgressEntries) + ".";
        if (preview.Pending.Count == 0 && preview.StructuralFinished == preview.StructuralTotal)
            return preview.Room.Name + " is structurally complete.";
        if (preview.Locked > 0) return "Waiting for research: "
            + SummarizePlanEntries(preview.LockedEntries) + ".";
        if (preview.Blocked > 0) return "Cannot place "
            + SummarizePlanEntries(preview.BlockedEntries) + ". "
            + SummarizeDetails(preview.BlockedDetails);
        int missingTypes = preview.Materials.Count(item => item.Missing > 0);
        if (missingTypes > 0)
        {
            string materials = string.Join("; ", preview.Materials
                .Where(item => item.Missing > 0)
                .Select(item => item.Type.NameT + " missing " + item.Missing
                    + " (need " + item.RequiredToStart + ", have "
                    + item.Available + ")"));
            return (continuous ? "Waiting to build " : "Pulse Wave unavailable for ")
                + SummarizePlanEntries(preview.Pending) + ": " + materials
                + ". The required amount includes the 10% buffer.";
        }
        return "Pulse Wave is ready.";
    }

    internal IReadOnlyList<PlanEntry> RecentEntries(int maximum)
    {
        IReadOnlyList<PlanEntry> roomEntries = planIndex.EntriesForRoom(
            currentRoomId);
        int count = Math.Min(Math.Max(0, maximum), roomEntries.Count);
        if (count == 0) return Array.Empty<PlanEntry>();
        var recent = new PlanEntry[count];
        for (int i = 0; i < count; i++)
            recent[i] = roomEntries[roomEntries.Count - 1 - i];
        return recent;
    }

    internal string EntryName(PlanEntry entry)
    {
        return The.Defs.TryGet(entry?.DefId)?.NameT ?? entry?.DefId ?? "Missing building";
    }

    internal string EntryIcon(PlanEntry entry)
    {
        Def def = The.Defs.TryGet(entry?.DefId);
        return string.IsNullOrWhiteSpace(def?.Preview) ? IconId.WHammer : def.Preview;
    }

    internal string SummarizePlanEntries(IEnumerable<PlanEntry> source,
        int maximumTypes = 4)
    {
        PlanEntry[] items = source?.Where(entry => entry != null).ToArray()
            ?? Array.Empty<PlanEntry>();
        if (items.Length == 0) return "no buildings";
        var groups = items.GroupBy(entry => entry.DefId ?? string.Empty,
                StringComparer.Ordinal)
            .OrderBy(group => EntryName(group.First()),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string result = string.Join("; ", groups.Take(maximumTypes).Select(group =>
        {
            PlanEntry first = group.First();
            string positions = string.Join(", ", group.Take(3)
                .Select(entry => PositionText(entry.Position)));
            if (group.Count() > 3) positions += ", ...";
            return EntryName(first) + " x" + group.Count() + " at " + positions;
        }));
        if (groups.Length > maximumTypes)
            result += "; plus " + (groups.Length - maximumTypes)
                + " more building type" + (groups.Length - maximumTypes == 1 ? "" : "s");
        return result;
    }

    private static string SummarizeDetails(IEnumerable<string> details,
        int maximum = 4)
    {
        string[] items = details?.Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        if (items.Length == 0) return string.Empty;
        string result = string.Join("; ", items.Take(maximum));
        if (items.Length > maximum)
            result += "; plus " + (items.Length - maximum) + " more";
        return result + ".";
    }

    private static string PositionText(int position)
    {
        Pos.ToXY(position, out int x, out int y);
        return "[" + x + ":" + y + "]";
    }

    internal bool EntryIsUnlocked(PlanEntry entry)
    {
        Def def = The.Defs.TryGet(entry?.DefId);
        return def != null && S.Research.IsDefUnlocked(def);
    }

    internal void SetBlueprintName(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) blueprintName = value.Trim();
        ui?.Refresh();
    }

    internal void SaveNamedBlueprint()
    {
        try
        {
            string saved = BlueprintRepository.Save(blueprintName,
                BlueprintCodec.Encode(CreateSnapshot(false)));
            lastStatus = "Saved reusable blueprint “" + saved + "”.";
        }
        catch (Exception ex)
        {
            lastStatus = "Could not save the blueprint: " + ex.Message;
        }
        ui?.Refresh();
    }

    internal IReadOnlyList<string> SavedBlueprints()
    {
        try { return BlueprintRepository.List(); }
        catch { return Array.Empty<string>(); }
    }

    internal void LoadNamedBlueprint(string name)
    {
        try { ImportBlueprintCode(BlueprintRepository.Load(name)); }
        catch (Exception ex)
        {
            lastStatus = "Could not load “" + name + "”: " + ex.Message;
            ui?.Refresh();
        }
    }

    internal void DeleteNamedBlueprint(string name)
    {
        try
        {
            lastStatus = BlueprintRepository.Delete(name)
                ? "Deleted saved blueprint “" + name + "”. The active Construction Plan was preserved."
                : "Saved blueprint “" + name + "” no longer exists.";
        }
        catch (Exception ex)
        {
            lastStatus = "Could not delete saved blueprint “" + name + "”: " + ex.Message;
        }
        ui?.Refresh();
    }

    internal void CopyBlueprintCode()
    {
        GUIUtility.systemCopyBuffer = BlueprintCodec.Encode(CreateSnapshot(false));
        lastStatus = "Blueprint code copied to the clipboard with its integrity check.";
        ui?.Refresh();
    }

    internal void CaptureConstructedShip()
    {
        Stopwatch perf = Stopwatch.StartNew();
        if (S?.Map?.Grids == null || S.Sys?.Areas?.Islands == null)
        {
            lastStatus = "The constructed ship is not ready to be captured.";
            ui?.Refresh();
            return;
        }
        PruneTrackedPlanWork();
        if (relocationTargets.Count > 0 || planDeconstructionRooms.Count > 0)
        {
            int activeTasks = relocationTargets.Count + planDeconstructionRooms.Count;
            lastStatus = "Finish " + activeTasks
                + " active relocation or deconstruction job"
                + (activeTasks == 1 ? string.Empty : "s")
                + " before capturing the constructed ship.";
            ui?.Refresh();
            return;
        }
        stagedBuildings.Clear();

        // Replacing the plan is also an explicit pause. Unclaimed construction
        // stops; a worker already performing a step may finish that step.
        Continuous = false;
        ClearAllMaterialWaits();
        ClearAllResearchWaits();
        ResetFloorFrontierValidation();
        continuousRoomId = 0;
        continuousRoomCursorId = 0;
        ResetPostRotationConfigurationPass();
        ResetActiveWorkRotation(clearWarning: true);
        ClearActivePulseSubmission(clearRoom: true);
        ApplyIssuedGoalPriorities(G.Ticks);

        int islandId = 0;
        ShipComputerComp shipComputer = S.Components.FindFirstByType<ShipComputerComp>();
        if (shipComputer?.Tile?.IsActive == true)
            islandId = S.Sys.Areas.IslandAt(shipComputer.Tile.PosIdx);
        if (islandId == 0 || !S.Sys.Areas.Islands.ContainsKey(islandId))
            islandId = S.Sys.Areas.Islands
                .OrderByDescending(item => item.Value.Count)
                .Select(item => item.Key)
                .FirstOrDefault();
        if (islandId == 0
                || !S.Sys.Areas.Islands.TryGetValue(islandId, out HashSet<int> island))
        {
            lastStatus = "No constructed ship island was found to capture.";
            ui?.Refresh();
            return;
        }

        var capturedTiles = new List<Tile>();
        var seenTileIds = new HashSet<int>();
        int[] layers = { WorldLayer.Floor, WorldLayer.Walls, WorldLayer.Objects };
        foreach (int layer in layers)
        {
            foreach (Tile raw in S.Map.Grids[layer].Data)
            {
                Tile tile = EntityUtils.ParentObj(raw);
                if (tile?.IsActive != true || !seenTileIds.Add(tile.Id)
                        || tile.Constructable == null || !tile.IsConstructed
                        || !ToolPlaceTiles.CanShowInBuildMenu(tile.Definition)
                        || !BelongsToCapturedIsland(tile, island)) continue;
                capturedTiles.Add(tile);
            }
        }
        if (capturedTiles.Count == 0)
        {
            lastStatus = "The selected ship island has no constructed, buildable tiles to capture.";
            ui?.Refresh();
            return;
        }

        Dictionary<int, HashSet<int>> areasByTile = capturedTiles.ToDictionary(
            tile => tile.Id, CapturedAreaIds);
        int[] capturedAreaIds = areasByTile.Values.SelectMany(value => value)
            .Distinct().ToArray();
        var capturedAreaPositions = capturedAreaIds.ToDictionary(areaId => areaId,
            areaId => S.Sys.Areas.Areas.TryGetValue(areaId,
                    out HashSet<int> positions)
                ? positions.ToArray() : Array.Empty<int>());
        List<int> areaIds = OrderCapturedAreasCenterOut(capturedAreaIds,
            capturedAreaPositions, island, S.GridHeight).ToList();

        var snapshot = new PlanningBlueprintSnapshot
        {
            Name = blueprintName,
            MapWidth = S.GridWidth,
            MapHeight = S.GridHeight
        };
        Dictionary<int, string> capturedRoomNames = CaptureCoreRoomNames();
        var roomByArea = new Dictionary<int, int>();
        foreach (int areaId in areaIds)
        {
            int roomId = snapshot.Rooms.Count + 1;
            roomByArea[areaId] = roomId;
            snapshot.Rooms.Add(new PlanRoom
            {
                Id = roomId,
                Order = snapshot.Rooms.Count,
                Name = capturedRoomNames.TryGetValue(areaId, out string roomName)
                    ? roomName : "Captured Room " + roomId
            });
        }
        bool hasExterior = areasByTile.Values.Any(value => value.Count == 0);
        int exteriorRoomId = 0;
        if (hasExterior)
        {
            exteriorRoomId = snapshot.Rooms.Count + 1;
            snapshot.Rooms.Add(new PlanRoom
            {
                Id = exteriorRoomId,
                Order = snapshot.Rooms.Count,
                Name = "Exterior",
                IsExterior = true
            });
        }

        var capturedEntryByTileId = new Dictionary<int, PlanEntry>();
        foreach (Tile tile in capturedTiles
                     .OrderBy(item => item.Definition.LayerId)
                     .ThenBy(item => item.PosIdx))
        {
            int roomId = areasByTile[tile.Id]
                .Where(roomByArea.ContainsKey)
                .Select(area => roomByArea[area])
                .DefaultIfEmpty(exteriorRoomId)
                .Min();
            var entry = new PlanEntry
            {
                RoomId = roomId,
                Position = tile.PosIdx,
                Rotation = (int)tile.Transform.Rotation,
                Layer = tile.Definition.LayerId,
                DefId = tile.DefinitionId
            };
            CaptureBuildingConfiguration(tile, entry);
            snapshot.Entries.Add(entry);
            capturedEntryByTileId[tile.Id] = entry;
        }
        int skippedExternalTransporterLinks = CaptureTransporterLinks(
            capturedTiles, capturedEntryByTileId);
        snapshot.CurrentRoomId = snapshot.Rooms[0].Id;
        ApplySnapshot(snapshot, false);
        lastStatus = "Captured " + capturedTiles.Count + " constructed buildings in "
            + snapshot.Rooms.Count + " rooms, ordered from the ship center outward. "
            + "Production settings, Transporter names and internal links, installed upgrades, "
            + "and planter crop and Auto Harvest settings were retained. Name and save this reusable blueprint."
            + (skippedExternalTransporterLinks > 0
                ? " " + skippedExternalTransporterLinks + " Transporter link"
                    + (skippedExternalTransporterLinks == 1 ? string.Empty : "s")
                    + " left the captured ship and could not be mapped into the blueprint."
                : string.Empty);
        ui?.Refresh();
        ReportSlowOperation("CaptureConstructedShip", perf.ElapsedMilliseconds,
            "capturedTiles=" + capturedTiles.Count
            + "; capturedRooms=" + snapshot.Rooms.Count,
            ref nextShipCapturePerfLogTime);
    }

    private static int[] OrderCapturedAreasCenterOut(IEnumerable<int> areaIds,
        IReadOnlyDictionary<int, int[]> areaPositions,
        IEnumerable<int> shipPositions, int mapHeight)
    {
        int[] ids = areaIds?.Distinct().ToArray() ?? Array.Empty<int>();
        if (ids.Length == 0) return ids;

        int[] ship = shipPositions?.Where(position => position >= 0).ToArray()
            ?? Array.Empty<int>();
        if (ship.Length == 0 && areaPositions != null)
            ship = ids.Where(areaPositions.ContainsKey)
                .SelectMany(areaId => areaPositions[areaId] ?? Array.Empty<int>())
                .Where(position => position >= 0).ToArray();
        if (mapHeight <= 0 || ship.Length == 0)
            return ids.OrderBy(areaId => areaId).ToArray();

        int minimumX = int.MaxValue, minimumY = int.MaxValue;
        int maximumX = int.MinValue, maximumY = int.MinValue;
        foreach (int position in ship)
        {
            int x = position / mapHeight;
            int y = position % mapHeight;
            minimumX = Math.Min(minimumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumX = Math.Max(maximumX, x);
            maximumY = Math.Max(maximumY, y);
        }
        double shipCenterX = (minimumX + maximumX) / 2d;
        double shipCenterY = (minimumY + maximumY) / 2d;

        return ids.Select(areaId =>
            {
                int[] positions = areaPositions != null
                    && areaPositions.TryGetValue(areaId, out int[] area)
                    ? area.Where(position => position >= 0).ToArray()
                    : Array.Empty<int>();
                if (positions.Length == 0)
                    return new
                    {
                        AreaId = areaId,
                        Distance = double.MaxValue,
                        Angle = double.MaxValue,
                        MinimumPosition = int.MaxValue
                    };

                double roomCenterX = positions.Average(position =>
                    (double)(position / mapHeight));
                double roomCenterY = positions.Average(position =>
                    (double)(position % mapHeight));
                double dx = roomCenterX - shipCenterX;
                double dy = roomCenterY - shipCenterY;
                double angle = Math.Atan2(dy, dx);
                if (angle < 0d) angle += Math.PI * 2d;
                return new
                {
                    AreaId = areaId,
                    Distance = dx * dx + dy * dy,
                    Angle = angle,
                    MinimumPosition = positions.Min()
                };
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Angle)
            .ThenBy(item => item.MinimumPosition)
            .ThenBy(item => item.AreaId)
            .Select(item => item.AreaId).ToArray();
    }

    private static void OrderBlueprintRoomsCenterOut(
        PlanningBlueprintSnapshot snapshot, int mapHeight)
    {
        if (snapshot?.Rooms == null || snapshot.Rooms.Count == 0) return;

        var positionsByRoom = snapshot.Rooms.ToDictionary(room => room.Id,
            _ => new HashSet<int>());
        var floorPositionsByRoom = snapshot.Rooms.ToDictionary(room => room.Id,
            _ => new HashSet<int>());
        foreach (PlanEntry entry in snapshot.Entries)
        {
            if (entry.Position < 0 || !positionsByRoom.TryGetValue(entry.RoomId,
                    out HashSet<int> roomPositions)) continue;
            roomPositions.Add(entry.Position);
            if (entry.Layer == WorldLayer.Floor)
                floorPositionsByRoom[entry.RoomId].Add(entry.Position);
        }

        var centerPositionsByRoom = snapshot.Rooms.ToDictionary(room => room.Id,
            room =>
            {
                HashSet<int> floors = floorPositionsByRoom[room.Id];
                return (floors.Count > 0 ? floors : positionsByRoom[room.Id])
                    .ToArray();
            });
        int[] shipPositions = floorPositionsByRoom.Values
            .SelectMany(positions => positions).Distinct().ToArray();
        if (shipPositions.Length == 0)
            shipPositions = positionsByRoom.Values.SelectMany(positions => positions)
                .Distinct().ToArray();

        int[] exteriorIds = snapshot.Rooms
            .Where(room => room.IsExterior || string.Equals(room.Name,
                "Exterior", StringComparison.OrdinalIgnoreCase))
            .OrderBy(room => room.Order).ThenBy(room => room.Id)
            .Select(room => room.Id).ToArray();
        var exterior = new HashSet<int>(exteriorIds);
        int[] interiorIds = snapshot.Rooms.Where(room => !exterior.Contains(room.Id))
            .Select(room => room.Id).ToArray();
        int[] orderedIds = OrderCapturedAreasCenterOut(interiorIds,
                centerPositionsByRoom, shipPositions, mapHeight)
            .Concat(exteriorIds).ToArray();

        var orderByRoom = orderedIds.Select((roomId, order) => (roomId, order))
            .ToDictionary(item => item.roomId, item => item.order);
        foreach (PlanRoom room in snapshot.Rooms)
            if (orderByRoom.TryGetValue(room.Id, out int order)) room.Order = order;
        if (orderedIds.Length > 0) snapshot.CurrentRoomId = orderedIds[0];
    }

    private Dictionary<int, string> CaptureCoreRoomNames()
    {
        var result = new Dictionary<int, string>();
        HashSet<IComponent> labels = S.Components.AllByType<TileLabelComp>();
        if (labels == null) return result;
        foreach (IComponent component in labels)
        {
            if (component is not TileLabelComp label || !label.IsRoomName
                    || string.IsNullOrWhiteSpace(label.Label)
                    || label.Tile?.IsActive != true) continue;
            int areaId = S.Sys.Areas.At(label.Tile.PosIdx);
            if (areaId > 0 && !result.ContainsKey(areaId))
                result[areaId] = label.Label.Trim();
        }
        return result;
    }

    private bool BelongsToCapturedIsland(Tile tile, HashSet<int> island)
    {
        int[] footprint = TileFootprint(tile, tile.PosIdx);
        if (footprint.Any(island.Contains)) return true;
        if (tile.Constructable?.IsBuiltInSpace != true) return false;
        foreach (int position in footprint)
        {
            int[] neighbors = { Pos.N(position), Pos.S(position), Pos.E(position), Pos.W(position) };
            if (neighbors.Any(neighbor => !Pos.IsOOB(neighbor) && island.Contains(neighbor)))
                return true;
        }
        return false;
    }

    private HashSet<int> CapturedAreaIds(Tile tile)
    {
        var result = new HashSet<int>();
        foreach (int position in TileFootprint(tile, tile.PosIdx))
            AddCapturedArea(position, result);

        // Objects whose own footprint is outdoors belong to the Exterior room.
        // Floors beneath boundary walls and the walls/doors themselves need
        // adjacent-area lookup because Core marks those occupied cells as wall.
        if (result.Count > 0 || tile.Definition.LayerId == WorldLayer.Objects)
            return result;
        foreach (int position in TileFootprint(tile, tile.PosIdx))
        {
            int[] neighbors = { Pos.N(position), Pos.S(position), Pos.E(position), Pos.W(position) };
            foreach (int neighbor in neighbors)
                if (!Pos.IsOOB(neighbor)) AddCapturedArea(neighbor, result);
        }
        return result;
    }

    private void AddCapturedArea(int position, HashSet<int> result)
    {
        int areaId = S.Sys.Areas.At(position);
        if (areaId > 0) result.Add(areaId);
    }

    internal void ImportBlueprintCode(string code)
    {
        if (!BlueprintCodec.TryDecode(code, out PlanningBlueprintSnapshot snapshot, out string error))
        {
            lastStatus = "Blueprint import failed: " + error;
            ui?.Refresh();
            return;
        }
        var sizes = new Dictionary<(string DefId, int Rotation), (int Width, int Height)>();
        (int Width, int Height) FootprintSize(PlanEntry entry)
        {
            var key = (entry.DefId, entry.Rotation);
            if (sizes.TryGetValue(key, out var size)) return size;
            Def def = The.Defs.TryGet(entry.DefId);
            Tile prototype = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
            if (prototype != null) prototype.Transform.Rotation = (Facing.Type)entry.Rotation;
            size = (Math.Max(1, prototype?.Transform.RotatedWidth ?? 1),
                Math.Max(1, prototype?.Transform.RotatedHeight ?? 1));
            sizes[key] = size;
            return size;
        }
        if (!BlueprintGeometry.TryAdapt(snapshot, S.GridWidth, S.GridHeight,
                FootprintSize, out PlanningBlueprintSnapshot adapted, out error,
                Game.Constants.Consts.CornerMargin))
        {
            lastStatus = "Blueprint import failed: " + error;
            ui?.Refresh();
            return;
        }
        OrderBlueprintRoomsCenterOut(adapted, S.GridHeight);
        ApplySnapshot(adapted, false);
        int missing = entries.Count(entry => The.Defs.TryGet(entry.DefId) == null);
        lastStatus = "Imported “" + blueprintName + "”: " + rooms.Count + " rooms and "
            + entries.Count + " building entries"
            + (missing > 0 ? ". " + missing + " entries need missing mod definitions" : string.Empty)
            + ". Layout preserved on the " + CurrentMapSize + " map. "
            + "Rooms ordered from the plan center outward, with Exterior last.";
        ui?.Refresh();
    }

    private PlanningBlueprintSnapshot CreateSnapshot(bool preserveRuntime)
    {
        var snapshot = new PlanningBlueprintSnapshot
        {
            Name = blueprintName,
            CurrentRoomId = currentRoomId,
            MapWidth = S.GridWidth,
            MapHeight = S.GridHeight
        };
        snapshot.Rooms.AddRange(rooms.Select(room => new PlanRoom
        {
            Id = room.Id,
            Order = room.Order,
            Name = room.Name,
            IsExterior = room.IsExterior
        }));
        snapshot.Entries.AddRange(entries.Select(entry => ClonePlanEntry(entry,
            preserveRuntime && entry.IssuedByPlan)));
        foreach (StagingAreaType type in Enum.GetValues(typeof(StagingAreaType)))
            snapshot.Staging[type] = new HashSet<int>(StagingPositions(type));
        return snapshot;
    }

    private void ApplySnapshot(PlanningBlueprintSnapshot snapshot, bool preserveRuntime)
    {
        if (!preserveRuntime) ReleaseConstructionSupportOwnership();
        else InvalidateConstructionSupportPlan();
        ClearAllMaterialWaits();
        ClearAllResearchWaits();
        ClearOwnedOverlay();
        ClearOwnedGhosts();
        rooms.Clear();
        entries.Clear();
        buildingColorOrdinals.Clear();
        rooms.AddRange(snapshot.Rooms);
        RefreshExteriorRoomIndex();
        entries.AddRange(snapshot.Entries);
        ClearStagingAreas();
        foreach (KeyValuePair<StagingAreaType, HashSet<int>> area in snapshot.Staging)
            StagingPositions(area.Key).UnionWith(area.Value);
        if (!preserveRuntime)
            foreach (PlanEntry entry in entries) planIndex.SetIssued(entry, false);
        blueprintName = string.IsNullOrWhiteSpace(snapshot.Name)
            ? "Construction Plan" : snapshot.Name;
        currentRoomId = snapshot.CurrentRoomId;
        nextRoomId = rooms.Count == 0 ? 1 : rooms.Max(room => room.Id) + 1;
        ClearActivePulseSubmission(clearRoom: true);
        Continuous = false;
        ResetFloorFrontierValidation();
        continuousRoomId = 0;
        continuousRoomCursorId = 0;
        planterMetadataMissingCount = 0;
        ResetPostRotationConfigurationPass();
        ResetActiveWorkRotation(clearWarning: true);
        EnsureDefaultRoom();
        RebuildOverlay();
    }

    private void EnsureDefaultRoom()
    {
        if (rooms.Count == 0)
        {
            rooms.Add(new PlanRoom { Id = nextRoomId++, Order = 0, Name = "Room 1" });
        }
        if (rooms.All(room => room.Id != currentRoomId))
            currentRoomId = rooms.OrderBy(room => room.Order).First().Id;
        nextRoomId = Math.Max(nextRoomId, rooms.Max(room => room.Id) + 1);
    }

    private static bool IsStructural(PlanEntry entry)
    {
        return entry.Layer == WorldLayer.Floor || entry.Layer == WorldLayer.Walls;
    }

    private Tile ExistingAt(PlanEntry entry)
    {
        if (entry.Position <= 0 || entry.Position >= S.GridSize) return null;
        return EntityUtils.ParentObj(EntityUtils.GetTileInLayer(entry.Position, entry.Layer));
    }

    private bool IsRoomStructureFinished(int roomId)
    {
        IReadOnlyList<PlanEntry> structural = RoomStructuralEntries(roomId);
        return structural.Count == 0 || structural.All(entry =>
        {
            Tile tile = ExistingAt(entry);
            return tile?.IsConstructed == true
                && string.Equals(tile.DefinitionId, entry.DefId, StringComparison.Ordinal);
        });
    }

    private void ApplyIssuedGoalPriorities(long ticks)
    {
        // IssuedByPlan is persisted ownership, not a queue of unfinished work.
        // Do not discard completed entries just to shorten this scan: native
        // replacements can later appear at the same owned plan position.
        foreach (PlanEntry entry in planIndex.IssuedEntries)
        {
            Tile tile = ExistingAt(entry);
            if (tile != null && relocationTargets.ContainsKey(tile.Id)) continue;
            bool activeConstruction = tile?.IsConstructed == false
                || tile?.PendingReplacement?.IsPending == true;
            if (!activeConstruction || tile.Goal == null) continue;
            planConstructionRooms[tile.Id] = entry.RoomId;
            ApplyIssuedGoalPriority(entry, tile, ticks);
        }
        ApplyConstructionSupportGoalPriorities(ticks);
    }

    internal void SetColorByBuildingType(bool value)
    {
        PlanColorKey next = value ? PlanColorKey.BuildingType : PlanColorKey.Room;
        if (planColorKey == next) return;
        planColorKey = next;
        RepaintOwnedOverlay();
        lastStatus = "Planning colors are now keyed by "
            + (value ? "building type." : "room.");
        ui?.Refresh();
    }

    internal Color RoomDisplayColor(int roomId) =>
        PlanningColorPalette.OpaqueColor(RoomColor(roomId));

    internal Color EntryDisplayColor(PlanEntry entry) =>
        PlanningColorPalette.OpaqueColor(OverlayColor(entry));

    private int RoomColor(int roomId)
    {
        if (!roomColorOrdinals.TryGetValue(roomId, out int ordinal))
        {
            RefreshRoomColorOrdinals();
            ordinal = roomColorOrdinals.TryGetValue(roomId, out int found)
                ? found : -1;
        }
        return PlanningColorPalette.BrushForOrdinal(ordinal);
    }

    private void RefreshRoomColorOrdinals()
    {
        roomColorOrdinals.Clear();
        int ordinal = 0;
        foreach (PlanRoom room in rooms.OrderBy(room => room.Order)
                     .ThenBy(room => room.Id))
            roomColorOrdinals[room.Id] = ordinal++;
    }

    private int OverlayColor(PlanEntry entry)
    {
        if (entry == null) return 0;
        if (!ColorByBuildingType) return RoomColor(entry.RoomId);
        string defId = entry.DefId ?? string.Empty;
        if (!buildingColorOrdinals.TryGetValue(defId, out int ordinal))
        {
            ordinal = buildingColorOrdinals.Count;
            buildingColorOrdinals[defId] = ordinal;
        }
        return PlanningColorPalette.BrushForOrdinal(ordinal);
    }

    private void EnsureBuildingColorOrdinals()
    {
        if (!ColorByBuildingType) return;
        foreach (PlanEntry entry in entries)
        {
            string defId = entry.DefId ?? string.Empty;
            if (!buildingColorOrdinals.ContainsKey(defId))
                buildingColorOrdinals[defId] = buildingColorOrdinals.Count;
        }
    }

    private PlanEntry VisibleOverlayEntry(IEnumerable<PlanEntry> candidates)
    {
        PlanEntry visible = null;
        int visibleRank = int.MinValue;
        if (candidates == null) return null;
        foreach (PlanEntry entry in candidates)
        {
            if (The.Defs.TryGet(entry.DefId) == null) continue;
            int rank = LayerRank(entry.Layer);
            if (rank < visibleRank) continue;
            visible = entry;
            visibleRank = rank;
        }
        return visible;
    }

    private void RebuildOverlay()
    {
        ResetProtectedValidation();
        InvalidateConstructionSupportPlan();
        Stopwatch perf = Stopwatch.StartNew();
        try
        {
        ClearAllMaterialWaits();
        ClearAllResearchWaits();
        if (S?.Map?.Plan?.Data == null) return;
        RefreshRoomColorOrdinals();
        EnsureBuildingColorOrdinals();
        ClearOwnedOverlay();
        planIndex.Clear();
        planEntriesByFootprint.Clear();
        multiTileGhostCoverage.Clear();
        plannedFloorRoomByPosition.Clear();
        plannedFullFloorPositions.Clear();
        var footprintPrototypes =
            new Dictionary<(string DefId, int Rotation), Tile>();
        foreach (PlanEntry entry in entries)
        {
            var prototypeKey = (entry.DefId ?? string.Empty, entry.Rotation);
            if (!footprintPrototypes.TryGetValue(prototypeKey,
                    out Tile prototype))
            {
                Def def = The.Defs.TryGet(entry.DefId);
                prototype = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
                if (prototype != null)
                    prototype.Transform.Rotation = (Facing.Type)entry.Rotation;
                footprintPrototypes[prototypeKey] = prototype;
            }
            if (prototype == null)
            {
                footprintBuffer[0] = entry.Position;
                planIndex.Add(entry, footprintBuffer, 1);
                IndexPlanFootprint(entry, entry.Position);
                continue;
            }
            int count = prototype.Transform.GetSubtilePos(entry.Position, footprintBuffer);
            planIndex.Add(entry, footprintBuffer, count);
            bool multiTile = count > 1;
            for (int i = 0; i < count; i++)
            {
                int position = footprintBuffer[i];
                IndexPlanFootprint(entry, position);
                if (multiTile) ChangeMultiTileGhostCoverage(position, 1);
                if (entry.Layer == WorldLayer.Floor)
                {
                    plannedFloorRoomByPosition[position] = entry.RoomId;
                    if (prototype.Integrity?.CanHaveFullIntegrity == true)
                        plannedFullFloorPositions.Add(position);
                }
            }
        }
        foreach (KeyValuePair<int, List<PlanEntry>> item in planEntriesByFootprint)
        {
            int position = item.Key;
            if (position <= 0 || position >= S.Map.Plan.Data.Length
                    || S.Map.Plan.Data[position] != 0) continue;
            PlanEntry visible = VisibleOverlayEntry(item.Value);
            if (visible == null) continue;
            int color = OverlayColor(visible);
            S.Map.Plan.Data[position] = color;
            ownedOverlay[position] = color;
        }
        PaintStagingOverlay();
        S.Sig.UpdatePlan.Send();
        RefreshGhosts(true);
        RefreshRoomNameLabels();
        }
        finally
        {
            ReportSlowOperation("RebuildOverlay", perf.ElapsedMilliseconds,
                "overlayCells=" + ownedOverlay.Count
                    + "; multiTileGhostCells=" + multiTileGhostCoverage.Count,
                ref nextOverlayPerfLogTime);
        }
    }

    private void RepaintOwnedOverlay()
    {
        if (S?.Map?.Plan?.Data == null) return;
        foreach (KeyValuePair<int, int> item in ownedOverlay)
            if (item.Key > 0 && item.Key < S.Map.Plan.Data.Length
                    && S.Map.Plan.Data[item.Key] == item.Value)
                S.Map.Plan.Data[item.Key] = 0;
        ownedOverlay.Clear();
        EnsureBuildingColorOrdinals();
        foreach (KeyValuePair<int, List<PlanEntry>> item in planEntriesByFootprint)
        {
            int position = item.Key;
            if (position <= 0 || position >= S.Map.Plan.Data.Length
                    || S.Map.Plan.Data[position] != 0) continue;
            PlanEntry visible = VisibleOverlayEntry(item.Value);
            if (visible == null) continue;
            int color = OverlayColor(visible);
            S.Map.Plan.Data[position] = color;
            ownedOverlay[position] = color;
        }
        PaintStagingOverlay();
        S.Sig.UpdatePlan.Send();
    }

    private void ClearOwnedOverlay()
    {
        if (S?.Map?.Plan?.Data != null)
        {
            foreach (KeyValuePair<int, int> item in ownedOverlay)
            {
                if (item.Key > 0 && item.Key < S.Map.Plan.Data.Length
                        && S.Map.Plan.Data[item.Key] == item.Value)
                    S.Map.Plan.Data[item.Key] = 0;
            }
            if (ownedOverlay.Count > 0) S.Sig.UpdatePlan.Send();
        }
        ownedOverlay.Clear();
    }

    public ComponentData Save()
    {
        // Persist only authoritative plan/job metadata. Derived room views and
        // occupancy caches must be rebuilt against the newly loaded world.
        Stopwatch perf = Stopwatch.StartNew();
        ComponentData data = ComponentData.Create(SysId);
        data.SetInt("SchemaVersion", SaveSchemaVersion);
        data.SetBool("Continuous", Continuous);
        data.SetInt("ManualPulseRoomId", manualPulseRoomId);
        data.SetInt("ActivePulseRoomId", activePulseRoomId);
        data.SetBool("ActivePulseIsManual", activePulseIsManual);
        data.SetInt("PlanColorKey", (int)planColorKey);
        SaveRelocationState(data);
        SaveConstructionSupportState(data);
        string encoded = BlueprintCodec.Encode(CreateSnapshot(true));
        data.SetString("Blueprint", encoded);
        ReportSlowOperation("SavePlanState", perf.ElapsedMilliseconds,
            "blueprintChars=" + encoded.Length,
            ref nextSavePerfLogTime);
        return data;
    }

    public void Load(ComponentData data)
    {
        if (data == null) return;
        planColorKey = data.GetInt("PlanColorKey", 0) == (int)PlanColorKey.BuildingType
            ? PlanColorKey.BuildingType : PlanColorKey.Room;
        string encoded = data.GetString("Blueprint", null);
        if (!string.IsNullOrWhiteSpace(encoded)
                && BlueprintCodec.TryDecode(encoded, out PlanningBlueprintSnapshot snapshot, out _))
            ApplySnapshot(snapshot, true);
        Continuous = data.GetBool("Continuous", false);
        continuousRoomId = 0;
        continuousRoomCursorId = 0;
        ResetPostRotationConfigurationPass();
        ResetActiveWorkRotation(clearWarning: true);
        manualPulseRoomId = data.GetInt("ManualPulseRoomId", 0);
        activePulseRoomId = data.GetInt("ActivePulseRoomId", 0);
        activePulseIsManual = data.GetBool("ActivePulseIsManual", false);
        LoadRelocationState(data);
        LoadConstructionSupportState(data);
    }

    private void DetachLoadedState()
    {
        ClearStructuralConstructionStatus();
        ClearAllMaterialWaits();
        ClearAllResearchWaits();
        if (loadedState == null) return;
        loadedState.Clock.OnTick.RemoveListener(OnTick);
        DetachPlanningState(loadedState);
        loadedState = null;
    }

    public override void Unload()
    {
        EndCapture();
        S.Sig.AfterLoadState.RemoveListener(OnLoadState);
        DetachLoadedState();
        ClearOwnedOverlay();
        ClearOwnedGhosts();
        ClearRoomNameLabels();
        ui?.Dispose();
        ui = null;
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        if (Instance == this) Instance = null;
        rooms.Clear();
        entries.Clear();
        planIndex.Clear();
        ReleaseConstructionSupportOwnership();
        PlanningOverhaulLog.Shutdown();
    }
}
