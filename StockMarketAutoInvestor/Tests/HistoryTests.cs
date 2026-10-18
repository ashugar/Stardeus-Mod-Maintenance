using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using StockMarketAutoInvestor;

internal static class HistoryTests
{
    private static Dictionary<int, float> Prices(params float[] prices) =>
        prices.Select((price, i) => (price, i)).ToDictionary(p => p.i + 1, p => p.price);
    private static InvestorSettings Settings() => new() {
        WindowDays = 10, ConfirmationDays = 2, BuyRangePercent = 30, MinimumRangePercent = 2, CooldownDays = 1
    };
    private static InvestmentDecision Evaluate(Dictionary<int, float> prices, AssetBook book,
        InvestorSettings settings, int day, long owned = 0, bool unlocked = true)
    {
        prices.TryGetValue(day, out float price);
        return InvestmentPolicy.Evaluate(book, settings, new MarketHistory(prices, 1), day, price, owned, unlocked);
    }

    internal static void Run(Action<bool, string> check)
    {
        var state = new InvestorState();
        check(!state.Enabled && !state.BeginMarketDay(1) && state.LastDay == -1,
            "New/off sessions reject market updates without processing state");
        state.SetEnabled(true, 10);
        check(!state.BeginMarketDay(10) && state.BeginMarketDay(11) && !state.BeginMarketDay(11),
            "Enabling waits for the next market update; duplicate notifications are rejected");
        var held = state.Book("asset");
        held.ManagedQuantity = 10; held.ManagedCost = 100; held.ManagedTick = 42; held.LastTradeDay = 5;
        held.BuyConfirmations = 2; held.DecisionDay = 11; held.ApprovedAction = InvestmentAction.Buy;
        state.SetEnabled(false, 11);
        check(held.ManagedQuantity == 10 && held.ManagedTick == 42 && held.LastTradeDay == 5,
            "Off preserves exact managed holdings and trade timing");
        check(held.BuyConfirmations == 0 && held.DecisionDay == int.MinValue && held.ApprovedAction == InvestmentAction.Wait,
            "Off invalidates all cached trade approvals");
        check(!state.BeginMarketDay(30), "Off performs no catch-up processing");
        state.SetEnabled(true, 30);
        check(!state.BeginMarketDay(30) && state.BeginMarketDay(35),
            "Re-enable or a day gap considers only the next received daily update");
        check(!state.BeginMarketDay(20) && !state.BeginMarketDay(20) && state.BeginMarketDay(21),
            "Rewound and repeated days cannot place a duplicate trade");

        var s = Settings(); var b = new AssetBook();
        var prices = Prices(120, 110, 100, 90, 91, 92);
        var d = Evaluate(prices, b, s, 6);
        check(d.Action == InvestmentAction.Buy && b.BuyConfirmations == 2 && d.BuyLevel == 99,
            "An untouched book qualifies immediately from two existing historical rises");
        d = Evaluate(prices, b, s, 6);
        check(d.Action == InvestmentAction.Buy && b.BuyConfirmations == 2,
            "Repeated evaluation recomputes the same streak without accumulating confirmations");
        b.ResetAnalysis();
        check(Evaluate(prices, b, s, 6).Action == InvestmentAction.Buy && prices.Count == 6,
            "Re-enable can reuse current Core history without clearing or copying its records");
        prices[7] = 91;
        check(Evaluate(prices, b, s, 7).Reason.Contains("falling") && b.BuyConfirmations == 0,
            "A renewed decline cancels a recovery despite the earlier streak");
        prices[8] = 91;
        check(Evaluate(prices, b, s, 8).Reason.Contains("unchanged"), "A flat day cannot confirm a purchase");
        prices[9] = 92;
        check(Evaluate(prices, b, s, 9).Reason == "Confirming price rise: 1 / 2",
            "Only the consecutive trailing rise counts after a flat day");
        prices[10] = 110;
        check(Evaluate(prices, b, s, 10).Reason == "Price above buy ceiling", "Rises above the ceiling do not qualify");
        prices = Prices(120, 110, 100, 90, 91, 99);
        check(Evaluate(prices, b, s, 6).Action == InvestmentAction.Buy, "A price exactly at the buy ceiling qualifies");
        check(Evaluate(prices, b, s, 6, 1).Reason == "Manual holdings excluded", "Manual holdings remain excluded");
        check(Evaluate(prices, b, s, 6, 0, false).Reason.Contains("research"), "Core trading research remains required");
        b.ManualConflict = true;
        check(Evaluate(prices, b, s, 6).Action == InvestmentAction.Wait, "Manual intervention pauses the asset");
        b.ManualConflict = false; b.LastTradeDay = 5; s.CooldownDays = 3;
        check(Evaluate(prices, b, s, 6).Reason == "Trade cooldown", "Trade cooldown is preserved");
        b.LastTradeDay = -1000000; s = Settings(); s.WindowDays = 4;
        prices = Prices(100, 10, 200, 90, 91, 92);
        check(Evaluate(prices, b, s, 6).Action == InvestmentAction.Wait && b.BuyConfirmations == 1,
            "Prior confirmations use their own day-specific ceiling instead of today's higher ceiling");
        s = Settings(); s.MinimumRangePercent = 2;
        check(Evaluate(Prices(100, 100.1f, 100, 100.1f, 100.2f), b, s, 5).Reason == "Price range below minimum",
            "Insufficient price movement has a distinct waiting reason");

        prices = Prices(120, 110, 100, 90, 91, 92);
        var history = new MarketHistory(prices, 1);
        check(history.Range(6, 3).Count == 3 && history.Range(6, 3).StartDay == 4 && history.Range(6, 3).High == 92,
            "The analysis window bounds reads to the selected days");
        prices[6] = 93;
        check(history.TryPrice(6, out double changed) && changed == 93, "The history view reads Core dictionary changes directly");
        prices[999] = 1000000;
        check(history.Range(6, 10).High == 120, "Future-dated records never influence the current range");
        check(InvestmentPolicy.Evaluate(b, s, history, 6, 92, 0, true).Reason.Contains("differs"),
            "A live quote inconsistent with the current history cannot trade");
        prices.Remove(6);
        check(Evaluate(prices, b, s, 6).Action == InvestmentAction.Wait, "Missing current-day history cannot trade");
        prices[6] = 92; prices.Remove(5);
        check(history.Range(6, 10).Count == 1 && Evaluate(prices, b, s, 6).Reason.Contains("consecutive"),
            "A missing recent day breaks continuity instead of bridging the gap");
        prices[5] = float.NaN;
        check(history.Range(6, 10).Count == 1, "Invalid historical prices also break continuity");
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            check(!new MarketHistory(Prices(invalid), 1).TryPrice(1, out _), "Invalid Core price rejected: " + invalid);
        check(!new MarketHistory(Prices(float.MaxValue), float.MaxValue).TryPrice(1, out _), "Overflowed historical multiplication is rejected");
        check(new MarketHistory(null, 1).Range(10, 60).Count == 0, "Absent Core history is handled without inventing prices");
        var warm = Prices(120, 110, 100, 90, 91, 92).ToDictionary(p => p.Key - 6, p => p.Value);
        check(Evaluate(warm, b, s, 0).Action == InvestmentAction.Buy, "Core's prewarmed negative market days are usable history");
        check(Evaluate(Prices(90, 91), b, s, 2).Action == InvestmentAction.Wait, "At least three consecutive valid prices are required");

        s = Settings(); s.WindowDays = 3;
        held = new AssetBook { ManagedQuantity = 10, ManagedCost = 100, LastTradeDay = 1 };
        prices = Prices(100, 150, 130, 125, 124, 120, 119);
        check(Evaluate(prices, held, s, 7, 10).Action == InvestmentAction.Sell && held.SellConfirmations == 2,
            "Profit sales recover the peak since purchase even outside the analysis window");
        held.ResetAnalysis();
        check(Evaluate(prices, held, s, 7, 10).Action == InvestmentAction.Sell,
            "A qualifying sale can use recorded days while Auto-Invest was off");
        check(Evaluate(prices, held, s, 7, 11).Action == InvestmentAction.Wait, "Changed managed holdings block sales");
        held.LastTradeDay = 2;
        prices = Prices(500, 100, 110, 116, 115, 114);
        check(Evaluate(prices, held, s, 6, 10).Action == InvestmentAction.Wait && held.SellConfirmations == 0,
            "A high before purchase cannot create a false profitable reversal");
        prices[50] = 1000;
        check(new MarketHistory(prices, 1).Peak(2, 5, 100) == 116, "Peak reconstruction excludes future records");
        prices = Prices(100, 95, 90, 85, 80, 79); held.LastTradeDay = 1;
        check(Evaluate(prices, held, s, 6, 10).Action == InvestmentAction.Wait, "Loss sales remain disabled by default");
        s.StopLossEnabled = true;
        check(Evaluate(prices, held, s, 6, 10).Action == InvestmentAction.Sell,
            "Existing confirmed loss-limit days can qualify after enabling the option");
        held.LastTradeDay = 5;
        check(Evaluate(prices, held, s, 6, 10).Action == InvestmentAction.Wait && held.SellConfirmations == 1,
            "Days before or on the purchase day cannot satisfy sale confirmations");

        var legacy = JsonConvert.DeserializeObject<InvestorState>("{\"Schema\":2,\"Enabled\":true,\"Settings\":{\"MinimumObservations\":30,\"WindowDays\":30,\"CashReservePercent\":40},\"Books\":{\"asset\":{\"Samples\":[{\"Day\":12,\"Price\":1}],\"Observations\":999,\"Peak\":999,\"BuyConfirmations\":9,\"ManagedQuantity\":2,\"ManagedCost\":90,\"ManagedTick\":123,\"LastTradeDay\":11}}}");
        check(legacy.Enabled && legacy.Settings.WindowDays == 30 && legacy.Settings.CashReservePercent == 40
            && legacy.Book("asset").ManagedQuantity == 2 && legacy.Book("asset").ManagedTick == 123,
            "Legacy JSON preserves settings and exact managed lots");
        string json = JsonConvert.SerializeObject(legacy);
        check(!new[] { "Samples", "Observations", "MinimumObservations", "Peak", "BuyConfirmations", "DecisionDay", "ApprovedAction" }.Any(json.Contains),
            "New JSON omits obsolete price collection and transient decision data");
        check(legacy.Book("asset").BuyConfirmations == 0 && legacy.Book("asset").DecisionDay == int.MinValue,
            "Legacy confirmation fields cannot revive a cached trade approval");
        s.WindowDays = 1; s.Normalize();
        check(s.WindowDays == 3, "Analysis window has an independent three-day lower limit");
        s.WindowDays = 1000; s.Normalize();
        check(s.WindowDays == 180, "Analysis window remains bounded at 180 days");
        QuotePrecisionTests(check);
        ReferenceComparison(check);
    }

