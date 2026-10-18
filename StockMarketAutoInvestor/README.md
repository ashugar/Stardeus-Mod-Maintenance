# Stock Market Auto-Investor

Version 0.1.0 is an initial test build for Stardeus 0.15.0. This revision uses Core history and AUMF's corrected pooled inputs. Requires Auto Utilities Mod Framework (AUMF) 1.4.0 with its 2026-09-11 input callback repair. The framework remains a separate dependency.

## Opening the mod

Enable the mod and AUMF in Stardeus, restart the game, then load a save. Click the graph icon in the top bar. The mod has its own resizable Investment and Settings panel. The native Stock Market window remains independent.

The Investment tab places a graph beside a scrolling asset list. Hovering changes the graph. Clicking an asset opens a separate details window with research-appropriate trends, asset information, and owned shares. Buy and Sell use the quantity entered in that window. Hovering another asset does not change the manual trade target.

Each stock row shows its current price, daily change when Market Insights is unlocked, and the potential profit/loss percentage of your owned shares. The last percentage compares their current value with the combined original purchase cost of the shares still owned. For example, $1,000 of shares now worth $1,100 shows **+10.0%** in green; a loss is red and zero is neutral. A dash means no shares are owned or a valid cost or quote is unavailable. Hover the row for an explanation. The values update with the existing panel refreshes and market updates.

The list supports name search, group/name/price ordering, and filters for owned assets, stocks, commodities, and managed positions. Select the desired filter or order directly from its dropdown. Trade history shows the most recent 25 records; the save retains up to 200. Sale tooltips show **Profit** in green or **Loss** in red, with the amount gained or lost. An exact zero shows **Break-even** in the normal text colour. Records without a known sale result omit that line.

The Settings tab has one Auto-Invest toggle row. Each numeric setting has its label and editable value on the same line. **Cash to reserve (%)** also shows the calculated Starcredit amount between the label and input. That amount updates as you type and when cash or holdings change while the panel is open. Hover over the label or input for its explanation; press Enter or leave the field to apply a value.

Search, manual quantity, and every numeric setting use AUMF's corrected `AddInput` helper. AUMF removes its end-edit listener and ignores old callbacks when panel content is disposed. The temporary mod-owned input binding and unpooled control workaround have been removed. Restart after installing this build and the repaired framework. Review values affected by earlier cross-field edits; the mod cannot infer their intended values. Search filters the visible list only and does not control investment decisions.

## Trade fanfare

Completed automatic trades and manual trades made through this mod announce their results in Core's normal on-screen banner, even when the investment panel is closed. A purchase shows the stock name, quantity and total spent, taken from the wallet debit. A sale shows the stock name, quantity, price per share and the same realized profit/loss as Trade history. Profit is green, loss is red and a zero result shows Break-even. Amounts use full Starcredit values rather than percentages.

**Trade fanfare** in Settings is on by default and is saved with the game. Switching it off hides future announcements without changing Auto-Invest or its decisions. Each completed order produces one announcement, including a sale across multiple purchase lots. Failed trades and loading old trade history do not announce. This applies to trades made through the mod; it does not monitor manual trades made in Core's separate Stock Market window.

Core controls the normal banner sound and approximately ten-second display time. It stacks recent messages, shows up to three by default and exposes the active messages while hovered. Busy market updates can therefore produce several announcements together. Trade history retains the records after banners expire. No mod-owned timer or message queue runs in the background.

## Auto-Invest

Auto-Invest starts **off** in new saves. While off, there is no background market listener, timer, collection, analysis, or automated trading. Opening the interface still performs normal display work, including reading Core's existing market history.

Enabling uses Core's existing daily price history, including records from before Auto-Invest was enabled. There is no new collection period. Decisions and trades begin on the next normal market update. Turning Auto-Invest off clears cached decisions without selling existing investments. Core continues recording prices independently. Re-enabling reads its latest records and does not replay missed trades.

