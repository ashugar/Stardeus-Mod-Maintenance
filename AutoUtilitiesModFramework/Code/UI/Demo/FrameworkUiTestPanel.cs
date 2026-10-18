using System.Collections.Generic;
using Game.Constants;
using Game.Systems.UIUX;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

// LIVING EXAMPLE FOR MOD AUTHORS
// This class intentionally uses the same public API available to dependent
// mods. Enable it with EnableDemonstrationUi in TopBarWidgetSys.cs, rebuild,
// and click the cyan copy/settings top-bar widget. Each Build... method shows
// a focused usage pattern that can be copied into a consumer mod.
internal sealed class FrameworkUiTestPanel
{
    private static readonly PanelTabDefinition[] Tabs =
    {
        new PanelTabDefinition { Id = "Controls", Label = "Controls" },
        new PanelTabDefinition { Id = "Layout", Label = "Layout" },
        new PanelTabDefinition { Id = "Sections", Label = "Sections" },
        new PanelTabDefinition { Id = "Scroll", Label = "Scroll" },
        new PanelTabDefinition { Id = "Theme", Label = "Theme" }
    };

    private readonly FrameworkPanel panel;
    private readonly System.Action refreshWidget;
    private string activeTab = "Controls";
    private bool enabled = true;
    private int actionCount;
    private string committedText = "Not committed yet";
    private float scrollPosition = 1f;
    private float sectionScrollPosition = 1f;
    private bool primarySectionExpanded = true;
    private bool secondarySectionExpanded;
    private bool nestedSectionExpanded = true;
    private float themeSliderValue = 50f;
    private bool corePaletteExpanded;

    internal FrameworkUiTestPanel(System.Action refreshWidget)
    {
        this.refreshWidget = refreshWidget;
        panel = new FrameworkPanel(new FrameworkPanelOptions
        {
            Id = "AutoUtilitiesModFramework.UiTest",
            Title = "Auto Utilities UI Test",
            // Keep all five demonstration tabs reachable at minimum width.
            MinSize = new Vector2(700f, 300f),
            MaxSize = new Vector2(1400f, 1000f),
            RememberGeometry = true
        }, Build);
    }

    internal bool IsOpen => panel.IsOpen;
    internal bool Enabled => enabled;
    internal int ActionCount => actionCount;

    internal void Toggle() => panel.Toggle();
    internal void Dispose() => panel.Dispose();

    private void Build(PanelContentContext context)
    {
        context.AddTabs(Tabs, activeTab, value => activeTab = value);
        switch (activeTab)
        {
            case "Layout": BuildLayout(context); break;
            case "Sections": BuildSections(context); break;
            case "Scroll": BuildScroll(context); break;
            case "Theme": BuildTheme(context); break;
            default: BuildControls(context); break;
        }
    }

    private void BuildControls(PanelContentContext context)
    {
        context.AddHeader("Native controls and callbacks");
        context.AddInfo(IconId.CInfo, "Framework version",
            AutoUtilitiesApi.FrameworkVersion,
            "The compiled public API version exposed to dependent mods.");
        context.AddToggle(enabled ? IconId.CCheck : "Icons/Color/NoEnergy",
            "Demonstration state", enabled ? "On" : "Off", enabled, value =>
            {
                enabled = value;
                refreshWidget();
                panel.Refresh();
            }, "Exercises a native toggle, consumer callback, panel rebuild, "
               + "widget tint update, and live tooltip refresh.");
        context.AddButton(IconId.CCog, "Run demonstration action",
            actionCount.ToString(), () =>
            {
                actionCount++;
                refreshWidget();
                panel.Refresh();
            }, "Exercises a native button and state-driven rebuild.");
        context.AddInput(IconId.CInfo, "Enter test text",
            string.Empty, value =>
            {
                committedText = string.IsNullOrWhiteSpace(value)
                    ? "(empty)" : value;
                panel.Refresh();
            }, TMP_InputField.ContentType.Standard,
            "Commits through the native Stardeus input submission path.");
        context.AddInfo(IconId.CInfo, "Last committed input", committedText);
    }

    private static void BuildLayout(PanelContentContext context)
    {
        context.AddHeader("Responsive flow layout");
        RectTransform flow = context.AddResponsiveFlow("DemoFlow",
            new Vector2(150f, 76f), new Vector2(8f, 8f),
            new RectOffset(8, 8, 8, 8));
        for (int i = 1; i <= 10; i++)
            CreateCard(flow, "Card " + i, i % 2 == 0);
        context.AddInfo(IconId.CInfo, "Resize test",
            "Drag the lower-right handle",
            "Cards should reflow immediately as the panel width changes.");
    }

