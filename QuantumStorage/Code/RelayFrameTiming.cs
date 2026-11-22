#if QUANTUM_RELAY_QUERY_BASELINE
using System;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;

namespace Game.Systems.QuantumStorage
{
    // Optional read-only engine telemetry. Wall gaps and all-thread process CPU
    // remain distinct from engine frame timings; none is exclusive gameplay work.
    internal sealed class RelayFrameTiming : IDisposable
    {
        private readonly FrameTiming[] latest = new FrameTiming[1];
        private Process process;
        private long processTicks, wallStart;
        private ulong timestamp;
        private string engine = "unavailable", cpu = "unavailable";
        private long engineSamples, missing, duplicate, gpuSamples, mainSamples, waitSamples, renderSamples;
        private double frameMs, mainMs, waitMs, renderMs, gpuMs;
        private long allocationStart;
        private bool allocationWindow, gcAvailable;
        private int gc0, gc1, gc2, gcMax = -1;
        internal void Start()
        {
            wallStart = Stopwatch.GetTimestamp();
            allocationWindow = RelayQueryFocus.TryReadAllocation(out allocationStart);
            try
            {
                gcMax = Math.Min(2, GC.MaxGeneration);
                gc0 = GC.CollectionCount(0);
                if (gcMax >= 1) gc1 = GC.CollectionCount(1);
                if (gcMax >= 2) gc2 = GC.CollectionCount(2);
                gcAvailable = gcMax >= 0;
            }
            catch (Exception) { gcAvailable = false; }
            try { engine = FrameTimingManager.IsFeatureEnabled() ? "enabled" : "disabled"; }
            catch (Exception) { engine = "unavailable"; }
            try { process = Process.GetCurrentProcess(); processTicks = process.TotalProcessorTime.Ticks; cpu = "available"; }
            catch (Exception) { CloseProcess(); cpu = "unavailable"; }
        }
        internal void Frame()
        {
            if (engine != "enabled") return;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, latest) == 0) { missing++; return; }
                var t = latest[0];
                if (t.frameStartTimestamp == 0) { missing++; return; }
                if (t.frameStartTimestamp <= timestamp) { duplicate++; return; }
                timestamp = t.frameStartTimestamp;
                if (!Valid(t.cpuFrameTime) || !Valid(t.cpuMainThreadFrameTime) ||
                    !Valid(t.cpuMainThreadPresentWaitTime) || !Valid(t.cpuRenderThreadFrameTime) || !Valid(t.gpuFrameTime))
                { missing++; return; }
                if (t.cpuFrameTime == 0) { missing++; return; }
                engineSamples++; frameMs += t.cpuFrameTime;
                if (t.cpuMainThreadFrameTime > 0) { mainSamples++; mainMs += t.cpuMainThreadFrameTime; }
                if (t.cpuMainThreadPresentWaitTime > 0) { waitSamples++; waitMs += t.cpuMainThreadPresentWaitTime; }
                if (t.cpuRenderThreadFrameTime > 0) { renderSamples++; renderMs += t.cpuRenderThreadFrameTime; }
                if (t.gpuFrameTime > 0) { gpuSamples++; gpuMs += t.gpuFrameTime; }
            }
            catch (Exception) { engine = "failed"; }
        }
        private static bool Valid(double value) => value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
        internal string Report()
        {
            string processMs = "unavailable";
            string allocated = "unavailable", collections = "unavailable";
            if (RelayQueryFocus.TryReadAllocation(out long allocatedNow))
            {
                if (allocationWindow && allocatedNow >= allocationStart) allocated = (allocatedNow - allocationStart).ToString(CultureInfo.InvariantCulture);
                allocationStart = allocatedNow; allocationWindow = true;
            }
            else allocationWindow = false;
            if (gcAvailable)
            {
                try
                {
                    int next0 = GC.CollectionCount(0), next1 = gcMax >= 1 ? GC.CollectionCount(1) : gc1, next2 = gcMax >= 2 ? GC.CollectionCount(2) : gc2;
                    if (next0 >= gc0 && next1 >= gc1 && next2 >= gc2)
                        collections = (next0 - gc0) + "," + (gcMax >= 1 ? (next1 - gc1).ToString(CultureInfo.InvariantCulture) : "unsupported") + "," + (gcMax >= 2 ? (next2 - gc2).ToString(CultureInfo.InvariantCulture) : "unsupported");
                    gc0 = next0; gc1 = next1; gc2 = next2;
                }
                catch (Exception) { gcAvailable = false; }
            }
            long now = Stopwatch.GetTimestamp();
            if (process != null)
            {
                try
                {
                    long next = process.TotalProcessorTime.Ticks;
                    if (next < processTicks) { cpu = "invalid_delta"; CloseProcess(); }
                    else { processMs = Number((next - processTicks) / (double)TimeSpan.TicksPerMillisecond); processTicks = next; }
                }
                catch (Exception) { cpu = "failed"; CloseProcess(); }
            }
            string result = "engine_timing=" + engine + " engine_unique_samples=" + engineSamples + " engine_missing=" + missing + " engine_duplicate=" + duplicate +
                " engine_cpu_frame_ms_sum=" + (engineSamples == 0 ? "unavailable" : Number(frameMs)) + " engine_main_positive_samples=" + mainSamples + " engine_main_thread_ms_sum=" + (mainSamples == 0 ? "unavailable" : Number(mainMs)) +
                " engine_wait_positive_samples=" + waitSamples + " engine_present_wait_ms_sum=" + (waitSamples == 0 ? "zero_or_unavailable" : Number(waitMs)) + " engine_render_positive_samples=" + renderSamples + " engine_render_thread_ms_sum=" + (renderSamples == 0 ? "unavailable" : Number(renderMs)) +
                " engine_gpu_positive_samples=" + gpuSamples + " engine_gpu_ms_sum=" + (gpuSamples == 0 ? "unavailable" : Number(gpuMs)) + " process_cpu=" + cpu + " process_cpu_all_threads_ms=" + processMs +
                " process_wall_ms=" + (now < wallStart ? "unavailable" : Number((now - wallStart) * 1000.0 / Stopwatch.Frequency)) +
                " owner_thread_allocated_bytes=" + allocated + " allocation_counter=" + RelayQueryFocus.AllocationStatus +
                " process_gc_collections_gen0_1_2=" + collections + " process_gc_observed_max_gen=" + gcMax + " outside_query_cpu=not_attributed";
            wallStart = now; engineSamples = missing = duplicate = gpuSamples = mainSamples = waitSamples = renderSamples = 0;
            frameMs = mainMs = waitMs = renderMs = gpuMs = 0;
            return result;
        }
        private void CloseProcess() { try { process?.Dispose(); } catch (Exception) { } process = null; }
        public void Dispose() { CloseProcess(); engine = "retired"; }
    }
}
#endif
