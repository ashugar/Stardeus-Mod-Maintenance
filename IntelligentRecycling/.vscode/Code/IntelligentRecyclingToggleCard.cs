using System;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Systems.IntelligentRecycling {
    internal static class IntelligentRecyclingToggleCard {
        private static readonly Color EnabledBody = new(0.12f, 0.28f, 0.18f, 0.94f);
        private static readonly Color EnabledHeader = new(0.13f, 0.34f, 0.20f, 0.98f);
        private static readonly Color DisabledBody = new(0.30f, 0.12f, 0.15f, 0.92f);
        private static readonly Color DisabledHeader = new(0.38f, 0.13f, 0.16f, 0.98f);
        private static readonly Color UnavailableBody = new(0.16f, 0.15f, 0.18f, 0.78f);
        private static readonly Color UnavailableHeader = new(0.21f, 0.20f, 0.23f, 0.86f);

        public static void Create(RectTransform parent, string label, bool enabled,
                Action toggle, string tooltip, bool available = true) {
            GameObject card = new($"IR_Toggle_{label}");
            card.transform.SetParent(parent, false);
            RectTransform rect = card.AddComponent<RectTransform>();
            Image background = card.AddComponent<Image>();
            background.color = !available ? UnavailableBody
                : enabled ? EnabledBody : DisabledBody;
            Button button = card.AddComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;
            button.interactable = available;
            button.onClick.AddListener(() => toggle?.Invoke());
            RectTransform header = AddPanel(rect, !available ? UnavailableHeader
                : enabled ? EnabledHeader : DisabledHeader);
            AddText(header, label, 13f, Vector2.zero, Vector2.one);
            AddText(rect, !available ? "Parent disabled" : enabled ? "Enabled" : "Disabled", 12f,
                Vector2.zero, new Vector2(1f, 0.70f));
            UIUtils.AddTooltipDynamic(card, () =>
                $"{label}\n\n{tooltip}\n\nCurrently: {(enabled ? "Enabled" : "Disabled")}" +
                (available ? "\nClick to toggle." : "\nEnable the parent setting to change this category."));
        }

        private static RectTransform AddPanel(RectTransform parent, Color color) {
            GameObject root = new("Header");
            root.transform.SetParent(parent, false);
            RectTransform rect = root.AddComponent<RectTransform>();
            SetAnchors(rect, new Vector2(0f, 0.70f), Vector2.one);
            Image image = root.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        private static void AddText(RectTransform parent, string value, float size,
                Vector2 min, Vector2 max) {
            GameObject root = new("Text");
            root.transform.SetParent(parent, false);
            RectTransform rect = root.AddComponent<RectTransform>();
            SetAnchors(rect, min, max);
            TextMeshProUGUI text = root.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = size;
            text.color = Color.white;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
        }

        private static void SetAnchors(RectTransform rect, Vector2 min, Vector2 max) {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
