using ColonistAndBotsAutoUpgrade.Models;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Systems.Creatures.Surgeries;
using Game.Systems.Items;
using Game.Utils;
using KL.Utils;
using System;
using System.Collections.Generic;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public static class UpgradeAnalyzer
    {
        public static List<BodyPartDef> GetCompatibleParts(BodyPartSlot slot)
        {
            var compatibleParts = new List<BodyPartDef>();

            foreach (var kvp in BodyPartDef.All)
            {
                var partDef = kvp.Value;

                if (partDef == null || slot == null)
                {
                    continue;
                }

                try
                {
                    if (partDef.IsCompatibleWith(slot, out var _))
                    {
                        compatibleParts.Add(partDef);
                    }
                }
                catch (Exception ex)
                {
                    var debugMessage =
                      $"Compatible check failed. Slot={slot?.NameT ?? "null"} | " +
                      $"PartDef={partDef?.Id ?? "null"} | " +
                      $"Error={ex.Message}";

                    CARAUDebugLog.Add(CARAUDebugCategory.Warning, debugMessage);

                    // Unexpected exception; keep this in Player.log.
                    D.Warn(debugMessage);
                }
            }

            compatibleParts.Sort((a, b) => {
                var tierCompare = a.Tier.CompareTo(b.Tier);

                if (tierCompare != 0)
                {
                    return tierCompare;
                }

                return string.Compare(a.NameT, b.NameT, StringComparison.Ordinal);
            });

            return compatibleParts;
        }

        public static UpgradeSlot AnalyzeSlot(BodyPartSlot slot)
        {
            var result = new UpgradeSlot
            {
                Slot = slot,
                InstalledPart = slot?.InstalledDef
            };

            if (slot == null)
            {
                return result;
            }

            result.CompatibleParts.AddRange(GetCompatibleParts(slot));

            if (result.CompatibleParts.Count > 0)
            {
                result.BestPart = result.CompatibleParts[
                    result.CompatibleParts.Count - 1
                ];
            }

            result.NeedsUpgrade =
                result.BestPart != null &&
                result.InstalledPart != null &&
                result.BestPart.Tier > result.InstalledPart.Tier;

            return result;
        }

        public static string GetUpgradeStatusText(GameState gameState, UpgradeSlot upgrade)
        {
            return GetUpgradeStatusText(AnalyzeDecision(gameState, upgrade));
        }

        public static string GetUpgradeStatusText(UpgradeDecision decision)
        {
            if (decision == null)
            {
                return "No upgrade data";
            }

            if (decision.Evaluation != null)
            {
                return UpgradeExplanationFormatter.BuildStatus(
                    decision.Evaluation
                );
            }

            switch (decision.Type)
            {
                case UpgradeDecisionType.Missing:
                    if (decision.TargetPart != null && decision.InventoryPart != null)
                    {
                        return $"Missing → {decision.TargetPart.NameT} (T:{decision.TargetPart.Tier}) | Available:{decision.InventoryCount} | Best Q:{Units.Percentage(decision.AvailableQuality, clamp: false)}";
                    }

                    return "Missing";

                case UpgradeDecisionType.BadPartReplacement:
                    if (decision.TargetPart != null &&
                        decision.InventoryPart != null)
                    {
                        return $"Replace bad part → " +
                          $"{decision.TargetPart.NameT} " +
                          $"(T:{decision.TargetPart.Tier}) | " +
                          $"Available:{decision.InventoryCount} | " +
                          $"Best Q:{Units.Percentage(decision.AvailableQuality, clamp: false)}";
                    }

                    return "Replace bad part";

                case UpgradeDecisionType.TierUpgrade:
                    if (decision.InventoryPart != null &&
                        decision.InventoryPart.BodyPartState != null)
                    {
                        return $"Upgrade → {decision.TargetPart.NameT} (T:{decision.TargetPart.Tier}) | Available:{decision.InventoryCount} | Best Q:{Units.Percentage(decision.AvailableQuality, clamp: false)}";
                    }

                    return $"Upgrade → {decision.TargetPart.NameT} (T:{decision.TargetPart.Tier}) | Available:{decision.InventoryCount}";

                case UpgradeDecisionType.QualityUpgrade:
                    return $"Quality → {decision.TargetPart.NameT} (T:{decision.TargetPart.Tier}) | Current Q:{Units.Percentage(decision.InstalledQuality, clamp: false)} | Best Q:{Units.Percentage(decision.AvailableQuality, clamp: false)}";

                case UpgradeDecisionType.BestInstalled:
                    return "Best Installed";

                case UpgradeDecisionType.NothingBetterFound:
                    return "Nothing Better Found";

                case UpgradeDecisionType.QualityThresholdMet:
                    return $"Within quality threshold | Current Q:{Units.Percentage(decision.InstalledQuality, clamp: false)} | Threshold:{CARAUSettings.QualityThresholdPercent}%";

                default:
                    return "No upgrade data";
            }
        }

        public static bool ShouldHideSlot(BodyPartSlot slot)
        {
            if (IsSlotExcludedByPolicy(slot))
            {
                return true;
            }

            if (!CARAUSettings.IsSlotEnabled(slot.Type.IdH) &&
                !CARAUSettings.ShowDisabledSlots)
            {
                return true;
            }

            return false;
        }

        public static bool ShouldSkipSlot(BodyPartSlot slot)
        {
            if (IsSlotExcludedByPolicy(slot))
            {
                return true;
            }

            if (!CARAUSettings.IsSlotEnabled(slot.Type.IdH))
            {
                return true;
            }

            return false;
        }

        public static bool IsSlotExcludedByPolicy(BodyPartSlot slot)
        {
            if (slot == null || slot.Type == null)
            {
                return true;
            }

            var slotId = slot.Type.Id;

            return
              slotId == "Body" ||
              slotId == "Head" ||
              slotId == "Implant" ||
              slotId == "Brain";
        }

        public static UpgradeDecision AnalyzeDecision(
          GameState gameState,
          UpgradeSlot upgrade,
          bool logHostileMindExclusions = false,
          ISet<uint> excludedPartIds = null,
          uint preferredPartId = 0,
          UpgradeEvaluationPurpose purpose = UpgradeEvaluationPurpose.Main,
          SurgicalOperation ignoredExistingOperation = null,
          UpgradeActionKind actionKind = UpgradeActionKind.Upgrade
        )
        {
            return UpgradeEvaluator.Evaluate(
                gameState,
                upgrade,
                purpose,
                logHostileMindExclusions,
                excludedPartIds,
                preferredPartId,
                ignoredExistingOperation,
                actionKind
            ).Decision;
        }

    }
}
