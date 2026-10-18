using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Systems;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Systems.Creatures.Surgeries;
using Game.Systems.Items;
using System.Collections.Generic;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public static class UpgradeEvaluator
    {
        private const float MinimumQualityImprovement = 0.05f;

        public static UpgradeEvaluation Evaluate(
          GameState gameState,
          UpgradeSlot upgrade,
          UpgradeEvaluationPurpose purpose =
            UpgradeEvaluationPurpose.Main,
          bool logHostileMindExclusions = false,
          ISet<uint> excludedPartIds = null,
          uint preferredPartId = 0,
          SurgicalOperation ignoredExistingOperation = null,
          UpgradeActionKind actionKind = UpgradeActionKind.Upgrade
        )
        {
            var decision = CreateDecision(upgrade, actionKind);
            var slot = upgrade?.Slot;

            if (gameState == null)
            {
                return Complete(
                    decision,
                    UpgradeDisposition.Unsupported,
                    UpgradeReasonCode.BeingUnavailable,
                    purpose,
                    detail: "Game state unavailable."
                );
            }

            if (slot == null || slot.Body == null)
            {
                return Complete(
                    decision,
                    UpgradeDisposition.Unsupported,
                    UpgradeReasonCode.SlotUnavailable,
                    purpose,
                    detail: "The target slot is no longer available."
                );
            }

            var existingOperation =
                gameState.Query.FindSurgicalOperation.Ask(slot, true);
            if (existingOperation != null &&
                !ReferenceEquals(
                    existingOperation,
                    ignoredExistingOperation
                ))
            {
                var isCARAU =
                    CARAUOperationTrackingSys.IsOperationTracked(
                        existingOperation
                    );
                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByExistingWork,
                    isCARAU
                        ? UpgradeReasonCode.ExistingCARAUOperation
                        : UpgradeReasonCode.ExistingExternalOperation,
                    purpose,
                    existingOperation: existingOperation
                );
            }

            var body = slot.Body;
            var owner = body.Owner;
            if (!body.IsAlive &&
                actionKind == UpgradeActionKind.InstallMissing)
            {
                return Complete(
                    decision,
                    UpgradeDisposition.Unsupported,
                    UpgradeReasonCode.DeadBeingMissingInstallUnsupported,
                    purpose
                );
            }

            if (!body.IsAlive && owner?.Species?.IsBiological == true)
            {
                return Complete(
                    decision,
                    UpgradeDisposition.Unsupported,
                    UpgradeReasonCode.DeadBiologicalBeing,
                    purpose
                );
            }

            if (slot.IsDestroyed)
            {
                return Complete(
                    decision,
                    UpgradeDisposition.Unsupported,
                    UpgradeReasonCode.SlotDestroyed,
                    purpose
                );
            }

            if (!slot.IsPartRemoved && !slot.CanRemovePart(false))
            {
                return Complete(
                    decision,
                    UpgradeDisposition.Unsupported,
                    UpgradeReasonCode.InstalledPartNotRemovable,
                    purpose
                );
            }

            var parentReason = GetParentBlockReason(slot);
            if (parentReason.HasValue)
            {
                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByLiveState,
                    parentReason.Value,
                    purpose
                );
            }

            if (UpgradeAnalyzer.IsSlotExcludedByPolicy(slot))
            {
                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByPolicy,
                    UpgradeReasonCode.SlotExcludedByPolicy,
                    purpose
                );
            }

            if (!CARAUSettings.IsSlotEnabled(slot.Type.IdH))
            {
                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByPolicy,
                    UpgradeReasonCode.SlotDisabled,
                    purpose
                );
            }

            if (actionKind == UpgradeActionKind.InstallMissing)
            {
                if (!slot.IsPartRemoved)
                {
                    return Complete(
                        decision,
                        UpgradeDisposition.NoUpgradeNeeded,
                        UpgradeReasonCode.SlotNotMissing,
                        purpose
                    );
                }
            }

            if (actionKind == UpgradeActionKind.ReplaceBadParts &&
                (
                    slot.IsPartRemoved ||
                    slot.State == null ||
                    !slot.State.IsDestroyed
                ))
            {
                return Complete(
                    decision,
                    UpgradeDisposition.NoUpgradeNeeded,
                    UpgradeReasonCode.InstalledPartNotBad,
                    purpose
                );
            }

            if (SurgeryQueue.HasMentalBreakdown(owner))
            {
                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByLiveState,
                    UpgradeReasonCode.MentalBreakdown,
                    purpose
                );
            }

            var compatibleParts = upgrade.CompatibleParts;
            if (compatibleParts == null || compatibleParts.Count == 0)
            {
                compatibleParts = UpgradeAnalyzer.GetCompatibleParts(slot);
            }

            if (compatibleParts == null || compatibleParts.Count == 0)
            {
                decision.Type = UpgradeDecisionType.NothingBetterFound;
                return Complete(
                    decision,
                    UpgradeDisposition.Unsupported,
                    UpgradeReasonCode.NoCompatibleDefinition,
                    purpose
                );
            }

            if (actionKind == UpgradeActionKind.ReplaceBadParts)
            {
                return EvaluateBadPartReplacement(
                    gameState,
                    decision,
                    compatibleParts,
                    purpose,
                    logHostileMindExclusions,
                    excludedPartIds,
                    preferredPartId
                );
            }

            var isMissing = upgrade.InstalledPart == null;
            var installedTier = isMissing ? 0 : upgrade.InstalledTier;
            var installedQuality = slot.Quality;
            var bestPossibleTier = compatibleParts[
                compatibleParts.Count - 1
            ].Tier;
            var allSummary = new UpgradeInventorySummary();
            var higherTierSummary = new UpgradeInventorySummary();
            var sameTierSummary = new UpgradeInventorySummary();
            var allowTierUpgrades =
                isMissing || CARAUSettings.AllowTierUpgrades;
            var allowQualityUpgrades =
                isMissing || CARAUSettings.AllowQualityUpgrades;
            var preferredBlockedByTierPolicy = false;
            var preferredBlockedByQualityPolicy = false;

            if (!isMissing &&
                !allowTierUpgrades &&
                !allowQualityUpgrades)
            {
                decision.Type = UpgradeDecisionType.NothingBetterFound;
                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByPolicy,
                    UpgradeReasonCode.UpgradePoliciesDisabled,
                    purpose,
                    detail: "Normal higher-Tier and same-tier Quality " +
                            "upgrades are both disabled."
                );
            }

            for (var index = compatibleParts.Count - 1;
                 index >= 0;
                 index--)
            {
                var candidateDef = compatibleParts[index];
                if (candidateDef == null)
                {
                    continue;
                }

                if (!isMissing && candidateDef.Tier < installedTier)
                {
                    break;
                }

                var isHigherTierCandidate =
                    !isMissing && candidateDef.Tier > installedTier;
                var isSameTierCandidate =
                    !isMissing && candidateDef.Tier == installedTier;
                var isCandidateAllowed =
                    isMissing ||
                    (isHigherTierCandidate && allowTierUpgrades) ||
                    (isSameTierCandidate && allowQualityUpgrades);

                // Normal planning does not inventory-scan a disabled class.
                // A retained retry with an exact planned object is the one
                // exception so its tooltip can identify the policy that now
                // blocks that specific replacement.
                if (!isCandidateAllowed && preferredPartId == 0)
                {
                    continue;
                }

                float? minimumQuality = null;
                if (!isMissing && candidateDef.Tier == installedTier)
                {
                    minimumQuality =
                        installedQuality + MinimumQualityImprovement;
                }

                var report = InventoryService.EvaluateObjParts(
                    gameState,
                    candidateDef,
                    logHostileMindExclusions,
                    excludedPartIds,
                    minimumQuality
                );
                allSummary.Merge(report.Summary);
                if (isHigherTierCandidate)
                {
                    higherTierSummary.Merge(report.Summary);
                }
                else if (isSameTierCandidate)
                {
                    sameTierSummary.Merge(report.Summary);
                }

                var bestPart = SelectPart(
                    report.AvailableParts,
                    preferredPartId
                );

                if (preferredPartId != 0 &&
                    ContainsPart(report.AvailableParts, preferredPartId))
                {
                    if (!isMissing &&
                        candidateDef.Tier > installedTier &&
                        !allowTierUpgrades)
                    {
                        preferredBlockedByTierPolicy = true;
                    }
                    else if (!isMissing &&
                             candidateDef.Tier == installedTier &&
                             !allowQualityUpgrades)
                    {
                        preferredBlockedByQualityPolicy = true;
                    }
                }

                if (isMissing || candidateDef.Tier > installedTier)
                {
                    if (bestPart == null)
                    {
                        continue;
                    }

                    if (!isMissing && !allowTierUpgrades)
                    {
                        continue;
                    }

                    decision.Type = isMissing
                        ? UpgradeDecisionType.Missing
                        : UpgradeDecisionType.TierUpgrade;
                    SetRecommendation(
                        decision,
                        candidateDef,
                        bestPart,
                        report.AvailableParts.Count
                    );
                    return Complete(
                        decision,
                        UpgradeDisposition.Queueable,
                        UpgradeReasonCode.Queueable,
                        purpose,
                        report.Summary
                    );
                }

                if (candidateDef.Tier != installedTier ||
                    bestPart == null ||
                    bestPart.BodyPartState == null ||
                    !allowQualityUpgrades ||
                    installedQuality >= CARAUSettings.QualityThreshold)
                {
                    continue;
                }

                decision.Type = UpgradeDecisionType.QualityUpgrade;
                SetRecommendation(
                    decision,
                    candidateDef,
                    bestPart,
                    report.AvailableParts.Count
                );
                return Complete(
                    decision,
                    UpgradeDisposition.Queueable,
                    UpgradeReasonCode.Queueable,
                    purpose,
                    report.Summary
                );
            }

            if (preferredPartId != 0 && allSummary.AvailableObjects > 0)
            {
                if (preferredBlockedByTierPolicy)
                {
                    decision.Type = UpgradeDecisionType.TierUpgrade;
                    return Complete(
                        decision,
                        UpgradeDisposition.BlockedByPolicy,
                        UpgradeReasonCode.TierUpgradesDisabled,
                        purpose,
                        higherTierSummary,
                        expectedPartId: preferredPartId
                    );
                }

                if (preferredBlockedByQualityPolicy)
                {
                    decision.Type = UpgradeDecisionType.QualityUpgrade;
                    return Complete(
                        decision,
                        UpgradeDisposition.BlockedByPolicy,
                        UpgradeReasonCode.QualityUpgradesDisabled,
                        purpose,
                        sameTierSummary,
                        expectedPartId: preferredPartId
                    );
                }

                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByLiveState,
                    UpgradeReasonCode.PreferredReplacementChanged,
                    purpose,
                    allSummary,
                    expectedPartId: preferredPartId
                );
            }

            if (isMissing)
            {
                decision.Type = UpgradeDecisionType.Missing;
                return CompleteFromInventory(
                    decision,
                    purpose,
                    allSummary
                );
            }

            if (allowTierUpgrades &&
                higherTierSummary.TotalMatchingObjects > 0)
            {
                decision.Type = UpgradeDecisionType.NothingBetterFound;
                return CompleteFromInventory(
                    decision,
                    purpose,
                    higherTierSummary
                );
            }

            if (!allowQualityUpgrades)
            {
                decision.Type = UpgradeDecisionType.QualityUpgrade;
                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByPolicy,
                    UpgradeReasonCode.QualityUpgradesDisabled,
                    purpose,
                    sameTierSummary
                );
            }

            if (allowQualityUpgrades &&
                installedQuality >= CARAUSettings.QualityThreshold)
            {
                decision.Type = UpgradeDecisionType.QualityThresholdMet;
                return Complete(
                    decision,
                    UpgradeDisposition.NoUpgradeNeeded,
                    UpgradeReasonCode.QualityThresholdMet,
                    purpose,
                    sameTierSummary
                );
            }

            if (allowQualityUpgrades &&
                sameTierSummary.TotalMatchingObjects == 0)
            {
                decision.Type = installedTier >= bestPossibleTier
                    ? UpgradeDecisionType.BestInstalled
                    : UpgradeDecisionType.NothingBetterFound;
                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByLiveState,
                    UpgradeReasonCode.NoMatchingInventoryObject,
                    purpose,
                    sameTierSummary
                );
            }

            if (allowQualityUpgrades &&
                sameTierSummary.EligibleMatchingObjects == 0)
            {
                decision.Type = UpgradeDecisionType.BestInstalled;
                return Complete(
                    decision,
                    UpgradeDisposition.NoUpgradeNeeded,
                    UpgradeReasonCode.InsufficientQualityImprovement,
                    purpose,
                    sameTierSummary
                );
            }

            if (allowQualityUpgrades &&
                sameTierSummary.AvailableObjects == 0)
            {
                decision.Type = UpgradeDecisionType.NothingBetterFound;
                return CompleteFromInventory(
                    decision,
                    purpose,
                    sameTierSummary
                );
            }

            decision.Type = UpgradeDecisionType.BestInstalled;
            return Complete(
                decision,
                UpgradeDisposition.NoUpgradeNeeded,
                UpgradeReasonCode.BestTierInstalled,
                purpose,
                sameTierSummary
            );
        }

        public static UpgradeEvaluation WithPolicyBlock(
          UpgradeEvaluation evaluation,
          UpgradeReasonCode reason,
          string detail
        )
        {
            if (evaluation == null)
            {
                return null;
            }

            evaluation.Disposition = UpgradeDisposition.BlockedByPolicy;
            evaluation.PrimaryReason = reason;
            evaluation.Context.Detail = detail;
            return evaluation;
        }

        public static UpgradeEvaluation WithNativeFailure(
          UpgradeEvaluation evaluation,
          UpgradeReasonCode reason,
          string nativeReason
        )
        {
            if (evaluation == null)
            {
                return null;
            }

            evaluation.Disposition = UpgradeDisposition.BlockedByLiveState;
            evaluation.PrimaryReason = reason;
            evaluation.Context.NativeReason = nativeReason;
            return evaluation;
        }

        private static UpgradeDecision CreateDecision(
          UpgradeSlot upgrade,
          UpgradeActionKind actionKind
        )
        {
            return new UpgradeDecision
            {
                ActionKind = actionKind,
                Slot = upgrade,
                Type = UpgradeDecisionType.None,
                InstalledPart = upgrade?.InstalledPart,
                InstalledTier = upgrade?.InstalledTier ?? 0,
                InstalledQuality = upgrade?.Slot?.Quality ?? 0f
            };
        }

        private static UpgradeEvaluation EvaluateBadPartReplacement(
          GameState gameState,
          UpgradeDecision decision,
          List<BodyPartDef> compatibleParts,
          UpgradeEvaluationPurpose purpose,
          bool logHostileMindExclusions,
          ISet<uint> excludedPartIds,
          uint preferredPartId
        )
        {
            var allSummary = new UpgradeInventorySummary();
            decision.Type = UpgradeDecisionType.BadPartReplacement;

            // A bad installed part is an emergency restoration target. Choose
            // CARAU's best available compatible tier, even when that requires
            // a downgrade, and do not apply the normal quality-improvement
            // threshold to a part that is already irreparable.
            for (var index = compatibleParts.Count - 1;
                 index >= 0;
                 index--)
            {
                var candidateDef = compatibleParts[index];
                if (candidateDef == null)
                {
                    continue;
                }

                var report = InventoryService.EvaluateObjParts(
                    gameState,
                    candidateDef,
                    logHostileMindExclusions,
                    excludedPartIds
                );
                allSummary.Merge(report.Summary);

                var bestPart = SelectPart(
                    report.AvailableParts,
                    preferredPartId
                );
                if (bestPart == null)
                {
                    continue;
                }

                SetRecommendation(
                    decision,
                    candidateDef,
                    bestPart,
                    report.AvailableParts.Count
                );
                return Complete(
                    decision,
                    UpgradeDisposition.Queueable,
                    UpgradeReasonCode.Queueable,
                    purpose,
                    report.Summary
                );
            }

            if (preferredPartId != 0 && allSummary.AvailableObjects > 0)
            {
                return Complete(
                    decision,
                    UpgradeDisposition.BlockedByLiveState,
                    UpgradeReasonCode.PreferredReplacementChanged,
                    purpose,
                    allSummary,
                    expectedPartId: preferredPartId
                );
            }

            return CompleteFromInventory(
                decision,
                purpose,
                allSummary
            );
        }

        private static void SetRecommendation(
          UpgradeDecision decision,
          BodyPartDef targetPart,
          ObjData inventoryPart,
          int inventoryCount
        )
        {
            decision.TargetPart = targetPart;
            decision.InventoryPart = inventoryPart;
            decision.InventoryCount = inventoryCount;
            decision.AvailableQuality =
                inventoryPart?.BodyPartState?.Quality ?? 0f;
        }

        private static ObjData SelectPart(
          List<ObjData> availableParts,
          uint preferredPartId
        )
        {
            if (availableParts == null || availableParts.Count == 0)
            {
                return null;
            }

            if (preferredPartId == 0)
            {
                return availableParts[0];
            }

            for (var index = 0; index < availableParts.Count; index++)
            {
                if (availableParts[index].Id == preferredPartId)
                {
                    return availableParts[index];
                }
            }

            return null;
        }

        private static bool ContainsPart(
          List<ObjData> availableParts,
          uint partId
        )
        {
            if (availableParts == null || partId == 0)
            {
                return false;
            }

            for (var index = 0; index < availableParts.Count; index++)
            {
                if (availableParts[index]?.Id == partId)
                {
                    return true;
                }
            }

            return false;
        }

        private static UpgradeReasonCode? GetParentBlockReason(
          BodyPartSlot slot
        )
        {
            var parent = slot?.AttachedToPart;
            if (parent == null)
            {
                return null;
            }

            if (parent.IsDestroyed)
            {
                return UpgradeReasonCode.ParentDestroyed;
            }

            if (parent.InstalledDef == null)
            {
                return UpgradeReasonCode.ParentMissing;
            }

            var body = slot.Body;
            var allowDeadMechanicalMaintenance =
                body != null &&
                !body.IsAlive &&
                body.Owner?.Species?.IsBiological == false;
            if (!allowDeadMechanicalMaintenance &&
                (parent.HasCriticalDamage ||
                 !parent.IsUsableSelf ||
                 !parent.IsUsableWithParent))
            {
                return UpgradeReasonCode.ParentUnusable;
            }

            return null;
        }

        private static UpgradeEvaluation CompleteFromInventory(
          UpgradeDecision decision,
          UpgradeEvaluationPurpose purpose,
          UpgradeInventorySummary summary
        )
        {
            var reason = UpgradeReasonCode.NoMatchingInventoryObject;
            var disposition = UpgradeDisposition.BlockedByLiveState;

            if (summary.ReservationInconsistencies > 0)
            {
                reason = UpgradeReasonCode.ReservationInconsistent;
                disposition = UpgradeDisposition.BlockedByExistingWork;
            }
            else if (summary.ReservedByCARAU > 0)
            {
                reason = UpgradeReasonCode.ReservedByCARAUSurgery;
                disposition = UpgradeDisposition.BlockedByExistingWork;
            }
            else if (summary.ReservedBySurgery > 0)
            {
                reason = UpgradeReasonCode.ReservedBySurgery;
                disposition = UpgradeDisposition.BlockedByExistingWork;
            }
            else if (summary.ReservedByActiveGoal > 0)
            {
                reason = UpgradeReasonCode.ReservedByActiveGoal;
                disposition = UpgradeDisposition.BlockedByExistingWork;
            }
            else if (summary.AllocatedEarlierInBatch > 0)
            {
                reason = UpgradeReasonCode.AllocatedEarlierInBatch;
                disposition = UpgradeDisposition.BlockedByPolicy;
            }
            else if (summary.HostileMindExcluded > 0)
            {
                reason = UpgradeReasonCode.HostileMindExcluded;
                disposition = UpgradeDisposition.BlockedByPolicy;
            }
            else if (summary.UnsupportedState > 0)
            {
                reason = UpgradeReasonCode.UnsupportedObjectState;
            }
            else if (summary.UnavailableObjects > 0)
            {
                reason = UpgradeReasonCode.MatchingObjectsUnavailable;
            }

            return Complete(
                decision,
                disposition,
                reason,
                purpose,
                summary
            );
        }

        private static UpgradeEvaluation Complete(
          UpgradeDecision decision,
          UpgradeDisposition disposition,
          UpgradeReasonCode reason,
          UpgradeEvaluationPurpose purpose,
          UpgradeInventorySummary summary = null,
          string detail = null,
          SurgicalOperation existingOperation = null,
          uint expectedPartId = 0
        )
        {
            var evaluation = new UpgradeEvaluation
            {
                Disposition = disposition,
                PrimaryReason = reason,
                Decision = decision,
                Context = new UpgradeEvaluationContext
                {
                    Purpose = purpose,
                    Inventory = summary ?? new UpgradeInventorySummary(),
                    Detail = detail,
                    ExistingOperation = existingOperation,
                    ExpectedBeingId =
                        decision?.Slot?.Slot?.Body?.Owner?.Id ?? 0,
                    ExpectedSlotId =
                        decision?.Slot?.Slot?.Id ?? string.Empty,
                    ExpectedPartId = expectedPartId
                }
            };

            if (decision != null)
            {
                decision.Evaluation = evaluation;
            }

            return evaluation;
        }
    }
}
