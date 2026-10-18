using AutoUtilitiesModFramework;
using System;
using UnityEngine;

namespace ColonistAndBotsAutoUpgrade.UI
{
    public static class RetryPolicyCardView
    {
        public static void Create(
          RectTransform parent,
          string label,
          string subtitle,
          string tooltip,
          bool selected,
          Action onSelect
        )
        {
            var card = UIComponentFactory.CreateStateCard(
                parent,
                label,
                subtitle,
                selected
                    ? FrameworkCardState.Selected
                    : FrameworkCardState.Neutral,
                () => onSelect?.Invoke(),
                $"{label}\n\n{tooltip}\n\n" +
                (selected ? "Currently selected." : "Click to select.")
            );
            card.gameObject.name = $"CARAU_RetryPolicy_{label}";
            card.Rect.localScale = Vector3.one;
        }
    }
}
