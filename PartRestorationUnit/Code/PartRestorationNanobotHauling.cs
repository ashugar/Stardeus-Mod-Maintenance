using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Items;
using Game.Systems.Nanobots;
using Game.Systems.Rendering;
using Game.Utils;
using HarmonyLib;
using KL.Grid;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.PartRestoration {
    internal static class PartRestorationNanobotHauling {
        private const string SavePrefix =
            "PartRestorationObjectHaul.";
        private const string CountKey = SavePrefix + "Count";
        private const float MinimumDurationTicks = 1f;
        private const int HaulingJobIdH = 105668070;

        private static readonly FieldInfo WorkingHivesField =
            AccessTools.Field(typeof(NanobotSys), "workingHives");
        private static readonly MethodInfo MarkHaulingWorkedMethod =
            AccessTools.Method(typeof(NanobotHiveComp),
                "MarkHaulingWorked");

        private static readonly Dictionary<uint, AIGoal> TrackedGoals =
            new Dictionary<uint, AIGoal>();
        private static readonly Dictionary<uint,
            PartRestorationNanobotHaulOp> ActiveOps =
            new Dictionary<uint, PartRestorationNanobotHaulOp>();
        private static readonly Dictionary<int,
            PartRestorationNanobotHaulOp> ActiveOpsByHive =
            new Dictionary<int, PartRestorationNanobotHaulOp>();
        private static readonly HashSet<int> HivesUsedByCore =
            new HashSet<int>();
        private static readonly List<uint> GoalIdsToRemove =
            new List<uint>();
        private static readonly List<PartRestorationNanobotHaulOp>
            LoadedOps = new List<PartRestorationNanobotHaulOp>();

        private static GameState state;
        private static bool bindingsReported;
        private static bool disabledForSession;

        internal static bool BindingsAvailable =>
            WorkingHivesField != null
            && MarkHaulingWorkedMethod != null;

        private static bool IsAvailable =>
            BindingsAvailable
            && !disabledForSession
            && QuantumNanobotCoordination.IsSafe;

        internal static void ReportBindings() {
            if (bindingsReported) {
                return;
            }
            bindingsReported = true;
            if (BindingsAvailable) {
                D.Log("Part Restoration Unit enabled Nanobot Hive "
                    + "delivery for queued body-part repairs.");
            } else {
                D.Warn("Part Restoration Unit could not bind the current "
                    + "Nanobot Hive hauling lifecycle. Ordinary repair "
                    + "hauling remains available.");
            }
        }

        internal static void RegisterGoal(
                PartRestorationUnitComp unit, AIGoal goal) {
            if (unit == null || goal == null
                    || !ReferenceEquals(goal.Target, unit)
                    || goal.JobIdH != HaulingJobIdH) {
                return;
            }
            TrackedGoals[goal.Id] = goal;
            if (!goal.IsNanobotCompatible) {
                goal.NanobotCompatible();
            }
        }

        internal static void BeginCoreTick(GameState gameState) {
            BindState(gameState);
            HivesUsedByCore.Clear();
            QuantumNanobotCoordination.MarkHivesUnavailable(
                ActiveOpsByHive.Keys);
        }

        internal static bool BeforeCoreWork(NanobotHiveComp hive) {
            if (hive == null) {
                return true;
            }
            if (IsAvailable
                    && ActiveOpsByHive.ContainsKey(hive.EntityId)) {
                return false;
            }
            HivesUsedByCore.Add(hive.EntityId);
            return true;
        }

        internal static void OnNanobotTick(
                NanobotSys nanobotSystem, long ticks) {
            if (!IsAvailable || nanobotSystem?.S == null) {
                return;
            }

            BindState(nanobotSystem.S);
            ValidateActiveOps(ticks);
            List<NanobotHiveComp> workingHives =
                GetWorkingHives(nanobotSystem);
            bool haulingEnabled = state.Query.IsNanobotsJobEnabled
                .Ask(HaulingJobIdH);
            MaintainActiveOps(workingHives, haulingEnabled);
            RefreshTrackedGoals();

            if (!haulingEnabled || workingHives == null
                    || workingHives.Count == 0) {
                return;
            }

            for (int i = workingHives.Count - 1; i >= 0; i--) {
                NanobotHiveComp hive = workingHives[i];
                if (hive == null || !hive.CanWork
                        || HivesUsedByCore.Contains(hive.EntityId)
                        || ActiveOpsByHive.ContainsKey(hive.EntityId)
                        || QuantumNanobotCoordination
                            .IsHiveBusy(hive.EntityId)) {
                    continue;
                }

                PartRestorationNanobotCandidate candidate =
                    FindBestCandidate(hive, ticks);
                if (candidate != null) {
                    Start(candidate, hive, nanobotSystem);
                }
            }
        }

        internal static void OnTickProgress(
                float ticksPerFrame, long ticks) {
            if (state == null || ActiveOps.Count == 0
                    || ticksPerFrame <= 0f) {
                return;
            }

            GoalIdsToRemove.Clear();
            foreach (KeyValuePair<uint,
                    PartRestorationNanobotHaulOp> entry in ActiveOps) {
                PartRestorationNanobotHaulOp op = entry.Value;
                if (!IsActiveOpValid(op)) {
                    GoalIdsToRemove.Add(entry.Key);
                    continue;
                }
                op.Progress = Mathf.Min(1f,
                    op.Progress + ticksPerFrame / op.DurationTicks);
                Teleport(op.Data.Obj,
                    Vector2.Lerp(op.From, op.To, op.Progress));
                if (op.Progress >= 1f) {
                    GoalIdsToRemove.Add(entry.Key);
                }
            }

            for (int i = 0; i < GoalIdsToRemove.Count; i++) {
                if (!ActiveOps.TryGetValue(
                        GoalIdsToRemove[i], out var op)) {
                    continue;
                }
                if (IsActiveOpValid(op)) {
                    Complete(op, ticks);
                } else {
                    Abort(op, ticks, true);
                }
            }
            GoalIdsToRemove.Clear();
        }

        internal static void Save(ref ComponentData data) {
            if (ActiveOps.Count == 0) {
                data?.SetInt(CountKey, 0);
                return;
            }
            data ??= new ComponentData(0, NanobotSys.SysId);
            data.SetInt(CountKey, ActiveOps.Count);
            int index = 0;
            foreach (PartRestorationNanobotHaulOp op
                    in ActiveOps.Values) {
                op.Save(data, SavePrefix + index + ".");
                index++;
            }
        }

        internal static void Load(
                GameState gameState, ComponentData data) {
            disabledForSession = false;
            BindState(gameState, true);
            LoadedOps.Clear();
            if (data == null) {
                return;
            }
            int count = data.GetInt(CountKey, 0);
            for (int i = 0; i < count; i++) {
                PartRestorationNanobotHaulOp op =
                    PartRestorationNanobotHaulOp.Load(
                        data, SavePrefix + i + ".");
                if (op != null) {
                    LoadedOps.Add(op);
                }
            }
        }

        internal static void AfterLoad(GameState gameState) {
            disabledForSession = false;
            if (!BindingsAvailable) {
                LoadedOps.Clear();
                return;
            }
            BindState(gameState);
            RegisterLoadedGoals();
            for (int i = 0; i < LoadedOps.Count; i++) {
                PartRestorationNanobotHaulOp op = LoadedOps[i];
                if (!TryRecover(op)) {
                    ResolveLoadedReferencesForRelease(op);
                    Abort(op, G.Ticks, true);
                }
            }
            LoadedOps.Clear();
        }

        internal static void Reset() {
            var ops = new PartRestorationNanobotHaulOp[
                ActiveOps.Count];
            ActiveOps.Values.CopyTo(ops, 0);
            for (int i = 0; i < ops.Length; i++) {
                ReleaseObject(ops[i], true);
            }
            ActiveOps.Clear();
            ActiveOpsByHive.Clear();
            TrackedGoals.Clear();
            HivesUsedByCore.Clear();
            GoalIdsToRemove.Clear();
            LoadedOps.Clear();
            state = null;
            disabledForSession = false;
            QuantumNanobotCoordination.Reset();
        }

        internal static void DisableForSession(
                Exception exception, string operation) {
            if (disabledForSession) {
                return;
            }
            disabledForSession = true;
            var ops = new PartRestorationNanobotHaulOp[
                ActiveOps.Count];
            ActiveOps.Values.CopyTo(ops, 0);
            for (int i = 0; i < ops.Length; i++) {
                try {
                    ReleaseObject(ops[i], true);
                    if (ops[i].Goal != null) {
                        ops[i].Goal.Flags &=
                            ~AIGoalFlags.NanobotWorkable;
                    }
                } catch {
                }
            }
            ActiveOps.Clear();
            ActiveOpsByHive.Clear();
            D.LogEx(exception, "Part Restoration Unit disabled "
                + "Nanobot Hive delivery for this session after a failure "
                + "during {0}. Ordinary repair hauling remains available.",
                operation);
        }

        private static void BindState(
                GameState gameState, bool forceReset = false) {
            if (!forceReset && ReferenceEquals(state, gameState)) {
                return;
            }
            ActiveOps.Clear();
            ActiveOpsByHive.Clear();
            TrackedGoals.Clear();
            HivesUsedByCore.Clear();
            GoalIdsToRemove.Clear();
            LoadedOps.Clear();
            state = gameState;
        }

        private static void RegisterLoadedGoals() {
            if (state?.Sys?.AI?.ColonyGoals == null) {
                return;
            }
            foreach (AIGoal goal in state.Sys.AI.ColonyGoals.Values) {
                if (goal?.Target is PartRestorationUnitComp unit) {
                    RegisterGoal(unit, goal);
                }
            }
        }

        private static void RefreshTrackedGoals() {
            GoalIdsToRemove.Clear();
            foreach (KeyValuePair<uint, AIGoal> entry
                    in TrackedGoals) {
                AIGoal goal = entry.Value;
                if (goal?.Target is not PartRestorationUnitComp unit
                        || unit.Entity?.IsActive != true) {
                    GoalIdsToRemove.Add(entry.Key);
                    continue;
                }
                if (!goal.IsNanobotCompatible) {
                    goal.NanobotCompatible();
                }
                if (!AIGoal.IsActive(goal)
                        && !AIGoal.IsFailed(goal)) {
                    GoalIdsToRemove.Add(entry.Key);
                }
            }
            for (int i = 0; i < GoalIdsToRemove.Count; i++) {
                if (!ActiveOps.ContainsKey(GoalIdsToRemove[i])) {
                    TrackedGoals.Remove(GoalIdsToRemove[i]);
                }
            }
            GoalIdsToRemove.Clear();
        }

        private static void ValidateActiveOps(long ticks) {
            if (ActiveOps.Count == 0) {
                return;
            }
            GoalIdsToRemove.Clear();
            foreach (KeyValuePair<uint,
                    PartRestorationNanobotHaulOp> entry in ActiveOps) {
                if (!IsActiveOpValid(entry.Value)) {
                    GoalIdsToRemove.Add(entry.Key);
                }
            }
            for (int i = 0; i < GoalIdsToRemove.Count; i++) {
                if (ActiveOps.TryGetValue(
                        GoalIdsToRemove[i], out var op)) {
                    Abort(op, ticks, true);
                }
            }
            GoalIdsToRemove.Clear();
        }

        private static void MaintainActiveOps(
                List<NanobotHiveComp> workingHives,
                bool haulingEnabled) {
            if (ActiveOps.Count == 0 || !haulingEnabled) {
                return;
            }
            foreach (PartRestorationNanobotHaulOp op
                    in ActiveOps.Values) {
                op.Goal.Flags |= AIGoalFlags.NanobotWorkable;
                QuantumNanobotCoordination.MarkHiveUnavailable(
                    op.HiveEntityId);
                NanobotHiveComp hive = FindHive(
                    workingHives, op.HiveEntityId);
                if (hive != null) {
                    MarkHaulingWorked(hive, op.Data?.Obj);
                }
            }
        }

        private static PartRestorationNanobotCandidate
                FindBestCandidate(NanobotHiveComp hive, long ticks) {
            PartRestorationNanobotCandidate best = null;
            foreach (AIGoal goal in TrackedGoals.Values) {
                if (goal == null || ActiveOps.ContainsKey(goal.Id)
                        || goal.JobIdH != HaulingJobIdH
                        || goal.Agent != null
                        || goal.Target is not PartRestorationUnitComp unit) {
                    continue;
                }
                if (!goal.IsNanobotCompatible) {
                    goal.NanobotCompatible();
                }
                if (!goal.IsAvailabilityGateOpen(state)
                        || AIGoal.IsBlockedByFire(goal)) {
                    goal.Flags &= ~AIGoalFlags.NanobotWorkable;
                    continue;
                }

                PartRestorationNanobotCandidate candidate =
                    BuildCandidate(goal, unit, hive);
                if (candidate == null) {
                    continue;
                }
                if (!AIGoal.IsActive(goal)) {
                    if (!AIGoal.IsFailed(goal) || goal.NonRetryable
                            || !AIGoal.TryRetry(
                                goal, ticks, true, false)) {
                        continue;
                    }
                }
                goal.Flags |= AIGoalFlags.NanobotWorkable;
                if (best == null || candidate.Score < best.Score) {
                    best = candidate;
                }
            }
            return best;
        }

        private static PartRestorationNanobotCandidate BuildCandidate(
                AIGoal goal, PartRestorationUnitComp unit,
                NanobotHiveComp hive) {
            ObjData data = unit.InputData;
            if (!IsSourceUsable(data, goal)
                    || !unit.CanReceiveNanobotPart(data, goal)) {
                return null;
            }

            ObjStorageComp sourceStorage = null;
            int sourcePos;
            Vector2 sourcePosition;
            bool sourceWasStored = data.State == ObjState.Stored;
            if (sourceWasStored) {
                sourceStorage = state.Components
                    .Find<ObjStorageComp>(data.StorageId);
                if (!IsSourceStorageUsable(sourceStorage, data)) {
                    return null;
                }
                sourcePos = sourceStorage.InPort;
                sourcePosition = EntityUtils.CenterOf(sourcePos);
            } else {
                sourcePos = data.Obj.PosIdx;
                sourcePosition = data.Obj.Position;
            }

            int destinationPos = unit.LoadObjSpotIdx;
            if (!EntityUtils.IsWithinBounds(sourcePos)
                    || !EntityUtils.IsWithinBounds(destinationPos)
                    || !state.Sys.Areas.IsSameAreaOrOutside(
                        sourcePos, destinationPos)
                    || !CanHiveReach(hive, sourcePos, sourcePosition)) {
                return null;
            }
            Vector2 destinationPosition =
                EntityUtils.CenterOf(destinationPos);
            if (!CanHiveReach(
                    hive, destinationPos, destinationPosition)) {
                return null;
            }

            return new PartRestorationNanobotCandidate {
                Goal = goal,
                Unit = unit,
                Data = data,
                SourceStorage = sourceStorage,
                SourceWasStored = sourceWasStored,
                SourcePosition = sourcePosition,
                DestinationPosition = destinationPosition,
                Score = Vector2.SqrMagnitude(
                        hive.Entity.Position - sourcePosition)
                    + Vector2.SqrMagnitude(
                        sourcePosition - destinationPosition)
            };
        }

        private static bool Start(
                PartRestorationNanobotCandidate candidate,
                NanobotHiveComp hive, NanobotSys nanobotSystem) {
            if (candidate == null
                    || ActiveOpsByHive.ContainsKey(hive.EntityId)
                    || QuantumNanobotCoordination
                        .IsHiveBusy(hive.EntityId)
                    || !candidate.Unit.CanReceiveNanobotPart(
                        candidate.Data, candidate.Goal)
                    || !IsSourceUsable(
                        candidate.Data, candidate.Goal)) {
                return false;
            }

            bool alreadyReserved = candidate.Data.Reservation
                ?.IsFor(candidate.Goal) == true
                && candidate.Data.Reservation.Goal == candidate.Goal;
            if (!alreadyReserved
                    && !candidate.Data.TryReserve(candidate.Goal)) {
                return false;
            }

            if (candidate.SourceWasStored
                    && !state.Query.UnstoreObj.Ask(
                        candidate.Data, candidate.SourceStorage)) {
                if (!alreadyReserved) {
                    candidate.Data.TryUnreserve(candidate.Goal);
                }
                return false;
            }

            Obj obj = candidate.Data.Obj;
            bool canRestoreUnclaimedObject = obj?.IsActive == true
                && obj.Carrier == null
                && obj.NanobotCarrierHiveId == 0;
            if (obj?.IsActive != true
                    || obj.Carrier != null
                    || obj.NanobotCarrierHiveId != 0
                    || !obj.SetNanobotCarrier(hive.EntityId)) {
                if (canRestoreUnclaimedObject) {
                    RestoreToSource(candidate.Data,
                        candidate.SourceStorage,
                        candidate.SourceWasStored,
                        candidate.SourcePosition,
                        candidate.Goal);
                }
                if (!alreadyReserved) {
                    candidate.Data.TryUnreserve(candidate.Goal);
                }
                return false;
            }

            Vector2 from = obj.Position;
            Vector2 to = candidate.DestinationPosition;
            float distance = Mathf.Max(1f,
                Vector2.Distance(from, to));
            float workSpeed = Mathf.Max(
                0.001f, hive.ActualWorkSpeed);
            var op = new PartRestorationNanobotHaulOp {
                GoalId = candidate.Goal.Id,
                Goal = candidate.Goal,
                HiveEntityId = hive.EntityId,
                ObjDataId = candidate.Data.Id,
                Data = candidate.Data,
                UnitEntityId = candidate.Unit.EntityId,
                Unit = candidate.Unit,
                SourceStorageEntityId =
                    candidate.SourceStorage?.EntityId ?? 0,
                SourceStorage = candidate.SourceStorage,
                SourceWasStored = candidate.SourceWasStored,
                SourcePosition = candidate.SourcePosition,
                From = from,
                To = to,
                DurationTicks = Mathf.Max(
                    MinimumDurationTicks, distance / workSpeed),
                Progress = 0f
            };

            ApplyCarryRenderOverride(obj);
            AddOp(op);
            candidate.Goal.Flags |= AIGoalFlags.NanobotWorkable;
            QuantumNanobotCoordination.MarkHiveUnavailable(
                hive.EntityId);
            MarkHaulingWorked(hive, obj);
            if (hive.ShowParticles && hive.Tile.IsActive
                    && A.IsCosmeticParticlesOn) {
                hive.ShowParticles = false;
                Vector2 size = obj.Size;
                if (size.x < 1f || size.y < 1f) {
                    size = Consts.V2One;
                }
                nanobotSystem.ShowParticlesAt(
                    new NanobotSys.NanobotFX(
                        hive.Tile, obj, size));
            }
            return true;
        }

        private static void Complete(
                PartRestorationNanobotHaulOp op, long ticks) {
            Obj obj = op.Data?.Obj;
            if (obj?.IsActive != true
                    || !op.Unit.CanReceiveNanobotPart(
                        op.Data, op.Goal)) {
                Abort(op, ticks, true);
                return;
            }

            ClearCarryRenderOverride(obj);
            if (obj.NanobotCarrierHiveId == op.HiveEntityId) {
                obj.SetNanobotCarrier(0);
            }
            Teleport(obj, op.To);
            if (!op.Unit.LoadObj(op.Data, op.Goal)) {
                Abort(op, ticks, true);
                return;
            }

            op.Data.TryUnreserve(op.Goal);
            op.Goal.WithWorld(-1707724012, true);
            op.Goal.CheckDone(ticks);
            RemoveOp(op);
        }

        private static void Abort(
                PartRestorationNanobotHaulOp op, long ticks,
                bool returnToSource) {
            if (op == null) {
                return;
            }
            ReleaseObject(op, returnToSource);
            if (op.Goal != null) {
                op.Goal.Flags &= ~AIGoalFlags.NanobotWorkable;
                if (AIGoal.IsActive(op.Goal)) {
                    op.Goal.CanRetryAfterTick =
                        ticks + Consts.TicksPer5Minutes;
                }
            }
            RemoveOp(op);
        }

        private static void ReleaseObject(
                PartRestorationNanobotHaulOp op,
                bool returnToSource) {
            Obj obj = op?.Data?.Obj;
            int carrierId = obj?.NanobotCarrierHiveId ?? 0;
            if (obj?.IsActive == true
                    && (carrierId == 0
                        || carrierId == op.HiveEntityId)) {
                ClearCarryRenderOverride(obj);
                if (carrierId == op.HiveEntityId) {
                    obj.SetNanobotCarrier(0);
                }
                if (returnToSource) {
                    RestoreToSource(op.Data, op.SourceStorage,
                        op.SourceWasStored, op.SourcePosition,
                        op.Goal);
                }
            }
            ObjReservation reservation = op?.Data?.Reservation;
            if (reservation == null || op.GoalId == 0u) {
                return;
            }
            if (op.Goal != null) {
                if (reservation.IsFor(op.Goal)
                        && ReferenceEquals(
                            reservation.Goal, op.Goal)) {
                    op.Data.TryUnreserve(op.Goal);
                }
                return;
            }
            uint reservedGoalId = reservation.Goal?.Id
                ?? reservation.LoadGoalId;
            if (reservedGoalId == op.GoalId) {
                op.Data.Reservation = null;
            }
        }

        private static bool TryRecover(
                PartRestorationNanobotHaulOp op) {
            if (state == null || op == null
                    || op.HiveEntityId == 0
                    || !state.Sys.AI.ColonyGoals.TryGetValue(
                        op.GoalId, out AIGoal goal)) {
                return false;
            }
            ObjData data = state.Query.FindObjData.Ask(op.ObjDataId);
            Tile unitTile = state.FindEntity<Tile>(op.UnitEntityId);
            if (data?.Obj?.IsActive != true || unitTile == null
                    || !unitTile.TryGetComponent(
                        out PartRestorationUnitComp unit)) {
                return false;
            }

            op.Goal = goal;
            op.Data = data;
            op.Unit = unit;
            op.SourceStorage = op.SourceStorageEntityId == 0
                ? null
                : state.Components.Find<ObjStorageComp>(
                    op.SourceStorageEntityId);
            if (ActiveOps.ContainsKey(op.GoalId)
                    || ActiveOpsByHive.ContainsKey(op.HiveEntityId)
                    || !AIGoal.IsActive(goal)
                    || goal.Agent != null
                    || !unit.CanReceiveNanobotPart(data, goal)
                    || data.State != ObjState.Active
                    || data.StorageId != 0
                    || data.Obj.Carrier != null
                    || (data.Obj.NanobotCarrierHiveId != 0
                        && data.Obj.NanobotCarrierHiveId
                            != op.HiveEntityId)) {
                return false;
            }

            bool hasGoalReservation = data.Reservation?.IsFor(goal)
                == true && data.Reservation.Goal == goal;
            if (!hasGoalReservation && !data.TryReserve(goal)) {
                return false;
            }
            if (data.Obj.NanobotCarrierHiveId == 0
                    && !data.Obj.SetNanobotCarrier(
                        op.HiveEntityId)) {
                data.TryUnreserve(goal);
                return false;
            }
            if (data.Obj.NanobotCarrierHiveId
                    != op.HiveEntityId) {
                data.TryUnreserve(goal);
                return false;
            }

            op.DurationTicks = Mathf.Max(
                MinimumDurationTicks, op.DurationTicks);
            op.Progress = Mathf.Clamp01(op.Progress);
            op.To = EntityUtils.CenterOf(unit.LoadObjSpotIdx);
            ApplyCarryRenderOverride(data.Obj);
            RegisterGoal(unit, goal);
            AddOp(op);
            goal.Flags |= AIGoalFlags.NanobotWorkable;
            QuantumNanobotCoordination.MarkHiveUnavailable(
                op.HiveEntityId);
            return true;
        }

        private static bool IsActiveOpValid(
                PartRestorationNanobotHaulOp op) {
            if (state == null || op?.Goal == null
                    || !AIGoal.IsActive(op.Goal)
                    || op.Goal.Agent != null
                    || op.Unit == null
                    || !op.Unit.CanReceiveNanobotPart(
                        op.Data, op.Goal)) {
                return false;
            }
            if (op.Data?.Obj?.IsActive != true
                    || op.Data.State != ObjState.Active
                    || op.Data.StorageId != 0
                    || op.Data.Reservation?.IsFor(op.Goal) != true
                    || op.Data.Reservation.Goal != op.Goal
                    || op.Data.Obj.Carrier != null) {
                return false;
            }
            int carrierId = op.Data.Obj.NanobotCarrierHiveId;
            return carrierId == op.HiveEntityId;
        }

        private static void ResolveLoadedReferencesForRelease(
                PartRestorationNanobotHaulOp op) {
            if (state == null || op == null) {
                return;
            }
            op.Data ??= state.Query.FindObjData.Ask(op.ObjDataId);
            if (op.SourceStorage == null
                    && op.SourceStorageEntityId != 0) {
                op.SourceStorage = state.Components.Find<ObjStorageComp>(
                    op.SourceStorageEntityId);
            }
        }

        private static bool IsSourceUsable(
                ObjData data, AIGoal goal) {
            if (data == null || data.BodyPart == null
                    || (data.Reservation != null
                        && (data.Reservation.IsFor(goal) != true
                            || data.Reservation.Goal != goal))) {
                return false;
            }
            if (data.State == ObjState.Stored) {
                return data.StorageId != 0;
            }
            return data.State == ObjState.Active
                && data.StorageId == 0
                && data.Obj?.IsActive == true
                && data.Obj.Carrier == null
                && data.Obj.NanobotCarrierHiveId == 0;
        }

        private static bool IsSourceStorageUsable(
                ObjStorageComp storage, ObjData data) {
            return storage?.Entity?.IsActive == true
                && storage.Tile != null
                && storage.Tile.IsActive
                && storage.Tile.IsConstructed
                && data.StorageId == storage.EntityId;
        }

        private static bool CanHiveReach(
                NanobotHiveComp hive, int targetPos,
                Vector2 targetPosition) {
            return hive != null && hive.CanWork
                && EntityUtils.IsWithinBounds(targetPos)
                && state.Sys.Areas.IsSameAreaOrOutside(
                    hive.Entity.PosIdx, targetPos)
                && Vector2.SqrMagnitude(
                    hive.Entity.Position - targetPosition)
                    <= hive.RangeSqr;
        }

        private static void RestoreToSource(
                ObjData data, ObjStorageComp storage,
                bool sourceWasStored, Vector2 sourcePosition,
                AIGoal goal) {
            if (data?.Obj?.IsActive != true) {
                return;
            }
            if (sourceWasStored
                    && storage?.Entity?.IsActive == true
                    && storage.Store(null, data, goal)) {
                return;
            }
            if (EntityUtils.IsWithinBounds(
                    EntityUtils.ToPos(sourcePosition))) {
                Teleport(data.Obj, sourcePosition);
            }
        }

        private static List<NanobotHiveComp> GetWorkingHives(
                NanobotSys nanobotSystem) {
            return WorkingHivesField?.GetValue(nanobotSystem)
                as List<NanobotHiveComp>;
        }

        private static NanobotHiveComp FindHive(
                List<NanobotHiveComp> hives, int entityId) {
            if (hives == null) {
                return null;
            }
            for (int i = hives.Count - 1; i >= 0; i--) {
                if (hives[i]?.EntityId == entityId) {
                    return hives[i];
                }
            }
            return null;
        }

        private static void MarkHaulingWorked(
                NanobotHiveComp hive, Entity target) {
            if (hive != null && target != null) {
                MarkHaulingWorkedMethod.Invoke(
                    hive, new object[] { target });
            }
        }

        private static void ApplyCarryRenderOverride(Obj obj) {
            obj?.Graphics?.SetTemporaryRendererOverride(
                Renderers.ObjAbove, SpriteOrder.HauledObj);
        }

        private static void ClearCarryRenderOverride(Obj obj) {
            obj?.Graphics?.ClearTemporaryRendererOverride();
        }

        private static void Teleport(Obj obj, Vector2 position) {
            if (obj?.IsActive != true) {
                return;
            }
            obj.Transform.TeleportTo(position);
            obj.Graphics.TeleportTo(position);
        }

        private static void AddOp(
                PartRestorationNanobotHaulOp op) {
            ActiveOps[op.GoalId] = op;
            ActiveOpsByHive[op.HiveEntityId] = op;
        }

        private static void RemoveOp(
                PartRestorationNanobotHaulOp op) {
            if (op == null) {
                return;
            }
            ActiveOps.Remove(op.GoalId);
            if (ActiveOpsByHive.TryGetValue(
                    op.HiveEntityId, out var current)
                    && ReferenceEquals(current, op)) {
                ActiveOpsByHive.Remove(op.HiveEntityId);
            }
        }
    }

    internal sealed class PartRestorationNanobotCandidate {
        internal AIGoal Goal;
        internal PartRestorationUnitComp Unit;
        internal ObjData Data;
        internal ObjStorageComp SourceStorage;
        internal bool SourceWasStored;
        internal Vector2 SourcePosition;
        internal Vector2 DestinationPosition;
        internal float Score;
    }

    internal sealed class PartRestorationNanobotHaulOp {
        internal uint GoalId;
        internal AIGoal Goal;
        internal int HiveEntityId;
        internal uint ObjDataId;
        internal ObjData Data;
        internal int UnitEntityId;
        internal PartRestorationUnitComp Unit;
        internal int SourceStorageEntityId;
        internal ObjStorageComp SourceStorage;
        internal bool SourceWasStored;
        internal Vector2 SourcePosition;
        internal Vector2 From;
        internal Vector2 To;
        internal float DurationTicks;
        internal float Progress;

        internal void Save(ComponentData data, string prefix) {
            data.SetUInt(prefix + "GoalId", GoalId);
            data.SetInt(prefix + "HiveEntityId", HiveEntityId);
            data.SetUInt(prefix + "ObjDataId", ObjDataId);
            data.SetInt(prefix + "UnitEntityId", UnitEntityId);
            data.SetInt(prefix + "SourceStorageEntityId",
                SourceStorageEntityId);
            data.SetBool(prefix + "SourceWasStored",
                SourceWasStored);
            data.SetVector2(prefix + "SourcePosition",
                SourcePosition);
            data.SetVector2(prefix + "From", From);
            data.SetVector2(prefix + "To", To);
            data.SetFloat(prefix + "DurationTicks", DurationTicks);
            data.SetFloat(prefix + "Progress", Progress);
        }

        internal static PartRestorationNanobotHaulOp Load(
                ComponentData data, string prefix) {
            uint goalId = data.GetUInt(prefix + "GoalId", 0u);
            uint objDataId = data.GetUInt(
                prefix + "ObjDataId", 0u);
            int unitEntityId = data.GetInt(
                prefix + "UnitEntityId", 0);
            if (goalId == 0u || objDataId == 0u
                    || unitEntityId == 0) {
                return null;
            }
            return new PartRestorationNanobotHaulOp {
                GoalId = goalId,
                HiveEntityId = data.GetInt(
                    prefix + "HiveEntityId", 0),
                ObjDataId = objDataId,
                UnitEntityId = unitEntityId,
                SourceStorageEntityId = data.GetInt(
                    prefix + "SourceStorageEntityId", 0),
                SourceWasStored = data.GetBool(
                    prefix + "SourceWasStored", false),
                SourcePosition = data.GetVector2(
                    prefix + "SourcePosition"),
                From = data.GetVector2(prefix + "From"),
                To = data.GetVector2(prefix + "To"),
                DurationTicks = data.GetFloat(
                    prefix + "DurationTicks", 1f),
                Progress = data.GetFloat(prefix + "Progress", 0f)
            };
        }
    }

    internal static class QuantumNanobotCoordination {
        private const string AssemblyName = "QuantumStorage";
        private const string TypeName =
            "Game.Systems.QuantumStorage.QuantumNanobotObjectHauling";
        private static FieldInfo activeOpsByHiveField;
        private static FieldInfo hivesUsedByCoreField;
        private static bool assemblyPresent;
        private static bool resolved;
        private static bool warningReported;

        internal static bool IsSafe {
            get {
                Resolve();
                if (!assemblyPresent) {
                    return true;
                }
                bool safe = activeOpsByHiveField != null
                    && hivesUsedByCoreField != null;
                if (!safe && !warningReported) {
                    warningReported = true;
                    D.Warn("Part Restoration Unit found Quantum Storage "
                        + "but could not bind its Nanobot Hive arbitration "
                        + "fields. PRU Nanobot delivery is disabled for "
                        + "this session to prevent double-assigning Hives.");
                }
                return safe;
            }
        }

        internal static bool IsHiveBusy(int hiveEntityId) {
            Resolve();
            if (!assemblyPresent || activeOpsByHiveField == null) {
                return false;
            }
            return activeOpsByHiveField.GetValue(null)
                is IDictionary active && active.Contains(hiveEntityId);
        }

        internal static void MarkHivesUnavailable(
                ICollection<int> hiveEntityIds) {
            if (hiveEntityIds == null || hiveEntityIds.Count == 0) {
                return;
            }
            Resolve();
            if (!assemblyPresent || hivesUsedByCoreField == null
                    || hivesUsedByCoreField.GetValue(null)
                        is not ICollection<int> used) {
                return;
            }
            foreach (int hiveEntityId in hiveEntityIds) {
                used.Add(hiveEntityId);
            }
        }

        internal static void MarkHiveUnavailable(int hiveEntityId) {
            Resolve();
            if (!assemblyPresent || hivesUsedByCoreField == null
                    || hivesUsedByCoreField.GetValue(null)
                        is not ICollection<int> used) {
                return;
            }
            used.Add(hiveEntityId);
        }

        internal static void Reset() {
            activeOpsByHiveField = null;
            hivesUsedByCoreField = null;
            assemblyPresent = false;
            resolved = false;
            warningReported = false;
        }

        private static void Resolve() {
            if (resolved) {
                return;
            }
            Assembly quantumAssembly = null;
            foreach (Assembly assembly
                    in AppDomain.CurrentDomain.GetAssemblies()) {
                if (string.Equals(assembly.GetName().Name,
                        AssemblyName,
                        StringComparison.OrdinalIgnoreCase)) {
                    quantumAssembly = assembly;
                    break;
                }
            }
            if (quantumAssembly == null) {
                return;
            }
            assemblyPresent = true;
            Type type = quantumAssembly.GetType(TypeName, false);
            activeOpsByHiveField = type == null ? null
                : AccessTools.Field(type, "ActiveOpsByHive");
            hivesUsedByCoreField = type == null ? null
                : AccessTools.Field(type, "HivesUsedByCore");
            resolved = true;
        }
    }

    [HarmonyPatch]
    internal static class NanobotSysOnTickPartRestorationPatch {
        private static bool Prepare() {
            return AccessTools.Method(typeof(NanobotSys),
                "OnTick") != null;
        }

        private static MethodBase TargetMethod() {
            return AccessTools.Method(typeof(NanobotSys), "OnTick");
        }

        [HarmonyPriority(HarmonyLib.Priority.Last)]
        private static void Prefix(NanobotSys __instance) {
            try {
                PartRestorationNanobotHauling.BeginCoreTick(
                    __instance.S);
            } catch (Exception ex) {
                PartRestorationNanobotHauling.DisableForSession(
                    ex, "Nanobot tick setup");
            }
        }

        [HarmonyPriority(HarmonyLib.Priority.Last)]
        private static void Postfix(
                NanobotSys __instance, long ticks) {
            try {
                PartRestorationNanobotHauling.OnNanobotTick(
                    __instance, ticks);
            } catch (Exception ex) {
                PartRestorationNanobotHauling.DisableForSession(
                    ex, "Nanobot work assignment");
            }
        }
    }

    [HarmonyPatch]
    internal static class NanobotSysPerformWorkOnPartRestorationPatch {
        private static bool Prepare() {
            return AccessTools.Method(typeof(NanobotSys),
                "PerformWorkOn") != null;
        }

        private static MethodBase TargetMethod() {
            return AccessTools.Method(
                typeof(NanobotSys), "PerformWorkOn");
        }

        [HarmonyPriority(HarmonyLib.Priority.First)]
        private static bool Prefix(NanobotHiveComp hive) {
            try {
                return PartRestorationNanobotHauling
                    .BeforeCoreWork(hive);
            } catch (Exception ex) {
                PartRestorationNanobotHauling.DisableForSession(
                    ex, "Hive work arbitration");
                return true;
            }
        }
    }

    [HarmonyPatch]
    internal static class NanobotSysOnTickProgressPartRestorationPatch {
        private static bool Prepare() {
            return AccessTools.Method(typeof(NanobotSys),
                "OnTickProgress") != null;
        }

        private static MethodBase TargetMethod() {
            return AccessTools.Method(
                typeof(NanobotSys), "OnTickProgress");
        }

        [HarmonyPriority(HarmonyLib.Priority.Last)]
        private static void Postfix(NanobotSys __instance) {
            try {
                PartRestorationNanobotHauling.OnTickProgress(
                    __instance.S.Clock.TicksPerUpdate, G.Ticks);
            } catch (Exception ex) {
                PartRestorationNanobotHauling.DisableForSession(
                    ex, "body-part transit");
            }
        }
    }

    [HarmonyPatch(typeof(NanobotSys), nameof(NanobotSys.Save))]
    internal static class NanobotSysSavePartRestorationPatch {
        private static void Postfix(ref ComponentData __result) {
            try {
                PartRestorationNanobotHauling.Save(ref __result);
            } catch (Exception ex) {
                PartRestorationNanobotHauling.DisableForSession(
                    ex, "saving");
            }
        }
    }

    [HarmonyPatch(typeof(NanobotSys), nameof(NanobotSys.Load))]
    internal static class NanobotSysLoadPartRestorationPatch {
        private static void Postfix(
                NanobotSys __instance, ComponentData data) {
            try {
                PartRestorationNanobotHauling.Load(
                    __instance.S, data);
            } catch (Exception ex) {
                PartRestorationNanobotHauling.DisableForSession(
                    ex, "loading");
            }
        }
    }

    [HarmonyPatch(typeof(NanobotSys), nameof(NanobotSys.Unload))]
    internal static class NanobotSysUnloadPartRestorationPatch {
        private static void Prefix() {
            try {
                PartRestorationNanobotHauling.Reset();
            } catch (Exception ex) {
                D.LogEx(ex, "Part Restoration Unit encountered a "
                    + "cleanup failure while unloading Nanobot Hive "
                    + "delivery.");
            }
        }
    }
}