Core's default market interval is 24 in-game hours. The mod follows the market-update event and does not change its frequency. Repeated notifications and rewound days cannot trade. If an update was missed, only the newly received day's decision is considered. Missing or invalid historical prices break continuity; the mod uses the most recent consecutive valid records, up to the selected window, with at least three prices required.

Settings, trade records, and managed purchase identities are stored inside the game's save through the mod's own system data. Core owns the price history. Loading clears cached decisions and never executes a trade; an enabled session resumes on the next daily update.

The earlier **Waiting for current Core price history** message could appear even with a full graph because an exact comparison rejected tiny differences in number precision. This correction checks Core's stored current and daily multipliers directly and explicitly rounds historical price calculations to the game's float precision. Missing daily records, invalid quotes, and genuinely changed quotes have separate status messages. It does not introduce a collection wait or change any trading threshold.

## Default rules

| Setting | Initial value |
|---|---:|
| Recent analysis window | 60 market days |
| Consecutive confirmation updates | 2 |
| Buy range ceiling | 25% |
| Minimum observed price range | 2% of the observed low |
| Minimum profit for a normal sale | 10% |
| Required decline from the observed holding peak | 3% |
| Sell to limit losses | Off |
| Price drop before selling | 15% |
| Cash to reserve | 90% of cash plus current share value |
| Maximum share price | 2,000 Starcreds |
| Purchase method | Divide Evenly |
| Asset trade cooldown | 3 market days |
| Maximum different stocks bought per market update | 4 |

These are initial settings for validation, not profitability measurements.

For a purchase, the current price must be in the lower part of its recorded range and rise on consecutive confirmation updates. A nearly flat range is excluded. One automated position is allowed per asset, and the mod buys only when no shares of that asset are already owned. The purchase method determines selection and allocation. Whole-share quantities respect the percentage cash reserve and maximum price per share.

With **Confirmation updates** set to 2, the price must rise from the previous day on two consecutive daily updates and stay at or below that day's cyan buy ceiling each time. Two falling or flat days below the line do not qualify. A flat or falling price, or a rise above the ceiling, resets the count. This looks for a recovery; it cannot guarantee the lowest price. The asset's Auto-Invest row reports whether it is waiting for a rise, above the ceiling, below the minimum observed range, or confirming a rise. Hover that row for the purchase explanation. The Settings tooltips for **Buy range ceiling (%)** and **Confirmation updates** explain the same rule.

The cyan line is calculated from Core prices within the analysis window. With a low of $80 and a high of $100, a 25% ceiling is $85. New prices or older prices leaving the window can move it. The horizontal line displays today's ceiling across the window. Each confirmation day uses the window ending on that day, so later prices cannot alter an earlier check. Changing the analysis window updates the graph immediately; decisions use the new setting on the next market update. Existing consecutive recorded days count without waiting for new days after enabling.

A normal sale requires both the profit threshold and a confirmed decline from the highest Core price recorded since purchase. Peak reconstruction includes days when Auto-Invest was off and excludes prices before purchase or after the day being checked. Sale confirmations must occur after purchase and satisfy the trade cooldown. A loss-limit sale is possible only when enabled in Settings. Historical confirmations can qualify either sale on the next daily update after enabling; toggling and loading never execute trades themselves.

**Sell to limit losses** allows Auto-Invest to sell shares it bought for less than it paid, to try to avoid a larger loss. For example, with **Price drop before selling (%)** set to 15, a share bought at $100 can qualify for sale at $85 or less. The drop is measured from the purchase price. Auto-Invest must also be on. Daily confirmation checks and the waiting time between trades still apply; existing Core records count. The price can fall further before the sale, so 15% is not a guaranteed maximum loss. When this option is off, the mod keeps the shares until the profit-sale rules are met or the player sells them manually. Shares bought manually remain excluded from automation.

The **Price drop before selling (%)** row appears directly below **Sell to limit losses** only when that option is on. Turning it off hides the row and retains the saved percentage for the next time it is enabled.

