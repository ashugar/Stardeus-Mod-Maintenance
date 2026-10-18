using ColonistAndBotsAutoUpgrade.Models;

namespace ColonistAndBotsAutoUpgrade.Services
{
    public sealed class AutomaticRetryChanceResult
    {
        public float? ExtractChance { get; set; }
        public float InstallChance { get; set; }
        public float EffectiveChance { get; set; }

        public string Describe(int thresholdPercent)
        {
            var extract = ExtractChance.HasValue
                ? $"Remove={ExtractChance.Value * 100f:0.0}% | "
                : string.Empty;
            return extract +
                   $"Install={InstallChance * 100f:0.0}% | " +
                   $"Effective={EffectiveChance * 100f:0.0}% | " +
                   $"Required=>{thresholdPercent}%";
        }
    }

    public static class AutomaticRetryChanceService
    {
        public static bool TryEvaluate(
          UpgradeDecision decision,
          out AutomaticRetryChanceResult result,
          out string reason
        )
        {
            result = null;
            reason = null;

            var slot = decision?.Slot?.Slot;
            var replacementState = decision?.InventoryPart?.BodyPartState;
            if (slot == null || replacementState == null)
            {
                reason = "Automatic retry chance unavailable: The current " +
                         "slot or replacement state is unavailable.";
                return false;
            }

            var installChance = slot.EstimateChanceOfSuccess(
                null,
                replacementState,
                isExtract: false,
                isEstimate: true
            );
            float? extractChance = null;
            var effectiveChance = installChance;

            if (!slot.IsPartRemoved)
            {
                if (slot.State == null)
                {
                    reason = "Automatic retry chance unavailable: The " +
                             "installed part state is unavailable.";
                    return false;
                }

                extractChance = slot.EstimateChanceOfSuccess(
                    null,
                    slot.State,
                    isExtract: true,
                    isEstimate: true
                );
                if (extractChance.Value < effectiveChance)
                {
                    effectiveChance = extractChance.Value;
                }
            }

            result = new AutomaticRetryChanceResult
            {
                ExtractChance = extractChance,
                InstallChance = installChance,
                EffectiveChance = effectiveChance
            };
            return true;
        }

        public static bool IsAboveConfiguredThreshold(
          AutomaticRetryChanceResult result
        )
        {
            return result != null &&
                   result.EffectiveChance >
                   CARAUSettings.AutomaticRetryChanceThreshold;
        }
    }
}
