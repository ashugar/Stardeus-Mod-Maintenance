using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using StockMarketAutoInvestor;

internal static class AssemblyProbe
{
    internal static void Run(string[] args)
    {
        string modFile = Path.GetFullPath(args[1]);
        string gameDir = Path.GetFullPath(args[2]);
        string frameworkDir = Path.GetFullPath(args[3]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string dir in new[] { gameDir, frameworkDir, Path.GetDirectoryName(modFile) })
            {
                string candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
        Assembly mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modFile);
        Type[] types = mod.GetTypes();
        int methods = 0;
        foreach (Type type in types)
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            { _ = method.ReturnType; _ = method.GetParameters(); methods++; }
        Type sys = types.Single(t => t.Name == "StockMarketAutoInvestorSys");
        MethodInfo register = sys.GetMethod("Register", BindingFlags.Static | BindingFlags.NonPublic);
        if (!register.CustomAttributes.Any(a => a.AttributeType.Name == "SelfInitAttribute")) throw new Exception("Missing SelfInit");
        Console.WriteLine($"ASSEMBLY_LOAD_OK types={types.Length} methods={methods} version={mod.GetName().Version} registration=SelfInit");

        // This is an isolated managed object test. It does not start Unity, load a save,
        // invoke achievements, register the mod, or touch the running game process.
        Assembly game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameDir, "Game.dll"));
        Type portfolioType = game.GetType("Game.Data.Economy.MarketPortfolio", true);
        Type positionType = game.GetType("Game.Data.Economy.MarketPosition", true);
        Type modifierType = game.GetType("Game.Data.Economy.MarketItemModifier", true);
        Type itemType = game.GetType("Game.Data.Economy.MarketItem", true);
        Type walletType = game.GetType("Game.Data.Economy.Wallet", true);
        Type stateType = game.GetType("Game.Data.GameState", true);
        FanfareProbe(mod, game, stateType);
        int checks = 0;
        for (int requested = 1; requested <= 37; requested++)
        {
            object portfolio = Activator.CreateInstance(portfolioType);
            var lots = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(positionType));
            foreach (int count in new[] { 10, 7, 15, 5 })
            {
                object lot = Activator.CreateInstance(positionType);
                positionType.GetField("IdH").SetValue(lot, 123);
                positionType.GetField("Position").SetValue(lot, count);
                positionType.GetField("Cost").SetValue(lot, 10f);
                lots.Add(lot);
            }
            portfolioType.GetField("Positions").SetValue(portfolio, lots);
            object wallet = Activator.CreateInstance(walletType);
            object state = RuntimeHelpers.GetUninitializedObject(stateType);
            stateType.GetField("Wallet").SetValue(state, wallet);
            object item = Activator.CreateInstance(itemType);
            itemType.GetField("Value").SetValue(item, 10f);
            object modifier = Activator.CreateInstance(modifierType);
            modifierType.GetField("IdH").SetValue(modifier, 123);
            modifierType.GetField("S").SetValue(modifier, state);
            modifierType.GetField("PriceMul").SetValue(modifier, 1f);
            modifierType.GetField("item", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(modifier, item);
            int remaining = requested;
            while (remaining > 0)
            {
                int latest = (int)positionType.GetField("Position").GetValue(lots[lots.Count - 1]);
                int batch = NativeSalePlan.NextBatch(remaining, latest);
                portfolioType.GetMethod("Sell").Invoke(portfolio, new[] { modifier, (object)batch });
                remaining -= batch;
            }
            int held = 0;
            foreach (object lot in lots) held += (int)positionType.GetField("Position").GetValue(lot);
            double paid = (double)walletType.GetField("Amount").GetValue(wallet);
            if (held != 37 - requested || paid != requested * 10d) throw new Exception("Native sale mismatch");
            checks++;
        }
        Console.WriteLine($"NATIVE_SELL_OK cases={checks} assembly={game.GetName().Name}");

        // Exercise the mod's actual valuation bridge against Core's lot and quote types.
        object valuationPortfolio = Activator.CreateInstance(portfolioType);
        var valuationLots = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(positionType));
        foreach (var spec in new[] { (Id: 123, Quantity: 10, Cost: 100f), (Id: 123, Quantity: 5, Cost: 120f), (Id: 456, Quantity: 10, Cost: 50f) })
        {
            object lot = Activator.CreateInstance(positionType);
            positionType.GetField("IdH").SetValue(lot, spec.Id);
            positionType.GetField("Position").SetValue(lot, spec.Quantity);
            positionType.GetField("Cost").SetValue(lot, spec.Cost);
            valuationLots.Add(lot);
        }
        portfolioType.GetField("Positions").SetValue(valuationPortfolio, valuationLots);
        object valuationWallet = Activator.CreateInstance(walletType);
        walletType.GetField("Amount").SetValue(valuationWallet, 8000d);
        object valuationState = RuntimeHelpers.GetUninitializedObject(stateType);
        stateType.GetField("Wallet").SetValue(valuationState, valuationWallet);
        stateType.GetField("Portfolio").SetValue(valuationState, valuationPortfolio);
        Type marketType = game.GetType("Game.Data.Economy.Market", true);
        object market = RuntimeHelpers.GetUninitializedObject(marketType);
        var quotes = (IDictionary)Activator.CreateInstance(typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(int), modifierType));
        foreach (var spec in new[] { (Id: 123, Price: 150f), (Id: 456, Price: 25f) })
        {
            object item = Activator.CreateInstance(itemType);
            itemType.GetField("Value").SetValue(item, spec.Price);
            object modifier = Activator.CreateInstance(modifierType);
            modifierType.GetField("IdH").SetValue(modifier, spec.Id);
            modifierType.GetField("PriceMul").SetValue(modifier, 1f);
            modifierType.GetField("item", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(modifier, item);
            quotes.Add(spec.Id, modifier);
        }
        marketType.GetField("Modifiers").SetValue(market, quotes);
        stateType.GetField("Market").SetValue(valuationState, market);
        object investor = Activator.CreateInstance(sys);
        sys.GetField("loadedState", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(investor, valuationState);
        var totals = sys.GetMethod("PortfolioTotals", BindingFlags.NonPublic | BindingFlags.Instance);
        object value = totals.Invoke(investor, null);
        Type valueType = value.GetType();
        double Field(string name) => (double)valueType.GetField(name).GetValue(value);
        double Property(string name) => (double)valueType.GetProperty(name).GetValue(value);
        if (Field("Cash") != 8000 || Field("CostBasis") != 2100 || Field("MarketValue") != 2500
            || Property("UnrealizedProfit") != 400 || Property("Total") != 10500
            || (double)valueType.GetMethod("Reserve").Invoke(value, new object[] { 30d }) != 3150)
            throw new Exception("Core portfolio valuation mismatch");
        quotes.Remove(456);
        value = totals.Invoke(investor, null);
        if ((bool)valueType.GetProperty("Valid").GetValue(value)) throw new Exception("Missing Core quote accepted");
        Console.WriteLine("NATIVE_PORTFOLIO_OK lots=3 cost=2100 market=2500 gain=400 reserve_30_percent=3150 missing_quote_rejected=true");

        // Call the compiled consumer's real bridge with Core's actual history field.
        object historyModifier = quotes[123];
        var nativeHistory = new System.Collections.Generic.Dictionary<int, float> {
            [1] = 1.2f, [2] = 1.1f, [3] = 1f, [4] = .9f, [5] = .91f, [6] = .92f
        };
        modifierType.GetField("History").SetValue(historyModifier, nativeHistory);
        MethodInfo historyBridge = sys.GetMethod("History", BindingFlags.NonPublic | BindingFlags.Static);
        object historyView = historyBridge.Invoke(null, new[] { historyModifier });
        Type historyType = historyView.GetType();
        MethodInfo tryPrice = historyType.GetMethod("TryPrice");
        object[] readArgs = { 6, 0d };
        if (!(bool)tryPrice.Invoke(historyView, readArgs) || (double)readArgs[1] != (double)(150f * .92f))
            throw new Exception("Core history price conversion mismatch");
        nativeHistory[6] = .93f;
        if (!(bool)tryPrice.Invoke(historyView, readArgs) || (double)readArgs[1] != (double)(150f * .93f))
            throw new Exception("History bridge copied native records");
        modifierType.GetField("PriceMul").SetValue(historyModifier, .93f);
        historyView = historyBridge.Invoke(null, new[] { historyModifier });
        object policyBook = Activator.CreateInstance(mod.GetType("StockMarketAutoInvestor.AssetBook", true));
        object policySettings = Activator.CreateInstance(mod.GetType("StockMarketAutoInvestor.InvestorSettings", true));
        object decision = mod.GetType("StockMarketAutoInvestor.InvestmentPolicy", true).GetMethod("Evaluate").Invoke(null,
            new[] { policyBook, policySettings, historyView, (object)6, (double)(float)modifierType.GetProperty("Price").GetValue(historyModifier), (object)0L, true, null });
        if (decision.GetType().GetField("Action").GetValue(decision).ToString() != "Buy")
            throw new Exception("Compiled policy failed to buy from existing Core history");
        Console.WriteLine("NATIVE_HISTORY_OK actual_Core_dictionary=true float_price_parity=true direct_view=true compiled_policy_buy=true");

        // Validate migration using the actual system validator, without Unity or player data.
        Type investorStateType = mod.GetType("StockMarketAutoInvestor.InvestorState", true);
        foreach (int schema in new[] { 1, 2, 3 })
        {
            object saved = Newtonsoft.Json.JsonConvert.DeserializeObject(
                "{\"Schema\":" + schema + ",\"Enabled\":true,\"Settings\":{\"WindowDays\":30},\"Books\":{\"asset\":{\"Samples\":[{\"Day\":1,\"Price\":999}],\"Observations\":99,\"BuyConfirmations\":8,\"ManagedQuantity\":2,\"ManagedCost\":90,\"ManagedTick\":123,\"LastTradeDay\":1}}}", investorStateType);
            sys.GetProperty("Data", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(investor, saved);
            sys.GetMethod("ValidateLoadedState", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(investor, null);
            string migrated = Newtonsoft.Json.JsonConvert.SerializeObject(saved);
            if (!migrated.Contains("\"ManagedQuantity\":2") || !migrated.Contains("\"ManagedTick\":123")
                || migrated.Contains("Samples") || migrated.Contains("BuyConfirmations")) throw new Exception("Legacy history validation mismatch");
        }
        Console.WriteLine("NATIVE_STATE_OK schemas=1,2,3 managed_lots_preserved=true obsolete_history_ignored=true");
    }

    private static void FanfareProbe(Assembly mod, Assembly game, Type stateType)
    {
        var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Type recordType = mod.GetType("StockMarketAutoInvestor.TradeRecord", true);
            object record = Activator.CreateInstance(recordType);
            recordType.GetField("Quantity").SetValue(record, 1500000);
            recordType.GetField("Price").SetValue(record, 12.5d);
            recordType.GetField("Buy").SetValue(record, true);
            Type fanfareType = mod.GetType("StockMarketAutoInvestor.TradeFanfare", true);
            var format = fanfareType.GetMethod("Format", BindingFlags.Static | BindingFlags.NonPublic);
            string Format() => (string)format.Invoke(null, new object[] { "Silicon", record, 123.45d });
            if (Format() != "Bought 1,500,000 × Silicon\nSpent: $123.45")
                throw new Exception("Fanfare did not use the actual purchase debit");
            recordType.GetField("Buy").SetValue(record, false);
            foreach (var spec in new[] { (Profit: 200d, Text: "<color=#66CC66>Profit: $200</color>"),
                (Profit: -150d, Text: "<color=#FF6666>Loss: $150</color>"), (Profit: 0d, Text: "Break-even: $0") })
            {
                recordType.GetField("RealizedProfit").SetValue(record, spec.Profit);
                if (Format() != "Sold 1,500,000 × Silicon at $12.5 per share\n" + spec.Text)
                    throw new Exception("Fanfare sale result or price mismatch");
            }
            object state = RuntimeHelpers.GetUninitializedObject(stateType);
            var signalField = stateType.GetField("Sig");
            // Only construct the one signal under test, without initializing Unity handlers.
            object signals = RuntimeHelpers.GetUninitializedObject(signalField.FieldType);
            var fanfareField = signals.GetType().GetField("ShowFanfare");
            object signal = Activator.CreateInstance(fanfareField.FieldType, new object[] { "InvestorFanfareProbe", false, true });
            fanfareField.SetValue(signals, signal);
            signalField.SetValue(state, signals);
            int delivered = 0;
            string received = null;
            Action<string> capture = text => { delivered++; received = text; };
            var add = signal.GetType().GetMethods().Single(m => m.Name == "AddListener"
                && m.GetParameters().Length == 1);
            Delegate listener = Delegate.CreateDelegate(add.GetParameters()[0].ParameterType, capture.Target, capture.Method);
            add.Invoke(signal, new object[] { listener });
            var send = fanfareType.GetMethod("Send", BindingFlags.Static | BindingFlags.NonPublic);
            send.Invoke(null, new object[] { state, false, "Silicon", record, 0d });
            if (delivered != 0) throw new Exception("Disabled fanfare emitted a banner");
            send.Invoke(null, new object[] { state, true, "Silicon", record, 0d });
            if (delivered != 1 || received != Format()) throw new Exception("Native fanfare signal delivery mismatch");
            Console.WriteLine("NATIVE_FANFARE_OK cases=6 purchase_debit=true sale_price=true profit_loss_zero=true toggle_gates_signal=true");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = priorCulture; }
    }
}
