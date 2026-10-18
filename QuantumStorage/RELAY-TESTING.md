# Storage Relays — compatibility and regression checks

## Core compatibility — 1.3.1 repair retained in 1.3.3

Subsequent gameplay and a fresh 1.3.3 load log cover the restart check, with no Quantum Storage/Relay missing-method failure recorded. The specialized replacement/overflow checks below remain targeted regression checks; they are not all claimed as individually exercised.

- After a future DLL change, restart Stardeus and confirm the installed release (currently Quantum Storage 1.3.3) loads on Core v0.15.0-rc327.3205. Let the main-menu demo tick: the `StorageRelaySys.OnTick` / `SpawnRawMaterial` missing-method warning must not recur.
- Load a separate copy of the existing save; confirm normal Relay balancing and unchanged local/network totals. No save migration is needed.
- If exercising a Relay-to-Quantum material replacement, verify the local share is transferred once and any fallback/overflow drop retains its quantity. Normal nearby-pile merging remains enabled; block-wide merging is explicitly disabled for these recovery drops.
- Check fresh logs separately from external tests. The pre-patch 1.3.0 package is backed up, but its old material-spawning call remains incompatible with rc327; it is not a gameplay workaround on this Core version.

## Historical 1.3.0 family acceptance pass

Keep a separate save from BEFORE placing any of the nine new building types. The backup is 1.2.1; it cannot safely load saves containing definitions introduced in 1.3.0. No existing save file has been edited by this install.

1. Restart and confirm Quantum Storage **1.3.0** loads without Relay initialization errors. Existing advanced Quantum stock/capacities and General Storage Relays should match the prior session.
2. With Quasi-Quantum Storage already researched (or complete Improved Assembly, then that Tier 3 research), confirm all ten build entries. Check each approved four-direction sprite, 1x1 footprint, ports, 12-tile placement/selection outline and 10 kW draw.
3. Check single-unit capacities: General 250, Cabinet 150, Refrigerator 275, Freezer 275, Beverage Cooler 75, Pantry 1300, Weapons Locker 100, Wardrobe 100, Organ Storage 300 and Part Storage 150.
4. Start with a Freezer chain and a Wardrobe chain. Then repeat with every type. Three powered units within 12 tiles should show 3x local capacity; turning off the only bridge must split them into two isolated 1x units plus the sealed bridge. The local share in the off unit must not change. Rejoining restores 3x capacity without adding/removing stock.
5. Place different Relay types together, especially Refrigerator and Freezer. They must NOT extend each other's chain. Adjacent advanced Quantum units must not join a Relay chain either.
6. For all material types, compare group filters to the vanilla counterpart and test hauling/withdrawal. For Refrigerator/Freezer/Beverage Cooler, include native food/drink selection with exhausted/reserved quantities; no zero or duplicate material options should be offered.
7. For objects, store several distinct records of the same type with different quality/history. Include damaged robotic/organic parts and mind-bearing brains. Record exact IDs/quality/condition/mind/ownership history before balancing, splitting, ejection and save/reload; totals alone are insufficient.
8. Let a Being reserve equipment, clothing or a surgery/loading part. Reserved records must keep their physical owner during balancing. A valid nearest connected endpoint should work through Core's native pathing; switching off or severing the chosen route must not bypass Core reacquisition/cancellation. Do not treat one transient Core retry warning as proof of a defect.
9. Eject All from a powered end unit. Only that ACTIVE connected component should eject there. Off units keep their sealed records and have disabled eject/disposal controls. Deconstruction/destruction ejects ONLY the removed unit's local share even while unpowered. Test native relocation separately.
10. Compare Core Global Inventory with unique stored/loose objects, not the sum of repeated connected-panel totals. Offline objects remain owned but unavailable for equipment/surgery/trade; restoring power re-enables normal access.
11. Save/reload with powered chains and again with offline bridges. Verify exact local contents, component counts, capacities, reservation identities and no loss/duplication. This runtime reload is still required: the external tests validate the native save record contents and handover journal, not a live Unity load.
12. Test native replacement of ONE Relay into its matching researched Quantum building. Only its local share should transfer; other Relay shares and existing advanced stock must remain intact. Object replacement follows Core ejection then stores the SAME records at the new Quantum unit. Failed/full handovers leave real items on the floor. Save/reload and compare identities again.
13. Relays must not acquire the mod's extra object-Hive or TACC routes. Core's ordinary material-Hive/local behavior is allowed. Clothing should still move through vanilla Being hauling when applicable.

