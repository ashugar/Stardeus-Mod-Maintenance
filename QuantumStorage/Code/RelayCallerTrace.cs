#if QUANTUM_RELAY_QUERY_BASELINE
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Game.Systems.QuantumStorage
{
    // Filename-free method identities only. This rare observer never receives
    // query arguments, game objects, reservation records or save identities.
    internal sealed class RelayCallerTrace
    {
        private readonly long frequency;
        private readonly Action<string> sink;
        private readonly string[] paths = new string[8];
        private readonly int[] hits = new int[8];
        private int attempts, captures, used, overflow;
        private long ticks;
        private bool failed, reported;
        internal RelayCallerTrace(long frequency, Action<string> sink) { this.frequency = frequency; this.sink = sink; }

        internal void Capture()
        {
            if (failed || reported || attempts >= 32) return;
            attempts++;
            long start = 0;
            try
            {
                start = Stopwatch.GetTimestamp();
                var trace = new StackTrace(false);
                var path = new StringBuilder(512);
                int kept = 0;
                for (int i = 0; i < Math.Min(32, trace.FrameCount) && kept < 7; i++)
                {
                    var method = trace.GetFrame(i)?.GetMethod();
                    if (method == null || IsWrapper(method)) continue;
                    if (kept++ > 0) path.Append('>');
                    path.Append(Identity(method));
                    if (path.Length > 768) { path.Length = 768; break; }
                }
                Add(path.Length == 0 ? "wrapper_or_inline_ambiguous" : path.ToString());
                captures++;
            }
            catch (Exception) { failed = true; }
            finally
            {
                try { long elapsed = Stopwatch.GetTimestamp() - start; if (start != 0 && elapsed >= 0) ticks += elapsed; }
                catch (Exception) { failed = true; }
            }
        }
        private static bool IsWrapper(MethodBase method)
        {
            string type = method.DeclaringType?.FullName ?? "";
            string name = method.Name;
            if (type == typeof(RelayCallerTrace).FullName || type == typeof(RelayQueryBaseline).FullName ||
                type == typeof(RelayQueryRecorder).FullName || type.StartsWith("Game.Systems.QuantumStorage.RelayMaterialQuery", StringComparison.Ordinal)) return true;
            if (type == "Game.Systems.QuantumStorage.Relays.RelayCore" && name == "HasEnough") return true;
            if (type == "Game.Systems.Inventory.InventorySys" && (name == "HasEnough" || name == "HasEnoughFor")) return true;
            return name.IndexOf("InventorySys.HasEnough", StringComparison.Ordinal) >= 0 || name.StartsWith("HasEnough_Patch", StringComparison.Ordinal);
        }
        private static string Identity(MethodBase method)
        {
            var type = method.DeclaringType;
            if (type?.IsGenericType == true) type = type.GetGenericTypeDefinition();
            string assembly = method.Module.Assembly.GetName().Name;
            return Safe(assembly, 40) + ":" + Safe(type?.FullName ?? "dynamic", 100) + "::" + Safe(method.Name, 80);
        }
        private static string Safe(string value, int limit)
        {
            var result = new StringBuilder(Math.Min(value?.Length ?? 0, limit));
            if (value != null) for (int i = 0; i < value.Length && result.Length < limit; i++)
            {
                char c = value[i];
                result.Append(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' ||
                    c == '.' || c == '_' || c == '+' || c == '<' || c == '>' || c == '`' ? c : '_');
            }
            return result.ToString();
        }
        private void Add(string path)
        {
            for (int i = 0; i < used; i++) if (paths[i] == path) { hits[i]++; return; }
            if (used == paths.Length) { overflow++; return; }
            paths[used] = path; hits[used++] = 1;
        }
        internal void Report()
        {
            if (reported) return;
            reported = true;
            try
            {
                sink("state=caller_summary scope=sparse_method_paths_not_caller_census attempts=" + attempts + " captures=" + captures +
                    " buckets=" + used + " overflow=" + overflow + " failed=" + (failed ? "true" : "false") +
                    " trace_wall_ms=" + (ticks * 1000.0 / frequency).ToString("F3", CultureInfo.InvariantCulture));
                for (int i = 0; i < used; i++) sink("state=caller_path hits=" + hits[i] + " path=" + paths[i]);
            }
            catch (Exception) { failed = true; }
        }
    }
}
#endif
