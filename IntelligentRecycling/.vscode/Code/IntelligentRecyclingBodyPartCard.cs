using System;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.UI;
using Game.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Systems.IntelligentRecycling {
    internal static class IntelligentRecyclingBodyPartCard {
        private static readonly Color Enabled = new(0.12f, 0.28f, 0.18f, 0.96f);
        private static readonly Color Disabled = new(0.30f, 0.12f, 0.15f, 0.94f);
        private static readonly Color Unavailable = new(0.16f, 0.15f, 0.18f, 0.80f);
        private static readonly Color Missing = new(0.11f, 0.10f, 0.13f, 0.82f);
        private static readonly Color Header = new(0.20f, 0.12f, 0.35f, 0.96f);

        public static void Create(RectTransform parent, string label,
                string biologicalIcon, string roboticIcon,
                bool biologicalEnabled, bool roboticEnabled,
                bool biologicalAvailable, bool roboticAvailable,
                Action toggleBiological, Action toggleRobotic,
                bool themeAware) {
            GameObject card = new($"IR_BodyPart_{label}");
            card.transform.SetParent(parent, false);
            RectTransform rect = card.AddComponent<RectTransform>();
            Image background = card.AddComponent<Image>();
            background.color = new Color(0.10f, 0.08f, 0.14f, 0.96f);

            RectTransform header = AddPanel(rect, "Header",
                new Vector2(0f, 0.66f), Vector2.one, Header);
            Image headerImage = header.GetComponent<Image>();
            TextMeshProUGUI title = AddText(header, label, 13f);
            if (themeAware) {
                FrameworkTheme.ApplyImage(background, "PanelContent");
                FrameworkTheme.ApplyImage(headerImage, "PanelHeader");
                FrameworkTheme.ApplyText(title, "Unsized");
            }

            bool biologicalExists = !string.IsNullOrEmpty(biologicalIcon);
            bool roboticExists = !string.IsNullOrEmpty(roboticIcon);
            CreateSide(rect, "Biological", biologicalIcon,
                new Vector2(0f, 0f), new Vector2(0.5f, 0.66f),
                biologicalEnabled, biologicalAvailable, biologicalExists,
                toggleBiological);
            CreateSide(rect, "Robotic", roboticIcon,
                new Vector2(0.5f, 0f), new Vector2(1f, 0.66f),
                roboticEnabled, roboticAvailable, roboticExists,
                toggleRobotic);

            UIUtils.AddTooltipDynamic(card, () =>
                $"{label}\n\nBiological: {State(biologicalEnabled, biologicalAvailable, biologicalExists)}\n" +
                $"Robotic: {State(roboticEnabled, roboticAvailable, roboticExists)}\n\n" +
                "Click the biological or robotic icon to toggle that type for this category.");
        }

        private static string State(bool enabled, bool available, bool exists) {
            if (!exists) {
                return "N/A (no loaded definition exists)";
            }
            return available ? (enabled ? "Enabled" : "Disabled")
                : $"{(enabled ? "Enabled" : "Disabled")} (parent disabled)";
        }

        private static void CreateSide(RectTransform parent, string name, string iconId,
                Vector2 min, Vector2 max, bool enabled, bool available,
                bool exists, Action toggle) {
            RectTransform side = AddPanel(parent, name, min, max,
                !exists ? Missing : !available ? Unavailable : enabled ? Enabled : Disabled);
            Button button = side.gameObject.AddComponent<Button>();
            button.targetGraphic = side.GetComponent<Image>();
            button.transition = Selectable.Transition.None;
            button.interactable = exists && available;
            button.onClick.AddListener(() => toggle?.Invoke());

            if (!exists) {
                // No fallback icon: it would falsely imply that this biological
                // or robotic counterpart exists in the loaded definitions.
                AddText(side, "N/A", 11f);
                return;
            }
            GameObject iconObject = new("Icon");
            iconObject.transform.SetParent(side, false);
            RectTransform iconRect = iconObject.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(27f, 27f);
            Image icon = iconObject.AddComponent<Image>();
            icon.sprite = Images.Sprite(iconId);
            icon.preserveAspect = true;
            icon.color = available ? Color.white : new Color(0.55f, 0.55f, 0.55f, 0.8f);
            icon.raycastTarget = false;
        }

        private static RectTransform AddPanel(RectTransform parent, string name,
                Vector2 min, Vector2 max, Color color) {
            GameObject root = new(name);
            root.transform.SetParent(parent, false);
            RectTransform rect = root.AddComponent<RectTransform>();
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = new Vector2(1f, 1f);
            rect.offsetMax = new Vector2(-1f, -1f);
            Image image = root.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            return rect;
        }

        private static TextMeshProUGUI AddText(RectTransform parent,
                string value, float size) {
            GameObject root = new("Text");
            root.transform.SetParent(parent, false);
            RectTransform rect = root.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            TextMeshProUGUI text = root.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = size;
            text.color = Color.white;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            return text;
        }
    }
}
