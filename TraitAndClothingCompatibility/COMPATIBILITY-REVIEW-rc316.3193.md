# TACC 1.1.2 compatibility review

Date: **2026-10-03**  
Target: **Stardeus v0.15.0-rc316.3193**  
Prior accepted runtime baseline: **TACC 1.1.1 / Core rc302.3178**  
Reviewed optional integration: **Quantum Storage 1.1.12**

## Reported findings

The supplied runtime log contained six current findings:

- `Game.dll` changed from `44DF974FE2ACA2CAC330DA09905CCB3FE13EFD86F7BBE45393E7880FFE8059ED` to `14B6B371F768723057A7B82A698DEFAC75F9397F586633F9B18B2830EF297D6A`.
- `Game.Constants.dll` changed from `AA626D1DA1702BD4771CE22839A9DA05467667F827F2ED2314E01484ABC1A479` to `98984D64B5444474DBF1B64B4A7BFFC393163CBE7E692F5D94245D28973EDA6E`.
- Quantum Storage added a prefix and finalizer to both `AIActionFindEquipment.Execute` and `AIActionFindBestEquipment.Execute`.

The log is direct evidence that TACC 1.1.1 correctly treated all six changes as unknown. Clearing the log was not used to validate them.

## Core comparison

The installed trait catalog still matches all **50** embedded trait snapshots. All **12** equipment-effect values also match, including the eight worn-gear mood values, tattered threshold, and two trade multipliers.

The retained rc302 `Game.dll` was compared with rc316 and the installed rc316 reference source was reviewed:

- `InventoryItemSlot`, `EquipmentPolicyFilter`, and `StorableObjComp` are unchanged.
- `ItemOwnership` adds a definition-based `TracksOwnership` overload. Its existing origin, pricing, refusal, and wearer-mood resolvers retain their behavior.
- `WornItemMood` and `AINeedEquipment` now maintain naked, second-hand, corpse-origin, and tattered mood through source-scoped permanent effects. Removing a cause removes its effect on a later gear-mood pass instead of waiting for a two-hour timed entry. The base values, per-category count cap, and Optimist/Pessimist adjustment remain unchanged.
- `AIActionFindEquipment` and `AIActionFindBestEquipment` add clearer handling for blacklisted slots and back off completed upgrade searches. Their patch targets and equipment-selection roles remain compatible with TACC.
- The reviewed `ObjData` changes concern body-part display, scars, and surgery reservation handling; `IsTattered` remains compatible.

TACC's wearer-specific comparison still calculates the same complete-loadout mood result. Patch monitoring now includes `MentalHealth.SetPermanentEffect`, its removal/query path, and `MentalEffect.CreatePermanent` so later changes or external hooks on the new Core lifecycle are reported.

## Quantum Storage review

Quantum Storage 1.1.12 DLL SHA-256 is `2CCBE31CE6BAAFB290862CB99E1FF4F693BD25547ACDAA1D2C361B8ED0B61B76`; its module MVID is `21cff6f4-a4cf-4638-9a2b-12e0444ea3db`.

The reviewed prefix stores the acting agent in thread-local scope while Core searches equipment. The paired finalizer restores the previous scope even if the action throws. While that scope is active, Quantum Storage can resolve a stored object through the nearest safe, reachable live endpoint. It does not change clothing origin, wearer mood, TACC policy eligibility, item quality, or Core reservations.

TACC accepts only the four exact snapshots reported for that module: prefix and finalizer on the two equipment-search actions, owner `jleas.quantumstorage`, Harmony priority 400, and no before/after constraints. Any changed module MVID, owner, target, method, priority, or ordering is unknown again and remains visible after clearing.

## Build and static validation

- Production build: **0 warnings, 0 errors**.
- Test build: **0 warnings, 0 errors**.
- Automated checks: **87 passed, 0 failed**.
- TACC DLL: assembly/file **1.1.2.0**, product **1.1.2**, SHA-256 `0CE91B99E10B7EC8554A9FF592C90439C9BEE0CBF83479CB33BA3E50AEB822DD`.
- AUMF remains external at required level **1.4.0**.
- The live mod contains no `Tests` folder; the staging workspace retains the development suite.

## Runtime acceptance still required

Fully restart Stardeus with TACC 1.1.2, Core rc316.3193, and Quantum Storage 1.1.12. Clear TACC's diagnostic log. The six reviewed findings should remain absent. Confirm one ordinary and one Thrifty managed colonist can still choose equipment, including an item stored behind a Quantum Wardrobe endpoint when available. Static/build evidence does not replace this in-game check.
