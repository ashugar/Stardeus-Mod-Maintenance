using Game.Systems.BodyParts;
using Game.Systems.Items;


namespace ColonistAndBotsAutoUpgrade.Models
{
    public enum UpgradeActionKind
    {
        Upgrade = 0,
        InstallMissing = 1,
        ReplaceBadParts = 2
    }

    public enum UpgradeDecisionType
    {
        None,
        Missing,
        BadPartReplacement,
        TierUpgrade,
        QualityUpgrade,
        QualityThresholdMet,
        BestInstalled,
        NothingBetterFound
    }

    public sealed class UpgradeDecision
    {
        public UpgradeActionKind ActionKind;

        public UpgradeDecisionType Type;

        public UpgradeSlot Slot;

        // Installed
        public BodyPartDef InstalledPart;
        public float InstalledQuality;
        public int InstalledTier;

        // Recommendation
        public BodyPartDef TargetPart;
        public ObjData InventoryPart;
        public int InventoryCount;
        public float AvailableQuality;

        // Runtime-only canonical queueability and explanation result. This is
        // deliberately not persisted; every consumer re-evaluates live state.
        public UpgradeEvaluation Evaluation;

        // Convenience
        public bool IsAvailable => InventoryPart != null;
    }
}
