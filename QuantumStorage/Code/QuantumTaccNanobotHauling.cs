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
using HarmonyLib;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.QuantumStorage
{
    internal static partial class QuantumNanobotObjectHauling
    {
        private const string TaccAssemblyName = "TraitAndClothingCompatibility";
        private const string TaccMachineTypeName = "TraitAndClothingCompatibility.TaccWashingMachineComp";
        private const string TaccSavePrefix = "QuantumTaccHaul.";
        private const string TaccCountKey = TaccSavePrefix + "Count";
        private const string TaccWashedCountKey = TaccSavePrefix + "WashedCount";
        private const string TaccReachabilityCountKey = TaccSavePrefix + "ReachabilityCount";
        private const string TaccReachabilitySavePrefix = TaccSavePrefix + "Reachability.";
        private const string WardrobeNetworkId = "Wardrobe";
        private const int TaccHiveAssignmentBudgetPerTick = 8;
        private const int TaccCandidateChecksPerHive = 32;
        private const int TaccGoalReconciliationBudgetPerTick = 64;
        private const int TaccReachabilityChecksPerTick = 256;
        private const int TaccReachabilityCacheCapacity = 256;
        private const int TaccEndpointChecksPerProbe = 32;
        private const long TaccAssemblyRetryTicks = Consts.TicksPer5Minutes;

        private static readonly Dictionary<uint, AIGoal> TaccTrackedGoals = new Dictionary<uint, AIGoal>();
        private static readonly Dictionary<uint, QuantumTaccRoute> TaccTrackedRoutes = new Dictionary<uint, QuantumTaccRoute>();
        private static readonly List<uint> TaccGoalOrder = new List<uint>();
        private static readonly HashSet<uint> TaccWashedOutputIds = new HashSet<uint>();
        private static readonly Dictionary<uint, QuantumTaccNanobotHaulOp> TaccActiveOps = new Dictionary<uint, QuantumTaccNanobotHaulOp>();
        private static readonly List<QuantumTaccNanobotHaulOp> LoadedTaccOps = new List<QuantumTaccNanobotHaulOp>();
        private static readonly List<uint> TaccGoalIdsToRemove = new List<uint>();
        private static readonly Dictionary<uint, QuantumTaccReachabilityProof> TaccReachabilityProofs =
            new Dictionary<uint, QuantumTaccReachabilityProof>();
        private static readonly List<uint> TaccReachabilityIdsToRemove = new List<uint>();

        private static Type taccMachineType;
        private static PropertyInfo taccInputDataProperty;
        private static PropertyInfo taccInputLoadedProperty;
        private static PropertyInfo taccLoadSpotProperty;
        private static MethodInfo taccLoadObjMethod;
        private static MethodInfo taccTryOrderWashMethod;
        private static MethodInfo taccFinishWashMethod;
        private static bool taccBindingsResolved;
        private static bool taccPatchesInstalled;
        private static bool taccBindingsReported;
        private static bool taccDisabledForSession;
        private static long nextTaccAssemblyResolveTick;
        private static int taccNextHiveIndex;
        private static int taccGoalCursor;
        private static int taccReconcileCursor;
        private static int taccReachabilityGoalCursor;
        private static int taccReachabilityHiveCursor;
        private static int taccReachabilityHivesChecked;
        private static int taccReachabilityHiveCount;
        private static int taccEndpointCursor;

        public static void InitializeOptionalIntegrations()
        {
            TryResolveTaccBindings(0L, true);
        }

        public static void AfterFullLoad(GameState gameState)
        {
            if (gameState == null)
            {
                return;
            }

            BindState(gameState);
            if (!TryResolveTaccBindings(gameState.Ticks, true))
            {
                ReleaseLoadedTaccOps();
                return;
            }

            RegisterLoadedTaccGoals();
            for (int i = 0; i < LoadedTaccOps.Count; i++)
            {
                QuantumTaccNanobotHaulOp op = LoadedTaccOps[i];
                if (!TryRecoverTaccOp(op))
                {
                    ReleaseLoadedTaccOp(op);
                }
            }
            LoadedTaccOps.Clear();
            RecoverStaleLoadedTaccMachineGoals();
        }

        private static bool TryResolveTaccBindings(long ticks, bool force)
        {
            if (taccDisabledForSession)
            {
                return false;
            }
            if (taccBindingsResolved)
            {
                return true;
            }
            if (!force && ticks < nextTaccAssemblyResolveTick)
            {
                return false;
            }
            nextTaccAssemblyResolveTick = ticks + TaccAssemblyRetryTicks;

            Assembly taccAssembly = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, TaccAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    taccAssembly = assembly;
                    break;
                }
            }
            if (taccAssembly == null)
            {
                return false;
            }

            try
            {
                taccMachineType = taccAssembly.GetType(TaccMachineTypeName, false);
                taccInputDataProperty = taccMachineType == null ? null : AccessTools.Property(taccMachineType, "InputData");
                taccInputLoadedProperty = taccMachineType == null ? null : AccessTools.Property(taccMachineType, "IsInputLoaded");
                taccLoadSpotProperty = taccMachineType == null ? null : AccessTools.Property(taccMachineType, "LoadObjSpotIdx");
                taccLoadObjMethod = taccMachineType == null ? null : AccessTools.Method(taccMachineType, "LoadObj", new[] { typeof(ObjData), typeof(AIGoal) });
                taccTryOrderWashMethod = taccMachineType == null ? null : AccessTools.Method(
                    taccMachineType,
                    "TryOrderWash",
                    new[] { typeof(ObjData), typeof(bool), typeof(string).MakeByRefType() });
                taccFinishWashMethod = taccMachineType == null ? null : AccessTools.Method(
                    taccMachineType,
                    "FinishWash",
                    new[] { typeof(bool), typeof(string) });

                if (taccMachineType == null || taccInputDataProperty == null || taccInputLoadedProperty == null ||
                    taccLoadSpotProperty == null || taccLoadObjMethod == null || taccTryOrderWashMethod == null ||
                    taccFinishWashMethod == null)
                {
                    throw new MissingMemberException("The current TACC Washing Machine contract is incomplete.");
                }

                if (!taccPatchesInstalled)
                {
                    Harmony harmony = new Harmony(QuantumStorageNetworkSys.HarmonyId);
                    harmony.Patch(
                        taccTryOrderWashMethod,
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(QuantumNanobotObjectHauling),
                            nameof(AfterTaccTryOrderWash))));
                    harmony.Patch(
                        taccFinishWashMethod,
                        prefix: new HarmonyMethod(AccessTools.Method(
                            typeof(QuantumNanobotObjectHauling),
                            nameof(BeforeTaccFinishWash))));
                    taccPatchesInstalled = true;
                }

                taccBindingsResolved = true;
                if (!taccBindingsReported)
                {
                    taccBindingsReported = true;
                    D.Log("Quantum Storage enabled optional Nanobot Hive transfers between Quantum Wardrobes and TACC Washing Machines.");
                }
                return true;
            }
            catch (Exception ex)
            {
                DisableTaccForSession(ex, "TACC binding");
                return false;
            }
        }

        private static void AfterTaccTryOrderWash(object __instance, bool __result)
        {
            if (!__result || taccDisabledForSession || __instance is not IComponent machine)
            {
                return;
            }

            try
            {
                GameState gameState = machine.Entity?.S;
                if (gameState == null)
                {
                    return;
                }
                BindState(gameState);
                AIGoal goal = machine.Entity.Goal;
                if (goal != null)
                {
                    RegisterTaccMachineGoal(machine, goal);
                }
            }
            catch (Exception ex)
            {
                DisableTaccForSession(ex, "Washing Machine goal registration");
            }
        }

        private static void BeforeTaccFinishWash(object __instance)
        {
            if (taccDisabledForSession)
            {
                return;
            }

            try
            {
                ObjData data = GetTaccInputData(__instance);
                if (data != null)
                {
                    TaccWashedOutputIds.Add(data.Id);
                }
            }
            catch (Exception ex)
            {
                DisableTaccForSession(ex, "washed-item registration");
            }
        }

        private static void RegisterTaccMachineGoal(IComponent machine, AIGoal goal)
        {
            if (!taccBindingsResolved || machine == null || goal == null ||
                !taccMachineType.IsInstanceOfType(machine) || !ReferenceEquals(goal.Target, machine))
            {
                return;
            }

            ObjData data = GetTaccInputData(machine);
            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (objectSystem == null ||
                !objectSystem.TryGetStoredObjectNetwork(data, WardrobeNetworkId, out _))
            {
                return;
            }

            TrackTaccGoal(goal, QuantumTaccRoute.ToWashingMachine);
        }

        private static bool TryRegisterTaccReturnGoal(StorableObjComp storable, AIGoal goal)
        {
            ObjData data = storable?.Obj?.Data;
            if (!taccBindingsResolved || data == null || goal == null ||
                !TaccWashedOutputIds.Contains(data.Id))
            {
                return false;
            }

            TrackTaccGoal(goal, QuantumTaccRoute.ToQuantumWardrobe);
            return true;
        }

        private static void TrackTaccGoal(AIGoal goal, QuantumTaccRoute route)
        {
            if (goal == null)
            {
                return;
            }

            bool isNew = !TaccTrackedGoals.ContainsKey(goal.Id);
            TaccTrackedGoals[goal.Id] = goal;
            TaccTrackedRoutes[goal.Id] = route;
            if (isNew)
            {
                TaccGoalOrder.Add(goal.Id);
            }
            if (!goal.IsNanobotCompatible)
            {
                goal.NanobotCompatible();
            }
        }

        private static void RegisterLoadedTaccGoals()
        {
            if (state?.Sys?.AI?.ColonyGoals == null)
            {
                return;
            }

            foreach (AIGoal goal in state.Sys.AI.ColonyGoals.Values)
            {
                if (goal?.Target is IComponent machine && taccMachineType.IsInstanceOfType(machine))
                {
                    RegisterTaccMachineGoal(machine, goal);
                    if (!TaccTrackedGoals.ContainsKey(goal.Id) &&
                        IsPreviouslyTrackedAssignedTaccMachineGoal(machine, goal))
                    {
                        TrackTaccGoal(goal, QuantumTaccRoute.ToWashingMachine);
                    }
                }
                else if (goal?.Target is StorableObjComp storable)
                {
                    TryRegisterTaccReturnGoal(storable, goal);
                }
            }
        }

        private static void RecoverStaleLoadedTaccMachineGoals()
        {
            if (state?.Sys?.AI?.ColonyGoals == null)
            {
                return;
            }

            List<AIGoal> staleGoals = null;
            foreach (AIGoal goal in state.Sys.AI.ColonyGoals.Values)
            {
                if (goal == null || !goal.IsNanobotCompatible || goal.Agent != null ||
                    !AIGoal.IsActive(goal) || TaccActiveOps.ContainsKey(goal.Id) ||
                    goal.Target is not IComponent machine || !taccMachineType.IsInstanceOfType(machine))
                {
                    continue;
                }

                ObjData data = GetTaccInputData(machine);
                if (data == null || !IsTaccMachinePending(machine, data, goal) ||
                    data.State != ObjState.Equipped ||
                    data.StorageId != 0 || data.BeingDataId == 0)
                {
                    continue;
                }

                ObjReservation reservation = data.Reservation;
                if (reservation != null &&
                    (!reservation.IsFor(goal) || !ReferenceEquals(reservation.Goal, goal)))
                {
                    continue;
                }

                staleGoals ??= new List<AIGoal>();
                staleGoals.Add(goal);
            }

            if (staleGoals == null)
            {
                return;
            }

            for (int i = 0; i < staleGoals.Count; i++)
            {
                AIGoal goal = staleGoals[i];
                ObjData data = GetTaccInputData(goal.Target as IComponent);
                goal.Flags &= ~(AIGoalFlags.NanobotCompatible | AIGoalFlags.NanobotWorkable);
                AIGoal.Cancel(
                    goal,
                    state.Ticks,
                    "The clothing became equipped before the saved washing task could load it.");
                RemoveTaccTrackedGoal(goal.Id);
                D.Log(
                    "Quantum Storage recovered stale optional TACC washing task {0}; clothing {1} is equipped by being {2}.",
                    goal.Id,
                    data?.Id ?? 0u,
                    data?.BeingDataId ?? 0u);
            }
        }

        private static void TaccOnNanobotTick(
            NanobotSys nanobotSystem,
            long ticks,
            List<NanobotHiveComp> workingHives,
            bool haulingEnabled)
        {
            if (!TryResolveTaccBindings(ticks, false) || state == null)
            {
                return;
            }

            try
            {
                ValidateTaccActiveOps(ticks);
                MaintainTaccActiveOps(workingHives, haulingEnabled);
                ReconcileTaccGoals();

                if (!haulingEnabled || workingHives == null || workingHives.Count == 0 ||
                    state.Research == null || !state.Research.IsUnlocked(NanobotsResearchH))
                {
                    ClearTaccReachabilityProofs();
                    return;
                }

                RefreshTaccReachability(workingHives);

                int count = Math.Min(TaccHiveAssignmentBudgetPerTick, workingHives.Count);
                for (int i = 0; i < count; i++)
                {
                    if (taccNextHiveIndex >= workingHives.Count)
                    {
                        taccNextHiveIndex = 0;
                    }
                    NanobotHiveComp hive = workingHives[taccNextHiveIndex++];
                    if (hive == null || !hive.CanWork)
                    {
                        continue;
                    }

                    QuantumTaccNanobotCandidate candidate = FindTaccCandidate(hive);
                    if (candidate != null && !HivesUsedByCore.Contains(hive.EntityId) &&
                        !ActiveOpsByHive.ContainsKey(hive.EntityId))
                    {
                        StartTaccOp(candidate, hive, nanobotSystem);
                    }
                }
            }
            catch (Exception ex)
            {
                DisableTaccForSession(ex, "TACC Nanobot assignment");
            }
        }

        private static void ReconcileTaccGoals()
        {
            TaccGoalIdsToRemove.Clear();
            int checks = Math.Min(TaccGoalReconciliationBudgetPerTick, TaccGoalOrder.Count);
            for (int i = 0; i < checks; i++)
            {
                if (taccReconcileCursor >= TaccGoalOrder.Count)
                {
                    taccReconcileCursor = 0;
                }
                uint goalId = TaccGoalOrder[taccReconcileCursor++];
                if (!TaccTrackedGoals.TryGetValue(goalId, out AIGoal goal))
                {
                    TaccGoalIdsToRemove.Add(goalId);
                    continue;
                }
                if (goal == null || (!AIGoal.IsActive(goal) && !AIGoal.IsFailed(goal)) ||
                    (AIGoal.IsFailed(goal) && goal.NonRetryable))
                {
                    if (!TaccActiveOps.ContainsKey(goalId))
                    {
                        if (TaccTrackedRoutes.TryGetValue(goalId, out QuantumTaccRoute route) &&
                            route == QuantumTaccRoute.ToQuantumWardrobe &&
                            goal?.Target is StorableObjComp storable && storable.Obj?.Data is ObjData data &&
                            (data.State != ObjState.Active || data.StorageId != 0))
                        {
                            TaccWashedOutputIds.Remove(data.Id);
                        }
                        TaccGoalIdsToRemove.Add(goalId);
                    }
                    continue;
                }
                if (!goal.IsNanobotCompatible)
                {
                    goal.NanobotCompatible();
                }
            }

            for (int i = 0; i < TaccGoalIdsToRemove.Count; i++)
            {
                RemoveTaccTrackedGoal(TaccGoalIdsToRemove[i]);
            }
            TaccGoalIdsToRemove.Clear();
        }

        private static void RefreshTaccReachability(List<NanobotHiveComp> workingHives)
        {
            // Core clears NanobotWorkable before this postfix runs. Preserve a bounded set of
            // previously proven routes while the rotating probe revalidates live Hive coverage.
            TaccReachabilityIdsToRemove.Clear();
            foreach (KeyValuePair<uint, QuantumTaccReachabilityProof> entry in TaccReachabilityProofs)
            {
                uint goalId = entry.Key;
                if (TaccTrackedGoals.TryGetValue(goalId, out AIGoal goal) && goal != null &&
                    TaccTrackedRoutes.ContainsKey(goalId) &&
                    entry.Value != null &&
                    (AIGoal.IsActive(goal) || (AIGoal.IsFailed(goal) && !goal.NonRetryable)))
                {
                    goal.Flags |= AIGoalFlags.NanobotWorkable;
                }
                else
                {
                    TaccReachabilityIdsToRemove.Add(goalId);
                }
            }
            for (int i = 0; i < TaccReachabilityIdsToRemove.Count; i++)
            {
                TaccReachabilityProofs.Remove(TaccReachabilityIdsToRemove[i]);
            }
            TaccReachabilityIdsToRemove.Clear();

            if (TaccGoalOrder.Count == 0)
            {
                ResetTaccReachabilityProbe();
                return;
            }
            if (taccReachabilityHiveCount != workingHives.Count)
            {
                ResetTaccReachabilityProbe();
                taccReachabilityHiveCount = workingHives.Count;
            }

            int pairChecks = 0;
            int completedGoalScans = 0;
            int goalScanLimit = TaccGoalOrder.Count;
            while (pairChecks < TaccReachabilityChecksPerTick && completedGoalScans < goalScanLimit &&
                TaccGoalOrder.Count != 0)
            {
                if (taccReachabilityGoalCursor >= TaccGoalOrder.Count)
                {
                    taccReachabilityGoalCursor = 0;
                }
                uint goalId = TaccGoalOrder[taccReachabilityGoalCursor];
                if (!TaccTrackedGoals.TryGetValue(goalId, out AIGoal goal) ||
                    !TaccTrackedRoutes.TryGetValue(goalId, out QuantumTaccRoute route) ||
                    TaccActiveOps.ContainsKey(goalId))
                {
                    AdvanceTaccReachabilityGoal();
                    completedGoalScans++;
                    continue;
                }

                bool found = false;
                while (pairChecks < TaccReachabilityChecksPerTick &&
                    taccReachabilityHivesChecked < workingHives.Count)
                {
                    if (taccReachabilityHiveCursor >= workingHives.Count)
                    {
                        taccReachabilityHiveCursor = 0;
                    }
                    NanobotHiveComp hive = workingHives[taccReachabilityHiveCursor++];
                    taccReachabilityHivesChecked++;
                    pairChecks++;
                    if (hive == null || !hive.CanWork)
                    {
                        continue;
                    }
                    if (TryGetTaccReachabilityProof(goal, route, hive, out QuantumTaccReachabilityProof proof))
                    {
                        RememberTaccReachability(goal, proof);
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    AdvanceTaccReachabilityGoal();
                    completedGoalScans++;
                }
                else if (taccReachabilityHivesChecked >= workingHives.Count)
                {
                    TaccReachabilityProofs.Remove(goalId);
                    goal.Flags &= ~AIGoalFlags.NanobotWorkable;
                    AdvanceTaccReachabilityGoal();
                    completedGoalScans++;
                }
                else
                {
                    break;
                }
            }
        }

        private static void RememberTaccReachability(AIGoal goal, QuantumTaccReachabilityProof proof)
        {
            if (goal == null || proof == null)
            {
                return;
            }
            goal.Flags |= AIGoalFlags.NanobotWorkable;
            if (TaccReachabilityProofs.ContainsKey(goal.Id) ||
                TaccReachabilityProofs.Count < TaccReachabilityCacheCapacity)
            {
                TaccReachabilityProofs[goal.Id] = proof;
            }
        }

        private static void AdvanceTaccReachabilityGoal()
        {
            taccReachabilityGoalCursor++;
            if (taccReachabilityGoalCursor >= TaccGoalOrder.Count)
            {
                taccReachabilityGoalCursor = 0;
            }
            taccReachabilityHiveCursor = 0;
            taccReachabilityHivesChecked = 0;
        }

        private static void ClearTaccReachabilityProofs()
        {
            foreach (uint goalId in TaccReachabilityProofs.Keys)
            {
                if (TaccTrackedGoals.TryGetValue(goalId, out AIGoal goal) && goal != null)
                {
                    goal.Flags &= ~AIGoalFlags.NanobotWorkable;
                }
            }
            TaccReachabilityProofs.Clear();
            ResetTaccReachabilityProbe();
        }

        private static void ResetTaccReachabilityProbe()
        {
            taccReachabilityGoalCursor = 0;
            taccReachabilityHiveCursor = 0;
            taccReachabilityHivesChecked = 0;
            taccReachabilityHiveCount = 0;
        }

        private static QuantumTaccNanobotCandidate FindTaccCandidate(NanobotHiveComp hive)
        {
            int goalCount = TaccGoalOrder.Count;
            if (goalCount == 0)
            {
                return null;
            }

            QuantumTaccNanobotCandidate best = null;
            int checks = Math.Min(TaccCandidateChecksPerHive, goalCount);
            for (int i = 0; i < checks; i++)
            {
                if (taccGoalCursor >= TaccGoalOrder.Count)
                {
                    taccGoalCursor = 0;
                }
                uint goalId = TaccGoalOrder[taccGoalCursor++];
                if (!TaccTrackedGoals.TryGetValue(goalId, out AIGoal goal) ||
                    !TaccTrackedRoutes.TryGetValue(goalId, out QuantumTaccRoute route) ||
                    TaccActiveOps.ContainsKey(goalId))
                {
                    continue;
                }

                QuantumTaccNanobotCandidate candidate = BuildTaccCandidate(goal, route, hive);
                if (candidate != null)
                {
                    // Reachability describes Hive coverage even when this Hive is already assigned for the tick.
                    RememberTaccReachability(candidate.Goal, CreateTaccReachabilityProof(candidate));
                    if (best == null || candidate.Score < best.Score)
                    {
                        best = candidate;
                    }
                }
            }
            return best;
        }

        private static bool TryGetTaccReachabilityProof(
            AIGoal goal,
            QuantumTaccRoute route,
            NanobotHiveComp hive,
            out QuantumTaccReachabilityProof proof)
        {
            proof = null;
            if (goal == null || goal.JobIdH != JobTypeIdH.Hauling ||
                (!AIGoal.IsActive(goal) && !AIGoal.IsFailed(goal)) ||
                (AIGoal.IsFailed(goal) && goal.NonRetryable) ||
                !goal.IsAvailabilityGateOpen(state) || AIGoal.IsBlockedByFire(goal))
            {
                return false;
            }

            if (TaccReachabilityProofs.TryGetValue(goal.Id, out QuantumTaccReachabilityProof cached))
            {
                if (cached.Route == route && IsTaccReachabilityProofLive(goal, cached))
                {
                    if (CanHiveReachTaccProof(hive, cached))
                    {
                        proof = cached;
                        return true;
                    }
                    return false;
                }
                TaccReachabilityProofs.Remove(goal.Id);
            }

            return route == QuantumTaccRoute.ToWashingMachine
                ? TryCreateTaccMachineReachabilityProof(goal, hive, out proof)
                : TryCreateTaccWardrobeReachabilityProof(goal, hive, out proof);
        }

        private static bool TryCreateTaccMachineReachabilityProof(
            AIGoal goal,
            NanobotHiveComp hive,
            out QuantumTaccReachabilityProof proof)
        {
            proof = null;
            if (goal.Target is not IComponent machine || !taccMachineType.IsInstanceOfType(machine))
            {
                return false;
            }

            ObjData data = GetTaccInputData(machine);
            if (!IsTaccMachinePending(machine, data, goal))
            {
                return false;
            }

            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (objectSystem == null)
            {
                return false;
            }

            Func<ObjStorageComp, bool> isReachable = storage =>
            {
                QuantumTaccReachabilityProof candidate =
                    CreateTaccMachineReachabilityProof(goal, machine, storage);
                return IsTaccReachabilityProofLive(goal, candidate) &&
                    CanHiveReachTaccProof(hive, candidate);
            };

            bool found;
            ObjStorageComp sourceStorage;
            if (data.State == ObjState.Stored)
            {
                found = objectSystem.TrySelectStoredObjectAccessPoint(
                    data,
                    WardrobeNetworkId,
                    ref taccEndpointCursor,
                    TaccEndpointChecksPerProbe,
                    isReachable,
                    null,
                    out sourceStorage);
            }
            else if (goal.Agent != null && IsAssignedTaccObjectInTransit(data, goal))
            {
                found = objectSystem.TrySelectNetworkAccessPoint(
                    WardrobeNetworkId,
                    ref taccEndpointCursor,
                    TaccEndpointChecksPerProbe,
                    isReachable,
                    null,
                    out sourceStorage);
            }
            else
            {
                return false;
            }

            if (!found)
            {
                return false;
            }
            proof = CreateTaccMachineReachabilityProof(goal, machine, sourceStorage);
            return true;
        }

        private static bool TryCreateTaccWardrobeReachabilityProof(
            AIGoal goal,
            NanobotHiveComp hive,
            out QuantumTaccReachabilityProof proof)
        {
            proof = null;
            if (goal.Target is not StorableObjComp source || source.Obj?.Data is not ObjData data ||
                !TaccWashedOutputIds.Contains(data.Id) || data.Obj != source.Obj ||
                data.State != ObjState.Active || data.StorageId != 0 || !source.Obj.IsActive)
            {
                return false;
            }

            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (objectSystem == null)
            {
                return false;
            }

            int sourcePos = source.Obj.PosIdx;
            Vector2 sourcePosition = source.Obj.Position;
            if (!CanHiveReach(hive, sourcePos, sourcePosition))
            {
                return false;
            }

            bool found = objectSystem.TrySelectNetworkAccessPoint(
                WardrobeNetworkId,
                ref taccEndpointCursor,
                TaccEndpointChecksPerProbe,
                storage =>
                    IsDestinationUsable(objectSystem, storage, WardrobeNetworkId, data) &&
                    state.Sys.Areas.IsSameAreaOrOutside(sourcePos, storage.InPort) &&
                    CanHiveReach(hive, storage.InPort, EntityUtils.CenterOf(storage.InPort)),
                null,
                out ObjStorageComp destination);
            if (!found)
            {
                return false;
            }

            proof = new QuantumTaccReachabilityProof
            {
                GoalId = goal.Id,
                Route = QuantumTaccRoute.ToQuantumWardrobe,
                SourceEntityId = source.EntityId,
                DestinationEntityId = destination.EntityId,
                SourcePos = sourcePos,
                DestinationPos = destination.InPort,
                SourcePosition = sourcePosition,
                DestinationPosition = EntityUtils.CenterOf(destination.InPort)
            };
            return true;
        }

        private static QuantumTaccReachabilityProof CreateTaccMachineReachabilityProof(
            AIGoal goal,
            IComponent machine,
            ObjStorageComp sourceStorage)
        {
            int sourcePos = sourceStorage?.InPort ?? -1;
            int destinationPos = GetTaccLoadSpot(machine);
            return new QuantumTaccReachabilityProof
            {
                GoalId = goal?.Id ?? 0u,
                Route = QuantumTaccRoute.ToWashingMachine,
                SourceEntityId = sourceStorage?.EntityId ?? 0,
                DestinationEntityId = machine?.EntityId ?? 0,
                SourcePos = sourcePos,
                DestinationPos = destinationPos,
                SourcePosition = EntityUtils.CenterOf(sourcePos),
                DestinationPosition = EntityUtils.CenterOf(destinationPos)
            };
        }

        private static QuantumTaccReachabilityProof CreateTaccReachabilityProof(
            QuantumTaccNanobotCandidate candidate)
        {
            if (candidate == null)
            {
                return null;
            }
            return new QuantumTaccReachabilityProof
            {
                GoalId = candidate.Goal?.Id ?? 0u,
                Route = candidate.Route,
                SourceEntityId = candidate.Route == QuantumTaccRoute.ToWashingMachine
                    ? candidate.SourceStorage?.EntityId ?? 0
                    : candidate.Data?.Obj?.StorableObj?.EntityId ?? 0,
                DestinationEntityId = candidate.Route == QuantumTaccRoute.ToWashingMachine
                    ? candidate.Machine?.EntityId ?? 0
                    : candidate.Destination?.EntityId ?? 0,
                SourcePos = EntityUtils.ToPos(candidate.SourcePosition),
                DestinationPos = EntityUtils.ToPos(candidate.DestinationPosition),
                SourcePosition = candidate.SourcePosition,
                DestinationPosition = candidate.DestinationPosition
            };
        }

        private static bool IsTaccReachabilityProofLive(
            AIGoal goal,
            QuantumTaccReachabilityProof proof)
        {
            if (state == null || goal == null || proof == null || proof.GoalId != goal.Id ||
                (!AIGoal.IsActive(goal) && !AIGoal.IsFailed(goal)) ||
                (AIGoal.IsFailed(goal) && goal.NonRetryable))
            {
                return false;
            }

            if (proof.Route == QuantumTaccRoute.ToWashingMachine)
            {
                ObjData inputData;
                if (goal.Target is not IComponent machine || machine.EntityId != proof.DestinationEntityId ||
                    !taccMachineType.IsInstanceOfType(machine) ||
                    !IsTaccMachinePending(machine, inputData = GetTaccInputData(machine), goal) ||
                    GetTaccLoadSpot(machine) != proof.DestinationPos)
                {
                    return false;
                }
                ObjStorageComp sourceStorage = state.Components.Find<ObjStorageComp>(proof.SourceEntityId);
                QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
                return sourceStorage?.Entity?.IsActive == true && sourceStorage.Tile?.IsActive == true &&
                    sourceStorage.Tile.IsConstructed && sourceStorage.InPort == proof.SourcePos &&
                    objectSystem != null &&
                    objectSystem.TryGetNetwork(sourceStorage, out QuantumObjectStorageNetwork network) &&
                    network.Id == WardrobeNetworkId &&
                    (inputData.State != ObjState.Stored ||
                        objectSystem.IsStoredObjectAccessibleAt(inputData, WardrobeNetworkId, sourceStorage));
            }

            if (goal.Target is not StorableObjComp source || source.EntityId != proof.SourceEntityId ||
                source.Obj?.Data is not ObjData data || !source.Obj.IsActive ||
                !TaccWashedOutputIds.Contains(data.Id))
            {
                return false;
            }
            ObjStorageComp destination = state.Components.Find<ObjStorageComp>(proof.DestinationEntityId);
            return destination?.InPort == proof.DestinationPos &&
                IsDestinationUsable(
                    QuantumObjectStorageNetworkSys.Get(state),
                    destination,
                    WardrobeNetworkId,
                    data);
        }

        private static bool CanHiveReachTaccProof(
            NanobotHiveComp hive,
            QuantumTaccReachabilityProof proof)
        {
            return proof != null && EntityUtils.IsWithinBounds(proof.SourcePos) &&
                EntityUtils.IsWithinBounds(proof.DestinationPos) &&
                state.Sys.Areas.IsSameAreaOrOutside(proof.SourcePos, proof.DestinationPos) &&
                CanHiveReach(hive, proof.SourcePos, proof.SourcePosition) &&
                CanHiveReach(hive, proof.DestinationPos, proof.DestinationPosition);
        }

        private static QuantumTaccNanobotCandidate BuildTaccCandidate(
            AIGoal goal,
            QuantumTaccRoute route,
            NanobotHiveComp hive)
        {
            if (goal == null || goal.JobIdH != JobTypeIdH.Hauling || goal.Agent != null ||
                (!AIGoal.IsActive(goal) && !AIGoal.IsFailed(goal)) ||
                (AIGoal.IsFailed(goal) && goal.NonRetryable) ||
                !goal.IsAvailabilityGateOpen(state) || AIGoal.IsBlockedByFire(goal))
            {
                return null;
            }

            return route == QuantumTaccRoute.ToWashingMachine
                ? BuildTaccMachineCandidate(goal, hive)
                : BuildTaccWardrobeCandidate(goal, hive);
        }

        private static QuantumTaccNanobotCandidate BuildTaccMachineCandidate(
            AIGoal goal,
            NanobotHiveComp hive)
        {
            if (goal.Target is not IComponent machine || !taccMachineType.IsInstanceOfType(machine))
            {
                return null;
            }
            ObjData data = GetTaccInputData(machine);
            if (!IsTaccMachinePending(machine, data, goal))
            {
                return null;
            }

            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (objectSystem == null ||
                !objectSystem.TrySelectStoredObjectAccessPoint(
                    data,
                    WardrobeNetworkId,
                    ref taccEndpointCursor,
                    TaccEndpointChecksPerProbe,
                    storage => IsTaccMachineAccessPointReachable(machine, hive, storage),
                    storage => ScoreTaccMachineAccessPoint(machine, storage),
                    out ObjStorageComp sourceStorage))
            {
                return null;
            }

            return BuildTaccMachineCandidateAt(goal, hive, machine, data, sourceStorage);
        }

        private static QuantumTaccNanobotCandidate BuildTaccMachineCandidateAt(
            AIGoal goal,
            NanobotHiveComp hive,
            IComponent machine,
            ObjData data,
            ObjStorageComp sourceStorage)
        {
            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (!IsTaccMachinePending(machine, data, goal) || objectSystem == null ||
                !objectSystem.IsStoredObjectAccessibleAt(data, WardrobeNetworkId, sourceStorage) ||
                !IsTaccMachineAccessPointReachable(machine, hive, sourceStorage))
            {
                return null;
            }

            Vector2 sourcePosition = EntityUtils.CenterOf(sourceStorage.InPort);
            Vector2 destinationPosition = EntityUtils.CenterOf(GetTaccLoadSpot(machine));
            return new QuantumTaccNanobotCandidate
            {
                Route = QuantumTaccRoute.ToWashingMachine,
                Goal = goal,
                Data = data,
                Machine = machine,
                SourceStorage = sourceStorage,
                SourceWasStored = true,
                SourcePosition = sourcePosition,
                DestinationPosition = destinationPosition,
                Score = Vector2.SqrMagnitude(hive.Entity.Position - sourcePosition) +
                    Vector2.SqrMagnitude(sourcePosition - destinationPosition)
            };
        }

        private static bool IsTaccMachineAccessPointReachable(
            IComponent machine,
            NanobotHiveComp hive,
            ObjStorageComp storage)
        {
            int sourcePos = storage?.InPort ?? -1;
            int destinationPos = GetTaccLoadSpot(machine);
            Vector2 sourcePosition = EntityUtils.CenterOf(sourcePos);
            Vector2 destinationPosition = EntityUtils.CenterOf(destinationPos);
            return EntityUtils.IsWithinBounds(sourcePos) && EntityUtils.IsWithinBounds(destinationPos) &&
                state.Sys.Areas.IsSameAreaOrOutside(sourcePos, destinationPos) &&
                CanHiveReach(hive, sourcePos, sourcePosition) &&
                CanHiveReach(hive, destinationPos, destinationPosition);
        }

        private static float ScoreTaccMachineAccessPoint(
            IComponent machine,
            ObjStorageComp storage)
        {
            Vector2 sourcePosition = EntityUtils.CenterOf(storage.InPort);
            Vector2 destinationPosition = EntityUtils.CenterOf(GetTaccLoadSpot(machine));
            return Vector2.SqrMagnitude(sourcePosition - destinationPosition);
        }

        private static QuantumTaccNanobotCandidate BuildTaccWardrobeCandidate(
            AIGoal goal,
            NanobotHiveComp hive)
        {
            if (goal.Target is not StorableObjComp source || source.Obj?.Data == null)
            {
                return null;
            }
            ObjData data = source.Obj.Data;
            if (!TaccWashedOutputIds.Contains(data.Id) || !IsLooseSourceUsable(source, data, goal))
            {
                return null;
            }

            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (objectSystem == null)
            {
                return null;
            }

            Vector2 sourcePosition = source.Obj.Position;
            if (!objectSystem.TrySelectNetworkAccessPoint(
                    WardrobeNetworkId,
                    ref taccEndpointCursor,
                    TaccEndpointChecksPerProbe,
                    storage => IsTaccWardrobeAccessPointReachable(
                        objectSystem,
                        source,
                        data,
                        hive,
                        storage),
                    storage =>
                        Vector2.SqrMagnitude(hive.Entity.Position - sourcePosition) +
                        Vector2.SqrMagnitude(sourcePosition - EntityUtils.CenterOf(storage.InPort)),
                    out ObjStorageComp bestStorage))
            {
                return null;
            }

            return BuildTaccWardrobeCandidateAt(goal, hive, source, data, bestStorage);
        }

        private static QuantumTaccNanobotCandidate BuildTaccWardrobeCandidateAt(
            AIGoal goal,
            NanobotHiveComp hive,
            StorableObjComp source,
            ObjData data,
            ObjStorageComp destination)
        {
            QuantumObjectStorageNetworkSys objectSystem = QuantumObjectStorageNetworkSys.Get(state);
            if (!TaccWashedOutputIds.Contains(data.Id) || !IsLooseSourceUsable(source, data, goal) ||
                objectSystem == null ||
                !IsTaccWardrobeAccessPointReachable(objectSystem, source, data, hive, destination))
            {
                return null;
            }

            Vector2 sourcePosition = source.Obj.Position;
            Vector2 destinationPosition = EntityUtils.CenterOf(destination.InPort);
            return new QuantumTaccNanobotCandidate
            {
                Route = QuantumTaccRoute.ToQuantumWardrobe,
                Goal = goal,
                Data = data,
                Destination = destination,
                SourceWasStored = false,
                SourcePosition = sourcePosition,
                DestinationPosition = destinationPosition,
                Score = Vector2.SqrMagnitude(hive.Entity.Position - sourcePosition) +
                    Vector2.SqrMagnitude(sourcePosition - destinationPosition)
            };
        }

        private static bool IsTaccWardrobeAccessPointReachable(
            QuantumObjectStorageNetworkSys objectSystem,
            StorableObjComp source,
            ObjData data,
            NanobotHiveComp hive,
            ObjStorageComp destination)
        {
            if (!IsDestinationUsable(objectSystem, destination, WardrobeNetworkId, data))
            {
                return false;
            }
            int sourcePos = source?.Obj?.PosIdx ?? -1;
            Vector2 sourcePosition = source?.Obj?.Position ?? Vector2.zero;
            Vector2 destinationPosition = EntityUtils.CenterOf(destination.InPort);
            return EntityUtils.IsWithinBounds(sourcePos) &&
                state.Sys.Areas.IsSameAreaOrOutside(sourcePos, destination.InPort) &&
                CanHiveReach(hive, sourcePos, sourcePosition) &&
                CanHiveReach(hive, destination.InPort, destinationPosition);
        }

        private static QuantumTaccNanobotCandidate RevalidateTaccCandidate(
            QuantumTaccNanobotCandidate candidate,
            NanobotHiveComp hive)
        {
            AIGoal goal = candidate?.Goal;
            if (goal == null || goal.JobIdH != JobTypeIdH.Hauling || goal.Agent != null ||
                (!AIGoal.IsActive(goal) && !AIGoal.IsFailed(goal)) ||
                (AIGoal.IsFailed(goal) && goal.NonRetryable) ||
                !goal.IsAvailabilityGateOpen(state) || AIGoal.IsBlockedByFire(goal))
            {
                return null;
            }

            if (candidate.Route == QuantumTaccRoute.ToWashingMachine)
            {
                return BuildTaccMachineCandidateAt(
                    goal,
                    hive,
                    candidate.Machine,
                    candidate.Data,
                    candidate.SourceStorage);
            }

            return goal.Target is StorableObjComp source
                ? BuildTaccWardrobeCandidateAt(
                    goal,
                    hive,
                    source,
                    candidate.Data,
                    candidate.Destination)
                : null;
        }

        private static bool StartTaccOp(
            QuantumTaccNanobotCandidate candidate,
            NanobotHiveComp hive,
            NanobotSys nanobotSystem)
        {
            if (candidate?.Goal == null || candidate.Data == null || hive == null || !hive.CanWork ||
                HivesUsedByCore.Contains(hive.EntityId) || ActiveOpsByHive.ContainsKey(hive.EntityId) ||
                TaccActiveOps.ContainsKey(candidate.Goal.Id))
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

            QuantumTaccNanobotCandidate current = RevalidateTaccCandidate(candidate, hive);
            if (current == null)
            {
                return false;
            }
            candidate = current;

            bool alreadyReserved = candidate.Data.Reservation?.IsFor(candidate.Goal) == true &&
                candidate.Data.Reservation.Goal == candidate.Goal;
            if (!alreadyReserved && !candidate.Data.TryReserve(candidate.Goal))
            {
                return false;
            }

            if (candidate.SourceWasStored && !state.Query.UnstoreObj.Ask(candidate.Data, candidate.SourceStorage))
            {
                if (!alreadyReserved)
                {
                    candidate.Data.TryUnreserve(candidate.Goal);
                }
                return false;
            }

            Obj obj = candidate.Data.Obj;
            if (obj?.IsActive != true || obj.Carrier != null || obj.NanobotCarrierHiveId != 0 ||
                !obj.SetNanobotCarrier(hive.EntityId))
            {
                RestoreTaccSource(candidate.Data, candidate.SourceStorage, candidate.SourceWasStored,
                    candidate.SourcePosition, candidate.Goal);
                if (!alreadyReserved)
                {
                    candidate.Data.TryUnreserve(candidate.Goal);
                }
                return false;
            }

            Vector2 from = obj.Position;
            float distance = Mathf.Max(1f, Vector2.Distance(from, candidate.DestinationPosition));
            float workSpeed = Mathf.Max(0.001f, hive.ActualWorkSpeed);
            QuantumTaccNanobotHaulOp op = new QuantumTaccNanobotHaulOp
            {
                Route = candidate.Route,
                GoalId = candidate.Goal.Id,
                Goal = candidate.Goal,
                HiveEntityId = hive.EntityId,
                ObjDataId = candidate.Data.Id,
                Data = candidate.Data,
                MachineEntityId = candidate.Machine?.EntityId ?? 0,
                Machine = candidate.Machine,
                SourceStorageEntityId = candidate.SourceStorage?.EntityId ?? 0,
                SourceStorage = candidate.SourceStorage,
                DestinationEntityId = candidate.Destination?.EntityId ?? 0,
                Destination = candidate.Destination,
                SourceWasStored = candidate.SourceWasStored,
                OwnsReservation = !alreadyReserved,
                OwnsCarrier = true,
                SourcePosition = candidate.SourcePosition,
                From = from,
                To = candidate.DestinationPosition,
                DurationTicks = Mathf.Max(MinimumDurationTicks, distance / workSpeed),
                Progress = 0f
            };

            ApplyCarryRenderOverride(obj);
            AddTaccOp(op);
            candidate.Goal.Flags |= AIGoalFlags.NanobotWorkable;
            MarkHaulingWorked(hive, obj);
            if (hive.ShowParticles && hive.Tile.IsActive && A.IsCosmeticParticlesOn)
            {
                hive.ShowParticles = false;
                Vector2 size = obj.Size;
                if (size.x < 1f || size.y < 1f)
                {
                    size = Consts.V2One;
                }
                nanobotSystem.ShowParticlesAt(new NanobotSys.NanobotFX(hive.Tile, obj, size));
            }
            return true;
        }

        private static void TaccOnTickProgress(float ticksPerFrame, long ticks)
        {
            if (TaccActiveOps.Count == 0)
            {
                return;
            }

            try
            {
                TaccGoalIdsToRemove.Clear();
                foreach (KeyValuePair<uint, QuantumTaccNanobotHaulOp> entry in TaccActiveOps)
                {
                    QuantumTaccNanobotHaulOp op = entry.Value;
                    if (!IsTaccOpValid(op))
                    {
                        TaccGoalIdsToRemove.Add(entry.Key);
                        continue;
                    }

                    op.Progress = Mathf.Min(1f, op.Progress + ticksPerFrame / op.DurationTicks);
                    Teleport(op.Data.Obj, Vector2.Lerp(op.From, op.To, op.Progress));
                    if (op.Progress >= 1f)
                    {
                        TaccGoalIdsToRemove.Add(entry.Key);
                    }
                }

                for (int i = 0; i < TaccGoalIdsToRemove.Count; i++)
                {
                    if (!TaccActiveOps.TryGetValue(TaccGoalIdsToRemove[i], out QuantumTaccNanobotHaulOp op))
                    {
                        continue;
                    }
                    if (IsTaccOpValid(op))
                    {
                        CompleteTaccOp(op, ticks);
                    }
                    else
                    {
                        AbortTaccOp(op, ticks, true);
                    }
                }
                TaccGoalIdsToRemove.Clear();
            }
            catch (Exception ex)
            {
                DisableTaccForSession(ex, "TACC Nanobot transit");
            }
        }

        private static void CompleteTaccOp(QuantumTaccNanobotHaulOp op, long ticks)
        {
            Obj obj = op.Data?.Obj;
            if (obj?.IsActive != true)
            {
                AbortTaccOp(op, ticks, true);
                return;
            }

            ClearCarryRenderOverride(obj);
            if (obj.NanobotCarrierHiveId == op.HiveEntityId)
            {
                obj.SetNanobotCarrier(0);
            }

            bool completed;
            if (op.Route == QuantumTaccRoute.ToWashingMachine)
            {
                Teleport(obj, op.To);
                completed = InvokeTaccLoadObj(op.Machine, op.Data, op.Goal);
            }
            else
            {
                completed = op.Destination?.Store(null, op.Data, op.Goal) == true;
            }
            if (!completed)
            {
                AbortTaccOp(op, ticks, true);
                return;
            }

            op.Data.TryUnreserve(op.Goal);
            int completedState = op.Route == QuantumTaccRoute.ToWashingMachine
                ? AIVarsH.IsObjLoaded
                : AIVarsH.IsObjStored;
            op.Goal.WithWorld(completedState, true);
            op.Goal.CheckDone(ticks);
            if (op.Route == QuantumTaccRoute.ToQuantumWardrobe)
            {
                TaccWashedOutputIds.Remove(op.ObjDataId);
            }
            RemoveTaccOp(op);
            RemoveTaccTrackedGoal(op.GoalId);
        }

        private static void AbortTaccOp(QuantumTaccNanobotHaulOp op, long ticks, bool returnToSource)
        {
            if (op == null)
            {
                return;
            }

            ReleaseTaccObject(op, returnToSource);
            if (op.Goal != null)
            {
                op.Goal.Flags &= ~AIGoalFlags.NanobotWorkable;
                if (AIGoal.IsActive(op.Goal))
                {
                    op.Goal.CanRetryAfterTick = ticks + Consts.TicksPer5Minutes;
                }
            }
            RemoveTaccOp(op);
        }

        private static void ReleaseTaccObject(QuantumTaccNanobotHaulOp op, bool returnToSource)
        {
            Obj obj = op?.Data?.Obj;
            if (op?.OwnsCarrier == true && obj?.IsActive == true)
            {
                ClearCarryRenderOverride(obj);
                if (obj.NanobotCarrierHiveId == op.HiveEntityId)
                {
                    obj.SetNanobotCarrier(0);
                }

                if (returnToSource)
                {
                    if (op.SourceWasStored && op.SourceStorage?.Entity?.IsActive == true &&
                        op.SourceStorage.Store(null, op.Data, op.Goal))
                    {
                    }
                    else if (EntityUtils.IsWithinBounds(EntityUtils.ToPos(op.SourcePosition)))
                    {
                        Teleport(obj, op.SourcePosition);
                    }
                }
            }

            ReleaseTaccReservation(op);
        }

        private static void ReleaseTaccReservation(QuantumTaccNanobotHaulOp op)
        {
            ObjReservation reservation = op?.Data?.Reservation;
            if (reservation == null || op.GoalId == 0u)
            {
                return;
            }

            if (op.Goal != null)
            {
                bool shouldRelease = op.Route == QuantumTaccRoute.ToQuantumWardrobe ||
                    !AIGoal.IsActive(op.Goal);
                if (shouldRelease && reservation.IsFor(op.Goal) &&
                    ReferenceEquals(reservation.Goal, op.Goal))
                {
                    op.Data.TryUnreserve(op.Goal);
                }
                return;
            }

            uint reservedGoalId = reservation.Goal?.Id ?? reservation.LoadGoalId;
            if (reservedGoalId == op.GoalId)
            {
                op.Data.Reservation = null;
            }
        }

        private static void RestoreTaccSource(
            ObjData data,
            ObjStorageComp storage,
            bool sourceWasStored,
            Vector2 sourcePosition,
            AIGoal goal)
        {
            if (data?.Obj?.IsActive != true)
            {
                return;
            }
            if (sourceWasStored && storage?.Entity?.IsActive == true && storage.Store(null, data, goal))
            {
                return;
            }
            if (EntityUtils.IsWithinBounds(EntityUtils.ToPos(sourcePosition)))
            {
                Teleport(data.Obj, sourcePosition);
            }
        }

        private static void ValidateTaccActiveOps(long ticks)
        {
            if (TaccActiveOps.Count == 0)
            {
                return;
            }
            TaccGoalIdsToRemove.Clear();
            foreach (KeyValuePair<uint, QuantumTaccNanobotHaulOp> entry in TaccActiveOps)
            {
                if (!IsTaccOpValid(entry.Value))
                {
                    TaccGoalIdsToRemove.Add(entry.Key);
                }
            }
            for (int i = 0; i < TaccGoalIdsToRemove.Count; i++)
            {
                if (TaccActiveOps.TryGetValue(TaccGoalIdsToRemove[i], out QuantumTaccNanobotHaulOp op))
                {
                    AbortTaccOp(op, ticks, true);
                }
            }
            TaccGoalIdsToRemove.Clear();
        }

        private static void MaintainTaccActiveOps(List<NanobotHiveComp> hives, bool haulingEnabled)
        {
            if (!haulingEnabled)
            {
                return;
            }
            foreach (QuantumTaccNanobotHaulOp op in TaccActiveOps.Values)
            {
                op.Goal.Flags |= AIGoalFlags.NanobotWorkable;
                NanobotHiveComp hive = FindHive(hives, op.HiveEntityId);
                if (hive != null)
                {
                    MarkHaulingWorked(hive, op.Data?.Obj);
                }
            }
        }

        private static bool IsTaccOpValid(QuantumTaccNanobotHaulOp op)
        {
            if (state == null || op?.Goal == null || !AIGoal.IsActive(op.Goal) || op.Goal.Agent != null ||
                op.Data?.Obj?.IsActive != true || op.Data.State != ObjState.Active || op.Data.StorageId != 0 ||
                op.Data.Obj.Carrier != null || op.Data.Reservation?.IsFor(op.Goal) != true ||
                op.Data.Reservation.Goal != op.Goal)
            {
                return false;
            }
            int carrierId = op.Data.Obj.NanobotCarrierHiveId;
            if (carrierId != op.HiveEntityId)
            {
                return false;
            }
            if (op.Route == QuantumTaccRoute.ToWashingMachine)
            {
                return IsTaccMachinePending(op.Machine, op.Data, op.Goal);
            }
            return TaccWashedOutputIds.Contains(op.ObjDataId) &&
                IsDestinationUsable(QuantumObjectStorageNetworkSys.Get(state), op.Destination, WardrobeNetworkId, op.Data);
        }

        private static bool IsTaccMachinePending(IComponent machine, ObjData data, AIGoal goal)
        {
            if (machine?.Entity?.IsActive != true || !taccMachineType.IsInstanceOfType(machine) ||
                !ReferenceEquals(goal?.Target, machine) || !ReferenceEquals(GetTaccInputData(machine), data))
            {
                return false;
            }
            return taccInputLoadedProperty.GetValue(machine) is bool loaded && !loaded;
        }

        private static bool IsPreviouslyTrackedAssignedTaccMachineGoal(IComponent machine, AIGoal goal)
        {
            if (goal?.Agent == null || !goal.IsNanobotCompatible)
            {
                return false;
            }
            ObjData data = GetTaccInputData(machine);
            return IsTaccMachinePending(machine, data, goal) && IsAssignedTaccObjectInTransit(data, goal);
        }

        private static bool IsAssignedTaccObjectInTransit(ObjData data, AIGoal goal)
        {
            return data?.Obj?.IsActive == true && data.State == ObjState.Active && data.StorageId == 0 &&
                data.Reservation?.IsFor(goal) == true && ReferenceEquals(data.Reservation.Goal, goal);
        }

        private static ObjData GetTaccInputData(object machine)
        {
            return machine == null || taccInputDataProperty == null
                ? null
                : taccInputDataProperty.GetValue(machine) as ObjData;
        }

        private static int GetTaccLoadSpot(object machine)
        {
            return machine == null || taccLoadSpotProperty == null
                ? -1
                : (int)taccLoadSpotProperty.GetValue(machine);
        }

        private static bool InvokeTaccLoadObj(IComponent machine, ObjData data, AIGoal goal)
        {
            return machine != null && taccLoadObjMethod != null &&
                taccLoadObjMethod.Invoke(machine, new object[] { data, goal }) is bool loaded && loaded;
        }

        private static void AddTaccOp(QuantumTaccNanobotHaulOp op)
        {
            TaccActiveOps[op.GoalId] = op;
            ActiveOpsByHive[op.HiveEntityId] = op;
        }

        private static void RemoveTaccOp(QuantumTaccNanobotHaulOp op)
        {
            if (op == null)
            {
                return;
            }
            TaccActiveOps.Remove(op.GoalId);
            if (ActiveOpsByHive.TryGetValue(op.HiveEntityId, out object current) && ReferenceEquals(current, op))
            {
                ActiveOpsByHive.Remove(op.HiveEntityId);
            }
        }

        private static void RemoveTaccTrackedGoal(uint goalId)
        {
            TaccTrackedGoals.Remove(goalId);
            TaccTrackedRoutes.Remove(goalId);
            TaccReachabilityProofs.Remove(goalId);
            ResetTaccReachabilityProbe();
            int index = TaccGoalOrder.IndexOf(goalId);
            if (index >= 0)
            {
                TaccGoalOrder.RemoveAt(index);
                if (taccGoalCursor > index)
                {
                    taccGoalCursor--;
                }
                if (taccGoalCursor >= TaccGoalOrder.Count)
                {
                    taccGoalCursor = 0;
                }
                if (taccReconcileCursor > index)
                {
                    taccReconcileCursor--;
                }
                if (taccReconcileCursor >= TaccGoalOrder.Count)
                {
                    taccReconcileCursor = 0;
                }
            }
        }

        private static void TaccSave(ref ComponentData data)
        {
            if (TaccActiveOps.Count == 0 && TaccWashedOutputIds.Count == 0 &&
                TaccReachabilityProofs.Count == 0 && data == null)
            {
                return;
            }
            data ??= new ComponentData(0, NanobotSys.SysId);
            data.SetInt(TaccCountKey, TaccActiveOps.Count);
            int index = 0;
            foreach (QuantumTaccNanobotHaulOp op in TaccActiveOps.Values)
            {
                op.Save(data, TaccSavePrefix + index + ".");
                index++;
            }

            data.SetInt(TaccWashedCountKey, TaccWashedOutputIds.Count);
            index = 0;
            foreach (uint objectId in TaccWashedOutputIds)
            {
                data.SetUInt(TaccSavePrefix + "Washed." + index, objectId);
                index++;
            }

            data.SetInt(TaccReachabilityCountKey, TaccReachabilityProofs.Count);
            index = 0;
            foreach (QuantumTaccReachabilityProof proof in TaccReachabilityProofs.Values)
            {
                proof.Save(data, TaccReachabilitySavePrefix + index + ".");
                index++;
            }
        }

        private static void TaccLoad(ComponentData data)
        {
            TaccStateReset();
            if (data == null)
            {
                return;
            }

            int count = data.GetInt(TaccCountKey, 0);
            for (int i = 0; i < count; i++)
            {
                QuantumTaccNanobotHaulOp op = QuantumTaccNanobotHaulOp.Load(data, TaccSavePrefix + i + ".");
                if (op != null)
                {
                    LoadedTaccOps.Add(op);
                }
            }
            int washedCount = data.GetInt(TaccWashedCountKey, 0);
            for (int i = 0; i < washedCount; i++)
            {
                uint objectId = data.GetUInt(TaccSavePrefix + "Washed." + i, 0u);
                if (objectId != 0u)
                {
                    TaccWashedOutputIds.Add(objectId);
                }
            }

            int reachabilityCount = Math.Min(
                Math.Max(0, data.GetInt(TaccReachabilityCountKey, 0)),
                TaccReachabilityCacheCapacity);
            for (int i = 0; i < reachabilityCount; i++)
            {
                QuantumTaccReachabilityProof proof = QuantumTaccReachabilityProof.Load(
                    data,
                    TaccReachabilitySavePrefix + i + ".");
                if (proof != null)
                {
                    TaccReachabilityProofs[proof.GoalId] = proof;
                }
            }
        }

        private static bool TryRecoverTaccOp(QuantumTaccNanobotHaulOp op)
        {
            if (state == null || op == null || op.HiveEntityId == 0 ||
                ActiveOpsByHive.ContainsKey(op.HiveEntityId) || TaccActiveOps.ContainsKey(op.GoalId) ||
                !state.Sys.AI.ColonyGoals.TryGetValue(op.GoalId, out AIGoal goal))
            {
                return false;
            }
            ObjData data = state.Query.FindObjData.Ask(op.ObjDataId);
            if (data?.Obj?.IsActive != true || !AIGoal.IsActive(goal) || goal.Agent != null ||
                data.State != ObjState.Active || data.StorageId != 0 || data.Obj.Carrier != null)
            {
                return false;
            }

            op.Goal = goal;
            op.Data = data;
            op.SourceStorage = op.SourceStorageEntityId == 0
                ? null
                : state.Components.Find<ObjStorageComp>(op.SourceStorageEntityId);
            if (op.Route == QuantumTaccRoute.ToWashingMachine)
            {
                op.Machine = goal.Target as IComponent;
                if (!IsTaccMachinePending(op.Machine, data, goal))
                {
                    return false;
                }
            }
            else
            {
                Tile destinationTile = state.FindEntity<Tile>(op.DestinationEntityId);
                if (destinationTile == null || !destinationTile.TryGetComponent(out ObjStorageComp destination))
                {
                    return false;
                }
                op.Destination = destination;
                if (!TaccWashedOutputIds.Contains(data.Id) ||
                    !IsDestinationUsable(QuantumObjectStorageNetworkSys.Get(state), destination, WardrobeNetworkId, data))
                {
                    return false;
                }
            }

            bool hasGoalReservation = data.Reservation?.IsFor(goal) == true && data.Reservation.Goal == goal;
            if (!hasGoalReservation && !data.TryReserve(goal))
            {
                return false;
            }
            if (data.Obj.NanobotCarrierHiveId == 0 && !data.Obj.SetNanobotCarrier(op.HiveEntityId))
            {
                if (!hasGoalReservation)
                {
                    data.TryUnreserve(goal);
                }
                return false;
            }
            if (data.Obj.NanobotCarrierHiveId != op.HiveEntityId)
            {
                if (!hasGoalReservation)
                {
                    data.TryUnreserve(goal);
                }
                return false;
            }

            op.OwnsCarrier = true;
            op.OwnsReservation = op.OwnsReservation || !hasGoalReservation;
            op.DurationTicks = Mathf.Max(MinimumDurationTicks, op.DurationTicks);
            op.Progress = Mathf.Clamp01(op.Progress);
            ApplyCarryRenderOverride(data.Obj);
            AddTaccOp(op);
            TrackTaccGoal(goal, op.Route);
            goal.Flags |= AIGoalFlags.NanobotWorkable;
            return true;
        }

        private static void ReleaseLoadedTaccOps()
        {
            for (int i = 0; i < LoadedTaccOps.Count; i++)
            {
                ReleaseLoadedTaccOp(LoadedTaccOps[i]);
            }
            LoadedTaccOps.Clear();
        }

        private static void ReleaseLoadedTaccOp(QuantumTaccNanobotHaulOp op)
        {
            if (state == null || op == null)
            {
                return;
            }
            op.Data ??= state.Query.FindObjData.Ask(op.ObjDataId);
            op.SourceStorage ??= op.SourceStorageEntityId == 0
                ? null
                : state.Components.Find<ObjStorageComp>(op.SourceStorageEntityId);
            if (state.Sys.AI.ColonyGoals.TryGetValue(op.GoalId, out AIGoal goal))
            {
                op.Goal = goal;
            }
            ReleaseTaccObject(op, true);
        }

        private static void TaccReset()
        {
            QuantumTaccNanobotHaulOp[] ops = new QuantumTaccNanobotHaulOp[TaccActiveOps.Count];
            TaccActiveOps.Values.CopyTo(ops, 0);
            for (int i = 0; i < ops.Length; i++)
            {
                ReleaseTaccObject(ops[i], true);
                RemoveTaccOp(ops[i]);
            }
            TaccStateReset();
            taccMachineType = null;
            taccInputDataProperty = null;
            taccInputLoadedProperty = null;
            taccLoadSpotProperty = null;
            taccLoadObjMethod = null;
            taccTryOrderWashMethod = null;
            taccFinishWashMethod = null;
            taccBindingsResolved = false;
            taccPatchesInstalled = false;
            taccBindingsReported = false;
            taccDisabledForSession = false;
            nextTaccAssemblyResolveTick = 0L;
        }

        private static void TaccStateReset()
        {
            TaccTrackedGoals.Clear();
            TaccTrackedRoutes.Clear();
            TaccGoalOrder.Clear();
            TaccWashedOutputIds.Clear();
            TaccActiveOps.Clear();
            LoadedTaccOps.Clear();
            TaccGoalIdsToRemove.Clear();
            TaccReachabilityProofs.Clear();
            TaccReachabilityIdsToRemove.Clear();
            taccNextHiveIndex = 0;
            taccGoalCursor = 0;
            taccReconcileCursor = 0;
            taccEndpointCursor = 0;
            ResetTaccReachabilityProbe();
        }

        private static void DisableTaccForSession(Exception exception, string operation)
        {
            if (taccDisabledForSession)
            {
                return;
            }
            taccDisabledForSession = true;
            QuantumTaccNanobotHaulOp[] ops = new QuantumTaccNanobotHaulOp[TaccActiveOps.Count];
            TaccActiveOps.Values.CopyTo(ops, 0);
            for (int i = 0; i < ops.Length; i++)
            {
                try
                {
                    AbortTaccOp(ops[i], G.Ticks, true);
                }
                catch
                {
                }
            }
            foreach (AIGoal goal in TaccTrackedGoals.Values)
            {
                if (goal != null)
                {
                    goal.Flags &= ~AIGoalFlags.NanobotWorkable;
                }
            }
            TaccTrackedGoals.Clear();
            TaccTrackedRoutes.Clear();
            TaccGoalOrder.Clear();
            TaccReachabilityProofs.Clear();
            TaccReachabilityIdsToRemove.Clear();
            ResetTaccReachabilityProbe();
            D.LogEx(exception,
                "Quantum Storage disabled optional TACC Nanobot transfers for this session after a failure during {0}. TACC, ordinary hauling, and Quantum storage remain available.",
                operation);
        }
    }

    internal enum QuantumTaccRoute
    {
        ToWashingMachine = 1,
        ToQuantumWardrobe = 2
    }

    internal sealed class QuantumTaccReachabilityProof
    {
        public uint GoalId;
        public QuantumTaccRoute Route;
        public int SourceEntityId;
        public int DestinationEntityId;
        public int SourcePos;
        public int DestinationPos;
        public Vector2 SourcePosition;
        public Vector2 DestinationPosition;

        public void Save(ComponentData data, string prefix)
        {
            data.SetUInt(prefix + "GoalId", GoalId);
            data.SetInt(prefix + "Route", (int)Route);
            data.SetInt(prefix + "SourceEntityId", SourceEntityId);
            data.SetInt(prefix + "DestinationEntityId", DestinationEntityId);
            data.SetInt(prefix + "SourcePos", SourcePos);
            data.SetInt(prefix + "DestinationPos", DestinationPos);
            data.SetVector2(prefix + "SourcePosition", SourcePosition);
            data.SetVector2(prefix + "DestinationPosition", DestinationPosition);
        }

        public static QuantumTaccReachabilityProof Load(ComponentData data, string prefix)
        {
            uint goalId = data.GetUInt(prefix + "GoalId", 0u);
            QuantumTaccRoute route = (QuantumTaccRoute)data.GetInt(prefix + "Route", 0);
            int sourceEntityId = data.GetInt(prefix + "SourceEntityId", 0);
            int destinationEntityId = data.GetInt(prefix + "DestinationEntityId", 0);
            int sourcePos = data.GetInt(prefix + "SourcePos", -1);
            int destinationPos = data.GetInt(prefix + "DestinationPos", -1);
            if (goalId == 0u || sourceEntityId == 0 || destinationEntityId == 0 ||
                (route != QuantumTaccRoute.ToWashingMachine &&
                    route != QuantumTaccRoute.ToQuantumWardrobe) ||
                !EntityUtils.IsWithinBounds(sourcePos) || !EntityUtils.IsWithinBounds(destinationPos))
            {
                return null;
            }
            return new QuantumTaccReachabilityProof
            {
                GoalId = goalId,
                Route = route,
                SourceEntityId = sourceEntityId,
                DestinationEntityId = destinationEntityId,
                SourcePos = sourcePos,
                DestinationPos = destinationPos,
                SourcePosition = data.GetVector2(prefix + "SourcePosition", EntityUtils.CenterOf(sourcePos)),
                DestinationPosition = data.GetVector2(
                    prefix + "DestinationPosition",
                    EntityUtils.CenterOf(destinationPos))
            };
        }
    }

    internal sealed class QuantumTaccNanobotCandidate
    {
        public QuantumTaccRoute Route;
        public AIGoal Goal;
        public ObjData Data;
        public IComponent Machine;
        public ObjStorageComp SourceStorage;
        public ObjStorageComp Destination;
        public bool SourceWasStored;
        public Vector2 SourcePosition;
        public Vector2 DestinationPosition;
        public float Score;
    }

    internal sealed class QuantumTaccNanobotHaulOp
    {
        public QuantumTaccRoute Route;
        public uint GoalId;
        public AIGoal Goal;
        public int HiveEntityId;
        public uint ObjDataId;
        public ObjData Data;
        public int MachineEntityId;
        public IComponent Machine;
        public int SourceStorageEntityId;
        public ObjStorageComp SourceStorage;
        public int DestinationEntityId;
        public ObjStorageComp Destination;
        public bool SourceWasStored;
        public bool OwnsReservation;
        public bool OwnsCarrier;
        public Vector2 SourcePosition;
        public Vector2 From;
        public Vector2 To;
        public float DurationTicks;
        public float Progress;

        public void Save(ComponentData data, string prefix)
        {
            data.SetInt(prefix + "Route", (int)Route);
            data.SetUInt(prefix + "GoalId", GoalId);
            data.SetInt(prefix + "HiveEntityId", HiveEntityId);
            data.SetUInt(prefix + "ObjDataId", ObjDataId);
            data.SetInt(prefix + "MachineEntityId", MachineEntityId);
            data.SetInt(prefix + "SourceStorageEntityId", SourceStorageEntityId);
            data.SetInt(prefix + "DestinationEntityId", DestinationEntityId);
            data.SetBool(prefix + "SourceWasStored", SourceWasStored);
            data.SetBool(prefix + "OwnsReservation", OwnsReservation);
            data.SetVector2(prefix + "SourcePosition", SourcePosition);
            data.SetVector2(prefix + "From", From);
            data.SetVector2(prefix + "To", To);
            data.SetFloat(prefix + "DurationTicks", DurationTicks);
            data.SetFloat(prefix + "Progress", Progress);
        }

        public static QuantumTaccNanobotHaulOp Load(ComponentData data, string prefix)
        {
            uint goalId = data.GetUInt(prefix + "GoalId", 0u);
            uint objDataId = data.GetUInt(prefix + "ObjDataId", 0u);
            QuantumTaccRoute route = (QuantumTaccRoute)data.GetInt(prefix + "Route", 0);
            if (goalId == 0u || objDataId == 0u ||
                (route != QuantumTaccRoute.ToWashingMachine && route != QuantumTaccRoute.ToQuantumWardrobe))
            {
                return null;
            }

            return new QuantumTaccNanobotHaulOp
            {
                Route = route,
                GoalId = goalId,
                HiveEntityId = data.GetInt(prefix + "HiveEntityId", 0),
                ObjDataId = objDataId,
                MachineEntityId = data.GetInt(prefix + "MachineEntityId", 0),
                SourceStorageEntityId = data.GetInt(prefix + "SourceStorageEntityId", 0),
                DestinationEntityId = data.GetInt(prefix + "DestinationEntityId", 0),
                SourceWasStored = data.GetBool(prefix + "SourceWasStored", false),
                OwnsReservation = data.GetBool(prefix + "OwnsReservation", false),
                SourcePosition = data.GetVector2(prefix + "SourcePosition"),
                From = data.GetVector2(prefix + "From"),
                To = data.GetVector2(prefix + "To"),
                DurationTicks = data.GetFloat(prefix + "DurationTicks", 1f),
                Progress = data.GetFloat(prefix + "Progress", 0f)
            };
        }
    }
}
