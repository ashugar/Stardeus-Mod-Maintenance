using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Services;
using ColonistAndBotsAutoUpgrade.Systems;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Data;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using KL.Pool;
using KL.Utils;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ColonistAndBotsAutoUpgrade.UI
{
    public sealed class ColonistAndBotsAutoUpgradePanel : System.IDisposable
    {
        private readonly GameState S;
        private readonly IShortcutHandle shortcutHandler;
        private readonly FrameworkPanel frameworkPanel;
        private WPopupData destructiveWeldedPartPopup;

        private string selectedType = "None";
        private string selectedName = "Nothing selected";
        private readonly List<string> debugLines = new();
        private BeingSnapshot currentSnapshot;
        private Being currentBeing;
        private readonly List<Being> selectedBeings = new();
        private readonly Dictionary<Being, BeingSnapshot>
          batchPlanSnapshots = new();
        private readonly Dictionary<Being, List<UpgradeDecision>>
          batchPlanOperations = new();
        private readonly Dictionary<Being, List<UpgradeDecision>>
          batchPlanMissingOperations = new();
        private readonly Dictionary<Being, List<UpgradeDecision>>
          batchPlanBadPartOperations = new();
        private readonly Dictionary<Being, RemoveAllPartsEligibility>
          batchPlanRemoveAllParts = new();
        private float mainBeingListScrollPosition = 1f;
        private int contentGeneration;
        private const double BatchPlanFrameBudgetMs = 4d;
        private const double BatchPlanFrameWarningMs = 50d;
        private const double BatchPlanDurationWarningMs = 10000d;
        private const double BatchQueueDurationWarningMs = 5000d;
        private const int BatchPlanProgressRefreshInterval = 25;
        private const string NativeProcedurePriorityPreference =
          "body.part.proc.priority";
        private const int MinimumNativeProcedurePriority = 1;
        private const int MaximumNativeProcedurePriority = 5;
        private int batchPlanGeneration;
        private int batchPlanNextBeingIndex;
        private int batchPlanActionPhase;
        private int batchPlanTotalPlanned;
        private int batchPlanTotalMissing;
        private int batchPlanTotalBadParts;
        private int batchPlanTotalRemoveAllParts;
        private int batchPlanLastDisplayedProgress;
        private bool batchPlanInProgress;
        private long batchPlanStartedAt;
        private double batchPlanMaximumFrameMs;
        private HashSet<uint> batchPlanAllocatedPartIds;
        private HashSet<uint> batchPlanMissingAllocatedPartIds;
        private HashSet<uint> batchPlanBadPartAllocatedPartIds;
        private System.IDisposable batchPlanLookupScope;

        public bool IsOpen => frameworkPanel.IsOpen;

        public ColonistAndBotsAutoUpgradePanel(
          GameState state,
          IShortcutHandle shortcut
        )
        {
            S = state;
            shortcutHandler = shortcut;
            frameworkPanel = new FrameworkPanel(
              new FrameworkPanelOptions
              {
                  Id = "ColonistAndBotsAutoUpgrade.Main",
                  Title = CARAUVersion.DisplayTitle,
                  MinSize = new Vector2(320f, 220f),
                  MaxSize = new Vector2(1400f, 1000f),
                  RememberGeometry = true,
                  OnClosed = OnFrameworkPanelClosed
              },
              BuildContents
            );

            if (shortcutHandler != null)
            {
                shortcutHandler.StateChanged +=
                  OnShortcutStateChanged;
            }
        }

        private enum PanelTab
        {
            Main,
            Slots,
            Quality,
            Settings,
            Debug
        }
        private PanelTab activeTab = PanelTab.Main;

        private sealed class SlotSettingRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public void Close()
        {
            var wasOpen = frameworkPanel.IsOpen;
            frameworkPanel.Close();
            if (!wasOpen)
            {
                OnFrameworkPanelClosed();
            }
        }

        public void Dispose()
        {
            ReleasePanelContentState();

            if (shortcutHandler != null)
            {
                shortcutHandler.StateChanged -= OnShortcutStateChanged;
            }

            frameworkPanel.Dispose();
        }

        private void OnFrameworkPanelClosed()
        {
            ReleasePanelContentState();
        }

        private void ReleasePanelContentState()
        {
            contentGeneration++;
            CancelBatchPlan("Cancelled:PanelClosed", true);
            destructiveWeldedPartPopup?.Despawn();
            destructiveWeldedPartPopup = null;
        }

        public void ResetForGameBoundary()
        {
            Close();
            selectedBeings.Clear();
            batchPlanSnapshots.Clear();
            batchPlanOperations.Clear();
            batchPlanMissingOperations.Clear();
            batchPlanBadPartOperations.Clear();
            batchPlanRemoveAllParts.Clear();
            currentBeing = null;
            currentSnapshot = null;
            selectedType = "None";
            selectedName = "Nothing selected";
            debugLines.Clear();
            mainBeingListScrollPosition = 1f;
        }

        private void CreateTabButton(RectTransform parent, PanelTab tab, string label)
        {
            var isActive = activeTab == tab;
            UIComponentFactory.CreateThemedTabButton(parent, label, isActive, () => {
                activeTab = tab;
                RebuildContents();
            }, 90f, 120f);
        }

        private void DrawDebugTab(
          PanelContentContext context,
          RectTransform container,
          ref int row
        )
        {
            DrawDebugHeaderRow(context, container, row++);

            var categoryContainer = CreateDebugCategoryContainer(
              context,
              container,
              row++
            );
            var categories = new[]
            {
                CARAUDebugCategory.Error,
                CARAUDebugCategory.Warning,
                CARAUDebugCategory.UI,
                CARAUDebugCategory.Selection,
                CARAUDebugCategory.Decision,
                CARAUDebugCategory.Queue,
                CARAUDebugCategory.Inventory,
                CARAUDebugCategory.Intent,
                CARAUDebugCategory.Cleanup
            };

            for (int i = 0; i < categories.Length; i++)
            {
                var category = categories[i];
                var enabled =
                  CARAUSettings.IsDebugCategoryEnabled(category);

                DebugCategoryCardView.Create(
                  categoryContainer,
                  category.ToString(),
                  enabled,
                  () => {
                      CARAUSettings.SetDebugCategoryEnabled(
                        category,
                        !enabled
                      );
                      RebuildContents();
                  }
                );
            }

            var entries = CARAUDebugLog.Entries;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                if (!CARAUSettings.IsDebugCategoryEnabled(entry.Category))
                {
                    continue;
                }

                UISpawn.Widget<WIconText>(
                  context.Lifetime,
                  UIPrefabId.WContent2Text,
                  container,
                  row++
                  ).Load(
                    IconId.CInfo,
                    $"{entry.GameTime} CARAU {entry.Category} | " +
                    entry.Message
                  );
            }
        }

        private void DrawMainTab(
          PanelContentContext context,
          RectTransform container,
          ref int row
        )
        {
            if (batchPlanInProgress)
            {
                DrawBatchPlanProgress(context, container, ref row);
                return;
            }

            var layoutObject = context.CreateOwned("CARAU_MainBeingLayout");
            layoutObject.transform.SetSiblingIndex(row++);

            var layoutRect = layoutObject.AddComponent<RectTransform>();
            layoutRect.localScale = Vector3.one;

            var layoutSize = layoutObject.AddComponent<LayoutElement>();
            layoutSize.minHeight = 560f;
            layoutSize.flexibleHeight = 1f;

            var layout = layoutObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 10, 10);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            var beingList = CreateMainBeingList(layoutRect);
            CreateMainSeparator(layoutRect);
            var details = CreateMainDetailsColumn(layoutRect);

            DrawMainBeingList(beingList);

            var detailsRow = 0;
            DrawMainBeingDetails(context, details, ref detailsRow);
        }

        private void DrawBatchPlanProgress(
          PanelContentContext context,
          RectTransform container,
          ref int row
        )
        {
            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CInfo,
              $"Analyzing {batchPlanNextBeingIndex} / " +
              $"{selectedBeings.Count} selected Beings"
            );

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CInfo,
              "CARAU is planning in displayed order. Batch Queue and " +
              "individual slot actions will become available after the " +
              "complete plan has been validated."
            );
        }

        private void DrawMainBeingDetails(
          PanelContentContext context,
          RectTransform container,
          ref int row
        )
        {
            var selectedInfo = currentSnapshot != null
              ? $"Selected: {selectedType}    |    Id: {currentSnapshot.Id}    |    {selectedName}"
              : $"Selected: {selectedType}    |    {selectedName}";

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CInfo,
              selectedInfo
            );

            var hasQueueableUpgrades =
              currentSnapshot != null &&
              !currentSnapshot.IsUnsupportedDeadBiological &&
              currentSnapshot.QueueableUpgrades > 0;

            var missingTargetCount = 0;
            var missingQueueableCount = 0;
            var badPartTargetCount = 0;
            var badPartQueueableCount = 0;
            RemoveAllPartsEligibility removeAllPartsEligibility = null;
            if (currentSnapshot != null &&
                !currentSnapshot.IsUnsupportedDeadBiological)
            {
                missingTargetCount = SurgeryQueue.CountActionTargets(
                    currentBeing,
                    UpgradeActionKind.InstallMissing
                );
                badPartTargetCount = SurgeryQueue.CountActionTargets(
                    currentBeing,
                    UpgradeActionKind.ReplaceBadParts
                );
                if (currentBeing != null)
                {
                    if (batchPlanMissingOperations.TryGetValue(
                            currentBeing,
                            out var missingOperations
                        ))
                    {
                        missingQueueableCount = missingOperations.Count;
                    }

                    if (batchPlanBadPartOperations.TryGetValue(
                            currentBeing,
                            out var badPartOperations
                        ))
                    {
                        badPartQueueableCount = badPartOperations.Count;
                    }

                    batchPlanRemoveAllParts.TryGetValue(
                        currentBeing,
                        out removeAllPartsEligibility
                    );
                }
            }

            if (currentSnapshot != null)
            {
                DrawMainActionRow(
                  context,
                  container,
                  row++,
                  hasQueueableUpgrades
                );

                if (missingTargetCount > 0 || badPartTargetCount > 0)
                {
                    DrawMainMaintenanceActionRow(
                        context,
                        container,
                        row++,
                        missingTargetCount,
                        missingQueueableCount,
                        badPartTargetCount,
                        badPartQueueableCount
                    );
                }

                if (removeAllPartsEligibility?.IsTriggered == true)
                {
                    DrawMainRemoveAllPartsRow(
                        context,
                        container,
                        row++,
                        removeAllPartsEligibility
                    );
                }

                if (SurgeryQueue.HasMentalBreakdown(currentBeing?.Data))
                {
                    UISpawn.Widget<WIconText>(
                      context.Lifetime,
                      UIPrefabId.WContent2Text,
                      container,
                      row++
                    ).Load(
                      IconId.CInfo,
                      "Automated operations are unavailable: This Being " +
                      "is currently untargetable due to a Mental Breakdown."
                    );
                }

                if (currentSnapshot.IsUnsupportedDeadBiological)
                {
                    UISpawn.Widget<WIconText>(
                      context.Lifetime,
                      UIPrefabId.WContent2Text,
                      container,
                      row++
                    ).Load(
                      IconId.CInfo,
                      "Automated operations are unavailable: " +
                      "CARAU does not support dead biological beings."
                    );

                    return;
                }
            }

            for (int i = 0; i < debugLines.Count; i++)
            {
                UISpawn.Widget<WIconText>(
                  context.Lifetime,
                  UIPrefabId.WContent2Text,
                  container,
                  row++
                ).Load(
                  IconId.CInfo,
                  debugLines[i]
                );
            }

            if (currentSnapshot == null)
            {
                return;
            }

            var cardContainer = CreateSlotCardContainer(
              context,
              container,
              row++
            );

            for (int i = 0; i < currentSnapshot.SlotCards.Count; i++)
            {
                var card = currentSnapshot.SlotCards[i];
                var destructiveEligibility =
                    DestructiveWeldedPartRemovalService.Evaluate(
                        S,
                        currentBeing?.Data,
                        card.Slot
                    );
                var canDestroyWeldedPart =
                    destructiveEligibility.CanRemove;
                var canQueueCard =
                    !card.IsWeldedDisplayOnly &&
                    SurgeryQueue.IsQueueable(S, card.Decision);
                SlotCardView.Create(
                  cardContainer,
                  card,
                  FormatSlotCardRight(card),
                  canQueueCard
                    ? () => QueueCardUpgrade(card)
                    : null,
                  footerOverride: canDestroyWeldedPart
                    ? card.Slot?.HoldsMind == true
                        ? "Destroy Welded Core"
                        : "Destroy Welded Part"
                    : null,
                  footerActionRequested: canDestroyWeldedPart
                    ? () => ConfirmDestructiveWeldedPartRemoval(card)
                    : null,
                  footerActionBadge: canDestroyWeldedPart
                    ? DestructiveWeldedPartRemovalService.ActionIconId
                    : null,
                  footerActionTooltip: canDestroyWeldedPart
                    ? "Destructive Welded-Part Removal\n\n" +
                      "Irreversibly extract and destroy this welded part " +
                      "and permanently destroy its slot. Confirmation is " +
                      "required."
                    : null,
                  footerOverrideColor: canDestroyWeldedPart
                    ? new Color(0.66f, 0.06f, 0.08f, 0.98f)
                    : null
                );
            }
        }

        private void ConfirmDestructiveWeldedPartRemoval(SlotCardData card)
        {
            var being = currentBeing?.Data;
            var slot = card?.Slot;
            var eligibility =
                DestructiveWeldedPartRemovalService.Evaluate(
                    S,
                    being,
                    slot
                );

            if (!eligibility.CanRemove)
            {
                var blockedReason = eligibility.Message ??
                    "The welded part is no longer eligible.";
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Warning,
                    "Destructive welded-part confirmation was not opened | " +
                    $"Being={being?.Name ?? "unknown"} | " +
                    $"Slot={slot?.NameT ?? "unknown"} | " +
                    $"Reason={blockedReason}"
                );
                S.Sig.ShowErrorMessage.Send(blockedReason);
                RefreshCurrentSnapshot();
                RebuildContents();
                return;
            }

            destructiveWeldedPartPopup?.Despawn();

            var beingId = being.Id;
            var slotId = slot.Id;
            var beingName = being.Name ?? "Unknown being";
            var slotName = slot.NameT ?? slot.Id ?? "Unknown slot";
            var partName = slot.InstalledDef?.NameT ?? "installed part";
            var coreWarning = slot.HoldsMind
                ? " The installed mind will move into the extracted Core, " +
                  "and the chassis may die or become empty."
                : string.Empty;
            var exceptionReason = eligibility.IsUsableCoreException
                ? $" This usable welded Core is eligible because the " +
                  $"complete {eligibility.TerminalGroupSummary} group is " +
                  "terminal."
                : string.Empty;
            var details =
                $"CARAU will forcibly extract the welded {partName} through " +
                "Stardeus's native body-part cleanup, mark the extracted " +
                $"part destroyed, and permanently destroy the {slotName} " +
                "slot. No replacement will be installed." +
                coreWarning + exceptionReason +
                "\n\nThis action cannot be undone. It is intended to make " +
                "an otherwise unrecoverable mechanical body available for " +
                "disposal or disassembly.";

            destructiveWeldedPartPopup = WPopup.Create(
                    isSystem: true,
                    IconId.CExplosion,
                    slot.HoldsMind
                        ? "Destroy Welded Core?"
                        : "Destroy Welded Part?",
                    beingName,
                    details
                )
                .WithAction2(
                    "Cancel",
                    () => destructiveWeldedPartPopup = null
                )
                .WithAction("Confirm Destruction", () =>
                {
                    destructiveWeldedPartPopup = null;
                    if (!DestructiveWeldedPartRemovalService.TryExecute(
                            S,
                            beingId,
                            slotId,
                            out var result))
                    {
                        S.Sig.ShowErrorMessage.Send(
                            result ?? "The destructive removal failed."
                        );
                    }

                    RefreshCurrentSnapshot();
                    RebuildContents();
                })
                .Spawn(UIPoolRoot.SystemPopupContainer);
        }

        private RectTransform CreateMainBeingList(RectTransform parent)
        {
            var listObject = new GameObject("CARAU_MainBeingColumn");
            listObject.transform.SetParent(parent, false);

            var listRect = listObject.AddComponent<RectTransform>();
            listRect.localScale = Vector3.one;

            var listSize = listObject.AddComponent<LayoutElement>();
            listSize.minWidth = 280f;
            listSize.preferredWidth = 280f;
            listSize.flexibleWidth = 0f;

            var columnLayout =
                listObject.AddComponent<VerticalLayoutGroup>();
            columnLayout.spacing = 7f;
            columnLayout.childAlignment = TextAnchor.UpperCenter;
            columnLayout.childControlWidth = true;
            columnLayout.childControlHeight = true;
            columnLayout.childForceExpandWidth = true;
            columnLayout.childForceExpandHeight = false;

            return listRect;
        }

        private void DrawMainBeingList(RectTransform parent)
        {
            var queueableCounts = new Dictionary<Being, int>();
            var totalQueueable = 0;

            foreach (var being in selectedBeings)
            {
                if (being == null)
                {
                    continue;
                }

                var snapshot = batchPlanSnapshots.TryGetValue(
                    being,
                    out var plannedSnapshot
                )
                    ? plannedSnapshot
                    : null;
                var count = snapshot == null ||
                    snapshot.IsUnsupportedDeadBiological
                    ? 0
                    : snapshot.QueueableUpgrades;

                queueableCounts[being] = count;
                totalQueueable += count;
            }

            CreateMainBatchButton(
                parent,
                "CARAU_BatchQueue",
                "Batch Queue",
                totalQueueable,
                totalQueueable > 0,
                QueueAllSelectedBeingUpgrades,
                () => BuildBatchQueueTooltip(
                    totalQueueable,
                    totalQueueable > 0
                )
            );
            CreateMainBatchButton(
                parent,
                "CARAU_QueueAllMissing",
                "Queue All Missing",
                batchPlanTotalMissing,
                batchPlanTotalMissing > 0,
                QueueAllSelectedMissing,
                () => BuildActionBatchTooltip(
                    UpgradeActionKind.InstallMissing,
                    batchPlanTotalMissing
                )
            );
            CreateMainBatchButton(
                parent,
                "CARAU_QueueAllBadParts",
                "Queue All Bad Parts",
                batchPlanTotalBadParts,
                batchPlanTotalBadParts > 0,
                QueueAllSelectedBadParts,
                () => BuildActionBatchTooltip(
                    UpgradeActionKind.ReplaceBadParts,
                    batchPlanTotalBadParts
                )
            );
            CreateMainBatchButton(
                parent,
                "CARAU_QueueAllRemoveParts",
                "Queue All Remove Parts",
                batchPlanTotalRemoveAllParts,
                batchPlanTotalRemoveAllParts > 0,
                QueueAllSelectedRemoveAllParts,
                () => BuildRemoveAllPartsBatchTooltip(
                    batchPlanTotalRemoveAllParts
                )
            );

            var scrollContent = AutoUtilitiesModFramework.VerticalScrollView.Create(
                parent,
                "CARAU_MainBeingList",
                0f,
                0f,
                1f,
                mainBeingListScrollPosition,
                position => mainBeingListScrollPosition = position
            ).Content;

            if (selectedBeings.Count == 0)
            {
                AddMainBeingListMessage(
                    scrollContent,
                    "No being selected"
                );
                return;
            }

            for (var index = 0; index < selectedBeings.Count; index++)
            {
                var being = selectedBeings[index];
                if (being == null)
                {
                    continue;
                }

                var beingName =
                    being.Data?.Name ?? being.ToString();
                queueableCounts.TryGetValue(being, out var count);

                CreateMainBeingTab(
                    scrollContent,
                    beingName,
                    count,
                    being,
                    ReferenceEquals(being, currentBeing),
                    index > 0,
                    index < selectedBeings.Count - 1
                );
            }
        }

        private void CreateMainBatchButton(
          RectTransform parent,
          string objectName,
          string label,
          int queueableCount,
          bool interactable,
          UnityEngine.Events.UnityAction onClick,
          System.Func<string> buildTooltip
        )
        {
            var buttonObject = new GameObject(objectName);
            buttonObject.transform.SetParent(parent, false);

            var buttonRect = buttonObject.AddComponent<RectTransform>();
            buttonRect.localScale = Vector3.one;

            var buttonImage = buttonObject.AddComponent<Image>();
            buttonImage.color = interactable
                ? new Color(0.35f, 0.22f, 0.65f, 0.98f)
                : new Color(0.20f, 0.18f, 0.24f, 0.75f);

            var buttonSize = buttonObject.AddComponent<LayoutElement>();
            buttonSize.minHeight = 48f;
            buttonSize.preferredHeight = 48f;

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            button.interactable = interactable;
            button.onClick.AddListener(onClick);

            var iconObject = new GameObject("KnifeIcon");
            iconObject.transform.SetParent(buttonRect, false);
            var iconRect = iconObject.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0.18f);
            iconRect.anchorMax = new Vector2(0f, 0.82f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(24f, 0f);
            iconRect.sizeDelta = new Vector2(24f, 0f);

            var icon = iconObject.AddComponent<Image>();
            icon.sprite = Images.Sprite(IconId.CKnife);
            icon.preserveAspect = true;
            icon.raycastTarget = false;

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(buttonRect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.22f, 0f);
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text = $"{label} ({queueableCount})";
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.fontSize = 15f;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            FrameworkTheme.ApplyButton(button, "Panel", text);

            UIUtils.AddTooltipDynamic(buttonObject, buildTooltip);
        }

        private string BuildActionBatchTooltip(
          UpgradeActionKind actionKind,
          int queueableCount
        )
        {
            if (actionKind == UpgradeActionKind.InstallMissing)
            {
                return queueableCount > 0
                    ? $"Queue {queueableCount} available missing-part " +
                      "installations across the selected Beings."
                    : "No missing enabled slots are currently queueable " +
                      "across the selected Beings.";
            }

            return queueableCount > 0
                ? $"Queue {queueableCount} replacements for irreparable " +
                  "installed parts across the selected Beings. CARAU uses " +
                  "the best available compatible replacement."
                : "No irreparable, removable installed parts are currently " +
                  "queueable across the selected Beings.";
        }

        private static string BuildRemoveAllPartsBatchTooltip(
          int queueableCount
        )
        {
            return queueableCount > 0
                ? $"Queue Stardeus Remove All Parts for {queueableCount} " +
                  "selected Being(s). A Being qualifies only when every " +
                  "slot in at least one Battery, Core, or Mobilizer group " +
                  "is destroyed or contains an unusable welded part."
                : "No selected Being currently has a fully terminal " +
                  "Battery, Core, or Mobilizer group with removable parts.";
        }

        private string BuildBatchQueueTooltip(
          int queueableCount,
          bool interactable
        )
        {
            var evaluations = new List<UpgradeEvaluation>();
            foreach (var snapshot in batchPlanSnapshots.Values)
            {
                if (snapshot == null)
                {
                    continue;
                }

                for (var index = 0;
                     index < snapshot.Evaluations.Count;
                     index++)
                {
                    var evaluation = snapshot.Evaluations[index];
                    if (evaluation != null)
                    {
                        evaluations.Add(evaluation);
                    }
                }
            }

            var heading = interactable
                ? $"Queue all {queueableCount} available operations " +
                  "for the Beings in the list, in displayed order."
                : "No operations are currently queueable for the " +
                  "Beings in the list.";
            return heading + "\n\n" +
                UpgradeExplanationFormatter.BuildAggregateSummary(
                    evaluations
                );
        }

        private void CreateMainBeingTab(
          RectTransform parent,
          string beingName,
          int queueableCount,
          Being being,
          bool isActive,
          bool canMoveUp,
          bool canMoveDown
        )
        {
            var tabObject = new GameObject("CARAU_MainBeing");
            tabObject.transform.SetParent(parent, false);

            var tabRect = tabObject.AddComponent<RectTransform>();
            tabRect.localScale = Vector3.one;

            var tabImage = tabObject.AddComponent<Image>();
            tabImage.color = isActive
                ? new Color(0.35f, 0.22f, 0.65f, 0.98f)
                : new Color(0.16f, 0.11f, 0.27f, 0.96f);

            var tabButton = tabObject.AddComponent<Button>();
            tabButton.targetGraphic = tabImage;
            tabButton.onClick.AddListener(
                () => SelectMainBeing(being)
            );

            var tabSize = tabObject.AddComponent<LayoutElement>();
            tabSize.minHeight = 58f;
            tabSize.preferredHeight = 58f;

            var textLeft = 6f;
            if (BeingPortraitView.Create(tabRect, S, being))
            {
                textLeft = 57f;
            }

            var textObject = new GameObject("BeingText");
            textObject.transform.SetParent(tabRect, false);

            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(textLeft, 4f);
            const float orderControlsWidth = 34f;
            textRect.offsetMax = new Vector2(-orderControlsWidth, -4f);

            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text =
                $"{beingName}\nQueueable: {queueableCount}";
            text.fontSize = 16f;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.textWrappingMode = TMPro.TextWrappingModes.Normal;
            text.raycastTarget = false;

            var availableTextWidth = Mathf.Max(
                0f,
                210f - textLeft - orderControlsWidth
            );
            var preferredTextHeight = text.GetPreferredValues(
                text.text,
                availableTextWidth,
                0f
            ).y;
            var cardHeight = Mathf.Max(
                58f,
                Mathf.Ceil(preferredTextHeight + 26f)
            );
            tabSize.minHeight = cardHeight;
            tabSize.preferredHeight = cardHeight;

            FrameworkTheme.ApplyButton(
                tabButton,
                isActive ? "Tab_Active" : "Tab",
                text
            );

            CreateMainBeingOrderButton(
                tabRect,
                "MoveUp",
                "▲",
                new Vector2(0.84f, 0.52f),
                new Vector2(0.98f, 0.96f),
                being,
                -1,
                canMoveUp
            );
            CreateMainBeingOrderButton(
                tabRect,
                "MoveDown",
                "▼",
                new Vector2(0.84f, 0.04f),
                new Vector2(0.98f, 0.48f),
                being,
                1,
                canMoveDown
            );
        }

        private void CreateMainBeingOrderButton(
          RectTransform parent,
          string name,
          string label,
          Vector2 anchorMin,
          Vector2 anchorMax,
          Being being,
          int direction,
          bool interactable
        )
        {
            var buttonObject = new GameObject(name);
            buttonObject.transform.SetParent(parent, false);

            var buttonRect = buttonObject.AddComponent<RectTransform>();
            buttonRect.anchorMin = anchorMin;
            buttonRect.anchorMax = anchorMax;
            buttonRect.offsetMin = Vector2.zero;
            buttonRect.offsetMax = Vector2.zero;

            var buttonImage = buttonObject.AddComponent<Image>();
            buttonImage.color = interactable
                ? new Color(0.31f, 0.20f, 0.55f, 0.98f)
                : new Color(0.13f, 0.10f, 0.18f, 0.72f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            button.interactable = interactable;
            button.onClick.AddListener(
                () => MoveMainBeing(being, direction)
            );

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(buttonRect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text = label;
            text.fontSize = 13f;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            FrameworkTheme.ApplyButton(button, "Panel", text);
        }

        private void MoveMainBeing(Being being, int direction)
        {
            if (batchPlanInProgress)
            {
                return;
            }

            var currentIndex = selectedBeings.IndexOf(being);
            var targetIndex = currentIndex + direction;

            if (currentIndex < 0 ||
                targetIndex < 0 ||
                targetIndex >= selectedBeings.Count)
            {
                return;
            }

            selectedBeings.RemoveAt(currentIndex);
            selectedBeings.Insert(targetIndex, being);
            CARAUOperationTrackingSys.SetDisplayedBeingOrder(selectedBeings);
            StartBatchPlan();

            var beingName = being?.Data?.Name ?? being?.ToString() ?? "unknown";
            CARAUDebugLog.Add(
                CARAUDebugCategory.Selection,
                $"Being order changed | Being={beingName} | " +
                $"Position={targetIndex + 1}/{selectedBeings.Count}"
            );

            RebuildContents();
        }

        private void SelectMainBeing(Being being)
        {
            if (batchPlanInProgress ||
                being == null ||
                ReferenceEquals(being, currentBeing))
            {
                return;
            }

            var name = being.Data?.Name ?? being.ToString();
            CARAUDebugLog.Add(
                CARAUDebugCategory.Selection,
                $"Main list being selected | Being={name}"
            );

            ApplyCurrentBeing(being, true);
        }

        private static void AddMainBeingListMessage(
          RectTransform parent,
          string value
        )
        {
            var messageObject = new GameObject("CARAU_MainBeingMessage");
            messageObject.transform.SetParent(parent, false);

            var messageRect =
                messageObject.AddComponent<RectTransform>();
            messageRect.localScale = Vector3.one;

            var messageSize =
                messageObject.AddComponent<LayoutElement>();
            messageSize.minHeight = 48f;
            messageSize.preferredHeight = 48f;

            var text =
                messageObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text = value;
            text.fontSize = 15f;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.color = new Color(0.78f, 0.75f, 0.84f, 1f);
            text.textWrappingMode = TMPro.TextWrappingModes.Normal;
            text.raycastTarget = false;
            FrameworkTheme.ApplyText(text, "Unsized");
        }

        private static void CreateMainSeparator(RectTransform parent)
        {
            var separatorObject = new GameObject("CARAU_MainSeparator");
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

        private static RectTransform CreateMainDetailsColumn(
          RectTransform parent
        )
        {
            var detailsObject = new GameObject("CARAU_MainBeingDetails");
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
            detailsLayout.spacing = 0f;
            detailsLayout.childAlignment = TextAnchor.UpperCenter;
            detailsLayout.childControlWidth = true;
            detailsLayout.childControlHeight = true;
            detailsLayout.childForceExpandWidth = true;
            detailsLayout.childForceExpandHeight = false;

            return detailsRect;
        }

        private void DrawDebugHeaderRow(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex
        )
        {
            var rowObject = context.CreateOwned("CARAU_DebugHeaderRow");
            rowObject.transform.SetParent(parent, false);
            rowObject.transform.SetSiblingIndex(siblingIndex);

            var rect = rowObject.AddComponent<RectTransform>();
            rect.localScale = Vector3.one;

            var background = rowObject.AddComponent<Image>();
            background.color = new Color(0.15f, 0.10f, 0.29f, 0.96f);
            background.raycastTarget = false;
            FrameworkTheme.ApplyImage(background, "PanelHeader");

            var size = rowObject.AddComponent<LayoutElement>();
            size.minHeight = 52f;
            size.preferredHeight = 52f;
            size.flexibleHeight = 0f;

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(10, 8, 6, 6);
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            var infoObject = new GameObject("Information");
            infoObject.transform.SetParent(rect, false);
            var infoRect = infoObject.AddComponent<RectTransform>();
            infoRect.localScale = Vector3.one;
            var infoSize = infoObject.AddComponent<LayoutElement>();
            infoSize.minWidth = 180f;
            infoSize.flexibleWidth = 1f;

            var iconObject = new GameObject("InfoIcon");
            iconObject.transform.SetParent(infoRect, false);
            var iconRect = iconObject.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0.18f);
            iconRect.anchorMax = new Vector2(0f, 0.82f);
            iconRect.anchoredPosition = new Vector2(13f, 0f);
            iconRect.sizeDelta = new Vector2(22f, 0f);
            var icon = iconObject.AddComponent<Image>();
            icon.sprite = Images.Sprite(IconId.CInfo);
            icon.preserveAspect = true;
            icon.raycastTarget = false;

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(infoRect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(30f, 0f);
            textRect.offsetMax = Vector2.zero;
            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text =
              "CARAU keeps its own diagnostic log here to reduce " +
              "Player.log noise. Reproduce an issue, then use Copy Log.";
            text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            text.fontSize = 15f;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            FrameworkTheme.ApplyText(text, "Unsized");

            CreateDebugHeaderButton(
              rect,
              "Copy Log",
              () => {
                  GUIUtility.systemCopyBuffer =
                    CARAUDebugLog.BuildText();
              }
            );
            CreateDebugHeaderButton(
              rect,
              "Clear Log",
              () => {
                  CARAUDebugLog.Clear();
                  RebuildContents();
              }
            );
        }

        private void CreateDebugHeaderButton(
          RectTransform parent,
          string label,
          System.Action action
        )
        {
            var buttonObject = new GameObject(label.Replace(" ", ""));
            buttonObject.transform.SetParent(parent, false);
            var rect = buttonObject.AddComponent<RectTransform>();
            rect.localScale = Vector3.one;
            var size = buttonObject.AddComponent<LayoutElement>();
            size.minWidth = 94f;
            size.preferredWidth = 112f;

            var image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.35f, 0.22f, 0.65f, 0.98f);
            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => action?.Invoke());

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(rect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text = label;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.fontSize = 15f;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            FrameworkTheme.ApplyButton(button, "Panel", text);
        }

        private RectTransform CreateDebugCategoryContainer(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex
        )
        {
            var containerObject =
              context.CreateOwned("CARAU_DebugCategoryFlow");
            containerObject.transform.SetParent(parent, false);
            containerObject.transform.SetSiblingIndex(siblingIndex);

            var rect = containerObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = Vector2.zero;

            var flow =
              containerObject.AddComponent<AutoUtilitiesModFramework.ResponsiveFlowLayout>();
            flow.CellSize = new Vector2(110f, 92f);
            flow.Spacing = new Vector2(8f, 8f);
            flow.padding = new RectOffset(8, 8, 8, 8);

            return rect;
        }

        private void DrawMainActionRow(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex,
          bool hasQueueableUpgrades
        )
        {
            var rowObject = context.CreateOwned("CARAU_MainActionRow");
            rowObject.transform.SetParent(parent, false);
            rowObject.transform.SetSiblingIndex(siblingIndex);

            var rowRect = rowObject.AddComponent<RectTransform>();
            rowRect.localScale = Vector3.one;

            var background = rowObject.AddComponent<Image>();
            background.color = new Color(0.15f, 0.10f, 0.29f, 0.96f);
            background.raycastTarget = false;
            FrameworkTheme.ApplyImage(background, "PanelHeader");

            var rowSize = rowObject.AddComponent<LayoutElement>();
            rowSize.minHeight = 48f;
            rowSize.preferredHeight = 48f;
            rowSize.flexibleHeight = 0f;

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(14, 8, 6, 6);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            var summaryObject = new GameObject("Summary");
            summaryObject.transform.SetParent(rowRect, false);
            var summaryRect =
              summaryObject.AddComponent<RectTransform>();
            summaryRect.localScale = Vector3.one;

            var summarySize =
              summaryObject.AddComponent<LayoutElement>();
            summarySize.minWidth = 120f;
            summarySize.flexibleWidth = 1f;

            var summary =
              summaryObject.AddComponent<TMPro.TextMeshProUGUI>();
            summary.text =
              $"Species: {currentSnapshot.Species}    |    " +
              $"Status: {currentSnapshot.Status}";
            summary.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            summary.fontSize = 16f;
            summary.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            summary.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            summary.raycastTarget = false;
            FrameworkTheme.ApplyText(summary, "Unsized");

            var buttonObject = new GameObject("QueueAll");
            buttonObject.transform.SetParent(rowRect, false);
            var buttonRect =
              buttonObject.AddComponent<RectTransform>();
            buttonRect.localScale = Vector3.one;

            var buttonSize =
              buttonObject.AddComponent<LayoutElement>();
            buttonSize.minWidth = 130f;
            buttonSize.preferredWidth = 190f;
            buttonSize.flexibleWidth = 0f;

            var buttonImage = buttonObject.AddComponent<Image>();
            buttonImage.color = hasQueueableUpgrades
              ? new Color(0.35f, 0.22f, 0.65f, 0.98f)
              : new Color(0.20f, 0.18f, 0.24f, 0.75f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            button.interactable = hasQueueableUpgrades;
            button.onClick.AddListener(QueueAllSingleBeingUpgrades);

            var iconObject = new GameObject("KnifeIcon");
            iconObject.transform.SetParent(buttonRect, false);
            var iconRect = iconObject.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0.18f);
            iconRect.anchorMax = new Vector2(0f, 0.82f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(24f, 0f);
            iconRect.sizeDelta = new Vector2(24f, 0f);

            var icon = iconObject.AddComponent<Image>();
            icon.sprite = Images.Sprite(IconId.CKnife);
            icon.preserveAspect = true;
            icon.raycastTarget = false;

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(buttonRect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.22f, 0f);
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text =
              $"Queue All ({currentSnapshot.QueueableUpgrades})";
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.fontSize = 16f;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            FrameworkTheme.ApplyButton(button, "Panel", text);

            UIUtils.AddTooltipDynamic(
              buttonObject,
              () => BuildSingleQueueTooltip(hasQueueableUpgrades)
            );
        }

        private void DrawMainMaintenanceActionRow(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex,
          int missingTargetCount,
          int missingQueueableCount,
          int badPartTargetCount,
          int badPartQueueableCount
        )
        {
            var rowObject = context.CreateOwned(
                "CARAU_MainMaintenanceActionRow"
            );
            rowObject.transform.SetParent(parent, false);
            rowObject.transform.SetSiblingIndex(siblingIndex);

            var rowRect = rowObject.AddComponent<RectTransform>();
            rowRect.localScale = Vector3.one;

            var background = rowObject.AddComponent<Image>();
            background.color = new Color(0.15f, 0.10f, 0.29f, 0.96f);
            background.raycastTarget = false;
            FrameworkTheme.ApplyImage(background, "PanelHeader");

            var rowSize = rowObject.AddComponent<LayoutElement>();
            rowSize.minHeight = 48f;
            rowSize.preferredHeight = 48f;
            rowSize.flexibleHeight = 0f;

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 6);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            if (missingTargetCount > 0)
            {
                CreateMaintenanceActionButton(
                    rowRect,
                    "InstallMissing",
                    "Install Missing",
                    IconId.CBodyInstallMissing,
                    missingTargetCount,
                    missingQueueableCount,
                    UpgradeActionKind.InstallMissing
                );
            }

            if (badPartTargetCount > 0)
            {
                CreateMaintenanceActionButton(
                    rowRect,
                    "ReplaceBadParts",
                    "Replace Bad Parts",
                    IconId.CBodyReplaceBad,
                    badPartTargetCount,
                    badPartQueueableCount,
                    UpgradeActionKind.ReplaceBadParts
                );
            }
        }

        private void CreateMaintenanceActionButton(
          RectTransform parent,
          string objectName,
          string label,
          string iconId,
          int targetCount,
          int queueableCount,
          UpgradeActionKind actionKind
        )
        {
            var buttonObject = new GameObject(objectName);
            buttonObject.transform.SetParent(parent, false);
            var buttonRect = buttonObject.AddComponent<RectTransform>();
            buttonRect.localScale = Vector3.one;

            var buttonSize = buttonObject.AddComponent<LayoutElement>();
            buttonSize.minWidth = 145f;
            buttonSize.preferredWidth = 195f;
            buttonSize.flexibleWidth = 1f;

            var isQueueable = queueableCount > 0;
            var buttonImage = buttonObject.AddComponent<Image>();
            buttonImage.color = isQueueable
              ? new Color(0.35f, 0.22f, 0.65f, 0.98f)
              : new Color(0.20f, 0.18f, 0.24f, 0.75f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            button.interactable = isQueueable;
            button.onClick.AddListener(
                () => QueueSingleBeingAction(actionKind)
            );

            var iconObject = new GameObject("ActionIcon");
            iconObject.transform.SetParent(buttonRect, false);
            var iconRect = iconObject.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0.18f);
            iconRect.anchorMax = new Vector2(0f, 0.82f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(22f, 0f);
            iconRect.sizeDelta = new Vector2(22f, 0f);

            var icon = iconObject.AddComponent<Image>();
            icon.sprite = Images.Sprite(iconId);
            icon.preserveAspect = true;
            icon.raycastTarget = false;

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(buttonRect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.17f, 0f);
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text = $"{label} ({queueableCount})";
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.fontSize = 14f;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            FrameworkTheme.ApplyButton(button, "Panel", text);

            UIUtils.AddTooltipDynamic(
                buttonObject,
                () => BuildSingleActionTooltip(
                    actionKind,
                    targetCount,
                    queueableCount
                )
            );
        }

        private void DrawMainRemoveAllPartsRow(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex,
          RemoveAllPartsEligibility eligibility
        )
        {
            var rowObject = context.CreateOwned(
                "CARAU_MainRemoveAllPartsRow"
            );
            rowObject.transform.SetParent(parent, false);
            rowObject.transform.SetSiblingIndex(siblingIndex);

            var rowRect = rowObject.AddComponent<RectTransform>();
            rowRect.localScale = Vector3.one;

            var background = rowObject.AddComponent<Image>();
            background.color = new Color(0.15f, 0.10f, 0.29f, 0.96f);
            background.raycastTarget = false;
            FrameworkTheme.ApplyImage(background, "PanelHeader");

            var rowSize = rowObject.AddComponent<LayoutElement>();
            rowSize.minHeight = 48f;
            rowSize.preferredHeight = 48f;
            rowSize.flexibleHeight = 0f;

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(14, 8, 6, 6);
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            var summaryObject = new GameObject("Summary");
            summaryObject.transform.SetParent(rowRect, false);
            var summaryRect =
                summaryObject.AddComponent<RectTransform>();
            summaryRect.localScale = Vector3.one;
            var summarySize =
                summaryObject.AddComponent<LayoutElement>();
            summarySize.minWidth = 120f;
            summarySize.flexibleWidth = 1f;

            var summary =
                summaryObject.AddComponent<TMPro.TextMeshProUGUI>();
            summary.text =
                $"Terminal group: {eligibility.TerminalGroupSummary}";
            summary.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            summary.fontSize = 15f;
            summary.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            summary.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            summary.raycastTarget = false;

            var buttonObject = new GameObject("RemoveAllParts");
            buttonObject.transform.SetParent(rowRect, false);
            var buttonRect =
                buttonObject.AddComponent<RectTransform>();
            buttonRect.localScale = Vector3.one;

            var buttonSize =
                buttonObject.AddComponent<LayoutElement>();
            buttonSize.minWidth = 160f;
            buttonSize.preferredWidth = 210f;
            buttonSize.flexibleWidth = 0f;

            var buttonImage = buttonObject.AddComponent<Image>();
            buttonImage.color = eligibility.CanQueue
                ? new Color(0.35f, 0.22f, 0.65f, 0.98f)
                : new Color(0.20f, 0.18f, 0.24f, 0.75f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = buttonImage;
            button.interactable = eligibility.CanQueue;
            button.onClick.AddListener(
                () => QueueSelectedRemoveAllParts(currentBeingOnly: true)
            );

            var iconObject = new GameObject("ActionIcon");
            iconObject.transform.SetParent(buttonRect, false);
            var iconRect = iconObject.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0f, 0.18f);
            iconRect.anchorMax = new Vector2(0f, 0.82f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(22f, 0f);
            iconRect.sizeDelta = new Vector2(22f, 0f);

            var icon = iconObject.AddComponent<Image>();
            icon.sprite = Images.Sprite("Icons/Color/BodyRemoveAll");
            icon.preserveAspect = true;
            icon.raycastTarget = false;

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(buttonRect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.17f, 0f);
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text =
                $"Remove All Parts ({eligibility.RemovablePartCount})";
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.fontSize = 14f;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            FrameworkTheme.ApplyButton(button, "Panel", text);

            UIUtils.AddTooltipDynamic(
                buttonObject,
                () => BuildSingleRemoveAllPartsTooltip(
                    RemoveAllPartsService.Evaluate(S, currentBeing)
                )
            );
        }

        private static string BuildSingleRemoveAllPartsTooltip(
          RemoveAllPartsEligibility eligibility
        )
        {
            if (eligibility == null)
            {
                return "Remove All Parts is unavailable.";
            }

            return eligibility.Message +
                "\n\nCARAU queues Stardeus's native Remove All Parts " +
                "operation. Stardeus performs the removals in attachment " +
                "order; CARAU does not directly alter the body.";
        }

        private static string BuildSingleActionTooltip(
          UpgradeActionKind actionKind,
          int targetCount,
          int queueableCount
        )
        {
            if (actionKind == UpgradeActionKind.InstallMissing)
            {
                return $"Missing enabled slots: {targetCount}; currently " +
                  $"queueable: {queueableCount}. CARAU applies its normal " +
                  "enabled-slot, inventory, reservation, " +
                  "facility, and Being-state safeguards.";
            }

            return $"Irreparable removable parts: {targetCount}; currently " +
              $"queueable: {queueableCount}. CARAU selects the best available " +
              "compatible replacement without applying the normal quality " +
              "improvement threshold.";
        }

        private string BuildSingleQueueTooltip(
          bool hasQueueableUpgrades
        )
        {
            if (currentSnapshot == null)
            {
                return "No Being is currently available for evaluation.";
            }

            var heading = hasQueueableUpgrades
                ? $"Queue all {currentSnapshot.QueueableUpgrades} " +
                  "currently available operations for this Being."
                : "No operations are currently queueable.";
            return heading + "\n\n" +
                UpgradeExplanationFormatter.BuildAggregateSummary(
                    currentSnapshot.Evaluations
                );
        }

        private RectTransform CreateSlotCardContainer(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex
        )
        {
            var containerObject =
              context.CreateOwned("CARAU_SlotCardFlow");
            containerObject.transform.SetParent(parent, false);
            containerObject.transform.SetSiblingIndex(siblingIndex);

            var rect =
              containerObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = Vector2.zero;

            var flow =
              containerObject.AddComponent<AutoUtilitiesModFramework.ResponsiveFlowLayout>();
            flow.CellSize = new Vector2(220f, 142f);
            flow.Spacing = new Vector2(8f, 8f);
            flow.padding = new RectOffset(8, 8, 8, 8);

            return rect;
        }

        private static string FormatSlotCardRight(SlotCardData card)
        {
            if (card?.Evaluation != null)
            {
                return UpgradeExplanationFormatter.BuildStatus(
                    card.Evaluation
                );
            }

            var decisionText =
              UpgradeAnalyzer.GetUpgradeStatusText(card.Decision);

            if (card.QueueStatus == "Queue: Not needed" ||
                string.IsNullOrWhiteSpace(card.QueueStatus))
            {
                return decisionText;
            }

            return $"{decisionText} | {card.QueueStatus}";
        }

        private void DrawQualityTab(
          PanelContentContext context,
          RectTransform container,
          ref int row
        )
        {
            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CCog,
              "Upgrade Policy"
            );

            var policyContainer = CreateSlotToggleContainer(
                context,
                container,
                row++
            );
            var allowTierUpgrades = CARAUSettings.AllowTierUpgrades;
            SlotToggleCardView.Create(
                policyContainer,
                "Higher-Tier Upgrades",
                allowTierUpgrades,
                () => SetUpgradePolicy(
                    tierUpgrades: !allowTierUpgrades,
                    qualityUpgrades: null
                ),
                "Allow normal CARAU planning to replace an installed part " +
                "with an available compatible part from a higher Tier. " +
                "Tier upgrades keep CARAU's existing tier-first selection " +
                "and are not limited by the Quality threshold."
            );

            var allowQualityUpgrades = CARAUSettings.AllowQualityUpgrades;
            SlotToggleCardView.Create(
                policyContainer,
                "Same-Tier Quality Upgrades",
                allowQualityUpgrades,
                () => SetUpgradePolicy(
                    tierUpgrades: null,
                    qualityUpgrades: !allowQualityUpgrades
                ),
                "Allow normal CARAU planning to replace an installed part " +
                "with a better available part of the same Tier. The " +
                "installed part must be below the Quality threshold and the " +
                "replacement must improve Quality by at least 5%."
            );

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CInfo,
              "These controls affect normal upgrades only. Install Missing " +
              "and Replace Bad Parts remain available as restoration actions."
            );

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CCog,
              "Same-Tier Quality Threshold"
            );

            DrawQualityAdjustmentRow(context, container, row++);
            var generation = contentGeneration;
            var editorRoot =
              context.CreateOwned("CARAU_QualityThresholdEditor");
            editorRoot.transform.SetParent(container, false);

            var editor = QualityThresholdView.Create(
              context.Lifetime,
              container,
              row++,
              CARAUSettings.QualityThresholdPercent,
              value => {
                  CARAUSettings.QualityThresholdPercent = value;
              },
              value => {
                  SetQualityThreshold(value);
              },
              () => IsContentGenerationCurrent(generation),
              editorRoot
            );

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CInfo,
              "Installed parts below this threshold may be replaced by a higher-quality compatible part of the same tier. Tier upgrades are not limited by this setting."
            );
        }

        private void DrawQualityAdjustmentRow(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex
        )
        {
            var rowObject =
              context.CreateOwned("CARAU_QualityAdjustmentRow");
            rowObject.transform.SetParent(parent, false);
            rowObject.transform.SetSiblingIndex(siblingIndex);

            var rowRect = rowObject.AddComponent<RectTransform>();
            rowRect.localScale = Vector3.one;

            var background = rowObject.AddComponent<Image>();
            background.color = new Color(0.15f, 0.10f, 0.29f, 0.96f);
            background.raycastTarget = false;
            FrameworkTheme.ApplyImage(background, "PanelHeader");

            var rowSize = rowObject.AddComponent<LayoutElement>();
            rowSize.minHeight = 48f;
            rowSize.preferredHeight = 48f;
            rowSize.flexibleHeight = 0f;

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 6);
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            CreateQualityAdjustmentButton(rowRect, "-10%", -10);
            CreateQualityAdjustmentButton(rowRect, "-1%", -1);
            CreateQualityAdjustmentButton(rowRect, "+1%", 1);
            CreateQualityAdjustmentButton(rowRect, "+10%", 10);
        }

        private void CreateQualityAdjustmentButton(
          RectTransform parent,
          string label,
          int amount
        )
        {
            var buttonObject = new GameObject(
              $"CARAU_QualityAdjust_{label}"
            );
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.AddComponent<RectTransform>();
            rect.localScale = Vector3.one;

            var size = buttonObject.AddComponent<LayoutElement>();
            size.minWidth = 72f;
            size.preferredWidth = 110f;
            size.flexibleWidth = 0f;

            var image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.35f, 0.22f, 0.65f, 0.98f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => {
                SetQualityThreshold(
                  CARAUSettings.QualityThresholdPercent + amount
                );
            });

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(rect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text = label;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.fontSize = 16f;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            FrameworkTheme.ApplyButton(button, "Panel", text);

            UIUtils.AddTooltipDynamic(
              buttonObject,
              () => $"Adjust the quality threshold by {label}."
            );
        }

        private void DrawSettingsTab(
          PanelContentContext context,
          RectTransform container,
          ref int row
        )
        {
            var generation = contentGeneration;

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CCog,
              "Native Surgery & Assembly Priority"
            );

            var priorityEditorRoot =
              context.CreateOwned("CARAU_NativeProcedurePriorityEditor");
            priorityEditorRoot.transform.SetParent(container, false);
            var nativePriority = Mathf.Clamp(
                Game.A.BodyPartProcedurePriority,
                MinimumNativeProcedurePriority,
                MaximumNativeProcedurePriority
            );
            var priorityEditor = QualityThresholdView.CreateBounded(
                context.Lifetime,
                container,
                row++,
                nativePriority,
                null,
                SetNativeProcedurePriority,
                () => IsContentGenerationCurrent(generation),
                "CARAU_NativeProcedurePriorityEditor",
                "Stardeus Procedure Priority",
                MinimumNativeProcedurePriority,
                MaximumNativeProcedurePriority,
                string.Empty,
                "Used by new CARAU and manual surgery or Assembly Hub tasks. Existing work is not changed.",
                priorityEditorRoot
            );
            UIUtils.AddTooltipDynamic(
                priorityEditor.Root,
                () =>
                    "Native Surgery & Assembly Priority\n\n" +
                    "This is Stardeus' current-game Body Part Procedure " +
                    "Priority, not a separate CARAU setting. New CARAU and " +
                    "manual procedures use it for the patient's task. When " +
                    "the patient reaches a Surgery Hub or Assembly Hub, the " +
                    "Hub carries the same priority into operator and " +
                    "replacement-hauling work.\n\n" +
                    "Changing it does not rewrite, interrupt, or reorder " +
                    "already-created tasks."
            );

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CCog,
              "Automatic Retry"
            );

            var policyContainer = CreateSlotToggleContainer(
                context,
                container,
                row++
            );
            CreateRetryPolicyCard(
                policyContainer,
                CARAURetryPolicy.Manual,
                "Manual",
                "Player starts retained retries",
                "Retained failures require a manual Retry action. The narrow " +
                "Hub-availability recovery remains subject to the shared " +
                "automatic attempt and chance controls."
            );
            CreateRetryPolicyCard(
                policyContainer,
                CARAURetryPolicy.TransientOnly,
                "Transient",
                "Temporary pre-surgery failures",
                "Automatically retry proven temporary failures that occurred " +
                "before surgery. Native surgical outcomes remain retained."
            );
            CreateRetryPolicyCard(
                policyContainer,
                CARAURetryPolicy.TransientAndSurgical,
                "Transient + Surgery",
                "Also repeats eligible surgery",
                "Includes transient retries and eligible native surgical " +
                "failures. Another replacement may be consumed and another " +
                "surgical failure can occur."
            );

            var attemptsEditorRoot =
              context.CreateOwned("CARAU_AutomaticRetryLimitEditor");
            attemptsEditorRoot.transform.SetParent(container, false);
            var attemptsEditor = QualityThresholdView.CreateBounded(
                context.Lifetime,
                container,
                row++,
                CARAUSettings.AutomaticRetryLimit,
                value => {
                    CARAUSettings.AutomaticRetryLimit =
                        CARAUSettings.ClampAutomaticRetryLimit(value);
                },
                SetAutomaticRetryLimit,
                () => IsContentGenerationCurrent(generation),
                "CARAU_AutomaticRetryLimitEditor",
                "Automatic Retry Attempts",
                CARAUSettings.MinimumAutomaticRetryLimit,
                CARAUSettings.MaximumAutomaticRetryLimit,
                string.Empty,
                "Shared by every CARAU automatic requeue. 0 disables automatic retries; manual controls are not limited.",
                attemptsEditorRoot
            );
            UIUtils.AddTooltipDynamic(
                attemptsEditor.Root,
                () =>
                    "Automatic Retry Attempts\n\n" +
                    "One shared maximum for Hub, transient, and surgical " +
                    "automatic requeues in an operation lineage.\n\n" +
                    "Manual Retry, Retry Failed, and Retry All bypass this " +
                    "maximum and begin a fresh lineage."
            );

            var chanceEditorRoot =
              context.CreateOwned("CARAU_AutomaticRetryChanceEditor");
            chanceEditorRoot.transform.SetParent(container, false);
            var chanceEditor = QualityThresholdView.CreateBounded(
                context.Lifetime,
                container,
                row++,
                CARAUSettings.AutomaticRetryChanceThresholdPercent,
                value => {
                    CARAUSettings.AutomaticRetryChanceThresholdPercent =
                        CARAUSettings.ClampPercentage(value);
                },
                SetAutomaticRetryChanceThreshold,
                () => IsContentGenerationCurrent(generation),
                "CARAU_AutomaticRetryChanceEditor",
                "Automatic Surgery Retry Chance Threshold",
                0,
                100,
                "%",
                $"Automatic retry requires a fresh Stardeus estimate above {CARAUSettings.AutomaticRetryChanceThresholdPercent}%.",
                chanceEditorRoot
            );
            UIUtils.AddTooltipDynamic(
                chanceEditor.Root,
                () =>
                    "Automatic Surgery Retry Chance Threshold\n\n" +
                    "CARAU uses Stardeus' operator-neutral estimate shown in " +
                    "Vanilla. Swaps use the lower of removal and installation. " +
                    "The value must be strictly above the threshold.\n\n" +
                    "The eventual operator and target state can change the " +
                    "actual roll. An automated Hub with no operator is " +
                    "evaluated by Core at skill level 0 and may have a " +
                    "materially lower actual chance.\n\n" +
                    "Manual retry actions ignore this threshold."
            );

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CStress,
              "Mental Breakdown Prevention"
            );

            var preventionContainer = CreateSlotToggleContainer(
                context,
                container,
                row++
            );
            var autoMoodRegulator =
                CARAUSettings.AutoInstallMoodRegulatorAfterMentalBreakdown;
            SlotToggleCardView.Create(
                preventionContainer,
                "Auto-install Mood Regulator",
                autoMoodRegulator,
                () => {
                    CARAUSettings.AutoInstallMoodRegulatorAfterMentalBreakdown =
                        !autoMoodRegulator;
                    RebuildContents();
                },
                "After a native Mental Breakdown interrupts CARAU work, " +
                "wait for the breakdown to end and then install an available " +
                "Mood Regulator before retrying. CARAU uses only an empty, " +
                "compatible Implant slot and never replaces an existing " +
                "implant. The automatic surgery-estimate threshold still " +
                "applies. If no eligible regulator is available, the original " +
                "retry continues. This option is off by default."
            );

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.IconsFactionVirus,
              "Hacking Prevention"
            );

            var hackingPreventionContainer = CreateSlotToggleContainer(
                context,
                container,
                row++
            );
            var autoFirewallChip =
                CARAUSettings.AutoInstallFirewallChipWhenHacked;
            SlotToggleCardView.Create(
                hackingPreventionContainer,
                "Auto-install Firewall Chip",
                autoFirewallChip,
                () => {
                    CARAUSettings.AutoInstallFirewallChipWhenHacked =
                        !autoFirewallChip;
                    CARAUOperationTrackingSys.Instance?
                        .OnPreventativeImplantSettingsChanged();
                    RebuildContents();
                },
                "When Core marks a living player-owned mechanical Being as " +
                "being hacked, install an available Firewall Chip through " +
                "native surgery. CARAU acts only when the Implant slot is " +
                "empty and usable; an Obey Chip, Mood Regulator, or any other " +
                "existing Implant is never replaced. Pending operations and " +
                "CARAU tracking are respected. The automatic surgery-estimate " +
                "threshold still applies. This option is off by default."
            );

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CCog,
              "Optional UI Shortcut"
            );

            DrawShortcutSettingsRow(context, container, row++);

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CInfo,
              shortcutHandler?.StatusMessage ??
              "Shortcut handling is unavailable."
            );

            UISpawn.Widget<WIconText>(
              context.Lifetime,
              UIPrefabId.WContent2Text,
              container,
              row++
            ).Load(
              IconId.CInfo,
              "CARAU checks Stardeus keyboard bindings before accepting a key. Other mods that listen for keys outside the Stardeus binding system cannot be detected."
            );
        }

        private void CreateRetryPolicyCard(
          RectTransform parent,
          CARAURetryPolicy policy,
          string label,
          string subtitle,
          string tooltip
        )
        {
            RetryPolicyCardView.Create(
                parent,
                label,
                subtitle,
                tooltip,
                CARAUSettings.RetryPolicy == policy,
                () => SetRetryPolicy(policy)
            );
        }

        private void DrawShortcutSettingsRow(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex
        )
        {
            var rowObject =
              context.CreateOwned("CARAU_ShortcutSettingsRow");
            rowObject.transform.SetParent(parent, false);
            rowObject.transform.SetSiblingIndex(siblingIndex);

            var rowRect = rowObject.AddComponent<RectTransform>();
            rowRect.localScale = Vector3.one;

            var background = rowObject.AddComponent<Image>();
            background.color = new Color(0.15f, 0.10f, 0.29f, 0.96f);
            background.raycastTarget = false;
            FrameworkTheme.ApplyImage(background, "PanelHeader");

            var rowSize = rowObject.AddComponent<LayoutElement>();
            rowSize.minHeight = 58f;
            rowSize.preferredHeight = 58f;
            rowSize.flexibleHeight = 0f;

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(16, 8, 8, 8);
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            var currentTextObject = new GameObject("CurrentShortcut");
            currentTextObject.transform.SetParent(rowRect, false);
            var currentTextRect =
              currentTextObject.AddComponent<RectTransform>();
            currentTextRect.localScale = Vector3.one;

            var currentTextSize =
              currentTextObject.AddComponent<LayoutElement>();
            currentTextSize.minWidth = 220f;
            currentTextSize.preferredWidth = 360f;
            currentTextSize.flexibleWidth = 1f;

            var currentText =
              currentTextObject.AddComponent<TMPro.TextMeshProUGUI>();
            currentText.text =
              $"Current shortcut: {shortcutHandler?.DisplayName ?? "Unavailable"}";
            currentText.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            currentText.fontSize = 17f;
            currentText.textWrappingMode =
              TMPro.TextWrappingModes.NoWrap;
            currentText.raycastTarget = false;
            FrameworkTheme.ApplyText(currentText, "Unsized");

            CreateShortcutSettingsButton(
              rowRect,
              shortcutHandler?.IsCapturing == true
                ? "Press a key..."
                : "Set Shortcut",
              () => shortcutHandler?.BeginCapture(),
              shortcutHandler != null &&
              shortcutHandler.IsCapturing != true
            );

            CreateShortcutSettingsButton(
              rowRect,
              "Clear",
              () => shortcutHandler?.Clear(),
              shortcutHandler?.HasShortcut == true ||
              shortcutHandler?.IsCapturing == true
            );
        }

        private static void CreateShortcutSettingsButton(
          RectTransform parent,
          string label,
          System.Action action,
          bool interactable
        )
        {
            var buttonObject = new GameObject(
              $"CARAU_Shortcut_{label}"
            );
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.AddComponent<RectTransform>();
            rect.localScale = Vector3.one;

            var size = buttonObject.AddComponent<LayoutElement>();
            size.minWidth = 110f;
            size.preferredWidth = 145f;
            size.flexibleWidth = 0f;

            var image = buttonObject.AddComponent<Image>();
            image.color = interactable
              ? new Color(0.35f, 0.22f, 0.65f, 0.98f)
              : new Color(0.22f, 0.18f, 0.28f, 0.70f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.interactable = interactable;
            button.onClick.AddListener(() => action?.Invoke());

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(rect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text = label;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.fontSize = 16f;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            FrameworkTheme.ApplyButton(button, "Panel", text);
        }

        private void DrawSlotsTab(
          PanelContentContext context,
          RectTransform container,
          ref int row
        )
        {
            DrawSlotControlRow(context, container, row++);

            var slotRows = GetKnownSlotRows();
            var cardContainer = CreateSlotToggleContainer(
              context,
              container,
              row++
            );

            for (int i = 0; i < slotRows.Count; i++)
            {
                var slotRow = slotRows[i];
                var enabled = CARAUSettings.IsSlotEnabled(slotRow.Id);

                SlotToggleCardView.Create(
                  cardContainer,
                  slotRow.Name,
                  enabled,
                  () => {
                      CARAUSettings.SetSlotEnabled(slotRow.Id, !enabled);
                      RefreshCurrentSnapshot();
                      RebuildContents();
                  }
                );
            }
        }

        private void DrawSlotControlRow(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex
        )
        {
            var rowObject =
              context.CreateOwned("CARAU_SlotControlRow");
            rowObject.transform.SetParent(parent, false);
            rowObject.transform.SetSiblingIndex(siblingIndex);

            var rowRect = rowObject.AddComponent<RectTransform>();
            rowRect.localScale = Vector3.one;

            var background = rowObject.AddComponent<Image>();
            background.color = new Color(0.15f, 0.10f, 0.29f, 0.96f);
            background.raycastTarget = false;
            FrameworkTheme.ApplyImage(background, "PanelHeader");

            var rowSize = rowObject.AddComponent<LayoutElement>();
            rowSize.minHeight = 48f;
            rowSize.preferredHeight = 48f;
            rowSize.flexibleHeight = 0f;

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 6);
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            CreateSlotControlButton(
              rowRect,
              CARAUSettings.ShowDisabledSlots
                ? "Show Disabled: Yes"
                : "Show Disabled: No",
              210f,
              () => {
                  CARAUSettings.ShowDisabledSlots =
                    !CARAUSettings.ShowDisabledSlots;
                  RefreshCurrentSnapshot();
                  RebuildContents();
              },
              "Show disabled slots on the Main tab."
            );

            CreateSlotControlButton(
              rowRect,
              "Enable All",
              140f,
              () => {
                  CARAUSettings.SelectAllSlots();
                  RefreshCurrentSnapshot();
                  RebuildContents();
              },
              "Enable every available slot type."
            );

            CreateSlotControlButton(
              rowRect,
              "Disable All",
              140f,
              () => {
                  CARAUSettings.DeselectAllSlots(GetKnownSlotIds());
                  RefreshCurrentSnapshot();
                  RebuildContents();
              },
              "Disable every displayed slot type."
            );
        }

        private static void CreateSlotControlButton(
          RectTransform parent,
          string label,
          float preferredWidth,
          System.Action onClick,
          string tooltip
        )
        {
            var buttonObject = new GameObject(
              $"CARAU_SlotControl_{label}"
            );
            buttonObject.transform.SetParent(parent, false);

            var rect = buttonObject.AddComponent<RectTransform>();
            rect.localScale = Vector3.one;

            var size = buttonObject.AddComponent<LayoutElement>();
            size.minWidth = 105f;
            size.preferredWidth = preferredWidth;
            size.flexibleWidth = 0f;

            var image = buttonObject.AddComponent<Image>();
            image.color = new Color(0.35f, 0.22f, 0.65f, 0.98f);

            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick?.Invoke());

            var textObject = new GameObject("Text");
            textObject.transform.SetParent(rect, false);
            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textObject.AddComponent<TMPro.TextMeshProUGUI>();
            text.text = label;
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.fontSize = 15f;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            text.raycastTarget = false;

            FrameworkTheme.ApplyButton(button, "Panel", text);

            UIUtils.AddTooltipDynamic(buttonObject, () => tooltip);
        }

        private RectTransform CreateSlotToggleContainer(
          PanelContentContext context,
          RectTransform parent,
          int siblingIndex
        )
        {
            var containerObject =
              context.CreateOwned("CARAU_SlotToggleFlow");
            containerObject.transform.SetParent(parent, false);
            containerObject.transform.SetSiblingIndex(siblingIndex);

            var rect = containerObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = Vector2.zero;

            var flow =
              containerObject.AddComponent<AutoUtilitiesModFramework.ResponsiveFlowLayout>();
            flow.CellSize = new Vector2(190f, 106f);
            flow.Spacing = new Vector2(8f, 8f);
            flow.padding = new RectOffset(8, 8, 8, 8);

            return rect;
        }

        private void DrawTabs(
          PanelContentContext context,
          RectTransform container,
          ref int row
        )
        {
            var rowObject = context.CreateOwned("CARAU_TabRow");
            rowObject.transform.SetParent(container, false);
            rowObject.transform.SetSiblingIndex(row++);

            var rowRect = rowObject.AddComponent<RectTransform>();
            rowRect.localScale = Vector3.one;

            var background = rowObject.AddComponent<Image>();
            background.color =
              new Color(0.10f, 0.08f, 0.14f, 1.0f);
            background.raycastTarget = false;
            FrameworkTheme.ApplyImage(background, "TabPanelButtons");

            var rowSize = rowObject.AddComponent<LayoutElement>();
            rowSize.minHeight = 52f;
            rowSize.preferredHeight = 52f;
            rowSize.flexibleHeight = 0f;

            var layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 8;
            layout.padding = new RectOffset(8, 8, 10, 10);

            CreateTabButton(rowRect, PanelTab.Main, "Main");
            CreateTabButton(rowRect, PanelTab.Slots, "Slots");
            CreateTabButton(rowRect, PanelTab.Quality, "Quality");
            CreateTabButton(rowRect, PanelTab.Settings, "Settings");
            CreateTabButton(rowRect, PanelTab.Debug, "Debug");
        }

        private List<int> GetKnownSlotIds()
        {
            var ids = new List<int>();
            var slotRows = GetKnownSlotRows();

            for (int i = 0; i < slotRows.Count; i++)
            {
                ids.Add(slotRows[i].Id);
            }

            return ids;
        }

        private List<SlotSettingRow> GetKnownSlotRows()
        {
            var rows = new List<SlotSettingRow>();
            var seen = new HashSet<int>();

            var body = currentBeing?.Data?.Body;

            if (body?.Slots == null)
            {
                return rows;
            }

            for (int i = 0; i < body.Slots.Length; i++)
            {
                var slot = body.Slots[i];

                if (slot?.Type == null)
                {
                    continue;
                }

                if (!seen.Add(slot.Type.IdH))
                {
                    continue;
                }

                rows.Add(new SlotSettingRow
                {
                    Id = slot.Type.IdH,
                    Name = GetSlotSettingDisplayName(slot.NameT)
                });
            }

            rows.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.Ordinal));

            return rows;
        }

        private string GetSlotSettingDisplayName(string slotName)
        {
            if (string.IsNullOrWhiteSpace(slotName))
            {
                return "Unknown";
            }

            if (slotName.EndsWith(" (L)") || slotName.EndsWith(" (R)"))
            {
                var baseName = slotName.Substring(0, slotName.Length - 4);

                switch (baseName)
                {
                    case "Arm":
                        return "Arms";

                    case "Shoulder":
                        return "Shoulders";

                    case "Hand":
                        return "Hands";

                    case "Leg":
                        return "Legs";

                    case "Foot":
                        return "Feet";

                    case "Eye":
                        return "Eyes";

                    case "Ear":
                        return "Ears";

                    case "Lung":
                        return "Lungs";

                    case "Kidney":
                        return "Kidneys";

                    case "Hip":
                        return "Hips";

                    default:
                        return $"{baseName}";
                }
            }

            return slotName;
        }

        private void LogUpgradeDecision(UpgradeDecision decision)
        {
            var debugMessage = "Queue decision was null.";
            if (decision == null)
            {
                CARAUDebugLog.Add(CARAUDebugCategory.Warning, debugMessage);
                return;
            }

            if (!CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Decision
                ))
            {
                return;
            }

            debugMessage = $"Queue attempt | Being={selectedName} | " +
              $"Slot={decision.Slot?.Slot?.NameT ?? "unknown"} | " +
              $"Purpose={decision.Evaluation?.Context?.Purpose.ToString() ?? "legacy"} | " +
              $"Disposition={decision.Evaluation?.Disposition.ToString() ?? "legacy"} | " +
              $"Reason={decision.Evaluation?.PrimaryReason.ToString() ?? "legacy"} | " +
              $"Type={decision.Type} | " +
              $"Installed={decision.InstalledPart?.NameT ?? "null"} " +
              $"T:{decision.InstalledTier} " +
              $"Q:{Units.Percentage(decision.InstalledQuality, clamp: false)} | " +
              $"Target={decision.TargetPart?.NameT ?? "null"} " +
              $"T:{decision.TargetPart?.Tier ?? 0} " +
              $"Q:{Units.Percentage(decision.AvailableQuality, clamp: false)} | " +
              $"ObjId={decision.InventoryPart?.Id ?? 0}";
            
            CARAUDebugLog.Add(CARAUDebugCategory.Decision, debugMessage);
        }

        private void QueueAllSingleBeingUpgrades()
        {
            if (BlockQueueWhilePlanning())
            {
                return;
            }

            var debugMessage = $"QueueAll requested | Being={selectedName}";
            CARAUDebugLog.Add(CARAUDebugCategory.Intent, debugMessage);

            var queued = 0;

            while (QueueNextUpgrade())
            {
                queued++;
            }

            debugMessage = $"QueueAll complete | Being={selectedName} | Queued={queued}";
            CARAUDebugLog.Add(CARAUDebugCategory.Queue,debugMessage);

            RefreshCurrentSnapshot();
            RebuildContents();
        }

        private void QueueCardUpgrade(SlotCardData card)
        {
            if (BlockQueueWhilePlanning())
            {
                return;
            }

            var decision = card?.Decision;

            if (!SurgeryQueue.IsQueueable(S, decision))
            {
                var unavailableMessage =
                  "This operation is no longer queueable. " +
                  "The CARAU display has been refreshed.";

                CARAUDebugLog.Add(
                  CARAUDebugCategory.Warning,
                  $"Card queue rejected after refresh check | " +
                  $"Being={selectedName} | " +
                  $"Slot={card?.SlotName ?? "unknown"}"
                );

                S.Sig.ShowErrorMessage.Send(unavailableMessage);
                RefreshCurrentSnapshot();
                RebuildContents();
                return;
            }

            var debugMessage =
              $"Card queue requested | Being={selectedName} | " +
              $"Slot={card.SlotName}";
            CARAUDebugLog.Add(
              CARAUDebugCategory.Intent,
              debugMessage
            );

            LogUpgradeDecision(decision);

            var queueResult = SurgeryService.TryQueueUpgradeResult(
                S,
                decision
            );
            if (!queueResult.Success)
            {
                var message =
                  queueResult.Message ?? "Failed to queue upgrade.";
                debugMessage =
                  $"Card queue failed | Being={selectedName} | " +
                  $"Slot={card.SlotName} | Reason={message}";

                CARAUDebugLog.Add(
                  CARAUDebugCategory.Error,
                  debugMessage
                );
                S.Sig.ShowErrorMessage.Send(message);
            }

            RefreshCurrentSnapshot();
            RebuildContents();
        }

        private void QueueAllSelectedBeingUpgrades()
        {
            QueueSelectedPlan(
                batchPlanOperations,
                "Batch Queue",
                currentBeingOnly: false
            );
        }

        private void QueueAllSelectedMissing()
        {
            QueueSelectedPlan(
                batchPlanMissingOperations,
                "Queue All Missing",
                currentBeingOnly: false
            );
        }

        private void QueueAllSelectedBadParts()
        {
            QueueSelectedPlan(
                batchPlanBadPartOperations,
                "Queue All Bad Parts",
                currentBeingOnly: false
            );
        }

        private void QueueAllSelectedRemoveAllParts()
        {
            QueueSelectedRemoveAllParts(currentBeingOnly: false);
        }

        private void QueueSelectedRemoveAllParts(bool currentBeingOnly)
        {
            if (BlockQueueWhilePlanning())
            {
                return;
            }

            var orderedBeings = currentBeingOnly
                ? new List<Being>()
                : new List<Being>(selectedBeings);
            if (currentBeingOnly && currentBeing != null)
            {
                orderedBeings.Add(currentBeing);
            }

            var originalBeing = currentBeing;
            var queued = 0;
            var skipped = 0;
            var failed = 0;
            var reasons = new List<string>();

            CARAUDebugLog.Add(
                CARAUDebugCategory.Intent,
                $"Remove All Parts requested | " +
                $"Beings={orderedBeings.Count} | " +
                $"Scope={(currentBeingOnly ? "SelectedBeing" : "Selection")}"
            );

            using (CARAUOperationTrackingSys.BeginBulkQueueTracking())
            {
                for (var index = 0; index < orderedBeings.Count; index++)
                {
                    var being = orderedBeings[index];
                    if (being == null ||
                        !batchPlanRemoveAllParts.TryGetValue(
                            being,
                            out var plannedEligibility
                        ) ||
                        plannedEligibility?.IsTriggered != true)
                    {
                        skipped++;
                        continue;
                    }

                    // Re-evaluate immediately before native registration so a
                    // repaired slot, new operation, or changed body cannot make
                    // an incremental batch plan stale.
                    var result = RemoveAllPartsService.TryQueue(S, being);
                    if (result.Success)
                    {
                        queued++;
                        CARAUDebugLog.Add(
                            CARAUDebugCategory.Queue,
                            $"Remove All Parts registered | " +
                            $"Position={index + 1}/{orderedBeings.Count} | " +
                            $"Being={being.Data?.Name ?? being.ToString()} | " +
                            $"TerminalGroups=" +
                            $"{result.Eligibility.TerminalGroupSummary} | " +
                            $"RemovableParts=" +
                            $"{result.Eligibility.RemovablePartCount}"
                        );
                        continue;
                    }

                    var wasStillTriggered =
                        result.Eligibility?.IsTriggered == true;
                    if (wasStillTriggered)
                    {
                        failed++;
                    }
                    else
                    {
                        skipped++;
                    }

                    if (reasons.Count < 3 &&
                        !string.IsNullOrWhiteSpace(result.Message))
                    {
                        reasons.Add(
                            $"{being.Data?.Name ?? being.ToString()}: " +
                            result.Message
                        );
                    }

                    CARAUDebugLog.Add(
                        wasStillTriggered
                            ? CARAUDebugCategory.Error
                            : CARAUDebugCategory.Warning,
                        $"Remove All Parts not registered | " +
                        $"Position={index + 1}/{orderedBeings.Count} | " +
                        $"Being={being.Data?.Name ?? being.ToString()} | " +
                        $"Reason={result.Message ?? "unknown"}"
                    );
                }
            }

            currentBeing = originalBeing;
            StartBatchPlan();

            if (skipped > 0 || failed > 0)
            {
                S.Sig.ShowErrorMessage.Send(
                    $"CARAU Remove All Parts queued {queued}; skipped " +
                    $"{skipped}; failed {failed}. " +
                    (reasons.Count > 0
                        ? "Reasons: " + string.Join(" | ", reasons)
                        : "See the CARAU Debug log for details.")
                );
            }

            RebuildContents();
        }

        private void QueueSingleBeingAction(
          UpgradeActionKind actionKind
        )
        {
            var operations = actionKind ==
                    UpgradeActionKind.InstallMissing
                ? batchPlanMissingOperations
                : batchPlanBadPartOperations;
            var label = actionKind == UpgradeActionKind.InstallMissing
                ? "Install Missing"
                : "Replace Bad Parts";

            QueueSelectedPlan(
                operations,
                label,
                currentBeingOnly: true
            );
        }

        private void QueueSelectedPlan(
          Dictionary<Being, List<UpgradeDecision>>
            plannedOperationsByBeing,
          string actionLabel,
          bool currentBeingOnly
        )
        {
            if (BlockQueueWhilePlanning())
            {
                return;
            }

            CARAUOperationTrackingSys.SetDisplayedBeingOrder(selectedBeings);
            var orderedBeings = currentBeingOnly
                ? new List<Being>()
                : new List<Being>(selectedBeings);

            if (currentBeingOnly && currentBeing != null)
            {
                orderedBeings.Add(currentBeing);
            }

            if (orderedBeings.Count == 0 && currentBeing != null)
            {
                orderedBeings.Add(currentBeing);
            }

            var originalBeing = currentBeing;
            var plannedOperations = new List<PlannedBatchOperation>();
            var batchQueueStartedAt = PerformanceTimestamp();

            for (var index = 0; index < orderedBeings.Count; index++)
            {
                var being = orderedBeings[index];
                if (being == null ||
                    !plannedOperationsByBeing.TryGetValue(
                        being,
                        out var beingOperations
                    ))
                {
                    continue;
                }

                for (var operationIndex = 0;
                    operationIndex < beingOperations.Count;
                    operationIndex++)
                {
                    plannedOperations.Add(new PlannedBatchOperation
                    {
                        Being = being,
                        Decision = beingOperations[operationIndex]
                    });
                }
            }

            var exactQueued = 0;
            var substituted = 0;
            var skipped = 0;
            var failed = 0;
            var batchChangeReasons = new List<string>();

            if (CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Intent
                ))
            {
                    CARAUDebugLog.Add(
                        CARAUDebugCategory.Intent,
                    $"{actionLabel} requested | Beings={orderedBeings.Count} | " +
                    $"Planned={plannedOperations.Count} | Order=displayed"
                );
            }
            using (InventoryService.BeginBatchLookup(S))
            using (CARAUOperationTrackingSys.BeginBulkQueueTracking())
            {
                for (var index = 0; index < plannedOperations.Count; index++)
                {
                    var plannedOperation = plannedOperations[index];
                    if (plannedOperation?.Being == null ||
                        plannedOperation.Decision == null)
                    {
                        skipped++;
                        continue;
                    }

                    var being = plannedOperation.Being;
                    var beingName =
                        being.Data?.Name ?? being.ToString();
                    if (CARAUSettings.IsDebugCategoryEnabled(
                            CARAUDebugCategory.Intent
                        ))
                    {
                        CARAUDebugLog.Add(
                            CARAUDebugCategory.Intent,
                            $"Batch operation started | Position={index + 1}/" +
                            $"{plannedOperations.Count} | Being={beingName} | " +
                            $"Slot={plannedOperation.Decision.Slot?.Slot?.NameT ?? "unknown"} | " +
                            $"PlannedObjId={plannedOperation.Decision.InventoryPart?.Id ?? 0}"
                        );
                    }
                    ApplyCurrentBeing(being, false);
                    var outcome = QueuePlannedUpgrade(
                        plannedOperation.Decision,
                        out var outcomeReason
                    );

                    switch (outcome)
                    {
                        case BatchOperationOutcome.Exact:
                            exactQueued++;
                            break;

                        case BatchOperationOutcome.Substituted:
                            substituted++;
                            break;

                        case BatchOperationOutcome.Skipped:
                            skipped++;
                            break;

                        default:
                            failed++;
                            break;
                    }

                    if (outcome != BatchOperationOutcome.Exact &&
                        batchChangeReasons.Count < 3)
                    {
                        var conciseReason = FirstLine(outcomeReason);
                        if (!string.IsNullOrWhiteSpace(conciseReason))
                        {
                            batchChangeReasons.Add(conciseReason);
                        }
                    }

                    var outcomeCategory =
                        outcome == BatchOperationOutcome.Failed
                            ? CARAUDebugCategory.Error
                            : outcome == BatchOperationOutcome.Exact
                                ? CARAUDebugCategory.Queue
                                : CARAUDebugCategory.Warning;
                    if (CARAUSettings.IsDebugCategoryEnabled(
                            outcomeCategory
                        ))
                    {
                        CARAUDebugLog.Add(
                            outcomeCategory,
                            $"Batch operation complete | Position={index + 1}/" +
                            $"{plannedOperations.Count} | Being={beingName} | " +
                            $"Slot={plannedOperation.Decision.Slot?.Slot?.NameT ?? "unknown"} | " +
                            $"Outcome={outcome} | Reason={outcomeReason ?? "none"}"
                        );
                    }
                }
            }

            LogBatchQueuePerformanceAnomaly(
                batchQueueStartedAt,
                actionLabel,
                orderedBeings.Count,
                plannedOperations.Count,
                exactQueued,
                substituted,
                skipped,
                failed
            );

            var restoreBeing =
                originalBeing != null &&
                orderedBeings.Contains(originalBeing)
                    ? originalBeing
                    : orderedBeings.Count > 0
                        ? orderedBeings[0]
                        : null;

            currentBeing = restoreBeing;
            StartBatchPlan();

            if (CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Queue
                ))
            {
                    CARAUDebugLog.Add(
                    CARAUDebugCategory.Queue,
                    $"{actionLabel} complete | Beings={orderedBeings.Count} | " +
                    $"Planned={plannedOperations.Count} | " +
                    $"Exact={exactQueued} | Substituted={substituted} | " +
                    $"Skipped={skipped} | Failed={failed}"
                );
            }

            if (substituted > 0 || skipped > 0 || failed > 0)
            {
                S.Sig.ShowErrorMessage.Send(
                    $"CARAU {actionLabel} completed with changes: " +
                    $"{exactQueued} exact, {substituted} substituted, " +
                    $"{skipped} skipped, {failed} failed. " +
                    (batchChangeReasons.Count > 0
                        ? "Reasons: " +
                          string.Join(" | ", batchChangeReasons) + ". "
                        : string.Empty) +
                    "See the CARAU Debug log for full details."
                );
            }

            RebuildContents();
        }

        private bool BlockQueueWhilePlanning()
        {
            if (!batchPlanInProgress)
            {
                return false;
            }

            S.Sig.ShowErrorMessage.Send(
                "CARAU is still analyzing the selected Beings. " +
                "Queue actions will be available when planning completes."
            );
            return true;
        }

        private BatchOperationOutcome QueuePlannedUpgrade(
          UpgradeDecision plannedDecision,
          out string outcomeReason
        )
        {
            outcomeReason = null;
            var slot = plannedDecision?.Slot?.Slot;

            if (slot == null)
            {
                outcomeReason = "Planned slot is no longer available.";
                return BatchOperationOutcome.Skipped;
            }

            var plannedContext = plannedDecision.Evaluation?.Context;
            var currentBeingId = slot.Body?.Owner?.Id ?? 0;
            if (plannedContext != null &&
                (
                    (plannedContext.ExpectedBeingId != 0 &&
                     plannedContext.ExpectedBeingId != currentBeingId) ||
                    (!string.IsNullOrEmpty(
                         plannedContext.ExpectedSlotId
                     ) &&
                     !string.Equals(
                         plannedContext.ExpectedSlotId,
                         slot.Id,
                         System.StringComparison.Ordinal
                     ))
                ))
            {
                UpgradeEvaluator.WithNativeFailure(
                    plannedDecision.Evaluation,
                    UpgradeReasonCode.StaleOwnership,
                    "The planned Being or slot identity changed before " +
                    "Batch Queue confirmation."
                );
                outcomeReason =
                    UpgradeExplanationFormatter.BuildTooltip(
                        plannedDecision.Evaluation
                    );
                return BatchOperationOutcome.Skipped;
            }

            var liveUpgrade = UpgradeAnalyzer.AnalyzeSlot(slot);
            var plannedPartId = plannedDecision.InventoryPart?.Id ?? 0;
            var liveDecision = UpgradeAnalyzer.AnalyzeDecision(
                S,
                liveUpgrade,
                preferredPartId: plannedPartId,
                purpose: UpgradeEvaluationPurpose.Confirmation,
                actionKind: plannedDecision.ActionKind
            );

            if (!SurgeryQueue.IsQueueable(S, liveDecision))
            {
                liveDecision = UpgradeAnalyzer.AnalyzeDecision(
                    S,
                    liveUpgrade,
                    purpose: UpgradeEvaluationPurpose.Confirmation,
                    actionKind: plannedDecision.ActionKind
                );

                if (!SurgeryQueue.IsQueueable(S, liveDecision))
                {
                    outcomeReason =
                        UpgradeExplanationFormatter.BuildTooltip(
                            liveDecision.Evaluation
                        );
                    return BatchOperationOutcome.Skipped;
                }
            }

            var livePartId = liveDecision.InventoryPart?.Id ?? 0;
            var isExact = plannedPartId != 0 &&
                plannedPartId == livePartId;

            LogUpgradeDecision(liveDecision);

            var queueResult = SurgeryService.TryQueueUpgradeResult(
                S,
                liveDecision
            );
            if (queueResult.Success)
            {
                if (!DidQueueExpectedPart(liveDecision, livePartId))
                {
                    outcomeReason =
                        $"Queued operation did not reserve expected part " +
                        $"{livePartId}.";
                    return BatchOperationOutcome.Failed;
                }

                outcomeReason = isExact
                    ? $"Queued planned part {livePartId}."
                    : $"Planned part {plannedPartId} was unavailable; " +
                      $"queued replacement {livePartId}.";
                return isExact
                    ? BatchOperationOutcome.Exact
                    : BatchOperationOutcome.Substituted;
            }

            if (queueResult.FailureCode !=
                    QueueAttemptFailureCode.ReplacementUnavailable &&
                queueResult.Evaluation?.PrimaryReason !=
                    UpgradeReasonCode.PreferredReplacementChanged)
            {
                outcomeReason =
                    queueResult.Message ??
                    "Surgery operation was rejected.";
                return BatchOperationOutcome.Failed;
            }

            var retryDecision = UpgradeAnalyzer.AnalyzeDecision(
                S,
                UpgradeAnalyzer.AnalyzeSlot(slot),
                purpose: UpgradeEvaluationPurpose.Confirmation,
                actionKind: plannedDecision.ActionKind
            );

            if (!SurgeryQueue.IsQueueable(S, retryDecision) ||
                retryDecision.InventoryPart?.Id == livePartId)
            {
                outcomeReason =
                    UpgradeExplanationFormatter.BuildTooltip(
                        retryDecision.Evaluation
                    );
                return BatchOperationOutcome.Skipped;
            }

            LogUpgradeDecision(retryDecision);

            var retryResult = SurgeryService.TryQueueUpgradeResult(
                S,
                retryDecision
            );
            if (!retryResult.Success)
            {
                outcomeReason =
                    retryResult.Message ??
                    "Substitute operation was rejected.";
                return BatchOperationOutcome.Failed;
            }

            if (!DidQueueExpectedPart(
                    retryDecision,
                    retryDecision.InventoryPart.Id
                ))
            {
                outcomeReason =
                    "Substitute operation did not reserve its expected part.";
                return BatchOperationOutcome.Failed;
            }

            outcomeReason =
                $"Planned part {plannedPartId} changed during queueing; " +
                $"queued replacement {retryDecision.InventoryPart.Id}.";
            return BatchOperationOutcome.Substituted;
        }

        private bool DidQueueExpectedPart(
          UpgradeDecision decision,
          uint expectedPartId
        )
        {
            var operation = SurgeryQueue.FindPendingOperation(S, decision);
            return operation?.ReservedPart?.Id == expectedPartId;
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var lineBreak = value.IndexOf('\n');
            return lineBreak < 0
                ? value.Trim()
                : value.Substring(0, lineBreak).Trim();
        }

        private enum BatchOperationOutcome
        {
            Exact,
            Substituted,
            Skipped,
            Failed
        }

        private sealed class PlannedBatchOperation
        {
            public Being Being;
            public UpgradeDecision Decision;
        }

        private bool QueueNextUpgrade()
        {
            var queue = SurgeryQueue.BuildQueue(S, currentSnapshot);
            var debugMessage = $"Next decision | Being={selectedName} | Count={queue.Count}";
            CARAUDebugLog.Add(CARAUDebugCategory.Decision, debugMessage);

            if (queue.Count == 0)
            {
                return false;
            }

            LogUpgradeDecision(queue[0]);

            var queueResult = SurgeryService.TryQueueUpgradeResult(
                S,
                queue[0]
            );
            if (!queueResult.Success)
            {
                var message =
                    queueResult.Message ?? "Failed to queue upgrade.";
                debugMessage = $"Queue failed | Being={selectedName} | Reason={message}";
                
                CARAUDebugLog.Add(CARAUDebugCategory.Error, debugMessage);

                S.Sig.ShowErrorMessage.Send(message);
                return false;
            }

            currentSnapshot = BeingScanner.Scan(S, currentBeing);

            selectedType = currentSnapshot.SelectedType;
            selectedName = currentSnapshot.Name;

            debugLines.Clear();
            debugLines.AddRange(currentSnapshot.DebugLines);

            return true;
        }

        private void RefreshCurrentSnapshot()
        {
            StartBatchPlan();
            if (!batchPlanInProgress)
            {
                ApplyCurrentSnapshotMetadata();
            }
        }

        private void StartBatchPlan()
        {
            CancelBatchPlan("Superseded", true);

            var generation = ++batchPlanGeneration;
            batchPlanNextBeingIndex = 0;
            batchPlanActionPhase = 0;
            batchPlanTotalPlanned = 0;
            batchPlanTotalMissing = 0;
            batchPlanTotalBadParts = 0;
            batchPlanTotalRemoveAllParts = 0;
            batchPlanLastDisplayedProgress = 0;
            batchPlanAllocatedPartIds = new HashSet<uint>();
            batchPlanMissingAllocatedPartIds = new HashSet<uint>();
            batchPlanBadPartAllocatedPartIds = new HashSet<uint>();
            batchPlanInProgress = true;
            batchPlanStartedAt = PerformanceTimestamp();
            batchPlanMaximumFrameMs = 0d;
            batchPlanSnapshots.Clear();
            batchPlanOperations.Clear();
            batchPlanMissingOperations.Clear();
            batchPlanBadPartOperations.Clear();
            batchPlanRemoveAllParts.Clear();

            if (currentBeing == null ||
                !selectedBeings.Contains(currentBeing))
            {
                currentBeing = selectedBeings.Count > 0
                    ? selectedBeings[0]
                    : null;
            }

            currentSnapshot = null;
            selectedType = selectedBeings.Count == 0
                ? "None"
                : "Beings";
            selectedName = selectedBeings.Count == 0
                ? "Nothing selected"
                : $"{selectedBeings.Count} selected";
            debugLines.Clear();

            if (selectedBeings.Count == 0)
            {
                CompleteBatchPlan(generation);
                return;
            }

            ScheduleBatchPlanAdvance(generation);
        }

        private void ScheduleBatchPlanAdvance(int generation)
        {
            Coroutines.OnNextFrame(() => AdvanceBatchPlan(generation));
        }

        private bool IsBatchPlanCurrent(int generation)
        {
            return batchPlanInProgress &&
                generation == batchPlanGeneration &&
                IsOpen;
        }

        private void AdvanceBatchPlan(int generation)
        {
            if (!IsBatchPlanCurrent(generation))
            {
                return;
            }

            var frameStartedAt = PerformanceTimestamp();

            try
            {
                if (batchPlanLookupScope == null)
                {
                    batchPlanLookupScope =
                        InventoryService.BeginBatchLookup(S);

                    if (PerformanceElapsedMilliseconds(frameStartedAt) >=
                        BatchPlanFrameBudgetMs)
                    {
                        RecordBatchPlanFrame(frameStartedAt);
                        ScheduleBatchPlanAdvance(generation);
                        return;
                    }
                }

                do
                {
                    ProcessNextBatchPlanStep();
                }
                while (batchPlanNextBeingIndex < selectedBeings.Count &&
                    PerformanceElapsedMilliseconds(frameStartedAt) <
                        BatchPlanFrameBudgetMs);

                RecordBatchPlanFrame(frameStartedAt);
            }
            catch (System.Exception ex)
            {
                var message =
                    "Incremental batch planning failed | " +
                    $"Generation={generation} | " +
                    $"BeingPosition={batchPlanNextBeingIndex}/" +
                    $"{selectedBeings.Count} | Error={ex}";
                CARAUDebugLog.Add(CARAUDebugCategory.Error, message);
                D.Warn(message);
                CancelBatchPlan("PlanFailed", true);
                selectedType = selectedBeings.Count == 0
                    ? "None"
                    : "Beings";
                selectedName = selectedBeings.Count == 0
                    ? "Nothing selected"
                    : $"{selectedBeings.Count} selected";
                debugLines.Clear();
                debugLines.Add(
                    "CARAU could not complete batch analysis. " +
                    "See the Debug log for details."
                );
                if (IsOpen)
                {
                    RebuildContents();
                }
                return;
            }

            if (!IsBatchPlanCurrent(generation))
            {
                return;
            }

            if (batchPlanNextBeingIndex >= selectedBeings.Count)
            {
                CompleteBatchPlan(generation);
                return;
            }

            if (activeTab == PanelTab.Main &&
                batchPlanNextBeingIndex -
                    batchPlanLastDisplayedProgress >=
                    BatchPlanProgressRefreshInterval)
            {
                batchPlanLastDisplayedProgress =
                    batchPlanNextBeingIndex;
                RebuildContents();
            }

            ScheduleBatchPlanAdvance(generation);
        }

        private void ProcessNextBatchPlanStep()
        {
            var beingIndex = batchPlanNextBeingIndex;

            if (beingIndex < 0 || beingIndex >= selectedBeings.Count)
            {
                return;
            }

            var being = selectedBeings[beingIndex];
            if (being == null)
            {
                batchPlanActionPhase = 0;
                batchPlanNextBeingIndex++;
                return;
            }

            if (batchPlanActionPhase == 0)
            {
                var snapshot = BeingScanner.Scan(
                    S,
                    being,
                    batchPlanAllocatedPartIds
                );
                var operations = new List<UpgradeDecision>();

                for (var cardIndex = 0;
                    cardIndex < snapshot.SlotCards.Count;
                    cardIndex++)
                {
                    var card = snapshot.SlotCards[cardIndex];

                    if (card?.IsWeldedDisplayOnly == true)
                    {
                        continue;
                    }

                    var decision = card?.Decision;
                    if (SurgeryQueue.IsQueueable(S, decision))
                    {
                        operations.Add(decision);
                    }
                }

                batchPlanSnapshots[being] = snapshot;
                batchPlanOperations[being] = operations;
                batchPlanTotalPlanned += operations.Count;
                LogBatchPlanAllocations(
                    beingIndex,
                    snapshot.Name,
                    "Upgrade",
                    operations
                );
                batchPlanActionPhase = 1;
                return;
            }

            if (batchPlanActionPhase == 1)
            {
                var missingOperations = SurgeryQueue.BuildActionQueue(
                    S,
                    being,
                    UpgradeActionKind.InstallMissing,
                    batchPlanMissingAllocatedPartIds
                );
                batchPlanMissingOperations[being] = missingOperations;
                batchPlanTotalMissing += missingOperations.Count;
                LogBatchPlanAllocations(
                    beingIndex,
                    being.Data?.Name ?? being.ToString(),
                    "InstallMissing",
                    missingOperations
                );
                batchPlanActionPhase = 2;
                return;
            }

            if (batchPlanActionPhase == 2)
            {
                var badPartOperations = SurgeryQueue.BuildActionQueue(
                    S,
                    being,
                    UpgradeActionKind.ReplaceBadParts,
                    batchPlanBadPartAllocatedPartIds
                );
                batchPlanBadPartOperations[being] = badPartOperations;
                batchPlanTotalBadParts += badPartOperations.Count;
                LogBatchPlanAllocations(
                    beingIndex,
                    being.Data?.Name ?? being.ToString(),
                    "ReplaceBadParts",
                    badPartOperations
                );
                batchPlanActionPhase = 3;
                return;
            }

            var removeAllPartsEligibility =
                RemoveAllPartsService.Evaluate(S, being);
            batchPlanRemoveAllParts[being] =
                removeAllPartsEligibility;
            if (removeAllPartsEligibility.CanQueue)
            {
                batchPlanTotalRemoveAllParts++;
            }

            if (removeAllPartsEligibility.IsTriggered &&
                CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Decision
                ))
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Decision,
                    $"Remove All Parts evaluation | " +
                    $"BeingPosition={beingIndex + 1}/" +
                    $"{selectedBeings.Count} | " +
                    $"Being={being.Data?.Name ?? being.ToString()} | " +
                    $"TerminalGroups=" +
                    $"{removeAllPartsEligibility.TerminalGroupSummary} | " +
                    $"RemovableParts=" +
                    $"{removeAllPartsEligibility.RemovablePartCount} | " +
                    $"Queueable={removeAllPartsEligibility.CanQueue} | " +
                    $"Reason={removeAllPartsEligibility.Message}"
                );
            }
            batchPlanActionPhase = 0;
            batchPlanNextBeingIndex++;
        }

        private void LogBatchPlanAllocations(
          int beingIndex,
          string beingName,
          string action,
          List<UpgradeDecision> operations
        )
        {
            if (!CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.Decision))
            {
                return;
            }

            for (var operationIndex = 0;
                operationIndex < operations.Count;
                operationIndex++)
            {
                var decision = operations[operationIndex];
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Decision,
                    $"Batch plan allocation | BeingPosition=" +
                    $"{beingIndex + 1}/{selectedBeings.Count} | " +
                    $"Being={beingName} | Action={action} | " +
                    $"Operation={operationIndex + 1}/" +
                    $"{operations.Count} | " +
                    $"Slot={decision.Slot?.Slot?.NameT ?? "unknown"} | " +
                    $"Disposition={decision.Evaluation?.Disposition.ToString() ?? "unknown"} | " +
                    $"Reason={decision.Evaluation?.PrimaryReason.ToString() ?? "unknown"} | " +
                    $"Type={decision.Type} | " +
                    $"Target={decision.TargetPart?.NameT ?? "unknown"} | " +
                    $"ObjId={decision.InventoryPart?.Id ?? 0}"
                );
            }
        }

        private void CompleteBatchPlan(int generation)
        {
            if (!batchPlanInProgress ||
                generation != batchPlanGeneration)
            {
                return;
            }

            batchPlanLookupScope?.Dispose();
            batchPlanLookupScope = null;
            batchPlanInProgress = false;

            var allocatedParts =
                (batchPlanAllocatedPartIds?.Count ?? 0) +
                (batchPlanMissingAllocatedPartIds?.Count ?? 0) +
                (batchPlanBadPartAllocatedPartIds?.Count ?? 0);
            var totalPlannedOperations =
                batchPlanTotalPlanned +
                batchPlanTotalMissing +
                batchPlanTotalBadParts +
                batchPlanTotalRemoveAllParts;
            LogBatchPlanPerformanceAnomaly(
                generation,
                selectedBeings.Count,
                totalPlannedOperations,
                allocatedParts
            );

            if (currentBeing != null &&
                batchPlanSnapshots.TryGetValue(
                    currentBeing,
                    out var plannedSnapshot
                ))
            {
                currentSnapshot = plannedSnapshot;
                ApplyCurrentSnapshotMetadata();
            }
            else
            {
                currentSnapshot = null;
            }

            if (CARAUSettings.IsDebugCategoryEnabled(
                CARAUDebugCategory.Decision))
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.Decision,
                    $"Batch plan rebuilt | Beings={selectedBeings.Count} | " +
                    $"Upgrades={batchPlanTotalPlanned} | " +
                    $"Missing={batchPlanTotalMissing} | " +
                    $"BadParts={batchPlanTotalBadParts} | " +
                    $"RemoveAllParts={batchPlanTotalRemoveAllParts} | " +
                    $"AllocatedParts={allocatedParts}"
                );
            }

            if (IsOpen)
            {
                RebuildContents();
            }
        }

        private void CancelBatchPlan(
          string result,
          bool clearPlanData
        )
        {
            batchPlanGeneration++;
            batchPlanLookupScope?.Dispose();
            batchPlanLookupScope = null;

            batchPlanInProgress = false;
            batchPlanNextBeingIndex = 0;
            batchPlanActionPhase = 0;
            batchPlanTotalPlanned = 0;
            batchPlanTotalMissing = 0;
            batchPlanTotalBadParts = 0;
            batchPlanTotalRemoveAllParts = 0;
            batchPlanLastDisplayedProgress = 0;
            batchPlanStartedAt = 0L;
            batchPlanMaximumFrameMs = 0d;
            batchPlanAllocatedPartIds = null;
            batchPlanMissingAllocatedPartIds = null;
            batchPlanBadPartAllocatedPartIds = null;

            if (clearPlanData)
            {
                batchPlanSnapshots.Clear();
                batchPlanOperations.Clear();
                batchPlanMissingOperations.Clear();
                batchPlanBadPartOperations.Clear();
                batchPlanRemoveAllParts.Clear();
                currentSnapshot = null;
            }
        }

        private void ApplyCurrentSnapshotMetadata()
        {
            if (currentSnapshot == null)
            {
                return;
            }

            selectedType = currentSnapshot.SelectedType;
            selectedName = currentSnapshot.Name;

            debugLines.Clear();
            debugLines.AddRange(currentSnapshot.DebugLines);
        }

        /// <summary>
        /// Invalidates the cached Main-tab snapshot after CARAU's operation
        /// tracker observes a cancellation or completion. This only refreshes
        /// an already-open panel and never changes the player's selection or
        /// opens CARAU on its own.
        /// </summary>
        public void RefreshTrackedOperationState()
        {
            if (!IsOpen || currentBeing == null)
            {
                return;
            }

            RefreshCurrentSnapshot();
            RebuildContents();
        }

        private void RebuildContents()
        {
            frameworkPanel.Refresh();
        }

        private void BuildContents(PanelContentContext context)
        {
            contentGeneration++;
            int row = 0;

            DrawTabs(context, context.Container, ref row);

            switch (activeTab)
            {
                case PanelTab.Main:
                    DrawMainTab(
                        context,
                        context.Container,
                        ref row
                    );
                    break;

                case PanelTab.Slots:
                    DrawSlotsTab(context, context.Container, ref row);
                    break;

                case PanelTab.Quality:
                    DrawQualityTab(context, context.Container, ref row);
                    break;

                case PanelTab.Settings:
                    DrawSettingsTab(context, context.Container, ref row);
                    break;

                case PanelTab.Debug:
                    DrawDebugTab(context, context.Container, ref row);
                    break;
            }
        }

        private static long PerformanceTimestamp()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static double PerformanceElapsedMilliseconds(long startedAt)
        {
            if (startedAt <= 0L)
            {
                return 0d;
            }

            return (PerformanceTimestamp() - startedAt) * 1000d /
                System.Diagnostics.Stopwatch.Frequency;
        }

        private void RecordBatchPlanFrame(long startedAt)
        {
            var elapsed = PerformanceElapsedMilliseconds(startedAt);
            if (elapsed > batchPlanMaximumFrameMs)
            {
                batchPlanMaximumFrameMs = elapsed;
            }
        }

        private void LogBatchPlanPerformanceAnomaly(
          int generation,
          int beings,
          int operations,
          int allocatedParts
        )
        {
            var totalMs = PerformanceElapsedMilliseconds(batchPlanStartedAt);
            if (totalMs < BatchPlanDurationWarningMs &&
                batchPlanMaximumFrameMs < BatchPlanFrameWarningMs)
            {
                return;
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Warning,
                "Batch plan performance guardrail exceeded | " +
                $"Generation={generation} | Duration={totalMs:0.###}ms | " +
                $"DurationLimit={BatchPlanDurationWarningMs:0}ms | " +
                $"MaxFrame={batchPlanMaximumFrameMs:0.###}ms | " +
                $"FrameLimit={BatchPlanFrameWarningMs:0}ms | " +
                $"Beings={beings} | Operations={operations} | " +
                $"AllocatedParts={allocatedParts}"
            );
        }

        private static void LogBatchQueuePerformanceAnomaly(
          long startedAt,
          string actionLabel,
          int beings,
          int planned,
          int exact,
          int substituted,
          int skipped,
          int failed
        )
        {
            var totalMs = PerformanceElapsedMilliseconds(startedAt);
            if (totalMs < BatchQueueDurationWarningMs)
            {
                return;
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Warning,
                $"{actionLabel} performance guardrail exceeded | " +
                $"Duration={totalMs:0.###}ms | " +
                $"Limit={BatchQueueDurationWarningMs:0}ms | " +
                $"Beings={beings} | Planned={planned} | Exact={exact} | " +
                $"Substituted={substituted} | Skipped={skipped} | " +
                $"Failed={failed}"
            );
        }

        private bool IsContentGenerationCurrent(int generation)
        {
            return frameworkPanel.IsOpen &&
                   generation == contentGeneration;
        }

        private void SetQualityThreshold(int value)
        {
            if (value < 0)
            {
                value = 0;
            }

            if (value > 100)
            {
                value = 100;
            }

            CARAUSettings.QualityThresholdPercent = value;

            RefreshCurrentSnapshot();
            RebuildContents();
        }

        private void SetUpgradePolicy(
          bool? tierUpgrades,
          bool? qualityUpgrades
        )
        {
            if (tierUpgrades.HasValue)
            {
                CARAUSettings.AllowTierUpgrades = tierUpgrades.Value;
            }

            if (qualityUpgrades.HasValue)
            {
                CARAUSettings.AllowQualityUpgrades = qualityUpgrades.Value;
            }

            CARAUDebugLog.Add(
                CARAUDebugCategory.Selection,
                "Upgrade policy changed | " +
                $"Tier={CARAUSettings.AllowTierUpgrades} | " +
                $"Quality={CARAUSettings.AllowQualityUpgrades}"
            );

            RefreshCurrentSnapshot();
            RebuildContents();
        }

        private void SetNativeProcedurePriority(int value)
        {
            var priority = Mathf.Clamp(
                value,
                MinimumNativeProcedurePriority,
                MaximumNativeProcedurePriority
            );
            var previous = Mathf.Clamp(
                Game.A.BodyPartProcedurePriority,
                MinimumNativeProcedurePriority,
                MaximumNativeProcedurePriority
            );

            S.Prefs.SetInt(
                NativeProcedurePriorityPreference,
                priority
            );

            if (previous != priority &&
                CARAUSettings.IsDebugCategoryEnabled(
                    CARAUDebugCategory.UI
                ))
            {
                CARAUDebugLog.Add(
                    CARAUDebugCategory.UI,
                    "Native procedure priority changed | " +
                    $"Previous={previous} | Current={priority} | " +
                    "Scope=new CARAU and manual procedures"
                );
            }

            RebuildContents();
        }

        private void SetRetryPolicy(CARAURetryPolicy policy)
        {
            CARAUSettings.RetryPolicy =
                CARAUSettings.IsKnownRetryPolicy(policy)
                    ? policy
                    : CARAURetryPolicy.Manual;
            CARAUOperationTrackingSys.Instance?.OnRetrySettingsChanged();
            RebuildContents();
        }

        private void SetAutomaticRetryLimit(int value)
        {
            CARAUSettings.AutomaticRetryLimit =
                CARAUSettings.ClampAutomaticRetryLimit(value);
            CARAUOperationTrackingSys.Instance?.OnRetrySettingsChanged();
            RebuildContents();
        }

        private void SetAutomaticRetryChanceThreshold(int value)
        {
            CARAUSettings.AutomaticRetryChanceThresholdPercent =
                CARAUSettings.ClampPercentage(value);
            CARAUOperationTrackingSys.Instance?.OnRetrySettingsChanged();
            RebuildContents();
        }

        public void SetSelection(string type, string name)
        {
            CancelBatchPlan("Superseded:NonBeingSelection", true);
            selectedBeings.Clear();
            currentBeing = null;
            currentSnapshot = null;
            selectedType = type;
            selectedName = name;

            debugLines.Clear();
            var debugMessage = $"Panel selection stored: {selectedType} - {selectedName}";

            CARAUDebugLog.Add(CARAUDebugCategory.Selection, debugMessage);

            if (IsOpen)
            {
                RebuildContents();
            }
        }

        public void SetBeingDebugInfo(Being being)
        {
            SetBeings(new List<Being> { being });
        }

        public void SetBeings(IList<Being> beings)
        {
            var previousBeing = currentBeing;
            var incomingBeings = new List<Being>();
            var incomingBeingSet = new HashSet<Being>();

            if (beings != null)
            {
                foreach (var being in beings)
                {
                    if (being != null && incomingBeingSet.Add(being))
                    {
                        incomingBeings.Add(being);
                    }
                }
            }

            var selectionUnchanged =
                selectedBeings.Count == incomingBeingSet.Count;
            if (selectionUnchanged)
            {
                for (var index = 0;
                    index < selectedBeings.Count;
                    index++)
                {
                    if (selectedBeings[index] == null ||
                        !incomingBeingSet.Contains(selectedBeings[index]))
                    {
                        selectionUnchanged = false;
                        break;
                    }
                }
            }

            if (selectionUnchanged)
            {
                CARAUOperationTrackingSys.SetDisplayedBeingOrder(
                    selectedBeings
                );
                if (IsOpen &&
                    !batchPlanInProgress &&
                    selectedBeings.Count > 0 &&
                    batchPlanSnapshots.Count == 0)
                {
                    StartBatchPlan();
                    RebuildContents();
                }
                return;
            }

            selectedBeings.RemoveAll(
                being => being == null || !incomingBeingSet.Contains(being)
            );

            var selectedBeingSet = new HashSet<Being>(selectedBeings);
            foreach (var being in incomingBeings)
            {
                if (selectedBeingSet.Add(being))
                {
                    selectedBeings.Add(being);
                }
            }

            CARAUOperationTrackingSys.SetDisplayedBeingOrder(selectedBeings);

            var activeBeing =
                previousBeing != null &&
                selectedBeings.Contains(previousBeing)
                    ? previousBeing
                    : selectedBeings.Count > 0
                        ? selectedBeings[0]
                        : null;

            if (!IsOpen)
            {
                batchPlanSnapshots.Clear();
                batchPlanOperations.Clear();
                batchPlanMissingOperations.Clear();
                batchPlanBadPartOperations.Clear();
                currentBeing = activeBeing;
                currentSnapshot = null;
                selectedType = selectedBeings.Count == 0
                    ? "None"
                    : "Beings";
                selectedName = selectedBeings.Count == 0
                    ? "Nothing selected"
                    : $"{selectedBeings.Count} selected";
                debugLines.Clear();
                return;
            }

            currentBeing = activeBeing;
            StartBatchPlan();
            RebuildContents();
        }

        private void ApplyCurrentBeing(Being being, bool rebuild)
        {
            currentBeing = being;
            currentSnapshot = being != null &&
                batchPlanSnapshots.TryGetValue(
                    being,
                    out var plannedSnapshot
                )
                    ? plannedSnapshot
                    : being == null
                        ? null
                        : batchPlanInProgress
                            ? null
                            : BeingScanner.Scan(S, being);

            if (currentSnapshot == null)
            {
                selectedType = selectedBeings.Count == 0
                    ? "None"
                    : "Beings";
                selectedName = selectedBeings.Count == 0
                    ? "Nothing selected"
                    : $"{selectedBeings.Count} selected";
                debugLines.Clear();
            }
            else
            {
                ApplyCurrentSnapshotMetadata();
            }

            if (rebuild)
            {
                RebuildContents();
            }
        }

        private void OnShortcutStateChanged()
        {
            if (IsOpen && activeTab == PanelTab.Settings)
            {
                RebuildContents();
            }
        }

        public void Show()
        {
            Close();
            StartBatchPlan();
            frameworkPanel.Show();
        }

    }
}
