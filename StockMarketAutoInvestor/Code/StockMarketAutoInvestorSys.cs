using System;
using System.Collections.Generic;
using System.Linq;
using AutoUtilitiesModFramework;
using Game;
using Game.CodeGen;
using Game.Components;
using Game.Constants;
using Game.Data;
using Game.Data.Economy;
using Game.Systems;
using Game.Utils;
using KL.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StockMarketAutoInvestor;

public sealed class StockMarketAutoInvestorSys : GameSystem, ISaveable
{
    public const string SysId = "StockMarketAutoInvestorSys";
    public const string ConsumerId = "StockMarketAutoInvestor";
    public const string RequiredUtilitiesVersion = "1.4.0";
    public override string Id => SysId;
    public override bool SkipInSandbox => false;
    internal InvestorState Data { get; private set; } = new();
    internal GameState State => loadedState;
    internal int Revision { get; private set; }
    internal bool Ready => loadedState?.Market?.Modifiers != null;
    internal bool MarketUnlocked => Ready && Unlocked(DefIdsH.ResearchEconomyWallet)
        && Unlocked(DefIdsH.ResearchEconomyStockMarket);
    internal bool TradingUnlocked => MarketUnlocked && Unlocked(DefIdsH.ResearchEconomyStockTrading);
    internal bool InsightsUnlocked => MarketUnlocked && Unlocked(DefIdsH.ResearchEconomyMarketInsights);
    internal bool PredictionUnlocked => MarketUnlocked && Unlocked(DefIdsH.ResearchEconomyMarketPrediction);
    internal int ResearchMask => (MarketUnlocked ? 1 : 0) | (TradingUnlocked ? 2 : 0)
        | (InsightsUnlocked ? 4 : 0) | (PredictionUnlocked ? 8 : 0)
        | (Unlocked(DefIdsH.ResearchEconomyBlackMarket) ? 16 : 0);
    private GameState loadedState;
    private bool listening;
    private bool compatible;
    private InvestorUi ui;
    private LegacySpendingSettings legacySpending;

    [SelfInit]
    private static void Register() => GameSystems.Register(SysId, () => new StockMarketAutoInvestorSys());

    protected override void OnInitialize()
    {
        compatible = AutoUtilitiesApi.TryRegisterConsumer(ConsumerId, "Stock Market Auto-Investor",
            typeof(StockMarketAutoInvestorSys).Assembly.GetName().Version.ToString(3), RequiredUtilitiesVersion);
        if (compatible) ui = new InvestorUi(this);
        The.SysSig.AfterLoadSaveState.AddListener(OnLoaded);
        D.Log("StockMarketAutoInvestor 0.1.0: initialized; AUMF {0}; automatic trading starts off in new saves.", RequiredUtilitiesVersion);
    }

    private void OnLoaded(GameState state)
    {
        if (state != S) return;
        Detach();
        ui?.Close();
        loadedState = state;
        if (!Ready) return;
        if (Data.Schema == 1)
        {
            (legacySpending ?? new LegacySpendingSettings()).Apply(Data.Settings, PortfolioTotals(), CommittedCapital());
            Data.Schema = 2;
            legacySpending = null;
            Data.LastResult = "Spending settings converted; review Cash to reserve (%) and Maximum share price in Settings.";
        }
        else if (Data.Schema < 3)
            Data.LastResult = "Using Core price history; decisions resume on the next market update.";
        Data.Schema = 3;
        if (!compatible) Data.Enabled = false;
        // Loading never places a trade, even if Core already has a qualifying streak.
        Data.ResetAnalysis(state.Market.Day);
        SyncListener();
        Changed();
    }

    private bool Unlocked(int id) => loadedState?.Research != null && loadedState.Research.IsUnlocked(id);
    internal bool Eligible(MarketItemModifier mod) => MarketUnlocked && mod?.Item != null
        && (!mod.Item.IsBlackMarket || Unlocked(DefIdsH.ResearchEconomyBlackMarket));
    internal MarketItemModifier Find(string id) => Ready && id != null
        ? loadedState.Market.Find(Hashes.S(id)) : null;

