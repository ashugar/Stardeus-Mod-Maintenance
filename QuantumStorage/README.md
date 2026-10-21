# Quantum Storage

Quantum Storage provides ten compact late-game storage endpoints. Matching endpoints share capacity and contents through a type-specific network. Each storage type remains separate, so its accepted materials and stored records retain their original purpose.

The first endpoint supplies a fixed initial capacity. Each additional endpoint of the same type doubles the network total.

## Current release — 1.3.9 optional panel diagnostics

Adds a default-off, bounded diagnostic capture for the sustained material-panel slowdown. The latest 1.3.8 log still records native-panel fallback on rc342.3224. The player reports about 75 FPS closed and 12–61 FPS open, averaging about 40, with recurring line additions/removals. Their frequency and cost have not yet been measured.

An empty local `PanelDiagnostics.enabled` file in this mod's installed folder opts in on the next game start. It is excluded from the repository and release package. Diagnostics report safe guard stages, application frame intervals, inclusive panel-call timing, and bounded raw key/quantity-change counters. They never log material names, quantities, entity IDs, player data or PC paths. The capture starts after entering the first loaded game, ends after three minutes or unload, and cannot restart in that process. Detailed sampling also stops at its work/overhead limits. Remove the marker before the next ordinary session to disable it.

The compatibility verdict, approval constants, row cache, native notifications and gameplay behavior are unchanged. This is an investigation build; no FPS fix, in-game capture result or Workshop publication is claimed. Follow [UI-PERFORMANCE-TESTING.md](UI-PERFORMANCE-TESTING.md) for the short capture sequence.

## Previous release — 1.3.8 material-panel contract guard

- Keeps material-panel row reuse enabled across harmless metadata-token renumbering. The startup guard compares resolved instructions, member identities, signatures, locals, exception handling and the fields used by the optimization; it does not target a game version or whole Core DLL hash.
- Reviews the current Core v0.15.0-rc342.3224 panel builder and all three action callbacks. Changed or unresolvable inspected contracts retain the complete native panel. This finite check does not establish arbitrary semantic equivalence or inspect every native helper, prefab or other mod.
- Preserves the existing row cache and native list, quantities, sorting, tooltips, ejection, filters and lifetimes. No per-refresh hashing, refresh delay, inventory, save-schema, hauling, Relay, recipe, research or capacity change.

The subsequent 1.3.8 log confirms loading and native-panel fallback. The reported sustained FPS drop remains unresolved. A successful optimized Mono startup and detailed affected controls remain unconfirmed; no Workshop publication is claimed. Historical unconfirmed scenarios remain coverage limits. See [COMPATIBILITY-TESTING.md](COMPATIBILITY-TESTING.md) and [UI-PERFORMANCE-TESTING.md](UI-PERFORMANCE-TESTING.md).

## Previous release — 1.3.7 Core rc341 material-panel compatibility

- Restores material-panel row reuse for reviewed Core v0.15.0-rc341.3223 after its changed fingerprints caused 1.3.6 to use the complete native panel.
- Re-reviews the native contents builder and all three action callbacks. Core retains the full list, current quantities, sorting, tooltips, ejection, filters and panel lifetimes; unknown contracts still use the complete native panel.
- Changes only the four reviewed fingerprints and release metadata. No row-cache algorithm, inventory, reservations, saved ownership, Relay rules, recipes, research or capacities change.

The subsequent user test still averaged about 30 FPS with the panel open, occasionally exceeding 50 FPS. Its fresh log loaded 1.3.7 on the newer rc342.3224 and recorded native-panel fallback. All four raw hashes changed while the captured resolved builder/callback instructions and body settings matched rc341. No enabled-optimization FPS result or Workshop publication of 1.3.7 is established.

## Previous release — 1.3.6 Core rc337 material-panel compatibility

- Restores material-panel row reuse for reviewed Core v0.15.0-rc337.3219 after the compatibility guard correctly fell back to the complete native panel.
- Reviews the contents builder and all three native action callbacks, preserving Core's current general forbidden-item policy after individual ejection.
- Keeps the full list, immediate quantities, sorting, tooltips, filters, Eject All and Relay offline controls. No refresh throttling, inventory, save-schema, capacity or gameplay changes.
- Player-confirmed on 2026-10-19: opening the Quantum Storage Unit panel caused only a slight initial FPS dip, then performance recovered to its starting level with the panel open. The sustained open-panel slowdown is resolved in that observed test; no controlled numeric benchmark or blanket gameplay acceptance is claimed.
- A fresh rc337.3219 log confirms 1.3.6 loading, with no matched Quantum Storage exception or material-panel fallback warning. The player also confirmed Workshop publication of 1.3.6 on 2026-10-19.

The narrow panel compatibility fix is accepted and published. Detailed panel controls, object-Hive transfers, Relay transitions and save/reload edge cases remain separately tracked, not reported defects. See [COMPATIBILITY-TESTING.md](COMPATIBILITY-TESTING.md) and [UI-PERFORMANCE-TESTING.md](UI-PERFORMANCE-TESTING.md). This documentation closeout retains version 1.3.6 and its tested DLL unchanged.

## Previous release — 1.3.5 Core rc335 hauling and panel compatibility

