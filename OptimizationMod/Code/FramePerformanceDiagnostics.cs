using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Game.CodeGen;
using Game.Constants;
using HarmonyLib;
using KL.Utils;

namespace Game.Systems.OptimizationMod {
    /// <summary>
    /// Temporary aggregate-only timing around the camera, map preparation, and
    /// renderer-update boundaries. CameraControls runs at Unity execution order
    /// -10000, so the interval from its Update prefix to RenderingSys.BeforeRender
    /// is a useful main-thread pre-render frame measurement without patching any
    /// being, object, tile, or renderer item loop.
    /// </summary>
    internal static class FramePerformanceDiagnostics {
        internal const int ExpectedProbeCount = 12;

        private const int MaxRetainedFrames = 12;
        private static readonly long SixteenMsTicks =
            (long) (Stopwatch.Frequency * 0.016d);
        private static readonly long ThirtyThreeMsTicks =
            (long) (Stopwatch.Frequency * 0.033d);
        private static readonly long FiftyMsTicks =
            (long) (Stopwatch.Frequency * 0.050d);
        private static readonly long HundredMsTicks =
            (long) (Stopwatch.Frequency * 0.100d);
        private static readonly Dictionary<MethodBase, TargetStats> Targets =
            new Dictionary<MethodBase, TargetStats>();
        private static readonly TargetStats PreRenderFrame =
            new TargetStats("Frame.PreRenderCPU");
        private static readonly TargetStats FrameInterval =
            new TargetStats("Frame.UpdateInterval");
        private static readonly List<FrameRecord> WorstFrames =
            new List<FrameRecord>(MaxRetainedFrames);
        private static readonly List<FrameRecord> WorstFrameIntervals =
            new List<FrameRecord>(MaxRetainedFrames);

        private static long sessionStartedAt;
        private static long frameOrdinal;
        private static long activeFrameOrdinal;
        private static long completedFrameOrdinal;
        private static long completedIntervalOrdinal;
        private static long frameStartedAt;
        private static long preRenderElapsed;
        private static int frameSpeed;
        private static bool cameraMovedThisFrame;
        private static bool frameCaptureActive;
        private static int frameGc0;
        private static int frameGc1;
        private static int frameGc2;
        private static long framesOver16Ms;
        private static long framesOver33Ms;
        private static long framesOver50Ms;
        private static long framesOver100Ms;
        private static long framesWithGc;
        private static long intervalsOver16Ms;
        private static long intervalsOver33Ms;
        private static long intervalsOver50Ms;
        private static long intervalsOver100Ms;
        private static long intervalsWithGc;

        internal static int Install(Harmony harmony) {
            Reset();
            sessionStartedAt = Stopwatch.GetTimestamp();
            int installed = 0;
            installed += Install(harmony, "Game.Input.CameraControls", "Update",
                "Camera.Update", nameof(BeginFrameAndTiming));
            installed += Install(harmony, "Game.Input.CameraControls", "LateUpdate",
                "Camera.LateUpdate", nameof(BeginTiming));
            installed += Install(harmony, "Game.Input.CameraControls", "HandleMotion",
                "Camera.HandleMotion", nameof(BeginTiming));
            installed += Install(harmony, "Game.Input.CameraControls",
                "MoveWithChildren",
                "Camera.MoveWithChildren", nameof(BeginMotionTiming));
            installed += Install(harmony, "Game.Input.CameraControls",
                "ZoomWithChildren",
                "Camera.ZoomWithChildren", nameof(BeginMotionTiming));
            installed += Install(harmony, "Game.Input.CameraControls", "NotifyMotion",
                "Camera.NotifyMotion", nameof(BeginTiming));
            installed += Install(harmony, "Game.Systems.RenderingSys", "BeforeRender",
                "Rendering.BeforeRender", nameof(BeginBeforeRender));
            installed += Install(harmony, "Game.Systems.RenderingSys",
                "BeforeRenderCamera", "Rendering.BeforeRenderCamera",
                nameof(BeginTiming));
            installed += Install(harmony, "Game.Systems.RenderingSys",
                "OnAfterLateUpdate", "Rendering.RendererUpdates",
                nameof(BeginTiming));
            installed += Install(harmony, "Game.Visuals.MapRenderer", "Update",
                "MapRenderer.Update", nameof(BeginTiming));
            installed += Install(harmony, "Game.Visuals.MapRenderer", "LateUpdate",
                "MapRenderer.LateUpdate", nameof(BeginTiming));
            installed += Install(harmony, "Game.Visuals.MapRenderer", "ApplyAllChanges",
                "MapRenderer.ApplyAllChanges", nameof(BeginTiming));
            return installed;
        }