    internal void SetEnabled(bool enabled)
    {
        if (!Ready || !compatible) return;
        Data.SetEnabled(enabled, loadedState.Market.Day);
        SyncListener();
        Changed();
    }

    internal void SetTradeFanfare(bool enabled)
    {
        Data.Settings.ShowTradeFanfare = enabled;
        Changed();
    }

    internal void SettingsChanged()
    {
        Data.Settings.Normalize();
        // Re-evaluate Core history with the new thresholds on the next daily update.
        foreach (AssetBook b in Data.Books.Values)
        {
            b.ResetAnalysis();
            b.Status = "Settings changed; waiting for the next market update";
        }
        Changed();
    }

    private void SyncListener()
    {
        bool wanted = compatible && Ready && Data.Enabled;
        if (wanted == listening) return;
        if (wanted) loadedState.Sig.MarketDayChanged.AddListener(OnMarketDay);
        else loadedState?.Sig.MarketDayChanged.RemoveListener(OnMarketDay);
        listening = wanted;
    }

    private void Detach()
    {
        if (listening) loadedState?.Sig.MarketDayChanged.RemoveListener(OnMarketDay);
        listening = false;
        loadedState = null;
    }

    private void OnMarketDay()
    {
        if (!Data.Enabled || !Ready) return;
        try
        {
            int day = loadedState.Market.Day;
            if (!Data.BeginMarketDay(day)) return;
            if (!MarketUnlocked)
            {
                Data.LastResult = "Stock Market research required; investment analysis paused.";
                Changed();
                return;
            }

            var proposals = new List<(MarketItemModifier Mod, InvestmentDecision Decision)>();
            var holdings = IndexHoldings();
            var analysis = new HistoryAnalysis();
            foreach (MarketItemModifier mod in loadedState.Market.Modifiers.Values)
            {
                if (!Eligible(mod)) continue;
                AssetBook book = Data.Book(mod.Id);
                holdings.TryGetValue(mod.IdH, out Holding holding);
                Reconcile(book, holding);
                InvestmentDecision decision = InvestmentPolicy.Evaluate(book, Data.Settings, History(mod), day,
                    mod.Price, holding?.Quantity ?? 0, TradingUnlocked, analysis);
                book.DecisionDay = day;
                book.ApprovedAction = decision.Action;
                if (decision.Action != InvestmentAction.Wait) proposals.Add((mod, decision));
            }
            int sales = 0, purchases = 0;
            var candidates = new List<PurchaseCandidate>();
            foreach (var proposal in proposals)
            {
                AssetBook book = Data.Book(proposal.Mod.Id);
                if (proposal.Decision.Action == InvestmentAction.Sell)
                {
                    if (Trade(proposal.Mod.Id, book.ManagedQuantity, false, true, proposal.Decision.Reason, out string result)) sales++;
                    else book.Status = result;
                    continue;
                }
                book.Status = proposal.Mod.Price > Data.Settings.MaximumSharePrice
                    ? "Price above maximum share price" : "Purchase qualified; waiting for available cash or a selection slot";
                candidates.Add(new PurchaseCandidate { Id = proposal.Mod.Id, Price = proposal.Mod.Price, Upside = proposal.Decision.Score });
            }
            var portfolio = PortfolioTotals();
            var plan = PurchasePlan.Build(Data.Settings, portfolio, candidates);
            foreach (var purchase in plan)
            {
                AssetBook book = Data.Book(purchase.Candidate.Id);
                var mod = Find(purchase.Candidate.Id);
                if (mod == null) continue;
                int quantity = Math.Min(purchase.Quantity, InvestmentPolicy.BuyQuantity(Data.Settings, PortfolioTotals(), mod.Price));
                if (quantity <= 0)
                { book.Status = "Purchase qualified; cash reserve or share-price limit prevents buying"; continue; }
                if (Trade(mod.Id, quantity, true, true, "Confirmed recovery; " + Data.Settings.PurchaseMethod, out string result)) purchases++;
                else book.Status = result;
            }
            if (!portfolio.Valid)
                foreach (var candidate in candidates) Data.Book(candidate.Id).Status = "Portfolio value unavailable; purchases paused";
            Data.LastResult = $"Market day {day}: {purchases} stock purchase(s), {sales} sale(s).";
            Changed();
        }
        catch (Exception ex)
        {
            Data.SetEnabled(false, loadedState.Market.Day);
            Data.LastResult = "Auto-Invest stopped after an error. See Player.log.";
            SyncListener();
            D.LogEx(ex, "StockMarketAutoInvestor: market update failed; automation disabled.");
            Changed();
        }
    }