- Repairs Quantum's extra object-Hive hauling after Core removed its old weighted-distance helper. Each object's eligible destination is chosen by Core's strict storage priority, then squared distance and a stable endpoint tie-break.
- Selection between ready hauling jobs uses the bounded existing picker with geometric two-leg trip distance and stable goal ties. This intentionally replaces the old weighted-priority contribution; storage priority does not dominate unrelated jobs.
- Re-enables material-panel row reuse on reviewed Stardeus v0.15.0-rc335.3217. Compatibility checks now cover the contents method and its action callbacks; unknown contracts retain Core's complete panel.
- Material panels reuse Core's existing rows for live quantity and sorting updates, retaining the complete contents list, native tooltips and ejection controls. No reduced refresh rate or truncation.
- Includes 1.3.2's incremental object-network counts, removing the global object recount after each normal deposit while preserving authoritative recovery.
- Retains the 1.3.1 Core material-spawning compatibility fix. No save migration, recipe, capacity or network-rule changes.

Player-confirmed on 2026-10-17: the Quantum Storage Unit panel showed no visible desynchronization or noticeable FPS drop after the 1.3.5 fix. This confirms no visual/performance regression in that observed panel test, not a measured FPS gain or completion of every UI interaction. The player subsequently confirmed Workshop publication of 1.3.5. That acceptance does not cover later Core builds or 1.3.6; actual object-Hive delivery, interruption/save-reload and detailed panel controls remain separate acceptance checks. See [COMPATIBILITY-TESTING.md](COMPATIBILITY-TESTING.md) and [UI-PERFORMANCE-TESTING.md](UI-PERFORMANCE-TESTING.md).

## Storage Relays

All ten precursor Relay types are enabled. General Storage's placement preview, local/network display, true chain splitting/rejoining, isolated ejection and deconstruction have been confirmed in game. The full family passed representative build/connection testing, was accepted by the player and published as 1.3.0. This does not claim every specialized object/save edge case was exercised.

| Relay | Local capacity | Vanilla recipe basis | Improved Microchips |
| --- | ---: | --- | ---: |
| General Storage | 250 | Storage Mini; highest general-storage research Tier 2 | 2 |
| Cabinet | 150 | Cabinet, Tier 1 | 1 |
| Refrigerator | 275 | Fridge, Tier 1 | 1 |
| Freezer | 275 | Freezer, Tier 2 | 2 |
| Beverage Cooler | 75 | Beverage Cooler, Tier 2 | 2 |
| Pantry | 1,300 | Large Pantry, Tier 2 | 2 |
| Weapons Locker | 100 | Weapons Locker, Tier 1 | 1 |
| Wardrobe | 100 | Steel Wardrobe, Tier 1 | 1 |
| Organ Storage | 300 | Organ Storage, Tier 1 | 1 |
| Part Storage | 150 | Part Storage, Tier 2 | 2 |

Recipes retain the vanilla counterpart's basic ingredients and add the listed Improved Microchips (`Microchip2`); Organ Storage's original basic Microchip is replaced by the Improved Microchip. No Relay recipe requires Nanobots. General Storage retains its accepted 4 Steel Plates, 1 Gear and 2 Improved Microchips recipe.

- Footprint: 1x1, four rotations. Each type contributes the fixed local capacity listed above, additive across its connected Relays.
- Link range: 12 tiles, centre-to-centre Euclidean distance, ignoring walls. Links are automatic and transitive between powered units of the same Relay type.
- Core's range outline is shown while placing a construction ghost and when selecting a Relay. A diagonal direct link can bypass a switched-off bridge if its straight-line distance is still within 12 tiles.
- Power: 10 kW per unit. An off, disconnected, under-supplied, relocating, or unusable unit drops out of the active chain. Its local contents remain saved but inaccessible until restored.
- Construction: 4 Steel Plates, 1 Gear, 2 Improved Microchips (`Microchip2`). This uses the compact vanilla storage's basic recipe and two chips for the highest vanilla general-storage tier in Tier 1–3 (Tier 2).
- Research: Tier 3 **Quasi-Quantum Storage**, requiring Improved Assembly. Cost matches current Advanced Assembly: 6 Synthetic Datoids and 11 High-Tech Datoids.
- Relays and advanced Quantum networks never share an inventory. Existing Quantum capacities, contents, research, object records, Hive scheduling, and optional runtime-only TACC integration are retained.

The six material types keep their own Core `MatStorage` contents and reservation books. The four object types keep each original Core `ObjData` record with one physical `StorageId`; there are no replicated inventory or mind records. Both adapters share the same range graph, per-kind allocation policy and local/network status format. Adding another building of either supported inventory kind requires definition data, not another network implementation.

Active endpoints expose connected stock to Core's normal source selection. Equipment/body-part routing reuses the existing nearest usable endpoint helper while retaining Core's path, safety, blacklist, adjacency and reservation checks. Unreserved material types and whole object records balance across their own active network; reservations pin stock to its physical owner. A material reservation cannot own more than that unit's fixed local space; normal short-load handling remains Core-owned. Rebalancing processes at most one dirty network per tick. The object index refreshes on relevant storage mutations, not by scanning all hauling tasks. Fridge and Freezer chains remain separate despite accepting the same material groups.

Turning off a bridge splits the chain without transferring its sealed contents. Removing a Relay ejects only its local share, including when unpowered; surviving shares remain stored. Native relocation moves only the relocated unit's share. Native completed replacement into the matching advanced Quantum building transfers only that Relay's share. A small saved handover journal protects the queued replacement interval. Material transfers retain their outstanding quantities; object transfers track only IDs of the original records ejected by Core during replacement. Failed/full object handovers leave those actual objects on the floor. They never create duplicate records or reclaim an object that has already been moved into another device or inventory.

