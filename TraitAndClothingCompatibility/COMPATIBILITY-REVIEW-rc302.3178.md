# TACC 1.1.1 compatibility and equipment-scheduling review

Date: **2026-09-24**  
Target: **Stardeus v0.15.0-rc302.3178**  
Previous reviewed Core: **v0.15.0-rc301.3177**  
Framework: **Auto Utilities Mod Framework 1.4.0**

## Supplied findings

The supplied TACC 1.1.0 log reported exactly two changed assembly identities:

- `Game.dll`: `44DF974FE2ACA2CAC330DA09905CCB3FE13EFD86F7BBE45393E7880FFE8059ED`
- `Game.Constants.dll`: `AA626D1DA1702BD4771CE22839A9DA05467667F827F2ED2314E01484ABC1A479`

An isolated regression reproduces those two findings against the published rc301 reference. TACC 1.1.1 records the hashes only after the comparisons below; clearing the diagnostic log is not used as evidence.

## Clothing compatibility comparison

All **50** validated trait snapshots and **12** validated equipment-effect values match TACC's existing references. No trait, clothing mood, tattered cutoff, item-count cap, or provenance trade multiplier changed.

Targeted decompilation found rc302 byte-identical to rc301 for `AINeedEquipment`, `AIActionFindEquipment`, `AIActionFindBestEquipment`, `BeingInventory`, `InventoryItemSlot`, `ObjsSys`, `EquipmentPolicyFilter`, `ItemOwnership`, `WornItemMood`, `MentalHealth`, and `TraitDef`. The two assembly identity changes are outside the reviewed clothing, equipment-policy, and equipment-selection paths.

## Equipment scheduling

Core's unchanged `AINeedEquipment` behavior explains the reported delay:

- A being with some equipment but an empty slot receives an equipment goal at priority **2**.
- A being Core considers naked receives priority **5**.
- Both use interrupt level **1** with a 240-tick grace period, and the find/equip actions are rest actions.
- Because ordinary work commonly also uses interrupt level 1, the equipment goal normally waits for the current task to finish. The individual **Best Available** policy controls selection after the goal runs; it does not raise the goal's scheduling priority.

TACC 1.1.1 changes scheduling only for TACC-managed worn slots:

- An empty non-manual worn slot raises the existing Core equipment goal to priority **5**, making it the next personal task.
- A completely naked managed being may use interrupt level **2** after Core's existing 240-tick grace period, but only when a compatible clothing item was available when that equipment-goal attempt began.
- Filled-slot upgrades and replacements retain Core's priority 2 and interrupt level 1.
- Core still owns candidate filtering, reservations, reachability, pathing, the equip action, life-support safety, cooldowns, and failure recovery.
- Weapons and manual slots are unchanged.

The availability check runs only when a naked being's equipment goal starts or restarts. It does not add a recurring loose-object scan.

## Validation

- Production Release build: **0 warnings, 0 errors**.
- Test Release build: **0 warnings, 0 errors**.
- Automated checks: **86 passed, 0 failed**.
- DLL version: assembly/file **1.1.1.0**, product **1.1.1**, SHA-256 `CA1BA74A4DAE5AF42C5142E99870FC7A2742114D24AE9A327FDF737B3CD12E58`.
- The supplied rc302 findings are reproduced against the rc301 hashes and absent against the reviewed rc302 hashes.
- Live and staging source parity and the final package boundaries were checked after the final build.
- Development tests remain in staging only; the live mod and Workshop package omit the entire `Tests` folder.

On **2026-09-24**, the user confirmed the complete 1.1.1 behavior set in game: beings equip themselves much faster, forbidden loose equipment can be selected when allowed, wearer-specific mood ranking works, and clearing the rc302 diagnostic log leaves zero current findings. This accepts the 1.1.1 gameplay and compatibility baseline. Workshop publication has not yet been reported.
