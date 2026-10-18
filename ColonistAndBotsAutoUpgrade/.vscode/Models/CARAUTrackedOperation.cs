using MessagePack;
using Game.Data;
using Game.Systems.BodyParts;
using Game.Systems.Creatures.Surgeries;
using System.Collections.Generic;

namespace ColonistAndBotsAutoUpgrade.Models
{
    public enum CARAUOperationOutcome
    {
        Pending = 0,
        Failed = 1,
        CancelledExternally = 2,
        Invalid = 3,
        Stale = 4
    }

    public enum CARAUFailureKind
    {
        None = 0,
        HubUnavailable = 1,
        BeingTemporarilyUntargetable = 2,
        PreSurgeryTaskFailure = 3,
        ReplacementDestroyedOrConsumed = 4,
        SurgicalRejection = 5,
        SurgicalRecoverableDamage = 6,
        ExternalCancellation = 7,
        StaleOwnership = 8,
        InvalidTarget = 9,
        BeingDied = 10,
        SlotDestroyed = 11,
        InstalledPartWelded = 12,
        Unknown = 13,
        IdentityCoreDestroyed = 14,
        InconsistentNativeReservation = 15,
        CompletedWithoutPlannedReplacement = 16,
        RemoveAllPartsFailed = 17
    }

    [MessagePackObject]
    public sealed class CARAUTrackedOperation
    {
        [Key(0)]
        public uint BeingId { get; set; }

        [Key(1)]
        public string BeingName { get; set; }

        [Key(2)]
        public string SlotName { get; set; }

        [Key(3)]
        public string TargetPartId { get; set; }

        [Key(4)]
        public string TargetPartName { get; set; }

        [Key(5)]
        public uint ReservedPartId { get; set; }

        [Key(6)]
        public long QueuedAtTick { get; set; }

        // Stable internal slot identifier used to reconnect the saved record
        // to the exact BodyPartSlot after loading. Older CARAU saves do not
        // contain this field, so restoration also falls back to SlotName.
        [Key(7)]
        public string SlotId { get; set; }

        [Key(8)]
        public CARAUOperationOutcome Outcome { get; set; }

        [Key(9)]
        public string OutcomeReason { get; set; }

        [Key(10)]
        public long EndedAtTick { get; set; }

        [Key(11)]
        public string InstalledPartId { get; set; }

        [Key(12)]
        public float InstalledQuality { get; set; }

        [Key(13)]
        public float TargetQuality { get; set; }

        [Key(14)]
        public bool HasOutcomeEvidence { get; set; }

        // Number of CARAU-managed requeues performed after Stardeus dropped
        // the operation during the narrow surgery-Hub availability race.
        [Key(15)]
        public int SilentHubRetryCount { get; set; }

        // Persisted so saving while CARAU is waiting for a free Hub does not
        // turn the deferred operation into an external cancellation on load.
        [Key(16)]
        public bool IsAwaitingHubRetry { get; set; }

        // Number of distinct Stardeus task-level Hub failures observed while
        // the surgical operation itself remained registered. This is separate
        // from SilentHubRetryCount, which counts surgeries CARAU requeued.
        [Key(17)]
        public int ObservedNativeHubFailureCount { get; set; }

        [Key(18)]
        public CARAUFailureKind FailureKind { get; set; }

        [Key(19)]
        public int PolicyRetryCount { get; set; }

        [Key(20)]
        public bool IsAwaitingPolicyRetry { get; set; }

        [Key(21)]
        public long NextPolicyRetryTick { get; set; }

        [Key(22)]
        public string PendingRetryReason { get; set; }

        // A single mental-breakdown episode may interrupt several operations
        // for the same Being. Persisting this decision prevents save/load or
        // multiple waiting slots from repeatedly ordering preventative
        // implant surgery for that one episode.
        [Key(23)]
        public bool MoodRegulatorAttempted { get; set; }

        // Preserves the focused Main-tab action through save/load and retry.
        // Older records deserialize to Upgrade, retaining their prior behavior.
        [Key(24)]
        public UpgradeActionKind ActionKind { get; set; }