**Eject All** empties the active connected component at the selected Relay's normal output using Core's ejection and reservation lifecycle. Offline ejection/disposal controls are unavailable. Single-material ejection preserves reserved stock. Object filters retain Core's quality and ownership-history controls. The panel and tooltip distinguish local share from connected capacity. Global inventory and trade accounting count stock once; offline objects are not offered as available equipment, surgery parts or trade goods.

Relay components are a distinct type from `QuantumStorageNode`. They do not enter the mod's extra object-Hive or TACC hauling routes. Existing native material-Hive interaction remains local to the physical Relay, subject to power and native range/path checks.

The 1.3.1 Core compatibility repair is retained in restart-tested 1.3.3; it does not change the accepted Relay mechanics. See [RELAY-TESTING.md](RELAY-TESTING.md) for targeted regression checks. Use a separate save for rollback; 1.2.1 does not contain the nine new building definitions. The one-off Core material reacquisition warning has intentionally not been investigated or changed.

## Material networks

Version 1.3.3 introduced the **material contents panel** optimization for both advanced Quantum endpoints and material Relays. Core still constructs every row and owns its click actions, quantity choices, tooltips and Eject All button. When the set of material types is unchanged, the mod updates changed quantity labels and moves existing rows into Core's current sort order instead of recreating the whole list. New/removed types, disposed rows, changed names/icons or a new panel lifetime use Core's full rebuild. There is no truncation, pagination or refresh delay; capacity/header, filter and priority bindings are unchanged. Version 1.3.8 checks the resolved panel contract once during startup, ignoring incidental metadata renumbering. An unknown inspected contract or unexpected runtime failure falls back to the complete native panel. Object-storage panels are unchanged. See [UI-PERFORMANCE-TESTING.md](UI-PERFORMANCE-TESTING.md).

| Endpoint | Accepted Core group | First endpoint capacity |
| --- | --- | ---: |
| Quantum Storage Unit | General raw materials | 500 |
| Quantum Cabinet | Upgrade modules | 100 |
| Quantum Freezer | Food, raw food, stem cells, and ice | 1,000 |
| Quantum Fridge | Food, raw food, stem cells, and ice | 200 |
| Quantum Beverage Cooler | Drinks | 50 |
| Quantum Pantry | Inedible raw food | 600 |

The Freezer and Fridge use separate networks although they accept the same Core material groups. A Quantum Pantry serves the group used by both the vanilla Pantry and Large Pantry.

## Object networks

Since version 1.3.2, the mod maintains a runtime-only count of original Core object records in each advanced Quantum network. Normal deposits, withdrawals and destruction update membership without recounting the global object collection. Membership updates are idempotent and follow actual record state, including operations that change state before reporting failure. Load/attachment, topology recovery, explicit ownership rewrites and detected inconsistencies reconcile from Core. Eject All and disposal defer display refresh while keeping full-state admission current. Direct enumeration reconciles before Core sizes its buffer; there is no added periodic world scan. Saved ownership counts still come directly from Core, and no derived inventory is saved. The count optimization does not change Relay inventory or material-network mechanics. See [PERFORMANCE-TESTING.md](PERFORMANCE-TESTING.md) for recorded results and optional controlled-comparison/regression checks.

| Endpoint | Accepted Core group | First endpoint capacity |
| --- | --- | ---: |
| Quantum Weapons Locker | Weapons | 25 |
| Quantum Wardrobe | Clothing | 25 |
| Quantum Organ Storage | Organic body parts | 500 |
| Quantum Parts Storage | Robotic body parts | 500 |

Each object-storage type has an independent network. Urns continue to use Core's memorial-storage system, which retains the identity associated with stored ashes.

## Capacity and endpoint behavior

- Every Quantum endpoint occupies one tile.
- Initial capacity follows the table above.
- Each matching endpoint doubles the total capacity of its network.
- Capacity stops at 2,000,000,000, which conforms to Core's integer storage contract.
- Removal of a non-final endpoint retains the shared contents.
- An overfull network refuses new deposits until sufficient space becomes available.
- Removal of the final endpoint ejects the contents through Core's standard storage process.

## Core storage integration

Material endpoints remain registered Stardeus storage devices. Core controls their groups and filters. Haul priorities, reservations, and reachability also use standard game systems.

Material-group option lists from attached Quantum endpoints contain each available material once. The shared reservation ledger limits each amount; exhausted options are removed before Core chooses food, drinks, or other grouped materials. Ordinary storage queries retain Core behavior.

Object endpoints retain every item as an individual Core record. Weapons and clothing keep their identity. Organs and robotic parts retain quality, condition, body-part state, and related data. Each matching endpoint displays the common contents and provides Core's ejection command.

Removal of a non-final object endpoint transfers storage ownership to another endpoint in the same network. The original item record remains intact.

## Nanobot Hive object delivery

Quantum Storage adds one-way Nanobot delivery for loose weapons, clothing, and body parts. A working Hive may carry an eligible object into the matching powered network under these conditions:

