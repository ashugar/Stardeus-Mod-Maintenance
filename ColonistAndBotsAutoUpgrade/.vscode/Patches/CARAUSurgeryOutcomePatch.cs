using System;
using ColonistAndBotsAutoUpgrade.Systems;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Creatures.Surgeries;
using HarmonyLib;
using KL.Randomness;

namespace ColonistAndBotsAutoUpgrade.Patches
{
    /// <summary>
    /// Observes Stardeus's authoritative surgery failure result. Automated
    /// operations can have no surgeon; Stardeus then omits the Being-log line
    /// and exposes the result only through this return value and Player.log.
    /// </summary>
    [HarmonyPatch(
        typeof(SurgeryUtils),
        nameof(SurgeryUtils.ApplySurgicalOpFailure),
        new Type[]
        {
            typeof(Rng),
            typeof(BeingData),
            typeof(BodyPartDef),
            typeof(BodyPartState),
            typeof(BodyPartSlot),
            typeof(bool),
            typeof(SurgicalOpType),
            typeof(string)
        }
    )]
    internal static class CARAUSurgeryOutcomePatch
    {
        [HarmonyPostfix]
        private static void Postfix(
          BodyPartDef part,
          BodyPartSlot slot,
          bool isExtract,
          SurgicalOpFailureType __result
        )
        {
            CARAUOperationTrackingSys.RecordNativeSurgicalFailure(
                part,
                slot,
                isExtract,
                __result
            );
        }
    }

    /// <summary>
    /// Starts CARAU's Core-transition guard before Stardeus broadcasts the
    /// outgoing MindRemoved signal. That signal can unregister operations
    /// synchronously, while the incoming Core has not been installed yet.
    /// </summary>
    [HarmonyPatch(
        typeof(BodyPartSlot),
        nameof(BodyPartSlot.TransferMindAfterPartRemoval)
    )]
    internal static class CARAUCoreMindTransferPatch
    {
        [HarmonyPrefix]
        private static void Prefix(BodyPartSlot __instance)
        {
            CARAUOperationTrackingSys.RecordCoreMindTransfer(__instance);
        }
    }
}
