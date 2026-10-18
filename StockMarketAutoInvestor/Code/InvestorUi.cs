using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutoUtilitiesModFramework;
using Game;
using Game.Constants;
using Game.Data.Economy;
using Game.Rendering;
using Game.Systems.UIUX;
using Game.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StockMarketAutoInvestor;

internal sealed class InvestorUi : IDisposable
{
    internal const string EnableTooltip = "ON: Uses the daily price history already recorded by the game. There is no new collection period. Existing consecutive days can satisfy confirmation checks; qualifying trades are considered on the next normal daily market update.\n\nOFF: Stops all background investment analysis and automatic trades. The game continues recording its own market history. When enabled again, Auto-Invest reads the latest records and checks the current settings. It does not replay missed trades. Opening this panel still displays market information.";
    private static readonly PanelTabDefinition[] Tabs = {
        new() { Id = "Investment", Label = "Investment" }, new() { Id = "Settings", Label = "Settings" }
    };
    private readonly StockMarketAutoInvestorSys sys;
    private readonly FrameworkPanel panel;
    private readonly FrameworkPanel detail;
    private readonly ITopBarWidgetHandle widget;
    private string tab = "Investment";
    private string hoveredId;
    private string selectedId;
    private string search = "";
    private int filter;
    private int sort;
    private float scrollPosition = 1;
    private string quantity = "1";
    private string inputError;
    private bool observationSettings = true, buySettings = true, sellSettings, budgetSettings, historyExpanded;
    private InvestorChart graph;
    private readonly Dictionary<string, ChartSeries> seriesCache = new(StringComparer.Ordinal);
    private readonly List<(RectTransform Rect, string Id)> assetRows = new();
    private VerticalScrollViewResult assetScroll;

    internal InvestorUi(StockMarketAutoInvestorSys system)
    {
        sys = system;
        detail = new FrameworkPanel(new FrameworkPanelOptions {
            Id = "StockMarketAutoInvestor.Asset", Title = "Investment details",
            MinSize = new Vector2(370, 540), MaxSize = new Vector2(620, 1000), RememberGeometry = true
        }, BuildDetail);
        panel = new FrameworkPanel(new FrameworkPanelOptions {
            Id = "StockMarketAutoInvestor.Main", Title = "Stock Market Auto-Investor",
            MinSize = new Vector2(850, 630), MaxSize = new Vector2(1600, 1050), RememberGeometry = true,
            OnClosed = () => detail.Close()
        }, Build);
        widget = AutoUtilitiesApi.RegisterTopBarWidget(new TopBarWidgetRegistration {
            Id = "StockMarketAutoInvestor.Widget", Icon = () => IconId.WChart,
            Tint = () => sys.Data.Enabled ? Color.white : Color.gray,
            Tooltip = () => "Stock Market Auto-Investor: " + (sys.Data.Enabled ? "ON" : "OFF")
                + "\n" + sys.Data.LastResult + "\nClick for Investment and Settings.",
            OnClick = () => panel.Toggle(), Width = 36, Order = 45
        });
    }

    internal void Refresh() { widget.Refresh(); panel.Refresh(); detail.Refresh(); }
    internal void Close() { detail.Close(); panel.Close(); }
    public void Dispose() { detail.Dispose(); panel.Dispose(); widget.Dispose(); }

    private void Build(PanelContentContext ui)
    {
        graph = null;
        seriesCache.Clear();
        assetRows.Clear();
        assetScroll = null;
        ui.AddTabs(Tabs, tab, value => { tab = value; if (value == "Settings") detail.Close(); });
        var balance = ui.AddInfo(IconId.WWallet, "Wallet", Wallet.ToValueString(sys.State?.Wallet?.Amount ?? 0));
        sys.State?.Wallet?.AmountB.Bind(ui.Lifetime, amount => balance.Load(IconId.WWallet, "Wallet", Wallet.ToValueString(amount)));
        if (tab != "Settings")
            ui.AddInfo(IconId.WChart, "Auto-Invest", sys.Data.Enabled ? "ON" : "OFF", EnableTooltip);
        var watcher = ui.CreateOwned("VisiblePanelWatch").AddComponent<InvestorPanelWatch>();
        watcher.System = sys; watcher.Refresh = Refresh;
        watcher.WatchedAsset = () => detail.IsOpen ? selectedId : hoveredId;
        watcher.Prime();
        ui.Lifetime.OnDispose(() => { watcher.Refresh = null; watcher.ScrollHover = null; watcher.WatchedAsset = null; watcher.RefreshValues = null; });
        if (tab == "Settings") { BuildSettings(ui, watcher); return; }
        if (!sys.MarketUnlocked)
        {
            ui.AddInfo(IconId.CInfo, "Market unavailable", "Wallet and Stock Market research required");
            return;
        }
        BuildInvestment(ui, watcher);
    }

