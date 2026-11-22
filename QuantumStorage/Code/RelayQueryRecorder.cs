#if QUANTUM_RELAY_QUERY_BASELINE
using System;
using System.Globalization;

namespace Game.Systems.QuantumStorage
{
    // Pure, owner-thread model. No game objects, query inputs or per-item counters.
    internal sealed class RelayQueryRecorder
    {
        private readonly Action<string> sink;
        private readonly long frequency;
        private readonly RelayCallerTrace callers;
        private readonly RelayQueryFocus.Interval focused;
        private long start, flush, lastFrame, entries, intervalEntries, selected, discarded, intervalDiscarded;
        private long samples, intervalSamples, queryTicks, intervalQueryTicks, topologyTicks, intervalTopologyTicks;
        private long validations, intervalValidations, failures, intervalFailures;
        private long frameCount, unfocusedFrames, slow33, slow66, slow100, maxGap;
        private int phase, epoch, lines;
        private long frameEntries, minFrameEntries = long.MaxValue, maxFrameEntries;
        private long detailSamples, intervalDetailSamples, intervalDetailQueryTicks, intervalDetailTopologyTicks, intervalDetailValidations;
        private long callerSamples, intervalCallerSamples;
        private long intervalColdQueries, intervalDependentQueries, intervalColdScopes, intervalTouchedScopes, intervalActivatedScopes, intervalInvalidScopes;
        private long intervalRegistryChecks, intervalAdvisoryQueries, intervalAdvisoryScopes, intervalAdvisoryRefreshes, intervalAdvisoryCacheHits;
        private long intervalCandidateQueries, intervalRevalidationQueries;
        private long intervalCurrentWorldQueries, intervalDecisionQueries;
        private readonly long[] detailTicks = new long[14], detailCounts = new long[14];
        internal bool SelectedDetail { get; private set; }
        internal bool SelectedCaller { get; private set; }
        internal Action BeforeReport;
        internal string FrameEvidence = "engine_timing=not_initialized process_cpu=not_initialized";
        internal string CandidateReadView = "not_observed";
        private bool cap, phaseHeld;
        internal bool Running { get; private set; }
        internal bool Spent { get; private set; }
        internal bool Sampling => Running && !cap && !phaseHeld && (phase & 1) == 1;
        internal int Phase => phase;
        internal int Epoch => epoch;
        internal RelayQueryRecorder(long frequency, Action<string> sink)
        { this.frequency = frequency; this.sink = sink; callers = new RelayCallerTrace(frequency, Write); focused = new RelayQueryFocus.Interval(frequency); }

        internal void Start(long now, bool active, string sourceView = "native", bool nativeCandidates = false, string candidateReadView = "not_observed")
        {
            if (!active || Running || Spent) return;
            Running = true; start = flush = lastFrame = now; phase = 0; epoch++;
            CandidateReadView = candidateReadView;
            Write("state=capturing build=local-relay-pool-1 source_view=" + sourceView +
                " candidate_contract=" + (nativeCandidates ? "available" : "broad") + " nanobot_read_view=" + candidateReadView +
                " window_s=120 anchor=after_load phase_s=20 modes=frame_only,sample,frame_only,sample,frame_only,sample sample_every=64 detail_every=512 caller_every=4096 caller_cap=32 detail_and_caller_excluded_coarse=true entry_scope=eligible_mod_entries_nested_suppressed_only_inside_selected_query entry_cap=1000000 query_scope=sampled_mod_queries_inclusive_nested_work per_item_counts=focused_outer_engine_only native_vertex_detail=disabled item_clock_cap=one_candidate_per_detail_query allocation=optional_current_thread_inclusive pool_probe=selected_witness inner_probe=one_of_three focused_clock_cap=42");
        }