    internal static MarketHistory History(MarketItemModifier mod) => new(mod?.History, mod?.Item.Value ?? 0, mod?.PriceMul);

    internal long Owned(int id)
    {
        long quantity = 0;
        var positions = loadedState?.Portfolio?.Positions;
        if (positions == null) return 0;
        foreach (MarketPosition p in positions) if (p.IdH == id && p.Position > 0) quantity += p.Position;
        return quantity;
    }

    internal void Holdings(int id, out long quantity, out double cost, out double average)
    {
        quantity = 0;
        cost = average = 0;
        var positions = loadedState?.Portfolio?.Positions;
        if (positions == null) return;
        foreach (MarketPosition p in positions)
        {
            if (p.IdH != id || p.Position <= 0) continue;
            quantity += p.Position;
            cost += (double)p.Cost * p.Position;
        }
        if (quantity > 0) average = cost / quantity;
    }

    private double CommittedCapital()
    {
        double total = 0;
        foreach (AssetBook b in Data.Books.Values) total += b.ManagedQuantity * b.ManagedCost;
        return total;
    }

    internal PortfolioValue PortfolioTotals()
    {
        var value = new PortfolioValue { Cash = loadedState?.Wallet?.Amount ?? double.NaN };
        var positions = loadedState?.Portfolio?.Positions;
        if (positions == null) return value;
        foreach (MarketPosition p in positions)
        {
            var mod = loadedState.Market.Find(p.IdH);
            value.AddLot(p.Position, p.Cost, mod?.Price ?? double.NaN);
        }
        return value;
    }

    private sealed class Holding
    {
        internal long Quantity;
        internal int Lots;
        internal MarketPosition Last;
    }

    private Dictionary<int, Holding> IndexHoldings()
    {
        var result = new Dictionary<int, Holding>();
        var positions = loadedState?.Portfolio?.Positions;
        if (positions == null) return result;
        foreach (var p in positions)
        {
            if (p.Position <= 0) continue;
            if (!result.TryGetValue(p.IdH, out Holding holding)) result[p.IdH] = holding = new Holding();
            holding.Quantity += p.Position;
            holding.Lots++;
            holding.Last = p;
        }
        return result;
    }

    private static void Reconcile(AssetBook book, Holding holding)
    {
        if (book.ManagedQuantity <= 0) return;
        var p = holding?.Last;
        if (holding == null || holding.Lots != 1 || p.Tick != book.ManagedTick
            || p.Position != book.ManagedQuantity || (double)p.Cost != book.ManagedCost) book.ManualConflict = true;
    }

    private void Reconcile(MarketItemModifier mod, AssetBook book)
    {
        if (book.ManagedQuantity <= 0) return;
        int count = 0;
        bool match = false;
        foreach (MarketPosition p in loadedState.Portfolio?.Positions ?? new List<MarketPosition>())
        {
            if (p.IdH != mod.IdH || p.Position <= 0) continue;
            count++;
            match = p.Tick == book.ManagedTick && p.Position == book.ManagedQuantity
                && (double)p.Cost == book.ManagedCost;
        }
        if (count != 1 || !match) book.ManualConflict = true;
    }

