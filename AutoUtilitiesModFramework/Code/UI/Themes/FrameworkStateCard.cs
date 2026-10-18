using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

/// <summary>Visual state only; consumers decide its gameplay meaning.</summary>
public enum FrameworkCardState
{
    /// <summary>Ordinary unselected card.</summary>
    Neutral,
    /// <summary>Selected choice, using the theme's accent and inverse text.</summary>
    Selected,
    /// <summary>Enabled setting; still clickable to turn it off.</summary>
    Enabled,
    /// <summary>Disabled setting; still clickable to turn it on.</summary>
    Disabled,
    /// <summary>Unavailable choice; temporarily non-interactive.</summary>
    Unavailable,
    /// <summary>Consumer-defined warning.</summary>
    Warning,
    /// <summary>Consumer-defined failure or dangerous action.</summary>
    Danger
}

/// <summary>
/// Theme-aware card, available since AUMF 1.4.0. The parent owns its lifetime. Create
/// beneath an AddResponsiveFlow/CreateOwned root, or destroy it with its custom UI.
/// </summary>
public sealed class FrameworkStateCard : MonoBehaviour
{
    private Image body, header, marker;
    private Button button;
    private Action clicked;

    /// <summary>Card layout rectangle.</summary>
    public RectTransform Rect => (RectTransform)transform;
    /// <summary>Consumer-owned title content. Its color follows the theme.</summary>
    public TMP_Text TitleText { get; private set; }
    /// <summary>Consumer-owned detail content. Its color follows the theme.</summary>
    public TMP_Text DetailText { get; private set; }
    /// <summary>Current visual state.</summary>
    public FrameworkCardState State { get; private set; }
    private TMP_Text statusText;

    /// <summary>
    /// Changes the visual state without rebuilding or changing callbacks.
    /// Disabled means a setting is off; only Unavailable blocks its click.
    /// </summary>
    public void SetState(FrameworkCardState state)
    {
        if (!Enum.IsDefined(typeof(FrameworkCardState), state))
            throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
        bool selected = state == FrameworkCardState.Selected;
        string textStyle = selected ? "UnsizedInverse" : "Unsized";
        if (selected)
        {
            FrameworkTheme.ApplyImageColor(body, "Accent");
            FrameworkTheme.ApplyImageColor(header, "Accent+L5");
        }
        else
        {
            FrameworkTheme.ApplyImage(body, "PanelContent");
            FrameworkTheme.ApplyImage(header, "PanelHeader");
        }
        string markerColor;
        switch (state)
        {
            case FrameworkCardState.Enabled: markerColor = "Green"; break;
            case FrameworkCardState.Disabled:
            case FrameworkCardState.Danger: markerColor = "Red"; break;
            // Stable semantic strips: do not inherit a theme accent or the
            // translucent Core Grey, which can obscure the state distinction.
            case FrameworkCardState.Warning: markerColor = "#FFA500"; break;
            case FrameworkCardState.Unavailable: markerColor = "#555555"; break;
            case FrameworkCardState.Selected: markerColor = "Black"; break;
            default: markerColor = "#D3D3D3"; break;
        }
        FrameworkTheme.ApplyImageColor(marker, markerColor);
        FrameworkTheme.ApplyText(TitleText, textStyle);
        FrameworkTheme.ApplyText(DetailText, textStyle);
        FrameworkTheme.ApplyText(statusText, textStyle);
        statusText.text = state == FrameworkCardState.Neutral ? "Available" : state.ToString();
        button.interactable = clicked != null && state != FrameworkCardState.Unavailable;
    }

    internal static FrameworkStateCard Create(RectTransform parent, string title,
        string detail, FrameworkCardState state, Action onClick, string tooltip)
    {
        if (parent == null) throw new ArgumentNullException(nameof(parent));
        if (!Enum.IsDefined(typeof(FrameworkCardState), state))
            throw new ArgumentOutOfRangeException(nameof(state));
        var root = new GameObject("AutoUtilities_StateCard");
        root.transform.SetParent(parent, false);
        var rect = root.AddComponent<RectTransform>();
        var card = root.AddComponent<FrameworkStateCard>();
        card.body = root.AddComponent<Image>();
        card.button = root.AddComponent<Button>();
        card.button.targetGraphic = card.body;
        // State colors must remain independent from selection/focus tint.
        card.button.transition = Selectable.Transition.None;
        card.clicked = onClick;
        card.button.onClick.AddListener(card.OnClick);
        var headerRect = Child(rect, "Header", new Vector2(0f, 0.68f), Vector2.one);
        card.header = headerRect.gameObject.AddComponent<Image>();
        card.header.raycastTarget = false;
        card.TitleText = Text(headerRect, title, 15f, Vector2.zero, Vector2.one);
        card.TitleText.fontStyle = FontStyles.Bold;
        card.DetailText = Text(rect, detail, 13f, new Vector2(0f, 0.24f), new Vector2(1f, 0.68f));
        card.statusText = Text(rect, string.Empty, 12f, Vector2.zero, new Vector2(1f, 0.24f));
        RectTransform markerRect = Child(rect, "StateMarker", Vector2.zero, new Vector2(0f, 1f));
        markerRect.pivot = new Vector2(0f, 0.5f);
        markerRect.sizeDelta = new Vector2(4f, 0f);
        card.marker = markerRect.gameObject.AddComponent<Image>();
        card.marker.raycastTarget = false;
        UIComponentFactory.SetTip(card.button, tooltip);
        card.SetState(state);
        return card;
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
        rect.offsetMin = new Vector2(8f, 1f);
        rect.offsetMax = new Vector2(-6f, -1f);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.text = value ?? string.Empty;
        text.fontSize = size;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        return text;
    }

    private void OnClick() => clicked?.Invoke();

    private void OnDestroy()
    {
        clicked = null;
        if (button != null) button.onClick.RemoveListener(OnClick);
    }
}
