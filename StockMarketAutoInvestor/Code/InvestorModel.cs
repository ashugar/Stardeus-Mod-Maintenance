using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace StockMarketAutoInvestor;

// These types contain no game or UI dependencies. The same policy runs in the test harness.
public sealed class InvestorSettings
{
    public int WindowDays = 60;
    public int ConfirmationDays = 2;
    public double BuyRangePercent = 25;
    public double MinimumRangePercent = 2;
    public double ProfitPercent = 10;
    public double ReversalPercent = 3;
    public bool StopLossEnabled;
    public double StopLossPercent = 15;
    public double CashReservePercent = 90;
    public double MaximumSharePrice = 2000;
    public PurchaseMethod PurchaseMethod;
    public int CooldownDays = 3;
    public int MaximumTradesPerDay = 4;
    public bool ShowTradeFanfare = true;

    public void Normalize()
    {
        WindowDays = Clamp(WindowDays, 3, 180);
        ConfirmationDays = Clamp(ConfirmationDays, 1, 10);
        BuyRangePercent = Bound(BuyRangePercent, 1, 49, 25);
        MinimumRangePercent = Bound(MinimumRangePercent, 0.1, 100, 2);
        ProfitPercent = Bound(ProfitPercent, 0.1, 1000, 10);
        ReversalPercent = Bound(ReversalPercent, 0.1, 99, 3);
        StopLossPercent = Bound(StopLossPercent, 0.1, 99, 15);
        CashReservePercent = Bound(CashReservePercent, 0, 100, 100);
        MaximumSharePrice = Bound(MaximumSharePrice, 0, 1e15, 0);
        if (!Enum.IsDefined(typeof(PurchaseMethod), PurchaseMethod)) PurchaseMethod = PurchaseMethod.DivideEvenly;
        CooldownDays = Clamp(CooldownDays, 1, 180);
        MaximumTradesPerDay = Clamp(MaximumTradesPerDay, 1, 20);
    }
    private static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
    private static double Bound(double v, double min, double max, double fallback) =>
        Numbers.Finite(v) ? Math.Max(min, Math.Min(max, v)) : fallback;
}

public static class Numbers
{
    public static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    public static bool Price(double v) => Finite(v) && v > 0;
    public static double NativePrice(double v) => (double)(float)v;
}

public sealed class AssetBook
{
    [JsonIgnore] public int BuyConfirmations;
    [JsonIgnore] public int SellConfirmations;
    [JsonIgnore] public int DecisionDay = int.MinValue;
    [JsonIgnore] public InvestmentAction ApprovedAction;
    public int LastTradeDay = -1000000;
    // One automated purchase lot per asset. Existing/manual lots are never adopted.
    public int ManagedQuantity;
    public double ManagedCost;
    public long ManagedTick;
    public bool ManualConflict;
    [JsonIgnore] public string Status = "Waiting for the next market update";

    public void ResetAnalysis()
    {
        BuyConfirmations = SellConfirmations = 0;
        DecisionDay = int.MinValue;
        ApprovedAction = InvestmentAction.Wait;
        Status = "Waiting for the next market update";
    }
}

public sealed class TradeRecord
{
    public string Asset;
    public int Day;
    public int Quantity;
    public double Price;
    public bool Buy;
    public bool Automatic;
    public string Reason;
    public double? CostBasis;
    public double? RealizedProfit;
}

public sealed class InvestorState
{
    public int Schema = 3;
    public bool Enabled;
    public InvestorSettings Settings = new();
    public Dictionary<string, AssetBook> Books = new(StringComparer.Ordinal);
    public List<TradeRecord> Trades = new();
    public int LastDay = -1;
    public string LastResult = "Auto-Invest is off.";

    public void SetEnabled(bool enabled, int day)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        ResetAnalysis(day);
        LastResult = enabled ? "Waiting for the next market update." : "Auto-Invest is off.";
    }

    public void ResetAnalysis(int day)
    {
        foreach (AssetBook book in Books.Values) book.ResetAnalysis();
        LastDay = day;
    }

    public AssetBook Book(string id)
    {
        if (!Books.TryGetValue(id, out AssetBook book)) Books[id] = book = new AssetBook();
        return book;
    }

    public bool BeginMarketDay(int day)
    {
        if (!Enabled || day == LastDay) return false;
        // Never replay trades for skipped days, or trade on a rewound notification.
        if (day < LastDay) { ResetAnalysis(day); return false; }
        LastDay = day;
        return true;
    }

    public void AddTrade(TradeRecord trade)
    {
        Trades.Add(trade);
        if (Trades.Count > 200) Trades.RemoveAt(0);
    }
}

public enum InvestmentAction { Wait, Buy, Sell }

public sealed class InvestmentDecision
{
    public InvestmentAction Action;
    public string Reason;
    public double BuyLevel;
    public double SellLevel;
    public double Score;
}