- Core's `Nanobots` research is complete.
- Weapons and clothing require no additional research.
- Robotic parts require `Basic Bot Parts` research.
- Organic parts require `Basic Synthetic Organs` research.
- The destination has capacity and a nonzero haul priority.
- Its active filter accepts the object.
- The Hive can reach both locations under Core's range and area rules.

These conditions use existing Core research. Delivery accepts active, loose objects as sources. Stored Quantum contents remain under the normal extraction and ejection systems.

Part Restoration Unit coordination prevents simultaneous assignment of one Hive. Each mod retains ownership of its work.

The scheduler separates new or changed goals from dormant work. Ready candidates are grouped by serviceable area. Every Nanobot tick has fixed processing limits, and a rotating reconciliation pass returns eligible dormant work to consideration. Final assignment rechecks the source and destination. Reservation and carrier state are also verified.

## Nanobot Hive boundary-floor replacement

A Nanobot Hive can service a floor replacement beneath a constructed wall or door from an adjacent room. A Quantum Storage Unit can supply the requested material through Core's normal Nanobot source selection.

- Core creates and owns the normal replacement task.
- Core selects an eligible material source and retains its material reservation process.
- Hive range, room coverage, and material availability remain required.
- After material delivery, the Hive can complete the existing replacement task from the same adjacent room.
- Floor replacement away from a wall or door continues to use Core's normal reachability rules.

## Optional TACC Washing Machine delivery

When Trait and Clothing Compatibility is installed, Nanobot Hives can service its Washing Machines through Quantum Wardrobes.

- A Hive can remove a washable clothing item from a Quantum Wardrobe and deliver it to the Washing Machine that requested it.
- A Hive can return clothing discharged by a Washing Machine to an eligible Quantum Wardrobe, including clothing returned without completing a wash.
- The Washing Machine must create the normal haul task before a Hive can accept the delivery.
- The Quantum Wardrobe must have capacity, a nonzero haul priority, and a filter that accepts the washed item.
- The Hive must cover the source and destination under Core's range and area rules.
- The route uses the existing `Nanobots` research gate.
- Ordinary workers remain eligible for the same haul tasks.

The integration uses runtime discovery. Trait and Clothing Compatibility is not a dependency, and Quantum Storage continues to load when it is absent. Quantum Storage, Core hauling, and Part Restoration Unit share Hive assignment state to prevent simultaneous use of one Hive.

## Construction and artwork

Each endpoint uses a distinct design within a common industrial style. Blue-gray machinery and cyan lighting identify the device family. Black-violet containment elements distinguish its Quantum function. All rotational views retain a neutral one-tile silhouette.

Current material endpoints use one construction recipe:

- 4 Titanium Plate
- 3 Advanced Microchip
- 3 Gold Wire
- 4 Optical Fiber
- 2 Nanobots
- 900 construction work

## Power and environment

Each powered endpoint consumes 25 kW. Energy use remains independent of the stored quantity.

Quantum material endpoints operate without a Safe Area requirement. Ambient temperature does not disable material storage because the contents use the shared network state.

## Damage feedback

Fire or explosion damage can transfer part of a hit to another active endpoint in the same network.

- Fire rolls once per continuous incident at 2%.
- Each explosion hit has a 5% transfer chance.
- One event selects a single active, powered peer.
- The peer receives 20% of the original hit as direct Energy damage.
- Transferred damage cannot produce another feedback event.
- A Fuse at the source cancels one successful transfer.

Feedback requires a second available endpoint. A notification and ship log entry identify a transferred or Fuse-cancelled event.

## Internal structure

Material networks share Core's material dictionary and reservation object. Object networks aggregate native storage ownership identifiers while each object remains in Core's global registry. An object's single ownership identifier is its serialization anchor, not its only physical access point. Runtime consumers resolve a live endpoint through the object network instead of treating that identifier as a location.

The network identifier defines each pool. Common systems manage capacity and interface queries. They also handle ejection, relocation, removal, and save data for every endpoint of the same storage medium.

## Compatibility and save behavior

Version 1.3.8 reviews installed Core `v0.15.0-rc342.3224`. It retains 1.3.5's `IsBetterStorage` selection rather than treating regional distance as tile distance. Existing research, filter, power, range, task and reservation checks remain authoritative. Stored object identities, transfer cleanup, the save format and optional TACC/Part Restoration Unit coordination are unchanged. The material-panel guard compares the resolved native builder, action callbacks and consumed fields once at startup; harmless token renumbering is accepted, while an unverified inspected contract retains the complete native panel. This review does not certify every behavior in older or later Core builds.

Version 1.3.1 targets installed Core `v0.15.0-rc327.3205`. Core extended the `SpawnRawMaterial(Mat, ...)` signature with `mergeIntoBlock`. Both Relay upgrade-recovery calls explicitly pass `false`, preserving normal nearby-pile merging without opting into block-wide deconstruction merging. The old binary could fail before the recovery method's early return, interrupting the Relay tick even in the main-menu demo. Save formats, inventories, capacities, reservations and network rules are unchanged. Restart after installing the rebuilt DLL; no save migration is required.

Version 1.1.15 corrects the totals used by Core's global "Material storages full" warning when Quantum material endpoints are present in that check. Each shared network contributes once. Totals use wide arithmetic and saturate safely for Core's warning comparisons. Core still owns the notification and its 100-free-space clearing threshold. Inventory contents, endpoint capacities, reservations and hauling are unchanged.

