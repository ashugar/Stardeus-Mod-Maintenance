#if QUANTUM_RELAY_QUERY_BASELINE
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Game.Constants;
using KL.Utils;
using UnityEngine;

namespace Game.Systems.QuantumStorage
{
    internal static class RelayQueryBaseline
    {
        private static RelayQueryRecorder recorder;
        private static RelayQueryBaselineTicker ticker;
        private static bool configured, loaded, captureStarted, enabled, samplingEnabled;
        private static int stages, ownerThread;
        private static long loadAnchor;
        private static RelayFrameTiming frameTiming;
        [ThreadStatic] private static bool sampleActive, detailActive;
        [ThreadStatic] private static long[] detailTicks, detailCounts;
        private static long focusedOrdinal;
        [ThreadStatic] private static long sampleId, topologyTicks, topologyCalls;
        [ThreadStatic] private static int topologyDepth;
        [ThreadStatic] private static long coldScopes, touchedScopes, activatedScopes, invalidScopes;
        [ThreadStatic] private static long registryChecks, advisoryScopes, advisoryRefreshes, advisoryCacheHits;
        [ThreadStatic] private static long candidateModes, revalidationModes;
        [ThreadStatic] private static long currentWorldModes, decisionModes;
        internal static bool SamplingQuery => sampleActive;

        internal struct QuerySample { internal long Id, Start; internal int Phase, Epoch; internal bool Detail, Caller; }
        internal struct DetailSample { internal bool Entered; internal int Bucket; internal long Start, Id; }
        internal struct TopologySample { internal bool Entered, Outer; internal long Id, Start; }