General Storage's checks and representative build/connection testing across the full family were accepted by the player, followed by Workshop publication. The checklist remains available for targeted regressions; it does not imply every specialized scenario was tested.

## Historical General Storage 1.2.1 checks

- While placing a Relay, confirm the 12-tile outline appears and crosses walls; selecting an existing Relay should show it too.
- In the reported six-unit layout, turning the bridge off should retain a five-unit 1,250-capacity network: the far unit at (290,233) is only sqrt(136), approximately 11.66 tiles, from the cluster unit at (280,227). The offline bridge should show only its local contents / 250 in both panel and tooltip, without a false full warning.
- For a true split, place the far Relay beyond 12 straight-line tiles from EVERY member of the cluster while keeping it within 12 of the bridge. Switching off the bridge should then show 1,000 for the four-unit cluster, 250 for the far unit, and retained local stock / 250 for the offline bridge.
- Repeat switching/disconnection while paused and after save/reload. Check exact material totals, local stock labels, and that offline access remains blocked. Restoring power should reconnect only physically in-range units.

## Full Relay checklist

Keep a separate pre-test save. This introduces a new building; restoring the old DLL is not a safe way to load a save that contains Relays. Use the preserved pre-test save when rolling back.

1. Restart Stardeus. Confirm the installed Quantum Storage version loads and check the fresh log for Quantum/Relay initialization errors. Initialization messages may be omitted by production logging; the version load line and absence of errors are the first checks, not gameplay acceptance.
2. Confirm existing advanced Quantum stock and capacity match the pre-test save before building anything new.
3. Complete **Improved Assembly**, then Tier 3 **Quasi-Quantum Storage**. Check the General Storage Relay build entry, all four rotations, 1x1 footprint, recipe, ports, and 10 kW demand.
4. Build and power one Relay: capacity is 250. Build two more at intervals of 12 tiles: connected capacity should be 750, even if walls separate the rooms. A gap over 12 should break the link. Normal hauling still needs access to the chosen port.
5. Deposit several material types. After a short tick, compare each unit's local share and the connected stock. Global Inventory must count each unit of stock once. Withdraw from different endpoints; watch Core's usual worker and reservation behaviour.
6. Switch off the middle Relay. Its local stock must remain visible as retained but inaccessible. The two ends should become separate 250-capacity networks, with no copying or loss. Eject controls on the unpowered unit should be disabled. Restore power and check reunion.
7. Repeat the split while a Being has a reservation. The pinned local share and reservation owner must not move. A worker whose access unit loses power must not extract from it until Core can proceed or cancels the work normally.
8. Save and reload with the chain connected; repeat with a middle unit offline. Compare exact per-material and global totals before and after. Do not rely on capacity alone.
9. In the test save, remove one unit while powered, then while unpowered: only its local contents should appear at its output. Remaining units retain their stock. Also test moving one unit.
10. Use **Eject All** from an end Relay: all active connected contents emerge at that end, while offline/disconnected units retain theirs. Core should release affected hauling reservations; global stock remains the same, now on the floor.
11. If testing native replacement into a researched Quantum Storage Unit, only the replaced Relay's local share should enter the advanced network. Other Relays stay separate. Verify both old advanced stock and transferred stock survive a save/reload.
12. Native material-Hive hauling may interact with local Relay stock under vanilla rules. No extra Quantum object delivery or TACC routes should enroll a Relay.

Report screenshots, local/network status, exact before/after counts or object identities, and a fresh Player.log for any repeatable mismatch.
