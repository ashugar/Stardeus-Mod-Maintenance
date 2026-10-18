using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Game.Systems.AI;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Diagnostic-only timing breakdown for Core's AISys.OnTick pipeline.
    /// Every target keeps Core's original implementation and return value.
    /// The remaining uninstrumented time is reported as residual rather than
    /// being assigned to a phase by assumption.
    /// </summary>
    internal static class AiPhasePerformanceDiagnostics {
        internal const int ExpectedHookCount = 10;

        internal enum Phase {
            AuctionPrepare,
            AgentPreparation,
            ColonyGoalProcessing,
            AuctionRun,
            WinnerAssignment,
            UnreachableResolve,
            AgentExecution,
            PendingGoalCleanup,
            UnreachableReconcile
        }

        private sealed class PhaseMetric {
            internal long Calls;
            internal long TotalTimestampTicks;
            internal long MaxTimestampTicks;
            internal long AtLeast4Ms;
            internal long AtLeast8Ms;
            internal long AtLeast16Ms;
            internal long AtLeast33Ms;

            internal void Record(long elapsed) {
                Calls++;
                TotalTimestampTicks += elapsed;
                if (elapsed > MaxTimestampTicks) {
                    MaxTimestampTicks = elapsed;
                }
                double ms = ToMilliseconds(elapsed);
                if (ms >= 4d) { AtLeast4Ms++; }
                if (ms >= 8d) { AtLeast8Ms++; }
                if (ms >= 16d) { AtLeast16Ms++; }
                if (ms >= 33d) { AtLeast33Ms++; }
            }

            internal void Reset() {
                Calls = 0;
                TotalTimestampTicks = 0;
                MaxTimestampTicks = 0;
                AtLeast4Ms = 0;
                AtLeast8Ms = 0;
                AtLeast16Ms = 0;
                AtLeast33Ms = 0;
            }
        }

        internal struct TimingState {
            internal bool Active;
            internal bool CountsTowardTick;
            internal Phase Phase;
            internal long StartedAt;
        }

        private static readonly Dictionary<MethodBase, Phase> PhaseByMethod =
            new Dictionary<MethodBase, Phase>();
        private static readonly Dictionary<Phase, PhaseMetric> Metrics =
            Enum.GetValues(typeof(Phase)).Cast<Phase>()
                .ToDictionary(phase => phase, phase => new PhaseMetric());

        private static bool tickActive;
        private static int phaseDepth;
        private static long currentKnownTimestampTicks;
        private static long aiTicks;
        private static long fullTimestampTicks;
        private static long fullMaxTimestampTicks;
        private static long residualTimestampTicks;
        private static long residualMaxTimestampTicks;
        private static long fullAtLeast16Ms;
        private static long fullAtLeast33Ms;
        private static long residualAtLeast4Ms;
        private static long residualAtLeast8Ms;
        private static long residualAtLeast16Ms;
        private static long residualAtLeast33Ms;
        private static long residualClampCount;
        private static bool runtimeFailureLogged;

        internal static bool IsFailOpen { get; private set; }

        internal static int Install(Harmony harmony) {
            int installed = 0;
            MethodInfo phasePrefix = AccessTools.DeclaredMethod(
                typeof(AiPhaseMethodPatch), nameof(AiPhaseMethodPatch.Prefix));
            MethodInfo phaseFinalizer = AccessTools.DeclaredMethod(
                typeof(AiPhaseMethodPatch),
                nameof(AiPhaseMethodPatch.Finalizer));
            MethodInfo tickPrefix = AccessTools.DeclaredMethod(
                typeof(AiPhaseTickPatch), nameof(AiPhaseTickPatch.Prefix));
            MethodInfo tickFinalizer = AccessTools.DeclaredMethod(
                typeof(AiPhaseTickPatch), nameof(AiPhaseTickPatch.Finalizer));

            installed += Patch(harmony,
                AccessTools.DeclaredMethod(typeof(AISys), "OnTick",
                    new[] { typeof(long) }),
                tickPrefix, tickFinalizer, null, "AISys.OnTick");
            installed += PatchPhase(harmony,
                AccessTools.DeclaredMethod(typeof(AIAuction), "Prepare",
                    Type.EmptyTypes),
                Phase.AuctionPrepare, phasePrefix, phaseFinalizer,
                "AIAuction.Prepare");
            installed += PatchPhase(harmony,
                AccessTools.DeclaredMethod(typeof(AISys), "PrepareAgent",
                    new[] {
                        typeof(AIAgentComp), typeof(int), typeof(long),
                        typeof(int), typeof(int)
                    }),
                Phase.AgentPreparation, phasePrefix, phaseFinalizer,
                "AISys.PrepareAgent");
            installed += PatchPhase(harmony,
                AccessTools.DeclaredMethod(typeof(AISys),
                    "ProcessColonyGoal",
                    new[] {
                        typeof(AIGoal), typeof(long), typeof(bool)
                    }),
                Phase.ColonyGoalProcessing, phasePrefix, phaseFinalizer,
                "AISys.ProcessColonyGoal");
            installed += PatchPhase(harmony,
                AccessTools.DeclaredMethod(typeof(AIAuction), "Run",
                    new[] { typeof(Game.Data.GameState), typeof(long), typeof(AIAuctionIndex) }),
                Phase.AuctionRun, phasePrefix, phaseFinalizer,
                "AIAuction.Run");
            installed += PatchPhase(harmony,
                AccessTools.DeclaredMethod(typeof(AIBid), nameof(AIBid.Assign),
                    new[] { typeof(long) }),
                Phase.WinnerAssignment, phasePrefix, phaseFinalizer,
                "AIBid.Assign");
            installed += PatchUnreachable(harmony, "Resolve", 4,
                Phase.UnreachableResolve, phasePrefix, phaseFinalizer);
            installed += PatchPhase(harmony,
                AccessTools.DeclaredMethod(typeof(AISys), "RunAgent",
                    new[] { typeof(AIAgentComp), typeof(long) }),
                Phase.AgentExecution, phasePrefix, phaseFinalizer,
                "AISys.RunAgent");
            installed += PatchPhase(harmony,
                AccessTools.DeclaredMethod(typeof(AISys),
                    "RemovePendingGoals", Type.EmptyTypes),
                Phase.PendingGoalCleanup, phasePrefix, phaseFinalizer,
                "AISys.RemovePendingGoals");
            installed += PatchUnreachable(harmony, "Reconcile", 1,
                Phase.UnreachableReconcile, phasePrefix, phaseFinalizer);
            return installed;
        }

        private static int PatchUnreachable(Harmony harmony, string name,
                int parameterCount, Phase phase, MethodInfo prefix,
                MethodInfo finalizer) {
            FieldInfo field = AccessTools.DeclaredField(
                typeof(AISys), "unreachable");
            MethodInfo method = field == null
                ? null
                : FindUniqueMethod(field.FieldType, name, parameterCount);
            return PatchPhase(harmony, method, phase, prefix, finalizer,
                "UnreachableTasks." + name);
        }

        private static MethodInfo FindUniqueMethod(Type type, string name,
                int parameterCount) {
            if (type == null) { return null; }
            MethodInfo[] matches = type.GetMethods(BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic)
                .Where(method => method.Name == name
                    && method.GetParameters().Length == parameterCount)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static int PatchPhase(Harmony harmony, MethodInfo original,
                Phase phase, MethodInfo prefix, MethodInfo finalizer,
                string targetName) {
            int patched = Patch(harmony, original, prefix, finalizer, phase,
                targetName);
            if (patched == 1) {
                PhaseByMethod[original] = phase;
            }
            return patched;
        }

        private static int Patch(Harmony harmony, MethodInfo original,
                MethodInfo prefix, MethodInfo finalizer, Phase? phase,
                string targetName) {
            try {
                if (harmony == null || original == null || prefix == null
                        || finalizer == null) {
                    TryLogError("Optimization Mod AI phase diagnostic skipped "
                        + "unavailable target " + targetName
                        + "; Core AI behavior remains unchanged.");
                    return 0;
                }
                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix) {
                        priority = Priority.Low
                    },
                    finalizer: new HarmonyMethod(finalizer) {
                        priority = Priority.Low
                    });
                return 1;
            } catch (Exception ex) {
                TryLogError("Optimization Mod AI phase diagnostic failed to "
                    + "patch " + targetName + "; Core AI behavior remains "
                    + "unchanged. " + ex);
                return 0;
            }
        }

        internal static long BeginTick() {
            if (IsFailOpen) { return 0L; }
            try {
                tickActive = true;
                phaseDepth = 0;
                currentKnownTimestampTicks = 0L;
                return Stopwatch.GetTimestamp();
            } catch (Exception ex) {
                FailOpen(ex);
                return 0L;
            }
        }

        internal static void EndTick(long startedAt) {
            if (IsFailOpen || !tickActive || startedAt == 0L) { return; }
            try {
                long elapsed = Math.Max(0L,
                    Stopwatch.GetTimestamp() - startedAt);
                long residual = elapsed - currentKnownTimestampTicks;
                if (residual < 0L) {
                    residual = 0L;
                    residualClampCount++;
                }
                aiTicks++;
                fullTimestampTicks += elapsed;
                residualTimestampTicks += residual;
                if (elapsed > fullMaxTimestampTicks) {
                    fullMaxTimestampTicks = elapsed;
                }
                if (residual > residualMaxTimestampTicks) {
                    residualMaxTimestampTicks = residual;
                }
                double fullMs = ToMilliseconds(elapsed);
                double residualMs = ToMilliseconds(residual);
                if (fullMs >= 16d) { fullAtLeast16Ms++; }
                if (fullMs >= 33d) { fullAtLeast33Ms++; }
                if (residualMs >= 4d) { residualAtLeast4Ms++; }
                if (residualMs >= 8d) { residualAtLeast8Ms++; }
                if (residualMs >= 16d) { residualAtLeast16Ms++; }
                if (residualMs >= 33d) { residualAtLeast33Ms++; }
                tickActive = false;
                phaseDepth = 0;
                currentKnownTimestampTicks = 0L;
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        internal static TimingState BeginPhase(MethodBase method) {
            var state = new TimingState();
            if (IsFailOpen || !tickActive || method == null) {
                return state;
            }
            try {
                if (!PhaseByMethod.TryGetValue(method, out Phase phase)) {
                    return state;
                }
                // AIBid.Assign is also used inside agent preparation and
                // execution. Only a top-level call is an auction winner
                // assignment; nested calls are already included by their
                // containing phase.
                if (phase == Phase.WinnerAssignment && phaseDepth > 0) {
                    return state;
                }
                state.Active = true;
                state.CountsTowardTick = phaseDepth == 0;
                state.Phase = phase;
                state.StartedAt = Stopwatch.GetTimestamp();
                phaseDepth++;
            } catch (Exception ex) {
                FailOpen(ex);
            }
            return state;
        }

        internal static void EndPhase(TimingState state) {
            if (!state.Active || IsFailOpen) { return; }
            try {
                long elapsed = Math.Max(0L,
                    Stopwatch.GetTimestamp() - state.StartedAt);
                if (phaseDepth > 0) { phaseDepth--; }
                Metrics[state.Phase].Record(elapsed);
                if (state.CountsTowardTick) {
                    currentKnownTimestampTicks += elapsed;
                }
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        internal static void LogSummary() {
            try {
                double fullMs = ToMilliseconds(fullTimestampTicks);
                double residualMs = ToMilliseconds(residualTimestampTicks);
                double knownMs = Math.Max(0d, fullMs - residualMs);
                double knownShare = fullMs > 0d
                    ? knownMs * 100d / fullMs
                    : 0d;
                D.Warn($"Optimization Mod AI phase diagnostic summary: AI "
                    + $"ticks={aiTicks}, full total/avg/max={fullMs:0.000}/"
                    + $"{AveragePerTick(fullMs):0.000}/"
                    + $"{ToMilliseconds(fullMaxTimestampTicks):0.000} ms, "
                    + $">=16/33 ms={fullAtLeast16Ms}/{fullAtLeast33Ms}; "
                    + $"instrumented phases={knownMs:0.000} ms "
                    + $"({knownShare:0.0}% of full), residual total/avg/max="
                    + $"{residualMs:0.000}/{AveragePerTick(residualMs):0.000}/"
                    + $"{ToMilliseconds(residualMaxTimestampTicks):0.000} ms, "
                    + $"residual >=4/8/16/33 ms={residualAtLeast4Ms}/"
                    + $"{residualAtLeast8Ms}/{residualAtLeast16Ms}/"
                    + $"{residualAtLeast33Ms}, residual clamps="
                    + $"{residualClampCount}, fail-open={IsFailOpen}.");

                LogPhase(Phase.AuctionPrepare, "Auction prepare");
                LogPhase(Phase.AgentPreparation,
                    "Agent preparation and personal goals");
                LogPhase(Phase.ColonyGoalProcessing,
                    "Colony-goal processing");
                LogPhase(Phase.AuctionRun, "Auction run");
                LogPhase(Phase.WinnerAssignment,
                    "Winner assignment (top-level AIBid.Assign only)");
                LogPhase(Phase.UnreachableResolve,
                    "Unreachable-task resolve");
                LogPhase(Phase.AgentExecution, "Agent execution");
                LogPhase(Phase.PendingGoalCleanup,
                    "Pending-goal cleanup");
                LogPhase(Phase.UnreachableReconcile,
                    "Unreachable-task reconcile");
                D.Warn("Optimization Mod AI phase residual includes Core's "
                    + "Nanobot job-type setup, colony-goal priority bucketing, "
                    + "per-loop overhead, winner gate rechecks, UI bookkeeping, "
                    + "exception guards, and Harmony work outside the timed "
                    + "private phase boundaries.");
            } catch (Exception ex) {
                TryLogError("Optimization Mod AI phase diagnostic summary "
                    + "failed: " + ex);
            }
        }

        private static void LogPhase(Phase phase, string label) {
            PhaseMetric metric = Metrics[phase];
            double totalMs = ToMilliseconds(metric.TotalTimestampTicks);
            double averageCallMs = metric.Calls > 0
                ? totalMs / metric.Calls
                : 0d;
            double callsPerTick = aiTicks > 0
                ? metric.Calls / (double) aiTicks
                : 0d;
            D.Warn($"Optimization Mod AI phase {label}: total="
                + $"{totalMs:0.000} ms, calls={metric.Calls} "
                + $"({callsPerTick:0.00}/AI tick), avg call/tick="
                + $"{averageCallMs:0.000}/{AveragePerTick(totalMs):0.000} ms, "
                + $"max call={ToMilliseconds(metric.MaxTimestampTicks):0.000} "
                + $"ms, calls >=4/8/16/33 ms={metric.AtLeast4Ms}/"
                + $"{metric.AtLeast8Ms}/{metric.AtLeast16Ms}/"
                + $"{metric.AtLeast33Ms}.");
        }

        internal static void Reset() {
            PhaseByMethod.Clear();
            foreach (PhaseMetric metric in Metrics.Values) {
                metric.Reset();
            }
            tickActive = false;
            phaseDepth = 0;
            currentKnownTimestampTicks = 0L;
            aiTicks = 0L;
            fullTimestampTicks = 0L;
            fullMaxTimestampTicks = 0L;
            residualTimestampTicks = 0L;
            residualMaxTimestampTicks = 0L;
            fullAtLeast16Ms = 0L;
            fullAtLeast33Ms = 0L;
            residualAtLeast4Ms = 0L;
            residualAtLeast8Ms = 0L;
            residualAtLeast16Ms = 0L;
            residualAtLeast33Ms = 0L;
            residualClampCount = 0L;
            runtimeFailureLogged = false;
            IsFailOpen = false;
        }

        private static double AveragePerTick(double totalMs) {
            return aiTicks > 0 ? totalMs / aiTicks : 0d;
        }

        private static double ToMilliseconds(long timestampTicks) {
            return timestampTicks * 1000d / Stopwatch.Frequency;
        }

        private static void FailOpen(Exception ex) {
            IsFailOpen = true;
            tickActive = false;
            phaseDepth = 0;
            currentKnownTimestampTicks = 0L;
            if (runtimeFailureLogged) { return; }
            runtimeFailureLogged = true;
            TryLogError("Optimization Mod AI phase diagnostics failed open; "
                + "Core AI behavior remains unchanged. " + ex);
        }

        private static void TryLogError(string message) {
            try {
                D.Err(message);
            } catch {
                // Unity logging is unavailable in headless static verifiers.
            }
        }
    }

    internal static class AiPhaseTickPatch {
        internal static void Prefix(out long __state) {
            __state = AiPhasePerformanceDiagnostics.BeginTick();
        }

        internal static Exception Finalizer(Exception __exception,
                long __state) {
            AiPhasePerformanceDiagnostics.EndTick(__state);
            return __exception;
        }
    }

    internal static class AiPhaseMethodPatch {
        internal static void Prefix(MethodBase __originalMethod,
                out AiPhasePerformanceDiagnostics.TimingState __state) {
            __state = AiPhasePerformanceDiagnostics.BeginPhase(
                __originalMethod);
        }

        internal static Exception Finalizer(Exception __exception,
                AiPhasePerformanceDiagnostics.TimingState __state) {
            AiPhasePerformanceDiagnostics.EndPhase(__state);
            return __exception;
        }
    }
}
