using AutoUtilitiesModFramework;
using System;
using UnityEngine;

namespace ColonistAndBotsAutoUpgrade.UI
{
    public static class SlotToggleCardView
    {
        public static void Create(
          RectTransform parent,
          string slotName,
          bool enabled,
          Action onToggle,
          string tooltip = null
        )
        {
            var state = enabled
                ? FrameworkCardState.Enabled
                : FrameworkCardState.Disabled;
            var tooltipText = string.IsNullOrWhiteSpace(tooltip)
                ? $"{slotName}\n\n" +
                  "Allow CARAU to automatically install or replace " +
                  "parts in this slot."
                : tooltip;

            var card = UIComponentFactory.CreateStateCard(
                parent,
                slotName,
                string.Empty,
                state,
                () => onToggle?.Invoke(),
                tooltipText +
                $"\n\nCurrently: {(enabled ? "Enabled" : "Disabled")}\n" +
                "Click to toggle."
            );
            card.gameObject.name = $"CARAU_SlotToggle_{slotName}";
            card.Rect.localScale = Vector3.one;
            CompactStateCardLayout.Apply(card);
        }
    }
}