        // Only sampling-phase query entries are observed. Off-phase means unknown,
        // not zero. No clock or allocation occurs during ordinal selection.
        internal bool SelectQuery()
        {
            if (!Sampling) return false;
            if (entries >= 1000000) { cap = true; return false; }
            entries++; intervalEntries++; frameEntries++;
            if ((entries & 63) != 0) return false;
            SelectedCaller = (entries & 4095) == 0;
            SelectedDetail = !SelectedCaller && (entries & 511) == 0;
            selected++;
            return true;
        }
        internal bool CanStartSample(long now)
        {
            if (!Sampling) return false;
            if (PhaseAt(now) != phase) { phaseHeld = true; DiscardSample(); return false; }
            return true;
        }
        internal bool SameSampleWindow(long now, int entryPhase, int entryEpoch) =>
            Running && !cap && entryEpoch == epoch && entryPhase == phase && PhaseAt(now) == phase && (phase & 1) == 1;
        internal void CaptureCaller() { callers.Capture(); }
        internal void CompleteSample(long now, int entryPhase, int entryEpoch, long elapsed, long topologyElapsed, long topologyCalls, bool failed,
            bool detail = false, long[] bucketTicks = null, long[] bucketCounts = null, bool caller = false,
            long coldScopes = 0, long touchedScopes = 0, long activatedScopes = 0, long invalidScopes = 0,
            long registryChecks = 0, long advisoryScopes = 0, long advisoryRefreshes = 0, long advisoryCacheHits = 0,
            long candidateModes = 0, long revalidationModes = 0, long currentWorldModes = 0, long decisionModes = 0,
            RelayQueryFocus.Snapshot focus = default)
        {
            if (!Running || cap || entryEpoch != epoch || entryPhase != phase || PhaseAt(now) != phase || (phase & 1) == 0)
            { DiscardSample(); return; }
            if (elapsed < 0 || topologyElapsed < 0 || topologyElapsed > elapsed || topologyCalls < 0)
            { DiscardSample(); return; }
            if (caller)
            {
                callerSamples++; intervalCallerSamples++;
                if (failed) { failures++; intervalFailures++; }
                return;
            }
            if (detail)
            {
                focused.Add(focus, elapsed);
                detailSamples++; intervalDetailSamples++; intervalDetailQueryTicks += elapsed; intervalDetailTopologyTicks += topologyElapsed; intervalDetailValidations += topologyCalls;
                if (bucketTicks != null && bucketCounts != null)
                    for (int i = 0; i < 14; i++) { detailTicks[i] += bucketTicks[i]; detailCounts[i] += bucketCounts[i]; }
                if (failed) { failures++; intervalFailures++; }
                return;
            }
            long duration = elapsed;
            long validation = topologyElapsed;
            samples++; intervalSamples++; queryTicks += duration; intervalQueryTicks += duration;
            if (touchedScopes == 0 && topologyCalls == 0) intervalColdQueries++; else intervalDependentQueries++;
            intervalColdScopes += coldScopes; intervalTouchedScopes += touchedScopes;
            intervalActivatedScopes += activatedScopes; intervalInvalidScopes += invalidScopes;
            intervalRegistryChecks += registryChecks; intervalAdvisoryScopes += advisoryScopes;
            intervalAdvisoryRefreshes += advisoryRefreshes; intervalAdvisoryCacheHits += advisoryCacheHits;
            if (advisoryScopes > 0) intervalAdvisoryQueries++;
            if (candidateModes > 0) intervalCandidateQueries++;
            if (revalidationModes > 0) intervalRevalidationQueries++;
            if (currentWorldModes > 0) intervalCurrentWorldQueries++;
            if (decisionModes > 0) intervalDecisionQueries++;
            topologyTicks += validation; intervalTopologyTicks += validation;
            validations += topologyCalls; intervalValidations += topologyCalls;
            if (failed) { failures++; intervalFailures++; }
        }
        internal void DiscardSample() { discarded++; intervalDiscarded++; }
        private int PhaseAt(long now) => now < start ? -1 : (int)Math.Min(6, (now - start) / (frequency * 20));