Automated sales are considered before new purchases and do not consume purchase slots. Changing settings clears cached decisions. The next daily update checks Core history against the new settings, including historical confirmation days.

## Cash reserve and purchase methods

The reserve is a percentage of **wallet cash + current market value of all owned shares**. Core already stores each purchase lot's original price and quantity. The mod reads those lots and Core's current quotes to calculate original purchase cost, current value, and the difference. It does not store a second holdings ledger or include ship, equipment, or inventory value in this calculation. Manual holdings count toward the reserve calculation while remaining excluded from automated trading.

For example, $8,000 cash and $2,000 in shares give a $10,000 total. At 30%, the cash reserve is $3,000 and $5,000 can be invested. The current value already includes any gain or loss; original purchase cost is not added a second time. If the reserve is larger than the wallet balance, purchases wait. The reserve does not force sales. Zero percent allows available cash to be invested; 100% prevents new purchases. A missing or invalid quote pauses purchases until all holdings can be valued.

**Maximum share price** is the most Auto-Invest may pay for one share. At a $1,000 limit, an $1,800 share is excluded and an $800 share can qualify. This is not a limit on total spending for that stock. The previous total investment and per-purchase budget controls have been removed.

| Purchase method | Selection and spending |
|---|---|
| Divide Evenly | Selects qualifying stocks by observed percentage upside and splits available cash equally among as many as fit the daily limit and their equal allocations. Whole-share rounding can leave cash unspent. |
| Purchase Most Profitable | Starts with the greatest potential percentage gain back to the highest price in the selected Core history window, spending as much as allowed on that stock. |
| Purchase By Quantity | Starts with the cheapest qualifying shares, spending as much as allowed to buy the largest number. |

The mod permits purchases above one million shares. Core's native Buy interface imposes that order limit; the portfolio trade methods accept larger quantities. Auto-Invest and this panel's manual trades use those methods directly. Core stores at most **2,147,483,647 shares per stock**, including existing holdings. The native market interface is unchanged.

The last two methods move to the next stock if money remains, including after reaching that storage limit. Selections are reconsidered each market day; the mod does not queue future orders. The observed high is a ranking reference, not a forecast or promised sale price. Every method still requires valid recent Core history and the recovery conditions. Existing holdings are not enlarged: the current purchase rule still requires no shares of the asset to be owned.

**Maximum trades per market update** limits the number of different stocks bought on one daily market update, not the number of individual shares. For example, 3 permits purchases in up to three stocks. A method may spend all available money on fewer stocks. Sales are separate. Available cash and the percentage reserve are checked again before each purchase using current holdings and a conservative bound for Core's rounded trade charge. Quantity calculations take at most 31 steps per check, regardless of order size. Whole-share rounding, storage limits or a lack of qualifying stocks may leave cash above the reserve unspent.

The asset detail shows original purchase cost, current value, and potential profit/loss separately. The potential profit/loss value is green when positive, red when negative, and the normal text colour when zero. New trade-history entries also retain the original cost of shares traded and the profit/loss on a completed sale. Older entries with unknown purchase costs remain unknown.

### Existing saves

Schema-1 and schema-2 investor data convert to schema 3 after Core loads the wallet and market. Old private price samples, observation counts, cached peaks, and the minimum-observation setting are ignored and omitted from subsequent saves. Enabled state, current settings, trade history, and managed purchase identities remain.

For schema-1 data only, the old cash and total-investment limits convert to a reserve percentage that allows no more immediate spending than the old remaining investment allowance. The percentage rounds upward to two decimals. A small old budget with a very large wallet can become a 100% reserve: review **Cash to reserve (%)** and choose the percentage you intend to keep.

For that older spending format, the per-asset amount becomes **Maximum share price**, the daily count is retained, and the initial purchase method is **Divide Evenly**. Schema-2 spending settings retain their values. Obsolete absolute-budget fields are omitted from subsequent saves.

## Manual holdings and execution

