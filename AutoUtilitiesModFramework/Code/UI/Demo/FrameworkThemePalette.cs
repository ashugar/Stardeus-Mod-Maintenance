using Game;
using Game.Systems.UIUX;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

// Demo-only, not a consumer API. The catalog is checked against the installed
// Core definitions by VerifyThemePalette.ps1; values are resolved by Core live.
internal static class FrameworkThemePalette
{
    internal static readonly string[] CoreColorIds =
    {
        "White", "Black", "Bright", "GreenDark", "Green", "Red", "Blue",
        "Yellow", "Grey", "AlertRed", "MiniExpeditionBackground",
        "MiniChartBackground", "ChartBackground", "ChartText", "ChartGradient",
        "ChartGrid", "ChartLine", "ChartBlue", "ChartRed", "ChartGreen",
        "ChartWhite", "ChartYellow", "ChartOrange", "PanelRed", "ButtonRed",
        "PanelHeader", "PanelTop", "PanelContent", "PanelFooter", "ButtonSlot",
        "ButtonPanel", "Accent", "Accent2", "Accent3", "ShipCombatFluxSoft",
        "ShipCombatFluxHard", "ShipCombatWeapon", "ShipCombatHP",
        "ShipCombatShields", "Button1", "Button2", "Button3", "ProgressFill",
        "ButtonAccent", "ButtonPanelFancy", "ButtonPanelFancy2", "InputHighlight",
        "ResearchAvailable", "ResearchUnlocked", "ResearchUnlockedLine",
        "ResearchUnlockableLine", "ResearchLocked", "ResearchLockedLine",
        "ResearchNotDiscovered", "ResearchTierLine_Unlocked", "ResearchTierLine_Locked",
        "HyperspaceProgressBG", "HyperspaceProgressStripe", "HyperspaceProgressBarBG",
        "HyperspaceProgressBarFill", "StarmapRing", "StarmapExitRing",
        "StarmapMarker", "StarmapMarker_Hyperspace", "PauseFrame", "RedPanel"
    };

    // This extra color is defined by the four alternative stock themes only.
    // Do not ask Core to resolve a missing color when viewing Default.
    internal static readonly string[] RedPanelThemeIds = { "Dark", "Elysium", "Grey", "NeonNights" };

    internal static bool HasColor(string id, string themeId)
        => id != "RedPanel" || System.Array.IndexOf(RedPanelThemeIds, themeId) >= 0;

    private static readonly string[,] FixedColors =
    {
        { "Light grey", "#D3D3D3" }, { "Dark grey", "#555555" },
        { "Pink", "#FFC0CB" }, { "Hot pink", "#FF69B4" },
        { "Cyan", "#00FFFF" }, { "Soft cyan", "#80D8D8" },
        { "Orange", "#FFA500" }, { "Yellow", "#E1CB73" },
        { "Red", "#F95858" }, { "Green", "#5EB509" },
        { "Blue", "#3363CB" }, { "Teal", "#00B3B3" },
        { "Lavender", "#B19CD9" }, { "Purple", "#9370DB" },
        { "White", "#DEDEDE" }, { "Black", "#252525" }
    };

    internal static void BuildFixed(PanelContentContext context)
    {
        RectTransform flow = CreateFlow(context, "DemoFixedPalette");
        for (int i = 0; i < FixedColors.GetLength(0); i++)
            FrameworkPaletteSwatch.Create(flow, FixedColors[i, 0], FixedColors[i, 1], true);
    }

    internal static void BuildCore(PanelContentContext context)
    {
        RectTransform flow = CreateFlow(context, "DemoCorePalette");
        foreach (string id in CoreColorIds)
            FrameworkPaletteSwatch.Create(flow, id, id, false);
    }

    private static RectTransform CreateFlow(PanelContentContext context, string name)
    {
        return context.AddResponsiveFlow(name, new Vector2(175f, 104f),
            new Vector2(8f, 8f), new RectOffset(8, 8, 8, 8));
    }
}

// Core includes UIThemedImage subclasses in its normal live reload, including
// inactive objects. This only updates a demo swatch and its value/tooltip;
// there is no frame polling, reflection, custom signal subscription or action.
internal sealed class FrameworkPaletteSwatch : UIThemedImage
{
    private Image swatch;
    private TMP_Text hexLabel;
    private UIPermanentTip tip;
    private string colorId, label;
    private bool fixedColor;

    internal static void Create(RectTransform parent, string label, string colorId, bool fixedColor)
    {
        var root = new GameObject("Palette_" + label);
        root.transform.SetParent(parent, false);
        var rect = root.AddComponent<RectTransform>();
        var backdrop = root.AddComponent<Image>();
        FrameworkTheme.ApplyImage(backdrop, "PanelFooter");

        RectTransform colorRect = Child(rect, "Swatch", new Vector2(0f, 0.43f), Vector2.one);
        colorRect.offsetMin = new Vector2(6f, 2f);
        colorRect.offsetMax = new Vector2(-6f, -6f);
        Image colorImage = colorRect.gameObject.AddComponent<Image>();
        colorImage.raycastTarget = false;
        Text(colorRect, "Aa", 20f, Vector2.zero, Vector2.one);
        Text(rect, label, 14f, new Vector2(0f, 0.21f), new Vector2(1f, 0.43f));
        TMP_Text value = Text(rect, string.Empty, 12f, Vector2.zero, new Vector2(1f, 0.21f));

        var binding = colorRect.gameObject.AddComponent<FrameworkPaletteSwatch>();
        binding.swatch = colorImage;
        binding.hexLabel = value;
        binding.label = label;
        binding.colorId = colorId;
        binding.fixedColor = fixedColor;
        binding.tip = UIComponentFactory.SetTip(backdrop, label);
        if (The.UITheme != null) binding.ForceApplyStyle();
    }

    protected override void ApplyTheme()
    {
        // AddComponent can invoke Awake before the explicit targets exist.
        if (swatch == null || hexLabel == null || colorId == null) return;
        if (!fixedColor && !FrameworkThemePalette.HasColor(colorId, The.UITheme.Id))
        {
            swatch.color = Color.clear;
            hexLabel.text = "Not defined";
            if (tip != null) tip.SetTip(label + " is not defined in this theme. No fallback color is invented.");
            return;
        }
        Color color = The.UITheme.GetColor(colorId, swatch);
        swatch.color = color;
        string hex = "#" + (color.a < 1f
            ? ColorUtility.ToHtmlStringRGBA(color) : ColorUtility.ToHtmlStringRGB(color));
        hexLabel.text = hex;
        if (tip != null)
            tip.SetTip(label + " | " + hex + (fixedColor ? " | Fixed comparison color."
                : " | Core color in " + The.UITheme.DisplayName + ".")
                + " Aa uses the theme's normal text color. Transparency blends over PanelFooter. "
                + "Visual reference only; no action or selection.");
    }

    private static RectTransform Child(RectTransform parent, string name, Vector2 min, Vector2 max)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent, false);
        var rect = child.AddComponent<RectTransform>();
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return rect;
    }

    private static TMP_Text Text(RectTransform parent, string value, float size, Vector2 min, Vector2 max)
    {
        RectTransform rect = Child(parent, "Text", min, max);
        rect.offsetMin = new Vector2(6f, 1f);
        rect.offsetMax = new Vector2(-6f, -1f);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = size;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        FrameworkTheme.ApplyText(text);
        return text;
    }
}