    private void BuildInvestment(PanelContentContext ui, InvestorPanelWatch watcher)
    {
        Input(ui, IconId.CInfo, "Search assets", search, value => {
            search = value.Trim(); scrollPosition = 1; panel.Refresh();
        }, tooltip: "Filter the investment list by asset name. Enter or leave the field to apply.");
        var tools = ui.CreateOwned("ListControls").AddComponent<RectTransform>();
        var toolSize = tools.gameObject.AddComponent<LayoutElement>();
        toolSize.minHeight = toolSize.preferredHeight = 44;
        toolSize.flexibleHeight = 0;
        var toolsLayout = tools.gameObject.AddComponent<HorizontalLayoutGroup>();
        toolsLayout.spacing = 8;
        toolsLayout.childControlWidth = toolsLayout.childControlHeight = true;
        toolsLayout.childForceExpandWidth = toolsLayout.childForceExpandHeight = true;
        var assetsDropdown = UISpawn.Widget<WDropdown>(ui.Lifetime,
            UIPrefabId.WDropdownWithFrame, tools, 0);
        assetsDropdown.Load(new List<string> { "All assets", "Owned", "Stocks", "Commodities", "Managed" }, filter,
            selected => { filter = selected; scrollPosition = 1; panel.Refresh(); });
        ConfigureCell(ui, assetsDropdown, 220, 360, 1);
        UIUtils.AddTip(ui.Lifetime, assetsDropdown, "Choose which assets appear in the investment list.");
        var sortDropdown = UISpawn.Widget<WDropdown>(ui.Lifetime,
            UIPrefabId.WDropdownWithFrame, tools, 1);
        sortDropdown.Load(new List<string> { "By group", "By name", "Price: low first", "Price: high first" }, sort,
            selected => { sort = selected; scrollPosition = 1; panel.Refresh(); });
        ConfigureCell(ui, sortDropdown, 220, 360, 1);
        UIUtils.AddTip(ui.Lifetime, sortDropdown, "Choose how the investment list is ordered.");

        var body = ui.CreateOwned("InvestmentBody").AddComponent<RectTransform>();
        var bodySize = body.gameObject.AddComponent<LayoutElement>();
        bodySize.minHeight = 440; bodySize.preferredHeight = 540; bodySize.flexibleHeight = 1;
        var layout = body.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandHeight = true; layout.childForceExpandWidth = false; layout.spacing = 12;
        graph = InvestorChart.Create(body);
        // Register the custom scroll with AUMF, then place it in the two-column body.
        assetScroll = ui.AddVerticalScroll("MarketAssets", 330, 410, 1, scrollPosition, v => scrollPosition = v);
        assetScroll.Root.SetParent(body, false);
        assetScroll.Content.GetComponent<VerticalLayoutGroup>().spacing = 2;
        assetScroll.Scroll.onValueChanged.AddListener(_ => watcher.ScrollDirty = true);
        watcher.ScrollHover = HoverAfterScroll;
        ui.Lifetime.OnDispose(() => assetRows.Clear());

        int row = 0;
        var index = UISpawn.Widget<WButtonIconText2>(ui.Lifetime, UIPrefabId.WContent2Button, assetScroll.Content, row++);
        index.Load(IconId.CStockMarket, "Global market index", "", () => Hover(null));
        UIUtils.AddHover(ui.Lifetime, index, on => { if (on) Hover(null); });
        assetRows.Add((index.Rect, null));
        IEnumerable<MarketItemModifier> assets = sys.State.Market.Modifiers.Values.Where(sys.Eligible);
        if (search.Length > 0) assets = assets.Where(m => m.Item.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        if (filter == 1) assets = assets.Where(m => sys.Owned(m.IdH) > 0);
        if (filter == 2) assets = assets.Where(m => m.Item.AssetType == MarketItemType.Equity);
        if (filter == 3) assets = assets.Where(m => m.Item.AssetType == MarketItemType.CFD);
        if (filter == 4) assets = assets.Where(m => sys.Data.Books.TryGetValue(m.Id, out var b) && b.ManagedQuantity > 0);
        assets = sort switch {
            1 => assets.OrderBy(m => m.Item.Name),
            2 => assets.OrderBy(m => m.Price),
            3 => assets.OrderByDescending(m => m.Price),
            _ => assets.OrderBy(m => m.Item.FirstGroup?.NameT).ThenBy(m => m.Item.Name)
        };
        string group = null;
        int shown = 0;
        foreach (MarketItemModifier mod in assets)
        {
            string groupName = mod.Item.FirstGroup?.NameT ?? "Other";
            if (sort == 0 && group != groupName)
            {
                group = groupName;
                var header = UISpawn.Widget<WText>(ui.Lifetime, UIPrefabId.WContent2Header, assetScroll.Content, row++);
                header.Load(group);
            }
            string id = mod.Id;
            string name = mod.Item.Name;
            sys.Holdings(mod.IdH, out long owned, out double cost, out _);
            if (owned > 0) name = "<color=#FFD966>" + name + "</color>";
            var item = UISpawn.Widget<WButtonIconText2WithLabel>(ui.Lifetime,
                UIPrefabId.WContent2ButtonText3, assetScroll.Content, row++);
            item.Button.Load(mod.Item.Icon, name, Wallet.ToValueString(mod.Price), () => Select(id));
            string potentialReturn = PotentialReturnPercent(owned, cost, mod.Price);
            item.Label.Load((sys.InsightsUnlocked ? mod.PriceChange(1) + "   " : "") + potentialReturn);
            // Reserve room for both percentages; restore pooled label sizing on disposal.
            float resultWidth = Mathf.Max(sys.InsightsUnlocked ? 146 : 80,
                item.Label.Text.GetPreferredValues().x + 12);
            ConfigureCell(ui, item.Label, resultWidth, resultWidth, 0);
            var tip = UIUtils.AddTipFunc(ui.Lifetime, item,
                () => "Click for asset details and manual Buy/Sell.\n"
                    + "The last percentage is potential profit/loss on the shares you own, compared with their original purchase cost. "
                    + "For example, $1,000 of shares now worth $1,100 shows +10.0%. "
                    + "It includes all remaining purchase lots. Green is a gain; red is a loss. "
                    + "A dash means no shares owned or no valid purchase cost or current price.\n" + AssetStatus(id));
            tip.HoverFun = on => { if (on) Hover(id); };
            assetRows.Add((item.Rect, id));
            shown++;
        }
        if (shown == 0)
        {
            var empty = UISpawn.Widget<WText>(ui.Lifetime, UIPrefabId.WContentTextBlock, assetScroll.Content, row++);
            empty.Load(search.Length > 0
                ? "No assets match the search \"" + search + "\". Clear the search field to show assets."
                : "No assets match this filter.");
        }
        ui.AddInfo(IconId.CInfo, "Last result", sys.Data.LastResult, sys.Data.LastResult);
        ui.AddCollapsibleHeader("Trade history (" + sys.Data.Trades.Count + ")", historyExpanded, value => historyExpanded = value);
        if (historyExpanded)
        {
            foreach (TradeRecord record in sys.Data.Trades.AsEnumerable().Reverse().Take(25))
            {
                string name = sys.Find(record.Asset)?.Item.Name ?? record.Asset;
                string saleResult = "";
                if (record.RealizedProfit.HasValue)
                {
                    double result = record.RealizedProfit.Value;
                    string amount = Wallet.ToValueString(Math.Abs(result), false);
                    saleResult = result > 0 ? "\n<color=#66CC66>Profit: " + amount + "</color>"
                        : result < 0 ? "\n<color=#FF6666>Loss: " + amount + "</color>"
                        : "\nBreak-even: " + amount;
                }
                ui.AddInfo(record.Buy ? IconId.CBuy : IconId.CSell,
                    $"Day {record.Day}: {name}", $"{(record.Automatic ? "Auto" : "Manual")} {(record.Buy ? "buy" : "sell")} {record.Quantity:N0}",
                    Wallet.ToValueString(record.Price, false) + " per share. " + record.Reason
                    + (record.CostBasis.HasValue ? "\nOriginal purchase cost: " + Wallet.ToValueString(record.CostBasis.Value, false) : "")
                    + saleResult);
            }
        }
        if (hoveredId != null && !sys.Eligible(sys.Find(hoveredId))) hoveredId = null;
        Hover(hoveredId);
    }

    internal static string PotentialReturnPercent(long owned, double cost, double price)
    {
        if (owned <= 0 || !Numbers.Price(cost) || !Numbers.Price(price)) return "—";
        double percent = (owned * price - cost) / cost * 100;
        if (!Numbers.Finite(percent)) return "—";
        string text = (percent > 0 ? "+" : percent < 0 ? "-" : "")
            + Math.Abs(percent).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        if (percent > 0) return "<color=#66CC66>" + text + "</color>";
        if (percent < 0) return "<color=#FF6666>" + text + "</color>";
        return text;
    }

    private string AssetStatus(string id)
    {
        if (!sys.Data.Enabled) return "Auto-Invest off. Background analysis and automated trades are stopped.";
        if (!sys.Data.Books.TryGetValue(id, out AssetBook book)) return "Waiting for the next market update.";
        return book.Status;
    }

    private string PurchaseTooltip()
    {
        int confirmations = sys.Data.Settings.ConfirmationDays;
        return "A low price alone does not trigger a purchase.\n\n"
            + $"With Confirmation updates set to {confirmations}, each of {confirmations} consecutive daily updates must be higher than the previous day's price "
            + "and still at or below that day's cyan buy ceiling. A flat or falling price, or a rise above the ceiling, restarts the count.\n\n"
            + "The ceiling is calculated from Core's lowest and highest recorded prices in the analysis window. It can move when new prices arrive or old ones leave. "
            + "The cyan line shows today's ceiling across that window, not the ceiling used on each earlier day.\n\n"
            + "Existing consecutive days count, including those before enabling Auto-Invest. This looks for a recovery; it cannot guarantee the lowest price. "
            + "Price-range, holdings, research, and spending rules must also allow the purchase. "
            + "Your purchase method decides how available cash is shared among qualifying stocks.";
    }

    private void HoverAfterScroll()
    {
        if (assetScroll == null || graph == null) return;
        Vector2 pointer = The.Bindings.Mouse.ScreenPosition;
        var first = The.Bindings.Mouse.FirstUnderCursor;
        if (first == null || !first.transform.IsChildOf(assetScroll.Root)) return;
        if (!RectTransformUtility.RectangleContainsScreenPoint(assetScroll.Scroll.viewport, pointer, null)) return;
        foreach (var row in assetRows)
        {
            if (row.Rect != null && RectTransformUtility.RectangleContainsScreenPoint(row.Rect, pointer, null))
            { Hover(row.Id); return; }
        }
    }

    private void Hover(string id)
    {
        if (graph == null) return;
        hoveredId = id;
        string key = id ?? "__GLOBAL__";
        if (!seriesCache.TryGetValue(key, out ChartSeries series))
            seriesCache[key] = series = BuildSeries(id);
        graph.Show(series);
    }

    private ChartSeries BuildSeries(string id)
    {
        MarketItemModifier mod = id == null ? null : sys.Find(id);
        var series = new ChartSeries { Title = mod == null ? "Global market index" : mod.Item.Name + "  " + Wallet.ToValueString(mod.Price, false), Money = mod != null };
        Dictionary<int, float> history = mod == null ? sys.State.Market.History : mod.History;
        int end = sys.State.Market.Day;
        var points = history?.Where(p => p.Key >= end - 180 && p.Key <= end).OrderBy(p => p.Key).ToList();
        if (points == null || points.Count < 2) { series.Caption = "Not enough market history to display a graph."; return series; }
        float multiplier = mod?.Item.Value ?? 1;
        for (int i = 1; i < points.Count; i++)
        {
            float a = points[i - 1].Value * multiplier, b = points[i].Value * multiplier;
            if (!Numbers.Price(a) || !Numbers.Price(b)) continue;
            series.Nodes.Add(Line(points[i - 1].Key, a, points[i].Key, b, b >= a ? Color.green : Color.red));
        }
        series.Caption = "Market history · up to 180 market days\nGreen: rise   Red: fall";
        if (mod == null) return series;
        sys.Holdings(mod.IdH, out long owned, out _, out double average);
        if (owned > 0) series.Nodes.Add(Line(points[0].Key, average, end, average, Color.yellow, true));
        sys.Data.Books.TryGetValue(id, out AssetBook book);
        if (sys.Data.Enabled)
        {
            HistoryRange range = StockMarketAutoInvestorSys.History(mod).Range(end, sys.Data.Settings.WindowDays);
            series.Caption += $"\nCore history used: {range.Count} / {sys.Data.Settings.WindowDays} days";
            if (range.Count >= 3)
            {
                double buy = range.BuyLevel(sys.Data.Settings.BuyRangePercent);
                series.Nodes.Add(Line(range.StartDay, buy, end, buy, Color.cyan, true));
                if (book?.ManagedQuantity > 0)
                {
                    double sell = book.ManagedCost * (1 + sys.Data.Settings.ProfitPercent / 100);
                    series.Nodes.Add(Line(range.StartDay, sell, end, sell, new Color(1, .6f, .1f), true));
                }
                series.Caption += "\nCyan: buy ceiling   Orange: profit target";
            }
        }
        else series.Caption += "\nAuto-Invest off";
        if (owned > 0) series.Caption += "\nYellow: average purchase price";
        foreach (TradeRecord trade in sys.Data.Trades)
        {
            if (trade.Asset != id || trade.Day < points[0].Key || trade.Day > end) continue;
            series.Nodes.Add(Line(trade.Day, trade.Price * .99, trade.Day, trade.Price * 1.01,
                trade.Buy ? Color.cyan : new Color(1, .6f, .1f), true));
        }
        return series;
    }

    private static RTChartNode Line(float x1, double y1, float x2, double y2, Color color, bool skip = false) => new() {
        Type = RTChartNode.NodeType.Line, From = new Vector2(x1, (float)y1), To = new Vector2(x2, (float)y2),
        Color = color, Thickness = skip ? 2 : 3, SkipTooltip = skip
    };

    private void Select(string id)
    {
        selectedId = id;
        quantity = "1";
        Hover(id);
        if (detail.IsOpen) detail.Refresh(); else { detail.Show(); PositionDetail(); }
    }

    private void PositionDetail()
    {
        if (panel.Rect == null || detail.Rect == null) return;
        var main = new Vector3[4]; var side = new Vector3[4];
        panel.Rect.GetWorldCorners(main); detail.Rect.GetWorldCorners(side);
        detail.Rect.position += new Vector3(main[2].x - side[1].x + 8, main[2].y - side[1].y, 0);
        if (detail.Rect.parent is RectTransform parent)
        {
            detail.Rect.GetWorldCorners(side);
            Vector3 min = parent.InverseTransformPoint(side[0]), max = parent.InverseTransformPoint(side[2]);
            float x = max.x > parent.rect.xMax ? parent.rect.xMax - max.x : min.x < parent.rect.xMin ? parent.rect.xMin - min.x : 0;
            float y = max.y > parent.rect.yMax ? parent.rect.yMax - max.y : min.y < parent.rect.yMin ? parent.rect.yMin - min.y : 0;
            detail.Rect.position += parent.TransformVector(new Vector3(x, y, 0));
        }
    }

    private void BuildDetail(PanelContentContext ui)
    {
        MarketItemModifier mod = sys.Find(selectedId);
        if (!sys.Eligible(mod)) { ui.AddHeader("Asset unavailable"); return; }
        // Callbacks capture the clicked asset, never the independently hovered graph.
        string id = selectedId;
        ui.AddHeader(mod.Item.Name);
        ui.AddInfo(mod.Item.Icon, "Current price", Wallet.ToValueString(mod.Price, false));
        if (sys.PredictionUnlocked)
        {
            ui.AddHeader("Trends");
            AddTrend(ui, "Short trend", mod.Trend);
            AddTrend(ui, "Global long trend", sys.State.Market.TrendLong);
            AddTrend(ui, "Global short trend", sys.State.Market.TrendShort);
            foreach (int group in mod.Item.GroupsH ?? Array.Empty<int>())
                if (sys.State.Market.GroupTrends.TryGetValue(group, out MarketTrend trend))
                    AddTrend(ui, MarketGroup.Find(group)?.NameT ?? "Group trend", trend);
        }
        else ui.AddInfo(IconId.CInfo, "Trends", "Market Prediction research required");
        ui.AddHeader("Details");
        if (mod.Item.IsBlackMarket) ui.AddInfo(IconId.CInfo, "Market", "Black Market");
        ui.AddInfo(IconId.CInfo, "Asset class", mod.Item.AssetClassT, mod.Item.AssetClassDescT);
        if (sys.InsightsUnlocked)
        {
            ui.AddInfo(IconId.CInfo, "Volatility", mod.Item.Volatility.ToString("0.0000"));
            ui.AddInfo(IconId.CSell, "High", Wallet.ToValueString(mod.PriceHigh));
            ui.AddInfo(IconId.CBuy, "Low", Wallet.ToValueString(mod.PriceLow));
            ui.AddInfo(IconId.CInfo, "Day", mod.PriceChange(1));
            ui.AddInfo(IconId.CInfo, "Week", mod.PriceChange(7));
            ui.AddInfo(IconId.CInfo, "Month", mod.PriceChange(30));
            ui.AddInfo(IconId.CInfo, "All time", mod.PriceChange(-1));
        }
        else ui.AddInfo(IconId.CInfo, "Price details", "Market Insights research required");
        sys.Holdings(mod.IdH, out long owned, out double cost, out double avg);
        ui.AddHeader("Owned");
        ui.AddInfo(IconId.WWallet, "Your shares", owned.ToString("N0"));
        ui.AddInfo(IconId.WWallet, "Purchase cost", Wallet.ToValueString(cost), "What was originally paid for the shares still owned. Core stores this for each purchase.");
        ui.AddInfo(IconId.WWallet, "Average price", Wallet.ToValueString(avg));
        ui.AddInfo(IconId.WWallet, "Current value", Wallet.ToValueString(owned * (double)mod.Price), "Shares owned multiplied by the current market price.");
        double potentialResult = owned * (double)mod.Price - cost;
        string potentialValue = Wallet.ToValueString(potentialResult);
        if (potentialResult > 0) potentialValue = "<color=#66CC66>" + potentialValue + "</color>";
        else if (potentialResult < 0) potentialValue = "<color=#FF6666>" + potentialValue + "</color>";
        ui.AddInfo(IconId.WWallet, "Potential profit/loss", potentialValue,
            "Current value minus original purchase cost. This gain or loss becomes final only when the shares are sold.");
        ui.AddInfo(IconId.CInfo, "Auto-Invest", AssetStatus(id),
            owned == 0 ? AssetStatus(id) + "\n\n" + PurchaseTooltip() : AssetStatus(id));
        if (sys.Data.Books.TryGetValue(id, out AssetBook book) && (book.ManualConflict || book.ManagedQuantity > 0))
            ui.AddButton(IconId.CInfo, "Leave holdings manual", "Reset asset management", () => sys.ReleaseManagement(id),
                "Remove this asset from automated management without selling shares. Existing shares remain manual. Once no shares remain, Core history can qualify a later automated purchase.");
        ui.AddHeader("Manual trade");
        if (sys.TradingUnlocked)
        {
            Input(ui, IconId.WWallet, "Quantity", quantity, value => quantity = value,
                TMP_InputField.ContentType.IntegerNumber, "Whole shares, from 1 to 2,147,483,647. This panel allows trades above one million shares. Core can store at most 2,147,483,647 shares of one stock, including shares already owned. Purchases still require enough cash; sales require enough shares. Manual trades do not use the cash reserve or daily confirmation checks.");
            ui.AddButton(IconId.CBuy, "Buy", mod.Item.Name, () => SubmitManual(id, true));
            ui.AddButton(IconId.CSell, "Sell", mod.Item.Name, () => SubmitManual(id, false));
            ui.AddInfo(IconId.CInfo, "Manual activity", "Pauses automation for this asset",
                "A manual trade pauses this asset's automated management. Other assets continue normally.");
        }
        else ui.AddInfo(IconId.CInfo, "Buy / Sell", "Stock Trading research required");
        ui.AddInfo(IconId.CInfo, "Last result", sys.Data.LastResult, sys.Data.LastResult);
    }

    private static void AddTrend(PanelContentContext ui, string name, MarketTrend trend)
    {
        if (trend != null) ui.AddInfo(IconId.CInfo, name, trend.NameT,
            $"Day {trend.CurrentDays} of {trend.TotalDays} in this trend.");
    }

    private void SubmitManual(string id, bool buy)
    {
        if (!int.TryParse(quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount < 1)
        { sys.Data.LastResult = "Enter a whole quantity from 1 to 2,147,483,647."; detail.Refresh(); return; }
        sys.ManualTrade(id, amount, buy);
    }

    private void BuildSettings(PanelContentContext ui, InvestorPanelWatch watcher)
    {
        InvestorSettings s = sys.Data.Settings;
        ui.AddToggle(IconId.WChart, "Auto-Invest", sys.Data.Enabled ? "ON" : "OFF", sys.Data.Enabled, sys.SetEnabled, EnableTooltip);
        ui.AddToggle(IconId.CInfo, "Trade fanfare", s.ShowTradeFanfare ? "ON" : "OFF", s.ShowTradeFanfare,
            sys.SetTradeFanfare,
            "Show the game's on-screen banner after each completed automatic or manual trade made through this mod. "
            + "Buys show quantity and total spent. Sales show quantity, price per share, and profit or loss in Starcredits. "
            + "Turning this off only hides future trade announcements. Core controls their sound, stacking and display time; hover the banner to read the active messages.");
        ui.AddInfo(IconId.CInfo, "Price history", "Uses the game's recorded daily prices", EnableTooltip);
        if (!string.IsNullOrEmpty(inputError)) ui.AddInfo(IconId.CInfo, "Invalid setting", inputError);
        ui.AddCollapsibleHeader("History and confirmation", observationSettings, v => observationSettings = v);
        if (observationSettings)
        {
            Number(ui, "Analysis window (market days)", s.WindowDays, 3, 180, true, v => s.WindowDays = (int)v,
                "How many recent daily prices from the game's history are used to find the low and high. "
                + "Existing records are available immediately; there is no new collection period. "
                + "Uses the consecutive available days ending today, up to this limit, with at least 3 valid prices required. "
                + "Shortening the window can move the cyan buy ceiling. Confirmation checks use the window ending on each checked day.");
            Number(ui, "Confirmation updates", s.ConfirmationDays, 1, 10, true, v => s.ConfirmationDays = (int)v,
                "How many consecutive recorded days, ending on the current market day, must meet a trading rule. "
                + "For buying, each price must rise from the previous day and stay at or below that day's cyan buy ceiling. "
                + "For selling, each day must meet a sale rule after purchase and the trade cooldown. "
                + "Existing Core records count, including days before enabling. Settings changes are checked against history on the next daily update.");
            Number(ui, "Trade cooldown (market days)", s.CooldownDays, 1, 180, true, v => s.CooldownDays = (int)v,
                "Minimum time between automated trades in the same asset.");
        }
        ui.AddCollapsibleHeader("Purchase conditions", buySettings, v => buySettings = v);
        if (buySettings)
        {
            Number(ui, "Buy range ceiling (%)", s.BuyRangePercent, 1, 49, false, v => s.BuyRangePercent = v,
                "Sets the cyan line between the lowest (0%) and highest (100%) Core prices in the analysis window. "
                + "Example: with a low of $80 and a high of $100, 25% puts the ceiling at $85.\n\n" + PurchaseTooltip());
            Number(ui, "Minimum observed range (%)", s.MinimumRangePercent, .1, 100, false, v => s.MinimumRangePercent = v,
                "Required difference between the observed high and low, as a percentage of the low. Avoids buying on nearly flat prices.");
            ui.AddInfo(IconId.CInfo, "Purchase rule", "One automated position per asset",
                "The mod buys only when no shares of that asset are owned. Existing manual holdings are excluded.\n\n" + PurchaseTooltip());
        }
        ui.AddCollapsibleHeader("Sale conditions", sellSettings, v => sellSettings = v);
        if (sellSettings)
        {
            Number(ui, "Minimum profit (%)", s.ProfitPercent, .1, 1000, false, v => s.ProfitPercent = v,
                "Required gain over the automated purchase price. A confirmed decline from the observed peak is also required.");
            Number(ui, "Reversal from peak (%)", s.ReversalPercent, .1, 99, false, v => s.ReversalPercent = v,
                "Required fall from the highest price recorded by the game since Auto-Invest bought the shares, including days while Auto-Invest was off. Price must also decline on each confirmation day and remain above the profit target.");
            ui.AddToggle(IconId.CSell, "Sell to limit losses", s.StopLossEnabled ? "ON" : "OFF", s.StopLossEnabled,
                v => { s.StopLossEnabled = v; sys.SettingsChanged(); },
                "ON: Allows Auto-Invest to sell shares it bought for less than it paid, to try to avoid a larger loss.\n\n"
                + "Example: bought at $100 with a 15% price drop setting, a sale can qualify at $85 or less.\n\n"
                + "OFF: Keeps the shares until the profit-sale rules are met or you sell them manually.\n\n"
                + "Auto-Invest must also be ON. Daily confirmation checks and the waiting time between trades still apply. Existing Core history counts. "
                + "The price can fall further before a sale, so 15% is not a guaranteed maximum loss.");
            if (s.StopLossEnabled)
            {
                Number(ui, "Price drop before selling (%)", s.StopLossPercent, .1, 99, false, v => s.StopLossPercent = v,
                    "How far the price must fall below what Auto-Invest paid before a sale at a loss can qualify. "
                    + "Example: 15% means a share bought at $100 can qualify for sale at $85 or less. "
                    + "Only used when Sell to limit losses and Auto-Invest are ON. The required checks and waiting periods can delay the sale and allow a larger loss.");
            }
        }
        ui.AddCollapsibleHeader("Spending and execution limits", budgetSettings, v => budgetSettings = v);
        if (budgetSettings)
        {
            Number(ui, "Cash to reserve (%)", s.CashReservePercent, 0, 100, false, v => s.CashReservePercent = v,
                "The percentage of your wallet cash plus the current value of all owned shares that Auto-Invest must keep as cash. "
                + "Share value is original purchase cost plus its current gain or loss. Manual holdings count too; ship and inventory value do not.\n\n"
                + "Example: $8,000 cash plus $2,000 in shares totals $10,000. Reserving 30% keeps $3,000 in cash and leaves $5,000 available to invest. "
                + "The amount beside this field updates as you type; press Enter or leave the field to apply. "
                + "Automatic purchases may exceed one million shares while preserving this reserve. Core's storage limit is 2,147,483,647 shares per stock. "
                + "If the reserve exceeds your cash, new purchases wait. This setting does not force sales or restrict manual trades.",
                percent => {
                    double reserve = sys.PortfolioTotals().Reserve(percent);
                    return Numbers.Finite(reserve) ? Wallet.ToValueString(reserve) : "Value unavailable";
                }, watcher);
            Number(ui, "Maximum share price", s.MaximumSharePrice, 0, 1e15, false, v => s.MaximumSharePrice = v,
                "The highest price Auto-Invest may pay for one share. With a limit of $1,000, a share priced at $1,800 is excluded; "
                + "a share at $800 can qualify. This does not limit the number of shares bought. Zero prevents new purchases.");
            PurchaseMethodDropdown(ui, s);
            Number(ui, "Maximum trades per market update", s.MaximumTradesPerDay, 1, 20, true, v => s.MaximumTradesPerDay = (int)v,
                "The maximum number of DIFFERENT stocks Auto-Invest may buy on one daily market update. "
                + "For example, 3 allows purchases in up to 3 different stocks that day, with many shares of each if cash allows. "
                + "The purchase method may use all available money on fewer stocks. Sales are handled separately and do not use these purchase slots.");
        }
    }

    private void PurchaseMethodDropdown(PanelContentContext ui, InvestorSettings settings)
    {
        const string tip = "Divide Evenly: split available cash equally among the selected qualifying stocks, prioritizing observed percentage upside. "
            + "Whole-share rounding can leave cash unspent.\n\n"
            + "Purchase Most Profitable: buy the qualifying stock with the greatest percentage gain back to its highest recorded price in the analysis window first. "
            + "This is an estimate of opportunity, not a guaranteed profit.\n\n"
            + "Purchase By Quantity: buy the cheapest qualifying shares first to maximize the share count.\n\n"
            + "The last two methods spend as much as allowed on their first choice, then move to the next if money remains, "
            + "including after reaching Core's storage limit of 2,147,483,647 shares per stock. There is no one-million-share order limit in this panel. Choices are reconsidered each market day. "
            + "Every method obeys the cash reserve, maximum share price, daily stock count, and normal purchase conditions.";
        var row = ui.CreateOwned("PurchaseMethod").AddComponent<RectTransform>();
        var size = row.gameObject.AddComponent<LayoutElement>();
        size.minHeight = size.preferredHeight = 40;
        size.flexibleHeight = 0;
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 8;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        var label = ui.AddInfo(IconId.CInfo, "Purchase method", string.Empty, tip);
        label.Rect.SetParent(row, false);
        ConfigureCell(ui, label, 230, 420, 1);
        var dropdown = UISpawn.Widget<WDropdown>(ui.Lifetime, UIPrefabId.WDropdownWithFrame, row, 1);
        dropdown.Load(new List<string> { "Divide Evenly", "Purchase Most Profitable", "Purchase By Quantity" },
            (int)settings.PurchaseMethod, value => { settings.PurchaseMethod = (PurchaseMethod)value; sys.SettingsChanged(); });
        ConfigureCell(ui, dropdown, 280, 340, 0);
        UIUtils.AddTip(ui.Lifetime, dropdown, tip);
    }

    private void Number(PanelContentContext ui, string label, double value, double min, double max,
        bool integer, Action<double> set, string tip, Func<double, string> preview = null, InvestorPanelWatch watcher = null)
    {
        var row = ui.CreateOwned("Setting_" + label).AddComponent<RectTransform>();
        var size = row.gameObject.AddComponent<LayoutElement>();
        size.minHeight = size.preferredHeight = 40;
        size.flexibleHeight = 0;
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 8;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = true;

        var name = ui.AddInfo(IconId.CInfo, label, string.Empty, tip);
        name.Rect.SetParent(row, false);
        ConfigureCell(ui, name, 280, 500, 1);
        var input = Input(ui, IconId.CInfo, label, value.ToString("0.##", CultureInfo.InvariantCulture), text =>
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                || !Numbers.Finite(parsed) || parsed < min || parsed > max || (integer && parsed != Math.Floor(parsed)))
            {
                inputError = $"{label}: enter {(integer ? "a whole number" : "a number")} from {min:0.##} to {max:0.##}.";
                panel.Refresh(); return;
            }
            inputError = null; set(parsed); sys.SettingsChanged();
        }, integer ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.DecimalNumber, tip, row);
        ConfigureCell(ui, input, 200, 240, 0);
        bool iconWasActive = input.Icon.gameObject.activeSelf;
        input.Icon.gameObject.SetActive(false);
        if (preview != null)
        {
            void RefreshPreview()
            {
                if (ui.Lifetime.IsDisposed || input == null || input.Input == null) return;
                bool valid = double.TryParse(input.Input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double typed)
                    && Numbers.Finite(typed) && typed >= min && typed <= max;
                name.SetText2(valid ? preview(typed) : "Enter 0–100%");
            }
            input.Input.SetOnChange(_ => RefreshPreview());
            if (watcher != null) watcher.RefreshValues = RefreshPreview;
            sys.State?.Wallet?.AmountB.Bind(ui.Lifetime, _ => RefreshPreview(), notifyImmediately: false);
            RefreshPreview();
            ui.Lifetime.OnDispose(() => {
                if (input != null && input.Input != null) input.Input.SetOnChange(null);
                if (watcher != null) watcher.RefreshValues = null;
            });
        }
        ui.Lifetime.OnDispose(() => {
            if (input != null && input.Icon != null) input.Icon.gameObject.SetActive(iconWasActive);
        });
    }

