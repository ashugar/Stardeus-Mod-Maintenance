using Game.Data;

namespace SuperCleaning;

internal sealed class IdleCleaningBotStatus
{
    internal Being Being { get; }
    internal string DisplayName { get; }
    internal string Reason { get; }
    internal int Position { get; }

    internal IdleCleaningBotStatus(Being being, string reason)
    {
        Being = being;
        DisplayName = being.Data.Mind.Name.DisplayName;
        Reason = reason;
        Position = being.PosIdx;
    }
}
