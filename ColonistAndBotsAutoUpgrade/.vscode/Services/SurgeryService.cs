using ColonistAndBotsAutoUpgrade.Models;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Creatures.Surgeries;
using Game.Utils;
using KL.Utils;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public static class SurgeryService
    {
        public static bool TryQueueUpgrade(
            GameState gameState,
            UpgradeDecision decision,
            out string failReason
        )
        {
            var result = TryQueueUpgradeResult(
                gameState,
                decision
            );
            failReason = result.Success ? null : result.Message;
            return result.Success;
        }

        public static QueueAttemptResult TryQueueUpgradeResult(
            GameState gameState,
            UpgradeDecision decision
        )
        {
            if (gameState == null)
            {
                var unavailableEvaluation = decision?.Evaluation;
                if (unavailableEvaluation != null)
                {
                    UpgradeEvaluator.WithNativeFailure(
                        unavailableEvaluation,
                        UpgradeReasonCode.BeingUnavailable,
                        "Game state unavailable."
                    );
                }

                return Failure(
                    QueueAttemptFailureCode.EvaluationNotQueueable,
                    unavailableEvaluation,
                    "Game state unavailable."
                );
            }

            var plannedContext = decision?.Evaluation?.Context;
            var currentSlot = decision?.Slot?.Slot;
            var currentBeingId =
                currentSlot?.Body?.Owner?.Id ?? 0;
            var currentSlotId = currentSlot?.Id ?? string.Empty;
            if (plannedContext != null &&
                (
                    (plannedContext.ExpectedBeingId != 0 &&
                     plannedContext.ExpectedBeingId != currentBeingId) ||
                    (!string.IsNullOrEmpty(
                         plannedContext.ExpectedSlotId
                     ) &&
                     !string.Equals(
                         plannedContext.ExpectedSlotId,
                         currentSlotId,
                         System.StringComparison.Ordinal
                     ))
                ))
            {
                UpgradeEvaluator.WithNativeFailure(
                    decision.Evaluation,
                    UpgradeReasonCode.StaleOwnership,
                    "The planned Being or slot identity changed before " +
                    "native registration. CARAU did not queue the operation."
                );
                return Failure(
                    QueueAttemptFailureCode.EvaluationNotQueueable,
                    decision.Evaluation,
                    UpgradeExplanationFormatter.BuildTooltip(
                        decision.Evaluation
                    )
                );
            }

            var evaluation = UpgradeEvaluator.Evaluate(
                gameState,
                decision?.Slot,
                UpgradeEvaluationPurpose.Confirmation,
                preferredPartId: decision?.InventoryPart?.Id ?? 0,
                actionKind:
                    decision?.ActionKind ?? UpgradeActionKind.Upgrade
            );
            decision = evaluation.Decision;

            if (!evaluation.IsQueueable)
            {
                return Failure(
                    QueueAttemptFailureCode.EvaluationNotQueueable,
                    evaluation,
                    UpgradeExplanationFormatter.BuildTooltip(evaluation)
                );
            }

            var slot = decision.Slot.Slot;
            var part = decision.InventoryPart;

            if (part == null || !part.IsAvailable)
            {
                UpgradeEvaluator.WithNativeFailure(
                    evaluation,
                    UpgradeReasonCode.PreferredReplacementChanged,
                    "Selected part is no longer available."
                );
                evaluation.Context.ExpectedPartId = part?.Id ?? 0;
                return Failure(
                    QueueAttemptFailureCode.ReplacementUnavailable,
                    evaluation,
                    UpgradeExplanationFormatter.BuildTooltip(evaluation)
                );
            }

            var op = new SurgicalOperation
            {
                TargetBeing = slot.Body.Owner,
                TargetSlot = slot,
                ReservedPart = part,
                OpType = slot.IsPartRemoved
                    ? SurgicalOpType.Install
                    : SurgicalOpType.Swap
            };

            var reserved = part.TryReserveForOperation(op);
            if (!reserved)
            {
                UpgradeEvaluator.WithNativeFailure(
                    evaluation,
                    UpgradeReasonCode.NativeReservationFailed,
                    "Selected part could not be reserved for surgery."
                );
                return Failure(
                    QueueAttemptFailureCode.NativeReservationFailed,
                    evaluation,
                    UpgradeExplanationFormatter.BuildTooltip(evaluation)
                );
            }

            var registered =
                gameState.Query.RegisterSurgicalOperation.Ask(op, true);
            if (!registered)
            {
                var nativeReason =
                    op.FailReason ?? "Surgery operation was rejected.";

                if (part.Reservation?.Surgery == op)
                {
                    part.TryUnreserveForOperation(op);
                }

                var requiredFacilityUnavailable =
                    IsRequiredFacilityUnavailable(nativeReason);
                UpgradeEvaluator.WithNativeFailure(
                    evaluation,
                    requiredFacilityUnavailable
                        ? UpgradeReasonCode.RequiredFacilityUnavailable
                        : UpgradeReasonCode.NativeRegistrationRejected,
                    nativeReason
                );
                return Failure(
                    requiredFacilityUnavailable
                        ? QueueAttemptFailureCode.RequiredFacilityUnavailable
                        : QueueAttemptFailureCode.NativeRegistrationRejected,
                    evaluation,
                    UpgradeExplanationFormatter.BuildTooltip(evaluation)
                );
            }

            if (part.Obj != null)
            {
                part.Obj.StorableObj.IsHaulingForbidden = true;

                if (part.Obj.Goal != null)
                {
                    AIGoal.Cancel(
                        part.Obj.Goal,
                        G.Ticks,
                        "Reserved for surgery"
                    );
                }
            }
            if (CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Queue
                ))
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Queue,
                    $"Surgery registered | " +
                    $"Being={decision.Slot?.Slot?.Body?.Owner?.Name ?? "unknown"} | " +
                    $"Slot={decision.Slot?.Slot?.NameT ?? "unknown"} | " +
                    $"Purpose={evaluation.Context.Purpose} | " +
                    $"Disposition={evaluation.Disposition} | " +
                    $"Reason={evaluation.PrimaryReason} | " +
                    $"Type={decision.Type} | " +
                    $"Installed={decision.InstalledPart?.NameT ?? "null"} " +
                    $"T:{decision.InstalledTier} " +
                    $"Q:{Units.Percentage(decision.InstalledQuality, clamp: false)} | " +
                    $"Target={decision.TargetPart?.NameT ?? "null"} " +
                    $"T:{decision.TargetPart?.Tier ?? 0} " +
                    $"Q:{Units.Percentage(decision.AvailableQuality, clamp: false)} | " +
                    $"ObjId={decision.InventoryPart?.Id ?? 0}"
                );
            }
            Systems.CARAUOperationTrackingSys.TrackQueued(decision);

            return new QueueAttemptResult
            {
                Success = true,
                FailureCode = QueueAttemptFailureCode.None,
                Evaluation = evaluation,
                Operation = op,
                Message = UpgradeExplanationFormatter.BuildStatus(evaluation)
            };
        }

        private static QueueAttemptResult Failure(
          QueueAttemptFailureCode failureCode,
          UpgradeEvaluation evaluation,
          string message
        )
        {
            return new QueueAttemptResult
            {
                Success = false,
                FailureCode = failureCode,
                Evaluation = evaluation,
                Message = message
            };
        }

        private static bool IsRequiredFacilityUnavailable(
          string reason
        )
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return false;
            }

            return reason.IndexOf(
                       "Required device is not available",
                       System.StringComparison.OrdinalIgnoreCase
                   ) >= 0 ||
                   (reason.IndexOf(
                        "Busy",
                        System.StringComparison.OrdinalIgnoreCase
                    ) >= 0 &&
                    (reason.IndexOf(
                         "Surgery Hub",
                         System.StringComparison.OrdinalIgnoreCase
                     ) >= 0 ||
                     reason.IndexOf(
                         "Assembly Hub",
                         System.StringComparison.OrdinalIgnoreCase
                     ) >= 0));
        }
    }
}
