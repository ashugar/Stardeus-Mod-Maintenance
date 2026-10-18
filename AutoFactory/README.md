# Auto Factory

Auto Factory adds a Tier 4 **Nano Foundry**, Tier 5 **Nano Fabricator**, and
Tier 5 **Nano Cloning Pod** that automate compatible Stardeus production while
preserving the normal material requirements and Core lifecycle of each recipe.

## Requirements

- Stardeus 0.15.x
- Auto Utilities Mod Framework (AUMF) 1.4.0
- The `Advanced Assembly` and `Nanobots` research projects must be completed
  before the Tier 5 `Nano Fabrication` research can be unlocked.
- The `Smelting` and `Nanobots` research projects must be completed before the
  Tier 4 `Nano Smelting` research can be unlocked.
- The `Genetic Engineering` and `Nanobots` research projects must be completed
  before the Tier 5 `Nano Cloning` research can be unlocked.

Auto Factory declares and registers against the AUMF 1.4.0 feature level. The
framework DLL remains external and is not bundled with this mod.

## Nano Fabricator behavior

- Recreates eligible recipes exposed by other crafting machines.
- Includes Core's tiered `Weapons*` recipes, including handheld equipment and
  C42, non-biological body parts from the `Robotics*` recipe families, and
  beings produced through ordinary Fabricator families. Robotic body parts are
  identified through Core's `BodyPartDef` metadata rather than a hard-coded
  product list. Being discovery admits Core's Robot and installed Android
  definitions without hard-coding another mod's identifier.
- Uses the original ingredients and accelerates production according to the
  product tier: `time × (1 - 1 / (tier + 2))`, with a 0.1-hour minimum.
- Uses 110% of the source recipe's energy cost.
- Requires Nanobots as fuel at 0.0625 Nanobots per active production hour.
- Creates only one automated order per Nano Fabricator at a time.
- Treats a player selection from `New Order` as a one-recipe-cycle manual
  override, then automatically returns that Nano Fabricator to automation.
- Records manual overrides in violet in the Nano Fabricator's `Log` tab.
- Provides a persistent global Auto Factory priority from 1/6 through 6/6.
  The elevated 6/6 setting uses Stardeus's native goal boost above ordinary
  5/5 work without modifying the priorities of unrelated buildings.
- Applies the shared production and refill priority policy to Nano Fabricators,
  Nano Foundries, and Nano Cloning Pods.
- Keeps every managed Nano facility configured in Core while it waits idle
  between automated orders, without creating a placeholder crafting order.
- Provides a persistent per-building `Scope` tab. `Global` factories use
  ship-wide demand, inventory, and ingredients. `Room` factories respond only
  to shortages originating in their enclosed room and produce eligible
  intermediaries required by those shortages first, then use otherwise-idle
  capacity to help fulfill ship-wide Production Goals. Recipe ingredients
  remain ship-wide and may be hauled in from any eligible storage.
- Schedules Room-scoped factory groups before Global factories. Factories in
  the same room share and distribute that room's work, while factories in
  different rooms operate independently.
- Leaves a Room-scoped factory idle when it is not inside an enclosed room;
  it never silently falls back to Global behavior.
- Distributes unique material needs across multiple Nano Fabricators first.
- Produces an eligible missing intermediary when the requested product cannot
  yet be crafted.
- Excludes the entire `Converter` source-recipe family, including material-
  conversion routes involving Carbon, Rubber, and Plastic.
- Excludes every product craftable in Core's Cloning Pod, identified through
  its exclusive `Human`, `Animal`, `Organics1`, `Organics2`, and `Organics3`
  recipe families. This keeps biological outputs such as Stem Cells, Crawler
  Goo, and Protein in their native facility even when their inventory category
  would otherwise be eligible. The product remains excluded if another route
  for it is introduced outside those families.
- Ranks every remaining route for the same product and facility by batch output
  per adjusted Auto Factory production hour. Only the highest-throughput route
  or equal-best routes are cloned, so lower-throughput alternatives do not
  appear in manual orders and cannot be selected by automation.
- Retains differently resourced routes when their throughput ties. Automation
  selects from tied routes whose complete ingredients are available and may
  choose randomly when multiple tied routes are equally craftable. When none
  is immediately craftable, every tied route remains eligible for intermediary
  and cross-facility dependency traversal.
- Preserves Global/Room scope, initialized labels, fuel-blocked trace state,
  and in-flight manual or automated order ownership when a managed facility is
  relocated.
- Persists in-flight manual, intermediary, Production Goal, and cross-facility
  order classification across save/load, together with produced-but-not-yet-
  stored delivery accounting. This prevents a restored order from being
  reclassified, cancelled, or counted twice merely because the game was saved.
- Does not produce materials in the Ore, Plants, Food Ingredients, or Food
  inventory groups.

## Nano Foundry behavior

- Unlocks from the Tier 4 `Nano Smelting` research after both `Smelting` and
  `Nanobots`.
- Ranks the vanilla Furnace `Ore` and `Burnable` recipe pool by throughput and
  clones only the best route or equal-best routes for each product.
- Uses only its `AutoFoundry` recipe family; Nano Fabricators cannot acquire
  Furnace recipes merely because a Nano Foundry is installed.
- Requires Nanobots as fuel at 0.0625 Nanobots per active production hour and
  applies the same 10% source-recipe energy surcharge.
- Receives a smaller tier-based speed bonus than the Nano Fabricator:
  `time × (1 - 0.5 / (tier + 2))`, with a 0.1-hour minimum.
- Uses the same Produce One automation, manual override, priority, Global/Room
  scope, enclosure pause, logging, Production Goals, and save/load protections
  as the Nano Fabricator.
- Uses dedicated four-direction Nano Foundry art with animated nanobot-flow overlays.
- Exposes the higher-throughput Iron route for Steel Plate production. The
  lower-throughput Scrap Metal route is omitted from both manual and automated
  orders.

## Nano Cloning Pod behavior

- Unlocks from the Tier 5 `Nano Cloning` research after both `Genetic
  Engineering` and `Nanobots`.
- Recreates Core's complete `Human`, `Animal`, and `Organics1`-`Organics3`
  Cloning Pod recipe families inside the isolated `AutoCloning` family.
- Throughput-ranks alternate routes independently from the Nano Fabricator and
  Nano Foundry and keeps the Cloning Pod product boundary intact for those
  facilities.
- Produces materials, eggs, body parts, biological beings, and other products
  through Core's native `CraftingSys` output path. Auto Factory never spawns or
  mutates a being directly.
- Requires Nanobots at the standard Auto Factory fuel rate, applies the same
  10% source-recipe energy surcharge, and uses the Nano Fabricator tier-based
  production-time formula.
- Uses the same Produce One automation, manual override, priority, Global/Room
  scope, enclosure pause, logging, Production Goals, relocation, and save/load
  protections as the other managed facilities.
- Can hand missing material intermediaries to an eligible idle Nano Fabricator
  or Nano Foundry while preserving the requester's scope preference.
- Uses dedicated four-direction nano-industrial art and a cyan suspension-
  bubble production animation.

## Auto Factory widget and production goals

The top-bar widget summarizes facility and order counts, the configured Auto
Factory priority, production-goal status, and the globally available Nanobot
fuel reserve.

The AUMF-managed top-bar widget opens the Auto Factory control panel. The
widget shows whether automation is enabled and badges the number of unmet
production goals.