Version 1.1.14 targets the four-argument damage implementation in Core `v0.15.0-rc320`. The older three-argument method forwards to that implementation. The hook runs once per hit and no longer fails initialization because of the added overload.

An initialization failure in 1.1.13 can leave matching endpoints disconnected and show empty local inventories. Existing material-network snapshots and Core object records remain the recovery sources. Keep a pre-failure save, restart with the corrected build, and verify contents and capacity before saving under a new name. This update does not change the save format or manufacture replacement inventory.

The material pool is saved once by the mod system. One vanilla storage-component copy is retained as a recovery record. Global inventory totals count each shared network once. The same rule applies to endgame wealth removal.

Core `v0.15.0-rc275` added a cached used-capacity value to material storage components. Quantum Storage synchronizes that cache and sends the associated interface notifications to every endpoint after a shared-pool change. Existing saves reconstruct their totals from the authoritative network during attachment.

Stardeus `v0.15.0-rc285` renamed the Being safety query used by material-availability checks. Quantum Storage uses `IsActuallySafeAt`, which matches `InventorySys.HasEnough`.

Core serializes object contents once as individual records. Quantum object endpoints save their ownership counts for validation, then restore network capacity after object and storage registration.

When a Being withdraws a stored object, Quantum Storage resolves the nearest safe, reachable endpoint in that object's matching network. The object's serialized owner is left unchanged unless Core successfully removes it from storage. Equipment searches, device loading, and organic or robotic surgery all use the same resolver.

## Change Log

### 1.3.8 — Material-panel contract guard

- Replace raw-byte fingerprints with resolved-contract checks so harmless compiler metadata renumbering does not disable row reuse.
- Review the current rc342 builder, complete action callback set, fields, locals and exception handling. Keep complete native fallback when verification fails.
- Preserve the existing cache and native controls without per-refresh checks, gameplay or save changes. Fresh Mono startup, FPS/control acceptance and Workshop publication remain pending.

### 1.3.7 — Core rc341 material-panel compatibility

- Rebaseline the existing fail-closed panel guard after reviewing the builder and all three action callbacks against rc341.3223.
- Preserve native panel actions, full immediate contents and the existing row-reuse policy; unknown contracts retain the complete native panel.
- No inventory, reservation, save, Relay, research, recipe or capacity changes. The later rc342 session loaded 1.3.7 but fell back again; no enabled-optimization FPS/control acceptance or Workshop publication is established.

### 1.3.6 — Core rc337 material-panel compatibility

- Rebaseline the existing fail-closed panel guard after reviewing the builder and all three action callbacks against rc337.3219.
- Preserve Core's general forbidden-item handling and inventory notification for ejected piles; retain complete immediate UI updates and native structural rebuilds.
- No stock, reservations, saved ownership, Relay behavior, research, recipes or capacities changed.
- On 2026-10-19, the player confirmed the sustained panel slowdown was resolved, with only a slight opening dip before FPS recovered, and confirmed Workshop publication. Fresh rc337.3219 load evidence has no matched Quantum Storage exception or panel-fallback warning. Detailed gameplay regression checks remain separately tracked; the documentation closeout does not change the version or DLL.

### 1.3.5 — Core rc335 object-Hive and material-panel compatibility

- Replace the removed object-Hive destination scorer with Core's strict priority-first destination comparison, using distance and stable endpoint IDs for ties. Between candidate jobs, retain bounded geometric trip-distance selection and stable goal ties; the former priority-weighted trip score is intentionally replaced.
- Restore the reviewed rc335 material-panel optimization with callback-aware compatibility checks. Preserve every native row, current quantity choices, sorting, tooltips, ejection, filter lifecycle and Relay offline button handling; unknown contracts still use the complete native panel.
- Preserve inventory records, reservations, capacities, network rules, transfer lifecycle, save schema, recipes, research and optional integration ownership.
- Player-confirmed on 2026-10-17: the Quantum Storage Unit panel remained visually synchronized with no noticeable FPS drop. No measured FPS gain or blanket gameplay acceptance is claimed; hauling, save/reload and detailed UI interactions remain separately tracked. Documentation and package preparation retain version 1.3.5 and its tested DLL; Workshop upload remains user-controlled.

### 1.3.4 — Core rc332 material-panel compatibility

- Reviewed the installed rc332.3211 panel and generated callbacks, then updated the exact method-body compatibility fingerprint. Unreviewed Core panels still use the complete native list.
- Core still builds the original rows, tooltips and actions. Reused rows read current ejection quantities when clicked and retain Core's recycled-row ejection fix. Filter controls remain entirely native.
- No changes to inventory ownership, reservations, capacities, network/Relay behavior, object-count maintenance, save formats, recipes or research.
- Fresh startup and gameplay acceptance pending; previous 1.3.3 performance results are historical evidence only.

### 1.3.3 — Performance update: native material-panel row reuse