        private static int Install(
                Harmony harmony,
                string targetTypeName,
                string targetName,
                string label,
                string prefixName) {
            try {
                Type targetType = AccessTools.TypeByName(targetTypeName);
                MethodInfo original = AccessTools.DeclaredMethod(
                    targetType, targetName);
                MethodInfo prefix = AccessTools.DeclaredMethod(
                    typeof(FramePerformanceDiagnostics), prefixName);
                MethodInfo postfix = AccessTools.DeclaredMethod(
                    typeof(FramePerformanceDiagnostics), nameof(EndTiming));
                if (original == null || prefix == null || postfix == null) {
                    D.Err("Optimization Mod frame diagnostic target is "
                        + "unavailable: {0}.{1}.", targetTypeName,
                        targetName);
                    return 0;
                }
                Targets[original] = new TargetStats(label);
                harmony.Patch(original,
                    prefix: new HarmonyMethod(prefix) {
                        priority = HarmonyLib.Priority.First
                    },
                    postfix: new HarmonyMethod(postfix) {
                        priority = HarmonyLib.Priority.Last
                    });
                return 1;
            } catch (Exception ex) {
                D.Err("Optimization Mod failed to install frame diagnostic "
                    + "for {0}.{1}: {2}", targetTypeName, targetName, ex);
                return 0;
            }
        }

        private static void BeginFrameAndTiming(out long __state) {
            long now = Stopwatch.GetTimestamp();
            if (!Ready.Game) {
                frameCaptureActive = false;
                frameStartedAt = 0;
                preRenderElapsed = 0;
                __state = now;
                return;
            }
            if (frameCaptureActive) {
                CompleteFrameInterval(now);
            }
            frameCaptureActive = true;
            frameOrdinal++;
            activeFrameOrdinal = frameOrdinal;
            frameStartedAt = now;
            preRenderElapsed = 0;
            frameSpeed = CurrentSpeed();
            cameraMovedThisFrame = false;
            frameGc0 = GC.CollectionCount(0);
            frameGc1 = GC.CollectionCount(1);
            frameGc2 = GC.CollectionCount(2);
            __state = now;
        }

        private static void BeginTiming(out long __state) {
            __state = Stopwatch.GetTimestamp();
        }

        private static void BeginMotionTiming(out long __state) {
            cameraMovedThisFrame = true;
            __state = Stopwatch.GetTimestamp();
        }

        private static void BeginBeforeRender(out long __state) {
            long now = Stopwatch.GetTimestamp();
            CompletePreRenderFrame(now);
            __state = now;
        }

        private static void EndTiming(long __state, MethodBase __originalMethod) {
            if (!frameCaptureActive || __state <= 0 || __originalMethod == null
                    || !Targets.TryGetValue(__originalMethod,
                        out TargetStats stats)) {
                return;
            }
            stats.Record(CurrentSpeed(), cameraMovedThisFrame,
                Stopwatch.GetTimestamp() - __state);
        }

        private static void CompletePreRenderFrame(long now) {
            if (frameStartedAt <= 0 || activeFrameOrdinal <= 0
                    || completedFrameOrdinal == activeFrameOrdinal) {
                return;
            }
            completedFrameOrdinal = activeFrameOrdinal;
            long elapsed = now - frameStartedAt;
            preRenderElapsed = elapsed;
            int gc0 = GC.CollectionCount(0) - frameGc0;
            int gc1 = GC.CollectionCount(1) - frameGc1;
            int gc2 = GC.CollectionCount(2) - frameGc2;
            PreRenderFrame.Record(frameSpeed, cameraMovedThisFrame, elapsed);
            if (elapsed >= SixteenMsTicks) { framesOver16Ms++; }
            if (elapsed >= ThirtyThreeMsTicks) { framesOver33Ms++; }
            if (elapsed >= FiftyMsTicks) { framesOver50Ms++; }
            if (elapsed >= HundredMsTicks) { framesOver100Ms++; }
            if (gc0 > 0 || gc1 > 0 || gc2 > 0) { framesWithGc++; }
            if (elapsed >= ThirtyThreeMsTicks) {
                RetainFrame(WorstFrames, new FrameRecord {
                    Elapsed = elapsed,
                    Offset = now - sessionStartedAt,
                    Ordinal = activeFrameOrdinal,
                    Speed = frameSpeed,
                    Moving = cameraMovedThisFrame,
                    GC0 = gc0,
                    GC1 = gc1,
                    GC2 = gc2
                });
            }
        }

