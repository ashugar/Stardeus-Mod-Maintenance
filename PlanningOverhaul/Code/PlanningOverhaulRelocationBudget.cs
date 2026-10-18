using System;
using System.Linq;
using Game.Components;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    private enum RelocationAdmissionBlockReason
    {
        None,
        StagingSerialization,
        CrewCapacity
    }

    private int PlannedRelocationCrewCount()
    {
        return S?.Beings?.Alive?.Count(being => being?.IsActive == true
            && being.IsAlive && being.Data?.IsPlayer == true) ?? 0;
    }

    private int PlannedRelocationLimit()
    {
        int crew = PlannedRelocationCrewCount();
        return crew <= 0 ? 1 : Math.Max(1, crew / 2);
    }

    private int ActivePlannedRelocationCount()
    {
        int active = 0;
        foreach (int targetId in relocationTargets.Keys)
        {
            ReinstallableComp target = S.Components.Find<ReinstallableComp>(
                targetId);
            if (target?.Entity?.IsActive == true && target.IsPendingQuick)
                active++;
        }
        return active;
    }

    private int ActiveStagingRelocationCount()
    {
        int active = 0;
        foreach (int targetId in stagedBuildings.Keys)
        {
            if (!relocationTargets.ContainsKey(targetId)) continue;
            ReinstallableComp target = S.Components.Find<ReinstallableComp>(
                targetId);
            if (target?.Entity?.IsActive == true && target.IsPendingQuick)
                active++;
        }
        return active;
    }

    private bool CanStartPlannedRelocation(bool toStaging)
    {
        return PlannedRelocationBlockReason(toStaging)
            == RelocationAdmissionBlockReason.None;
    }

    private RelocationAdmissionBlockReason PlannedRelocationBlockReason(
        bool toStaging)
    {
        if (toStaging && ActiveStagingRelocationCount() >= 1)
            return RelocationAdmissionBlockReason.StagingSerialization;
        return ActivePlannedRelocationCount() >= PlannedRelocationLimit()
            ? RelocationAdmissionBlockReason.CrewCapacity
            : RelocationAdmissionBlockReason.None;
    }

    private string PlannedRelocationCapacitySummary()
    {
        return ActivePlannedRelocationCount() + "/"
            + PlannedRelocationLimit() + " priority-5 relocation slots active "
            + "from " + PlannedRelocationCrewCount() + " living crew";
    }
}
