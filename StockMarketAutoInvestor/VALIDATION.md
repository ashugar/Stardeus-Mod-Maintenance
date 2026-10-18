# Validation record — 0.1.0 test build, trade fanfare / UI revision 13

Checked on 2026-10-06 against the installed Stardeus assemblies and repaired AUMF 1.4.0 DLL. Exact hashes are recorded in `Validation/Provenance.json`. Gameplay acceptance of the new trade banner and the preceding list-percentage layout remains pending.

## Trade fanfare

Read-only Core inspection confirms the screenshot's native banner path: GameState.Sig.ShowFanfare, OnScreenAlertHandler and FanfareHandler. Core owns rich-text rendering, message stacking, timeout and sound. The mod sends one message after an order's expected holdings are verified and its history record has been added. A multi-lot sale sends once after the complete order. Notification exceptions are contained so they cannot make a completed transaction fail or stop automation.

Buys report quantity and actual wallet debit. Sales report quantity, per-share sale price and TradeRecord.RealizedProfit, with the established green/red colours. Zero is Break-even. Failed orders and saved history are not sent. The default-on ShowTradeFanfare setting is additive within schema 3, and old saves without it default to on. Its setter refreshes the interface without clearing decisions or modifying Auto-Invest. Manual trades made through the mod also announce while Auto-Invest is off; native Stock Market trades are outside this hook.

Release build: zero warnings and errors. All 115 policy/state checks pass, including old-save defaults and persistence of the disabled setting. The compiled fanfare probe covers six cases: actual purchase debit, sale price and positive/negative/zero results, disabled signal suppression and enabled delivery through Core's real ShowFanfare signal. Existing compiled Core probes and both Mono quote/large-order modes also pass against the current Core DLL. These checks use isolated managed objects and do not render a banner in Unity. The prior performance benchmark remains historical evidence.

In game, confirm a completed buy and profitable/loss-making sell with the investment panel closed, the displayed values against the wallet and Trade history, and the toggle across save/reload. Check several trades in one daily update and the normal hover/expiry behaviour. A failed manual order must produce no success banner. No game process or player save was modified outside the normal mod workflow. The read-only log has a 0.1.0 load marker, which alone cannot prove this internal revision has loaded; restart and new runtime acceptance remain pending.

## List profit/loss percentage (UI revision 12)

Each stock row adds the potential return of the remaining owned shares after the daily change. The calculation is `(owned quantity * current price - remaining purchase cost) / remaining purchase cost * 100`, using the existing Holdings reader over Core's purchase lots. Manual and automated holdings both count. Positive results have a plus sign and green text; negative results have a minus sign and red text. Exact zero is neutral. Percentages show one decimal place. Unowned assets and invalid or zero purchase costs show a dash; the row tooltip explains the calculation and missing values.

The native row's final label has enough width for the daily change and return percentage, using its actual text size. AUMF lifetime cleanup restores the pooled label's original sizing. Market Insights still gates the daily change; the ownership return can be shown whenever holdings are available, matching the detail panel. Existing hover, clicked-asset selection and panel refresh paths are retained. There is no new background worker, market listener, saved field or trading rule.

Verify the row at narrow and wide panel sizes, positive/negative/zero results, unowned stocks, and assets with several purchase lots. Check hover graph response, click selection and switching between the native market and mod panels for pooled-layout reuse. This UI revision changes only InvestorUi.cs among production sources. Core's installed DLL has changed since the September 12 record; current build and compiled Core checks are recorded separately from older gameplay evidence.

The September 28 build had zero warnings and errors. All 113 then-existing policy/state checks and the compiled Core probes passed with those dependencies. Both installed Mono modes also passed the quote and large-trade probes. The September 12 isolated benchmark remains historical evidence. A read-only Player.log check showed the 0.1.0 load marker and no investor errors among matching lines; the shared public version did not establish that UI revision 12 had loaded. In-game layout confirmation remains pending.

## Potential profit/loss value colour

