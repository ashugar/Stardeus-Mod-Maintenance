using Game.Systems.UIUX;
using UnityEngine;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

/// <summary>
/// Gives a framework panel's single managed content block at least the height
/// of the native popup viewport so flexible children can consume resized space.
/// </summary>
/// <remarks>
/// This component lives only on an AUMF-owned panel instance. It restores the
/// pooled content block before despawn and is removed before the popup returns
/// to Stardeus's global pool.
/// </remarks>
internal sealed class FrameworkPanelContentHeightFitter : MonoBehaviour
{
    private const float ChangeThreshold = 0.25f;

    private RectTransform viewport;
    private RectTransform contentBlock;
    private LayoutElement contentBlockSize;
    private bool addedContentBlockSize;
    private float originalMinHeight;
    private HorizontalOrVerticalLayoutGroup contentLayout;
    private bool originalChildForceExpandHeight;
    private bool adjustedContentLayout;

    internal void Initialize(RectTransform targetViewport)
    {
        viewport = targetViewport;
    }

    internal void Bind(WContentBlock block)
    {
        ClearContentBlock();
        if (block == null) return;

        contentBlock = block.Rect;
        contentBlockSize = contentBlock.GetComponent<LayoutElement>();
        addedContentBlockSize = contentBlockSize == null;
        if (addedContentBlockSize)
            contentBlockSize = contentBlock.gameObject.AddComponent<LayoutElement>();
        originalMinHeight = contentBlockSize.minHeight;

        // The pooled WContentBlock layout normally forces every child to
        // consume spare height. That is appropriate for some native popups,
        // but a FrameworkPanel uses LayoutElement.flexibleHeight to distinguish
        // fixed rows from the children that should grow with the viewport.
        // Override only while this framework-owned block is bound and restore
        // the pooled component before its lifetime is disposed.
        contentLayout = block.Container
            ?.GetComponent<HorizontalOrVerticalLayoutGroup>();
        if (contentLayout != null)
        {
            originalChildForceExpandHeight =
                contentLayout.childForceExpandHeight;
            contentLayout.childForceExpandHeight = false;
            adjustedContentLayout = true;
            LayoutRebuilder.MarkLayoutForRebuild(block.Container);
        }
        RefreshNow();
    }

    internal void ClearContentBlock()
    {
        RectTransform layoutParent = contentBlock?.parent as RectTransform;
        RectTransform layoutContainer = contentLayout?.transform
            as RectTransform;

        if (adjustedContentLayout && contentLayout != null)
            contentLayout.childForceExpandHeight =
                originalChildForceExpandHeight;

        if (contentBlockSize != null)
        {
            if (addedContentBlockSize)
                UnityEngine.Object.DestroyImmediate(contentBlockSize);
            else
                contentBlockSize.minHeight = originalMinHeight;
        }

        contentBlock = null;
        contentBlockSize = null;
        addedContentBlockSize = false;
        originalMinHeight = 0f;
        contentLayout = null;
        originalChildForceExpandHeight = false;
        adjustedContentLayout = false;
        if (layoutContainer != null)
            LayoutRebuilder.MarkLayoutForRebuild(layoutContainer);
        if (layoutParent != null)
            LayoutRebuilder.MarkLayoutForRebuild(layoutParent);
    }

    internal void RefreshNow()
    {
        if (viewport == null || contentBlock == null
            || contentBlockSize == null) return;

        float availableHeight = Mathf.Max(0f, viewport.rect.height);
        RectTransform layoutParent = contentBlock.parent as RectTransform;
        LayoutGroup parentLayout = layoutParent?.GetComponent<LayoutGroup>();
        if (parentLayout != null)
            availableHeight = Mathf.Max(0f,
                availableHeight - parentLayout.padding.vertical);

        float targetMinHeight = Mathf.Max(originalMinHeight, availableHeight);
        if (Mathf.Abs(contentBlockSize.minHeight - targetMinHeight)
            < ChangeThreshold) return;

        contentBlockSize.minHeight = targetMinHeight;
        LayoutRebuilder.MarkLayoutForRebuild(contentBlock);
        if (layoutParent != null)
            LayoutRebuilder.MarkLayoutForRebuild(layoutParent);
    }

    private void LateUpdate() => RefreshNow();

    private void OnDisable() => ClearContentBlock();

    private void OnDestroy() => ClearContentBlock();
}
