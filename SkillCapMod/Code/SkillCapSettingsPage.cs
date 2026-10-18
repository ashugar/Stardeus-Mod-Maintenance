using System.Numerics;
using Game.CodeGen;
using Game.Systems.UIUX;
using Game.Utils;

namespace SkillCapMod;

/// <summary>
/// A native Core settings page that is registered early enough to appear in the main menu.
/// Using <see cref="BaseSettingsPage"/> keeps the mod inside Core's normal tab, widget, lifetime,
/// tooltip, and preference-save behavior instead of maintaining a separate Unity canvas.
/// </summary>
public sealed class SkillCapSettingsPage : BaseSettingsPage
{
    public const string PageId = "SkillCapMod";

    // Core uses these properties to identify, sort, and label the page in the Settings menu.
    public override string Id => PageId;
    public override int Order => 35;
    public override string TabName => "Skill Cap";

    /// <summary>
    /// Called by Core's generated <see cref="SelfInitAttribute"/> bootstrap. RegisterPage adds this
    /// instance to the same settings-page registry used by Core's built-in pages.
    /// </summary>
    [SelfInit]
    private static void Register() => RegisterPage(new SkillCapSettingsPage());

    /// <summary>
    /// Builds the tab with Core's native settings widgets each time the page is shown. The footer
    /// is hidden because this page owns an immediate Apply button rather than participating in
    /// Core pages that stage several unrelated settings together.
    /// </summary>
    protected override void LoadTab()
    {
        ShowFooter(false);
        int idx = 0;
        AddHeader(idx++, "Skill Cap");
        AddHeader(idx++, "Choose the highest trainable level.");
        AddHeader(idx++, "Core's default maximum is level 25.");
        AddHeader(idx++, "Scientific notation is supported.");
        AddHeader(idx++, "Use 1e308 as an effectively unlimited cap.");
        AddHeader(idx++, "Above level 50, gains diminish logarithmically.");

        WInputWithLabel input = null;
        WText status = null;
        WText statusDetail = null;
        input = AddInput(idx++, "Maximum skill level", "25 or 1e308",
            SkillCapSettings.RawMaximumLevel, value =>
            {
                // Core calls this callback for edits to WInputWithLabel. It previews validation
                // only; no global preference or loaded creature is changed until Apply runs.
                if (status == null || statusDetail == null) return;
                if (SkillCapMath.TryParseLevel(value, out BigInteger parsed, out string error))
                {
                    status.Load($"Valid maximum: {SkillCapMath.Format(parsed)}.");
                    statusDetail.Load("Press Apply to use this value.");
                    return;
                }

                (string first, string second) = SplitStatus(error);
                status.Load(first);
                statusDetail.Load(second);
            }, "A whole number from 1 through 1e308. Lowering the cap clamps higher saved skills when the game is loaded.");

        status = AddHeader(idx++, CurrentMaximum());
        statusDetail = AddHeader(idx++, CurrentScope());

        // Local to LoadTab so both Core's submit event and the button share exactly the same
        // validation and persistence path. Keeping the references local also matches the lifetime
        // of the Core widgets that are destroyed when the tab unloads.
        void Apply()
        {
            if (!SkillCapMath.TryParseLevel(input.Input.Text, out BigInteger parsed, out string error))
            {
                (string first, string second) = SplitStatus(error);
                status.Load(first);
                statusDetail.Load(second);
                return;
            }
            SkillCapSettings.SetMaximumLevel(parsed);
            input.Input.SetValue(SkillCapMath.Canonical(parsed), false);
            status.Load(CurrentMaximum());
            statusDetail.Load(CurrentScope());
        }

        input.Input.SetOnSubmit(_ => Apply());
        AddButton(idx++, "Apply maximum level", Apply,
            "Applies globally. If a game is open, existing higher skills are clamped immediately.");
    }

    /// <summary>
    /// Formats the currently persisted global cap for a short, native Core header row.
    /// </summary>
    private static string CurrentMaximum()
    {
        BigInteger cap = SkillCapSettings.MaximumLevel;
        return $"Current maximum: {SkillCapMath.Format(cap)}.";
    }

    /// <summary>
    /// Describes whether Core currently has a live game runtime, without coupling the page to a
    /// particular save. The cap itself remains global in both cases.
    /// </summary>
    private static string CurrentScope() =>
        SkillCapSettings.HasCurrentGame
            ? "Applies now and to future loads."
            : "Applies when a game starts or loads.";

    /// <summary>
    /// Splits validation feedback across two rows because Core's WTabPanelItem_Header prefab is a
    /// fixed-height, single-line label. This preserves the native UI while avoiding ellipsis at the
    /// normal settings-window width.
    /// </summary>
    private static (string First, string Second) SplitStatus(string message)
    {
        const int targetLength = 42;
        if (string.IsNullOrWhiteSpace(message) || message.Length <= targetLength)
        {
            return (message, string.Empty);
        }

        int split = message.LastIndexOf(' ', targetLength);
        if (split < 1)
        {
            split = message.IndexOf(' ', targetLength);
        }
        if (split < 1)
        {
            return (message, string.Empty);
        }

        return (message.Substring(0, split), message.Substring(split + 1));
    }
}
