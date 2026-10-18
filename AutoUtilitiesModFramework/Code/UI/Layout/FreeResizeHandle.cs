using System;
using Game;
using Game.Constants;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AutoUtilitiesModFramework;

/// <summary>Allows a popup to resize freely from its native resize handle.</summary>
/// <remarks>
/// <see cref="FrameworkPanel"/> installs this automatically. For a custom
/// popup, add it to a lower-right handle whose parent is the panel rectangle.
/// Pointer methods are Unity callbacks and should not be invoked manually.
/// </remarks>
/// <example><code>
/// var resize = handle.gameObject.AddComponent&lt;FreeResizeHandle&gt;();
/// resize.MinSize = new Vector2(360f, 240f);
/// resize.MaxSize = new Vector2(1200f, 900f);
/// resize.SizeChanged = size =&gt; rememberedSize = size;
/// </code></example>
public sealed class FreeResizeHandle : MonoBehaviour, IPointerDownHandler,
    IDragHandler, IPointerUpHandler, IPointerEnterHandler, IPointerExitHandler
{
    private const int CursorOpenHand = -1237112475;
    private const int CursorClosedHand = -534383826;

    private RectTransform panel;
    private Vector2 previousPointerPosition;
    private bool hovering;
    private bool dragging;

    /// <summary>Smallest permitted panel size.</summary>
    public Vector2 MinSize = new Vector2(320f, 220f);
    /// <summary>Largest permitted panel size.</summary>
    public Vector2 MaxSize = new Vector2(1400f, 1000f);
    /// <summary>Optional callback raised while dragging with the new size.</summary>
    public Action<Vector2> SizeChanged;

    private void Awake()
    {
        panel = transform.parent.GetComponent<RectTransform>();
    }

    /// <summary>Unity pointer callback; updates the resize cursor.</summary>
    public void OnPointerEnter(PointerEventData eventData)
    {
        if (G.IsClickDrag) return;
        hovering = true;
        The.SysSig.SetCursorOverride.Send(CursorOpenHand);
    }

    /// <summary>Unity pointer callback; clears the resize cursor.</summary>
    public void OnPointerExit(PointerEventData eventData)
    {
        hovering = false;
        if (!G.IsClickDrag) The.SysSig.SetCursorOverride.Send(0);
    }

    /// <summary>Unity pointer callback; begins a left-button resize.</summary>
    public void OnPointerDown(PointerEventData eventData)
    {
        if (panel == null
            || eventData.button != PointerEventData.InputButton.Left) return;
        panel.SetAsLastSibling();
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            panel.parent as RectTransform, eventData.position,
            eventData.pressEventCamera, out previousPointerPosition);
        if (G.IsClickDrag) return;
        dragging = true;
        The.SysSig.SetCursorOverride.Send(CursorClosedHand);
    }

    /// <summary>Unity pointer callback; applies a clamped panel size.</summary>
    public void OnDrag(PointerEventData eventData)
    {
        if (panel == null || !dragging) return;
        The.SysSig.SetCursorOverride.Send(CursorClosedHand);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            panel.parent as RectTransform, eventData.position,
            eventData.pressEventCamera, out Vector2 current);
        Vector2 delta = current - previousPointerPosition;
        Vector2 size = panel.sizeDelta + new Vector2(delta.x, -delta.y);
        panel.sizeDelta = new Vector2(
            Mathf.Clamp(size.x, MinSize.x, MaxSize.x),
            Mathf.Clamp(size.y, MinSize.y, MaxSize.y));
        previousPointerPosition = current;
        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.MarkLayoutForRebuild(panel);
        SizeChanged?.Invoke(panel.sizeDelta);
    }

    /// <summary>Unity pointer callback; completes resizing.</summary>
    public void OnPointerUp(PointerEventData eventData)
    {
        dragging = false;
        The.SysSig.SetCursorOverride.Send(hovering ? CursorOpenHand : 0);
    }

    private void OnDisable()
    {
        hovering = false;
        dragging = false;
        The.SysSig.SetCursorOverride.Send(0);
    }
}
