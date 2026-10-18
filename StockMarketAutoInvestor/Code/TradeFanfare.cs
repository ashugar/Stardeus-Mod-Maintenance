using System;
using Game.Data;
using Game.Data.Economy;
using KL.Utils;

namespace StockMarketAutoInvestor;

internal static class TradeFanfare
{
    internal static void Send(GameState state, bool enabled, string assetName, TradeRecord trade, double spent)
    {
        if (!enabled) return;
        // A notification failure must not turn a completed trade into a failed order.
        try { state?.Sig?.ShowFanfare.Send(Format(assetName, trade, spent)); }
        catch (Exception ex) { D.LogEx(ex, "StockMarketAutoInvestor: trade completed, but its fanfare could not be shown."); }
    }

    internal static string Format(string assetName, TradeRecord trade, double spent)
    {
        string shares = $"{trade.Quantity:N0} × {assetName}";
        if (trade.Buy)
            return "Bought " + shares + "\nSpent: " + Wallet.ToValueString(spent, false);

        string result;
        if (!trade.RealizedProfit.HasValue) result = "Profit/loss unavailable";
        else
        {
            double profit = trade.RealizedProfit.Value;
            string amount = Wallet.ToValueString(Math.Abs(profit), false);
            result = profit > 0 ? "<color=#66CC66>Profit: " + amount + "</color>"
                : profit < 0 ? "<color=#FF6666>Loss: " + amount + "</color>"
                : "Break-even: " + amount;
        }
        return "Sold " + shares + " at " + Wallet.ToValueString(trade.Price, false) + " per share\n" + result;
    }
}