        internal void Frame(long now, bool focused)
        {
            if (!Running) return;
            long gap = Math.Max(0, now - lastFrame); lastFrame = now;
            if ((phase & 1) == 1) { minFrameEntries = Math.Min(minFrameEntries, frameEntries); maxFrameEntries = Math.Max(maxFrameEntries, frameEntries); }
            frameEntries = 0;
            frameCount++; if (!focused) unfocusedFrames++;
            if (gap > frequency / 30) slow33++;
            if (gap > frequency / 15) slow66++;
            if (gap > frequency / 10) slow100++;
            maxGap = Math.Max(maxGap, gap);
            int next = PhaseAt(now);
            bool transition = next != phase;
            // Mode changes only at this frame boundary. A long frame crossing a
            // scheduled boundary cannot be assigned to a single 20-second mode.
            bool mixed = transition && (next - phase > 1 || gap > frequency / 4);
            if (cap || next >= 6)
            {
                Report(cap ? "entry_cap" : "deadline", now, mixed, next);
                Running = false; Spent = true; epoch++;
                return;
            }
            if (transition || now - flush >= frequency * 10) Report(transition ? "phase_end" : "periodic", now, mixed, next);
            if (transition) { phase = next; epoch++; }
            phaseHeld = false;
        }
        internal void Stop(string reason, long now)
        {
            if (!Running) { Spent = true; epoch++; return; }
            int next = PhaseAt(now);
            Report(reason, now, next != phase, next); Running = false; Spent = true; epoch++;
        }
        internal void Fail() { Running = false; Spent = true; epoch++; }
        private void Report(string reason, long now, bool mixed, int next)
        {
            try { BeforeReport?.Invoke(); } catch (Exception) { FrameEvidence = "engine_timing=report_failed process_cpu=report_failed"; }
            string mode = (phase & 1) == 0 ? "frame_only" : "sample";
            Write("state=" + reason + " phase=" + phase + " mode=" + mode + " next_phase=" + next +
                " attribution=" + (mixed ? "mixed_unattributable" : "completed_mode") +
                " elapsed_s=" + Seconds(now - start) + " interval_s=" + Seconds(now - flush) +
                " query_counts=" + (mode == "sample" ? "eligible_mod_entries" : "not_observed") +
                " interval_entries_observed=" + intervalEntries + " interval_sampled_mod_queries=" + intervalSamples +
                " interval_query_ms=" + Milliseconds(intervalQueryTicks) + " interval_topology_ms=" + Milliseconds(intervalTopologyTicks) +
                " interval_outer_topology_blocks=" + intervalValidations + " interval_sampled_failures=" + intervalFailures +
                " interval_discarded_samples=" + intervalDiscarded + " frames=" + frameCount + " unfocused_frames=" + unfocusedFrames +
                " gaps_gt33ms=" + slow33 + " gaps_gt66ms=" + slow66 + " gaps_gt100ms=" + slow100 + " max_gap_ms=" + Milliseconds(maxGap) +
                " entries_observed=" + entries + " selected_samples=" + selected + " sampled_mod_queries=" + samples +
                " query_ms=" + Milliseconds(queryTicks) + " topology_ms=" + Milliseconds(topologyTicks) +
                " outer_topology_blocks=" + validations + " sampled_failures=" + failures + " discarded_samples=" + discarded);
            Write("state=attribution phase=" + phase + " mode=" + mode + " elapsed_s=" + Seconds(now - start) +
                " attribution=" + (mixed ? "mixed_unattributable" : "completed_mode") + " eligible_mod_entries=" + (mode == "sample" ? intervalEntries.ToString(CultureInfo.InvariantCulture) : "not_observed") + " entries_per_update_mean=" + (mode != "sample" || frameCount == 0 ? "unavailable" : (intervalEntries / (double)frameCount).ToString("F3", CultureInfo.InvariantCulture)) +
                " entries_per_update_min=" + (mode != "sample" || minFrameEntries == long.MaxValue ? "unavailable" : minFrameEntries.ToString(CultureInfo.InvariantCulture)) + " entries_per_update_max=" + (mode == "sample" ? maxFrameEntries.ToString(CultureInfo.InvariantCulture) : "unavailable") +
                " detail_samples=" + intervalDetailSamples + " detail_query_ms=" + Milliseconds(intervalDetailQueryTicks) + " detail_topology_ms=" + Milliseconds(intervalDetailTopologyTicks) +
                " detail_outer_topology_blocks=" + intervalDetailValidations + " detail_boundary_labels=first_outer,later_outer" +
                " caller_queries=" + intervalCallerSamples + " caller_queries_total=" + callerSamples +
                " coarse_cold_queries=" + intervalColdQueries + " coarse_dependent_queries=" + intervalDependentQueries +
                " coarse_cold_scopes=" + intervalColdScopes + " coarse_touched_scopes=" + intervalTouchedScopes +
                " coarse_activated_scopes=" + intervalActivatedScopes + " coarse_invalid_scopes=" + intervalInvalidScopes +
                " coarse_registry_check_calls=" + intervalRegistryChecks + " coarse_advisory_queries=" + intervalAdvisoryQueries +
                " coarse_advisory_scopes=" + intervalAdvisoryScopes + " coarse_advisory_refreshes=" + intervalAdvisoryRefreshes +
                " coarse_advisory_cache_hits=" + intervalAdvisoryCacheHits +
                " coarse_candidate_queries=" + intervalCandidateQueries + " coarse_revalidation_queries=" + intervalRevalidationQueries +
                " coarse_current_world_queries=" + intervalCurrentWorldQueries + " coarse_decision_context_queries=" + intervalDecisionQueries +
                " nanobot_read_view=" + CandidateReadView);
            Write("state=focused_counts phase=" + phase + " elapsed_s=" + Seconds(now - start) + " " + focused.CountsLine);
            Write("state=focused_timing phase=" + phase + " elapsed_s=" + Seconds(now - start) + " " + focused.TimingLine);
            Write("state=frame_evidence phase=" + phase + " elapsed_s=" + Seconds(now - start) + " " + FrameEvidence);
            if (reason != "periodic" && reason != "phase_end") callers.Report();
            Array.Clear(detailTicks, 0, 14); Array.Clear(detailCounts, 0, 14);
            focused.Reset();
            intervalDetailSamples = intervalDetailQueryTicks = intervalDetailTopologyTicks = intervalDetailValidations = 0;
            intervalCallerSamples = intervalColdQueries = intervalDependentQueries = intervalColdScopes = intervalTouchedScopes = intervalActivatedScopes = intervalInvalidScopes = 0;
            intervalRegistryChecks = intervalAdvisoryQueries = intervalAdvisoryScopes = intervalAdvisoryRefreshes = intervalAdvisoryCacheHits = 0;
            intervalCandidateQueries = intervalRevalidationQueries = 0;
            intervalCurrentWorldQueries = intervalDecisionQueries = 0;
            minFrameEntries = long.MaxValue; maxFrameEntries = 0;
            flush = now;
            intervalEntries = intervalSamples = intervalQueryTicks = intervalTopologyTicks = intervalValidations = intervalFailures = intervalDiscarded = 0;
            frameCount = unfocusedFrames = slow33 = slow66 = slow100 = maxGap = 0;
        }
        private string Seconds(long value) => (Math.Max(0, value) / (double)frequency).ToString("F3", CultureInfo.InvariantCulture);
        private string Milliseconds(long value) => (Math.Max(0, value) * 1000.0 / frequency).ToString("F3", CultureInfo.InvariantCulture);
        private void Write(string line)
        {
            if (lines >= 80) return;
            lines++;
            try { if (line.Length <= 1800) sink("[RelayQueryIsolation/1] " + line); }
            catch (Exception) { Fail(); }
        }
    }
}
#endif
