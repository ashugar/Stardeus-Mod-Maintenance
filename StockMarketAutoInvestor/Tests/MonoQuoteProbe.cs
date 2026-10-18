// Compile separately with the .NET Framework compiler and run via RunMonoQuoteProbe.ps1.
// Uses isolated managed Core objects; never starts Unity or reads a save.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Game.Data;
using Game.Data.Economy;
using StockMarketAutoInvestor;

public static class MonoQuoteProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double NativeQuote(MarketItemModifier modifier) { return modifier.Price; }

    public static int Main(string[] args)
    {
        var item = new MarketItem();
        var modifier = new MarketItemModifier();
        typeof(MarketItemModifier).GetField("item", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(modifier, item);
        var random = new Random(1421);
        var entries = new Dictionary<int, float>();
        int mismatches = 0;
        for (int i = 0; i < 100000; i++)
        {
            item.Value = (float)(1 + random.NextDouble() * 5000);
            modifier.PriceMul = (float)(.5 + random.NextDouble() * 2);
            entries[6] = modifier.PriceMul;
            var history = new MarketHistory(entries, item.Value, modifier.PriceMul);
            double price;
            history.TryPrice(6, out price);
            double native = NativeQuote(modifier);
            if (price != native || !history.MatchesCurrentQuote(6, native)) mismatches++;
        }
        Console.WriteLine("MONO_QUOTE_PARITY cases=100000 mismatches=" + mismatches);
        if (mismatches != 0) return 1;
        item.Value = 2200; modifier.PriceMul = .93f;
        entries = new Dictionary<int, float> { {1,1.2f}, {2,1.1f}, {3,1f}, {4,.9f}, {5,.91f}, {6,.93f} };
        var current = new MarketHistory(entries, item.Value, modifier.PriceMul);
        var book = new AssetBook(); var settings = new InvestorSettings();
        var decision = InvestmentPolicy.Evaluate(book, settings, current, 6, NativeQuote(modifier), 0, true);
        if (decision.Action != InvestmentAction.Buy) throw new Exception("Valid quote blocked: " + decision.Reason);
        modifier.PriceMul = .94f;
        var stale = new MarketHistory(entries, item.Value, modifier.PriceMul);
        decision = InvestmentPolicy.Evaluate(book, settings, stale, 6, NativeQuote(modifier), 0, true);
        if (decision.Action != InvestmentAction.Wait || !decision.Reason.Contains("differs")) throw new Exception("Changed quote accepted");
        Console.WriteLine("MONO_POLICY valid_history_buy=true changed_multiplier_blocked=true");
        LargeNativeTrades();
        return 0;
    }

    private static void LargeNativeTrades()
    {
        // Uninitialized managed state avoids Unity setup; all objects are private to this process.
        var state = (GameState)FormatterServices.GetUninitializedObject(typeof(GameState));
        state.Wallet = new Wallet();
        var item = new MarketItem();
        var modifier = new MarketItemModifier { Id = "large-probe", IdH = 123, S = state, PriceMul = 1 };
        typeof(MarketItemModifier).GetField("item", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(modifier, item);
        var settings = new InvestorSettings { MaximumSharePrice = 10000 };
        var random = new Random(1000001);
        int aboveMillion = 0;
        for (int run = 0; run < 2000; run++)
        {
            int desired = run % 3 == 0 ? int.MaxValue : random.Next(2000000, int.MaxValue);
            item.Value = (float)(.01 + random.NextDouble() * 999);
            float price = (float)NativeQuote(modifier);
            settings.CashReservePercent = run % 4 == 0 ? 99.9999 : random.NextDouble() * 90;
            var value = new PortfolioValue { Cash = desired * (double)price / (1 - settings.CashReservePercent / 100), MarketValue = 10000 };
            int amount = InvestmentPolicy.BuyQuantity(settings, value, price);
            if (amount <= 0) throw new Exception("Large order did not qualify");
            if (amount > 1000000) aboveMillion++;
            state.Wallet.Amount = value.Cash;
            var portfolio = new MarketPortfolio();
            portfolio.Buy(modifier, amount);
            if (portfolio.Positions == null || portfolio.Positions.Count != 1 || portfolio.OwnedPositions(123) != amount)
                throw new Exception("Core did not buy the exact large quantity");
            double totalAfter = state.Wallet.Amount + (value.MarketValue + amount * (double)price);
            if (state.Wallet.Amount < value.Reserve(settings.CashReservePercent)
                || state.Wallet.Amount < totalAfter * (settings.CashReservePercent / 100))
                throw new Exception("Native large purchase violated the reserve");
            item.Value *= 2; // Positive gain avoids the achievement path in this isolated test.
            int firstSale = amount / 2;
            portfolio.Sell(modifier, firstSale);
            if (portfolio.OwnedPositions(123) != amount - firstSale) throw new Exception("Core partial sale mismatch");
            portfolio.Sell(modifier, amount - firstSale);
            if (portfolio.OwnedPositions(123) != 0) throw new Exception("Core full sale mismatch");
        }
        if (aboveMillion != 2000) throw new Exception("Large-order probe did not exercise enough large purchases");
        Console.WriteLine("MONO_LARGE_TRADES buys=2000 above_million=" + aboveMillion + " partial_and_full_sales=4000 reserves_preserved=true");
        foreach (int requested in new[] { 1000001, 1500000000, int.MaxValue })
        {
            var portfolio = new MarketPortfolio();
            item.Value = 1;
            state.Wallet.Amount = 1e12;
            foreach (int count in new[] { 1000000000, 1000000000, 147483647 }) portfolio.Buy(modifier, count);
            if (portfolio.OwnedPositions(123) != int.MaxValue) throw new Exception("Core total holdings overflowed");
            item.Value = 2;
            int remaining = requested;
            while (remaining > 0)
            {
                int batch = NativeSalePlan.NextBatch(remaining, portfolio.Positions[portfolio.Positions.Count - 1].Position);
                int before = portfolio.OwnedPositions(123);
                portfolio.Sell(modifier, batch);
                if (before - portfolio.OwnedPositions(123) != batch) throw new Exception("Core large multi-lot oversale");
                remaining -= batch;
            }
            if (portfolio.OwnedPositions(123) != int.MaxValue - requested) throw new Exception("Core large multi-lot remainder mismatch");
        }
        Console.WriteLine("MONO_LARGE_LOTS cases=3 maximum_owned=2147483647 exact_sales=true");
    }
}
