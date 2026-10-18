using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Game.Systems.IntelligentRecycling {
    [Flags]
    public enum IntelligentRecyclingApiCapabilities : long {
        None = 0,
        Settings = 1L << 0,
        CategoryCatalog = 1L << 1,
        ProcessorThresholds = 1L << 2,
        ScanState = 1L << 3,
        ScanStatistics = 1L << 4,
        AccumulatedTotals = 1L << 5,
        TrackedTasks = 1L << 6,
        PerObjectDispositions = 1L << 7,
        SpecialTargets = 1L << 8,
        UiShortcutStatus = 1L << 9,
        ExtensibleDiagnostics = 1L << 10,
        All = (1L << 11) - 1
    }

    public enum IntelligentRecyclingTargetType {
        Unsupported = 0,
        BiologicalBodyPart = 1,
        RoboticBodyPart = 2,
        Equipment = 3,
        Hullpiercer = 4,
        StorageCapsule = 5,
        ArtilleryFungus = 6,
        DeadCrawler = 7,
        StasisPod = 8,
        BreachCapsulePort = 9,
        WholeBeing = 10,
        SafeBox = 11,
        LostShuttle = 12
    }

    public enum IntelligentRecyclingProcessorFamily {
        None = 0,
        Biological = 1,
        Robotic = 2
    }

    public enum IntelligentRecyclingCategoryKind {
        BodyPart = 1,
        Equipment = 2
    }

    public enum IntelligentRecyclingOwnership {
        Unknown = 0,
        IntelligentRecycling = 1,
        ReleasedForExternalHauling = 2,
        PermanentlyProtected = 3,
        NotManaged = 4
    }

    public enum IntelligentRecyclingQualityRelation {
        NotApplicable = 0,
        BelowThreshold = 1,
        EqualToThreshold = 2,
        AboveThreshold = 3,
        NoThreshold = 4
    }

    public enum IntelligentRecyclingDecisionReason {
        Unknown = 0,
        OwnedBelowThreshold = 1,
        OwnedEqualThreshold = 2,
        ReleasedAboveThreshold = 3,
        PermanentlyProtected = 4,
        SpecialTargetReserved = 5,
        MasterDisabled = 6,
        ParentDisabled = 7,
        CategoryDisabled = 8,
        NoCompatibleProcessor = 9,
        UnsupportedTarget = 10,
        NotQualityBearing = 11,
        NotActiveLoose = 12,
        InvalidObject = 13,
        SpecialConditionNotReady = 14,
        WholeBeingNotSupported = 15
    }

    [Flags]
    public enum IntelligentRecyclingBlockers : long {
        None = 0,
        Forbidden = 1L << 0,
        ExistingGoal = 1L << 1,
        WorkerAssigned = 1L << 2,
        Stored = 1L << 3,
        Carried = 1L << 4,
        Inactive = 1L << 5,
        NoProcessor = 1L << 6,
        MasterDisabled = 1L << 7,
        ParentDisabled = 1L << 8,
        CategoryDisabled = 1L << 9,
        PermanentlyProtected = 1L << 10,
        SpecialCondition = 1L << 11,
        Unsupported = 1L << 12,
        VanillaOrderRejected = 1L << 13,
        Invalid = 1L << 14,
        NotStorable = 1L << 15,
        InDevice = 1L << 16,
        Reserved = 1L << 17
    }

    public enum IntelligentRecyclingTrackedTargetKind {
        Object = 1,
        Being = 2,
        StasisPod = 3,
        BreachCapsulePort = 4,
        LostShuttle = 5
    }

    internal static class IntelligentRecyclingApiIds {
        internal static string TargetType(IntelligentRecyclingTargetType value) {
            switch (value) {
                case IntelligentRecyclingTargetType.BiologicalBodyPart:
                    return "biological-body-part";
                case IntelligentRecyclingTargetType.RoboticBodyPart:
                    return "robotic-body-part";
                case IntelligentRecyclingTargetType.Equipment:
                    return "equipment";
                case IntelligentRecyclingTargetType.Hullpiercer:
                    return "hullpiercer";
                case IntelligentRecyclingTargetType.StorageCapsule:
                    return "storage-capsule";
                case IntelligentRecyclingTargetType.ArtilleryFungus:
                    return "artillery-fungus";
                case IntelligentRecyclingTargetType.DeadCrawler:
                    return "dead-crawler";
                case IntelligentRecyclingTargetType.StasisPod:
                    return "stasis-pod";
                case IntelligentRecyclingTargetType.BreachCapsulePort:
                    return "breach-capsule-port";
                case IntelligentRecyclingTargetType.WholeBeing:
                    return "whole-being";
                case IntelligentRecyclingTargetType.SafeBox:
                    return "safe-box";
                case IntelligentRecyclingTargetType.LostShuttle:
                    return "lost-shuttle";
                default:
                    return "unsupported";
            }
        }

        internal static string ProcessorFamily(
                IntelligentRecyclingProcessorFamily value) {
            switch (value) {
                case IntelligentRecyclingProcessorFamily.Biological:
                    return "biological";
                case IntelligentRecyclingProcessorFamily.Robotic:
                    return "robotic";
                default:
                    return "none";
            }
        }

        internal static string Ownership(IntelligentRecyclingOwnership value) {
            switch (value) {
                case IntelligentRecyclingOwnership.IntelligentRecycling:
                    return "intelligent-recycling";
                case IntelligentRecyclingOwnership.ReleasedForExternalHauling:
                    return "released-for-external-hauling";
                case IntelligentRecyclingOwnership.PermanentlyProtected:
                    return "permanently-protected";
                case IntelligentRecyclingOwnership.NotManaged:
                    return "not-managed";
                default:
                    return "unknown";
            }
        }

        internal static string QualityRelation(
                IntelligentRecyclingQualityRelation value) {
            switch (value) {
                case IntelligentRecyclingQualityRelation.BelowThreshold:
                    return "below-threshold";
                case IntelligentRecyclingQualityRelation.EqualToThreshold:
                    return "equal-to-threshold";
                case IntelligentRecyclingQualityRelation.AboveThreshold:
                    return "above-threshold";
                case IntelligentRecyclingQualityRelation.NoThreshold:
                    return "no-threshold";
                default:
                    return "not-applicable";
            }
        }

        internal static string DecisionReason(
                IntelligentRecyclingDecisionReason value) {
            switch (value) {
                case IntelligentRecyclingDecisionReason.OwnedBelowThreshold:
                    return "owned-below-threshold";
                case IntelligentRecyclingDecisionReason.OwnedEqualThreshold:
                    return "owned-equal-threshold";
                case IntelligentRecyclingDecisionReason.ReleasedAboveThreshold:
                    return "released-above-threshold";
                case IntelligentRecyclingDecisionReason.PermanentlyProtected:
                    return "permanently-protected";
                case IntelligentRecyclingDecisionReason.SpecialTargetReserved:
                    return "special-target-reserved";
                case IntelligentRecyclingDecisionReason.MasterDisabled:
                    return "master-disabled";
                case IntelligentRecyclingDecisionReason.ParentDisabled:
                    return "parent-disabled";
                case IntelligentRecyclingDecisionReason.CategoryDisabled:
                    return "category-disabled";
                case IntelligentRecyclingDecisionReason.NoCompatibleProcessor:
                    return "no-compatible-processor";
                case IntelligentRecyclingDecisionReason.UnsupportedTarget:
                    return "unsupported-target";
                case IntelligentRecyclingDecisionReason.NotQualityBearing:
                    return "not-quality-bearing";
                case IntelligentRecyclingDecisionReason.NotActiveLoose:
                    return "not-active-loose";
                case IntelligentRecyclingDecisionReason.InvalidObject:
                    return "invalid-object";
                case IntelligentRecyclingDecisionReason.SpecialConditionNotReady:
                    return "special-condition-not-ready";
                case IntelligentRecyclingDecisionReason.WholeBeingNotSupported:
                    return "whole-being-not-supported";
                default:
                    return "unknown";
            }
        }

        internal static IReadOnlyList<string> BlockerIds(
                IntelligentRecyclingBlockers blockers) {
            List<string> result = new List<string>();
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.Forbidden,
                "forbidden");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.ExistingGoal,
                "existing-goal");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.WorkerAssigned,
                "worker-assigned");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.Stored,
                "stored");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.Carried,
                "carried");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.Inactive,
                "inactive");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.NoProcessor,
                "no-processor");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.MasterDisabled,
                "master-disabled");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.ParentDisabled,
                "parent-disabled");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.CategoryDisabled,
                "category-disabled");
            AddBlocker(result, blockers,
                IntelligentRecyclingBlockers.PermanentlyProtected,
                "permanently-protected");
            AddBlocker(result, blockers,
                IntelligentRecyclingBlockers.SpecialCondition,
                "special-condition");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.Unsupported,
                "unsupported");
            AddBlocker(result, blockers,
                IntelligentRecyclingBlockers.VanillaOrderRejected,
                "vanilla-order-rejected");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.Invalid,
                "invalid");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.NotStorable,
                "not-storable");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.InDevice,
                "in-device");
            AddBlocker(result, blockers, IntelligentRecyclingBlockers.Reserved,
                "reserved");
            return new ReadOnlyCollection<string>(result);
        }

        private static void AddBlocker(List<string> result,
                IntelligentRecyclingBlockers blockers,
                IntelligentRecyclingBlockers value, string id) {
            if ((blockers & value) == value) {
                result.Add(id);
            }
        }

        internal static IReadOnlyList<T> ReadOnlyCopy<T>(IEnumerable<T> source) {
            return new ReadOnlyCollection<T>(source == null
                ? new List<T>()
                : new List<T>(source));
        }
    }

    public sealed class IntelligentRecyclingApiInfo {
        internal IntelligentRecyclingApiInfo(int contractVersion,
                IntelligentRecyclingApiCapabilities capabilities,
                string modId, string displayName, string modVersion,
                string assemblyVersion, int saveSchemaVersion,
                string requiredAumfVersion, bool systemInitialized,
                bool gameStateLoaded, int gameId, long simulationTick,
                long dataRevision, string unavailableReason) {
            ContractVersion = contractVersion;
            Capabilities = capabilities;
            ModId = modId;
            DisplayName = displayName;
            ModVersion = modVersion;
            AssemblyVersion = assemblyVersion;
            SaveSchemaVersion = saveSchemaVersion;
            RequiredAumfVersion = requiredAumfVersion;
            SystemInitialized = systemInitialized;
            GameStateLoaded = gameStateLoaded;
            GameId = gameId;
            SimulationTick = simulationTick;
            DataRevision = dataRevision;
            UnavailableReason = unavailableReason ?? string.Empty;
        }

        public int ContractVersion { get; }
        public IntelligentRecyclingApiCapabilities Capabilities { get; }
        public string ModId { get; }
        public string DisplayName { get; }
        public string ModVersion { get; }
        public string AssemblyVersion { get; }
        public int SaveSchemaVersion { get; }
        public string RequiredAumfVersion { get; }
        public bool SystemInitialized { get; }
        public bool GameStateLoaded { get; }
        public int GameId { get; }
        public long SimulationTick { get; }
        public long DataRevision { get; }
        public string UnavailableReason { get; }
        public bool MainThreadOnly => true;
        public bool SupportsSettings => Has(
            IntelligentRecyclingApiCapabilities.Settings);
        public bool SupportsCategoryCatalog => Has(
            IntelligentRecyclingApiCapabilities.CategoryCatalog);
        public bool SupportsProcessorThresholds => Has(
            IntelligentRecyclingApiCapabilities.ProcessorThresholds);
        public bool SupportsScanState => Has(
            IntelligentRecyclingApiCapabilities.ScanState);
        public bool SupportsScanStatistics => Has(
            IntelligentRecyclingApiCapabilities.ScanStatistics);
        public bool SupportsAccumulatedTotals => Has(
            IntelligentRecyclingApiCapabilities.AccumulatedTotals);
        public bool SupportsTrackedTasks => Has(
            IntelligentRecyclingApiCapabilities.TrackedTasks);
        public bool SupportsPerObjectDispositions => Has(
            IntelligentRecyclingApiCapabilities.PerObjectDispositions);
        public bool SupportsSpecialTargets => Has(
            IntelligentRecyclingApiCapabilities.SpecialTargets);
        public bool SupportsUiShortcutStatus => Has(
            IntelligentRecyclingApiCapabilities.UiShortcutStatus);

        private bool Has(IntelligentRecyclingApiCapabilities capability) {
            return (Capabilities & capability) == capability;
        }
    }

    public sealed class IntelligentRecyclingSettingSnapshot {
        internal IntelligentRecyclingSettingSnapshot(string id,
                string displayName, bool value, bool defaultValue,
                string description, bool effective) {
            Id = id;
            DisplayName = displayName;
            Value = value;
            DefaultValue = defaultValue;
            Description = description;
            Effective = effective;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public bool Value { get; }
        public bool DefaultValue { get; }
        public string Description { get; }
        public bool Effective { get; }
    }

    public sealed class IntelligentRecyclingCategorySnapshot {
        internal IntelligentRecyclingCategorySnapshot(
                IntelligentRecyclingCategoryKind kind, string id,
                string displayName, bool biologicalVariantExists,
                bool roboticVariantExists, string biologicalIconId,
                string roboticIconId, string equipmentIconId,
                bool biologicalEnabled, bool roboticEnabled,
                bool equipmentEnabled, bool biologicalParentEnabled,
                bool roboticParentEnabled, bool equipmentParentEnabled,
                bool biologicalEffective, bool roboticEffective,
                bool equipmentEffective, bool permanentlyProtected,
                string protectionReason, bool fromLoadedRegistry) {
            Kind = kind;
            Id = id;
            DisplayName = displayName;
            BiologicalVariantExists = biologicalVariantExists;
            RoboticVariantExists = roboticVariantExists;
            BiologicalIconId = biologicalIconId ?? string.Empty;
            RoboticIconId = roboticIconId ?? string.Empty;
            EquipmentIconId = equipmentIconId ?? string.Empty;
            BiologicalEnabled = biologicalEnabled;
            RoboticEnabled = roboticEnabled;
            EquipmentEnabled = equipmentEnabled;
            BiologicalParentEnabled = biologicalParentEnabled;
            RoboticParentEnabled = roboticParentEnabled;
            EquipmentParentEnabled = equipmentParentEnabled;
            BiologicalEffective = biologicalEffective;
            RoboticEffective = roboticEffective;
            EquipmentEffective = equipmentEffective;
            PermanentlyProtected = permanentlyProtected;
            ProtectionReason = protectionReason ?? string.Empty;
            FromLoadedRegistry = fromLoadedRegistry;
        }

        public IntelligentRecyclingCategoryKind Kind { get; }
        public string Id { get; }
        public string DisplayName { get; }
        public bool BiologicalVariantExists { get; }
        public bool RoboticVariantExists { get; }
        public string BiologicalIconId { get; }
        public string RoboticIconId { get; }
        public string EquipmentIconId { get; }
        public bool BiologicalEnabled { get; }
        public bool RoboticEnabled { get; }
        public bool EquipmentEnabled { get; }
        public bool BiologicalParentEnabled { get; }
        public bool RoboticParentEnabled { get; }
        public bool EquipmentParentEnabled { get; }
        public bool BiologicalEffective { get; }
        public bool RoboticEffective { get; }
        public bool EquipmentEffective { get; }
        public bool PermanentlyProtected { get; }
        public string ProtectionReason { get; }
        public bool FromLoadedRegistry { get; }
    }

    public sealed class IntelligentRecyclingProcessorSnapshot {
        internal IntelligentRecyclingProcessorSnapshot(int entityId,
                string definitionId, string displayName,
                IntelligentRecyclingProcessorFamily family,
                bool constructed, float configuredThreshold,
                bool contributesToEffectiveThreshold,
                int queueCount, int capacity) {
            EntityId = entityId;
            DefinitionId = definitionId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Family = family;
            FamilyId = IntelligentRecyclingApiIds.ProcessorFamily(family);
            Constructed = constructed;
            ConfiguredThreshold = configuredThreshold;
            ContributesToEffectiveThreshold = contributesToEffectiveThreshold;
            QueueCount = queueCount;
            Capacity = capacity;
        }

        public int EntityId { get; }
        public string DefinitionId { get; }
        public string DisplayName { get; }
        public IntelligentRecyclingProcessorFamily Family { get; }
        public string FamilyId { get; }
        public bool Constructed { get; }
        public float ConfiguredThreshold { get; }
        public bool ContributesToEffectiveThreshold { get; }
        public int QueueCount { get; }
        public int Capacity { get; }
    }

    public sealed class IntelligentRecyclingProcessorFamilySnapshot {
        internal IntelligentRecyclingProcessorFamilySnapshot(
                IntelligentRecyclingProcessorFamily family,
                bool managedThresholdAvailable, float managedThreshold,
                bool compatibleProcessorExists,
                bool effectiveThresholdAvailable, float effectiveThreshold,
                int compatibleConstructedProcessorCount) {
            Family = family;
            FamilyId = IntelligentRecyclingApiIds.ProcessorFamily(family);
            ManagedThresholdAvailable = managedThresholdAvailable;
            ManagedThreshold = managedThreshold;
            CompatibleProcessorExists = compatibleProcessorExists;
            EffectiveThresholdAvailable = effectiveThresholdAvailable;
            EffectiveThreshold = effectiveThreshold;
            CompatibleConstructedProcessorCount =
                compatibleConstructedProcessorCount;
        }

        public IntelligentRecyclingProcessorFamily Family { get; }
        public string FamilyId { get; }
        public bool ManagedThresholdAvailable { get; }
        public float ManagedThreshold { get; }
        public bool CompatibleProcessorExists { get; }
        public bool EffectiveThresholdAvailable { get; }
        public float EffectiveThreshold { get; }
        public int CompatibleConstructedProcessorCount { get; }
    }

    public sealed class IntelligentRecyclingScanSnapshot {
        internal IntelligentRecyclingScanSnapshot(long scanIntervalTicks,
                long lastScanTick, long nextScheduledScanTick,
                bool eventRequestedScanPending, bool trackedGoalReconciliationReady,
                bool automationCanScan, int eligibleLooseObjectsChecked,
                int tasksQueued, int aboveThreshold, int noCompatibleProcessor,
                int protectedMindParts, int existingTask,
                int forbidden, int reserved, int parentDisabled,
                int categoryDisabled,
                int storedOrNotLoose, int carried, int inactiveOrInvalid,
                int unsupportedTarget, int specialConditionNotReady,
                int vanillaOrderRejected, int legacyUnsplitIgnored) {
            ScanIntervalTicks = scanIntervalTicks;
            LastScanTick = lastScanTick;
            NextScheduledScanTick = nextScheduledScanTick;
            EventRequestedScanPending = eventRequestedScanPending;
            TrackedGoalReconciliationReady = trackedGoalReconciliationReady;
            AutomationCanScan = automationCanScan;
            EligibleLooseObjectsChecked = eligibleLooseObjectsChecked;
            TasksQueued = tasksQueued;
            AboveThreshold = aboveThreshold;
            NoCompatibleProcessor = noCompatibleProcessor;
            ProtectedMindParts = protectedMindParts;
            ExistingTask = existingTask;
            Forbidden = forbidden;
            Reserved = reserved;
            ParentDisabled = parentDisabled;
            CategoryDisabled = categoryDisabled;
            StoredOrNotLoose = storedOrNotLoose;
            Carried = carried;
            InactiveOrInvalid = inactiveOrInvalid;
            UnsupportedTarget = unsupportedTarget;
            SpecialConditionNotReady = specialConditionNotReady;
            VanillaOrderRejected = vanillaOrderRejected;
            LegacyUnsplitIgnored = legacyUnsplitIgnored;
        }

        public long ScanIntervalTicks { get; }
        public long LastScanTick { get; }
        public long NextScheduledScanTick { get; }
        public bool EventRequestedScanPending { get; }
        public bool TrackedGoalReconciliationReady { get; }
        public bool AutomationCanScan { get; }
        public int EligibleLooseObjectsChecked { get; }
        public int TasksQueued { get; }
        public int AboveThreshold { get; }
        public int NoCompatibleProcessor { get; }
        public int ProtectedMindParts { get; }
        public int ExistingTask { get; }
        public int Forbidden { get; }
        public int Reserved { get; }
        public int ParentDisabled { get; }
        public int CategoryDisabled { get; }
        public int StoredOrNotLoose { get; }
        public int Carried { get; }
        public int InactiveOrInvalid { get; }
        public int UnsupportedTarget { get; }
        public int SpecialConditionNotReady { get; }
        public int VanillaOrderRejected { get; }
        public int LegacyUnsplitIgnored { get; }
    }

    public sealed class IntelligentRecyclingTotalsSnapshot {
        internal IntelligentRecyclingTotalsSnapshot(int biological,
                int robotic, int equipment, int activeTracked,
                int biologicalBodyParts, int roboticBodyParts,
                int equipmentItems, int hullpiercers, int storageCapsules,
                int safeBoxes, int artilleryFungus, int deadCrawlers, int stasisPods,
                int breachCapsulePorts, int lostShuttleReplacements,
                int activeObjects, int activeBeings, int activeStasisPods,
                int activeBreachCapsulePorts,
                int activeLostShuttleReplacements) {
            BiologicalTasksQueued = biological;
            RoboticTasksQueued = robotic;
            EquipmentTasksQueued = equipment;
            ActiveTrackedTasks = activeTracked;
            BiologicalBodyPartTasksQueued = biologicalBodyParts;
            RoboticBodyPartTasksQueued = roboticBodyParts;
            EquipmentItemTasksQueued = equipmentItems;
            HullpiercerTasksQueued = hullpiercers;
            StorageCapsuleTasksQueued = storageCapsules;
            SafeBoxTasksQueued = safeBoxes;
            ArtilleryFungusTasksQueued = artilleryFungus;
            DeadCrawlerTasksQueued = deadCrawlers;
            StasisPodTasksQueued = stasisPods;
            BreachCapsulePortTasksQueued = breachCapsulePorts;
            LostShuttleReplacementsQueued = lostShuttleReplacements;
            ActiveObjectTasks = activeObjects;
            ActiveBeingTasks = activeBeings;
            ActiveStasisPodTasks = activeStasisPods;
            ActiveBreachCapsulePortTasks = activeBreachCapsulePorts;
            ActiveLostShuttleReplacements = activeLostShuttleReplacements;
        }

        public int BiologicalTasksQueued { get; }
        public int RoboticTasksQueued { get; }
        public int EquipmentTasksQueued { get; }
        public int ActiveTrackedTasks { get; }
        public int BiologicalBodyPartTasksQueued { get; }
        public int RoboticBodyPartTasksQueued { get; }
        public int EquipmentItemTasksQueued { get; }
        public int HullpiercerTasksQueued { get; }
        public int StorageCapsuleTasksQueued { get; }
        public int SafeBoxTasksQueued { get; }
        public int ArtilleryFungusTasksQueued { get; }
        public int DeadCrawlerTasksQueued { get; }
        public int StasisPodTasksQueued { get; }
        public int BreachCapsulePortTasksQueued { get; }
        public int LostShuttleReplacementsQueued { get; }
        public int ActiveObjectTasks { get; }
        public int ActiveBeingTasks { get; }
        public int ActiveStasisPodTasks { get; }
        public int ActiveBreachCapsulePortTasks { get; }
        public int ActiveLostShuttleReplacements { get; }
    }

    public sealed class IntelligentRecyclingTrackedTaskSnapshot {
        internal IntelligentRecyclingTrackedTaskSnapshot(uint goalId,
                long targetId, IntelligentRecyclingTrackedTargetKind targetKind,
                string definitionOrSpeciesId, string displayName,
                string categoryId, IntelligentRecyclingTargetType targetType,
                int goalStateCode, string goalStateId, int jobTypeCode,
                string jobTypeId, bool workerAssigned, uint workerBeingId,
                uint workerMindId, long createdTick, string failureReason,
                int timesFailed, long canRetryAfterTick, bool nonRetryable,
                bool createdByIntelligentRecycling, bool active) {
            GoalId = goalId;
            TargetId = targetId;
            TargetKind = targetKind;
            DefinitionOrSpeciesId = definitionOrSpeciesId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            CategoryId = categoryId ?? string.Empty;
            TargetType = targetType;
            TargetTypeId = IntelligentRecyclingApiIds.TargetType(targetType);
            GoalStateCode = goalStateCode;
            GoalStateId = goalStateId ?? string.Empty;
            JobTypeCode = jobTypeCode;
            JobTypeId = jobTypeId ?? string.Empty;
            WorkerAssigned = workerAssigned;
            WorkerBeingId = workerBeingId;
            WorkerMindId = workerMindId;
            CreatedTick = createdTick;
            FailureReason = failureReason ?? string.Empty;
            TimesFailed = timesFailed;
            CanRetryAfterTick = canRetryAfterTick;
            NonRetryable = nonRetryable;
            CreatedByIntelligentRecycling = createdByIntelligentRecycling;
            Active = active;
        }

        public uint GoalId { get; }
        public long TargetId { get; }
        public IntelligentRecyclingTrackedTargetKind TargetKind { get; }
        public string DefinitionOrSpeciesId { get; }
        public string DisplayName { get; }
        public string CategoryId { get; }
        public IntelligentRecyclingTargetType TargetType { get; }
        public string TargetTypeId { get; }
        public int GoalStateCode { get; }
        public string GoalStateId { get; }
        public int JobTypeCode { get; }
        public string JobTypeId { get; }
        public bool WorkerAssigned { get; }
        public uint WorkerBeingId { get; }
        public uint WorkerMindId { get; }
        public long CreatedTick { get; }
        public string FailureReason { get; }
        public int TimesFailed { get; }
        public long CanRetryAfterTick { get; }
        public bool NonRetryable { get; }
        public bool CreatedByIntelligentRecycling { get; }
        public bool Active { get; }
    }

    internal sealed class IntelligentRecyclingObjectSnapshotData {
        internal bool ObjectFound;
        internal uint ObjectId;
        internal string DefinitionId;
        internal string DisplayName;
        internal bool Active;
        internal bool Loose;
        internal bool Stored;
        internal bool Carried;
        internal bool InDevice;
        internal bool HasPosition;
        internal int PositionIndex;
        internal bool HasObjQuality;
        internal float Quality;
        internal IntelligentRecyclingTargetType TargetType;
        internal IntelligentRecyclingProcessorFamily ProcessorFamily;
        internal string CategoryId;
        internal string CategoryDisplayName;
        internal bool ParentSettingEnabled;
        internal bool CategorySettingEnabled;
        internal bool PermanentlyProtected;
        internal string ProtectionReason;
        internal bool CompatibleProcessorExists;
        internal bool EffectiveThresholdAvailable;
        internal float EffectiveThreshold;
        internal IntelligentRecyclingQualityRelation QualityRelation;
        internal bool Forbidden;
        internal bool ExistingGoal;
        internal uint GoalId;
        internal int GoalJobTypeCode;
        internal string GoalJobTypeId;
        internal int GoalStateCode;
        internal string GoalStateId;
        internal bool WorkerAssigned;
        internal bool Reserved;
        internal IntelligentRecyclingBlockers Blockers;
        internal IntelligentRecyclingOwnership Ownership;
        internal IntelligentRecyclingDecisionReason DecisionReason;
        internal string DiagnosticReason;
        internal bool IntelligentRecyclingWouldClaimUnderPolicy;
        internal bool IntelligentRecyclingCanQueueNow;
        internal bool ReleasedForAutoHauling;
        internal bool CanCurrentlyBeHauled;
        internal bool TrackedByIntelligentRecycling;
        internal bool SpecialTarget;
        internal bool SpecialReadyForProcessing;
    }

    public sealed class IntelligentRecyclingObjectSnapshot {
        internal IntelligentRecyclingObjectSnapshot(
                IntelligentRecyclingObjectSnapshotData data) {
            ObjectFound = data.ObjectFound;
            ObjectId = data.ObjectId;
            DefinitionId = data.DefinitionId ?? string.Empty;
            DisplayName = data.DisplayName ?? string.Empty;
            Active = data.Active;
            Loose = data.Loose;
            Stored = data.Stored;
            Carried = data.Carried;
            InDevice = data.InDevice;
            HasPosition = data.HasPosition;
            PositionIndex = data.PositionIndex;
            HasObjQuality = data.HasObjQuality;
            Quality = data.Quality;
            TargetType = data.TargetType;
            TargetTypeId = IntelligentRecyclingApiIds.TargetType(data.TargetType);
            ProcessorFamily = data.ProcessorFamily;
            ProcessorFamilyId = IntelligentRecyclingApiIds.ProcessorFamily(
                data.ProcessorFamily);
            CategoryId = data.CategoryId ?? string.Empty;
            CategoryDisplayName = data.CategoryDisplayName ?? string.Empty;
            ParentSettingEnabled = data.ParentSettingEnabled;
            CategorySettingEnabled = data.CategorySettingEnabled;
            PermanentlyProtected = data.PermanentlyProtected;
            ProtectionReason = data.ProtectionReason ?? string.Empty;
            CompatibleProcessorExists = data.CompatibleProcessorExists;
            EffectiveThresholdAvailable = data.EffectiveThresholdAvailable;
            EffectiveThreshold = data.EffectiveThreshold;
            QualityRelation = data.QualityRelation;
            QualityRelationId = IntelligentRecyclingApiIds.QualityRelation(
                data.QualityRelation);
            Forbidden = data.Forbidden;
            ExistingGoal = data.ExistingGoal;
            GoalId = data.GoalId;
            GoalJobTypeCode = data.GoalJobTypeCode;
            GoalJobTypeId = data.GoalJobTypeId ?? string.Empty;
            GoalStateCode = data.GoalStateCode;
            GoalStateId = data.GoalStateId ?? string.Empty;
            WorkerAssigned = data.WorkerAssigned;
            Reserved = data.Reserved;
            Blockers = data.Blockers;
            BlockerIds = IntelligentRecyclingApiIds.BlockerIds(data.Blockers);
            Ownership = data.Ownership;
            OwnershipId = IntelligentRecyclingApiIds.Ownership(data.Ownership);
            DecisionReason = data.DecisionReason;
            DecisionReasonId = IntelligentRecyclingApiIds.DecisionReason(
                data.DecisionReason);
            DiagnosticReason = data.DiagnosticReason ?? string.Empty;
            IntelligentRecyclingWouldClaimUnderPolicy =
                data.IntelligentRecyclingWouldClaimUnderPolicy;
            IntelligentRecyclingCanQueueNow =
                data.IntelligentRecyclingCanQueueNow;
            ReleasedForAutoHauling = data.ReleasedForAutoHauling;
            CanCurrentlyBeHauled = data.CanCurrentlyBeHauled;
            TrackedByIntelligentRecycling = data.TrackedByIntelligentRecycling;
            SpecialTarget = data.SpecialTarget;
            SpecialReadyForProcessing = data.SpecialReadyForProcessing;
        }

        public bool ObjectFound { get; }
        public uint ObjectId { get; }
        public string DefinitionId { get; }
        public string DisplayName { get; }
        public bool Active { get; }
        public bool Loose { get; }
        public bool Stored { get; }
        public bool Carried { get; }
        public bool InDevice { get; }
        public bool HasPosition { get; }
        public int PositionIndex { get; }
        public bool HasObjQuality { get; }
        public float Quality { get; }
        public IntelligentRecyclingTargetType TargetType { get; }
        public string TargetTypeId { get; }
        public IntelligentRecyclingProcessorFamily ProcessorFamily { get; }
        public string ProcessorFamilyId { get; }
        public string CategoryId { get; }
        public string CategoryDisplayName { get; }
        public bool ParentSettingEnabled { get; }
        public bool CategorySettingEnabled { get; }
        public bool PermanentlyProtected { get; }
        public string ProtectionReason { get; }
        public bool CompatibleProcessorExists { get; }
        public bool EffectiveThresholdAvailable { get; }
        public float EffectiveThreshold { get; }
        public IntelligentRecyclingQualityRelation QualityRelation { get; }
        public string QualityRelationId { get; }
        public bool Forbidden { get; }
        public bool ExistingGoal { get; }
        public uint GoalId { get; }
        public int GoalJobTypeCode { get; }
        public string GoalJobTypeId { get; }
        public int GoalStateCode { get; }
        public string GoalStateId { get; }
        public bool WorkerAssigned { get; }
        public bool Reserved { get; }
        public IntelligentRecyclingBlockers Blockers { get; }
        public IReadOnlyList<string> BlockerIds { get; }
        public IntelligentRecyclingOwnership Ownership { get; }
        public string OwnershipId { get; }
        public IntelligentRecyclingDecisionReason DecisionReason { get; }
        public string DecisionReasonId { get; }
        public string DiagnosticReason { get; }
        public bool IntelligentRecyclingWouldClaimUnderPolicy { get; }
        public bool IntelligentRecyclingCanQueueNow { get; }
        public bool ReleasedForAutoHauling { get; }
        public bool CanCurrentlyBeHauled { get; }
        public bool TrackedByIntelligentRecycling { get; }
        public bool SpecialTarget { get; }
        public bool SpecialReadyForProcessing { get; }
    }

    public sealed class IntelligentRecyclingSpecialTargetSnapshot {
        internal IntelligentRecyclingSpecialTargetSnapshot(long targetId,
                IntelligentRecyclingTargetType targetType,
                string definitionOrSpeciesId, string displayName,
                bool recognized, bool toggleEnabled, bool active,
                bool readyForProcessing, bool intelligentRecyclingOwned,
                bool processorAvailable, bool qualityThresholdApplicable,
                bool midFlight, bool open, bool merchantTradeProtected,
                bool raidersReleased, bool alive, bool incapacitated,
                bool dead, string stateDetail) {
            TargetId = targetId;
            TargetType = targetType;
            TargetTypeId = IntelligentRecyclingApiIds.TargetType(targetType);
            DefinitionOrSpeciesId = definitionOrSpeciesId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Recognized = recognized;
            ToggleEnabled = toggleEnabled;
            Active = active;
            ReadyForProcessing = readyForProcessing;
            IntelligentRecyclingOwned = intelligentRecyclingOwned;
            ProcessorAvailable = processorAvailable;
            QualityThresholdApplicable = qualityThresholdApplicable;
            MidFlight = midFlight;
            Open = open;
            MerchantTradeProtected = merchantTradeProtected;
            RaidersReleased = raidersReleased;
            Alive = alive;
            Incapacitated = incapacitated;
            Dead = dead;
            StateDetail = stateDetail ?? string.Empty;
        }

        public long TargetId { get; }
        public IntelligentRecyclingTargetType TargetType { get; }
        public string TargetTypeId { get; }
        public string DefinitionOrSpeciesId { get; }
        public string DisplayName { get; }
        public bool Recognized { get; }
        public bool ToggleEnabled { get; }
        public bool Active { get; }
        public bool ReadyForProcessing { get; }
        public bool IntelligentRecyclingOwned { get; }
        public bool ProcessorAvailable { get; }
        public bool QualityThresholdApplicable { get; }
        public bool MidFlight { get; }
        public bool Open { get; }
        public bool MerchantTradeProtected { get; }
        public bool RaidersReleased { get; }
        public bool Alive { get; }
        public bool Incapacitated { get; }
        public bool Dead { get; }
        public string StateDetail { get; }
    }

    public sealed class IntelligentRecyclingUiSnapshot {
        internal IntelligentRecyclingUiSnapshot(bool panelOpen,
                bool topBarWidgetRegistered, bool topBarWidgetCreated,
                bool shortcutAssigned, string shortcutDisplayValue,
                bool shortcutCaptureActive, string shortcutStatusSummary,
                string shortcutStatusMessage) {
            PanelOpen = panelOpen;
            TopBarWidgetRegistered = topBarWidgetRegistered;
            TopBarWidgetCreated = topBarWidgetCreated;
            ShortcutAssigned = shortcutAssigned;
            ShortcutDisplayValue = shortcutDisplayValue ?? string.Empty;
            ShortcutCaptureActive = shortcutCaptureActive;
            ShortcutStatusSummary = shortcutStatusSummary ?? string.Empty;
            ShortcutStatusMessage = shortcutStatusMessage ?? string.Empty;
        }

        public bool PanelOpen { get; }
        public bool TopBarWidgetRegistered { get; }
        public bool TopBarWidgetCreated { get; }
        public bool ShortcutAssigned { get; }
        public string ShortcutDisplayValue { get; }
        public bool ShortcutCaptureActive { get; }
        public string ShortcutStatusSummary { get; }
        public string ShortcutStatusMessage { get; }
    }

    public sealed class IntelligentRecyclingStateSnapshot {
        internal IntelligentRecyclingStateSnapshot(bool available,
                string unavailableReason, int gameId, long simulationTick,
                long dataRevision,
                IEnumerable<IntelligentRecyclingSettingSnapshot> settings,
                IEnumerable<IntelligentRecyclingCategorySnapshot> categories,
                IEnumerable<IntelligentRecyclingProcessorSnapshot> processors,
                IEnumerable<IntelligentRecyclingProcessorFamilySnapshot>
                    processorFamilies,
                IntelligentRecyclingScanSnapshot scan,
                IntelligentRecyclingTotalsSnapshot totals,
                IEnumerable<IntelligentRecyclingTrackedTaskSnapshot> trackedTasks,
                IEnumerable<IntelligentRecyclingSpecialTargetSnapshot>
                    specialTargets,
                IntelligentRecyclingUiSnapshot ui) {
            Available = available;
            UnavailableReason = unavailableReason ?? string.Empty;
            GameId = gameId;
            SimulationTick = simulationTick;
            DataRevision = dataRevision;
            Settings = IntelligentRecyclingApiIds.ReadOnlyCopy(settings);
            Categories = IntelligentRecyclingApiIds.ReadOnlyCopy(categories);
            Processors = IntelligentRecyclingApiIds.ReadOnlyCopy(processors);
            ProcessorFamilies = IntelligentRecyclingApiIds.ReadOnlyCopy(
                processorFamilies);
            Scan = scan;
            Totals = totals;
            TrackedTasks = IntelligentRecyclingApiIds.ReadOnlyCopy(trackedTasks);
            SpecialTargets = IntelligentRecyclingApiIds.ReadOnlyCopy(specialTargets);
            Ui = ui;
        }

        public bool Available { get; }
        public string UnavailableReason { get; }
        public int GameId { get; }
        public long SimulationTick { get; }
        public long DataRevision { get; }
        public IReadOnlyList<IntelligentRecyclingSettingSnapshot> Settings { get; }
        public IReadOnlyList<IntelligentRecyclingCategorySnapshot> Categories { get; }
        public IReadOnlyList<IntelligentRecyclingProcessorSnapshot> Processors { get; }
        public IReadOnlyList<IntelligentRecyclingProcessorFamilySnapshot>
            ProcessorFamilies { get; }
        public IntelligentRecyclingScanSnapshot Scan { get; }
        public IntelligentRecyclingTotalsSnapshot Totals { get; }
        public IReadOnlyList<IntelligentRecyclingTrackedTaskSnapshot>
            TrackedTasks { get; }
        public IReadOnlyList<IntelligentRecyclingSpecialTargetSnapshot>
            SpecialTargets { get; }
        public IntelligentRecyclingUiSnapshot Ui { get; }
    }
}
