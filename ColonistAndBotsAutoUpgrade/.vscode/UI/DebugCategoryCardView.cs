using AutoUtilitiesModFramework;
using System;
using UnityEngine;

namespace ColonistAndBotsAutoUpgrade.UI
{
    public static class DebugCategoryCardView
    {
        public static void Create(
          RectTransform parent,
          string category,
          bool enabled,
          Action onToggle
        )
        {
            var card = UIComponentFactory.CreateStateCard(
                parent,
                category,
                string.Empty,
                enabled
                    ? FrameworkCardState.Enabled
                    : FrameworkCardState.Disabled,
                () => onToggle?.Invoke(),
                $"{category} diagnostics are " +
                $"{(enabled ? "shown" : "hidden")} in this tab " +
                "and in copied logs.\n\nClick to toggle."
            );
            card.gameObject.name = $"CARAU_DebugCategory_{category}";
            card.Rect.localScale = Vector3.one;
            CompactStateCardLayout.Apply(card);
        }
    }
}
