# TACC 1.1.4 compatibility review

Date: **2026-10-19**

Target: **Stardeus v0.15.0-rc337.3219**

Prior accepted runtime baseline: **TACC 1.1.1 / Core rc302.3178**

Reviewed optional integrations: **Quantum Storage 1.3.5** and **Expanded Traits 0.1.4**, with the exact prior reviewed snapshots retained

## Confirmed current-build failure

Building TACC 1.1.3 against the exact installed rc337 assemblies failed at `TraitPatches.cs` because `StorableObjComp.IsHaulingForbidden` no longer exists. Current Core exposes `StorableObjComp.IsForbidden`, and both equipment-search actions and ordinary automatic hauling use that property for the same red forbidden marker.

TACC 1.1.4 updates the Harmony getter target, the empty-slot candidate check, and the diagnostic inspection target to `IsForbidden`. The scoped Harmony postfix still returns `false` only while a TACC-managed equipment search explicitly allows forbidden loose equipment. It does not clear the marker or change ordinary hauling.

Initialization after a successful AUMF handshake is now transactional. TACC publishes `Instance` only after patching, panel/widget creation, and listener registration all succeed. A setup exception first clears setup fields and `Instance`, then independently attempts listener removal, UI disposal, and unpatching of TACC's Harmony owner. Cleanup failures are logged without skipping the remaining cleanup or replacing the original setup error, which is rethrown. The AUMF compatibility registration has no unregister API and is retained. Normal `Unload` behavior is retained.

## Core review

The reviewed rc337 assembly identities are:

- `Game.dll`: SHA-256 `38985F083AF23E557664230BC835BBF8BED3A4256F4F346C32E8428D68B33FA4`
- `Game.Constants.dll`: SHA-256 `F49F4FAEF233ED15DEA4BB3061E196ACD1C16E193FC49B3FB20C6F3A5F599909`

The current Core catalog contains **52 traits**. **49** match the prior reference. The reviewed differences are:

- **Stoic** is new: Human-only, incompatible with Wimp, and adds 25% to PainThreshold.
- **Wimp** is new: Human-only, incompatible with Stoic, and subtracts 50% from PainThreshold.
- **Enlightened** now sets medium and heavy mental-breakdown thresholds to zero instead of applying the previous percentage reductions. Core treats zero breakdown thresholds as disabled.

Only those three snapshots and the two new shipped trait IDs are updated. These fields do not alter TACC clothing profiles or provenance mood ranking. All **12 equipment-effect values** match; the equipment reference and clothing policy defaults remain unchanged.

## Quantum Storage review

Quantum Storage 1.3.5 DLL SHA-256 is `653111FD04A2ED9E9F1B62FA4EB3B124E0CF4D9AEA2EF9E7E52BE7C6BD4F6872`; its module MVID is `5eb2c05e-1725-4d0b-ae5c-6768b6f15319`.

Its prefix/finalizer pairs remain on `AIActionFindEquipment.Execute` and `AIActionFindBestEquipment.Execute`, using owner `jleas.quantumstorage`, priority 400, and no before/after constraints. TACC adds those four exact 1.3.5 snapshots while retaining the reviewed 1.1.12 and 1.3.0 snapshots. A changed target, patch method, owner, MVID, priority, or ordering remains unknown.

## Optional Expanded Traits review

Expanded Traits remains optional. An absent installation is clean and produces no missing optional-trait finding. TACC accepts either of these exact reviewed DLLs only when the unchanged reviewed 13-definition aggregate SHA-256 `94A39E354BE0C29C0A3B9EF31B6DB12C27D644A8C4F3B3107CA144177F690DE2` also matches:

- 0.1.3: DLL SHA-256 `FB8C13C813F491B980402BB416806C1139E23525AA8028D4B9E1280973DC1130`, MVID `9dc01d16-8daa-4e5f-a1be-a5b5be1b9858`
- 0.1.4: DLL SHA-256 `5A9ACA692F52B41D845DECACFAE9EE4CF0886B2D155D0247616C1D2A0BADC46F`, MVID `95e164e0-26dd-451e-984e-11e28c262fd0`

For each MVID, TACC recognizes the exact priority-400 prefixes owned by `ExpandedTraitsMod.Runtime.Bootstrap` on `InventoryItemSlot.BetterItem(ObjData, ObjData)` and `MentalHealth.AddEffect(GameState, MentalEffect, Single, Boolean)`, with no before/after constraints. TACC's own mood-first patch continues to declare `HarmonyBefore` the Expanded Traits owner. Any unknown DLL hash, definition aggregate, patch target, method, owner, MVID, priority, or ordering remains unvalidated.

## Build and static validation

- Production build against the exact installed rc337 managed assemblies and AUMF 1.4.0: **0 warnings, 0 errors**.
- Public optional-reference harness: **187 assertions passed**.
- The harness explicitly accepts both exact reviewed Expanded Traits hashes and rejects an unknown hash.
- AUMF, Core, Harmony, Newtonsoft, Expanded Traits, and Quantum Storage remain external and are not bundled.
- The curated Workshop package excludes development-only `Tests`, `.vscode`, and generated build outputs.

## Runtime acceptance still required

Fully restart Stardeus with TACC 1.1.4 and Core rc337.3219. Confirm TACC initializes once and the top-bar panel opens. With Quantum Storage 1.3.5 and Expanded Traits 0.1.4 enabled, clear TACC's diagnostic log; the reviewed Core identities and exact optional patch identities should remain absent. Repeat with Expanded Traits disabled and confirm its optional traits do not appear as missing or unknown. Exercise one managed **Allow forbidden loose equipment** pickup and confirm the item can be equipped while its red marker and ordinary hauling restriction remain intact. Static/build evidence does not replace these in-game checks.
