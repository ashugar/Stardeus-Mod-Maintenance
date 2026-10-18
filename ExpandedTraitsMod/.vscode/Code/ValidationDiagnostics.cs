using System.Collections.Generic;
using KL.Utils;

namespace ExpandedTraitsMod {
    /// <summary>
    /// Temporary runtime evidence for gameplay behaviors that are difficult to observe.
    /// Remove this class and its call sites after the behaviors have been validated in game.
    /// </summary>
    internal static class ValidationDiagnostics {
        internal const string Prefix = "[Expanded Traits][Validation]";
        private static readonly HashSet<string> OnceKeys = new();

        internal static void Reset() => OnceKeys.Clear();

        internal static void Log(string message, params object[] args) =>
            D.Log(Prefix + " " + message, args);

        internal static void Once(string key, string message, params object[] args) {
            if (key == null || !OnceKeys.Add(key)) return;
            Log(message, args);
        }
    }
}
