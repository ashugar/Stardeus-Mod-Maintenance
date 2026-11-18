using ColonistAndBotsAutoUpgrade.Systems;
using Game.Data;
using Game.Systems.UIUX;
using HarmonyLib;

namespace ColonistAndBotsAutoUpgrade.Patches
{
    /// <summary>
    /// Bot Auto Hack rechecks Core's CanHack method immediately before
    /// dispatch. A false-only postfix makes an exact CARAU conversion claim
    /// monotonic without enabling any target that Core or another mod rejects.
    /// </summary>
    [HarmonyPatch(
        typeof(HackAction),
        "CanHack",
        new[] { typeof(GameState), typeof(Being) }
    )]
    internal static class CARAUEnemyConversionHackGuardPatch
    {
        private static void Postfix(Being being, ref bool __result)
        {
            if (__result && being?.Data != null &&
                CARAUOperationTrackingSys.IsEnemyConversionPending(
                    being.Data.Id
                ))
            {
                __result = false;
            }
        }
    }
}
