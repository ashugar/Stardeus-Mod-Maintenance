# Intelligent Recycling

Intelligent Recycling automatically sends eligible loose body parts and
equipment to compatible Nutrient Extractors or Disassemblers when their quality
is at or below the shared biological or robotic/equipment threshold. Those two
thresholds can be set in Intelligent Recycling and are synchronized with every
compatible facility in both directions. The mod also cleans up selected event
objects after Stardeus marks them safe for Disassembly or Deconstruction, and
can automatically dispose of dead Crawler pests. Separate opt-in settings can
dispose of biological and robotic corpses after their removable parts have been
harvested. An opt-in recovery workflow
can also replace a Shuttle landing pad after its Shuttle is permanently lost.

## Requirements

- Stardeus 0.15.x (rc337.3219 or newer)
- Auto Utilities Mod Framework feature level 1.4.0

The mod declares AUMF 1.4.0 as its tested feature level through the framework's
consumer compatibility handshake. Intelligent Recycling initializes its AUMF
widget and panel only when that handshake reports a compatible framework.

## Features

- Uses vanilla Dispose, Disassemble, and Deconstruct tasks.
- Reacts promptly to loose-item, body-part, Being-death, processor-construction,
  processor-threshold, and event-object state changes.
- Coalesces frequent object creation and carrier-release events into targeted
  dirty-object passes while retaining a five-second full reconciliation scan.
- Reconciles loose recycling candidates from Core's active-object collection
  instead of iterating every persistent stored object.
- Performs no automation scans while the game is paused.
- Supports biological parts, robotic parts, and equipment independently.
- Provides independent, default-disabled biological and robotic corpse cleanup
  controls after all removable body parts have been harvested.
- Provides shared biological and robotic/equipment Quality controls in the
  Settings panel. Changes synchronize to every matching facility, while a
  facility-side change updates Intelligent Recycling and its peers.
- Adds the otherwise-hidden biological threshold control to the Nutrient
  Extractor's native processor tab. The Disassembler retains Core's native
  robotic/equipment control.
- Discovers Inventory body-part and equipment categories dynamically, including
  compatible definitions supplied by other mods.
- Provides separate biological and robotic choices for every applicable body-
  part category.
- Protects Brain, Core, mind-bearing, stored, forbidden, reserved, and
  already-busy items.
- Can optionally replace unassigned storage-haul tasks; this is disabled by
  default and never interrupts hauling already assigned to a worker.
- Can optionally replace a permanently lost Shuttle pad at the same position
  and rotation, then reorder its installed upgrades through normal Core jobs.
  This is disabled by default because removing the original pad gives up the
  possible stolen-Shuttle return story event.
- Saves settings, accumulated queue statistics, last-scan results, and active
  tracked recycling and deconstruction tasks with the game.
- Uses AUMF for its cooperative top-bar widget and managed, freely resizable
  Main/Settings panel.
- Makes the custom Body part category card shells, title bars, and titles follow
  the selected Stardeus theme, including live theme reloads.
- Provides an optional persistent keyboard shortcut for opening or closing the
  panel, with capture and conflict protection managed centrally by AUMF.
- Exposes a versioned, read-only compatibility API for settings, categories,
  processor thresholds, scan state, totals, tracked tasks, special targets,
  shortcut status, and authoritative per-object ownership decisions. See
  [API.md](API.md).

## Event object cleanup

- **Hullpiercers:** queued for Disassembly after landing.
- **Storage Capsules:** queued for Disassembly only after opening.
- **Safe Boxes:** queued for Disassembly only after opening. Intelligent
  Recycling verifies the exact Safe Box definition and its persisted open state,
  then still asks Stardeus to approve the vanilla Disassemble task.
- **Stasis Pods:** queued for Deconstruction only after Core's Waiting countdown
  completes and the pod opens; Merchant/Trade pods are always protected while
  their merchant activity is active.
- **Breach Capsule Ports:** queued for Deconstruction only after Stardeus has
  resolved the raider release or its failed-spawn fallback.
- **Artillery Fungus:** queued for biological Disposal at any growth stage;
  quality thresholds do not apply, but a constructed Nutrient Extractor is
  still required.
