using ColonistAndBotsAutoUpgrade.Services;
using Game.Components;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Utils;
using System.Collections.Generic;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public sealed class BeingSnapshot
    {
        public string SelectedType { get; private set; } = "Being";
        public string Name { get; private set; } = "Nothing selected";
        public readonly List<string> DebugLines = new();
        public readonly List<UpgradeSlot> UpgradeSlots = new List<UpgradeSlot>();
        public readonly List<SlotCardData> SlotCards = new();
        public readonly List<UpgradeEvaluation> Evaluations = new();
        private readonly List<SlotCardData> suppressedSlotCards = new();
        public int Id { get; private set; }
        public string Species { get; private set; } = "";
        public string Status { get; private set; } = "";
        public bool IsUnsupportedDeadBiological { get; private set; }
        public UpgradeEvaluation OverallEvaluation { get; private set; }
        public int QueueableUpgrades { get; set; }

        public static BeingSnapshot FromBeing(
          GameState gameState,
          Being being,
          ISet<uint> allocatedPartIds = null
        )
        {
            var snapshot = new BeingSnapshot();

            if (being == null)
            {
                return snapshot;
            }

            snapshot.Id = being.Id;
            snapshot.Species = being.Species?.Name ?? being.Species?.ToString() ?? "Unknown";
            snapshot.Name = being.Data?.Name ?? being.ToString();
            snapshot.AddBodyDebugInfo(
                gameState,
                being,
                allocatedPartIds
            );

            return snapshot;
        }

        private void AddBodyDebugInfo(
          GameState gameState,
          Being being,
          ISet<uint> allocatedPartIds
        )
        {
            var body = being.Data?.Body;

            if (body == null)
            {
                DebugLines.Add("Body: null");
                return;
            }

            Status = GetBodyStatus(body);

            var species = being.Species;

            if (!body.IsAlive && species?.IsBiological == true)
            {
                IsUnsupportedDeadBiological = true;
                OverallEvaluation = new UpgradeEvaluation
                {
                    Disposition = UpgradeDisposition.Unsupported,
                    PrimaryReason =
                        UpgradeReasonCode.DeadBiologicalBeing,
                    Context = new UpgradeEvaluationContext
                    {
                        Purpose = allocatedPartIds == null
                            ? UpgradeEvaluationPurpose.Main
                            : UpgradeEvaluationPurpose.BatchPlan,
                        ExpectedBeingId = being.Id < 0
                            ? 0u
                            : (uint)being.Id
                    }
                };
                Evaluations.Add(OverallEvaluation);

                if (CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Decision))
                {
                    var debugMessage =
                        $"Dead biological being skipped | " +
                        $"Being={being.Data?.Name ?? "unknown"} | " +
                        $"Species={species.Name ?? "unknown"} | " +
                        "Reason=Automated operations are not supported";

                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Decision,
                        debugMessage
                    );
                }

                return;
            }

            if (!body.IsAlive &&
                CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Decision))
            {
                var debugMessage =
                    $"Dead-being analysis started | " +
                    $"Being={being.Data?.Name ?? "unknown"} | " +
                    $"Species={species?.Name ?? "unknown"} | " +
                    $"Biological={species?.IsBiological.ToString() ?? "unknown"} | " +
                    $"Slots={body.Slots?.Length ?? 0}";

                CARAUDebugLog.Add(
                    CARAUDebugCategory.Decision,
                    debugMessage
                );
            }

            if (body.Slots == null)
            {
                return;
            }

            for (int i = 0; i < body.Slots.Length; i++)
            {
                var slot = body.Slots[i];

                if (IsInstalledWeldedSlot(slot))
                {
                    AddWeldedSlotCard(gameState, slot, i);
                    continue;
                }

                AddSlotDebugInfo(
                    gameState,
                    slot,
                    i,
                    allocatedPartIds
                );
            }

            AddPendingSlotCounterparts();
        }

        private void AddWeldedSlotCard(
          GameState gameState,
          BodyPartSlot slot,
          int displayOrder
        )
        {
            if (!IsInstalledWeldedSlot(slot))
            {
                return;
            }

            for (int cardIndex = 0;
                 cardIndex < SlotCards.Count;
                 cardIndex++)
            {
                if (object.ReferenceEquals(
                      SlotCards[cardIndex].Slot,
                      slot
                    ))
                {
                    return;
                }
            }

            var displayUpgrade = new UpgradeSlot
            {
                Slot = slot,
                InstalledPart = slot.InstalledDef
            };
            var displayCard = CreateSlotCard(
              slot,
              displayUpgrade,
              displayOrder
            );
            displayCard.IsWeldedDisplayOnly = true;
            displayCard.Evaluation = UpgradeEvaluator.Evaluate(
                gameState,
                displayUpgrade
            );
            displayCard.Decision = displayCard.Evaluation.Decision;
            displayCard.PendingOperation =
                displayCard.Evaluation.Context.ExistingOperation;
            displayCard.HasPendingOperation =
                displayCard.PendingOperation != null;
            displayCard.QueueStatus =
                UpgradeExplanationFormatter.BuildQueueStatus(
                    displayCard.Evaluation
                );
            Evaluations.Add(displayCard.Evaluation);
            LogUpgradeEvaluation(displayCard.Evaluation);
            SlotCards.Add(displayCard);
        }

        private static bool IsInstalledWeldedSlot(BodyPartSlot slot)
        {
            return
              slot != null &&
              !slot.IsPartRemoved &&
              slot.InstalledDef != null &&
              slot.State?.IsWelded == true;
        }

        private void AddSlotDebugInfo(
          GameState gameState,
          BodyPartSlot slot,
          int index,
          ISet<uint> allocatedPartIds
        )
        {
            if (slot == null)
            {
                DebugLines.Add($"Slot[{index}]: null");
                return;
            }

            LogDeadBeingRawSlot(slot, index);

            if (UpgradeAnalyzer.IsSlotExcludedByPolicy(slot))
            {
                return;
            }

            var hideSlot = UpgradeAnalyzer.ShouldHideSlot(slot);
            var upgrade = UpgradeAnalyzer.AnalyzeSlot(slot);
            var evaluation = UpgradeEvaluator.Evaluate(
              gameState,
              upgrade,
              allocatedPartIds == null
                ? UpgradeEvaluationPurpose.Main
                : UpgradeEvaluationPurpose.BatchPlan,
              logHostileMindExclusions: true,
              excludedPartIds: allocatedPartIds
            );
            Evaluations.Add(evaluation);
            LogUpgradeEvaluation(evaluation);

            if (hideSlot)
            {
                return;
            }

            var card = CreateSlotCard(slot, upgrade, index);

            card.Decision = evaluation.Decision;
            card.Evaluation = evaluation;
            card.PendingOperation =
              evaluation.Context.ExistingOperation;
            card.HasPendingOperation =
              card.PendingOperation != null;
            card.QueueStatus =
              UpgradeExplanationFormatter.BuildQueueStatus(evaluation);

            if (UpgradeAnalyzer.ShouldSkipSlot(slot))
            {
                card.IsDisabled = true;
                SlotCards.Add(card);
                return;
            }

            UpgradeSlots.Add(upgrade);

            var decision = evaluation.Decision;

            if (SurgeryQueue.IsQueueable(gameState, decision))
            {
                QueueableUpgrades++;
                allocatedPartIds?.Add(decision.InventoryPart.Id);
            }

            if (evaluation.Disposition ==
                    UpgradeDisposition.NoUpgradeNeeded &&
                decision.Type == UpgradeDecisionType.BestInstalled &&
                !card.HasPendingOperation)
            {
                suppressedSlotCards.Add(card);
                return;
            }

            LogDeadBeingQueueDecision(
                gameState,
                slot,
                decision,
                card.QueueStatus
            );

            SlotCards.Add(card);
        }

        private static void LogDeadBeingQueueDecision(
            GameState gameState,
            BodyPartSlot slot,
            UpgradeDecision decision,
            string queueStatus
        )
        {
            var body = slot?.Body;

            if (body == null || body.IsAlive || decision == null)
            {
                return;
            }

            if (!CARAUSettings.IsDebugCategoryEnabled(
                CARAUDebugCategory.Decision))
            {
                return;
            }

            var owner = body.Owner;
            var species = owner?.Species;
            var parent = slot.AttachedToPart;
            var parentState = parent == null
                ? "none"
                : $"{parent.NameT}" +
                  $"/Removed:{parent.IsPartRemoved}" +
                  $"/Destroyed:{parent.IsDestroyed}" +
                  $"/Critical:{parent.HasCriticalDamage}" +
                  $"/UsableSelf:{parent.IsUsableSelf}" +
                  $"/UsableWithParent:{parent.IsUsableWithParent}";

            var debugMessage =
                $"Dead-being decision | " +
                $"Being={owner?.Name ?? "unknown"} | " +
                $"Species={species?.Name ?? "unknown"} | " +
                $"Biological={species?.IsBiological.ToString() ?? "unknown"} | " +
                $"Slot={slot.NameT ?? "unknown"} | " +
                $"Decision={decision.Type} | " +
                $"Disposition={decision.Evaluation?.Disposition.ToString() ?? "legacy"} | " +
                $"Reason={decision.Evaluation?.PrimaryReason.ToString() ?? "legacy"} | " +
                $"InventoryPart={decision.InventoryPart?.Id ?? 0} | " +
                $"InventoryCount={decision.InventoryCount} | " +
                $"Queueable={SurgeryQueue.IsQueueable(gameState, decision)} | " +
                $"QueueStatus={queueStatus ?? "none"} | " +
                $"Removed={slot.IsPartRemoved} | " +
                $"Destroyed={slot.IsDestroyed} | " +
                $"Critical={slot.HasCriticalDamage} | " +
                $"UsableSelf={slot.IsUsableSelf} | " +
                $"UsableWithParent={slot.IsUsableWithParent} | " +
                $"Parent={parentState}";

            CARAUDebugLog.Add(
                CARAUDebugCategory.Decision,
                debugMessage
            );
        }

        private static void LogUpgradeEvaluation(
          UpgradeEvaluation evaluation
        )
        {
            if (evaluation == null ||
                !CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Decision
                ))
            {
                return;
            }

            var decision = evaluation.Decision;
            var summary = evaluation.Context?.Inventory;
            CARAUDebugLog.Add(
                CARAUDebugCategory.Decision,
                $"Slot evaluation | " +
                $"Being={decision?.Slot?.Slot?.Body?.Owner?.Name ?? "unknown"} | " +
                $"Slot={decision?.Slot?.Slot?.NameT ?? "unknown"} | " +
                $"Purpose={evaluation.Context?.Purpose} | " +
                $"Disposition={evaluation.Disposition} | " +
                $"Reason={evaluation.PrimaryReason} | " +
                $"Decision={decision?.Type.ToString() ?? "none"} | " +
                $"ObjId={decision?.InventoryPart?.Id ?? 0} | " +
                $"Matching={summary?.TotalMatchingObjects ?? 0} | " +
                $"Available={summary?.AvailableObjects ?? 0} | " +
                $"CARAUReserved={summary?.ReservedByCARAU ?? 0} | " +
                $"SurgeryReserved={summary?.ReservedBySurgery ?? 0} | " +
                $"GoalReserved={summary?.ReservedByActiveGoal ?? 0} | " +
                $"Inconsistent={summary?.ReservationInconsistencies ?? 0}"
            );
        }

        private static void LogDeadBeingRawSlot(
            BodyPartSlot slot,
            int index
        )
        {
            var body = slot?.Body;

            if (body == null || body.IsAlive)
            {
                return;
            }

            if (!CARAUSettings.IsDebugCategoryEnabled(
                CARAUDebugCategory.Decision))
            {
                return;
            }

            var owner = body.Owner;
            var species = owner?.Species;
            var parent = slot.AttachedToPart;
            var debugMessage =
                $"Dead-being raw slot | " +
                $"Being={owner?.Name ?? "unknown"} | " +
                $"Species={species?.Name ?? "unknown"} | " +
                $"Biological={species?.IsBiological.ToString() ?? "unknown"} | " +
                $"Index={index} | " +
                $"Slot={slot.NameT ?? "unknown"} | " +
                $"Hidden={UpgradeAnalyzer.ShouldHideSlot(slot)} | " +
                $"Skipped={UpgradeAnalyzer.ShouldSkipSlot(slot)} | " +
                $"Removed={slot.IsPartRemoved} | " +
                $"Destroyed={slot.IsDestroyed} | " +
                $"Critical={slot.HasCriticalDamage} | " +
                $"UsableSelf={slot.IsUsableSelf} | " +
                $"UsableWithParent={slot.IsUsableWithParent} | " +
                $"Parent={parent?.NameT ?? "none"}";

            CARAUDebugLog.Add(
                CARAUDebugCategory.Decision,
                debugMessage
            );
        }

        private void AddPendingSlotCounterparts()
        {
            if (suppressedSlotCards.Count == 0)
            {
                return;
            }

            var pendingSlotTypeIds = new HashSet<int>();

            for (int i = 0; i < SlotCards.Count; i++)
            {
                var card = SlotCards[i];

                if (card.HasPendingOperation && card.Slot?.Type != null)
                {
                    pendingSlotTypeIds.Add(card.Slot.Type.IdH);
                }
            }

            for (int i = 0; i < suppressedSlotCards.Count; i++)
            {
                var card = suppressedSlotCards[i];

                if (card.Slot?.Type == null ||
                    !pendingSlotTypeIds.Contains(card.Slot.Type.IdH))
                {
                    continue;
                }

                card.Decision.Type =
                  UpgradeDecisionType.NothingBetterFound;
                card.QueueStatus = "Queue: Not needed";
                SlotCards.Add(card);
            }

            SlotCards.Sort((a, b) =>
              a.DisplayOrder.CompareTo(b.DisplayOrder)
            );
        }

        private static string GetBodyStatus(Body body)
        {
            if (!body.IsAlive)
            {
                return "Dead";
            }

            if (body.IsIncapacitated)
            {
                return "Incapacitated";
            }

            if (body.IsUnhealthy)
            {
                return "Unhealthy";
            }

            if (body.IsInjured)
            {
                return "Injured";
            }

            return "Alive";
        }

        private static SlotCardData CreateSlotCard(
          BodyPartSlot slot,
          UpgradeSlot upgrade,
          int displayOrder
        )
        {
            var parent = slot.AttachedToPart;
            var parentDestroyed = parent?.IsDestroyed == true;
            var parentHasInstalledPart =
                parent?.InstalledDef != null;

            return new SlotCardData
            {
                Slot = slot,
                Upgrade = upgrade,
                SlotName = slot.NameT,
                DisplayOrder = displayOrder,
                InstalledPartName = slot.ItemName,
                InstalledPartIconId = upgrade.InstalledPart?.IconId,
                InstalledTier = upgrade.InstalledTier,
                InstalledQuality = slot.Quality,
                InstalledCondition = slot.State?.Condition ?? 0f,
                HasInstalledCondition = slot.State != null,
                HealthState = GetSlotHealthState(slot),
                HasMissingParent =
                  parent != null &&
                  !parentDestroyed &&
                  !parentHasInstalledPart,
                HasDestroyedParent =
                  parentDestroyed,
                HasUnusableParent =
                  parentHasInstalledPart &&
                  !parentDestroyed &&
                  (
                      parent.HasCriticalDamage ||
                      !parent.IsUsableSelf ||
                      !parent.IsUsableWithParent
                  )
            };
        }

        private static SlotHealthState GetSlotHealthState(BodyPartSlot slot)
        {
            if (slot.IsDestroyed)
            {
                return SlotHealthState.Destroyed;
            }

            if (slot.IsPartRemoved)
            {
                return SlotHealthState.Missing;
            }

            if (!slot.CanRemovePart(false))
            {
                return SlotHealthState.NotRemovable;
            }

            if (slot.HasCriticalDamage)
            {
                return SlotHealthState.Critical;
            }

            if (slot.HasModerateDamage)
            {
                return SlotHealthState.Damaged;
            }

            if (!slot.IsUsableSelf || !slot.IsUsableWithParent)
            {
                return SlotHealthState.Unusable;
            }

            return SlotHealthState.Okay;
        }
    }
}
