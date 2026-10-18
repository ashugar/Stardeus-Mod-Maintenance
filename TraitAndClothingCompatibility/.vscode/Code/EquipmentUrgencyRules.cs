namespace TraitAndClothingCompatibility {
    public readonly struct EquipmentUrgencyDecision {
        public readonly bool Adjust;
        public readonly byte GoalPriority;
        public readonly byte InterruptPriority;

        public EquipmentUrgencyDecision(bool adjust, byte goalPriority, byte interruptPriority) {
            Adjust = adjust;
            GoalPriority = goalPriority;
            InterruptPriority = interruptPriority;
        }
    }

    public static class EquipmentUrgencyRules {
        public static EquipmentUrgencyDecision Decide(bool managed, bool hasEmptyAutomaticWornSlot,
                bool isNaked, bool hasAvailableCandidate) {
            if (!managed || !hasEmptyAutomaticWornSlot)
                return new EquipmentUrgencyDecision(false, 0, 0);

            // Empty worn slots should be the next personal task. Only a completely naked being
            // with an item already available may interrupt ordinary work.
            return new EquipmentUrgencyDecision(true, 5,
                (byte)(isNaked && hasAvailableCandidate ? 2 : 1));
        }
    }
}