    private static void QuotePrecisionTests(Action<bool, string> check)
    {
        float value = 2200, multiplier = .93f;
        double extended = (double)value * multiplier;
        double stored = (double)(float)extended;
        check(extended != stored && Numbers.NativePrice(extended) == stored,
            "Extra-precision arithmetic normalizes to Core's stored float price");
        var entries = new Dictionary<int, float> { [1] = 1.2f, [2] = 1.1f, [3] = 1, [4] = .9f, [5] = .91f, [6] = multiplier };
        var h = new MarketHistory(entries, value, multiplier);
        h.TryPrice(6, out double historical);
        check(historical == stored && h.MatchesCurrentQuote(6, extended),
            "Matching stored Core multipliers accept a valid quote despite extra arithmetic precision");
        var book = new AssetBook(); var s = Settings();
        check(InvestmentPolicy.Evaluate(book, s, h, 6, extended, 0, true).Action == InvestmentAction.Buy,
            "Precision alone cannot block a confirmed historical purchase");
        var changed = new MarketHistory(entries, value, .94f);
        check(InvestmentPolicy.Evaluate(book, s, changed, 6, value * .94f, 0, true).Reason.Contains("differs"),
            "A genuinely different current Core multiplier remains blocked with an accurate explanation");
        entries.Remove(6);
        check(InvestmentPolicy.Evaluate(book, s, h, 6, stored, 0, true).Reason == "No valid price recorded for market day 6",
            "A missing daily record identifies its market day");
        check(InvestmentPolicy.Evaluate(book, s, h, 6, double.NaN, 0, true).Reason.Contains("Current market price is invalid"),
            "An invalid live quote has a separate status");
        var random = new Random(1421);
        for (int i = 0; i < 100000; i++)
        {
            value = (float)(1 + random.NextDouble() * 5000);
            multiplier = (float)(.5 + random.NextDouble() * 2);
            entries[6] = multiplier;
            h = new MarketHistory(entries, value, multiplier);
            extended = (double)value * multiplier;
            if (!h.TryPrice(6, out historical) || historical != (double)(float)extended || !h.MatchesCurrentQuote(6, extended))
                throw new Exception("Float quote precision mismatch: " + i);
        }
        check(true, "100,000 varied Core quotes preserve float price parity and multiplier freshness");
    }

