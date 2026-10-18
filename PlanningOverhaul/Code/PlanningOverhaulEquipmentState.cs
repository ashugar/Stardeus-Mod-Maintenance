using System;
using System.Collections.Generic;
using System.Linq;
using Game.Data;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace PlanningOverhaul;

/// <summary>
/// Shared equipment inspection and its derived state. World state is captured
/// fresh unless the explicit, bounded Exterior admission cache owns the reuse.
/// </summary>
public sealed partial class PlanningOverhaulSys
{
    private int postFloorPlanRevision = -1;
    private PlanEntry[] postFloorPlanEntries = Array.Empty<PlanEntry>();

    private IReadOnlyList<PlanEntry> PostFloorPlanEntries()
    {
        // Only blueprint membership is cached here, never completion, power,
        // occupancy, research, or staging. Preserve original entry order for
        // deterministic source/destination tie-breaking.
        if (postFloorPlanRevision != planIndex.Revision)
        {
            postFloorPlanEntries = entries.Where(IsRoomPostFloorEntry).ToArray();
            postFloorPlanRevision = planIndex.Revision;
        }
        return postFloorPlanEntries;
    }

    private EquipmentStageState BuildEquipmentStageState()
    {
        Stopwatch perf = Stopwatch.StartNew();
        PruneConstructionSupportAdditions();
        PlanningOverhaulOccupancySnapshot occupancy =
            PlanningOverhaulOccupancySnapshot.Capture(S,
                plannedFloorRoomByPosition.Keys);
        var state = new EquipmentStageState(occupancy);
        foreach (PlanRoom room in rooms)
        {
            state.StructureFinished[room.Id] = true;
            state.RoomOrder[room.Id] = room.Order;
        }

        // One missing structural entry makes the room incomplete. Do not keep
        // walking thousands of later floor entries in that same room. Index
        // keys also preserve the old handling of entries with an orphan room ID.
        foreach (int roomId in planIndex.IndexedRoomIds)
        {
            state.StructureFinished[roomId] = true;
            foreach (PlanEntry entry in RoomStructuralEntries(roomId))
            {
                Tile tile = ExistingAt(entry);
                if (tile?.IsConstructed != true
                        || !string.Equals(tile.DefinitionId, entry.DefId,
                            StringComparison.Ordinal))
                {
                    state.StructureFinished[entry.RoomId] = false;
                    if (state.StructuralWorkExamples.Count < 4)
                        state.StructuralWorkExamples.Add(entry);
                    break;
                }
            }
        }

        foreach (PlanEntry entry in PostFloorPlanEntries())
        {
            if (!state.StructureFinished.ContainsKey(entry.RoomId))
                state.StructureFinished[entry.RoomId] = true;
            state.AddEquipmentEntry(entry);
            bool exterior = IsExteriorRoom(entry.RoomId);
            int phase = EquipmentPhase(entry);
            bool incomplete = !IsEquipmentEntrySatisfied(entry);
            if (!exterior && phase == 0)
                incomplete = !IsPowerInfrastructureReady(entry);
            if (!incomplete) continue;
            state.PendingEquipmentEntries.Add(entry);
            if (exterior)
                state.RoomsWaitingForEquipment.Add(entry.RoomId);
            else if (phase == 0)
                state.RoomsWaitingForPower.Add(entry.RoomId);
            else if (phase == 1)
                state.RoomsWaitingForPowerSupport.Add(entry.RoomId);
            else if (phase == 2)
                state.RoomsWaitingForAtmosphere.Add(entry.RoomId);
            else
                state.RoomsWaitingForEquipment.Add(entry.RoomId);
        }

        foreach (Tile tile in occupancy.PlanRoomBuildings)
        {
            if (tile?.IsActive != true || !tile.IsConstructed
                    || !TryPlanRoomFor(tile, out int roomId)
                    || OccupiesDesignatedStaging(tile)
                    || MatchesPlannedTile(tile)
                    || IsConstructionSupportTile(tile)) continue;
            if (IsPlannerRemovalProtected(tile)
                    && ProtectedPlanEntriesNeedingWork(tile, state).Length == 0)
            {
                state.RetainedProtectedBuildings.Add(tile);
                continue;
            }
            state.AddUnplannedBuilding(roomId, tile);
        }
        ReportSlowOperation("BuildEquipmentStageState", perf.ElapsedMilliseconds,
            "pendingEquipment=" + state.PendingEquipmentEntries.Count
                + "; structuralExamples=" + state.StructuralWorkExamples.Count
                + "; plannedPositions=" + occupancy.PlannedPositionCount
                + "; occupancyCandidates=" + occupancy.PlanRoomBuildings.Count
                + "; unplannedRooms=" + rooms.Count(room =>
                    state.HasUnplannedBuildings(room.Id)),
            ref nextEquipmentStatePerfLogTime);
        return state;
    }