The panel provides:

- An in-game toggle for managed Auto Factory automation.
- An AUMF-managed, user-assignable keyboard shortcut for enabling or disabling
  automation, including guarded capture, conflict reporting, and clear/change
  controls.
- Current Nano Fabricator, Nano Foundry, Nano Cloning Pod, and automated-order
  status.
- A searchable, inventory-style list of all eligible produced products with
  independently collapsible categories, including Weapons, Robotic Body Parts,
  Cloning, and Beings categories for non-material products. Filtering
  temporarily opens matching categories without changing their previous
  collapsed state.
- A desired global inventory amount for each eligible product. These goals
  use ship-wide inventory and may be fulfilled by any compatible managed
  facility after its scope-specific shortage pass; they never create or
  maintain a room-local stockpile.
- A compact single-row product layout showing Item, Owned count, Goal amount,
  and a per-product Priority field.

A target of `0` disables that product's production goal. When inventory is
below a positive target, compatible managed facilities continue to issue
`Produce One` orders even when Stardeus has no matching `Lacking materials`
warning. Room
factories handle same-room shortages and their eligible intermediary chain
before joining the shared Global Production Goal pool. Global owned inventory,
global pending output, and already assigned factory work are coordinated once
ship-wide so each room does not create its own copy of a goal. Recipe output
stacks can cause global inventory to finish slightly above the configured
target.

For biological Being products, Owned continues to use Core's exact active-
definition count. For robotic Being products, Owned includes available
chassis without an installed Core plus active, living robots in the player's
faction. Coreless chassis stored in Mechuaries count; inactive Core-equipped
robots, expedition members, bodies that left for a colony, and missing bodies
do not. Installing or removing a Core updates the goal count on the next safe
planning tick without treating that change as a newly crafted delivery.

For Brain and Core object products, Owned uses Core's generic
`BodyPartDef.HoldsMind` metadata rather than a product-name list. Blank parts
and parts carrying Minds that Core does not consider hostile toward the player
count toward the goal; hostile Minds do not. Object inventory, Mind
installation/removal, and player-faction relationship changes schedule a
refreshed count without treating a disposition change as newly crafted output.

Production Goal priority is clamped from `0` through `100`. Zero is the default,
1 is the lowest explicit priority, and 100 is highest. Room- and Global-scope
`Lacking materials` demand always schedules before Production Goals. Within the
remaining goal pool, the scheduler evaluates higher-priority products first,
including their eligible intermediary and cross-facility chains. Products that
share a priority are shuffled before selection. Changing a priority affects the
next available facility; it does not cancel a recipe already in progress.
Configured goals for research-locked products remain saved but inactive. Auto
Factory resumes them automatically after Core reports the required research as
unlocked.

Automation enablement, production goals, per-product goal priorities,
facility priority, per-building scope,
in-flight order classification, and material, object, and being pending-
delivery accounting are stored in the Stardeus save. Category collapse state
is UI-session-only and is not added to Auto Factory's save data.

## 1.5.2 late-game performance release

- A fresh 1,140-pass diagnostic capture after incremental demand-cache refresh
  reduced `AutoFactorySys.OnTick` from 41.87 ms to 13.39 ms on the affected
  surgery-heavy save. The player-observed frame rate improved from the 10s to
  the upper 20s/lower 30s.
- The same capture moved the remaining planner cost to shortage scheduling
  (4.27 ms/tick), demand-cache maintenance (4.09 ms/tick), and Production Goal
  scheduling (2.67 ms/tick). `BeingCounts` remained bounded at 0.65 ms/tick.
- A subsequent 957-pass capture after deficit aggregation and per-domain order
  counting reduced `OnTick` again to 10.69 ms, another 20% improvement and
  about 74.5% below the original 41.87 ms baseline. Observed frame rate rose to
  the mid-30s, with brief dips to 28-29 and peaks in the mid-40s.
- Demand-cache maintenance fell from 4.09 to 1.85 ms/tick. The added phase split
  isolated the remaining frame spikes to direct domain shortage assignment
  (3.48 ms average, 189.53 ms maximum); cross-facility work remained bounded at
  0.61 ms average and 3.14 ms maximum.
- The final recipe-index pass caches immutable managed Nano recipe families
  once after recipe registration. Planner lookups retain the same ordered
  equal-best recipe arrays, research gates, ingredient checks, and randomized
  selection without rescanning and sorting the full recipe catalog.
- The accepted 1,017-pass capture reduced `AutoFactorySys.OnTick` to 2.76 ms
  average and 17.25 ms maximum, about 93.4% below the original 41.87 ms
  average. All planning passes completed, with no Auto Factory errors or
  exceptions. Observed frame rate rose to the mid-to-upper 40s, with brief
  dips into the upper 30s and peaks in the lower 60s.
- The 60-tick full reconciliation, shortage-first ordering, Production Goal
  semantics, Core ownership rules, and save schema remain unchanged. The full
  performance set is gameplay-accepted and released as version 1.5.2.
  
## 1.5.1 Brain/Core hostile Mind inventory correction

Version 1.5.1 corrects Production Goal Owned values for every eligible body
part whose Core metadata says it holds a Mind.

- Preserves Core's normal object-state rules for stored, active, equipped, and
  installed parts.
- Counts blank and non-hostile Brains/Cores while excluding Minds from
  born-hostile, anti-player, or currently enemy factions.
- Applies the filtered total to goal evaluation and pending-delivery accounting,
  so capturing a hostile Mind cannot falsely satisfy a production target.
- Rechecks after ordinary object inventory changes, Mind installation/removal,
  and player-faction diplomacy changes.
- Leaves ordinary object products, Being counts, recipes, priorities, scopes,
  saved tables, and the save schema unchanged.

The installed-Core API audit, local build, and in-game count validation are
complete. Quantum Nexus totals confirmed that friendly and neutral Minds count
while truly hostile Minds do not.

## 1.5.0 reusable Production Goals Tables

Version 1.5.0 adds named Production Goals Tables for moving a tuned Goal and
Priority setup between playthroughs.

- `Create Production Goals Table` requires a unique label, captures every
  product whose Goal or Priority is nonzero, saves the table, and makes it the
  currently loaded table.
- `Save Production Goals Table` replaces the loaded table with the current
  grid. When no table is loaded, the panel directs focus to the required label
  field and prompts the player to create one.
- Each saved table has a Load action. Loading deliberately clears the current
  Goal and Priority grid before applying the table, so products omitted from
  the table return to zero instead of retaining values from another setup.
- `Clear all production goals` clears the grid and unloads the selected table
  without modifying its saved copy. Save therefore cannot accidentally replace
  that table with zeros; the player must create or load a table first.
- Saved tables appear in a bounded management list with Load controls and a
  demolition icon matching Planning Overhaul's blueprint management. Deleting
  removes only the globally saved table, preserves the current Goal/Priority
  grid, and unloads the table name when that table was selected.
- The table-label and product-filter fields have isolated pooled-input
  lifecycles. Rebuilding or reopening the panel removes Auto Factory's old
  custom listeners before Core reuses a field for another row, so typing a
  table label cannot also change the product filter.
- The table catalog uses Core's global settings and is therefore available to
  every playthrough. The selected table name is stored in the individual game
  save; existing saves without that optional value continue with no table
  selected.
- Table data uses stable product identifiers. Entries for products that are no
  longer eligible in the installed recipe set are ignored safely when loaded.
