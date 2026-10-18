using System;
using System.Linq;
using AutoUtilitiesModFramework;
using Game.Constants;
using Game.Systems.UIUX;
using Game.Utils;
using KL.Grid;
using KL.Pool;
using KL.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReconnectTeleporterConnections;

internal sealed class TeleporterConnectionPanel : IDisposable
{
    private readonly ReconnectTeleporterConnectionsSys system;
    private readonly FrameworkPanel panel;
    private readonly bool themeAwareUi;
    private string activeTab = "map";
    private float recoveryScroll = 1f;
    private float historyScroll = 1f;

    internal TeleporterConnectionPanel(
        ReconnectTeleporterConnectionsSys system)
    {
        this.system = system;
        themeAwareUi = AutoUtilitiesApi.IsFeatureAvailable(
            ReconnectTeleporterConnectionsSys.ConsumerId,
            ReconnectTeleporterConnectionsSys.RequiredUtilitiesVersion);
        panel = new FrameworkPanel(new FrameworkPanelOptions
        {
            Id = "ReconnectTeleporterConnections.Manager",
            Title = "Teleporter Connections",
            MinSize = new Vector2(580f, 420f),
            MaxSize = new Vector2(1400f, 1000f),
            RememberGeometry = true
        }, Build);
    }

    internal void ShowMap()
    {
        activeTab = "map";
        panel.Show();
    }

    internal void Close() => panel.Close();
    internal void Refresh() => panel.Refresh();
    public void Dispose() => panel.Dispose();

    private void Build(PanelContentContext ui)
    {
        ui.AddTabs(new[]
        {
            new PanelTabDefinition { Id = "map", Label = "Map" },
            new PanelTabDefinition { Id = "recovery", Label = "Recovery" },
            new PanelTabDefinition { Id = "history", Label = "History" }
        }, activeTab, value => activeTab = value);

        if (activeTab == "recovery")
        {
            BuildRecovery(ui);
        }
        else if (activeTab == "history")
        {
            BuildHistory(ui);
        }
        else
        {
            BuildMap(ui);
        }
    }

    private void BuildMap(PanelContentContext ui)
    {
        var teleporters = system.ActiveTeleporters();
        var tracked = system.TrackedTeleporters();
        ui.AddInfo(IconId.CTargetSpot, "Constructed Teleporters",
            teleporters.Count.ToString());
        ui.AddInfo(IconId.CInfo, "Directed connections",
            tracked.Count(item => item.DesiredOutboundId != 0).ToString(),
            "Each arrow represents one live or remembered outbound "
            + "destination. Two arrows between a pair represent a two-way "
            + "connection.");
        ui.AddInfo(IconId.CNotConfigured, "Waiting for recovery",
            system.PendingRecoveryCount.ToString(),
            "Destroyed Teleporters that were not deliberately deconstructed "
            + "remain here until rebuilt or explicitly forgotten.");
        ui.AddInfo(IconId.CInfo, "Map color legend",
            "Red: none • Yellow: built • Cyan: rebuilding • Green: two-way",
            "A card and its outbound arrow use the same state color. In a "
            + "two-way pair with one missing Teleporter, the direction toward "
            + "the missing endpoint is cyan and the return direction is green.");
        ui.AddButton(IconId.CCog, "Refresh connection data", null,
            system.ReconcileAll,
            "Recheck labels, positions, and currently applicable recovery records.");

        if (tracked.Count == 0)
        {
            ui.AddInfo(IconId.CInfo, "Connection map",
                "No tracked Teleporters");
            return;
        }

        GameObject graphObject = ui.CreateOwned("RTC_ConnectionGraph");
        RectTransform graphRect = graphObject.AddComponent<RectTransform>();
        var graphSize = graphObject.AddComponent<LayoutElement>();
        graphSize.minHeight = 390f;
        graphSize.preferredHeight = 520f;
        graphSize.flexibleHeight = 1f;
        var background = graphObject.AddComponent<Image>();
        background.color = new Color(0.075f, 0.055f, 0.13f, 0.96f);
        background.raycastTarget = false;

        var graph = graphObject.AddComponent<TeleporterGraphView>();
        graph.Configure(system, tracked, graphRect);

        ui.AddInfo(IconId.CInfo, "Map controls",
            "Select a constructed Teleporter node to focus it",
            "Open the selected Teleporter's Teleporter tab to link, replace, "
            + "or unlink its outbound destination. Rebuilding nodes remain "
            + "visible but cannot be focused until construction completes.");
    }

