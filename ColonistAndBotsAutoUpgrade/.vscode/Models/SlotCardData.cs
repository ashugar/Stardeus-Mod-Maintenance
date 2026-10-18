using Game.Systems.BodyParts;
using Game.Systems.Creatures.Surgeries;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public enum SlotHealthState
    {
        Okay,
        Missing,
        Destroyed,
        Critical,
        Damaged,
        NotRemovable,
        Unusable
    }

    public sealed class SlotCardData
    {
        public BodyPartSlot Slot { get; set; }
        public UpgradeSlot Upgrade { get; set; }
        public UpgradeDecision Decision { get; set; }
        public UpgradeEvaluation Evaluation { get; set; }

        public string SlotName { get; set; }
        public int DisplayOrder { get; set; }
        public string InstalledPartName { get; set; }
        public string InstalledPartIconId { get; set; }
        public int InstalledTier { get; set; }
        public float InstalledQuality { get; set; }
        public float InstalledCondition { get; set; }
        public bool HasInstalledCondition { get; set; }

        public SlotHealthState HealthState { get; set; }
        public bool HasMissingParent { get; set; }
        public bool HasDestroyedParent { get; set; }
        public bool HasUnusableParent { get; set; }
        public bool IsDisabled { get; set; }
        public bool IsWeldedDisplayOnly { get; set; }
        public bool HasPendingOperation { get; set; }
        public SurgicalOperation PendingOperation { get; set; }
        public string QueueStatus { get; set; }
        public bool IsMassOperation { get; set; }
    }
}
