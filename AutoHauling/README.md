# Auto Hauling

Auto Hauling periodically finds eligible loose cargo that has no task and asks
Stardeus to create its ordinary Store task. Stardeus continues to choose the
storage destination and worker and remains responsible for storage filters,
priorities, pathfinding, retries, and completion. Worker bidding is managed
entirely by Stardeus's current scheduler.

## Safety boundary

- The master switch is disabled on a brand-new save.
- Every eligible material, object category, and specific item is enabled by
  default underneath the master switch.
- Existing tasks, reservations, carried items, stored items, unavailable
  objects, Forbidden items, and material piles marked unreachable by Core
  are left alone by automatic scans.
- Auto Hauling never cancels assigned, active, disabled-priority, failed,
  cooling-down, or unrelated work. Its optional stale-task refresh is limited
  to eligible ordinary Store tasks that have remained continuously unassigned
  and bidding for more than two in-game days.
- Worker selection stays with Core. Auto Hauling never changes a job setting,
  interrupts current work, or bypasses task feasibility.
- Unusable storable objects are skipped so Stardeus's storage-order helper
  cannot divert the request into disposal.
- Quality-bearing material piles remain outside the automation lane.
- Hullpiercers, Storage Capsules, Artillery Fungus, whole Beings and bodies,
  and mind-bearing or mind-associated objects remain hard-protected.

Whole-Being hauling is intentionally outside Auto Hauling's scope. Core's
multi-selection and Morgue/Mechuary actions already provide a concise manual
workflow for biological and mechanical Beings.

## Stale Store task refresh

While the master automation switch is enabled, a separate saved toggle allows
Auto Hauling to refresh eligible ordinary cargo Store tasks regardless of who
created them. The toggle defaults on for a brand-new save; it migrates off for
an existing pre-0.3.0 save so an already-enabled master switch cannot begin
cancelling tasks until the player deliberately enables the new behavior. The
master switch remains the top-level gate and still defaults off on a brand-new
save.

A task qualifies only when it has remained continuously in Core's `Bidding`
state for more than two in-game days with no assigned worker or live bid. The
target must still be loose, available, enabled by Auto Hauling's cargo settings,
accepted by compatible storage, and inside every Intelligent Recycling, Part
Restoration Unit, quality, mind, body, and special-target boundary. Priority-0
tasks, assigned work, active work, failed or cooling-down work, restricted-worker
tasks, Optimization Mod dormant tasks, and non-Store jobs that merely use the
Hauling skill are never refreshed. Forbidden cargo and material piles marked
unreachable by Core are also excluded from refresh.

The old task is cancelled through Core and the normal cargo scan recreates it
through the same vanilla Store-order path during that scan. The replacement
keeps the old task's priority. Core marks a material pile Forbidden whenever
its Store task is cancelled; Auto Hauling restores only the exact allowed state
that its own refresh cancellation changed so the replacement can be requested.
It does not clear a pre-existing Forbidden flag or widen the manual **Allow
Forbidden Loose Cargo** action.

## Core-managed worker bidding

Stardeus v0.15.0-rc332.3211 indexes bidding goals by job and priority, rotates
workers and goal buckets, and lets unsuccessful bidders try other candidates
across multiple rounds. These changes address the scheduler limits and bid
collisions that Auto Hauling's optional idle assist originally worked around.

Version 1.1.5 retires that private auction and its 30-tick callback, task index,
and bid counters. The Controls tab now shows **Idle-worker bidding — Managed
by Stardeus**. Core selects workers independently of Auto Hauling's master
switch. Eligible loose-cargo discovery and the optional stalled Store-task
refresh continue to use their existing controls.

Save schema 3 is unchanged. The former `IdleHaulingBidAssistEnabled` preference
is read and written only for compatibility; it never activates bidding. Old
assist counters in the loaded save container are preserved as historical data
for rollback and are no longer displayed as current activity. Existing cargo
settings, tracked Store tasks, and failure history retain their usual formats.

## Intelligent Recycling cooperation

Auto Hauling can optionally discover Intelligent Recycling's read-only
API without a compiled assembly dependency. Ordinary quality-bearing storable
objects, including eligible loose body parts and equipment, are considered only
when all of the following are true at the current decision point:

- Intelligent Recycling contract version 1 is loaded.
- Capability 128 (`PerObjectDispositions`) is available.
- Intelligent Recycling reports its system and game state initialized.
- `TryGetObjectSnapshot` succeeds for the current object.
- Both `ReleasedForAutoHauling` and `CanCurrentlyBeHauled` are true.

Auto Hauling never reads processor thresholds or reproduces Intelligent
Recycling's category, protection, or ownership calculations. Snapshot results
are queried again on later scans rather than cached as object ownership.

