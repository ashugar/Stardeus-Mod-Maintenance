namespace TraitAndClothingCompatibility {
    public static class ForbiddenEquipmentRules {
        public static bool ShouldAllow(bool taccEnabled, bool allowForbiddenEquipment,
                bool ownsPolicy, bool optedOut) => taccEnabled && allowForbiddenEquipment
            && ownsPolicy && !optedOut;
    }
}
