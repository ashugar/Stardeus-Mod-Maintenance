# Colonist And Bots Auto Upgrade (CARAU)

CARAU evaluates installed body parts, selects compatible replacements from
available inventory, and queues ordinary Stardeus surgery and assembly work.

## Requirements

- Stardeus `0.15.0`
- Auto Utilities Mod Framework (AUMF) feature level `1.4.0`

CARAU registers through AUMF's consumer compatibility handshake before
initializing shared AUMF UI components. CARAU has been reviewed and built
against AUMF `1.4.0`, including its compatibility diagnostics,
theme-aware panel surfaces, tabs, cards, controls, sliders, inputs, and
scrollbars, plus version-gated feature infrastructure and centralized guarded
shortcuts.
CARAU does not currently use AUMF's optional Collapsible Sections feature.

## Opening CARAU

- The permanent plain CARAU top-bar widget opens or closes the main interface.
- A second CARAU widget appears only while tracked operations, automatic-retry
  waits, or retained failures exist. It opens or closes Tracked Operations.
- An optional global keyboard shortcut for the main interface can be assigned
  from CARAU's Settings tab.

The former Stardeus Overlays-menu launcher was retired after the two-widget
model completed live validation.

## Release history

### 1.1.3

- Restores `CARAUOperationTrackingSys` initialization on Stardeus
  `v0.15.0-rc313`. Core now provides two `ApplySurgicalOpFailure` overloads,
  which made CARAU's earlier name-only Harmony target ambiguous.
- Binds the surgery-outcome observer to Core's current eight-parameter method,
  including `performerLink`. Native failure capture for automated hubs remains
  intact.
- Leaves operation queueing and failure classification unchanged.
- Runtime-validated startup with CARAU 1.1.2 on rc313. The fresh Player.log
  contains no tracking-system initialization failure, ambiguous Harmony match,
  or CARAU warning or exception.

### 1.1.2

- Adds an optional, default-off Firewall Chip prevention setting for living,
  player-owned mechanical Beings with Stardeus's active `Hacked` condition.
- Queues an ordinary native Implant surgery only when the Being has an empty,
  usable Implant slot, a Firewall Chip is available, and the configured
  automatic surgery threshold is met. An installed Implant is never replaced.
- Tracks each CARAU-created Firewall Chip procedure in Tracked Operations and
  uses the existing bounded retry path for temporary inventory, facility, and
  targetability blockers.
- Keeps Firewall prevention separate from Mood Regulator prevention while both
  use the same preventative-implant eligibility and queueing service.
- Runtime-validated the hacking trigger, native procedure queue, and Tracked
  Operations handoff before promoting the feature from the 1.1.1 test build.

### 1.1.1

- Restores a manual, confirmation-only escape hatch for player-owned
  mechanical Beings with welded unusable parts. The action uses Stardeus's
  native body-part cleanup, then irreparably destroys the extracted part and
  permanently destroys the emptied slot.
- Makes a welded unusable Core eligible through the same action. A welded
  usable Core becomes eligible only when every Battery slot or every Mobilizer
  slot is welded and unusable; multi-slot groups require every slot.
- Preserves the original mind inside an extracted Core, recalculates the
  chassis condition, and re-resolves all eligibility immediately after the
  player confirms the irreversible action.
- Keeps destructive welded-part removal outside normal upgrades, Queue All,
  Batch Queue, operation tracking, and retry automation. The action remains
  unavailable while native or CARAU work is active for the Being.
- Runtime-validated ordinary welded/unusable removal and the terminal-group
  usable-Core exception through successful disassembly recovery.

### 1.1.0

- Raises CARAU's required AUMF feature level from `1.3.0` to `1.4.0` and
  applies the selected AUMF theme throughout the Main and Tracked Operations
  interfaces.
- Themes panel and control strips, tabs, ordinary action buttons, sliders,
  numeric inputs, scrollbars, Being cards, body-part cards, and their text
  while preserving CARAU's semantic warning, failure, and operation colors.
- Migrates Slots, upgrade-policy, retry-policy, Mental Breakdown prevention,
  and Debug-category choices to AUMF's shared state-card control.
- Adds one CARAU compact-card adapter for choices without detail text, reducing
  the title-to-state gap and increasing the Enabled/Disabled state font while
  leaving explanatory retry-policy cards unchanged.
