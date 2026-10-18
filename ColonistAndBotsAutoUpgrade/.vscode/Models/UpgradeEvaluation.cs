using Game.Systems.Creatures.Surgeries;
using Game.Systems.Items;
using System.Collections.Generic;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public enum UpgradeDisposition
    {
        Queueable,
        NoUpgradeNeeded,
        BlockedByPolicy,
        BlockedByLiveState,
        BlockedByExistingWork,
        Unsupported
    }

    public enum UpgradeReasonCode
    {
        Queueable,
        NoCompatibleDefinition,
        NoMatchingInventoryObject,
        QualityThresholdMet,
        InsufficientQualityImprovement,
        BestTierInstalled,
        UpgradePoliciesDisabled,
        TierUpgradesDisabled,
        QualityUpgradesDisabled,
        SlotDisabled,
        SlotExcludedByPolicy,
        AllocatedEarlierInBatch,
        RetryPolicyDisallows,
        RetryLimitReached,
        RetryChanceBelowThreshold,
        MentalBreakdown,
        MatchingObjectsUnavailable,
        HostileMindExcluded,
        UnsupportedObjectState,
        ExistingCARAUOperation,
        ExistingExternalOperation,
        ReservedByCARAUSurgery,
        ReservedBySurgery,
        ReservedByActiveGoal,
        DeadBiologicalBeing,
        DeadBeingMissingInstallUnsupported,
        DeadBeingRetryUnsupported,
        SlotDestroyed,
        InstalledPartNotRemovable,
        ParentMissing,
        ParentDestroyed,
        ParentUnusable,
        BeingUnavailable,
        SlotUnavailable,
        SlotNotMissing,
        InstalledPartNotBad,
        PreferredReplacementChanged,
        StaleOwnership,
        ReservationInconsistent,
        NativeReservationFailed,
        NativeRegistrationRejected,
        RequiredFacilityUnavailable
    }

    public enum UpgradeEvaluationPurpose
    {
        Main,
        BatchPlan,
        Confirmation,
        ManualRetry,
        AutomaticRetry
    }

    public enum ReservationOwnerKind
    {
        Surgery,
        CARAUSurgery,
        ActiveGoal,
        Inconsistent
    }

    public enum ReservationMismatchKind
    {
        None,
        DuplicateRegisteredOperations,
        RegisteredOperationMissingReservation,
        RegisteredOperationOwnedByDifferentSurgery,
        RegisteredOperationGoalMissingSurgeryLink,
        RegisteredOperationGoalReferencesDifferentSurgery,
        InactiveGoalPendingCleanup,
        ReservationReferencesUnregisteredSurgery,
        BlankReservationMarker,
        UnresolvedLoadReservation,
        SurgeryGoalWithoutRegisteredOperation,
        ReservationWithoutOwner
    }

    public sealed class ReservationExplanation
    {
        public ReservationOwnerKind OwnerKind;
        public ReservationMismatchKind MismatchKind;
        public uint PartObjectId;
        public string PartName;
        public string BeingName;
        public string SlotName;
        public string GoalName;
        public string AgentName;
        public SurgicalOpType? OperationType;
        public SurgicalOpState? OperationState;
        public int RegisteredSurgeryId;
        public int ReservationSurgeryId;
        public int GoalSurgeryId;
        public string LoadGoalId;
        public string LoadSurgeryId;
        public bool GoalIsActive;
        public string GoalState;
    }

    public sealed class UpgradeInventorySummary
    {
        public int TotalMatchingObjects;
        public int EligibleMatchingObjects;
        public int AvailableObjects;
        public int AllocatedEarlierInBatch;
        public int ReservedBySurgery;
        public int ReservedByCARAU;
        public int ReservedByActiveGoal;
        public int HostileMindExcluded;
        public int UnsupportedState;
        public int UnavailableObjects;
        public int ReservationInconsistencies;
        public int BelowMinimumQuality;

        public readonly List<ReservationExplanation> ReservationExamples =
          new List<ReservationExplanation>();

        public void Merge(UpgradeInventorySummary other)
        {
            if (other == null)
            {
                return;
            }

            TotalMatchingObjects += other.TotalMatchingObjects;
            EligibleMatchingObjects += other.EligibleMatchingObjects;
            AvailableObjects += other.AvailableObjects;
            AllocatedEarlierInBatch += other.AllocatedEarlierInBatch;
            ReservedBySurgery += other.ReservedBySurgery;
            ReservedByCARAU += other.ReservedByCARAU;
            ReservedByActiveGoal += other.ReservedByActiveGoal;
            HostileMindExcluded += other.HostileMindExcluded;
            UnsupportedState += other.UnsupportedState;
            UnavailableObjects += other.UnavailableObjects;
            ReservationInconsistencies +=
              other.ReservationInconsistencies;
            BelowMinimumQuality += other.BelowMinimumQuality;

            for (var index = 0;
                 index < other.ReservationExamples.Count &&
                 ReservationExamples.Count < 3;
                 index++)
            {
                ReservationExamples.Add(
                    other.ReservationExamples[index]
                );
            }
        }
    }

    public sealed class InventoryCandidateReport
    {
        public readonly List<ObjData> AvailableParts =
          new List<ObjData>();
        public UpgradeInventorySummary Summary =
          new UpgradeInventorySummary();
    }

    public sealed class UpgradeEvaluationContext
    {
        public UpgradeEvaluationPurpose Purpose;
        public UpgradeInventorySummary Inventory =
          new UpgradeInventorySummary();
        public SurgicalOperation ExistingOperation;
        public string Detail;
        public string NativeReason;
        public uint ExpectedBeingId;
        public string ExpectedSlotId;
        public uint ExpectedPartId;
        public uint ActualPartId;
    }

    public sealed class UpgradeEvaluation
    {
        public UpgradeDisposition Disposition;
        public UpgradeReasonCode PrimaryReason;
        public UpgradeDecision Decision;
        public UpgradeEvaluationContext Context =
          new UpgradeEvaluationContext();

        public bool IsQueueable =>
          Disposition == UpgradeDisposition.Queueable;
    }

    public enum QueueAttemptFailureCode
    {
        None,
        EvaluationNotQueueable,
        ReplacementUnavailable,
        NativeReservationFailed,
        RequiredFacilityUnavailable,
        NativeRegistrationRejected
    }

    public sealed class QueueAttemptResult
    {
        public bool Success;
        public QueueAttemptFailureCode FailureCode;
        public UpgradeEvaluation Evaluation;
        public SurgicalOperation Operation;
        public string Message;
    }
}
