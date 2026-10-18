using Game;

namespace TractorBeamAutoConnect;

public static class TractorBeamAutoConnectSettings
{
    public const string EnabledKey = "tractor_beam_auto_connect.enabled";
    public const string ShortcutKey = "tractor_beam_auto_connect.ui.shortcut";

    public static bool Enabled { get; private set; }

    public static void Initialize()
    {
        The.Prefs.RegisterBool(EnabledKey, true);
        Enabled = The.Prefs.GetBoolAutoHash(EnabledKey, true);
    }

    public static void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        The.Prefs.SetBool(EnabledKey, enabled);
        The.SaveGlobalSettings();
    }
}
