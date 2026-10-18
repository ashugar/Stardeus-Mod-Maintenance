using System;
using System.Collections.Generic;

namespace TraitAndClothingCompatibility {
    public readonly struct ClothingMoodState {
        public readonly bool DeadMans;
        public readonly bool SecondHand;
        public readonly bool Tattered;

        public ClothingMoodState(bool deadMans, bool secondHand, bool tattered) {
            DeadMans = deadMans;
            SecondHand = !deadMans && secondHand;
            Tattered = tattered;
        }
    }

    public static class ClothingMoodPriority {
        public static int Score(IEnumerable<ClothingMoodState> items, int secondHandMood,
                int deadMansMood, int tatteredMood, int wearerAdjustment) {
            if (items == null) return 0;
            int dead = 0;
            int secondHand = 0;
            int tattered = 0;
            foreach (ClothingMoodState item in items) {
                if (item.DeadMans) dead++;
                else if (item.SecondHand) secondHand++;
                if (item.Tattered) tattered++;
            }
            return CategoryEffect(deadMansMood, dead, wearerAdjustment)
                + CategoryEffect(secondHandMood, secondHand, wearerAdjustment)
                + CategoryEffect(tatteredMood, tattered, wearerAdjustment);
        }

        private static int CategoryEffect(int baseValue, int count, int wearerAdjustment) {
            if (baseValue == 0 || count <= 0) return 0;
            int extra = Math.Min(count - 1, 2);
            int effect = baseValue < 0 ? baseValue - extra : baseValue + extra;
            return effect + wearerAdjustment;
        }
    }
}