        // Counts the narrow native Move-to-AutoDoc recovery performed while
        // the matching CARAU surgery is still Pending. This participates in
        // the same configured automatic-attempt budget as CARAU requeues.
        [Key(25)]
        public int PreSurgeryAutoDocRetryCount { get; set; }

        // Prevents a rapidly recurring native carry error from being retried
        // on every 0.2-second tracking refresh. It is save-persistent so a
        // reload cannot reset the retry cadence.
        [Key(26)]
        public long NextPreSurgeryAutoDocRetryTick { get; set; }

        // Remove All Parts is one slotless native mass operation. These
        // appended keys preserve save compatibility with earlier records
        // while giving that operation its own unambiguous identity.
        [Key(27)]
        public bool IsRemoveAllParts { get; set; }

        [Key(28)]
        public int NativeOperationId { get; set; }

        [IgnoreMember]
        public bool IsPending =>
            Outcome == CARAUOperationOutcome.Pending;

        [IgnoreMember]
        public bool IsFailure => !IsPending;

        [IgnoreMember]
        public bool IsAwaitingAutomaticRetry =>
            IsPending && (IsAwaitingHubRetry || IsAwaitingPolicyRetry);

        // Runtime-only reference to the exact slot whose operation CARAU
        // registered. This is deliberately excluded from save data. It lets
        // the tracking system notice a cancellation or completion immediately
        // without rescanning every being in the game.
        [IgnoreMember]
        public BodyPartSlot RuntimeSlot { get; set; }

        // Runtime-only reference used by the tracking window to build the
        // same live BeingSnapshot and slot cards as CARAU's Main tab.
        [IgnoreMember]
        public Being RuntimeBeing { get; set; }

        [IgnoreMember]
        public SurgicalOperation RuntimeOperation { get; set; }

        [IgnoreMember]
        public bool RuntimeCancellationRequestedByCARAU { get; set; }

        // Runtime identity of the last failed native goal prevents the
        // 0.2-second tracker refresh from counting one goal more than once.
        [IgnoreMember]
        public object RuntimeObservedHubFailureGoal { get; set; }

        [IgnoreMember]
        public bool RuntimeNativeHubRecoveryPending { get; set; }

        // A registered pre-surgery operation may lose its exact-part
        // reservation when Core cleans up an unrelated inactive goal. CARAU
        // repairs that native ownership transition at most once so it never
        // repeatedly fights genuinely competing work.
        [IgnoreMember]
        public int RuntimePreSurgeryReservationRepairCount { get; set; }

        // Prevents a terminal Hub-owned delivery goal from being processed
        // more than once while CARAU performs its guarded pre-surgery cleanup.
        [IgnoreMember]
        public bool RuntimeStaleHubGoalRecoveryAttempted { get; set; }

        // Direct result returned by Stardeus's surgery failure resolver.
        // This is essential for automated operations whose surgeon is null:
        // Stardeus writes those outcomes to Player.log but deliberately does
        // not add them to either Being's in-game log.
        [IgnoreMember]
        public string RuntimeNativeSurgicalFailureReason { get; set; }

        [IgnoreMember]
        public CARAUFailureKind RuntimeNativeSurgicalFailureKind { get; set; }

        [IgnoreMember]
        public bool RuntimeNativeSurgicalFailureWasExtraction { get; set; }

        [IgnoreMember]
        public bool RuntimeCoreTransitionAwaitingValidation { get; set; }

        [IgnoreMember]
        public float RuntimeCoreTransitionValidateAfterRealtime { get; set; }

        [IgnoreMember]
        public bool RuntimeInterruptedByCoreIdentityTransition { get; set; }

        [IgnoreMember]
        public float? RuntimeRetryExtractChance { get; set; }

        [IgnoreMember]
        public float? RuntimeRetryInstallChance { get; set; }

        [IgnoreMember]
        public float? RuntimeRetryEffectiveChance { get; set; }

        [IgnoreMember]
        public bool RuntimeAutoDocRetryLimitReported { get; set; }
    }

    [MessagePackObject]
    public sealed class CARAUTrackingData
    {
        [Key(0)]
        public List<CARAUTrackedOperation> Operations { get; set; } = new();

        [Key(1)]
        public int SuccessfulOperations { get; set; }

        [Key(2)]
        public int FailedOperations { get; set; }
    }
}
