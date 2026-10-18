using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Game.CodeGen;
using Game.Constants;
using Game.Utils;
using HarmonyLib;
using KL.Signals;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Temporary, observation-only whole-frame profiler. It discovers actual
    /// registered Signal listeners after game initialization, attributes those
    /// callbacks and Unity lifecycle methods to their owning assemblies,
    /// subtracts nested measured scopes, and retains bounded worst-frame trees.
    /// It does not replace delegates, mutate listener lists, or change callback
    /// ordering and exception behavior.
    /// </summary>
    internal static class WholeFrameHierarchyDiagnostics {
        private const int MaxScopeDepth = 128;
        private const int MaxRetainedFrames = 16;
        private const int MaxFrameContributors = 10;
        private const int MaxLoggedTargets = 512;

        private static readonly string[] LifecycleNames = {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI"
        };
        private static readonly long FourMsTicks =
            (long) (Stopwatch.Frequency * 0.004d);
        private static readonly long EightMsTicks =
            (long) (Stopwatch.Frequency * 0.008d);
        private static readonly long SixteenMsTicks =
            (long) (Stopwatch.Frequency * 0.016d);
        private static readonly long ThirtyThreeMsTicks =
            (long) (Stopwatch.Frequency * 0.033d);
        private static readonly long FiftyMsTicks =
            (long) (Stopwatch.Frequency * 0.050d);
        private static readonly long HundredMsTicks =
            (long) (Stopwatch.Frequency * 0.100d);

        private static readonly Dictionary<TargetKey, TargetStats> Targets =
            new Dictionary<TargetKey, TargetStats>();
        private static readonly Dictionary<MethodBase, TargetStats>
            CallbackTargets = new Dictionary<MethodBase, TargetStats>();
        private static readonly HashSet<MethodBase> PatchedMethods =
            new HashSet<MethodBase>();
        private static readonly Dictionary<TargetStats, long>
            CurrentFrameContributors =
                new Dictionary<TargetStats, long>();
        private static readonly List<FrameRecord> WorstFrames =
            new List<FrameRecord>(MaxRetainedFrames);
        private static readonly ScopeEntry[] ScopeStack =
            new ScopeEntry[MaxScopeDepth];

        private static Harmony profilerHarmony;
        private static int mainThreadId;
        private static int scopeDepth;
        private static long sessionStartedAt;
        private static long instrumentationTicks;
        private static long scopeEntries;
        private static long frameStartedAt;
        private static long frameOrdinal;
        private static long frameMeasuredExclusive;
        private static int frameSpeed;
        private static int frameGc0;
        private static int frameGc1;
        private static int frameGc2;
        private static bool frameActive;
        private static long framesOver16Ms;
        private static long framesOver33Ms;
        private static long framesOver50Ms;
        private static long framesOver100Ms;
        private static long signalListenerCalls;
        private static int signalListenerMethodsInstalled;
        private static int lifecycleMethodsInstalled;
        private static int listenerScans;
        private static int listenersObserved;
        private static int listenerPatchSkips;
        private static int patchFailures;
        private static long listenerScanTicks;
        private static bool needsFinalListenerScan;
        private static bool failureLogged;

        internal static bool IsFailOpen { get; private set; }

        internal static int Install(Harmony harmony) {
            Reset();
            profilerHarmony = harmony;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            sessionStartedAt = Stopwatch.GetTimestamp();
            int installed = InstallLifecycleProfiling(harmony);

            // Capture listeners already registered before this GameSystem.
            installed += RefreshSignalListenerProfiling();
            // GameSystem initialization order is not an ownership contract.
            // Rescan once at the first real frame, after all systems and mods
            // have had their initialization opportunity.
            needsFinalListenerScan = true;
            return installed;
        }

        private static int InstallLifecycleProfiling(Harmony harmony) {
            int installed = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain
                    .GetAssemblies().Where(IsProfiledAssembly)) {
                foreach (Type type in GetTypesSafe(assembly)) {
                    if (type == null
                            || !typeof(MonoBehaviour).IsAssignableFrom(type)) {
                        continue;
                    }
                    foreach (string methodName in LifecycleNames) {
                        MethodInfo method = type.GetMethod(methodName,
                            BindingFlags.Instance | BindingFlags.Public
                            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, Type.EmptyTypes, null);
                        if (!CanProfileMethod(method)) { continue; }
                        bool frameRoot = type == typeof(Bootstrap)
                            && methodName == "Update";
                        var target = GetOrCreateTarget(
                            "Unity." + methodName, string.Empty, method,
                            assembly.GetName().Name, frameRoot);
                        if (TryPatchCallback(harmony, method, target)) {
                            installed++;
                            lifecycleMethodsInstalled++;
                        }
                    }
                }
            }
            return installed;
        }

        private static int RefreshSignalListenerProfiling() {
            if (IsFailOpen || profilerHarmony == null) { return 0; }
            long startedAt = Stopwatch.GetTimestamp();
            int installed = 0;
            try {
                listenerScans++;
                var contexts = new Dictionary<MethodInfo, HashSet<string>>();
                foreach (ISignal signal in EnumerateSignals()) {
                    FieldInfo listenersField = FindListenersField(
                        signal.GetType());
                    if (!(listenersField?.GetValue(signal)
                            is IEnumerable listeners)) {
                        continue;
                    }
                    foreach (object value in listeners) {
                        if (!(value is Delegate listener)
                                || listener.Method == null) {
                            continue;
                        }
                        listenersObserved++;
                        MethodInfo method = listener.Method;
                        if (!contexts.TryGetValue(
                                method, out HashSet<string> handles)) {
                            handles = new HashSet<string>();
                            contexts.Add(method, handles);
                        }
                        handles.Add(signal.Handle ?? "unnamed signal");
                    }
                }

                foreach (KeyValuePair<MethodInfo, HashSet<string>> item
                        in contexts) {
                    MethodInfo method = item.Key;
                    if (PatchedMethods.Contains(method)) { continue; }
                    if (!CanProfileMethod(method)) {
                        listenerPatchSkips++;
                        continue;
                    }
                    string handles = string.Join(", ",
                        item.Value.OrderBy(value => value));
                    string assembly = method.DeclaringType?.Assembly
                        .GetName().Name ?? "unknown";
                    var target = GetOrCreateTarget(
                        "Signal.Listener", handles, method, assembly,
                        isFrameRoot: false);
                    if (TryPatchCallback(
                            profilerHarmony, method, target)) {
                        installed++;
                        signalListenerMethodsInstalled++;
                    }
                }
            } catch (Exception ex) {
                FailOpen(ex);
            } finally {
                listenerScanTicks +=
                    Stopwatch.GetTimestamp() - startedAt;
            }
            return installed;
        }

        private static IEnumerable<ISignal> EnumerateSignals() {
            foreach (string fieldName in new[] { "gameSignals", "sysSignals" }) {
                FieldInfo field = AccessTools.Field(
                    typeof(SignalRegistry), fieldName);
                if (!(field?.GetValue(null) is IEnumerable signals)) {
                    continue;
                }
                foreach (object value in signals) {
                    if (value is ISignal signal) { yield return signal; }
                }
            }
        }

        private static FieldInfo FindListenersField(Type type) {
            for (Type current = type; current != null;
                    current = current.BaseType) {
                FieldInfo field = current.GetField("listeners",
                    BindingFlags.Instance | BindingFlags.NonPublic
                    | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null) { return field; }
            }
            return null;
        }

        private static bool TryPatchCallback(
                Harmony harmony,
                MethodInfo method,
                TargetStats target) {
            if (method == null || target == null
                    || !PatchedMethods.Add(method)) {
                return false;
            }
            CallbackTargets[method] = target;
            try {
                harmony.Patch(method,
                    prefix: new HarmonyMethod(
                        typeof(WholeFrameHierarchyDiagnostics),
                        nameof(BeginObservedCallback)) {
                        priority = HarmonyLib.Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        typeof(WholeFrameHierarchyDiagnostics),
                        nameof(EndObservedCallback)) {
                        priority = HarmonyLib.Priority.Last
                    });
                return true;
            } catch (Exception ex) {
                PatchedMethods.Remove(method);
                CallbackTargets.Remove(method);
                patchFailures++;
                if (patchFailures <= 8) {
                    TryLogError("Optimization Mod whole-frame profiler failed "
                        + "to instrument callback " + method + ": " + ex,
                        suppressFutureMessages: false);
                }
                return false;
            }
        }

        private static bool CanProfileMethod(MethodInfo method) {
            if (method == null || method.IsAbstract
                    || method.ContainsGenericParameters
                    || method.DeclaringType == null
                    || method.DeclaringType.ContainsGenericParameters
                    || method.DeclaringType ==
                        typeof(WholeFrameHierarchyDiagnostics)
                    || method.Module.Assembly.IsDynamic) {
                return false;
            }
            try {
                return method.GetMethodBody() != null;
            } catch {
                return false;
            }
        }

        private static bool IsProfiledAssembly(Assembly assembly) {
            if (assembly == null || assembly.IsDynamic
                    || assembly == typeof(WholeFrameHierarchyDiagnostics)
                        .Assembly) {
                return false;
            }
            if (assembly == typeof(Bootstrap).Assembly) { return true; }
            try {
                return assembly.GetReferencedAssemblies()
                    .Any(reference => reference.Name == "Game");
            } catch {
                return false;
            }
        }

        private static IEnumerable<Type> GetTypesSafe(Assembly assembly) {
            try {
                return assembly.GetTypes();
            } catch (ReflectionTypeLoadException ex) {
                return ex.Types.Where(type => type != null);
            } catch {
                return Array.Empty<Type>();
            }
        }

        private static void BeginObservedCallback(
                MethodBase __originalMethod,
                out int __state) {
            __state = -1;
            if (IsFailOpen || __originalMethod == null
                    || !CallbackTargets.TryGetValue(
                        __originalMethod, out TargetStats target)) {
                return;
            }
            if (target.IsFrameRoot && Ready.Game) {
                if (needsFinalListenerScan) {
                    needsFinalListenerScan = false;
                    RefreshSignalListenerProfiling();
                }
                BeginFrame(Stopwatch.GetTimestamp());
            }
            __state = EnterScope(target);
        }

        private static Exception EndObservedCallback(
                Exception __exception,
                int __state) {
            EndScope(__state);
            return __exception;
        }

        private static int EnterScope(TargetStats target) {
            if (IsFailOpen || target == null
                    || Thread.CurrentThread.ManagedThreadId != mainThreadId
                    || !Ready.Game) {
                return -1;
            }
            long outerStartedAt = Stopwatch.GetTimestamp();
            try {
                if (scopeDepth >= ScopeStack.Length) {
                    throw new InvalidOperationException(
                        "Whole-frame profiler scope depth exceeded "
                        + ScopeStack.Length + ".");
                }
                int token = scopeDepth;
                long targetStartedAt = Stopwatch.GetTimestamp();
                instrumentationTicks += targetStartedAt - outerStartedAt;
                scopeEntries++;
                ScopeStack[scopeDepth++] = new ScopeEntry {
                    Target = target,
                    OuterStartedAt = outerStartedAt,
                    TargetStartedAt = targetStartedAt,
                    ChildSpanTicks = 0
                };
                return token;
            } catch (Exception ex) {
                FailOpen(ex);
                return -1;
            }
        }

        private static void EndScope(int token) {
            if (token < 0 || IsFailOpen
                    || Thread.CurrentThread.ManagedThreadId != mainThreadId) {
                return;
            }
            long targetEndedAt = Stopwatch.GetTimestamp();
            try {
                if (scopeDepth <= 0 || token != scopeDepth - 1) {
                    throw new InvalidOperationException(
                        "Whole-frame profiler scope stack mismatch.");
                }
                ScopeEntry entry = ScopeStack[--scopeDepth];
                long inclusive = Math.Max(
                    0L, targetEndedAt - entry.TargetStartedAt);
                long exclusive = Math.Max(
                    0L, inclusive - entry.ChildSpanTicks);
                entry.Target.Record(CurrentSpeed(), inclusive, exclusive);
                if (entry.Target.Category == "Signal.Listener") {
                    signalListenerCalls++;
                }
                if (frameActive) {
                    frameMeasuredExclusive += exclusive;
                    if (CurrentFrameContributors.TryGetValue(
                            entry.Target, out long existing)) {
                        CurrentFrameContributors[entry.Target] =
                            existing + exclusive;
                    } else {
                        CurrentFrameContributors[entry.Target] = exclusive;
                    }
                }

                long probeEndedAt = Stopwatch.GetTimestamp();
                instrumentationTicks += probeEndedAt - targetEndedAt;
                if (scopeDepth > 0) {
                    ScopeEntry parent = ScopeStack[scopeDepth - 1];
                    parent.ChildSpanTicks +=
                        probeEndedAt - entry.OuterStartedAt;
                    ScopeStack[scopeDepth - 1] = parent;
                }
            } catch (Exception ex) {
                FailOpen(ex);
            }
        }

        private static void BeginFrame(long now) {
            if (frameActive) { CompleteFrame(now); }
            frameActive = true;
            frameStartedAt = now;
            frameOrdinal = GameTimer.frameCount;
            frameSpeed = CurrentSpeed();
            frameGc0 = GC.CollectionCount(0);
            frameGc1 = GC.CollectionCount(1);
            frameGc2 = GC.CollectionCount(2);
            frameMeasuredExclusive = 0;
            CurrentFrameContributors.Clear();
        }

        private static void CompleteFrame(long now) {
            if (!frameActive || frameStartedAt <= 0) { return; }
            long interval = Math.Max(0L, now - frameStartedAt);
            if (interval >= SixteenMsTicks) { framesOver16Ms++; }
            if (interval >= ThirtyThreeMsTicks) { framesOver33Ms++; }
            if (interval >= FiftyMsTicks) { framesOver50Ms++; }
            if (interval >= HundredMsTicks) { framesOver100Ms++; }
            if (interval >= ThirtyThreeMsTicks
                    || frameMeasuredExclusive >= SixteenMsTicks) {
                RetainFrame(new FrameRecord {
                    Offset = now - sessionStartedAt,
                    Frame = frameOrdinal,
                    Speed = frameSpeed,
                    Interval = interval,
                    MeasuredExclusive = frameMeasuredExclusive,
                    GC0 = GC.CollectionCount(0) - frameGc0,
                    GC1 = GC.CollectionCount(1) - frameGc1,
                    GC2 = GC.CollectionCount(2) - frameGc2,
                    Contributors = CurrentFrameContributors
                        .OrderByDescending(pair => pair.Value)
                        .Take(MaxFrameContributors)
                        .Select(pair => new Contributor {
                            Label = pair.Key.DisplayLabel,
                            Assembly = pair.Key.AssemblyName,
                            Exclusive = pair.Value
                        }).ToArray()
                });
            }
        }

        private static void RetainFrame(FrameRecord record) {
            int insertAt = 0;
            while (insertAt < WorstFrames.Count
                    && WorstFrames[insertAt].Interval >= record.Interval) {
                insertAt++;
            }
            if (insertAt >= MaxRetainedFrames) { return; }
            WorstFrames.Insert(insertAt, record);
            if (WorstFrames.Count > MaxRetainedFrames) {
                WorstFrames.RemoveAt(WorstFrames.Count - 1);
            }
        }

        private static TargetStats GetOrCreateTarget(
                string category,
                string context,
                MethodBase method,
                string assembly,
                bool isFrameRoot) {
            var key = new TargetKey(category, context ?? string.Empty, method);
            if (Targets.TryGetValue(key, out TargetStats target)) {
                return target;
            }
            target = new TargetStats(category, context ?? string.Empty,
                method, assembly ?? "unknown", isFrameRoot);
            Targets.Add(key, target);
            return target;
        }

        internal static void LogSummary() {
            long now = Stopwatch.GetTimestamp();
            double sessionSeconds = sessionStartedAt <= 0
                ? 0d
                : (now - sessionStartedAt) /
                    (double) Stopwatch.Frequency;
            var observed = Targets.Values
                .Where(target => target.Calls > 0)
                .OrderByDescending(target => target.ExclusiveTotal)
                .ToList();
            D.Warn($"Optimization Mod whole-frame profiler summary: "
                + $"{sessionSeconds:0.0}s captured, listener scans="
                + $"{listenerScans}, listeners observed={listenersObserved}, "
                + $"unique listener methods patched="
                + $"{signalListenerMethodsInstalled}, Unity lifecycle methods "
                + $"patched={lifecycleMethodsInstalled}, skipped listener "
                + $"methods={listenerPatchSkips}, patch failures="
                + $"{patchFailures}, observed targets={observed.Count}, "
                + $"listener calls={signalListenerCalls}, scopes="
                + $"{scopeEntries}, fail-open={IsFailOpen}.");
            D.Warn("Optimization Mod whole-frame profiler overhead: "
                + "scope bookkeeping={0:0.###} ms ({1:0.###} us per scope), "
                + "listener discovery/patching={2:0.###} ms. Scope "
                + "bookkeeping is excluded from target exclusive totals where "
                + "nesting permits.", ToMilliseconds(instrumentationTicks),
                scopeEntries == 0 ? 0d
                    : ToMilliseconds(instrumentationTicks) * 1000d
                        / scopeEntries,
                ToMilliseconds(listenerScanTicks));
            D.Warn("Optimization Mod whole-frame thresholds: frame interval "
                + ">=16/33/50/100 ms={0}/{1}/{2}/{3}; retained worst={4}.",
                framesOver16Ms, framesOver33Ms, framesOver50Ms,
                framesOver100Ms, WorstFrames.Count);

            foreach (var assemblyGroup in observed
                    .GroupBy(target => target.AssemblyName)
                    .OrderByDescending(group =>
                        group.Sum(target => target.ExclusiveTotal))) {
                long calls = assemblyGroup.Sum(target => target.Calls);
                long inclusive = assemblyGroup.Sum(
                    target => target.InclusiveTotal);
                long exclusive = assemblyGroup.Sum(
                    target => target.ExclusiveTotal);
                D.Warn("Optimization Mod whole-frame assembly {0}: targets="
                    + "{1}, calls={2}, inclusive={3:0.###} ms, exclusive="
                    + "{4:0.###} ms.", assemblyGroup.Key,
                    assemblyGroup.Count(), calls,
                    ToMilliseconds(inclusive), ToMilliseconds(exclusive));
            }

            int logged = 0;
            foreach (TargetStats target in observed) {
                if (logged++ >= MaxLoggedTargets) { break; }
                target.Log(logged);
            }
            if (observed.Count > MaxLoggedTargets) {
                D.Warn("Optimization Mod whole-frame profiler omitted {0} "
                    + "lower-cost observed targets after the bounded {1}-target "
                    + "detail limit; assembly totals still include them.",
                    observed.Count - MaxLoggedTargets, MaxLoggedTargets);
            }

            for (int i = 0; i < WorstFrames.Count; i++) {
                FrameRecord frame = WorstFrames[i];
                long residual = Math.Max(
                    0L, frame.Interval - frame.MeasuredExclusive);
                string contributors = frame.Contributors == null
                    || frame.Contributors.Length == 0
                    ? "none"
                    : string.Join(" | ", frame.Contributors.Select(value =>
                        value.Label + " [" + value.Assembly + "] "
                        + ToMilliseconds(value.Exclusive).ToString("0.###")
                        + " ms"));
                D.Warn($"Optimization Mod whole-frame worst #{i + 1}: "
                    + $"+{ToSeconds(frame.Offset):0.000}s, "
                    + $"frame={frame.Frame}, "
                    + $"speed={(ClockSpeed) frame.Speed}, "
                    + $"interval={ToMilliseconds(frame.Interval):0.###} ms, "
                    + "measured exclusive="
                    + $"{ToMilliseconds(frame.MeasuredExclusive):0.###} ms, "
                    + "unmanaged/wait/uncovered="
                    + $"{ToMilliseconds(residual):0.###} ms, "
                    + $"GC delta={frame.GC0}/{frame.GC1}/{frame.GC2}; "
                    + $"top: {contributors}");
            }
        }

        internal static void Reset() {
            Targets.Clear();
            CallbackTargets.Clear();
            PatchedMethods.Clear();
            CurrentFrameContributors.Clear();
            WorstFrames.Clear();
            Array.Clear(ScopeStack, 0, ScopeStack.Length);
            profilerHarmony = null;
            mainThreadId = 0;
            scopeDepth = 0;
            sessionStartedAt = 0;
            instrumentationTicks = 0;
            scopeEntries = 0;
            frameStartedAt = 0;
            frameOrdinal = 0;
            frameMeasuredExclusive = 0;
            frameSpeed = 0;
            frameGc0 = 0;
            frameGc1 = 0;
            frameGc2 = 0;
            frameActive = false;
            framesOver16Ms = 0;
            framesOver33Ms = 0;
            framesOver50Ms = 0;
            framesOver100Ms = 0;
            signalListenerCalls = 0;
            signalListenerMethodsInstalled = 0;
            lifecycleMethodsInstalled = 0;
            listenerScans = 0;
            listenersObserved = 0;
            listenerPatchSkips = 0;
            patchFailures = 0;
            listenerScanTicks = 0;
            needsFinalListenerScan = false;
            failureLogged = false;
            IsFailOpen = false;
        }

        private static int CurrentSpeed() {
            int speed = (int) G.ClockSpeed;
            return speed >= 0 && speed < 5 ? speed : 0;
        }

        private static void FailOpen(Exception ex) {
            IsFailOpen = true;
            scopeDepth = 0;
            frameActive = false;
            TryLogError("Optimization Mod whole-frame profiler failed open; "
                + "all original callbacks continue normally: " + ex,
                suppressFutureMessages: true);
        }

        private static void TryLogError(
                string message,
                bool suppressFutureMessages) {
            if (suppressFutureMessages && failureLogged) { return; }
            if (suppressFutureMessages) { failureLogged = true; }
            try {
                D.Err(message);
            } catch {
            }
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000d / Stopwatch.Frequency;
        }

        private static double ToSeconds(long ticks) {
            return ticks / (double) Stopwatch.Frequency;
        }

        private readonly struct TargetKey : IEquatable<TargetKey> {
            private readonly string category;
            private readonly string context;
            private readonly MethodBase method;

            internal TargetKey(
                    string category,
                    string context,
                    MethodBase method) {
                this.category = category;
                this.context = context;
                this.method = method;
            }

            public bool Equals(TargetKey other) {
                return category == other.category && context == other.context
                    && Equals(method, other.method);
            }

            public override bool Equals(object obj) {
                return obj is TargetKey other && Equals(other);
            }

            public override int GetHashCode() {
                unchecked {
                    int hash = category?.GetHashCode() ?? 0;
                    hash = hash * 397 ^ (context?.GetHashCode() ?? 0);
                    hash = hash * 397 ^ (method?.GetHashCode() ?? 0);
                    return hash;
                }
            }
        }

        private struct ScopeEntry {
            internal TargetStats Target;
            internal long OuterStartedAt;
            internal long TargetStartedAt;
            internal long ChildSpanTicks;
        }

        private sealed class TargetStats {
            private readonly TimingStats[] bySpeed = {
                new TimingStats(), new TimingStats(), new TimingStats(),
                new TimingStats(), new TimingStats()
            };

            internal readonly string Category;
            internal readonly string Context;
            internal readonly MethodBase Method;
            internal readonly string AssemblyName;
            internal readonly bool IsFrameRoot;
            internal readonly string DisplayLabel;

            internal TargetStats(
                    string category,
                    string context,
                    MethodBase method,
                    string assembly,
                    bool isFrameRoot) {
                Category = category;
                Context = context;
                Method = method;
                AssemblyName = assembly;
                IsFrameRoot = isFrameRoot;
                string methodName = method?.DeclaringType == null
                    ? string.Empty
                    : method.DeclaringType.FullName + "." + method.Name;
                DisplayLabel = string.IsNullOrEmpty(context)
                    ? category + "/" + methodName
                    : category + "/" + context + " -> " + methodName;
            }

            internal long Calls => bySpeed.Sum(value => value.Calls);
            internal long InclusiveTotal =>
                bySpeed.Sum(value => value.Inclusive);
            internal long ExclusiveTotal =>
                bySpeed.Sum(value => value.Exclusive);

            internal void Record(
                    int speed,
                    long inclusive,
                    long exclusive) {
                TimingStats stats = bySpeed[speed];
                stats.Calls++;
                stats.Inclusive += inclusive;
                stats.Exclusive += exclusive;
                if (inclusive > stats.MaxInclusive) {
                    stats.MaxInclusive = inclusive;
                }
                if (inclusive >= FourMsTicks) { stats.OverFourMs++; }
                if (inclusive >= EightMsTicks) { stats.OverEightMs++; }
                if (inclusive >= SixteenMsTicks) { stats.OverSixteenMs++; }
                if (inclusive >= ThirtyThreeMsTicks) {
                    stats.OverThirtyThreeMs++;
                }
            }

            internal void Log(int rank) {
                long calls = Calls;
                long inclusive = InclusiveTotal;
                long exclusive = ExclusiveTotal;
                long max = bySpeed.Max(value => value.MaxInclusive);
                long over4 = bySpeed.Sum(value => value.OverFourMs);
                long over8 = bySpeed.Sum(value => value.OverEightMs);
                long over16 = bySpeed.Sum(value => value.OverSixteenMs);
                long over33 = bySpeed.Sum(value => value.OverThirtyThreeMs);
                D.Warn($"Optimization Mod whole-frame target #{rank}: "
                    + $"{DisplayLabel} [{AssemblyName}], calls={calls}, "
                    + $"inclusive={ToMilliseconds(inclusive):0.###} ms total/"
                    + $"{(calls == 0 ? 0d : ToMilliseconds(inclusive) / calls):0.###} "
                    + $"ms avg/{ToMilliseconds(max):0.###} ms max, "
                    + $"exclusive={ToMilliseconds(exclusive):0.###} ms total, "
                    + $">=4/8/16/33 ms={over4}/{over8}/{over16}/{over33}.");
            }
        }

        private sealed class TimingStats {
            internal long Calls;
            internal long Inclusive;
            internal long Exclusive;
            internal long MaxInclusive;
            internal long OverFourMs;
            internal long OverEightMs;
            internal long OverSixteenMs;
            internal long OverThirtyThreeMs;
        }

        private sealed class FrameRecord {
            internal long Offset;
            internal long Frame;
            internal int Speed;
            internal long Interval;
            internal long MeasuredExclusive;
            internal int GC0;
            internal int GC1;
            internal int GC2;
            internal Contributor[] Contributors;
        }

        private sealed class Contributor {
            internal string Label;
            internal string Assembly;
            internal long Exclusive;
        }
    }
}
