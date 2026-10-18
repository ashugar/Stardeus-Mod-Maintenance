using System;
using Game;
using Game.Systems.UIUX;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

// These derive from Core's concrete themed components so its normal reload
// pass includes them, even when inactive. No registry, signal listener, Update,
// or whole-panel descendant scan is needed. Awake may precede Configure: every
// binding is inert until its explicit target is set. Only colors are adopted;
// applying a complete native style would also replace fonts, sizing and sprites.
internal sealed class FrameworkThemedImage : UIThemedImage
{
    private Image target;
    private string id;
    private bool isColor;

    internal void Configure(Image image, string style, bool color)
    {
        target = image;
        id = style;
        isColor = color;
        if (The.UITheme != null) ForceApplyStyle();
    }

    protected override void ApplyTheme()
    {
        if (target == null) return;
        if (isColor) target.color = The.UITheme.GetColor(id, target);
        else
        {
            UIThemeImage style = The.UITheme.GetImage(id, target);
            if (style != null) target.color = style.Color;
        }
    }
}

internal sealed class FrameworkThemedText : UIThemedText
{
    private TMP_Text target;
    private TMP_Text source;
    private string id;
    private bool isColor;

    internal void Configure(TMP_Text text, string style, bool color)
    {
        target = text;
        source = null;
        id = style;
        isColor = color;
        if (The.UITheme != null) ForceApplyStyle();
    }

    internal void FollowHeader(TMP_Text text, TMP_Text header)
    {
        target = text;
        source = header;
        id = null;
        CopyHeader();
    }

    protected override void ApplyTheme()
    {
        if (target == null) return;
        if (source != null)
        {
            // Core does not guarantee iteration order. Apply the source first
            // so the arrow cannot copy the previous theme during a live switch.
            source.GetComponent<UIThemedText>()?.ForceApplyStyle();
            CopyHeader();
        }
        else if (id != null)
        {
            if (isColor) target.color = The.UITheme.GetColor(id, target);
            else
            {
                UIThemeText style = The.UITheme.GetText(id, target);
                if (style != null) target.color = style.Color;
            }
        }
    }

    private void CopyHeader()
    {
        if (source == null || target == null) return;
        if (source.font != null) target.font = source.font;
        target.fontSize = source.fontSize;
        target.fontStyle = source.fontStyle;
        target.color = source.color;
    }
}

internal sealed class FrameworkThemedButton : UIThemedButton
{
    private Button target;
    private TMP_Text[] labels;
    private string id;

    internal void Configure(Button button, string style, TMP_Text[] text)
    {
        target = button;
        id = style;
        labels = text == null ? Array.Empty<TMP_Text>() : (TMP_Text[])text.Clone();
        if (The.UITheme != null) ForceApplyStyle();
    }

    protected override void ApplyTheme()
    {
        if (target == null) return;
        UIThemeButton style = The.UITheme.GetButton(id, target);
        if (style == null) return;
        if (style.Image != null && target.targetGraphic is Image image)
            image.color = style.Image.Color;
        if (style.Selectable != null)
        {
            target.transition = Selectable.Transition.ColorTint;
            style.Selectable.ApplyTo(target, The.UITheme.SelectableFadeTime);
            // Keep unavailable buttons distinct from the theme's normal state,
            // matching the state-card strip. Unity handles interactability
            // transitions (including CanvasGroup changes) without polling.
            ColorBlock colors = target.colors;
            colors.disabledColor = The.UITheme.GetColor("#555555", target);
            target.colors = colors;
        }
        if (style.Text == null) return;
        foreach (TMP_Text text in labels)
            if (text != null) text.color = style.Text.Color;
    }
}

internal sealed class FrameworkThemedSlider : UIThemedSlider
{
    private Slider target;
    private Image background, fill, handle;
    private string id;

    internal void Configure(Slider slider, Image track, Image progress, Image thumb, string style)
    {
        target = slider;
        background = track;
        fill = progress;
        handle = thumb;
        id = style;
        if (The.UITheme != null) ForceApplyStyle();
    }

    protected override void ApplyTheme()
    {
        if (target == null) return;
        UIThemeSlider style = The.UITheme.GetSlider(id, target);
        if (style == null) return;
        if (background != null && style.ImageBackground != null) background.color = style.ImageBackground.Color;
        if (fill != null && style.ImageFill != null) fill.color = style.ImageFill.Color;
        // Core's default handle has no image style: white lets Selectable's
        // theme tint supply its color instead of multiplying an old purple tint.
        if (handle != null) handle.color = style.ImageHandle?.Color ?? Color.white;
        if (style.Selectable != null)
        {
            target.transition = Selectable.Transition.ColorTint;
            style.Selectable.ApplyTo(target, The.UITheme.SelectableFadeTime);
        }
    }
}

internal sealed class FrameworkThemedScrollbar : UIThemedScrollbar
{
    private Scrollbar target;
    private Image background, handle;
    private string id;

    internal void Configure(Scrollbar scrollbar, Image track, Image thumb, string style)
    {
        target = scrollbar;
        background = track;
        handle = thumb;
        id = style;
        if (The.UITheme != null) ForceApplyStyle();
    }

    protected override void ApplyTheme()
    {
        if (target == null) return;
        UIThemeScrollbar style = The.UITheme.GetScrollbar(id, target);
        if (style == null) return;
        if (background != null && style.ImageScroll != null) background.color = style.ImageScroll.Color;
        if (handle != null && style.ImageHandle != null) handle.color = style.ImageHandle.Color;
        if (style.Selectable != null)
        {
            target.transition = Selectable.Transition.ColorTint;
            style.Selectable.ApplyTo(target, The.UITheme.SelectableFadeTime);
        }
    }
}
