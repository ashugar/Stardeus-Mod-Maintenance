using System;
using System.Diagnostics;

namespace PlanningOverhaul;

/// <summary>
/// Keeps the platform clipboard boundary small and testable. The clipboard
/// value exists only as a local for the duration of the explicit import.
/// </summary>
internal static class PlanningOverhaulClipboardImport
{
    internal static void Run(Func<string> readClipboard,
        Action<string, long> importBlueprint)
    {
        if (readClipboard == null)
            throw new ArgumentNullException(nameof(readClipboard));
        if (importBlueprint == null)
            throw new ArgumentNullException(nameof(importBlueprint));

        Stopwatch timing = Stopwatch.StartNew();
        string code = readClipboard();
        importBlueprint(code, timing.ElapsedMilliseconds);
    }
}