- **Dead Crawlers:** queued for biological Disposal after death. Living and
  incapacitated Crawlers are never targeted, and a constructed Nutrient
  Extractor is required.
- **Lost Shuttle pads:** when enabled, a permanently lost Shuttle pad is queued
  for ordinary Deconstruction, followed by a replacement construction blueprint
  at the same position and rotation. Installed upgrade assignments are remembered
  by slot and reordered through ordinary Install Upgrade jobs after construction.
  Real recovered or available upgrade modules are still required. Canceling the
  deconstruction order suppresses that pad until the setting is toggled off and
  on. A replacement already in progress continues if the setting is disabled.

Each event-object cleanup path has an independent toggle. Existing cleanup paths
are enabled by default; lost Shuttle pad replacement is disabled by default.

## Optional corpse cleanup

**Biological corpses** and **Robotic corpses** are separate Settings options.
Both start disabled, including when loading an existing save. They do not remove
body parts or change harvesting orders. Eligible corpses use the normal Nutrient
Extractor or Disassembler work, with Core handling workers, transport, equipment
stripping, processor availability and outputs.

A corpse must be genuinely dead, with no removable installed body parts left.
Optional implants count, and a destroyed-but-still-installed removable part must
still be removed. Welded parts and the body's inherent nonremovable structure
may remain. An installed Brain, Core or other mind-bearing part must be removed
or actually welded; other reasons a part cannot be removed do not waive this
protection. Loose Brain/Core and mind-associated parts remain protected.

The body species selects the biological or robotic setting, independently of
its mind or the materials of its installed parts. The existing **Dead Crawlers**
control remains exclusively responsible for Crawlers, including when disabled.

Stored or unavailable bodies, bodies carried by another worker, and bodies with
existing work or pending surgery are left alone. An active IR job uses only its
own worker's processor reservation. Eligibility is checked
again for IR-owned corpse jobs before processing. Disabling a corpse option or
Automation stops pending IR corpse work through Core's normal cancellation;
processing already accepted by a device continues normally. Settings and exact
task ownership survive save/load. Corpse discovery uses the existing 300-tick
full reconciliation scan rather than a new scan for every loose item.

Corpse admission does not compare quality against the loose-part thresholds.
Core retains its ordinary processing and output rules, including any native
recovery of parts remaining in a robotic corpse.

Before downgrading or removing IR, disable both corpse options and let pending
corpse jobs cancel, then save. An older IR DLL cannot enforce these new saved
jobs' eligibility checks; restoring the old DLL alone does not undo saved work.

## Defaults

- Automation: On
- Biological body parts: On
- Robotic body parts: On
- Equipment: Off
- Replace storage-haul tasks: Off
- Hullpiercers: On
- Opened Storage Capsules: On
- Opened Safe Boxes: On
- Stasis Pods: On
- Breach Capsule Ports: On
- Artillery Fungus: On
- Dead Crawlers: On
- Biological corpses: Off
- Robotic corpses: Off
- Lost Shuttle pads: Off
- Biological quality threshold: 50%
- Robotic/equipment quality threshold: 50%
- Discovered category choices: On, except Brain and Core, which are permanently
  hidden and disabled

## Compatibility and safety

Category settings are stored by stable Stardeus definition IDs rather than
translated display names. New categories default to enabled, while categories
that have no biological or robotic definition are shown as `N/A`.

The normal quality-threshold workflow considers loose active objects with
Stardeus's disposable component. The only constructed objects considered are
the explicitly supported Stasis Pods, Breach Capsule Ports, and permanently lost
Shuttle pads, with their respective safety checks. Raw materials and stored
inventory are outside this mod's scope. Dead Crawlers and the opt-in stripped
corpse settings are separate Being-level paths. The Crawler path requires its
species and Dead state; generic corpse cleanup additionally verifies that the
body is no longer alive and all removable parts have been harvested. Neither
generic corpse option targets a living or merely incapacitated Being.