- The implementation preserves shortage-first scheduling, research locks,
  owned and pending counts, normal crafting orders, facility scope, and AUMF
  1.4.0 compatibility. Initial naming and saving were gameplay-accepted on
  2026-10-03, and a saved table loaded successfully in a different game save.
  Text-field isolation was gameplay-accepted on 2026-10-03. The Clear/Unload
  and saved-table deletion corrections retain separate acceptance boundaries.

## 1.4.5 robotic Being inventory correction

Version 1.4.5 corrects the Owned count used by robotic Being Production Goals.

- Reads robotic chassis from Core's complete Being data registry instead of
  counting only active entities.
- Counts available coreless chassis in the world and in Mechuary storage, plus
  active, living robots in the player's faction even when they have a Core.
- Excludes inactive Core-equipped robots, expedition members, bodies that left
  for a colony, and missing bodies.
- Recounts after Core installation and removal on the next safe planning tick
  without consuming pending crafted-being delivery credit.
- Runtime-accepted on 2026-09-30: the Production Goals panel reported the
  corrected robotic totals, including `Owned: 19` Carrier Drones against a goal
  of 10, and stopped treating that fulfilled goal as a shortage. The fresh log
  loaded Auto Factory 1.4.5, recorded no failed planning passes, and contained
  no Auto Factory- or AUMF-associated error.
- Leaves biological Being counting, recipes, scheduling, scope, priorities,
  fuel, production timing, AUMF 1.4.0 compatibility, and the save schema
  unchanged.

## 1.4.4 research-locked production correction

Version 1.4.4 prevents Auto Factory from bypassing Core's native research gate
when it creates automated crafting orders.

- Checks `Research.IsDefUnlocked` before scheduling any direct, intermediary,
  or cross-facility recipe, matching Core's native production picker.
- Excludes locked Production Goal roots from the active planning cache while
  preserving their saved target and priority.
- Cancels an already-tracked automated order if either its product or goal root
  is still research-locked.
- Reopens cached goal routes on Core's research-change signal so a preserved
  goal can resume on the next safe planning tick after it becomes available.
- Preserves recipe ranking, shortage-first scheduling, scopes, priorities,
  manual overrides, fuel, energy, production time, AUMF 1.4.0 compatibility,
  and the save schema.

## 1.4.3 managed idle configuration fix

Version 1.4.3 keeps automation-owned Nano facilities configured while they are
idle between orders. This is a Core-status correction; it does not change
production scheduling or save data.

- Enables Core's supported `IsIgnoreUnconfiguredOn` crafter setting for every
  managed Nano Fabricator, Nano Foundry, and Nano Cloning Pod.
- Clears an already-latched `Unconfigured` issue during managed-facility
  maintenance, including after an existing save is loaded.
- Does not create placeholder orders, reserve ingredients, consume fuel or
  energy, change scope or priorities, or affect recipe and goal selection.
- Preserves the AUMF 1.4.0 requirement, external framework reference, and
  existing save schema.

The idle-state behavior described in the 1.4.1 section below is superseded by
this release: an idle Auto Factory facility remains configured because Auto
Factory, rather than a persistent native recipe selection, owns its next order.

## 1.4.2 AUMF theme compatibility update

Version 1.4.2 adopts the AUMF 1.4.0 `ThemeAwareUi` consumer contract. This is
a framework-compatibility and presentation release; it does not change Auto
Factory gameplay or save data.

- Builds against the AUMF 1.4.0 DLL and declares that tested framework level
  through the existing four-argument compatibility handshake.
- Receives AUMF's automatic theme handling for framework-managed tabs,
  scrollbars, and collapsible-header arrows, including live theme reloads.
- Requires no optional theme-helper call or feature gate because Auto Factory
  uses the existing helpers that AUMF 1.4.0 updates automatically.
- Retains the external `Private=False` framework reference and does not bundle
  the AUMF DLL.
- Preserves recipes, scheduling, facility scope, Production Goals, priorities,
  manual overrides, pending-delivery accounting, fuel, energy, production
  timing, building replacement, Core crafting authority, and the save schema.

Static API, build, metadata, and package validation are complete for the staged
artifact. In-game theme and fresh-log acceptance remain a separate release
gate.

## 1.4.1 validation closure

Version 1.4.1 closes the runtime-accepted configuration-status and production-
planning work completed after 1.4.0. It changes when planning work is computed,
not what Auto Factory is allowed to schedule.

### Native configuration status

- Marks a managed facility configured only after Auto Factory successfully
  creates a real Core crafting order.
- Clears the stale `Unconfigured` issue for facilities that are actively
  producing while preserving the warning for a genuinely idle facility with no
  selected recipe.
- Uses Core's supported `MarkConfigured` lifecycle instead of suppressing or
  bypassing the issue system.

### Bounded production planning

- Maintains session-local facility-domain, demand, and priority-ordered unmet-
  goal caches instead of rebuilding unchanged topology after every relevant
  inventory or object signal.
- Tracks affected products and facilities as worksets so bursts of related Core
  events coalesce into focused planning work on the next safe tick.
- Tracks changed lacking-material types separately and refreshes only those
  entries across existing scheduler domains instead of rebuilding every
  material after each deficit or pending-delivery change.
- Caches temporarily unavailable Production Goal routes by product and facility
  family, invalidating them when relevant ingredients or compatible facility
  capacity change.
- Attempts at most eight unresolved Production Goal roots in one planning pass
  and continues deferred roots on later safe ticks.
- Keeps the independent 60-tick full reconciliation clock; partial work never
  postpones this safety pass.
- Reports aggregated planner timings every 60 ticks so short captures include
  `BeingCounts`, `Rebalance`, domain, demand-cache, goal-cache, and scheduling
  phases without per-tick log spam.
- Debounces inventory-driven control-panel refreshes separately from simulation
  planning.
- Preserves shortage-first scheduling, Production Goal priority and equal-
  priority randomization, Global/Room scope, manual overrides, intermediary and
  cross-facility ownership, pending-delivery accounting, Core's final crafting
  authority, and the existing save schema.

### Runtime result

- The accepted 25,920-tick large-save capture completed 433 full safety
  reconciliations with zero failed planner passes.
- `AutoFactorySys.OnTick` averaged 4.323 ms, down from 9.14 ms before the final
  route-cache pass and from the original 41.076 ms bottleneck capture. This is
  approximately an 89.5% reduction from the original measured average.
- Production Goal planning averaged 2.119 ms, down from 8.72 ms, while complete
  rebalancing averaged 4.512 ms, down from 11.65 ms.
- The capture recorded 152,070 goal-route probes, 524,500 cached skips, and
  18,118 bounded continuations. Gameplay remained correct and felt smoother
  during combat, raids, and large reconstruction activity.
- The fresh log contained no Auto Factory or AUMF error, stale `Unconfigured`
  issue, early crafting-responder warning, missing reference, null reference,
  destroyed-after-despawn failure, or input exception.

The 1.4.1 promotion synchronizes release metadata, documentation, and the
compiled assembly after that gameplay acceptance. It does not add a recipe,
scheduler-policy, fuel, production-time, energy, scope, priority, order-
ownership, or save-format change.

## 1.4.0 validation closure

Version 1.4.0 promotes the runtime-accepted additions developed after 1.3.0:
lower Nano facility fuel consumption, native multi-selection Scope controls,
and Nano Fabricator support for Core-defined robotic body parts.

