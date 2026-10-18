using System;

namespace SuperCleaning;

internal sealed class OwnedCleaningTask
{
    internal uint GoalId { get; }
    internal int Position { get; }

    internal OwnedCleaningTask(uint goalId, int position)
    {
        GoalId = goalId;
        Position = position;
    }

    internal string Serialize() => GoalId + "|" + Position;

    internal static bool TryDeserialize(string value,
        out OwnedCleaningTask task)
    {
        task = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Split('|');
        if (parts.Length != 2
            || !uint.TryParse(parts[0], out uint goalId)
            || goalId == 0
            || !int.TryParse(parts[1], out int position)
            || position < 0)
        {
            return false;
        }

        task = new OwnedCleaningTask(goalId, position);
        return true;
    }
}
