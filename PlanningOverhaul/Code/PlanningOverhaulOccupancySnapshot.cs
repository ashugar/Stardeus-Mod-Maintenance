using System.Collections.Generic;
using Game;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Utils;

namespace PlanningOverhaul;

/// <summary>
/// A short-lived view of the constructed buildings relevant to one planning
/// inspection. It replaces whole-grid scans without becoming authoritative:
/// every selected building is still revalidated immediately before an action.
/// </summary>
internal sealed class PlanningOverhaulOccupancySnapshot
{
    private readonly List<Tile> planRoomBuildings = new();
    private readonly List<Tile> relocatableEquipment = new();

    internal IReadOnlyList<Tile> PlanRoomBuildings => planRoomBuildings;
    internal IReadOnlyList<Tile> RelocatableEquipment => relocatableEquipment;
    internal int PlannedPositionCount { get; private set; }

    internal static PlanningOverhaulOccupancySnapshot Capture(GameState state,
        IEnumerable<int> plannedFloorPositions)
    {
        var snapshot = new PlanningOverhaulOccupancySnapshot();
        if (state?.Map?.Grids == null) return snapshot;

        var seenPlanBuildings = new HashSet<int>();
        if (plannedFloorPositions != null)
        {
            foreach (int position in plannedFloorPositions)
            {
                snapshot.PlannedPositionCount++;
                snapshot.AddPlanBuilding(
                    EntityUtils.ParentObjAt(position, WorldLayer.Walls),
                    seenPlanBuildings);
                snapshot.AddPlanBuilding(EntityUtils.ParentObjAt(position),
                    seenPlanBuildings);
            }
        }

        HashSet<IComponent> reinstallables =
            state.Components.AllByType<ReinstallableComp>();
        if (reinstallables == null) return snapshot;

        var seenRelocatableEquipment = new HashSet<int>();
        foreach (IComponent component in reinstallables)
        {
            if (component is not ReinstallableComp relocation) continue;
            Tile tile = relocation.Tile;
            if (tile?.IsActive != true || !tile.IsConstructed
                    || tile.Definition?.LayerId != WorldLayer.Objects
                    || !seenRelocatableEquipment.Add(tile.Id)) continue;
            snapshot.relocatableEquipment.Add(tile);
        }
        return snapshot;
    }

    private void AddPlanBuilding(Tile tile, HashSet<int> seen)
    {
        tile = EntityUtils.ParentObj(tile);
        if (tile?.IsActive != true || !tile.IsConstructed
                || !seen.Add(tile.Id)) return;
        planRoomBuildings.Add(tile);
    }
}