- Preserve Core-created buttons, tooltips, full quantities, all sorting choices and individual/network ejection. Reuse intact rows for live quantity and sort-order changes; defer nothing and hide nothing.
- Apply one shared adapter to Quantum material storage and material Relays only. Native storage, object panels, inventory mutation, reservations, capacities, saved data and the 1.3.2 object-count implementation are unchanged.
- Validate row identity/hierarchy and bind cache cleanup to Core's contents lifetime. Rebuild normally for structural changes; use the full native panel when the reviewed Core method changes or the optional optimization fails.
- Player-confirmed on 2026-10-11: no appreciable FPS drop while the material panel is open. Fresh 1.3.3 load evidence has no material-panel fallback or Quantum Storage exceptions recorded. External row-policy and regression tests passed; detailed interaction edge cases are not claimed as individually tested.
- Object delivery averaged 0.101169 ms across 1,068 calls in the 1.3.2 log, and 0.10003 ms across 995 calls in the 1.3.3 log, versus 2.985 ms in the earlier handoff. These are separate sessions, not a controlled FPS comparison. No automatic Workshop publication.

### 1.3.2 — Incremental Quantum object counts

- Removed the per-deposit global recount for advanced Quantum weapon, wardrobe, organic-part and robotic-part networks. Runtime membership references original Core records; no objects are copied or reconstructed.
- Observe real before/after membership for store, unstore and destroy; duplicate/nested callbacks cannot double-count. Reconcile uncertain/exceptional mutations, endpoint topology changes and native validation from Core.
- Preserve cross-endpoint access, reservations, saved ownership and fixed/exponential capacities. Relays and material networks are unchanged, including the 1.3.1 `mergeIntoBlock` fix.
- Retain nested bulk deferral for Eject All and extend it to native disposal operations. Recount at enumeration entry prevents an unseen undercount from truncating Core's buffer; normal deposits do not enumerate global objects.
- External lifecycle/regression checks passed. Subsequent 1.3.2/1.3.3 logs recorded substantially lower object-delivery costs; exact same-condition timing and specialized object/save edge cases remain distinct from that evidence. Included in the 1.3.3 performance release; no automatic Workshop publication.

### 1.3.1 — Core material-spawning compatibility

- Rebuilt against Core `v0.15.0-rc327.3205` and updated the two Relay recovery drops to the current `SpawnRawMaterial` signature with explicit `mergeIntoBlock: false`.
- Preserved existing drop/nearby-merge behavior, saved handover quantities and all network mechanics. No inventory migration or other-mod changes.
- The 1.3.0 gameplay acceptance and Workshop publication are recorded below. Subsequent 1.3.3 gameplay and fresh load evidence cover the restart check with this repair retained; the full replacement/overflow checklist remains available. No automatic Workshop publication.

### 1.3.0 — Complete Relay family

- Enabled Cabinet, Refrigerator, Freezer, Beverage Cooler, Pantry, Weapons Locker, Wardrobe, Organ Storage and Part Storage Relays with the approved capacities and four-direction artwork.
- Shared one range graph and allocation policy between material and individual-object adapters; no per-building runtime logic.
- Preserved original object identities, quality, condition, minds and reservation records while distributing local ownership.
- Added connected object views, power-gated access, nearest usable endpoint routing, native ejection/removal, local-only relocation and matching Quantum replacement handovers.
- Wait for Core object restoration before indexing or updating local counters; persist physical ownership, never connected counts.
- Kept advanced Quantum definitions, pool logic, research and extra Hive/TACC integrations unchanged.
- General Storage's reported acceptance checks remain established. The full family subsequently passed representative build/connection testing, was accepted by the player and published to the Workshop. Specialized save/reload and object edge cases were not individually claimed as tested.

### 1.2.1 — Relay range preview and capacity display

- Added Core-native 12-tile placement/selection outlines, unaffected by walls.
- Refresh all capacity bars when the connected graph changes, including offline endpoints.
- Relay capacity tooltips read current network/local totals instead of cached bar labels. Offline access is no longer displayed as a full inventory unless the retained local share is actually full; intake and reservation gates are unchanged.
- Switch/move messages invalidate the graph read cache, and hovering capacity rechecks connectivity even while paused.
- Verified the reported six-Relay layout from saved positions: the far unit has a direct 11.66-tile link to the cluster. Five active units correctly retain 1,250 capacity. No range or balancing change was needed.
- No inventory migration, recipe, sprite, reservation, advanced Quantum network, or Nanobot/TACC changes. Build checks are separate from in-game acceptance.

### 1.2.0 — General Storage Relay test candidate

- Added only General Storage Relay and Tier 3 Quasi-Quantum Storage research.
- Added reusable, type-isolated range topology and reservation-aware additive allocation.
- Kept per-unit Core inventory saves; added power-off isolation, network ejection, local destruction/relocation, and durable matching-Quantum replacement handover.
- Added endpoint-aware accounting and local/network status feedback. New Relay hooks initialize as a separate transaction so their failure cannot remove established Quantum hooks.
- Left all existing advanced Quantum definitions and the 40 approved Relay sprites unchanged. Freezer Relay, Wardrobe Relay, and the remaining variants are not enabled yet.
- Build/static and isolated Core-data tests are separate from the required in-game acceptance pass.

### 1.1.15

- Fixed a false "Material storages full" warning caused by repeatedly adding large shared Quantum capacities into Core's 32-bit totals.
- Counted each eligible Quantum material network once in the warning check and used overflow-safe aggregation alongside ordinary storage.
- Preserved Core's storage eligibility, notification, fanfare and 100-free-space clearing threshold. Checks without eligible Quantum endpoints retain Core's original totals.
- Kept the correction local to this warning. No inventory, save format, hauling, reservation, routing or endpoint-definition changes.

### 1.1.14