    internal bool Trade(string assetId, int amount, bool buy, bool automatic, string reason, out string result)
    {
        result = "Trade unavailable";
        MarketItemModifier mod = Find(assetId);
        if (!TradingUnlocked || !Eligible(mod)) { result = "Required trading research is locked."; return false; }
        if (amount <= 0)
        { result = "Enter a whole quantity from 1 to 2,147,483,647."; return false; }
        if (!Numbers.Price(mod.Price)) { result = "Current market price is invalid."; return false; }
        if (!Numbers.Finite(loadedState.Wallet.Amount)) { result = "Wallet balance is invalid."; return false; }
        AssetBook book = Data.Book(assetId);
        long before = Owned(mod.IdH);
        if (automatic)
        {
            if (!Data.Enabled || book.DecisionDay != loadedState.Market.Day
                || book.ApprovedAction != (buy ? InvestmentAction.Buy : InvestmentAction.Sell))
            { result = "A qualifying decision on the current market update is required."; return false; }
            Reconcile(mod, book);
            if (book.ManualConflict || (buy && (before != 0 || book.ManagedQuantity > 0)))
            { result = "Manual holdings excluded or management paused."; return false; }
            if (!buy && before != book.ManagedQuantity) { result = "Managed holdings changed."; return false; }
            if (buy && amount > InvestmentPolicy.BuyQuantity(Data.Settings, PortfolioTotals(), mod.Price))
            { result = "Cash reserve or maximum share price prevents this purchase."; return false; }
        }
        float price = mod.Price;
        double walletBefore = loadedState.Wallet.Amount;
        double tradedCostBasis = 0;
        double saleProceeds = 0;
        if (buy)
        {
            if (amount > CoreTradeLimits.RemainingCapacity(before))
            { result = "Core can store at most 2,147,483,647 shares of one stock. Reduce the purchase quantity."; return false; }
            double charge = CoreTradeLimits.ChargeCeiling(amount, price);
            if (!Numbers.Finite(charge) || charge > loadedState.Wallet.Amount)
            { result = "Insufficient cash for this purchase."; return false; }
            loadedState.Portfolio ??= new MarketPortfolio();
            loadedState.Portfolio.Buy(mod, amount);
            tradedCostBasis = (double)price * amount;
        }
        else
        {
            if (amount > before) { result = "Not enough shares owned."; return false; }
            int remaining = amount;
            while (remaining > 0)
            {
                MarketPosition latest = loadedState.Portfolio.Positions.LastOrDefault(p => p.IdH == mod.IdH && p.Position > 0);
                int batch = NativeSalePlan.NextBatch(remaining, latest?.Position ?? 0);
                if (batch <= 0) break;
                long batchBefore = Owned(mod.IdH);
                tradedCostBasis += (double)latest.Cost * batch;
                loadedState.Portfolio.Sell(mod, batch);
                saleProceeds += batch * price;
                if (batchBefore - Owned(mod.IdH) != batch)
                    throw new InvalidOperationException("Native sale quantity did not match the requested batch.");
                remaining -= batch;
            }
        }
        long expected = before + (buy ? amount : -amount);
        if (Owned(mod.IdH) != expected) { result = "Native trade did not complete; check holdings."; return false; }
        if (automatic)
        {
            book.ManagedQuantity = buy ? amount : 0;
            book.ManagedCost = buy ? price : 0;
            book.ManagedTick = buy ? loadedState.Portfolio.Positions.Last().Tick : 0;
            book.ResetAnalysis();
        }
        else book.ManualConflict = true;
        book.LastTradeDay = loadedState.Market.Day;
        result = $"{(buy ? "Bought" : "Sold")} {amount:N0} × {mod.Item.Name} at {Wallet.ToValueString(price)}.";
        book.Status = automatic ? result : "Manual activity: automation paused for this asset";
        var record = new TradeRecord { Asset = assetId, Day = loadedState.Market.Day, Quantity = amount,
            Price = price, Buy = buy, Automatic = automatic, Reason = reason, CostBasis = tradedCostBasis,
            RealizedProfit = buy ? (double?)null : saleProceeds - tradedCostBasis };
        Data.AddTrade(record);
        Data.LastResult = result;
        Revision++;
        // Publish only after the full order succeeds and its durable record is complete.
        TradeFanfare.Send(loadedState, Data.Settings.ShowTradeFanfare, mod.Item.Name, record,
            buy ? Math.Max(0, walletBefore - loadedState.Wallet.Amount) : 0);
        return true;
    }

