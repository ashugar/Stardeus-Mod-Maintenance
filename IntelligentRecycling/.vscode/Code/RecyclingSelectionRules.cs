using System;

namespace Game.Systems.IntelligentRecycling {
    public enum IntelligentRecyclingEquipmentOrigin {
        NotApplicable = 0,
        Untracked = 1,
        New = 2,
        SecondHand = 3,
        DeadMans = 4,
        Unknown = 5
    }

    internal static class RecyclingSelectionRules {
        internal const string AlienEggDefinitionId = "Obj/Eggs/Egg01";

        internal static bool IsAlienEgg(string definitionId) =>
            string.Equals(definitionId, AlienEggDefinitionId, StringComparison.Ordinal);

        internal static bool AllowsOrigin(IntelligentRecyclingEquipmentOrigin origin,
                bool allowSecondHand, bool allowDeadMans) {
            switch (origin) {
                case IntelligentRecyclingEquipmentOrigin.SecondHand:
                    return allowSecondHand;
                case IntelligentRecyclingEquipmentOrigin.DeadMans:
                    return allowDeadMans;
                case IntelligentRecyclingEquipmentOrigin.New:
                case IntelligentRecyclingEquipmentOrigin.Untracked:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool AllowsWeapon(bool weapon, bool filter, bool selected) =>
            !weapon || !filter || selected;

        internal static bool BypassesWeaponQuality(bool weapon, bool filter,
                bool selected, bool followsQuality) =>
            weapon && filter && selected && !followsQuality;
    }
}