- Fixed the damage hook's ambiguous match after Core rc320 added a `TakeDamage` overload. It now targets the four-argument implementation, including calls forwarded from the compatibility overload.
- Added the loaded assembly version to the successful initialization log entry.
- Reviewed all 50 Core patch targets and 260 referenced Core/utility members against the installed build. Shared capacity, material snapshots, object ownership, reservations, bounded Nanobot scheduling, and runtime-only TACC support retain their existing behavior.
- Kept all endpoint definitions and saved-inventory formats unchanged. Fresh in-game reload and inventory verification remain separate from the build and audit checks.

### 1.1.13

- Removed zero, negative, and duplicate entries from Core's material-group option lists for attached Quantum endpoints.
- Rechecked option amounts against the matching network's unreserved stock, including stacks above Core's reservation shortcut threshold.
- Applied the correction to all six material networks through one explicitly targeted Core overload. Core continues to select and reserve materials through its existing tasks.
- Kept network contents, reservation ownership, save format, object routing, and optional TACC integration unchanged.

### 1.1.12

- Routed Being withdrawals through the nearest safe, reachable endpoint in the item's Quantum object-storage network.
- Applied the shared endpoint resolver to weapons, clothing, organic parts, and robotic parts instead of adding storage-specific routing code.
- Ranked optional TACC Washing Machine sources by Wardrobe-to-machine distance while retaining Hive reachability as an eligibility requirement.
- Kept the serialized owner, object reservation, Core task, and save format unchanged while an alternate endpoint is used.
- Retained Core's existing nearest-endpoint behavior for all six material-storage networks, which already expose their shared inventory through every matching endpoint.

### 1.1.11

- Added a shared object-network resolver that separates an item's serialized owner from the endpoint used to access it.
- Allowed optional TACC transfers to withdraw clothing through any live Quantum Wardrobe in the item's network.
- Applied bounded, rotating endpoint selection to Washing Machine delivery and washed-clothing return routes.
- Revalidated the selected endpoint immediately before extraction while preserving the original item record, reservation, and Core task.
- Removed the temporary 1.1.10 reachability diagnostics after identifying the owner-versus-access defect.

### 1.1.10

- Added transition-only diagnostics for optional TACC Washing Machine reachability.
- Reported whether a wash task was discovered and tracked, which lifecycle gate rejected it, and whether one working Hive can reach both stable endpoints.
- Bounded diagnostic discovery, goal retention, Hive checks, and Quantum Wardrobe node checks.
- Left Core task ownership, reservations, Nanobot scheduling, transfer eligibility, and save data unchanged.

### 1.1.9

- Retained the original Quantum Wardrobe and Washing Machine endpoints after a reachable TACC task is assigned to a Being.
- Revalidated the stable endpoint pair against live Hive range and area connectivity instead of the Being's moving position.
- Persisted up to 256 bounded reachability proofs across save and load.
- Kept Core task ownership, reservations, filters, and Nanobot assignment restrictions unchanged.

### 1.1.8

- Separated optional TACC reachability proof from task ownership and transfer eligibility.
- Kept a Washing Machine task marked reachable while its assigned Being carries the clothing through live Hive coverage.
- Recovered that display state after loading an already-assigned task previously marked by Quantum Storage.
- Kept Core's Being assignment, object reservation, and Nanobot start restrictions unchanged.

### 1.1.7

- Separated optional TACC reachability reporting from the eight-Hive assignment slice.
- Added a bounded 256-pair rotating proof pass so a reachable Washing Machine task remains marked reachable while another Hive slice is scheduled.
- Cached at most 256 proven TACC goals and revalidated them against the live Hive list without changing assignment or transfer behavior.

### 1.1.6

- Completed TACC Washing Machine haul tasks with Core's `IsObjLoaded` outcome after Nanobot delivery.
- Removed the stale `Unreachable by Nanobots` status once clothing reaches the machine and washing begins.
- Kept the separate `IsObjStored` outcome for washed clothing returned to a Quantum Wardrobe.

### 1.1.5

- Recovered optional TACC wash tasks that remained Nanobot-compatible after their clothing became equipped during an earlier interrupted haul.
- Cancelled only orphaned, workerless tasks whose clothing is still assigned to that task or has no reservation.
- Preserved equipped clothing, unrelated reservations, ordinary hauling, and bounded Nanobot scheduling.

### 1.1.4

- Corrected optional TACC Nanobot reachability reporting when a covering Hive is occupied by Core work.
- Reachable tasks remain pending until the Hive can accept the transfer. Core-first assignment and bounded candidate checks remain unchanged.

### 1.1.3

- Corrected optional TACC Nanobot save recovery when a saved transfer goal no longer exists.
- Avoided reservation ownership checks with an unresolved goal.
- Released only a stale reservation whose saved goal ID matches the recovered transfer.
- Preserved unrelated Core reservations and ordinary hauling tasks.

### 1.1.2

- Corrected Trade material quantities so each shared Quantum network is counted once.
- Added Nanobot Hive material delivery and construction work for floor replacement beneath constructed walls and doors.
- Matched Core's eight-tile work adjacency, including corners accessible only from a diagonal floor.
- Allowed boundary-floor replacement from compatible unsealed and outside areas. Blueprint walls do not create room boundaries before construction.
- Recognized Core's outside-area marker for wall and door cells.
- Kept Core replacement tasks, material reservations, Hive range, and room coverage.
- Validated optional TACC transfers in both directions between Quantum Wardrobes and Washing Machines.

