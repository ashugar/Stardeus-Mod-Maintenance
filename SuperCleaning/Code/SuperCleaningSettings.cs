using Game;

namespace SuperCleaning;

internal static class SuperCleaningSettings
{
    internal const string EnabledKey = "super_cleaning.enabled";
    internal const string IdleAssistEnabledKey =
        "super_cleaning.idle_assist.enabled";
    internal const string ColonistAssistedKey =
        "super_cleaning.colonist_assisted.enabled";

    internal static bool Enabled { get; private set; }
    internal static bool IdleAssistEnabled { get; private set; }
    internal static bool ColonistAssisted { get; private set; }

    internal static void Initialize()
    {
        The.Prefs.RegisterBool(EnabledKey, true);
        The.Prefs.RegisterBool(IdleAssistEnabledKey, true);
        The.Prefs.RegisterBool(ColonistAssistedKey, false);
        Enabled = The.Prefs.GetBoolAutoHash(EnabledKey, true);
        IdleAssistEnabled = The.Prefs.GetBoolAutoHash(
            IdleAssistEnabledKey, true);
        ColonistAssisted = The.Prefs.GetBoolAutoHash(
            ColonistAssistedKey, false);
    }

    internal static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        The.Prefs.SetBool(EnabledKey, enabled);
        The.SaveGlobalSettings();
    }

    internal static void SetIdleAssistEnabled(bool enabled)
    {
        IdleAssistEnabled = enabled;
        The.Prefs.SetBool(IdleAssistEnabledKey, enabled);
        The.SaveGlobalSettings();
    }

    internal static void SetColonistAssisted(bool enabled)
    {
        ColonistAssisted = enabled;
        The.Prefs.SetBool(ColonistAssistedKey, enabled);
        The.SaveGlobalSettings();
    }
}
