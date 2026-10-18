using Game.Constants;

namespace PlanningOverhaul;

/// <summary>
/// Selects the staging family from Core placement and environment contracts.
/// Native placement remains authoritative for the exact destination.
/// </summary>
internal static class PlanningOverhaulStagingPolicy
{
    internal static StagingAreaType SelectType(PlacementConstraint placement,
        bool? mustBeOutdoors, bool sourceHasFloor)
    {
        // Edge-attached objects must retain their structural attachment even
        // when they also carry Core's ordinary outdoors requirement.
        if (placement == PlacementConstraint.InSpaceAttached
                || placement == PlacementConstraint.InSpaceAttachedChained)
            return StagingAreaType.OutdoorEdge;

        // OutdoorsCheck is independent of Constructable.Placement. For
        // example, Return Beacon is OnFloor but explicitly MustBeOutdoors.
        if (mustBeOutdoors.HasValue)
            return mustBeOutdoors.Value
                ? StagingAreaType.Outdoor : StagingAreaType.Indoor;

        if (placement == PlacementConstraint.InSpace)
            return StagingAreaType.Outdoor;
        if (placement == PlacementConstraint.Anywhere && !sourceHasFloor)
            return StagingAreaType.Outdoor;
        return StagingAreaType.Indoor;
    }

    internal static bool OutdoorCellIsReady(bool hasFloor,
        bool floorIsConstructed, bool isOutside)
    {
        // One Outdoor designation serves both Core support variants: open
        // space for InSpace objects and completed exterior floor for OnFloor
        // objects. The final native placement check chooses the valid one.
        return isOutside && (!hasFloor || floorIsConstructed);
    }
}