The Investment details value is green (`#66CC66`) when positive and red (`#FF6666`) when negative. Exact zero retains the normal text colour. The user confirmed these colours in game. They also confirmed the green trade-history tooltip and a profitable automatic sale of 1,000,000 Bytecoin shares on day 1450 for about 5.09B Starcredits. These confirmations apply to those paths; they do not establish a large automated sale at the newer storage ceiling.

The user subsequently confirmed automated purchases of 2,147,483,647 shares in game. Their screenshot shows two such purchases on market day 1440. This confirms larger automatic purchases, without claiming acceptance of large manual trades or large automated sales.

## Large orders

Read-only Core source and compiled-code inspection place the one-million-share clamp in `WalletTab_StockMarket.ExecuteBuy`. `MarketPortfolio.Buy` and `Sell` accept Int32 quantities without that clamp. Core stores lot quantities and per-stock ownership totals as Int32, so the mod permits up to 2,147,483,647 total shares of one stock. This replaces the mod's copied one-million limit in automatic allocation and manual Buy/Sell. The native market window, Core files, daily cadence and saved schema are unchanged. Existing holdings are not enlarged automatically.

Purchase quantities use bounded binary searches rather than decrementing one share at a time. Each search takes at most 31 iterations. Planned charges conservatively cover Core's float conversion of large counts and both float32 and extended-precision multiplication. Current portfolio valuation and the reserve are checked again before each automated purchase. Manual purchases also reject insufficient cash and orders that would exceed the per-stock storage ceiling. Sales retain the existing exact per-lot batching.

All 113 policy/state checks pass. New coverage includes five-million-share purchases, allocation at the storage ceiling, 20,000 additional large-order reserve cases, large partial/full sales and saved Int32-maximum quantities. In each of two isolated installed Mono modes, actual Core methods complete 2,000 purchases above one million and 4,000 partial/full sales with reserves preserved. Three multi-lot cases per mode verify exact sales from 2,147,483,647 owned shares. These probes use isolated managed objects and do not operate the running game or a player save.

## Trade result tooltip

Trade-history tooltips label a positive realized result **Profit** and colour the entire result line green (`#66CC66`). Negative results show **Loss** in red (`#FF6666`) with the positive amount lost. Exact zero shows **Break-even** in the normal text colour. Unknown results still omit the line. This preceding UI revision changed only display text and colour; it remains included in this build.

The large-order build has zero warnings and errors. The rebuilt assembly loads successfully, and the compiled Core probes pass. Policy, fuzz and Mono checks were rerun for the large-order changes.

The user confirmed the preceding quote-precision correction, automated sales and **Sell to limit losses** in game. Their market-day-1425 screenshot shows two automated purchases and two automated sales. This is acceptance of those behaviours, not a complete check of every investment rule or UI control.

## Quote precision correction

The user showed 180 / 180 Core history days and an owned Armored Helmet while its status said **Waiting for current Core price history**. That status combined an invalid quote, a missing current-day record, and an exact double-price mismatch. Their screenshot also supplies in-game evidence of automated purchases in the preceding history build; it does not validate this correction or automated sales.

The old history reader emitted `mul; conv.r8` without an explicit float rounding step. An isolated host of the installed Stardeus Mono DLL reproduced false mismatches with `--optimize=-float32,-inline`: Core returned 2046 while the reconstructed double was 2046.0000157356262. All 100,000 varied direct quote comparisons in that mode mismatched in the old build. The old build passed under default Mono settings. These tests establish the precision defect; the running game's exact JIT configuration and the specific day-1421 price pair were not captured.

A read-only decode of the latest save at market day 1424 found matching stored current and daily multipliers for every asset, including the helmet entries. The game log showed loading entries and no matched investor error. No save or game process was modified.

Production freshness now compares Core's stored `PriceMul` with that day's stored multiplier, rather than comparing two independently calculated double prices. History and peak calculations explicitly narrow to float precision. Standalone policy views also compare at native float precision. Invalid live prices, missing daily records (with the day identified), and genuinely changed quotes now have distinct statuses.

