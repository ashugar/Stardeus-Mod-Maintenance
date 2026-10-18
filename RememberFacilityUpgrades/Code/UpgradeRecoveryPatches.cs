using Game.Commands;
using Game.Components;
using Game.Data;
using Game.Systems.AI;
using HarmonyLib;

namespace RememberFacilityUpgrades
{

[HarmonyPatch(typeof(UpgradesComp), nameof(UpgradesComp.Receive))]
internal static class UpgradeDestructionCapturePatch
{
    private const int EntityDestroyedMessage = -1107284702;

    private static void Prefix(UpgradesComp __instance, int message)
    {
        if (message != EntityDestroyedMessage) return;
        RememberFacilityUpgradesSys.Instance?
            .CaptureDamageDestruction(__instance);
    }
}

[HarmonyPatch(typeof(CmdRemoveTile), "Execute")]
internal static class UpgradeAutoRebuildCommandPatch
{
    private static void Prefix(Tile ___tileToRemove,
        bool ___autoRebuild)
    {
        RememberFacilityUpgradesSys.Instance?
            .CaptureAutoRebuildCommand(___tileToRemove, ___autoRebuild);
    }
}

[HarmonyPatch(typeof(AIBreakdownActionRemoveUpgrade), "RemoveUpgrade")]
internal static class UpgradeTheftRecoveryPatch
{
    private static void Prefix(Tile target,
        out UpgradeTheftCapture __state)
    {
        __state = RememberFacilityUpgradesSys.Instance?
            .CaptureBeforeUpgradeTheft(target);
    }

    private static void Postfix(Tile target,
        UpgradeTheftCapture __state)
    {
        RememberFacilityUpgradesSys.Instance?
            .RecoverAfterUpgradeTheft(target, __state);
    }
}

[HarmonyPatch(typeof(AIBreakdownActionDeconstruct),
    "PerformDeconstruction")]
internal static class UpgradeBreakdownDeconstructionRecoveryPatch
{
    private static void Prefix(AIAgentComp agent,
        out UpgradeDeconstructionCapture __state)
    {
        Tile target = agent == null ? null : agent.BB.TargetTile;
        __state = RememberFacilityUpgradesSys.Instance?
            .CaptureBeforeBreakdownDeconstruction(target);
    }

    private static void Postfix(UpgradeDeconstructionCapture __state)
    {
        RememberFacilityUpgradesSys.Instance?
            .CaptureAfterBreakdownDeconstruction(__state);
    }
}

internal sealed class UpgradeTheftCapture
{
    public int EntityId { get; set; }
    public string[] InstalledBefore { get; set; }
    public string[] DesiredBefore { get; set; }
    public bool AwaitingRebuildBefore { get; set; }
}

internal sealed class UpgradeDeconstructionCapture
{
    public int EntityId { get; set; }
    public int Position { get; set; }
    public int Layer { get; set; }
    public string DefinitionId { get; set; }
    public string[] InstalledBefore { get; set; }
}
}