- Runtime-validated theme application and compact state-card readability with
  the AUMF Elysium theme.

### 1.0.1

- Adds selected-Being and `Queue All Remove All Parts` actions for terminal
  mechanical chassis. A Being qualifies when every present slot in at least
  one Battery, Core, or Mobilizer group is destroyed or contains an unusable
  welded part; multi-Battery and multi-Mobilizer groups must be terminal in
  every slot.
- Queues Stardeus's native `RemoveAll` surgery so native attachment order and
  removability remain authoritative. Destroyed slots, welded parts, and other
  non-removable parts remain in place.
- Tracks each CARAU-created mass operation as one persistent `All Parts` card
  through save/load, completion, failure, external cancellation, and CARAU
  cancellation without weakening exact-object ownership for ordinary swaps.
- Widens the Main tab's Being and batch-action column so the longer Remove All
  batch label and count remain inside the list pane.

### 1.0.0

- Establishes CARAU's first feature-complete release for its intended scope,
  promoting the gameplay-accepted `0.5.3` behavior without further functional
  changes.
- Includes authoritative compatibility and inventory evaluation, independent
  Tier and Quality policies, native procedure priority, focused missing- and
  bad-part actions, order-aware batch planning, persistent operation tracking,
  bounded retry and recovery, and explicit native-outcome classifications.
- Supports surgery for living biological and mechanical Beings plus safe
  non-missing-part maintenance of dead mechanical Beings. Missing-part
  installation is restricted to living Beings, including selected and batch
  actions, stale UI requests, restored state, and retry paths.
- Completes the final regression pass with the four Tier/Quality combinations,
  focused restoration actions, the earlier nearly 500-operation run, clean
  package audits, and direct in-game acceptance of the dead-Being guard.

### 0.5.3

- Prevents `Install Missing` and `Queue All Missing` from targeting any dead
  Being. Dead targets are excluded while action counts are built, so the
  selected-Being and batch controls do not appear or enable incorrectly.
- Enforces the same restriction in the authoritative evaluator so stale UI,
  save/load reconstruction, and retry paths cannot queue a missing-part
  installation after the Being dies.
- Preserves dead mechanical maintenance through normal upgrades and
  `Replace Bad Parts`; only installation into an already missing slot is
  excluded for dead Beings.

### 0.5.2

- Consolidates retry-budget exhaustion, occupied-slot ownership, and fresh
  retry-decision validation shared by Hub-availability and policy-driven retry
  workflows. The accepted limits, chance gate, stale-ownership behavior, and
  messages remain unchanged.
- Removes pass-through `*Core` wrappers from inventory, upgrade analysis, and
  queue services so their public authoritative methods contain the behavior
  directly.
- Reduces maintained C# source by 59 lines while preserving the established
  evaluator, compatibility, reservation, native surgery, and outcome contracts.
- Adds a repeatable release audit covering version metadata, Workshop length,
  external AUMF integration, serialization keys and migration, reason-code
  explanation coverage, restoration-policy boundaries, and staged/live parity.
- Adds a v1.0 regression matrix that separates current proof, earlier accepted
  gameplay, and focused final-candidate scenarios still requiring replay.

### 0.5.1

- Adds independent, save-specific `Higher-Tier Upgrades` and `Same-Tier
  Quality Upgrades` controls to the Quality tab. Both default to enabled, so
  existing saves retain CARAU's previous behavior.
- Applies the controls inside candidate selection: disabling Tier upgrades
  still allows an eligible same-tier Quality replacement, and disabling
  Quality upgrades still allows an eligible higher-Tier replacement.
- Preserves CARAU's existing tier-first ordering whenever both paths are
  enabled. The Quality threshold and minimum 5% improvement continue to apply
  only to same-tier Quality replacements.
- Leaves `Install Missing` and `Replace Bad Parts` outside the new controls;
  those focused restoration actions remain available even when normal upgrades
  are disabled.
- Gives policy-blocked slots and retained retries explicit explanations that
  identify which upgrade path is disabled.

### 0.5.0

- Adds a `Native Surgery & Assembly Priority` control to CARAU's Settings tab.
  It reads and writes Stardeus' existing save-specific Body Part Procedure
  Priority (1-5), so new CARAU and manual procedures use the same native value.