`Tests/MonoQuoteProbe.cs` runs against actual managed Core objects and the installed Mono runtime. Both extended-precision/no-inlining and float32 modes pass 100,000 quote comparisons each, a valid historical purchase, and rejection of a changed current multiplier. See `Validation/MonoQuoteProbe.log`. This is isolated runtime evidence, not a live Unity/gameplay test.

## Current change

The mod reads Core's daily price dictionary directly. It no longer collects, retains, or serializes its own prices. Core stores daily multipliers; the reader multiplies by the item's base value with the same float rounding as Core's current price. Analysis uses consecutive valid records ending on the current day, bounded by the selected 3–180 day window. At least three records are required. Negative prewarmed days are valid; missing, invalid, and future prices cannot extend the current window.

Confirmation streaks are reconstructed from recorded days at each daily update. Each day uses its own trailing range. Settings changes are applied to history on the next update. Sale peaks use history since purchase, including days while Auto-Invest was off. A daily scratch calculator derives all overlapping confirmation ranges in one pass of at most 189 records per asset, and is shared across that pass. No history or analysis worker survives between daily events.

Enabling and loading do not trade. The next normal daily event may qualify immediately from existing history. Off unsubscribes the market listener and clears cached trade approvals. Duplicate or rewound notifications cannot trade, and skipped days do not produce replayed transactions. Automated execution requires a current-day approved decision, plus the existing research, lot identity, spending and share-count checks.

Schema 3 omits old samples, observation totals, minimum-observation settings, peaks, and transient confirmation/approval fields. Schemas 1 and 2 remain accepted. Existing settings, enabled state, trade records and managed lots remain; schema 1 retains its earlier spending conversion. No player save file was edited outside the game's normal save lifecycle.

All text fields use AUMF's corrected `AddInput`. The consumer's temporary `ScopedInputBinding` and unpooled-input helper are removed. The reserve preview retains its own scoped change-callback cleanup. The input helper's compiled call to AUMF was inspected, and the exact installed framework repair was verified by source and decompilation. AUMF's own 15 isolated regression tests are recorded in its `INPUT_LIFECYCLE_TESTING.md`; those are upstream evidence, not newly executed consumer UI tests. The previous consumer binding tests are superseded because that binding is no longer present.

## Completed checks

- Release build: 0 warnings and 0 errors; version remains 0.1.0 / assembly and file 0.1.0.0.
- 113 current policy/state checks pass, including 100,000 varied quote-precision cases, 5,000 history/range comparisons, 20,000 reserve arithmetic cases, 20,000 additional large-order reserve cases and 6,000 allocation cases. Two separate Mono modes also pass 100,000 quote comparisons each, plus the actual large Core trades described above.
- Historical checks cover existing-day qualification, day-specific ceilings, falling/flat interruptions, gaps, invalid prices, prewarmed history, post-purchase peak boundaries, loss-limit behavior, repeated evaluation, enable/disable transitions, and legacy JSON.
- The isolated compiled assembly and Core probes verify registration, exact native sale arithmetic in 37 cases, valuation of actual Core lots, direct history access, and schemas 1–3 preserving managed lots while rejecting obsolete cached decisions.
- The current 5,000-asset benchmark with a 180-day window, ten confirmation days and 1e15 cash measured 4.978 ms median and 5.099 ms maximum across 15 measured runs after warm-up. It exercises allocations at the new storage ceiling. These timings exclude Unity, native trades and rendering; they do not establish in-game frame performance.
- AUMF remains external with `Private=False` and the four-argument registration handshake. Core and AUMF were inspected read-only. No other mod or player data was changed.

Logs are in `Validation/Build.log`, `PolicyTests.log`, `AssemblyProbe.log`, and `Performance.log`. The isolated probe does not run Unity, register into a live game, or load a player save.

## In-game checks still required

