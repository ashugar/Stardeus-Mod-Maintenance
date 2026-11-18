using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Services;
using ColonistAndBotsAutoUpgrade.Systems;
using Game.Constants;
using Game.Data;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using KL.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ColonistAndBotsAutoUpgrade.UI
{
    public static class EnemyConversionConfirmation
    {
        public static bool Request(
          GameState state,
          IEnumerable<Being> targets,
          Action onCompleted = null
        )
        {
            var requested = targets?
                .Where(being => being != null)
                .Distinct()
                .ToList() ?? new List<Being>();
            var evaluations = EnemyConversionService.BuildEvaluations(
                state,
                requested
            );
            var planned = evaluations
                .Where(plan => plan.IsQueueable)
                .ToList();
            if (planned.Count == 0)
            {
                state?.Sig.ShowErrorMessage.Send(
                    "No selected hostile Being currently has an eligible " +
                    "Enemy Conversion route."
                );
                return false;
            }

            var skipped = evaluations
                .Where(plan => !plan.IsQueueable)
                .ToList();
            var detail = BuildConfirmationText(planned, skipped);
            WPopup.Create(
                    isSystem: true,
                    IconId.CRobotEyeGreen,
                    planned.Count == 1
                        ? "Confirm Enemy Conversion?"
                        : $"Confirm {planned.Count} Enemy Conversions?",
                    "CARAU Enemy Conversion",
                    detail
                )
                .WithAction2("Cancel", null)
                .WithAction(
                    planned.Count == 1 ? "Convert" : "Convert All",
                    () => ConfirmExact(
                        state,
                        requested,
                        planned,
                        onCompleted
                    )
                )
                .Spawn(UIPoolRoot.SystemPopupContainer);
            return true;
        }

        private static void ConfirmExact(
          GameState state,
          List<Being> requested,
          List<EnemyConversionPlan> confirmed,
          Action onCompleted
        )
        {
            var current = EnemyConversionService.BuildPlans(state, requested);
            if (!SameMappings(confirmed, current))
            {
                state.Sig.ShowErrorMessage.Send(
                    "Enemy Conversion mapping changed before queueing. " +
                    "Nothing was queued; press Convert again to review the " +
                    "new exact plan."
                );
                RefreshUiNextFrame(state, onCompleted);
                return;
            }

            var queued = 0;
            var failed = 0;
            string lastReason = null;
            using (CARAUOperationTrackingSys.BeginBulkQueueTracking())
            {
                foreach (var plan in confirmed)
                {
                    var result = EnemyConversionService.TryQueueExact(
                        state,
                        plan
                    );
                    if (result.Success)
                    {
                        queued++;
                    }
                    else
                    {
                        failed++;
                        lastReason = result.Message;
                    }
                }
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Queue,
                $"Enemy Conversion confirmation processed | " +
                $"Confirmed={confirmed.Count} | Queued={queued} | " +
                $"Failed={failed}"
            );
            if (failed > 0)
            {
                state.Sig.ShowErrorMessage.Send(
                    $"Enemy Conversion queued: {queued}; failed: {failed}. " +
                    (lastReason ?? "An exact confirmed mapping became unavailable.")
                );
            }

            RefreshUiNextFrame(state, onCompleted);
        }

        private static void RefreshUiNextFrame(
          GameState state,
          Action onCompleted
        )
        {
            Coroutines.OnNextFrame(() => {
                state?.UIQuery.RebuildEntityActions.Ask();
                onCompleted?.Invoke();
            });
        }

        private static bool SameMappings(
          IReadOnlyList<EnemyConversionPlan> left,
          IReadOnlyList<EnemyConversionPlan> right
        )
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }
            for (var index = 0; index < left.Count; index++)
            {
                if (!left[index].SameMappingAs(right[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static string BuildConfirmationText(
          IReadOnlyList<EnemyConversionPlan> plans,
          IReadOnlyList<EnemyConversionPlan> skipped
        )
        {
            var text = new StringBuilder();
            text.AppendLine(
                "CARAU will queue only the exact mappings below. If any " +
                "mapping changes before confirmation, nothing is queued. " +
                "After queueing CARAU will not retry, switch routes, or " +
                "substitute another part."
            );
            text.AppendLine();
            for (var index = 0; index < plans.Count; index++)
            {
                var plan = plans[index];
                text.Append(index + 1)
                    .Append(". ")
                    .Append(plan.Target?.Name ?? "Unknown enemy")
                    .Append(" — ")
                    .Append(plan.RouteName)
                    .Append(" — ")
                    .Append(plan.ReservedPart?.BodyPart?.NameT ?? "Unknown part")
                    .Append(" #")
                    .Append(plan.ReservedPartId);

                if (plan.Route == EnemyConversionRoute.CoreSwap)
                {
                    text.Append(" | outgoing mind #")
                        .Append(plan.OriginalMindId)
                        .Append(" -> extracted Core; incoming ")
                        .Append(plan.IncomingMindId == 0
                            ? "new Player mind"
                            : $"mind #{plan.IncomingMindId}");
                }
                else
                {
                    text.Append(" | preserves mind #")
                        .Append(plan.OriginalMindId);
                }
                text.AppendLine();
            }

            if (skipped?.Count > 0)
            {
                text.AppendLine()
                    .Append("Excluded selections:")
                    .AppendLine();
                foreach (var plan in skipped)
                {
                    text.Append("• ")
                        .Append(plan.Target?.Name ?? "Unknown Being")
                        .Append(": ")
                        .Append(plan.Reason ?? "Not eligible")
                        .AppendLine();
                }
            }
            return text.ToString();
        }
    }
}
