using System;
using System.Collections.Generic;

namespace StockMarketAutoInvestor;

public enum PurchaseMethod { DivideEvenly, MostProfitable, ByQuantity }

public static class CoreTradeLimits
{
    // Core stores each lot and the total owned count for an asset as Int32.
    public const int MaximumShares = int.MaxValue;

    public static int RemainingCapacity(long owned) => owned < 0 || owned >= MaximumShares
        ? 0 : (int)(MaximumShares - owned);

    public static double ChargeCeiling(int quantity, float price)
    {
        if (quantity < 0 || !Numbers.Price(price)) return double.NaN;
        // Large counts lose integer precision when Core converts them to float.
        // Cover both float32 and extended-precision Mono multiplication modes.
        double count = Math.Max((double)quantity, Numbers.NativePrice(quantity));
        double product = count * price;
        return Math.Max(product, Numbers.NativePrice(product));
    }
}

// Read from Core on demand; never a second persisted portfolio or price history.
public sealed class PortfolioValue
{
    public double Cash;
    public double CostBasis;
    public double MarketValue;
    public bool Complete = true;
    public double UnrealizedProfit => MarketValue - CostBasis;
    public double Total => Cash + MarketValue;
    public bool Valid => Complete && Numbers.Finite(Cash) && Cash >= 0
        && Numbers.Finite(CostBasis) && CostBasis >= 0 && Numbers.Finite(MarketValue) && MarketValue >= 0
        && Numbers.Finite(Total);

    public void AddLot(int quantity, double originalPrice, double currentPrice)
    {
        if (quantity == 0) return;
        if (quantity < 0 || !Numbers.Finite(originalPrice) || originalPrice < 0 || !Numbers.Price(currentPrice))
        { Complete = false; return; }
        CostBasis += quantity * originalPrice;
        MarketValue += quantity * currentPrice;
    }

    public double Reserve(double percent) => Valid && Numbers.Finite(percent) && percent >= 0 && percent <= 100
        ? Total * (percent / 100) : double.NaN;

    public double Spendable(double percent)
    {
        double reserve = Reserve(percent);
        return Numbers.Finite(reserve) ? Math.Max(0, Cash - reserve) : 0;
    }
}

// Used only to convert schema-1 saves. These fields are absent from new saved settings.
public sealed class LegacySpendingSettings
{
    public double CashReserve = 10000;
    public double TotalBudget = 10000;
    public double AssetBudget = 2000;

    public void Apply(InvestorSettings settings, PortfolioValue portfolio, double committed)
    {
        double available = portfolio.Valid && Numbers.Finite(CashReserve) && Numbers.Finite(TotalBudget)
            && Numbers.Finite(committed) ? Math.Max(0, Math.Min(portfolio.Cash - Math.Max(0, CashReserve),
                Math.Max(0, TotalBudget) - Math.Max(0, committed))) : 0;
        // Round toward a larger reserve; upgrading must not unexpectedly unlock the wallet.
        double percent = portfolio.Valid && portfolio.Total > 0
            ? Math.Ceiling(Math.Max(0, portfolio.Cash - available) / portfolio.Total * 10000) / 100 : 100;
        settings.CashReservePercent = Math.Max(0, Math.Min(100, percent));
        settings.MaximumSharePrice = Numbers.Finite(AssetBudget) ? Math.Max(0, AssetBudget) : 0;
        settings.PurchaseMethod = PurchaseMethod.DivideEvenly;
        settings.Normalize();
    }
}

public sealed class PurchaseCandidate
{
    public string Id;
    public float Price;
    public double Upside;
}

public sealed class PlannedPurchase
{
    public PurchaseCandidate Candidate;
    public int Quantity;
    public double Charge => CoreTradeLimits.ChargeCeiling(Quantity, Candidate.Price);
}

public static class PurchasePlan
{
    public static int Quantity(double budget, float price)
    {
        if (!Numbers.Finite(budget) || !Numbers.Price(price) || budget < price) return 0;
        int low = 0, high = (int)Math.Min(CoreTradeLimits.MaximumShares, Math.Floor(budget / price));
        // At most 31 steps, even for billion-share orders or rounded charges.
        while (low < high)
        {
            int count = low + (int)(((long)high - low + 1) / 2);
            double charge = CoreTradeLimits.ChargeCeiling(count, price);
            if (Numbers.Finite(charge) && charge <= budget) low = count;
            else high = count - 1;
        }
        return low;
    }

    public static List<PlannedPurchase> Build(InvestorSettings settings, PortfolioValue portfolio,
        IEnumerable<PurchaseCandidate> candidates)
    {
        var plan = new List<PlannedPurchase>();
        double available = portfolio.Spendable(settings.CashReservePercent);
        var ranked = new List<PurchaseCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
            if (candidate != null && candidate.Id != null && Numbers.Price(candidate.Price)
                && Numbers.Finite(candidate.Upside) && candidate.Price <= settings.MaximumSharePrice
                && candidate.Price <= available && seen.Add(candidate.Id)) ranked.Add(candidate);
        ranked.Sort((a, b) => {
            int comparison = settings.PurchaseMethod == PurchaseMethod.ByQuantity
                ? a.Price.CompareTo(b.Price) : b.Upside.CompareTo(a.Upside);
            if (comparison == 0) comparison = b.Upside.CompareTo(a.Upside);
            return comparison != 0 ? comparison : string.CompareOrdinal(a.Id, b.Id);
        });
        int limit = Math.Max(0, Math.Min(20, settings.MaximumTradesPerDay));
        if (settings.PurchaseMethod == PurchaseMethod.DivideEvenly)
        {
            // Find the largest affordable spread; do not let an unaffordable
            // high-ranked share block cheaper qualifying alternatives.
            for (int count = Math.Min(limit, ranked.Count); count > 0; count--)
            {
                double allowance = available / count;
                var selected = new List<PurchaseCandidate>();
                foreach (var candidate in ranked)
                    if (Quantity(allowance, candidate.Price) > 0)
                    { selected.Add(candidate); if (selected.Count == count) break; }
                if (selected.Count != count) continue;
                double remaining = available;
                foreach (var candidate in selected)
                {
                    int quantity = Quantity(Math.Min(allowance, remaining), candidate.Price);
                    if (quantity <= 0) continue;
                    var purchase = new PlannedPurchase { Candidate = candidate, Quantity = quantity };
                    plan.Add(purchase);
                    remaining = Math.Max(0, remaining - purchase.Charge);
                }
                break;
            }
        }
        else
        {
            foreach (var candidate in ranked)
            {
                if (plan.Count >= limit) break;
                int quantity = Quantity(available, candidate.Price);
                if (quantity == 0) continue;
                var purchase = new PlannedPurchase { Candidate = candidate, Quantity = quantity };
                plan.Add(purchase);
                available = Math.Max(0, available - purchase.Charge);
            }
        }
        return plan;
    }
}