1. Restart Stardeus with the repaired AUMF and this mod. Load the existing investor save. Confirm enabled state, reserve, purchase method, analysis window and managed holdings are retained.
   After the next daily update, check the formerly affected Armored Helmet. Its status should reflect actual trading conditions instead of the former precision mismatch. A genuine missing or changed record must have a specific explanation.
2. Settings should show **History and confirmation**, with no **Minimum fresh observations**. The toggle tooltip should explain Core history, the next daily update, and true Off behavior.
3. Enable Auto-Invest. The graph should immediately show the available Core history count and cyan ceiling. No trade should occur merely from toggling. On the next daily update, existing qualifying consecutive days may produce a purchase, subject to reserve and share-price limits.
4. Keep Auto-Invest off across several days with the panel closed. No automated buys or sales should occur. Enable again and verify the next update uses the current Core records without a collection countdown or missed-day trade replay.
5. Test profitable and optional loss-limit sales on an automated lot. Pre-purchase highs must not qualify a reversal. Manual holdings remain excluded; native manual changes to a managed lot pause automation.
6. Change the analysis window and confirmation count. The graph should reflect the selected history window and the next daily decision should use the new rules. The horizontal cyan line represents today's ceiling, while confirmations use their respective days.
7. Save and reload while enabled, then while disabled. Loading must not trade or restore obsolete collected prices. Load a different save and check that state does not cross between saves.
8. Repeatedly edit settings, search and manual quantity using Enter and focus loss. Switch tabs, close/reopen, resize, and allow daily refreshes. Each edit must affect only its intended field. Check reserve-preview updates and a fresh game log for focus/pool errors.
9. Retain the earlier spending checks: validate cash plus current share value, all three allocation methods, maximum price per share, and the daily limit on different purchased stocks. Sales do not consume purchase slots.
10. Check hover and scroll graph response, independent clicked-asset trade targets, native market coexistence, theme changes, and a fresh `Player.log`.
11. With enough available cash, verify a new qualifying automated purchase exceeds one million shares while leaving the configured reserve. Check a manual purchase and partial/full sale above one million, then save/reload the larger holding. Confirm the daily limit still counts stock types and existing holdings are not enlarged automatically.

The earlier layout and dropdowns have user acceptance, as do the quote-precision correction, automated sales and loss-limit sales described above. Full profitability, spending behaviour and input reuse checks remain outstanding. Verify the new trade-history tooltip colours on positive and negative sales. No Workshop publication or version promotion was performed.

## Historical evidence

The following records describe earlier builds and their rules. Fresh collection requirements and the mod-owned input workaround below are historical and are superseded by this revision.

## UI revision 1

User screenshots from the initial build establish that the panel, tabs, asset list, Settings controls, and global-index graph rendered in game. They do not establish trading or save acceptance.

This revision removes the duplicate Auto-Invest status row from Settings, places each numeric label and input on the same line, and replaces the asset-filter and order cycling buttons with native dropdowns. The input callbacks and pooled control lifetimes remain managed by AUMF. Temporary layout sizes and input-icon visibility are restored when controls return to the pool.

The policy, system, and chart source hashes match the preceding build. Core's market interval, observation requirements, and other investment thresholds have not changed. The 51 policy/state checks remain applicable to the unchanged policy source; the rebuilt assembly and 37 native-sale cases were checked again. The user subsequently confirmed the Settings layout is easier to read and the Investment dropdowns work as intended. Their screenshot shows 2 / 30 fresh observations and zero automated trades at market day 1320. Trading acceptance remains pending.

## UI revision 2

Renames the loss-sale toggle to **Sell to limit losses** and its percentage field to **Price drop before selling (%)**. The tooltips explain ON/OFF behavior with a $100 purchase and 15% drop example. They state that fresh data, confirmation checks, and the time between trades can delay the sale, so the percentage does not guarantee a maximum loss. The README includes the same explanation.

This revision changes display text only. Settings keys, stored values, defaults, and trading logic are unchanged. The previous policy/state results remain applicable. Build and assembly checks cover the new DLL; gameplay checks and the new tooltip's on-screen readability remain pending.

