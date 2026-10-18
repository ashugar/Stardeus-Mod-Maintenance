using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Nanobots;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Supplies NanobotSys with a session-only working set instead of making
    /// Core reevaluate every compatible colony goal every tick. Core still
    /// performs every availability, state, hauling, reachability, and work
    /// decision for each returned goal.
    /// </summary>
    internal static class NanobotDirtyGoalIndex {
        private const int FallbackGoalsPerTick = 256;
        private const int MaterialWakeGoalsPerTick = 64;
        private const int StructuralReconcileTicks = 60;

        private sealed class GoalRecord {
            internal AIGoal Goal;
            internal bool Candidate;
            internal bool FallbackProbe;
            internal int MaterialId;
        }

        private static readonly Dictionary<uint, GoalRecord> Records = new();
        private static readonly List<uint> OrderedIds = new();
        private static readonly Dictionary<uint, AIGoal> CandidateView = new();
        private static readonly HashSet<int> PendingMaterialTypes = new();
        private static readonly List<uint> RemovedIds = new();
        private static readonly FieldInfo WorkingHivesField = AccessTools.Field(
            typeof(NanobotSys), "workingHives");

        private static GameState state;
        private static AISys cachedAI;
        private static int cachedGoalCount = -1;
        private static int fallbackCursor;
        private static int materialWakeCursor;
        private static int ticksUntilStructuralReconcile;
        private static bool structureDirty = true;
        private static bool viewDirty = true;
        private static bool hasCompletedEvaluation;
        private static bool failOpen;
        private static string failOpenReason;
        private static bool loggedInitialBuild;

        private static long tickCount;
        private static long returnedCandidates;
        private static int maxReturnedCandidates;
        private static long fallbackProbes;
        private static long fallbackRecoveries;
        private static long explicitDirties;
        private static long materialSignals;
        private static long materialWakeCandidates;
        private static long globalChangeSignals;
        private static long structuralReconciles;
        private static long integrityReconciles;
        private static long incrementalAdds;
        private static long incrementalRemoves;

        internal static bool IsTranspilerActive { get; private set; }
        internal static string LastTranspilerMatch { get; private set; }
        internal static bool IsFailOpen => failOpen;

        internal static void Initialize(GameState gameState) {
            Reset();
            state = gameState;
            if (state?.Sig == null) {
                Disable("game signals were unavailable during initialization");
                return;
            }
            state.Sig.ConstructionCompleted.AddListener(
                OnConstructionCompleted);
            state.Sig.RecalculatePassability.AddListener(
                OnRecalculatePassability);
            state.Sig.AreasRecalculated.AddListener(OnAreasRecalculated);
            state.Sig.MatProduced.AddListener(OnMaterialProduced);
        }

        internal static void Reset() {
            DetachSignals();
            state = null;
            cachedAI = null;
            cachedGoalCount = -1;
            fallbackCursor = 0;
            materialWakeCursor = 0;
            ticksUntilStructuralReconcile = 0;
            structureDirty = true;
            viewDirty = true;
            hasCompletedEvaluation = false;
            failOpen = false;
            failOpenReason = null;
            loggedInitialBuild = false;
            IsTranspilerActive = false;
            LastTranspilerMatch = null;
            Records.Clear();
            OrderedIds.Clear();
            CandidateView.Clear();
            PendingMaterialTypes.Clear();
            RemovedIds.Clear();
            tickCount = 0;
            returnedCandidates = 0;
            maxReturnedCandidates = 0;
            fallbackProbes = 0;
            fallbackRecoveries = 0;
            explicitDirties = 0;
            materialSignals = 0;
            materialWakeCandidates = 0;
            globalChangeSignals = 0;
            structuralReconciles = 0;
            integrityReconciles = 0;
            incrementalAdds = 0;
            incrementalRemoves = 0;
        }

        private static void DetachSignals() {
            if (state?.Sig == null) { return; }
            state.Sig.ConstructionCompleted.RemoveListener(
                OnConstructionCompleted);
            state.Sig.RecalculatePassability.RemoveListener(
                OnRecalculatePassability);
            state.Sig.AreasRecalculated.RemoveListener(OnAreasRecalculated);
            state.Sig.MatProduced.RemoveListener(OnMaterialProduced);
        }

        internal static Dictionary<uint, AIGoal> GoalsForNanobots(AISys ai) {
            if (ai == null || failOpen) {
                return ai?.ColonyGoals ?? CandidateView;
            }
            long preparationStartedAt =
                DirtyIndexPerformanceDiagnostics.StartTiming();
            try {
                bool aiChanged = !ReferenceEquals(ai, cachedAI);
                bool explicitlyDirty = structureDirty;
                bool periodicReconcile = ticksUntilStructuralReconcile <= 0;
                bool integrityMismatch = cachedGoalCount != ai.ColonyGoals.Count;
                if (aiChanged || explicitlyDirty
                        || integrityMismatch || periodicReconcile) {
                    if (integrityMismatch && cachedGoalCount >= 0) {
                        integrityReconciles++;
                    }
                    int reasons = 0;
                    if (aiChanged) {
                        reasons |= DirtyIndexPerformanceDiagnostics
                            .ReconcileAIChanged;
                    }
                    if (explicitlyDirty) {
                        reasons |= DirtyIndexPerformanceDiagnostics
                            .ReconcileStructureDirty;
                    }
                    if (integrityMismatch) {
                        reasons |= DirtyIndexPerformanceDiagnostics
                            .ReconcileCountMismatch;
                    }
                    if (periodicReconcile) {
                        reasons |= DirtyIndexPerformanceDiagnostics
                            .ReconcilePeriodic;
                    }
                    long reconcileStartedAt =
                        DirtyIndexPerformanceDiagnostics.StartTiming();
                    ReconcileStructure(ai);
                    DirtyIndexPerformanceDiagnostics.RecordReconcile(
                        DirtyIndexPerformanceDiagnostics.ElapsedSince(
                            reconcileStartedAt),
                        tickCount + 1,
                        reasons,
                        Records.Count,
                        CandidateView.Count);
                }

                if (hasCompletedEvaluation) {
                    if (PendingMaterialTypes.Count > 0
                            && OrderedIds.Count > 0) {
                        long materialStartedAt =
                            DirtyIndexPerformanceDiagnostics.StartTiming();
                        AddMaterialWakeCandidates();
                        DirtyIndexPerformanceDiagnostics.Record(
                            DirtyIndexPerformanceDiagnostics.MaterialWakeScan,
                            DirtyIndexPerformanceDiagnostics.ElapsedSince(
                                materialStartedAt));
                    }
                    long fallbackStartedAt =
                        DirtyIndexPerformanceDiagnostics.StartTiming();
                    AddFallbackProbes();
                    DirtyIndexPerformanceDiagnostics.Record(
                        DirtyIndexPerformanceDiagnostics.FallbackProbeScan,
                        DirtyIndexPerformanceDiagnostics.ElapsedSince(
                            fallbackStartedAt));
                }
                if (viewDirty) {
                    int inspected = OrderedIds.Count;
                    long viewStartedAt =
                        DirtyIndexPerformanceDiagnostics.StartTiming();
                    RebuildCandidateView();
                    DirtyIndexPerformanceDiagnostics.Record(
                        DirtyIndexPerformanceDiagnostics.CandidateViewRebuild,
                        DirtyIndexPerformanceDiagnostics.ElapsedSince(
                            viewStartedAt));
                    DirtyIndexPerformanceDiagnostics.RecordViewRebuild(
                        inspected, CandidateView.Count);
                }
                ticksUntilStructuralReconcile--;
                tickCount++;
                returnedCandidates += CandidateView.Count;
                if (CandidateView.Count > maxReturnedCandidates) {
                    maxReturnedCandidates = CandidateView.Count;
                }
                return CandidateView;
            } catch (Exception ex) {
                Disable($"candidate preparation failed: {ex}");
                return ai.ColonyGoals;
            } finally {
                DirtyIndexPerformanceDiagnostics.Record(
                    DirtyIndexPerformanceDiagnostics.CandidatePreparation,
                    DirtyIndexPerformanceDiagnostics.ElapsedSince(
                        preparationStartedAt));
            }
        }

        internal static void AfterNanobotTick(NanobotSys nanobots) {
            if (failOpen || !IsTranspilerActive || nanobots == null) { return; }
            long processingStartedAt =
                DirtyIndexPerformanceDiagnostics.StartTiming();
            try {
                if (!HasWorkingHive(nanobots)) {
                    // Core cannot set NanobotWorkable without a working hive,
                    // so these flags do not describe candidate eligibility.
                    return;
                }
                foreach (KeyValuePair<uint, AIGoal> entry in CandidateView) {
                    if (!Records.TryGetValue(entry.Key, out GoalRecord record)
                            || !ReferenceEquals(record.Goal, entry.Value)) {
                        structureDirty = true;
                        continue;
                    }
                    bool wasProbe = record.FallbackProbe;
                    bool keep = record.Goal.IsNanobotCompatible
                        && record.Goal.IsNanobotWorkable;
                    record.Candidate = keep;
                    record.FallbackProbe = false;
                    if (!keep) {
                        RefreshMaterial(record);
                    }
                    if (wasProbe && keep) {
                        fallbackRecoveries++;
                    }
                }
                hasCompletedEvaluation = true;
                viewDirty = true;
            } catch (Exception ex) {
                Disable($"candidate result processing failed: {ex}");
            } finally {
                DirtyIndexPerformanceDiagnostics.Record(
                    DirtyIndexPerformanceDiagnostics.ResultProcessing,
                    DirtyIndexPerformanceDiagnostics.ElapsedSince(
                        processingStartedAt));
            }
        }

        private static bool HasWorkingHive(NanobotSys nanobots) {
            if (WorkingHivesField?.GetValue(nanobots)
                    is List<NanobotHiveComp> workingHives) {
                return workingHives.Count > 0;
            }
            Disable("Core Nanobot working-hive field was unavailable");
            return false;
        }

        private static void ReconcileStructure(AISys ai) {
            cachedAI = ai;
            structureDirty = false;
            structuralReconciles++;
            ticksUntilStructuralReconcile = StructuralReconcileTicks;
            RemovedIds.Clear();
            foreach (uint id in Records.Keys) {
                if (!ai.ColonyGoals.TryGetValue(id, out AIGoal live)
                        || !ReferenceEquals(Records[id].Goal, live)) {
                    RemovedIds.Add(id);
                }
            }
            for (int i = 0; i < RemovedIds.Count; i++) {
                RemoveRecord(RemovedIds[i]);
            }

            OrderedIds.Clear();
            foreach (KeyValuePair<uint, AIGoal> entry in ai.ColonyGoals) {
                OrderedIds.Add(entry.Key);
                if (!Records.TryGetValue(entry.Key, out GoalRecord record)) {
                    record = new GoalRecord {
                        Goal = entry.Value,
                        Candidate = entry.Value?.IsNanobotCompatible == true,
                    };
                    Records.Add(entry.Key, record);
                    RefreshMaterial(record);
                }
            }
            cachedGoalCount = ai.ColonyGoals.Count;
            if (fallbackCursor >= OrderedIds.Count) {
                fallbackCursor = 0;
            }
            if (materialWakeCursor >= OrderedIds.Count) {
                materialWakeCursor = 0;
            }
            viewDirty = true;

            if (!loggedInitialBuild
                    && OptimizationDiagnosticsSettings
                        .CoreProfilerSuiteEnabled) {
                loggedInitialBuild = true;
                int compatible = 0;
                for (int i = 0; i < OrderedIds.Count; i++) {
                    if (Records[OrderedIds[i]].Goal?.IsNanobotCompatible == true) {
                        compatible++;
                    }
                }
                D.Warn("Optimization Mod Nanobot dirty index active: {0} "
                    + "colony goals, {1} initially compatible. Core remains "
                    + "the final eligibility and work authority; {2} dormant "
                    + "goals are reconciled per simulation tick.",
                    Records.Count, compatible, FallbackGoalsPerTick);
            }
        }

        private static void AddFallbackProbes() {
            if (OrderedIds.Count == 0) { return; }
            int selected = 0;
            int inspected = 0;
            while (selected < FallbackGoalsPerTick
                    && inspected < OrderedIds.Count) {
                if (fallbackCursor >= OrderedIds.Count) {
                    fallbackCursor = 0;
                }
                uint id = OrderedIds[fallbackCursor++];
                inspected++;
                if (!Records.TryGetValue(id, out GoalRecord record)
                        || record.Candidate
                        || record.Goal?.IsNanobotCompatible != true) {
                    continue;
                }
                record.Candidate = true;
                record.FallbackProbe = true;
                selected++;
            }
            if (selected > 0) {
                fallbackProbes += selected;
                viewDirty = true;
            }
            DirtyIndexPerformanceDiagnostics.RecordFallbackScan(
                inspected, selected);
        }

        private static void AddMaterialWakeCandidates() {
            if (PendingMaterialTypes.Count == 0 || OrderedIds.Count == 0) {
                return;
            }
            int selected = 0;
            int inspected = 0;
            while (selected < MaterialWakeGoalsPerTick
                    && inspected < OrderedIds.Count) {
                if (materialWakeCursor >= OrderedIds.Count) {
                    materialWakeCursor = 0;
                }
                uint id = OrderedIds[materialWakeCursor++];
                inspected++;
                if (!Records.TryGetValue(id, out GoalRecord record)
                        || record.Candidate
                        || record.Goal?.IsNanobotCompatible != true
                        || record.MaterialId == 0
                        || !PendingMaterialTypes.Contains(record.MaterialId)) {
                    continue;
                }
                record.Candidate = true;
                record.FallbackProbe = false;
                selected++;
            }
            PendingMaterialTypes.Clear();
            if (selected > 0) {
                materialWakeCandidates += selected;
                viewDirty = true;
            }
            DirtyIndexPerformanceDiagnostics.RecordMaterialScan(
                inspected, selected);
        }

        private static void RebuildCandidateView() {
            if (!viewDirty) { return; }
            CandidateView.Clear();
            for (int i = 0; i < OrderedIds.Count; i++) {
                uint id = OrderedIds[i];
                if (Records.TryGetValue(id, out GoalRecord record)
                        && record.Candidate
                        && record.Goal?.IsNanobotCompatible == true) {
                    CandidateView[id] = record.Goal;
                }
            }
            viewDirty = false;
        }

        private static void RemoveRecord(uint id) {
            if (!Records.ContainsKey(id)) { return; }
            Records.Remove(id);
            CandidateView.Remove(id);
        }

        private static void RefreshMaterial(GoalRecord record) {
            record.MaterialId = MaterialIdFor(record.Goal);
        }

        private static int MaterialIdFor(AIGoal goal) {
            if (goal?.JobIdH != JobTypeIdH.Hauling) { return 0; }
            if (goal.Target is UnstoredMatComp pile) {
                return pile.Type?.IdH ?? 0;
            }
            if (goal.Target is IMatRequester requester) {
                return requester.NextMissingMat?.Type?.IdH ?? 0;
            }
            return 0;
        }

        internal static void MarkStructureDirty() {
            structureDirty = true;
        }

        internal static void GoalAdded(AISys ai, AIGoal goal) {
            if (failOpen || ai == null || goal == null) { return; }
            if (!ReferenceEquals(ai, cachedAI)) {
                structureDirty = true;
                return;
            }
            if (!ai.ColonyGoals.TryGetValue(goal.Id, out AIGoal live)
                    || !ReferenceEquals(live, goal)) {
                return;
            }
            if (Records.TryGetValue(goal.Id, out GoalRecord existing)) {
                if (!ReferenceEquals(existing.Goal, goal)) {
                    structureDirty = true;
                }
                cachedGoalCount = ai.ColonyGoals.Count;
                return;
            }
            var record = new GoalRecord {
                Goal = goal,
                Candidate = goal.IsNanobotCompatible,
            };
            Records.Add(goal.Id, record);
            OrderedIds.Add(goal.Id);
            RefreshMaterial(record);
            cachedGoalCount = ai.ColonyGoals.Count;
            incrementalAdds++;
            viewDirty = true;
        }

        internal static void GoalRemoved(AISys ai, AIGoal goal) {
            if (failOpen || ai == null || goal == null) { return; }
            if (!ReferenceEquals(ai, cachedAI)) {
                structureDirty = true;
                return;
            }
            if (ai.ColonyGoals.ContainsKey(goal.Id)) {
                return;
            }
            if (Records.Remove(goal.Id)) {
                CandidateView.Remove(goal.Id);
                OrderedIds.Remove(goal.Id);
                incrementalRemoves++;
                viewDirty = true;
            }
            cachedGoalCount = ai.ColonyGoals.Count;
            if (fallbackCursor >= OrderedIds.Count) {
                fallbackCursor = 0;
            }
            if (materialWakeCursor >= OrderedIds.Count) {
                materialWakeCursor = 0;
            }
        }

        internal static void MarkGoalDirty(AIGoal goal) {
            if (goal == null || failOpen) { return; }
            if (!Records.TryGetValue(goal.Id, out GoalRecord record)
                    || !ReferenceEquals(record.Goal, goal)) {
                return;
            }
            RefreshMaterial(record);
            if (goal.IsNanobotCompatible && !record.Candidate) {
                record.Candidate = true;
                record.FallbackProbe = false;
                explicitDirties++;
                viewDirty = true;
            }
        }

        internal static void MarkConstructionDirty(ConstructableComp comp) {
            MarkGoalDirty(comp?.Entity?.Goal);
        }

        internal static void MarkJobDirty(int jobIdH) {
            if (failOpen) { return; }
            foreach (GoalRecord record in Records.Values) {
                if (record.Goal?.JobIdH == jobIdH
                        && record.Goal.IsNanobotCompatible
                        && !record.Candidate) {
                    record.Candidate = true;
                    record.FallbackProbe = false;
                    explicitDirties++;
                    viewDirty = true;
                }
            }
        }

        internal static void NoteGlobalChange() {
            // Do not rewind fallbackCursor. Map changes can occur every tick on
            // a damaged ship; preserving the cursor guarantees that later
            // dormant goals cannot starve behind repeatedly changed early ones.
            globalChangeSignals++;
        }

        private static void OnConstructionCompleted(Tile tile) {
            MarkGoalDirty(tile?.Goal);
            NoteGlobalChange();
        }

        private static void OnRecalculatePassability(int posIdx) {
            NoteGlobalChange();
        }

        private static void OnAreasRecalculated() {
            NoteGlobalChange();
        }

        private static void OnMaterialProduced(MatType type, int amount) {
            if (type != null && type.IdH != 0 && !failOpen) {
                PendingMaterialTypes.Add(type.IdH);
                materialSignals++;
            }
        }

        private static void Disable(string reason) {
            if (failOpen) { return; }
            failOpen = true;
            failOpenReason = reason;
            D.Err("Optimization Mod disabled the Nanobot dirty index for this "
                + "session ({0}). Core's complete colony-goal view is active.",
                reason);
        }

        internal static void LogSummary() {
            double average = tickCount == 0
                ? 0d : returnedCandidates / (double)tickCount;
            string reason = failOpenReason == null
                ? "" : $" ({failOpenReason})";
            D.Warn($"Optimization Mod Nanobot dirty-index summary: "
                + $"ticks={tickCount}, goals={Records.Count}, candidates "
                + $"avg={average:F1} max={maxReturnedCandidates}, fallback "
                + $"probes={fallbackProbes}, fallback recoveries="
                + $"{fallbackRecoveries}, explicit dirties={explicitDirties}, "
                + $"material signals={materialSignals}, material wake "
                + $"candidates={materialWakeCandidates}, global change signals="
                + $"{globalChangeSignals}, incremental adds={incrementalAdds}, "
                + $"incremental removes={incrementalRemoves}, structural reconciles="
                + $"{structuralReconciles}, integrity reconciles="
                + $"{integrityReconciles}, fail-open={failOpen}{reason}.");
        }

        internal static IEnumerable<CodeInstruction> Transpiler(
                IEnumerable<CodeInstruction> instructions) {
            var original = instructions.Select(code =>
                new CodeInstruction(code)).ToList();
            var patched = original.Select(code =>
                new CodeInstruction(code)).ToList();
            MethodInfo colonyGoalsGetter = AccessTools.PropertyGetter(
                typeof(AISys), nameof(AISys.ColonyGoals));
            MethodInfo dirtyGoals = AccessTools.DeclaredMethod(
                typeof(NanobotDirtyGoalIndex), nameof(GoalsForNanobots));

            int getterIndex = -1;
            int getterMatches = 0;
            for (int i = 0; i < patched.Count; i++) {
                if (colonyGoalsGetter != null
                        && patched[i].Calls(colonyGoalsGetter)) {
                    getterIndex = i;
                    getterMatches++;
                }
            }
            LastTranspilerMatch = $"colonyGoalsGetter={getterIndex}, "
                + $"matches={getterMatches}";
            if (getterMatches != 1 || getterIndex < 0 || dirtyGoals == null) {
                IsTranspilerActive = false;
                D.Err("Optimization Mod Nanobot dirty index did not match Core "
                    + "OnTick ({0}); vanilla goal enumeration remains active.",
                    LastTranspilerMatch);
                return original;
            }

            CodeInstruction oldCall = patched[getterIndex];
            var replacement = new CodeInstruction(OpCodes.Call, dirtyGoals);
            replacement.labels.AddRange(oldCall.labels);
            replacement.blocks.AddRange(oldCall.blocks);
            patched[getterIndex] = replacement;
            bool firstActivation = !IsTranspilerActive;
            IsTranspilerActive = true;
            if (firstActivation
                    && OptimizationDiagnosticsSettings
                        .CoreProfilerSuiteEnabled) {
                D.Warn("Optimization Mod installed the Core Nanobot dirty-goal "
                    + "enumeration index.");
            }
            return patched;
        }
    }

    internal static class NanobotDirtyIndexTickPatch {
        internal static void Postfix(NanobotSys __instance) {
            NanobotDirtyGoalIndex.AfterNanobotTick(__instance);
        }
    }

    internal static class NanobotGoalStateChangedPatch {
        internal static void Postfix(AIGoal __instance) {
            NanobotDirtyGoalIndex.MarkGoalDirty(__instance);
        }
    }

    internal static class NanobotCompatibilityChangedPatch {
        internal static void Postfix(AIGoal __instance) {
            NanobotDirtyGoalIndex.MarkGoalDirty(__instance);
        }
    }

    internal static class NanobotConstructionGateChangedPatch {
        internal static void Postfix(ConstructableComp __instance) {
            NanobotDirtyGoalIndex.MarkConstructionDirty(__instance);
        }
    }

    internal static class NanobotHiveRegistrationChangedPatch {
        internal static void Postfix() {
            NanobotDirtyGoalIndex.NoteGlobalChange();
        }
    }

    internal static class NanobotJobTogglePatch {
        internal static void Postfix(int jobIdH) {
            NanobotDirtyGoalIndex.MarkJobDirty(jobIdH);
        }
    }

    internal static class NanobotGoalRemovedFromIndexPatch {
        internal static void Postfix(AISys __instance, AIGoal goal) {
            NanobotDirtyGoalIndex.GoalRemoved(__instance, goal);
        }
    }
}
