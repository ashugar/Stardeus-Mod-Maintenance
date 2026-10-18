using Game.Commands;
using Game.Components;
using Game.Data;
using HarmonyLib;

namespace ReconnectTeleporterConnections;

[HarmonyPatch(typeof(TeleporterComp), nameof(TeleporterComp.Outbound),
    MethodType.Setter)]
internal static class TeleporterOutboundSetterPatch
{
    private static void Postfix(TeleporterComp __instance,
        TeleporterComp __0)
    {
        ReconnectTeleporterConnectionsSys.Instance?
            .OnOutboundChanged(__instance, __0);
    }
}

[HarmonyPatch(typeof(ConstructableComp), nameof(ConstructableComp.BreakDown))]
internal static class TeleporterBreakDownPatch
{
    private static void Prefix(ConstructableComp __instance,
        bool autoRebuild)
    {
        ReconnectTeleporterConnectionsSys.Instance?
            .MarkPlayerRemoval(__instance, autoRebuild);
    }
}

[HarmonyPatch(typeof(CmdRemoveTile), "Execute")]
internal static class TeleporterInstantRemovePatch
{
    private static void Prefix(Tile ___tileToRemove, bool ___autoRebuild)
    {
        ReconnectTeleporterConnectionsSys.Instance?
            .MarkInstantPlayerRemoval(___tileToRemove, ___autoRebuild);
    }
}