## UI revision 3

The **Price drop before selling (%)** row is created only when **Sell to limit losses** is on. The existing toggle callback refreshes the AUMF panel, so the row appears or disappears with the toggle. Hiding the input does not change its stored percentage. The row remains directly below the toggle and uses the existing panel width.

Only the UI source and documentation changed. The policy, system, and chart source hashes match revision 2. Build and assembly checks cover the rebuilt DLL; the prior policy/state checks remain applicable. In-game acceptance of showing and hiding the row remains pending. Verify that an edited percentage returns unchanged after toggling off and on.

## UI revision 4

The user reported falling Nanobots prices below the cyan line at market days 1361–1362 without a purchase. The existing purchase rule requires a higher price than the preceding day, at or below the current ceiling, on every consecutive confirmation update. A falling price therefore does not qualify. The user confirmed changing the analysis window from 60 to 30 days between screenshots; the next update trims the retained prices to 30 and can move the ceiling. The graph draws the current ceiling across the window, not the historical ceiling for each day.

The asset detail's **Auto-Invest** tooltip now explains the purchase rule when no shares are owned. The Settings tooltips explain consecutive daily rises, the moving ceiling, and the retained observation count. Waiting messages distinguish falling prices, unchanged prices, a price above the ceiling, and an observed range below the configured minimum. Confirmation progress is labelled **Confirming price rise**. No new panel rows or width requirements were introduced.

Purchase and sale conditions, defaults, and save fields are unchanged. The model changes only purchase status text; the system and chart source hashes match revision 3. Eleven added checks cover the reported waiting behavior, a new low moving the ceiling, interrupted/restarted confirmation, buying at the ceiling, a narrow range, the shorter window, and the default budget being smaller than one Nanobots share. All 62 checks passed. Both builds had zero warnings/errors; the isolated assembly and 37 native-sale cases passed. An initial probe invocation used a duplicated directory name; the corrected invocation passed against the installed game assemblies.

The new tooltip layout and status text still need in-game acceptance. Existing saved waiting text updates at the next market update. These checks establish the configured decision behavior, not actual gameplay purchases or profitability.

## Spending revision 1 / UI revision 5

Replaces the absolute cash reserve, total budget and per-purchase budget with a percentage reserve and purchase-method dropdown. The old per-asset budget becomes the maximum eligible price for one share. The daily limit now counts different stocks purchased; sales occur first and do not consume these slots. The allocation methods are Divide Evenly, Purchase Most Profitable, and Purchase By Quantity. The opportunity score uses potential percentage gain to the highest freshly observed price, without treating it as a prediction.

Core's `MarketPosition.Cost` and quantity retain original purchase information; `MarketItemModifier.Price` supplies current value. Core's general net-worth calculation also includes structures and other property, so it is not used for the requested cash-plus-shares reserve. The mod keeps these derived values separate without copying the portfolio into its saved state. New trade records retain cost basis and realized profit/loss; old unknown costs remain null.

The reserve row uses AUMF's normal label/input controls. Its native second text field shows the calculated Starcredits. Typing updates only the preview; Enter/end-edit still commits through AUMF. A wallet binding and the existing open-panel watcher update the preview as holdings change. The added input callback and watcher callback are removed on panel disposal. There is no new background processing while Auto-Invest is off and its UI is closed.

Schema-1 saves migrate after Core state is ready. The old remaining investment allowance becomes a conservatively rounded reserve percentage, the old per-asset amount becomes the share-price cap, and the purchase method starts at Divide Evenly. Existing fresh observations and ownership are retained; confirmation counts clear. Very large wallets can migrate to 100% reserved, requiring the player to choose a new percentage before buying. Migration, all three method round trips, removal of old spending fields, and nullable historical cost/profit fields are covered by the harness.