        private static void CompleteFrameInterval(long now) {
            if (frameStartedAt <= 0 || activeFrameOrdinal <= 0
                    || completedIntervalOrdinal == activeFrameOrdinal) {
                return;
            }
            completedIntervalOrdinal = activeFrameOrdinal;
            long elapsed = now - frameStartedAt;
            int gc0 = GC.CollectionCount(0) - frameGc0;
            int gc1 = GC.CollectionCount(1) - frameGc1;
            int gc2 = GC.CollectionCount(2) - frameGc2;
            FrameInterval.Record(frameSpeed, cameraMovedThisFrame, elapsed);
            if (elapsed >= SixteenMsTicks) { intervalsOver16Ms++; }
            if (elapsed >= ThirtyThreeMsTicks) { intervalsOver33Ms++; }
            if (elapsed >= FiftyMsTicks) { intervalsOver50Ms++; }
            if (elapsed >= HundredMsTicks) { intervalsOver100Ms++; }
            if (gc0 > 0 || gc1 > 0 || gc2 > 0) { intervalsWithGc++; }
            if (elapsed >= ThirtyThreeMsTicks) {
                RetainFrame(WorstFrameIntervals, new FrameRecord {
                    Elapsed = elapsed,
                    PreRenderElapsed = preRenderElapsed,
                    Offset = now - sessionStartedAt,
                    Ordinal = activeFrameOrdinal,
                    Speed = frameSpeed,
                    Moving = cameraMovedThisFrame,
                    GC0 = gc0,
                    GC1 = gc1,
                    GC2 = gc2
                });
            }
        }

        private static void RetainFrame(
                List<FrameRecord> records,
                FrameRecord record) {
            int insertAt = 0;
            while (insertAt < records.Count
                    && records[insertAt].Elapsed >= record.Elapsed) {
                insertAt++;
            }
            if (insertAt >= MaxRetainedFrames) { return; }
            records.Insert(insertAt, record);
            if (records.Count > MaxRetainedFrames) {
                records.RemoveAt(records.Count - 1);
            }
        }

        internal static void LogSummary() {
            PreRenderFrame.Log();
            D.Warn("Optimization Mod pre-render frame thresholds: >=16 ms={0}, "
                + ">=33 ms={1}, >=50 ms={2}, >=100 ms={3}, frames with "
                + "observed GC={4}.", framesOver16Ms, framesOver33Ms,
                framesOver50Ms, framesOver100Ms, framesWithGc);
            FrameInterval.Log();
            D.Warn("Optimization Mod update-interval frame thresholds: "
                + ">=16 ms={0}, >=33 ms={1}, >=50 ms={2}, >=100 ms={3}, "
                + "frames with observed GC={4}.", intervalsOver16Ms,
                intervalsOver33Ms, intervalsOver50Ms, intervalsOver100Ms,
                intervalsWithGc);
            foreach (TargetStats stats in Targets.Values) {
                stats.Log();
            }
            for (int i = 0; i < WorstFrames.Count; i++) {
                FrameRecord r = WorstFrames[i];
                D.Warn($"Optimization Mod pre-render frame spike #{i + 1}: "
                    + $"+{ToSeconds(r.Offset):0.000}s, frame={r.Ordinal}, "
                    + $"speed={(ClockSpeed) r.Speed}, moving={r.Moving}, "
                    + $"{ToMilliseconds(r.Elapsed):0.###} ms, GC delta="
                    + $"{r.GC0}/{r.GC1}/{r.GC2}.");
            }
            for (int i = 0; i < WorstFrameIntervals.Count; i++) {
                FrameRecord r = WorstFrameIntervals[i];
                D.Warn($"Optimization Mod update-interval frame spike "
                    + $"#{i + 1}: +{ToSeconds(r.Offset):0.000}s, "
                    + $"frame={r.Ordinal}, speed={(ClockSpeed) r.Speed}, "
                    + $"moving={r.Moving}, interval="
                    + $"{ToMilliseconds(r.Elapsed):0.###} ms, pre-render CPU="
                    + $"{ToMilliseconds(r.PreRenderElapsed):0.###} ms, "
                    + $"GC delta={r.GC0}/{r.GC1}/{r.GC2}.");
            }
        }

