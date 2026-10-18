using AutoUtilitiesModFramework;
using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Services;
using Game.Constants;
using Game.UI;
using Game.Utils;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ColonistAndBotsAutoUpgrade.UI
{
    public static class SlotCardView
    {
        private static readonly Color CardColor =
          new Color(0.16f, 0.09f, 0.30f, 0.96f);
        private static readonly Color HeaderColor =
          new Color(0.20f, 0.12f, 0.36f, 1f);
        private static readonly Color BarBackgroundColor =
          new Color(0.045f, 0.03f, 0.07f, 1f);
        private static readonly Color QualityColor =
          new Color(0.34f, 0.78f, 0.08f, 1f);
        private static readonly Color ConditionColor =
          new Color(0.16f, 0.58f, 0.06f, 1f);
        private static readonly Color MissingBodyColor =
          new Color(0.39f, 0.34f, 0.51f, 1f);
        private static readonly Color MissingTextColor =
          new Color(1f, 0.28f, 0.28f, 1f);
        private static readonly Color DestroyedBodyColor =
          new Color(0.72f, 0.12f, 0.14f, 1f);
        private static readonly Color DestroyedTextColor =
          new Color(0.08f, 0.02f, 0.02f, 1f);
        private static readonly Color CriticalStateColor =
          new Color(0.62f, 0.08f, 0.10f, 0.96f);
        private static readonly Color DamagedStateColor =
          new Color(0.65f, 0.31f, 0.04f, 0.96f);
        private static readonly Color UnusableStateColor =
          new Color(0.50f, 0.12f, 0.12f, 0.96f);
        private static readonly Color NotRemovableStateColor =
          new Color(0.55f, 0.20f, 0.08f, 0.96f);
        private static readonly Color DisabledStateColor =
          new Color(0.24f, 0.23f, 0.28f, 0.96f);

        public static void Create(
          RectTransform parent,
          SlotCardData card,
          string statusText,
          Action onQueueRequested,
          Action onCancelRequested = null,
          string footerOverride = null,
          string tooltipOverride = null,
          bool cancelIsDismiss = false,
          Action footerActionRequested = null,
          string footerActionBadge = null,
          string footerActionTooltip = null,
          bool footerIsWaiting = false,
          Color? footerOverrideColor = null
        )
        {
            var cardObject =
              new GameObject($"CARAU_SlotCard_{card.DisplayOrder}");
            cardObject.transform.SetParent(parent, false);

            var cardRect = cardObject.AddComponent<RectTransform>();
            cardRect.localScale = Vector3.one;

            var cardBackground = AddImage(
              cardObject,
              card.IsDisabled
                ? new Color(0.12f, 0.11f, 0.15f, 0.92f)
                : CardColor
            );
            cardBackground.raycastTarget = true;
            FrameworkTheme.ApplyImage(cardBackground, "PanelContent");

            if (onQueueRequested != null)
            {
                var button = cardObject.AddComponent<Button>();
                button.targetGraphic = cardBackground;
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(
                  () => onQueueRequested()
                );
            }

            var header = AddPanel(
              cardRect,
              "Header",
              new Vector2(0f, 0.80f),
              Vector2.one,
              HeaderColor
            );
            FrameworkTheme.ApplyImage(
              header.GetComponent<Image>(),
              "PanelHeader"
            );
            AddText(
              header,
              "SlotName",
              card.SlotName,
              15f,
              FontStyles.Normal
            );

            if (card.IsMassOperation)
            {
                AddStateBody(
                  cardRect,
                  "Remove all removable parts",
                  MissingBodyColor,
                  Color.white
                );
            }
            else if (card.HealthState == SlotHealthState.Destroyed)
            {
                AddStateBody(
                  cardRect,
                  "Destroyed",
                  DestroyedBodyColor,
                  DestroyedTextColor
                );
            }
            else if (card.HealthState == SlotHealthState.Missing)
            {
                AddStateBody(
                  cardRect,
                  "Missing",
                  MissingBodyColor,
                  MissingTextColor
                );
            }
            else
            {
                AddInstalledPartBody(cardRect, card);
            }

            // A retained failure supplies its own footer and tooltip. Do not
            // leave the normal queue/upgrade badge underneath that footer,
            // where it can still intercept pointer hover events.
            if (string.IsNullOrWhiteSpace(footerOverride))
            {
                AddStatusFooter(
                  cardRect,
                  card,
                  statusText,
                  onQueueRequested != null
                );
            }

            if (!string.IsNullOrWhiteSpace(footerOverride))
            {
                AddOverrideFooter(
                    cardRect,
                    footerOverride,
                    footerActionRequested,
                    footerActionBadge,
                    footerActionTooltip,
                    footerIsWaiting,
                    footerOverrideColor
                );
            }

            if (onCancelRequested != null)
            {
                AddCancelButton(
                    cardRect,
                    onCancelRequested,
                    cancelIsDismiss
                );
            }

            var tooltipText = string.IsNullOrWhiteSpace(tooltipOverride)
                ? BuildTooltip(card, statusText)
                : tooltipOverride;
            if (onQueueRequested != null)
            {
                tooltipText += "\n\nClick to queue this operation.";
            }

            UIUtils.AddTooltipDynamic(
              cardObject,
              () => tooltipText
            );
        }

        private static void AddCancelButton(
          RectTransform cardRect,
          Action onCancelRequested,
          bool isDismiss
        )
        {
            var cancelObject = new GameObject("CancelTrackedOperation");
            cancelObject.transform.SetParent(cardRect, false);

            var cancelRect = cancelObject.AddComponent<RectTransform>();
            cancelRect.anchorMin = Vector2.one;
            cancelRect.anchorMax = Vector2.one;
            cancelRect.pivot = Vector2.one;
            cancelRect.anchoredPosition = new Vector2(-4f, -4f);
            cancelRect.sizeDelta = new Vector2(26f, 26f);

            var background = AddImage(
              cancelObject,
              new Color(0.66f, 0.06f, 0.08f, 0.98f)
            );
            background.raycastTarget = true;

            var button = cancelObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() => onCancelRequested());

            var label = AddText(
              cancelRect,
              "CancelLabel",
              "X",
              16f,
              FontStyles.Bold,
              theme: false
            );
            label.color = new Color(1f, 0.82f, 0.82f, 1f);
            FrameworkTheme.ApplyButton(button, "Red", label);

            UIUtils.AddTooltipDynamic(
              cancelObject,
              () =>
                isDismiss
                    ? "Dismiss Failure\n\n" +
                      "Remove this retained failure detail. " +
                      "The failure total will not be changed."
                    : "Cancel Procedure\n\n" +
                      "Click this X to cancel this CARAU-scheduled procedure."
            );

            cancelObject.transform.SetAsLastSibling();
        }

        private static void AddOverrideFooter(
          RectTransform cardRect,
          string footerText,
          Action action,
          string actionBadge,
          string actionTooltip,
          bool isWaiting,
          Color? colorOverride
        )
        {
            var footer = AddPanel(
                cardRect,
                "OverrideStateFooter",
                new Vector2(0f, 0.02f),
                new Vector2(1f, 0.27f),
                colorOverride ??
                    (isWaiting ? DamagedStateColor : CriticalStateColor)
            );

            AddText(
                footer,
                "Status",
                footerText,
                13f,
                FontStyles.Bold,
                Vector2.zero,
                action != null
                    ? new Vector2(0.78f, 1f)
                    : Vector2.one,
                theme: false
            );

            if (action != null)
            {
                var badge = AddPanel(
                    footer,
                    "OverrideActionBadge",
                    new Vector2(0.78f, 0f),
                    Vector2.one,
                    new Color(0.28f, 0.18f, 0.58f, 0.98f)
                );
                var badgeImage = badge.GetComponent<Image>();
                badgeImage.raycastTarget = true;

                var button = badge.gameObject.AddComponent<Button>();
                button.targetGraphic = badgeImage;
                button.onClick.AddListener(() => action());

                var iconObject = new GameObject("FooterActionIcon");
                iconObject.transform.SetParent(badge, false);

                var iconRect =
                    iconObject.AddComponent<RectTransform>();
                SetAnchors(
                    iconRect,
                    new Vector2(0.22f, 0.12f),
                    new Vector2(0.78f, 0.88f)
                );

                var icon = iconObject.AddComponent<Image>();
                TextMeshProUGUI actionText = null;
                if (!string.IsNullOrWhiteSpace(actionBadge) &&
                    Images.TrySprite(actionBadge, out var actionSprite) &&
                    actionSprite != null)
                {
                    icon.sprite = actionSprite;
                    icon.preserveAspect = true;
                    icon.raycastTarget = false;
                }
                else
                {
                    UnityEngine.Object.Destroy(icon);
                    actionText = AddText(
                        badge,
                        "FooterActionText",
                        string.IsNullOrWhiteSpace(actionBadge)
                            ? "↻"
                            : actionBadge,
                        20f,
                        FontStyles.Bold,
                        theme: false
                    );
                }

                if (actionText == null)
                {
                    FrameworkTheme.ApplyButton(button, "Panel");
                }
                else
                {
                    FrameworkTheme.ApplyButton(
                        button,
                        "Panel",
                        actionText
                    );
                }

                UIUtils.AddTooltipDynamic(
                    badge.gameObject,
                    () => string.IsNullOrWhiteSpace(actionTooltip)
                        ? "Retry this failed operation."
                        : actionTooltip
                );
            }

            footer.transform.SetAsLastSibling();
        }

        private static void AddInstalledPartBody(
          RectTransform cardRect,
          SlotCardData card
        )
        {
            AddInstalledPartIcon(cardRect, card.InstalledPartIconId);

            AddText(
              cardRect,
              "InstalledPart",
              card.InstalledPartName,
              16f,
              FontStyles.Normal,
              new Vector2(0.04f, 0.62f),
              new Vector2(0.96f, 0.80f)
            );

            AddText(
              cardRect,
              "QualityText",
              $"Quality {Units.Percentage(card.InstalledQuality, clamp: false)}",
              13f,
              FontStyles.Normal,
              new Vector2(0.04f, 0.48f),
              new Vector2(0.96f, 0.63f)
            );
            AddProgressBar(
              cardRect,
              "QualityBar",
              card.InstalledQuality,
              0.455f,
              QualityColor
            );

            var conditionText = card.HasInstalledCondition
              ? Units.Percentage(card.InstalledCondition)
              : "n/a";

            AddText(
              cardRect,
              "ConditionText",
              $"Condition {conditionText}",
              13f,
              FontStyles.Normal,
              new Vector2(0.04f, 0.31f),
              new Vector2(0.96f, 0.45f)
            );
            AddProgressBar(
              cardRect,
              "ConditionBar",
              card.HasInstalledCondition
                ? card.InstalledCondition
                : 0f,
              0.285f,
              ConditionColor
            );
        }

        private static void AddInstalledPartIcon(
          RectTransform cardRect,
          string iconId
        )
        {
            if (string.IsNullOrWhiteSpace(iconId) ||
                !Images.TrySprite(iconId, out var sprite) ||
                sprite == null)
            {
                return;
            }

            var iconObject = new GameObject("InstalledPartIcon");
            iconObject.transform.SetParent(cardRect, false);

            var iconRect = iconObject.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0.71f);
            iconRect.anchorMax = new Vector2(0f, 0.71f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(22f, 0f);
            iconRect.sizeDelta = new Vector2(30f, 30f);

            var icon = iconObject.AddComponent<Image>();
            icon.sprite = sprite;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
        }

        private static void AddStateBody(
          RectTransform cardRect,
          string stateText,
          Color backgroundColor,
          Color textColor
        )
        {
            var stateBody = AddPanel(
              cardRect,
              $"{stateText}Body",
              new Vector2(0f, 0.27f),
              new Vector2(1f, 0.80f),
              backgroundColor
            );

            var text = AddText(
              stateBody,
              "StateText",
              stateText,
              20f,
              FontStyles.Normal,
              theme: false
            );
            text.color = textColor;
        }

        private static void AddStatusFooter(
          RectTransform cardRect,
          SlotCardData card,
          string statusText,
          bool canQueue
        )
        {
            var displayedStatus = statusText;
            Color? stateColor = null;

            if (card.IsDisabled)
            {
                displayedStatus = "Disabled in Slots tab";
                stateColor = DisabledStateColor;
            }
            else
            {
                switch (card.HealthState)
                {
                    case SlotHealthState.Destroyed:
                        // The card body already communicates this terminal
                        // state. Leave the footer empty rather than exposing
                        // an unavailable replacement decision.
                        return;

                    case SlotHealthState.Missing:
                        if (card.HasDestroyedParent)
                        {
                            displayedStatus = "Parent Part Destroyed";
                            stateColor = UnusableStateColor;
                        }
                        else if (card.HasMissingParent)
                        {
                            displayedStatus = "Parent Part Missing";
                            stateColor = UnusableStateColor;
                        }
                        else if (card.HasUnusableParent)
                        {
                            displayedStatus = "Parent Part Unusable";
                            stateColor = UnusableStateColor;
                        }
                        break;

                    case SlotHealthState.Unusable:
                        if (card.HasDestroyedParent)
                        {
                            displayedStatus = "Parent Part Destroyed";
                        }
                        else if (card.HasMissingParent)
                        {
                            displayedStatus = "Parent Part Missing";
                        }
                        else if (card.HasUnusableParent)
                        {
                            displayedStatus = "Parent Part Unusable";
                        }
                        else if (card.QueueStatus != null &&
                                 card.QueueStatus.Contains("parent"))
                        {
                            displayedStatus = "Parent Part Unusable";
                        }
                        else
                        {
                            displayedStatus = "Unusable";
                        }

                        stateColor = UnusableStateColor;
                        break;

                    case SlotHealthState.Critical:
                        displayedStatus = "Critical Damage";
                        stateColor = CriticalStateColor;
                        break;

                    case SlotHealthState.Damaged:
                        displayedStatus = "Damaged";
                        stateColor = DamagedStateColor;
                        break;

                    case SlotHealthState.NotRemovable:
                        displayedStatus = "Not Removable";
                        stateColor = NotRemovableStateColor;
                        break;
                }
            }

            if (stateColor.HasValue)
            {
                var stateFooter = AddPanel(
                  cardRect,
                  "StateFooter",
                  new Vector2(0f, 0.02f),
                  new Vector2(1f, 0.27f),
                  stateColor.Value
                );

                var hasActionBadge = TryAddActionBadge(
                    stateFooter,
                    card,
                    statusText,
                    canQueue,
                    new Vector2(0.78f, 0f),
                    Vector2.one
                );

                AddText(
                  stateFooter,
                  "Status",
                  displayedStatus,
                  13f,
                  FontStyles.Bold,
                  Vector2.zero,
                  hasActionBadge
                    ? new Vector2(0.78f, 1f)
                    : Vector2.one,
                  theme: false
                );
                return;
            }

            if (TryAddActionBadge(
                  cardRect,
                  card,
                  statusText,
                  canQueue,
                  new Vector2(0.40f, 0.02f),
                  new Vector2(0.60f, 0.27f)
                ))
            {
                return;
            }

            if (card.Evaluation?.Disposition ==
                    UpgradeDisposition.NoUpgradeNeeded ||
                (card.Evaluation == null &&
                 (card.Decision?.Type ==
                      UpgradeDecisionType.NothingBetterFound ||
                  card.Decision?.Type ==
                      UpgradeDecisionType.QualityThresholdMet)))
            {
                return;
            }

            AddText(
              cardRect,
              "Status",
              displayedStatus,
              11f,
              FontStyles.Normal,
              new Vector2(0.04f, 0.02f),
              new Vector2(0.96f, 0.27f)
            );
        }

        private static bool TryAddActionBadge(
          RectTransform cardRect,
          SlotCardData card,
          string statusText,
          bool canQueue,
          Vector2 anchorMin,
          Vector2 anchorMax
        )
        {
            string badgeText = null;
            var useKnifeIcon = false;

            if (card.HasPendingOperation)
            {
                useKnifeIcon = true;
            }
            else if (canQueue &&
                     card.Decision?.Type ==
                       UpgradeDecisionType.Missing &&
                     card.Decision.InventoryPart != null &&
                     card.Decision.InventoryCount > 0)
            {
                badgeText = "+";
            }
            else if (card.QueueStatus == "Queue: Ready")
            {
                switch (card.Decision?.Type)
                {
                    case UpgradeDecisionType.TierUpgrade:
                        badgeText = "T↑";
                        break;

                    case UpgradeDecisionType.QualityUpgrade:
                        badgeText = "Q↑";
                        break;

                }
            }

            if (!useKnifeIcon && badgeText == null)
            {
                return false;
            }

            var badge = AddPanel(
              cardRect,
              "ActionBadge",
              anchorMin,
              anchorMax,
              new Color(0.28f, 0.18f, 0.45f, 0.96f)
            );
            FrameworkTheme.ApplyImage(
              badge.GetComponent<Image>(),
              "PanelHeader"
            );
            badge.GetComponent<Image>().raycastTarget = true;

            if (useKnifeIcon)
            {
                var iconObject = new GameObject("KnifeIcon");
                iconObject.transform.SetParent(badge, false);

                var iconRect =
                  iconObject.AddComponent<RectTransform>();
                SetAnchors(
                  iconRect,
                  new Vector2(0.22f, 0.12f),
                  new Vector2(0.78f, 0.88f)
                );

                var icon = iconObject.AddComponent<Image>();
                icon.sprite = Images.Sprite(IconId.CKnife);
                icon.preserveAspect = true;
                icon.raycastTarget = false;
            }
            else
            {
                AddText(
                  badge,
                  "BadgeText",
                  badgeText,
                  20f,
                  FontStyles.Bold,
                  theme: false
                );
            }

            var tooltipText = BuildTooltip(card, statusText);
            if (canQueue)
            {
                tooltipText += "\n\nClick to queue this operation.";
            }

            UIUtils.AddTooltipDynamic(
              badge.gameObject,
              () => tooltipText
            );

            return true;
        }

        private static string BuildTooltip(
          SlotCardData card,
          string statusText
        )
        {
            if (card?.Evaluation != null &&
                !card.Evaluation.IsQueueable)
            {
                return UpgradeExplanationFormatter.BuildTooltip(
                    card.Evaluation
                );
            }

            if (card.HasPendingOperation)
            {
                var operation = card.PendingOperation;
                var reservedPart = operation?.ReservedPart;
                var replacementName =
                  reservedPart?.Def?.NameT ?? "Reserved part";
                var replacementQuality =
                  reservedPart?.BodyPartState != null
                    ? Units.Percentage(
                        reservedPart.BodyPartState.Quality,
                        clamp: false
                      )
                    : "Unknown";

                var pendingText =
                  $"Pending Procedure\n\n" +
                  $"Slot: {operation?.TargetSlot?.NameT ?? card.SlotName}\n" +
                  $"Installed: {card.InstalledPartName} " +
                  $"(Tier {card.InstalledTier}, " +
                  $"{Units.Percentage(card.InstalledQuality, clamp: false)})\n" +
                  $"Reserved Replacement: {replacementName}\n" +
                  $"Replacement Quality: {replacementQuality}\n" +
                  $"Operation: {operation?.OpType.ToString() ?? "Unknown"}";

                if (card.HealthState == SlotHealthState.NotRemovable)
                {
                    pendingText +=
                      "\n\nWarning: This installed part cannot be " +
                      "removed due to a previous surgical error.";
                }

                return pendingText;
            }

            if (card.HealthState == SlotHealthState.NotRemovable)
            {
                return
                  $"Not Removable\n\n" +
                  $"Slot: {card.SlotName}\n" +
                  $"Installed: {card.InstalledPartName}\n" +
                  "This body part can no longer be removed due to a " +
                  "previous surgical error.";
            }

            if (card.HealthState == SlotHealthState.Destroyed)
            {
                return
                  $"Destroyed Slot\n\n" +
                  $"Slot: {card.SlotName}\n" +
                  "This slot has been destroyed and cannot receive a " +
                  "replacement.";
            }

            if (card.HasDestroyedParent)
            {
                return
                  $"Parent Part Destroyed\n\n" +
                  $"Slot: {card.SlotName}\n" +
                  "This slot cannot receive a replacement because its " +
                  "parent slot has been destroyed.";
            }

            if (card.HasMissingParent)
            {
                return
                  $"Parent Part Missing\n\n" +
                  $"Slot: {card.SlotName}\n" +
                  "Install the missing parent part before installing a " +
                  "replacement here.";
            }

            if (card.HasUnusableParent)
            {
                return
                  $"Parent Part Unusable\n\n" +
                  $"Slot: {card.SlotName}\n" +
                  "This slot cannot receive a replacement until its " +
                  "installed parent part is usable or replaced.";
            }

            var decision = card.Decision;

            if (decision == null)
            {
                return
                  $"{card.SlotName}\n\n{statusText}";
            }

            switch (decision.Type)
            {
                case UpgradeDecisionType.TierUpgrade:
                    return
                      $"Tier Upgrade\n\n" +
                      $"Slot: {card.SlotName}\n" +
                      $"Installed: {card.InstalledPartName} " +
                      $"(Tier {card.InstalledTier}, " +
                      $"{Units.Percentage(card.InstalledQuality, clamp: false)})\n" +
                      $"Replacement: {decision.TargetPart?.NameT ?? "Unknown"} " +
                      $"(Tier {decision.TargetPart?.Tier ?? 0}, " +
                      $"{Units.Percentage(decision.AvailableQuality, clamp: false)})\n" +
                      $"Available: {decision.InventoryCount}";

                case UpgradeDecisionType.QualityUpgrade:
                    return
                      $"Quality Upgrade\n\n" +
                      $"Slot: {card.SlotName}\n" +
                      $"Part: {card.InstalledPartName}\n" +
                      $"Current Quality: " +
                      $"{Units.Percentage(card.InstalledQuality, clamp: false)}\n" +
                      $"Replacement Quality: " +
                      $"{Units.Percentage(decision.AvailableQuality, clamp: false)}\n" +
                      $"Available: {decision.InventoryCount}";

                case UpgradeDecisionType.Missing:
                    return
                      $"Install Missing Part\n\n" +
                      $"Slot: {card.SlotName}\n" +
                      $"Replacement: {decision.TargetPart?.NameT ?? "Unknown"} " +
                      $"(Tier {decision.TargetPart?.Tier ?? 0}, " +
                      $"{Units.Percentage(decision.AvailableQuality, clamp: false)})\n" +
                      $"Available: {decision.InventoryCount}";

                case UpgradeDecisionType.QualityThresholdMet:
                    return
                      $"Within Quality Threshold\n\n" +
                      $"Slot: {card.SlotName}\n" +
                      $"Current Quality: " +
                      $"{Units.Percentage(card.InstalledQuality, clamp: false)}\n" +
                      $"{statusText}";

                case UpgradeDecisionType.NothingBetterFound:
                    return
                      $"Nothing Better Found\n\n" +
                      $"Slot: {card.SlotName}\n" +
                      $"Installed: {card.InstalledPartName}";

                default:
                    return
                      $"{card.SlotName}\n\n{statusText}";
            }
        }

        private static RectTransform AddPanel(
          RectTransform parent,
          string name,
          Vector2 anchorMin,
          Vector2 anchorMax,
          Color color
        )
        {
            var panelObject = new GameObject(name);
            panelObject.transform.SetParent(parent, false);

            var rect = panelObject.AddComponent<RectTransform>();
            SetAnchors(rect, anchorMin, anchorMax);

            AddImage(panelObject, color);
            return rect;
        }

        private static void AddProgressBar(
          RectTransform parent,
          string name,
          float value,
          float verticalAnchor,
          Color fillColor
        )
        {
            var background = AddPanel(
              parent,
              name,
              new Vector2(0.08f, verticalAnchor),
              new Vector2(0.92f, verticalAnchor + 0.035f),
              BarBackgroundColor
            );

            var fill = AddPanel(
              background,
              "Fill",
              Vector2.zero,
              new Vector2(Mathf.Clamp01(value), 1f),
              fillColor
            );
            fill.pivot = new Vector2(0f, 0.5f);
        }

        private static TextMeshProUGUI AddText(
          RectTransform parent,
          string name,
          string value,
          float fontSize,
          FontStyles fontStyle,
          Vector2? anchorMin = null,
          Vector2? anchorMax = null,
          bool theme = true
        )
        {
            var textObject = new GameObject(name);
            textObject.transform.SetParent(parent, false);

            var rect = textObject.AddComponent<RectTransform>();
            SetAnchors(
              rect,
              anchorMin ?? Vector2.zero,
              anchorMax ?? Vector2.one
            );

            var text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = value ?? "";
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;

            if (theme)
            {
                FrameworkTheme.ApplyText(text, "Unsized");
            }

            return text;
        }

        private static Image AddImage(
          GameObject target,
          Color color
        )
        {
            var image = target.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static void SetAnchors(
          RectTransform rect,
          Vector2 anchorMin,
          Vector2 anchorMax
        )
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
