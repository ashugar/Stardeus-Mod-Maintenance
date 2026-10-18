# TACC 1.0.1 compatibility review: rc294.3170

Reviewed on 2026-09-17 against the installed Core matching the user's four-finding log. This is a clothing/ownership compatibility review; it does not certify unrelated code throughout either game assembly.

## Findings and disposition

| Reported finding | Reviewed observation | TACC update |
|---|---|---|
| Game.dll fingerprint | Installed binary exactly matches the reported new hash; relevant compiled paths reviewed below. | Replace the previous reviewed fingerprint. |
| Game.Constants.dll fingerprint | Installed binary exactly matches the reported new hash; existing clothing values retained and new economy fields inspected. | Replace the previous reviewed fingerprint. |
| Tunables.TradeSecondHandPriceMul | 0.5; second-hand provenance price multiplier. | Add this explicit reviewed value. |
| Tunables.TradeDeadMansPriceMul | 0.25; corpse-origin provenance price multiplier. | Add this explicit reviewed value. |

The two values are authored in Core's Config/Tunable/Economy.json and initialized in the installed Game.Constants.dll. ItemOwnership.PriceMulFor returns 0.5 for SecondHand, 0.25 for DeadMans, and 1 for New/Untracked. ItemOwnership.BucketFor ignores non-worn items, gives corpse-origin priority, then checks whether clothing is used. TradeableItem.Origin uses that category; Trader.UpdatePriceFor applies PriceMulFor to the calculated price before assigning PriceOfOne. Other pricing factors still apply. These are trade adjustments, not wearer mood modifiers.

TACC's existing ownership-term scan correctly discovered these new fields. They remain in the comparison rather than being broadly excluded as trade settings. Later value changes or removals must still be reported. ItemOwnership methods are already part of patch inspection, so its new grouping/pricing helpers are automatically covered; this does not claim coverage of every trade method or arbitrary external mod behavior.

## Clothing and trait conclusions

- All 50 loaded Core trait snapshots match the existing shipped trait reference. The reference file is unchanged.
- The existing ten equipment entries are unchanged: eight mood values, the two-extra-item cap, and the 0.4 tattered cutoff.
- Thrifty: second-hand +3, corpse-origin -3, tattered -3. Fastidious: -8, -10, -6. Other minds with mental health: -4, -6, -3.
- Second-hand, corpse-origin, and tattered categories emit independently. Per-category extra items add one point in the base value's direction, capped at two extra points; a zero base emits nothing.
- Optimist/Pessimist still adjusts each emitted effect by +1/-1. Same-reason effects replace rather than stack; clothing effects last 120 game minutes.
- Fastidious used-gear refusal and AllowsToKeep behavior remain compatible with TACC's policy adapter. Minds without mental health retain the existing exemption.
- Ownership tracking remains limited to worn slots, with body/mind owner matching and persistent second-hand/corpse flags as reviewed.
- Core's successful property reads still mutate SerializableProperty.IsUsed. Only that previously reviewed bookkeeping field remains excluded; real property values are still compared.
- Clothing policies, automation timing, UI layout, saved settings, save schema, and artwork are unchanged.

## Reviewed evidence

Targeted decompilation of the installed binaries covered ItemOwnership, WornItemMood, EquipmentPolicyFilter, AINeedEquipment, MentalHealth, MentalEffect, ObjData, TraitDef, ComponentConfig, TradeableItem, Trader, and Tunables. Reviewed the relevant method bodies, compared the applicable contract to TACC's established assumptions, and checked the Core AI/Economy configurations and trait definitions/English translations. The test runner exercises real Core mood/ownership resolver and property-read methods against isolated data.

- Game.dll SHA-256: 1072B5CFCCC57EBF6A432860BCC8597E82E0B6209C0B2A016FB3EFC21882B501
- Game.Constants.dll SHA-256: BC4C98E9ACDB2C77482E872D88A8316F14DEDED31243616C068AE9CFB61DBF9B
- Exact installed external AUMF 1.4.0 DLL SHA-256: 6E76464569AAE84CEACCF961CE62FDE12B29E901C88390437C017004FF8B42E8

## Verification and runtime boundary

Release build: 0 warnings, 0 errors. Test build: 0 warnings, 0 errors. Automated result: **69 passed, 0 failed**.

Direct DLL inspection confirms assembly/file versions **1.0.1.0**, product version **1.0.1**, and external AUMF reference **1.4.0.0**. Both embedded reference catalogs match their source files. TACC DLL SHA-256: `DA5CAD4DC5E71AAA58103DDE74EB4F415CC7A35B1A3CE0F7ADB015FD2C9925D8`. The Workshop description, published item ID, and images are preserved.

Five additional checks cover reproduction of the exact four findings against the previous reference, Core price multipliers, persistent warnings after clearing modified trade values while mood remains unchanged, installed AI/Economy configuration agreement, and inclusion of the new ownership helpers in patch inspection. The existing checks cover all 50 traits before/after property reads, clothing values, policy preservation, unknown findings, and the reflected method targets. The updated reference produces zero findings in the isolated comparison.

On **2026-09-17**, the user confirmed the update validated and supplied an in-game screenshot identifying **v0.15.0-rc294.3170** with **0 current unvalidated findings** after clearing. TACC **1.0.1** is accepted as the current compatibility baseline. See RUNTIME-CHECKS.md for the acceptance record and retained maintenance checks.

No external patches were approved, no saved observations were promoted to validation, and no game/save/dependency files were changed. The unknown-until-reviewed behavior continues to apply to future differences.
