using System;
using Game.Systems.UIUX;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

/// <summary>
/// Opt-in theme colors for custom, non-pooled UI, available since AUMF 1.4.0.
/// Bind once after constructing a control; Core reapplies it on theme reload.
/// Text geometry, sprites, values, callbacks, and interactability are preserved.
/// Buttons/sliders/scrollbars opt into Unity's ColorTint transition.
/// </summary>
public static class FrameworkTheme
{
    /// <summary>Colors a custom image using a Core Images style ID.</summary>
    public static void ApplyImage(Image target, string style = "PanelContent")
    {
        RequireId(style);
        Binding<FrameworkThemedImage>(target).Configure(target, style, false);
    }

    /// <summary>Colors a custom image using a Core color ID or a supported #RRGGBB[AA] literal.</summary>
    public static void ApplyImageColor(Image target, string colorId)
    {
        RequireId(colorId);
        Binding<FrameworkThemedImage>(target).Configure(target, colorId, true);
    }

    /// <summary>Colors custom text using a Core Texts style, preserving typography.</summary>
    public static void ApplyText(TMP_Text target, string style = "Unsized")
    {
        RequireId(style);
        Binding<FrameworkThemedText>(target).Configure(target, style, false);
    }

    /// <summary>Colors custom text using a semantic Core color ID.</summary>
    public static void ApplyTextColor(TMP_Text target, string colorId)
    {
        RequireId(colorId);
        Binding<FrameworkThemedText>(target).Configure(target, colorId, true);
    }

    /// <summary>
    /// Themes a custom button and only the explicitly supplied labels. Styles
    /// include Panel, Red, Tab, and Tab_Active. Icons and nested controls are untouched.
    /// Apply again to change its role; do not also bind its background/labels separately.
    /// </summary>
    public static void ApplyButton(Button target, string style = "Panel",
        params TMP_Text[] labels)
    {
        RequireId(style);
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (!(target.targetGraphic is Image))
            throw new ArgumentException("The button needs an Image targetGraphic.", nameof(target));
        foreach (TMP_Text label in labels ?? Array.Empty<TMP_Text>())
        {
            if (label == null || !label.transform.IsChildOf(target.transform))
                throw new ArgumentException("Button labels must belong to the button.", nameof(labels));
            if (label.GetComponent<UIThemedBase>() != null)
                throw new ArgumentException("A button label already has its own theme binding.", nameof(labels));
        }
        Binding<FrameworkThemedButton>(target).Configure(target, style, labels);
    }

    /// <summary>
    /// Themes a custom slider using explicit part references; no hierarchy names
    /// are required. The handle must already be its targetGraphic.
    /// </summary>
    public static void ApplySlider(Slider target, Image background, Image fill,
        Image handle, string style = "Accent")
    {
        RequireId(style);
        RequireParts(target, background, handle);
        RequireChild(target, fill);
        Binding<FrameworkThemedSlider>(target).Configure(target, background, fill, handle, style);
    }

    /// <summary>Themes a custom scrollbar's explicit background and handle.</summary>
    public static void ApplyScrollbar(Scrollbar target, Image background,
        Image handle, string style = "Default")
    {
        RequireId(style);
        RequireParts(target, background, handle);
        Binding<FrameworkThemedScrollbar>(target).Configure(target, background, handle, style);
    }

    internal static T Binding<T>(Component target) where T : UIThemedBase
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        UIThemedBase existing = target.GetComponent<UIThemedBase>();
        if (existing != null && !(existing is T))
            throw new ArgumentException("Use theme helpers only on custom controls without a native or competing theme binding.", nameof(target));
        return existing as T ?? target.gameObject.AddComponent<T>();
    }

    private static void RequireId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A Core theme style or color ID is required.", nameof(id));
    }

    private static void RequireParts(Selectable target, Image background, Image handle)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        RequireChild(target, background);
        RequireChild(target, handle);
        if (target.targetGraphic != handle)
            throw new ArgumentException("The handle must already be the control's targetGraphic.", nameof(handle));
    }

    private static void RequireChild(Component target, Component part)
    {
        if (part == null || !part.transform.IsChildOf(target.transform))
            throw new ArgumentException("Theme parts must belong to their control.", nameof(part));
    }
}
