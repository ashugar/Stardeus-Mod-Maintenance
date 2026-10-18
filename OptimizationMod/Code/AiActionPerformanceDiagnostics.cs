using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Diagnostics-only profiler for the concrete AIAction.Execute boundaries
    /// supplied by Core and currently loaded mods. The measured boundary also
    /// includes Harmony work attached to an action. It never changes an action
    /// result, exception, goal, or agent state.
    /// </summary>
    internal static class AiActionPerformanceDiagnostics {
        private const int MaxRows = 25;
        private const int MaxRetainedSpikes = 12;

        private static readonly Type[] ExecuteParameters = {
            typeof(GameState), typeof(AIAgentComp), typeof(AIGoal),
            typeof(long)
        };

        private static readonly long OneMsTicks =
            (long) (Stopwatch.Frequency * 0.001d);
        private static readonly long FourMsTicks =
            (long) (Stopwatch.Frequency * 0.004d);
        private static readonly long EightMsTicks =
            (long) (Stopwatch.Frequency * 0.008d);
        private static readonly long SixteenMsTicks =
            (long) (Stopwatch.Frequency * 0.016d);
        private static readonly long ThirtyThreeMsTicks =
            (long) (Stopwatch.Frequency * 0.033d);

        private static readonly Dictionary<ActionKey, ActionStats> Stats =
            new Dictionary<ActionKey, ActionStats>();
        private static readonly List<ActionSpike> WorstSpikes =
            new List<ActionSpike>(MaxRetainedSpikes);

        private static long sessionStartedAt;
        private static long totalCalls;
        private static long totalTicks;
        private static long maximumTicks;
        private static long exceptionCalls;
        private static bool failureLogged;
        [ThreadStatic] private static int actionDepth;

        internal static int DiscoveredImplementationCount { get; private set; }
        internal static int InstalledImplementationCount { get; private set; }
        internal static int InstallFailures { get; private set; }
        internal static bool IsFailOpen { get; private set; }
        internal static bool IsInsideAction => actionDepth > 0;

        internal static int Install(Harmony harmony) {
            Reset();
            sessionStartedAt = Stopwatch.GetTimestamp();

            MethodInfo prefix = AccessTools.DeclaredMethod(
                typeof(AiActionPerformanceDiagnostics), nameof(Begin));
            MethodInfo finalizer = AccessTools.DeclaredMethod(
                typeof(AiActionPerformanceDiagnostics), nameof(End));
            if (prefix == null || finalizer == null) {
                TryLogError("Optimization Mod AI action diagnostic patch "
                    + "methods are unavailable.");
                InstallFailures = 1;
                return 0;
            }

            int installed = 0;
            var seen = new HashSet<MethodInfo>();
            Assembly[] assemblies;
            try {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            } catch (Exception ex) {
                TryLogError("Optimization Mod could not enumerate assemblies "
                    + "for AI action diagnostics: " + ex);
                InstallFailures = 1;
                return 0;
            }

            for (int a = 0; a < assemblies.Length; a++) {
                Type[] types = SafeTypes(assemblies[a]);
                for (int t = 0; t < types.Length; t++) {
                    Type type = types[t];
                    if (type == null
                            || !typeof(AIAction).IsAssignableFrom(type)) {
                        continue;
                    }

                    MethodInfo execute;
                    try {
                        execute = type.GetMethod(nameof(AIAction.Execute),
                            BindingFlags.Instance | BindingFlags.Public
                            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, ExecuteParameters, null);
                    } catch {
                        InstallFailures++;
                        continue;
                    }
                    if (execute == null || execute.IsAbstract
                            || execute.ReturnType != typeof(AIActionResult)
                            || !seen.Add(execute)) {
                        continue;
                    }

                    DiscoveredImplementationCount++;
                    try {
                        harmony.Patch(execute,
                            prefix: new HarmonyMethod(prefix) {
                                priority = HarmonyLib.Priority.First
                            },
                            finalizer: new HarmonyMethod(finalizer) {
                                priority = HarmonyLib.Priority.Last
                            });
                        installed++;
                        InstalledImplementationCount++;
                    } catch (Exception ex) {
                        InstallFailures++;
                        TryLogError("Optimization Mod failed to install AI "
                            + "action diagnostics for " + type.FullName + "."
                            + nameof(AIAction.Execute) + ": " + ex);
                    }
                }
            }
            return installed;
        }

        private static Type[] SafeTypes(Assembly assembly) {
            try {
                return assembly.GetTypes();
            } catch (ReflectionTypeLoadException ex) {
                var available = new List<Type>();
                Type[] partial = ex.Types;
                if (partial != null) {
                    for (int i = 0; i < partial.Length; i++) {
                        if (partial[i] != null) { available.Add(partial[i]); }
                    }
                }
                return available.ToArray();
            } catch {
                return Type.EmptyTypes;
            }
        }

        private static void Begin(
                AIAction __instance,
                AIGoal __2,
                out ActionTimingState __state) {
            __state = default;
            if (IsFailOpen) { return; }
            try {
                __state = new ActionTimingState {
                    Active = true,
                    StartedAt = Stopwatch.GetTimestamp(),
                    Action = __instance,
                    GoalType = __2 == null ? -1 : (int) __2.Type,
                    JobIdH = __2?.JobIdH ?? 0,
                    PreviousDepth = actionDepth
                };
                actionDepth++;
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static Exception End(
                Exception __exception,
                ActionTimingState __state) {
            if (!__state.Active) { return __exception; }
            try {
                if (IsFailOpen) { return __exception; }
                long now = Stopwatch.GetTimestamp();
                long elapsed = Math.Max(0, now - __state.StartedAt);
                bool threw = __exception != null;
                var key = new ActionKey(
                    __state.Action, __state.GoalType, __state.JobIdH);
                if (!Stats.TryGetValue(key, out ActionStats stats)) {
                    stats = new ActionStats();
                    Stats.Add(key, stats);
                }
                stats.Record(elapsed, threw);
                totalCalls++;
                totalTicks += elapsed;
                if (elapsed > maximumTicks) { maximumTicks = elapsed; }
                if (threw) { exceptionCalls++; }

                RetainSpike(new ActionSpike {
                    Offset = now - sessionStartedAt,
                    Tick = G.Ticks,
                    Action = __state.Action,
                    GoalType = __state.GoalType,
                    JobIdH = __state.JobIdH,
                    Elapsed = elapsed,
                    Threw = threw
                });
            } catch (Exception ex) {
                FailOpen(ex);
            } finally {
                actionDepth = __state.PreviousDepth;
            }
            return __exception;
        }

        private static void RetainSpike(ActionSpike spike) {
            if (WorstSpikes.Count >= MaxRetainedSpikes
                    && WorstSpikes[WorstSpikes.Count - 1].Elapsed
                        >= spike.Elapsed) {
                return;
            }
            int insertAt = 0;
            while (insertAt < WorstSpikes.Count
                    && WorstSpikes[insertAt].Elapsed >= spike.Elapsed) {
                insertAt++;
            }
            if (insertAt >= MaxRetainedSpikes) { return; }
            WorstSpikes.Insert(insertAt, spike);
            if (WorstSpikes.Count > MaxRetainedSpikes) {
                WorstSpikes.RemoveAt(WorstSpikes.Count - 1);
            }
        }

        internal static void LogSummary() {
            try {
                double averageMs = totalCalls > 0
                    ? ToMilliseconds(totalTicks) / totalCalls
                    : 0d;
                D.Warn("Optimization Mod AI action diagnostic summary: "
                    + $"implementations discovered/patched="
                    + $"{DiscoveredImplementationCount}/"
                    + $"{InstalledImplementationCount}, "
                    + $"install failures={InstallFailures}, calls={totalCalls}, "
                    + $"total={ToMilliseconds(totalTicks):0.###} ms, "
                    + $"avg/call={averageMs:0.######} ms, max="
                    + $"{ToMilliseconds(maximumTicks):0.###} ms, action "
                    + $"exceptions={exceptionCalls}, fail-open={IsFailOpen}. "
                    + "The measured boundary includes Harmony patches attached "
                    + "to each action and is inclusive if an action invokes "
                    + "another profiled action.");

                var rows = new List<KeyValuePair<ActionKey, ActionStats>>(
                    Stats);
                rows.Sort((left, right) =>
                    right.Value.TotalTicks.CompareTo(left.Value.TotalTicks));
                int rowCount = Math.Min(MaxRows, rows.Count);
                for (int i = 0; i < rowCount; i++) {
                    ActionKey key = rows[i].Key;
                    ActionStats stats = rows[i].Value;
                    double rowAverage = stats.Calls > 0
                        ? ToMilliseconds(stats.TotalTicks) / stats.Calls
                        : 0d;
                    D.Warn($"Optimization Mod AI action total #{i + 1}: "
                        + $"action={ActionLabel(key.Action)}, goal="
                        + $"{GoalLabel(key.GoalType)}, job="
                        + $"{JobLabel(key.JobIdH)}, calls={stats.Calls}, "
                        + $"total={ToMilliseconds(stats.TotalTicks):0.###} ms, "
                        + $"avg={rowAverage:0.######} ms, max="
                        + $"{ToMilliseconds(stats.MaximumTicks):0.###} ms, "
                        + $">=1/4/8/16/33 ms={stats.AtLeastOne}/"
                        + $"{stats.AtLeastFour}/{stats.AtLeastEight}/"
                        + $"{stats.AtLeastSixteen}/{stats.AtLeastThirtyThree}, "
                        + $"exceptions={stats.Exceptions}, attached Harmony "
                        + $"owners={PatchOwners(key.Action)}.");
                }

                for (int i = 0; i < WorstSpikes.Count; i++) {
                    ActionSpike spike = WorstSpikes[i];
                    D.Warn($"Optimization Mod AI action spike #{i + 1}: "
                        + $"+{ToSeconds(spike.Offset):0.000}s, tick="
                        + $"{spike.Tick}, action={ActionLabel(spike.Action)}, "
                        + $"goal={GoalLabel(spike.GoalType)}, job="
                        + $"{JobLabel(spike.JobIdH)}, elapsed="
                        + $"{ToMilliseconds(spike.Elapsed):0.###} ms, "
                        + $"exception={spike.Threw}.");
                }
            } catch (Exception ex) {
                TryLogError("Optimization Mod could not write AI action "
                    + "diagnostic summary: " + ex);
            }
        }

        private static string ActionLabel(AIAction action) {
            if (action == null) { return "<null>"; }
            try {
                Type type = action.GetType();
                string assembly = type.Assembly.GetName().Name;
                string id = string.IsNullOrEmpty(action.Id)
                    ? "<no-id>"
                    : action.Id;
                return id + " [" + type.FullName + " @ " + assembly + "]";
            } catch {
                return "<unavailable>";
            }
        }

        private static string GoalLabel(int goalType) {
            return goalType < 0
                ? "<none>"
                : ((AIGoalType) goalType).ToString();
        }

        private static string PatchOwners(AIAction action) {
            if (action == null) { return "<unavailable>"; }
            try {
                MethodInfo execute = action.GetType().GetMethod(
                    nameof(AIAction.Execute),
                    BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic,
                    null, ExecuteParameters, null);
                Patches patches = execute == null
                    ? null
                    : Harmony.GetPatchInfo(execute);
                if (patches == null) { return "<none>"; }
                var owners = new List<string>();
                foreach (string owner in patches.Owners) {
                    if (owner == "jleas.stardeus.optimizationmod"
                            || owners.Contains(owner)) {
                        continue;
                    }
                    owners.Add(owner);
                }
                if (owners.Count == 0) { return "<none>"; }
                owners.Sort(StringComparer.Ordinal);
                return string.Join(",", owners);
            } catch {
                return "<unavailable>";
            }
        }

        private static string JobLabel(int jobIdH) {
            if (jobIdH == 0) { return "<none>"; }
            try {
                return JobTypeIdH.ResolveName(jobIdH) + " (" + jobIdH + ")";
            } catch {
                return jobIdH.ToString();
            }
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000d / Stopwatch.Frequency;
        }

        private static double ToSeconds(long ticks) {
            return ticks / (double) Stopwatch.Frequency;
        }

        private static void FailOpen(Exception ex) {
            IsFailOpen = true;
            if (failureLogged) { return; }
            failureLogged = true;
            TryLogError("Optimization Mod AI action diagnostics disabled "
                + "after an internal error; AI behavior is unchanged: " + ex);
        }

        private static void TryLogError(string message) {
            try { D.Err(message); } catch { }
        }

        internal static void Reset() {
            Stats.Clear();
            WorstSpikes.Clear();
            sessionStartedAt = 0;
            totalCalls = 0;
            totalTicks = 0;
            maximumTicks = 0;
            exceptionCalls = 0;
            failureLogged = false;
            actionDepth = 0;
            DiscoveredImplementationCount = 0;
            InstalledImplementationCount = 0;
            InstallFailures = 0;
            IsFailOpen = false;
        }

        private readonly struct ActionKey : IEquatable<ActionKey> {
            internal readonly AIAction Action;
            internal readonly int GoalType;
            internal readonly int JobIdH;

            internal ActionKey(AIAction action, int goalType, int jobIdH) {
                Action = action;
                GoalType = goalType;
                JobIdH = jobIdH;
            }

            public bool Equals(ActionKey other) {
                return ReferenceEquals(Action, other.Action)
                    && GoalType == other.GoalType
                    && JobIdH == other.JobIdH;
            }

            public override bool Equals(object obj) {
                return obj is ActionKey other && Equals(other);
            }

            public override int GetHashCode() {
                unchecked {
                    int hash = Action == null
                        ? 0
                        : RuntimeHelpers.GetHashCode(Action);
                    hash = hash * 397 ^ GoalType;
                    return hash * 397 ^ JobIdH;
                }
            }
        }

        private sealed class ActionStats {
            internal long Calls;
            internal long TotalTicks;
            internal long MaximumTicks;
            internal long AtLeastOne;
            internal long AtLeastFour;
            internal long AtLeastEight;
            internal long AtLeastSixteen;
            internal long AtLeastThirtyThree;
            internal long Exceptions;

            internal void Record(long elapsed, bool threw) {
                Calls++;
                TotalTicks += elapsed;
                if (elapsed > MaximumTicks) { MaximumTicks = elapsed; }
                if (elapsed >= OneMsTicks) { AtLeastOne++; }
                if (elapsed >= FourMsTicks) { AtLeastFour++; }
                if (elapsed >= EightMsTicks) { AtLeastEight++; }
                if (elapsed >= SixteenMsTicks) { AtLeastSixteen++; }
                if (elapsed >= ThirtyThreeMsTicks) { AtLeastThirtyThree++; }
                if (threw) { Exceptions++; }
            }
        }

        private struct ActionTimingState {
            internal bool Active;
            internal long StartedAt;
            internal AIAction Action;
            internal int GoalType;
            internal int JobIdH;
            internal int PreviousDepth;
        }

        private struct ActionSpike {
            internal long Offset;
            internal long Tick;
            internal AIAction Action;
            internal int GoalType;
            internal int JobIdH;
            internal long Elapsed;
            internal bool Threw;
        }
    }
}
