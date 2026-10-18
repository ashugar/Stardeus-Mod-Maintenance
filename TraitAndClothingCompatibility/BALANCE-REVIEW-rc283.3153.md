# TACC 0.1.3 balance review

Reviewed on 2026-09-06 against the installed Stardeus v0.15.0-rc283.3153 assemblies and Core definitions. The supplied TACC 0.1.2 log reports the same assembly identities.

## Accounting for the 34 findings

| Finding | Count | Reviewed outcome |
|---|---:|---|
| Trait `AddsStatModifiers` changes | 17 | Actual values unchanged. Core sets `SerializableProperty.IsUsed` when it reads a property; this bookkeeping field is now excluded. |
| Missing old equipment constants | 7 | Removed constants have been replaced by the mood tunables below. |
| Unknown equipment tunables | 8 | Reviewed in the installed `Game.Constants.dll` and in the compiled clothing resolvers. |
| Changed Core assemblies | 2 | Clothing emission, mental adjustments, ownership/refusal, policy filtering, and config-read behavior inspected before updating fingerprints. |

The 17 traits are Ablutophobic, AlwaysHungry, BadAppetite, Clumsy, Enlightened, Fast, FastMetabolism, Insomniac, Lazy, LongSleeper, ShortSleeper, Showerer, Slow, SlowMetabolism, SlowPooper, Technophile, and Technophobe. All 50 Core definition snapshots match the prior reference before normalization. The normalized reference differs only by removal of the reviewed `IsUsed` flag from those 17 traits. This does not accept changes to their modifier amounts, property keys, descriptions, compatibility, or other definition fields.

## Validated effect values

| Tunable | Value |
|---|---:|
| `WornGearMoodSecondHand` | -4 |
| `WornGearMoodSecondHandThrifty` | +3 |
| `WornGearMoodSecondHandFastidious` | -8 |
| `WornGearMoodDeadMans` | -6 |
| `WornGearMoodDeadMansThrifty` | -3 |
| `WornGearMoodDeadMansFastidious` | -10 |
| `WornGearMoodTattered` | -3 |
| `WornGearMoodTatteredFastidious` | -6 |

The tattered cutoff remains 0.4; the extra-item cap remains 2. For each nonempty category with nonzero base `b` and count `n`, Core applies `b + sign(b) * min(n - 1, 2)`. Second-hand and corpse categories are exclusive per item and independent per being. Tattered items are counted separately. Zero base suppresses emission, including with multiple items. Optimist/Pessimist adds/subtracts one per emitted effect. Duration remains two game hours, refreshed by the equipment need; history can lag changes to gear.

The original Thrifty example now gives `3 + 1 = +4` for two second-hand items, and `-3` for one corpse item: total **+1** without tatters or another mood adjustment. Corpse gear no longer suppresses the second-hand bonus.

Core's Fastidious refusal applies when choosing gear, including second-hand and corpse-origin items. `AllowsToKeep` bypasses that trait refusal, although normal policy restrictions still apply. Existing TACC defaults remain appropriate and were not changed.

## Evidence and validation boundaries

Reviewed compiled types: `WornItemMood`, `ItemOwnership`, `MentalHealth`, `EquipmentPolicyFilter`, `TraitDef`, and `ComponentConfig`. Checked Core `Config/Traits/Defs` and English translations against the existing 50-trait snapshot. Actual installed mood resolvers and property-lookup behavior were also exercised in the isolated test runner. Test fixtures supply cached property IDs because Unity's native string hashing is unavailable outside the game.

- Game.dll SHA-256: `7F25568CC242D5CB5C8EFFA92B103550E2746DE93EA205FB6F248225F1EB7B27`
- Game.Constants.dll SHA-256: `3C5FADB3ECEA3611ABBDC17834994F3276360CB76BDDDE1382A00574E17E548F`
- Referenced AUMF: 1.4.0, external DLL SHA-256 `1A1664605F4BCBB69BAA588E81D1868C5815DBA70D1455B866549D74D51904BD`
- Release build: 0 warnings, 0 errors. Automated checks: 64 passed, 0 failed.

No external clothing patches have been approved. Future changed values, definitions, code, or inspected patches remain reportable after clearing or reloading. The review updates the built-in comparison reference, not a save's last-seen observations.

On **2026-09-06**, the user confirmed TACC 0.1.3 working and supplied an in-game screenshot showing the reviewed effect values and **0 current unvalidated findings**. This accepted balance/diagnostic baseline is promoted unchanged to **1.0.0**. See `RUNTIME-CHECKS.md` for the acceptance record and retained maintenance checklists.
