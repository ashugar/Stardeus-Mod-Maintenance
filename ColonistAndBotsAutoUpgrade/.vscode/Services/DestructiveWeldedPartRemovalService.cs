using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Systems;
using Game.Constants;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Creatures.Surgeries;
using System;
using System.Linq;

namespace ColonistAndBotsAutoUpgrade.Services
{
    /// <summary>
    /// Manual-only recovery for welded parts that leave a mechanical body in
    /// an otherwise unrecoverable state. The normal queue, batch, and retry
    /// paths never call this service.
    /// </summary>
    public static class DestructiveWeldedPartRemovalService
    {
        public const string ActionIconId = "Obj/Misc/C42";

        public static DestructiveWeldedPartEligibility Evaluate(
          GameState gameState,
          BeingData being,
          BodyPartSlot slot
        )
        {
            var result = new DestructiveWeldedPartEligibility();
            var body = being?.Body;

            if (gameState == null || body?.Slots == null || slot == null)
            {
                result.Message = "The Being or body-part slot is unavailable.";
                return result;
            }

            if (being.Species == null || being.Species.IsBiological)
            {
                result.Message = "Only mechanical Beings are supported.";
                return result;
            }

            if (being.EffectiveFaction?.IsPlayer != true)
            {
                result.Message = "Only player-owned Beings are supported.";
                return result;
            }

            if (slot.Body != body)
            {
                result.Message = "The selected slot does not belong to this Being.";
                return result;
            }

            if (slot.IsDestroyed || slot.IsPartRemoved ||
                slot.InstalledDef == null || slot.State == null)
            {
                result.Message = "The slot does not contain an installed part.";
                return result;
            }

            if (!slot.State.IsWelded)
            {
                result.Message = "The installed part is not welded in place.";
                return result;
            }

            var terminalGroups =
                TerminalBodyPartGroupService
                    .GetCompleteWeldedUnusableGroupLabels(
                    body.Slots,
                    "Battery",
                    "Mobilizer"
                );
            var isUsableCoreException =
                slot.HoldsMind &&
                slot.State.IsUsableSelf &&
                terminalGroups.Length > 0;

            if (slot.State.IsUsableSelf && !isUsableCoreException)
            {
                result.Message =
                    slot.HoldsMind
                        ? "The welded Core remains usable and no complete " +
                          "Battery or Mobilizer group is terminal."
                        : "The welded part remains usable.";
                return result;
            }

            if (HasNativeSurgicalOperation(gameState, body.Slots))
            {
                result.Message =
                    "A native surgical or Assembly Hub operation is already " +
                    "registered for this Being.";
                return result;
            }

            if (CARAUOperationTrackingSys.HasPendingOperationForBeing(being.Id))
            {
                result.Message =
                    "CARAU is already tracking active or retrying work for " +
                    "this Being.";
                return result;
            }

            result.CanRemove = true;
            result.IsUsableCoreException = isUsableCoreException;
            result.TerminalGroups = terminalGroups;
            result.Message = isUsableCoreException
                ? $"The welded Core is usable, but terminal " +
                  $"{result.TerminalGroupSummary} slot group(s) make the " +
                  "chassis unrecoverable."
                : "The installed welded part is unusable.";
            return result;
        }

        public static bool TryExecute(
          GameState gameState,
          uint beingId,
          string slotId,
          out string result
        )
        {
            result = string.Empty;

            var being = gameState?.Query.FindBeingData.Ask(beingId);
            var slot = being?.Body?.Slots?.FirstOrDefault(candidate =>
                candidate != null &&
                string.Equals(candidate.Id, slotId, StringComparison.Ordinal)
            );
            var eligibility = Evaluate(gameState, being, slot);

            if (!eligibility.CanRemove)
            {
                result = eligibility.Message;
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    "Destructive welded-part removal blocked | " +
                    $"BeingId={beingId} | SlotId={slotId ?? "unknown"} | " +
                    $"Reason={eligibility.Message ?? "unknown"}"
                );
                return false;
            }