### Nanobot fuel balance

- Halves the shared Nano facility fuel rate from 0.125 to 0.0625 Nanobots per
  active production hour. The Nano Fabricator, Nano Foundry, and Nano Cloning
  Pod all use the same centralized per-step fuel value.
- Continues charging fuel only when a production tick advances progress or
  completes output.
- Retains the existing Nanobot shortage gate, refill behavior, storage, and
  Core-owned explosiveness handling.
- The 2026-08-28 in-game pass confirmed the reduced burn rate. Longer-term
  Nanobot economy balance remains observational and can be revisited if normal
  end-game use still cannot sustain Auto Factory and Nano Hive demand.

### Multi-selection Scope action

- Uses Core's native multi-selection entity-action registry rather than
  patching the bottom bar or adding an AUMF-owned interface.
- Shows a cog-labelled `Scope` action when at least two constructed Nano
  facilities are selected, including homogeneous or mixed facility types.
- Offers explicit `Set all to Global` and `Set all to Room` choices and changes
  only the selected Nano facilities; unrelated selected buildings are ignored.
- Deduplicates multi-tile selections and displays the number of matched Nano
  facilities on the native multi-action badge.
- Reuses each facility's existing saved scope ownership and activity log while
  collapsing the bulk update into one inventory refresh and one pending
  production-planning reconciliation.
- Leaves recipes, active orders, fuel, production time, energy, priorities,
  Production Goals, and the save schema unchanged.
- The 2026-08-28 closeout accepts the native multi-selection workflow as part
  of the validated update without changing individual facility scope behavior.

### Robotic body-part recipe coverage

- Adds Core-defined non-biological body parts from the `Robotics*` recipe
  families to the Nano Fabricator's throughput-ranked recipe pool.
- Uses `BodyPartDef.IsBiological` to preserve the Nano Cloning Pod boundary;
  biological parts, including the Fabricator-crafted Rebreather implant,
  remain excluded from the Nano Fabricator.
- Exposes the resulting object products in the `Robotic Body Parts` Production
  Goals category with the existing owned-count, pending-delivery, priority,
  intermediary, and save/load behavior.
- Does not change source ingredients, output quantities, production time,
  energy, fuel, body-part compatibility, installation, or surgery behavior.
- The 2026-08-28 in-game pass confirmed the expected manual and automated
  recipe lists, Rebreather exclusion, Production Goal scheduling, owned counts,
  save/load behavior, and a clean fresh log for the seven-item acceptance
  checklist.

## 1.3.0 validation closure

Version 1.3.0 promotes the runtime-accepted native same-footprint building
replacement feature from the 1.2.0 development baseline.

- A Nano Foundry ghost can replace a constructed Core Furnace.
- A Nano Fabricator ghost can replace a constructed Core Fabricator.
- A Nano Cloning Pod ghost can replace a constructed Core Cloning Pod.
- Each pair uses a unique `Constructable.CanReplaceGroup` supplied through
  Stardeus's supported JSON patch system; no Core file is overwritten.
- The Nano facilities inherit their source building's TileTransform, so every
  pair has the same footprint, rotation rules, ports, and object layer.
- Core remains responsible for the pending-replacement goal, target materials,
  construction work, cancellation, save/load, old-building removal, supported
  configuration copying, and electrical reconnection.
- Auto Factory continues to discover the completed Nano facility through its
  existing registration/removal signals; no scheduler or save-schema change is
  introduced.

`CanReplaceGroup` is Core's symmetric native replacement mechanism. Deliberately
placing the unlocked source building over its Nano successor can therefore
request the reverse replacement, just as other same-group Core buildings can be
replaced in either direction.

The 2026-08-27 in-game pass confirmed that the overlay replacement workflow is
working as intended. The release promotion changes version metadata,
documentation, and the compiled assembly after that acceptance; it does not
introduce another scheduler, recipe, fuel, scope, timing, or save-schema
change.

## 1.2.0 release snapshot

Version 1.2.0 adds player-directed scheduling priority to individual Production
Goals while retaining shortage-first automation and the existing facility,
recipe, fuel, timing, and scope rules.

- Reworks each Production Goal into a compact Item/Owned/Goal/Priority row.
- Adds a per-product priority from `0` through `100`: zero is the default,
  1 is the lowest explicit priority, and 100 is highest.
- Keeps Room- and Global-scope `Lacking materials` demand ahead of every
  Production Goal, then evaluates unmet goals from highest priority to lowest.
- Shuffles products that share a priority so equal-priority goals do not gain a
  permanent ordering advantage.
- Carries the root product's priority through eligible intermediary and
  cross-facility work without interrupting recipes already in progress.
- Persists nonzero priorities in the backward-compatible
  `ProductionPriorities` save collection; older saves load every priority as
  zero.
- Clamps out-of-range input, rejects nonnumeric text, and makes both Goal and
  Priority clear buttons commit and visibly restore numeric `0` immediately.
- Preserves AUMF 1.3.0 as an external dependency and retains the established
  Steam Workshop identity.

The compact layout, numeric input bounds, and corrected clear-button behavior
have passed in-game acceptance. Focused long-horizon observation of
shortage-versus-goal ordering and equal-priority selection remains separate
from that accepted UI pass. The release promotion only synchronizes version
metadata, documentation, and the compiled assembly; it does not add another
scheduler, recipe, fuel, scope, timing, or save-format change beyond the
Production Goal priority feature.

## 1.1.0 validation closure

The Nano Cloning Pod and being-based Production Goals are runtime-accepted for
version 1.1.0. The 2026-08-25 in-game pass confirmed automated Stem Cells,
Production Goal pigs, Production Goal Sentry Drones, manual Nano Cloning Pod
orders, and continued Nano Fabricator exclusion of Cloning Pod-specific
recipes. All four direction sprites load in game and are accepted for release;
closer rotational-art consistency remains an optional future refinement rather
than committed roadmap work.

The release promotion changes version metadata and documentation after the
accepted implementation. It does not make an additional production, goal,
scheduler, fuel, scope, timing, or save-format change.

For compatibility with saves made before throughput filtering, previously
exposed recipe IDs that are now retired remain registered as hidden load-only
aliases. They never appear in `New Order`, Production Goals, or automation.
If an old save restores an in-flight retired route, Auto Factory cancels that
order explicitly during its first reconciliation pass and can then schedule the
current highest-throughput route. This migration does not change the save
schema.

Production planning is signal-driven and workset-bounded. Relevant material
deficits, supported inventory changes, completed production, manual or removed
orders, facility lifecycle changes, save restoration, and player configuration
changes identify affected products or facilities for the next safe game tick.
Repeated signals in the same update coalesce. Session-local caches retain
unchanged facility domains, demand topology, and the priority-ordered unmet-goal
index. Temporarily unavailable goal routes are remembered by product and
facility family until relevant ingredients or facility capability changes, and
no planning pass recursively probes more than eight unresolved goal roots.

Deferred goal roots continue over later safe ticks. The separate one-in-game-
hour interval, currently 60 ticks, still performs a complete reconciliation for
room, construction, reservation, or lifecycle transitions without a dependable
signal; partial passes never reset that clock. Inventory-driven UI refreshes are
debounced independently from simulation planning. None of these session-local
caches or continuations are written to the save.

## Completed runtime validation