    private static void ReferenceComparison(Action<bool, string> check)
    {
        var rng = new Random(91226);
        var scratch = new HistoryAnalysis();
        for (int run = 0; run < 5000; run++)
        {
            var s = Settings(); s.WindowDays = rng.Next(3, 181); s.ConfirmationDays = rng.Next(1, 11);
            var prices = new Dictionary<int, float>();
            float p = 100;
            for (int day = -180; day <= 10; day++) { p = Math.Max(1, p + rng.Next(-10, 11)); prices[day] = p; }
            if (run % 5 == 0) prices.Remove(rng.Next(-20, 10));
            if (run % 7 == 0) prices[rng.Next(-20, 10)] = float.NaN;
            var view = new MarketHistory(prices, 1);
            scratch.Prepare(view, 10, s.WindowDays, s.ConfirmationDays);
            for (int offset = 0; offset < s.ConfirmationDays; offset++)
            {
                HistoryRange expected = view.Range(10 - offset, s.WindowDays), actual = scratch.At(offset);
                if (expected.Count != actual.Count || expected.StartDay != actual.StartDay
                    || (expected.Count > 0 && (expected.Low != actual.Low || expected.High != actual.High)))
                    throw new Exception("Rolling range mismatch: " + run);
            }
            int streak = 0;
            // Slow independent chronological reference; no current-day extrema reused.
            for (int day = 1; day <= 10; day++)
            {
                var window = new List<float>();
                for (int old = day; old > day - s.WindowDays && prices.TryGetValue(old, out float value) && Numbers.Price(value); old--) window.Add(value);
                bool buy = window.Count >= 3 && prices.TryGetValue(day - 1, out float previous)
                    && window[0] > previous && (window.Max() - (double)window.Min()) / window.Min() * 100 >= s.MinimumRangePercent
                    && window[0] <= window.Min() + (window.Max() - (double)window.Min()) * s.BuyRangePercent / 100;
                streak = buy ? streak + 1 : 0;
            }
            var book = new AssetBook();
            var result = Evaluate(prices, book, s, 10);
            if ((result.Action == InvestmentAction.Buy) != (streak >= s.ConfirmationDays)
                || book.BuyConfirmations != Math.Min(streak, s.ConfirmationDays)) throw new Exception("Historical reference mismatch: " + run);
        }
        check(true, "5,000 varied histories agree with independent chronological confirmations and range scans, including scratch reuse");
    }