Quality thresholds are global per processor family. On the first load after
upgrading from 1.1.1 or earlier, Intelligent Recycling adopts the highest
currently constructed facility threshold for each family, preserving the
effective cutoff that save was already using. It then synchronizes every
matching facility. If no compatible facility exists during migration, the
corresponding threshold starts at Core's existing 50% default. New facilities
inherit the saved Intelligent Recycling threshold when construction completes.

## 1.5.1

- Hides the weapon checklist while the Weapon equipment category is disabled.
  Re-enabling the category restores the checklist with its saved choices.

## 1.5.0

- **Alien eggs** is off by default. It orders native biological disposal for
  intact Alien Eggs. An egg can still hatch while waiting for delivery.
  Egg Shell and Crawler Goo are not disposal targets.
- If enabled when unfinished Alien Datoid research becomes available, egg
  automation pauses new orders while you review the setting. One Egg Shell
  and one Crawler Goo craft an Alien Datoid. Choose to keep disposal enabled,
  or turn it off in Settings. The decision is saved with the game.
- **Second-hand equipment** and **Belonged to a dead person** independently
  control worn gear. Both start on to preserve existing behavior. Core assigns
  gear that matches both to the dead-owner category. Forbidden gear stays
  protected.
- Enable **Use weapon checklist** to limit the Weapon category to checked
  definitions. Search by name or ID, then browse up to twelve results per page.
  Compatible mod-added weapon-slot items are included.
- **Follow quality threshold** starts on. Turning it off bypasses the quality
  cutoff only for checked weapons when the checklist is in use. Equipment and
  Weapon category switches, processor availability and safety still apply.

In-game acceptance: load an older save and verify its existing equipment
behavior is preserved. With egg disposal enabled, reach or load a save with
eligible Alien Datoid research; verify Review opens Settings and that egg
orders wait for your decision. Save and reload after choosing, and check that
the reminder does not repeat. For equipment, try second-hand and dead-owner
gear separately, including a forbidden item. For weapons, search across pages,
select a mod-added item, and compare checked and unchecked weapons above the
cutoff with Follow quality threshold on and off.

For an exact rollback to 1.4.0, keep a save made before upgrading. Restoring
the previous DLL does not revert settings or disposal jobs saved by 1.5.0.

## 1.4.0

Status: implementation candidate; fresh startup and gameplay validation pending.

- Adds independent, default-disabled **Biological corpses** and **Robotic
  corpses** Settings options, including safe defaults for existing saves.
- Uses native disposal work only after all removable parts are removed. Welded
  remnants, including welded Brain/Core parts, may remain; nonwelded installed
  mind parts and detached protected parts retain their safeguards.
- Preserves genuine body-death checks, player work, pending surgery and foreign
  ownership. Revalidates only exact IR-owned corpse jobs during execution and
  immediately before processor acceptance, including after save/load.
- Keeps Crawlers exclusively under their existing dedicated control and retains
  the 300-tick full reconciliation backstop.
- Extends read-only API contract 1 with additive corpse settings and task/target
  reporting. AUMF requirement 1.4.0 remains unchanged.
- Advances the save schema to 14 for corpse settings and exact task ownership.
- Processor outputs remain Core-owned; this feature promises neither destruction
  nor recovery of welded remnants.

Remaining in-game acceptance for 1.4.0:

1. Load an existing save and confirm both corpse options start off while the
   existing Crawler and loose-part choices are preserved.
2. Enable one corpse family with its compatible processor. A dead body with one
   removable part remaining must stay untouched; after the last removable part
   is harvested it should receive native work within the normal advancing
   300-tick scan. Repeat for the other family and a welded Brain/Core remnant.
3. Confirm living/incapacitated Beings, ongoing Core replacement, pending surgery,
   stored bodies and existing manual jobs remain untouched.
4. Turn the relevant option or Automation off during pending and carried work.
   Confirm IR cancels its pending job normally without losing the body or
   interfering with another job. Work already accepted by a processor continues.
5. Save/load with pending and carried corpse jobs; confirm correct ownership,
   no duplicate orders and normal reservation/carrier cleanup. Check a fresh
   log for the expected version and corpse-hook initialization errors.

## 1.3.3

