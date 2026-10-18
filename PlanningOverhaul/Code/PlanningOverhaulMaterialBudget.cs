using System;
using System.Collections.Generic;
using Game.Constants;

namespace PlanningOverhaul;

public sealed partial class PlanningOverhaulSys
{
    private Dictionary<MatType, int> SnapshotOutstandingPlanMaterialCommitments()
    {
        var commitments = new Dictionary<MatType, int>();
        AddActivePlanConstructionCommitments(commitments);
        return commitments;
    }

    private int AvailableAfterPlanCommitments(MatType type,
        IReadOnlyDictionary<MatType, int> commitments,
        int additionalCommitment = 0)
    {
        if (type == null) return 0;
        int outstanding = commitments != null
            && commitments.TryGetValue(type, out int amount) ? amount : 0;
        return Math.Max(0, S.Sys.Inventory.AvailableCount(type)
            - outstanding - additionalCommitment);
    }

    private void RefreshMaterialAvailability(
        IEnumerable<MaterialRequirement> requirements,
        IReadOnlyDictionary<MatType, int> commitments = null)
    {
        if (requirements == null) return;
        commitments ??= SnapshotOutstandingPlanMaterialCommitments();
        foreach (MaterialRequirement requirement in requirements)
        {
            if (requirement?.Type == null) continue;
            requirement.Available = AvailableAfterPlanCommitments(
                requirement.Type, commitments);
        }
    }
}
