using System;

namespace Game.Systems.BotAutoHack {
    /// <summary>
    /// Publicly documented conversion policy. Core's former harmless-failure
    /// branch becomes a conversion; Core still owns the remaining curve roll.
    /// </summary>
    internal static class HackConversionFormula {
        internal const float PromotedBranchChance = 0.30f;
        internal const float Day0CoreCurveChance = 0f;
        internal const float Day1CoreCurveChance = 0.05f;
        internal const float Day3CoreCurveChance = 0.20f;
        internal const float Day7CoreCurveChance = 0.35f;

        internal static float ConversionChance(float coreCurveChance) {
            ValidateChance(coreCurveChance);
            return PromotedBranchChance
                + (1f - PromotedBranchChance) * coreCurveChance;
        }

        internal static float BurnoutChance(float coreCurveChance) {
            ValidateChance(coreCurveChance);
            return (1f - PromotedBranchChance) * (1f - coreCurveChance);
        }

        private static void ValidateChance(float chance) {
            if (chance < 0f || chance > 1f) {
                throw new ArgumentOutOfRangeException(
                    nameof(chance), chance, "Chance must be between zero and one.");
            }
        }
    }
}
