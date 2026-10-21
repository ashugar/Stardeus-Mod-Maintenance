using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Components;
using Game.Constants;
using Game.Mods;
using Game.Systems.QuantumStorage.Relays;
using Game.Systems.UIUX;
using Game.Utils;
using HarmonyLib;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.QuantumStorage
{
    // Temporary observation only. The marker is local, optional and never packaged.
    // No observer is installed and no frame component exists in ordinary play.
    internal static class QuantumPanelDiagnostics
    {
        internal const string Marker = "PanelDiagnostics.enabled";
        internal const string Owner = "jleas.quantumstorage.panel-diagnostics";
        private static bool configured, enabled, spent, running, loaded;
        private static long armedAt, startedAt, lastFrame, lastFlush;
        private static int lines, startupLines, periodicBatches, faults;
        private static Harmony observer;
        private static QuantumPanelFrameSampler sampler;
        private static PanelDiagnosticRecorder recorder;
        private static MatStorageTab trackedTab;
        private static MatStorageComp trackedStorage;
        private static Lifetime trackedLife;
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static string[] outcomes;
        [ThreadStatic] private static bool[] observing;
        [ThreadStatic] private static MatStorageTab[] observedTabs;

        internal static bool Enabled => enabled && !spent;
        internal static long Now => Stopwatch.GetTimestamp();
        internal static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

        internal static void Configure()
        {
            if (configured) { return; }
            configured = true;
            try
            {
                enabled = ModRegistry.IsModType(typeof(QuantumStorageNetworkSys), out var mod) &&
                    File.Exists(mod.ModFilePath(Marker, false, false));
                if (!enabled) { return; }
                armedAt = Now;
                Guard("state=armed schema=1 capture_s=180 call_cap=2048 raw_cap=512 key_cap=4096 line_cap=96 warnings_are_diagnostic");
            }
            catch (Exception) { enabled = false; }
        }

        // Only trusted constant labels, hashes and safe numeric exception identities.
        // This boundary also protects guard acceptance from a diagnostic sink failure.
        internal static void Guard(string trustedPreformattedDetail)
        {
            try
            {
                // Reserve two startup records for the actual capture census/state.
                if (!Enabled || startupLines >= 8) { return; }
                startupLines++;
                Write(trustedPreformattedDetail, false);
            }
            catch (Exception) { }
        }

        internal static void GuardException(string fixedStage, Exception ex)
        {
            try { Guard("guard stage=" + fixedStage + " exception=" + ExceptionCode(ex)); }
            catch (Exception) { }
        }

        internal static string ExceptionCode(Exception exception)
        {
            string result = "";
            for (int i = 0; exception != null && i < 3; i++, exception = exception.InnerException)
            {
                if (i != 0) { result += "/"; }
                string kind = exception is TypeInitializationException ? "TypeInitializationException" :
                    exception is TypeLoadException ? "TypeLoadException" :
                    exception is FileNotFoundException ? "FileNotFoundException" :
                    exception is FileLoadException ? "FileLoadException" :
                    exception is BadImageFormatException ? "BadImageFormatException" :
                    exception is MissingMemberException ? "MissingMemberException" :
                    exception is TargetInvocationException ? "TargetInvocationException" :
                    exception is InvalidOperationException ? "InvalidOperationException" :
                    exception is ArgumentException ? "ArgumentException" :
                    exception is NotSupportedException ? "NotSupportedException" :
                    exception is IOException ? "IOException" : "OtherException";
                result += kind + ":" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture);
            }
            return result;
        }

        private static void Write(string text, bool terminal)
        {
            if (lines >= (terminal ? 96 : 92)) { return; }
            // Defense in depth for accidental diagnostic text, never sanitize a path
            // into an apparently complete report. Drop the entire offending record.
            if (text == null || text.Length > 1980 || text.IndexOf(":/", StringComparison.Ordinal) >= 0 ||
                text.IndexOf(":\\", StringComparison.Ordinal) >= 0 || text.IndexOf("\\\\", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("/Users/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("/home/", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
            { text = "record=omitted reason=unsafe_or_oversized"; }
            lines++;
            D.Warn("[QuantumPanelDiag/1] " + text);
        }

        internal static void Install()
        {
            if (!Enabled || observer != null) { return; }
            try
            {
                var target = AccessTools.DeclaredMethod(typeof(MatStorageTab), "BuildContentsList", Type.EmptyTypes);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var storage = typeof(MatStorageTab).GetField("storage", flags);
                var life = typeof(MatStorageTab).GetField("contentsLifetime", flags);
                if (target == null || target.IsStatic || target.ReturnType != typeof(void) ||
                    storage?.FieldType != typeof(MatStorageComp) || life?.FieldType != typeof(Lifetime))
                { Guard("observer=unavailable reason=target_schema"); return; }
                observer = new Harmony(Owner);
                observer.Patch(target,
                    new HarmonyMethod(typeof(QuantumPanelDiagnostics), nameof(Before)) { priority = HarmonyLib.Priority.First },
                    new HarmonyMethod(typeof(QuantumPanelDiagnostics), nameof(After)) { priority = HarmonyLib.Priority.First },
                    finalizer: new HarmonyMethod(typeof(QuantumPanelDiagnostics), nameof(Finally)) { priority = HarmonyLib.Priority.Last });
                var patches = Harmony.GetPatchInfo(target);
                ReportCensus(target, patches, "install");
            }
            catch (Exception ex)
            {
                try { observer?.UnpatchAll(Owner); } catch (Exception) { }
                observer = null;
                GuardException("observer_install", ex);
            }
        }

        private static void ReportCensus(MethodBase target, Patches patches, string phase)
        {
            try
            {
                string Counts(IEnumerable<Patch> entries) =>
                    entries.Count(p => p.owner == Owner) + "/" +
                    entries.Count(p => p.owner == QuantumStorageNetworkSys.HarmonyId) + "/" +
                    entries.Count(p => p.owner != Owner && p.owner != QuantumStorageNetworkSys.HarmonyId);
                int Rank(IEnumerable<Patch> entries, string name) => PatchProcessor.GetSortedPatchMethods(target, entries.ToArray())
                    .IndexOf(AccessTools.DeclaredMethod(typeof(QuantumPanelDiagnostics), name));
                string detail = "observer=installed phase=" + phase + " owner_counts_diag_quantum_other prefix=" + Counts(patches.Prefixes) +
                    " postfix=" + Counts(patches.Postfixes) + " finalizer=" + Counts(patches.Finalizers) +
                    " diag_sorted_rank_zero_based=" + Rank(patches.Prefixes, nameof(Before)) + "/" +
                    Rank(patches.Postfixes, nameof(After)) + "/" + Rank(patches.Finalizers, nameof(Finally)) +
                    " diag_priorities=800/800/0 timing=inclusive_hook_window order_coverage=native_unavailable";
                if (phase == "capture") { Write(detail, false); }
                else { Guard(detail); }
            }
            catch (Exception ex) { GuardException("observer_census", ex); }
        }

        internal static void AfterLoad()
        {
            if (!Enabled || observer == null || G.IsDemoMode) { return; }
            try
            {
                if (Seconds(Now - armedAt) > 600) { spent = true; return; }
                loaded = true;
                if (sampler != null) { return; }
                var go = new GameObject("QuantumPanelDiagnosticSampler");
                go.hideFlags = HideFlags.HideAndDontSave;
                sampler = go.AddComponent<QuantumPanelFrameSampler>();
            }
            catch (Exception ex) { Fault("sampler_start", ex); }
        }

        internal static void Tick()
        {
            long probeStart = Now;
            try
            {
                if (!Enabled) { DestroySampler(); return; }
                long now = Now;
                if (!running)
                {
                    if (!loaded || G.IsDemoMode || !G.HasEnteredGame) { return; }
                    if (Seconds(now - armedAt) > 600) { Stop("arm_expired"); return; }
                    recorder = new PanelDiagnosticRecorder();
                    running = true;
                    startedAt = lastFrame = lastFlush = now;
                    var target = AccessTools.DeclaredMethod(typeof(MatStorageTab), "BuildContentsList", Type.EmptyTypes);
                    ReportCensus(target, Harmony.GetPatchInfo(target), "capture");
                    Write("state=capturing mod=" + typeof(QuantumPanelDiagnostics).Assembly.GetName().Version +
                        " duration_s=180 timing=application_frame_intervals", false);
                    recorder.AddProbe(Milliseconds(Now - probeStart), 0);
                    return;
                }
                int panelState = trackedTab == null ? 0 : trackedLife == null ? 0 : trackedLife.IsDisposed ? 2 : 1;
                recorder.Frame(Milliseconds(now - lastFrame), panelState);
                lastFrame = now;
                recorder.AddProbe(Milliseconds(Now - probeStart), Seconds(now - startedAt));
                if (Seconds(now - startedAt) >= 180) { Stop("time_cap"); return; }
                if (Seconds(now - lastFlush) >= 5 && periodicBatches < 36)
                { lastFlush = now; periodicBatches++; Flush(false, "periodic"); }
            }
            catch (Exception ex) { Fault("sampler_tick", ex); }
        }

        private static bool Managed(MatStorageComp storage) => storage != null &&
            (QuantumStorageNetworkSys.TryGetNode(storage, out _) || StorageRelaySys.IsRelay(storage));

        internal struct Observation
        {
            internal bool Active, Completed, Entered;
            internal long Start;
            internal int Level, Frame;
        }

        // No raw or sorted contents are read before this window.
        private static void Before(MatStorageTab __instance, MatStorageComp ___storage, out Observation __state)
        {
            __state = default;
            if (!running || recorder == null || (recorder.Calls >= 2048 && depth == 0)) { return; }
            long setup = Now;
            try
            {
                if (!Managed(___storage)) { return; }
                depth++;
                __state = new Observation { Entered = true, Level = depth };
                if (depth > 4 || recorder.Calls >= 2048) { recorder.DepthSkipped++; return; }
                if (outcomes == null) { outcomes = new string[4]; observing = new bool[4]; observedTabs = new MatStorageTab[4]; }
                outcomes[depth - 1] = null;
                observing[depth - 1] = true;
                observedTabs[depth - 1] = __instance;
                __state.Active = true;
                __state.Frame = Time.frameCount;
                recorder.AddProbe(Milliseconds(Now - setup), Seconds(Now - startedAt));
                __state.Start = Now;
            }
            catch (Exception ex) { Fault("observer_before", ex); }
        }

        private static void After(MatStorageTab __instance, MatStorageComp ___storage,
            Lifetime ___contentsLifetime, bool __runOriginal, ref Observation __state)
        {
            if (!__state.Active || __state.Completed) { return; }
            long end = Now;
            __state.Completed = true;
            try
            {
                if (!running || recorder == null) { return; }
                recorder.Call(Milliseconds(end - __state.Start), __runOriginal, outcomes[__state.Level - 1], __state.Frame);
                bool switched = !ReferenceEquals(trackedTab, __instance) || !ReferenceEquals(trackedStorage, ___storage);
                if (switched) { recorder.Switch(); }
                trackedTab = __instance;
                trackedStorage = ___storage;
                trackedLife = ___contentsLifetime;
                if (__state.Level == 1) { recorder.Raw(___storage.Contents); }
                else { recorder.NestedSkipped++; }
                recorder.AddProbe(Milliseconds(Now - end), Seconds(Now - startedAt));
            }
            catch (Exception ex) { Fault("observer_after", ex); }
        }

        private static Exception Finally(Exception __exception, bool __runOriginal, ref Observation __state)
        {
            if (!__state.Entered) { return __exception; }
            long begin = Now;
            try
            {
                if (__state.Active && !__state.Completed && running && recorder != null)
                {
                    __state.Completed = true;
                    recorder.Call(Milliseconds(begin - __state.Start), __runOriginal, "exception", __state.Frame);
                    recorder.Gap();
                }
                if (__state.Level <= 4 && observing != null)
                { observing[__state.Level - 1] = false; outcomes[__state.Level - 1] = null; observedTabs[__state.Level - 1] = null; }
                depth = Math.Max(0, depth - 1);
                if (running && recorder != null) { recorder.AddProbe(Milliseconds(Now - begin), Seconds(Now - startedAt)); }
            }
            catch (Exception ex) { Fault("observer_finalizer", ex); }
            return __exception;
        }

        internal static void Outcome(MatStorageTab tab, string fixedReason)
        {
            if (!running || depth == 0 || depth > 4 || observing == null || !observing[depth - 1] ||
                !ReferenceEquals(tab, observedTabs[depth - 1])) { return; }
            outcomes[depth - 1] = fixedReason;
            if (recorder != null && (fixedReason == "capture_success" || fixedReason == "captured")) { recorder.Captures++; }
        }

        internal static void Notify(MatStorageComp storage)
        {
            if (running && recorder != null && ReferenceEquals(storage, trackedStorage)) { recorder.SelectedNotifications++; }
        }
        internal static void Synchronize()
        { if (running && recorder != null) { recorder.NetworkSynchronizations++; } }

        private static void Flush(bool terminal, string reason)
        {
            if (recorder == null) { return; }
            long begin = Now;
            try
            {
                Write(recorder.Summary(Seconds(begin - startedAt), reason), terminal);
                Write(recorder.Changes(), terminal);
                recorder.AddProbe(Milliseconds(Now - begin), Seconds(Now - startedAt));
            }
            catch (Exception ex) { Fault("flush", ex); }
        }

        private static void Fault(string stage, Exception ex)
        {
            running = false;
            spent = true;
            try { if (faults++ < 2) { Write("state=fault stage=" + stage + " exception=" + ExceptionCode(ex), true); } }
            catch (Exception) { }
            Release();
        }
        private static void Stop(string reason)
        {
            running = false;
            spent = true;
            try { Flush(true, reason); } catch (Exception) { }
            Release();
            DestroySampler();
        }
        private static void Release()
        {
            recorder?.Clear(); recorder = null;
            trackedTab = null; trackedStorage = null; trackedLife = null;
        }
        private static void DestroySampler()
        {
            try { if (sampler != null) { UnityEngine.Object.Destroy(sampler.gameObject); sampler = null; } }
            catch (Exception) { sampler = null; }
        }
        internal static void SamplerDestroyed(QuantumPanelFrameSampler component)
        {
            try
            {
                if (!ReferenceEquals(sampler, component)) { return; }
                sampler = null;
                if (running) { Stop("sampler_destroyed"); }
                else { Release(); }
            }
            catch (Exception ex) { Fault("sampler_destroyed", ex); }
        }
        internal static void Unload()
        {
            try
            {
                if (running) { Stop("unload"); }
                else { Release(); DestroySampler(); }
                loaded = false;
                observer?.UnpatchAll(Owner); observer = null;
            }
            catch (Exception) { }
        }
    }

    // Created only after local opt-in and actual save loading. Never serialized.
    public sealed class QuantumPanelFrameSampler : MonoBehaviour
    {
        private void Update() { QuantumPanelDiagnostics.Tick(); }
        private void OnDestroy() { QuantumPanelDiagnostics.SamplerDestroyed(this); }
    }

    // Pure, independently testable recorder: no Unity, IO, logging or inventory writes.
    internal sealed class PanelDiagnosticRecorder
    {
        private sealed class Identity : IEqualityComparer<MatType>
        {
            public bool Equals(MatType x, MatType y) => ReferenceEquals(x, y);
            public int GetHashCode(MatType obj) => RuntimeHelpers.GetHashCode(obj);
        }
        private Dictionary<MatType, int> previous = new Dictionary<MatType, int>(4096, new Identity());
        private Dictionary<MatType, int> current = new Dictionary<MatType, int>(4096, new Identity());
        private bool baseline;
        internal bool Detailed = true;
        internal int Calls, Native, Skipped, Reused, Refused, Captures, Exceptions, Switches;
        internal int RawSamples, Compared, RawSkipped, Added, Removed, Changed, Identical, QuantityOnly, Membership;
        internal int DepthSkipped, NestedSkipped, Frames, OpenFrames, ClosedFrames, UnknownFrames, SameFrameCalls;
        internal int SelectedNotifications, NetworkSynchronizations, Work;
        internal long ProbeSamples;
        internal double CallMs, CallMaxMs, ProbeMs, ProbeMaxMs, FrameMs, FrameMaxMs;
        internal double OpenMs, ClosedMs, UnknownMs;
        private readonly int[] frameBuckets = new int[6];
        private int lastCallFrame = -1;
        internal string DetailEnd = "active";

        internal void Frame(double ms, int state)
        {
            if (double.IsNaN(ms) || ms < 0 || double.IsInfinity(ms)) { return; }
            Frames++; FrameMs += ms; FrameMaxMs = Math.Max(FrameMaxMs, ms);
            if (state == 1) { OpenFrames++; OpenMs += ms; }
            else if (state == 2) { ClosedFrames++; ClosedMs += ms; }
            else { UnknownFrames++; UnknownMs += ms; }
            int bucket = ms <= 16.667 ? 0 : ms <= 25 ? 1 : ms <= 33.334 ? 2 : ms <= 50 ? 3 : ms <= 100 ? 4 : 5;
            frameBuckets[bucket]++;
        }
        internal void Call(double ms, bool native, string reason, int frame)
        {
            if (Calls >= 2048) { return; }
            Calls++; CallMs += ms; CallMaxMs = Math.Max(CallMaxMs, ms);
            if (native) { Native++; } else { Skipped++; }
            if (reason == "reuse" || reason == "reused") { Reused++; }
            else if (reason == "exception") { Exceptions++; }
            else if (reason != null && reason != "capture_success" && reason != "captured") { Refused++; }
            if (frame == lastCallFrame) { SameFrameCalls++; } lastCallFrame = frame;
        }
        internal void AddProbe(double ms, double elapsedSeconds)
        {
            ProbeSamples++; ProbeMs += ms; ProbeMaxMs = Math.Max(ProbeMaxMs, ms);
            if (Detailed && (ms > 2 || (elapsedSeconds >= 2 && ProbeMs > elapsedSeconds * 10)))
            { Retire(ms > 2 ? "probe_2ms" : "probe_1pct"); }
        }
        internal void Switch() { Switches++; Gap(); }
        internal void Gap() { baseline = false; previous.Clear(); current.Clear(); }
        private void Retire(string reason) { Detailed = false; DetailEnd = reason; Gap(); }
        internal void Raw(Dictionary<MatType, int> contents)
        {
            if (!Detailed) { RawSkipped++; return; }
            if (RawSamples >= 512) { Retire("sample_cap"); RawSkipped++; return; }
            int cost = (contents?.Count ?? 0) + (baseline ? (contents?.Count ?? 0) + previous.Count : 0);
            if (contents == null || contents.Count > 4096 || cost > 262144 - Work)
            { RawSkipped++; Gap(); if (cost > 262144 - Work) { Retire("work_cap"); } return; }
            current.Clear();
            foreach (var item in contents) { current.Add(item.Key, item.Value); Work++; }
            RawSamples++;
            if (baseline)
            {
                int added = 0, removed = 0, changed = 0;
                foreach (var item in current)
                {
                    Work++;
                    if (!previous.TryGetValue(item.Key, out var count)) { added++; }
                    else if (count != item.Value) { changed++; }
                }
                foreach (var item in previous) { Work++; if (!current.ContainsKey(item.Key)) { removed++; } }
                Compared++; Added += added; Removed += removed; Changed += changed;
                if (added != 0 || removed != 0) { Membership++; }
                else if (changed != 0) { QuantityOnly++; } else { Identical++; }
            }
            var swap = previous; previous = current; current = swap;
            baseline = true;
        }
        private static string N(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
        internal string Summary(double elapsed, string reason) =>
            "state=" + reason + " elapsed_s=" + N(elapsed) + " frames=" + Frames + " frame_ms=" + N(FrameMs) +
            " frame_max_ms=" + N(FrameMaxMs) + " frame_bins_16_25_33_50_100_over=" + string.Join(",", frameBuckets) +
            " open_frames=" + OpenFrames + " open_ms=" + N(OpenMs) + " closed_frames=" + ClosedFrames + " closed_ms=" + N(ClosedMs) +
            " unknown_frames=" + UnknownFrames + " unknown_ms=" + N(UnknownMs) + " calls=" + Calls + " native=" + Native +
            " skipped=" + Skipped + " reused=" + Reused + " refused=" + Refused + " captures=" + Captures + " exceptions=" + Exceptions +
            " call_ms=" + N(CallMs) + " call_max_ms=" + N(CallMaxMs) + " same_frame_calls=" + SameFrameCalls + " call_cap=2048";
        internal string Changes() =>
            "raw_samples=" + RawSamples + " compared=" + Compared + " added_keys=" + Added + " removed_keys=" + Removed +
            " quantity_changes=" + Changed + " membership_intervals=" + Membership + " quantity_only=" + QuantityOnly + " identical_raw=" + Identical +
            " switches=" + Switches + " raw_skipped=" + RawSkipped + " examined=" + Work + " detailed=" + Detailed + " detail_end=" + DetailEnd +
            " selected_notify=" + SelectedNotifications + " network_sync=" + NetworkSynchronizations + " nested_skipped=" + NestedSkipped +
            " depth_skipped=" + DepthSkipped + " probe_samples=" + ProbeSamples + " probe_ms=" + N(ProbeMs) + " probe_max_ms=" + N(ProbeMaxMs) +
            " native_order=unavailable timing=inclusive_not_exclusive allocation=two_bounded_dictionary_buffers";
        internal void Clear() { previous?.Clear(); current?.Clear(); previous = null; current = null; }
    }
}