        internal static void Configure()
        {
            if (configured || captureStarted) return;
            try
            {
                var mod = The.ModLoader?.FindModById("QuantumStorage");
                if (mod == null || !File.Exists(mod.ModFilePath("RelayQueryBaseline.enabled", false, false))) return;
                enabled = true;
                recorder = new RelayQueryRecorder(Stopwatch.Frequency, line => D.Warn(line));
                configured = true;
                Stage("armed");
            }
            catch (Exception) { Stage("configure_failed"); Disable(); }
        }
        internal static void AfterLoad()
        {
            try
            {
                if (G.IsDemoMode || captureStarted) return;
                Configure();
                if (!configured || recorder == null || recorder.Spent) return;
                ownerThread = Thread.CurrentThread.ManagedThreadId;
                loaded = true; loadAnchor = Stopwatch.GetTimestamp();
                if (ticker == null) { var go = new GameObject("RelayQueryIsolationTicker"); go.hideFlags = HideFlags.HideAndDontSave; ticker = go.AddComponent<RelayQueryBaselineTicker>(); }
                TryStart();
            }
            catch (Exception) { Stage("load_failed"); Disable(); }
        }
        private static void TryStart()
        {
            if (!loaded || captureStarted || G.IsDemoMode || !G.HasEnteredGame || recorder == null || recorder.Spent) return;
            captureStarted = true;
            if (Stopwatch.GetTimestamp() - loadAnchor >= Stopwatch.Frequency * 120) Stage("expired_before_entry");
            RelayQueryFocus.Configure();
            if (detailTicks == null) { detailTicks = new long[14]; detailCounts = new long[14]; }
            frameTiming = new RelayFrameTiming();
            frameTiming.Start();
            recorder.BeforeReport = () =>
            {
                recorder.FrameEvidence = frameTiming?.Report() ?? "engine_timing=retired process_cpu=retired";
                recorder.CandidateReadView = Relays.NanobotCandidateQueries.Status;
            };
            recorder.Start(loadAnchor, true, Relays.RelayMaterialSources.Status, RelayMaterialQuery.FastCandidatesAvailable,
                Relays.NanobotCandidateQueries.Status);
        }
        internal static void Tick()
        {
            try
            {
                if (recorder == null || Thread.CurrentThread.ManagedThreadId != ownerThread) return;
                TryStart();
                frameTiming?.Frame();
                recorder.Frame(Stopwatch.GetTimestamp(), Application.isFocused);
                samplingEnabled = recorder.Sampling;
                if (recorder.Spent) Retire();
            }
            catch (Exception) { Disable(); }
        }
        internal static bool TryBeginQuery(out QuerySample sample)
        {
            sample = default;
            // The passive phase adds no query clock, lock, counter or array work.
            if (!samplingEnabled || sampleActive || Thread.CurrentThread.ManagedThreadId != ownerThread) return false;
            try
            {
                if (recorder == null || !recorder.SelectQuery()) return false;
                long now = Stopwatch.GetTimestamp();
                if (!recorder.CanStartSample(now)) return false;
                sampleActive = true; unchecked { sampleId++; }
                detailActive = recorder.SelectedDetail;
                if (detailActive)
                {
                    if (detailTicks == null) { detailTicks = new long[14]; detailCounts = new long[14]; }
                    Array.Clear(detailTicks, 0, 14); Array.Clear(detailCounts, 0, 14);
                }
                topologyDepth = 0; topologyTicks = topologyCalls = 0;
                coldScopes = touchedScopes = activatedScopes = invalidScopes = 0;
                registryChecks = advisoryScopes = advisoryRefreshes = advisoryCacheHits = 0;
                candidateModes = revalidationModes = 0;
                currentWorldModes = decisionModes = 0;
                sample = new QuerySample { Id = sampleId, Start = now, Phase = recorder.Phase, Epoch = recorder.Epoch, Detail = detailActive, Caller = recorder.SelectedCaller };
                if (detailActive) RelayQueryFocus.BeginQuery(focusedOrdinal++);
                return true;
            }
            catch (Exception) { Disable(); return false; }
        }
        internal static void EndQuery(QuerySample sample, bool failed)
        {
            if (!sampleActive || sample.Id != sampleId || Thread.CurrentThread.ManagedThreadId != ownerThread) return;
            bool trace = false;
            try
            {
                long now = Stopwatch.GetTimestamp();
                var focus = sample.Detail ? RelayQueryFocus.EndQuery() : default;
                if (topologyDepth == 0 && recorder != null)
                {
                    trace = sample.Caller && topologyTicks >= 0 && now >= sample.Start && topologyTicks <= now - sample.Start &&
                        recorder.SameSampleWindow(now, sample.Phase, sample.Epoch);
                    recorder.CompleteSample(now, sample.Phase, sample.Epoch, now - sample.Start, topologyTicks, topologyCalls, failed,
                        sample.Detail, detailTicks, detailCounts, sample.Caller, coldScopes, touchedScopes, activatedScopes, invalidScopes,
                        registryChecks, advisoryScopes, advisoryRefreshes, advisoryCacheHits, candidateModes, revalidationModes,
                        currentWorldModes, decisionModes, focus);
                }
                else recorder?.DiscardSample();
            }
            catch (Exception) { Disable(); }
            finally { ClearSample(); }
            // The query stop timestamp and cohort accounting precede stack work.
            // Caller failures cannot replace the original query result/exception.
            if (trace) { try { recorder?.CaptureCaller(); } catch (Exception) { } }
        }
        internal static void NoteReadScope(bool touched, bool activated, bool valid, bool advisory = false)
        {
            if (!sampleActive) return;
            if (touched) touchedScopes++; else coldScopes++;
            if (activated) activatedScopes++;
            if (!valid) invalidScopes++;
            if (advisory) advisoryScopes++;
        }
        internal static void NoteTopologyWalk() { if (sampleActive) registryChecks++; }
        internal static void NoteCandidateMode(bool candidatePresent, bool revalidation, bool currentWorld = false, bool decisionBound = false)
        {
            if (!sampleActive) return;
            if (candidatePresent) candidateModes++;
            if (revalidation) revalidationModes++;
            if (currentWorld) currentWorldModes++;
            if (decisionBound) decisionModes++;
        }
        internal static void NoteAdvisoryRefresh(bool cached)
        {
            if (!sampleActive) return;
            advisoryRefreshes++;
            if (cached) advisoryCacheHits++;
        }
        internal static TopologySample BeginTopology()
        {
            if (!sampleActive || Thread.CurrentThread.ManagedThreadId != ownerThread) return default;
            try
            {
                bool outer = topologyDepth++ == 0;
                var token = new TopologySample { Entered = true, Outer = outer, Id = sampleId };
                if (outer) { token.Start = Stopwatch.GetTimestamp(); topologyCalls++; }
                return token;
            }
            catch (Exception) { Disable(); return default; }
        }
        internal static void EndTopology(TopologySample sample)
        {
            if (!sample.Entered || !sampleActive || sample.Id != sampleId || Thread.CurrentThread.ManagedThreadId != ownerThread) return;
            try
            {
                if (topologyDepth > 0) topologyDepth--;
                if (sample.Outer)
                {
                    long elapsed = Stopwatch.GetTimestamp() - sample.Start;
                    if (elapsed < 0) topologyTicks = -1; // Invalid sample; never turn a bad clock interval into zero cost.
                    else if (topologyTicks >= 0) topologyTicks += elapsed;
                }
            }
            catch (Exception) { Disable(); }
        }
        internal static int SelectDetailVertex(int total)
        {
            // This pass replaces native-vertex clocks with focused engine detail;
            // never stack both instruments onto the sparse detail cohort.
            return -1;
        }
        internal static DetailSample BeginDetail(int bucket)
        {
            if (!detailActive || !sampleActive || bucket < 0 || bucket >= 7) return default;
            try { return new DetailSample { Entered = true, Bucket = bucket + (topologyCalls <= 1 ? 0 : 7), Start = Stopwatch.GetTimestamp(), Id = sampleId }; }
            catch (Exception) { Disable(); return default; }
        }
        internal static void EndDetail(DetailSample token)
        {
            if (!token.Entered || !detailActive || !sampleActive || token.Id != sampleId) return;
            try
            {
                long elapsed = Stopwatch.GetTimestamp() - token.Start;
                if (elapsed < 0) { Disable(); return; }
                detailTicks[token.Bucket] += elapsed; detailCounts[token.Bucket]++;
            }
            catch (Exception) { Disable(); }
        }
        internal static void Unload()
        {
            samplingEnabled = false;
            try { recorder?.Stop("unload", Stopwatch.GetTimestamp()); } catch (Exception) { }
            Retire(); loaded = false;
            if (!captureStarted) { recorder = null; configured = false; Stage("pre_capture_unload"); }
        }
        private static void ClearSample()
        {
            RelayQueryFocus.ClearQuery();
            sampleActive = detailActive = false; topologyDepth = 0; topologyTicks = topologyCalls = 0;
            coldScopes = touchedScopes = activatedScopes = invalidScopes = 0;
            registryChecks = advisoryScopes = advisoryRefreshes = advisoryCacheHits = 0;
            candidateModes = revalidationModes = 0;
            currentWorldModes = decisionModes = 0;
        }
        private static void Disable() { samplingEnabled = false; recorder?.Fail(); ClearSample(); frameTiming?.Dispose(); frameTiming = null; }
        private static void Retire()
        {
            samplingEnabled = false; ClearSample(); configured = false;
            try { if (ticker != null) UnityEngine.Object.Destroy(ticker.gameObject); } catch (Exception) { }
            ticker = null;
            frameTiming?.Dispose(); frameTiming = null;
        }
        private static void Stage(string value)
        {
            if (!enabled || stages++ >= 8) return;
            try { D.Warn("[RelayQueryIsolation/1] state=" + value + " build=local-relay-pool-1"); } catch (Exception) { }
        }
    }
    internal sealed class RelayQueryBaselineTicker : MonoBehaviour { private void Update() { RelayQueryBaseline.Tick(); } }
}
#endif
