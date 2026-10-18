using AutoUtilitiesModFramework;
using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Services;
using ColonistAndBotsAutoUpgrade.Systems;
using Game.Constants;
using Game.Data;
using Game.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ColonistAndBotsAutoUpgrade.UI
{
    /// <summary>
    /// Dedicated window for operations scheduled and tracked by CARAU.
    ///
    /// The top-bar widget opens this window instead of the primary CARAU
    /// configuration panel. Tracked beings form a vertical tab list on the
    /// left; the selected being's live Main-tab-style cards appear on the
    /// right. Each tracked card can cancel the exact CARAU-scheduled
    /// operation it represents; queue controls remain in CARAU's primary
    /// window.
    /// </summary>
    public sealed class CARAUOperationTrackingPanel : IDisposable
    {
        private static readonly Color NativeCompletionMismatchColor =
            new Color(0.76f, 0.12f, 0.48f, 0.98f);

        private readonly GameState S;
        private readonly List<CARAUTrackedOperation> operations = new();
        private readonly FrameworkPanel panel;
        private uint selectedBeingId;
        private float trackedBeingListScrollPosition = 1f;
        private bool applyInitialSize = true;

        public bool IsOpen => panel.IsOpen;

        public CARAUOperationTrackingPanel(GameState state)
        {
            S = state;
            panel = new FrameworkPanel(
                new FrameworkPanelOptions
                {
                    Id = "ColonistAndBotsAutoUpgrade.TrackedOperations",
                    Title = $"{CARAUVersion.DisplayTitle} — Tracked Operations",
                    MinSize = new Vector2(700f, 320f),
                    MaxSize = new Vector2(1600f, 1100f),
                    RememberGeometry = true
                },
                BuildContents
            );
        }

        public void Show(
          IReadOnlyList<CARAUTrackedOperation> trackedOperations
        )
        {
            CopyOperations(trackedOperations);
            EnsureValidSelection();
            if (panel.IsOpen)
            {
                panel.Refresh();
                return;
            }

            panel.Show();
            if (applyInitialSize && panel.Rect != null)
            {
                panel.Rect.sizeDelta = new Vector2(1100f, 700f);
                applyInitialSize = false;
                panel.Refresh();
            }
        }

        public void Refresh(
          IReadOnlyList<CARAUTrackedOperation> trackedOperations
        )
        {
            CopyOperations(trackedOperations);

            if (!IsOpen)
            {
                return;
            }

            if (operations.Count == 0)
            {
                Close();
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Cleanup,
                    "Operation tracking window closed | " +
                    "Reason=no CARAU operations remain"
                );
                return;
            }

            EnsureValidSelection();
            RebuildContents();
        }

        public void Close()
        {
            panel.Close();
        }

        public void Dispose() => panel.Dispose();

        private void CopyOperations(
          IReadOnlyList<CARAUTrackedOperation> trackedOperations
        )
        {
            operations.Clear();

            if (trackedOperations == null)
            {
                return;
            }

            for (var i = 0; i < trackedOperations.Count; i++)
            {
                if (trackedOperations[i] != null)
                {
                    operations.Add(trackedOperations[i]);
                }
            }
        }

        private void EnsureValidSelection()
        {
            if (operations.Any(op => op.BeingId == selectedBeingId))
            {
                return;
            }

            selectedBeingId = operations
                .OrderBy(op =>
                    CARAUOperationTrackingSys.GetDisplayedBeingOrder(
                        op.BeingId
                    )
                )
                .Select(op => op.BeingId)
                .FirstOrDefault();
        }

        private void RebuildContents()
        {
            panel.Refresh();
        }

        private void BuildContents(PanelContentContext context)
        {
            var rootObject = context.CreateOwned("CARAU_TrackingLayout");

            var rootRect = rootObject.AddComponent<RectTransform>();
            rootRect.localScale = Vector3.one;

            var rootSize = rootObject.AddComponent<LayoutElement>();
            rootSize.minHeight = 620f;
            rootSize.preferredHeight = 620f;
            rootSize.flexibleHeight = 1f;

            var rootLayout =
                rootObject.AddComponent<HorizontalLayoutGroup>();
            rootLayout.padding = new RectOffset(10, 10, 10, 10);
            rootLayout.spacing = 10f;
            rootLayout.childAlignment = TextAnchor.UpperLeft;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;
            rootLayout.childForceExpandWidth = false;
            rootLayout.childForceExpandHeight = true;

            var tabsColumn = CreateBeingTabsColumn(rootRect);
            CreateCancelBatchButton(tabsColumn);
            CreateRetryBatchButton(tabsColumn);
            CreateClearBatchFailuresButton(tabsColumn);
            var tabs = CreateBeingTabs(tabsColumn);
            CreateSeparator(rootRect);
            var details = CreateDetailsColumn(rootRect);

            if (operations.Count == 0)
            {
                AddMessage(
                    details,
                    "No CARAU-scheduled operations are currently pending."
                );
                return;
            }

            DrawSelectedBeing(details);
        }

        private static RectTransform CreateBeingTabsColumn(
          RectTransform parent
        )
        {
            var columnObject = new GameObject("TrackedBeingColumn");
            columnObject.transform.SetParent(parent, false);

            var columnRect =
                columnObject.AddComponent<RectTransform>();
            columnRect.localScale = Vector3.one;

            var columnSize =
                columnObject.AddComponent<LayoutElement>();
            columnSize.minWidth = 210f;
            columnSize.preferredWidth = 210f;
            columnSize.flexibleWidth = 0f;

            var columnLayout =
                columnObject.AddComponent<VerticalLayoutGroup>();
            columnLayout.spacing = 7f;
            columnLayout.childAlignment = TextAnchor.UpperCenter;
            columnLayout.childControlWidth = true;
            columnLayout.childControlHeight = true;
            columnLayout.childForceExpandWidth = true;
            columnLayout.childForceExpandHeight = false;

            return columnRect;
        }

        private void CreateCancelBatchButton(RectTransform parent)
        {
            var pendingCount =
                operations.Count(operation =>
                    operation.IsPending &&
                    !operation.IsAwaitingAutomaticRetry
                );

            if (pendingCount == 0)
            {
                return;
            }

            CreateBulkCancelButton(
                parent,
                "CancelBatch",
                $"Cancel Batch ({pendingCount})",
                () => CARAUOperationTrackingSys.Instance?
                    .CancelAllTrackedOperations()
            );
        }

        private void CreateClearBatchFailuresButton(RectTransform parent)
        {
            var failureCount =
                operations.Count(operation => operation.IsFailure);

            if (failureCount == 0)
            {
                return;
            }

            CreateBulkCancelButton(
                parent,
                "ClearBatchFailures",
                $"Clear Failures ({failureCount})",
                () => CARAUOperationTrackingSys.Instance?
                    .DismissAllFailedOperations()
            );
        }

        private void CreateRetryBatchButton(RectTransform parent)
        {
            var retryableCount = CARAUOperationTrackingSys.Instance?
                .GetRetryableFailureCount() ?? 0;

            if (retryableCount == 0)
            {
                return;
            }

            CreateBulkCancelButton(
                parent,
                "RetryAllFailures",
                $"Retry All ({retryableCount})",
                () => CARAUOperationTrackingSys.Instance?
                    .RetryAllFailedOperations(),
                "Panel"
            );
        }

        private static void CreateBulkCancelButton(
          RectTransform parent,
          string name,
          string label,
          Action action,
          string themeStyle = "Red"
        )
        {
            var buttonObject = new GameObject(name);
            buttonObject.transform.SetParent(parent, false);
            var buttonRect =
                buttonObject.AddComponent<RectTransform>();
            buttonRect.localScale = Vector3.one;

            var background = buttonObject.AddComponent<Image>();
            background.color = themeStyle == "Red"
                ? new Color(0.58f, 0.08f, 0.10f, 0.98f)
                : new Color(0.28f, 0.18f, 0.58f, 0.98f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() => action?.Invoke());

            var size = buttonObject.AddComponent<LayoutElement>();
            size.minHeight = 46f;
            size.preferredHeight = 46f;

            var text = AddText(
                buttonRect,
                label,
                16f,
                TextAlignmentOptions.Center
            );
            FrameworkTheme.ApplyButton(button, themeStyle, text);
        }

        private RectTransform CreateBeingTabs(RectTransform parent)
        {
            var tabsRect = AutoUtilitiesModFramework.VerticalScrollView.Create(
                parent,
                "TrackedBeings",
                0f,
                0f,
                1f,
                trackedBeingListScrollPosition,
                position => trackedBeingListScrollPosition = position
            ).Content;

            foreach (var group in operations
                .GroupBy(op => new { op.BeingId, op.BeingName })
                .OrderBy(group =>
                    CARAUOperationTrackingSys.GetDisplayedBeingOrder(
                        group.Key.BeingId
                    )
                )
                .ThenBy(group => operations.IndexOf(group.First())))
            {
                var being = ResolveBeing(group.FirstOrDefault());

                CreateBeingTab(
                    tabsRect,
                    group.Key.BeingId,
                    group.Key.BeingName,
                    group.Count(operation =>
                        operation.IsPending &&
                        !operation.IsAwaitingAutomaticRetry
                    ),
                    group.Count(operation =>
                        operation.IsAwaitingAutomaticRetry
                    ),
                    group.Count(operation => operation.IsFailure),
                    being
                );
            }

            return tabsRect;
        }

        private void CreateBeingTab(
          RectTransform parent,
          uint beingId,
          string beingName,
          int pendingCount,
          int waitingCount,
          int failureCount,
          Being being
        )
        {
            var selected = beingId == selectedBeingId;
            var tabObject =
                new GameObject($"TrackedBeing_{beingId}");
            tabObject.transform.SetParent(parent, false);

            var tabRect = tabObject.AddComponent<RectTransform>();
            tabRect.localScale = Vector3.one;

            var tabImage = tabObject.AddComponent<Image>();
            tabImage.color = selected
                ? new Color(0.35f, 0.22f, 0.65f, 0.98f)
                : new Color(0.20f, 0.12f, 0.35f, 0.90f);

            var tabSize = tabObject.AddComponent<LayoutElement>();
            tabSize.minHeight = 58f;
            tabSize.preferredHeight = 58f;

            var button = tabObject.AddComponent<Button>();
            button.targetGraphic = tabImage;
            button.onClick.AddListener(() =>
            {
                selectedBeingId = beingId;
                RebuildContents();
            });

            var textLeft = 6f;
            if (BeingPortraitView.Create(tabRect, S, being))
            {
                textLeft = 57f;
            }

            var preferredTextHeight = AddBeingTabText(
                tabRect,
                $"{beingName}\nActive: {pendingCount} | " +
                $"Waiting: {waitingCount} | Failed: {failureCount}",
                textLeft,
                out var text
            );
            var cardHeight = Mathf.Max(
                58f,
                Mathf.Ceil(preferredTextHeight + 26f)
            );
            tabSize.minHeight = cardHeight;
            tabSize.preferredHeight = cardHeight;
            FrameworkTheme.ApplyButton(
                button,
                selected ? "Tab_Active" : "Tab",
                text
            );
        }

        private static float AddBeingTabText(
          RectTransform parent,
          string value,
          float leftOffset,
          out TextMeshProUGUI text
        )
        {
            var textObject = new GameObject("BeingTabText");
            textObject.transform.SetParent(parent, false);

            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(leftOffset, 4f);
            textRect.offsetMax = new Vector2(-6f, -4f);

            text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = 16f;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.raycastTarget = false;

            var availableTextWidth = Mathf.Max(
                0f,
                210f - leftOffset - 6f
            );
            return text.GetPreferredValues(
                value,
                availableTextWidth,
                0f
            ).y;
        }

        private static void CreateSeparator(RectTransform parent)
        {
            var separatorObject = new GameObject("Separator");
            separatorObject.transform.SetParent(parent, false);
            separatorObject.AddComponent<RectTransform>();

            var image = separatorObject.AddComponent<Image>();
            image.color = new Color(0.42f, 0.28f, 0.68f, 0.90f);
            image.raycastTarget = false;
            FrameworkTheme.ApplyImage(image, "PanelHeader");

            var size = separatorObject.AddComponent<LayoutElement>();
            size.minWidth = 3f;
            size.preferredWidth = 3f;
            size.flexibleWidth = 0f;
        }

        private static RectTransform CreateDetailsColumn(
          RectTransform parent
        )
        {
            var detailsObject = new GameObject("TrackedBeingDetails");
            detailsObject.transform.SetParent(parent, false);
            var detailsRect =
                detailsObject.AddComponent<RectTransform>();
            detailsRect.localScale = Vector3.one;

            var detailsSize =
                detailsObject.AddComponent<LayoutElement>();
            detailsSize.minWidth = 460f;
            detailsSize.flexibleWidth = 1f;

            var detailsLayout =
                detailsObject.AddComponent<VerticalLayoutGroup>();
            detailsLayout.spacing = 8f;
            detailsLayout.childAlignment = TextAnchor.UpperCenter;
            detailsLayout.childControlWidth = true;
            detailsLayout.childControlHeight = true;
            detailsLayout.childForceExpandWidth = true;
            detailsLayout.childForceExpandHeight = false;

            return detailsRect;
        }

        private void DrawSelectedBeing(RectTransform details)
        {
            using (InventoryService.BeginBatchLookup(S))
            {
                DrawSelectedBeingCore(details);
            }
        }

        private void DrawSelectedBeingCore(RectTransform details)
        {
            var selectedOperations = operations
                .Where(op => op.BeingId == selectedBeingId)
                .ToList();
            var being = selectedOperations
                .Select(ResolveBeing)
                .FirstOrDefault(candidate => candidate != null);

            if (being == null)
            {
                var pendingCount = selectedOperations.Count(
                    operation =>
                        operation.IsPending &&
                        !operation.IsAwaitingAutomaticRetry
                );
                var waitingCount = selectedOperations.Count(
                    operation => operation.IsAwaitingAutomaticRetry
                );
                var failureCount = selectedOperations.Count(
                    operation => operation.IsFailure
                );
                AddHeader(
                    details,
                    selectedOperations.FirstOrDefault()?.BeingName ??
                    "Unknown being",
                    pendingCount,
                    waitingCount,
                    failureCount,
                    selectedBeingId
                );
                AddMessage(
                    details,
                    failureCount > 0
                        ? "The original being is unavailable. Retained " +
                          "failure details are shown below."
                        : "This save-restored tracking record has not yet " +
                          "been reconnected to its runtime being."
                );
                AddRetainedFailureCards(
                    details,
                    selectedOperations.Where(
                        operation => operation.IsFailure
                    )
                );
                return;
            }

            var snapshot = BeingScanner.Scan(S, being);
            var selectedPendingCount = selectedOperations.Count(
                operation =>
                    operation.IsPending &&
                    !operation.IsAwaitingAutomaticRetry
            );
            var selectedWaitingCount = selectedOperations.Count(
                operation => operation.IsAwaitingAutomaticRetry
            );
            var selectedFailureCount = selectedOperations.Count(
                operation => operation.IsFailure
            );
            AddHeader(
                details,
                $"{snapshot.Name} | {snapshot.Species} | {snapshot.Status}",
                selectedPendingCount,
                selectedWaitingCount,
                selectedFailureCount,
                selectedBeingId
            );

            var flowObject = new GameObject("TrackedSlotCards");
            flowObject.transform.SetParent(details, false);
            var flowRect = flowObject.AddComponent<RectTransform>();
            flowRect.localScale = Vector3.one;

            var flow = flowObject.AddComponent<AutoUtilitiesModFramework.ResponsiveFlowLayout>();
            flow.CellSize = new Vector2(220f, 142f);
            flow.Spacing = new Vector2(8f, 8f);
            flow.padding = new RectOffset(8, 8, 8, 8);

            var trackedSlotIds = selectedOperations
                .Select(operation => operation.SlotId)
                .Where(slotId => !string.IsNullOrWhiteSpace(slotId))
                .ToHashSet(StringComparer.Ordinal);
            var legacyTrackedSlotNames = selectedOperations
                .Where(operation => string.IsNullOrEmpty(operation.SlotId))
                .Select(operation => operation.SlotName)
                .Where(slotName => !string.IsNullOrWhiteSpace(slotName))
                .ToHashSet(StringComparer.Ordinal);
            var trackedCards = snapshot.SlotCards
                .Where(card =>
                    card != null &&
                    (
                        trackedSlotIds.Contains(card.Slot?.Id) ||
                        legacyTrackedSlotNames.Contains(card.SlotName)
                    )
                )
                .ToList();
            var displayedOperations =
                new HashSet<CARAUTrackedOperation>();

            for (var i = 0; i < trackedCards.Count; i++)
            {
                var card = trackedCards[i];
                var trackedOperation = selectedOperations.FirstOrDefault(
                    operation =>
                        (
                            !string.IsNullOrEmpty(operation.SlotId) &&
                            string.Equals(
                                operation.SlotId,
                                card.Slot?.Id,
                                StringComparison.Ordinal
                            )
                        ) ||
                        (
                            string.IsNullOrEmpty(operation.SlotId) &&
                            string.Equals(
                                operation.SlotName,
                                card.SlotName,
                                StringComparison.Ordinal
                            )
                        )
                );

                if (trackedOperation != null)
                {
                    displayedOperations.Add(trackedOperation);
                }

                var canRetry =
                    trackedOperation?.IsFailure == true &&
                    CARAUOperationTrackingSys.Instance?
                        .IsRetryableFailure(trackedOperation) == true;
                var offerReplacementRetry =
                    trackedOperation?.IsFailure == true &&
                    (canRetry ||
                     CARAUOperationTrackingSys
                         .HasInstalledUnusableReplacement(
                             trackedOperation
                         ));
                var retryExplanation =
                    trackedOperation?.IsFailure == true
                        ? CARAUOperationTrackingSys.Instance?
                            .GetRetryExplanation(trackedOperation)
                        : null;
                var isWaiting =
                    trackedOperation?.IsAwaitingAutomaticRetry == true;
                var automaticRetryExplanation = isWaiting
                    ? CARAUOperationTrackingSys.Instance?
                        .GetAutomaticRetryExplanation(trackedOperation)
                    : null;

                SlotCardView.Create(
                    flowRect,
                    card,
                    isWaiting
                        ? "Waiting to Retry"
                        : trackedOperation?.IsFailure == true
                        ? BuildFailureCardStatus(trackedOperation)
                        : FormatSlotCardStatus(card),
                    null,
                    trackedOperation == null
                        ? null
                        : trackedOperation.IsFailure
                            ? () => CARAUOperationTrackingSys.Instance?
                                .DismissFailedOperation(trackedOperation)
                            : isWaiting
                                ? null
                                : () => CARAUOperationTrackingSys.Instance?
                                .CancelTrackedOperation(trackedOperation),
                    isWaiting
                        ? "Waiting to Retry"
                        : trackedOperation?.IsFailure == true
                        ? BuildFailureBadge(trackedOperation)
                        : null,
                    isWaiting
                        ? BuildWaitingTooltip(
                            trackedOperation,
                            automaticRetryExplanation
                          )
                        : trackedOperation?.IsFailure == true
                        ? BuildFailureTooltip(
                            trackedOperation,
                            retryExplanation
                          )
                        : null,
                    trackedOperation?.IsFailure == true
                        ? true
                        : false,
                    isWaiting
                        ? () => CARAUOperationTrackingSys.Instance?
                            .StopAutomaticRetry(trackedOperation)
                        : offerReplacementRetry
                            ? () => CARAUOperationTrackingSys.Instance?
                            .RetryFailedOperation(trackedOperation)
                            : null,
                    isWaiting ? "■" : offerReplacementRetry ? "↻" : null,
                    isWaiting
                        ? "Stop Retry\n\nStop this automatic retry wait and " +
                          "retain the original failure for manual action."
                        : offerReplacementRetry
                        ? BuildReplacementRetryTooltip(
                            canRetry,
                            retryExplanation
                          )
                        : retryExplanation,
                    footerIsWaiting: isWaiting,
                    footerOverrideColor:
                        GetFailureFooterColor(trackedOperation)
                );
            }

            var removeAllOperations = selectedOperations
                .Where(operation => operation.IsRemoveAllParts)
                .ToList();
            for (var index = 0;
                 index < removeAllOperations.Count;
                 index++)
            {
                var operation = removeAllOperations[index];
                displayedOperations.Add(operation);
                AddRemoveAllPartsTrackingCard(
                    flowRect,
                    operation,
                    trackedCards.Count + index
                );
            }

            AddRetainedFailureCards(
                details,
                selectedOperations.Where(operation =>
                    operation.IsFailure &&
                    !displayedOperations.Contains(operation)
                )
            );

            if (displayedOperations.Count == 0 &&
                (selectedPendingCount > 0 || selectedWaitingCount > 0))
            {
                AddMessage(
                    details,
                    "CARAU is tracking an operation for this being, " +
                    "but its live slot card is not currently available."
                );
            }
        }

        private static void AddRemoveAllPartsTrackingCard(
          RectTransform flowRect,
          CARAUTrackedOperation operation,
          int displayOrder
        )
        {
            var isFailure = operation?.IsFailure == true;
            var card = new SlotCardData
            {
                DisplayOrder = displayOrder,
                SlotName = "All Parts",
                InstalledPartName = "Remove All Parts",
                InstalledPartIconId = "Icons/Color/BodyRemoveAll",
                HealthState = SlotHealthState.Okay,
                HasPendingOperation = !isFailure,
                PendingOperation = operation?.RuntimeOperation,
                IsMassOperation = true
            };

            SlotCardView.Create(
                flowRect,
                card,
                isFailure
                    ? "Queue: Remove All Failed"
                    : "Queue: Remove All Parts",
                null,
                operation == null
                    ? null
                    : isFailure
                        ? () => CARAUOperationTrackingSys.Instance?
                            .DismissFailedOperation(operation)
                        : () => CARAUOperationTrackingSys.Instance?
                            .CancelTrackedOperation(operation),
                isFailure ? "Remove All Failed" : null,
                isFailure
                    ? BuildFailureTooltip(operation, null)
                    : BuildRemoveAllPartsTrackingTooltip(operation),
                cancelIsDismiss: isFailure,
                footerOverrideColor: null
            );
        }

        private static string BuildRemoveAllPartsTrackingTooltip(
          CARAUTrackedOperation operation
        )
        {
            return
                "Remove All Parts In Progress\n\n" +
                $"Being: {operation?.BeingName ?? "Unknown being"}\n" +
                "Operation: Native Remove All Parts\n\n" +
                "Stardeus will remove every currently removable installed " +
                "part in native attachment order. Destroyed slots, welded " +
                "parts, and other non-removable parts remain in place.";
        }

        private static void AddRetainedFailureCards(
          RectTransform details,
          IEnumerable<CARAUTrackedOperation> failures
        )
        {
            var retainedFailures = failures?.ToList() ??
                new List<CARAUTrackedOperation>();

            if (retainedFailures.Count == 0)
            {
                return;
            }

            var flowObject =
                new GameObject("RetainedFailureSlotCards");
            flowObject.transform.SetParent(details, false);
            var flowRect = flowObject.AddComponent<RectTransform>();
            flowRect.localScale = Vector3.one;

            var flow = flowObject.AddComponent<AutoUtilitiesModFramework.ResponsiveFlowLayout>();
            flow.CellSize = new Vector2(220f, 142f);
            flow.Spacing = new Vector2(8f, 8f);
            flow.padding = new RectOffset(8, 8, 8, 8);

            for (var index = 0;
                index < retainedFailures.Count;
                index++)
            {
                var operation = retainedFailures[index];
                var slot = operation.RuntimeSlot;
                var installedDef = slot?.InstalledDef;
                var card = new SlotCardData
                {
                    DisplayOrder = index,
                    SlotName = operation.IsRemoveAllParts
                        ? "All Parts"
                        : operation.SlotName,
                    InstalledPartName = operation.IsRemoveAllParts
                        ? "Remove All Parts"
                        : installedDef != null
                        ? slot.ItemName
                        : operation.TargetPartName,
                    InstalledPartIconId = operation.IsRemoveAllParts
                        ? "Icons/Color/BodyRemoveAll"
                        : installedDef?.IconId,
                    InstalledTier = installedDef?.Tier ?? 0,
                    InstalledQuality = slot?.Quality ?? 0f,
                    InstalledCondition = slot?.State?.Condition ?? 0f,
                    HasInstalledCondition = slot?.State != null,
                    HealthState = operation.IsRemoveAllParts
                        ? SlotHealthState.Okay
                        : slot?.IsDestroyed == true
                        ? SlotHealthState.Destroyed
                        : slot == null ||
                          slot.IsPartRemoved ||
                          installedDef == null
                            ? SlotHealthState.Missing
                            : SlotHealthState.Unusable,
                    IsMassOperation = operation.IsRemoveAllParts
                };

                var canRetry = CARAUOperationTrackingSys.Instance?
                    .IsRetryableFailure(operation) == true;
                var offerReplacementRetry =
                    !operation.IsRemoveAllParts &&
                    (canRetry ||
                    CARAUOperationTrackingSys
                        .HasInstalledUnusableReplacement(operation));
                var retryExplanation =
                    CARAUOperationTrackingSys.Instance?
                        .GetRetryExplanation(operation);

                SlotCardView.Create(
                    flowRect,
                    card,
                    BuildFailureCardStatus(operation),
                    null,
                    () => CARAUOperationTrackingSys.Instance?
                        .DismissFailedOperation(operation),
                    BuildFailureBadge(operation),
                    BuildFailureTooltip(
                        operation,
                        retryExplanation
                    ),
                    true,
                    offerReplacementRetry
                        ? () => CARAUOperationTrackingSys.Instance?
                            .RetryFailedOperation(operation)
                        : null,
                    offerReplacementRetry ? "↻" : null,
                    offerReplacementRetry
                        ? BuildReplacementRetryTooltip(
                            canRetry,
                            retryExplanation
                          )
                        : retryExplanation,
                    footerIsWaiting: false,
                    footerOverrideColor: GetFailureFooterColor(operation)
                );
            }
        }

        private static Being ResolveBeing(
          CARAUTrackedOperation operation
        )
        {
            return operation?.RuntimeBeing ??
                operation?.RuntimeSlot?.Body?.Owner?.Being;
        }

        private static string BuildFailureCardStatus(
          CARAUTrackedOperation operation
        )
        {
            if (operation?.FailureKind ==
                CARAUFailureKind.CompletedWithoutPlannedReplacement)
            {
                return "Queue: Failed - Native Mismatch";
            }

            return CARAUOperationTrackingSys
                .HasInstalledUnusableReplacement(operation)
                ? "Queue: Failed - Unusable"
                : "Queue: Failed";
        }

        private static string BuildFailureBadge(
          CARAUTrackedOperation operation
        )
        {
            if (operation?.FailureKind ==
                CARAUFailureKind.CompletedWithoutPlannedReplacement)
            {
                return "Failed - Native Mismatch";
            }

            return CARAUOperationTrackingSys
                .HasInstalledUnusableReplacement(operation)
                ? "Failed - Unusable"
                : "Failed";
        }

        private static Color? GetFailureFooterColor(
          CARAUTrackedOperation operation
        )
        {
            return operation?.FailureKind ==
                   CARAUFailureKind.CompletedWithoutPlannedReplacement
                ? NativeCompletionMismatchColor
                : (Color?)null;
        }

        private static string BuildReplacementRetryTooltip(
          bool canRetry,
          string retryExplanation
        )
        {
            var availability = canRetry
                ? "A replacement can currently be queued."
                : "The retry is currently blocked. Click to re-check and " +
                  "show the exact current blocker; CARAU will not take or " +
                  "cancel another operation's reservation.";

            return "Retry Replacement\n\nRe-plan this slot against current " +
                   "inventory and attempt to queue one new CARAU " +
                   "replacement operation. " + availability +
                   "\n\n" + retryExplanation;
        }

        private static string FormatSlotCardStatus(SlotCardData card)
        {
            if (card?.Evaluation != null)
            {
                return UpgradeExplanationFormatter.BuildStatus(
                    card.Evaluation
                );
            }

            if (card.HasPendingOperation)
            {
                return "Queue: Pending";
            }

            if (!string.IsNullOrWhiteSpace(card.QueueStatus))
            {
                return card.QueueStatus;
            }

            return UpgradeAnalyzer.GetUpgradeStatusText(card.Decision);
        }

        private static string BuildFailureTooltip(
          CARAUTrackedOperation operation,
          string retryExplanation
        )
        {
            if (operation.IsRemoveAllParts)
            {
                return
                    "Remove All Parts Operation Failed\n\n" +
                    $"Being: {operation.BeingName}\n" +
                    "Operation: Native Remove All Parts\n" +
                    $"Classification: {FormatOutcome(operation.Outcome)}\n" +
                    $"Failure kind: {operation.FailureKind}\n" +
                    $"Reason: {operation.OutcomeReason}\n\n" +
                    "Any parts Stardeus removed before the failure remain " +
                    "removed. CARAU will not automatically repeat this " +
                    "mass operation; re-evaluate it from the Main tab.";
            }

            if (operation.FailureKind ==
                CARAUFailureKind.IdentityCoreDestroyed)
            {
                var used = Math.Max(0, operation.SilentHubRetryCount) +
                           Math.Max(0, operation.PolicyRetryCount) +
                           Math.Max(
                               0,
                               operation.PreSurgeryAutoDocRetryCount
                           );
                return
                    "Core operation cannot be retried by CARAU\n\n" +
                    $"Being: {operation.BeingName}\n" +
                    $"Slot: {operation.SlotName}\n" +
                    $"Replacement: {operation.TargetPartName}\n\n" +
                    "A destructive Core surgery left this chassis without " +
                    "an installed mind-holding Core, and the Being is now " +
                    "dead.\n\n" +
                    "CARAU will not automatically select another Core or " +
                    "attempt post-death recovery. This retained failure also " +
                    "has no CARAU manual retry. Use Vanilla body-management " +
                    "controls to decide how the chassis should be recovered " +
                    "or disposed of.\n\n" +
                    $"Automatic attempts used: {used}.";
            }

            var installedUnusable = CARAUOperationTrackingSys
                .HasInstalledUnusableReplacement(operation);
            var failure =
                (installedUnusable
                    ? "Operation Failed - Replacement Installed Unusable\n\n"
                    : "Operation Failed\n\n") +
                $"Being: {operation.BeingName}\n" +
                $"Slot: {operation.SlotName}\n" +
                $"Replacement: {operation.TargetPartName}\n" +
                $"Classification: {FormatOutcome(operation.Outcome)}" +
                (installedUnusable
                    ? " - installation completed, but the installed " +
                      "replacement is Unusable\n"
                    : "\n") +
                $"Failure kind: {operation.FailureKind}\n" +
                $"Automatic attempts used: " +
                $"{Math.Max(0, operation.SilentHubRetryCount) + Math.Max(0, operation.PolicyRetryCount) + Math.Max(0, operation.PreSurgeryAutoDocRetryCount)}\n" +
                $"Reason: {operation.OutcomeReason}";

            return string.IsNullOrWhiteSpace(retryExplanation)
                ? failure
                : failure + "\n\nCurrent retry evaluation:\n" +
                  retryExplanation;
        }

        private static string BuildWaitingTooltip(
          CARAUTrackedOperation operation,
          string currentEvaluation
        )
        {
            var extract = operation.RuntimeRetryExtractChance.HasValue
                ? $"Remove estimate: " +
                  $"{operation.RuntimeRetryExtractChance.Value * 100f:0.0}%\n"
                : string.Empty;
            var install = operation.RuntimeRetryInstallChance.HasValue
                ? $"Install estimate: " +
                  $"{operation.RuntimeRetryInstallChance.Value * 100f:0.0}%\n"
                : string.Empty;
            var used = Math.Max(0, operation.SilentHubRetryCount) +
                       Math.Max(0, operation.PolicyRetryCount) +
                       Math.Max(
                           0,
                           operation.PreSurgeryAutoDocRetryCount
                       );

            var waiting =
                "Waiting to Retry\n\n" +
                $"Being: {operation.BeingName}\n" +
                $"Slot: {operation.SlotName}\n" +
                $"Replacement: {operation.TargetPartName}\n" +
                $"Failure kind: {operation.FailureKind}\n" +
                $"Policy: {CARAUSettings.RetryPolicy}\n" +
                $"Automatic attempts: {used}/" +
                $"{CARAUSettings.AutomaticRetryLimit}\n" +
                extract + install +
                $"Required estimate: >" +
                $"{CARAUSettings.AutomaticRetryChanceThresholdPercent}%\n" +
                $"Reason: {operation.PendingRetryReason}";

            return string.IsNullOrWhiteSpace(currentEvaluation)
                ? waiting
                : waiting + "\n\nCurrent evaluation:\n" +
                  currentEvaluation;
        }

        private static string FormatOutcome(CARAUOperationOutcome outcome)
        {
            switch (outcome)
            {
                case CARAUOperationOutcome.CancelledExternally:
                    return "Cancelled Externally";

                case CARAUOperationOutcome.Invalid:
                    return "Invalid";

                case CARAUOperationOutcome.Stale:
                    return "Stale";

                default:
                    return "Failed";
            }
        }

        private static void AddHeader(
          RectTransform parent,
          string beingText,
          int pendingCount,
          int waitingCount,
          int failureCount,
          uint beingId
        )
        {
            var headerObject = new GameObject("BeingHeader");
            headerObject.transform.SetParent(parent, false);
            var headerRect = headerObject.AddComponent<RectTransform>();
            headerRect.localScale = Vector3.one;

            var background = headerObject.AddComponent<Image>();
            background.color = new Color(0.18f, 0.11f, 0.34f, 0.98f);
            FrameworkTheme.ApplyImage(background, "PanelHeader");

            var retryableCount = failureCount > 0
                ? CARAUOperationTrackingSys.Instance?
                    .GetRetryableFailureCount(beingId) ?? 0
                : 0;
            var actionCount =
                (pendingCount > 0 ? 1 : 0) +
                (retryableCount > 0 ? 1 : 0) +
                (failureCount > 0 ? 1 : 0);
            var size = headerObject.AddComponent<LayoutElement>();
            var hasActions = actionCount > 0;
            size.minHeight = actionCount > 1
                ? 10f + (actionCount * 38f)
                : 52f;
            size.preferredHeight = size.minHeight;

            var headerText = AddText(
                headerRect,
                $"{beingText}    |    Active: {pendingCount}    |    " +
                $"Waiting: {waitingCount}    |    " +
                $"Failed: {failureCount}",
                18f,
                TextAlignmentOptions.Center,
                Vector2.zero,
                hasActions
                    ? new Vector2(0.78f, 1f)
                    : Vector2.one
            );
            FrameworkTheme.ApplyText(headerText, "Unsized");

            var actionIndex = 0;
            if (pendingCount > 0)
            {
                CreateHeaderActionButton(
                    headerRect,
                    "CancelAll",
                    $"Cancel All ({pendingCount})",
                    GetHeaderActionMin(actionIndex, actionCount),
                    GetHeaderActionMax(actionIndex++, actionCount),
                    () => CARAUOperationTrackingSys.Instance?
                        .CancelTrackedOperationsForBeing(beingId)
                );
            }

            if (retryableCount > 0)
            {
                CreateHeaderActionButton(
                    headerRect,
                    "RetryFailed",
                    $"Retry Failed ({retryableCount})",
                    GetHeaderActionMin(actionIndex, actionCount),
                    GetHeaderActionMax(actionIndex++, actionCount),
                    () => CARAUOperationTrackingSys.Instance?
                        .RetryFailedOperationsForBeing(beingId),
                    "Panel"
                );
            }

            if (failureCount > 0)
            {
                CreateHeaderActionButton(
                    headerRect,
                    "ClearFailures",
                    $"Clear Failures ({failureCount})",
                    GetHeaderActionMin(actionIndex, actionCount),
                    GetHeaderActionMax(actionIndex, actionCount),
                    () => CARAUOperationTrackingSys.Instance?
                        .DismissFailedOperationsForBeing(beingId)
                );
            }
        }

        private static float GetHeaderActionMin(int index, int count)
        {
            var segment = 1f / Math.Max(1, count);
            var padding = count == 1 ? 0.10f : 0.04f;
            return 1f - ((index + 1) * segment) + padding;
        }

        private static float GetHeaderActionMax(int index, int count)
        {
            var segment = 1f / Math.Max(1, count);
            var padding = count == 1 ? 0.10f : 0.04f;
            return 1f - (index * segment) - padding;
        }

        private static void CreateHeaderActionButton(
          RectTransform headerRect,
          string name,
          string label,
          float anchorMinY,
          float anchorMaxY,
          Action action,
          string themeStyle = "Red"
        )
        {
            var buttonObject = new GameObject(name);
            buttonObject.transform.SetParent(headerRect, false);
            var buttonRect =
                buttonObject.AddComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0.78f, anchorMinY);
            buttonRect.anchorMax = new Vector2(0.99f, anchorMaxY);
            buttonRect.offsetMin = Vector2.zero;
            buttonRect.offsetMax = Vector2.zero;

            var buttonBackground =
                buttonObject.AddComponent<Image>();
            buttonBackground.color = themeStyle == "Red"
                ? new Color(0.58f, 0.08f, 0.10f, 0.98f)
                : new Color(0.28f, 0.18f, 0.58f, 0.98f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonBackground;
            button.onClick.AddListener(() => action?.Invoke());

            var text = AddText(
                buttonRect,
                label,
                15f,
                TextAlignmentOptions.Center
            );
            FrameworkTheme.ApplyButton(button, themeStyle, text);
        }

        private static void AddMessage(
          RectTransform parent,
          string message
        )
        {
            var messageObject = new GameObject("Message");
            messageObject.transform.SetParent(parent, false);
            var messageRect =
                messageObject.AddComponent<RectTransform>();
            messageRect.localScale = Vector3.one;

            var size = messageObject.AddComponent<LayoutElement>();
            size.minHeight = 72f;
            size.preferredHeight = 72f;

            AddText(
                messageRect,
                message,
                17f,
                TextAlignmentOptions.Center
            );
        }

        private static TextMeshProUGUI AddText(
          RectTransform parent,
          string value,
          float fontSize,
          TextAlignmentOptions alignment,
          Vector2? anchorMin = null,
          Vector2? anchorMax = null
        )
        {
            var textObject = new GameObject("Text");
            textObject.transform.SetParent(parent, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = anchorMin ?? Vector2.zero;
            textRect.anchorMax = anchorMax ?? Vector2.one;
            textRect.offsetMin = new Vector2(6f, 4f);
            textRect.offsetMax = new Vector2(-6f, -4f);

            var text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.raycastTarget = false;
            return text;
        }

    }
}