- Version 1.5.1 Brain/Core inventory filtering is gameplay-accepted. Quantum
  Nexus counts confirmed that Auto Factory includes friendly and neutral Minds
  while excluding Minds Core considers hostile toward the player.
- Version 1.5.0 initial naming, saving, and cross-playthrough persistence are
  gameplay-accepted. A saved Production Goals Table loaded successfully in a
  different game save, and the pooled table-label/product-filter isolation was
  subsequently confirmed in game. Clear/Unload and table deletion retain their
  separate acceptance boundaries.
- Version 1.4.5 robotic Being inventory counting is gameplay-accepted. Stored
  coreless chassis and active living player-faction robots produced the
  corrected Owned totals, including 19 Carrier Drones against a goal of 10;
  the fresh log loaded 1.4.5 with no failed Auto Factory planning pass or
  Auto Factory/AUMF error.
- Nano Cloning Pod automation is functionally validated: Stem Cells queued and
  completed automatically, pigs queued from Production Goals, manual Cloning
  Pod orders worked, and Nano Fabricators continued excluding Cloning Pod-only
  recipes. Fabricator-built Sentry Drones also queued from Production Goals.
- The dedicated funnel filter, equality goal, nanobot widget, building art,
  animations, ghosts, icons, and workshop-cover art have been validated in
  game.
- Shortcut assignment, conflict reporting, text-entry suppression,
  enable/disable dispatch, clearing, and the Overview toggle were validated
  under the AUMF 1.3.0 baseline. AUMF 1.4.0 theme compatibility has a separate
  1.4.2 smoke-test gate.
- Global and Room production scope, enclosure pausing and restoration,
  production-goal queueing, lacking-material queueing, intermediary production,
  and cross-facility hand-off logs have been validated in game.
- Save/load and relocation have been validated for Global and Room scope,
  Production Goals, pending-delivery accounting, and automated, manual,
  intermediary, Production Goal, and cross-facility orders. Idle and active
  Nano Fabricators and Nano Foundries retained scope and order ownership;
  facility removal cleared its tracking state; relocated Room-scoped facilities
  responded correctly to enclosure changes; and no duplicate orders, stale
  cancellations, or new load-time exceptions were observed in the tested cases.
- Native crafting-order inspection now begins only after Core registers its
  `GetCraftingOrdersFor` responder and restores saved order lists. The same
  Celesterra save completed two loads in one session with all managed-facility
  behavior intact and zero occurrences of the former early-query warning.
- Representative tier-based durations have been validated for both facilities:
  Silver Ingot at 2h37 in the Nano Foundry and Rubber at 2h15 in the Nano
  Fabricator.
- The Petroleum blank-ingredient and queue/cancel regression passed a focused
  smoke test: the automated Petroleum order retained its Crude Oil ingredient
  row and proceeded normally. The combined shortage-demand, Production Goal,
  and save/load stress case remains in the pre-1.0 checklist.
- AUMF 1.3.0 collapsible Production Goals categories have been validated in
  game. Categories start expanded, collapse independently, preserve UI-session
  state across refreshes, tab changes, and panel close/reopen, temporarily open
  during filtering, restore their prior state when filtering ends, and leave
  hidden goals, owned counts, and Clear All behavior intact. The corresponding
  fresh `Player.log` contained no Auto Factory or AUMF failures.
- Throughput-ranked recipes are runtime-complete. Manual and automated lists
  expose only the highest-output-per-adjusted-hour route for current Core
  duplicates; Converter and lower-throughput routes remain absent; direct,
  intermediary, Production Goal, cross-facility, manual-override, save/load,
  and retired-route behavior passed the explicit in-game checklist. Core has no
  equal-best duplicate after filtering, so the generic availability-aware tie
  branch remains accepted without a naturally occurring Core tie fixture.
- The Cloning Pod product boundary is runtime-complete. Stem Cells, Crawler Goo,
  Protein, and other Cloning Pod products remain absent from Nano Fabricator
  manual orders, automation, and Production Goals; their native Cloning Pod
  recipes remain intact; and a restored retired Stem Cells order was cancelled
  safely without a missing-recipe or load failure.
- Core-aligned explosiveness-provider selection is runtime-complete. Both the
  Nano Fabricator and Nano Foundry retain Nanobot `RefillableStorage` as their
  sole active provider while `MultiCrafter` opts out through Core's supported
  configuration flag. Existing, newly constructed, relocated, saved, and
  restored facilities passed the production and fuel checks. An intentional
  Nano Foundry explosion consumed all internal Nanobot fuel and dropped none,
  and the fresh `Player.log` contained no duplicate-provider, Auto Factory,
  AUMF, missing-reference, null-reference, despawn, or input failure.
- Bounded production planning is runtime-complete. The final 25,920-tick
  large-save capture reduced `AutoFactorySys.OnTick` from the original measured
  41.076 ms average to 4.323 ms, completed 433 full safety reconciliations with
  zero failed passes, and retained normal production during combat, raids, and
  large reconstruction activity. The fresh log contained no Auto Factory,
  AUMF, crafting-responder, `Unconfigured`, missing-reference, null-reference,
  destroyed-after-despawn, or input failure.

## 1.1.0 release snapshot

Version 1.1.0 extends the stable Auto Factory feature set with biological and
manufactured-being automation while preserving Core's native crafting and
being-spawn lifecycles.

- Adds the Tier 5 Nano Cloning Pod, its research, four-direction art, animated
  chamber overlay, Nanobot fuel storage, Global/Room scope, manual orders, and
  full participation in Auto Factory scheduling and persistence.
- Recreates Core Cloning Pod recipe families inside the dedicated
  `AutoCloning` family while keeping every Cloning Pod-specific product excluded
  from the Nano Fabricator.
- Adds Production Goals and exact active-definition Owned counts for biological
  beings and the installed Fabricator being families, including Robot, Android,
  Carrier, Drone, Orbotron, and Sentry products.
- Tracks pending being delivery credit across completion and Core spawning so a
  completed being cannot trigger a duplicate automated order before its active
  count rises.
- Extends facility-family isolation, intermediary selection, cross-facility
  hand-offs, labels, relocation, removal, and save/load restoration to the Nano
  Cloning Pod without changing the save schema.
- Retains throughput-ranked recipes, the Converter boundary, dirty-driven
  planning, tier-based production time, the 10% energy surcharge, Core-aligned
  Nanobot fuel behavior, AUMF 1.3.0 as an external dependency, and the existing
  Workshop item identity.
- Synchronizes the manifest, Release project, editor project, documentation,
  and compiled assembly at version 1.1.0.

## 1.0.0 validation closure

The complete pre-1.0 gameplay matrix has passed in-game acceptance testing.

- The global Auto Factory priority control, including boosted 6/6 operation
  under competing 5/5 work, behaves as intended.
- Nano Foundry research, construction, throughput-filtered Furnace recipe pool,
  Nanobot fuel, recipe-family isolation, automation, and save/load behavior are
  validated.
- Displayed and actual tier-based production times are validated for both
  managed facilities, including a missing/zero-tier product and the 0.1-hour
  minimum.
- Simultaneous Petroleum shortage demand and a Petroleum Production Goal remain
  stable: goal-owned orders retain their Crude Oil ingredient and do not enter
  repeated queue/cancel cycles.
- The Weapons Production Goals category, stored-plus-equipped counts, direct
  and intermediary production, manual orders, and goal persistence through
  save/load are validated.
