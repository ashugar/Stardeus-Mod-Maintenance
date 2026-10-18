using System;
using Game.Input;
using Game.Systems.UIUX;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

/// <summary>
/// Adds a reusable whole-row click target and disclosure indicator to the
/// pooled native WContent2Header prefab. Configuration is cleared whenever the
/// pooled header is disabled so the decoration cannot leak into Core UI or an
/// ordinary AUMF header.
/// </summary>
internal sealed class CollapsibleHeaderDecorator : MonoBehaviour
{
    private const string OverlayName =
        "AutoUtilities_CollapsibleHeaderOverlay";
    private const string IndicatorName =
        "AutoUtilities_CollapsibleHeaderIndicator";

    private GameObject overlayObject;
    private Image overlayImage;
    private Button button;
    private TooltipHoverRelay tooltipRelay;
    private TextMeshProUGUI indicator;
    private Action clicked;
    private bool clickConsumed;

    internal void Configure(WText header, bool expanded, Action onClicked,
        string tooltip, bool interactable)
    {
        EnsureCreated(header);
        ResetConfiguration();

        overlayObject.SetActive(true);
        indicator.gameObject.SetActive(true);
        indicator.text = expanded
            ? GlyphMap.TriangleDown
            : GlyphMap.TriangleRight;
        tooltipRelay.Text = tooltip;
        overlayImage.raycastTarget = interactable
            || !string.IsNullOrEmpty(tooltip);
        button.interactable = interactable;
        clicked = onClicked;
        clickConsumed = false;
        button.onClick.AddListener(InvokeClick);
    }

    internal void ResetConfiguration()
    {
        BindExistingForReset();
        clicked = null;
        clickConsumed = false;
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.interactable = false;
        }
        if (overlayImage != null)
            overlayImage.raycastTarget = false;
        if (tooltipRelay != null)
            tooltipRelay.Text = null;
        if (indicator != null)
        {
            indicator.text = string.Empty;
            indicator.gameObject.SetActive(false);
        }
        if (overlayObject != null)
            overlayObject.SetActive(false);
    }

    private void EnsureCreated(WText header)
    {
        if (overlayObject == null)
        {
            Transform existing = transform.Find(OverlayName);
            overlayObject = existing != null
                ? existing.gameObject
                : new GameObject(OverlayName);
            if (existing == null)
                overlayObject.transform.SetParent(transform, false);

            RectTransform overlayRect =
                overlayObject.GetComponent<RectTransform>()
                ?? overlayObject.AddComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.offsetMin = Vector2.zero;
            overlayRect.offsetMax = Vector2.zero;
            overlayRect.SetAsLastSibling();

            overlayImage = overlayObject.GetComponent<Image>()
                ?? overlayObject.AddComponent<Image>();
            overlayImage.color = new Color(1f, 1f, 1f, 0.001f);

            button = overlayObject.GetComponent<Button>()
                ?? overlayObject.AddComponent<Button>();
            button.targetGraphic = overlayImage;
            button.navigation = new Navigation
            {
                mode = Navigation.Mode.None
            };

            tooltipRelay =
                overlayObject.GetComponent<TooltipHoverRelay>()
                ?? overlayObject.AddComponent<TooltipHoverRelay>();

            Transform existingIndicator =
                overlayObject.transform.Find(IndicatorName);
            GameObject indicatorObject = existingIndicator != null
                ? existingIndicator.gameObject
                : new GameObject(IndicatorName);
            if (existingIndicator == null)
                indicatorObject.transform.SetParent(overlayObject.transform,
                    false);

            RectTransform indicatorRect =
                indicatorObject.GetComponent<RectTransform>()
                ?? indicatorObject.AddComponent<RectTransform>();
            indicatorRect.anchorMin = new Vector2(0f, 0f);
            indicatorRect.anchorMax = new Vector2(0f, 1f);
            indicatorRect.pivot = new Vector2(0f, 0.5f);
            indicatorRect.anchoredPosition = Vector2.zero;
            indicatorRect.sizeDelta = new Vector2(44f, 0f);

            indicator = indicatorObject.GetComponent<TextMeshProUGUI>()
                ?? indicatorObject.AddComponent<TextMeshProUGUI>();
            indicator.alignment = TextAlignmentOptions.Center;
            indicator.textWrappingMode = TextWrappingModes.NoWrap;
            indicator.raycastTarget = false;
        }

        // Match the indicator to the current native header font and color. The
        // same decorator can survive many pool cycles and Core theme reloads.
        TMP_Text source = header?.Text;
        if (source != null)
        {
            FrameworkTheme.Binding<FrameworkThemedText>(indicator)
                .FollowHeader(indicator, source);
        }
    }

    private void BindExistingForReset()
    {
        // After a development assembly reload, Unity can preserve the named
        // child hierarchy while nonserialized component fields are cleared.
        // Rebind without creating anything so AddHeader and OnDisable can still
        // neutralize a decorator left by the prior assembly instance.
        if (overlayObject != null) return;
        Transform existing = transform.Find(OverlayName);
        if (existing == null) return;

        overlayObject = existing.gameObject;
        overlayImage = overlayObject.GetComponent<Image>();
        button = overlayObject.GetComponent<Button>();
        tooltipRelay = overlayObject.GetComponent<TooltipHoverRelay>();
        Transform existingIndicator = existing.Find(IndicatorName);
        if (existingIndicator != null)
            indicator = existingIndicator.GetComponent<TextMeshProUGUI>();
    }

    private void InvokeClick()
    {
        if (clickConsumed || button == null || !button.interactable) return;
        clickConsumed = true;
        button.interactable = false;
        clicked?.Invoke();
    }

    private void OnDisable()
    {
        ResetConfiguration();
    }

    private void OnDestroy()
    {
        button?.onClick.RemoveAllListeners();
        clicked = null;
    }
}
