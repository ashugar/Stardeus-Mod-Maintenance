using System;
using System.Collections.Generic;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Utils;
using KL.Utils;

namespace Game.Systems.IntelligentRecycling {
    public sealed partial class IntelligentRecyclingSys {
        private ProcessorContext cachedProcessorContext;
        private long cachedProcessorContextTick = long.MinValue;

        private sealed class ProcessorContext {
            internal float BiologicalThreshold = -1f;
            internal float RoboticThreshold = -1f;
            internal bool BiologicalThresholdInvalid;
            internal bool RoboticThresholdInvalid;
            internal int BiologicalCount;
            internal int RoboticCount;
            internal readonly List<IntelligentRecyclingProcessorSnapshot>
                Processors = new List<IntelligentRecyclingProcessorSnapshot>();
        }

        private sealed class ObjectEvaluation {
            internal ObjData Data;
            internal Obj Obj;
            internal DisposableComp Disposable;
            internal StorableObjComp Storable;
            internal IntelligentRecyclingObjectSnapshotData Snapshot;
            internal bool Recognized;
            internal bool PolicyCategoryReady;
            internal bool SpecialReady;
        }

        internal static IntelligentRecyclingSys ApiInstance => activeInstance;

        internal static IntelligentRecyclingApiInfo BuildApiInfo() {
            IntelligentRecyclingSys system = activeInstance;
            bool canReadState = system != null && A.IsMainThread;
            bool gameLoaded = canReadState && system.loadedState != null;
            return new IntelligentRecyclingApiInfo(
                IntelligentRecyclingApi.ContractVersion,
                IntelligentRecyclingApiCapabilities.All,
                ModId,
                DisplayName,
                ModVersion,
                typeof(IntelligentRecyclingSys).Assembly.GetName().Version
                    .ToString(),
                SaveSchemaVersion,
                RequiredUtilitiesVersion,
                system != null,
                gameLoaded,
                gameLoaded ? system.loadedState.GameId : -1,
                gameLoaded ? system.loadedState.Ticks : 0,
                apiRevision,
                system == null
                    ? "system-not-initialized"
                    : !canReadState
                        ? "main-thread-required"
                        : !gameLoaded
                            ? "game-state-not-loaded"
                            : string.Empty);
        }

        internal static IntelligentRecyclingStateSnapshot
                BuildUnavailableStateSnapshot(string reason) {
            return new IntelligentRecyclingStateSnapshot(
                false,
                reason,
                -1,
                0,
                apiRevision,
                null,
                null,
                null,
                null,
                EmptyScanSnapshot(),
                EmptyTotalsSnapshot(),
                null,
                null,
                EmptyUiSnapshot());
        }

        internal IntelligentRecyclingStateSnapshot BuildStateSnapshot() {
            if (loadedState == null) {
                return BuildUnavailableStateSnapshot("game-state-not-loaded");
            }
            ProcessorContext processors = BuildProcessorContext();
            return new IntelligentRecyclingStateSnapshot(
                true,
                string.Empty,
                loadedState.GameId,
                loadedState.Ticks,
                apiRevision,
                BuildSettingSnapshotsInternal(),
                BuildCategorySnapshotsInternal(),
                processors.Processors,
                BuildProcessorFamilySnapshots(processors),
                BuildScanSnapshot(),
                BuildTotalsSnapshot(),
                BuildTrackedTaskSnapshotsInternal(processors),
                BuildSpecialTargetSnapshotsInternal(processors),
                BuildUiSnapshot());
        }

        internal IReadOnlyList<IntelligentRecyclingCategorySnapshot>
                BuildCategorySnapshots() {
            if (loadedState == null) {
                return IntelligentRecyclingApiIds.ReadOnlyCopy(
                    (IEnumerable<IntelligentRecyclingCategorySnapshot>) null);
            }
            return IntelligentRecyclingApiIds.ReadOnlyCopy(
                BuildCategorySnapshotsInternal());
        }

        internal IReadOnlyList<IntelligentRecyclingProcessorSnapshot>
                BuildProcessorSnapshots() {
            if (loadedState == null) {
                return IntelligentRecyclingApiIds.ReadOnlyCopy(
                    (IEnumerable<IntelligentRecyclingProcessorSnapshot>) null);
            }
            return IntelligentRecyclingApiIds.ReadOnlyCopy(
                BuildProcessorContext().Processors);
        }

        internal IReadOnlyList<IntelligentRecyclingTrackedTaskSnapshot>
                BuildTrackedTaskSnapshots() {
            if (loadedState == null) {
                return IntelligentRecyclingApiIds.ReadOnlyCopy(
                    (IEnumerable<IntelligentRecyclingTrackedTaskSnapshot>) null);
            }
            ProcessorContext processors = BuildProcessorContext();
            return IntelligentRecyclingApiIds.ReadOnlyCopy(
                BuildTrackedTaskSnapshotsInternal(processors));
        }

        internal IReadOnlyList<IntelligentRecyclingSpecialTargetSnapshot>
                BuildSpecialTargetSnapshots() {
            if (loadedState == null) {
                return IntelligentRecyclingApiIds.ReadOnlyCopy(
                    (IEnumerable<IntelligentRecyclingSpecialTargetSnapshot>) null);
            }
            ProcessorContext processors = BuildProcessorContext();
            return IntelligentRecyclingApiIds.ReadOnlyCopy(
                BuildSpecialTargetSnapshotsInternal(processors));
        }

        internal bool TryBuildObjectSnapshot(uint objectId,
                out IntelligentRecyclingObjectSnapshot snapshot) {
            snapshot = null;
            if (loadedState == null) {
                return false;
            }
            Dictionary<uint, ObjData> objects = S.Query.GetAllObjs.Ask();
            if (objects == null
                    || !objects.TryGetValue(objectId, out ObjData data)
                    || data == null) {
                return false;
            }
            ObjectEvaluation evaluation = EvaluateObject(
                data, BuildProcessorContext());
            snapshot = new IntelligentRecyclingObjectSnapshot(
                evaluation.Snapshot);
            return true;
        }

        internal bool TryBuildBeingSnapshot(uint beingId,
                out IntelligentRecyclingSpecialTargetSnapshot snapshot) {
            snapshot = null;
            if (loadedState == null) {
                return false;
            }
            Dictionary<uint, BeingData> beings = S.Query.AllBeingData.Ask();
            if (beings == null
                    || !beings.TryGetValue(beingId, out BeingData data)
                    || data == null) {
                return false;
            }
            ProcessorContext processors = BuildProcessorContext();
            snapshot = BuildBeingSpecialSnapshot(data, processors);
            return snapshot != null;
        }