- Repeated gameplay, save/load, relocation, compatibility, and smoke testing
  found both managed facilities operating as intended. The final fresh-log
  review contains no Auto Factory- or AUMF-associated warning, error, or
  exception, and no crafting-responder, missing-reference, null-reference,
  despawn, or input failure.

## 1.0.0 release snapshot

Version 1.0.0 promotes the runtime-accepted 0.3.2 implementation to the first
stable release. This promotion changes release metadata and documentation only;
the validated recipes, scheduler policy, facilities, scope behavior, production
time, energy, Nanobot fuel, priorities, UI behavior, and save schema are
unchanged.

- Declares the Nano Fabricator and Nano Foundry feature set complete against the
  accepted gameplay and persistence matrix.
- Retains throughput-ranked, availability-aware recipe selection and the
  Converter and Cloning Pod ownership boundaries.
- Retains dirty-driven planning with next-tick signal coalescing and the
  conservative 60-tick fallback reconciliation.
- Retains the AUMF 1.3.0 external framework contract and the existing Steam
  Workshop item identity.
- Synchronizes the manifest, Release project, editor project, documentation,
  and compiled assembly at version 1.0.0 without migrating save data.

## 0.3.2 optimization snapshot

Version 0.3.2 closes the runtime-validated production-planning optimization
developed on 0.3.1. It changes scheduler cadence, not scheduler policy: recipes,
targets, order ownership, scope, production time, energy, Nanobot fuel,
priorities, hauling, UI behavior, and save data remain unchanged.

- Replaced unconditional full reconciliation on every game tick with a
  coalesced dirty flag processed on the next safe tick.
- Limited inventory invalidation to materials used as supported products or
  Nano recipe ingredients and to supported object products.
- Invalidated planning from relevant deficits, inventory, production,
  manual-order, order-removal, factory lifecycle, relocation, save-load,
  scope, goal, and enablement transitions.
- Suppressed nested invalidation from Auto Factory's own planner-driven order
  cancellations so one reconciliation cannot schedule a redundant second pass.
- Retained a 60-tick fallback reconciliation for Core transitions without a
  dependable signal and refreshed room-local pending-delivery accounting at
  that safe planning boundary.
- Completed repeated smoke testing with both facilities operating normally and
  a fresh mod-specific log review showing no Auto Factory or AUMF failures.

## 0.3.1 maintenance snapshot

Version 0.3.1 is a compatibility and presentation maintenance release over
0.3.0. It does not change Auto Factory's recipes, scheduler, production time,
energy, Nanobot fuel, scope rules, Production Goals, order ownership, or save
schema.

- Reworked the Workshop description into the established structured format,
  covering both facilities, throughput-ranked recipes, automation, scope,
  persistence, safety boundaries, and the AUMF 1.3.0 requirement. The existing
  Workshop item identity is preserved and `SteamWorkshop` remains enabled.
- Removed premature native crafting-order reads from component registration
  and system-state loading. Pending ownership restoration and legacy-order
  cleanup now occur during the first ordinary reconciliation tick, after Core
  has registered `GetCraftingOrdersFor` and restored the saved order lists.
- Validated the lifecycle correction through two completed loads of the same
  late-game save in one Stardeus session. The former responder warning did not
  recur, and the fresh log contained no Auto Factory, AUMF, missing-reference,
  null-reference, destroyed-after-despawn, input, or scale-correction failure.
- Reconciled both maintained build projects with the manifest so the release,
  editor, and compiled assembly version surfaces all report 0.3.1.

## 0.3.0 transfer snapshot

Version 0.3.0 consolidates the runtime-validated feature work completed after
0.2.0 without changing Auto Factory's save schema. Every named method and
constructor in the maintained source now has a concise `Purpose` comment so a
future maintainer can trace UI, lifecycle, recipe, scheduler, save/load, and
Harmony-patch responsibilities from the declaration itself.

- Configured the Nano Fabricator and Nano Foundry `MultiCrafter` components with
  Core's `DisableExplosivenessProvider` flag. Their Nanobot
  `RefillableStorage` components remain the sole active providers; no Core
  validation is skipped, and recipes, orders, fuel consumption, production
  timing, and save data are unchanged. Runtime validation passed, including a
  destructive Nano Foundry test that consumed all internal fuel without
  dropping any.
- Replaced product-specific automatic recipe preferences with general
  throughput ranking. Converter sources and lower-throughput alternatives are
  not cloned; equal-best routes remain visible and automation selects among
  those with available ingredients while preserving intermediary and cross-
  facility traversal.
- Preserved Core's Cloning Pod boundary by excluding every product declared by
  its exclusive `Human`, `Animal`, and `Organics1`-`Organics3` recipe families
  before ranking, including any alternate recipe for the same product.
- Added hidden load-only aliases for recipe IDs retired by throughput filtering.
  They prevent missing-recipe failures in older saves while remaining invisible
  to new orders and automation; restored retired orders are cancelled once and
  returned to the current policy.
- Added explicit save/load and relocation transfer for Auto Factory-owned
  in-flight order classification, scope state, and pending-delivery state, and
  complete per-facility cleanup when a managed building is removed.
- Deferred native crafting-order inspection until Core has registered its
  per-crafter order responder and restored saved orders. Registration now only
  records facilities; the first ordinary reconciliation tick restores saved
  ownership and clears legacy orders without producing early-query warnings.
- Preserved Production Goal ownership when the same material is also needed by
  active shortage demand, preventing repeated Petroleum queue/cancel churn and
  stale blank ingredient panels. Material and production callbacks now defer
  reconciliation to the safe clock pass after Core finishes processing the
  current order.
- Extended eligible recipe discovery and global Production Goals to Core's
  `Weapons*` object products. Weapon goals use Core's stored-and-equipped object
  counts and preserve pending output, intermediary roots, cross-facility roots,
  and in-flight order classification through save/load.
- Adopted AUMF 1.3.0 collapsible section headers for Production Goals while
  keeping category state UI-session-only and leaving filtering, targets,
  automation, and save ownership in Auto Factory.
- Added an explicit consumer DLL manifest entry and tightened release output so
  the package Libraries directory contains only `AutoFactory.dll`.

## Throughput-ranked recipe policy

Auto Factory ranks eligible routes by throughput: product output divided by
adjusted Auto Factory production time. For example, a route producing 10 units
in 3 hours is preferred over one producing 5 units in 2 hours because their
effective rates are 3.33 and 2.5 units per hour, respectively. The full batch
may be produced even when current demand is smaller than that batch.

Only the highest-throughput route or routes for each product and facility are
exposed in Auto Factory's recipe list. Lower-throughput alternatives are not
cloned, reducing the manual list and preventing the scheduler from selecting a
knowingly less efficient route. If differently resourced routes tie for best
throughput, all tied routes remain eligible. The scheduler selects from tied
routes whose complete ingredients are available; if more than one tied route is
craftable, random selection is acceptable because time efficiency is equal.
When none is immediately craftable, intermediary and cross-facility traversal
inspect every tied route rather than treating the first unavailable route as
authoritative.

The installed Core recipe set has no equal-best duplicate after filtering. It
selects the Waste route for Biofuel, Crude Oil refining for Carbon, the Oil
routes for Plastic and Rubber, the Iron route for Steel Plate, and the Ice route
for Water. Equal-best handling remains available for future Core or third-party
recipes.