    private void BuildSections(PanelContentContext context)
    {
        context.AddHeader("Static header remains non-interactive");
        context.AddCollapsibleHeader("Initially expanded section",
            primarySectionExpanded,
            value => primarySectionExpanded = value,
            primarySectionExpanded
                ? "Collapse the initially expanded test section."
                : "Expand the initially expanded test section.");
        if (primarySectionExpanded)
        {
            context.AddInfo(IconId.CInfo, "Expanded content", "Visible",
                "The consumer renders this row only while its stored state is expanded.");
        }

        context.AddCollapsibleHeader("Initially collapsed section",
            secondarySectionExpanded,
            value => secondarySectionExpanded = value,
            secondarySectionExpanded
                ? "Collapse the initially collapsed test section."
                : "Expand the initially collapsed test section.");
        if (secondarySectionExpanded)
        {
            context.AddInfo(IconId.CInfo, "Second section", "Visible");
        }

        context.AddCollapsibleHeader("Forced open, temporarily disabled", true,
            _ => { },
            "Demonstrates a section that remains open and non-interactive while a consumer filter is active.",
            false);

        context.AddHeader("Collapsible header inside managed scrolling");
        VerticalScrollViewResult scroll = context.AddVerticalScroll(
            "DemoSectionScroll", 260f, 500f, 1f, sectionScrollPosition,
            value => sectionScrollPosition = value);
        var size = scroll.Root.GetComponent<LayoutElement>();
        size.minHeight = 240f;
        size.preferredHeight = 320f;

        int row = 0;
        context.AddCollapsibleHeader(scroll.Content, row++,
            "Nested scroll section", nestedSectionExpanded,
            value => nestedSectionExpanded = value,
            nestedSectionExpanded
                ? "Collapse the section without losing the scroll position."
                : "Expand the section without losing the scroll position.");
        if (nestedSectionExpanded)
        {
            for (int i = 1; i <= 12; i++)
            {
                WIconText2 item = UISpawn.Widget<WIconText2>(context.Lifetime,
                    UIPrefabId.WContent2Text2, scroll.Content, row++);
                item.Load(IconId.CInfo, "Nested native row " + i,
                    i % 2 == 0 ? "Even" : "Odd");
            }
        }
        context.AddInfo(IconId.CInfo, "Fixed row after flexible scroll",
            "Remains visible",
            "The scroll consumes spare resized height without covering this row.");
    }

    private void BuildScroll(PanelContentContext context)
    {
        context.AddHeader("Vertical scrolling and position restoration");
        VerticalScrollViewResult scroll = context.AddVerticalScroll(
            "DemoScroll", 260f, 500f, 1f, scrollPosition,
            value => scrollPosition = value);
        var size = scroll.Root.GetComponent<LayoutElement>();
        size.minHeight = 260f;
        size.preferredHeight = 360f;
        for (int i = 1; i <= 24; i++)
            CreateScrollRow(scroll.Content, i);
    }

    private static void CreateCard(RectTransform parent, string label,
        bool highlighted)
    {
        UIComponentFactory.CreateStateCard(parent, label, "Resize test",
            highlighted ? FrameworkCardState.Selected : FrameworkCardState.Neutral,
            tooltip: label + " is managed by ResponsiveFlowLayout.");
    }

    private static void CreateScrollRow(RectTransform parent, int index)
    {
        var root = new GameObject("ScrollRow" + index);
        root.transform.SetParent(parent, false);
        RectTransform rect = root.AddComponent<RectTransform>();
        var size = root.AddComponent<LayoutElement>();
        size.minHeight = 38f;
        size.preferredHeight = 38f;
        var image = root.AddComponent<Image>();
        FrameworkTheme.ApplyImage(image, index % 2 == 0 ? "PanelContent" : "PanelFooter");
        AddCenteredText(rect, "Scrollable test row " + index, 15f);
    }

