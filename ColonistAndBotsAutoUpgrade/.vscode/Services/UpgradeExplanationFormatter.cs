using ColonistAndBotsAutoUpgrade.Models;
using Game.Systems.Creatures.Surgeries;
using Game.Utils;
using System.Text;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public static class UpgradeExplanationFormatter
    {
        public static string BuildStatus(UpgradeEvaluation evaluation)
        {
            if (evaluation == null)
            {
                return "No upgrade data";
            }

            var decision = evaluation.Decision;
            switch (evaluation.PrimaryReason)
            {
                case UpgradeReasonCode.Queueable:
                    return BuildQueueableDecisionStatus(decision);
                case UpgradeReasonCode.QualityThresholdMet:
                    return "No upgrade needed — Quality threshold met";
                case UpgradeReasonCode.InsufficientQualityImprovement:
                    return "No upgrade needed — improvement below 5%";
                case UpgradeReasonCode.BestTierInstalled:
                    return "No upgrade needed — best tier installed";
                case UpgradeReasonCode.UpgradePoliciesDisabled:
                    return "Blocked — Tier and Quality upgrades disabled";
                case UpgradeReasonCode.TierUpgradesDisabled:
                    return "Blocked — higher-Tier upgrades disabled";
                case UpgradeReasonCode.QualityUpgradesDisabled:
                    return "Blocked — same-tier Quality upgrades disabled";
                case UpgradeReasonCode.SlotDisabled:
                    return "Blocked — disabled in Slots tab";
                case UpgradeReasonCode.SlotExcludedByPolicy:
                    return "Blocked — excluded by CARAU slot policy";
                case UpgradeReasonCode.AllocatedEarlierInBatch:
                    return "Assigned earlier in Batch Queue";
                case UpgradeReasonCode.RetryPolicyDisallows:
                    return "Retry blocked — current policy does not allow it";
                case UpgradeReasonCode.RetryLimitReached:
                    return "Retry blocked — automatic limit reached";
                case UpgradeReasonCode.RetryChanceBelowThreshold:
                    return "Retry blocked — estimate below threshold";
                case UpgradeReasonCode.MentalBreakdown:
                    return "Temporarily blocked — Mental Breakdown";
                case UpgradeReasonCode.NoCompatibleDefinition:
                    return "Unsupported — no compatible part definition";
                case UpgradeReasonCode.NoMatchingInventoryObject:
                    return "Unavailable — no matching part in inventory";
                case UpgradeReasonCode.MatchingObjectsUnavailable:
                    return "Blocked — matching parts unavailable";
                case UpgradeReasonCode.HostileMindExcluded:
                    return "Blocked by CARAU safety policy — hostile mind";
                case UpgradeReasonCode.UnsupportedObjectState:
                    return "Blocked — matching parts are in unsupported states";
                case UpgradeReasonCode.ExistingCARAUOperation:
                    return "Pending — CARAU procedure";
                case UpgradeReasonCode.ExistingExternalOperation:
                    return "Pending — existing procedure";
                case UpgradeReasonCode.ReservedByCARAUSurgery:
                    return "Blocked — replacement reserved by CARAU";
                case UpgradeReasonCode.ReservedBySurgery:
                    return "Blocked — replacement reserved";
                case UpgradeReasonCode.ReservedByActiveGoal:
                    return "Blocked — replacement assigned to a task";
                case UpgradeReasonCode.DeadBiologicalBeing:
                    return "Unsupported — dead biological Being";
                case UpgradeReasonCode.DeadBeingMissingInstallUnsupported:
                    return "Unsupported — cannot install missing parts on a dead Being";
                case UpgradeReasonCode.DeadBeingRetryUnsupported:
                    return "Retry unavailable — Being is no longer alive";
                case UpgradeReasonCode.SlotDestroyed:
                    return "Unsupported — slot destroyed";
                case UpgradeReasonCode.InstalledPartNotRemovable:
                    return "Unsupported — installed part not removable";
                case UpgradeReasonCode.ParentMissing:
                    return "Blocked — parent part missing";
                case UpgradeReasonCode.ParentDestroyed:
                    return "Blocked — parent slot destroyed";
                case UpgradeReasonCode.ParentUnusable:
                    return "Blocked — parent part unusable";
                case UpgradeReasonCode.BeingUnavailable:
                    return "Unavailable — Being no longer exists";
                case UpgradeReasonCode.SlotUnavailable:
                    return "Unavailable — slot no longer exists";
                case UpgradeReasonCode.SlotNotMissing:
                    return "No action needed — slot is no longer missing";
                case UpgradeReasonCode.InstalledPartNotBad:
                    return "No action needed — installed part is not bad";
                case UpgradeReasonCode.PreferredReplacementChanged:
                    return "Changed — planned replacement unavailable";
                case UpgradeReasonCode.StaleOwnership:
                    return "Blocked — target ownership changed";
                case UpgradeReasonCode.ReservationInconsistent:
                    return "Blocked — inconsistent native reservation";
                case UpgradeReasonCode.NativeReservationFailed:
                    return "Blocked — native reservation failed";
                case UpgradeReasonCode.RequiredFacilityUnavailable:
                    return "Blocked — required facility unavailable";
                case UpgradeReasonCode.NativeRegistrationRejected:
                    return "Blocked — native operation rejected";
                default:
                    return "Not queueable";
            }
        }

        public static string BuildQueueStatus(
          UpgradeEvaluation evaluation
        )
        {
            if (evaluation == null)
            {
                return "Queue: Not queueable — no evaluation";
            }

            if (evaluation.IsQueueable)
            {
                return "Queue: Ready";
            }

            if (evaluation.Disposition ==
                UpgradeDisposition.NoUpgradeNeeded)
            {
                return "Queue: Not needed";
            }

            if (evaluation.PrimaryReason ==
                    UpgradeReasonCode.ExistingCARAUOperation ||
                evaluation.PrimaryReason ==
                    UpgradeReasonCode.ExistingExternalOperation)
            {
                return "Queue: Pending";
            }

            return "Queue: " + BuildStatus(evaluation);
        }

        public static string BuildTooltip(UpgradeEvaluation evaluation)
        {
            if (evaluation == null)
            {
                return "No upgrade evaluation is available.";
            }

            var builder = new StringBuilder();
            builder.AppendLine(BuildStatus(evaluation));

            var slot = evaluation.Decision?.Slot?.Slot;
            if (slot != null)
            {
                builder.AppendLine();
                builder.Append("Slot: ");
                builder.AppendLine(slot.NameT ?? "Unknown");
            }

            AppendCauseAndAction(builder, evaluation);
            AppendExistingOperation(builder, evaluation.Context);
            AppendInventorySummary(builder, evaluation.Context?.Inventory);

            if (evaluation.Context?.ExistingOperation != null &&
                slot != null &&
                !slot.IsPartRemoved &&
                !slot.CanRemovePart(false))
            {
                builder.AppendLine();
                builder.AppendLine(
                    "Warning: The installed part is currently not " +
                    "removable. CARAU will not create another operation."
                );
            }

            if (!string.IsNullOrWhiteSpace(evaluation.Context?.Detail))
            {
                builder.AppendLine();
                builder.AppendLine(evaluation.Context.Detail);
            }

            if (!string.IsNullOrWhiteSpace(
                    evaluation.Context?.NativeReason
                ))
            {
                builder.AppendLine();
                builder.Append("Stardeus reason: ");
                builder.AppendLine(evaluation.Context.NativeReason);
            }

            return builder.ToString().TrimEnd();
        }

        public static string BuildAggregateSummary(
          System.Collections.Generic.IEnumerable<UpgradeEvaluation>
            evaluations
        )
        {
            var total = 0;
            var queueable = 0;
            var noUpgrade = 0;
            var policy = 0;
            var live = 0;
            var existing = 0;
            var unsupported = 0;
            var reasons = new System.Collections.Generic.Dictionary<
                string,
                int
            >();

            if (evaluations != null)
            {
                foreach (var evaluation in evaluations)
                {
                    if (evaluation == null)
                    {
                        continue;
                    }

                    total++;
                    switch (evaluation.Disposition)
                    {
                        case UpgradeDisposition.Queueable:
                            queueable++;
                            break;
                        case UpgradeDisposition.NoUpgradeNeeded:
                            noUpgrade++;
                            break;
                        case UpgradeDisposition.BlockedByPolicy:
                            policy++;
                            break;
                        case UpgradeDisposition.BlockedByLiveState:
                            live++;
                            break;
                        case UpgradeDisposition.BlockedByExistingWork:
                            existing++;
                            break;
                        case UpgradeDisposition.Unsupported:
                            unsupported++;
                            break;
                    }

                    if (!evaluation.IsQueueable)
                    {
                        var status = BuildStatus(evaluation);
                        reasons.TryGetValue(status, out var reasonCount);
                        reasons[status] = reasonCount + 1;
                    }
                }
            }

            var builder = new StringBuilder();
            builder.Append($"Evaluated: {total}; queueable: {queueable}; ");
            builder.Append($"no upgrade needed: {noUpgrade}; policy: {policy}; ");
            builder.Append($"live state: {live}; existing work: {existing}; ");
            builder.Append($"unsupported: {unsupported}.");

            var listed = 0;
            foreach (var reason in reasons)
            {
                if (listed == 0)
                {
                    builder.AppendLine();
                    builder.Append("Reasons: ");
                }
                else
                {
                    builder.Append("; ");
                }

                builder.Append(reason.Value);
                builder.Append(" × ");
                builder.Append(reason.Key);
                listed++;
                if (listed >= 6)
                {
                    break;
                }
            }

            return builder.ToString();
        }

        private static string BuildQueueableDecisionStatus(
          UpgradeDecision decision
        )
        {
            if (decision == null)
            {
                return "Queueable";
            }

            var targetName = decision.TargetPart?.NameT ?? "replacement";
            switch (decision.Type)
            {
                case UpgradeDecisionType.Missing:
                    return $"Missing → {targetName} " +
                           $"(T:{decision.TargetPart?.Tier ?? 0}) | " +
                           $"Available:{decision.InventoryCount} | " +
                           $"Best Q:{Units.Percentage(decision.AvailableQuality, clamp: false)}";
                case UpgradeDecisionType.BadPartReplacement:
                    return $"Replace bad part → {targetName} " +
                           $"(T:{decision.TargetPart?.Tier ?? 0}) | " +
                           $"Available:{decision.InventoryCount} | " +
                           $"Best Q:{Units.Percentage(decision.AvailableQuality, clamp: false)}";
                case UpgradeDecisionType.TierUpgrade:
                    return $"Upgrade → {targetName} " +
                           $"(T:{decision.TargetPart?.Tier ?? 0}) | " +
                           $"Available:{decision.InventoryCount} | " +
                           $"Best Q:{Units.Percentage(decision.AvailableQuality, clamp: false)}";
                case UpgradeDecisionType.QualityUpgrade:
                    return $"Quality → {targetName} | Current Q:" +
                           $"{Units.Percentage(decision.InstalledQuality, clamp: false)} | " +
                           $"Best Q:{Units.Percentage(decision.AvailableQuality, clamp: false)}";
                default:
                    return "Queueable";
            }
        }

        private static void AppendCauseAndAction(
          StringBuilder builder,
          UpgradeEvaluation evaluation
        )
        {
            string action = null;
            switch (evaluation.PrimaryReason)
            {
                case UpgradeReasonCode.UpgradePoliciesDisabled:
                    action = "Enable Higher-Tier Upgrades or Same-Tier " +
                             "Quality Upgrades in CARAU's Quality tab.";
                    break;
                case UpgradeReasonCode.TierUpgradesDisabled:
                    action = "Enable Higher-Tier Upgrades in CARAU's " +
                             "Quality tab if this replacement is desired.";
                    break;
                case UpgradeReasonCode.QualityUpgradesDisabled:
                    action = "Enable Same-Tier Quality Upgrades in CARAU's " +
                             "Quality tab if this replacement is desired.";
                    break;
                case UpgradeReasonCode.SlotDisabled:
                    action = "Enable this slot type in CARAU's Slots tab " +
                             "if it should be eligible for automation.";
                    break;
                case UpgradeReasonCode.SlotNotMissing:
                case UpgradeReasonCode.InstalledPartNotBad:
                    action = "Refresh the CARAU plan to use the current body " +
                             "state.";
                    break;
                case UpgradeReasonCode.RetryPolicyDisallows:
                    action = "Change the Failure Retry Policy if automatic " +
                             "retry is desired.";
                    break;
                case UpgradeReasonCode.RetryLimitReached:
                    action = "Adjust the shared automatic-attempt limit to " +
                             "allow another automatic attempt.";
                    break;
                case UpgradeReasonCode.RetryChanceBelowThreshold:
                    action = "Adjust the automatic surgery-estimate threshold " +
                             "if automatic retry is desired.";
                    break;
                case UpgradeReasonCode.ExistingCARAUOperation:
                case UpgradeReasonCode.ExistingExternalOperation:
                case UpgradeReasonCode.ReservedByCARAUSurgery:
                case UpgradeReasonCode.ReservedBySurgery:
                case UpgradeReasonCode.ReservedByActiveGoal:
                    action = "Complete or cancel the existing work. CARAU " +
                             "will then reevaluate this slot.";
                    break;
                case UpgradeReasonCode.AllocatedEarlierInBatch:
                    action = "The part is assigned to an earlier operation " +
                             "in displayed order. Reorder the Beings if that " +
                             "allocation should change.";
                    break;
                case UpgradeReasonCode.NoMatchingInventoryObject:
                    action = "Obtain or produce a compatible replacement and " +
                             "CARAU will reevaluate the slot.";
                    break;
                case UpgradeReasonCode.MatchingObjectsUnavailable:
                    action = "Wait for current handling to finish or return " +
                             "a matching part to available inventory.";
                    break;
                case UpgradeReasonCode.UnsupportedObjectState:
                    action = "Move a matching part into a supported stored " +
                             "or active inventory state.";
                    break;
                case UpgradeReasonCode.MentalBreakdown:
                    action = "Wait for the Mental Breakdown to end. CARAU " +
                             "will not force work while the Being is untargetable.";
                    break;
                case UpgradeReasonCode.HostileMindExcluded:
                    action = "CARAU will not automatically install a hostile " +
                             "housed mind.";
                    break;
                case UpgradeReasonCode.ParentMissing:
                    action = "Install the missing parent part first.";
                    break;
                case UpgradeReasonCode.ParentDestroyed:
                case UpgradeReasonCode.ParentUnusable:
                    action = "Repair or replace the parent chain before " +
                             "queueing this operation.";
                    break;
                case UpgradeReasonCode.ReservationInconsistent:
                    action = "CARAU left the reservation untouched. Complete " +
                             "or cancel related native work; use the CARAU " +
                             "Debug log if the state persists.";
                    break;
                case UpgradeReasonCode.PreferredReplacementChanged:
                    action = "CARAU will perform a fresh substitution check " +
                             "before skipping the operation.";
                    break;
                case UpgradeReasonCode.StaleOwnership:
                    action = "Refresh the CARAU plan and review the Being's " +
                             "current body state before queueing again.";
                    break;
                case UpgradeReasonCode.NativeReservationFailed:
                    action = "CARAU did not take the part. Refresh the plan " +
                             "after current work and reservations settle.";
                    break;
                case UpgradeReasonCode.RequiredFacilityUnavailable:
                    action = "Free the required Surgery or Assembly Hub, " +
                             "or wait for its current work to finish.";
                    break;
                case UpgradeReasonCode.NativeRegistrationRejected:
                    action = "Review the Stardeus reason below, resolve that " +
                             "condition, and let CARAU reevaluate the slot.";
                    break;
            }

            if (!string.IsNullOrWhiteSpace(action))
            {
                builder.AppendLine();
                builder.AppendLine(action);
            }
        }

        private static void AppendExistingOperation(
          StringBuilder builder,
          UpgradeEvaluationContext context
        )
        {
            var operation = context?.ExistingOperation;
            if (operation == null)
            {
                return;
            }

            builder.AppendLine();
            builder.Append("Existing work: ");
            builder.Append(operation.TargetBeing?.Name ?? "Unknown Being");
            builder.Append(" — ");
            builder.Append(operation.TargetSlot?.NameT ?? "Unknown slot");
            builder.Append(" — ");
            builder.Append(operation.OpType);
            builder.Append(" (");
            builder.Append(operation.State.Value);
            builder.AppendLine(")");

            var reservedPart = operation.ReservedPart;
            if (reservedPart != null)
            {
                builder.Append("Reserved replacement: ");
                builder.Append(
                    reservedPart.Def?.NameT ?? "Unknown part"
                );
                if (reservedPart.BodyPartState != null)
                {
                    builder.Append(" — Quality ");
                    builder.Append(
                        Units.Percentage(
                            reservedPart.BodyPartState.Quality,
                            clamp: false
                        )
                    );
                }
                builder.AppendLine();
            }
        }

        private static void AppendInventorySummary(
          StringBuilder builder,
          UpgradeInventorySummary summary
        )
        {
            if (summary == null || summary.TotalMatchingObjects == 0)
            {
                return;
            }

            builder.AppendLine();
            builder.Append("Matching inventory: ");
            builder.Append(summary.TotalMatchingObjects);
            builder.Append(" total; ");
            builder.Append(summary.AvailableObjects);
            builder.Append(" available");
            AppendCount(builder, "CARAU surgery", summary.ReservedByCARAU);
            AppendCount(builder, "surgery", summary.ReservedBySurgery);
            AppendCount(builder, "active task", summary.ReservedByActiveGoal);
            AppendCount(builder, "earlier batch allocation", summary.AllocatedEarlierInBatch);
            AppendCount(builder, "hostile mind", summary.HostileMindExcluded);
            AppendCount(builder, "unsupported state", summary.UnsupportedState);
            AppendCount(builder, "unavailable", summary.UnavailableObjects);
            AppendCount(builder, "inconsistent reservation", summary.ReservationInconsistencies);
            AppendCount(builder, "below 5% improvement", summary.BelowMinimumQuality);
            builder.AppendLine(".");

            for (var index = 0;
                 index < summary.ReservationExamples.Count;
                 index++)
            {
                var example = summary.ReservationExamples[index];
                builder.Append("• ");
                builder.Append(BuildReservationExample(example));
                builder.AppendLine();
            }
        }

        private static void AppendCount(
          StringBuilder builder,
          string label,
          int count
        )
        {
            if (count <= 0)
            {
                return;
            }

            builder.Append("; ");
            builder.Append(count);
            builder.Append(' ');
            builder.Append(label);
        }

        private static string BuildReservationExample(
          ReservationExplanation example
        )
        {
            if (example == null)
            {
                return "Reservation details unavailable";
            }

            switch (example.OwnerKind)
            {
                case ReservationOwnerKind.CARAUSurgery:
                    return "CARAU procedure for " +
                           (example.BeingName ?? "Unknown Being") + " / " +
                           (example.SlotName ?? "Unknown slot") +
                           FormatOperation(example);
                case ReservationOwnerKind.Surgery:
                    return "Non-CARAU procedure for " +
                           (example.BeingName ?? "Unknown Being") + " / " +
                           (example.SlotName ?? "Unknown slot") +
                           FormatOperation(example);
                case ReservationOwnerKind.ActiveGoal:
                    return "Task: " +
                           (example.GoalName ?? "Unnamed task") +
                           (string.IsNullOrWhiteSpace(example.AgentName)
                               ? string.Empty
                                : " (" + example.AgentName + ")");
                default:
                    return BuildInconsistentReservationExample(example);
            }
        }

        private static string BuildInconsistentReservationExample(
          ReservationExplanation example
        )
        {
            var part = (example.PartName ?? "Unknown part") +
                       (example.PartObjectId == 0
                           ? string.Empty
                           : " object #" + example.PartObjectId);
            var registered = example.RegisteredSurgeryId == 0
                ? "the registered surgery"
                : "registered surgery #" + example.RegisteredSurgeryId;
            var reserved = example.ReservationSurgeryId == 0
                ? "another surgery"
                : "surgery #" + example.ReservationSurgeryId;
            var goal = string.IsNullOrWhiteSpace(example.GoalName)
                ? "unnamed native task"
                : example.GoalName + " task";
            var goalState = string.IsNullOrWhiteSpace(example.GoalState)
                ? string.Empty
                : " (" + example.GoalState + ")";

            switch (example.MismatchKind)
            {
                case ReservationMismatchKind.DuplicateRegisteredOperations:
                    return part + " — duplicate native registration: multiple " +
                           "surgeries reference this exact replacement object";
                case ReservationMismatchKind
                    .RegisteredOperationMissingReservation:
                    return part + " — " + registered + " expects this object, " +
                           "but the object has no native reservation";
                case ReservationMismatchKind
                    .RegisteredOperationOwnedByDifferentSurgery:
                    return part + " — " + registered + " expects this object, " +
                           "but its reservation belongs to " + reserved;
                case ReservationMismatchKind
                    .RegisteredOperationGoalMissingSurgeryLink:
                    return part + " — " + registered + " expects this object, " +
                           "but its active " + goal + " has no surgery link";
                case ReservationMismatchKind
                    .RegisteredOperationGoalReferencesDifferentSurgery:
                    return part + " — " + registered + " expects this object, " +
                           "but its active " + goal + " references surgery #" +
                           example.GoalSurgeryId;
                case ReservationMismatchKind.InactiveGoalPendingCleanup:
                    return part + " — native cleanup pending: inactive " + goal +
                           goalState + " still owns the reservation" +
                           (example.RegisteredSurgeryId == 0
                               ? string.Empty
                               : " while " + registered + " remains registered");
                case ReservationMismatchKind
                    .ReservationReferencesUnregisteredSurgery:
                    return part + " — stale surgery reservation: it references " +
                           reserved + " that is no longer in the native " +
                           "operation registry";
                case ReservationMismatchKind.BlankReservationMarker:
                    return part + " — native reservation marker is present, " +
                           "but it identifies no surgery or task owner";
                case ReservationMismatchKind.UnresolvedLoadReservation:
                    return part + " — unresolved native loading reservation" +
                           FormatLoadReservationIds(example);
                case ReservationMismatchKind
                    .SurgeryGoalWithoutRegisteredOperation:
                    return part + " — active " + goal + " references surgery #" +
                           example.GoalSurgeryId + ", but that surgery is not " +
                           "in the native operation registry";
                case ReservationMismatchKind.ReservationWithoutOwner:
                    return part + " — native reservation exists without a " +
                           "surgery, task, or loading owner";
                default:
                    return part + " — native reservation details are " +
                           "inconsistent with the registered operation state";
            }
        }

        private static string FormatLoadReservationIds(
          ReservationExplanation example
        )
        {
            var goal = string.IsNullOrWhiteSpace(example.LoadGoalId)
                ? string.Empty
                : "goal #" + example.LoadGoalId;
            var surgery = string.IsNullOrWhiteSpace(example.LoadSurgeryId)
                ? string.Empty
                : "surgery #" + example.LoadSurgeryId;
            if (goal.Length == 0)
            {
                return surgery.Length == 0 ? string.Empty : " (" + surgery + ")";
            }

            return surgery.Length == 0
                ? " (" + goal + ")"
                : " (" + goal + ", " + surgery + ")";
        }

        private static string FormatOperation(
          ReservationExplanation example
        )
        {
            if (!example.OperationType.HasValue)
            {
                return string.Empty;
            }

            return " — " + example.OperationType.Value +
                   (example.OperationState.HasValue
                       ? " (" + example.OperationState.Value + ")"
                       : string.Empty);
        }
    }
}