All source recipes whose type is `Converter` remain excluded before ranking.
The Cloning Pod boundary is now facility-specific: products declared by Core's
`Human`, `Animal`, `Organics1`, `Organics2`, and `Organics3` families remain
excluded from the Nano Fabricator product-wide, while the Nano Cloning Pod
recreates those source families inside `AutoCloning`. This preserves ownership
without preventing Auto Factory from automating the native facility role.

## Release history

### 1.5.2

- Replaced broad repeated planner scans with event-scoped dirty material
  demand refresh, aggregate deficit calculation, and per-domain scheduled-order
  counts while retaining the 60-tick full safety reconciliation.
- Prevented object-only inventory decreases from invalidating unrelated
  Production Goal routes, while inventory increases still wake eligible goals.
- Cached immutable managed Nano recipe-family lookups after recipe registration
  without changing research gates, ingredient checks, equal-best ordering, or
  randomized recipe selection.
- Preserved shortage-first scheduling, robotic chassis and Mind/Core ownership
  rules, Production Goal semantics, facility scopes, and the save schema.
- Gameplay acceptance reduced `AutoFactorySys.OnTick` from 41.87 ms to 2.76 ms
  average on the surgery-heavy test save, with a 17.25 ms maximum, no failed
  planning passes, and no Auto Factory errors or exceptions.
- Synchronized the manifest, Release project, editor project, documentation,
  and compiled DLL at version 1.5.2.

### 1.5.1

- Corrected Production Goal Owned values for every eligible Brain/Core-type
  body part by excluding only Minds Core considers hostile toward the player.
- Preserved blank parts plus friendly and neutral Minds, including neutral
  Minds that the Replicator groups under its broader non-player-faction label.
- Rechecks filtered counts after object inventory changes, Mind
  installation/removal, and player-faction diplomacy changes.
- Preserved ordinary object products, Being counts, recipes, priorities,
  scopes, saved Production Goals Tables, and the save schema.
- Gameplay-accepted Quantum Nexus and Quantum Nexus Core counts, then
  synchronized the manifest, Release project, editor project, documentation,
  and compiled DLL at version 1.5.1.

### 1.5.0

- Added labelled, cross-playthrough Production Goals Tables that capture all
  currently set Goal and Priority values.
- Added explicit Load and Save actions: Load replaces the current grid, while
  Save updates only the currently loaded table and prompts for Create when no
  table is selected.
- Made `Clear all production goals` unload the selected table without changing
  its globally saved copy, preventing an accidental Save of an all-zero grid.
- Added a Planning Overhaul-style demolition icon beside every saved table.
  Deletion preserves the current Goal/Priority grid and unloads a deleted table
  when it was selected.
- Released Auto Factory's custom search and Goal/Priority input listeners on
  every panel rebuild and close, preventing Core's pooled table-label and
  product-filter fields from inheriting each other's bindings.
- Stored the reusable table catalog in Core's global settings and added only
  the optional selected-table name to each game save, preserving compatibility
  with existing saves.
- Ignored stale product identifiers safely and scheduled one complete planning
  refresh after applying a table instead of refreshing once per product.
- Synchronized the manifest, Release project, editor project, documentation,
  and compiled DLL at version 1.5.0. Initial naming, saving, and loading in a
  different game save are gameplay-accepted, as is the later text-field
  isolation correction. Clear/Unload and table deletion retain their separate
  acceptance boundaries.

### 1.4.5

- Corrected robotic Being Production Goal Owned counts to represent available
  coreless chassis plus active, living player-faction robots.
- Included coreless chassis stored in Mechuaries through Core's complete Being
  data registry instead of its active-entity collection.
- Excluded inactive Core-equipped robots and unavailable expedition, colony,
  or missing bodies, with safe deferred Core-installation and removal updates.
- Runtime-accepted the corrected Production Goals counts and confirmed a fresh
  1.4.5 log with no failed planning pass or Auto Factory/AUMF error.
- Preserved biological Being counts and all recipe, scheduling, scope,
  priority, framework, and save-schema behavior outside the corrected path.

### 1.4.4

- Matched Core's native research gate before Auto Factory creates automated
  direct, intermediary, or cross-facility orders.
- Kept research-locked Production Goals saved but inactive until the required
  technology unlocks, then invalidated planning caches for immediate retry.
- Cancelled already-tracked automated locked orders during reconciliation
  without clearing the player's target or priority.
- Preserved all recipe, scope, priority, fuel, timing, framework, and save-schema
  behavior outside the corrected eligibility path.

### 1.4.3

- Kept idle Nano Fabricators, Nano Foundries, and Nano Cloning Pods out of
  Core's `Unconfigured` issue list through its supported crafter setting.
- Cleared stale configuration alerts during managed-facility maintenance
  without creating dummy orders or changing production planning.
- Runtime-validated the correction on an idle managed facility.
- Preserved recipes, scheduling, scope, priorities, manual overrides,
  pending-delivery accounting, fuel, energy, production time, Core crafting
  authority, AUMF 1.4.0 compatibility, and the save schema.

### 1.4.2

- Updated the tested/required framework declaration from AUMF 1.3.0 to 1.4.0
  using the established four-argument consumer handshake.
- Adopted AUMF 1.4.0's automatic `ThemeAwareUi` treatment for Auto Factory's
  framework-managed tabs, scrollbars, and collapsible-header arrows.
- Kept the AUMF reference external with `Private=False`; no framework DLL is
  bundled and no optional theme-helper migration is required.
- Preserved all production behavior and the existing save schema. In-game
  theme switching and fresh-log review remain the runtime acceptance gate.

### 1.4.1

- Corrected managed-facility configuration signaling by calling Core's
  `MarkConfigured` only after a real crafting order is created, removing stale
  `Unconfigured` issues without hiding genuinely idle facilities.
- Replaced broad dirty-plan recomputation with session-local domain, demand,
  and priority-ordered goal caches plus affected-product and affected-facility
  worksets.
- Added product/facility-family caching for temporarily unavailable Production
  Goal routes and limited recursive root probing to eight goals per planning
  pass, with deferred continuation on later safe ticks.
- Kept an independent 60-tick full safety reconciliation and separately
  debounced inventory-driven UI refreshes.
- Runtime-validated the planner on a 25,920-tick large save: average Auto
  Factory tick cost fell from the original 41.076 ms capture to 4.323 ms, 433
  full reconciliations completed, and no planner pass failed.
- Preserved facility scope, priorities, manual overrides, intermediary and
  cross-facility ownership, pending-delivery accounting, recipes, fuel,
  production time, Core crafting authority, save schema, the external AUMF
  1.3.0 dependency, and the existing Steam Workshop identity.

### 1.4.0

- Reduced the shared Nano Fabricator, Nano Foundry, and Nano Cloning Pod fuel
  rate from 0.125 to 0.0625 Nanobots per active production hour while retaining
  the existing productive-tick gate, shortage handling, refill storage, and
  Core-owned explosion behavior.
- Added a native cog-labelled Scope action for multi-selected Nano facilities,
  with explicit set-all Global and Room choices, mixed-facility support,
  multi-tile deduplication, and unrelated-selection filtering.
- Added Core-defined non-biological `Robotics*` body-part recipes to the Nano
  Fabricator and exposed them in the `Robotic Body Parts` Production Goals
  category.
- Preserved the biological boundary through `BodyPartDef.IsBiological`, so the
  Fabricator-crafted Rebreather and other biological products remain excluded.
