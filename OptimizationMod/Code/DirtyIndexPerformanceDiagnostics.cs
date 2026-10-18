using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Constants;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Temporary aggregate-only instrumentation for the session-only Nanobot
    /// dirty index. Timers sit around whole index phases rather than inside
    /// goal loops, so diagnostic overhead does not scale per inspected goal.
    /// </summary>
    internal static class DirtyIndexPerformanceDiagnostics {
        internal const int CandidatePreparation = 0;
        internal const int StructuralReconcile = 1;
        internal const int MaterialWakeScan = 2;
        internal const int FallbackProbeScan = 3;
        internal const int CandidateViewRebuild = 4;
        internal const int ResultProcessing = 5;

        internal const int ReconcileAIChanged = 1;
        internal const int ReconcileStructureDirty = 2;
        internal const int ReconcileCountMismatch = 4;
        internal const int ReconcilePeriodic = 8;

        private const int MaxRetainedReconciles = 8;
        private static readonly string[] PhaseNames = {
            "CandidatePreparation",
            "StructuralReconcile",
            "MaterialWakeScan",
            "FallbackProbeScan",
            "CandidateViewRebuild",
            "ResultProcessing"
        };
        private static readonly PhaseStats[] Phases = {
            new PhaseStats(), new PhaseStats(), new PhaseStats(),
            new PhaseStats(), new PhaseStats(), new PhaseStats()
        };
        private static readonly List<ReconcileRecord> WorstReconciles =
            new List<ReconcileRecord>(MaxRetainedReconciles);

        private static long materialScanCalls;
        private static long materialInspected;
        private static long materialSelected;
        private static int maxMaterialInspected;
        private static int maxMaterialSelected;
        private static long fallbackScanCalls;
        private static long fallbackInspected;
        private static long fallbackSelected;
        private static int maxFallbackInspected;
        private static int maxFallbackSelected;
        private static long viewRebuilds;
        private static long viewEntriesInspected;
        private static long viewEntriesReturned;
        private static int maxViewEntriesInspected;
        private static int maxViewEntriesReturned;

        internal static bool Enabled { get; private set; }

        internal static void Configure(bool enabled) {
            Enabled = enabled;
        }

        internal static long StartTiming() {
            return Enabled ? Stopwatch.GetTimestamp() : 0L;
        }

        internal static long ElapsedSince(long startedAt) {
            return Enabled ? Stopwatch.GetTimestamp() - startedAt : 0L;
        }

        internal static void Record(int phase, long elapsed) {
            if (!Enabled || phase < 0 || phase >= Phases.Length
                    || elapsed < 0) {
                return;
            }
            int speed = (int) G.ClockSpeed;
            if (speed < 0 || speed >= 5) { speed = 0; }
            Phases[phase].Record(speed, elapsed);
        }

        internal static void RecordReconcile(
                long elapsed,
                long indexTick,
                int reasons,
                int goalCount,
                int candidateCount) {
            if (!Enabled) { return; }
            Record(StructuralReconcile, elapsed);
            var record = new ReconcileRecord {
                Elapsed = elapsed,
                IndexTick = indexTick,
                Reasons = reasons,
                GoalCount = goalCount,
                CandidateCount = candidateCount
            };
            int insertAt = 0;
            while (insertAt < WorstReconciles.Count
                    && WorstReconciles[insertAt].Elapsed >= elapsed) {
                insertAt++;
            }
            if (insertAt >= MaxRetainedReconciles) { return; }
            WorstReconciles.Insert(insertAt, record);
            if (WorstReconciles.Count > MaxRetainedReconciles) {
                WorstReconciles.RemoveAt(WorstReconciles.Count - 1);
            }
        }

        internal static void RecordMaterialScan(int inspected, int selected) {
            if (!Enabled) { return; }
            materialScanCalls++;
            materialInspected += inspected;
            materialSelected += selected;
            if (inspected > maxMaterialInspected) {
                maxMaterialInspected = inspected;
            }
            if (selected > maxMaterialSelected) {
                maxMaterialSelected = selected;
            }
        }

        internal static void RecordFallbackScan(int inspected, int selected) {
            if (!Enabled) { return; }
            fallbackScanCalls++;
            fallbackInspected += inspected;
            fallbackSelected += selected;
            if (inspected > maxFallbackInspected) {
                maxFallbackInspected = inspected;
            }
            if (selected > maxFallbackSelected) {
                maxFallbackSelected = selected;
            }
        }

        internal static void RecordViewRebuild(int inspected, int returned) {
            if (!Enabled) { return; }
            viewRebuilds++;
            viewEntriesInspected += inspected;
            viewEntriesReturned += returned;
            if (inspected > maxViewEntriesInspected) {
                maxViewEntriesInspected = inspected;
            }
            if (returned > maxViewEntriesReturned) {
                maxViewEntriesReturned = returned;
            }
        }

        internal static void LogSummary() {
            if (!Enabled) { return; }
            for (int i = 0; i < Phases.Length; i++) {
                Phases[i].Log(PhaseNames[i]);
            }
            if (materialScanCalls > 0) {
                D.Warn("Optimization Mod dirty-index material scan work: "
                    + "{0} calls, {1:0.0} goals inspected avg/{2} max, "
                    + "{3:0.0} selected avg/{4} max.",
                    materialScanCalls,
                    materialInspected / (double) materialScanCalls,
                    maxMaterialInspected,
                    materialSelected / (double) materialScanCalls,
                    maxMaterialSelected);
            }
            if (fallbackScanCalls > 0) {
                D.Warn("Optimization Mod dirty-index fallback scan work: "
                    + "{0} calls, {1:0.0} goals inspected avg/{2} max, "
                    + "{3:0.0} selected avg/{4} max.",
                    fallbackScanCalls,
                    fallbackInspected / (double) fallbackScanCalls,
                    maxFallbackInspected,
                    fallbackSelected / (double) fallbackScanCalls,
                    maxFallbackSelected);
            }
            if (viewRebuilds > 0) {
                D.Warn("Optimization Mod dirty-index candidate-view rebuild "
                    + "work: {0} rebuilds, {1:0.0} entries inspected avg/{2} "
                    + "max, {3:0.0} candidates returned avg/{4} max.",
                    viewRebuilds,
                    viewEntriesInspected / (double) viewRebuilds,
                    maxViewEntriesInspected,
                    viewEntriesReturned / (double) viewRebuilds,
                    maxViewEntriesReturned);
            }
            for (int i = 0; i < WorstReconciles.Count; i++) {
                ReconcileRecord r = WorstReconciles[i];
                D.Warn("Optimization Mod dirty-index reconcile #{0}: index "
                    + "tick={1}, {2:0.###} ms, reasons={3}, goals={4}, "
                    + "candidates={5}.",
                    i + 1, r.IndexTick, ToMilliseconds(r.Elapsed),
                    ReconcileReasons(r.Reasons), r.GoalCount,
                    r.CandidateCount);
            }
        }

        internal static void Reset() {
            Enabled = false;
            for (int i = 0; i < Phases.Length; i++) {
                Phases[i].Reset();
            }
            WorstReconciles.Clear();
            materialScanCalls = 0;
            materialInspected = 0;
            materialSelected = 0;
            maxMaterialInspected = 0;
            maxMaterialSelected = 0;
            fallbackScanCalls = 0;
            fallbackInspected = 0;
            fallbackSelected = 0;
            maxFallbackInspected = 0;
            maxFallbackSelected = 0;
            viewRebuilds = 0;
            viewEntriesInspected = 0;
            viewEntriesReturned = 0;
            maxViewEntriesInspected = 0;
            maxViewEntriesReturned = 0;
        }

        private static string ReconcileReasons(int reasons) {
            var parts = new List<string>(4);
            if ((reasons & ReconcileAIChanged) != 0) {
                parts.Add("ai-changed");
            }
            if ((reasons & ReconcileStructureDirty) != 0) {
                parts.Add("structure-dirty");
            }
            if ((reasons & ReconcileCountMismatch) != 0) {
                parts.Add("count-mismatch");
            }
            if ((reasons & ReconcilePeriodic) != 0) {
                parts.Add("periodic");
            }
            return parts.Count == 0 ? "unknown" : string.Join("+", parts);
        }

        private sealed class PhaseStats {
            private readonly TimingStats[] bySpeed = {
                new TimingStats(), new TimingStats(), new TimingStats(),
                new TimingStats(), new TimingStats()
            };

            internal void Record(int speed, long elapsed) {
                TimingStats stats = bySpeed[speed];
                stats.Calls++;
                stats.Total += elapsed;
                if (elapsed > stats.Max) { stats.Max = elapsed; }
            }

            internal void Log(string name) {
                long calls = 0;
                long total = 0;
                long max = 0;
                var speedParts = new List<string>();
                for (int i = 0; i < bySpeed.Length; i++) {
                    TimingStats stats = bySpeed[i];
                    if (stats.Calls == 0) { continue; }
                    calls += stats.Calls;
                    total += stats.Total;
                    if (stats.Max > max) { max = stats.Max; }
                    double ms = ToMilliseconds(stats.Total);
                    speedParts.Add($"{(ClockSpeed) i}: {stats.Calls} calls, "
                        + $"{ms / stats.Calls:0.###} ms avg, "
                        + $"{ToMilliseconds(stats.Max):0.###} ms max");
                }
                if (calls == 0) { return; }
                double totalMs = ToMilliseconds(total);
                D.Warn($"Optimization Mod dirty-index phase {name}: {calls} "
                    + $"calls, {totalMs:0.###} ms total, "
                    + $"{totalMs / calls:0.###} ms avg, "
                    + $"{ToMilliseconds(max):0.###} ms max; "
                    + string.Join(" | ", speedParts));
            }

            internal void Reset() {
                for (int i = 0; i < bySpeed.Length; i++) {
                    bySpeed[i].Reset();
                }
            }
        }

        private sealed class TimingStats {
            internal long Calls;
            internal long Total;
            internal long Max;

            internal void Reset() {
                Calls = 0;
                Total = 0;
                Max = 0;
            }
        }

        private sealed class ReconcileRecord {
            internal long Elapsed;
            internal long IndexTick;
            internal int Reasons;
            internal int GoalCount;
            internal int CandidateCount;
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000d / Stopwatch.Frequency;
        }
    }
}
