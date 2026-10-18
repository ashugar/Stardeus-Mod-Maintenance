using System;
using System.Collections.Generic;
using System.Reflection;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Items;
using Game.Systems.Nanobots;
using Game.Systems.Rendering;
using Game.Systems.Creatures;
using HarmonyLib;
using KL.Grid;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.QuantumStorage
{
    internal static partial class QuantumNanobotObjectHauling
    {
        private const string SavePrefix = "QuantumObjectHaul.";
        private const string CountKey = SavePrefix + "Count";
        private const float MinimumDurationTicks = 1f;
        private const int GoalPreparationBudgetPerTick = 256;
        private const int GoalReconciliationBudgetPerTick = 64;
        private const int HiveAssignmentBudgetPerTick = 16;
        private const int CandidateChecksPerHive = 32;
        private const int DormantRetryTicks = Consts.TicksPer5Minutes;

        private const string OrganicNetworkId = "OrganicParts";
        private const string RoboticNetworkId = "RoboticParts";
        private const string WeaponsNetworkId = "Weapons";
        private const string OrganicGroupId = "BodyPartOrganic";
        private const string RoboticGroupId = "BodyPartRobotic";
        private const string WeaponGroupId = "Weapon";
        private const string ClothingGroupId = "Clothing";

        private static readonly int OrganicGroupH = Hashes.S(OrganicGroupId);
        private static readonly int RoboticGroupH = Hashes.S(RoboticGroupId);
        private static readonly int WeaponGroupH = Hashes.S(WeaponGroupId);
        private static readonly int ClothingGroupH = Hashes.S(ClothingGroupId);
        private static readonly int NanobotsResearchH = Hashes.S(DefIds.ResearchRoboticsNanobots);
        private static readonly int BasicBotPartsResearchH = Hashes.S(DefIds.ResearchRoboticsPartsBasic);
        private static readonly int BasicSyntheticOrgansResearchH = Hashes.S(DefIds.ResearchLifeSupportGrowingPartsBasic);
        private static readonly FieldInfo WorkingHivesField = AccessTools.Field(typeof(NanobotSys), "workingHives");
        private static readonly MethodInfo MarkHaulingWorkedMethod = AccessTools.Method(typeof(NanobotHiveComp), "MarkHaulingWorked");

        private static readonly Dictionary<uint, AIGoal> TrackedGoals = new Dictionary<uint, AIGoal>();
        private static readonly Dictionary<uint, QuantumNanobotPreparedCandidate> PreparedCandidates = new Dictionary<uint, QuantumNanobotPreparedCandidate>();
        private static readonly List<uint> DirtyGoalIds = new List<uint>();
        private static readonly HashSet<uint> DirtyGoalSet = new HashSet<uint>();
        private static readonly List<uint> ReconcileGoalIds = new List<uint>();
        private static readonly HashSet<uint> ReconcileGoalSet = new HashSet<uint>();
        private static readonly HashSet<uint> ReadyGoalSet = new HashSet<uint>();
        private static readonly Dictionary<int, QuantumNanobotReadyAreaQueue> ReadyGoalsByArea = new Dictionary<int, QuantumNanobotReadyAreaQueue>();
        private static readonly Dictionary<uint, int> ReadyQueuedAreas = new Dictionary<uint, int>();
        private static readonly HashSet<uint> OrganicTrackedGoalIds = new HashSet<uint>();
        private static readonly HashSet<uint> RoboticTrackedGoalIds = new HashSet<uint>();
        private static readonly HashSet<uint> WeaponTrackedGoalIds = new HashSet<uint>();
        private static readonly HashSet<uint> ClothingTrackedGoalIds = new HashSet<uint>();
        private static readonly Dictionary<uint, QuantumNanobotObjectHaulOp> ActiveOps = new Dictionary<uint, QuantumNanobotObjectHaulOp>();
        private static readonly Dictionary<int, object> ActiveOpsByHive = new Dictionary<int, object>();
        private static readonly HashSet<int> HivesUsedByCore = new HashSet<int>();
        private static readonly List<uint> GoalIdsToRemove = new List<uint>();
        private static readonly List<QuantumNanobotObjectHaulOp> LoadedOps = new List<QuantumNanobotObjectHaulOp>();

        private static GameState state;
        private static bool bindingsReported;
        private static bool disabledForSession;
        private static int nextHiveIndex;
        private static int dirtyGoalCursor;
        private static int reconcileGoalCursor;

        public static bool HasActiveOps => ActiveOps.Count != 0;

        public static bool BindingsAvailable => WorkingHivesField != null && MarkHaulingWorkedMethod != null;

        public static bool IsAvailable => BindingsAvailable && !disabledForSession;

        public static void ReportBindings()
        {
            if (bindingsReported)
            {
                return;
            }

            bindingsReported = true;
            if (BindingsAvailable)
            {
                D.Log("Quantum Storage enabled research-gated Nanobot Hive hauling into Quantum object storage.");
            }
            else
            {
                D.Warn("Quantum Storage could not bind the current Nanobot Hive hauling lifecycle; object-storage nanobot hauling is disabled, while shared storage remains available.");
            }
        }

        public static void BeginCoreTick(GameState gameState)
        {
            BindState(gameState);
            HivesUsedByCore.Clear();
        }

        public static bool BeforeCoreWork(NanobotHiveComp hive)
        {
            if (hive == null)
            {
                return true;
            }

            if (BindingsAvailable && ActiveOpsByHive.ContainsKey(hive.EntityId))
            {
                return false;
            }

            HivesUsedByCore.Add(hive.EntityId);
            return true;
        }

        public static void RegisterGoal(StorableObjComp storable, AIGoal goal)
        {
            if (storable == null || goal == null)
            {
                return;
            }

            if (TryRegisterTaccReturnGoal(storable, goal) || disabledForSession ||
                !IsSupportedObjectGroup(storable.GroupH))
            {
                return;
            }

            bool isNew = !TrackedGoals.ContainsKey(goal.Id);
            TrackedGoals[goal.Id] = goal;
            TrackGoalNetwork(goal.Id, storable.GroupH);
            if (isNew && ReconcileGoalSet.Add(goal.Id))
            {
                ReconcileGoalIds.Add(goal.Id);
            }
            MarkGoalDirty(goal.Id);
            if (IsAvailable && IsResearchGateOpen(storable.GroupH) && !goal.IsNanobotCompatible)
            {
                goal.NanobotCompatible();
            }
        }

        public static void OnNanobotTick(NanobotSys nanobotSystem, long ticks)
        {
            if (!BindingsAvailable || nanobotSystem?.S == null)
            {
                return;
            }

            BindState(nanobotSystem.S);
            List<NanobotHiveComp> workingHives = GetWorkingHives(nanobotSystem);
            bool haulingEnabled = state.Query.IsNanobotsJobEnabled.Ask(JobTypeIdH.Hauling);
            if (!disabledForSession)
            {
                ValidateActiveOps(ticks);
                MaintainActiveOps(workingHives, haulingEnabled);
                if (haulingEnabled && workingHives != null && workingHives.Count != 0)
                {
                    ProcessDirtyGoals(ticks);
                    ReconcileTrackedGoals(ticks);
                    AssignReadyGoals(workingHives, ticks, nanobotSystem);
                }
            }

            TaccOnNanobotTick(nanobotSystem, ticks, workingHives, haulingEnabled);
        }

        public static void OnTickProgress(float ticksPerFrame, long ticks)
        {
            if (state == null || ticksPerFrame <= 0f)
            {
                return;
            }

            if (ActiveOps.Count != 0)
            {
                GoalIdsToRemove.Clear();
                foreach (KeyValuePair<uint, QuantumNanobotObjectHaulOp> entry in ActiveOps)
                {
                    QuantumNanobotObjectHaulOp op = entry.Value;
                    if (!IsActiveOpValid(op))
                    {
                        GoalIdsToRemove.Add(entry.Key);
                        continue;
                    }

                    op.Progress = Mathf.Min(1f, op.Progress + ticksPerFrame / op.DurationTicks);
                    Teleport(op.Data.Obj, Vector2.Lerp(op.From, op.To, op.Progress));
                    if (op.Progress >= 1f)
                    {
                        GoalIdsToRemove.Add(entry.Key);
                    }
                }

                for (int i = 0; i < GoalIdsToRemove.Count; i++)
                {
                    if (!ActiveOps.TryGetValue(GoalIdsToRemove[i], out QuantumNanobotObjectHaulOp op))
                    {
                        continue;
                    }

                    if (IsActiveOpValid(op))
                    {
                        Complete(op, ticks);
                    }
                    else
                    {
                        Abort(op, ticks, true);
                    }
                }
                GoalIdsToRemove.Clear();
            }

            TaccOnTickProgress(ticksPerFrame, ticks);
        }

        public static void Save(ref ComponentData data)
        {
            if (ActiveOps.Count == 0 && data != null)
            {
                data.SetInt(CountKey, 0);
            }
            else if (ActiveOps.Count != 0)
            {
                data ??= new ComponentData(0, NanobotSys.SysId);
                data.SetInt(CountKey, ActiveOps.Count);
                int index = 0;
                foreach (QuantumNanobotObjectHaulOp op in ActiveOps.Values)
                {
                    op.Save(data, SavePrefix + index + ".");
                    index++;
                }
            }

            TaccSave(ref data);
        }

        public static void Load(GameState gameState, ComponentData data)
        {
            disabledForSession = false;
            BindState(gameState, true);
            LoadedOps.Clear();
            TaccLoad(data);
            if (data == null)
            {
                return;
            }

            int count = data.GetInt(CountKey, 0);
            for (int i = 0; i < count; i++)
            {
                QuantumNanobotObjectHaulOp op = QuantumNanobotObjectHaulOp.Load(data, SavePrefix + i + ".");
                if (op != null)
                {
                    LoadedOps.Add(op);
                }
            }
        }

        public static void AfterLoad(GameState gameState)
        {
            disabledForSession = false;
            if (!BindingsAvailable)
            {
                LoadedOps.Clear();
                return;
            }

            BindState(gameState);
            RegisterLoadedGoals();
            for (int i = 0; i < LoadedOps.Count; i++)
            {
                QuantumNanobotObjectHaulOp op = LoadedOps[i];
                if (!TryRecover(op))
                {
                    Abort(op, G.Ticks, true);
                }
            }
            LoadedOps.Clear();
        }

        public static void Reset()
        {
            TaccReset();
            QuantumNanobotObjectHaulOp[] ops = new QuantumNanobotObjectHaulOp[ActiveOps.Count];
            ActiveOps.Values.CopyTo(ops, 0);
            for (int i = 0; i < ops.Length; i++)
            {
                ReleaseObject(ops[i], true);
            }

            ActiveOps.Clear();
            ActiveOpsByHive.Clear();
            ClearSchedulingState();
            HivesUsedByCore.Clear();
            GoalIdsToRemove.Clear();
            LoadedOps.Clear();
            state = null;
            disabledForSession = false;
        }

        public static void DisableForSession(Exception exception, string operation)
        {
            if (disabledForSession)
            {
                return;
            }

            disabledForSession = true;
            QuantumNanobotObjectHaulOp[] ops = new QuantumNanobotObjectHaulOp[ActiveOps.Count];
            ActiveOps.Values.CopyTo(ops, 0);
            for (int i = 0; i < ops.Length; i++)
            {
                try
                {
                    ReleaseObject(ops[i], true);
                    if (ops[i].Goal != null)
                    {
                        ops[i].Goal.Flags &= ~AIGoalFlags.NanobotWorkable;
                    }
                }
                catch
                {
                }
                RemoveOp(ops[i]);
            }
            ActiveOps.Clear();
            ClearSchedulingState();
            D.LogEx(exception, "Quantum Storage disabled object-storage nanobot hauling for this session after a failure during {0}; shared storage and Core hauling remain available.", operation);
        }

        private static void BindState(GameState gameState, bool forceReset = false)
        {
            if (!forceReset && ReferenceEquals(state, gameState))
            {
                return;
            }

            TaccStateReset();
            ActiveOps.Clear();
            ActiveOpsByHive.Clear();
            ClearSchedulingState();
            HivesUsedByCore.Clear();
            GoalIdsToRemove.Clear();
            LoadedOps.Clear();
            state = gameState;
        }

        private static void RegisterLoadedGoals()
        {
            if (state?.Sys?.AI?.ColonyGoals == null)
            {
                return;
            }

            foreach (AIGoal goal in state.Sys.AI.ColonyGoals.Values)
            {
                if (goal?.Target is StorableObjComp storable)
                {
                    RegisterGoal(storable, goal);
                }
            }
        }

        public static void InvalidateNetwork(string networkId)
        {
            HashSet<uint> goalIds = networkId == OrganicNetworkId
                ? OrganicTrackedGoalIds
                : networkId == RoboticNetworkId
                    ? RoboticTrackedGoalIds
                    : networkId == WeaponsNetworkId
                        ? WeaponTrackedGoalIds
                        : networkId == WardrobeNetworkId
                            ? ClothingTrackedGoalIds
                            : null;
            if (goalIds == null)
            {
                return;
            }

            foreach (uint goalId in goalIds)
            {
                MarkGoalDirty(goalId);
            }
        }

        public static void InvalidateStorageSettings(ObjHaulTargetComp haulTarget)
        {
            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(haulTarget?.S ?? state);
            if (haulTarget?.Storage != null && objectSystem != null &&
                objectSystem.TryGetNetwork(haulTarget.Storage, out QuantumObjectStorageNetwork network))
            {
                InvalidateNetwork(network.Id);
            }
        }

        private static void TrackGoalNetwork(uint goalId, int groupH)
        {
            OrganicTrackedGoalIds.Remove(goalId);
            RoboticTrackedGoalIds.Remove(goalId);
            WeaponTrackedGoalIds.Remove(goalId);
            ClothingTrackedGoalIds.Remove(goalId);
            if (groupH == OrganicGroupH)
            {
                OrganicTrackedGoalIds.Add(goalId);
            }
            else if (groupH == RoboticGroupH)
            {
                RoboticTrackedGoalIds.Add(goalId);
            }
            else if (groupH == WeaponGroupH)
            {
                WeaponTrackedGoalIds.Add(goalId);
            }
            else if (groupH == ClothingGroupH)
            {
                ClothingTrackedGoalIds.Add(goalId);
            }
        }

        private static void MarkGoalDirty(uint goalId)
        {
            if (!TrackedGoals.ContainsKey(goalId))
            {
                return;
            }

            if (PreparedCandidates.TryGetValue(goalId, out QuantumNanobotPreparedCandidate candidate))
            {
                candidate.Ready = false;
                candidate.NextPrepareTick = 0;
            }
            ReadyGoalSet.Remove(goalId);
            ReadyQueuedAreas.Remove(goalId);
            if (DirtyGoalSet.Add(goalId))
            {
                DirtyGoalIds.Add(goalId);
            }
        }

        private static void ProcessDirtyGoals(long ticks)
        {
            int processed = 0;
            while (processed < GoalPreparationBudgetPerTick && dirtyGoalCursor < DirtyGoalIds.Count)
            {
                uint goalId = DirtyGoalIds[dirtyGoalCursor++];
                processed++;
                if (!DirtyGoalSet.Remove(goalId) || !TrackedGoals.TryGetValue(goalId, out AIGoal goal))
                {
                    continue;
                }

                PrepareTrackedGoal(goalId, goal, ticks);
            }
            CompactConsumedPrefix(DirtyGoalIds, ref dirtyGoalCursor);
        }

        private static void ReconcileTrackedGoals(long ticks)
        {
            int count = Math.Min(GoalReconciliationBudgetPerTick, ReconcileGoalIds.Count - reconcileGoalCursor);
            for (int i = 0; i < count; i++)
            {
                uint goalId = ReconcileGoalIds[reconcileGoalCursor++];
                ReconcileGoalSet.Remove(goalId);
                if (!TrackedGoals.TryGetValue(goalId, out AIGoal goal))
                {
                    continue;
                }

                if (ReconcileGoalSet.Add(goalId))
                {
                    ReconcileGoalIds.Add(goalId);
                }
                if (DirtyGoalSet.Contains(goalId))
                {
                    continue;
                }

                if (!PreparedCandidates.TryGetValue(goalId, out QuantumNanobotPreparedCandidate candidate) ||
                    ticks >= candidate.NextPrepareTick)
                {
                    PrepareTrackedGoal(goalId, goal, ticks);
                }
            }
            CompactConsumedPrefix(ReconcileGoalIds, ref reconcileGoalCursor);
        }

        private static void PrepareTrackedGoal(uint goalId, AIGoal goal, long ticks)
        {
            if (!PreparedCandidates.TryGetValue(goalId, out QuantumNanobotPreparedCandidate candidate))
            {
                candidate = new QuantumNanobotPreparedCandidate { GoalId = goalId };
                PreparedCandidates.Add(goalId, candidate);
            }
            if (candidate.LastPreparedTick == ticks)
            {
                return;
            }

            candidate.LastPreparedTick = ticks;
            candidate.NextPrepareTick = ticks + DormantRetryTicks;
            candidate.Ready = false;
            candidate.Goal = null;
            candidate.Source = null;
            candidate.Data = null;
            candidate.Network = null;
            candidate.NetworkId = null;
            candidate.SourceAreaKey = 0;
            candidate.Destinations.Clear();
            ReadyGoalSet.Remove(goalId);
            ReadyQueuedAreas.Remove(goalId);

            GoalPreparationResult result = TryPrepareCandidate(goal, candidate);
            if (result == GoalPreparationResult.Remove)
            {
                RemoveTrackedGoal(goalId);
                return;
            }
            if (result != GoalPreparationResult.Ready)
            {
                if (goal != null && !ActiveOps.ContainsKey(goalId))
                {
                    goal.Flags &= ~AIGoalFlags.NanobotWorkable;
                }
                return;
            }

            candidate.Ready = true;
            candidate.SourceAreaKey = AreaKeyAt(candidate.Source.Obj.PosIdx);
            EnqueueReadyGoal(goalId, candidate.SourceAreaKey);
        }

        private static GoalPreparationResult TryPrepareCandidate(AIGoal goal, QuantumNanobotPreparedCandidate candidate)
        {
            if (goal?.Target is not StorableObjComp source || source.Entity?.IsActive != true || !IsSupportedObjectGroup(source.GroupH))
            {
                return GoalPreparationResult.Remove;
            }
            if (!AIGoal.IsActive(goal) && !AIGoal.IsFailed(goal))
            {
                return GoalPreparationResult.Remove;
            }
            if (AIGoal.IsFailed(goal) && goal.NonRetryable)
            {
                return GoalPreparationResult.Remove;
            }
            if (ActiveOps.ContainsKey(goal.Id) || goal.JobIdH != JobTypeIdH.Hauling || goal.Agent != null ||
                !IsResearchGateOpen(source.GroupH) || !goal.IsAvailabilityGateOpen(state) || AIGoal.IsBlockedByFire(goal))
            {
                return GoalPreparationResult.Dormant;
            }

            if (!goal.IsNanobotCompatible)
            {
                goal.NanobotCompatible();
            }
            ObjData data = source.Obj?.Data;
            if (!IsLooseSourceUsable(source, data, goal))
            {
                return GoalPreparationResult.Dormant;
            }

            string networkId = NetworkIdFor(source.GroupH);
            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (objectSystem == null || string.IsNullOrEmpty(networkId) ||
                !objectSystem.TryGetNetwork(networkId, out QuantumObjectStorageNetwork network))
            {
                return GoalPreparationResult.Dormant;
            }

            candidate.Goal = goal;
            candidate.Source = source;
            candidate.Data = data;
            candidate.Network = network;
            candidate.NetworkId = networkId;
            foreach (ObjStorageComp storage in network.Nodes)
            {
                if (IsDestinationUsable(objectSystem, storage, networkId, data) &&
                    state.Sys.Areas.IsSameAreaOrOutside(source.Obj.PosIdx, storage.InPort))
                {
                    candidate.Destinations.Add(storage);
                }
            }
            return candidate.Destinations.Count == 0
                ? GoalPreparationResult.Dormant
                : GoalPreparationResult.Ready;
        }

        private static void AssignReadyGoals(List<NanobotHiveComp> workingHives, long ticks, NanobotSys nanobotSystem)
        {
            if (ReadyGoalSet.Count == 0 || workingHives.Count == 0)
            {
                return;
            }

            int count = Math.Min(HiveAssignmentBudgetPerTick, workingHives.Count);
            for (int i = 0; i < count; i++)
            {
                if (nextHiveIndex >= workingHives.Count)
                {
                    nextHiveIndex = 0;
                }
                NanobotHiveComp hive = workingHives[nextHiveIndex++];
                if (hive == null || !hive.CanWork || HivesUsedByCore.Contains(hive.EntityId) || ActiveOpsByHive.ContainsKey(hive.EntityId))
                {
                    continue;
                }

                QuantumNanobotObjectCandidate best = FindBestReadyCandidate(hive, ticks);
                if (best == null)
                {
                    continue;
                }
                if (Start(best, hive, nanobotSystem))
                {
                    ReadyGoalSet.Remove(best.Goal.Id);
                    if (PreparedCandidates.TryGetValue(best.Goal.Id, out QuantumNanobotPreparedCandidate prepared))
                    {
                        prepared.Ready = false;
                        prepared.NextPrepareTick = ticks + DormantRetryTicks;
                    }
                }
                else
                {
                    DeferPreparedCandidate(best.Goal.Id, ticks);
                }
            }
        }

        private static void EnqueueReadyGoal(uint goalId, int areaKey)
        {
            ReadyGoalSet.Add(goalId);
            if (ReadyQueuedAreas.TryGetValue(goalId, out int queuedArea) && queuedArea == areaKey)
            {
                return;
            }

            ReadyQueuedAreas[goalId] = areaKey;
            if (!ReadyGoalsByArea.TryGetValue(areaKey, out QuantumNanobotReadyAreaQueue queue))
            {
                queue = new QuantumNanobotReadyAreaQueue();
                ReadyGoalsByArea.Add(areaKey, queue);
            }
            queue.GoalIds.Add(goalId);
        }

        private static QuantumNanobotObjectCandidate FindBestReadyCandidate(NanobotHiveComp hive, long ticks)
        {
            QuantumNanobotObjectCandidate best = null;
            int areaKey = AreaKeyAt(hive.Entity.PosIdx);
            if (!ReadyGoalsByArea.TryGetValue(areaKey, out QuantumNanobotReadyAreaQueue queue))
            {
                return null;
            }

            int count = Math.Min(CandidateChecksPerHive, queue.GoalIds.Count - queue.Cursor);
            for (int i = 0; i < count; i++)
            {
                uint goalId = queue.GoalIds[queue.Cursor++];
                if (!ReadyQueuedAreas.TryGetValue(goalId, out int queuedArea) || queuedArea != areaKey)
                {
                    continue;
                }
                ReadyQueuedAreas.Remove(goalId);
                if (!ReadyGoalSet.Contains(goalId) ||
                    !PreparedCandidates.TryGetValue(goalId, out QuantumNanobotPreparedCandidate prepared) || !prepared.Ready ||
                    prepared.SourceAreaKey != areaKey)
                {
                    continue;
                }

                EnqueueReadyGoal(goalId, areaKey);
                if (!TryResolveCandidateForHive(prepared, hive, out QuantumNanobotObjectCandidate candidate, out bool stale))
                {
                    if (stale)
                    {
                        DeferPreparedCandidate(goalId, ticks);
                    }
                    continue;
                }

                prepared.Goal.Flags |= AIGoalFlags.NanobotWorkable;
                if (best == null || QuantumNanobotHaulSelection.IsBetterCandidate(
                    candidate.Score, candidate.Goal.Id, best.Score, best.Goal.Id))
                {
                    best = candidate;
                }
            }
            CompactConsumedPrefix(queue.GoalIds, ref queue.Cursor);
            if (queue.GoalIds.Count == 0)
            {
                ReadyGoalsByArea.Remove(areaKey);
            }
            return best;
        }

        private static bool TryResolveCandidateForHive(QuantumNanobotPreparedCandidate prepared, NanobotHiveComp hive,
            out QuantumNanobotObjectCandidate candidate, out bool stale)
        {
            candidate = null;
            stale = false;
            AIGoal goal = prepared?.Goal;
            StorableObjComp source = prepared?.Source;
            ObjData data = prepared?.Data;
            if (goal == null || !TrackedGoals.TryGetValue(prepared.GoalId, out AIGoal tracked) || !ReferenceEquals(goal, tracked) ||
                ActiveOps.ContainsKey(goal.Id) || goal.JobIdH != JobTypeIdH.Hauling || goal.Agent != null ||
                (!AIGoal.IsActive(goal) && !AIGoal.IsFailed(goal)) || (AIGoal.IsFailed(goal) && goal.NonRetryable) ||
                source?.Entity?.IsActive != true || !IsResearchGateOpen(source.GroupH) ||
                !goal.IsAvailabilityGateOpen(state) || AIGoal.IsBlockedByFire(goal) || !IsLooseSourceUsable(source, data, goal))
            {
                stale = true;
                return false;
            }
            if (AreaKeyAt(source.Obj.PosIdx) != prepared.SourceAreaKey)
            {
                stale = true;
                return false;
            }

            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (objectSystem == null || !objectSystem.TryGetNetwork(prepared.NetworkId, out QuantumObjectStorageNetwork network) ||
                !ReferenceEquals(network, prepared.Network))
            {
                stale = true;
                return false;
            }
            if (!CanHiveReach(hive, source.Obj.PosIdx, source.Obj.Position))
            {
                return false;
            }

            ObjStorageComp bestStorage = null;
            int bestStoragePriority = 0;
            float bestStorageDistance = float.MaxValue;
            bool hasUsableDestination = false;
            for (int i = 0; i < prepared.Destinations.Count; i++)
            {
                ObjStorageComp storage = prepared.Destinations[i];
                if (!IsDestinationUsable(objectSystem, storage, prepared.NetworkId, data) ||
                    !state.Sys.Areas.IsSameAreaOrOutside(source.Obj.PosIdx, storage.InPort))
                {
                    continue;
                }

                hasUsableDestination = true;
                Vector2 destinationPosition = EntityUtils.CenterOf(storage.InPort);
                if (!CanHiveReach(hive, storage.InPort, destinationPosition))
                {
                    continue;
                }

                float distance = Vector2.SqrMagnitude(source.Obj.Position - destinationPosition);
                int priority = storage.HaulTarget.HaulPriority;
                if (bestStorage == null || QuantumNanobotHaulSelection.IsBetterDestination(
                    priority, distance, storage.EntityId,
                    bestStoragePriority, bestStorageDistance, bestStorage.EntityId))
                {
                    bestStorage = storage;
                    bestStoragePriority = priority;
                    bestStorageDistance = distance;
                }
            }

            if (bestStorage == null)
            {
                stale = !hasUsableDestination;
                return false;
            }

            candidate = new QuantumNanobotObjectCandidate
            {
                Goal = goal,
                Source = source,
                Data = data,
                Destination = bestStorage,
                Score = QuantumNanobotHaulSelection.TripScore(
                    Vector2.SqrMagnitude(hive.Entity.Position - source.Obj.Position), bestStorageDistance)
            };
            return true;
        }

        private static void DeferPreparedCandidate(uint goalId, long ticks)
        {
            ReadyGoalSet.Remove(goalId);
            ReadyQueuedAreas.Remove(goalId);
            if (PreparedCandidates.TryGetValue(goalId, out QuantumNanobotPreparedCandidate prepared))
            {
                prepared.Ready = false;
                prepared.NextPrepareTick = ticks + DormantRetryTicks;
            }
            if (TrackedGoals.TryGetValue(goalId, out AIGoal goal) && !ActiveOps.ContainsKey(goalId))
            {
                goal.Flags &= ~AIGoalFlags.NanobotWorkable;
            }
        }

        private static void RemoveTrackedGoal(uint goalId)
        {
            TrackedGoals.Remove(goalId);
            PreparedCandidates.Remove(goalId);
            DirtyGoalSet.Remove(goalId);
            ReconcileGoalSet.Remove(goalId);
            ReadyGoalSet.Remove(goalId);
            ReadyQueuedAreas.Remove(goalId);
            OrganicTrackedGoalIds.Remove(goalId);
            RoboticTrackedGoalIds.Remove(goalId);
            WeaponTrackedGoalIds.Remove(goalId);
            ClothingTrackedGoalIds.Remove(goalId);
        }

        private static void ClearSchedulingState()
        {
            TrackedGoals.Clear();
            PreparedCandidates.Clear();
            DirtyGoalIds.Clear();
            DirtyGoalSet.Clear();
            ReconcileGoalIds.Clear();
            ReconcileGoalSet.Clear();
            ReadyGoalSet.Clear();
            ReadyGoalsByArea.Clear();
            ReadyQueuedAreas.Clear();
            OrganicTrackedGoalIds.Clear();
            RoboticTrackedGoalIds.Clear();
            WeaponTrackedGoalIds.Clear();
            ClothingTrackedGoalIds.Clear();
            nextHiveIndex = 0;
            dirtyGoalCursor = 0;
            reconcileGoalCursor = 0;
        }

        private static int AreaKeyAt(int pos)
        {
            int areaId = state.Sys.Areas.At(pos);
            return areaId > 0 || areaId == -1 ? areaId : 0;
        }

        private static void CompactConsumedPrefix(List<uint> values, ref int cursor)
        {
            if (cursor == 0)
            {
                return;
            }
            if (cursor == values.Count)
            {
                values.Clear();
                cursor = 0;
                return;
            }
            if (cursor >= 1024 && cursor * 2 >= values.Count)
            {
                values.RemoveRange(0, cursor);
                cursor = 0;
            }
        }

        private static void ValidateActiveOps(long ticks)
        {
            if (ActiveOps.Count == 0)
            {
                return;
            }

            GoalIdsToRemove.Clear();
            foreach (KeyValuePair<uint, QuantumNanobotObjectHaulOp> entry in ActiveOps)
            {
                if (!IsActiveOpValid(entry.Value))
                {
                    GoalIdsToRemove.Add(entry.Key);
                }
            }

            for (int i = 0; i < GoalIdsToRemove.Count; i++)
            {
                if (ActiveOps.TryGetValue(GoalIdsToRemove[i], out QuantumNanobotObjectHaulOp op))
                {
                    Abort(op, ticks, true);
                }
            }
            GoalIdsToRemove.Clear();
        }

        private static void MaintainActiveOps(List<NanobotHiveComp> workingHives, bool haulingEnabled)
        {
            if (ActiveOps.Count == 0 || !haulingEnabled)
            {
                return;
            }

            foreach (QuantumNanobotObjectHaulOp op in ActiveOps.Values)
            {
                op.Goal.Flags |= AIGoalFlags.NanobotWorkable;
                NanobotHiveComp hive = FindHive(workingHives, op.HiveEntityId);
                if (hive != null)
                {
                    MarkHaulingWorked(hive, op.Data?.Obj);
                }
            }
        }

        private static bool Start(QuantumNanobotObjectCandidate candidate, NanobotHiveComp hive, NanobotSys nanobotSystem)
        {
            if (candidate?.Goal == null || candidate.Source?.Obj == null || candidate.Data == null || candidate.Destination == null ||
                hive == null || !hive.CanWork || HivesUsedByCore.Contains(hive.EntityId) ||
                ActiveOps.ContainsKey(candidate.Goal.Id) || ActiveOpsByHive.ContainsKey(hive.EntityId) ||
                candidate.Goal.JobIdH != JobTypeIdH.Hauling || candidate.Goal.Agent != null ||
                !IsResearchGateOpen(candidate.Source.GroupH) || !candidate.Goal.IsAvailabilityGateOpen(state) ||
                AIGoal.IsBlockedByFire(candidate.Goal))
            {
                return false;
            }
            if (!AIGoal.IsActive(candidate.Goal))
            {
                if (!AIGoal.IsFailed(candidate.Goal) || candidate.Goal.NonRetryable ||
                    !AIGoal.TryRetry(candidate.Goal, G.Ticks, true, false))
                {
                    return false;
                }
            }

            string networkId = NetworkIdFor(candidate.Source.GroupH);
            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (!IsLooseSourceUsable(candidate.Source, candidate.Data, candidate.Goal) ||
                !IsDestinationUsable(objectSystem, candidate.Destination, networkId, candidate.Data) ||
                !state.Sys.Areas.IsSameAreaOrOutside(candidate.Source.Obj.PosIdx, candidate.Destination.InPort) ||
                !CanHiveReach(hive, candidate.Source.Obj.PosIdx, candidate.Source.Obj.Position) ||
                !CanHiveReach(hive, candidate.Destination.InPort, EntityUtils.CenterOf(candidate.Destination.InPort)))
            {
                return false;
            }
            if (!candidate.Data.TryReserve(candidate.Goal))
            {
                return false;
            }
            if (!candidate.Source.Entity.SetNanobotCarrier(hive.EntityId))
            {
                candidate.Data.TryUnreserve(candidate.Goal);
                return false;
            }

            Vector2 from = candidate.Source.Obj.Position;
            Vector2 to = EntityUtils.CenterOf(candidate.Destination.InPort);
            float distance = Mathf.Max(1f, Vector2.Distance(from, to));
            float workSpeed = Mathf.Max(0.001f, hive.ActualWorkSpeed);
            QuantumNanobotObjectHaulOp op = new QuantumNanobotObjectHaulOp
            {
                GoalId = candidate.Goal.Id,
                Goal = candidate.Goal,
                HiveEntityId = hive.EntityId,
                ObjDataId = candidate.Data.Id,
                Data = candidate.Data,
                OwnsCarrier = true,
                DestinationEntityId = candidate.Destination.EntityId,
                Destination = candidate.Destination,
                SourcePosition = from,
                From = from,
                To = to,
                DurationTicks = Mathf.Max(MinimumDurationTicks, distance / workSpeed),
                Progress = 0f
            };

            ApplyCarryRenderOverride(candidate.Source.Obj);
            AddOp(op);
            candidate.Goal.Flags |= AIGoalFlags.NanobotWorkable;
            MarkHaulingWorked(hive, candidate.Source.Entity);
            if (hive.ShowParticles && hive.Tile.IsActive && A.IsCosmeticParticlesOn)
            {
                hive.ShowParticles = false;
                Vector2 size = candidate.Source.Entity.Size;
                if (size.x < 1f || size.y < 1f)
                {
                    size = Consts.V2One;
                }
                nanobotSystem.ShowParticlesAt(new NanobotSys.NanobotFX(hive.Tile, candidate.Source.Entity, size));
            }
            return true;
        }

        private static void Complete(QuantumNanobotObjectHaulOp op, long ticks)
        {
            Obj obj = op.Data?.Obj;
            if (obj?.IsActive != true || obj.StorableObj == null || !IsDestinationUsable(QuantumObjectStorageNetworkSys.Get(state), op.Destination, NetworkIdFor(obj.StorableObj.GroupH), op.Data))
            {
                Abort(op, ticks, true);
                return;
            }

            ClearCarryRenderOverride(obj);
            if (obj.NanobotCarrierHiveId == op.HiveEntityId)
            {
                obj.SetNanobotCarrier(0);
            }

            if (!op.Destination.Store(null, op.Data, op.Goal))
            {
                Abort(op, ticks, true);
                return;
            }

            op.Data.TryUnreserve(op.Goal);
            op.Goal.WithWorld(AIVarsH.IsObjStored, true);
            op.Goal.CheckDone(ticks);
            RemoveOp(op);
        }

        private static void Abort(QuantumNanobotObjectHaulOp op, long ticks, bool returnToSource)
        {
            if (op == null)
            {
                return;
            }

            ReleaseObject(op, returnToSource);
            if (op.Goal != null)
            {
                op.Goal.Flags &= ~AIGoalFlags.NanobotWorkable;
                if (AIGoal.IsActive(op.Goal))
                {
                    op.Goal.CanRetryAfterTick = ticks + Consts.TicksPer5Minutes;
                }
            }
            RemoveOp(op);
        }

        private static void ReleaseObject(QuantumNanobotObjectHaulOp op, bool returnToSource)
        {
            Obj obj = op?.Data?.Obj;
            if (op?.OwnsCarrier == true && obj?.IsActive == true)
            {
                ClearCarryRenderOverride(obj);
                if (obj.NanobotCarrierHiveId == op.HiveEntityId)
                {
                    obj.SetNanobotCarrier(0);
                }
                if (returnToSource && EntityUtils.IsWithinBounds(EntityUtils.ToPos(op.SourcePosition)))
                {
                    Teleport(obj, op.SourcePosition);
                }
            }
            if (op?.Data?.Reservation?.IsFor(op.Goal) == true && op.Data.Reservation.Goal == op.Goal)
            {
                op.Data.TryUnreserve(op.Goal);
            }
        }

        private static bool TryRecover(QuantumNanobotObjectHaulOp op)
        {
            if (state == null || op == null || !state.Sys.AI.ColonyGoals.TryGetValue(op.GoalId, out AIGoal goal))
            {
                return false;
            }

            ObjData data = state.Query.FindObjData.Ask(op.ObjDataId);
            Tile destinationTile = state.FindEntity<Tile>(op.DestinationEntityId);
            if (data?.Obj?.StorableObj == null || destinationTile == null || !destinationTile.TryGetComponent(out ObjStorageComp destination))
            {
                return false;
            }

            op.Goal = goal;
            op.Data = data;
            op.Destination = destination;
            if (ActiveOps.ContainsKey(op.GoalId) || ActiveOpsByHive.ContainsKey(op.HiveEntityId) ||
                !AIGoal.IsActive(goal) || goal.Agent != null || data.State != ObjState.Active || data.StorageId != 0 || !data.Obj.IsActive ||
                data.Obj.Carrier != null || (data.Obj.NanobotCarrierHiveId != 0 && data.Obj.NanobotCarrierHiveId != op.HiveEntityId) ||
                !IsResearchGateOpen(data.Obj.StorableObj.GroupH) ||
                !IsDestinationUsable(QuantumObjectStorageNetworkSys.Get(state), destination, NetworkIdFor(data.Obj.StorableObj.GroupH), data))
            {
                return false;
            }

            bool hasGoalReservation = data.Reservation?.IsFor(goal) == true && data.Reservation.Goal == goal;
            if (!hasGoalReservation && !data.TryReserve(goal))
            {
                return false;
            }
            if (data.Obj.NanobotCarrierHiveId == 0 && !data.Obj.SetNanobotCarrier(op.HiveEntityId))
            {
                data.TryUnreserve(goal);
                return false;
            }
            if (data.Obj.NanobotCarrierHiveId != op.HiveEntityId)
            {
                data.TryUnreserve(goal);
                return false;
            }

            op.OwnsCarrier = true;
            op.DurationTicks = Mathf.Max(MinimumDurationTicks, op.DurationTicks);
            op.Progress = Mathf.Clamp01(op.Progress);
            ApplyCarryRenderOverride(data.Obj);
            RegisterGoal(data.Obj.StorableObj, goal);
            AddOp(op);
            goal.Flags |= AIGoalFlags.NanobotWorkable;
            return true;
        }

        private static bool IsActiveOpValid(QuantumNanobotObjectHaulOp op)
        {
            if (state == null || op?.Goal == null || !AIGoal.IsActive(op.Goal) || op.Goal.Agent != null)
            {
                return false;
            }
            if (!op.OwnsCarrier || op.Data?.Obj?.StorableObj == null || op.Data.State != ObjState.Active || op.Data.StorageId != 0 || !op.Data.Obj.IsActive)
            {
                return false;
            }
            if (!IsResearchGateOpen(op.Data.Obj.StorableObj.GroupH) || op.Data.Reservation?.IsFor(op.Goal) != true || op.Data.Reservation.Goal != op.Goal)
            {
                return false;
            }
            if (op.Data.Obj.Carrier != null || (op.Data.Obj.NanobotCarrierHiveId != 0 && op.Data.Obj.NanobotCarrierHiveId != op.HiveEntityId))
            {
                return false;
            }
            return op.Destination?.Entity?.IsActive == true;
        }

        private static bool IsLooseSourceUsable(StorableObjComp source, ObjData data, AIGoal goal)
        {
            return source?.Obj?.IsActive == true && data != null && data.Obj == source.Obj && data.State == ObjState.Active && data.StorageId == 0 &&
                source.Entity.Carrier == null && source.Entity.NanobotCarrierHiveId == 0 &&
                (data.Reservation == null || data.Reservation.IsFor(goal));
        }

        private static bool IsDestinationUsable(QuantumObjectStorageNetworkSys objectSystem, ObjStorageComp storage, string networkId, ObjData data)
        {
            return objectSystem != null && storage?.Entity?.IsActive == true && storage.Tile.IsConstructed && !storage.IsFull &&
                storage.HaulTarget != null && storage.HaulTarget.HaulPriority != 0 && storage.HaulTarget.Accepts(data) &&
                objectSystem.TryGetNetwork(storage, out QuantumObjectStorageNetwork network) && network.Id == networkId;
        }

        private static bool CanHiveReach(NanobotHiveComp hive, int targetPos, Vector2 targetPosition)
        {
            return hive != null && hive.CanWork && EntityUtils.IsWithinBounds(targetPos) &&
                state.Sys.Areas.IsSameAreaOrOutside(hive.Entity.PosIdx, targetPos) &&
                Vector2.SqrMagnitude(hive.Entity.Position - targetPosition) <= hive.RangeSqr;
        }

        private static bool IsResearchGateOpen(int groupH)
        {
            if (state?.Research == null || !state.Research.IsUnlocked(NanobotsResearchH))
            {
                return false;
            }
            if (groupH == RoboticGroupH)
            {
                return state.Research.IsUnlocked(BasicBotPartsResearchH);
            }
            if (groupH == OrganicGroupH)
            {
                return state.Research.IsUnlocked(BasicSyntheticOrgansResearchH);
            }
            return groupH == WeaponGroupH || groupH == ClothingGroupH;
        }

        private static bool IsSupportedObjectGroup(int groupH)
        {
            return groupH == OrganicGroupH || groupH == RoboticGroupH ||
                groupH == WeaponGroupH || groupH == ClothingGroupH;
        }

        private static string NetworkIdFor(int groupH)
        {
            if (groupH == OrganicGroupH)
            {
                return OrganicNetworkId;
            }
            if (groupH == RoboticGroupH)
            {
                return RoboticNetworkId;
            }
            if (groupH == WeaponGroupH)
            {
                return WeaponsNetworkId;
            }
            if (groupH == ClothingGroupH)
            {
                return WardrobeNetworkId;
            }
            return null;
        }

        private static List<NanobotHiveComp> GetWorkingHives(NanobotSys nanobotSystem)
        {
            return WorkingHivesField?.GetValue(nanobotSystem) as List<NanobotHiveComp>;
        }

        private static NanobotHiveComp FindHive(List<NanobotHiveComp> hives, int entityId)
        {
            if (hives == null)
            {
                return null;
            }
            for (int i = hives.Count - 1; i >= 0; i--)
            {
                if (hives[i]?.EntityId == entityId)
                {
                    return hives[i];
                }
            }
            return null;
        }

        private static void MarkHaulingWorked(NanobotHiveComp hive, Entity target)
        {
            if (hive != null && target != null)
            {
                MarkHaulingWorkedMethod.Invoke(hive, new object[] { target });
            }
        }

        private static void ApplyCarryRenderOverride(Obj obj)
        {
            obj?.Graphics?.SetTemporaryRendererOverride(Renderers.ObjAbove, SpriteOrder.HauledObj);
        }

        private static void ClearCarryRenderOverride(Obj obj)
        {
            obj?.Graphics?.ClearTemporaryRendererOverride();
        }

        private static void Teleport(Obj obj, Vector2 position)
        {
            if (obj?.IsActive != true)
            {
                return;
            }
            obj.Transform.TeleportTo(position);
            obj.Graphics.TeleportTo(position);
        }

        private static void AddOp(QuantumNanobotObjectHaulOp op)
        {
            ActiveOps[op.GoalId] = op;
            ActiveOpsByHive[op.HiveEntityId] = op;
        }

        private static void RemoveOp(QuantumNanobotObjectHaulOp op)
        {
            if (op == null)
            {
                return;
            }
            ActiveOps.Remove(op.GoalId);
            if (ActiveOpsByHive.TryGetValue(op.HiveEntityId, out object current) && ReferenceEquals(current, op))
            {
                ActiveOpsByHive.Remove(op.HiveEntityId);
            }
        }
    }

    internal enum GoalPreparationResult
    {
        Ready,
        Dormant,
        Remove
    }

    internal sealed class QuantumNanobotPreparedCandidate
    {
        public uint GoalId;
        public AIGoal Goal;
        public StorableObjComp Source;
        public ObjData Data;
        public string NetworkId;
        public QuantumObjectStorageNetwork Network;
        public int SourceAreaKey;
        public readonly List<ObjStorageComp> Destinations = new List<ObjStorageComp>();
        public long LastPreparedTick = long.MinValue;
        public long NextPrepareTick;
        public bool Ready;
    }

    internal sealed class QuantumNanobotReadyAreaQueue
    {
        public readonly List<uint> GoalIds = new List<uint>();
        public int Cursor;
    }

    internal sealed class QuantumNanobotObjectCandidate
    {
        public AIGoal Goal;
        public StorableObjComp Source;
        public ObjData Data;
        public ObjStorageComp Destination;
        public float Score;
    }

    internal sealed class QuantumNanobotObjectHaulOp
    {
        public uint GoalId;
        public AIGoal Goal;
        public int HiveEntityId;
        public uint ObjDataId;
        public ObjData Data;
        public bool OwnsCarrier;
        public int DestinationEntityId;
        public ObjStorageComp Destination;
        public Vector2 SourcePosition;
        public Vector2 From;
        public Vector2 To;
        public float DurationTicks;
        public float Progress;

        public void Save(ComponentData data, string prefix)
        {
            data.SetUInt(prefix + "GoalId", GoalId);
            data.SetInt(prefix + "HiveEntityId", HiveEntityId);
            data.SetUInt(prefix + "ObjDataId", ObjDataId);
            data.SetInt(prefix + "DestinationEntityId", DestinationEntityId);
            data.SetVector2(prefix + "SourcePosition", SourcePosition);
            data.SetVector2(prefix + "From", From);
            data.SetVector2(prefix + "To", To);
            data.SetFloat(prefix + "DurationTicks", DurationTicks);
            data.SetFloat(prefix + "Progress", Progress);
        }

        public static QuantumNanobotObjectHaulOp Load(ComponentData data, string prefix)
        {
            uint goalId = data.GetUInt(prefix + "GoalId", 0);
            uint objDataId = data.GetUInt(prefix + "ObjDataId", 0);
            if (goalId == 0 || objDataId == 0)
            {
                return null;
            }

            return new QuantumNanobotObjectHaulOp
            {
                GoalId = goalId,
                HiveEntityId = data.GetInt(prefix + "HiveEntityId", 0),
                ObjDataId = objDataId,
                DestinationEntityId = data.GetInt(prefix + "DestinationEntityId", 0),
                SourcePosition = data.GetVector2(prefix + "SourcePosition"),
                From = data.GetVector2(prefix + "From"),
                To = data.GetVector2(prefix + "To"),
                DurationTicks = data.GetFloat(prefix + "DurationTicks", 1f),
                Progress = data.GetFloat(prefix + "Progress", 0f)
            };
        }
    }

    [HarmonyPatch(typeof(StorableObjComp), nameof(StorableObjComp.TryOrderStoring))]
    internal static class StorableObjTryOrderStoringQuantumNanobotPatch
    {
        private static void Postfix(StorableObjComp __instance, bool __result)
        {
            if (__result)
            {
                QuantumNanobotObjectHauling.RegisterGoal(__instance, __instance.Entity.Goal);
            }
        }
    }

    [HarmonyPatch(typeof(StorableObjComp), nameof(StorableObjComp.OnGoalLoad))]
    internal static class StorableObjOnGoalLoadQuantumNanobotPatch
    {
        private static void Postfix(StorableObjComp __instance, AIGoal goal)
        {
            QuantumNanobotObjectHauling.RegisterGoal(__instance, goal);
        }
    }

    [HarmonyPatch(typeof(ObjHaulTargetComp), nameof(ObjHaulTargetComp.SetFilterMode))]
    internal static class ObjHaulTargetSetFilterModeQuantumNanobotPatch
    {
        private static void Postfix(ObjHaulTargetComp __instance)
        {
            QuantumNanobotObjectHauling.InvalidateStorageSettings(__instance);
        }
    }

    [HarmonyPatch(typeof(ObjHaulTargetComp), nameof(ObjHaulTargetComp.SetFilterState))]
    internal static class ObjHaulTargetSetFilterStateQuantumNanobotPatch
    {
        private static void Postfix(ObjHaulTargetComp __instance)
        {
            QuantumNanobotObjectHauling.InvalidateStorageSettings(__instance);
        }
    }

    [HarmonyPatch]
    internal static class NanobotSysOnTickQuantumObjectPatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(NanobotSys), "OnTick") != null;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(NanobotSys), "OnTick");
        }

        [HarmonyPriority(HarmonyLib.Priority.First)]
        private static void Prefix(NanobotSys __instance)
        {
            try
            {
                QuantumNanobotObjectHauling.BeginCoreTick(__instance.S);
            }
            catch (Exception ex)
            {
                QuantumNanobotObjectHauling.DisableForSession(ex, "Nanobot tick setup");
            }
        }

        [HarmonyPriority(HarmonyLib.Priority.Last)]
        private static void Postfix(NanobotSys __instance, long ticks)
        {
            try
            {
                QuantumNanobotObjectHauling.OnNanobotTick(__instance, ticks);
            }
            catch (Exception ex)
            {
                QuantumNanobotObjectHauling.DisableForSession(ex, "Nanobot work assignment");
            }
        }
    }

    [HarmonyPatch]
    internal static class NanobotSysPerformWorkOnQuantumObjectPatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(NanobotSys), "PerformWorkOn") != null;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(NanobotSys), "PerformWorkOn");
        }

        [HarmonyPriority(HarmonyLib.Priority.First)]
        private static bool Prefix(NanobotHiveComp hive)
        {
            try
            {
                return QuantumNanobotObjectHauling.BeforeCoreWork(hive);
            }
            catch (Exception ex)
            {
                QuantumNanobotObjectHauling.DisableForSession(ex, "Hive work arbitration");
                return true;
            }
        }
    }

    [HarmonyPatch]
    internal static class NanobotSysOnTickProgressQuantumObjectPatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(NanobotSys), "OnTickProgress") != null;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(NanobotSys), "OnTickProgress");
        }

        [HarmonyPriority(HarmonyLib.Priority.Last)]
        private static void Postfix(NanobotSys __instance)
        {
            try
            {
                QuantumNanobotObjectHauling.OnTickProgress(__instance.S.Clock.TicksPerUpdate, G.Ticks);
            }
            catch (Exception ex)
            {
                QuantumNanobotObjectHauling.DisableForSession(ex, "object-storage transit");
            }
        }
    }

    [HarmonyPatch]
    internal static class NanobotSysOnLoadSaveQuantumObjectPatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(NanobotSys), "OnLoadSave") != null;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(NanobotSys), "OnLoadSave");
        }

        [HarmonyPriority(HarmonyLib.Priority.Last)]
        private static void Postfix(GameState state)
        {
            try
            {
                QuantumNanobotObjectHauling.AfterLoad(state);
            }
            catch (Exception ex)
            {
                QuantumNanobotObjectHauling.DisableForSession(ex, "save recovery");
            }
        }
    }

    [HarmonyPatch(typeof(NanobotSys), nameof(NanobotSys.Save))]
    internal static class NanobotSysSaveQuantumObjectPatch
    {
        private static void Postfix(ref ComponentData __result)
        {
            try
            {
                QuantumNanobotObjectHauling.Save(ref __result);
            }
            catch (Exception ex)
            {
                QuantumNanobotObjectHauling.DisableForSession(ex, "saving");
            }
        }
    }

    [HarmonyPatch(typeof(NanobotSys), nameof(NanobotSys.Load))]
    internal static class NanobotSysLoadQuantumObjectPatch
    {
        private static void Postfix(NanobotSys __instance, ComponentData data)
        {
            try
            {
                QuantumNanobotObjectHauling.Load(__instance.S, data);
            }
            catch (Exception ex)
            {
                QuantumNanobotObjectHauling.DisableForSession(ex, "loading");
            }
        }
    }

    [HarmonyPatch(typeof(NanobotSys), nameof(NanobotSys.Unload))]
    internal static class NanobotSysUnloadQuantumObjectPatch
    {
        private static void Prefix()
        {
            try
            {
                QuantumNanobotObjectHauling.Reset();
            }
            catch (Exception ex)
            {
                D.LogEx(ex, "Quantum Storage encountered a cleanup failure while unloading object-storage nanobot hauling.");
            }
        }
    }
}
