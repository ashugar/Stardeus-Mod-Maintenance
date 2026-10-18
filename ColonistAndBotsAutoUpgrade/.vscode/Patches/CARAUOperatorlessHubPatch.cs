using ColonistAndBotsAutoUpgrade.Systems;
using Game.Components;
using Game.Systems.BodyParts;
using HarmonyLib;

namespace ColonistAndBotsAutoUpgrade.Patches
{
    /// <summary>
    /// Emits the accepted one-time diagnostic at the point Core has selected
    /// the actual Hub/operator context. It does not alter Core execution or
    /// CARAU's already-authorized operator-neutral estimate.
    /// </summary>
    [HarmonyPatch(
        typeof(BodyModSpotComp),
        "PerformActionOnBodyPartSlot"
    )]
    internal static class CARAUOperatorlessHubPatch
    {
        [HarmonyPrefix]
        private static void Prefix(
          [HarmonyArgument(0)] BodyPartSlot slot,
          OperatableDeviceComp ___operatable
        )
        {
            CARAUOperationTrackingSys.ObserveOperatorlessAutomatedHub(
                slot,
                ___operatable
            );
        }
    }
}
