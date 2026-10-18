using System;
using System.Collections.Generic;
using System.Linq;
using Stopwatch = System.Diagnostics.Stopwatch;
using Game;
using Game.Commands;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems;
using Game.Systems.AI;
using Game.Tools;
using Game.Utils;
using KL.Grid;
using KL.Text;
using KL.Utils;
using UnityEngine;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    private const int ConstructionStageStructure = 0;
    private const int ConstructionStageClearing = 1;
    private const int ConstructionStagePower = 2;
    private const int ConstructionStageAtmosphere = 3;
    private const int ConstructionStageEquipment = 4;
    private const int ConstructionStageComplete = 5;

    internal const string StagingIndoorMode = "po_staging_indoor";
    internal const string StagingOutdoorMode = "po_staging_outdoor";
    internal const string StagingOutdoorEdgeMode = "po_staging_outdoor_edge";
    internal const string StagingClearMode = "po_staging_clear";

    private readonly HashSet<int> indoorStaging = new();
    private readonly HashSet<int> outdoorStaging = new();
    private readonly HashSet<int> outdoorEdgeStaging = new();
    private readonly Dictionary<int, Tile> ownedGhosts = new();
    private readonly Dictionary<int, PlanEntry> ghostEntryByPosition = new();
    private readonly Dictionary<int, List<PlanEntry>> planEntriesByFootprint = new();
    private readonly Dictionary<int, int> multiTileGhostCoverage = new();
    private readonly Dictionary<int, int> plannedFloorRoomByPosition = new();
    private readonly HashSet<int> plannedFullFloorPositions = new();
    private readonly Dictionary<int, GameTextTMP> roomNameLabels = new();
    private readonly HashSet<int> dirtyRoomNameLabels = new();
    private readonly HashSet<int> constructionErasePositionsThisGesture = new();
    private readonly HashSet<int> stagingPositionsChangedThisGesture = new();
    private readonly HashSet<PlanEntry> erasedEntriesThisGesture = new();
    private readonly Dictionary<int, RelocationWorkState> relocationTargets = new();
    private readonly Dictionary<int, RelocationWorkState> stagedBuildings = new();
    private readonly Dictionary<int, int> planConstructionRooms = new();
    private readonly Dictionary<int, int> planDeconstructionRooms = new();
    private readonly List<int> structuralStatusCandidates = new();
    private readonly List<int> structuralStatusExamples = new(4);
    private readonly List<Tile> structuralStatusCurrentTiles = new(4);
    private PlanRoom structuralStatusRoom;
    private string structuralStatusHeading;
    private string structuralStatusPublishedMessage;
    private string structuralStatusNoJobDetails;
    private int structuralStatusCandidateIndex;
    private readonly Dictionary<string, List<int>> activePulseWallSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> activePulseWallDefinitions = new(StringComparer.Ordinal);
    private int activePulseWallScanPosition;
    private bool activePulseWallsIndexed;
    private readonly Dictionary<string, int> equipmentPhaseByDefId = new(StringComparer.Ordinal);
    private bool planOverlayVisible;
    private bool planGestureChanged;
    private bool erasingConstructionPlan;
    private bool erasingStaging;
    private int stagingCellsErasedThisGesture;
    private int stagingCellsWithPlanColorThisGesture;
    private int nativePlanCellsErasedThisGesture;
    private int erasedPlanningLayerThisGesture = -1;
    private float nextGhostPerfLogTime;
    private float nextLabelPerfLogTime;
    private int lastGhostHoverPosition = Pos.Invalid;

    internal bool NativeRelocationActive { get; private set; }

    private void AttachPlanningState(GameState state)
    {
        state.Sig.ToggleOverlay.AddListener(OnToggleOverlay);
        state.Sig.TilesSelected.AddListener(OnUpgradeSelectionChanged);
    }

    private void DetachPlanningState(GameState state)
    {
        state.Sig.ToggleOverlay.RemoveListener(OnToggleOverlay);
        state.Sig.TilesSelected.RemoveListener(OnUpgradeSelectionChanged);
        planOverlayVisible = false;
        ClearRoomNameLabels();
    }

    private void OnUpgradeSelectionChanged(List<Tile> selected)
    {
        if (batchingUpgradeReplacement)
        {
            suppressedUpgradeSelectionRefreshes++;
            return;
        }
        if (ui?.IsOpen != true) return;
        Stopwatch perf = Stopwatch.StartNew();
        ui.Refresh();
        ReportSlowOperation("UpgradeSelectionRefresh", perf.ElapsedMilliseconds,
            "selected=" + (selected?.Count ?? 0), ref nextUpgradePerfLogTime);
    }

    private void OnToggleOverlay(OverlayInfo overlay, bool on)
    {
        planOverlayVisible = overlay?.IdH == OverlayIdH.Plan && on;
        RefreshGhosts(true);
        RefreshRoomNameLabels();
    }

    private void EnsurePlanOverlayVisible()
    {
        if (!planOverlayVisible) S.Sig.ShowOverlay.Send(OverlayId.Plan);
    }

    internal void BeginPlanEraser()
    {
        EnsurePlanOverlayVisible();
        ToolPlan tool = The.Toolbox.Get<ToolPlan>(ToolPlan.ToolId);
        The.Toolbox.SetActive(tool, "clear");
        lastStatus = "Plan Eraser selected. One click or drag clears ordinary Planning cells and removes the highest Construction Plan layer across the highlighted area; staging is preserved.";
        ui?.Refresh();
    }

    internal void BeginStagingArea(StagingAreaType type)
    {
        EnsurePlanOverlayVisible();
        ToolPlan tool = The.Toolbox.Get<ToolPlan>(ToolPlan.ToolId);
        tool.Configure(StagingColor(type).ToString());
        The.Toolbox.SetActive(tool, StagingMode(type));
        lastStatus = "Drawing " + StagingName(type)
            + " staging. Drag a rectangle on the map; drawing another type over a cell replaces the old designation.";
        ui?.Refresh();
    }

    internal void BeginStagingEraser()
    {
        EnsurePlanOverlayVisible();
        ToolPlan tool = The.Toolbox.Get<ToolPlan>(ToolPlan.ToolId);
        The.Toolbox.SetActive(tool, StagingClearMode);
        tool.toolColor = Consts.RedRectColor;
        lastStatus = "Staging Eraser selected. Click or drag over staging cells; construction-plan ghosts are preserved.";
        ui?.Refresh();
    }

    internal void OnPlanToolClickDown(string mode)
    {
        planGestureChanged = false;
        constructionErasePositionsThisGesture.Clear();
        stagingPositionsChangedThisGesture.Clear();
        erasedEntriesThisGesture.Clear();
        stagingCellsErasedThisGesture = 0;
        stagingCellsWithPlanColorThisGesture = 0;
        nativePlanCellsErasedThisGesture = 0;
        erasedPlanningLayerThisGesture = -1;
        erasingConstructionPlan = string.Equals(mode, "clear", StringComparison.Ordinal);
        erasingStaging = string.Equals(mode, StagingClearMode, StringComparison.Ordinal);
    }

    internal bool OnPlanToolDrawAt(string mode, int position)
    {
        if (TryParseStagingMode(mode, out StagingAreaType type))
        {
            SetStagingAt(type, position);
            return true;
        }
        if (erasingStaging && string.Equals(mode, StagingClearMode,
                StringComparison.Ordinal))
        {
            EraseStagingAt(position);
            return true;
        }
        if (erasingConstructionPlan && string.Equals(mode, "clear", StringComparison.Ordinal))
        {
            QueueConstructionPlanEraseAt(position);
            // Core's clear brush also clears staging and unrelated planning
            // colors. Apply this selection to our stored entries on mouse-up.
            return true;
        }
        return false;
    }

    internal void OnPlanToolClickUp(string mode)
    {
        if (erasingConstructionPlan && string.Equals(mode, "clear", StringComparison.Ordinal))
            EraseConstructionPlanSelection();
        if (erasingConstructionPlan && !planGestureChanged)
        {
            lastStatus = "No Construction Plan building or ordinary Planning cell was found in the highlighted area. "
                + "Use Clear blueprint from plan in the Blueprints tab to remove the full design.";
            ui?.Refresh();
        }
        if (planGestureChanged)
        {
            PlanEntry[] removedEntries = erasedEntriesThisGesture.ToArray();
            if (removedEntries.Length > 0)
            {
                entries.RemoveAll(erasedEntriesThisGesture.Contains);
                ApplyIncrementalPlanChange(removedEntries, null);
            }
            foreach (int position in stagingPositionsChangedThisGesture)
                RefreshPlanIndexesAndOverlayAt(position);
            if (stagingPositionsChangedThisGesture.Count > 0)
                ResetExteriorFrontierAdmissionGate();
            if (stagingPositionsChangedThisGesture.Count > 0
                    || nativePlanCellsErasedThisGesture > 0)
                S.Sig.UpdatePlan.Send();
            RefreshDirtyRoomNameLabels();
            if (TryParseStagingMode(mode, out StagingAreaType type))
                lastStatus = StagingName(type) + " staging updated.";
            else if (string.Equals(mode, StagingClearMode, StringComparison.Ordinal))
                lastStatus = stagingCellsErasedThisGesture
                    + " staging cell" + (stagingCellsErasedThisGesture == 1 ? string.Empty : "s")
                    + " removed. Construction-plan ghosts were preserved."
                    + (stagingCellsWithPlanColorThisGesture > 0
                        ? " " + stagingCellsWithPlanColorThisGesture
                            + " cleared cell" + (stagingCellsWithPlanColorThisGesture == 1 ? string.Empty : "s")
                            + " still show their Construction Plan color because they also belong to the construction plan."
                        : string.Empty);
            else
            {
                string buildingResult = erasedEntriesThisGesture.Count > 0
                    ? erasedEntriesThisGesture.Count + " planned building"
                        + (erasedEntriesThisGesture.Count == 1 ? string.Empty : "s")
                        + " removed from the highest layer present across the highlighted area"
                        + (erasedPlanningLayerThisGesture == 0
                            ? "." : "; underlying floors were preserved.")
                    : string.Empty;
                string nativeResult = nativePlanCellsErasedThisGesture > 0
                    ? nativePlanCellsErasedThisGesture + " ordinary Planning cell"
                        + (nativePlanCellsErasedThisGesture == 1 ? string.Empty : "s")
                        + " removed."
                    : string.Empty;
                lastStatus = string.Join(" ", new[] { buildingResult, nativeResult }
                        .Where(value => value.Length > 0))
                    + " Staging areas were preserved.";
            }
            ui?.Refresh();
        }
        erasingConstructionPlan = false;
        erasingStaging = false;
        constructionErasePositionsThisGesture.Clear();
        stagingPositionsChangedThisGesture.Clear();
        erasedEntriesThisGesture.Clear();
    }

    private void SetStagingAt(StagingAreaType type, int position)
    {
        if (position <= 0 || position >= S.GridSize) return;
        bool changed = indoorStaging.Remove(position);
        changed |= outdoorStaging.Remove(position);
        changed |= outdoorEdgeStaging.Remove(position);
        changed |= StagingPositions(type).Add(position);
        if (changed) stagingPositionsChangedThisGesture.Add(position);
        planGestureChanged |= changed;
    }

    private void QueueConstructionPlanEraseAt(int position)
    {
        if (position < 0 || position >= S.GridSize) return;
        constructionErasePositionsThisGesture.Add(position);
    }

    private void EraseConstructionPlanSelection()
    {
        EraseConstructionPlanSelectionCore(S.Map.Plan.Data);
    }

    private void EraseConstructionPlanSelectionCore(int[] planData)
    {
        var candidates = new HashSet<PlanEntry>();
        foreach (int position in constructionErasePositionsThisGesture)
        {
            if (TryEraseOrdinaryPlanCell(position, planData, ownedOverlay))
                nativePlanCellsErasedThisGesture++;
            if (!planEntriesByFootprint.TryGetValue(position,
                    out List<PlanEntry> footprintEntries)) continue;
            foreach (PlanEntry entry in footprintEntries) candidates.Add(entry);
        }
        if (candidates.Count > 0)
        {
            int highestLayer = candidates.Max(entry => LayerRank(entry.Layer));
            foreach (PlanEntry entry in candidates)
                if (LayerRank(entry.Layer) == highestLayer)
                    erasedEntriesThisGesture.Add(entry);
            erasedPlanningLayerThisGesture = highestLayer;
        }
        planGestureChanged = erasedEntriesThisGesture.Count > 0
            || nativePlanCellsErasedThisGesture > 0;
    }

    private static bool TryEraseOrdinaryPlanCell(int position, int[] planData,
        IReadOnlyDictionary<int, int> overlayOwnership)
    {
        if (planData == null || position <= 0 || position >= planData.Length
                || planData[position] == 0) return false;
        if (overlayOwnership != null
                && overlayOwnership.TryGetValue(position, out int ownedColor)
                && planData[position] == ownedColor) return false;
        planData[position] = 0;
        return true;
    }

    private void EraseStagingAt(int position)
    {
        bool removed = indoorStaging.Remove(position);
        removed |= outdoorStaging.Remove(position);
        removed |= outdoorEdgeStaging.Remove(position);
        if (removed)
        {
            stagingPositionsChangedThisGesture.Add(position);
            stagingCellsErasedThisGesture++;
            if (planEntriesByFootprint.TryGetValue(position,
                    out List<PlanEntry> planEntries) && planEntries.Count > 0)
                stagingCellsWithPlanColorThisGesture++;
        }
        planGestureChanged |= removed;
    }

    private void IndexPlanFootprint(PlanEntry entry, int position)
    {
        if (position <= 0 || position >= S.GridSize) return;
        if (!planEntriesByFootprint.TryGetValue(position,
                out List<PlanEntry> footprintEntries))
        {
            footprintEntries = new List<PlanEntry>();
            planEntriesByFootprint[position] = footprintEntries;
        }
        footprintEntries.Add(entry);
    }

    private void RemovePlanFootprint(PlanEntry entry, int position)
    {
        if (!planEntriesByFootprint.TryGetValue(position,
                out List<PlanEntry> footprintEntries)) return;
        footprintEntries.Remove(entry);
        if (footprintEntries.Count == 0) planEntriesByFootprint.Remove(position);
    }

    internal void ApplyIncrementalPlanChange(IEnumerable<PlanEntry> removedEntries,
        IEnumerable<PlanEntry> addedEntries)
    {
        if (S?.Map?.Plan?.Data == null) return;
        PlanEntry[] removed = removedEntries?.Distinct().ToArray()
            ?? Array.Empty<PlanEntry>();
        PlanEntry[] added = addedEntries?.Distinct().ToArray()
            ?? Array.Empty<PlanEntry>();
        if (removed.Length == 0 && added.Length == 0) return;
        if (removed.Length > 0 || added.Length > 0)
        {
            InvalidateConstructionSupportPlan(removed.Concat(added)
                .Select(entry => entry.RoomId));
            ClearAllMaterialWaits();
            ClearAllResearchWaits();
            ResetFloorFrontierValidation();
        }
        var affectedOverlayPositions = new HashSet<int>();
        var affectedGhostPositions = new HashSet<int>();

        foreach (PlanEntry entry in removed)
        {
            int[] footprint = EntryFootprint(entry);
            planIndex.Remove(entry, footprint);
            dirtyRoomNameLabels.Add(entry.RoomId);
            bool multiTile = footprint.Length > 1;
            foreach (int position in footprint)
            {
                affectedOverlayPositions.Add(position);
                affectedGhostPositions.Add(position);
                RemovePlanFootprint(entry, position);
                if (multiTile) ChangeMultiTileGhostCoverage(position, -1);
            }
        }
        foreach (PlanEntry entry in added)
        {
            int[] footprint = EntryFootprint(entry);
            planIndex.Add(entry, footprint);
            dirtyRoomNameLabels.Add(entry.RoomId);
            bool multiTile = footprint.Length > 1;
            foreach (int position in footprint)
            {
                affectedOverlayPositions.Add(position);
                affectedGhostPositions.Add(position);
                IndexPlanFootprint(entry, position);
                if (multiTile) ChangeMultiTileGhostCoverage(position, 1);
            }
        }

        foreach (int position in affectedOverlayPositions)
            RefreshPlanIndexesAndOverlayAt(position);
        foreach (int position in affectedGhostPositions)
            ClearGhostEntryAt(position);
        foreach (int position in affectedGhostPositions)
            IndexGhostEntryAt(position);
        foreach (int position in affectedGhostPositions)
            RestoreGhostAt(position);
        S.Sig.UpdatePlan.Send();
    }

    private void RefreshPlanIndexesAndOverlayAt(int position)
    {
        if (position <= 0 || position >= S.Map.Plan.Data.Length) return;
        PlanEntry floor = null;
        if (planEntriesByFootprint.TryGetValue(position,
                out List<PlanEntry> footprintEntries))
            floor = footprintEntries.LastOrDefault(entry =>
                entry.Layer == WorldLayer.Floor);
        if (floor == null) plannedFloorRoomByPosition.Remove(position);
        else plannedFloorRoomByPosition[position] = floor.RoomId;
        if (footprintEntries?.Any(entry => entry.Layer == WorldLayer.Floor
                && PlannedFloorCanSupportWall(entry)) == true)
            plannedFullFloorPositions.Add(position);
        else plannedFullFloorPositions.Remove(position);

        if (ownedOverlay.TryGetValue(position, out int oldColor))
        {
            if (S.Map.Plan.Data[position] == oldColor) S.Map.Plan.Data[position] = 0;
            ownedOverlay.Remove(position);
        }
        if (S.Map.Plan.Data[position] != 0) return;

        int color = 0;
        PlanEntry visible = VisibleOverlayEntry(footprintEntries);
        if (visible != null) color = OverlayColor(visible);
        else
        {
            foreach (StagingAreaType type in Enum.GetValues(typeof(StagingAreaType)))
            {
                if (!StagingPositions(type).Contains(position)) continue;
                color = StagingColor(type);
                break;
            }
        }
        if (color == 0) return;
        S.Map.Plan.Data[position] = color;
        ownedOverlay[position] = color;
    }

    private void ChangeMultiTileGhostCoverage(int position, int delta)
    {
        if (position <= 0 || delta == 0) return;
        multiTileGhostCoverage.TryGetValue(position, out int count);
        count += delta;
        if (count <= 0) multiTileGhostCoverage.Remove(position);
        else multiTileGhostCoverage[position] = count;
    }

    internal void ApplyMultiTileGhostAlpha(float[] alphaData)
    {
        ApplyMultiTileGhostAlpha(alphaData, multiTileGhostCoverage, 0.12f);
    }

    private static int ApplyMultiTileGhostAlpha(float[] alphaData,
        IReadOnlyDictionary<int, int> coverage, float ghostAlpha)
    {
        if (alphaData == null || coverage == null || ghostAlpha < 0f) return 0;
        int changed = 0;
        foreach (KeyValuePair<int, int> item in coverage)
        {
            int position = item.Key;
            if (item.Value <= 0 || position <= 0 || position >= alphaData.Length
                    || alphaData[position] <= ghostAlpha) continue;
            alphaData[position] = ghostAlpha;
            changed++;
        }
        return changed;
    }

    private void ClearGhostEntryAt(int position)
    {
        if (position <= 0 || position >= S.GridSize) return;
        if (S?.Map?.Grids != null && ownedGhosts.TryGetValue(position, out Tile oldGhost))
        {
            var toolGrid = S.Map.Grids[WorldLayer.Tools];
            if (ReferenceEquals(toolGrid.Get(position), oldGhost))
                toolGrid.TrySet(position, null);
            ownedGhosts.Remove(position);
        }

        ghostEntryByPosition.Remove(position);
    }

    private void IndexGhostEntryAt(int position)
    {
        if (position <= 0 || position >= S.GridSize) return;
        ghostEntryByPosition.Remove(position);
        if (planEntriesByFootprint.TryGetValue(position,
                out List<PlanEntry> footprintEntries))
        {
            PlanEntry root = null;
            foreach (PlanEntry entry in footprintEntries)
            {
                if (entry.Position != position) continue;
                if (root == null || LayerRank(entry.Layer) >= LayerRank(root.Layer))
                    root = entry;
            }
            if (root == null) return;

            int rootRank = LayerRank(root.Layer);
            // A multi-tile upper ghost owns every rendered cell in its
            // footprint. Do not place a lower-layer floor/wall ghost at one of
            // those cells, because Core would draw that one-tile root over the
            // already-split building sprite and leave only its corner visible.
            foreach (PlanEntry covering in footprintEntries)
                if (covering.Position != position
                        && CoveringLayerSuppressesRoot(rootRank,
                            LayerRank(covering.Layer)))
                    return;
            ghostEntryByPosition[position] = root;
        }
    }

    private static bool CoveringLayerSuppressesRoot(int rootRank,
        int coveringRank) => coveringRank >= rootRank;

    internal int StagingAreaCount(StagingAreaType type) => StagingPositions(type).Count;

    internal int ReadyStagingCellCount(StagingAreaType type)
    {
        HashSet<int> reserved = ReservedStagingPositions();
        return StagingPositions(type).Count(position => IsStagingCellReady(type, position)
            && !reserved.Contains(position)
            && (type != StagingAreaType.Indoor
                || IsIndoorStagingClearanceReady(position, 1, 1)));
    }

    private HashSet<int> ReservedStagingPositions()
    {
        var reserved = new HashSet<int>();
        foreach (int entityId in stagedBuildings.Keys.ToArray())
        {
            Tile target = S.FindEntity<Tile>(entityId);
            if (target?.IsActive != true) continue;
            AddRelocationReservation(reserved, target, target.PosIdx, true);
        }
        return reserved;
    }

    private void AddRelocationReservation(HashSet<int> reserved, Tile tile,
        int rootPosition, bool toStaging)
    {
        if (!toStaging || StagingTypeAtDestination(tile, rootPosition) != StagingAreaType.Indoor)
        {
            foreach (int position in TileFootprint(tile, rootPosition))
                reserved.Add(position);
            return;
        }

        int width = Math.Max(1, tile.Transform.RotatedWidth);
        int height = Math.Max(1, tile.Transform.RotatedHeight);
        for (int x = -1; x <= width; x++)
            for (int y = -1; y <= height; y++)
            {
                int position = Pos.NeighborAt(rootPosition, x, y);
                if (!Pos.IsOOB(position)) reserved.Add(position);
            }
    }

    private bool IsIndoorStagingClearanceReady(Tile tile, int rootPosition)
    {
        return IsIndoorStagingClearanceReady(rootPosition,
            Math.Max(1, tile.Transform.RotatedWidth),
            Math.Max(1, tile.Transform.RotatedHeight));
    }

    private bool IsIndoorStagingClearanceReady(int rootPosition, int width, int height)
    {
        for (int x = -1; x <= width; x++)
            for (int y = -1; y <= height; y++)
            {
                int position = Pos.NeighborAt(rootPosition, x, y);
                if (Pos.IsOOB(position)
                        || !indoorStaging.Contains(position)
                        || !IsStagingCellReady(StagingAreaType.Indoor, position)
                        || EntityUtils.IsWallAt(position)
                        || IsReservedByUnfinishedPlan(position))
                    return false;
            }
        return true;
    }

    internal void ClearStagingArea(StagingAreaType type)
    {
        HashSet<int> positions = StagingPositions(type);
        int[] removedPositions = positions.ToArray();
        int removed = removedPositions.Length;
        positions.Clear();
        foreach (int position in removedPositions)
            RefreshPlanIndexesAndOverlayAt(position);
        if (removed > 0) S.Sig.UpdatePlan.Send();
        lastStatus = removed + " cells removed from " + StagingName(type) + " staging.";
        ui?.Refresh();
    }

    internal static string StagingName(StagingAreaType type)
    {
        if (type == StagingAreaType.Indoor) return "Indoor";
        if (type == StagingAreaType.Outdoor) return "Outdoor";
        return "Outdoor Edge";
    }

    private static string StagingMode(StagingAreaType type)
    {
        if (type == StagingAreaType.Indoor) return StagingIndoorMode;
        if (type == StagingAreaType.Outdoor) return StagingOutdoorMode;
        return StagingOutdoorEdgeMode;
    }

    internal static bool IsStagingMode(string mode)
    {
        return TryParseStagingMode(mode, out _);
    }

    private static bool TryParseStagingMode(string mode, out StagingAreaType type)
    {
        if (string.Equals(mode, StagingIndoorMode, StringComparison.Ordinal))
        {
            type = StagingAreaType.Indoor;
            return true;
        }
        if (string.Equals(mode, StagingOutdoorMode, StringComparison.Ordinal))
        {
            type = StagingAreaType.Outdoor;
            return true;
        }
        if (string.Equals(mode, StagingOutdoorEdgeMode, StringComparison.Ordinal))
        {
            type = StagingAreaType.OutdoorEdge;
            return true;
        }
        type = default;
        return false;
    }

    private HashSet<int> StagingPositions(StagingAreaType type)
    {
        if (type == StagingAreaType.Indoor) return indoorStaging;
        if (type == StagingAreaType.Outdoor) return outdoorStaging;
        return outdoorEdgeStaging;
    }

    private void ClearStagingAreas()
    {
        indoorStaging.Clear();
        outdoorStaging.Clear();
        outdoorEdgeStaging.Clear();
    }

    private static int StagingColor(StagingAreaType type)
    {
        if (type == StagingAreaType.Indoor)
            return PlanningColorPalette.IndoorStagingBrush;
        if (type == StagingAreaType.Outdoor)
            return PlanningColorPalette.OutdoorStagingBrush;
        return PlanningColorPalette.OutdoorEdgeStagingBrush;
    }

    private void PaintStagingOverlay()
    {
        foreach (StagingAreaType type in Enum.GetValues(typeof(StagingAreaType)))
        {
            int color = StagingColor(type);
            foreach (int position in StagingPositions(type))
            {
                if (position <= 0 || position >= S.Map.Plan.Data.Length) continue;
                if (S.Map.Plan.Data[position] != 0) continue;
                S.Map.Plan.Data[position] = color;
                ownedOverlay[position] = color;
            }
        }
    }

    private bool IsStagingCellReady(StagingAreaType type, int position)
    {
        if (position <= 0 || position >= S.GridSize) return false;
        Tile floor = EntityUtils.ParentObj(EntityUtils.FloorAt(position));
        if (EntityUtils.ParentObjAt(position) != null) return false;
        if (type == StagingAreaType.Indoor)
            return floor?.IsConstructed == true;
        bool outside = S.Sys.Areas.IsOutside(position);
        if (type == StagingAreaType.Outdoor)
            return PlanningOverhaulStagingPolicy.OutdoorCellIsReady(
                floor != null, floor?.IsConstructed == true, outside);
        if (floor != null || !outside) return false;
        return HasAdjacentConstructedFloor(position);
    }

    private bool OccupiesDesignatedStaging(Tile tile)
    {
        if (tile == null) return false;
        int[] footprint = TileFootprint(tile, tile.PosIdx);
        if (footprint.Length == 0) return false;
        return footprint.All(indoorStaging.Contains)
            || footprint.All(outdoorStaging.Contains)
            || footprint.All(outdoorEdgeStaging.Contains);
    }

    private bool OccupiesCompatibleDesignatedStaging(Tile tile)
    {
        if (tile == null) return false;
        int[] footprint = TileFootprint(tile, tile.PosIdx);
        if (footprint.Length == 0) return false;
        foreach (StagingAreaType type in StagingTypesFor(tile))
        {
            if (!footprint.All(StagingPositions(type).Contains)) continue;
            if (type == StagingAreaType.Outdoor
                    && footprint.Any(position => !S.Sys.Areas.IsOutside(position)))
                continue;
            if (type == StagingAreaType.Indoor
                    && tile.Outdoors?.MustBeOutdoors == false
                    && footprint.Any(S.Sys.Areas.IsOutside)) continue;
            return true;
        }
        return false;
    }

    private static bool HasAdjacentConstructedFloor(int position)
    {
        int[] neighbors = { Pos.N(position), Pos.S(position), Pos.E(position), Pos.W(position) };
        return neighbors.Any(neighbor => !Pos.IsOOB(neighbor)
            && EntityUtils.ParentObj(EntityUtils.FloorAt(neighbor))?.IsConstructed == true);
    }

    private static bool SamePlanningLayer(PlanEntry first, PlanEntry second)
    {
        return PlanningLayer(first) == PlanningLayer(second);
    }

    private static int PlanningLayer(PlanEntry entry)
    {
        // Resolve the definition again instead of trusting legacy/imported
        // numeric layer data. Construction planning has exactly two collision
        // bands: floors, and everything placed above a floor.
        Def def = The.Defs.TryGet(entry?.DefId);
        int worldLayer = def?.LayerId ?? entry?.Layer ?? WorldLayer.Objects;
        return worldLayer == WorldLayer.Floor ? 1 : 2;
    }

    private int[] EntryFootprint(PlanEntry entry)
    {
        int[] indexedFootprint = planIndex.FootprintFor(entry);
        if (indexedFootprint.Length > 0) return indexedFootprint;

        Def def = The.Defs.TryGet(entry?.DefId);
        Tile prototype = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
        if (prototype == null) return entry == null ? Array.Empty<int>() : new[] { entry.Position };
        prototype.Transform.Rotation = (Facing.Type)entry.Rotation;
        int[] buffer = new int[Math.Max(1,
            prototype.Transform.RotatedWidth * prototype.Transform.RotatedHeight)];
        int count = prototype.Transform.GetSubtilePos(entry.Position, buffer);
        if (count == buffer.Length) return buffer;
        Array.Resize(ref buffer, count);
        return buffer;
    }

    private static int LayerRank(int layer)
    {
        if (layer == WorldLayer.Objects) return 3;
        if (layer == WorldLayer.Walls) return 2;
        if (layer == WorldLayer.Floor) return 1;
        return 0;
    }

    internal void RefreshGhosts(bool rebuild = false)
    {
        Stopwatch perf = Stopwatch.StartNew();
        try
        {
        if (rebuild || !planOverlayVisible) ClearOwnedGhosts();
        if (rebuild) RebuildGhostEntryIndex();
        if (!planOverlayVisible || S?.Map?.Grids == null) return;
        var toolGrid = S.Map.Grids[WorldLayer.Tools];
        // Core's placement preview overwrites the saved ghost beneath the
        // cursor and clears its former cell as it moves. The OnHover postfix
        // reconciles those two cells, leaving the moving preview exclusive at
        // its current position while immediately restoring every saved ghost
        // behind it.
        foreach (int position in ownedGhosts.Keys.ToArray())
            if (!ReferenceEquals(toolGrid.Get(position), ownedGhosts[position]))
                ownedGhosts.Remove(position);
        if (ghostEntryByPosition.Count == 0 && entries.Count > 0)
            RebuildGhostEntryIndex();
        foreach (KeyValuePair<int, PlanEntry> item in ghostEntryByPosition)
        {
            RestoreGhostAt(item.Key);
        }
        }
        finally
        {
            ReportSlowOperation("RefreshGhosts", perf.ElapsedMilliseconds,
                "rebuild=" + rebuild + "; overlayVisible=" + planOverlayVisible
                + "; ownedGhosts=" + ownedGhosts.Count,
                ref nextGhostPerfLogTime);
        }
    }

    private void RebuildGhostEntryIndex()
    {
        ghostEntryByPosition.Clear();
        foreach (int position in entries.Select(entry => entry.Position).Distinct())
            IndexGhostEntryAt(position);
    }

    internal void RefreshGhostAt(Vector2 coords)
    {
        if (!planOverlayVisible || S?.Map?.Grids == null) return;
        var toolGrid = S.Map.Grids[WorldLayer.Tools];
        int current = EntityUtils.ToPosSafe(coords);
        if (lastGhostHoverPosition != current)
            RestoreGhostAt(lastGhostHoverPosition);
        if (current > 0 && current < S.GridSize
                && ownedGhosts.TryGetValue(current, out Tile old)
                && !ReferenceEquals(toolGrid.Get(current), old))
            ownedGhosts.Remove(current);
        lastGhostHoverPosition = current;
    }

    private void RestoreGhostAt(int position)
    {
        if (position <= 0 || position >= S.GridSize || S?.Map?.Grids == null) return;
        var toolGrid = S.Map.Grids[WorldLayer.Tools];
        Tile current = toolGrid.Get(position);
        if (ownedGhosts.TryGetValue(position, out Tile old))
        {
            if (ReferenceEquals(current, old)) return;
            ownedGhosts.Remove(position);
        }
        if (current != null || !ghostEntryByPosition.TryGetValue(position,
                out PlanEntry entry)) return;
        Def def = The.Defs.TryGet(entry.DefId);
        Tile ghost = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
        if (ghost == null) return;
        ghost.Transform.Rotation = (Facing.Type)entry.Rotation;
        ghost.Graphics.OnReady(false);
        // A persistent saved ghost is not the active placement cursor. Leaving
        // placement state unset keeps Core's detailed construction rendering
        // instead of replacing the whole sprite with its solid green/red
        // placement-validity shader. The Planning overlay supplies the saved
        // room/building color beneath the complete multi-tile sprite.
        ghost.PlacementCheckResult = null;
        toolGrid.TrySet(position, ghost);
        if (ReferenceEquals(toolGrid.Get(position), ghost)) ownedGhosts[position] = ghost;
    }

    private void ClearOwnedGhosts(bool forceStaleGraphicsCleanup = false)
    {
        if (S?.Map?.Grids != null)
        {
            var toolGrid = S.Map.Grids[WorldLayer.Tools];
            var trackedPositions = new HashSet<int>(ownedGhosts.Keys);
            foreach (KeyValuePair<int, Tile> item in ownedGhosts)
            {
                if (item.Key <= 0 || item.Key >= S.GridSize) continue;
                Tile current = toolGrid.Get(item.Key);
                if (ReferenceEquals(current, item.Value))
                    toolGrid.TrySet(item.Key, null);
                else if (forceStaleGraphicsCleanup && current == null)
                {
                    // The active placement cursor can temporarily replace a
                    // saved ghost and leave split sprite pieces in Core's tile
                    // renderer after the root cell is cleared. Replaying the
                    // tracked root and removing it gives Core the previous
                    // multi-tile value it needs to unset every sprite piece.
                    toolGrid.TrySet(item.Key, item.Value);
                    toolGrid.TrySet(item.Key, null);
                }
            }
            if (forceStaleGraphicsCleanup)
            {
                foreach (KeyValuePair<int, PlanEntry> item in ghostEntryByPosition)
                {
                    int position = item.Key;
                    if (trackedPositions.Contains(position) || position <= 0
                            || position >= S.GridSize || toolGrid.Get(position) != null)
                        continue;
                    Def def = The.Defs.TryGet(item.Value.DefId);
                    Tile stale = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
                    if (stale == null) continue;
                    stale.Transform.Rotation = (Facing.Type)item.Value.Rotation;
                    stale.Graphics.OnReady(false);
                    stale.PlacementCheckResult = null;
                    toolGrid.TrySet(position, stale);
                    toolGrid.TrySet(position, null);
                }
            }
        }
        ownedGhosts.Clear();
        lastGhostHoverPosition = Pos.Invalid;
        if (!planOverlayVisible || forceStaleGraphicsCleanup)
            ghostEntryByPosition.Clear();
    }

    private void RefreshRoomNameLabels()
    {
        Stopwatch perf = Stopwatch.StartNew();
        try
        {
            dirtyRoomNameLabels.Clear();
            ClearRoomNameLabels();
            if (!planOverlayVisible || S?.Map?.Grids == null) return;
            foreach (PlanRoom room in rooms.OrderBy(item => item.Order))
                RefreshRoomNameLabel(room.Id);
        }
        finally
        {
            ReportSlowOperation("RefreshRoomNameLabels", perf.ElapsedMilliseconds,
                "overlayVisible=" + planOverlayVisible
                + "; labels=" + roomNameLabels.Count,
                ref nextLabelPerfLogTime);
        }
    }

    private void RefreshDirtyRoomNameLabels()
    {
        if (dirtyRoomNameLabels.Count == 0) return;
        foreach (int roomId in dirtyRoomNameLabels.ToArray())
            RefreshRoomNameLabel(roomId);
        dirtyRoomNameLabels.Clear();
    }

    private void RefreshRoomNameLabel(int roomId)
    {
        if (roomNameLabels.TryGetValue(roomId, out GameTextTMP existing))
        {
            existing?.Destroy();
            roomNameLabels.Remove(roomId);
        }
        if (!planOverlayVisible || S?.Map?.Grids == null) return;
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == roomId);
        if (room == null
                || !planIndex.TryGetRoomLabelPosition(roomId,
                    out int labelPosition)) return;

        bool active = room.Id == currentRoomId;
        string text = active ? "[ " + room.Name + " ]" : room.Name;
        GameTextTMP label = GameTextTMP.Create("TileLabel", text,
            Pos.ToVector(labelPosition));
        label.SetFontSize(active ? 2.2f : 1.8f);
        label.SetColor(RoomDisplayColor(room.Id));
        if (label.Transform is RectTransform rect)
            rect.sizeDelta = new Vector2(Mathf.Max(4f, text.Length * 0.28f), 1f);
        roomNameLabels[room.Id] = label;
    }

    private void ClearRoomNameLabels()
    {
        foreach (GameTextTMP label in roomNameLabels.Values) label?.Destroy();
        roomNameLabels.Clear();
    }

    private void AssessRelocationsForPulse(PulsePreview preview)
    {
        if (preview.Room == null) return;
        List<Tile> blockers = StructuralBlockers(preview.Room.Id);
        var reserved = new HashSet<int>();
        foreach (Tile blocker in blockers)
        {
            ReinstallableComp relocation = blocker.Reinstallable;
            if (relocation?.IsPending == true)
            {
                preview.RelocationsInProgress++;
                Tile target = relocation.Target?.Tile;
                if (target != null)
                    AddRelocationReservation(reserved, target, target.PosIdx,
                        stagedBuildings.ContainsKey(target.Id));
                continue;
            }
            if (!CanEventuallyRelocate(blocker)
                    || !TryFindRelocationDestination(blocker, preview.Room.Id,
                        reserved, out int destination, out PlanEntry directEntry,
                        directRoomId: Continuous ? 0 : preview.Room.Id))
            {
                preview.RelocationsWaiting++;
                continue;
            }
            preview.RelocationsReady++;
            if (directEntry == null)
                AddRelocationReservation(reserved, blocker, destination, true);
            else
                reserved.UnionWith(EntryFootprint(directEntry));
        }

        foreach (PlanEntry entry in preview.Pending)
        {
            if (CanPlaceStructuralNow(entry)) continue;
            if (entry.Layer == WorldLayer.Walls
                    && !HasConstructedWallSupport(entry))
            {
                if (HasWallSupportReadyOrPlanned(entry))
                    continue;
                preview.Blocked++;
                preview.BlockedEntries.Add(entry);
                preview.BlockedDetails.Add(EntryName(entry) + " at "
                    + PositionText(entry.Position)
                    + ": requires a completed full floor across its footprint; "
                    + "the current floor is missing or partial and no full "
                    + "supporting floor is planned");
                continue;
            }
            if (BlockersFor(entry).Count > 0) continue;
            Tile existing = ExistingAt(entry);
            Def def = The.Defs.TryGet(entry.DefId);
            Tile prototype = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
            if (prototype == null) continue;
            prototype.Transform.Rotation = (Facing.Type)entry.Rotation;
            if (existing == null || prototype.Constructable?.CanReplaceWith(entry.Position, out _) != true)
            {
                preview.Blocked++;
                preview.BlockedEntries.Add(entry);
                preview.BlockedDetails.Add(EntryName(entry) + " at "
                    + PositionText(entry.Position)
                    + ": native placement validation rejected this destination");
            }
        }
    }

    private List<Tile> StructuralBlockers(int roomId)
    {
        var result = new List<Tile>();
        var seen = new HashSet<int>();
        foreach (PlanEntry entry in RoomStructuralEntries(roomId))
        {
            Tile existing = ExistingAt(entry);
            if (existing != null && string.Equals(existing.DefinitionId, entry.DefId,
                    StringComparison.Ordinal)) continue;
            if (entry.IssuedByPlan && existing?.PendingReplacement?.IsPending == true) continue;
            if (CanPlaceStructuralNow(entry)) continue;
            foreach (Tile blocker in BlockersFor(entry))
                if (blocker?.IsConstructed == true && seen.Add(blocker.Id)) result.Add(blocker);
        }
        return result;
    }

    private List<Tile> BlockersFor(PlanEntry entry)
    {
        var result = new List<Tile>();
        var seen = new HashSet<int>();
        bool instantFloorReplacement = IsInstantFloorReplacement(entry);
        Def destinationDef = entry?.Layer == WorldLayer.Floor
            ? null : The.Defs.TryGet(entry?.DefId);
        Tile destinationPrototype = destinationDef == null ? null
            : EntityUtils.CreatePrototype<Tile>(destinationDef);
        if (destinationPrototype != null)
            destinationPrototype.Transform.Rotation = (Facing.Type)entry.Rotation;
        foreach (int position in EntryFootprint(entry))
        {
            // Core's Sandbox replacement removes the old floor, queues its
            // dangling-object check, and only then places the new floor. Treat
            // walls above that floor as blockers so the planner clears them
            // deliberately instead of letting that check break them down.
            if (instantFloorReplacement)
            {
                Tile wall = EntityUtils.ParentObjAt(position, WorldLayer.Walls);
                if (wall != null && seen.Add(wall.Id)) result.Add(wall);
            }
            Tile blocker = EntityUtils.ParentObjAt(position);
            if (blocker != null && seen.Add(blocker.Id)) result.Add(blocker);
            if (entry.Layer != WorldLayer.Floor)
                AddFunctionalClearanceBlockers(position,
                    destinationPrototype, result, seen);
        }
        if (entry.Layer != WorldLayer.Floor)
            AddDestinationFunctionalClearanceBlockers(entry.Position,
                destinationPrototype, result, seen);
        return result;
    }

    private void AddFunctionalClearanceBlockers(int destinationPosition,
        Tile destinationPrototype, ICollection<Tile> blockers,
        ISet<int> seen)
    {
        bool workSpotCompatible = destinationPrototype?.Transform
            ?.IsWorkSpotCompatible == true;
        Pos.ForEachAround(destinationPosition, 1, false, false, neighbor =>
        {
            if (!S.Grid.IsWithinBounds(neighbor)) return true;
            Tile candidate = EntityUtils.ParentObj(EntityUtils.ObjAt(neighbor));
            if (candidate?.IsActive != true || seen.Contains(candidate.Id))
                return true;
            TileTransformComp transform = candidate.Transform;
            bool blocks = transform.HasInPort
                    && transform.RotatedInPort(candidate.PosIdx)
                        == destinationPosition
                || transform.HasOutPort
                    && transform.RotatedOutPort(candidate.PosIdx)
                        == destinationPosition
                || !workSpotCompatible && transform.HasWorkSpot
                    && transform.RotatedWorkSpot(candidate.PosIdx)
                        == destinationPosition;
            if (blocks)
            {
                seen.Add(candidate.Id);
                blockers.Add(candidate);
            }
            return true;
        });
    }

    private void AddDestinationFunctionalClearanceBlockers(
        int destinationPosition, Tile destinationPrototype,
        ICollection<Tile> blockers, ISet<int> seen)
    {
        foreach (FunctionalClearanceCell clearance in
                 FunctionalClearanceCells(destinationPrototype,
                     destinationPosition))
        {
            Tile wall = EntityUtils.ParentObjAt(clearance.Position,
                WorldLayer.Walls);
            if (wall?.IsActive == true && seen.Add(wall.Id))
                blockers.Add(wall);

            Tile candidate = EntityUtils.ParentObjAt(clearance.Position,
                WorldLayer.Objects);
            if (candidate?.IsActive != true
                    || clearance.Kind == FunctionalClearanceKind.WorkSpot
                        && candidate.Transform.IsWorkSpotCompatible
                    || !seen.Add(candidate.Id)) continue;
            blockers.Add(candidate);
        }
    }

    private string PlacementBlockerDescription(PlanEntry destination,
        Tile blocker)
    {
        bool occupiesFootprint = EntryFootprint(destination).Any(position =>
            EntityUtils.ParentObjAt(position)?.Id == blocker?.Id);
        if (occupiesFootprint)
            return "Destination occupied for " + EntryName(destination)
                + " at " + PositionText(destination.Position) + " by "
                + (blocker?.Definition?.NameT ?? blocker?.DefinitionId
                    ?? "an unknown building");
        return "Functional clearance for " + EntryName(destination)
            + " at " + PositionText(destination.Position) + " is blocked by "
            + (blocker?.Definition?.NameT ?? blocker?.DefinitionId
                ?? "an unknown building") + " at "
            + PositionText(blocker?.PosIdx ?? Pos.Invalid);
    }

    private bool IsInstantFloorReplacement(PlanEntry entry)
    {
        if (!A.InstantBuild || entry?.Layer != WorldLayer.Floor) return false;
        Tile existing = ExistingAt(entry);
        return existing?.IsConstructed == true
            && !string.Equals(existing.DefinitionId, entry.DefId,
                StringComparison.Ordinal);
    }

    private bool CanPlaceStructuralNow(PlanEntry entry)
    {
        // An instant floor replacement briefly leaves the cell floorless.
        // Core's queued dangling-object check can otherwise dismantle a wall
        // or building above it before the replacement floor is placed.
        if (IsInstantFloorReplacement(entry)
                && BlockersFor(entry).Any(blocker => blocker?.IsActive == true
                    && blocker.IsConstructed)) return false;
        Def def = The.Defs.TryGet(entry.DefId);
        Tile prototype = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
        if (prototype == null) return false;
        prototype.Transform.Rotation = (Facing.Type)entry.Rotation;
        return CanPlannerPlaceTile(entry.Position, prototype, entry.Layer);
    }

    private static bool HasConstructedFloorNeighbor(PlanEntry entry)
    {
        if (entry.Layer != WorldLayer.Floor) return true;
        return EntityUtils.HasCardinalConstructedFloor(entry.Position);
    }

    private bool PlannedFloorCanSupportWall(PlanEntry floorEntry)
    {
        if (floorEntry?.Layer != WorldLayer.Floor) return false;
        Def def = The.Defs.TryGet(floorEntry.DefId);
        Tile prototype = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
        return prototype?.Integrity?.CanHaveFullIntegrity == true;
    }

    private bool HasConstructedWallSupport(PlanEntry entry)
    {
        return EntryFootprint(entry).All(position =>
        {
            Tile floor = EntityUtils.ParentObj(EntityUtils.FloorAt(position));
            return floor?.IsConstructed == true
                && floor.Integrity?.CanHaveFullIntegrity == true
                && floor.Goal?.JobIdH != JobTypeIdH.Demolition;
        });
    }

    private bool HasWallSupportReadyOrPlanned(PlanEntry entry)
    {
        return EntryFootprint(entry).All(position =>
        {
            Tile floor = EntityUtils.ParentObj(EntityUtils.FloorAt(position));
            bool constructedSupport = floor?.IsConstructed == true
                && floor.Integrity?.CanHaveFullIntegrity == true
                && floor.Goal?.JobIdH != JobTypeIdH.Demolition;
            return constructedSupport || plannedFullFloorPositions.Contains(position);
        });
    }

    private static bool CanEventuallyRelocate(Tile tile)
    {
        ReinstallableComp relocation = tile?.Reinstallable;
        return relocation != null && !relocation.IsForbidden && !tile.IsImmutable
            && tile.IsConstructed;
    }

    private bool TryFindRelocationDestination(Tile source, int roomId,
        HashSet<int> reserved, out int destination, out PlanEntry directEntry,
        EquipmentStageState stageState = null, int directRoomId = 0)
    {
        if (TryFindDirectRelocationDestination(source, roomId, reserved,
                out destination, out directEntry, stageState, directRoomId,
                requireEquipmentPhase:
                    source?.Definition?.LayerId == WorldLayer.Objects))
            return true;
        // Walls serve planned structural destinations. A surplus wall is
        // demolished once those are filled; it does not need a staging trip.
        if (source.Definition.LayerId == WorldLayer.Walls) return false;
        // A special original can wait at its current position for its final
        // destination. Staging is needed only to clear a planned placement.
        if (IsPlannerRemovalProtected(source)
                && !ProtectedBuildingBlocksPlan(source)) return false;

        return TryFindStagingDestination(source, reserved, out destination);
    }

    private bool TryFindStagingDestination(Tile source, HashSet<int> reserved,
        out int destination)
    {
        destination = Pos.Invalid;
        HashSet<int> pendingStaging = ReservedStagingPositions();
        var allReserved = new HashSet<int>(reserved);
        allReserved.UnionWith(pendingStaging);
        foreach (StagingAreaType type in StagingTypesFor(source))
        {
            foreach (int candidate in StagingPositions(type)
                         .Where(position => IsStagingCellReady(type, position))
                         .OrderBy(position => Pos.SqrDistance(source.PosIdx, position)))
            {
                int[] footprint = TileFootprint(source, candidate);
                if (footprint.Any(position => !StagingPositions(type).Contains(position)
                        || !IsStagingFootprintCellReady(type, position)
                        || reserved.Contains(position)
                        || pendingStaging.Contains(position)
                        || IsReservedByUnfinishedPlan(position))) continue;
                if (type == StagingAreaType.Indoor
                        && source.Outdoors?.MustBeOutdoors == false
                        && footprint.Any(S.Sys.Areas.IsOutside)) continue;
                if (type == StagingAreaType.Indoor
                        && !IsIndoorStagingClearanceReady(source, candidate)) continue;
                if (!CanPlaceRelocation(source, candidate, source.Transform.Rotation,
                        allReserved)) continue;
                if (!TryPrepareRelocationPower(source, candidate, out _)) continue;
                destination = candidate;
                return true;
            }
        }
        return false;
    }


    private bool DirectDestinationStructureIsReady(PlanEntry entry)
    {
        if (IsExteriorRoom(entry?.RoomId ?? 0))
            return ExteriorRelocationDestinationFloorIsReady(entry);

        // Clearing can use a completed final footprint even if another part
        // of its room still needs structure. Staged returns retain their
        // separate room-wide equipment-stage gates.
        foreach (int position in EntryFootprint(entry))
        {
            if (!planEntriesByFootprint.TryGetValue(position,
                    out List<PlanEntry> footprintEntries)) continue;
            foreach (PlanEntry structural in footprintEntries
                         .Where(IsRoomStructuralEntry))
            {
                // A wall's own destination must be empty, so only its floor
                // and other supporting layers can be prerequisites.
                if (entry.Layer == WorldLayer.Walls
                        && structural.Layer == WorldLayer.Walls) continue;
                Tile existing = ExistingAt(structural);
                if (existing?.IsConstructed != true
                        || !string.Equals(existing.DefinitionId, structural.DefId,
                            StringComparison.Ordinal)
                        || existing.Goal?.JobIdH == JobTypeIdH.Demolition
                        || existing.PendingReplacement?.IsPending == true)
                    return false;
            }
        }
        return true;
    }

    private bool IsStagingFootprintCellReady(StagingAreaType type, int position)
    {
        if (type != StagingAreaType.OutdoorEdge) return IsStagingCellReady(type, position);
        if (position <= 0 || position >= S.GridSize) return false;
        return EntityUtils.ParentObj(EntityUtils.FloorAt(position)) == null
            && EntityUtils.ParentObjAt(position) == null
            && S.Sys.Areas.IsOutside(position);
    }

    private bool CanPlaceRelocation(Tile source, int destination, Facing.Type rotation,
        HashSet<int> reserved)
    {
        if (!ProtectedDestinationIsReady(source, destination, rotation)) return false;
        Tile prototype = EntityUtils.CreatePrototype<Tile>(source.Definition);
        if (prototype == null) return false;
        prototype.Transform.Rotation = rotation;
        int[] footprint = TileFootprint(prototype, destination);
        if (footprint.Any(reserved.Contains)) return false;
        return CanPlannerPlaceTile(destination, prototype,
            source.Definition.LayerId, source);
    }

    private bool IsReservedByUnfinishedPlan(int position)
    {
        if (!planEntriesByFootprint.TryGetValue(position,
                out List<PlanEntry> footprintEntries)) return false;
        foreach (PlanEntry entry in footprintEntries)
        {
            Tile existing = ExistingAt(entry);
            if (existing?.IsConstructed == true
                    && string.Equals(existing.DefinitionId, entry.DefId,
                        StringComparison.Ordinal)) continue;
            return true;
        }
        return false;
    }

    private static int[] TileFootprint(Tile tile, int position)
    {
        int[] buffer = new int[Math.Max(1,
            tile.Transform.RotatedWidth * tile.Transform.RotatedHeight)];
        int count = tile.Transform.GetSubtilePos(position, buffer);
        if (count == buffer.Length) return buffer;
        Array.Resize(ref buffer, count);
        return buffer;
    }

    private static FunctionalClearanceCell[] FunctionalClearanceCells(
        Tile tile, int position)
    {
        if (tile?.Transform == null || !tile.Transform.HasAnyPort)
            return Array.Empty<FunctionalClearanceCell>();
        var footprint = new HashSet<int>(TileFootprint(tile, position));
        var cells = new List<FunctionalClearanceCell>(3);
        if (tile.Transform.HasInPort)
            AddFunctionalClearanceCell(cells, footprint,
                tile.Transform.RotatedInPort(position),
                FunctionalClearanceKind.InputPort);
        if (tile.Transform.HasOutPort)
            AddFunctionalClearanceCell(cells, footprint,
                tile.Transform.RotatedOutPort(position),
                FunctionalClearanceKind.OutputPort);
        if (tile.Transform.HasWorkSpot)
            AddFunctionalClearanceCell(cells, footprint,
                tile.Transform.RotatedWorkSpot(position),
                FunctionalClearanceKind.WorkSpot);
        return cells.ToArray();
    }

    private static void AddFunctionalClearanceCell(
        ICollection<FunctionalClearanceCell> cells, ISet<int> footprint,
        int position, FunctionalClearanceKind kind)
    {
        // Core ignores a port or work spot embedded in the building itself.
        // Only external cells can reject another building's placement.
        if (!footprint.Contains(position))
            cells.Add(new FunctionalClearanceCell(position, kind));
    }

    private static StagingAreaType StagingTypeFor(Tile source)
    {
        ProtectedBuildingLocation special = ProtectedLocationFor(source.DefinitionId);
        if (special == ProtectedBuildingLocation.IndoorAtmosphere) return StagingAreaType.Indoor;
        if (special == ProtectedBuildingLocation.ExteriorEdge) return StagingAreaType.OutdoorEdge;
        PlacementConstraint placement = source.Constructable?.Placement
            ?? PlacementConstraint.OnFloor;
        bool? mustBeOutdoors = source.Outdoors == null
            ? null : source.Outdoors.MustBeOutdoors;
        return PlanningOverhaulStagingPolicy.SelectType(placement,
            mustBeOutdoors, EntityUtils.FloorAt(source.PosIdx) != null);
    }

    private bool EnsureNativeStructuralGoal(PlanEntry entry, Tile existing,
        out bool restored)
    {
        restored = false;
        if (entry == null || existing?.IsActive != true
                || existing.Constructable == null
                || relocationTargets.ContainsKey(existing.Id)) return false;

        if (existing.IsConstructed)
        {
            if (existing.PendingReplacement?.IsPending != true
                    || !string.Equals(existing.PendingReplacement.TargetDef?.Id,
                        entry.DefId, StringComparison.Ordinal)) return false;
            if (existing.Goal == null || AIGoal.IsArchived(existing.Goal.State))
            {
                if (!RestartOrphanedStructuralReplacement(entry, existing))
                    return false;
                restored = true;
                existing = ExistingAt(entry);
            }
            if (existing?.PendingReplacement?.IsPending != true
                    || existing.Goal == null
                    || AIGoal.IsArchived(existing.Goal.State)) return A.InstantBuild;
            planIndex.SetIssued(entry, true);
            planConstructionRooms[existing.Id] = entry.RoomId;
            ApplyIssuedGoalPriority(entry, existing, G.Ticks);
            return true;
        }

        if (!string.Equals(existing.DefinitionId, entry.DefId,
                StringComparison.Ordinal)) return false;

        if (existing.Goal == null || AIGoal.IsArchived(existing.Goal.State))
        {
            if (existing.Goal != null)
                existing.ClearGoal("Planning Overhaul is restoring an orphaned structural construction task.");
            existing.Constructable.CreateConstructGoal(null);
            restored = existing.Goal != null;
        }
        if (existing.Goal == null) return false;

        planIndex.SetIssued(entry, true);
        planConstructionRooms[existing.Id] = entry.RoomId;
        ApplyIssuedGoalPriority(entry, existing, G.Ticks);
        return true;
    }

    private bool RestartOrphanedStructuralReplacement(PlanEntry entry,
        Tile existing)
    {
        Def def = The.Defs.TryGet(entry.DefId);
        Tile replacement = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
        if (replacement?.Constructable == null) return false;
        replacement.Transform.Rotation = (Facing.Type)entry.Rotation;

        if (existing.Goal != null)
            existing.ClearGoal("Planning Overhaul is restoring an orphaned native replacement task.");
        existing.PendingReplacement.CancelReplacement();
        planIndex.SetIssued(entry, false);

        var command = new CmdPlaceTile(entry.Position, replacement,
            warn: false, instant: A.InstantBuild);
        command.Execute(S);
        if (!command.IsSuccess) return false;

        Tile restarted = ExistingAt(entry);
        if (A.InstantBuild) return restarted?.IsConstructed == true
            && string.Equals(restarted.DefinitionId, entry.DefId,
                StringComparison.Ordinal);
        return restarted?.PendingReplacement?.IsPending == true
            && string.Equals(restarted.PendingReplacement.TargetDef?.Id,
                entry.DefId, StringComparison.Ordinal)
            && restarted.Goal != null
            && !AIGoal.IsArchived(restarted.Goal.State);
    }

    private bool RoomHasActiveStructuralJob(int roomId)
    {
        foreach (PlanEntry entry in RoomStructuralEntries(roomId)
                     .Where(item => item.IssuedByPlan))
        {
            if (!HasConstructedFloorNeighbor(entry)) continue;
            Tile tile = ExistingAt(entry);
            if (tile?.IsActive != true || tile.Goal == null
                    || AIGoal.IsArchived(tile.Goal.State)) continue;
            if (!tile.IsConstructed
                    && string.Equals(tile.DefinitionId, entry.DefId,
                        StringComparison.Ordinal)) return true;
            if (tile.PendingReplacement?.IsPending == true
                    && string.Equals(tile.PendingReplacement.TargetDef?.Id,
                        entry.DefId, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private int RestoreMissingStructuralGoals(IEnumerable<PlanEntry> candidates)
    {
        if (candidates == null) return 0;
        Stopwatch budget = Stopwatch.StartNew();
        int examined = 0;
        int restored = 0;
        foreach (PlanEntry entry in candidates)
        {
            if (examined >= StructuralCandidatesPerTick
                    || examined > 0 && budget.ElapsedMilliseconds
                        >= StructuralBatchBudgetMilliseconds)
                break;
            examined++;
            if (!HasConstructedFloorNeighbor(entry)) continue;
            Tile existing = ExistingAt(entry);
            if (EnsureNativeStructuralGoal(entry, existing,
                    out bool wasRestored) && wasRestored)
                restored++;
            if (restored >= StructuralOrdersPerTick) break;
        }
        return restored;
    }

    private string DescribeStructuralDependencies(IEnumerable<PlanEntry> candidates,
        int maximum = 4)
    {
        if (candidates == null) return string.Empty;
        var details = new List<string>();
        foreach (PlanEntry entry in candidates.Where(item => item != null)
                     .Distinct().Take(maximum))
        {
            Tile existing = ExistingAt(entry);
            if (existing?.IsConstructed == false
                    && string.Equals(existing.DefinitionId, entry.DefId,
                        StringComparison.Ordinal))
            {
                details.Add(existing.Goal == null
                    ? EntryName(entry) + " at " + PositionText(entry.Position)
                        + " is a construction ghost without a native task; Core did not retain "
                        + "the construction job when it was restored this pass"
                    : EntryName(entry) + " at " + PositionText(entry.Position)
                        + " is waiting on " + DescribePendingPlanGoal(existing.Goal));
                continue;
            }

            if (existing?.PendingReplacement?.IsPending == true
                    && string.Equals(existing.PendingReplacement.TargetDef?.Id,
                        entry.DefId, StringComparison.Ordinal))
            {
                details.Add(EntryName(entry) + " at "
                    + PositionText(entry.Position)
                    + " has a native replacement task waiting on "
                    + DescribePendingPlanGoal(existing.Goal));
                continue;
            }

            if (entry.Layer == WorldLayer.Floor
                    && !HasConstructedFloorNeighbor(entry))
            {
                int[] neighbors = { Pos.N(entry.Position), Pos.E(entry.Position),
                    Pos.S(entry.Position), Pos.W(entry.Position) };
                string ownerNames = PlannedRoomNames(neighbors
                    .Where(plannedFloorRoomByPosition.ContainsKey)
                    .Select(position => plannedFloorRoomByPosition[position]));
                details.Add(EntryName(entry) + " at " + PositionText(entry.Position)
                    + " is waiting for a completed adjacent floor"
                    + (string.IsNullOrEmpty(ownerNames) ? string.Empty
                        : " planned in " + ownerNames));
                continue;
            }

            if (!HasConstructedFloorNeighbor(entry)
                    || entry.Layer == WorldLayer.Walls
                        && !HasConstructedWallSupport(entry))
            {
                int[] footprint = EntryFootprint(entry);
                string ownerNames = PlannedRoomNames(footprint
                    .Where(position => plannedFullFloorPositions.Contains(position)
                        && plannedFloorRoomByPosition.ContainsKey(position))
                    .Select(position => plannedFloorRoomByPosition[position]));
                details.Add(EntryName(entry) + " at " + PositionText(entry.Position)
                    + " is waiting for completed full-floor support"
                    + (string.IsNullOrEmpty(ownerNames) ? string.Empty
                        : " planned in " + ownerNames));
                continue;
            }

            details.Add(EntryName(entry) + " at " + PositionText(entry.Position)
                + " is waiting for Core placement prerequisites to clear");
        }
        if (details.Count == 0) return string.Empty;
        return string.Join("; ", details) + ".";
    }

    private string PlannedRoomNames(IEnumerable<int> roomIds)
    {
        string[] names = roomIds.Distinct().Select(roomId => rooms
                .FirstOrDefault(room => room.Id == roomId)?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Take(3).ToArray();
        return string.Join(", ", names);
    }

    private bool AdvanceActivePulse(out bool structuralPhaseCompleted,
        out bool broaderRelocationBlock)
    {
        structuralPhaseCompleted = false;
        broaderRelocationBlock = false;
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == activePulseRoomId);
        if (room == null)
        {
            ClearActivePulseSubmission(clearRoom: true);
            return false;
        }

        PruneTrackedPlanWork();
        Stopwatch structuralBudget = Stopwatch.StartNew();
        int structuralOrders = QueueActivePulseStructures(room,
            structuralBudget,
            out bool continueImmediately, out bool passExhausted,
            out List<Tile> blockers);
        if (continueImmediately) ScheduleStructuralBatchContinuation();
        if (structuralOrders > 0)
        {
            int queued = activePulseQueuedOrders;
            int total = activePulseQueueTotal;
            int remaining = ActivePulseQueueCount;
            if (remaining == 0 && IsRoomStructureFinished(room.Id))
            {
                ClearActivePulseSubmission(clearRoom: true);
                ClearStructuralConstructionStatus();
                continuousRoomId = room.Id;
                lastStatus = room.Name
                    + " completed its structural Pulse Wave. Advancing to its next construction phase.";
                structuralPhaseCompleted = true;
                return false;
            }
            if (remaining == 0) ScheduleStructuralBatchContinuation();
            bool batchDrained = activePulseYieldsWhenQueueDrains && remaining == 0;
            if (batchDrained)
            {
                ClearActivePulseSubmission(clearRoom: true);
                continuousRoomId = room.Id;
                ScheduleStructuralBatchContinuation();
            }
            lastStatus = batchDrained
                ? room.Name + " shell queued all " + queued
                    + " currently affordable structural orders in small batches. "
                    + "This room remains active while those jobs complete and open the next floor frontier."
                : "Pulse Wave queuing " + room.Name + " in small batches: "
                    + queued + " of " + total + " structural orders submitted; "
                    + remaining + " remain to inspect or submit.";
            SetStructuralConstructionStatus(room, lastStatus);
            return false;
        }

        if (!passExhausted && ActivePulseQueueCount > 0)
        {
            lastStatus = "Pulse Wave is inspecting " + room.Name
                + " in bounded batches: " + ActivePulseQueueCount
                + " structural entries remain.";
            SetStructuralConstructionStatus(room, lastStatus);
            return false;
        }

        if (Continuous && !activePulseIsManual && passExhausted
                && ActivePulseQueueCount > 0
                && activePulseMaterialLimitedEntries.Count > 0)
        {
            var waiting = new HashSet<PlanEntry>(activePulseMaterialLimitedEntries);
            if (materialWaits.TryGetValue(room.Id, out MaterialWaitState oldWait))
                waiting.UnionWith(oldWait.Entries);
            PlanEntry[] waitingEntries = waiting
                .OrderBy(entry => entry.Layer == WorldLayer.Floor ? 0 : 1)
                .ThenBy(entry => entry.Position)
                .ToArray();
            List<MaterialRequirement> requirements =
                BuildMaterialRequirements(waitingEntries);
            int queued = activePulseQueuedOrders;
            int deferred = ActivePulseQueueCount;
            bool signaled = SetRoomMaterialWait(room, "shell",
                waitingEntries.FirstOrDefault()?.Position ?? 0,
                requirements, waitingEntries,
                RoomHasTrackedPlanWork(room.Id));
            ClearActivePulseSubmission(clearRoom: true);
            continuousRoomId = 0;
            lastStatus = room.Name + " shell reached the live material limit after "
                + queued + " structural order" + (queued == 1 ? "" : "s")
                + " were submitted. " + deferred
                + " entries will be reconsidered after later rooms. Waiting for "
                + MaterialRequirementsSummary(requirements) + "."
                + AutoFactorySignalSuffix(signaled);
            return false;
        }

        if (activePulseYieldsWhenQueueDrains && ActivePulseQueueCount == 0)
        {
            int queued = activePulseQueuedOrders;
            ClearActivePulseSubmission(clearRoom: true);
            lastStatus = room.Name + " shell queued all " + queued
                + " currently affordable structural orders in small batches. "
                + "Native jobs already submitted for this room will continue in the background.";
            if (queued > 0) RecordAutomationProgress(room.Id);
            if (RoomHasTrackedPlanWork(room.Id))
                YieldRoomForActivePlanWork(room.Id, lastStatus);
            else
            {
                continuousRoomId = room.Id;
                ScheduleStructuralBatchContinuation();
            }
            return false;
        }

        if (ActivePulseQueueCount == 0 && RoomHasTrackedPlanWork(room.Id))
        {
            bool hasStructuralJob = planConstructionRooms.Any(work =>
                work.Value == room.Id
                && !relocationTargets.ContainsKey(work.Key)
                && CurrentStructuralConstructionJob(work.Key) != null);
            string heading = hasStructuralJob
                ? room.Name + " shell is under construction."
                : DescribePendingPlanWork(room.Id);
            if (Continuous && !activePulseIsManual)
            {
                heading += hasStructuralJob
                    ? " Viable native structural work is already in Core's task list."
                    : " A native plan task is already tracked for this room.";
                ClearActivePulseSubmission(clearRoom: true);
                YieldRoomForActivePlanWork(room.Id, heading);
            }
            if (hasStructuralJob)
                SetStructuralConstructionStatus(room, heading);
            else lastStatus = heading;
            return false;
        }

        if (blockers.Count > 0)
        {
            int ordered = 0;
            int moving = 0;
            int busy = 0;
            int stagingFull = 0;
            int poweredDestinationWaiting = 0;
            int deconstructed = 0;
            var protectedBlockers = new List<Tile>();
            int blockersExamined = 0;
            var reserved = new HashSet<int>();
            foreach (Tile blocker in blockers)
            {
                if (blockersExamined > 0
                        && structuralBudget.ElapsedMilliseconds
                            >= StructuralBatchBudgetMilliseconds)
                    break;
                blockersExamined++;
                ReinstallableComp relocation = blocker.Reinstallable;
                if (relocation?.IsPending == true)
                {
                    SetRelocationPriority(relocation, 5);
                    moving++;
                    continue;
                }
                bool canRelocate = CanEventuallyRelocate(blocker);
                if (canRelocate && TryFindRelocationDestination(blocker, room.Id,
                            reserved, out int destination, out PlanEntry directEntry,
                            directRoomId: activePulseIsManual ? room.Id : 0))
                {
                    if (directEntry == null)
                        AddRelocationReservation(reserved, blocker, destination, true);
                    else
                        reserved.UnionWith(EntryFootprint(directEntry));
                    if (!relocation.CanOrderRelocationNow)
                    {
                        busy++;
                        continue;
                    }
                    if (OrderRelocation(blocker, destination, room.Id,
                            directEntry == null, activePulseIsManual,
                            directEntry == null ? blocker.Transform.Rotation
                                : (Facing.Type)directEntry.Rotation))
                    {
                        if (directEntry != null)
                            planIndex.SetIssued(directEntry, true);
                        ordered++;
                    }
                    else busy++;
                    continue;
                }
                if (IsPlannerRemovalProtected(blocker))
                {
                    protectedBlockers.Add(blocker);
                    continue;
                }
                if (canRelocate && RequiresPoweredRelocationDestination(blocker))
                {
                    poweredDestinationWaiting++;
                    continue;
                }
                if (canRelocate
                        && StagingPositions(StagingTypeFor(blocker)).Count > 0)
                {
                    stagingFull++;
                    continue;
                }

                if (TryOrderPlannerDeconstruction(blocker, room.Id)) deconstructed++;
                else busy++;
            }
            bool blockerBudgetExhausted = blockersExamined < blockers.Count;
            bool relocationBlocked = !blockerBudgetExhausted
                && !activePulseIsManual && Continuous
                && structuralOrders == 0 && ordered == 0 && moving == 0
                && deconstructed == 0
                && (stagingFull > 0 || busy > 0
                    || poweredDestinationWaiting > 0);
            broaderRelocationBlock = relocationBlocked
                && (busy > 0 || poweredDestinationWaiting > 0
                    || protectedBlockers.Count > 0);
            if (((ordered > 0 || deconstructed > 0)
                        && ActivePulseQueueCount > 0)
                    || blockerBudgetExhausted)
            {
                activePulseDeferredWithoutProgress = 0;
                ScheduleStructuralBatchContinuation();
            }
            lastStatus = "Pulse Wave clearing " + room.Name + ": "
                + (ordered + moving) + " relocation jobs active"
                + (busy > 0 ? ", " + busy + " buildings finishing current work" : string.Empty)
                + (deconstructed > 0 ? ", " + deconstructed
                    + " native deconstruction jobs active" : string.Empty)
                + (stagingFull > 0 ? ", " + stagingFull
                    + " waiting because compatible staging is full" : string.Empty)
                + (poweredDestinationWaiting > 0 ? ", "
                    + poweredDestinationWaiting
                    + " configured production buildings waiting for a valid powered relocation destination"
                    : string.Empty)
                + (structuralOrders > 0 ? ", " + structuralOrders
                    + " clear structural orders queued" : string.Empty)
                + (protectedBlockers.Count > 0 ? ". " + SummarizeDetails(
                    protectedBlockers.Select(ProtectedBuildingWaitDescription)) : string.Empty)
                + ". Blockers: " + SummarizeTiles(blockers)
                + ". Relocation priority is 5.";
            bool hasActivePlannerWork = RoomHasTrackedPlanWork(room.Id)
                && (ordered > 0 || moving > 0 || deconstructed > 0);
            if (!activePulseIsManual && Continuous && hasActivePlannerWork)
            {
                if (ordered > 0 || deconstructed > 0)
                    RecordAutomationProgress(room.Id);
                ClearActivePulseSubmission(clearRoom: true);
                YieldRoomForActivePlanWork(room.Id, lastStatus);
                return false;
            }
            if (relocationBlocked)
            {
                ClearActivePulseSubmission(clearRoom: true);
            }
            else if (!blockerBudgetExhausted && !activePulseIsManual && Continuous
                    && protectedBlockers.Count > 0 && structuralOrders == 0
                    && ordered == 0 && moving == 0 && busy == 0 && deconstructed == 0)
            {
                ClearActivePulseSubmission(clearRoom: true);
                YieldRoomForProtectedBuildings(room.Id, SummarizeDetails(
                    protectedBlockers.Select(ProtectedBuildingWaitDescription)));
            }
            return relocationBlocked;
        }

        if (Continuous && !activePulseIsManual
                && ActivePulseQueueCount > 0)
        {
            int queued = activePulseQueuedOrders;
            int deferred = ActivePulseQueueCount;
            bool hasTrackedPlanWork = RoomHasTrackedPlanWork(room.Id);
            bool hasViableStructuralWork = RoomHasActiveStructuralJob(room.Id);
            bool waitingForFloorFrontier = activePulsePendingEntries.Any(entry =>
                entry.Layer == WorldLayer.Floor
                && !HasConstructedFloorNeighbor(entry));
            PlanEntry[] deferredEntries = activePulsePendingEntries
                .Take(4).ToArray();
            ClearActivePulseSubmission(clearRoom: true);
            continuousRoomId = 0;
            if (!hasViableStructuralWork && waitingForFloorFrontier)
                MarkRoomFloorFrontierBlocked(room.Id);
            lastStatus = room.Name + " shell queued " + queued
                + " currently affordable structural orders. " + deferred
                + " entries still need a prerequisite and will be reconsidered "
                + "after later structural rooms. "
                + (hasTrackedPlanWork
                    ? "Native jobs already submitted for this room will continue in the background. "
                    : string.Empty)
                + DescribeStructuralDependencies(deferredEntries);
            return false;
        }

        if (ActivePulseQueueCount > 0)
            lastStatus = room.Name + " has " + ActivePulseQueueCount
                + " structural entries that cannot be submitted yet and will retry.";
        else
        {
            // Submitted ghosts can later be cancelled or removed. An empty
            // submission queue does not establish that its room is finished.
            PulsePreview remaining = BuildPulsePreview(room.Id, assessRelocations: false);
            if (remaining.InProgress > 0)
            {
                int restoredGoals = RestoreMissingStructuralGoals(
                    remaining.InProgressEntries);
                string heading = room.Name + " has " + remaining.InProgress
                    + " structural construction ghost"
                    + (remaining.InProgress == 1 ? string.Empty : "s")
                    + " pending. "
                    + (restoredGoals > 0
                        ? "Restored " + restoredGoals
                            + " missing native construction job"
                            + (restoredGoals == 1 ? string.Empty : "s") + ". "
                        : string.Empty);
                if (Continuous && !activePulseIsManual)
                {
                    bool hasViableNativeWork = restoredGoals > 0
                        || RoomHasActiveStructuralJob(room.Id);
                    ClearActivePulseSubmission(clearRoom: true);
                    if (hasViableNativeWork)
                    {
                        continuousRoomId = room.Id;
                        heading += "Remaining with this room while Core completes this work and opens the next floor frontier.";
                        ScheduleStructuralBatchContinuation();
                    }
                    else
                    {
                        continuousRoomId = 0;
                        heading += "No viable native structural task remains, so the next room will be checked.";
                    }
                }
                SetStructuralConstructionStatus(room,
                    heading,
                    remaining.InProgressEntries);
            }
            else if (remaining.Pending.Count > 0)
            {
                if (activePulseIsManual)
                    BeginActivePulseSubmission(remaining.Pending, null, false);
                else
                {
                    ClearActivePulseSubmission(clearRoom: true);
                    continuousRoomId = 0;
                }
                lastStatus = room.Name + " has no active structural construction jobs. "
                    + "Rechecking " + remaining.Pending.Count + " unfinished entries: "
                    + SummarizePlanEntries(remaining.Pending) + ".";
                ScheduleStructuralBatchContinuation();
            }
            else
            {
                ClearActivePulseSubmission(clearRoom: true);
                ClearStructuralConstructionStatus();
                continuousRoomId = room.Id;
                lastStatus = room.Name
                    + " completed its structural Pulse Wave. Advancing to its next construction phase.";
                structuralPhaseCompleted = true;
            }
        }
        return false;
    }

    private bool OrderRelocation(Tile source, int destination, int roomId,
        bool toStaging, bool manualAuthorized, Facing.Type targetRotation)
    {
        ReinstallableComp relocation = source.Reinstallable;
        if (relocation == null) return false;
        if (!CanStartPlannedRelocation(toStaging)) return false;
        if (!CanPlaceRelocation(source, destination, targetRotation,
                new HashSet<int>())) return false;
        if (!TryPrepareRelocationPower(source, destination,
                out int powerConnectorPosition)) return false;
        if (toStaging)
        {
            StagingAreaType type = StagingTypeAtDestination(source, destination);
            HashSet<int> reserved = ReservedStagingPositions();
            int[] footprint = TileFootprint(source, destination);
            if (footprint.Any(position => !StagingPositions(type).Contains(position)
                    || !IsStagingFootprintCellReady(type, position)
                    || reserved.Contains(position)))
                return false;
            if (type == StagingAreaType.Indoor
                    && !IsIndoorStagingClearanceReady(source, destination))
                return false;
        }
        var work = new RelocationWorkState
        {
            RoomId = roomId,
            ManualAuthorized = manualAuthorized
        };
        NativeRelocationActive = true;
        try
        {
            bool accepted = TryOrderBackgroundRelocation(source, destination,
                targetRotation, powerConnectorPosition, relocated =>
                {
                    if (toStaging && relocated?.IsActive == true)
                        stagedBuildings[relocated.Id] = work;
                }, out ReinstallableComp target);
            if (!accepted) return false;
            // Sandbox instant relocation has no pending target or hauling goal.
            if (target == null) return true;
            relocationTargets[target.EntityId] = work;
            if (toStaging) stagedBuildings[target.EntityId] = work;
            SetRelocationPriority(relocation, 5);
            return true;
        }
        finally
        {
            NativeRelocationActive = false;
        }
    }

    private static void SetRelocationPriority(ReinstallableComp relocation, int priority)
    {
        if (relocation?.Target?.Entity?.Goal != null)
            relocation.Target.Entity.Goal.SetPriority(G.Ticks, priority);
    }

    private void BeginActivePulseSubmission(IEnumerable<PlanEntry> candidates,
        HashSet<PlanEntry> allowedEntries, bool yieldsWhenQueueDrains)
    {
        activePulseQueue.Clear();
        activePulseQueueHead = 0;
        activePulsePendingEntries.Clear();
        activePulseMainEntries.Clear();
        activePulseReadyFloors.Clear();
        activePulseReadyFloorEntries.Clear();
        activePulseReadyFloorHead = 0;
        activePulseAllowedEntries = allowedEntries;
        activePulseMaterialLimitedEntries.Clear();
        activePulseExistingMaterialCommitments.Clear();
        activePulseMaterialCaps.Clear();
        activePulseCommittedCosts.Clear();
        ClearActivePulseWallSources();
        if (Continuous && !activePulseIsManual)
            AddActivePlanConstructionCommitments(
                activePulseExistingMaterialCommitments);
        foreach (PlanEntry entry in candidates
                     .OrderBy(item => item.Layer == WorldLayer.Floor ? 0 : 1)
                     .ThenBy(item => item.Position))
        {
            activePulseQueue.Add(entry);
            activePulsePendingEntries.Add(entry);
            activePulseMainEntries.Add(entry);
            if (entry.Layer == WorldLayer.Walls)
                activePulseWallDefinitions.Add(entry.DefId);
            if (entry.Layer == WorldLayer.Floor
                    && HasConstructedFloorNeighbor(entry))
                PromoteReadyFloorEntry(entry);
        }
        activePulseQueueInitialized = true;
        activePulseYieldsWhenQueueDrains = yieldsWhenQueueDrains;
        activePulseQueueTotal = ActivePulseQueueCount;
        activePulseQueuedOrders = 0;
        activePulseDeferredWithoutProgress = 0;
    }

    private void ClearActivePulseSubmission(bool clearRoom)
    {
        activePulseQueue.Clear();
        activePulseQueueHead = 0;
        activePulsePendingEntries.Clear();
        activePulseMainEntries.Clear();
        activePulseReadyFloors.Clear();
        activePulseReadyFloorEntries.Clear();
        activePulseReadyFloorHead = 0;
        activePulseAllowedEntries = null;
        activePulseMaterialLimitedEntries.Clear();
        activePulseExistingMaterialCommitments.Clear();
        activePulseMaterialCaps.Clear();
        activePulseCommittedCosts.Clear();
        ClearActivePulseWallSources();
        activePulseQueueInitialized = false;
        activePulseYieldsWhenQueueDrains = false;
        activePulseQueueTotal = 0;
        activePulseQueuedOrders = 0;
        activePulseDeferredWithoutProgress = 0;
        if (!clearRoom) return;
        activePulseRoomId = 0;
        activePulseIsManual = false;
        manualPulseRoomId = 0;
    }

    private void CompactActivePulseQueue()
    {
        if (activePulseQueueHead <= 0) return;
        if (activePulseQueueHead >= activePulseQueue.Count)
        {
            activePulseQueue.Clear();
            activePulseQueueHead = 0;
            return;
        }
        if (activePulseQueueHead < 1024
                || activePulseQueueHead * 2 < activePulseQueue.Count) return;
        activePulseQueue.RemoveRange(0, activePulseQueueHead);
        activePulseQueueHead = 0;
    }

    private void CompactActivePulseReadyFloors()
    {
        if (activePulseReadyFloorHead <= 0) return;
        if (activePulseReadyFloorHead >= activePulseReadyFloors.Count)
        {
            activePulseReadyFloors.Clear();
            activePulseReadyFloorHead = 0;
            return;
        }
        if (activePulseReadyFloorHead < 256
                || activePulseReadyFloorHead * 2 < activePulseReadyFloors.Count)
            return;
        activePulseReadyFloors.RemoveRange(0, activePulseReadyFloorHead);
        activePulseReadyFloorHead = 0;
    }

    private void PromoteReadyFloorEntry(PlanEntry entry)
    {
        if (entry == null || !activePulsePendingEntries.Contains(entry)
                || !activePulseReadyFloorEntries.Add(entry)) return;
        activePulseReadyFloors.Add(entry);
    }

    private PlanEntry TakeNextActivePulseEntry()
    {
        while (activePulseReadyFloorHead < activePulseReadyFloors.Count)
        {
            PlanEntry entry = activePulseReadyFloors[activePulseReadyFloorHead++];
            activePulseReadyFloorEntries.Remove(entry);
            if (activePulsePendingEntries.Contains(entry)) return entry;
        }
        while (activePulseQueueHead < activePulseQueue.Count)
        {
            PlanEntry entry = activePulseQueue[activePulseQueueHead++];
            activePulseMainEntries.Remove(entry);
            if (activePulsePendingEntries.Contains(entry)) return entry;
        }
        return null;
    }

    private void RequeueActivePulseEntry(PlanEntry entry)
    {
        if (entry == null || !activePulsePendingEntries.Contains(entry)
                || !activePulseMainEntries.Add(entry)) return;
        activePulseQueue.Add(entry);
    }

    private void CompleteActivePulseEntry(PlanEntry entry)
    {
        if (entry == null) return;
        activePulsePendingEntries.Remove(entry);
        activePulseReadyFloorEntries.Remove(entry);
    }

    private void PromoteReadyFloorNeighbors(int position, int roomId)
    {
        if (!activePulseQueueInitialized || activePulseRoomId != roomId) return;
        int[] neighbors = { Pos.N(position), Pos.E(position),
            Pos.S(position), Pos.W(position) };
        foreach (int neighbor in neighbors)
        {
            if (!planEntriesByFootprint.TryGetValue(neighbor,
                    out List<PlanEntry> planned)) continue;
            foreach (PlanEntry entry in planned)
            {
                if (entry.RoomId != roomId || entry.Layer != WorldLayer.Floor
                        || !HasConstructedFloorNeighbor(entry)) continue;
                PromoteReadyFloorEntry(entry);
            }
        }
    }

    private void ScheduleStructuralBatchContinuation()
    {
        long next = G.Ticks + 1;
        if (nextAutomationTick > next) nextAutomationTick = next;
    }

    private int QueueActivePulseStructures(PlanRoom room,
        Stopwatch structuralBudget,
        out bool continueImmediately, out bool passExhausted,
        out List<Tile> blockers)
    {
        if (!activePulseQueueInitialized)
        {
            BeginActivePulseSubmission(entries
                    .Where(item => item.RoomId == room.Id
                        && IsRoomStructuralEntry(item))
                    .Where(item => activePulseAllowedEntries == null
                        || activePulseAllowedEntries.Contains(item)),
                activePulseAllowedEntries, activePulseYieldsWhenQueueDrains);
        }

        int placed = 0;
        int examined = 0;
        bool madeProgress = false;
        passExhausted = false;
        blockers = new List<Tile>();
        // Index eligible walls incrementally inside the existing batch budget.
        // The resulting IDs are reused throughout this wave and revalidated.
        if (!IndexActivePulseWallSources(structuralBudget))
        {
            continueImmediately = true;
            return 0;
        }
        var blockerIds = new HashSet<int>();
        var entryCosts = new Dictionary<MatType, int>();
        while (ActivePulseQueueCount > 0
                && placed < StructuralOrdersPerTick
                && examined < StructuralCandidatesPerTick
                && (examined == 0
                    || structuralBudget.ElapsedMilliseconds
                        < StructuralBatchBudgetMilliseconds))
        {
            PlanEntry entry = TakeNextActivePulseEntry();
            examined++;
            activePulseMaterialLimitedEntries.Remove(entry);
            if (entry == null || entry.RoomId != room.Id
                    || !IsRoomStructuralEntry(entry)
                    || (activePulseAllowedEntries != null
                        && !activePulseAllowedEntries.Contains(entry)))
            {
                CompleteActivePulseEntry(entry);
                activePulseDeferredWithoutProgress = 0;
                madeProgress = true;
                continue;
            }

            if (!HasConstructedFloorNeighbor(entry))
            {
                RequeueActivePulseEntry(entry);
                activePulseDeferredWithoutProgress++;
                if (activePulseDeferredWithoutProgress >= ActivePulseQueueCount)
                {
                    passExhausted = true;
                    activePulseDeferredWithoutProgress = 0;
                    break;
                }
                continue;
            }

            Tile existing = ExistingAt(entry);
            if (existing != null && string.Equals(existing.DefinitionId,
                    entry.DefId, StringComparison.Ordinal))
            {
                if (!existing.IsConstructed)
                {
                    planIndex.SetIssued(entry, true);
                    if (!relocationTargets.ContainsKey(existing.Id))
                        EnsureNativeStructuralGoal(entry, existing, out _);
                }
                CompleteActivePulseEntry(entry);
                activePulseDeferredWithoutProgress = 0;
                madeProgress = true;
                continue;
            }
            if (entry.IssuedByPlan
                    && existing?.PendingReplacement?.IsPending == true
                    && string.Equals(existing.PendingReplacement.TargetDef?.Id,
                        entry.DefId, StringComparison.Ordinal))
            {
                ApplyIssuedGoalPriority(entry, existing, G.Ticks);
                CompleteActivePulseEntry(entry);
                activePulseDeferredWithoutProgress = 0;
                madeProgress = true;
                continue;
            }

            if (entry.Layer == WorldLayer.Walls
                    && !HasConstructedWallSupport(entry))
            {
                RequeueActivePulseEntry(entry);
                activePulseDeferredWithoutProgress++;
                if (activePulseDeferredWithoutProgress >= ActivePulseQueueCount)
                {
                    passExhausted = true;
                    activePulseDeferredWithoutProgress = 0;
                    break;
                }
                continue;
            }

            if (TryReuseStructuralWall(entry, out bool movedWall,
                    out bool destinationSatisfied))
            {
                if (movedWall)
                {
                    placed++;
                    activePulseQueuedOrders++;
                    activePulseDeferredWithoutProgress = 0;
                    madeProgress = true;
                }
                else activePulseDeferredWithoutProgress++;
                if (destinationSatisfied) CompleteActivePulseEntry(entry);
                else RequeueActivePulseEntry(entry);
                if (activePulseDeferredWithoutProgress >= ActivePulseQueueCount
                        && ActivePulseQueueCount > 0)
                {
                    passExhausted = true;
                    activePulseDeferredWithoutProgress = 0;
                    break;
                }
                continue;
            }

            if (!CanPlaceStructuralNow(entry))
            {
                foreach (Tile blocker in BlockersFor(entry))
                {
                    if (blockers.Count >= StructuralOrdersPerTick) break;
                    if (blocker?.IsConstructed == true
                            && blockerIds.Add(blocker.Id))
                        blockers.Add(blocker);
                }
                RequeueActivePulseEntry(entry);
                activePulseDeferredWithoutProgress++;
                if (activePulseDeferredWithoutProgress >= ActivePulseQueueCount)
                {
                    passExhausted = true;
                    activePulseDeferredWithoutProgress = 0;
                    break;
                }
                continue;
            }

            Def def = The.Defs.TryGet(entry.DefId);
            Tile tile = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
            if (tile == null)
            {
                RequeueActivePulseEntry(entry);
                activePulseDeferredWithoutProgress++;
                if (activePulseDeferredWithoutProgress >= ActivePulseQueueCount)
                {
                    passExhausted = true;
                    activePulseDeferredWithoutProgress = 0;
                    break;
                }
                continue;
            }
            tile.Transform.Rotation = (Facing.Type)entry.Rotation;

            if (Continuous && !activePulseIsManual)
            {
                entryCosts.Clear();
                AddConstructionCost(entry, entryCosts);
                bool affordableNow = true;
                foreach (KeyValuePair<MatType, int> item in entryCosts)
                {
                    int committed = activePulseCommittedCosts.TryGetValue(item.Key,
                        out int old) ? old : 0;
                    int existingCommitment = activePulseExistingMaterialCommitments.TryGetValue(
                        item.Key, out int outstanding) ? outstanding : 0;
                    int liveUncommitted = Math.Max(0,
                        S.Sys.Inventory.AvailableCount(item.Key) - existingCommitment);
                    if (!activePulseMaterialCaps.TryGetValue(item.Key, out int cap))
                    {
                        cap = liveUncommitted;
                    }
                    else
                    {
                        cap = Math.Min(cap, committed + liveUncommitted);
                    }
                    activePulseMaterialCaps[item.Key] = cap;
                    int proposed = committed + item.Value;
                    if (cap >= proposed + ((proposed + 9) / 10)) continue;
                    affordableNow = false;
                    break;
                }
                if (!affordableNow)
                {
                    activePulseMaterialLimitedEntries.Add(entry);
                    RequeueActivePulseEntry(entry);
                    activePulseDeferredWithoutProgress++;
                    if (activePulseDeferredWithoutProgress >= ActivePulseQueueCount)
                    {
                        passExhausted = true;
                        activePulseDeferredWithoutProgress = 0;
                        break;
                    }
                    continue;
                }
            }

            var command = new CmdPlaceTile(entry.Position, tile, warn: true,
                instant: A.InstantBuild);
            command.Execute(S);
            if (!command.IsSuccess)
            {
                foreach (Tile blocker in BlockersFor(entry))
                {
                    if (blockers.Count >= StructuralOrdersPerTick) break;
                    if (blocker?.IsConstructed == true
                            && blockerIds.Add(blocker.Id))
                        blockers.Add(blocker);
                }
                RequeueActivePulseEntry(entry);
                activePulseDeferredWithoutProgress++;
                if (activePulseDeferredWithoutProgress >= ActivePulseQueueCount)
                {
                    passExhausted = true;
                    activePulseDeferredWithoutProgress = 0;
                    break;
                }
                continue;
            }

            if (Continuous && !activePulseIsManual)
                foreach (KeyValuePair<MatType, int> item in entryCosts)
                    activePulseCommittedCosts[item.Key] = activePulseCommittedCosts.TryGetValue(
                        item.Key, out int old) ? old + item.Value : item.Value;
            planIndex.SetIssued(entry, true);
            Tile placedOrReplacement = command.PlacedTile ?? ExistingAt(entry);
            if (placedOrReplacement != null)
            {
                ApplyIssuedGoalPriority(entry, placedOrReplacement, G.Ticks);
                if (placedOrReplacement.Goal != null)
                    planConstructionRooms[placedOrReplacement.Id] = entry.RoomId;
            }
            placed++;
            activePulseQueuedOrders++;
            CompleteActivePulseEntry(entry);
            activePulseDeferredWithoutProgress = 0;
            madeProgress = true;
        }

        CompactActivePulseQueue();
        CompactActivePulseReadyFloors();
        bool budgetExhausted = ActivePulseQueueCount > 0 && !passExhausted
            && structuralBudget.ElapsedMilliseconds
                >= StructuralBatchBudgetMilliseconds;
        continueImmediately = ActivePulseQueueCount > 0 && !passExhausted
            && (placed > 0 || madeProgress
                || examined >= StructuralCandidatesPerTick
                || budgetExhausted);
        return placed;
    }

    private void ClearActivePulseWallSources()
    {
        activePulseWallSources.Clear();
        activePulseWallDefinitions.Clear();
        activePulseWallScanPosition = 0;
        activePulseWallsIndexed = false;
    }

    private bool IndexActivePulseWallSources(Stopwatch budget)
    {
        if (activePulseWallsIndexed) return true;
        if (activePulseWallDefinitions.Count == 0)
            return activePulseWallsIndexed = true;
        Tile[] walls = S.Map.Grids[WorldLayer.Walls].Data;
        int examined = 0;
        while (activePulseWallScanPosition < walls.Length
                && (examined == 0 || budget.ElapsedMilliseconds
                    < StructuralBatchBudgetMilliseconds))
        {
            Tile raw = walls[activePulseWallScanPosition++];
            examined++;
            Tile tile = EntityUtils.ParentObj(raw);
            if (tile == null || tile.PosIdx != activePulseWallScanPosition - 1
                    || !activePulseWallDefinitions.Contains(tile.DefinitionId)
                    || !CanEventuallyRelocate(tile) || MatchesPlannedTile(tile)) continue;
            if (!activePulseWallSources.TryGetValue(tile.DefinitionId, out List<int> sources))
                activePulseWallSources[tile.DefinitionId] = sources = new List<int>();
            sources.Add(tile.Id);
        }
        return activePulseWallsIndexed = activePulseWallScanPosition >= walls.Length;
    }

    private bool TryReuseStructuralWall(PlanEntry entry, out bool moved,
        out bool destinationSatisfied)
    {
        moved = false;
        destinationSatisfied = false;
        if (entry.Layer != WorldLayer.Walls) return false;
        Tile existing = ExistingAt(entry);
        var reserved = new HashSet<int>();
        // Move a displaced wall to another needed location before replacing
        // its old cell with the wall or door required there.
        if (existing?.IsConstructed == true && !MatchesPlannedTile(existing)
                && CanEventuallyRelocate(existing)
                && TryFindDirectRelocationDestination(existing, entry.RoomId, reserved,
                    out int destination, out PlanEntry needed,
                    directRoomId: activePulseIsManual ? entry.RoomId : 0))
        {
            if (existing.Reinstallable.CanOrderRelocationNow
                    && OrderRelocation(existing, destination, entry.RoomId, false,
                        activePulseIsManual, (Facing.Type)needed.Rotation))
            {
                planIndex.SetIssued(needed, true);
                moved = true;
            }
            return true;
        }
        if (existing != null || EntryFootprint(entry).Any(position =>
                EntityUtils.ParentObjAt(position) != null)) return false;
        if (!activePulseWallSources.TryGetValue(entry.DefId, out List<int> sources))
            return false;
        Tile source = null;
        int distance = int.MaxValue;
        foreach (int entityId in sources)
        {
            Tile candidate = S.FindEntity<Tile>(entityId);
            if (candidate?.IsActive != true || !CanEventuallyRelocate(candidate)
                    || candidate.Reinstallable.IsPendingQuick
                    || candidate.PendingReplacement?.IsPending == true
                    || candidate.Goal?.JobIdH == JobTypeIdH.Demolition
                    || MatchesPlannedTile(candidate)
                    || !string.Equals(candidate.DefinitionId, entry.DefId, StringComparison.Ordinal))
                continue;
            int candidateDistance = Pos.SqrDistance(candidate.PosIdx, entry.Position);
            if (source != null
                    && (source.Reinstallable.CanOrderRelocationNow
                            && !candidate.Reinstallable.CanOrderRelocationNow
                        || source.Reinstallable.CanOrderRelocationNow
                            == candidate.Reinstallable.CanOrderRelocationNow
                            && candidateDistance >= distance)) continue;
            if (!CanPlaceRelocation(candidate, entry.Position,
                    (Facing.Type)entry.Rotation, reserved)) continue;
            source = candidate;
            distance = candidateDistance;
        }
        if (source == null) return false;
        if (source.Reinstallable.CanOrderRelocationNow
                && OrderRelocation(source, entry.Position, entry.RoomId, false,
                    activePulseIsManual, (Facing.Type)entry.Rotation))
        {
            planIndex.SetIssued(entry, true);
            moved = true;
            destinationSatisfied = true;
        }
        return true;
    }

    private bool HasUnfilledPlannedWallDestination(Tile source)
    {
        return source?.Definition?.LayerId == WorldLayer.Walls
            && planIndex.EntriesForDefinition(source.DefinitionId)
                .Any(entry => entry.Layer == WorldLayer.Walls
                && string.Equals(entry.DefId, source.DefinitionId, StringComparison.Ordinal)
                && entry.Position != source.PosIdx && !IsEquipmentEntrySatisfied(entry));
    }

    private void AddActivePlanConstructionCommitments(
        Dictionary<MatType, int> costs)
    {
        foreach (int tileId in planConstructionRooms.Keys)
        {
            if (relocationTargets.ContainsKey(tileId)) continue;
            Tile tile = S.FindEntity<Tile>(tileId);
            if (tile?.IsActive != true) continue;

            PendingReplacementComp replacement = tile.PendingReplacement;
            if (replacement?.IsPending == true)
            {
                AddMissingMaterialRequests(replacement.MissingMats, costs);
                continue;
            }
            if (tile.IsConstructed) continue;
            Mat[] rawMaterials = tile.Constructable?.RawMaterials;
            if (rawMaterials == null) continue;
            foreach (Mat material in rawMaterials)
            {
                if (material.Type == null || material.Diff <= 0) continue;
                costs[material.Type] = costs.TryGetValue(material.Type,
                    out int old) ? old + material.Diff : material.Diff;
            }
        }
    }

    private static void AddMissingMaterialRequests(
        IEnumerable<MatRequest> requests, Dictionary<MatType, int> costs)
    {
        if (requests == null) return;
        foreach (MatRequest request in requests)
        {
            if (request?.Type == null || request.IsCancelled || request.Amount <= 0)
                continue;
            costs[request.Type] = costs.TryGetValue(request.Type,
                out int old) ? old + request.Amount : request.Amount;
        }
    }

    private void UpdateActivePulseState()
    {
        if (activePulseQueueInitialized && ActivePulseQueueCount > 0) return;
        if (activePulseRoomId == 0 || !IsRoomStructureFinished(activePulseRoomId)) return;
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == activePulseRoomId);
        ClearActivePulseSubmission(clearRoom: true);
        if (structuralStatusRoom?.Id == room?.Id)
            ClearStructuralConstructionStatus();
        lastStatus = (room?.Name ?? "The room")
            + " completed its structural Pulse Wave. Advancing to its next construction phase.";
        ui?.Refresh();
    }

    private void PruneTrackedPlanWork()
    {
        foreach (int targetId in relocationTargets.Keys.ToArray())
        {
            ReinstallableComp target = S.Components.Find<ReinstallableComp>(targetId);
            if (target?.Entity?.IsActive == true && target.IsPendingQuick) continue;
            relocationTargets.Remove(targetId);
            if (S.FindEntity<Tile>(targetId)?.IsActive != true)
                stagedBuildings.Remove(targetId);
        }

        foreach (int entityId in stagedBuildings.Keys.ToArray())
        {
            Tile staged = S.FindEntity<Tile>(entityId);
            ReinstallableComp pending = S.Components.Find<ReinstallableComp>(entityId);
            if (staged?.IsActive == true
                    || pending?.Entity?.IsActive == true && pending.IsPendingQuick) continue;
            stagedBuildings.Remove(entityId);
        }

        foreach (int tileId in planDeconstructionRooms.Keys.ToArray())
        {
            Tile tile = S.FindEntity<Tile>(tileId);
            if (tile?.IsActive == true && tile.Goal?.JobIdH == 1411359855) continue;
            planDeconstructionRooms.Remove(tileId);
        }
        foreach (int tileId in planConstructionRooms.Keys.ToArray())
        {
            Tile tile = S.FindEntity<Tile>(tileId);
            bool activeConstruction = tile?.IsActive == true && tile.Goal != null
                && (!tile.IsConstructed || tile.PendingReplacement?.IsPending == true);
            if (activeConstruction) continue;
            if (tile?.IsConstructed == true
                    && tile.Definition?.LayerId == WorldLayer.Floor)
                PromoteReadyFloorNeighbors(tile.PosIdx,
                    planConstructionRooms[tileId]);
            planConstructionRooms.Remove(tileId);
        }
    }

    private void SetStructuralConstructionStatus(PlanRoom room, string heading,
        IEnumerable<PlanEntry> knownInProgress = null)
    {
        if (structuralStatusRoom?.Id != room.Id) structuralStatusExamples.Clear();
        structuralStatusRoom = room;
        structuralStatusHeading = heading;
        structuralStatusPublishedMessage = heading;
        structuralStatusNoJobDetails = DescribeStructuralDependencies(
            knownInProgress);
        lastStatus = heading;
        structuralStatusCandidates.Clear();
        structuralStatusCandidateIndex = 0;
        // Snapshot only tracked job IDs when the scheduler visits this phase.
        // Per-tick sampling never traverses the full blueprint.
        foreach (var work in planConstructionRooms)
            if (work.Value == room.Id && !relocationTargets.ContainsKey(work.Key))
                structuralStatusCandidates.Add(work.Key);
        // The existing preview can also see player-created construction ghosts.
        // Reuse a few of those entries without performing another plan scan.
        if (knownInProgress != null)
            foreach (PlanEntry entry in knownInProgress.Take(4))
            {
                Tile tile = ExistingAt(entry);
                if (tile != null && !structuralStatusCandidates.Contains(tile.Id))
                    structuralStatusCandidates.Add(tile.Id);
            }
        UpdateStructuralConstructionStatus(allowRoomEvaluation: true);
    }

    private void UpdateStructuralConstructionStatus(bool allowRoomEvaluation)
    {
        Stopwatch perf = Stopwatch.StartNew();
        try
        {
        if (structuralStatusRoom == null) return;
        if ((!Continuous && !activePulseIsManual)
                || !string.Equals(lastStatus, structuralStatusPublishedMessage,
                    StringComparison.Ordinal))
        {
            ClearStructuralConstructionStatus();
            ui?.RefreshStatus();
            return;
        }

        long started = Stopwatch.GetTimestamp();
        long budget = Math.Max(1L, Stopwatch.Frequency / 1000);
        int examined = 0;
        structuralStatusCurrentTiles.Clear();
        for (int i = 0; i < structuralStatusExamples.Count; i++)
        {
            examined++;
            Tile tile = CurrentStructuralConstructionJob(structuralStatusExamples[i]);
            if (tile == null) structuralStatusExamples.RemoveAt(i--);
            else structuralStatusCurrentTiles.Add(tile);
        }
        while (structuralStatusExamples.Count < 4
                && structuralStatusCandidateIndex < structuralStatusCandidates.Count
                && examined < StructuralCandidatesPerTick
                && Stopwatch.GetTimestamp() - started < budget)
        {
            int entityId = structuralStatusCandidates[structuralStatusCandidateIndex++];
            examined++;
            if (structuralStatusExamples.Contains(entityId)) continue;
            Tile tile = CurrentStructuralConstructionJob(entityId);
            if (tile == null) continue;
            structuralStatusExamples.Add(entityId);
            structuralStatusCurrentTiles.Add(tile);
        }

        var details = new List<string>(5) { structuralStatusHeading };
        foreach (Tile tile in structuralStatusCurrentTiles)
        {
            string kind = tile.PendingReplacement?.IsPending == true
                ? "Replacement" : "Construction";
            details.Add(kind + ": " + tile.Definition.NameT + " at "
                + PositionText(tile.PosIdx) + " · " + DescribePendingPlanGoal(tile.Goal));
        }
        if (structuralStatusCurrentTiles.Count == 0)
        {
            if (allowRoomEvaluation
                    && structuralStatusCandidateIndex >= structuralStatusCandidates.Count
                    && IsRoomStructureFinished(structuralStatusRoom.Id))
            {
                string roomName = structuralStatusRoom.Name;
                ClearStructuralConstructionStatus();
                ScheduleStructuralBatchContinuation();
                lastStatus = roomName
                    + " structure is complete. Advancing to its next construction phase.";
                ui?.RefreshStatus();
                return;
            }
            if (allowRoomEvaluation
                    && structuralStatusCandidateIndex >= structuralStatusCandidates.Count
                    && string.IsNullOrWhiteSpace(structuralStatusNoJobDetails))
            {
                ScheduleStructuralBatchContinuation();
                PulsePreview remaining = BuildPulsePreview(
                    structuralStatusRoom.Id, assessRelocations: false);
                if (remaining.InProgress > 0)
                {
                    structuralStatusNoJobDetails = structuralStatusRoom.Name
                        + " still has " + remaining.InProgress
                        + " structural construction ghost"
                        + (remaining.InProgress == 1 ? string.Empty : "s")
                        + ", but none currently has an active Core construction job. "
                        + "The next pass will restore or resubmit: "
                        + SummarizePlanEntries(remaining.InProgressEntries.Take(4)) + ".";
                }
                else if (remaining.Pending.Count > 0)
                {
                    structuralStatusNoJobDetails = structuralStatusRoom.Name
                        + " still has " + remaining.Pending.Count
                        + " unfinished structural entr"
                        + (remaining.Pending.Count == 1 ? "y" : "ies") + ". "
                        + DescribeStructuralDependencies(remaining.Pending.Take(4));
                }
            }
            string noJobStatus = structuralStatusCandidateIndex < structuralStatusCandidates.Count
                ? "Checking current structural construction jobs for " + structuralStatusRoom.Name + "."
                : !string.IsNullOrWhiteSpace(structuralStatusNoJobDetails)
                    ? structuralStatusNoJobDetails
                    : structuralStatusRoom.Name + " has no active structural construction job to list. "
                        + "The structural phase will be rechecked.";
            if (structuralStatusHeading.Contains("under construction")) details[0] = noJobStatus;
            else details.Add(noJobStatus);
        }
        structuralStatusPublishedMessage = string.Join("\n", details);
        lastStatus = structuralStatusPublishedMessage;
        ui?.RefreshStatus();
        }
        finally
        {
            ReportSlowOperation("StructuralStatus", perf.ElapsedMilliseconds,
                "allowRoomEvaluation=" + allowRoomEvaluation
                    + "; candidates=" + structuralStatusCandidates.Count
                    + "; examples=" + structuralStatusExamples.Count,
                ref nextStructuralStatusPerfLogTime);
        }
    }

    private Tile CurrentStructuralConstructionJob(int entityId)
    {
        if (relocationTargets.ContainsKey(entityId)) return null;
        Tile tile = S.FindEntity<Tile>(entityId);
        if (tile?.IsActive != true || tile.Goal == null || tile.Definition == null
                || AIGoal.IsArchived(tile.Goal.State))
            return null;
        int layer = tile.Definition.LayerId;
        if (layer != WorldLayer.Floor && layer != WorldLayer.Walls) return null;
        return !tile.IsConstructed || tile.PendingReplacement?.IsPending == true
            ? tile : null;
    }

    private void ClearStructuralConstructionStatus()
    {
        structuralStatusRoom = null;
        structuralStatusHeading = null;
        structuralStatusPublishedMessage = null;
        structuralStatusNoJobDetails = null;
        structuralStatusCandidates.Clear();
        structuralStatusExamples.Clear();
        structuralStatusCurrentTiles.Clear();
        structuralStatusCandidateIndex = 0;
    }

    private IEnumerable<(string Kind, int EntityId, int RoomId)> PendingPlanWork()
    {
        foreach (var work in relocationTargets)
            yield return ("Relocation", work.Key, work.Value.RoomId);
        foreach (var work in planConstructionRooms)
            if (!relocationTargets.ContainsKey(work.Key))
                yield return ("Construction", work.Key, work.Value);
        foreach (var work in planDeconstructionRooms)
            if (!relocationTargets.ContainsKey(work.Key)
                    && !planConstructionRooms.ContainsKey(work.Key))
                yield return ("Deconstruction", work.Key, work.Value);
    }

    private string DescribePendingPlanWork(int roomId = 0)
    {
        const int maximumExamples = 4;
        var pending = PendingPlanWork().Where(work => roomId == 0 || work.RoomId == roomId);
        int total = pending.Count();
        var details = new List<string>(maximumExamples + 2)
        {
            (roomId == 0 ? "Every room matches its plan. Waiting for "
                : (rooms.FirstOrDefault(room => room.Id == roomId)?.Name ?? "The room")
                    + " is waiting for ") + total
                + " remaining plan job" + (total == 1 ? "" : "s") + ":"
        };
        foreach (var work in pending.Take(maximumExamples))
        {
            Tile tile = S.FindEntity<Tile>(work.EntityId);
            ReinstallableComp relocation = work.Kind == "Relocation"
                ? S.Components.Find<ReinstallableComp>(work.EntityId) : null;
            // Find by ID instead of reading Source, whose getter can repair or
            // cancel native relocation state. Status reporting must be read-only.
            Tile source = relocation?.SourceId > 0
                ? S.FindEntity<Tile>(relocation.SourceId) : null;
            string name = tile?.Definition?.NameT ?? source?.Definition?.NameT
                ?? "Missing building";
            string location = tile == null ? "at an unavailable location"
                : source == null ? "at " + PositionText(tile.PosIdx)
                : "from " + PositionText(source.PosIdx) + " to "
                    + PositionText(tile.PosIdx);
            string roomName = rooms.FirstOrDefault(room => room.Id == work.RoomId)?.Name
                ?? "Room " + work.RoomId;
            string kind = work.Kind == "Construction"
                && tile?.PendingReplacement?.IsPending == true
                ? "Replacement" : work.Kind;
            details.Add(kind + ": " + name + " " + location + " (" + roomName
                + ") · " + DescribePendingPlanGoal(tile?.Goal ?? relocation?.Entity?.Goal));
        }
        if (total > maximumExamples)
            details.Add("Plus " + (total - maximumExamples) + " more pending job"
                + (total - maximumExamples == 1 ? "" : "s") + ".");
        return string.Join("\n", details);
    }

    private static string DescribePendingPlanGoal(AIGoal goal)
    {
        if (goal == null) return "no active task";
        string description = string.IsNullOrWhiteSpace(goal.Name) ? string.Empty
            : goal.Name + " · ";
        description += AIGoal.StateName(goal.State);
        string workerName = goal.Agent?.BeingData?.Name;
        description += goal.Agent == null ? " · no worker assigned"
            : string.IsNullOrWhiteSpace(workerName) ? " · worker assigned"
            : " · worker: " + workerName;
        description += " · priority " + goal.GoalPriority;
        if (AIGoal.IsFailed(goal.State) && !string.IsNullOrWhiteSpace(goal.FailReason))
            description += " · last failure: " + goal.FailReason;
        return description;
    }

    private int CancelAllTrackedPlanWork()
    {
        PruneTrackedPlanWork();
        var goals = new HashSet<AIGoal>();
        foreach (int targetId in relocationTargets.Keys)
        {
            AIGoal goal = S.Components.Find<ReinstallableComp>(targetId)?.Entity?.Goal;
            if (goal != null) goals.Add(goal);
        }
        foreach (int tileId in planDeconstructionRooms.Keys)
        {
            AIGoal goal = S.FindEntity<Tile>(tileId)?.Goal;
            if (goal != null) goals.Add(goal);
        }
        foreach (int tileId in planConstructionRooms.Keys)
        {
            AIGoal goal = S.FindEntity<Tile>(tileId)?.Goal;
            if (goal != null) goals.Add(goal);
        }
        foreach (PlanEntry entry in planIndex.IssuedEntries)
        {
            Tile tile = ExistingAt(entry);
            if (tile?.Goal == null) continue;
            if (!tile.IsConstructed || tile.PendingReplacement?.IsPending == true)
                goals.Add(tile.Goal);
        }

        foreach (AIGoal goal in goals)
            AIGoal.Cancel(goal, G.Ticks);
        planIndex.ClearIssued();
        relocationTargets.Clear();
        stagedBuildings.Clear();
        planConstructionRooms.Clear();
        planDeconstructionRooms.Clear();
        return goals.Count;
    }

    private void ApplyRelocationGoalPriorities(long ticks)
    {
        PruneTrackedPlanWork();
        foreach (int targetId in relocationTargets.Keys.ToArray())
        {
            ReinstallableComp target = S.Components.Find<ReinstallableComp>(targetId);
            if (target?.Entity?.IsActive != true || !target.IsPendingQuick)
            {
                relocationTargets.Remove(targetId);
                continue;
            }
            if (target.Entity.Goal == null) continue;
            RelocationWorkState work = relocationTargets[targetId];
            bool shouldRun = Continuous || work.ManualAuthorized;
            if (!shouldRun && target.Entity.Goal.Agent != null) continue;
            int desired = shouldRun ? 5 : 0;
            if (target.Entity.Goal.GoalPriority != desired)
                target.Entity.Goal.SetPriority(ticks, desired);
        }
    }

    private bool TryReturnStagedBuildings(int roomId,
        EquipmentStageState stageState = null, bool directOnly = false)
    {
        int moved = 0;
        int shuffled = 0;
        int restaged = 0;
        var reserved = new HashSet<int>();
        int[] candidates = directOnly ? EquipmentReconciliationCandidates()
            : stagedBuildings.Keys.ToArray();
        foreach (int entityId in candidates)
        {
            RelocationWorkState work = stagedBuildings[entityId];
            Tile source = S.FindEntity<Tile>(entityId);
            if (source?.IsActive != true)
            {
                stagedBuildings.Remove(entityId);
                continue;
            }
            if (!work.ManualAuthorized && !Continuous) continue;
            if (!CanEventuallyRelocate(source)
                    || source.Reinstallable.IsPending) continue;
            if (MatchesPlannedTile(source))
            {
                stagedBuildings.Remove(entityId);
                continue;
            }

            if (directOnly)
            {
                // No new direct move can be admitted at capacity. Keep the
                // per-source cleanup above, but avoid discovering destinations.
                if (!CanStartPlannedRelocation(toStaging: false)) break;
                if (!source.Reinstallable.CanOrderRelocationNow
                    || !TryFindDirectRelocationDestination(source,
                            work.RoomId, reserved, out int destination,
                            out PlanEntry directEntry, stageState, roomId,
                            requireEquipmentPhase: true)) continue;
                if (!CanStartPlannedRelocation(toStaging: false)) break;
                if (!OrderRelocation(source, destination, directEntry.RoomId,
                        false, work.ManualAuthorized,
                        (Facing.Type)directEntry.Rotation)) continue;
                planIndex.SetIssued(directEntry, true);
                reserved.UnionWith(EntryFootprint(directEntry));
                moved++;
                continue;
            }
            stageState ??= BuildEquipmentStageState();
            PlanEntry destinationEntry = planIndex.EntriesForDefinition(source.DefinitionId)
                .Where(entry => entry.Layer == WorldLayer.Objects
                    && (roomId == 0 || entry.RoomId == roomId)
                    && string.Equals(entry.DefId, source.DefinitionId, StringComparison.Ordinal)
                    && (stageState.IsStructureFinished(entry.RoomId)
                        || IsExteriorRoom(entry.RoomId)
                            && ExteriorDestinationFloorIsReady(entry))
                    && !IsEquipmentEntrySatisfied(entry)
                    && RoomAllowsEquipmentPhase(entry, stageState))
                .OrderBy(entry => entry.RoomId == work.RoomId ? 0 : 1)
                .ThenBy(entry => Pos.SqrDistance(source.PosIdx, entry.Position))
                .FirstOrDefault();
            if (destinationEntry == null)
            {
                // Older builds could route an OnFloor building carrying
                // Core's MustBeOutdoors component into Indoor staging. Only
                // planner-tracked staged buildings are corrected, and the
                // correction still uses a normal priority-5 relocation.
                if (OccupiesCompatibleDesignatedStaging(source)
                        || !CanStartPlannedRelocation(toStaging: true)
                        || !source.Reinstallable.CanOrderRelocationNow
                        || !TryFindStagingDestination(source, reserved,
                            out int stagingDestination)) continue;
                AddRelocationReservation(reserved, source,
                    stagingDestination, true);
                if (OrderRelocation(source, stagingDestination,
                        work.RoomId, true, work.ManualAuthorized,
                        source.Transform.Rotation)) restaged++;
                continue;
            }

            Tile blocker = BlockersFor(destinationEntry)
                .FirstOrDefault(tile => tile != null && tile.Id != source.Id);
            if (blocker != null)
            {
                if (!blocker.IsConstructed && string.Equals(blocker.DefinitionId,
                        destinationEntry.DefId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!CanEventuallyRelocate(blocker)
                        || !TryFindRelocationDestination(blocker,
                            destinationEntry.RoomId, reserved,
                            out int blockerDestination, out PlanEntry blockerDirect,
                            stageState))
                {
                    continue;
                }
                if (!blocker.Reinstallable.CanOrderRelocationNow)
                {
                    continue;
                }
                bool blockerToStaging = blockerDirect == null;
                if (!CanStartPlannedRelocation(blockerToStaging))
                {
                    if (!blockerToStaging) break;
                    continue;
                }
                if (blockerToStaging)
                    AddRelocationReservation(reserved, blocker,
                        blockerDestination, true);
                else reserved.UnionWith(EntryFootprint(blockerDirect));
                if (OrderRelocation(blocker, blockerDestination,
                        destinationEntry.RoomId, blockerToStaging,
                        work.ManualAuthorized,
                        blockerToStaging ? blocker.Transform.Rotation
                            : (Facing.Type)blockerDirect.Rotation))
                {
                    if (blockerDirect != null)
                        planIndex.SetIssued(blockerDirect, true);
                    shuffled++;
                }
                continue;
            }

            if (!CanPlaceRelocation(source, destinationEntry.Position,
                    (Facing.Type)destinationEntry.Rotation, reserved))
            {
                continue;
            }
            if (!source.Reinstallable.CanOrderRelocationNow)
            {
                continue;
            }
            if (!CanStartPlannedRelocation(toStaging: false)) break;
            if (OrderRelocation(source, destinationEntry.Position,
                    destinationEntry.RoomId, false, work.ManualAuthorized,
                    (Facing.Type)destinationEntry.Rotation))
            {
                planIndex.SetIssued(destinationEntry, true);
                reserved.UnionWith(EntryFootprint(destinationEntry));
                moved++;
            }
        }
        if (moved + shuffled + restaged == 0) return false;
        string stagedMoveStatus = moved > 0
            ? moved + " relocation" + (moved == 1 ? string.Empty : "s")
                + " from staging to ready final blueprint destinations"
            : string.Empty;
        string blockerMoveStatus = shuffled > 0
            ? shuffled + " destination blocker"
                + (shuffled == 1 ? string.Empty : "s") + " moved"
            : string.Empty;
        string correctedStagingStatus = restaged > 0
            ? restaged + " incompatible staged building"
                + (restaged == 1 ? string.Empty : "s")
                + " moved to compatible staging"
            : string.Empty;
        lastStatus = "Started " + string.Join("; ", new[]
            { stagedMoveStatus, blockerMoveStatus, correctedStagingStatus }
            .Where(value => value.Length > 0)) + " at priority 5. "
            + PlannedRelocationCapacitySummary() + ".";
        return true;
    }

    private bool TryMoveAvailableBuildingsIntoPlan(EquipmentStageState currentStageState,
        PlanEntry[] stageEntries, out HashSet<PlanEntry> relocationWaits,
        out bool waitingForRelocation, out int stagingBlockedEntries,
        out bool madeProgress)
    {
        relocationWaits = new HashSet<PlanEntry>();
        waitingForRelocation = false;
        stagingBlockedEntries = 0;
        madeProgress = false;
        if (stageEntries.Length == 0) return false;

        int moved = 0;
        int shuffled = 0;
        int deconstructed = 0;
        bool handled = false;
        bool batchFull = false;
        bool stagingAdmissionFull = false;
        var reserved = new HashSet<int>();
        Dictionary<string, List<Tile>> availableSources =
            BuildAvailableEquipmentSources(stageEntries,
                currentStageState.Occupancy);
        foreach (PlanEntry destinationEntry in stageEntries
                     .Where(entry => entry.Layer == WorldLayer.Objects))
        {
            Tile blocker = BlockersFor(destinationEntry)
                .FirstOrDefault(tile => tile != null);
            if (blocker != null)
            {
                if (!blocker.IsConstructed && string.Equals(blocker.DefinitionId,
                        destinationEntry.DefId, StringComparison.Ordinal))
                {
                    planIndex.SetIssued(destinationEntry, true);
                    handled = true;
                    continue;
                }
                if (blocker.Reinstallable?.IsPending == true)
                {
                    lastStatus = PlacementBlockerDescription(destinationEntry,
                            blocker)
                        + ". Waiting for the active clearing or staging task.";
                    relocationWaits.Add(destinationEntry);
                    waitingForRelocation = true;
                    handled = true;
                    continue;
                }
                if (CanEventuallyRelocate(blocker)
                        && TryFindRelocationDestination(blocker,
                            destinationEntry.RoomId, reserved,
                            out int blockerDestination, out PlanEntry blockerDirect,
                            currentStageState))
                {
                    if (!blocker.Reinstallable.CanOrderRelocationNow)
                    {
                        lastStatus = blocker.Definition.NameT
                            + " is finishing its current task before the staging shuffle.";
                        relocationWaits.Add(destinationEntry);
                        waitingForRelocation = true;
                        continue;
                    }
                    bool blockerToStaging = blockerDirect == null;
                    RelocationAdmissionBlockReason admissionBlock =
                        PlannedRelocationBlockReason(blockerToStaging);
                    if (admissionBlock != RelocationAdmissionBlockReason.None)
                    {
                        relocationWaits.Add(destinationEntry);
                        waitingForRelocation = true;
                        handled = true;
                        if (admissionBlock
                                == RelocationAdmissionBlockReason.StagingSerialization)
                        {
                            stagingAdmissionFull = true;
                        }
                        else
                        {
                            batchFull = true;
                            if (!blockerToStaging) break;
                        }
                        continue;
                    }
                    if (blockerToStaging)
                        AddRelocationReservation(reserved, blocker,
                            blockerDestination, true);
                    else reserved.UnionWith(EntryFootprint(blockerDirect));
                    if (OrderRelocation(blocker, blockerDestination,
                            destinationEntry.RoomId, blockerToStaging, false,
                            blockerToStaging ? blocker.Transform.Rotation
                                : (Facing.Type)blockerDirect.Rotation))
                    {
                        if (blockerDirect != null)
                            planIndex.SetIssued(blockerDirect, true);
                        shuffled++;
                        handled = true;
                        continue;
                    }
                    relocationWaits.Add(destinationEntry);
                    waitingForRelocation = true;
                    continue;
                }
                if (IsPlannerRemovalProtected(blocker))
                {
                    lastStatus = ProtectedBuildingWaitDescription(blocker);
                    relocationWaits.Add(destinationEntry);
                    waitingForRelocation = true;
                    continue;
                }
                if (CanEventuallyRelocate(blocker)
                        && StagingPositions(StagingTypeFor(blocker)).Count > 0)
                {
                    lastStatus = PlacementBlockerDescription(destinationEntry,
                            blocker)
                        + ". Compatible staging is full.";
                    relocationWaits.Add(destinationEntry);
                    waitingForRelocation = true;
                    stagingBlockedEntries++;
                    continue;
                }

                if (TryOrderPlannerDeconstruction(blocker, destinationEntry.RoomId))
                {
                    deconstructed++;
                    lastStatus = "Clearing " + blocker.Definition.NameT
                        + " because it blocks the planned destination or functional "
                        + "clearance for " + EntryName(destinationEntry) + ".";
                }
                else
                {
                    lastStatus = PlacementBlockerDescription(destinationEntry,
                            blocker)
                        + ". Waiting for the blocker to become removable.";
                }
                if (blocker.Goal != null)
                {
                    handled = true;
                    continue;
                }
                relocationWaits.Add(destinationEntry);
                waitingForRelocation = true;
                continue;
            }

            Tile source = FindAvailableBuildingForPlan(destinationEntry,
                availableSources);
            if (source == null) continue;
            if (!CanPlaceRelocation(source, destinationEntry.Position,
                    (Facing.Type)destinationEntry.Rotation, reserved))
            {
                lastStatus = "The planned destination for " + EntryName(destinationEntry)
                    + " at " + PositionText(destinationEntry.Position)
                    + " is not ready for relocation yet.";
                relocationWaits.Add(destinationEntry);
                waitingForRelocation = true;
                continue;
            }
            if (!source.Reinstallable.CanOrderRelocationNow)
            {
                lastStatus = source.Definition.NameT
                    + " is finishing its current task before moving to "
                    + rooms.First(room => room.Id == destinationEntry.RoomId).Name + ".";
                relocationWaits.Add(destinationEntry);
                waitingForRelocation = true;
                continue;
            }
            if (!CanStartPlannedRelocation(toStaging: false))
            {
                relocationWaits.Add(destinationEntry);
                waitingForRelocation = true;
                handled = true;
                batchFull = true;
                break;
            }
            if (OrderRelocation(source, destinationEntry.Position,
                    destinationEntry.RoomId, false, false,
                    (Facing.Type)destinationEntry.Rotation))
            {
                planIndex.SetIssued(destinationEntry, true);
                reserved.UnionWith(EntryFootprint(destinationEntry));
                if (availableSources.TryGetValue(destinationEntry.DefId,
                        out List<Tile> sources)) sources.Remove(source);
                moved++;
                handled = true;
                continue;
            }
            relocationWaits.Add(destinationEntry);
            waitingForRelocation = true;
        }
        madeProgress = moved + shuffled + deconstructed > 0;
        if (moved + shuffled > 0)
        {
            string availableMoveStatus = moved > 0
                ? moved + " available-building relocation"
                    + (moved == 1 ? string.Empty : "s")
                    + " to ready final blueprint destinations"
                : string.Empty;
            string blockerMoveStatus = shuffled > 0
                ? shuffled + " destination blocker"
                    + (shuffled == 1 ? string.Empty : "s") + " moved"
                : string.Empty;
            lastStatus = "Started " + availableMoveStatus
                + (moved > 0 && shuffled > 0 ? "; " : string.Empty)
                + blockerMoveStatus
                + " at priority 5. " + PlannedRelocationCapacitySummary() + ".";
        }
        else if (batchFull)
        {
            lastStatus = "Ready final-destination moves are waiting for relocation "
                + "batch capacity. " + PlannedRelocationCapacitySummary() + ".";
        }
        else if (stagingAdmissionFull)
        {
            lastStatus = "A destination blocker is waiting for the active staging "
                + "relocation to finish. Staging admission is one at a time; "
                + PlannedRelocationCapacitySummary() + ".";
        }
        return handled;
    }

    private Dictionary<string, List<Tile>> BuildAvailableEquipmentSources(
        PlanEntry[] stageEntries, PlanningOverhaulOccupancySnapshot occupancy)
    {
        Stopwatch perf = Stopwatch.StartNew();
        var neededDefinitions = new HashSet<string>(stageEntries
            .Where(entry => entry.Layer == WorldLayer.Objects)
            .Select(entry => entry.DefId), StringComparer.Ordinal);
        var plannedDestinations = new Dictionary<string, HashSet<int>>(
            StringComparer.Ordinal);
        foreach (string definitionId in neededDefinitions)
        {
            foreach (PlanEntry entry in planIndex.EntriesForDefinition(definitionId))
            {
                if (entry.Layer != WorldLayer.Objects) continue;
                if (!plannedDestinations.TryGetValue(entry.DefId,
                        out HashSet<int> positions))
                {
                    positions = new HashSet<int>();
                    plannedDestinations[entry.DefId] = positions;
                }
                positions.Add(entry.Position);
            }
        }

        var result = new Dictionary<string, List<Tile>>(StringComparer.Ordinal);
        foreach (Tile tile in occupancy.RelocatableEquipment)
        {
            if (tile?.IsActive != true || !tile.IsConstructed
                    || tile.Reinstallable == null || tile.Reinstallable.IsPending
                    || !neededDefinitions.Contains(tile.DefinitionId)
                    || (plannedDestinations.TryGetValue(tile.DefinitionId,
                            out HashSet<int> positions)
                        && positions.Contains(tile.PosIdx))) continue;
            if (!result.TryGetValue(tile.DefinitionId, out List<Tile> sources))
            {
                sources = new List<Tile>();
                result[tile.DefinitionId] = sources;
            }
            sources.Add(tile);
        }
        ReportSlowOperation("BuildAvailableEquipmentSources",
            perf.ElapsedMilliseconds,
            "neededDefinitions=" + neededDefinitions.Count
                + "; occupancyCandidates=" + occupancy.RelocatableEquipment.Count
                + "; availableSources=" + result.Values.Sum(items => items.Count),
            ref nextEquipmentSourcePerfLogTime);
        return result;
    }


    private PlanEntry[] NextEquipmentStageEntries(EquipmentStageState state,
        int roomId)
    {
        bool exterior = IsExteriorRoom(roomId);
        PlanEntry[] pending = RoomPostFloorEntries(roomId)
            .Where(entry => (state.IsStructureFinished(entry.RoomId) || exterior)
                && !IsEquipmentEntrySatisfied(entry)
                && RoomAllowsEquipmentPhase(entry, state)
                && (!exterior || ExteriorDestinationFloorIsReady(entry)))
            .OrderBy(entry => state.OrderFor(entry.RoomId))
            .ThenBy(EquipmentPhase)
            .ThenBy(entry => entry.Position)
            .ToArray();
        if (exterior) return pending;
        PlanEntry first = pending.FirstOrDefault();
        if (first == null) return Array.Empty<PlanEntry>();
        int phase = EquipmentPhase(first);
        return pending.Where(entry => entry.RoomId == roomId
                && EquipmentPhase(entry) == phase)
            .ToArray();
    }

    private bool TryBuildPlannedEquipmentStage(PlanEntry[] stageEntries,
        out bool materialBlocked, out bool researchBlocked)
    {
        const int maximumOrders = 64;
        materialBlocked = false;
        researchBlocked = false;
        if (stageEntries.Length == 0) return false;

        PlanRoom room = rooms.FirstOrDefault(item => item.Id == stageEntries[0].RoomId);
        int phase = EquipmentPhase(stageEntries[0]);
        bool exterior = IsExteriorRoom(stageEntries[0].RoomId);
        string phaseName = exterior ? "exterior" : EquipmentPhaseName(phase);
        int active = 0;
        int locked = 0;
        int blocked = 0;
        var ready = new List<PlanEntry>();
        var lockedEntries = new List<PlanEntry>();
        var blockedEntries = new List<PlanEntry>();
        var blockedDetails = new List<string>();
        foreach (PlanEntry entry in stageEntries)
        {
            Tile existing = ExistingAt(entry);
            if (existing != null)
            {
                if (string.Equals(existing.DefinitionId, entry.DefId,
                        StringComparison.Ordinal) && !existing.IsConstructed)
                {
                    planIndex.SetIssued(entry, true);
                    if (existing.Goal != null)
                    {
                        planConstructionRooms[existing.Id] = entry.RoomId;
                        ApplyIssuedGoalPriority(entry, existing, G.Ticks);
                    }
                    active++;
                }
                else
                {
                    blocked++;
                    blockedEntries.Add(entry);
                    blockedDetails.Add(EntryName(entry) + " at "
                        + PositionText(entry.Position) + ": occupied by "
                        + (existing.Definition?.NameT ?? existing.DefinitionId));
                }
                continue;
            }

            planIndex.SetIssued(entry, false);
            if (ProtectedLocationFor(entry.DefId) != ProtectedBuildingLocation.None)
            {
                blocked++;
                blockedEntries.Add(entry);
                blockedDetails.Add(EntryName(entry) + " at "
                    + PositionText(entry.Position)
                    + ": requires an existing special building for relocation; "
                    + "the planner cannot construct a replacement");
                continue;
            }
            Def def = The.Defs.TryGet(entry.DefId);
            if (def == null)
            {
                blocked++;
                blockedEntries.Add(entry);
                blockedDetails.Add(EntryName(entry) + " at "
                    + PositionText(entry.Position)
                    + ": building definition is unavailable");
                continue;
            }
            if (!S.Research.IsDefUnlocked(def))
            {
                locked++;
                lockedEntries.Add(entry);
                continue;
            }
            Tile prototype = EntityUtils.CreatePrototype<Tile>(def);
            if (prototype == null)
            {
                blocked++;
                blockedEntries.Add(entry);
                blockedDetails.Add(EntryName(entry) + " at "
                    + PositionText(entry.Position)
                    + ": no construction prototype is available");
                continue;
            }
            prototype.Transform.Rotation = (Facing.Type)entry.Rotation;
            if (!CanPlannerPlaceTile(entry.Position, prototype, entry.Layer))
            {
                blocked++;
                blockedEntries.Add(entry);
                blockedDetails.Add(EntryName(entry) + " at "
                    + PositionText(entry.Position)
                    + ": native placement validation rejected the destination");
                continue;
            }
            ready.Add(entry);
        }

        string roomName = room?.Name ?? "The planned room";
        if (active > 0)
        {
            lastStatus = roomName + " " + phaseName + " stage has " + active
                + " construction order" + (active == 1 ? string.Empty : "s")
                + " in progress. No additional construction will be queued in "
                + "this room until those orders finish.";
            return false;
        }
        if (ready.Count == 0)
        {
            if (locked > 0)
            {
                if (exterior && ExteriorHasIncompleteFloors(room?.Id ?? 0))
                    lastStatus = roomName + " has " + locked
                        + " exterior building"
                        + (locked == 1 ? string.Empty : "s")
                        + " waiting for research. Floor expansion and other ready exterior work will continue.";
                else
                {
                    researchBlocked = true;
                    YieldRoomForResearch(room, phaseName, lockedEntries);
                }
            }
            else if (blocked > 0)
                lastStatus = roomName + " " + phaseName
                    + " stage cannot place " + SummarizePlanEntries(blockedEntries)
                    + ". " + SummarizeDetails(blockedDetails);
            else
                lastStatus = roomName + " " + phaseName
                    + " stage is waiting for an available relocation target.";
            return false;
        }

        PlanEntry[] affordable = SelectAffordableConstructionEntries(ready,
            maximumOrders, out PlanEntry[] waiting,
            out Dictionary<MatType, int> committedCosts);
        List<MaterialRequirement> waitingRequirements =
            BuildMaterialRequirements(waiting, committedCosts);
        if (affordable.Length == 0)
        {
            SetContinuousConstructionPreview(room, phaseName, waiting,
                waitingRequirements);
            bool expandingExterior = exterior
                && ExteriorHasIncompleteFloors(room?.Id ?? 0);
            bool signaled = !expandingExterior && SetRoomMaterialWait(room,
                phaseName, waiting[0].Position, waitingRequirements, waiting);
            materialBlocked = !expandingExterior;
            lastStatus = roomName + " " + phaseName
                + " stage cannot start another building yet: "
                + MaterialRequirementsSummary(waitingRequirements)
                + (expandingExterior
                    ? ". Floor expansion and other ready exterior work will continue."
                    : ". This room will yield while later rooms are checked.")
                + AutoFactorySignalSuffix(signaled);
            return false;
        }

        int placed = 0;
        int failed = 0;
        foreach (PlanEntry entry in affordable)
        {
            Def def = The.Defs.TryGet(entry.DefId);
            Tile tile = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
            if (tile == null)
            {
                failed++;
                continue;
            }
            tile.Transform.Rotation = (Facing.Type)entry.Rotation;
            if (!CanPlannerPlaceTile(entry.Position, tile, entry.Layer))
            {
                failed++;
                continue;
            }
            var command = new CmdPlaceTile(entry.Position, tile, warn: true,
                instant: A.InstantBuild);
            command.Execute(S);
            if (!command.IsSuccess)
            {
                failed++;
                continue;
            }
            planIndex.SetIssued(entry, true);
            Tile placedTile = command.PlacedTile ?? ExistingAt(entry);
            if (placedTile != null)
            {
                ApplyIssuedGoalPriority(entry, placedTile, G.Ticks);
                if (placedTile.Goal != null)
                    planConstructionRooms[placedTile.Id] = entry.RoomId;
            }
            placed++;
        }
        if (waiting.Length > 0)
        {
            if (placed > 0) RecordAutomationProgress(room?.Id ?? 0);
            SetContinuousConstructionPreview(room, phaseName, waiting,
                waitingRequirements);
            bool expandingExterior = exterior
                && ExteriorHasIncompleteFloors(room?.Id ?? 0);
            bool signaled = !expandingExterior && SetRoomMaterialWait(room,
                phaseName, waiting[0].Position, waitingRequirements,
                waiting, placed > 0);
            materialBlocked = !expandingExterior;
            lastStatus = roomName + " " + phaseName + " stage queued "
                + placed + " affordable construction order"
                + (placed == 1 ? string.Empty : "s")
                + (failed > 0 ? "; " + failed + " will retry" : string.Empty)
                + ". Remaining buildings are waiting for "
                + MaterialRequirementsSummary(waitingRequirements)
                + (expandingExterior
                    ? "; floor expansion and other ready exterior work will continue."
                    : "; this room will yield while later rooms are checked.")
                + AutoFactorySignalSuffix(signaled);
            return placed > 0;
        }

        Dictionary<MatType, int> costs = ConstructionCosts(affordable);
        SetContinuousConstructionPreview(room, phaseName, affordable, costs);
        if (placed > 0)
        {
            lastStatus = roomName + " " + phaseName + " stage started new construction: "
                + SummarizePlanEntries(affordable.Take(placed)) + ". " + placed
                + " order" + (placed == 1 ? string.Empty : "s") + " queued"
                + (failed > 0 ? "; " + failed + " will retry" : string.Empty)
                + (ready.Count > maximumOrders ? "; remaining entries will follow" : string.Empty)
                + ". The 10% stock buffer remains in inventory.";
            return true;
        }
        lastStatus = roomName + " " + phaseName
            + " stage could not start any construction orders and will retry.";
        return false;
    }

    private PlanEntry[] SelectAffordableConstructionEntries(
        IEnumerable<PlanEntry> candidates, int maximum,
        out PlanEntry[] waiting, out Dictionary<MatType, int> committedCosts)
    {
        var selected = new List<PlanEntry>();
        var delayed = new List<PlanEntry>();
        committedCosts = new Dictionary<MatType, int>();
        var entryCosts = new Dictionary<MatType, int>();
        var availableCounts = new Dictionary<MatType, int>();
        Dictionary<MatType, int> outstandingCommitments =
            SnapshotOutstandingPlanMaterialCommitments();
        foreach (PlanEntry entry in candidates)
        {
            if (selected.Count >= maximum) break;
            entryCosts.Clear();
            AddConstructionCost(entry, entryCosts);
            bool affordable = true;
            foreach (KeyValuePair<MatType, int> item in entryCosts)
            {
                int committed = committedCosts.TryGetValue(item.Key,
                    out int old) ? old : 0;
                int proposed = committed + item.Value;
                if (!availableCounts.TryGetValue(item.Key, out int available))
                {
                    available = AvailableAfterPlanCommitments(item.Key,
                        outstandingCommitments);
                    availableCounts[item.Key] = available;
                }
                if (available >= proposed + ((proposed + 9) / 10)) continue;
                affordable = false;
                break;
            }
            if (!affordable)
            {
                delayed.Add(entry);
                continue;
            }
            selected.Add(entry);
            foreach (KeyValuePair<MatType, int> item in entryCosts)
                committedCosts[item.Key] = committedCosts.TryGetValue(item.Key,
                    out int old) ? old + item.Value : item.Value;
        }
        waiting = selected.Count >= maximum
            ? Array.Empty<PlanEntry>() : delayed.ToArray();
        return selected.ToArray();
    }

    private Dictionary<MatType, int> ConstructionCosts(
        IEnumerable<PlanEntry> constructionEntries)
    {
        var costs = new Dictionary<MatType, int>();
        foreach (PlanEntry entry in constructionEntries)
            AddConstructionCost(entry, costs);
        return costs;
    }

    private static void AddConstructionCost(PlanEntry entry,
        Dictionary<MatType, int> costs)
    {
        Def def = The.Defs.TryGet(entry.DefId);
        if (def?.ConstructedMats == null) return;
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

    private List<MaterialRequirement> BuildMaterialRequirements(
        IEnumerable<PlanEntry> constructionEntries,
        IReadOnlyDictionary<MatType, int> committedCosts = null)
    {
        Dictionary<MatType, int> costs = ConstructionCosts(
            constructionEntries);
        Dictionary<MatType, int> outstandingCommitments =
            SnapshotOutstandingPlanMaterialCommitments();
        return costs.OrderBy(item => item.Key.NameT)
            .Select(item =>
            {
                int committed = committedCosts != null
                    && committedCosts.TryGetValue(item.Key, out int value)
                    ? value : 0;
                return new MaterialRequirement
                {
                    Type = item.Key,
                    Cost = item.Value,
                    RequiredToStart = item.Value + ((item.Value + 9) / 10),
                    Available = AvailableAfterPlanCommitments(item.Key,
                        outstandingCommitments, committed)
                };
            }).ToList();
    }

    private static string EquipmentPhaseName(int phase)
    {
        return phase <= 1 ? "power"
            : phase == 2 ? "atmosphere"
            : "equipment";
    }

    private void SetContinuousConstructionPreview(PlanRoom room,
        string phaseName, IEnumerable<PlanEntry> batch,
        IReadOnlyDictionary<MatType, int> costs)
    {
        var preview = new EquipmentConstructionPreview
        {
            RoomId = room?.Id ?? 0,
            RoomName = room?.Name ?? "The active room",
            PhaseName = phaseName
        };
        preview.Entries.AddRange(batch);
        foreach (KeyValuePair<MatType, int> item in costs
                     .OrderBy(item => item.Key.NameT))
        {
            preview.Materials.Add(new MaterialRequirement
            {
                Type = item.Key,
                Cost = item.Value,
                RequiredToStart = item.Value + ((item.Value + 9) / 10),
                Available = S.Sys.Inventory.AvailableCount(item.Key)
            });
        }
        continuousConstructionPreview = preview;
    }

    private void SetContinuousConstructionPreview(PlanRoom room,
        string phaseName, IEnumerable<PlanEntry> batch,
        IEnumerable<MaterialRequirement> requirements)
    {
        var preview = new EquipmentConstructionPreview
        {
            RoomId = room?.Id ?? 0,
            RoomName = room?.Name ?? "The active room",
            PhaseName = phaseName
        };
        preview.Entries.AddRange(batch);
        preview.Materials.AddRange(requirements);
        continuousConstructionPreview = preview;
    }

    private void ClearContinuousConstructionPreview()
    {
        continuousConstructionPreview = null;
    }

    private bool TryConformUnplannedBuildings(EquipmentStageState stageState,
        int roomId, out bool madeProgress, out bool relocationBlocked,
        out bool broaderRelocationBlock, out bool poweredDestinationWait,
        out string protectedWait)
    {
        protectedWait = null;
        madeProgress = false;
        relocationBlocked = false;
        broaderRelocationBlock = false;
        poweredDestinationWait = false;
        const int maximumActions = 24;
        int moved = 0;
        int staged = 0;
        int deconstructed = 0;
        int busy = 0;
        int stagingFull = 0;
        int stagingAdmissionWaiting = 0;
        int batchCapacityWaiting = 0;
        int wallDestinationWaiting = 0;
        int plannedWallWaiting = 0;
        int poweredDestinationWaiting = 0;
        var protectedWaiting = new List<Tile>();
        var reserved = new HashSet<int>();
        Tile[] unplanned = stageState.UnplannedBuildingsFor(roomId).ToArray();
        foreach (Tile tile in unplanned)
        {
            if (tile?.IsActive != true || !tile.IsConstructed
                    || !TryPlanRoomFor(tile, out int currentRoomId)
                    || currentRoomId != roomId
                    || !stageState.IsStructureFinished(roomId)
                    || OccupiesDesignatedStaging(tile)
                    || MatchesPlannedTile(tile)) continue;
            if (IsPlannerRemovalProtected(tile)
                    && ProtectedPlanEntriesNeedingWork(tile, stageState).Length == 0) continue;

            if (tile.Reinstallable?.IsPending == true)
            {
                busy++;
                continue;
            }

            bool canRelocate = CanEventuallyRelocate(tile);
            if (canRelocate && TryFindRelocationDestination(tile, roomId,
                    reserved, out int destination, out PlanEntry directEntry,
                    stageState))
            {
                if (!tile.Reinstallable.CanOrderRelocationNow)
                {
                    busy++;
                    continue;
                }
                bool toStaging = directEntry == null;
                RelocationAdmissionBlockReason admissionBlock =
                    PlannedRelocationBlockReason(toStaging);
                if (admissionBlock != RelocationAdmissionBlockReason.None)
                {
                    if (admissionBlock
                            == RelocationAdmissionBlockReason.StagingSerialization)
                        stagingAdmissionWaiting++;
                    else
                        batchCapacityWaiting++;
                    continue;
                }
                if (toStaging)
                    AddRelocationReservation(reserved, tile, destination, true);
                else
                    reserved.UnionWith(EntryFootprint(directEntry));
                if (OrderRelocation(tile, destination, roomId,
                        toStaging, false,
                        toStaging ? tile.Transform.Rotation
                            : (Facing.Type)directEntry.Rotation))
                {
                    if (directEntry != null)
                    {
                        planIndex.SetIssued(directEntry, true);
                        moved++;
                    }
                    else staged++;
                }
            }
            else if (IsPlannerRemovalProtected(tile))
            {
                protectedWaiting.Add(tile);
            }
            else if (canRelocate && RequiresPoweredRelocationDestination(tile))
            {
                poweredDestinationWaiting++;
            }
            else if (canRelocate && HasUnfilledPlannedWallDestination(tile))
            {
                wallDestinationWaiting++;
            }
            else if (canRelocate
                    && tile.Definition.LayerId != WorldLayer.Walls
                    && StagingPositions(StagingTypeFor(tile)).Count > 0)
            {
                // A staging design exists for this building type. Wait for it
                // to become usable instead of discarding the building.
                stagingFull++;
            }
            else
            {
                // A wall footprint still requested by the blueprint belongs
                // to structural replacement, never surplus demolition.
                if (tile.Definition.LayerId == WorldLayer.Walls
                        && TileFootprint(tile, tile.PosIdx).Any(position =>
                            planEntriesByFootprint.TryGetValue(position,
                                out List<PlanEntry> planned)
                            && planned.Any(entry => entry.Layer == WorldLayer.Walls)))
                {
                    plannedWallWaiting++;
                    continue;
                }
                if (TryOrderPlannerDeconstruction(tile, roomId)) deconstructed++;
                else busy++;
            }

            if (moved + staged + deconstructed >= maximumActions) break;
        }

        if (moved + staged + deconstructed == 0 && busy == 0
                && stagingAdmissionWaiting == 0 && batchCapacityWaiting == 0
                && stagingFull == 0
                && wallDestinationWaiting == 0 && plannedWallWaiting == 0
                && poweredDestinationWaiting == 0
                && protectedWaiting.Count == 0)
            return false;
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == roomId);
        string roomName = room?.Name ?? "The active room";
        madeProgress = moved + staged + deconstructed > 0;
        poweredDestinationWait = poweredDestinationWaiting > 0;
        relocationBlocked = !madeProgress
            && (busy > 0 || stagingAdmissionWaiting > 0
                || batchCapacityWaiting > 0 || stagingFull > 0
                || wallDestinationWaiting > 0 || plannedWallWaiting > 0
                || poweredDestinationWaiting > 0);
        broaderRelocationBlock = relocationBlocked
            && (busy > 0 || stagingAdmissionWaiting > 0
                || batchCapacityWaiting > 0 || wallDestinationWaiting > 0
                || plannedWallWaiting > 0 || poweredDestinationWaiting > 0
                || protectedWaiting.Count > 0);
        if (protectedWaiting.Count > 0 && !madeProgress && busy == 0
                && stagingAdmissionWaiting == 0 && batchCapacityWaiting == 0
                && stagingFull == 0 && wallDestinationWaiting == 0
                && plannedWallWaiting == 0 && poweredDestinationWaiting == 0)
            protectedWait = SummarizeDetails(
                protectedWaiting.Select(tile => ProtectedBuildingWaitDescription(tile, stageState)));
        lastStatus = roomName + " is clearing buildings that are not in its plan: "
            + moved + " moving directly to final blueprint locations; "
            + staged + " moving to staging at priority 5; "
            + deconstructed + " queued for native deconstruction"
            + (wallDestinationWaiting > 0 ? "; " + wallDestinationWaiting
                + " walls waiting for supported, vacant final blueprint locations" : string.Empty)
            + (plannedWallWaiting > 0 ? "; " + plannedWallWaiting
                + " walls retained on planned footprints for structural replacement" : string.Empty)
            + (busy > 0 ? "; " + busy + " waiting for current work" : string.Empty)
            + (poweredDestinationWaiting > 0 ? "; " + poweredDestinationWaiting
                + " configured production buildings waiting for a valid powered relocation destination"
                : string.Empty)
            + (stagingAdmissionWaiting > 0 ? "; " + stagingAdmissionWaiting
                + " waiting for the active staging relocation to finish (staging admission is one at a time; "
                + PlannedRelocationCapacitySummary() + ")" : string.Empty)
            + (batchCapacityWaiting > 0 ? "; " + batchCapacityWaiting
                + " waiting for relocation batch capacity ("
                + PlannedRelocationCapacitySummary() + ")" : string.Empty)
            + (stagingFull > 0 ? "; " + stagingFull
                + " waiting because compatible staging is full" : string.Empty)
            + (protectedWaiting.Count > 0 ? ". " + SummarizeDetails(
                protectedWaiting.Select(tile => ProtectedBuildingWaitDescription(tile, stageState))) : string.Empty)
            + ". Buildings: " + SummarizeTiles(unplanned) + ".";
        return true;
    }

    private bool TryPlanRoomFor(Tile tile, out int roomId)
    {
        foreach (int position in TileFootprint(tile, tile.PosIdx))
            if (plannedFloorRoomByPosition.TryGetValue(position, out roomId))
                return true;
        roomId = 0;
        return false;
    }

    private bool MatchesPlannedTile(Tile tile)
    {
        if (!planEntriesByFootprint.TryGetValue(tile.PosIdx,
                out List<PlanEntry> candidates)) return false;
        return candidates.Any(entry => entry.Position == tile.PosIdx
            && entry.Layer == tile.Definition.LayerId
            && string.Equals(entry.DefId, tile.DefinitionId, StringComparison.Ordinal)
            && PlanRotationMatches(entry, tile));
    }

    private bool IsEquipmentEntrySatisfied(PlanEntry entry)
    {
        Tile tile = ExistingAt(entry);
        return tile?.IsConstructed == true
            && tile.PosIdx == entry.Position
            && string.Equals(tile.DefinitionId, entry.DefId, StringComparison.Ordinal)
            && PlanRotationMatches(entry, tile);
    }

    private static bool PlanRotationMatches(PlanEntry entry, Tile tile)
    {
        // Structural completion already treats walls as matching by type and
        // position. Core owns wall connectivity and automatic orientation;
        // clearing and reuse must agree that an occupied matching cell is filled.
        return entry.Layer == WorldLayer.Walls
            || entry.Rotation == (int)tile.Transform.Rotation;
    }

    private static string SummarizeTiles(IEnumerable<Tile> source,
        int maximumTypes = 4)
    {
        Tile[] tiles = source?.Where(tile => tile != null).ToArray()
            ?? Array.Empty<Tile>();
        if (tiles.Length == 0) return "none";
        var groups = tiles.GroupBy(tile => tile.DefinitionId ?? string.Empty,
                StringComparer.Ordinal)
            .OrderBy(group => group.First().Definition?.NameT,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string result = string.Join("; ", groups.Take(maximumTypes).Select(group =>
        {
            Tile first = group.First();
            string positions = string.Join(", ", group.Take(3)
                .Select(tile => PositionText(tile.PosIdx)));
            if (group.Count() > 3) positions += ", ...";
            return (first.Definition?.NameT ?? first.DefinitionId) + " x"
                + group.Count() + " at " + positions;
        }));
        if (groups.Length > maximumTypes)
            result += "; plus " + (groups.Length - maximumTypes)
                + " more building type" + (groups.Length - maximumTypes == 1 ? "" : "s");
        return result;
    }


    private PlanRoom FindNextIncompleteRoom(EquipmentStageState state,
        ISet<int> excludedRooms, bool wrap = true)
    {
        PlanRoom[] candidates = rooms.OrderBy(room => room.Order)
            .ThenBy(room => room.Id)
            .Where(room => (excludedRooms == null
                    || !excludedRooms.Contains(room.Id))
                && RoomNeedsWork(room.Id, state))
            .ToArray();
        if (candidates.Length == 0) return null;

        PlanRoom active = candidates.FirstOrDefault(room =>
            room.Id == continuousRoomId);
        if (active != null) return active;

        PlanRoom cursor = rooms.FirstOrDefault(room =>
            room.Id == continuousRoomCursorId);
        if (cursor == null) return candidates[0];
        PlanRoom following = candidates.FirstOrDefault(room => room.Order > cursor.Order
            || (room.Order == cursor.Order && room.Id > cursor.Id));
        return following ?? (wrap ? candidates[0] : null);
    }

    private int ConstructionStageForRoom(int roomId,
        EquipmentStageState state)
    {
        if (!state.IsStructureFinished(roomId))
            return ConstructionStageStructure;
        if (state.HasUnplannedBuildings(roomId))
            return ConstructionStageClearing;
        if (IsExteriorRoom(roomId))
            return state.HasIncompleteEquipment(roomId)
                    || ConstructionSupportNeedsPass(roomId, state)
                ? ConstructionStageEquipment : ConstructionStageComplete;
        if (state.RoomsWaitingForPower.Contains(roomId)
                || state.RoomsWaitingForPowerSupport.Contains(roomId)
                || ConstructionSupportNeedsPass(roomId, state))
            return ConstructionStagePower;
        if (state.RoomsWaitingForAtmosphere.Contains(roomId))
            return ConstructionStageAtmosphere;
        if (state.RoomsWaitingForEquipment.Contains(roomId))
            return ConstructionStageEquipment;
        return ConstructionStageComplete;
    }

    private static int ConstructionStageForMaterialPhase(string phaseName)
    {
        return string.Equals(phaseName, "shell",
                StringComparison.OrdinalIgnoreCase)
            ? ConstructionStageStructure
            : string.Equals(phaseName, "power",
                StringComparison.OrdinalIgnoreCase)
                ? ConstructionStagePower
                : string.Equals(phaseName, "atmosphere",
                    StringComparison.OrdinalIgnoreCase)
                    ? ConstructionStageAtmosphere
                    : ConstructionStageEquipment;
    }

    private bool RoomNeedsWork(int roomId, EquipmentStageState state)
    {
        return !state.IsStructureFinished(roomId)
            || state.HasUnplannedBuildings(roomId)
            || state.HasIncompleteEquipment(roomId)
            || ConstructionSupportNeedsPass(roomId, state);
    }

    private bool RoomHasIncompleteEquipment(int roomId)
    {
        bool exterior = IsExteriorRoom(roomId);
        return RoomPostFloorEntries(roomId).Any(entry =>
            !exterior && EquipmentPhase(entry) == 0
                ? !IsPowerInfrastructureReady(entry)
                : !IsEquipmentEntrySatisfied(entry));
    }

    private string DescribeEquipmentWait(int roomId)
    {
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == roomId);
        string roomName = room?.Name ?? "The active room";
        IReadOnlyList<PlanEntry> equipment = RoomPostFloorEntries(roomId);
        if (IsExteriorRoom(roomId))
        {
            PlanEntry[] waiting = equipment
                .Where(entry => !IsEquipmentEntrySatisfied(entry)).ToArray();
            return waiting.Length > 0
                ? roomName + " still needs exterior buildings on completed floor: "
                    + SummarizePlanEntries(waiting)
                    + ". Power and atmosphere categories do not gate this room."
                : roomName + " is waiting for its active exterior relocation or construction task to finish.";
        }
        PlanEntry[] power = equipment.Where(entry => EquipmentPhase(entry) == 0
                && !IsPowerInfrastructureReady(entry))
            .ToArray();
        if (power.Length > 0)
            return roomName + " is waiting for its power network to be ready: "
                + SummarizePlanEntries(power) + ". Built connectors must connect before construction support and atmosphere work start. "
                + "This room will retain its power phase and yield while later rooms are checked.";

        PlanEntry[] support = equipment.Where(entry => EquipmentPhase(entry) == 1
                && !IsEquipmentEntrySatisfied(entry))
            .ToArray();
        if (support.Length > 0)
            return roomName + " still needs its planned construction support: "
                + SummarizePlanEntries(support)
                + ". Nanobot Hives and Quantum Storage endpoints are prioritized after the power network connects.";

        PlanEntry[] atmosphere = equipment.Where(entry => EquipmentPhase(entry) == 2
                && !IsEquipmentEntrySatisfied(entry))
            .ToArray();
        if (atmosphere.Length > 0)
            return roomName + " still needs atmosphere buildings: "
                + SummarizePlanEntries(atmosphere)
                + ". They will be moved from an available source or constructed when their destinations are ready.";

        PlanEntry[] ordinary = equipment.Where(entry => EquipmentPhase(entry) > 2
                && !IsEquipmentEntrySatisfied(entry))
            .ToArray();
        if (ordinary.Length > 0)
            return roomName + " still needs equipment: "
                + SummarizePlanEntries(ordinary)
                + ". The scheduler will reuse an available matching building, or construct a new one when no reusable copy is available.";
        return roomName + " is waiting for its active relocation or construction task to finish.";
    }

    private string DescribePausedRoomInspection(int roomId,
        PulsePreview structuralPreview,
        EquipmentConstructionPreview constructionPreview, EquipmentStageState state)
    {
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == roomId);
        if (room == null) return "Paused room inspection has no selected room.";
        if (materialWaits.TryGetValue(roomId, out MaterialWaitState wait))
            return "Paused inspection: " + room.Name + " " + wait.PhaseName
                + " is waiting for " + MaterialWaitSummary(wait) + ".";

        state ??= BuildEquipmentStageState();
        if (!state.IsStructureFinished(roomId))
            return "Paused inspection: "
                + PulseBlockReason(structuralPreview, true);

        int unplanned = state.UnplannedBuildingCount(roomId);
        if (unplanned > 0)
            return "Paused inspection: " + room.Name + " is structurally complete and "
                + "is waiting for " + unplanned + " unplanned building"
                + (unplanned == 1 ? string.Empty : "s")
                + " to be relocated or cleared before its next phase.";
        if (constructionPreview?.Entries.Count > 0)
        {
            PlanEntry[] unavailable = constructionPreview.Entries
                .Where(entry => The.Defs.TryGet(entry.DefId) == null).ToArray();
            if (unavailable.Length > 0)
                return "Paused inspection: " + room.Name
                    + " cannot resolve these planned building definitions: "
                    + SummarizePlanEntries(unavailable) + ".";
            PlanEntry[] locked = constructionPreview.Entries
                .Where(entry => !S.Research.IsDefUnlocked(
                    The.Defs.TryGet(entry.DefId))).ToArray();
            if (locked.Length > 0)
                return "Paused inspection: " + room.Name + " is waiting for "
                    + "research before its next construction batch: "
                    + SummarizePlanEntries(locked) + ".";
            MaterialRequirement[] missing = constructionPreview.Materials
                .Where(item => item.Missing > 0).ToArray();
            string phase = string.IsNullOrWhiteSpace(constructionPreview.PhaseName)
                ? "equipment" : constructionPreview.PhaseName;
            if (missing.Length > 0)
                return "Paused inspection: " + room.Name + " " + phase
                    + " is waiting for "
                    + MaterialRequirementsSummary(constructionPreview.Materials) + ".";
            return "Paused inspection: " + room.Name + " has "
                + constructionPreview.Entries.Count + " " + phase + " building"
                + (constructionPreview.Entries.Count == 1 ? string.Empty : "s")
                + " ready to construct; the required materials are available.";
        }
        if (state.HasIncompleteEquipment(roomId))
            return "Paused inspection: " + DescribeEquipmentWait(roomId);
        if (RoomHasPendingConfiguration(roomId))
            return "Paused inspection: " + DescribeConfigurationWait(roomId);
        return "Paused inspection: " + room.Name
            + " currently matches every planned construction phase.";
    }

    private void ResetStagingValidation()
    {
        stagingBlockedRooms.Clear();
        broaderRelocationBlockedRooms.Clear();
        stagingValidationPasses = 0;
        nextStagingValidationTick = 0;
    }

    private void ResetFloorFrontierValidation()
    {
        floorFrontierBlockedRooms.Clear();
        floorFrontierValidationPasses = 0;
        nextFloorFrontierValidationTick = 0;
    }

    private bool IsFloorFrontierValidationWaiting(long ticks)
    {
        return floorFrontierValidationPasses >= 2
            && ticks < nextFloorFrontierValidationTick;
    }

    private void MarkRoomFloorFrontierBlocked(int roomId)
    {
        if (roomId != 0) floorFrontierBlockedRooms.Add(roomId);
    }

    private void BeginOrFinishFloorFrontierValidation(
        EquipmentStageState state, long ticks)
    {
        PlanRoom[] blocked = rooms.OrderBy(room => room.Order)
            .Where(room => !state.IsStructureFinished(room.Id)
                && floorFrontierBlockedRooms.Contains(room.Id))
            .ToArray();
        if (floorFrontierValidationPasses == 0)
        {
            floorFrontierValidationPasses = 1;
            floorFrontierBlockedRooms.Clear();
            continuousRoomId = 0;
            continuousRoomCursorId = 0;
            lastStatus = "No unfinished structural room has a valid completed-floor "
                + "frontier. Starting one final room-by-room validation pass; no floor "
                + "construction will be queued in open space.";
            return;
        }

        floorFrontierValidationPasses = 2;
        continuousRoomId = 0;
        nextFloorFrontierValidationTick = ticks + AutomationIntervalTicks * 10;
        lastStatus = "Construction halted because the remaining structural work has "
            + "no planned floor cardinally adjacent to a completed floor. No floating "
            + "floor construction was queued. Blocked rooms: "
            + string.Join(", ", blocked.Select(room => room.Name))
            + ". Build a connected floor path to the blueprint, or complete the "
            + "required supporting floors manually; Continuous construction will "
            + "check again periodically.";
    }

    private void RecordAutomationProgress(int roomId)
    {
        RecordActiveWorkRotationChange();
        ResetProtectedValidation();
        ResetFloorFrontierValidation();
        ClearRoomMaterialWait(roomId);
        ClearRoomResearchWait(roomId);
        if (stagingValidationPasses > 0)
        {
            ResetStagingValidation();
            return;
        }
        stagingBlockedRooms.Remove(roomId);
        broaderRelocationBlockedRooms.Remove(roomId);
        nextStagingValidationTick = 0;
    }

    private void MarkRoomStagingBlocked(int roomId, EquipmentStageState state,
        long ticks, bool broaderRelocationBlock = false)
    {
        if (roomId == 0) return;
        stagingBlockedRooms.Add(roomId);
        if (broaderRelocationBlock)
            broaderRelocationBlockedRooms.Add(roomId);
        PlanRoom[] incomplete = rooms.OrderBy(room => room.Order)
            .Where(room => RoomNeedsWork(room.Id, state)).ToArray();
        if (incomplete.Length > 0
                && incomplete.All(room => stagingBlockedRooms.Contains(room.Id)))
        {
            BeginOrFinishStagingValidation(state, ticks);
            return;
        }
        PlanRoom room = rooms.FirstOrDefault(item => item.Id == roomId);
        lastStatus += " " + (room?.Name ?? "This room") + (broaderRelocationBlock
            ? " is completely blocked by its current relocation constraints and will yield; the next room will be checked."
            : " will yield while compatible staging is full; the next room will be checked.");
    }

    private void BeginOrFinishStagingValidation(EquipmentStageState state,
        long ticks)
    {
        PlanRoom[] blocked = rooms.OrderBy(room => room.Order)
            .Where(room => RoomNeedsWork(room.Id, state)).ToArray();
        bool broaderRelocationBlock = blocked.Any(room =>
            broaderRelocationBlockedRooms.Contains(room.Id));
        if (stagingValidationPasses == 0)
        {
            stagingValidationPasses = 1;
            stagingBlockedRooms.Clear();
            broaderRelocationBlockedRooms.Clear();
            lastStatus = broaderRelocationBlock
                ? "Relocation constraints blocked every room that still needs work. Starting one final room-by-room validation pass."
                : "Compatible staging blocked every room that still needs work. Starting one final room-by-room validation pass.";
            return;
        }

        stagingValidationPasses = 2;
        continuousRoomId = 0;
        nextStagingValidationTick = ticks + AutomationIntervalTicks * 10;
        lastStatus = broaderRelocationBlock
            ? "Construction halted because the remaining rooms are still blocked by relocation constraints after a complete validation pass. Blocked rooms: "
                + string.Join(", ", blocked.Select(room => room.Name))
                + ". Inspect current building tasks, powered final destinations, and compatible staging; Continuous construction will check again periodically."
            : "Construction halted because compatible staging is full. Blocked rooms: "
                + string.Join(", ", blocked.Select(room => room.Name))
                + ". Clear or enlarge staging to resume; Continuous construction will check again periodically.";
    }

    private bool RoomAllowsEquipmentPhase(PlanEntry entry, EquipmentStageState state)
    {
        if (IsExteriorRoom(entry?.RoomId ?? 0)) return true;
        int phase = EquipmentPhase(entry);
        if (phase > 0 && state.RoomsWaitingForPower.Contains(entry.RoomId))
            return false;
        if (phase > 1
                && state.RoomsWaitingForPowerSupport.Contains(entry.RoomId))
            return false;
        if (phase > 2 && state.RoomsWaitingForAtmosphere.Contains(entry.RoomId))
            return false;
        return true;
    }

    private static bool ShouldYieldForPowerNetwork(EquipmentStageState state,
        int roomId, int actionableEntryCount)
    {
        return state != null && actionableEntryCount == 0
            && state.RoomsWaitingForPower.Contains(roomId);
    }

    private bool IsPowerInfrastructureReady(PlanEntry entry)
    {
        Tile tile = ExistingAt(entry);
        if (!IsEquipmentEntrySatisfied(entry)) return false;
        return tile.ENode == null || tile.ENode.IsConnected;
    }

    private int EquipmentPhase(PlanEntry entry)
    {
        string defId = entry?.DefId ?? string.Empty;
        if (equipmentPhaseByDefId.TryGetValue(defId, out int phase)) return phase;
        phase = IsPowerInfrastructure(entry) ? 0
            : IsConstructionSupportInfrastructure(entry) ? 1
            : IsAtmosphereInfrastructure(entry) ? 2
            : 3;
        equipmentPhaseByDefId[defId] = phase;
        return phase;
    }

    private bool IsPowerInfrastructure(PlanEntry entry)
    {
        string id = entry?.DefId ?? string.Empty;
        return id.IndexOf("connector", StringComparison.OrdinalIgnoreCase) >= 0
            || id.IndexOf("socket", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsAtmosphereInfrastructure(PlanEntry entry)
    {
        Def def = The.Defs.TryGet(entry?.DefId);
        Tile prototype = def == null ? null : EntityUtils.CreatePrototype<Tile>(def);
        return prototype != null && (prototype.HasComponent<OxygenProducerComp>()
            || prototype.HasComponent<VentComp>()
            || prototype.HasComponent<FloorVentComp>());
    }

    private void SaveRelocationState(ComponentData data)
    {
        data.SetString("RelocationTargets", string.Join(";", relocationTargets
            .Select(item => item.Key + "," + item.Value.RoomId + ","
                + (item.Value.ManualAuthorized ? "1" : "0"))));
        data.SetString("StagedBuildings", string.Join(";", stagedBuildings
            .Select(item => item.Key + "," + item.Value.RoomId + ","
                + (item.Value.ManualAuthorized ? "1" : "0"))));
        data.SetString("PlanDeconstructionRooms", string.Join(";",
            planDeconstructionRooms.Select(item => item.Key + "," + item.Value)));
        data.SetString("PlanConstructionRooms", string.Join(";",
            planConstructionRooms.Select(item => item.Key + "," + item.Value)));
    }

    private void LoadRelocationState(ComponentData data)
    {
        relocationTargets.Clear();
        stagedBuildings.Clear();
        planConstructionRooms.Clear();
        planDeconstructionRooms.Clear();
        ParseRelocationMap(data.GetString("RelocationTargets", string.Empty), relocationTargets);
        ParseRelocationMap(data.GetString("StagedBuildings", string.Empty), stagedBuildings);
        ParseIntMap(data.GetString("PlanDeconstructionRooms", string.Empty),
            planDeconstructionRooms);
        ParseIntMap(data.GetString("PlanConstructionRooms", string.Empty),
            planConstructionRooms);
    }

    private static void ParseIntMap(string value, Dictionary<int, int> result)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (string item in value.Split(new[] { ';' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = item.Split(',');
            if (fields.Length == 2 && int.TryParse(fields[0], out int key)
                    && int.TryParse(fields[1], out int mappedValue))
                result[key] = mappedValue;
        }
    }

    private static void ParseRelocationMap(string value,
        Dictionary<int, RelocationWorkState> target)
    {
        foreach (string pair in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = pair.Split(',');
            if (fields.Length >= 2 && int.TryParse(fields[0], out int targetId)
                    && int.TryParse(fields[1], out int roomId))
                target[targetId] = new RelocationWorkState
                {
                    RoomId = roomId,
                    ManualAuthorized = fields.Length >= 3 && fields[2] == "1"
                };
        }
    }

    private enum FunctionalClearanceKind
    {
        WorkSpot,
        InputPort,
        OutputPort
    }

    private readonly struct FunctionalClearanceCell
    {
        internal readonly int Position;
        internal readonly FunctionalClearanceKind Kind;

        internal FunctionalClearanceCell(int position,
            FunctionalClearanceKind kind)
        {
            Position = position;
            Kind = kind;
        }
    }

    private sealed class RelocationWorkState
    {
        internal int RoomId;
        internal bool ManualAuthorized;
    }

}