    private static WInputWithIcon Input(PanelContentContext ui, string icon, string placeholder, string value,
        Action<string> committed, TMP_InputField.ContentType contentType = TMP_InputField.ContentType.Standard,
        string tooltip = null, RectTransform parent = null)
    {
        // The installed AUMF maintenance build owns commit cleanup and pooled reuse.
        var input = ui.AddInput(icon, placeholder, value, committed, contentType, tooltip);
        if (parent != null) input.Rect.SetParent(parent, false);
        return input;
    }

    private static void ConfigureCell(PanelContentContext ui, Component cell,
        float minWidth, float preferredWidth, float flexibleWidth)
    {
        var sizing = cell.GetComponent<LayoutElement>();
        bool added = sizing == null;
        if (added) sizing = cell.gameObject.AddComponent<LayoutElement>();
        float oldMinWidth = sizing.minWidth, oldPreferredWidth = sizing.preferredWidth,
            oldFlexibleWidth = sizing.flexibleWidth, oldMinHeight = sizing.minHeight,
            oldPreferredHeight = sizing.preferredHeight, oldFlexibleHeight = sizing.flexibleHeight;
        sizing.minWidth = minWidth;
        sizing.preferredWidth = preferredWidth;
        sizing.flexibleWidth = flexibleWidth;
        sizing.minHeight = sizing.preferredHeight = 36;
        sizing.flexibleHeight = 0;
        // Native controls are pooled. Do not leave these mod-specific dimensions on them.
        ui.Lifetime.OnDispose(() => {
            if (sizing == null) return;
            if (added) { UnityEngine.Object.DestroyImmediate(sizing); return; }
            sizing.minWidth = oldMinWidth;
            sizing.preferredWidth = oldPreferredWidth;
            sizing.flexibleWidth = oldFlexibleWidth;
            sizing.minHeight = oldMinHeight;
            sizing.preferredHeight = oldPreferredHeight;
            sizing.flexibleHeight = oldFlexibleHeight;
        });
    }
}