Manual Buy/Sell remains available while Auto-Invest is off or waiting for trading conditions, after the normal research unlocks. Automation budgets do not constrain these explicit manual trades. Quantity must be from 1 to 2,147,483,647 and cannot exceed available funds or shares. New purchases must also fit within Core's total storage limit for that stock.

Existing manual holdings are excluded from automated trading. Manual trades through this panel pause automated management for that asset. Changes made through the native market are detected before the next automated decision by checking the managed purchase lot against actual holdings. The mod does not adopt or sell a changed or ambiguous lot.

“Leave holdings manual” relinquishes the mod's management record without selling shares. Those shares remain excluded from automation. After the asset has no shares remaining, a later automated purchase is possible when Core history and the other rules qualify it.

Paused managed positions continue to contribute their current value to the reserve calculation. Releasing them to manual control does not remove their value from that calculation.

Trades use Core's `MarketPortfolio.Buy` and `MarketPortfolio.Sell`. The installed Core sale loop can exceed a partial request when it crosses purchase lots. This mod avoids that path by making each native sale call no larger than the latest matching lot and verifying the remaining quantity. It does not patch Core or modify the native market window. Other mods' trade hooks are not bypassed.

## Graph

The graph uses a separate instance of Core's `RTChart` renderer. Chart series are cached within the open panel; hovering does not run the investment policy. The graph displays up to 180 market days of Core history. Analysis uses the selected recent window from those same records. The caption shows the number of consecutive Core history days used, up to that setting.

- Green and red segments show rising and falling prices.
- Yellow shows the average price of currently owned shares.
- Cyan shows the buy ceiling when Auto-Invest is on and at least three consecutive Core prices are available.
- Orange shows the profit target for an automated position.
- Short cyan/orange marks indicate recorded purchases/sales.

A price below the cyan line alone does not authorize a purchase. Recovery confirmation, research, holdings, share-price limit, and cash reserve must also permit it. The asset status explains the current decision.

## AUMF use and ownership

AUMF owns the top-bar widget, resizable panels, focus release and close/rebuild lifecycle, tabs, toggles, collapsible settings, scrolling, theme bindings, custom-root cleanup, and pooled input commit callbacks. The mod removes its own reserve-preview change callback and restores temporary layout changes on teardown. Core owns daily prices and portfolio lots. This mod owns investment policy, trade records, ownership checks, chart contents, and coordination between its windows.

The consumer uses the four-argument AUMF handshake and references the exact installed 1.4.0 framework DLL with `Private=False`. No framework DLL is bundled. Core, AUMF, and other installed mods are not modified. No Harmony patch is required.

## Validation status

Build and policy checks are recorded in `VALIDATION.md`. The user confirmed the earlier Settings layout and Investment dropdowns work in game. Core-history trading, migration, and AUMF input reuse still require in-game acceptance, along with the earlier spending changes. The isolated stress test evaluates 5,000 assets with a 180-day window and ten confirmation days. A shared temporary range calculator reads at most 189 records per asset instead of rescanning overlapping windows. Timing is recorded in `Validation/Performance.log`; it excludes Unity, native trades, and rendering. Calculation remains once per market update, with no per-frame background worker. This initial build is not a Workshop release.

## Building

`mod.csproj` targets .NET Framework 4.8.1 and the installed Stardeus managed assemblies. Override `StardeusManagedPath` and `AutoUtilitiesModFrameworkPath` for another installation. The local `NuGet.Config` uses cached packages only. The reference-assemblies package must already be installed, or restored using an appropriate local NuGet configuration.

Build with `dotnet build mod.csproj -c Release`. The policy harness uses .NET 10: build `Tests/Investor.Tests.csproj`, then run its output. It does not start Stardeus or change a save.

## Removal

Disable Auto-Invest, save, then disable the mod and restart Stardeus. Purchased shares remain ordinary Core portfolio positions and can be managed through the native market. The mod's settings and management data have no effect while it is disabled.