### 1.1.1

- Restored Nanobot Hive delivery of loose weapons to Quantum Weapons Lockers.
- Restored Nanobot Hive delivery of loose clothing to Quantum Wardrobes.
- Kept Core's existing Nanobot material route for upgrade modules unchanged.
- Retained the additional research gates for organic and robotic body parts.
- Preserved TACC Washing Machine transfers and one-way Quantum object delivery.

### 1.1.0

- Added optional Nanobot Hive delivery from Quantum Wardrobes to TACC Washing Machines.
- Added Nanobot Hive return delivery for clothing discharged by a Washing Machine.
- Retained the normal Washing Machine task, reservation, power, and completion process.
- Retained Quantum Wardrobe capacity, priority, and filter checks.
- Shared Hive assignment state with Core hauling and Part Restoration Unit.
- Added save recovery for clothing already in Nanobot transit.
- Kept TACC optional through runtime discovery with no assembly dependency.
- Bounded TACC goal reconciliation and Hive assignment work per Nanobot tick.

### 1.0.5

- Counted outstanding reservations against the shared material pool at every stock level.
- Prevented workers from reserving more material than a Quantum network contains.
- Excluded fully reserved Quantum stock from Core's flying-worker reachability warning.
- Retained warnings for material that remains available but has no reachable endpoint.

### 1.0.4

- Revised the Workshop description and README with direct, formal technical language.
- Removed conversational marketing copy and figurative language.
- Retained the established features and requirements.
- Compatibility notes and release history remain available.
- This release changes documentation and version metadata only.

### 1.0.3

- Adopted Core's `BeingSafety.IsActuallySafeAt` method for Stardeus `v0.15.0-rc285`.
- Restored colony-goal and material-hauling availability checks.
- Restored related crafting evaluations.
- Retained shared storage contents, endpoint settings, and Nanobot behavior.

### 1.0.2

- Replaced the Hive-by-goal scan with bounded preparation and room-aware ready queues.
- Added rotating Hive assignment and a limited dormant-goal reconciliation pass.
- Final assignment checks research and source state.
- Destination eligibility, reservation state, and carrier ownership are rechecked before commitment.
- Retained colonist hauling, save recovery, and Part Restoration Unit coordination.
- Maintained one-way Nanobot delivery into Quantum storage.

### 1.0.1

- Reduced bulk object-ejection work to one network recount and endpoint refresh per batch.
- Core creates every ejected object through its standard process.
- Single-object storage and retrieval update the network immediately.

### 1.0.0

- Completed gameplay validation for all ten endpoints.
- Confirmed network separation and shared capacity.
- Verified save behavior and object identity.
- Validated Nanobot delivery, damage feedback, and Fuse protection.
- Added the Workshop description and final `ModCover.jpg`.

### 0.5.3

- Corrected component order for existing Quantum object endpoints.
- Repaired the runtime haul-target reference during attachment.
- Restored eligible Nanobot delivery into Quantum Organ Storage and Parts Storage.
- Preserved priority zero as an explicit disabled-hauling setting.
- Removed temporary diagnostic logging.

### 0.5.2

- Added temporary `[Quantum Storage Nanobot Probe]` logging.
- Recorded lifecycle binding and goal registration.
- Captured endpoint attachment, Hive coverage, and route eligibility.
- Storage behavior remained unchanged.

### 0.5.1

- Corrected Nanobot reachability reporting when a covering Hive had Core work during the same tick.
- Kept eligible body-part goals available until the occupied Hive could accept another task.
- Retained Core work precedence and the established research gates.
- Built against Core `v0.15.0-rc280`.

### 0.5.0

- Added research-gated Nanobot delivery for loose body parts.
- Applied Core's Hive coverage and endpoint eligibility rules.
- Restricted the route to Quantum storage delivery.

### 0.4.1

- Replaced temporary object-endpoint sprites with four distinct designs.
- Added a secured weapon rack and garment wardrobe.
- Added separate cryogenic organ and robotic-parts storage artwork.
- Retained established storage definitions and saved contents.

### 0.4.0

- Added Quantum Weapons Locker and Wardrobe networks.
- Added independent Quantum Organ Storage and Parts Storage networks.
- Reused the one-tile, powered, Fuse-protected storage architecture.
- Preserved each object's Core record.

### 0.3.1

- Declared every required component and definition field for material endpoints.
- Corrected a load-time null layer-key failure during Core render-layer resolution.
- Retained existing shared-network contents.

### 0.3.0

- Added Quantum Cabinet, Freezer, and Fridge.
- Added Quantum Beverage Cooler and Pantry.
- Assigned an isolated pool and initial capacity to each type.
- Added endpoint definitions, translations, and artwork.

### 0.2.2

- Adopted Core's cached material used-capacity contract.
- Synchronized capacity and interface notifications across matching endpoints.
- Reconstructed totals from the authoritative pool during attachment.

### 0.2.1

- Added industrial Quantum Storage Unit artwork.
- Supplied all four placement directions.
- Retained shared-storage behavior.

### 0.2.0

- Added the one-tile Quantum Storage Unit.
- Set initial capacity to 500.
- Doubled capacity for every additional endpoint.
- Used Core's hauling and filter systems.
- Applied Core's priority and reservation contracts.
- Removed the Safe Area requirement.
- Added limited damage feedback with Fuse protection.