    private void BuildTheme(PanelContentContext context)
    {
        context.AddHeader("Theme reference - AUMF " + AutoUtilitiesApi.FrameworkVersion);
        context.AddInfo(IconId.CInfo, "Change Theme in game settings", "Updates live",
            "Test Default, Elysium, Dark, Grey and Neon Nights. Reopen, resize, "
            + "and switch tabs; colors should follow the selected theme.");
        RectTransform states = context.AddResponsiveFlow("DemoThemeCards",
            new Vector2(175f, 100f), new Vector2(8f, 8f), new RectOffset(8, 8, 8, 8));
        foreach (FrameworkCardState state in System.Enum.GetValues(typeof(FrameworkCardState)))
        {
            UIComponentFactory.CreateStateCard(states, state.ToString(), "Theme + state",
                state, () => { actionCount++; panel.Refresh(); },
                "Enabled/Disabled are setting states. Only Unavailable blocks a supplied click.");
        }
        FrameworkStateCard toggle = null;
        toggle = UIComponentFactory.CreateStateCard(states, "Toggle in place", "Click to toggle",
            FrameworkCardState.Enabled, () => toggle.SetState(
                toggle.State == FrameworkCardState.Enabled
                    ? FrameworkCardState.Disabled : FrameworkCardState.Enabled));

        context.AddHeader("Buttons and legacy color override");
        RectTransform buttons = context.AddResponsiveFlow("DemoThemeButtons",
            new Vector2(180f, 42f), new Vector2(8f, 8f), new RectOffset(8, 8, 8, 8));
        UIComponentFactory.CreateThemedTabButton(buttons, "Normal", false,
            () => { actionCount++; panel.Refresh(); });
        UIComponentFactory.CreateThemedTabButton(buttons, "Selected", true, () => { });
        Button disabled = UIComponentFactory.CreateThemedTabButton(buttons, "Unavailable", false, () => { });
        disabled.interactable = false;
        Button danger = UIComponentFactory.CreateTabButton(buttons, "Danger", false, () => { });
        FrameworkTheme.ApplyButton(danger, "Red", danger.GetComponentInChildren<TMP_Text>());
        Button legacy = UIComponentFactory.CreateTabButton(buttons, "Legacy custom", false, () => { });
        legacy.GetComponent<Image>().color = new Color(0.12f, 0.65f, 0.20f, 1f);
        UIComponentFactory.SetTip(legacy,
            "This intentional legacy color must stay green across theme changes, like RTC graph nodes.");

        GameObject sliderRow = context.CreateOwned("DemoThemeSliderRow");
        RectTransform rowRect = sliderRow.AddComponent<RectTransform>();
        var rowSize = sliderRow.AddComponent<LayoutElement>();
        rowSize.minHeight = rowSize.preferredHeight = 100f;
        FrameworkTheme.ApplyImage(sliderRow.AddComponent<Image>(), "PanelFooter");
        var labelObject = new GameObject("SliderLabel");
        labelObject.transform.SetParent(rowRect, false);
        var labelRect = labelObject.AddComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0f, 0.6f);
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
        TMP_Text sliderLabel = AddCenteredText(labelRect, "Theme slider: " + themeSliderValue.ToString("0"), 17f);
        var sliderObject = new GameObject("CustomSlider");
        sliderObject.transform.SetParent(rowRect, false);
        var sliderRect = sliderObject.AddComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0.06f, 0.12f);
        sliderRect.anchorMax = new Vector2(0.94f, 0.55f);
        sliderRect.offsetMin = sliderRect.offsetMax = Vector2.zero;
        Image background = SliderPart(sliderRect, "Track", new Vector2(0f, 0.42f), new Vector2(1f, 0.58f));
        // Unity drives both fill anchor axes. Constrain its parent instead so
        // the fill stays centered at half the slider height at every value.
        var fillAreaObject = new GameObject("FillArea");
        fillAreaObject.transform.SetParent(sliderRect, false);
        var fillArea = fillAreaObject.AddComponent<RectTransform>();
        fillArea.anchorMin = new Vector2(0f, 0.25f);
        fillArea.anchorMax = new Vector2(1f, 0.75f);
        fillArea.offsetMin = fillArea.offsetMax = Vector2.zero;
        Image fill = SliderPart(fillArea, "Progress", Vector2.zero, Vector2.one);
        Image handle = SliderPart(sliderRect, "Thumb", Vector2.zero, new Vector2(0f, 1f));
        handle.rectTransform.sizeDelta = new Vector2(16f, 0f);
        Slider slider = sliderObject.AddComponent<Slider>();
        slider.minValue = 0f;
        slider.maxValue = 100f;
        slider.wholeNumbers = true;
        slider.fillRect = fill.rectTransform;
        slider.handleRect = handle.rectTransform;
        slider.targetGraphic = handle;
        slider.value = themeSliderValue;
        slider.onValueChanged.AddListener(value =>
        {
            themeSliderValue = value;
            sliderLabel.text = "Theme slider: " + value.ToString("0");
        });
        FrameworkTheme.ApplySlider(slider, background, fill, handle);
        context.AddInfo(IconId.CInfo, "Theme test button clicks", actionCount.ToString());
        BuildPalette(context);
    }

    private void BuildPalette(PanelContentContext context)
    {
        context.AddHeader("Color palette - fixed comparison colors");
        context.AddInfo(IconId.CInfo, "Fixed colors and text contrast", "Reference only",
            "These swatch colors stay fixed. The Aa sample uses the current theme's normal text color. "
            + "No sample changes the actual Unavailable button or any setting.");
        FrameworkThemePalette.BuildFixed(context);
        context.AddCollapsibleHeader("All Core theme colors ("
            + FrameworkThemePalette.CoreColorIds.Length + ")", corePaletteExpanded,
            value => corePaletteExpanded = value,
            "Expand to compare every named color in the current Core fallback palette. "
            + "Swatches and hexadecimal values update live when the theme changes.");
        if (corePaletteExpanded) FrameworkThemePalette.BuildCore(context);
    }

    private static Image SliderPart(RectTransform parent, string name, Vector2 min, Vector2 max)
    {
        var child = new GameObject(name);
        child.transform.SetParent(parent, false);
        var rect = child.AddComponent<RectTransform>();
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        return child.AddComponent<Image>();
    }

    private static TMP_Text AddCenteredText(RectTransform parent, string value,
        float fontSize)
    {
        var textObject = new GameObject("Text");
        textObject.transform.SetParent(parent, false);
        RectTransform rect = textObject.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var text = textObject.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        FrameworkTheme.ApplyText(text);
        return text;
    }
}