Status: published to Steam Workshop and closed; maintenance mode.
Publication confirmed by the maintainer on 2026-10-19.

- Restored compatibility with Stardeus 0.15.0-rc337.3219 after Core renamed
  `StorableObjComp.IsHaulingForbidden` to `IsForbidden`.
- Uses Core's current forbidden-state contract for both recycling eligibility
  and read-only object snapshots, preserving the existing exclusion of
  forbidden objects and the API's established `Forbidden` meaning.
- Auto Utilities Mod Framework feature level 1.4.0, save schema 13, and
  read-only API contract 1 are unchanged.
- Fresh runtime log review confirmed 1.3.3 loading and successful automation
  initialization on rc337.3219, with no Intelligent Recycling missing-method,
  initialization, or `OnTick` failures in the captured session.
- Further work is on demand for reported issues or compatibility changes.

## 1.3.2

- Prevented Intelligent Recycling from creating a Breach Capsule Port
  Deconstruction order during Core's short opened-but-raid-pending interval.
- Uses Core's public `IsRaidStarted` lifecycle boundary and still requires the
  capsule to be opened plus vanilla `CanDeconstruct()` approval.
- Cancels only an exact Deconstruction goal already tracked as IR-owned if a
  save or ordering race finds it before raiders have been released or a failed
  spawn has been resolved. Player and unrelated goals remain untouched.
- Save schema 13 and read-only API contract 1 are unchanged.

## 1.3.1

- Prevented Intelligent Recycling from creating a Stasis Pod Deconstruction
  order before Core's two-hour Waiting lifecycle has completed.
- Requires both the elapsed landing countdown and `IsOpened` state, while
  retaining the existing Merchant/Trade protection and vanilla
  `CanDeconstruct()` approval.
- Cancels only an exact Deconstruction goal already tracked as IR-owned if a
  save or ordering race finds it on a pod that is still Waiting. Player and
  unrelated goals remain untouched.
- Save schema 13 and read-only API contract 1 are unchanged.

## 1.3.0

- Added an independent, default-disabled **Lost Shuttle pads** automation
  setting for Shuttle pads Core has marked permanently lost.
- Uses Core's ordinary Deconstruction and construction paths, preserves the
  original position and rotation, and never creates free construction material.
- Remembers installed upgrades by exact slot before deconstruction and reorders
  them through ordinary Install Upgrade jobs after the replacement is built.
- Continues an in-flight replacement if the setting is later disabled, while a
  player-canceled deconstruction suppresses that pad until deliberate re-enable.
- Documents that removing the original lost pad gives up Core's possible
  stolen-Shuttle return story event.
- Reports the save's current conditional return-event percentage in the setting
  tooltip. Core's equal-weight full-pool reference is about 5.6% of an eligible
  Positive-event pick (one of 18 built-in Positive candidates). Current
  eligibility and event-history weights can change that share. Core has no
  fixed return deadline or guaranteed lifetime percentage.
- Extends read-only API contract 1 additively with the
  `replace-lost-shuttles` setting, `LostShuttle` target and tracked-task kinds,
  replacement totals, active workflow count, and special-target snapshots.
- Advances the save schema to 13 for the setting, accumulated total, in-flight
  replacement records, and canceled-pad suppression state.

## 1.2.6

- Updated both native disposal eligibility checks for Stardeus rc290's
  `CanOrderDispose(DisposeCheck)` method, replacing the removed property.
- Uses `DisposeCheck.BulkOrder`, matching the existing
  `TryOrderDispose(false, out _)` task path.
- Preserves recycling policy, safety gates, tracked tasks, priorities, and the
  targeted-scan optimization. Save schema 12 and API contract 1 are unchanged.
- The compatibility repair was validated in game and published to Workshop.

## 1.2.5

- Replaced full active-object rescans triggered by every object creation or
  carrier release with a coalesced set of affected object IDs.
- Preserves Core `ActiveObjs` order when processing targeted candidates, along
  with every existing eligibility, priority, reservation, threshold, ownership,
  and vanilla task rule.
- Keeps settings and special-event changes on the prompt full-scan path and
  retains the independent 300-tick full reconciliation correctness backstop.
