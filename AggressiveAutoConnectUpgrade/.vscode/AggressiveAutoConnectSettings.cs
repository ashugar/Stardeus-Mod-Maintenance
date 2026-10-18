using Game;

namespace AggressiveAutoConnectUpgrade;

public static class AggressiveAutoConnectSettings
{
    public const string EnabledKey = "aggressive_auto_connect.enabled";
    public const string ShortcutKey = "aggressive_auto_connect.shortcut";
    public const string PulseIntervalKey = "aggressive_auto_connect.pulse_interval";
    public const string CompleteReconnectKey =
        "aggressive_auto_connect.complete_reconnect";
    public const string RestoreColonistShutdownsKey =
        "aggressive_auto_connect.restore_colonist_shutdowns";
    public const float DefaultPulseIntervalSeconds = 5f;
    public const float MinPulseIntervalSeconds = 0.25f;
    public const float MaxPulseIntervalSeconds = 3600f;

    public static bool Enabled { get; private set; }
    public static bool CompleteReconnectEnabled { get; private set; }
    public static bool RestoreColonistShutdownsEnabled { get; private set; }
    public static float PulseIntervalSeconds { get; private set; } =
        DefaultPulseIntervalSeconds;

    public static void Initialize()
    {
        The.Prefs.RegisterBool(EnabledKey, false);
        The.Prefs.RegisterFloat(PulseIntervalKey, DefaultPulseIntervalSeconds);
        The.Prefs.RegisterBool(CompleteReconnectKey, false);
        The.Prefs.RegisterBool(RestoreColonistShutdownsKey, false);
        Enabled = The.Prefs.GetBoolAutoHash(EnabledKey, false);
        CompleteReconnectEnabled =
            The.Prefs.GetBoolAutoHash(CompleteReconnectKey, false);
        RestoreColonistShutdownsEnabled =
            The.Prefs.GetBoolAutoHash(RestoreColonistShutdownsKey, false);
        PulseIntervalSeconds = UnityEngine.Mathf.Clamp(
            The.Prefs.GetFloatAutoHash(
                PulseIntervalKey,
                DefaultPulseIntervalSeconds),
            MinPulseIntervalSeconds,
            MaxPulseIntervalSeconds);
    }

    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        The.Prefs.SetBool(EnabledKey, enabled);
        The.SaveGlobalSettings();
    }

    public static void SetPulseInterval(float seconds)
    {
        PulseIntervalSeconds = UnityEngine.Mathf.Clamp(
            seconds,
            MinPulseIntervalSeconds,
            MaxPulseIntervalSeconds);
        The.Prefs.SetFloat(PulseIntervalKey, PulseIntervalSeconds);
        The.SaveGlobalSettings();
    }

    public static void SetCompleteReconnectEnabled(bool enabled)
    {
        CompleteReconnectEnabled = enabled;
        The.Prefs.SetBool(CompleteReconnectKey, enabled);
        The.SaveGlobalSettings();
    }

    public static void SetRestoreColonistShutdownsEnabled(bool enabled)
    {
        RestoreColonistShutdownsEnabled = enabled;
        The.Prefs.SetBool(RestoreColonistShutdownsKey, enabled);
        The.SaveGlobalSettings();
    }
}