    private void BuildRecovery(PanelContentContext ui)
    {
        var missing = system.TrackedTeleporters()
            .Where(record => record.IsMissing)
            .ToArray();

        ui.AddInfo(IconId.CNotConfigured, "Waiting for recovery",
            missing.Length.ToString(),
            "A record is kept after damage or other non-player destruction. "
            + "Building a Teleporter at its position recovers it automatically.");

        RecoveryHistoryEntry[] recoveryEvents = system.RecoveryEvents()
            .ToArray();
        if (missing.Length == 0 && recoveryEvents.Length == 0)
        {
            ui.AddInfo(IconId.CInfo, "Status",
                "No Teleporters are waiting and no recovery events are recorded");
            return;
        }

        var scroll = ui.AddVerticalScroll("RTC_RecoveryScroll",
            480f, 780f, 1f, recoveryScroll,
            value => recoveryScroll = value);
        ConfigureDetailScroll(scroll);
        int row = 0;
        if (missing.Length == 0)
        {
            AddScrollInfo(ui, scroll.Content, row++, IconId.CInfo,
                "Current status", "No Teleporters are waiting for recovery");
        }
        foreach (TrackedTeleporter record in missing)
        {
            AddScrollInfo(ui, scroll.Content, row++,
                IconId.CNotConfigured, system.Describe(record),
                "Destroyed — waiting for a replacement");
            AddScrollInfo(ui, scroll.Content, row++, IconId.CTargetSpot,
                "Replacement match", system.DescribeReplacementMatch(record));
            AddScrollInfo(ui, scroll.Content, row++, IconId.CTargetSpot,
                "Remembered outbound",
                record.DesiredOutboundId == 0
                    ? "No outbound connection was recorded"
                    : system.DesiredDestinationDescription(record));

            var incoming = system.IncomingConnectionSources(record.Id);
            if (incoming.Count == 0)
            {
                AddScrollInfo(ui, scroll.Content, row++, IconId.CInfo,
                    "Remembered incoming",
                    "No Teleporter was pointing to this device");
            }
            else
            {
                foreach (TrackedTeleporter source in incoming)
                {
                    AddScrollInfo(ui, scroll.Content, row++,
                        IconId.CTargetSpot, "Waiting incoming from",
                        system.Describe(source));
                }
            }

            RecoveryHistoryEntry[] activity = system
                .RecoveryHistoryFor(record.Id).Take(5).ToArray();
            AddScrollInfo(ui, scroll.Content, row++, IconId.CInfo,
                "Recovery activity", activity.Length == 0
                    ? "No detailed event was recorded for this older entry"
                    : activity.Length + " recent event"
                        + (activity.Length == 1 ? string.Empty : "s"));
            foreach (RecoveryHistoryEntry entry in activity)
                CreateEventCard(scroll.Content, row++, entry, themeAwareUi);

            int recordId = record.Id;
            WButtonIconText2 forget = UISpawn.Widget<WButtonIconText2>(
                ui.Lifetime, UIPrefabId.WContent4Button,
                scroll.Content, row++);
            forget.Load(IconId.CNotConfigured,
                "Forget this recovery record", null,
                () => system.ForgetMissingRecord(recordId));
            UIComponentFactory.SetTip(forget,
                "Stop remembering this destroyed Teleporter and clear every "
                + "saved connection that points to it.");
        }

        AddScrollInfo(ui, scroll.Content, row++, IconId.CInfo,
            "Recent recovery events", recoveryEvents.Length == 0
                ? "No recovery events recorded yet"
                : recoveryEvents.Length + " retained — newest first");
        foreach (RecoveryHistoryEntry entry in recoveryEvents)
            CreateEventCard(scroll.Content, row++, entry, themeAwareUi);
    }

    private void BuildHistory(PanelContentContext ui)
    {
        var entries = system.RecoveryHistory();
        ui.AddInfo(IconId.CInfo, "Recorded events",
            entries.Count + " — newest first",
            "The newest label, connection, relocation, and recovery events "
            + "are retained in the save.");

        if (entries.Count == 0)
        {
            ui.AddInfo(IconId.CInfo, "History", "No events recorded yet");
            return;
        }

        var scroll = ui.AddVerticalScroll("RTC_HistoryScroll",
            480f, 780f, 1f, historyScroll,
            value => historyScroll = value);
        ConfigureDetailScroll(scroll);
        int row = 0;
        foreach (RecoveryHistoryEntry entry in entries)
            CreateEventCard(scroll.Content, row++, entry, themeAwareUi);
    }

