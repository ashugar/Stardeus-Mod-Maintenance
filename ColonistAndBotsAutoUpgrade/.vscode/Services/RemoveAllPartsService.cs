using Game.Constants;
using Game.Data;
using Game.Systems.Creatures.Surgeries;
using ColonistAndBotsAutoUpgrade.Systems;
using System;

namespace ColonistAndBotsAutoUpgrade.Services
{
    /// <summary>
    /// Shared eligibility and native queueing for CARAU's terminal-chassis
    /// salvage action. Stardeus remains responsible for performing every
    /// removal and for respecting attachment order and non-removable parts.
    /// </summary>
    public static class RemoveAllPartsService
    {
        private static readonly string[] CriticalSlotGroupIds =
        {
            "Battery",
            "Core",
            "Mobilizer"
        };

        public static RemoveAllPartsEligibility Evaluate(
          GameState gameState,
          Being being
        )
        {
            var result = new RemoveAllPartsEligibility();
            var body = being?.Data?.Body;

            if (gameState == null || body?.Slots == null)
            {
                result.Message = "The Being or game state is unavailable.";
                return result;
            }

            var terminalGroups =
                TerminalBodyPartGroupService.GetCompleteTerminalGroupLabels(
                    body.Slots,
                    CriticalSlotGroupIds
                );

            if (terminalGroups.Length == 0)
            {
                result.Message =
                    "No complete Battery, Core, or Mobilizer slot group is " +
                    "destroyed or fitted only with unusable welded parts.";
                return result;
            }

            result.IsTriggered = true;
            result.TerminalGroups = terminalGroups;

            for (var slotIndex = 0;
                 slotIndex < body.Slots.Length;
                 slotIndex++)
            {
                var slot = body.Slots[slotIndex];
                if (slot != null &&
                    !slot.IsPartRemoved &&
                    slot.CanRemovePart(warn: false))
                {
                    result.RemovablePartCount++;
                }
            }

            if (!body.CanPerformMassAction(SurgicalOpType.RemoveAll) ||
                !body.HasSlotsForOperation(SurgicalOpType.RemoveAll) ||
                result.RemovablePartCount == 0)
            {
                result.Message =
                    $"Terminal group: {result.TerminalGroupSummary}. " +
                    "No installed removable parts remain for Stardeus to " +
                    "remove.";
                return result;
            }

            if (!gameState.Query.CanEnqueueMassSurgicalOp.Ask(
                    being.Data,
                    SurgicalOpType.RemoveAll
                ))
            {
                result.Message =
                    $"Terminal group: {result.TerminalGroupSummary}. " +
                    "Another mass surgical operation is already registered " +
                    "for this Being.";
                return result;
            }

            result.CanQueue = true;
            result.Message =
                $"Terminal group: {result.TerminalGroupSummary}. Stardeus " +
                $"can remove {result.RemovablePartCount} installed removable " +
                "part(s). Destroyed, welded, and non-removable parts will " +
                "remain in place.";
            return result;
        }

        public static RemoveAllPartsQueueResult TryQueue(
          GameState gameState,
          Being being
        )
        {
            var eligibility = Evaluate(gameState, being);
            if (!eligibility.CanQueue)
            {
                return new RemoveAllPartsQueueResult
                {
                    Eligibility = eligibility,
                    Message = eligibility.Message
                };
            }

            var operation = new SurgicalOperation
            {
                TargetBeing = being.Data,
                TargetSlot = null,
                OpType = SurgicalOpType.RemoveAll,
                Icon = "Icons/Color/BodyRemoveAll"
            };

            if (!gameState.Query.RegisterSurgicalOperation.Ask(
                    operation,
                    true
                ))
            {
                return new RemoveAllPartsQueueResult
                {
                    Eligibility = eligibility,
                    Operation = operation,
                    Message = operation.FailReason ??
                        "Stardeus rejected the Remove All Parts operation."
                };
            }

            CARAUOperationTrackingSys.TrackQueuedRemoveAllParts(
                being,
                operation,
                eligibility
            );

            return new RemoveAllPartsQueueResult
            {
                Success = true,
                Eligibility = eligibility,
                Operation = operation,
                Message =
                    $"Queued Stardeus Remove All Parts for " +
                    $"{being.Data?.Name ?? "the Being"}."
            };
        }

    }

    public sealed class RemoveAllPartsEligibility
    {
        public bool IsTriggered;
        public bool CanQueue;
        public int RemovablePartCount;
        public string[] TerminalGroups = Array.Empty<string>();
        public string Message;

        public string TerminalGroupSummary =>
            TerminalGroups == null || TerminalGroups.Length == 0
                ? "none"
                : string.Join(", ", TerminalGroups);
    }

    public sealed class RemoveAllPartsQueueResult
    {
        public bool Success;
        public RemoveAllPartsEligibility Eligibility;
        public SurgicalOperation Operation;
        public string Message;
    }
}
