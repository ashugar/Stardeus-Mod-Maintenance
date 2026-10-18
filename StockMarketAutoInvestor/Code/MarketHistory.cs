using System;
using System.Collections.Generic;

namespace StockMarketAutoInvestor;

// Read-only view of Core's dictionary. No price copy, observer, or saved history.
public readonly struct MarketHistory
{
    private readonly IReadOnlyDictionary<int, float> entries;
    private readonly float baseValue;
    private readonly float? currentMultiplier;

    public MarketHistory(IReadOnlyDictionary<int, float> entries, float baseValue, float? currentMultiplier = null)
    { this.entries = entries; this.baseValue = baseValue; this.currentMultiplier = currentMultiplier; }

    public bool TryPrice(int day, out double price)
    {
        price = 0;
        if (entries == null || !entries.TryGetValue(day, out float multiplier)) return false;
        // Mono may retain extra precision for float arithmetic until explicitly
        // narrowed. Match Core's stored float price before returning a double.
        price = Numbers.NativePrice(multiplier * baseValue);
        return Numbers.Price(price);
    }

    public bool MatchesCurrentQuote(int day, double quote)
    {
        if (!TryPrice(day, out double recorded) || !Numbers.Price(quote)) return false;
        // Production compares Core's stored multipliers, avoiding a second price
        // calculation as the freshness test. Standalone views use float precision.
        return currentMultiplier.HasValue
            ? entries.TryGetValue(day, out float multiplier) && multiplier == currentMultiplier.Value
            : recorded == Numbers.NativePrice(quote);
    }

    public HistoryRange Range(int day, int window)
    {
        var range = new HistoryRange { StartDay = day, Low = double.MaxValue };
        for (int offset = 0; offset < Math.Min(180, Math.Max(0, window)); offset++)
        {
            long target = (long)day - offset;
            if (target < int.MinValue || !TryPrice((int)target, out double price)) break;
            range.Count++;
            range.StartDay = (int)target;
            range.Low = Math.Min(range.Low, price);
            range.High = Math.Max(range.High, price);
        }
        return range;
    }

    public double Peak(int purchaseDay, int throughDay, double purchasePrice)
    {
        double peak = purchasePrice;
        if (entries == null || throughDay < purchaseDay) return peak;
        foreach (var entry in entries)
        {
            if (entry.Key < purchaseDay || entry.Key > throughDay) continue;
            double price = Numbers.NativePrice(entry.Value * baseValue);
            if (Numbers.Price(price)) peak = Math.Max(peak, price);
        }
        return peak;
    }
}

public struct HistoryRange
{
    public int Count;
    public int StartDay;
    public double Low;
    public double High;
    public double Percent => Count > 0 ? (High - Low) / Low * 100 : 0;
    public double BuyLevel(double percent) => Count > 0 ? Low + (High - Low) * percent / 100 : 0;
}

// Scratch space shared by assets within one daily pass, then released. Monotonic
// queues find all confirmation-day ranges in one bounded traversal (at most 189
// records), including when the confirmation count exceeds the window length.
public sealed class HistoryAnalysis
{
    private readonly HistoryRange[] ranges = new HistoryRange[10];
    private readonly int[] lowDays = new int[190], highDays = new int[190];
    private readonly double[] lows = new double[190], highs = new double[190];

    public HistoryRange At(int offset) => ranges[offset];

    public void Prepare(MarketHistory history, int day, int window, int confirmations)
    {
        window = Math.Max(3, Math.Min(180, window));
        confirmations = Math.Max(1, Math.Min(10, confirmations));
        int lowHead = 0, lowTail = 0, highHead = 0, highTail = 0, count = 0;
        long first = (long)day - window - confirmations + 2;
        for (long target = first; target <= day; target++)
        {
            int offset = (int)((long)day - target);
            if (target < int.MinValue || !history.TryPrice((int)target, out double price))
            {
                lowHead = lowTail = highHead = highTail = count = 0;
                if (offset < confirmations) ranges[offset] = new HistoryRange { StartDay = (int)target };
                continue;
            }
            while (lowHead < lowTail && lowDays[lowHead] <= target - window) lowHead++;
            while (highHead < highTail && highDays[highHead] <= target - window) highHead++;
            while (lowTail > lowHead && lows[lowTail - 1] >= price) lowTail--;
            while (highTail > highHead && highs[highTail - 1] <= price) highTail--;
            lowDays[lowTail] = highDays[highTail] = (int)target;
            lows[lowTail++] = highs[highTail++] = price;
            count = Math.Min(window, count + 1);
            if (offset < confirmations) ranges[offset] = new HistoryRange {
                Count = count, StartDay = (int)(target - count + 1), Low = lows[lowHead], High = highs[highHead]
            };
        }
    }
}