When Intelligent Recycling is absent, initializing, incompatible, missing the
required capability, or throws during a query, ordinary non-quality hauling
continues while every quality-bearing object remains fail-closed. Quality-item
settings mean "eligible if Intelligent Recycling releases this object," never
"override Intelligent Recycling."

Controls, Items, and the top-bar tooltip visibly report this diminished state.
The panel uses a red quality-hauling notice while also stating that Intelligent
Recycling is optional and non-quality hauling remains available. Saved quality
item preferences remain configurable and are not erased while the API is
unavailable.

## Part Restoration Unit cooperation

Part Restoration Unit is optional and is discovered through its read-only
contract-1 API without a compiled dependency. Auto Hauling queries that API
only for quality-bearing body parts. If PRU is installed, the body part must be
affirmatively marked `ReleasedForExternalAutomation` before Auto Hauling can
create or refresh its Store task. Within an installed PRU, a missing or
incompatible API or a failed query is treated as no permission for that body
part, matching PRU's published coordination contract. If PRU is not installed,
this additional body-part gate does not apply and ordinary cargo behavior is
unchanged.

## Interface

Auto Hauling requires Auto Utilities Mod Framework 1.4.0. Its AUMF top-bar
widget opens a managed panel with Controls, Items, and Failures tabs. AUMF's
managed tabs, vertical scrollbars, and collapsible-section indicators follow
the selected Stardeus theme and live theme reloads. Item categories can be
searched, enabled or disabled as a group, or configured one item at a time.

The Controls tab reports the optional Intelligent Recycling connection as one
of: Ready, Not installed, Initializing, Unsupported contract, or Per-object
disposition unavailable. Every non-ready state displays a red notice that only
the quality-object lane is unavailable and that ordinary non-quality hauling
continues. The Items tab shows the same limitation and marks enabled quality
preferences as waiting for Intelligent Recycling without disabling their
toggles. It also reports how many quality-bearing body parts were held by an
installed Part Restoration Unit during the latest scan.

Controls explains that idle-worker bidding is managed by Stardeus. The former
**Prompt idle beings to haul** toggle and its bid diagnostics are retired.

The Controls tab also provides **Allow Forbidden Loose Cargo**. This is always
a deliberate manual action. It removes every vanilla Forbidden flag
currently found on a loose material pile or storable object, including quality
objects, Intelligent Recycling targets, and mind-associated objects. It does
not create or cancel a task, change a reservation, or modify either mod's
settings. **Warning:** Forbidden now blocks more than hauling. Removing it can
also permit construction, crafting, eating, equipment use, or processing by
other mods where applicable. It does not clear Core's separate unreachable-pile
marker. The action reports the number of flags removed, schedules
an early normal Auto Hauling scan, and never runs repeatedly on its own.

An optional AUMF keyboard shortcut invokes that exact same manual action once
per keypress. It is unassigned by default and stored in AUMF's global settings.
The Controls tab can assign, change, cancel capture, or clear the shortcut and
shows AUMF's conflict status. AUMF suppresses it during text entry or any
shortcut capture and rejects keys reserved by Stardeus, Steam, or another
registered mod. Assigning a key does not run the action; pressing the assigned
key later immediately clears every currently detected loose Forbidden flag.

## Diagnostics

Auto Hauling tracks the vanilla goals it created and retains Core-provided
failure reasons in its bounded Recent Haul Failures list. Routine order or API
query outcomes are not written to `Player.log`. The mod does not force retries;
Stardeus owns the goal lifecycle. When a Core failure reason contains a map
coordinate or linked being name, that exact portion can be clicked to focus the
reported location or being.

## Release history

### 1.1.6

- Updated material and storable-object access to Core's current `IsForbidden`
  API, fixing the removed-member exceptions in scans, Controls previews,
  manual cleanup, and eligible stale-task refresh.
- Added Core's separate unreachable-pile refusal to automatic Store creation
  and stale refresh. These refusals count as unavailable, not Forbidden.
- Kept the manual button and shortcut as deliberate full Forbidden overrides
  within their existing loose, uncarried cargo scope; expanded both warnings
  to cover construction, crafting, eating, equipment, and other mod processing.
  Neither action clears the separate unreachable marker.
- Preserved native setter side effects and the narrow restoration of an
  allowed pile after Auto Hauling's own stale-task cancellation.
- Retained save schema 3, cargo settings, tracked tasks, failure history,
  Core-managed bidding, optional integration contracts, and AUMF 1.4.0.
- Built against Stardeus v0.15.0-rc337.3219. Source, binary, and automated
  compatibility checks are separate from fresh startup/gameplay validation,
  which remains pending.

### 1.1.5

- Retired the private idle-worker auction after Core rc332 introduced indexed,
  rotating candidate selection and multiple rounds of collision resolution.
