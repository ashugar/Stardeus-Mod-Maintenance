# TACC 1.1.0 feature and compatibility review

Date: **2026-09-23**  
Target: **Stardeus v0.15.0-rc301.3177**  
Framework: **Auto Utilities Mod Framework 1.4.0**

## Washing Machine

Version 1.1.0 adds one building and no weapon-restoration system.

- **Washing Machine:** 2×2, rotatable, unlocked by `Research/Habitation/Storage`.
- **Construction:** 4 Titanium Plate, 1 Plastic, 1 Glass, 1 Transistor, and 3 Copper Wire, matching the current Freezer materials.
- **Power:** requests 5 kW while actively washing and no input while idle or waiting for a haul.
- **Work:** an ordinary Core hauling goal reserves one exact eligible item and loads it through the device input port. A cycle takes 60 game minutes.
- **Upgrades:** the three slots inherited from Core's `BaseDevice` accept Speed and Energy Efficiency modules. Each module applies Core's 15% amount; speed multiplies progress and efficiency divides active power demand.
- **Result:** clears `BelongedToDead`, sets `SecondHand`, and preserves quality and all unrelated flags.
- **Eligibility:** any Core or modded item whose equipment slot is marked `Worn`; this currently covers hats, helmets, suits, and clothing. Weapons are excluded by Core's slot metadata.
- **Automation:** a saved global toggle controls new automatic orders for eligible stored items. Manual single-item and multi-selection Wash actions remain available when automation is disabled.
- **Recovery:** the input ID, loaded state, and progress are saved. Removal ejects an unchanged loaded item; relocation transfers a loaded item and its progress.
- **Graphics:** the user confirmed the four in-game orientations after swapping the `_L` and `_R` filenames to match the sequence used by the game. That mapping is retained as the asset baseline.

The implementation keeps normal power, construction, storage, reservation, reachability, hauling, and worker pressure. It does not modify mood formulas, weapon behavior, or item quality. A later 2026-09-24 maintenance change adjusts only the final comparison of eligible clothing for TACC-managed worn slots, as recorded below.

## rc301 compatibility review

The installed Core DLL identities are:

- `Game.dll`: `A1E12C9C8883728B3D37C861379C88ABBD5D5E9BA7592FA2D623961AFEBDA37C`
- `Game.Constants.dll`: `93076662463C695CEEE3E4E25076BE7FAE1EBC3EF1967C541954A47A3082BDA5`

All **50** validated trait snapshots and **12** validated equipment-effect entries match the 1.0.2 references. Targeted decompilation found the current `ComponentConfig`, `AINeedEquipment`, `WornItemMood`, `ItemOwnership`, `ObjData`, `MentalEffect`, `MentalHealth`, `EquipmentPolicyFilter`, `TradeableItem`, `Trader`, and `TraitDef` types byte-identical to the recorded rc296 versions. The full Tunables type changed for unrelated systems, including Elysium and asteroid settings; the reviewed clothing mood, tattered-threshold, and provenance price values remain unchanged.

The version and two assembly fingerprints advance only after that comparison. Historical rc294 and rc296 diagnostic regressions now use their recorded observed hashes, so a later installed Core cannot rewrite the meaning of those checks.

## Validation

- Production Release build: **0 warnings, 0 errors**.
- Test Release build: **0 warnings, 0 errors**.
- Automated checks: **82 passed, 0 failed**.
- DLL version: assembly/file **1.1.0.0**, product **1.1.0**, SHA-256 `92B5C8BD2B58A630E4CAB9086E09E5D07BBABD98EBDB2082CD2ADE3A88C088AE`.
- The mod continues to reference AUMF externally and does not bundle framework or Core libraries.
- Development test sources and outputs are staging-only. The live mod and Workshop package omit the entire `Tests` folder; distributed source remains under `.vscode`.

On **2026-09-23**, the user confirmed version 1.1.0 validated in game. This accepts the Washing Machine feature build, including its native Speed and Energy Efficiency Upgrade support, as the local release baseline. The confirmation does not record every retained regression scenario as individually retested. Workshop publication has not been reported for 1.1.0.

On **2026-09-24**, a local maintenance rebuild added the saved **Allow forbidden loose equipment** setting. The setting lets only TACC-managed colonists ignore the red forbidden marker while Core evaluates equipment searches; the marker remains set and ordinary hauling remains unchanged.

The same rebuild adds wearer-specific clothing comparison for TACC-managed worn slots. Core normally ranks eligible equipment by market value multiplied by quality and only considers origin mood within a five-percent tie. TACC now compares the complete worn loadout's mood result first, using Core's second-hand, corpse-origin, tattered, item-count, Optimist, and Pessimist rules. Thrifty beings therefore prefer beneficial second-hand clothing over new clothing when that produces the higher total mood. Equal-mood candidates return to Core's unchanged comparison. Policy filters still determine which candidates are eligible. These maintenance paths were subsequently confirmed in game with version 1.1.1.

`RUNTIME-CHECKS.md` retains the detailed checks for future maintenance and regression testing.
