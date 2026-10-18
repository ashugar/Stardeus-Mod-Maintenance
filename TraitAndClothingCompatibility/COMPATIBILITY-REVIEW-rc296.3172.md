# TACC 1.0.2 compatibility review: rc296.3172

Reviewed on 2026-09-19 against the installed binaries matching the user's two-finding log. The previous accepted/published baseline is TACC 1.0.1 for rc294.3170. This review covers clothing/ownership compatibility and related methods; it does not certify unrelated code throughout the game assemblies.

## Reported findings

| Assembly | Previously reviewed SHA-256 | Newly reviewed SHA-256 |
|---|---|---|
| Game.dll | 1072B5CFCCC57EBF6A432860BCC8597E82E0B6209C0B2A016FB3EFC21882B501 | 189A44FB75B380693ECDFE5E51633F3BD7866D85D26D9F7DB069B75815721ADD |
| Game.Constants.dll | BC4C98E9ACDB2C77482E872D88A8316F14DEDED31243616C068AE9CFB61DBF9B | DC5F0EE98A1FAED7DFE66E1CFEB7A64522AC86E9171B0437F70AC11FC20CD290 |

The updated assembly identities trigger a review even when tracked values have not changed. After the review below, TACC advances its tested Core label and those two fingerprints. No trait/equipment values are newly accepted or regenerated.

## Reviewed behavior

Targeted decompilation of the current installed binaries covered ItemOwnership, WornItemMood, EquipmentPolicyFilter, AINeedEquipment, MentalHealth, MentalEffect, ObjData, TraitDef, ComponentConfig, TradeableItem, Trader, and Tunables. Applicable method bodies were rechecked against the contract recorded in COMPATIBILITY-REVIEW-rc294.3170.md. Core's AI/Economy configurations and trait definitions/English translations were also checked. This is a renewed contract review, not a claim that the complete old/new assemblies are identical.

- All 50 current Core trait snapshots match the shipped trait reference, including after the 17 modifier definitions undergo Core property reads.
- All 12 equipment entries match the shipped equipment reference: eight clothing mood values, the extra-item cap of two, tattered cutoff of 0.4, and trade multipliers of 0.5/0.25.
- Worn-slot ownership tracking, body/mind owner continuity, persistent second-hand/corpse flags, and the no-mental-health exemption retain the reviewed behavior.
- Second-hand, corpse-origin, and tattered effects remain separate categories; additional items increase each nonzero category's magnitude by up to two points. Zero-valued categories emit nothing.
- Optimist/Pessimist still adjusts each emitted effect by +1/-1; same-reason effects replace each other, with the 120-game-minute clothing duration retained.
- Fastidious used-gear refusal, AllowsToKeep, and the policy filter's item, quality, color, and ownership handling remain compatible with TACC's adapter.
- ItemOwnership.BucketFor and PriceMulFor still assign corpse-origin before second-hand and return 1 for new/untracked, 0.5 for second-hand, and 0.25 for corpse-origin. Trader.UpdatePriceFor applies that multiplier to the calculated price. This is pricing, not a mood adjustment.
- Core still mutates SerializableProperty.IsUsed during a successful lookup; the existing narrowly scoped bookkeeping exclusion remains appropriate.

Both reference JSON files, policy logic, scan/clear behavior, save schema, UI, artwork, Workshop description, and published item ID are preserved. No external patch is newly approved. Future differences continue to report as unvalidated after clearing or loading.

## Verification

- Release build: 0 warnings, 0 errors.
- Final test build: 0 warnings, 0 errors.
- Automated checks: **70 passed, 0 failed**.
- Added regression reproduces exactly the two assembly findings against the published rc294 reference using the current trait/equipment snapshots. It confirms warnings remain after clearing and the reviewed rc296 comparison is clean.
- Existing checks cover the 50 traits, real Core mood/pricing/property-read methods, policy preservation, unknown/changed/missing values, external patch entries, and resolved method targets.
- Exact installed external AUMF 1.4.0 DLL SHA-256: 6E76464569AAE84CEACCF961CE62FDE12B29E901C88390437C017004FF8B42E8.

Direct DLL inspection confirms assembly/file versions **1.0.2.0**, product version **1.0.2**, and external AUMF reference **1.4.0.0**. TACC DLL SHA-256: `FC9F9D2DD67FB9521CE51066AA0B8B82BB51848BD41DBEE58E3458CEF2CB7514`.

On **2026-09-19**, the user confirmed in-game validation, Workshop publication, and the return to maintenance mode. **TACC 1.0.2 is the accepted, published baseline for rc296.3172.** RUNTIME-CHECKS.md retains the checklist for future changes. The separate .NET runner cannot execute the game's Harmony runtime; the user's validation supplies the runtime acceptance.

No game, dependency, save, or other mod files were modified. Future changes remain unvalidated until separately reviewed.
