using System;
using System.Collections.Generic;
using System.Linq;
using Game.Constants;
using Game.Data;
using Game.Utils;
using KL.Grid;
using KL.Utils;

namespace PlanningOverhaul;

/// <summary>
/// Discovery only: choose reusable buildings and final destinations in stable
/// preference order. The caller still admits capacity and submits Core jobs;
/// candidates are never a substitute for current placement/power validation.
/// </summary>
public sealed partial class PlanningOverhaulSys
{
    private bool TryFindDirectRelocationDestination(Tile source, int roomId,
        HashSet<int> reserved, out int destination, out PlanEntry directEntry,
        EquipmentStageState stageState = null, int directRoomId = 0,
        bool requireEquipmentPhase = false)
    {
        destination = Pos.Invalid;
        directEntry = null;
        if (MatchesPlannedTile(source)
                && (!IsPlannerRemovalProtected(source)
                    || !ProtectedBuildingBlocksPlan(source))) return false;
        IEnumerable<PlanEntry> matchingEntries = stageState != null
                && source.Definition.LayerId != WorldLayer.Walls
            ? stageState.EquipmentEntriesFor(source.DefinitionId)
            : planIndex.EntriesForDefinition(source.DefinitionId);
        foreach (PlanEntry entry in matchingEntries
                     .Where(item => item.Layer == source.Definition.LayerId
                         && (item.Layer == WorldLayer.Objects || item.Layer == WorldLayer.Walls)
                         && string.Equals(item.DefId, source.DefinitionId, StringComparison.Ordinal)
                         && item.Position != source.PosIdx
                         && (directRoomId == 0 || item.RoomId == directRoomId))
                     .OrderBy(item => item.RoomId == roomId ? 0 : 1)
                     .ThenBy(item => Pos.SqrDistance(source.PosIdx, item.Position)))
        {
            // Validate in the same stable preference order. Once a destination
            // passes Core, farther candidates need no occupancy or power probes.
            if (IsEquipmentEntrySatisfied(entry)
                    || !DirectDestinationStructureIsReady(entry)
                    || (entry.Layer == WorldLayer.Walls
                        && (!HasConstructedWallSupport(entry)
                            || !EntryFootprint(entry).All(position =>
                                 EntityUtils.ParentObjAt(position, WorldLayer.Walls) == null
                                 && EntityUtils.ParentObjAt(position) == null)))
                    || !TryPrepareRelocationPower(source, entry.Position, out _)
                    || (requireEquipmentPhase
                        && !DestinationAllowsEquipmentPhase(entry, stageState))) continue;
            if (!CanPlaceRelocation(source, entry.Position,
                    (Facing.Type)entry.Rotation, reserved)) continue;
            destination = entry.Position;
            directEntry = entry;
            return true;
        }

        return false;
    }

    private Tile FindAvailableBuildingForPlan(PlanEntry destinationEntry,
        Dictionary<string, List<Tile>> availableSources)
    {
        if (!availableSources.TryGetValue(destinationEntry.DefId,
                out List<Tile> sources)) return null;
        // Power checks can walk the connector network; only probe candidates
        // until the first eligible source in the existing preference order.
        return sources
            .OrderBy(tile => tile.Reinstallable.CanOrderRelocationNow ? 0 : 1)
            .ThenBy(tile => Pos.SqrDistance(tile.PosIdx, destinationEntry.Position))
            .Where(tile => TryPrepareRelocationPower(tile,
                destinationEntry.Position, out _))
            .FirstOrDefault();
    }
}
