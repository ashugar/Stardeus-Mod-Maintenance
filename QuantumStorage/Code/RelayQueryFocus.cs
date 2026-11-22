#if QUANTUM_RELAY_QUERY_BASELINE
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace Game.Systems.QuantumStorage
{
    // Sparse observational detail, separate from the comparable coarse cohort.
    // The outer engine owns counters; native/nested queries keep their ordinary path.
    internal static class RelayQueryFocus
    {
        internal const int CountSize = 23, StageSize = 15;
        private static Func<long> clock = Stopwatch.GetTimestamp;
        private static Func<long> allocatedBytes;
        private static string allocationStatus = "not_initialized";
        [ThreadStatic] private static State state;
        private sealed class State
        {
            internal readonly long[] Counts = new long[CountSize], Ticks = new long[StageSize], TimedCounts = new long[StageSize];
            internal bool Active, EngineArmed, Entered, Armed, Witness, Allocation, PoolPending, PoolClaimed, RelayPending;
            internal int PoolOutcome, PoolInnerKind;
            internal long Ordinal, Target, Visits, AllocationStart;
        }
        // Counts: attempts, candidates, eligible, pool_keys, duplicates,
        // providers, positive_providers, retries, witnesses, provider_witnesses,
        // observer_failures, count_reads. Times may include nested/native work.
        internal struct Snapshot
        {
            internal bool Valid, AllocationAvailable;
            internal long AllocatedBytes;
            internal long[] Counts, Ticks, TimedCounts;
            internal int PoolOutcome, PoolInnerKind;
        }
        internal static string AllocationStatus => allocationStatus;
        internal static void Configure()
        {
            allocatedBytes = null; allocationStatus = "unavailable";
            try
            {
                if (state == null) state = new State();
                var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method == null || method.ReturnType != typeof(long)) { allocationStatus = "unavailable"; return; }
                var read = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
                ProbeAllocation(read);
            }
            catch (Exception) { allocatedBytes = null; allocationStatus = "unavailable"; }
        }
        private static bool ProbeAllocation(Func<long> read)
        {
            allocatedBytes = null; allocationStatus = "unavailable";
            try
            {
                read(); // Warm the delegate/runtime before testing responsiveness.
                long before = read();
                var probe = new byte[256];
                long after = read();
                GC.KeepAlive(probe);
                if (before < 0 || after < before || after - before < 256) { allocationStatus = "unresponsive"; return false; }
                allocatedBytes = read; allocationStatus = "available"; return true;
            }
            catch (Exception) { return false; }
        }
        internal static void BeginQuery(long ordinal)
        {
            try
            {
                var s = state;
                if (s == null || s.Active) return;
                Array.Clear(s.Counts, 0, CountSize); Array.Clear(s.Ticks, 0, StageSize); Array.Clear(s.TimedCounts, 0, StageSize);
                s.Active = true; s.EngineArmed = s.Entered = s.Armed = s.Witness = s.Allocation = false;
                s.PoolPending = s.PoolClaimed = s.RelayPending = false; s.PoolOutcome = 0;
                s.PoolInnerKind = (int)((ulong)ordinal % 3);
                s.Ordinal = ordinal; s.Target = s.Visits = 0;
                s.Allocation = TryReadAllocation(out s.AllocationStart);
            }
            catch (Exception) { ClearQuery(); }
        }
        internal static Snapshot EndQuery()
        {
            var s = state;
            if (s == null || !s.Active) return default;
            long bytes = 0;
            bool allocation = false;
            if (s.Allocation && TryReadAllocation(out long end))
            {
                if (end >= s.AllocationStart) { bytes = end - s.AllocationStart; allocation = true; }
                else { allocatedBytes = null; allocationStatus = "invalid_delta"; }
            }
            s.Active = s.Entered = s.PoolPending = s.PoolClaimed = s.RelayPending = false;
            return new Snapshot { Valid = true, AllocationAvailable = allocation, AllocatedBytes = bytes, Counts = s.Counts, Ticks = s.Ticks, TimedCounts = s.TimedCounts, PoolOutcome = s.PoolOutcome, PoolInnerKind = s.PoolInnerKind };
        }
        internal static void ClearQuery() { var s = state; if (s != null) { s.Active = s.EngineArmed = s.Entered = s.Witness = s.PoolPending = s.PoolClaimed = s.RelayPending = false; } }
        internal static bool TryReadAllocation(out long value)
        {
            value = 0;
            if (allocatedBytes == null) return false;
            try
            {
                value = allocatedBytes();
                if (value >= 0) return true;
                allocationStatus = "invalid_value";
            }
            catch (Exception) { allocationStatus = "failed"; }
            allocatedBytes = null; return false;
        }
        internal static void ArmEngine() { var s = state; if (s != null && s.Active && !s.Entered) s.EngineArmed = true; }
        internal static bool TryEnterEngine()
        {
            var s = state;
            if (s == null || !s.Active || !s.EngineArmed || s.Entered) return false;
            s.Entered = true; return true;
        }
        internal static void LeaveEngine() { var s = state; if (s != null) { s.Witness = false; } }
        internal static void Attempt() { state.Counts[0]++; }
        internal static void CountRead(int value)
        {
            var s = state; s.Counts[11]++;
            if (!s.Armed && value > 0) { s.Armed = true; s.Target = (long)((ulong)s.Ordinal % (uint)value); }
        }
        internal static bool Visit()
        {
            var s = state; s.Counts[1]++;
            s.Witness = s.Armed && s.Visits++ == s.Target;
            if (s.Witness) s.Counts[8]++;
            return s.Witness;
        }
        internal static void Eligible(bool eligible) { if (eligible) state.Counts[2]++; }
        internal static void BeforePool() { state.Counts[3]++; }
        internal static void Pool(bool duplicate)
        {
            var s = state;
            if (duplicate) s.Counts[4]++;
            if (s.Witness) { s.PoolOutcome = duplicate ? 2 : 1; if (duplicate) s.Counts[22]++; }
        }
        internal static int PoolInnerKind => state?.PoolInnerKind ?? -1;
        internal static void ArmPoolProbe(bool witness)
        { var s = state; if (s != null && s.Active && s.Entered && witness) s.PoolPending = true; }
        internal static bool TryEnterPoolProbe()
        {
            var s = state;
            if (s == null || !s.Active || !s.PoolPending) return false;
            s.PoolPending = false; s.PoolClaimed = true; s.Counts[12]++; return true;
        }
        internal static void LeavePoolProbe()
        { var s = state; if (s != null) s.PoolPending = s.PoolClaimed = s.RelayPending = false; }
        internal static void ArmRelayPoolProbe()
        { var s = state; if (s != null && s.Active && s.PoolClaimed) s.RelayPending = true; }
        internal static bool TryEnterRelayPoolProbe()
        {
            var s = state;
            if (s == null || !s.Active || !s.RelayPending) return false;
            s.RelayPending = false; s.Counts[19]++; return true;
        }
        internal static void LeaveRelayPoolProbe() { var s = state; if (s != null) s.RelayPending = false; }
        internal static void NotePoolBranch(int branch) { if (branch >= 0 && branch < 6) state.Counts[13 + branch]++; }
        internal static void NoteForcedRefresh() { state.Counts[20]++; }
        internal static void NoteRefetch() { state.Counts[21]++; }
        internal static void BeforeAvailable() { state.Counts[5]++; if (state.Witness) state.Counts[9]++; }
        internal static void Available(int amount) { if (amount > 0) state.Counts[6]++; }
        internal static void Retry() { state.Counts[7]++; }
        internal static long BeginStage(int bucket, bool enabled = true)
        {
            if (!enabled) return -1;
            try { return clock(); }
            catch (Exception) { state.Counts[10]++; return -1; }
        }
        internal static void EndStage(int bucket, long start)
        {
            if (start < 0) return;
            try
            {
                long end = clock();
                if (end < start) { state.Counts[10]++; return; }
                state.Ticks[bucket] += end - start; state.TimedCounts[bucket]++;
            }
            catch (Exception) { state.Counts[10]++; }
        }
        internal sealed class Interval
        {
            private readonly long[] counts = new long[CountSize], ticks = new long[StageSize], timedCounts = new long[StageSize];
            private long samples, elapsedTicks, allocationSamples, bytes;
            private long duplicateWitnesses, uniqueWitnesses, duplicateKeyTicks, uniqueKeyTicks, duplicateActiveTicks, uniqueActiveTicks;
            private long duplicateKeyN, uniqueKeyN, duplicateActiveN, uniqueActiveN;
            private readonly long[] innerRequested = new long[3];
            private readonly long frequency;
            internal Interval(long frequency) { this.frequency = frequency; }
            internal void Add(Snapshot sample, long elapsed)
            {
                if (!sample.Valid) return;
                samples++; elapsedTicks += elapsed;
                for (int i = 0; i < CountSize; i++) counts[i] += sample.Counts[i];
                for (int i = 0; i < StageSize; i++) { ticks[i] += sample.Ticks[i]; timedCounts[i] += sample.TimedCounts[i]; }
                if (sample.AllocationAvailable) { allocationSamples++; bytes += sample.AllocatedBytes; }
                if (sample.Counts[19] > 0 && sample.PoolInnerKind >= 0 && sample.PoolInnerKind < 3) innerRequested[sample.PoolInnerKind]++;
                if (sample.PoolOutcome == 2)
                {
                    duplicateWitnesses++; duplicateKeyTicks += sample.Ticks[6]; duplicateActiveTicks += sample.Ticks[14];
                    duplicateKeyN += sample.TimedCounts[6]; duplicateActiveN += sample.TimedCounts[14];
                }
                else if (sample.PoolOutcome == 1)
                {
                    uniqueWitnesses++; uniqueKeyTicks += sample.Ticks[6]; uniqueActiveTicks += sample.Ticks[14];
                    uniqueKeyN += sample.TimedCounts[6]; uniqueActiveN += sample.TimedCounts[14];
                }
            }
            internal string CountsLine => "focus_samples=" + samples + " attempts=" + counts[0] + " candidates=" + counts[1] +
                " eligible=" + counts[2] + " pool_keys=" + counts[3] + " duplicate_keys=" + counts[4] + " provider_calls=" + counts[5] +
                " positive_providers=" + counts[6] + " retries=" + counts[7] + " witnesses=" + counts[8] + " missing_witnesses=" + (samples - counts[8]) +
                " provider_witnesses=" + counts[9] + " observer_failures=" + counts[10] + " count_reads=" + counts[11] +
                " allocation_counter=" + AllocationStatus + " allocation_samples=" + allocationSamples + " allocated_bytes=" + (allocationSamples == 0 ? "unavailable" : bytes.ToString(CultureInfo.InvariantCulture)) +
                " pool_probes=" + counts[12] + " pool_ordinary=" + counts[13] + " pool_relay=" + counts[14] + " pool_quantum=" + counts[15] +
                " pool_missing_scope=" + counts[16] + " pool_resolved=" + counts[17] + " pool_fallback=" + counts[18] + " relay_pool_probes=" + counts[19] +
                " forced_refreshes=" + counts[20] + " refetches=" + counts[21] + " duplicate_witnesses=" + duplicateWitnesses + " unique_witnesses=" + uniqueWitnesses +
                " inner_activation_requested=" + innerRequested[0] + " inner_lookup_requested=" + innerRequested[1] + " inner_active_requested=" + innerRequested[2] +
                " scope=selected_outer_engine_query allocation_scope=inclusive_query";
            internal string TimingLine
            {
                get
                {
                    string line = "focus_samples=" + samples + " focus_query_ms=" + Ms(elapsedTicks);
                    string[] labels = { "hashset_setup", "begin", "validate", "cleanup", "candidate", "eligibility", "key_resolve", "provider", "scan", "dedup", "is_relay", "pool_resolution", "pool_activation", "pool_first_lookup", "pool_active" };
                    for (int i = 0; i < StageSize; i++) line += " " + labels[i] + "_ms=" + Ms(ticks[i]) + " " + labels[i] + "_n=" + timedCounts[i];
                    return line + " duplicate_key_ms=" + Ms(duplicateKeyTicks) + " duplicate_key_n=" + duplicateKeyN +
                        " unique_key_ms=" + Ms(uniqueKeyTicks) + " unique_key_n=" + uniqueKeyN +
                        " duplicate_active_ms=" + Ms(duplicateActiveTicks) + " duplicate_active_n=" + duplicateActiveN +
                        " unique_active_ms=" + Ms(uniqueActiveTicks) + " unique_active_n=" + uniqueActiveN +
                        " item_timing=one_rotating_candidate_per_query pool_inner_timing=one_of_three scan_includes_items=true category_times_overlap_topology=true";
                }
            }
            private string Ms(long value) => (value * 1000.0 / frequency).ToString("F3", CultureInfo.InvariantCulture);
            internal void Reset()
            {
                Array.Clear(counts, 0, CountSize); Array.Clear(ticks, 0, StageSize); Array.Clear(timedCounts, 0, StageSize);
                samples = elapsedTicks = allocationSamples = bytes = 0;
                duplicateWitnesses = uniqueWitnesses = duplicateKeyTicks = uniqueKeyTicks = duplicateActiveTicks = uniqueActiveTicks = 0;
                duplicateKeyN = uniqueKeyN = duplicateActiveN = uniqueActiveN = 0;
                Array.Clear(innerRequested, 0, 3);
            }
        }
    }
}
#endif