- Removed the call to the retired `AIAuction.Run(..., IPriorityQueue<AIGoal>)`
  signature that disabled idle assistance with a `MissingMethodException`.
- Removed the assist's tick listener, Hauling-goal index, scratch queues,
  worker checks, and obsolete bid counters from the UI.
- Replaced the assist toggle with a Core-managed bidding notice in Controls
  and updated the top-bar tooltip and Workshop description.
- Preserved save schema 3, the legacy assist preference and historical data,
  cargo settings, tracked task identities, and the 50-entry failure history.
- Built against Stardeus v0.15.0-rc332.3211 and installed AUMF 1.4.0.
- Runtime validation completed on 2026-10-15: the user confirmed the new
  Core-managed bidding notice, continued hauling, and settings surviving
  save/reload. Fresh logs confirmed 1.1.5 loaded and initialized successfully,
  with no Auto Hauling errors or recurrence of the retired auction exception.

### 1.1.4

- Updated idle-worker eligibility for the current Core API by removing the
  retired worker-wide `GoalFailStreak` check. Core's task-specific cooldowns,
  retry backoff, and remaining native worker guards continue to apply.
- Added Core's current last-moment doability safeguard to assisted winning bids
  so storage or another transient requirement cannot become stale between
  bidding and assignment.
- Retained the existing save schema, default-off idle assist, 100-being and
  40-task batches, 30-tick cadence, and all cargo and companion-mod boundaries.

### 1.1.3

- Changed the idle assist to auction one task at a time against the remaining
  eligible worker batch.
- Prevented Core's same-best-worker collision from discarding later task bids
  without trying another eligible worker. This was responsible for the
  repeatable roughly 19-of-40 assignment pattern with 100 available bidders.
- Retained the 100-being, 40-task, 30-tick validation window and all native
  planning, permission, safety, reachability, job, and assignment checks.

### 1.1.2

- Corrected failure-history links to use Stardeus's live input polling and UI
  raycast path, allowing coordinates and being names to work directly from the
  history row instead of only inside its tooltip.
- Added detailed, bounded idle-assist counters for scanned, idle,
  Hauling-enabled, eligible, and docked bidders plus indexed, bidding, won, and
  assigned tasks.
- Added separate docked winning-bid and accepted-assignment counts so docked
  robot participation can be proven without per-task `Player.log` output.

### 1.1.1

- Made map coordinates and Core-linked being names in failure reasons clickable
  so the player can focus the reported location or being directly.
- Increased the optional idle hauling assist's validation window to 100 idle
  beings and 40 bidding Hauling tasks every 30 game ticks.
- Split the larger window across bounded native Core auctions so Core's
  256-comparison cap does not truncate the requested coverage. This aggressive
  setting remains subject to in-game performance validation.

### 1.1.0

- Added an optional, saved, default-off idle hauling bid assist for very large
  crews whose useful workers can fall outside Core's bounded auction slice.
- Reused Core's `AIAuction`, planner, bidding, and assignment path; every being
  still needs the Hauling job and must pass native task permission, ability,
  cooldown, safety, planning, and reachability checks.
- Runs after Core's normal AI tick so personal goals and ordinary bidding keep
  first choice, and never interrupts busy, direct-controlled, incapacitated, or
  otherwise ineligible beings.
- Added a signal-maintained Hauling-goal index and rotating batches capped at
  32 idle beings by 8 bidding goals every 30 ticks, matching Core's 256-check
  auction ceiling without a new every-tick population scan.
- Excluded priority-0 and nanobot-owned work while allowing any other ordinary
  colony task whose actual job is Hauling, regardless of who created it.
- Added last-pass agent, goal, and assignment counts plus a one-fault session
  circuit breaker to prevent an unexpected Core API change from spamming the
  log.

### 1.0.0

- Promoted the runtime-validated feature set to the first stable release with
  no remaining planned gameplay features.
- Hardened optional API discovery and query failures so malformed or reloaded
  companion assemblies cannot escape into Auto Hauling's scan loop.
- Updated Part Restoration Unit cooperation to use its authoritative
  `ReleasedForExternalAutomation` decision and to fail closed only for
  quality-bearing body parts when PRU is installed but uncertain.
- Added tracked-goal target identity validation, defensive save-data
  sanitization, idempotent UI disposal, constructor rollback, and null-safe
  layout and shortcut status handling.
- Added functional comments around load sequencing, scan eligibility,
  stale-task refresh, tracked-goal reconciliation, bounded diagnostics, and
  parallel-array save restoration.
- Preserved the accepted 300-tick active-object scan, 30-tick tracked-goal
  reconciliation, vanilla Store-task path, default-off master switch, and all
  compatibility boundaries.