- Runtime-accepted the seven-item robotic body-part checklist, including manual
  and automated production, owned counts, save/load, and a clean fresh log.
- Preserved recipes, ingredients, output quantities, production timing, energy,
  Production Goal priority behavior, save schema, external AUMF 1.3.0
  dependency, and Steam Workshop identity outside the stated additions.

### 1.3.0

- Added native overlay replacement from Furnace to Nano Foundry, Fabricator to
  Nano Fabricator, and Cloning Pod to Nano Cloning Pod.
- Used Stardeus's supported JSON patch system to assign matching replacement
  groups without overwriting Core files or patching placement code.
- Retained each source facility's footprint, rotation rules, ports, and object
  layer through the Nano definition's existing inheritance.
- Left material delivery, construction work, cancellation, save/load, old-tile
  removal, supported configuration copying, and electrical reconnection under
  Core's native pending-replacement lifecycle.
- Runtime-validated the overlay upgrade workflow and retained the existing Auto
  Factory facility registration, automation, recipes, fuel, timing, scope, and
  save schema.
- Preserved AUMF 1.3.0 as an external dependency and retained the established
  Steam Workshop identity.

### 1.2.0

- Added persistent per-product Production Goal priorities from 0 through 100.
- Kept Room/Global shortages first, then ranked unmet Production Goals from
  highest priority to lowest with randomized selection among equal priorities.
- Applied each goal root's priority to eligible intermediary and cross-facility
  scheduling without cancelling active work.
- Replaced the tall goal-entry layout with compact Item/Owned/Goal/Priority
  rows and retained searchable, collapsible categories.
- Runtime-validated the compact presentation, numeric filtering and bounds,
  and immediate Goal/Priority clear-to-zero behavior.
- Preserved the existing facility behavior, throughput-ranked recipes,
  Converter and Cloning Pod boundaries, Core-aligned fuel/explosion handling,
  AUMF 1.3.0 external dependency, and Workshop identity.

### 1.1.0

- Added the automated Nano Cloning Pod, its research, Nanobot fuel, scope,
  production UI, labels, directional art, and animation.
- Added Cloning Pod materials, objects, and biological beings to Production
  Goals through the dedicated `AutoCloning` recipe family.
- Added eligible Fabricator-built beings to Nano Fabricator manual orders and
  Production Goals while continuing to exclude Cloning Pod-specific products.
- Added exact active-being counts, pending-spawn delivery credit, and lifecycle
  invalidation to prevent duplicate being orders.
- Extended scheduling, intermediary and cross-facility work, relocation,
  removal, and save/load restoration to the third facility family.
- Runtime-validated automated Stem Cells, Production Goal pigs and Sentry
  Drones, manual cloning orders, recipe-boundary preservation, and all four
  installed direction sprites.
- Preserved AUMF as an external dependency and retained the established Steam
  Workshop identity.

### 1.0.0

- Promoted the complete Nano Fabricator and Nano Foundry feature set from the
  runtime-accepted 0.3.2 baseline to the first stable release.
- Closed the remaining priority, Nano Foundry, production-time, concurrent
  Petroleum demand, Weapons, save/load, relocation, compatibility, and log
  validation matrix.
- Retained throughput-ranked recipes, availability-aware equal-best selection,
  native facility boundaries, Core-aligned Nanobot fuel and explosiveness
  handling, Global/Room automation, Production Goals, and order ownership.
- Retained next-tick dirty-signal planning with a 60-tick fallback and made no
  recipe, scheduler-policy, UI-behavior, fuel, timing, priority, or save-schema
  change during the version promotion.
- Synchronized the manifest, Release project, editor project, README, and
  compiled assembly at 1.0.0 while preserving AUMF as an external dependency
  and preserving the established Workshop item identity.

### 0.3.2

- Replaced every-tick full production planning with next-tick dirty-signal
  coalescing and a conservative 60-tick reconciliation fallback.
- Filtered inventory invalidation to supported Nano products and ingredients,
  while retaining order, facility, relocation, save/load, scope, and goal
  lifecycle coverage.
- Preserved all recipe, scheduler-policy, fuel, timing, priority, UI, and save
  semantics while reducing redundant idle planning work.
- Closed the optimization after repeated Nano Fabricator and Nano Foundry smoke
  tests and a fresh log review with no Auto Factory or AUMF failures.

### 0.3.1

- Updated the Workshop-facing description and retained the existing published
  item identity with `SteamWorkshop` enabled.
- Deferred all native crafting-order inspection until Core's responder and
  restored order lists are available, eliminating the load-time
  `GetCraftingOrdersFor` early-query warning without changing automation or
  persistence semantics.
- Runtime-validated the correction across two loads of the same late-game save
  in one session, with normal Auto Factory behavior and a clean mod-specific
  log result.
- Synchronized the manifest, Release project, editor project, and compiled DLL
  version surfaces at 0.3.1.

### 0.3.0

- Added the Tier 4 Nano Foundry, its dedicated recipe family and art, and
  scope-aware Nano Fabricator-to-Nano Foundry dependency hand-offs.
- Added Global/Room factory scope, enclosure pausing, shared priority and
  labeling, activity logs, and complete state cleanup when a facility is
  removed.
- Extended Production Goals to object products, including the Weapons
  category and stored-plus-equipped counts, and adopted AUMF 1.3.0
  independently collapsible category headers with session-only state.
- Preserved scope, Production Goals, pending deliveries, and manual,
  automated, intermediary, goal, and cross-facility order ownership through
  save/load and facility relocation.
- Deferred saved-order ownership restoration and legacy-order cleanup to the
  first ordinary reconciliation tick, after Core's crafting responder and
  saved order lists are available.
- Reconciled order callbacks so Petroleum and other overlapping shortage/goal
  demand retain ingredients and avoid repeated queue/cancel churn.
- Ranked eligible routes by output per adjusted production hour, retained only
  equal-best ties, and made tie selection ingredient-aware while preserving
  intermediary and cross-facility traversal.
- Excluded Converter recipes and every Cloning Pod product before throughput
  ranking, preserving their native facility boundaries.
- Added hidden load-only aliases for retired recipe IDs so older in-flight
  orders load safely before being reconciled to the current recipe policy.
- Aligned both facilities with Core's explosiveness-provider contract by
  retaining Nanobot fuel storage as the sole active provider and disabling the
  `MultiCrafter` provider through Core's supported definition flag.
- Added concise purpose comments to every maintained source method and
  constructor to make UI, recipe, scheduler, lifecycle, and save logic easier
  to trace.

### 0.2.0

- Updated the AUMF compatibility declaration to 1.2.0 and added its centralized,
  guarded shortcut service for enabling or disabling Auto Factory.
- Added an AUMF-managed top-bar status widget and resizable control panel.
- Added persistent per-material global inventory production goals.
- Added target-driven production alongside native `Lacking materials` demand.
- Added event-driven inventory displays without rebuilding the panel every
  simulation frame.
- Added complete AUMF widget, panel, and game-signal cleanup during unload.

### 0.1.0

- Added Tier 5 Nano Fabrication research and the Nano Fabricator.
- Added universal eligible recipe cloning with a 10% energy surcharge.
- Added Nanobots fuel and one-order-at-a-time automatic shortage processing.
- Added multi-machine work distribution, ingredient-aware selection, and
  recursive eligible intermediary production.
- Added automatic completed-order cleanup and selected-building UI refresh.