        internal static void Reset() {
            Targets.Clear();
            PreRenderFrame.Reset();
            FrameInterval.Reset();
            WorstFrames.Clear();
            WorstFrameIntervals.Clear();
            sessionStartedAt = 0;
            frameOrdinal = 0;
            activeFrameOrdinal = 0;
            completedFrameOrdinal = 0;
            completedIntervalOrdinal = 0;
            frameStartedAt = 0;
            preRenderElapsed = 0;
            frameSpeed = 0;
            cameraMovedThisFrame = false;
            frameCaptureActive = false;
            frameGc0 = 0;
            frameGc1 = 0;
            frameGc2 = 0;
            framesOver16Ms = 0;
            framesOver33Ms = 0;
            framesOver50Ms = 0;
            framesOver100Ms = 0;
            framesWithGc = 0;
            intervalsOver16Ms = 0;
            intervalsOver33Ms = 0;
            intervalsOver50Ms = 0;
            intervalsOver100Ms = 0;
            intervalsWithGc = 0;
        }

        private static int CurrentSpeed() {
            int speed = (int) G.ClockSpeed;
            return speed >= 0 && speed < 5 ? speed : 0;
        }

        private sealed class TargetStats {
            private readonly string name;
            private readonly TimingStats[] moving = {
                new TimingStats(), new TimingStats(), new TimingStats(),
                new TimingStats(), new TimingStats()
            };
            private readonly TimingStats[] idle = {
                new TimingStats(), new TimingStats(), new TimingStats(),
                new TimingStats(), new TimingStats()
            };

            internal TargetStats(string name) {
                this.name = name;
            }

            internal void Record(int speed, bool moved, long elapsed) {
                TimingStats stats = moved ? moving[speed] : idle[speed];
                stats.Calls++;
                stats.Total += elapsed;
                if (elapsed > stats.Max) { stats.Max = elapsed; }
            }

            internal void Log() {
                long calls = 0;
                long total = 0;
                long max = 0;
                var parts = new List<string>();
                for (int speed = 0; speed < 5; speed++) {
                    Append(parts, moving[speed], speed, true,
                        ref calls, ref total, ref max);
                    Append(parts, idle[speed], speed, false,
                        ref calls, ref total, ref max);
                }
                if (calls == 0) { return; }
                double totalMs = ToMilliseconds(total);
                D.Warn("Optimization Mod frame diagnostic {0}: {1} calls, "
                    + "{2:0.###} ms total, {3:0.###} ms avg, "
                    + "{4:0.###} ms max; {5}", name, calls, totalMs,
                    totalMs / calls, ToMilliseconds(max),
                    string.Join(" | ", parts));
            }

            private static void Append(
                    List<string> parts,
                    TimingStats stats,
                    int speed,
                    bool moved,
                    ref long calls,
                    ref long total,
                    ref long max) {
                if (stats.Calls == 0) { return; }
                calls += stats.Calls;
                total += stats.Total;
                if (stats.Max > max) { max = stats.Max; }
                double ms = ToMilliseconds(stats.Total);
                parts.Add($"{(ClockSpeed) speed}/{(moved ? "moving" : "idle")}: "
                    + $"{stats.Calls} calls, {ms / stats.Calls:0.###} ms avg, "
                    + $"{ToMilliseconds(stats.Max):0.###} ms max");
            }

            internal void Reset() {
                for (int i = 0; i < 5; i++) {
                    moving[i].Reset();
                    idle[i].Reset();
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

        private sealed class FrameRecord {
            internal long Elapsed;
            internal long PreRenderElapsed;
            internal long Offset;
            internal long Ordinal;
            internal int Speed;
            internal bool Moving;
            internal int GC0;
            internal int GC1;
            internal int GC2;
        }

        private static double ToMilliseconds(long ticks) {
            return ticks * 1000d / Stopwatch.Frequency;
        }

        private static double ToSeconds(long ticks) {
            return ticks / (double) Stopwatch.Frequency;
        }
    }
}
