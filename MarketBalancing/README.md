# Market Balancing 0.1.0

Raises underpriced crafted goods to at least the base market value of their ingredients.

For each eligible recipe:

`minimum product value = sum(ingredient base value × ingredient quantity) / output quantity`

The mod preserves a product's existing value when it is already higher. If several ordinary recipes make the same product, the floor covers the most expensive recipe. Ingredient increases propagate through later production steps. There is no additional profit markup, labor charge, or energy charge.

## Scope and exceptions

- Uses the recipes and physical market items loaded by Core and enabled mods when the market initializes.
- Includes recipes whose `IgnoreMarket` flag excludes them from vanilla market validation, including survival meals, ammunition, clothing, upgrades, and many intermediate materials.
- Keeps Core's own market validation, then raises any remaining underpriced products before Core applies market items, prewarms a new market, or calculates initial wealth.
- Excludes recipes with crafting type `Converter`. Deliberately lossy conversions cannot all satisfy this rule: plastic consumes three rubber, while rubber consumes three plastic, plus other ingredients in both directions. Ordinary recipes for those materials remain eligible.
- Excludes any additional recipe that closes a circular dependency. Logs the excluded recipe IDs and retains independent recipes and downstream products.
- Materials explicitly marked `IgnoreMarket` have no market value to add. A missing market entry for an otherwise tradable ingredient skips the whole recipe; malformed or overflowing recipes are also skipped and counted in the log.
- Products without a physical market entry, disposal-only recipes, raw resources without eligible recipes, and financial stocks receive no direct adjustment.

All values are **base market values**. Native price fluctuations, demand, trader heat, buy/sell multipliers, quality, and trade spreads still affect actual quotes. Equal base ingredient value does not guarantee a profitable buy/craft/sell transaction. Recipe quantities, worker requirements, research, crafting time, and energy consumption retain their native behavior.

## Production chains can change ingredient prices too

This covers more than meals. For example, Core's survival-meal definition costs 50 while its raw protein and water definitions cost 89 and 30. The isolated meal floor would be 119. In the static Core snapshot, other eligible recipes first raise protein to about 173.33 and water to 250, producing a survival-meal floor of about 423.33. Water has an expensive stem-cell recipe as well as cheaper recovery routes; the floor covers every ordinary route.

These numbers illustrate the rule, not a fixed price list. Core's native validation and enabled mods can change the runtime inputs. See [the static Core price audit](Tests/CorePriceAudit.json) for every calculated change and its evidence limitations.

## Installation and removal

The local mod folder is `MarketBalancing`; its only runtime DLL is `Libraries/MarketBalancing.dll`. No external mod dependency is required. Enable **Market Balancing** in the mod manager, restart Stardeus, then load an existing save or start a game.

The mod updates shared in-memory base values, owns no save schema, and edits no Core files or player settings. It preserves native saved market modifiers and histories. On unload, it restores its own base-value changes when another mod has not subsequently changed or replaced that item. Restoring before the next native market validation prevents the mod's floor from compounding across repeated loads.

To roll back, disable Market Balancing and restart the game. No prior mod installation was replaced. Trades already completed and native market effects saved during play are not undone by disabling it. Higher base values also feed native inventory/colony wealth calculations and systems that use them.

## Validation

Baseline examined: **Stardeus v0.15.0-rc333.3215**, installed Core source/configuration and `Managed/Game.dll`. DLL identity and full-signature patch checks are recorded in [the binary audit](Tests/BinaryAudit.json).

- Release-configuration build: zero warnings and errors.
- 346 automated assertions passed: price floors, batch yields, alternative recipes, chained production, fractional rounding, cycles, invalid data, overflow, repeated calculation, and adapter restoration/ownership cases.
- Static Core JSON audit: 159 physical market entries, 229 recipes, 141 eligible recipes, and 101 increased product values; all eligible floors passed.
- The Core snapshot uses raw JSON values and adapter stand-ins. It does not execute Core's own validator, merge enabled mods, install Harmony patches, load a save, or exercise trading.
- Runtime startup, existing-save/new-game behavior, and trading acceptance remain pending. No Workshop publication or release archive was created.

Build and repeat the focused checks from the parent Mods directory:

```powershell
dotnet build MarketBalancing/mod.csproj -c Release
dotnet run --project MarketBalancing/Tests/MarketBalancing.Tests.csproj -c Release -- "D:/Program Files (x86)/Steam/steamapps/common/Stardeus/Stardeus_Data/StreamingAssets/Mods/Core" MarketBalancing/Tests/CorePriceAudit.json
& MarketBalancing/Tests/Test-Binary.ps1
```

The test harness requires .NET 10. The mod targets the installed game's .NET Framework/Mono assemblies. The metadata script uses the installed ILSpy Mono.Cecil library; pass `-Cecil` if its path changes. Do not distribute `Tests/bin`, `Tests/obj`, or project `obj` output as runtime libraries.

## Remaining in-game checks

1. Enable the mod and start a fresh game process. Confirm `[Market Balancing] 0.1.0 initialized` followed by the applied-price summary in `Player.log`, with no mod initialization or Harmony errors.
2. Load an existing save. Compare the logged survival-meal base value with the logged/current protein and water base values; it must cover their sum. Check representative later products and batch recipes too. Use base values for the comparison, since actual quotes include native market modifiers.
3. Exercise crafting and trading normally; confirm recipe quantities, production requirements, inventory, and saved market modifiers behave normally.
4. Save/reload, then return to the menu and load again. The price plan should remain stable when recipe inputs and other mods are unchanged. Repeat on a new game and check its market initialization.
5. Disable the mod and restart to confirm removal restores native base pricing. Keep a pre-test save if comparing saved native market outcomes.
