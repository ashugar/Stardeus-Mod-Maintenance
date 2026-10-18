# TACC 1.1.3 compatibility review

Date: **2026-10-10**  
Target: **Stardeus v0.15.0-rc324.3201**  
Prior accepted runtime baseline: **TACC 1.1.1 / Core rc302.3178**  
Reviewed optional integrations: **Quantum Storage 1.3.0** and **Expanded Traits 0.1.3**

## Reported findings

The supplied TACC 1.1.2 log contained 23 current findings: the two changed Core assembly identities, 15 new Expanded Traits entries, Cat Person and Dog Person rarity changes, and the four current Quantum Storage equipment-routing hooks. TACC correctly kept every difference unvalidated against its older references. Clearing the log was not used as approval.

Core rc324 hashes are:

- `Game.dll`: `8B8FB232CB23F0D91433270BB015F1B328C82C7877B37A1F398BC61753E19FFB`
- `Game.Constants.dll`: `568103B1C72E0ADA8645FD57E83478A4E7B335969798CDE48B53BEFA602EEB2B`

## Core comparison

The installed catalog still matches all **50** embedded Core trait snapshots. All **12** equipment-effect values also match, including the eight worn-gear mood values, tattered threshold, count cap, and provenance trade multipliers.

Retained rc316 binaries were compared with rc324 for `ItemOwnership`, `WornItemMood`, `AINeedEquipment`, `EquipmentPolicyFilter`, `MentalHealth`, `MentalEffect`, `InventoryItemSlot`, `AIActionFindEquipment`, `AIActionFindBestEquipment`, `StorableObjComp`, and `ObjData`. The decompiled outputs for all eleven targeted types are identical. The assembly changes are outside TACC's reviewed clothing and equipment behavior.

## Expanded Traits review

Expanded Traits remains an optional mod. TACC does not reference or bundle it. Compatibility recognition activates only when both of these exact reviewed inputs are loaded:

- Expanded Traits DLL SHA-256 `FB8C13C813F491B980402BB416806C1139E23525AA8028D4B9E1280973DC1130`, module MVID `9dc01d16-8daa-4e5f-a1be-a5b5be1b9858`.
- Aggregate SHA-256 `94A39E354BE0C29C0A3B9EF31B6DB12C27D644A8C4F3B3107CA144177F690DE2` for its 13 sorted trait-definition files, including file names and lengths.

The 13 static definitions are embedded as a separate optional reference. Expanded Traits also widens their compatible species at runtime, generates `ExpandedTraits.PetAffinity.<species>` entries for loaded pets, and normalizes Cat Person, Dog Person, Animal Person, and active generated affinities to a shared total rarity of 1.0. TACC validates those reviewed dynamic fields and requires all behavior-bearing static fields to retain their reference values. A missing Expanded Traits installation creates no missing-trait findings. A changed DLL, definition set, static field, generated-trait structure, or family rarity becomes unvalidated again.

Expanded Traits prefixes `InventoryItemSlot.BetterItem` for Minimalist equipment preferences. TACC accepts only that exact 0.1.3 patch snapshot and declares that its wearer-mood comparison runs first. A mood difference therefore remains decisive; Expanded Traits can choose its Minimalist preference when the two candidates produce equal clothing mood.

## Quantum Storage review

Quantum Storage 1.3.0 DLL SHA-256 is `1FDB70B0D64750E82AC5755ADC802EF03353D35A8F609154C93FD6A0B1DA65A1`; its module MVID is `e7abb8be-08ae-47a0-92bb-626504e51949`.

Its prefix/finalizer pairs on `AIActionFindEquipment.Execute` and `AIActionFindBestEquipment.Execute` still establish and restore the acting-agent routing context. The endpoint resolver now also falls back to Storage Relays. These hooks do not change clothing origin, mood, policy eligibility, item quality, reservation ownership, or item ranking. TACC accepts the four exact 1.3.0 snapshots and retains the earlier reviewed 1.1.12 snapshots for compatible installations. A different module identity or patch shape is unknown again.

## Build and static validation

- Production build: **0 warnings, 0 errors**.
- Test build: **0 warnings, 0 errors**.
- Automated checks: **90 passed, 0 failed**.
- TACC DLL: assembly/file **1.1.3.0**, product **1.1.3**, SHA-256 `54CFFD630E9AD1521385B054E5D1071055BC21559F946B53E8B2946FD279023F`.
- AUMF remains external at required level **1.4.0**.
- The live mod contains no `Tests` folder; the staging workspace retains the development suite.

## Runtime acceptance still required

Fully restart Stardeus with TACC 1.1.3 and Core rc324.3201. With the reviewed Quantum Storage and Expanded Traits versions enabled, clear TACC's diagnostic log; all 23 supplied findings should remain absent. Repeat once with Expanded Traits disabled and confirm its optional traits do not appear as missing or unknown. Confirm an ordinary and a Thrifty managed colonist still choose clothing by wearer mood, and a Minimalist uses its preference only when candidate mood is equal. Static/build evidence does not replace these in-game checks.
