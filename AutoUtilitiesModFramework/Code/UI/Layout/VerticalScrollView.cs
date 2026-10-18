using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

/// <summary>References returned for populating and controlling a scroll view.</summary>
public sealed class VerticalScrollViewResult
{
    /// <summary>Top-level rectangle placed in the parent layout.</summary>
    public RectTransform Root { get; internal set; }
    /// <summary>Parent to which the consumer adds scrollable rows.</summary>
    public RectTransform Content { get; internal set; }
    /// <summary>Native scroll component for advanced configuration.</summary>
    public ScrollRect Scroll { get; internal set; }
}

/// <summary>Creates a vertical scroll view with optional position retention.</summary>
public static class VerticalScrollView
{
    /// <summary>Creates a vertical scroll view and optionally restores position.</summary>
    /// <param name="parent">Layout parent receiving the scroll root.</param>
    /// <param name="name">Diagnostic GameObject name.</param>
    /// <param name="minWidth">Minimum layout width.</param>
    /// <param name="preferredWidth">Preferred layout width.</param>
    /// <param name="flexibleWidth">Share of additional available width.</param>
    /// <param name="initialVerticalPosition">Normalized position: 1 top, 0 bottom.</param>
    /// <param name="onVerticalPositionChanged">Optional normalized-position callback.</param>
    /// <returns>Root, content parent, and native scroll component.</returns>
    /// <remarks>
    /// Inside <see cref="FrameworkPanel"/>, prefer
    /// <see cref="PanelContentContext.AddVerticalScroll"/> so cleanup and row
    /// ordering are automatic.
    /// </remarks>
    /// <example><code>
    /// var result = VerticalScrollView.Create(parent, "History",
    ///     300f, 600f, 1f, savedPosition, p =&gt; savedPosition = p);
    /// row.transform.SetParent(result.Content, false);
    /// </code></example>
    public static VerticalScrollViewResult Create(RectTransform parent,
        string name, float minWidth, float preferredWidth,
        float flexibleWidth, float initialVerticalPosition = 1f,
        Action<float> onVerticalPositionChanged = null)
    {
        var rootObject = new GameObject(name);
        rootObject.transform.SetParent(parent, false);
        var rootRect = rootObject.AddComponent<RectTransform>();
        rootRect.localScale = Vector3.one;

        var rootSize = rootObject.AddComponent<LayoutElement>();
        rootSize.minWidth = minWidth;
        rootSize.preferredWidth = preferredWidth;
        rootSize.flexibleWidth = flexibleWidth;
        rootSize.minHeight = 0f;
        rootSize.preferredHeight = 0f;
        rootSize.flexibleHeight = 1f;

        var viewportObject = new GameObject("Viewport");
        viewportObject.transform.SetParent(rootRect, false);
        var viewportRect = viewportObject.AddComponent<RectTransform>();
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = new Vector2(-10f, 0f);
        var viewportImage = viewportObject.AddComponent<Image>();
        viewportImage.color = Color.clear;
        viewportImage.raycastTarget = true;
        viewportObject.AddComponent<RectMask2D>();

        var contentObject = new GameObject("Content");
        contentObject.transform.SetParent(viewportRect, false);
        var contentRect = contentObject.AddComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = Vector2.zero;
        var contentLayout = contentObject.AddComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 7f;
        contentLayout.childAlignment = TextAnchor.UpperCenter;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;
        var contentSize = contentObject.AddComponent<ContentSizeFitter>();
        contentSize.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        contentSize.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scrollbarObject = new GameObject("Scrollbar");
        scrollbarObject.transform.SetParent(rootRect, false);
        var scrollbarRect = scrollbarObject.AddComponent<RectTransform>();
        scrollbarRect.anchorMin = new Vector2(1f, 0f);
        scrollbarRect.anchorMax = Vector2.one;
        scrollbarRect.pivot = new Vector2(1f, 0.5f);
        scrollbarRect.offsetMin = new Vector2(-8f, 0f);
        scrollbarRect.offsetMax = Vector2.zero;
        var scrollbarBackground = scrollbarObject.AddComponent<Image>();

        var handleObject = new GameObject("Handle");
        handleObject.transform.SetParent(scrollbarRect, false);
        var handleRect = handleObject.AddComponent<RectTransform>();
        handleRect.anchorMin = Vector2.zero;
        handleRect.anchorMax = Vector2.one;
        handleRect.offsetMin = Vector2.zero;
        handleRect.offsetMax = Vector2.zero;
        var handleImage = handleObject.AddComponent<Image>();
        var scrollbar = scrollbarObject.AddComponent<Scrollbar>();
        scrollbar.handleRect = handleRect;
        scrollbar.targetGraphic = handleImage;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        FrameworkTheme.ApplyScrollbar(scrollbar, scrollbarBackground, handleImage);

        var scroll = rootObject.AddComponent<ScrollRect>();
        scroll.content = contentRect;
        scroll.viewport = viewportRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.scrollSensitivity = 32f;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

        rootObject.AddComponent<ScrollPositionRestorer>().Initialize(
            scroll, contentRect, initialVerticalPosition,
            onVerticalPositionChanged);

        return new VerticalScrollViewResult
        {
            Root = rootRect,
            Content = contentRect,
            Scroll = scroll
        };
    }

    private sealed class ScrollPositionRestorer : MonoBehaviour
    {
        private ScrollRect scroll;
        private RectTransform content;
        private float initialPosition;
        private Action<float> positionChanged;

        internal void Initialize(ScrollRect targetScroll,
            RectTransform targetContent, float initialVerticalPosition,
            Action<float> onVerticalPositionChanged)
        {
            scroll = targetScroll;
            content = targetContent;
            initialPosition = Mathf.Clamp01(initialVerticalPosition);
            positionChanged = onVerticalPositionChanged;
            StartCoroutine(RestoreAfterLayout());
        }

        private IEnumerator RestoreAfterLayout()
        {
            yield return new WaitForEndOfFrame();
            if (scroll == null || content == null) yield break;
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            scroll.verticalNormalizedPosition = initialPosition;
            if (positionChanged != null)
                scroll.onValueChanged.AddListener(OnScrollValueChanged);
        }

        private void OnScrollValueChanged(Vector2 position) =>
            positionChanged?.Invoke(Mathf.Clamp01(position.y));

        private void OnDestroy()
        {
            if (scroll != null)
                scroll.onValueChanged.RemoveListener(OnScrollValueChanged);
        }
    }
}
