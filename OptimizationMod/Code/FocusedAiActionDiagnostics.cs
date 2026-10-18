using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.Components;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Creatures;
using Game.Systems.Items;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Focused observation-only follow-up for StoreObj, Reinstall, and the
    /// non-action work nested inside AISys.RunAgent.
    /// </summary>
    internal static class FocusedAiActionDiagnostics {
        internal const int ExpectedHookCount = 8;

        private const int StorePhaseCount = 5;
        private const int ReinstallPhaseCount = 7;
        private const int WrapperPhaseCount = 4;

        private static readonly Metric[] StorePhases = CreateMetrics(
            "busy movement", "reserve target", "reach or pick up",
            "find storage", "deliver");
        private static readonly Metric[] ReinstallPhases = CreateMetrics(
            "busy movement", "check reachability", "reach source",
            "pick up tile", "reach target", "put down tile", "unknown");
        private static readonly Metric[] WrapperPhases = CreateMetrics(
            "EnsureCurrentAction and activation", "work or idle counters",
            "ClearGoal and deactivation", "CheckDone");
        private static readonly Dictionary<SearchKey, SearchStats> Searches =
            new Dictionary<SearchKey, SearchStats>();

        [ThreadStatic] private static int runAgentDepth;
        private static long runAgentCalls;
        private static long runAgentTicks;
        private static long lastRunAgentTick;
        private static int installedHooks;
        private static bool failureLogged;

        internal static bool IsFailOpen { get; private set; }

        internal static int Install(Harmony harmony) {
            Reset();
            installedHooks += Patch(harmony, typeof(AISys), "RunAgent",
                new[] { typeof(AIAgentComp), typeof(long) },
                nameof(BeginRunAgent), nameof(EndRunAgent));
            installedHooks += Patch(harmony, typeof(AISys),
                nameof(AISys.EnsureCurrentAction),
                new[] { typeof(AIAgentComp), typeof(long), typeof(AIGoal) },
                nameof(BeginEnsureCurrentAction), nameof(EndNested));
            installedHooks += Patch(harmony, typeof(BeingUtils),
                nameof(BeingUtils.IncrementCounterFor),
                new[] { typeof(BeingData), typeof(AIAction) },
                nameof(BeginCounterUpdate), nameof(EndNested));
            installedHooks += Patch(harmony, typeof(AIAgentComp),
                nameof(AIAgentComp.ClearGoal), Type.EmptyTypes,
                nameof(BeginClearGoal), nameof(EndNested));
            installedHooks += Patch(harmony, typeof(AIGoal),
                nameof(AIGoal.CheckDone), new[] { typeof(long) },
                nameof(BeginCheckDone), nameof(EndNested));
            installedHooks += Patch(harmony, typeof(AIActionStoreObj),
                nameof(AIActionStoreObj.Execute),
                new[] {
                    typeof(GameState), typeof(AIAgentComp), typeof(AIGoal),
                    typeof(long)
                }, nameof(BeginStoreObj), nameof(EndStoreObj));
            installedHooks += Patch(harmony, typeof(AIActionStoreObj),
                "FindStorage",
                new[] {
                    typeof(GameState), typeof(AIAgentComp), typeof(ObjData),
                    typeof(int), typeof(AIGoal), typeof(long)
                }, nameof(BeginStorageSearch), nameof(EndStorageSearch));
            installedHooks += Patch(harmony, typeof(AIActionReinstall),
                nameof(AIActionReinstall.Execute),
                new[] {
                    typeof(GameState), typeof(AIAgentComp), typeof(AIGoal),
                    typeof(long)
                }, nameof(BeginReinstall), nameof(EndReinstall));
            return installedHooks;
        }

        private static int Patch(
                Harmony harmony,
                Type targetType,
                string targetName,
                Type[] parameterTypes,
                string prefixName,
                string finalizerName) {
            try {
                MethodInfo original = AccessTools.DeclaredMethod(
                    targetType, targetName, parameterTypes);
                MethodInfo prefix = AccessTools.DeclaredMethod(
                    typeof(FocusedAiActionDiagnostics), prefixName);
                MethodInfo finalizer = AccessTools.DeclaredMethod(
                    typeof(FocusedAiActionDiagnostics), finalizerName);
                if (original == null || prefix == null || finalizer == null) {
                    TryLogError("Optimization Mod focused AI diagnostic "
                        + "target is unavailable: " + targetType.FullName
                        + "." + targetName + ".");
                    return 0;
                }
                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix) {
                        priority = HarmonyLib.Priority.First
                    },
                    finalizer: new HarmonyMethod(finalizer) {
                        priority = HarmonyLib.Priority.Last
                    });
                return 1;
            } catch (Exception ex) {
                TryLogError("Optimization Mod failed to install focused AI "
                    + "diagnostic for " + targetType.FullName + "."
                    + targetName + ": " + ex);
                return 0;
            }
        }

        private static void BeginRunAgent(out int __state) {
            __state = runAgentDepth;
            if (IsFailOpen) { return; }
            try {
                runAgentDepth++;
                if (__state == 0) {
                    runAgentCalls++;
                    long tick = Game.Constants.G.Ticks;
                    if (tick != lastRunAgentTick) {
                        lastRunAgentTick = tick;
                        runAgentTicks++;
                    }
                }
            } catch (Exception ex) {
                runAgentDepth = __state;
                FailOpen(ex);
            }
        }

        private static Exception EndRunAgent(
                Exception __exception, int __state) {
            runAgentDepth = __state;
            return __exception;
        }

        private static void BeginEnsureCurrentAction(out TimingState __state) {
            BeginNested(0, out __state);
        }

        private static void BeginCounterUpdate(out TimingState __state) {
            BeginNested(1, out __state);
        }

        private static void BeginClearGoal(out TimingState __state) {
            BeginNested(2, out __state);
        }

        private static void BeginCheckDone(out TimingState __state) {
            BeginNested(3, out __state);
        }

        private static void BeginNested(int phase, out TimingState state) {
            state = default;
            if (IsFailOpen || runAgentDepth <= 0
                    || AiActionPerformanceDiagnostics.IsInsideAction) {
                return;
            }
            state.Active = true;
            state.Phase = phase;
            state.StartedAt = Stopwatch.GetTimestamp();
        }

        private static Exception EndNested(
                Exception __exception, TimingState __state) {
            Record(WrapperPhases, __state, __exception != null);
            return __exception;
        }

        private static void BeginStoreObj(
                AIAgentComp __1, out TimingState __state) {
            __state = default;
            if (IsFailOpen) { return; }
            try {
                int phase;
                if (__1.Motion.IsBusy) {
                    phase = 0;
                } else if (__1.BB.TargetObjData == null) {
                    phase = 1;
                } else if (!__1.BB.Bool1NotSaved) {
                    phase = 2;
                } else if (__1.BB.TargetTile == null) {
                    phase = 3;
                } else {
                    phase = 4;
                }
                __state = new TimingState {
                    Active = true,
                    Phase = phase,
                    StartedAt = Stopwatch.GetTimestamp()
                };
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static Exception EndStoreObj(
                Exception __exception, TimingState __state) {
            Record(StorePhases, __state, __exception != null);
            return __exception;
        }

        private static void BeginStorageSearch(
                GameState __0,
                AIAgentComp __1,
                ObjData __2,
                out SearchTimingState __state) {
            __state = default;
            if (IsFailOpen) { return; }
            try {
                int count = 0;
                StorableObjComp storable = __2?.Obj?.StorableObj;
                if (storable != null) {
                    List<ObjStorageComp> storages = __0.Query
                        .ObjStoragesForGroup.Ask(storable.GroupH);
                    count = storages?.Count ?? 0;
                }
                __state = new SearchTimingState {
                    Active = true,
                    StartedAt = Stopwatch.GetTimestamp(),
                    Bucket = StorageBucket(count),
                    CandidateCount = count,
                    Grounded = __1.Motion.IsGrounded,
                    Agent = __1
                };
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static Exception EndStorageSearch(
                Exception __exception, SearchTimingState __state) {
            if (!__state.Active || IsFailOpen) { return __exception; }
            try {
                long elapsed = Math.Max(0,
                    Stopwatch.GetTimestamp() - __state.StartedAt);
                var key = new SearchKey(__state.Bucket, __state.Grounded);
                if (!Searches.TryGetValue(key, out SearchStats stats)) {
                    stats = new SearchStats();
                    Searches.Add(key, stats);
                }
                bool selected = __state.Agent?.BB.TargetTile != null;
                stats.Record(elapsed, __state.CandidateCount, selected,
                    __exception != null);
            } catch (Exception ex) {
                FailOpen(ex);
            }
            return __exception;
        }

        private static void BeginReinstall(
                AIActionReinstall __instance,
                AIAgentComp __1,
                out TimingState __state) {
            __state = default;
            if (IsFailOpen) { return; }
            try {
                int phase = __1.Motion.IsBusy
                    ? 0
                    : 1 + (int) __instance.GetPhase(__1);
                if (phase < 0 || phase >= ReinstallPhaseCount - 1) {
                    phase = ReinstallPhaseCount - 1;
                }
                __state = new TimingState {
                    Active = true,
                    Phase = phase,
                    StartedAt = Stopwatch.GetTimestamp()
                };
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static Exception EndReinstall(
                Exception __exception, TimingState __state) {
            Record(ReinstallPhases, __state, __exception != null);
            return __exception;
        }

        private static void Record(
                Metric[] metrics, TimingState state, bool threw) {
            if (!state.Active || IsFailOpen) { return; }
            try {
                long elapsed = Math.Max(0,
                    Stopwatch.GetTimestamp() - state.StartedAt);
                if (state.Phase >= 0 && state.Phase < metrics.Length) {
                    metrics[state.Phase].Record(elapsed, threw);
                }
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static int StorageBucket(int count) {
            if (count <= 0) { return 0; }
            if (count <= 4) { return 1; }
            if (count <= 16) { return 2; }
            if (count <= 64) { return 3; }
            if (count <= 256) { return 4; }
            return 5;
        }

        private static string StorageBucketLabel(int bucket) {
            switch (bucket) {
                case 0: return "0";
                case 1: return "1-4";
                case 2: return "5-16";
                case 3: return "17-64";
                case 4: return "65-256";
                default: return "257+";
            }
        }

        internal static void LogSummary() {
            try {
                D.Warn("Optimization Mod focused AI diagnostic summary: "
                    + $"hooks={installedHooks}/{ExpectedHookCount}, "
                    + $"RunAgent calls={runAgentCalls}, AI ticks="
                    + $"{runAgentTicks}, fail-open={IsFailOpen}.");
                LogMetrics("StoreObj branch", StorePhases);
                foreach (KeyValuePair<SearchKey, SearchStats> row in Searches) {
                    SearchStats stats = row.Value;
                    double average = stats.Calls > 0
                        ? ToMilliseconds(stats.TotalTicks) / stats.Calls
                        : 0d;
                    double averageCandidates = stats.Calls > 0
                        ? stats.CandidateTotal / (double) stats.Calls
                        : 0d;
                    D.Warn("Optimization Mod StoreObj storage search: "
                        + $"candidates={StorageBucketLabel(row.Key.Bucket)}, "
                        + $"grounded={row.Key.Grounded}, calls={stats.Calls}, "
                        + $"candidate avg/max={averageCandidates:0.0}/"
                        + $"{stats.CandidateMaximum}, selected/no-target="
                        + $"{stats.Selected}/{stats.NoTarget}, total="
                        + $"{ToMilliseconds(stats.TotalTicks):0.###} ms, "
                        + $"avg={average:0.######} ms, max="
                        + $"{ToMilliseconds(stats.MaximumTicks):0.###} ms, "
                        + $">=1/4/8/16/33 ms={stats.AtLeastOne}/"
                        + $"{stats.AtLeastFour}/{stats.AtLeastEight}/"
                        + $"{stats.AtLeastSixteen}/{stats.AtLeastThirtyThree}, "
                        + $"exceptions={stats.Exceptions}.");
                }
                LogMetrics("Reinstall phase", ReinstallPhases);
                LogMetrics("RunAgent nested", WrapperPhases);
            } catch (Exception ex) {
                TryLogError("Optimization Mod could not write focused AI "
                    + "diagnostic summary: " + ex);
            }
        }

        private static void LogMetrics(string heading, Metric[] metrics) {
            for (int i = 0; i < metrics.Length; i++) {
                Metric metric = metrics[i];
                if (metric.Calls == 0) { continue; }
                double average = ToMilliseconds(metric.TotalTicks)
                    / metric.Calls;
                double averagePerTick = runAgentTicks > 0
                    ? ToMilliseconds(metric.TotalTicks) / runAgentTicks
                    : 0d;
                D.Warn($"Optimization Mod {heading} {metric.Name}: "
                    + $"calls={metric.Calls}, total="
                    + $"{ToMilliseconds(metric.TotalTicks):0.###} ms, "
                    + $"avg/call={average:0.######} ms, avg/AI tick="
                    + $"{averagePerTick:0.###} ms, max="
                    + $"{ToMilliseconds(metric.MaximumTicks):0.###} ms, "
                    + $">=1/4/8/16/33 ms={metric.AtLeastOne}/"
                    + $"{metric.AtLeastFour}/{metric.AtLeastEight}/"
                    + $"{metric.AtLeastSixteen}/{metric.AtLeastThirtyThree}, "
                    + $"exceptions={metric.Exceptions}.");
            }
        }

        private static Metric[] CreateMetrics(params string[] names) {
            var metrics = new Metric[names.Length];
            for (int i = 0; i < names.Length; i++) {
                metrics[i] = new Metric(names[i]);
            }
            return metrics;
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000d / Stopwatch.Frequency;
        }

        private static void FailOpen(Exception ex) {
            IsFailOpen = true;
            if (failureLogged) { return; }
            failureLogged = true;
            TryLogError("Optimization Mod focused AI diagnostics disabled "
                + "after an internal error; AI behavior is unchanged: " + ex);
        }

        private static void TryLogError(string message) {
            try { D.Err(message); } catch { }
        }

        internal static void Reset() {
            ResetMetrics(StorePhases);
            ResetMetrics(ReinstallPhases);
            ResetMetrics(WrapperPhases);
            Searches.Clear();
            runAgentDepth = 0;
            runAgentCalls = 0;
            runAgentTicks = 0;
            lastRunAgentTick = long.MinValue;
            installedHooks = 0;
            failureLogged = false;
            IsFailOpen = false;
        }

        private static void ResetMetrics(Metric[] metrics) {
            for (int i = 0; i < metrics.Length; i++) { metrics[i].Reset(); }
        }

        private class Metric {
            internal readonly string Name;
            internal long Calls;
            internal long TotalTicks;
            internal long MaximumTicks;
            internal long AtLeastOne;
            internal long AtLeastFour;
            internal long AtLeastEight;
            internal long AtLeastSixteen;
            internal long AtLeastThirtyThree;
            internal long Exceptions;

            internal Metric(string name) { Name = name; }

            internal void Record(long elapsed, bool threw) {
                Calls++;
                TotalTicks += elapsed;
                if (elapsed > MaximumTicks) { MaximumTicks = elapsed; }
                if (elapsed >= Stopwatch.Frequency * 0.001d) { AtLeastOne++; }
                if (elapsed >= Stopwatch.Frequency * 0.004d) { AtLeastFour++; }
                if (elapsed >= Stopwatch.Frequency * 0.008d) { AtLeastEight++; }
                if (elapsed >= Stopwatch.Frequency * 0.016d) { AtLeastSixteen++; }
                if (elapsed >= Stopwatch.Frequency * 0.033d) {
                    AtLeastThirtyThree++;
                }
                if (threw) { Exceptions++; }
            }

            internal void Reset() {
                Calls = 0;
                TotalTicks = 0;
                MaximumTicks = 0;
                AtLeastOne = 0;
                AtLeastFour = 0;
                AtLeastEight = 0;
                AtLeastSixteen = 0;
                AtLeastThirtyThree = 0;
                Exceptions = 0;
            }
        }

        private readonly struct SearchKey : IEquatable<SearchKey> {
            internal readonly int Bucket;
            internal readonly bool Grounded;

            internal SearchKey(int bucket, bool grounded) {
                Bucket = bucket;
                Grounded = grounded;
            }

            public bool Equals(SearchKey other) {
                return Bucket == other.Bucket && Grounded == other.Grounded;
            }

            public override bool Equals(object obj) {
                return obj is SearchKey other && Equals(other);
            }

            public override int GetHashCode() {
                return Bucket * 397 ^ (Grounded ? 1 : 0);
            }
        }

        private sealed class SearchStats : Metric {
            internal long CandidateTotal;
            internal int CandidateMaximum;
            internal long Selected;
            internal long NoTarget;

            internal SearchStats() : base("storage search") { }

            internal void Record(
                    long elapsed, int candidates, bool selected, bool threw) {
                base.Record(elapsed, threw);
                CandidateTotal += candidates;
                if (candidates > CandidateMaximum) {
                    CandidateMaximum = candidates;
                }
                if (selected) { Selected++; } else { NoTarget++; }
            }
        }

        private struct TimingState {
            internal bool Active;
            internal int Phase;
            internal long StartedAt;
        }

        private struct SearchTimingState {
            internal bool Active;
            internal long StartedAt;
            internal int Bucket;
            internal int CandidateCount;
            internal bool Grounded;
            internal AIAgentComp Agent;
        }
    }
}