    internal void ManualTrade(string assetId, int amount, bool buy)
    {
        try { Trade(assetId, amount, buy, false, "Manual trade", out string result); Data.LastResult = result; }
        catch (Exception ex)
        {
            SetEnabled(false);
            Data.LastResult = "Trade interrupted; inspect holdings. See Player.log.";
            D.LogEx(ex, "StockMarketAutoInvestor: manual trade failed.");
        }
        Changed();
    }

    internal void ReleaseManagement(string id)
    {
        AssetBook b = Data.Book(id);
        b.ManagedQuantity = 0;
        b.ManagedCost = 0;
        b.ManagedTick = 0;
        b.ManualConflict = false;
        b.ResetAnalysis();
        b.Status = "Existing holdings remain manual; decisions resume on the next market update";
        Changed();
    }

    private void Changed() { Revision++; ui?.Refresh(); }

    public ComponentData Save()
    {
        ComponentData data = ComponentData.Create(SysId);
        data.SetString("State", JsonConvert.SerializeObject(Data));
        return data;
    }

    public void Load(ComponentData data)
    {
        Detach();
        legacySpending = null;
        Data = new InvestorState();
        if (data == null) return;
        try
        {
            string json = data.GetString("State");
            if (string.IsNullOrEmpty(json)) return;
            Data = JsonConvert.DeserializeObject<InvestorState>(json, new JsonSerializerSettings
                { TypeNameHandling = TypeNameHandling.None, MaxDepth = 32 }) ?? new InvestorState();
            if (Data.Schema == 1)
                legacySpending = JObject.Parse(json)["Settings"]?.ToObject<LegacySpendingSettings>() ?? new LegacySpendingSettings();
            ValidateLoadedState();
        }
        catch (Exception ex)
        {
            Data = new InvestorState { LastResult = "Saved investor data could not be read; Auto-Invest is off." };
            D.LogEx(ex, "StockMarketAutoInvestor: saved state rejected.");
        }
    }

    private void ValidateLoadedState()
    {
        if (Data.Schema < 1 || Data.Schema > 3) throw new InvalidOperationException("Unsupported investor save schema.");
        Data.Settings ??= new InvestorSettings();
        Data.Settings.Normalize();
        Data.Books ??= new Dictionary<string, AssetBook>(StringComparer.Ordinal);
        Data.Trades ??= new List<TradeRecord>();
        Data.Trades = Data.Trades.Where(t => t != null && t.Asset != null && t.Quantity > 0 && Numbers.Price(t.Price)).ToList();
        foreach (var trade in Data.Trades)
        {
            if (trade.CostBasis.HasValue && (!Numbers.Finite(trade.CostBasis.Value) || trade.CostBasis.Value < 0)) trade.CostBasis = null;
            if (trade.RealizedProfit.HasValue && !Numbers.Finite(trade.RealizedProfit.Value)) trade.RealizedProfit = null;
        }
        if (Data.Trades.Count > 200) Data.Trades.RemoveRange(0, Data.Trades.Count - 200);
        foreach (string id in Data.Books.Keys.ToList())
        {
            AssetBook b = Data.Books[id];
            if (b == null || string.IsNullOrEmpty(id)) { Data.Books.Remove(id); continue; }
            b.ResetAnalysis();
            if (b.ManagedQuantity < 0 || !Numbers.Finite(b.ManagedCost) || b.ManagedCost < 0)
            { b.ManagedQuantity = 0; b.ManagedCost = 0; b.ManualConflict = true; }
        }
    }

    public override void OnDeactivate() => Unload();
    public override void Unload()
    {
        The.SysSig.AfterLoadSaveState.RemoveListener(OnLoaded);
        Detach();
        ui?.Dispose();
        ui = null;
    }
}
