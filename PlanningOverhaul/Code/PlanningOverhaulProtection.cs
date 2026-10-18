using System;
using System.Collections.Generic;
using System.Linq;
using Game;
using Game.Commands;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Utils;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    // These originals cannot be replaced by ordinary construction. Protection
    // belongs to the planner, leaving the player's native commands unchanged.
    private enum ProtectedBuildingLocation
    {
        None,
        IndoorAtmosphere,
        ExteriorEdge,
        IndoorOrOutdoor
    }

    private readonly Dictionary<int, string> protectedBuildingWaits = new();
    private int protectedValidationPasses;
    private long nextProtectedValidationTick;

    private static PlanEntry[] SelectProtectedPlanWork(Tile source,
        IEnumerable<PlanEntry> matching, IEnumerable<PlanEntry> overlapping,
        Func<PlanEntry, bool> isSatisfied)
    {
        var work = new HashSet<PlanEntry>();
        foreach (PlanEntry entry in matching)
            if (entry.Layer == source.Definition.LayerId
                    && entry.DefId == source.DefinitionId && !isSatisfied(entry))
                work.Add(entry);
        foreach (PlanEntry entry in overlapping)
            if (entry.Layer >= WorldLayer.Walls && !isSatisfied(entry)) work.Add(entry);
        return work.ToArray();
    }

    private IEnumerable<PlanEntry> ProtectedOverlappingPlanEntries(Tile source)
    {
        foreach (int position in TileFootprint(source, source.PosIdx))
            if (planEntriesByFootprint.TryGetValue(position,
                    out List<PlanEntry> overlapping))
                foreach (PlanEntry entry in overlapping) yield return entry;
    }

    private PlanEntry[] ProtectedPlanEntriesNeedingWork(Tile source,
        EquipmentStageState state = null)
    {
        IEnumerable<PlanEntry> matching = state == null
            ? planIndex.EntriesForDefinition(source.DefinitionId)
            : state.EquipmentEntriesFor(source.DefinitionId);
        return SelectProtectedPlanWork(source, matching,
            ProtectedBlockedPlanEntries(source), IsEquipmentEntrySatisfied);
    }

    private IEnumerable<PlanEntry> ProtectedBlockedPlanEntries(Tile source)
    {
        // Sharing a cell with a different layer does not itself require a
        // move. Let Core accept sockets or other structure beneath an object.
        return ProtectedOverlappingPlanEntries(source).Where(entry =>
            !IsEquipmentEntrySatisfied(entry)
            && (entry.Layer == source.Definition.LayerId
                || entry.Layer == WorldLayer.Floor
                    && IsInstantFloorReplacement(entry)
                || entry.Layer == WorldLayer.Walls && HasConstructedWallSupport(entry)
                    && !CanPlaceStructuralNow(entry)));
    }

    private bool ProtectedBuildingBlocksPlan(Tile source)
    {
        return ProtectedBlockedPlanEntries(source).Any();
    }

    private void ResetProtectedValidation()
    {
        protectedBuildingWaits.Clear();
        protectedValidationPasses = 0;
        nextProtectedValidationTick = 0;
    }

    private bool IsProtectedValidationWaiting(long ticks)
    {
        return protectedValidationPasses >= 2 && ticks < nextProtectedValidationTick;
    }

    private void FinishProtectedRoomRotation(EquipmentStageState state, long ticks)
    {
        if (protectedValidationPasses == 0)
        {
            protectedValidationPasses = 1;
            protectedBuildingWaits.Clear();
            lastStatus = "No further work could start in this rotation. "
                + "Starting one final validation pass for required protected-building work.";
        }
        else
        {
            protectedValidationPasses = 2;
            nextProtectedValidationTick = ticks + AutomationIntervalTicks * 10;
            continuousRoomId = 0;
            lastStatus = DescribeProtectedPlanHalt(state);
        }
    }

    private static ProtectedBuildingLocation ProtectedLocationFor(string definitionId)
    {
        if (definitionId == DefIds.ObjectsDevicesStasisArray
                || definitionId == DefIds.ObjectsDevicesPentagramStatue)
            return ProtectedBuildingLocation.IndoorAtmosphere;
        if (definitionId == DefIds.ObjectsDevicesParticleCollector)
            return ProtectedBuildingLocation.ExteriorEdge;
        if (definitionId?.StartsWith("Objects/Cosmetics/Chess/",
                StringComparison.Ordinal) == true)
            return ProtectedBuildingLocation.IndoorOrOutdoor;
        return ProtectedBuildingLocation.None;
    }

    private static bool IsProtectedBuilding(Tile tile)
    {
        return ProtectedLocationFor(tile?.DefinitionId) != ProtectedBuildingLocation.None;
    }

    private static bool IsShipComputer(Tile tile)
    {
        if (tile == null) return false;
        return tile.TryGetComponent(out ShipComputerComp _)
            || tile.DefinitionId == DefIds.ObjectsDevicesShipComputer
            || tile.DefinitionId == DefIds.ObjectsDevicesQuantumShipComputer;
    }

    private static bool IsPlannerRemovalProtected(Tile tile)
    {
        return IsProtectedBuilding(tile) || IsShipComputer(tile);
    }

    private static bool CanPlannerDeconstruct(Tile tile)
    {
        return tile != null && !IsPlannerRemovalProtected(tile);
    }

    private static bool CanPlannerReplace(Tile previous, Tile relocating)
    {
        return previous == relocating || !IsPlannerRemovalProtected(previous);
    }

    private bool TryOrderPlannerDeconstruction(Tile tile, int roomId)
    {
        if (!CanPlannerDeconstruct(tile)) return false;
        var command = new CmdOrderDeconstruct(tile.PosIdx, tile.Definition.LayerId);
        command.Execute(S);
        if (tile.Goal?.JobIdH != JobTypeIdH.Demolition) return false;
        tile.Goal.SetPriority(G.Ticks, 3);
        planDeconstructionRooms[tile.Id] = roomId;
        return true;
    }

    private void CancelPreviouslyQueuedProtectedPlanWork()
    {
        // Only saved planner ownership authorizes cancelling old destructive
        // orders. Native orders created by the player are outside this policy.
        foreach (int id in planDeconstructionRooms.Keys.ToArray())
        {
            Tile tile = S.FindEntity<Tile>(id);
            if (!IsPlannerRemovalProtected(tile)) continue;
            if (tile.Goal?.JobIdH == JobTypeIdH.Demolition)
                AIGoal.Cancel(tile.Goal, G.Ticks);
            planDeconstructionRooms.Remove(id);
        }
        foreach (int id in planConstructionRooms.Keys.ToArray())
        {
            Tile tile = S.FindEntity<Tile>(id);
            if (!IsPlannerRemovalProtected(tile) || !tile.IsConstructed
                    || tile.PendingReplacement?.IsPending != true) continue;
            tile.PendingReplacement.CancelReplacement();
            planConstructionRooms.Remove(id);
        }
    }

    private bool CanPlannerPlaceTile(int position, Tile prototype, int layer,
        Tile relocating = null)
    {
        if (prototype == null || relocating == null && IsProtectedBuilding(prototype)) return false;
        // Guard every cell of a multi-tile replacement, including child cells.
        foreach (int cell in TileFootprint(prototype, position))
        {
            Tile previous = EntityUtils.ParentObjAt(cell, layer);
            if (!CanPlannerReplace(previous, relocating)) return false;
        }
        return CmdPlaceTile.CanPlace(position, prototype, layer, false, relocating);
    }

    private bool ProtectedDestinationIsReady(Tile source, int destination,
        Facing.Type rotation)
    {
        return ProtectedDestinationIsReady(source, destination, rotation, out _);
    }

    private bool ProtectedDestinationIsReady(Tile source, int destination,
        Facing.Type rotation, out string reason)
    {
        reason = null;
        ProtectedBuildingLocation location = ProtectedLocationFor(source.DefinitionId);
        if (location == ProtectedBuildingLocation.None
                || location == ProtectedBuildingLocation.IndoorOrOutdoor) return true;
        Tile prototype = EntityUtils.CreatePrototype<Tile>(source.Definition);
        if (prototype == null)
        {
            reason = "no relocation prototype is available";
            return false;
        }
        prototype.Transform.Rotation = rotation;
        foreach (int position in TileFootprint(prototype, destination))
        {
            if (position <= 0 || position >= S.GridSize)
            {
                reason = "the footprint leaves the map";
                return false;
            }
            Tile floor = EntityUtils.ParentObj(EntityUtils.FloorAt(position));
            if (location == ProtectedBuildingLocation.IndoorAtmosphere)
            {
                if (floor?.IsConstructed != true)
                    reason = "no completed floor at " + PositionText(position);
                else if (S.Sys.Areas.IsOutside(position))
                    reason = PositionText(position) + " is outside an enclosed room";
                else if (!(S.GetO2(position) >= Consts.MinOxygen))
                    reason = "oxygen at " + PositionText(position) + " is "
                        + S.GetO2(position).ToString("P0") + "; needs at least "
                        + Consts.MinOxygen.ToString("P0");
            }
            else if (floor != null)
                reason = "exterior-edge footprint overlaps floor at " + PositionText(position);
            else if (!S.Sys.Areas.IsOutside(position))
                reason = PositionText(position) + " is not exterior space";
            if (reason != null) return false;
        }
        // Core permits an unfinished adjoining floor; special edge storage
        // requires a completed attachment as well as Core's rotated check.
        if (location == ProtectedBuildingLocation.ExteriorEdge
                && !HasAdjacentConstructedFloor(destination))
            reason = "no completed adjoining floor at the exterior-edge attachment";
        return reason == null;
    }

    private IEnumerable<StagingAreaType> StagingTypesFor(Tile source)
    {
        StagingAreaType preferred = StagingTypeFor(source);
        yield return preferred;
        if (ProtectedLocationFor(source.DefinitionId)
                == ProtectedBuildingLocation.IndoorOrOutdoor)
            yield return preferred == StagingAreaType.Indoor
                ? StagingAreaType.Outdoor : StagingAreaType.Indoor;
    }

    private StagingAreaType StagingTypeAtDestination(Tile source, int destination)
    {
        // Outdoor staging can now contain either open space or completed
        // exterior floor, so support alone no longer identifies its family.
        // Prefer the live, mutually exclusive designation at the target.
        foreach (StagingAreaType type in StagingTypesFor(source))
            if (StagingPositions(type).Contains(destination)) return type;
        return StagingTypeFor(source);
    }

    private string ProtectedBuildingWaitDescription(Tile tile)
    {
        return ProtectedBuildingWaitDescription(tile, null);
    }

    private string ProtectedBuildingWaitDescription(Tile tile, EquipmentStageState state)
    {
        PlanEntry[] work = ProtectedPlanEntriesNeedingWork(tile, state);
        string heading = tile.Definition.NameT + " at " + PositionText(tile.PosIdx);
        if (work.Length == 0)
            return heading + " is retained as a protected original; no missing matching "
                + "blueprint location or blocked planned placement requires it to move.";
        var details = new List<string>();
        foreach (PlanEntry entry in work.Take(3))
        {
            string roomName = rooms.FirstOrDefault(room => room.Id == entry.RoomId)?.Name
                ?? "Room " + entry.RoomId;
            string location = EntryName(entry) + " at " + PositionText(entry.Position)
                + " (" + roomName + ")";
            if (entry.DefId != tile.DefinitionId)
                details.Add("blocks planned " + location + "; needs safe relocation to clear it");
            else
                details.Add("missing blueprint destination " + location + ": "
                    + ProtectedRelocationWaitReason(tile, entry));
        }
        if (work.Length > 3) details.Add("plus " + (work.Length - 3) + " more required locations");
        string staging = string.Join("; ", StagingTypesFor(tile).Select(type =>
            StagingPositions(type).Count == 0 ? type + " staging is not designated"
                : "no compatible " + type + " staging move was accepted"));
        return heading + " is protected from deconstruction. "
            + string.Join("; ", details) + ". " + staging + ".";
    }

    private string ProtectedRelocationWaitReason(Tile source, PlanEntry entry)
    {
        Tile existing = ExistingAt(entry);
        if (existing == source && !PlanRotationMatches(entry, source))
            return "the original is already here, but rotation is " + source.Transform.Rotation
                + "; blueprint requires " + (Facing.Type)entry.Rotation;
        if (existing != null && existing != source)
            return "occupied by " + (existing.Definition?.NameT ?? existing.DefinitionId)
                + (existing.Goal == null ? string.Empty : " · " + DescribePendingPlanGoal(existing.Goal));
        if (!DirectDestinationStructureIsReady(entry))
            return "supporting blueprint structure is not finished";
        if (!ProtectedDestinationIsReady(source, entry.Position,
                (Facing.Type)entry.Rotation, out string reason)) return reason;
        if (!CanPlaceRelocation(source, entry.Position,
                (Facing.Type)entry.Rotation, new HashSet<int>()))
            return "Core rejected the footprint or access-port placement";
        if (source.Reinstallable == null || source.Reinstallable.IsForbidden || source.IsImmutable)
            return "Core does not allow relocation of this original";
        if (!source.Reinstallable.CanOrderRelocationNow)
            return "waiting for current native work · " + DescribePendingPlanGoal(source.Goal);
        if (source.Reinstallable.IsAdvanced && !A.InstantBuild
                && !S.BB.Get(BBKeyH.RelocationAdvanced, false))
            return "Advanced Relocation research is required";
        return "destination passes placement checks, but no relocation has been accepted";
    }

    private string DescribeProtectedPlanHalt(EquipmentStageState state)
    {
        int structuralRooms = rooms.Count(room => !state.IsStructureFinished(room.Id));
        int clearing = rooms.Sum(room => state.UnplannedBuildingsFor(room.Id).Count);
        string description = "Construction halted after a full pass and one validation pass "
            + "could not start more work. Outstanding work: " + structuralRooms
            + " rooms with unfinished structure; " + state.PendingEquipmentEntries.Count
            + " unfinished equipment entries; " + clearing + " buildings requiring clearing.";
        var details = state.StructuralWorkExamples.Concat(state.PendingEquipmentEntries)
            .Take(4).Select(entry =>
            {
                Tile existing = ExistingAt(entry);
                string reason = existing == null
                    ? (ProtectedLocationFor(entry.DefId) != ProtectedBuildingLocation.None
                        ? "an existing original must be relocated here" : "construction has not been queued")
                    : !string.Equals(existing.DefinitionId, entry.DefId, StringComparison.Ordinal)
                        ? "occupied by " + (existing.Definition?.NameT ?? existing.DefinitionId)
                        : !existing.IsConstructed
                            ? "construction ghost · " + DescribePendingPlanGoal(existing.Goal)
                            : !PlanRotationMatches(entry, existing)
                                ? "rotation " + existing.Transform.Rotation + "; needs " + (Facing.Type)entry.Rotation
                                : "waiting for its planned position or power connection";
                return EntryName(entry) + " at " + PositionText(entry.Position) + " ("
                    + (rooms.FirstOrDefault(room => room.Id == entry.RoomId)?.Name
                        ?? "Room " + entry.RoomId) + "): " + reason;
            });
        return description + "\n" + string.Join("\n", details)
            + "\n" + SummarizeDetails(protectedBuildingWaits
                .OrderBy(item => state.OrderFor(item.Key)).Select(item => item.Value))
            + " Protected originals remain intact. Continuous construction will recheck periodically.";
    }

    private void YieldRoomForProtectedBuildings(int roomId, string description)
    {
        protectedBuildingWaits[roomId] = description;
        continuousRoomId = 0;
        lastStatus = description + " Other rooms will be checked before retrying.";
    }
}
