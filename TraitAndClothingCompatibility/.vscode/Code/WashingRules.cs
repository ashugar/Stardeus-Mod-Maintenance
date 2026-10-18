using Game.Systems.Items;

namespace TraitAndClothingCompatibility {
    internal static class WashingRules {
        internal static ObjFlags WashedFlags(ObjFlags flags) {
            return (flags | ObjFlags.SecondHand) & ~ObjFlags.BelongedToDead;
        }

        internal static float EffectiveProgressPerTick(
                float baseProgressPerTick, float speedMultiplier) {
            return baseProgressPerTick * AtLeastOne(speedMultiplier);
        }

        internal static float EffectiveEnergyCost(
                float baseEnergyCost, float efficiencyMultiplier) {
            return baseEnergyCost / AtLeastOne(efficiencyMultiplier);
        }

        private static float AtLeastOne(float multiplier) {
            return multiplier < 1f ? 1f : multiplier;
        }
    }
}