        private static IntelligentRecyclingScanSnapshot EmptyScanSnapshot() {
            return new IntelligentRecyclingScanSnapshot(
                ScanIntervalTicks, 0, 0, false, false, false,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        private static IntelligentRecyclingTotalsSnapshot EmptyTotalsSnapshot() {
            return new IntelligentRecyclingTotalsSnapshot(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0);
        }

        private static IntelligentRecyclingUiSnapshot EmptyUiSnapshot() {
            return new IntelligentRecyclingUiSnapshot(
                false, false, false, false, string.Empty, false,
                "Unavailable", string.Empty);
        }

        private List<IntelligentRecyclingSettingSnapshot>
                BuildSettingSnapshotsInternal() {
            List<IntelligentRecyclingSettingSnapshot> result =
                new List<IntelligentRecyclingSettingSnapshot>();
            result.Add(Setting("automation", "Automation", Enabled, true,
                "Master switch for Intelligent Recycling.", Enabled));
            result.Add(Setting("biological-body-parts", "Biological body parts",
                RecycleBiologicalParts, true,
                "Send qualifying loose biological parts to a Nutrient Extractor.",
                Enabled && RecycleBiologicalParts));
            result.Add(Setting("robotic-body-parts", "Robotic body parts",
                RecycleRoboticParts, true,
                "Send qualifying loose robotic parts to a Disassembler.",
                Enabled && RecycleRoboticParts));
            result.Add(Setting("equipment", "Equipment", RecycleEquipment, false,
                "Send qualifying loose equipment to a Disassembler.",
                Enabled && RecycleEquipment));
            result.Add(Setting("replace-storage-haul-tasks",
                "Replace storage-haul tasks", ReplaceStorageHaulTasks, false,
                "Replace only exact unassigned Store goals for qualifying items.",
                Enabled && ReplaceStorageHaulTasks));
            result.Add(Setting("hullpiercers", "Hullpiercers",
                RecycleHullpiercers, true,
                "Send landed Hullpiercers to a Disassembler.",
                Enabled && RecycleHullpiercers));
            result.Add(Setting("opened-storage-capsules",
                "Opened storage capsules", RecycleOpenedStorageCapsules, true,
                "Send Storage Capsules to a Disassembler only after opening.",
                Enabled && RecycleOpenedStorageCapsules));
            result.Add(Setting("opened-safe-boxes", "Opened Safe Boxes",
                RecycleOpenedSafeBoxes, true,
                "Send Safe Boxes to a Disassembler only after opening.",
                Enabled && RecycleOpenedSafeBoxes));
            result.Add(Setting("stasis-pods", "Stasis pods",
                DeconstructStasisPods, true,
                "Deconstruct Stasis Pods after Core's Waiting lifecycle "
                    + "completes while protecting active trade.",
                Enabled && DeconstructStasisPods));
            result.Add(Setting("breach-capsule-ports", "Breach capsule ports",
                DeconstructBreachCapsulePorts, true,
                "Deconstruct resolved Breach Capsule Ports.",
                Enabled && DeconstructBreachCapsulePorts));
            result.Add(Setting("artillery-fungus", "Artillery Fungus",
                RecycleArtilleryFungus, true,
                "Dispose active Artillery Fungus without a quality filter.",
                Enabled && RecycleArtilleryFungus));
            result.Add(Setting("dead-crawlers", "Dead Crawlers",
                DisposeDeadCrawlers, true,
                "Dispose dead Crawler corpses through a Nutrient Extractor.",
                Enabled && DisposeDeadCrawlers));
            result.Add(Setting("replace-lost-shuttles", "Lost Shuttle pads",
                ReplaceLostShuttles, false,
                "Replace permanently lost Shuttle pads and reorder installed upgrades.",
                Enabled && ReplaceLostShuttles));
            return result;
        }

        private static IntelligentRecyclingSettingSnapshot Setting(string id,
                string name, bool value, bool defaultValue,
                string description, bool effective) {
            return new IntelligentRecyclingSettingSnapshot(
                id, name, value, defaultValue, description, effective);
        }

        private List<IntelligentRecyclingCategorySnapshot>
                BuildCategorySnapshotsInternal() {
            Dictionary<string, BodyPartCategoryInfo> bodyCategories =
                new Dictionary<string, BodyPartCategoryInfo>(
                    StringComparer.Ordinal);
            foreach (BodyPartDef part in BodyPartDef.All.Values) {
                if (part?.SlotType == null
                        || string.IsNullOrEmpty(part.SlotTypeId)) {
                    continue;
                }
                if (!bodyCategories.TryGetValue(part.SlotTypeId,
                        out BodyPartCategoryInfo info)) {
                    info = new BodyPartCategoryInfo {
                        Id = part.SlotTypeId,
                        Name = part.SlotType.NameT ?? part.SlotTypeId
                    };
                    bodyCategories.Add(part.SlotTypeId, info);
                }
                if (part.IsBiological) {
                    info.BiologicalIcon = info.BiologicalIcon ?? part.IconId;
                } else {
                    info.RoboticIcon = info.RoboticIcon ?? part.IconId;
                }
            }

            List<IntelligentRecyclingCategorySnapshot> result =
                new List<IntelligentRecyclingCategorySnapshot>();
            foreach (BodyPartCategoryInfo info in bodyCategories.Values) {
                bool protectedCategory = IsProtectedCategoryId(info.Id);
                bool bioExists = !string.IsNullOrEmpty(info.BiologicalIcon);
                bool roboExists = !string.IsNullOrEmpty(info.RoboticIcon);
                bool bioEnabled = IsBiologicalCategoryEnabled(info.Id);
                bool roboEnabled = IsRoboticCategoryEnabled(info.Id);
                result.Add(new IntelligentRecyclingCategorySnapshot(
                    IntelligentRecyclingCategoryKind.BodyPart,
                    info.Id,
                    info.Name,
                    bioExists,
                    roboExists,
                    info.BiologicalIcon,
                    info.RoboticIcon,
                    string.Empty,
                    bioEnabled,
                    roboEnabled,
                    false,
                    RecycleBiologicalParts,
                    RecycleRoboticParts,
                    false,
                    Enabled && RecycleBiologicalParts && bioEnabled,
                    Enabled && RecycleRoboticParts && roboEnabled,
                    false,
                    protectedCategory,
                    protectedCategory
                        ? "Brain and Core categories are permanently protected."
                        : string.Empty,
                    true));
            }

            Dictionary<string, string> equipmentNames =
                new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, string> equipmentIcons =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (InventoryItemDef item in InventoryItemDef.All.Values) {
                if (item?.SlotType == null || string.IsNullOrEmpty(item.SlotId)) {
                    continue;
                }
                equipmentNames[item.SlotId] =
                    item.SlotType.Name ?? item.SlotId;
                if (!equipmentIcons.ContainsKey(item.SlotId)) {
                    equipmentIcons[item.SlotId] = item.Def?.Preview;
                }
            }
            foreach (KeyValuePair<string, string> item in equipmentNames) {
                bool categoryEnabled = IsEquipmentCategoryEnabled(item.Key);
                equipmentIcons.TryGetValue(item.Key, out string icon);
                result.Add(new IntelligentRecyclingCategorySnapshot(
                    IntelligentRecyclingCategoryKind.Equipment,
                    item.Key,
                    item.Value,
                    false,
                    false,
                    string.Empty,
                    string.Empty,
                    icon,
                    false,
                    false,
                    categoryEnabled,
                    false,
                    false,
                    RecycleEquipment,
                    false,
                    false,
                    Enabled && RecycleEquipment && categoryEnabled,
                    false,
                    string.Empty,
                    true));
            }
            result.Sort((a, b) => {
                int kind = a.Kind.CompareTo(b.Kind);
                return kind != 0 ? kind : string.Compare(
                    a.DisplayName, b.DisplayName,
                    StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        private ProcessorContext BuildProcessorContext() {
            long currentTick = loadedState?.Ticks ?? long.MinValue;
            if (cachedProcessorContext != null
                    && cachedProcessorContextTick == currentTick) {
                return cachedProcessorContext;
            }
            ProcessorContext result = new ProcessorContext();
            List<DisposerComp> found = new List<DisposerComp>();
            S.Components.FindAll(found);
            foreach (DisposerComp disposer in found) {
                if (disposer?.Tile == null) {
                    continue;
                }
                bool constructed = disposer.Tile.IsConstructed;
                IntelligentRecyclingProcessorFamily family;
                if (disposer.SlotDesignation
                        == SlotDesignationH.Disposer_Organic) {
                    family = IntelligentRecyclingProcessorFamily.Biological;
                    if (constructed) {
                        result.BiologicalCount++;
                        IncludeProcessorThreshold(
                            disposer.PartQualityThreshold,
                            ref result.BiologicalThreshold,
                            ref result.BiologicalThresholdInvalid);
                    }
                } else if (disposer.SlotDesignation
                        == SlotDesignationH.Disposer_Robotic) {
                    family = IntelligentRecyclingProcessorFamily.Robotic;
                    if (constructed) {
                        result.RoboticCount++;
                        IncludeProcessorThreshold(
                            disposer.PartQualityThreshold,
                            ref result.RoboticThreshold,
                            ref result.RoboticThresholdInvalid);
                    }
                } else {
                    continue;
                }
                result.Processors.Add(new IntelligentRecyclingProcessorSnapshot(
                    disposer.EntityId,
                    disposer.Tile.Definition?.Id,
                    disposer.Tile.Definition?.NameT,
                    family,
                    constructed,
                    disposer.PartQualityThreshold,
                    false,
                    disposer.Queue.Count,
                    disposer.MaxCapacity));
            }

            if (result.Processors.Count > 0) {
                List<IntelligentRecyclingProcessorSnapshot> corrected =
                    new List<IntelligentRecyclingProcessorSnapshot>();
                foreach (IntelligentRecyclingProcessorSnapshot processor
                        in result.Processors) {
                    float effective = processor.Family
                        == IntelligentRecyclingProcessorFamily.Biological
                            ? result.BiologicalThreshold
                            : result.RoboticThreshold;
                    bool thresholdInvalid = processor.Family
                        == IntelligentRecyclingProcessorFamily.Biological
                            ? result.BiologicalThresholdInvalid
                            : result.RoboticThresholdInvalid;
                    corrected.Add(new IntelligentRecyclingProcessorSnapshot(
                        processor.EntityId,
                        processor.DefinitionId,
                        processor.DisplayName,
                        processor.Family,
                        processor.Constructed,
                        processor.ConfiguredThreshold,
                        processor.Constructed
                            && !thresholdInvalid
                            && IsFinite(processor.ConfiguredThreshold)
                            && effective >= 0f
                            && processor.ConfiguredThreshold == effective,
                        processor.QueueCount,
                        processor.Capacity));
                }
                result.Processors.Clear();
                result.Processors.AddRange(corrected);
            }
            cachedProcessorContext = result;
            cachedProcessorContextTick = currentTick;
            return result;
        }

        private static void IncludeProcessorThreshold(float configured,
                ref float effective, ref bool invalid) {
            if (!IsFinite(configured)) {
                invalid = true;
                return;
            }
            effective = Math.Max(effective, configured);
        }

        private void InvalidateProcessorContext() {
            cachedProcessorContext = null;
            cachedProcessorContextTick = long.MinValue;
        }

        private List<IntelligentRecyclingProcessorFamilySnapshot>
                BuildProcessorFamilySnapshots(ProcessorContext context) {
            return new List<IntelligentRecyclingProcessorFamilySnapshot> {
                new IntelligentRecyclingProcessorFamilySnapshot(
                    IntelligentRecyclingProcessorFamily.Biological,
                    thresholdAuthorityReady,
                    BiologicalQualityThreshold,
                    context.BiologicalCount > 0,
                    !context.BiologicalThresholdInvalid
                        && context.BiologicalThreshold >= 0f,
                    !context.BiologicalThresholdInvalid
                        && context.BiologicalThreshold >= 0f
                        ? context.BiologicalThreshold
                        : 0f,
                    context.BiologicalCount),
                new IntelligentRecyclingProcessorFamilySnapshot(
                    IntelligentRecyclingProcessorFamily.Robotic,
                    thresholdAuthorityReady,
                    RoboticQualityThreshold,
                    context.RoboticCount > 0,
                    !context.RoboticThresholdInvalid
                        && context.RoboticThreshold >= 0f,
                    !context.RoboticThresholdInvalid
                        && context.RoboticThreshold >= 0f
                        ? context.RoboticThreshold
                        : 0f,
                    context.RoboticCount)
            };
        }

        private ObjectEvaluation EvaluateObject(ObjData data,
                ProcessorContext processors) {
            IntelligentRecyclingObjectSnapshotData snapshot =
                new IntelligentRecyclingObjectSnapshotData();
            ObjectEvaluation result = new ObjectEvaluation {
                Data = data,
                Snapshot = snapshot
            };
            if (data == null) {
                snapshot.Ownership = IntelligentRecyclingOwnership.Unknown;
                snapshot.DecisionReason =
                    IntelligentRecyclingDecisionReason.InvalidObject;
                snapshot.DiagnosticReason = "Object data is unavailable.";
                snapshot.Blockers = IntelligentRecyclingBlockers.Invalid;
                return result;
            }

            Obj obj = data.Obj;
            result.Obj = obj;
            snapshot.ObjectFound = true;
            snapshot.ObjectId = data.Id;
            snapshot.DefinitionId = data.Def?.Id;
            snapshot.DisplayName = data.Def?.NameT ?? data.Def?.Id;
            snapshot.Active = data.State == ObjState.Active
                && obj != null && obj.IsActive;
            snapshot.Stored = data.State == ObjState.Stored;
            snapshot.InDevice = data.State == ObjState.InDevice;
            snapshot.Carried = data.State == ObjState.Equipped
                || obj?.IsCarriedByAny == true;
            snapshot.Loose = snapshot.Active && !snapshot.Carried;
            snapshot.HasPosition = obj != null && obj.IsActive;
            snapshot.PositionIndex = snapshot.HasPosition ? obj.PosIdx : 0;
            snapshot.HasObjQuality = data.Def?.HasComponent(
                    Hashes.S(ComponentId.ObjQuality)) == true
                || (obj != null
                    && obj.TryGetComponent<ObjQualityComp>(out _));
            snapshot.Quality = data.Quality;
            snapshot.Reserved = data.Reservation != null;

            if (obj != null) {
                obj.TryGetComponent<DisposableComp>(out result.Disposable);
                obj.TryGetComponent<StorableObjComp>(out result.Storable);
            }
            AIGoal goal = obj?.Goal;
            snapshot.Forbidden = result.Storable?.IsForbidden == true;
            snapshot.ExistingGoal = goal != null;
            snapshot.GoalId = goal?.Id ?? 0;
            snapshot.GoalJobTypeCode = goal?.JobIdH ?? 0;
            snapshot.GoalJobTypeId = goal == null
                ? string.Empty
                : JobTypeIdH.ResolveName(goal.JobIdH);
            snapshot.GoalStateCode = goal == null ? 0 : (int) goal.State;
            snapshot.GoalStateId = goal?.State.ToString() ?? string.Empty;
            snapshot.WorkerAssigned = goal?.Agent != null;
            snapshot.TrackedByIntelligentRecycling =
                trackedGoalsByObjectId.TryGetValue(data.Id,
                    out uint trackedGoalId)
                && goal != null
                && goal.Id == trackedGoalId;

            ClassifyObject(data, obj, result.Disposable, snapshot,
                out bool specialTarget, out bool specialReady);
            result.SpecialReady = specialReady;
            snapshot.SpecialTarget = specialTarget;
            snapshot.SpecialReadyForProcessing = specialReady;
            result.Recognized = snapshot.TargetType
                != IntelligentRecyclingTargetType.Unsupported;

            bool compatibleProcessorExists;
            bool thresholdAvailable;
            bool thresholdInvalid;
            float threshold;
            if (snapshot.ProcessorFamily
                    == IntelligentRecyclingProcessorFamily.Biological) {
                compatibleProcessorExists = processors.BiologicalCount > 0;
                thresholdInvalid = processors.BiologicalThresholdInvalid;
                thresholdAvailable = compatibleProcessorExists
                    && !thresholdInvalid
                    && processors.BiologicalThreshold >= 0f;
                threshold = processors.BiologicalThreshold;
            } else if (snapshot.ProcessorFamily
                    == IntelligentRecyclingProcessorFamily.Robotic) {
                compatibleProcessorExists = processors.RoboticCount > 0;
                thresholdInvalid = processors.RoboticThresholdInvalid;
                thresholdAvailable = compatibleProcessorExists
                    && !thresholdInvalid
                    && processors.RoboticThreshold >= 0f;
                threshold = processors.RoboticThreshold;
            } else {
                compatibleProcessorExists = false;
                thresholdAvailable = false;
                thresholdInvalid = false;
                threshold = -1f;
            }
            snapshot.CompatibleProcessorExists = compatibleProcessorExists;
            snapshot.EffectiveThresholdAvailable = thresholdAvailable;
            snapshot.EffectiveThreshold = thresholdAvailable ? threshold : 0f;
            snapshot.QualityRelation = ClassifyQualityRelation(
                data.Quality, specialTarget, thresholdAvailable,
                thresholdInvalid, threshold, out bool numericValuesValid);

            IntelligentRecyclingBlockers blockers =
                IntelligentRecyclingBlockers.None;
            if (!snapshot.Active) {
                blockers |= IntelligentRecyclingBlockers.Inactive;
            }
            if (snapshot.Stored) {
                blockers |= IntelligentRecyclingBlockers.Stored;
            }
            if (snapshot.Carried) {
                blockers |= IntelligentRecyclingBlockers.Carried;
            }
            if (snapshot.InDevice) {
                blockers |= IntelligentRecyclingBlockers.InDevice;
            }
            if (snapshot.Forbidden) {
                blockers |= IntelligentRecyclingBlockers.Forbidden;
            }
            if (snapshot.ExistingGoal) {
                blockers |= IntelligentRecyclingBlockers.ExistingGoal;
            }
            if (snapshot.WorkerAssigned) {
                blockers |= IntelligentRecyclingBlockers.WorkerAssigned;
            }
            if (snapshot.Reserved) {
                blockers |= IntelligentRecyclingBlockers.Reserved;
            }
            if (!numericValuesValid) {
                blockers |= IntelligentRecyclingBlockers.Invalid;
            }
            if (!Enabled) {
                blockers |= IntelligentRecyclingBlockers.MasterDisabled;
            }
            if (!snapshot.ParentSettingEnabled) {
                blockers |= IntelligentRecyclingBlockers.ParentDisabled;
            }
            if (!snapshot.CategorySettingEnabled) {
                blockers |= IntelligentRecyclingBlockers.CategoryDisabled;
            }
            if (snapshot.PermanentlyProtected) {
                blockers |= IntelligentRecyclingBlockers.PermanentlyProtected;
            }
            if (specialTarget && !specialReady) {
                blockers |= IntelligentRecyclingBlockers.SpecialCondition;
            }
            if (result.Recognized && !thresholdAvailable
                    && !thresholdInvalid) {
                blockers |= IntelligentRecyclingBlockers.NoProcessor;
            }
            if (!result.Recognized || result.Disposable == null) {
                blockers |= IntelligentRecyclingBlockers.Unsupported;
            }
            if (result.Storable == null) {
                blockers |= IntelligentRecyclingBlockers.NotStorable;
            }
            snapshot.Blockers = blockers;

            result.PolicyCategoryReady = Enabled
                && snapshot.ParentSettingEnabled
                && snapshot.CategorySettingEnabled
                && (!specialTarget || specialReady)
                && thresholdAvailable
                && numericValuesValid;
            ApplyOwnership(result);

            snapshot.IntelligentRecyclingCanQueueNow =
                snapshot.IntelligentRecyclingWouldClaimUnderPolicy
                && IsTransientQueueStateClear(snapshot, false)
                && result.Disposable?.CanOrderDispose(
                    DisposeCheck.BulkOrder) == true;
            snapshot.ReleasedForAutoHauling =
                snapshot.Ownership
                    == IntelligentRecyclingOwnership.ReleasedForExternalHauling
                && snapshot.Loose
                && snapshot.HasObjQuality;
            snapshot.CanCurrentlyBeHauled = snapshot.ReleasedForAutoHauling
                && IsTransientQueueStateClear(snapshot, false)
                && result.Storable != null;
            return result;
        }

        private static IntelligentRecyclingQualityRelation
                ClassifyQualityRelation(float quality, bool specialTarget,
                bool thresholdAvailable, bool thresholdInvalid,
                float threshold, out bool numericValuesValid) {
            numericValuesValid = IsFinite(quality)
                && !thresholdInvalid
                && (!thresholdAvailable || IsFinite(threshold));
            if (specialTarget || !numericValuesValid) {
                return IntelligentRecyclingQualityRelation.NotApplicable;
            }
            if (!thresholdAvailable) {
                return IntelligentRecyclingQualityRelation.NoThreshold;
            }
            if (quality < threshold) {
                return IntelligentRecyclingQualityRelation.BelowThreshold;
            }
            if (quality == threshold) {
                return IntelligentRecyclingQualityRelation.EqualToThreshold;
            }
            return IntelligentRecyclingQualityRelation.AboveThreshold;
        }

        private static bool IsFinite(float value) {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsTransientQueueStateClear(
                IntelligentRecyclingObjectSnapshotData snapshot,
                bool allowExistingGoal) {
            return snapshot.Loose
                && !snapshot.Forbidden
                && !snapshot.Reserved
                && !snapshot.PermanentlyProtected
                && (allowExistingGoal || !snapshot.ExistingGoal)
                && (snapshot.Blockers
                    & IntelligentRecyclingBlockers.Invalid) == 0;
        }

        private void ClassifyObject(ObjData data, Obj obj,
                DisposableComp disposable,
                IntelligentRecyclingObjectSnapshotData snapshot,
                out bool specialTarget, out bool specialReady) {
            specialTarget = false;
            specialReady = true;
            string definitionId = data.Def?.Id;
            if (string.Equals(definitionId, DefIds.ObjContainersHullpiercer,
                    StringComparison.Ordinal)) {
                specialTarget = true;
                snapshot.TargetType =
                    IntelligentRecyclingTargetType.Hullpiercer;
                snapshot.ProcessorFamily =
                    IntelligentRecyclingProcessorFamily.Robotic;
                snapshot.CategoryId = "hullpiercers";
                snapshot.CategoryDisplayName = "Hullpiercers";
                snapshot.ParentSettingEnabled = true;
                snapshot.CategorySettingEnabled = RecycleHullpiercers;
                specialReady = obj == null
                    || !obj.TryGetComponent<HullpiercerComp>(out var hullpiercer)
                    || !hullpiercer.IsMidFlight;
                return;
            }
            if (string.Equals(definitionId,
                    DefIds.ObjContainersStorageCapsule,
                    StringComparison.Ordinal)) {
                specialTarget = true;
                snapshot.TargetType =
                    IntelligentRecyclingTargetType.StorageCapsule;
                snapshot.ProcessorFamily =
                    IntelligentRecyclingProcessorFamily.Robotic;
                snapshot.CategoryId = "opened-storage-capsules";
                snapshot.CategoryDisplayName = "Opened storage capsules";
                snapshot.ParentSettingEnabled = true;
                snapshot.CategorySettingEnabled =
                    RecycleOpenedStorageCapsules;
                specialReady = obj != null
                    && obj.TryGetComponent<StorageCapsuleComp>(out var capsule)
                    && capsule.IsOpen;
                return;
            }
            if (string.Equals(definitionId,
                    DefIds.ObjContainersQuestPackage,
                    StringComparison.Ordinal)) {
                specialTarget = true;
                snapshot.TargetType =
                    IntelligentRecyclingTargetType.SafeBox;
                snapshot.ProcessorFamily =
                    IntelligentRecyclingProcessorFamily.Robotic;
                snapshot.CategoryId = "opened-safe-boxes";
                snapshot.CategoryDisplayName = "Opened Safe Boxes";
                snapshot.ParentSettingEnabled = true;
                snapshot.CategorySettingEnabled = RecycleOpenedSafeBoxes;
                specialReady = obj != null
                    && obj.TryGetComponent<QuestPackageComp>(out var safeBox)
                    && safeBox.IsOpen;
                return;
            }
            if (string.Equals(definitionId, ArtilleryFungusDefinitionId,
                    StringComparison.Ordinal)) {
                specialTarget = true;
                snapshot.TargetType =
                    IntelligentRecyclingTargetType.ArtilleryFungus;
                snapshot.ProcessorFamily =
                    IntelligentRecyclingProcessorFamily.Biological;
                snapshot.CategoryId = "artillery-fungus";
                snapshot.CategoryDisplayName = "Artillery Fungus";
                snapshot.ParentSettingEnabled = true;
                snapshot.CategorySettingEnabled = RecycleArtilleryFungus;
                return;
            }
            if (data.BodyPart != null) {
                bool biological = data.BodyPart.IsBiological;
                snapshot.TargetType = biological
                    ? IntelligentRecyclingTargetType.BiologicalBodyPart
                    : IntelligentRecyclingTargetType.RoboticBodyPart;
                snapshot.ProcessorFamily = biological
                    ? IntelligentRecyclingProcessorFamily.Biological
                    : IntelligentRecyclingProcessorFamily.Robotic;
                snapshot.CategoryId = data.BodyPart.SlotTypeId;
                snapshot.CategoryDisplayName = data.BodyPart.SlotType?.NameT
                    ?? data.BodyPart.SlotTypeId;
                snapshot.ParentSettingEnabled = biological
                    ? RecycleBiologicalParts
                    : RecycleRoboticParts;
                snapshot.CategorySettingEnabled = biological
                    ? IsBiologicalCategoryEnabled(data.BodyPart.SlotTypeId)
                    : IsRoboticCategoryEnabled(data.BodyPart.SlotTypeId);
                snapshot.PermanentlyProtected = IsProtectedMindPart(data);
                snapshot.ProtectionReason = snapshot.PermanentlyProtected
                    ? "Brain, Core, HoldsMind, or mind-associated body part."
                    : string.Empty;
                return;
            }
            if (data.Item != null) {
                snapshot.TargetType = IntelligentRecyclingTargetType.Equipment;
                snapshot.ProcessorFamily = disposable?.IsBiological == true
                    ? IntelligentRecyclingProcessorFamily.Biological
                    : IntelligentRecyclingProcessorFamily.Robotic;
                snapshot.CategoryId = data.Item.SlotId;
                snapshot.CategoryDisplayName = data.Item.SlotType?.Name
                    ?? data.Item.SlotId;
                snapshot.ParentSettingEnabled = RecycleEquipment;
                snapshot.CategorySettingEnabled =
                    IsEquipmentCategoryEnabled(data.Item.SlotId);
                return;
            }
            snapshot.TargetType = IntelligentRecyclingTargetType.Unsupported;
            snapshot.ProcessorFamily =
                IntelligentRecyclingProcessorFamily.None;
            snapshot.ParentSettingEnabled = false;
            snapshot.CategorySettingEnabled = false;
        }

        private static void ApplyOwnership(ObjectEvaluation evaluation) {
            IntelligentRecyclingObjectSnapshotData snapshot =
                evaluation.Snapshot;
            if (!evaluation.Recognized || evaluation.Disposable == null) {
                snapshot.Ownership = IntelligentRecyclingOwnership.NotManaged;
                snapshot.DecisionReason = snapshot.HasObjQuality
                    ? IntelligentRecyclingDecisionReason.UnsupportedTarget
                    : IntelligentRecyclingDecisionReason.NotQualityBearing;
                snapshot.DiagnosticReason = snapshot.HasObjQuality
                    ? "Quality-bearing object is not a supported Intelligent Recycling target."
                    : "Object is not a supported quality-bearing target.";
                return;
            }
            if (snapshot.PermanentlyProtected) {
                snapshot.Ownership =
                    IntelligentRecyclingOwnership.PermanentlyProtected;
                snapshot.DecisionReason =
                    IntelligentRecyclingDecisionReason.PermanentlyProtected;
                snapshot.DiagnosticReason = snapshot.ProtectionReason;
                return;
            }
            if (snapshot.SpecialTarget) {
                snapshot.Ownership =
                    IntelligentRecyclingOwnership.IntelligentRecycling;
                snapshot.DecisionReason =
                    IntelligentRecyclingDecisionReason.SpecialTargetReserved;
                snapshot.DiagnosticReason = snapshot.SpecialReadyForProcessing
                    ? "Explicit Intelligent Recycling-only special target."
                    : "Special target is reserved for Intelligent Recycling but is not ready.";
                snapshot.IntelligentRecyclingWouldClaimUnderPolicy =
                    evaluation.PolicyCategoryReady;
                return;
            }
            if ((snapshot.Blockers
                    & IntelligentRecyclingBlockers.Invalid) != 0) {
                SetUnknown(snapshot,
                    IntelligentRecyclingDecisionReason.InvalidObject,
                    "Object quality or the applicable processor threshold is non-finite.");
                return;
            }
            if ((snapshot.Blockers
                    & IntelligentRecyclingBlockers.MasterDisabled) != 0) {
                SetUnknown(snapshot,
                    IntelligentRecyclingDecisionReason.MasterDisabled,
                    "Intelligent Recycling automation is disabled.");
                return;
            }
            if ((snapshot.Blockers
                    & IntelligentRecyclingBlockers.ParentDisabled) != 0) {
                SetUnknown(snapshot,
                    IntelligentRecyclingDecisionReason.ParentDisabled,
                    "The parent target category is disabled.");
                return;
            }
            if ((snapshot.Blockers
                    & IntelligentRecyclingBlockers.CategoryDisabled) != 0) {
                SetUnknown(snapshot,
                    IntelligentRecyclingDecisionReason.CategoryDisabled,
                    "The specific target category is disabled.");
                return;
            }
            if ((snapshot.Blockers
                    & IntelligentRecyclingBlockers.NoProcessor) != 0) {
                SetUnknown(snapshot,
                    IntelligentRecyclingDecisionReason.NoCompatibleProcessor,
                    "No constructed compatible processor provides a threshold.");
                return;
            }
            if (snapshot.QualityRelation
                    == IntelligentRecyclingQualityRelation.BelowThreshold) {
                snapshot.Ownership =
                    IntelligentRecyclingOwnership.IntelligentRecycling;
                snapshot.DecisionReason =
                    IntelligentRecyclingDecisionReason.OwnedBelowThreshold;
                snapshot.DiagnosticReason =
                    "Quality is below the effective processor threshold.";
                snapshot.IntelligentRecyclingWouldClaimUnderPolicy = true;
                return;
            }
            if (snapshot.QualityRelation
                    == IntelligentRecyclingQualityRelation.EqualToThreshold) {
                snapshot.Ownership =
                    IntelligentRecyclingOwnership.IntelligentRecycling;
                snapshot.DecisionReason =
                    IntelligentRecyclingDecisionReason.OwnedEqualThreshold;
                snapshot.DiagnosticReason =
                    "Quality equals the threshold; equality remains Intelligent Recycling-owned.";
                snapshot.IntelligentRecyclingWouldClaimUnderPolicy = true;
                return;
            }
            if (snapshot.QualityRelation
                    == IntelligentRecyclingQualityRelation.AboveThreshold) {
                snapshot.Ownership =
                    IntelligentRecyclingOwnership.ReleasedForExternalHauling;
                snapshot.DecisionReason =
                    IntelligentRecyclingDecisionReason.ReleasedAboveThreshold;
                snapshot.DiagnosticReason =
                    "Quality is strictly above the effective processor threshold.";
                return;
            }
            SetUnknown(snapshot, IntelligentRecyclingDecisionReason.Unknown,
                "No authoritative ownership decision is available.");
        }

        private static void SetUnknown(
                IntelligentRecyclingObjectSnapshotData snapshot,
                IntelligentRecyclingDecisionReason reason,
                string diagnostic) {
            snapshot.Ownership = IntelligentRecyclingOwnership.Unknown;
            snapshot.DecisionReason = reason;
            snapshot.DiagnosticReason = diagnostic;
        }

        private IntelligentRecyclingScanSnapshot BuildScanSnapshot() {
            return new IntelligentRecyclingScanSnapshot(
                ScanIntervalTicks,
                lastScanTick,
                nextScanTick,
                scanRequested || pendingObjectScanIds.Count > 0,
                trackedGoalsReady,
                loadedState != null && Enabled,
                LastEligibleLoose,
                LastQueued,
                LastAboveThreshold,
                LastNoCompatibleProcessor,
                LastProtectedMindParts,
                LastBlockedByTask,
                LastForbidden,
                LastReserved,
                LastParentDisabled,
                LastCategoryDisabled,
                LastStoredOrNotLoose,
                LastCarried,
                LastInactiveOrInvalid,
                LastUnsupportedTarget,
                LastSpecialConditionNotReady,
                LastVanillaOrderRejected,
                LegacyLastIgnoredByQuality);
        }

        private IntelligentRecyclingTotalsSnapshot BuildTotalsSnapshot() {
            return new IntelligentRecyclingTotalsSnapshot(
                BiologicalTasksQueued,
                RoboticTasksQueued,
                EquipmentTasksQueued,
                ActiveTrackedTasks,
                BiologicalBodyPartTasksQueued,
                RoboticBodyPartTasksQueued,
                EquipmentItemTasksQueued,
                HullpiercerTasksQueued,
                StorageCapsuleTasksQueued,
                SafeBoxTasksQueued,
                ArtilleryFungusTasksQueued,
                DeadCrawlerTasksQueued,
                StasisPodTasksQueued,
                BreachCapsulePortTasksQueued,
                LostShuttleReplacementsQueued,
                trackedGoalsByObjectId.Count,
                trackedGoalsByBeingId.Count,
                trackedGoalsByStasisPodId.Count,
                trackedGoalsByBreachCapsuleId.Count,
                lostShuttleReplacements.Count);
        }

        private List<IntelligentRecyclingTrackedTaskSnapshot>
                BuildTrackedTaskSnapshotsInternal(ProcessorContext processors) {
            List<IntelligentRecyclingTrackedTaskSnapshot> result =
                new List<IntelligentRecyclingTrackedTaskSnapshot>();
            Dictionary<uint, ObjData> objects = S.Query.GetAllObjs.Ask();
            foreach (KeyValuePair<uint, uint> tracked
                    in trackedGoalsByObjectId) {
                ObjData data = null;
                if (objects != null) {
                    objects.TryGetValue(tracked.Key, out data);
                }
                ObjectEvaluation evaluation = data == null
                    ? null
                    : EvaluateObject(data, processors);
                AIGoal goal = data?.Obj?.Goal;
                AddTrackedTask(result, tracked.Value, tracked.Key,
                    IntelligentRecyclingTrackedTargetKind.Object,
                    data?.Def?.Id,
                    data?.Def?.NameT,
                    evaluation?.Snapshot?.CategoryId,
                    evaluation?.Snapshot?.TargetType
                        ?? IntelligentRecyclingTargetType.Unsupported,
                    goal);
            }

            Dictionary<uint, BeingData> beings = S.Query.AllBeingData.Ask();
            foreach (KeyValuePair<uint, uint> tracked
                    in trackedGoalsByBeingId) {
                BeingData data = null;
                if (beings != null) {
                    beings.TryGetValue(tracked.Key, out data);
                }
                AddTrackedTask(result, tracked.Value, tracked.Key,
                    IntelligentRecyclingTrackedTargetKind.Being,
                    data?.Species?.Id,
                    data?.Species?.Name,
                    "dead-crawlers",
                    IntelligentRecyclingTargetType.DeadCrawler,
                    data?.Being?.Goal);
            }

            List<StasisPodComp> foundPods = new List<StasisPodComp>();
            S.Components.FindAll(foundPods);
            foreach (KeyValuePair<int, uint> tracked
                    in trackedGoalsByStasisPodId) {
                StasisPodComp pod = foundPods.Find(
                    value => value != null && value.EntityId == tracked.Key);
                AddTrackedTask(result, tracked.Value, tracked.Key,
                    IntelligentRecyclingTrackedTargetKind.StasisPod,
                    pod?.Tile?.Definition?.Id,
                    pod?.Tile?.Definition?.NameT,
                    "stasis-pods",
                    IntelligentRecyclingTargetType.StasisPod,
                    pod?.Tile?.Goal);
            }

            List<BreachCapsuleComp> foundBreach =
                new List<BreachCapsuleComp>();
            S.Components.FindAll(foundBreach);
            foreach (KeyValuePair<int, uint> tracked
                    in trackedGoalsByBreachCapsuleId) {
                BreachCapsuleComp capsule = foundBreach.Find(
                    value => value != null && value.EntityId == tracked.Key);
                AddTrackedTask(result, tracked.Value, tracked.Key,
                    IntelligentRecyclingTrackedTargetKind.BreachCapsulePort,
                    capsule?.Tile?.Definition?.Id,
                    capsule?.Tile?.Definition?.NameT,
                    "breach-capsule-ports",
                    IntelligentRecyclingTargetType.BreachCapsulePort,
                    capsule?.Tile?.Goal);
            }

            foreach (LostShuttleReplacementRecord record
                    in lostShuttleReplacements.Values) {
                Tile tile = record.Phase
                        == LostShuttleReplacementPhase.Deconstructing
                    ? S.FindEntity<Tile>(record.OriginalEntityId)
                    : S.FindEntity<Tile>(record.CurrentEntityId);
                AIGoal goal = tile?.Goal;
                uint goalId = record.Phase
                        == LostShuttleReplacementPhase.Deconstructing
                    ? record.DeconstructionGoalId
                    : goal?.Id ?? 0;
                AddTrackedTask(result, goalId,
                    tile?.Id ?? record.OriginalEntityId,
                    IntelligentRecyclingTrackedTargetKind.LostShuttle,
                    record.DefinitionId,
                    tile?.Definition?.NameT ?? "Shuttle",
                    "replace-lost-shuttles",
                    IntelligentRecyclingTargetType.LostShuttle,
                    goal);
            }
            return result;
        }

        private static void AddTrackedTask(
                List<IntelligentRecyclingTrackedTaskSnapshot> result,
                uint trackedGoalId, long targetId,
                IntelligentRecyclingTrackedTargetKind targetKind,
                string definitionOrSpeciesId, string displayName,
                string categoryId, IntelligentRecyclingTargetType targetType,
                AIGoal goal) {
            bool active = goal != null && goal.Id == trackedGoalId;
            AIAgentComp agent = active ? goal.Agent : null;
            result.Add(new IntelligentRecyclingTrackedTaskSnapshot(
                trackedGoalId,
                targetId,
                targetKind,
                definitionOrSpeciesId,
                displayName,
                categoryId,
                targetType,
                active ? (int) goal.State : 0,
                active ? goal.State.ToString() : "Missing",
                active ? goal.JobIdH : 0,
                active ? JobTypeIdH.ResolveName(goal.JobIdH) : string.Empty,
                agent != null,
                agent?.BeingData?.Id ?? 0,
                agent?.BeingData?.Mind?.Id ?? 0,
                active ? goal.CreatedAt : 0,
                active ? goal.FailReason : string.Empty,
                active ? goal.TimesFailed : 0,
                active ? goal.CanRetryAfterTick : 0,
                active && goal.NonRetryable,
                true,
                active));
        }

        private List<IntelligentRecyclingSpecialTargetSnapshot>
                BuildSpecialTargetSnapshotsInternal(ProcessorContext processors) {
            List<IntelligentRecyclingSpecialTargetSnapshot> result =
                new List<IntelligentRecyclingSpecialTargetSnapshot>();
            Dictionary<uint, ObjData> objects = S.Query.GetAllObjs.Ask();
            if (objects != null) {
                foreach (ObjData data in objects.Values) {
                    if (data == null) {
                        continue;
                    }
                    ObjectEvaluation evaluation = EvaluateObject(data, processors);
                    IntelligentRecyclingTargetType type =
                        evaluation.Snapshot.TargetType;
                    if (type != IntelligentRecyclingTargetType.Hullpiercer
                            && type != IntelligentRecyclingTargetType.StorageCapsule
                            && type != IntelligentRecyclingTargetType.SafeBox
                            && type != IntelligentRecyclingTargetType.ArtilleryFungus) {
                        continue;
                    }
                    bool midFlight = data.Obj != null
                        && data.Obj.TryGetComponent<HullpiercerComp>(
                            out var hullpiercer)
                        && hullpiercer.IsMidFlight;
                    bool open = data.Obj != null
                        && ((data.Obj.TryGetComponent<StorageCapsuleComp>(
                                out var capsule)
                            && capsule.IsOpen)
                            || (data.Obj.TryGetComponent<QuestPackageComp>(
                                    out var safeBox)
                                && safeBox.IsOpen));
                    result.Add(new IntelligentRecyclingSpecialTargetSnapshot(
                        data.Id,
                        type,
                        data.Def?.Id,
                        data.Def?.NameT,
                        true,
                        evaluation.Snapshot.CategorySettingEnabled,
                        evaluation.Snapshot.Active,
                        evaluation.Snapshot.IntelligentRecyclingCanQueueNow,
                        true,
                        evaluation.Snapshot.CompatibleProcessorExists,
                        false,
                        midFlight,
                        open,
                        false,
                        false,
                        false,
                        false,
                        false,
                        evaluation.Snapshot.DiagnosticReason));
                }
            }

            List<StasisPodComp> pods = new List<StasisPodComp>();
            S.Components.FindAll(pods);
            foreach (StasisPodComp pod in pods) {
                Tile tile = pod?.Tile;
                if (tile == null) {
                    continue;
                }
                bool merchantProtected = tile.TryGetComponent<
                        StasisPodMerchantComp>(out var merchant)
                    && (merchant.CanTrade || merchant.IsTraderActive);
                bool waitingComplete =
                    IsStasisPodReadyForDeconstruction(pod);
                bool ready = Enabled && DeconstructStasisPods
                    && tile.IsActive && tile.IsConstructed
                    && waitingComplete && !merchantProtected
                    && tile.Goal == null
                    && tile.Constructable.CanDeconstruct();
                result.Add(new IntelligentRecyclingSpecialTargetSnapshot(
                    pod.EntityId,
                    IntelligentRecyclingTargetType.StasisPod,
                    tile.Definition?.Id,
                    tile.Definition?.NameT,
                    true,
                    DeconstructStasisPods,
                    tile.IsActive,
                    ready,
                    true,
                    false,
                    false,
                    false,
                    pod.IsOpened,
                    merchantProtected,
                    false,
                    false,
                    false,
                    false,
                    merchantProtected
                        ? "Protected while Merchant/Trade activity is available or active."
                        : waitingComplete
                            ? "Opened Stasis Pod."
                            : "Stasis Pod Waiting lifecycle is not complete."));
            }

            List<BreachCapsuleComp> breaches =
                new List<BreachCapsuleComp>();
            S.Components.FindAll(breaches);
            foreach (BreachCapsuleComp capsule in breaches) {
                Tile tile = capsule?.Tile;
                if (tile == null) {
                    continue;
                }
                bool released = capsule.IsRaidStarted;
                bool ready = Enabled && DeconstructBreachCapsulePorts
                    && tile.IsActive && tile.IsConstructed
                    && IsBreachCapsuleReadyForDeconstruction(capsule)
                    && tile.Goal == null
                    && tile.Constructable.CanDeconstruct();
                result.Add(new IntelligentRecyclingSpecialTargetSnapshot(
                    capsule.EntityId,
                    IntelligentRecyclingTargetType.BreachCapsulePort,
                    tile.Definition?.Id,
                    tile.Definition?.NameT,
                    true,
                    DeconstructBreachCapsulePorts,
                    tile.IsActive,
                    ready,
                    true,
                    false,
                    false,
                    false,
                    capsule.IsOpened,
                    false,
                    released,
                    false,
                    false,
                    false,
                    released
                        ? "Raiders were released or the failed spawn was resolved."
                        : "Raider release is unresolved."));
            }

            List<ShuttleComp> foundShuttles = new List<ShuttleComp>();
            S.Components.FindAll(foundShuttles);
            foreach (ShuttleComp shuttle in foundShuttles) {
                Tile tile = shuttle?.Tile;
                if (tile == null || !shuttle.IsLost) {
                    continue;
                }
                string key = MakeLostShuttleKey(tile.Definition.LayerId,
                    tile.PosIdx, tile.DefinitionId);
                bool owned = lostShuttleReplacements.ContainsKey(key);
                bool suppressed = suppressedLostShuttleEntityIds.Contains(tile.Id);
                bool ready = Enabled && ReplaceLostShuttles
                    && tile.IsActive && tile.IsConstructed && !suppressed
                    && (owned || (tile.Goal == null
                        && tile.Constructable.CanDeconstruct()
                        && S.Research.IsDefUnlocked(tile.Definition)));
                string detail = owned
                    ? "Lost Shuttle pad replacement is in progress."
                    : suppressed
                        ? "Lost Shuttle pad replacement was suppressed after its deconstruction order was canceled."
                        : ready
                            ? "Lost Shuttle pad is ready for replacement."
                            : "Lost Shuttle pad is not currently eligible for replacement.";
                result.Add(new IntelligentRecyclingSpecialTargetSnapshot(
                    tile.Id,
                    IntelligentRecyclingTargetType.LostShuttle,
                    tile.DefinitionId,
                    tile.Definition?.NameT,
                    true,
                    ReplaceLostShuttles,
                    tile.IsActive,
                    ready,
                    owned,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    detail));
            }

            Dictionary<uint, BeingData> beings = S.Query.AllBeingData.Ask();
            if (beings != null) {
                foreach (BeingData data in beings.Values) {
                    IntelligentRecyclingSpecialTargetSnapshot snapshot =
                        BuildBeingSpecialSnapshot(data, processors);
                    if (snapshot != null) {
                        result.Add(snapshot);
                    }
                }
            }
            return result;
        }

        private IntelligentRecyclingSpecialTargetSnapshot
                BuildBeingSpecialSnapshot(BeingData data,
                ProcessorContext processors) {
            if (data?.Species?.IdH != SpeciesIdH.Crawler) {
                return null;
            }
            Being being = data.Being;
            bool alive = data.HasStateFlag(BeingState.Alive);
            bool dead = data.HasStateFlag(BeingState.Dead);
            bool incapacitated = data.Body?.IsIncapacitated == true;
            bool active = being != null && being.IsActive;
            bool processor = processors.BiologicalThreshold >= 0f;
            bool canOrder = false;
            if (being != null
                    && being.TryGetComponent<DisposableComp>(out var disposable)) {
                canOrder = disposable.IsBiological
                    && disposable.CanOrderDispose(DisposeCheck.BulkOrder);
            }
            bool ready = Enabled && DisposeDeadCrawlers
                && dead && active && processor
                && being.Goal == null && canOrder;
            string detail = dead
                ? ready
                    ? "Dead Crawler is ready for disposal."
                    : "Dead Crawler is reserved for Intelligent Recycling but currently blocked."
                : incapacitated
                    ? "Incapacitated living Crawler is protected."
                    : "Living Crawler is protected.";
            return new IntelligentRecyclingSpecialTargetSnapshot(
                data.Id,
                IntelligentRecyclingTargetType.DeadCrawler,
                data.Species.Id,
                data.Species.Name,
                true,
                DisposeDeadCrawlers,
                active,
                ready,
                true,
                processor,
                false,
                false,
                false,
                false,
                false,
                alive,
                incapacitated,
                dead,
                detail);
        }

        private IntelligentRecyclingUiSnapshot BuildUiSnapshot() {
            bool capturing = panelShortcut?.IsCapturing == true;
            string status = capturing
                ? "Waiting for key"
                : panelShortcut?.StatusMessage?.IndexOf("conflict",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Shortcut conflict"
                    : panelShortcut?.HasShortcut == true
                        ? "Ready"
                        : "Not assigned";
            return new IntelligentRecyclingUiSnapshot(
                panel?.IsOpen == true,
                topWidget != null,
                topWidget?.IsCreated == true,
                panelShortcut?.HasShortcut == true,
                panelShortcut?.DisplayName,
                capturing,
                status,
                panelShortcut?.StatusMessage);
        }

        private static void MarkApiRevision() {
            unchecked {
                apiRevision++;
                if (apiRevision <= 0) {
                    apiRevision = 1;
                }
            }
        }
    }
}