    private void ConfigureDetailScroll(
        VerticalScrollViewResult scroll)
    {
        LayoutElement size = scroll.Root.GetComponent<LayoutElement>();
        size.minHeight = 180f;
        size.preferredHeight = 520f;
        size.flexibleHeight = 1f;
    }

    private static WIconText2 AddScrollInfo(PanelContentContext ui,
        RectTransform parent, int row, string icon, string label,
        string value)
    {
        WIconText2 result = UISpawn.Widget<WIconText2>(ui.Lifetime,
            UIPrefabId.WContent2Text2, parent, row);
        result.Load(icon, label, value);
        return result;
    }

    private static void CreateEventCard(RectTransform parent, int row,
        RecoveryHistoryEntry entry, bool applyTheme)
    {
        string message = entry.Message ?? "Event details unavailable";
        int estimatedLines = Math.Max(1,
            (int)Math.Ceiling(message.Length / 70f)
            + message.Count(character => character == '\n'));
        float height = 40f + estimatedLines * 21f;

        var card = new GameObject("RTC_Event_" + entry.Sequence);
        card.transform.SetParent(parent, false);
        card.transform.SetSiblingIndex(row);
        RectTransform cardRect = card.AddComponent<RectTransform>();
        var layout = card.AddComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;
        layout.flexibleHeight = 0f;
        var background = card.AddComponent<Image>();
        background.color = entry.Category == "Recovery"
            ? new Color(0.23f, 0.15f, 0.34f, 0.96f)
            : new Color(0.14f, 0.10f, 0.23f, 0.94f);
        background.raycastTarget = false;

        var headingObject = new GameObject("Heading");
        headingObject.transform.SetParent(cardRect, false);
        RectTransform headingRect =
            headingObject.AddComponent<RectTransform>();
        headingRect.anchorMin = new Vector2(0f, 1f);
        headingRect.anchorMax = new Vector2(1f, 1f);
        headingRect.pivot = new Vector2(0.5f, 1f);
        headingRect.offsetMin = new Vector2(12f, -29f);
        headingRect.offsetMax = new Vector2(-12f, -7f);
        var heading = headingObject.AddComponent<TextMeshProUGUI>();
        heading.text = EventHeading(entry);
        heading.fontSize = 15f;
        heading.fontStyle = FontStyles.Bold;
        heading.textWrappingMode = TextWrappingModes.NoWrap;
        heading.overflowMode = TextOverflowModes.Ellipsis;
        heading.color = new Color(0.82f, 0.72f, 1f, 1f);
        heading.raycastTarget = false;
        heading.richText = false;

        var bodyObject = new GameObject("Details");
        bodyObject.transform.SetParent(cardRect, false);
        RectTransform bodyRect = bodyObject.AddComponent<RectTransform>();
        bodyRect.anchorMin = Vector2.zero;
        bodyRect.anchorMax = Vector2.one;
        bodyRect.offsetMin = new Vector2(12f, 7f);
        bodyRect.offsetMax = new Vector2(-12f, -32f);
        var body = bodyObject.AddComponent<TextMeshProUGUI>();
        body.text = message;
        body.fontSize = 16f;
        body.textWrappingMode = TextWrappingModes.Normal;
        body.overflowMode = TextOverflowModes.Overflow;
        body.alignment = TextAlignmentOptions.TopLeft;
        body.raycastTarget = false;
        body.richText = false;

        if (applyTheme)
            ApplyEventCardTheme(background, heading, body);
    }

    private static void ApplyEventCardTheme(Image background,
        TMP_Text heading, TMP_Text body)
    {
        FrameworkTheme.ApplyImage(background, "PanelContent");
        FrameworkTheme.ApplyText(heading, "Unsized");
        FrameworkTheme.ApplyText(body, "Unsized");
    }

    private static string EventHeading(RecoveryHistoryEntry entry)
    {
        string category = string.IsNullOrWhiteSpace(entry.Category)
            ? "Recorded event"
            : entry.Category;
        if (entry.Tick <= 0) return category;
        return category + " • " + Texts.TicksToDate(entry.Tick)
            + " • " + Units.TicksAgoFromNow(entry.Tick);
    }
}
