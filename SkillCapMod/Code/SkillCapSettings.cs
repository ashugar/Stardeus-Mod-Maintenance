using System.Numerics;
using Game;
using Game.Data;

namespace SkillCapMod;

/// <summary>
/// Owns the one global setting used by the mod. It deliberately uses Core's preference system,
/// rather than per-save data, so the cap is available from the main menu before a save is loaded.
/// Individual beings' extended skill levels are still stored inside each save by
/// <see cref="SkillCapSys"/>.
/// </summary>
internal static class SkillCapSettings
{
    // A mod-specific key prevents collisions with Core and other mods in the global settings file.
    internal const string PreferenceKey = "SkillCapMod.MaximumLevel";

    /// <summary>
    /// Reads and validates the configured cap from Core's global preferences on demand. Invalid or
    /// missing data falls back to Core's ordinary level-25 cap instead of breaking save loading.
    /// </summary>
    internal static BigInteger MaximumLevel
    {
        get
        {
            string raw = The.Prefs?.GetStringAutoHash(
                PreferenceKey, SkillCapMath.Canonical(SkillCapMath.DefaultCap));
            return SkillCapMath.TryParseLevel(raw, out BigInteger parsed, out _)
                ? parsed
                : SkillCapMath.DefaultCap;
        }
    }

    /// <summary>
    /// Returns the canonical preference value for populating Core's settings input widget.
    /// </summary>
    internal static string RawMaximumLevel => SkillCapMath.Canonical(MaximumLevel);

    /// <summary>
    /// Writes the new cap through Core's preference API, flushes it immediately, and asks the
    /// active mod system to clamp loaded skills. The setting therefore applies both before and
    /// during a game without maintaining a second in-memory source of truth.
    /// </summary>
    internal static void SetMaximumLevel(BigInteger value)
    {
        The.Prefs.SetString(PreferenceKey, SkillCapMath.Canonical(value));
        The.SaveGlobalSettings();
        SkillCapRuntime.MaximumLevelChanged();
    }

    /// <summary>
    /// Uses Core's current-runtime check to choose accurate explanatory text in the settings page.
    /// </summary>
    internal static bool HasCurrentGame => GameState.IsCurrent(The.R?.State);
}
