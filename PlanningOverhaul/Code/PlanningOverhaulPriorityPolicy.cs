using Game.Constants;
using Game.Data;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    // Planning Overhaul owns whether a plan-issued goal is active, but a
    // positive priority belongs to the player. New and resumed goals receive
    // the normal priority 3 default; later player changes remain untouched.
    private void ApplyIssuedGoalPriority(PlanEntry entry, Tile tile, long ticks)
    {
        if (entry == null || tile?.Goal == null) return;

        bool shouldRun = Continuous
            || (IsStructural(entry) && entry.RoomId == manualPulseRoomId);
        bool waitingForFloorNeighbor = entry.Layer == WorldLayer.Floor
            && !HasConstructedFloorNeighbor(entry);
        if (waitingForFloorNeighbor) shouldRun = false;

        if (shouldRun)
        {
            if (tile.Goal.GoalPriority <= 0)
                tile.Goal.SetPriority(ticks, 3);
            return;
        }

        // Let an already claimed native job finish unless it is an unsupported
        // floor order. The latter must remain disabled so Core never receives a
        // floating-floor construction queue.
        if (tile.Goal.Agent != null && !waitingForFloorNeighbor) return;
        if (tile.Goal.GoalPriority != 0)
            tile.Goal.SetPriority(ticks, 0);
    }
}
