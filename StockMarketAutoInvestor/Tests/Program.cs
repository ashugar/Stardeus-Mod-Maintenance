using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using StockMarketAutoInvestor;

internal static class Program
{
    private static int passed;
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
        passed++;
        Console.WriteLine("PASS: " + message);
    }

    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe") { AssemblyProbe.Run(args); return; }
        if (args.Length > 0 && args[0] == "--benchmark") { HistoryTests.Benchmark(); return; }
        HistoryTests.Run(Check);
        BudgetTests();
        LargeOrderTests();
        AllocationTests();
        SpendingMigrationTests();
        SaleTests();
        SerializationTests();
        Console.WriteLine($"PASS_COUNT={passed}");
    }

    private static void BudgetTests()
    {
        var s = new InvestorSettings { CashReservePercent = 30, MaximumSharePrice = 1000 };
        var wealth = new PortfolioValue { Cash = 8000 };
        wealth.AddLot(10, 150, 200);
        Check(wealth.CostBasis == 1500 && wealth.UnrealizedProfit == 500 && wealth.MarketValue == 2000,
            "Original cost and current gain remain separate without double-counting share value");
        Check(wealth.Total == 10000 && wealth.Reserve(30) == 3000 && wealth.Spendable(30) == 5000,
            "Reserve is a percentage of cash plus the current value of all shares");
        Check(InvestmentPolicy.BuyQuantity(s, wealth, 1000) == 5, "A share exactly at the maximum price is eligible");
        Check(InvestmentPolicy.BuyQuantity(s, wealth, 1800) == 0 && InvestmentPolicy.BuyQuantity(s, wealth, 800) == 6,
            "A 1000 price limit rejects an 1800 share and permits multiple 800 shares");
        wealth.AddLot(10, 200, 100);
        Check(wealth.CostBasis == 3500 && wealth.MarketValue == 3000 && wealth.UnrealizedProfit == -500,
            "Losing lots reduce market value without erasing original purchase costs");
        Check(new PortfolioValue { Cash = 100, MarketValue = 1000 }.Spendable(30) == 0,
            "A reserve larger than wallet cash prevents purchases without forcing a sale");
        Check(wealth.Spendable(0) == wealth.Cash && wealth.Spendable(100) == 0,
            "Zero percent permits available cash and 100 percent prevents purchases");
        var invalid = new PortfolioValue { Cash = 1000 };
        invalid.AddLot(1, 20, double.NaN);
        Check(!invalid.Valid && InvestmentPolicy.BuyQuantity(s, invalid, 10) == 0,
            "An unpriced holding prevents an underestimated reserve and unsafe purchases");
        Check(InvestmentPolicy.BuyQuantity(s, new PortfolioValue { Cash = double.NaN }, 10) == 0
            && InvestmentPolicy.BuyQuantity(s, wealth, float.NaN) == 0
            && wealth.Spendable(double.NaN) == 0, "Invalid balances, quotes and percentages prevent purchases");
        s = new InvestorSettings { CashReservePercent = 0, MaximumSharePrice = 1e15 };
        Check(InvestmentPolicy.BuyQuantity(s, new PortfolioValue { Cash = 1e18 }, 1) == int.MaxValue,
            "Large wallets can buy up to Core's per-stock storage limit");
        var random = new Random(1234);
        for (int i = 0; i < 20000; i++)
        {
            float price = (float)(.01 + random.NextDouble() * 1000000);
            s.CashReservePercent = random.NextDouble() * 100;
            wealth = new PortfolioValue { Cash = random.NextDouble() * 1e12, MarketValue = random.NextDouble() * 1e11 };
            int amount = InvestmentPolicy.BuyQuantity(s, wealth, price);
            double actualNativeCharge = amount * price;
            if (amount < 0 || actualNativeCharge > wealth.Spendable(s.CashReservePercent))
                throw new Exception("Native float charge exceeded spendable cash");
            double postCash = wealth.Cash - actualNativeCharge;
            double postTotal = postCash + (wealth.MarketValue + amount * (double)price);
            if (amount > 0 && postCash < postTotal * (s.CashReservePercent / 100))
                throw new Exception("Post-purchase cash fell below the recalculated reserve");
        }
        Check(true, "20,000 varied purchases preserve both pre-trade and post-trade percentage reserves with Core's rounded charge");
        s = new InvestorSettings { WindowDays = 999, ConfirmationDays = 0,
            BuyRangePercent = double.NaN, CashReservePercent = double.PositiveInfinity, MaximumSharePrice = -5,
            PurchaseMethod = (PurchaseMethod)99 };
        s.Normalize();
        Check(s.WindowDays == 180 && s.ConfirmationDays == 1, "Invalid count settings normalize to supported bounds");
        Check(Numbers.Finite(s.BuyRangePercent) && s.CashReservePercent == 100 && s.MaximumSharePrice == 0
            && s.PurchaseMethod == PurchaseMethod.DivideEvenly, "Invalid spending settings normalize conservatively");
    }

    private static void LargeOrderTests()
    {
        var settings = new InvestorSettings { CashReservePercent = 30, MaximumSharePrice = 1000 };
        var portfolio = new PortfolioValue { Cash = 800000000, MarketValue = 200000000 };
        Check(InvestmentPolicy.BuyQuantity(settings, portfolio, 100) == 5000000,
            "Thirty percent reserve leaves five million 100-credit shares affordable");
        Check(CoreTradeLimits.RemainingCapacity(0) == int.MaxValue
            && CoreTradeLimits.RemainingCapacity(int.MaxValue - 10L) == 10
            && CoreTradeLimits.RemainingCapacity(int.MaxValue) == 0
            && CoreTradeLimits.RemainingCapacity((long)int.MaxValue + 1) == 0
            && CoreTradeLimits.RemainingCapacity(-1) == 0,
            "Existing holdings count toward the Int32 storage ceiling without overflow");
        Check(PurchasePlan.Quantity(1e300, float.Epsilon) == int.MaxValue
            && PurchasePlan.Quantity(1e300, float.MaxValue) == 1,
            "Extreme prices and budgets cannot overflow share counts or native float charges");
        var random = new Random(214748);
        foreach (int quantity in new[] { 1000001, 16777215, 16777216, 16777217, int.MaxValue - 64, int.MaxValue })
        {
            foreach (float price in new[] { .01f, 1f, 123.45f, 2000f })
            {
                double ceiling = CoreTradeLimits.ChargeCeiling(quantity, price);
                double exact = quantity * (double)price;
                double roundedCount = (double)(float)quantity * price;
                if (ceiling < exact || ceiling < roundedCount || ceiling < (double)(float)exact
                    || ceiling < (double)(float)roundedCount) throw new Exception("Native charge bound is too low");
                int affordable = PurchasePlan.Quantity(ceiling, price);
                if (affordable < quantity) throw new Exception("Large funded quantity incorrectly rejected");
            }
        }
        Check(true, "Float count and price rounding boundaries are covered through 2,147,483,647 shares");
        for (int i = 0; i < 20000; i++)
        {
            float price = (float)(.001 + random.NextDouble() * 1000);
            settings.CashReservePercent = i % 5 == 0 ? 99.9999999 : random.NextDouble() * 100;
            portfolio = new PortfolioValue { Cash = random.NextDouble() * 1e15, MarketValue = random.NextDouble() * 1e13 };
            int amount = InvestmentPolicy.BuyQuantity(settings, portfolio, price);
            double bound = CoreTradeLimits.ChargeCeiling(amount, price);
            double exact = amount * (double)price;
            double roundedCount = (double)(float)amount * price;
            foreach (double charge in new[] { exact, roundedCount, (double)(float)exact, (double)(float)roundedCount, bound })
            {
                double cashAfter = portfolio.Cash - charge;
                double totalAfter = cashAfter + portfolio.MarketValue + exact;
                if (charge > portfolio.Spendable(settings.CashReservePercent)
                    || (amount > 0 && cashAfter < totalAfter * (settings.CashReservePercent / 100)))
                    throw new Exception("Large order exceeded the reserve");
            }
        }
        Check(true, "20,000 large orders preserve reserves across float and extended-precision charge variants");
        settings = new InvestorSettings { StopLossEnabled = true, ConfirmationDays = 1 };
        var book = new AssetBook { ManagedQuantity = int.MaxValue, ManagedCost = 100, LastTradeDay = 1 };
        var history = new MarketHistory(new Dictionary<int, float> { [1] = 100, [2] = 95, [3] = 90, [4] = 80 }, 1);
        Check(InvestmentPolicy.Evaluate(book, settings, history, 4, 80, int.MaxValue, true).Action == InvestmentAction.Sell,
            "Loss-limit policy can sell a managed holding at Core's storage ceiling");
        var state = new InvestorState();
        state.Books["large"] = book;
        state.AddTrade(new TradeRecord { Asset = "large", Quantity = int.MaxValue, Price = 100, Buy = true });
        var restored = JsonConvert.DeserializeObject<InvestorState>(JsonConvert.SerializeObject(state));
        Check(restored.Books["large"].ManagedQuantity == int.MaxValue && restored.Trades[0].Quantity == int.MaxValue,
            "Large managed holdings and trade records round-trip without a save schema change");
    }

    private static void AllocationTests()
    {
        var s = new InvestorSettings { CashReservePercent = 20, MaximumSharePrice = 1000, MaximumTradesPerDay = 2 };
        var wallet = new PortfolioValue { Cash = 1000 };
        var candidates = new[] {
            new PurchaseCandidate { Id = "best", Price = 100, Upside = .5 },
            new PurchaseCandidate { Id = "cheap", Price = 10, Upside = .1 },
            new PurchaseCandidate { Id = "second", Price = 50, Upside = .3 } };
        var plan = PurchasePlan.Build(s, wallet, candidates);
        Check(plan.Count == 2 && plan[0].Candidate.Id == "best" && plan[0].Quantity == 4
            && plan[1].Candidate.Id == "second" && plan[1].Quantity == 8,
            "Divide Evenly selects by observed upside and divides cash equally across distinct stocks");
        s.PurchaseMethod = PurchaseMethod.MostProfitable;
        plan = PurchasePlan.Build(s, wallet, candidates);
        Check(plan.Count == 1 && plan[0].Candidate.Id == "best" && plan[0].Quantity == 8,
            "Most Profitable commits available cash to the greatest observed percentage upside");
        s.PurchaseMethod = PurchaseMethod.ByQuantity;
        plan = PurchasePlan.Build(s, wallet, candidates);
        Check(plan.Count == 1 && plan[0].Candidate.Id == "cheap" && plan[0].Quantity == 80,
            "By Quantity selects the cheapest qualifying shares");
        wallet.Cash = 200000000;
        s.PurchaseMethod = PurchaseMethod.MostProfitable;
        plan = PurchasePlan.Build(s, wallet, candidates);
        Check(plan.Count == 1 && plan[0].Quantity == 1600000 && plan[0].Candidate.Id == "best",
            "Most Profitable invests available cash above one million shares");
        s.PurchaseMethod = PurchaseMethod.ByQuantity;
        plan = PurchasePlan.Build(s, wallet, candidates);
        Check(plan.Count == 1 && plan[0].Candidate.Id == "cheap" && plan[0].Quantity == 16000000,
            "Quantity mode invests available cash in sixteen million cheap shares");
        wallet.Cash = 1e15;
        plan = PurchasePlan.Build(s, wallet, candidates);
        Check(plan.Count == 2 && plan.All(p => p.Quantity == int.MaxValue)
            && plan[0].Candidate.Id == "cheap" && plan[1].Candidate.Id == "second",
            "Quantity mode continues to the next stock at Core's storage limit");
        s.PurchaseMethod = PurchaseMethod.MostProfitable;
        plan = PurchasePlan.Build(s, wallet, candidates);
        Check(plan.Count == 2 && plan.All(p => p.Quantity == int.MaxValue)
            && plan[0].Candidate.Id == "best" && plan[1].Candidate.Id == "second",
            "Most Profitable continues by observed upside at Core's storage limit");
        s.MaximumTradesPerDay = 1;
        Check(PurchasePlan.Build(s, wallet, candidates).Count == 1, "One purchase slot means one stock type, not one share");
        s.MaximumSharePrice = 10;
        s.PurchaseMethod = PurchaseMethod.MostProfitable;
        Check(PurchasePlan.Build(s, wallet, candidates).Single().Candidate.Id == "cheap",
            "A better-ranked but overpriced share cannot block an affordable alternative");
        s.MaximumTradesPerDay = 3;
        Check(PurchasePlan.Build(s, wallet, new[] { candidates[1], candidates[1] }).Count == 1,
            "Duplicate candidates cannot produce multiple purchases of the same stock");
        s.PurchaseMethod = PurchaseMethod.DivideEvenly; s.MaximumSharePrice = 1000; wallet.Cash = 1000;
        candidates = new[] { new PurchaseCandidate { Id = "expensive", Price = 800, Upside = 1 },
            new PurchaseCandidate { Id = "a", Price = 300, Upside = .3 }, new PurchaseCandidate { Id = "b", Price = 200, Upside = .2 } };
        plan = PurchasePlan.Build(s, wallet, candidates);
        Check(plan.Count == 2 && plan.All(p => p.Candidate.Id != "expensive") && plan.Sum(p => p.Charge) <= 800,
            "Even allocation finds an affordable spread without spending the reserve");
        plan = PurchasePlan.Build(s, wallet, candidates.Take(1));
        Check(plan.Count == 1 && plan[0].Quantity == 1, "Even allocation can select fewer stocks when only one share fits");
        s.CashReservePercent = 100;
        Check(PurchasePlan.Build(s, wallet, candidates).Count == 0, "All purchase methods respect a fully reserved wallet");
        s.CashReservePercent = 0;
        Check(PurchasePlan.Build(s, new PortfolioValue { Cash = 1000, Complete = false }, candidates).Count == 0,
            "Missing portfolio valuation blocks allocation even at zero percent");
        var settings = new InvestorSettings();
        var a = InvestmentPolicy.Evaluate(new AssetBook(), settings,
            new MarketHistory(new Dictionary<int, float> { [1] = 200, [2] = 150, [3] = 100, [4] = 80, [5] = 81 }, 1), 5, 81, 0, true);
        var b = InvestmentPolicy.Evaluate(new AssetBook(), settings,
            new MarketHistory(new Dictionary<int, float> { [1] = 102, [2] = 101, [3] = 100, [4] = 99, [5] = 99.1f }, 1), 5, (double)99.1f, 0, true);
        Check(a.Score > b.Score && Math.Abs(a.Score - (200 - 81d) / 81) < 1e-10,
            "Opportunity ranking compares potential gain per invested Starcredit from Core history");
        var random = new Random(912);
        foreach (PurchaseMethod method in Enum.GetValues(typeof(PurchaseMethod)))
        {
            s.PurchaseMethod = method;
            for (int run = 0; run < 2000; run++)
            {
                s.CashReservePercent = random.NextDouble() * 100;
                s.MaximumTradesPerDay = random.Next(1, 21);
                s.MaximumSharePrice = random.NextDouble() * 10000;
                wallet = new PortfolioValue { Cash = random.NextDouble() * 1e10, MarketValue = random.NextDouble() * 1e9 };
                candidates = Enumerable.Range(0, 30).Select(i => new PurchaseCandidate {
                    Id = i.ToString(), Price = (float)(.001 + random.NextDouble() * 15000), Upside = random.NextDouble()
                }).ToArray();
                plan = PurchasePlan.Build(s, wallet, candidates);
                if (plan.Count > s.MaximumTradesPerDay || plan.Select(p => p.Candidate.Id).Distinct().Count() != plan.Count
                    || plan.Sum(p => p.Charge) > wallet.Spendable(s.CashReservePercent)
                    || plan.Any(p => p.Quantity <= 0 || p.Candidate.Price > s.MaximumSharePrice))
                    throw new Exception("Unsafe purchase allocation: " + method);
            }
        }
        Check(true, "6,000 varied allocations preserve reserve, maximum share price and distinct-stock limits across all methods");
    }

    private static void SpendingMigrationTests()
    {
        var old = JsonConvert.DeserializeObject<LegacySpendingSettings>("{\"CashReserve\":1000,\"TotalBudget\":2000,\"AssetBudget\":180,\"TradeBudget\":30}");
        var s = new InvestorSettings();
        var wealth = new PortfolioValue { Cash = 8000, CostBasis = 1000, MarketValue = 2000 };
        old.Apply(s, wealth, 500);
        Check(s.CashReservePercent == 65 && wealth.Spendable(s.CashReservePercent) == 1500,
            "Legacy conversion preserves the previous remaining investment allowance as a percentage reserve");
        Check(s.MaximumSharePrice == 180 && s.PurchaseMethod == PurchaseMethod.DivideEvenly,
            "The old per-asset amount becomes the share-price limit and initial allocation is even");
        old.Apply(s, new PortfolioValue { Cash = 1e18 }, 0);
        Check(s.CashReservePercent == 100, "Huge-wallet migration rounds conservatively instead of exposing the entire wallet");
        old.Apply(s, new PortfolioValue { Cash = 8000, Complete = false }, 0);
        Check(s.CashReservePercent == 100, "Migration with unknown holdings protects all available cash");
        var json = JsonConvert.SerializeObject(s);
        var restored = JsonConvert.DeserializeObject<InvestorSettings>(json);
        Check(!json.Contains("TotalBudget") && !json.Contains("AssetBudget") && !json.Contains("TradeBudget")
            && !json.Contains("\"CashReserve\":"), "New saves omit every obsolete absolute spending field");
        Check(restored.CashReservePercent == s.CashReservePercent && restored.MaximumSharePrice == 180,
            "Percentage reserve and share-price cap survive a settings round trip");
        foreach (PurchaseMethod method in Enum.GetValues(typeof(PurchaseMethod)))
        {
            s.PurchaseMethod = method;
            Check(JsonConvert.DeserializeObject<InvestorSettings>(JsonConvert.SerializeObject(s)).PurchaseMethod == method,
                "Purchase method survives save/load: " + method);
        }
    }

    // Reproduces the verified Core lot loop, including its original-amount bug.
    private static int NativeSell(List<int> lots, int amount)
    {
        int sold = 0;
        for (int i = lots.Count - 1; i >= 0; i--)
        {
            int take = Math.Min(amount, lots[i]);
            sold += take; lots[i] -= take;
            if (lots[i] == 0) lots.RemoveAt(i);
            if (sold == amount) break;
        }
        return sold;
    }
    private static void SaleTests()
    {
        Check(NativeSell(new List<int> { 10, 5 }, 8) == 13, "Reference Core behaviour reproduces the cross-lot oversale");
        for (int requested = 1; requested <= 37; requested++)
        {
            var lots = new List<int> { 10, 7, 15, 5 };
            int remaining = requested, sold = 0;
            while (remaining > 0)
            {
                int batch = NativeSalePlan.NextBatch(remaining, lots[lots.Count - 1]);
                int actual = NativeSell(lots, batch);
                if (actual != batch) throw new Exception("Batch oversale");
                remaining -= actual; sold += actual;
            }
            if (sold != requested || lots.Sum() != 37 - requested) throw new Exception("Sale mismatch");
        }
        Check(true, "All 37 partial/full quantities sell exactly the requested shares across four lots");
        foreach (int requested in new[] { 1000001, 1500000000, int.MaxValue })
        {
            var lots = new List<int> { 1000000000, 1000000000, 147483647 };
            int remaining = requested;
            while (remaining > 0)
            {
                int batch = NativeSalePlan.NextBatch(remaining, lots[lots.Count - 1]);
                if (NativeSell(lots, batch) != batch) throw new Exception("Large batch oversale");
                remaining -= batch;
            }
            if (lots.Sum(n => (long)n) != (long)int.MaxValue - requested) throw new Exception("Large sale mismatch");
        }
        Check(true, "Partial and full multi-lot sales above one million preserve exact quantities through Int32 max");
        Check(NativeSalePlan.NextBatch(10, 0) == 0 && NativeSalePlan.NextBatch(-1, 5) == 0, "Empty and invalid sale batches stop safely");
    }

    private static void SerializationTests()
    {
        Check(JsonConvert.DeserializeObject<InvestorSettings>("{}").ShowTradeFanfare,
            "Existing saves without the new setting enable trade fanfare by default");
        var quietSettings = new InvestorSettings { ShowTradeFanfare = false };
        Check(!JsonConvert.DeserializeObject<InvestorSettings>(JsonConvert.SerializeObject(quietSettings)).ShowTradeFanfare,
            "Disabled trade fanfare survives save/load");
        var state = new InvestorState(); state.SetEnabled(true, 10);
        state.BeginMarketDay(11);
        state.Book("asset").ManagedQuantity = 4; state.Book("asset").ManagedTick = 123456789;
        state.Book("asset").ManagedCost = (double)(float)123.45;
        state.AddTrade(new TradeRecord { Asset = "asset", Day = 11, Price = 123.45, Quantity = 4, Buy = true, Automatic = true });
        var restored = JsonConvert.DeserializeObject<InvestorState>(JsonConvert.SerializeObject(state));
        Check(restored.Enabled && restored.LastDay == 11 && restored.Books["asset"].ManagedQuantity == 4, "Save round trip preserves enabled state and managed holdings");
        Check(!restored.BeginMarketDay(11), "Loading does not duplicate the last processed day");
        Check(restored.Books["asset"].ManagedTick == 123456789 && restored.Books["asset"].ManagedCost == state.Books["asset"].ManagedCost,
            "Exact lot identity and purchase price survive serialization");
        Check(restored.Trades.Count == 1 && restored.Trades[0].Automatic, "Trade history survives serialization");
        restored.AddTrade(new TradeRecord { Asset = "asset", Day = 12, Quantity = 2, Price = 150,
            Buy = false, CostBasis = 200, RealizedProfit = 100 });
        restored = JsonConvert.DeserializeObject<InvestorState>(JsonConvert.SerializeObject(restored));
        Check(restored.Schema == 3 && restored.Trades.Last().CostBasis == 200 && restored.Trades.Last().RealizedProfit == 100
            && restored.Trades[0].CostBasis == null, "New cost/profit records persist while unknown older trade costs remain unknown");
        restored.SetEnabled(false, 11);
        restored = JsonConvert.DeserializeObject<InvestorState>(JsonConvert.SerializeObject(restored));
        Check(!restored.Enabled && !JsonConvert.SerializeObject(restored).Contains("Samples"), "Disabled saves contain no duplicate price history");
        for (int i = 0; i < 300; i++) restored.AddTrade(new TradeRecord { Day = i });
        Check(restored.Trades.Count == 200 && restored.Trades[0].Day == 100, "Trade history remains bounded");
    }
}
