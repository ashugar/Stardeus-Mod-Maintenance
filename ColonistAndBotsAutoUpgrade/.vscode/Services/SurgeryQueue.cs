using ColonistAndBotsAutoUpgrade.Models;
using Game;
using Game.Components;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Systems.Creatures.Surgeries;
using System.Collections.Generic;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public static class SurgeryQueue
    {
        public static List<UpgradeDecision> BuildQueue(
            GameState gameState,
            BeingSnapshot snapshot
        )
        {
            var queue = new List<UpgradeDecision>();

            if (gameState == null || snapshot == null)
            {
                return queue;
            }

            using (InventoryService.BeginBatchLookup(gameState))
            {
              foreach (var slot in snapshot.UpgradeSlots)
              {
                if (slot == null)
                {
                    continue;
                }

                var decision = UpgradeAnalyzer.AnalyzeDecision(
                    gameState,
                    slot,
                    purpose: UpgradeEvaluationPurpose.BatchPlan
                );

                if(!IsQueueable(gameState, decision))
                {
                    continue;
                }

                queue.Add(decision);
              }
            }

            return queue;
        }

        public static List<UpgradeDecision> BuildActionQueue(
          GameState gameState,
          Being being,
          UpgradeActionKind actionKind,
          ISet<uint> allocatedPartIds = null,
          UpgradeEvaluationPurpose purpose =
            UpgradeEvaluationPurpose.BatchPlan
        )
        {
            var queue = new List<UpgradeDecision>();
            var slots = being?.Data?.Body?.Slots;

            if (gameState == null || slots == null)
            {
                return queue;
            }

            for (var index = 0; index < slots.Length; index++)
            {
                var slot = slots[index];
                if (!IsActionTarget(slot, actionKind))
                {
                    continue;
                }

                var decision = UpgradeAnalyzer.AnalyzeDecision(
                    gameState,
                    UpgradeAnalyzer.AnalyzeSlot(slot),
                    excludedPartIds: allocatedPartIds,
                    purpose: purpose,
                    actionKind: actionKind
                );
                if (!IsQueueable(gameState, decision))
                {
                    continue;
                }

                queue.Add(decision);
                if (decision.InventoryPart != null)
                {
                    allocatedPartIds?.Add(decision.InventoryPart.Id);
                }
            }

            return queue;
        }

        public static int CountActionTargets(
          Being being,
          UpgradeActionKind actionKind
        )
        {
            var slots = being?.Data?.Body?.Slots;
            if (slots == null)
            {
                return 0;
            }

            var count = 0;
            for (var index = 0; index < slots.Length; index++)
            {
                if (IsActionTarget(slots[index], actionKind))
                {
                    count++;
                }
            }

            return count;
        }

        public static bool IsActionTarget(
          BodyPartSlot slot,
          UpgradeActionKind actionKind
        )
        {
            if (slot == null ||
                UpgradeAnalyzer.IsSlotExcludedByPolicy(slot) ||
                !CARAUSettings.IsSlotEnabled(slot.Type.IdH) ||
                slot.IsDestroyed)
            {
                return false;
            }

            switch (actionKind)
            {
                case UpgradeActionKind.InstallMissing:
                    return
                      slot.Body?.IsAlive == true &&
                      slot.IsPartRemoved;

                case UpgradeActionKind.ReplaceBadParts:
                    return
                      !slot.IsPartRemoved &&
                      slot.State?.IsDestroyed == true &&
                      slot.CanRemovePart(false);

                default:
                    return true;
            }
        }

        public static string GetQueueStatusText(GameState gameState, UpgradeDecision decision)
        {
            if (decision == null)
            {
                return "Queue: Not queueable — no decision";
            }

            if (decision.Evaluation != null)
            {
                return UpgradeExplanationFormatter.BuildQueueStatus(
                    decision.Evaluation
                );
            }

            if (decision.Type != UpgradeDecisionType.TierUpgrade &&
                decision.Type != UpgradeDecisionType.QualityUpgrade &&
                decision.Type != UpgradeDecisionType.Missing &&
                decision.Type != UpgradeDecisionType.BadPartReplacement)
            {
                return "Queue: Not needed";
            }

            if (decision.InventoryPart == null)
            {
                return "Queue: Blocked — no available inventory part";
            }

            var slot = decision.Slot?.Slot;

            if (slot == null)
            {
                return "Queue: Blocked — slot unavailable";
            }

            if (HasMentalBreakdown(slot.Body?.Owner))
            {
                return "Queue: Blocked — Mental Breakdown";
            }

            var allowDeadMechanicalMaintenance =
                IsDeadMechanicalBeing(slot);

            if (gameState.Query.FindSurgicalOperation.Ask(slot, true) != null)
            {
                return "Queue: Pending";
            }

            if (slot.IsDestroyed)
            {
                return "Queue: Blocked — slot destroyed";
            }

            if (!slot.IsPartRemoved && !slot.CanRemovePart(false))
            {
                return "Queue: Blocked — installed part not removable";
            }

            if (decision.Type == UpgradeDecisionType.Missing &&
                slot.IsPartRemoved)
            {
                return IsParentChainUsable(
                    slot,
                    allowDeadMechanicalMaintenance
                )
                    ? "Queue: Ready"
                    : "Queue: Blocked — parent part unusable";
            }

            if (!IsParentChainUsable(
                    slot,
                    allowDeadMechanicalMaintenance
                ))
            {
                return "Queue: Blocked — parent part unusable";
            }

            return "Queue: Ready";
        }

        public static bool HasPendingOperation(GameState gameState, UpgradeDecision decision)
        {
            return FindPendingOperation(gameState, decision) != null;
        }

        public static SurgicalOperation FindPendingOperation(
          GameState gameState,
          UpgradeDecision decision
        )
        {
            var slot = decision?.Slot?.Slot;

            if (gameState == null || slot == null)
            {
                return null;
            }

            return gameState.Query.FindSurgicalOperation.Ask(slot, true);
        }

        public static bool IsQueueable(GameState gameState, UpgradeDecision decision)
        {
            if (gameState == null || decision == null)
            {
                return false;
            }

            if (decision.Evaluation != null)
            {
                return decision.Evaluation.IsQueueable;
            }

            if (decision.Type != UpgradeDecisionType.TierUpgrade &&
                decision.Type != UpgradeDecisionType.QualityUpgrade &&
                decision.Type != UpgradeDecisionType.Missing &&
                decision.Type != UpgradeDecisionType.BadPartReplacement)
            {
                return false;
            }

            if (decision.InventoryPart == null)
            {
                return false;
            }

            var slot = decision.Slot?.Slot;

            if (slot == null)
            {
                return false;
            }

            if (HasMentalBreakdown(slot.Body?.Owner))
            {
                return false;
            }

            if (gameState.Query.FindSurgicalOperation.Ask(slot, true) != null)
            {
                return false;
            }

            if (!IsSlotInstallable(decision))
            {
                return false;
            }

            return true;
        }

        public static bool HasMentalBreakdown(
          Game.Systems.Creatures.BeingData beingData
        )
        {
            if (beingData?.Conditions == null)
            {
                return false;
            }

            foreach (var state in beingData.Conditions.Values)
            {
                if (state?.Condition != null &&
                    !state.IsInactive &&
                    !state.IsDisabled &&
                    string.Equals(
                        state.Condition.Id,
                        "MentalBreakdown",
                        System.StringComparison.Ordinal
                    ))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSlotInstallable(UpgradeDecision decision)
        {
            var slot = decision?.Slot?.Slot;

            if (slot == null)
            {
                return false;
            }

            var allowDeadMechanicalMaintenance =
                IsDeadMechanicalBeing(slot);

            if (slot.IsDestroyed)
            {
                return false;
            }

            if (!slot.IsPartRemoved && !slot.CanRemovePart(false))
            {
                return false;
            }

            // A genuinely missing slot is expected to be unusable by itself.
            // Installing a part is valid as long as the parent chain can
            // support the operation.
            if (decision.Type == UpgradeDecisionType.Missing &&
                slot.IsPartRemoved)
            {
                return IsParentChainUsable(
                    slot,
                    allowDeadMechanicalMaintenance
                );
            }

            // An installed part may itself be critical or unusable and still
            // be a valid replacement target. CanRemovePart above governs the
            // installed part; only the parent chain must support the swap.
            return IsParentChainUsable(
                slot,
                allowDeadMechanicalMaintenance
            );
        }

        private static bool IsParentChainUsable(
            BodyPartSlot slot,
            bool allowCriticalDamage = false
        )
        {
            var parent = slot?.AttachedToPart;

            if (parent == null)
            {
                return true;
            }

            if (parent.IsDestroyed || parent.InstalledDef == null)
            {
                return false;
            }

            if (allowCriticalDamage)
            {
                return true;
            }

            if (parent.HasCriticalDamage)
            {
                return false;
            }

            return parent.IsUsableSelf && parent.IsUsableWithParent;
        }

        private static bool IsDeadMechanicalBeing(BodyPartSlot slot)
        {
            var body = slot?.Body;
            var species = body?.Owner?.Species;

            return body != null &&
                   !body.IsAlive &&
                   species != null &&
                   !species.IsBiological;
        }
    }
}
