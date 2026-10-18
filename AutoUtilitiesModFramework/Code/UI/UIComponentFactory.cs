using System;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

/// <summary>Low-level helpers for custom UI that complements FrameworkPanel.</summary>
public static class UIComponentFactory
{
    /// <summary>Depth-first search for a named descendant.</summary>
    /// <param name="parent">Root whose descendants are searched.</param>
    /// <param name="name">Exact Unity GameObject name.</param>
    /// <returns>The first match, or <see langword="null"/>.</returns>
    /// <example><code>
    /// Transform handle = UIComponentFactory.FindChildRecursive(
    ///     popup.transform, "ResizeHandle");
    /// </code></example>
    public static Transform FindChildRecursive(Transform parent, string name)
    {
        if (parent == null) return null;
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name) return child;
            Transform found = FindChildRecursive(child, name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Adds or updates a native permanent tooltip on a component.</summary>
    /// <returns>The tooltip, or <see langword="null"/> for no target/text.</returns>
    /// <example><code>
    /// UIComponentFactory.SetTip(button, "Runs the scan immediately.");
    /// </code></example>
    public static UIPermanentTip SetTip(Component target, string text)
    {
        if (target == null) return null;
        UIPermanentTip tip = target.GetComponent<UIPermanentTip>();
        if (string.IsNullOrEmpty(text))
        {
            // Native widgets are pooled. Clear a framework tooltip when the
            // same object is reused for a row that does not specify one.
            tip?.SetTip(string.Empty);
            return tip;
        }
        tip ??= target.gameObject.AddComponent<UIPermanentTip>();
        tip.SetTip(text);
        return tip;
    }

    /// <summary>Creates a styled tab button under a custom tab-row parent.</summary>
    /// <remarks>
    /// Prefer <see cref="PanelContentContext.AddTabs"/> for complete panel tab
    /// rows. This method is useful when composing a custom layout.
    /// Its legacy colors remain consumer-overridable. Use CreateThemedTabButton
    /// to opt a custom tab into live Core theme updates.
    /// </remarks>
    /// <example><code>
    /// UIComponentFactory.CreateTabButton(tabRow, "Status", activeTab == "status",
    ///     () =&gt; { activeTab = "status"; panel.Refresh(); });
    /// </code></example>
    public static Button CreateTabButton(RectTransform parent, string label,
        bool active, Action clicked, float minWidth = 90f,
        float preferredWidth = 120f)
    {
        var root = new GameObject("Tab_" + label);
        root.transform.SetParent(parent, false);
        RectTransform rect = root.AddComponent<RectTransform>();
        var image = root.AddComponent<Image>();
        image.color = active ? new Color(0.35f, 0.22f, 0.65f, 0.95f)
            : new Color(0.20f, 0.12f, 0.35f, 0.75f);
        var button = root.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(() => clicked?.Invoke());
        var size = root.AddComponent<LayoutElement>();
        // Preserve compact tabs for narrow panels while allowing longer names
        // such as "Production Goals" to grow enough to remain legible.
        float labelWidth = 48f + (label?.Length ?? 0) * 9.5f;
        size.minWidth = Mathf.Max(minWidth, labelWidth);
        size.preferredWidth = Mathf.Max(preferredWidth, size.minWidth);
        size.flexibleWidth = 0f;

        var textObject = new GameObject("Text");
        textObject.transform.SetParent(rect, false);
        RectTransform textRect = textObject.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        var text = textObject.AddComponent<TextMeshProUGUI>();
        text.text = active ? "[ " + label + " ]" : label;
        text.alignment = TextAlignmentOptions.Center;
        text.fontSize = 18f;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        return button;
    }

    /// <summary>
    /// Creates a custom tab with Core Tab/Tab_Active colors, including its label.
    /// Available since AUMF 1.4.0. The parent row remains consumer-owned; style it explicitly
    /// with FrameworkTheme.ApplyImage or use PanelContentContext.AddTabs.
    /// </summary>
    public static Button CreateThemedTabButton(RectTransform parent, string label,
        bool active, Action clicked, float minWidth = 90f,
        float preferredWidth = 120f)
    {
        if (parent == null) throw new ArgumentNullException(nameof(parent));
        Button button = CreateTabButton(parent, label, active, clicked,
            minWidth, preferredWidth);
        FrameworkTheme.ApplyButton(button, active ? "Tab_Active" : "Tab",
            button.GetComponentInChildren<TextMeshProUGUI>());
        return button;
    }

    /// <summary>
    /// Creates a theme-aware state card under a consumer-owned layout (AUMF 1.4.0).
    /// Parent it under AddResponsiveFlow for sizing and automatic panel cleanup.
    /// Consumers supply content, state and callbacks; no gameplay policy is inferred.
    /// </summary>
    public static FrameworkStateCard CreateStateCard(RectTransform parent,
        string title, string detail, FrameworkCardState state,
        Action clicked = null, string tooltip = null)
    {
        return FrameworkStateCard.Create(parent, title, detail, state, clicked, tooltip);
    }
}