The daily scan indexes holdings once instead of repeatedly summing all lots for every asset. Purchases are ranked once and allocation is bounded by the 20-stock daily maximum. Current holdings, affordability and the reserve are rechecked before execution. Tests account for Core's float charge and the portfolio value after the purchase.

`Validation/Performance.log` records a 5,000-asset, 180-observation isolated benchmark: 15 measured passes after warm-up, 8.595 ms median and 10.553 ms maximum. This measures observation, policy, ranking and allocation; it excludes Core trade calls, Unity and rendering. The once-per-day calculation was retained rather than introducing a multi-frame worker based solely on this isolated result. Real gameplay responsiveness remains unmeasured.

## Input lifecycle correction / UI revision 6

The user reported numeric entries changing unrelated fields, with the affected fields changing across edits and tabs. The preceding screenshot showed `10` in the asset search and **No assets match this filter**. Search filtering is separate from market collection; the empty list alone did not establish lost tracking.

Read-only source inspection and decompilation of the exact installed AUMF 1.4.0 DLL confirmed that `PanelContentContext.AddInput` adds a new `onEndEdit` listener on each use without removing it or checking whether its old lifetime has ended. Core's `WInput.Load` replaces its own submit/change callbacks but does not clear that TMP event. Old closures therefore remain on pooled controls and can update previous settings or search fields. This is a callback-retention defect. The read-only game-log lookup showed mod loading and no matched investor-error entries; it does not prove ongoing collection or validate this correction.

This mod now uses fresh native `WContent2Input` prefabs through `UISpawn.WidgetPermanent` under AUMF-owned content roots. These inputs are not added to the shared widget pool or despawned through it. The installed Core helper creates a fresh prefab and runs pre-initialization without registering a pool member. AUMF's owned-root destruction removes the inputs on teardown, and its normal focus release remains in place.

`ScopedInputBinding` owns the submit and end-edit routes, commits once per frame, checks whether the field's lifetime is active, removes only its own event listener, and releases its captured callbacks on disposal. Late queued callbacks are ignored. Search, manual quantity and every Settings input use this path; the reserve preview also ignores a disposed lifetime. Empty search results now name the search and explain how to clear it.

Ten new regression checks reproduce the old callback accumulation and cover Enter/end-edit coalescing, inactive-lifetime rejection, delayed events after disposal, preserving unrelated listeners, concurrent fields, and 1,000 randomized field reassignments with no cross-writes or retained owned listeners. These exercise the production binding logic through managed event endpoints; they do not render Unity fields. The build and isolated assembly/Core probes passed. The model, spending policy, market system and chart source hashes match the prior build. AUMF and Core are unchanged.

Restart is required to discard the previous session's leaked UI callbacks. Previously overwritten settings cannot be reconstructed reliably and must be reviewed by the player. No save values were edited or reset as part of this fix. Live tab switching, typing, focus behavior and cleanup still require in-game acceptance.

## Reproduction and rollback

Build `mod.csproj` and `Tests/Investor.Tests.csproj` in Release. Run the test harness normally for policy checks; use `--benchmark` for the isolated stress case or `--probe <mod DLL> <Stardeus Managed directory> <AUMF Libraries directory>` for the compiled/Core probes.

The pre-history mod remains under `.release-backups/StockMarketAutoInvestor/history-before-20260912-011717`. The complete pre-precision build is preserved under `quote-before-20260912-031235`. This correction's stage, archive and parity report are stored beside them under a timestamped `0.1.0-quote1-...` directory.

The Mono regression probe is separate from the .NET 10 policy harness. Compile `Tests/MonoQuoteProbe.cs` with the .NET Framework C# compiler, referencing the installed `Game.dll`, `KLUnityUtils.dll` and consumer DLL, and put the consumer DLL beside the resulting executable. Run `Tests/RunMonoQuoteProbe.ps1 -AssemblyPath <absolute executable path>` in a separate PowerShell process; its default options exercise extended precision without inlining. Repeat with `-Options '--optimize=float32'`. The helper uses this installation's Mono and Managed paths; adjust those paths for another installation.