- Preserves Core ownership of scheduling: the patient task inherits the native
  priority, and the accepting Surgery Hub or Assembly Hub carries it into its
  operator and replacement-hauling work.
- Does not add a duplicate CARAU preference, patch Core goal creation, rewrite
  active tasks, interrupt work, or reorder already-registered operations.
- Runtime-validated that changing the CARAU control updates Stardeus' Task
  Priority setting across multiple values.

### 0.4.15

- Adds focused `Install Missing` and `Replace Bad Parts` actions for the
  selected Being, plus `Queue All Missing` and `Queue All Bad Parts` batch
  actions. Each action appears or enables only when its live eligibility and
  inventory requirements are satisfied.
- Makes retained surgery results phase-aware and explicit about what happened
  to the previously installed part, incoming replacement, and final slot.
  Duplicate native log entries remain visible rather than being merged when
  they may represent distinct extraction and installation outcomes.
- Treats a successfully installed, usable but damaged replacement as a
  completed operation because normal Repair Stations can restore it. A
  replacement installed at 0% Condition remains an explicit `Unusable`
  failure with fresh manual-retry evaluation.
- Detects native `Completed` operations whose exact planned replacement result
  was not produced. These retain a distinct `Failed - Native Mismatch` badge
  and report the planned object, observed slot result, surviving inventory,
  and the limits of the evidence exposed by Stardeus.
- Refines reservation diagnostics to identify the exact object and whether its
  native marker lacks a current surgery or task owner. CARAU repairs only the
  narrow inactive-goal ownership transition proven to belong to its still-
  registered pre-surgery operation; active and foreign work remains untouched.
- Recovers a stale failed Hub body-part delivery goal only when it blocks the
  exact CARAU operation whose patient is already in that Hub and no surgery has
  begun. Core performs normal cancellation, ejection, and retry cleanup.
- Treats external pre-surgery AI interruptions and Mental Breakdowns as
  transient when the configured policy permits. Retries are deferred until the
  Being is targetable and every slot, inventory, reservation, and estimate
  check passes again.
- Adds an optional Mental Breakdown prevention setting that may queue a normal
  Mood Regulator implant before retrying interrupted work. It uses only an
  empty compatible Implant slot and an available native-compatible part.