    internal string InspectPausedRoom(int roomId, PulsePreview structuralPreview,
        out EquipmentConstructionPreview constructionPreview)
    {
        // Both UI rows describe the same synchronous read-only inspection.
        // Share its snapshot locally; never retain paused world state across
        // UI refreshes or a subsequent player edit.
        EquipmentStageState state = null;
        constructionPreview = BuildPausedRoomMaterialPreview(roomId, ref state);
        return DescribePausedRoomInspection(roomId, structuralPreview,
            constructionPreview, state);
    }

    private sealed class EquipmentStageState
    {
        internal readonly PlanningOverhaulOccupancySnapshot Occupancy;
        internal readonly List<Tile> RetainedProtectedBuildings = new();
        internal readonly List<PlanEntry> PendingEquipmentEntries = new();
        internal readonly List<PlanEntry> StructuralWorkExamples = new();
        internal readonly Dictionary<int, bool> StructureFinished = new();
        internal readonly Dictionary<int, int> RoomOrder = new();
        internal readonly HashSet<int> RoomsWaitingForPower = new();
        internal readonly HashSet<int> RoomsWaitingForPowerSupport = new();
        internal readonly HashSet<int> RoomsWaitingForAtmosphere = new();
        internal readonly HashSet<int> RoomsWaitingForEquipment = new();
        private readonly Dictionary<int, List<Tile>> unplannedBuildings = new();
        private readonly Dictionary<string, List<PlanEntry>> equipmentEntries =
            new(StringComparer.Ordinal);

        internal EquipmentStageState(PlanningOverhaulOccupancySnapshot occupancy)
        {
            Occupancy = occupancy;
        }

        internal void AddEquipmentEntry(PlanEntry entry)
        {
            if (!equipmentEntries.TryGetValue(entry.DefId,
                    out List<PlanEntry> matching))
            {
                matching = new List<PlanEntry>();
                equipmentEntries[entry.DefId] = matching;
            }
            matching.Add(entry);
        }

        internal IReadOnlyList<PlanEntry> EquipmentEntriesFor(string definitionId)
        {
            return equipmentEntries.TryGetValue(definitionId,
                out List<PlanEntry> matching) ? matching : Array.Empty<PlanEntry>();
        }

        internal bool IsStructureFinished(int roomId)
        {
            return StructureFinished.TryGetValue(roomId, out bool finished) && finished;
        }

        internal int OrderFor(int roomId)
        {
            return RoomOrder.TryGetValue(roomId, out int order) ? order : int.MaxValue;
        }

        internal void AddUnplannedBuilding(int roomId, Tile tile)
        {
            if (!unplannedBuildings.TryGetValue(roomId, out List<Tile> tiles))
            {
                tiles = new List<Tile>();
                unplannedBuildings[roomId] = tiles;
            }
            tiles.Add(tile);
        }

        internal bool HasUnplannedBuildings(int roomId)
        {
            return unplannedBuildings.TryGetValue(roomId, out List<Tile> tiles)
                && tiles.Count > 0;
        }

        internal int UnplannedBuildingCount(int roomId)
        {
            return unplannedBuildings.TryGetValue(roomId, out List<Tile> tiles)
                ? tiles.Count : 0;
        }

        internal bool HasIncompleteEquipment(int roomId)
        {
            return RoomsWaitingForPower.Contains(roomId)
                || RoomsWaitingForPowerSupport.Contains(roomId)
                || RoomsWaitingForAtmosphere.Contains(roomId)
                || RoomsWaitingForEquipment.Contains(roomId);
        }

        internal IReadOnlyList<Tile> UnplannedBuildingsFor(int roomId)
        {
            return unplannedBuildings.TryGetValue(roomId, out List<Tile> tiles)
                ? tiles : Array.Empty<Tile>();
        }
    }
}
