using System.Collections.Generic;

namespace TractorBeamOverhaul;

internal readonly struct TargetLoad
{
    public TargetLoad(int entityId, int assignmentCount,
        int movableAssignmentCount, float distanceSquared = 0f)
    {
        EntityId = entityId;
        AssignmentCount = assignmentCount;
        MovableAssignmentCount = movableAssignmentCount;
        DistanceSquared = distanceSquared;
    }

    public int EntityId { get; }
    public int AssignmentCount { get; }
    public int MovableAssignmentCount { get; }
    public float DistanceSquared { get; }

    public TargetLoad WithCounts(int assignmentCount,
        int movableAssignmentCount)
    {
        return new TargetLoad(EntityId, assignmentCount,
            movableAssignmentCount, DistanceSquared);
    }

    public TargetLoad WithDistance(float distanceSquared)
    {
        return new TargetLoad(EntityId, AssignmentCount,
            MovableAssignmentCount, distanceSquared);
    }
}

internal static class EvenLoadPolicy
{
    public static int ChooseLeastLoadedTargetIndex(
        IReadOnlyList<TargetLoad> targets)
    {
        int bestIndex = -1;

        for (int i = 0; i < targets.Count; i++)
        {
            if (bestIndex < 0 || IsBetterIdleTarget(
                    targets[i], targets[bestIndex]))
            {
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    public static bool TryChooseMove(IReadOnlyList<TargetLoad> targets,
        out int donorIndex, out int receiverIndex)
    {
        donorIndex = -1;
        receiverIndex = ChooseStableLowestCountIndex(targets);
        if (receiverIndex < 0)
        {
            return false;
        }

        TargetLoad receiver = targets[receiverIndex];
        for (int i = 0; i < targets.Count; i++)
        {
            TargetLoad candidate = targets[i];
            if (candidate.MovableAssignmentCount <= 0
                || candidate.AssignmentCount
                    <= receiver.AssignmentCount + 1)
            {
                continue;
            }

            if (donorIndex < 0 || IsBetterDonor(
                    candidate, targets[donorIndex]))
            {
                donorIndex = i;
            }
        }

        return donorIndex >= 0;
    }

    private static int ChooseStableLowestCountIndex(
        IReadOnlyList<TargetLoad> targets)
    {
        int bestIndex = -1;
        for (int i = 0; i < targets.Count; i++)
        {
            TargetLoad candidate = targets[i];
            if (bestIndex < 0
                || candidate.AssignmentCount
                    < targets[bestIndex].AssignmentCount
                || candidate.AssignmentCount
                    == targets[bestIndex].AssignmentCount
                    && candidate.EntityId < targets[bestIndex].EntityId)
            {
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static bool IsBetterIdleTarget(TargetLoad candidate,
        TargetLoad current)
    {
        if (candidate.AssignmentCount != current.AssignmentCount)
        {
            return candidate.AssignmentCount < current.AssignmentCount;
        }

        if (candidate.DistanceSquared != current.DistanceSquared)
        {
            return candidate.DistanceSquared < current.DistanceSquared;
        }

        return candidate.EntityId < current.EntityId;
    }

    private static bool IsBetterDonor(TargetLoad candidate,
        TargetLoad current)
    {
        if (candidate.AssignmentCount != current.AssignmentCount)
        {
            return candidate.AssignmentCount > current.AssignmentCount;
        }

        return candidate.EntityId < current.EntityId;
    }
}