- Reduced measured `OnTick` time on the stress save from 12.113 ms to 0.465 ms
  on average (96.2%), while the average object phase fell by 97.2%.
- Runtime validation confirmed that items were still designated for recycling
  or released for other processing exactly as expected.
- Removed the temporary performance probes after validation. Save schema 12 and
  read-only API contract 1 are unchanged.

## 1.2.4

- Raised the tested Auto Utilities Mod Framework feature level to 1.4.0.
- Made Body part category card shells, title bars, and titles follow the selected
  Stardeus theme and live theme reloads through AUMF's `ThemeAwareUi` bindings.
- Preserved the existing two-column biological/robotic layout, independent
  toggles, semantic state colors, full-color icons, sizing, and callbacks.
- Intentionally leaves Automation, Equipment, and other selection/state toggle
  cards on their established semantic colors instead of binding them to themes.
- Gates the new bindings through AUMF's 1.4.0 feature check. Save schema 12 and
  read-only API contract 1 are unchanged because this is presentation-only.
- Runtime and visual validation completed on September 5, 2026 across the
  supplied Stardeus theme examples, including live theme changes.

## 1.2.3

- Added an independent, default-enabled **Opened Safe Boxes** cleanup toggle.
- Requires the exact Safe Box definition and its persisted `IsOpen` state, and
  still relies on Stardeus's own disposal eligibility and Disassemble task.
- Requests one coalesced scan when a Safe Box opens while retaining the
  five-second active-object reconciliation backstop.
- Extended read-only API contract 1 with the additive `SafeBox` target type,
  setting, accumulated counter, and special-target snapshot support.
- Advanced the save schema to 12 for the new persisted toggle and statistic.

## 1.2.2

- Added optional, read-only Part Restoration Unit ownership compatibility.
- Preserves pending or loaded restoration work without cancelling or replacing
  the restoration task.
- Adds no hard dependency and does not change Quality thresholds or recycling
  policy when Part Restoration Unit is absent.

## 1.2.1

- Replaced the five-second loose-object scan's enumeration of every persistent
  object with Core's much smaller active-object collection.
- Kept event-triggered scan requests coalesced through the simulation tick and
  retained the five-second reconciliation as a correctness backstop.
- Preserved every existing eligibility, safety, threshold, task, and carried-
  object check. Tracked-task and public per-object API lookups remain keyed
  against Core's complete object dictionary.
- Kept read-only API contract 1 and save schema 11 unchanged.

## 1.2.0

- Added player-facing biological and robotic/equipment Quality thresholds to
  Intelligent Recycling's Settings panel.
- Made each threshold authoritative for its processor family and synchronized
  it bidirectionally with every Nutrient Extractor or Disassembler.
- Added the previously hidden biological threshold slider to the Nutrient
  Extractor's native processor tab. Core's existing Disassembler slider remains
  in place and now participates in the same synchronization.
- Preserved existing-save behavior during migration by adopting the highest
  constructed facility threshold before normalizing same-family facilities.
- Persisted both managed thresholds with save schema 11. Saves without these
  fields migrate from their current processors or Core's 50% default.
- Extended the contract-1 processor-family snapshot additively with
  `ManagedThresholdAvailable` and `ManagedThreshold`; ownership and release
  semantics are unchanged.

## 1.1.1

- Made the automation scan honor `ObjData.Reservation`, using the same
  transient-queue gate as the public per-object snapshot. Reserved objects are
  skipped before storage-goal replacement or disposal and counted separately
  in `Scan.Reserved`.
- Preserved the intentional replacement exception only for exact unassigned
  Store goals in Available, Pending, or Bidding state; reservations remain a
  hard transient blocker.
- Made non-finite object quality and applicable processor-threshold values fail
  closed. They now report `NotApplicable` quality relation, `Invalid` blocker,
  `Unknown` ownership with `InvalidObject` reason, and both release Booleans
  false.
- Retained API contract 1 and save schema 10. The new reservation counter is an
  optional schema-10 value and defaults to zero when loading a 1.1.0 save.

## 1.1.0