- Adds a tightly guarded automatic retry for Stardeus's failed `Move to
  AutoDoc` emergency transfer when that exact native goal blocks a still-
  pending CARAU surgery. It uses Stardeus's normal Retry action, shares CARAU's
  configured automatic-attempt limit, backs off between attempts, and never
  touches active, unrelated, or already-started surgery work.
- Cancels remaining CARAU operations after a tracked Being dies, including a
  destructive Core outcome, so Vanilla can release the body and the player can
  decide recovery or disposal without CARAU operating on a corpse.

### 0.4.14

- Retires CARAU's obsolete destructive welded-Core recovery route now that
  Core supports the intended Vanilla disassembly behavior. A welded Core uses
  the same standard informational `Not Removable` card as any other welded
  installed part; CARAU does not attempt post-death Core recovery.
- Displays every installed welded slot once, without requiring a welded Core
  or otherwise upgradeable slot. These cards expose the installed part's
  Quality, Condition, and current state while remaining excluded from Queue,
  Queue All, and Batch Queue.
- Revalidates the exact replacement object and reservation for CARAU-owned
  native operations that are still `Pending` or `Preparing`. Proven-invalid
  pre-surgery work is removed through Core's normal cancellation path and
  handed to CARAU's existing retained-failure and retry policies. `Ongoing`
  surgery and non-CARAU operations remain outside this guard.
- Stops rebuilding an invisible batch plan while the main CARAU window is
  closed. Large Core selections retain their ordered selection state without
  scanning body-part inventory until CARAU is opened.
- Builds large batch plans incrementally across frames under a 4 ms work
  budget, shows honest `Analyzing x / y` progress, blocks queue actions until
  the current generation completes, and discards stale generations after
  selection, order, setting, window, save, or game-boundary changes.
- Builds one definition-indexed and pre-sorted body-part inventory snapshot
  for each planning or Batch Queue scope instead of repeatedly traversing and
  sorting Core's persistent-object registry.
- Revalidates every planned operation immediately before queueing, reserves
  the exact replacement before Core registration, bulk-updates CARAU tracking,
  and performs one deferred Tracked Operations widget refresh for the batch.
  The validated 4,051-operation test improved from 7,334.618 ms to 515.882 ms
  with 4,051 exact replacements and no substitutions, skips, or failures.
- Removes the temporary fine-grained optimization profilers after validation.
  The player-facing progress counter remains, and lightweight warnings are
  retained only when a planning frame, complete plan, or Batch Queue exceeds
  its production performance guardrail.
- Preserves quality and condition ordering, hostile-mind exclusion, all live
  eligibility and reservation checks, selected-Being order, retry boundaries,
  and one-inventory-part-per-planned-operation allocation.

### 0.4.12

- Moves the main and Tracked Operations windows onto AUMF's managed
  `FrameworkPanel` lifecycle while preserving CARAU's independent widgets,
  selections, settings, batch plans, operation state, and scroll positions.
- Corrects Main-tab preferred-height propagation so responsive slot-card rows
  extend the outer scroll boundary and every card remains reachable at narrow
  or wide panel sizes.
- Keeps the Debug header fixed at its intended height while retaining access
  to all category controls and diagnostic entries through the managed panel
  scroll.
- Runtime-validates bottom-edge scroll attachment, repeated resize and pooled
  popup cleanup, complete slot-card access, Debug-log access, and a fresh log
  without CARAU/AUMF scale, layout, lifecycle, or reservation errors.

### 0.4.11

- Adds save-specific Manual, Transient, and Transient + Surgery retry policies;
  Manual remains the safe default and all manual queue/retry actions remain
  outside automatic limits.
- Adds one shared automatic-requeue maximum from 0 to 10, default 3, plus a
  strict operator-neutral surgery-estimate threshold from 0% to 100%, default
  greater than 90%. Swaps conservatively use the lower current removal or
  installation estimate.
- Presents automatic waits as standard Tracked Operations cards with distinct
  active, waiting, and failed counts, progressive game-time delays, pause-safe
  execution, save/load persistence, and a Stop Retry action.
- Fresh-plans every automatic requeue against current inventory, reservations,
  slot policy, Quality threshold, ownership, removability, welded/parent/body
  state, native operation ownership, and queueability. Death, destroyed slots,
  welded parts, stale ownership, external cancellation, and unknown outcomes
  never retry automatically.
- Makes Core replacement tracking phase-aware so the outgoing identity's death
  or Core destruction does not preempt a successful incoming-Core installation;
  reconciles retained transition records against the stable installed result.
- Treats destructive incoming-Core or stable empty/destroyed mind-holding-slot
  outcomes as terminal. CARAU offers no automatic or manual post-death Core
  retry and leaves recovery or disposal to the player and Vanilla.
- When a welded Core is present, exposes every installed welded slot as one
  standard, non-queueable Main-tab card, including slots normally hidden by
  upgrade-oriented filters, while keeping destructive recovery Core-only.
- Adds a permanent main-interface top-bar widget alongside the conditional
  Tracked Operations widget. Both independently open or close their respective
  windows, and the redundant Stardeus Overlays-menu entry is removed.
- Corrects pooled popup resize ownership and transform preservation so repeated
  open/close/resize cycles do not leave stale drag handlers or reciprocal UI
  scale corrections in later pooled controls.
- Reserves every selected replacement through Core before surgical operation
  registration, rolls back CARAU's exact reservation on rejection, and leaves
  successful cancellation/completion cleanup to Core's native lifecycle.

### 0.4.10

- Adds a separate, manual C4-style recovery action for a player-owned
  mechanical Being whose mind-holding Core is welded in place.
- Keeps welded-Core destruction outside upgrade planning, Queue All, Batch
  Queue, retry, and every automatic CARAU path.
- Re-resolves and revalidates the current Being, mind-holding slot, Core,
  Vanilla surgery state, and CARAU tracking state after confirmation.
- Uses Stardeus' native swap-removal path, irreversibly destroys the extracted
  Core and emptied Core slot, preserves the original mind's Core linkage, and
  recalculates the body's condition without deleting the extracted Core object.
- Confirms that ordinary welded non-Core parts are not eligible for the action
  and deliberately retains confirmed access for a welded Core regardless of
  its current usability state.
- Runtime-validates cancellation, confirmed destruction, mind preservation,
  destroyed-state persistence across save/load, clean diagnostic output, and
  completion of the resulting body's Vanilla Disassemble route.
- Declares and validates against AUMF `1.3.0` while continuing to use its
  centralized guarded shortcut persistence, capture, conflict handling,
  text-entry suppression, once-per-press dispatch, and lifecycle cleanup while
  preserving the existing `carau.ui.shortcut` key and Settings controls.

### 0.4.9

- Uses AUMF's centralized guarded-shortcut API while preserving CARAU's
  existing global shortcut preference and Settings controls.
- Removes CARAU's redundant local input polling, conflict detection, capture,
  persistence, and shortcut-action lifecycle implementation.
- Stores Quality threshold, enabled slot types, and disabled-slot visibility
  independently in each save with a versioned settings schema.
- Resets in-memory settings at every game boundary before applying that save's
  CARAU data, preventing settings from leaking into older saves or new games.
- Preserves the global CARAU keyboard shortcut across saves while keeping
  gameplay policy settings save-specific.
- Restores safe defaults when a save has no CARAU settings and rejects newer,
  unsupported settings schemas without carrying stale state forward.
- Defers and coalesces main-panel content reconstruction so input, slider, tab,
  and button callbacks cannot invalidate the UI generation handling the event.
- Ties rebuilt native controls to their content-generation lifetime, invalidates
  stale callbacks, removes retired listeners, and safely returns pooled widgets
  before destroying CARAU-owned custom roots.
- Eliminates the quality-slider null references, destroyed pooled-widget
  warnings, pool fallbacks, and listener accumulation found during validation.

### 0.4.8

- Captures native Stardeus surgical failure outcomes directly, including
  operatorless automated surgeries that do not produce a Being-log entry.
- Validates the projected replacement in the resulting slot before treating
  supplemental Stardeus log or task text as a failure.
- Preserves specific failure reasons for destroyed, wounded, welded, rejected,
  fatal, and destroyed-slot surgical outcomes.
- Improves Core-replacement identity transition handling so a successful mind
  swap is not treated as the death of the body.
- Provides individual, per-Being, and session-wide bounded manual retries with
  fresh slot, inventory, compatibility, and reservation planning.
- Defers recognized temporary Assembly Hub saturation and permits up to three
  silent retries before retaining a manually retryable soft failure.
- Prevents or cancels CARAU work while a Being is untargetable during a Mental
  Breakdown and retains an explanatory failure card.
- Resets in-memory tracking at every game boundary before applying save data,
  preventing state from leaking into older saves or new games.
- Prefixes every Debug entry with its stored Stardeus game day and time in
  `day|HH:mm` format, including copied diagnostic logs.

### 0.4.7

- Validated CARAU against AUMF feature level `1.1.0`.
- Declares CARAU's compiled release version and tested AUMF feature level
  through the four-argument consumer compatibility handshake.
- Preserves the existing AUMF-owned widget, panel, responsive layout,
  scrolling, tooltip, and input behavior.
- Documents CARAU's existing bounded manual retry controls as a current
  Tracked Operations feature rather than future work.

### 0.4.6

- Migrated shared UI infrastructure to AUMF `1.0.0`.
- Uses cooperative tracked-operation widget lifecycle and placement.
- Uses AUMF dynamic widget icons, tooltips, click callbacks, tab controls,
  free resizing, responsive layouts, vertical scrolling, scroll restoration,
  and input/slider commit handling.
- Removed redundant local copies of the five transferred UI helpers.

## Version and release checklist

When updating a mod version, synchronize every applicable version source:

- `ModInfo.json` -> `ModVersion`
- `.csproj` -> `VersionPrefix`
- `AssemblyInfo.cs` -> `AssemblyVersion`, `AssemblyFileVersion`, and
  `AssemblyInformationalVersion`
- `README.md` -> requirements and release history
- Source compatibility constant -> required AUMF version

If the project contains `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>`,
the project version does not generate DLL metadata. Update the explicit
attributes in `AssemblyInfo.cs` manually.

After merging or refreshing framework changes, compare documentation and
manifest descriptions against the previous release so newer functionality is
not overwritten by older text.

Finish every version update by:

1. Rebuilding the project.
2. Inspecting the compiled DLL's assembly version.
3. Inspecting the compiled DLL's file version.
4. Inspecting the compiled DLL's product/informational version.
5. Confirming the live DLL matches the verified build.
