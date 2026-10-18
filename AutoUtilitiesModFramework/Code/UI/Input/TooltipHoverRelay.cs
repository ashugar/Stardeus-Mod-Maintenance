using Game;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AutoUtilitiesModFramework;

/// <summary>
/// Explicitly bridges Unity pointer events to Stardeus's tooltip signals.
/// Synthetic UI objects are not recognized by UIPermanentTip's native lookup,
/// so full-row custom hit areas use this relay instead.
/// </summary>
internal sealed class TooltipHoverRelay : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler
{
    internal string Text { get; set; }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (!string.IsNullOrEmpty(Text))
            The.SysSig.ShowTooltip.Send(Text, true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        The.SysSig.HideTooltip.Send();
    }

    private void OnDisable()
    {
        The.SysSig?.HideTooltip.Send();
    }
}