- Added contract version 1 of the read-only compatibility API under
  `Game.Systems.IntelligentRecycling.IntelligentRecyclingApi`.
- Added immutable snapshots for the complete current mod state and a focused
  per-object query intended for hot-path cooperation by mods such as Auto
  Hauling.
- Centralized object ownership in the same classifier used by Intelligent
  Recycling's automation scan. Quality equal to a processor threshold remains
  owned by Intelligent Recycling; only quality strictly above that threshold
  can be released for external hauling.
- Kept forbidden state, reservations, and existing goals as transient blockers:
  they can make an externally released object not currently haulable without
  changing its policy owner.
- Reserved Hullpiercers, Storage Capsules, Safe Boxes, Artillery Fungus, dead
  Crawlers, Stasis Pods, and Breach Capsule Ports for their explicit Intelligent
  Recycling safety paths; they are never released to general hauling by this
  API.
- Split the former "Above threshold / no processor" result into authoritative
  `AboveThreshold` and `NoCompatibleProcessor` statistics.
- Expanded persisted feature totals and diagnostic scan counters. Save schema
  10 migrates the old combined statistic as legacy, unsplit data until the
  first new scan replaces it.

## 1.0.6

- Updated the consumer release and tested AUMF feature level to 1.3.0 through
  the existing four-argument compatibility handshake.
- This is a compatibility/versioning update only. Intelligent Recycling does
  not adopt AUMF's Collapsible Sections in this release; its interface,
  automation behavior, settings, and save schema are unchanged.

## 1.0.5

- Added an independent, default-enabled Dead Crawlers automation toggle.
- Requests vanilla biological Disposal only for active Crawler corpses, with an
  immediate scan request from Core's Being-death signal and the normal periodic
  fallback reconciliation.
- Added save/load persistence and active-task reconciliation for Being disposal
  goals without changing the existing loose-object or event-object trackers.

## 1.0.4

- Added a default-enabled Artillery Fungus automation toggle.
- Queues active Artillery Fungus for vanilla biological Disposal at any growth
  stage, independent of quality thresholds.
- Counts queued fungus under Biological statistics and preserves active task
  tracking across save/load through the existing object-goal tracking system.

## 1.0.3

- Updated the tested AUMF feature level to 1.2.0.
- Added an optional, globally persisted shortcut for opening or closing the
  Intelligent Recycling panel.
- Added Settings controls to assign, cancel capture, clear, and inspect the
  shortcut, including conflict and reserved-key feedback from AUMF.
- Delegated shortcut persistence, native/mod conflict checks, text-entry
  suppression, once-per-press dispatch, and input cleanup to AUMF.

## 1.0.2

- Validated the complete consumer integration against AUMF feature level 1.1.0.
- Registered the compiled Intelligent Recycling version and tested AUMF feature
  level through AUMF's four-argument compatibility handshake.
- Confirmed no AUMF 1.1-only optional UI feature requires a separate feature
  gate in this consumer.
- Merged the AUMF update with the complete event-object cleanup feature set.
- Synchronized the manifest, assembly, file, product, and documentation versions.
- Restored the complete Workshop description after the framework refresh.

## 1.0.1

- Refactored the cooperative Recycling widget and Main/Settings interface to use
  Auto Utilities Mod Framework 0.2.2.
- Added an exact AUMF compatibility check and declared the framework dependency.
- Removed superseded local flow-layout and resize helpers.
- Added default-enabled cleanup toggles for landed Hullpiercers, opened Storage
  Capsules, safe Stasis Pods, and resolved Breach Capsule Ports.
- Preserved scanning, categories, statistics, settings, save/load, tracked-task,
  and Harmony behavior through the refactor.

## 1.0.0

- Completed the release audit and source cleanup.
- Replaced the Managed-directory wildcard with a minimal explicit assembly set;
  clean rebuilds now complete with zero warnings and zero errors.
- Removed the obsolete overlay configuration and unused lifetime total.
- Replaced the deprecated Unity object lookup overload.
- Added maintenance comments around save migration, dynamic discovery, paused
  scanning, parent settings, layout rebuilding, and Brain/Core protection.
