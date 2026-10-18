using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace PlanningOverhaul;

/// <summary>
/// Keeps Planning Overhaul diagnostics out of Unity's shared Player.log.
/// The file is recreated once per mod session and remains readable while the
/// game is running.
/// </summary>
internal static class PlanningOverhaulLog
{
    private const string FileName = "PlanningOverhaul.log";
    private static readonly object Sync = new();
    private static StreamWriter writer;
    private static bool initialized;

    internal static string LogPath { get; private set; }

    internal static bool TryStartSession(string version, out string error)
    {
        lock (Sync)
        {
            error = null;
            if (initialized) return writer != null;
            initialized = true;
            try
            {
                LogPath = ResolveLogPath();
                writer = new StreamWriter(new FileStream(LogPath,
                    FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                if (!WriteLine("SESSION", "Planning Overhaul " + version
                    + " diagnostics started. Player.log receives only an "
                    + "actionable warning if this private log cannot be created."))
                {
                    error = "The private diagnostic log could not be written.";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                CloseWriter();
                return false;
            }
        }
    }

    internal static void Info(string message)
    {
        lock (Sync)
            WriteLine("INFO", message);
    }

    internal static void Performance(string operation,
        long elapsedMilliseconds, string detail)
    {
        lock (Sync)
            WriteLine("PERF", operation + " took " + elapsedMilliseconds
                + " ms; " + detail + ".");
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            if (writer != null)
            {
                WriteLine("SESSION", "Planning Overhaul diagnostics stopped.");
                CloseWriter();
            }
            writer = null;
            initialized = false;
        }
    }

    private static string ResolveLogPath()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string assemblyDirectory = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            throw new InvalidOperationException(
                "The Planning Overhaul assembly location is unavailable.");

        DirectoryInfo directory = new(assemblyDirectory);
        if (string.Equals(directory.Name, "Libraries",
                StringComparison.OrdinalIgnoreCase)
                && directory.Parent != null)
            directory = directory.Parent;
        return Path.Combine(directory.FullName, FileName);
    }

    private static bool WriteLine(string category, string message)
    {
        if (writer == null) return false;
        try
        {
            writer.WriteLine(DateTimeOffset.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.fff zzz") + " [" + category + "] "
                + (message ?? string.Empty));
            return true;
        }
        catch
        {
            CloseWriter();
            return false;
        }
    }

    private static void CloseWriter()
    {
        // A failed disk write can also make Dispose/Flush fail. Diagnostics
        // must never abort a construction pass or send an exception into Core.
        StreamWriter failedWriter = writer;
        writer = null;
        try { failedWriter?.Dispose(); }
        catch { /* Logging is best-effort; normal gameplay keeps running. */ }
    }
}