            var beingName = being.Name ?? "Unknown being";
            var slotName = slot.NameT ?? slot.Id ?? "Unknown slot";
            var partName = slot.InstalledDef.NameT ?? "Installed part";
            var originalMind = slot.HoldsMind ? being.Mind : null;

            if (!slot.TryRemovePart(
                    gameState,
                    out var extractedPart,
                    null,
                    canFail: false,
                    failure: out var nativeFailure,
                    isForSwap: true,
                    opType: SurgicalOpType.Undefined) ||
                extractedPart?.Data?.BodyPartState == null)
            {
                result =
                    "Stardeus could not extract the welded part through its " +
                    "native body-part cleanup path.";
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    "Destructive welded-part removal failed during native " +
                    $"extraction | Being={beingName} | BeingId={beingId} | " +
                    $"Slot={slotName} | Part={partName} | " +
                    $"NativeFailure={nativeFailure}"
                );
                return false;
            }

            var extractedData = extractedPart.Data;
            extractedData.BodyPartState.Flags |= BodyPartStateFlags.Destroyed;
            slot.IsDestroyed = true;

            // Swap removal defers the body check because an installation
            // normally follows. No replacement follows this manual action.
            being.Body.CheckCondition(isLoading: false);

            var mindPreserved =
                originalMind == null ||
                (extractedData.Mind == originalMind &&
                 originalMind.ObjId == extractedData.Id);
            var completed =
                slot.IsPartRemoved &&
                slot.InstalledDef == null &&
                slot.IsDestroyed &&
                extractedData.BodyPartState.IsDestroyed &&
                mindPreserved;

            if (!completed)
            {
                result =
                    "The native extraction completed, but CARAU could not " +
                    "verify every destruction invariant. Review the Debug log.";
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Error,
                    "Destructive welded-part removal verification failed | " +
                    $"Being={beingName} | BeingId={beingId} | Slot={slotName} | " +
                    $"SlotRemoved={slot.IsPartRemoved} | " +
                    $"SlotDestroyed={slot.IsDestroyed} | " +
                    $"PartDestroyed={extractedData.BodyPartState.IsDestroyed} | " +
                    $"PartObjId={extractedData.Id} | " +
                    $"MindPreserved={mindPreserved}"
                );
                return false;
            }

            result =
                $"The welded {partName} was extracted and destroyed. " +
                $"The {slotName} slot was permanently destroyed.";
            CARAUDebugLog.Add(
                CARAUDebugCategory.Cleanup,
                "Welded part irreversibly removed | " +
                $"Being={beingName} | BeingId={beingId} | Slot={slotName} | " +
                $"Part={partName} | PartObjId={extractedData.Id} | " +
                $"UsableCoreException={eligibility.IsUsableCoreException} | " +
                $"TerminalGroups={eligibility.TerminalGroupSummary} | " +
                $"MindPreserved={mindPreserved} | " +
                $"BodyAlive={being.Body.IsAlive} | " +
                $"HasRemovableParts={being.Body.HasRemovableParts}"
            );
            return true;
        }

        private static bool HasNativeSurgicalOperation(
          GameState gameState,
          BodyPartSlot[] slots
        )
        {
            if (slots == null)
            {
                return false;
            }

            for (var index = 0; index < slots.Length; index++)
            {
                var candidate = slots[index];
                if (candidate != null &&
                    gameState.Query.FindSurgicalOperation.Ask(
                        candidate,
                        true
                    ) != null)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class DestructiveWeldedPartEligibility
    {
        public bool CanRemove;
        public bool IsUsableCoreException;
        public string[] TerminalGroups = Array.Empty<string>();
        public string Message;

        public string TerminalGroupSummary =>
            TerminalGroups == null || TerminalGroups.Length == 0
                ? "none"
                : string.Join(", ", TerminalGroups);
    }
}