### 0.3.1

- Reduced tracked Store-goal safety reconciliation from every game tick to
  every 30 ticks while tracked goals exist.
- Retained Core's immediate removal signal for user-initiated cancellation and
  a one-time post-load reconciliation before the interval begins.
- Chose the 30-tick interval below Core's minimum 32-tick counted colony-goal
  retry delay, preserving failure capture before the earliest normal retry.
- Eliminated reconciliation work entirely while no Auto Hauling Store goals
  are being tracked.

### 0.3.0

- Added an independently saved, master-gated stale Store task refresh, enabled
  by default on new saves and safely migrated off for pre-0.3.0 saves.
- Refreshes eligible ordinary cargo Store tasks from any creator after more
  than two continuous in-game days in Core's unassigned bidding state.
- Preserves the prior task priority and recreates work through the existing
  vanilla Store-order path during the same Auto Hauling scan.
- Excludes assigned/active work, priority-0 and restricted-worker tasks,
  Optimization Mod dormant tasks, non-Store Hauling jobs, and anything outside
  Auto Hauling's item, storage, quality, Intelligent Recycling, Part Restoration
  Unit, body, mind, or special-target boundaries.
- Restores only the material-pile allowed state changed as a direct side effect
  of Auto Hauling's own Core cancellation; pre-existing Forbidden flags remain
  untouched.
- Added last-scan found, cancelled, and successfully requeued counts to the
  Controls panel and top-bar summary.
- Closed the proposed whole-Being hauling scope item; Core's multi-select
  Morgue/Mechuary controls already cover that workflow adequately.

### 0.2.5

- Added an optional, user-assigned AUMF shortcut for **Allow Forbidden Loose
  Cargo**, unassigned by default.
- Routes the panel button and shortcut through the same guarded manual action;
  neither creates recurring behavior or changes the full-override boundary.
- Added assignment, capture cancellation, clearing, conflict status, and an
  explicit immediate-action warning to Controls.
- Added the current binding and last cleanup result to the top-bar tooltip.
- Ignores shortcut presses when no game state is loaded and retains AUMF's
  once-per-press, text-entry, capture, reserved-key, and conflict protections.

### 0.2.4

- Validated the consumer handshake against Auto Utilities Mod Framework 1.4.0.
- Adopted AUMF's automatic theme-aware tabs, managed vertical scrollbars, and
  collapsible-section indicators without changing Auto Hauling's layout,
  callbacks, saved settings, or hauling behavior.
- Retained the deliberate red Intelligent Recycling availability notice as a
  semantic warning across Stardeus themes.

### 0.2.3

- Added optional, read-only Part Restoration Unit compatibility.
- Leaves damaged detached body parts alone when an idle restoration unit would
  claim them and preserves existing restoration reservations.
- Adds no hard dependency; behavior is unchanged when Part Restoration Unit is
  not installed.

### 0.2.2

- Changed recurring cargo scans to iterate Core's maintained active-object
  collection instead of traversing the complete persistent-object dictionary.
- Preserved the defensive active-state check and every existing eligibility,
  ownership, Forbidden, reservation, storage, body, and Intelligent Recycling
  boundary after narrowing the scan population.
- Reused tracked-goal snapshot storage instead of allocating a new array on
  every game tick while diagnostic goals are active.
- Retained the per-tick safety reconciliation cadence for a later Core timing
  audit; version 0.3.1 completed that audit and safely reduced the cadence.

### 0.2.1

- Added conspicuous red Controls and Items notices whenever the optional
  Intelligent Recycling API is not ready, while explicitly confirming that
  non-quality Auto Hauling remains available.
- Marked enabled quality-item preferences as temporarily unavailable without
  erasing or disabling the user's saved choices.
- Added the diminished-quality state to the top-bar tooltip without treating it
  as a Store failure or writing per-object messages to `Player.log`.
- Hardened reflective discovery so the API type is accepted only from the
  assembly named `IntelligentRecycling`.

### 0.2.0

- Added optional, reflective Intelligent Recycling contract-1 cooperation for
  ordinary quality-bearing storable objects.
- Required capability 128 and both positive per-object release properties;
  every absent, incompatible, unavailable, or failed API state remains
  fail-closed for quality objects.
- Added the Intelligent Recycling compatibility state and held-object count to
  Controls.
- Expanded quality-bearing object settings while retaining hard exclusions for
  material piles, special cleanup targets, whole Beings/bodies, and
  mind-associated objects.
- Changed the manual Forbidden action to remove all currently detected loose
  item flags without creating work or writing a warning to `Player.log`.

### 0.1.0

- Initial Workshop-ready release with vanilla Store-task automation, granular
  cargo controls, bounded failure diagnostics, and conservative hard
  exclusions.