    internal static void Benchmark()
    {
        const int assets = 5000;
        var prices = new Dictionary<int, float>();
        for (int day = -200; day <= 0; day++) prices[day] = 100 + Math.Abs(day % 30);
        for (int day = 1; day <= 10; day++) prices[day] = 100 + day * .1f;
        var s = new InvestorSettings { WindowDays = 180, ConfirmationDays = 10, MaximumTradesPerDay = 20, CashReservePercent = 30 };
        var books = Enumerable.Range(0, assets).Select(_ => new AssetBook()).ToArray();
        var analysis = new HistoryAnalysis();
        var ids = Enumerable.Range(0, assets).Select(i => i.ToString()).ToArray();
        var watch = new Stopwatch(); var times = new List<double>();
        for (int run = 0; run < 20; run++)
        {
            watch.Restart(); var candidates = new List<PurchaseCandidate>();
            for (int asset = 0; asset < assets; asset++)
            {
                var d = InvestmentPolicy.Evaluate(books[asset], s, new MarketHistory(prices, 1), 10, prices[10], 0, true, analysis);
                if (d.Action == InvestmentAction.Buy) candidates.Add(new PurchaseCandidate { Id = ids[asset], Price = prices[10], Upside = d.Score });
            }
            PurchasePlan.Build(s, new PortfolioValue { Cash = 1e15 }, candidates);
            watch.Stop(); if (run >= 5) times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Console.WriteLine($"HISTORY_BENCHMARK assets={assets} window=180 confirmations=10 cash=1e15 runs={times.Count} median_ms={times[times.Count / 2]:F3} max_ms={times.Last():F3}");
        Console.WriteLine("Isolated Core-history reads, analysis, ranking and allocation; excludes Unity, native trades and rendering.");
    }
}