public static class InvestmentPolicy
{
    public static InvestmentDecision Evaluate(AssetBook b, InvestorSettings s, MarketHistory history, int day,
        double price, long owned, bool tradingUnlocked, HistoryAnalysis analysis = null)
    {
        var d = new InvestmentDecision { Reason = "Waiting" };
        b.BuyConfirmations = b.SellConfirmations = 0;
        price = Numbers.NativePrice(price);
        if (!Numbers.Price(price)) return Finish(b, d, "Current market price is invalid; automation paused", true);
        if (!history.TryPrice(day, out _))
            return Finish(b, d, $"No valid price recorded for market day {day}", true);
        if (!history.MatchesCurrentQuote(day, price))
            return Finish(b, d, "Current quote differs from today's recorded price; automation paused", true);
        analysis ??= new HistoryAnalysis();
        analysis.Prepare(history, day, s.WindowDays, s.ConfirmationDays);
        HistoryRange range = analysis.At(0);
        d.BuyLevel = range.BuyLevel(s.BuyRangePercent);
        d.SellLevel = b.ManagedQuantity > 0 ? b.ManagedCost * (1 + s.ProfitPercent / 100) : 0;
        if (range.Count < 3)
            return Finish(b, d, $"Not enough consecutive Core history: {range.Count} / 3 days", true);
        if (!tradingUnlocked) return Finish(b, d, "Stock Trading research required", true);
        if (b.ManualConflict) return Finish(b, d, "Manual activity: automation paused for this asset", true);
        if (b.ManagedQuantity == 0 && owned > 0) return Finish(b, d, "Manual holdings excluded", true);
        if ((long)day - b.LastTradeDay < s.CooldownDays) return Finish(b, d, "Trade cooldown", true);
        history.TryPrice(day - 1, out double previous);
        if (b.ManagedQuantity > 0)
        {
            if (owned != b.ManagedQuantity) return Finish(b, d, "Holdings changed; automation paused", true);
            // Reconstruct each peak from records at or before that day, after purchase.
            // This includes price changes while automation was off, without a second ledger.
            int first = Math.Max(day - s.ConfirmationDays + 1, b.LastTradeDay + 1);
            double peak = history.Peak(b.LastTradeDay, first - 1, b.ManagedCost);
            bool loss = false;
            for (int checkDay = first; checkDay <= day; checkDay++)
            {
                if (!history.TryPrice(checkDay, out double current)) { b.SellConfirmations = 0; continue; }
                peak = Math.Max(peak, current);
                bool valid = (long)checkDay - b.LastTradeDay >= s.CooldownDays
                    && analysis.At(day - checkDay).Count >= 3
                    && history.TryPrice(checkDay - 1, out previous);
                loss = s.StopLossEnabled && current <= b.ManagedCost * (1 - s.StopLossPercent / 100);
                bool reversal = current >= d.SellLevel && current < previous
                    && current <= peak * (1 - s.ReversalPercent / 100);
                b.SellConfirmations = valid && (loss || reversal) ? b.SellConfirmations + 1 : 0;
            }
            if (b.SellConfirmations >= s.ConfirmationDays)
            {
                d.Action = InvestmentAction.Sell;
                return Finish(b, d, loss ? "Confirmed loss limit" : "Confirmed profitable reversal", false);
            }
            return Finish(b, d, b.SellConfirmations > 0
                ? $"Confirming sale: {b.SellConfirmations} / {s.ConfirmationDays}"
                : "Holding; sale conditions not met", false);
        }
        bool wideEnough = range.Percent >= s.MinimumRangePercent;
        // Check the trailing streak against each day's own window. Today's high/low
        // must not be applied retrospectively to older confirmation days.
        for (int offset = 0; offset < s.ConfirmationDays; offset++)
        {
            int checkDay = day - offset;
            if ((long)checkDay - b.LastTradeDay < s.CooldownDays) break;
            HistoryRange past = analysis.At(offset);
            if (past.Count < 3 || past.Percent < s.MinimumRangePercent
                || !history.TryPrice(checkDay, out double current)
                || !history.TryPrice(checkDay - 1, out double prior)
                || current <= prior || current > past.BuyLevel(s.BuyRangePercent)) break;
            b.BuyConfirmations++;
        }
        // Comparable return per Starcredit invested, using the selected Core window.
        // The observed high is a ranking reference, not a predicted sale price.
        d.Score = Math.Max(0, (range.High - price) / price);
        if (b.BuyConfirmations >= s.ConfirmationDays)
        {
            d.Action = InvestmentAction.Buy;
            return Finish(b, d, "Confirmed recovery in the lower price range", false);
        }
        return Finish(b, d, b.BuyConfirmations > 0
            ? $"Confirming price rise: {b.BuyConfirmations} / {s.ConfirmationDays}"
            : !wideEnough ? "Price range below minimum"
            : price > d.BuyLevel ? "Price above buy ceiling"
            : price < previous ? "Price falling; waiting for a rise"
            : "Price unchanged; waiting for a rise", false);
    }

    private static InvestmentDecision Finish(AssetBook b, InvestmentDecision d, string reason, bool clear)
    {
        if (clear) b.BuyConfirmations = b.SellConfirmations = 0;
        b.Status = d.Reason = reason;
        return d;
    }

    public static int BuyQuantity(InvestorSettings s, PortfolioValue portfolio, float price)
    {
        if (!Numbers.Price(price) || price > s.MaximumSharePrice) return 0;
        int low = 0, high = PurchasePlan.Quantity(portfolio.Spendable(s.CashReservePercent), price);
        while (low < high)
        {
            int quantity = low + (int)(((long)high - low + 1) / 2);
            double charge = CoreTradeLimits.ChargeCeiling(quantity, price);
            double cashAfter = portfolio.Cash - charge;
            double totalAfter = cashAfter + (portfolio.MarketValue + quantity * (double)price);
            if (cashAfter >= totalAfter * (s.CashReservePercent / 100)) low = quantity;
            else high = quantity - 1;
        }
        return low;
    }
}

public static class NativeSalePlan
{
    // Core's Sell does not subtract previously sold shares when crossing purchase lots.
    // Limit each call to the latest remaining lot, and inspect holdings after each call.
    public static int NextBatch(int remaining, int latestLot) =>
        Math.Max(0, Math.Min(remaining, Math.Max(0, latestLot)));
}
