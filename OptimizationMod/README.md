# Optimization Mod

Optimization Mod applies narrowly scoped, fail-open Harmony patches to reduce
Core's worst global-registry searches in very large, long-running Stardeus
saves. Version 0.1.34 updates scheduler diagnostics for Stardeus
0.15.0-rc332.3211 and retains the useful
Nanobot dirty-goal index, indexed Trap Floor dispatcher, equipment and storage
lookups, and shared active-object hauling cache. It also corrects Core's
misleading Nanobot availability tooltip without changing task behavior. All
investigative timing probes remain available behind explicit startup switches
that default to off.

## Version history

The mod was developed in alternating optimization and diagnostic releases.
Diagnostic releases measure suspected bottlenecks without intentionally
changing gameplay results. Focused probes may remain available for later
investigations, while expensive broad profiling is disabled during ordinary
play unless explicitly enabled before startup.

### 0.1.34 - rc332 indexed scheduler diagnostics repair

- Replaced the obsolete queue-dequeue probe and removed the reference to
  `AIAuction.AllBids`. Current Core uses `AIAuctionIndex`, budgeted candidate
  inspections, and bid rounds rather than the old colony-goal queue.
- Observes actual index insertions, indexed goals at auction start, candidate
  inspections, bid attempts, successful bid preparation, winners, assignments,
  and state changes. The existing per-priority snapshots remain bounded to
  once every 120 AI ticks. Counters are aggregated until unload.
- Removed old queue-capacity, queue-leftover, dequeue-mismatch, and apparent
  priority-inversion statistics, which no longer describe the native scheduler.
  A candidate can be inspected for several workers; a successful `TryBid`
  still awaits region-path measurement and final resolution. Workers left in
  the auction list can receive personal work and are not reported as idle.
- Targets all seven scheduler boundaries by complete signatures. The group
  preflights its targets and uses its own Harmony owner so a failed install
  rolls back the entire group without removing active optimization patches.
  Exception cleanup closes the observation window without suppressing errors.
- Repaired the adjacent AI phase profiler's `ProcessColonyGoal` target for
  Core's current `(AIGoal, long, bool)` signature and made its `AIAuction.Run`
  timing target use the full current signature.
- This maintenance update changes diagnostic observation only. No save data,
  eligibility, task ordering, assignments, reservations, or optimization
  algorithms were changed. The master diagnostic toggle remains disabled.
- Release build passed with zero warnings/errors. An isolated test using the
  game's bundled Mono 6.13 runtime passed 40 assertions, including all seven
  real scheduler hook targets, counters, passive state observation, exception
  preservation on a throwing fixture, cleanup, missing-target rejection, and
  rollback after a deliberately invalid third hook. The separate AI phase
  profiler installed all ten targets. Unity logging is unavailable in that
  headless process; expected error-path tests can emit missing logging-call
  messages. No game process or save was used by these tests.
- Metadata checks resolved 153 external Core/Harmony/KL member references and
  found identical method instructions in all 111 methods across the 22 active
  optimization types compared with 0.1.33. These checks establish binary and
  hook compatibility for the inspected paths, not a new in-game capture.
- Rollback source, metadata, translations, settings, and DLL are preserved in
  `Mods/.release-backups/OptimizationMod/0.1.33-before-rc332-diagnostics-20261015`.

### 0.1.33 - neutral Nanobot task-status tooltip

- Corrected Core's misleading `Unreachable by Nanobots` tooltip for a
  Nanobot-compatible task whose `NanobotWorkable` flag is currently false.
  The neutral status now reads `Not currently available to Nanobots.` because
  the flag can also be cleared by unavailable or reserved materials and other
  eligibility gates before physical reachability is evaluated.
- Changed presentation only. The patch does not set `NanobotWorkable`, rerun
  material or refill checks, inspect building history, scan the world, or
  alter priorities, reservations, retries, assignments, or Hive behavior.
- Preserved Core's existing research-unlocked, Nanobot-compatible, and
  job-enabled guards, as well as the positive `Reachable by Nanobots` status.
- The guarded transpiler requires exactly one load of Core's reviewed
  `NanobotUnreachable` text inside `FreeSelectGoalHandler.LoadGoalTooltip`. If
  a future Core update changes that shape, Optimization Mod retains vanilla
  wording and reports the skipped correction instead of patching broadly.
- The master diagnostic switch remains disabled for ordinary play; retained
  profiler selections remain inert while that master switch is off.

### 0.1.32 - focused StoreObj and RunAgent diagnostics

- Added an observation-only breakdown of `AIActionStoreObj.Execute` into busy
  movement, target reservation, reach/pickup, storage search, and delivery.
  The nested storage search is correlated with candidate-list size, grounded
  state, and whether a destination was found.
- Added focused timings for the work inside `AISys.RunAgent` that is outside an
  action's `Execute`: current-action selection/activation, work and idle
  counters, goal clearing/deactivation, and goal completion checks.
- Added phase classification for `AIActionReinstall.Execute` so relocation
  spikes can be attributed to reachability, source approach, pickup, target
  approach, or placement instead of being reported as one opaque total.
- AI action summaries now list other Harmony owners attached to each reported
  `Execute` method. Optimization Mod excludes its own owner from this list so
  Core execution and third-party patch participation are easier to distinguish.
- Recorded the completed 0.1.31 capture: `StoreObj` accounted for about 60% of
  measured action execution, while the largest `Reinstall` spikes aligned with
  Area/Region reconstruction. The corrected Area hook armed successfully.
- This release changes diagnostics only. Action decisions, storage selection,
  goal state, ordering, results, and exceptions remain owned by Core and other
  gameplay mods. The focused Core suite remains enabled for the requested
  capture; broad whole-frame and external Nanobot profiling remain disabled.

### 0.1.31 - rc327 AI action diagnostics and Area hook repair

- Added a diagnostics-only profiler for every concrete `AIAction.Execute`
  implementation available from Core and currently loaded mods. It aggregates
  the complete action execution boundary by action identity, implementation
  type and assembly, goal type, and job, then reports the highest total costs
  and individual spikes on shutdown. Harmony work attached to an action is
  deliberately included in that boundary.
- This pass separates time spent inside actions such as movement, gathering,
  construction, surgery, and mod-provided work from the remaining
  `AISys.RunAgent` wrapper. Action results, exceptions, ordering, goals, agents,
  and save data are observed but never changed.
- The profiler deliberately patches many action implementations and records
  every execution. It is intended only for targeted captures; its overhead is
  expected and it remains behind `DiagnosticsEnabled` plus
  `CoreProfilerSuite`.
- Repaired the retained `AreaSys.FullRecalculate` diagnostic hook for rc327's
  current Boolean parameter. The Area/Region suite can once again arm all eight
  expected boundaries; the repair affects diagnostics only.
- Left the focused Core diagnostic suite enabled for the requested follow-up
  capture. The broad whole-frame and external Nanobot profilers remain off.

### 0.1.30 - rc327 AI phase diagnostics

- Added a behavior-neutral breakdown of Core's complete `AISys.OnTick`
  boundary after the previous broad capture identified it as the largest
  sustained named Core subsystem.
- Measures auction setup, agent preparation and personal-goal evaluation,
  colony-goal processing, auction execution, top-level winner assignment,
  unreachable-task resolution, agent execution, pending-goal cleanup, and
  unreachable-task reconciliation.
- Reports the uninstrumented remainder separately instead of assigning it by
  guesswork. That residual includes priority-bucket construction, loop and UI
  bookkeeping, winner gate rechecks, exception guards, and Harmony work that
  occurs outside the timed private-method boundaries.
- Records total, per-AI-tick, per-call, maximum, and threshold counts. The
  probes preserve exceptions and return values and do not alter scheduler
  ordering, eligibility, reachability, assignments, task state, or save data.
- The phase probes are installed only with `DiagnosticsEnabled` and
  `CoreProfilerSuite`. They remain available for future investigations but
  should be disabled for ordinary play after a capture.
- Enabled the Core diagnostic suite for the next requested test while leaving
  the more intrusive whole-frame and external Nanobot profilers disabled.

### 0.1.29 - rc316 Core maintenance

- Audited every active optimization against Stardeus 0.15.0-rc316.3193 Core
  source and the installed runtime assemblies.
- Retired the separate AI colony-goal priority buckets. Core now builds its
  native priority lists in one dictionary pass per AI tick, so the old
  transpiler was redundant and no longer matched its target. The goal-add hook
  required by the separate Nanobot dirty index remains installed on its own.
- Updated loose-object hauling to build one shared haulable snapshot per tick
  from Core's smaller `ActiveObjs` collection. Each caller still revalidates
  reservation, goal, age, and hauling state so an object claimed earlier in
  the tick is not returned to a later caller.
- Changed best-equipment lookup to scan only the indexed slot candidates in
  Core insertion order and delegate every comparison to Core's current
  `BetterItem`. This preserves rc316's origin and wearer-mood tie-breaking and
  automatically inherits future Core comparison rules.
- Retained the compatible-equipment and equipment-UI indexes, stored-object
  filter-before-sort path, Nanobot dirty-goal index, and Trap Floor dispatcher;
  Core still performs the corresponding global scans or per-trap fan-out.
- Kept all diagnostic switches set to `false`. No save data or serialized mod
  state was added or changed.

### 0.1.28 - rc284 external Nanobot attribution diagnostics

- Added an optional, behavior-neutral profiler for the Nanobot extensions in
  Quantum Storage and Part Restoration Unit. The targets are found by runtime
  type name, so Optimization Mod gains no compile-time dependency on either
  mod and safely skips an extension when it is not installed.
- Times eight boundaries per extension: total `OnNanobotTick`, active-operation
  validation and maintenance, tracked-goal refresh, best-candidate search,
  candidate construction, operation start, and per-frame transit progress.
- Records aggregate working-Hive, tracked-goal, and active-operation counts.
  The unload summary also reports calls per outer tick and an approximate
  uninstrumented residual. Nested timings are explicitly identified as
  non-additive.
- Added `CoreProfilerSuite` and `ExternalNanobotProfiler` sub-switches. This
  permits the new attribution pass to run without reinstalling the retained
  broad Core, scheduler, map, and panning probes. Older diagnostic files that
  omit `CoreProfilerSuite` preserve the prior behavior when diagnostics are
  enabled.
- The preceding broad capture measured the complete Nanobot listener at about
  48.5 ms per active tick while Core's Nanobot method body averaged about
  0.8 ms. This release narrows that surrounding Harmony-patch time to the two
  known external extensions and their internal phases.
- The completed targeted capture attributed the sustained cost to Quantum
  Storage: roughly 4,640 tracked object-hauling goals were compared with about
  133 working Hives, causing 376.7 million `BuildCandidate` calls across 611
  Nanobot ticks (about 616,591 calls per tick). Its instrumented outer average
  was 144.9 ms; the per-candidate probe itself inflated that absolute number,
  while the earlier 48.5 ms outer measurement remains the lower-overhead
  baseline. Part Restoration Unit averaged only 0.223 ms per tick with about
  11 tracked goals and is not a meaningful contributor.
- A follow-up 239.8-second broad capture after Quantum Storage's optimization
  covered 1,875 simulation ticks on the same stress save. Core's complete
  Nanobot listener fell to a 0.994 ms average, with only 8 calls at or above
  4 ms. This confirms that the former sustained Nanobot bottleneck was removed.
- The in-game FPS display remained above 50 during the follow-up capture even
  with the deliberately expensive broad profiler active. Remaining long frames
  were intermittent rather than a new sustained Core bottleneck.
- The strongest recurring residuals belong to other mods: Intelligent
  Recycling averaged 11.875 ms across 1,875 ticks and exceeded 8 ms 1,547
  times; Auto Factory averaged 4.874 ms across 1,876 ticks and exceeded 4 ms
  970 times. These are documented for their owning mods and are not altered by
  Optimization Mod.
- Distinct event spikes were also separated from the steady workload: eight
  Aggressive Auto Connect Upgrade incidents reached 154.501 ms, seven Auto
  Hauling ticks reached at least 16 ms with a 72.602 ms maximum, and Core's 163
  Region rebuilds averaged 12.307 ms with one 347.342 ms outlier. The known
  reconnect incidents and genuine Region rebuilds do not justify a broader
  behavior-changing Core patch here.
- A later rc314 stress capture used the retained whole-frame profiler to
  identify a separate sustained 8 FPS regression. Planning Overhaul's `OnTick`
  consumed 60.582 seconds of exclusive main-thread time across 644 calls in a
  92.2-second sample: 94.071 ms per call on average, 226.340 ms maximum, and
  every call exceeded 33 ms. Its private phase log independently attributed
  the repeated exterior pass primarily to equipment-stage selection (61.2 ms
  average) and equipment-state rebuilding (19.1 ms average) over a 71,786-entry
  plan. Optimization Mod does not patch that workload; the finding belongs to
  Planning Overhaul's own optimization path.
- All diagnostic switches were returned to `false` after each attribution run;
  the probes remain compiled in for future investigations.
- No Core, Quantum Storage, Part Restoration Unit, save data, Nanobot task
  eligibility, reservation, delivery, or timing behavior was changed.

### 0.1.27 - rc283 equipment-policy compatibility audit

- Audited every behavior-changing optimization against the installed rc283
  Core source and compiled `Game.dll`. Core still performs the global object,
  equipment, colony-goal, Nanobot-goal, and Trap Floor listener work that the
  retained indexes reduce; none of those optimizations became redundant.
- Updated the best-equipment and compatible-equipment AI paths for rc283's
  wearer-aware `EquipmentPolicyFilter.For(policy, wearer)` contract. This
  preserves Core's newer second-hand, dead-owner, and wearer-specific refusal
  rules while retaining the lifecycle-maintained equipment index.
- Kept the manual equipment UI deliberately policy-unfiltered, matching Core's
  player-override behavior.
- Confirmed that Core's newer Nanobot rejection ordering, fire blocking,
  area-based failed-haul recovery, and reachability reductions remain inside
  Core's original loop after the dirty-goal view is substituted.
- Revalidated the AI priority-bucket, Nanobot dirty-goal, and Trap Floor
  dispatcher transpiler/lifecycle contracts against rc283. No save data or
  gameplay timing rules were changed.

### 0.1.26 - Indexed Trap Floor dispatch and global diagnostics switch

- Promoted the validated Trap Floor shadow index into an active positional
  dispatcher. One `BeingPosChanged` listener now looks up only Trap Floors at
  the destination tile instead of invoking every Trap Floor for every being
  movement.
- The dispatcher invokes Core's original private `OnBeingPosChange` handler for
  every destination candidate. Core therefore remains authoritative for trap
  readiness, trigger targeting, grounded/carried/incapacitated checks, damage,
  power, charging, graphics, sound, and save state.
- Initial takeover occurs only after Core finishes registering loaded Trap
  Floors. Construction, removal, and position-update lifecycle hooks maintain
  the session-only index afterward.
- A bounded audit every 600 simulation ticks verifies the one central listener,
  absence of redundant per-trap listeners, Core subscription state, and indexed
  positions. Any discrepancy disables indexed dispatch and restores Core's
  exact bound per-trap delegates.
- Listener restoration preserves the original per-trap registration order.
  Other `BeingPosChanged` subscribers, including mod-provided listeners, are
  never removed or replaced.
- Added `DiagnosticsEnabled` as the global diagnostics switch. It defaults to
  `false`; while false, the stress, Nanobot phase, dirty-index timing, frame,
  scheduler, Area/Region, passability, and whole-frame probes are not installed
  or timed and no normal diagnostic summaries are logged.
- The Trap Floor consistency audit remains active with diagnostics disabled
  because it is the dispatcher's gameplay fail-safe. Its counters and normal
  summary output remain off; only a genuine failure is logged.

### 0.1.25 - Reusable profiler switch and Trap Floor shadow index

- Retained the 0.1.24 hierarchical whole-frame profiler as reusable debugging
  infrastructure instead of removing it. `Diagnostics.json` now controls its
  startup state through `WholeFrameProfiler`, which defaults to `false`.
- When the broad profiler is disabled, its Harmony callback and Unity lifecycle
  probes are not installed. This avoids leaving a disabled-condition check on
  every profiled call. Changing the setting requires a complete Stardeus
  restart.
- Added a behavior-neutral Trap Floor positional shadow index. Core retains all
  original `BeingPosChanged` listeners and remains solely responsible for trap
  eligibility, triggering, damage, charging, graphics, sound, and save state.
- The shadow index records movement-signal count, current and maximum registered
  Trap Floors, the callback fan-out implied by Core's global listener design,
  destination-tile candidate counts, and the number of callbacks a positional
  dispatcher could theoretically avoid.
- Tracks Trap Floor readiness, removal, and position-update lifecycle methods.
  An initial listener seed and periodic listener-list audits compare the shadow
  registry with Core's actual Trap Floor subscriptions. Missing, extra,
  relocated, duplicated, or invalid entries are counted and reconciled only in
  the shadow registry; Core's listener list is never changed.
- Adds one observation-only listener to `BeingPosChanged` and one periodic
  aggregate audit every 600 simulation ticks. Nothing is logged per movement,
  Trap Floor, or audit while the simulation is running.
- No save data, task ordering, movement, Trap Floor behavior, signal order, or
  Core listener is changed by this diagnostic release.

## Diagnostic startup setting

`Diagnostics.json` is stored in the Optimization Mod root:

```json
{
  "DiagnosticsEnabled": false,
  "CoreProfilerSuite": false,
  "WholeFrameProfiler": false,
  "ExternalNanobotProfiler": false
}
```

Leave `DiagnosticsEnabled` set to `false` for ordinary play. Set it to `true`
and completely restart Stardeus when timing probes are needed.
`CoreProfilerSuite`, `WholeFrameProfiler`, and `ExternalNanobotProfiler` are
second-level switches and are considered only when `DiagnosticsEnabled` is
also `true`. `CoreProfilerSuite` controls the retained Core, scheduler, AI
phase/action, map, Nanobot-phase, panning, and index diagnostics.
`WholeFrameProfiler` remains the most intrusive option.
`ExternalNanobotProfiler` installs only the targeted Quantum Storage and Part
Restoration Unit attribution probes. A missing, invalid, or unreadable
configuration fails safely to all diagnostics disabled.

The completed 0.1.28 external-attribution capture used this temporary profile:

```json
{
  "DiagnosticsEnabled": true,
  "CoreProfilerSuite": false,
  "WholeFrameProfiler": false,
  "ExternalNanobotProfiler": true
}
```

The completed follow-up post-Quantum-Storage optimization scan used the
retained Core and whole-frame profilers while leaving the extremely intrusive
per-candidate external Nanobot profiler disabled:

```json
{
  "DiagnosticsEnabled": true,
  "CoreProfilerSuite": true,
  "WholeFrameProfiler": true,
  "ExternalNanobotProfiler": false
}
```

### 0.1.0 - Initial Core query optimizations

- Introduced five fail-open Harmony query patches.
- Replaced the global persistent-object scan used for loose-object hauling with
  Core's smaller `ActiveObjs` collection while retaining vanilla eligibility
  and storage checks.
- Added a per-simulation-tick equipment index for best-equipment, compatible-
  equipment, and equipment-UI lookups. Dynamic reservations, goals,
  compatibility, blacklists, and `BetterItem` checks remained query-time
  decisions.
- Changed stored-object UI queries to filter by storage and search text before
  sorting only the matching contents.
- Added session-only comparison counters for development validation. No save
  data was introduced.

### 0.1.1 - Lifecycle-maintained equipment index and Core profiling

- Changed the equipment index from a per-tick rebuild to a session-only index
  maintained by Core's persistent-object add and deactivate lifecycle methods.
- Added count and last-object-ID consistency checks as a fail-safe rebuild path.
- Added two lifecycle hooks, raising the permanent optimization patch count
  from five to seven.
- Added 13 temporary high-level Core timing probes, grouped by game speed, to
  identify simulation systems associated with the recurring hitch.

### 0.1.2 - Expanded AI and query diagnostics

- Retained the same seven optimization patches and gameplay behavior.
- Expanded temporary profiling from 13 to 19 Core probes.
- Split AI timing into personal-goal, colony-goal, auction, agent-execution,
  and cleanup phases, and added aggregate timings for the optimized query
  families.
- Logged only aggregate counts, averages, maxima, and spike counts when the
  game state unloaded; no per-call logging or persistent data was added.

### 0.1.3 - Ranked best-equipment lookup

- Added best-first equipment candidate ordering using Core's own
  `market value x quality` ranking rule.
- Preserved marketless-item and equal-score ordering behavior, then applied all
  dynamic vanilla eligibility checks from the best candidate downward.
- Added temporary Core-equivalent shadow scans. Any winner mismatch would
  disable ranked lookup for the rest of the session and use the linear result.
- Retained the 19 Core/AI timing probes while the ranked path was validated.

### 0.1.4 - Clean validated optimization build

- Confirmed the ranked result matched Core's full linear result in all 48
  runtime shadow comparisons.
- Removed the temporary Core timers, per-query timers, filter counters, session
  summaries, and full-registry shadow scans. The periodic shadow scans had
  themselves caused a small diagnostic hitch.
- Retained the seven validated optimization patches with initialization and
  fail-open error logging only.

### 0.1.5 - Large-selection diagnostic build

- Temporarily added 12 phase timers and seven aggregate detail probes for
  selections of at least 128 beings.
- Measured selection, selection VFX, multi-being UI, entity messages, and
  being-name update paths without changing selection results.
- Profiling showed that Core's selection work completed in only a few
  milliseconds. The remaining multi-second delay was isolated to CARAU's
  synchronous batch-plan UI work and handed off to that mod for its own fix.
- Left all seven Core optimization patches unchanged.

### 0.1.6 - Clean build after selection diagnostics

- Removed all temporary 0.1.5 selection timers, aggregate probes, and related
  log traffic after the bottleneck was isolated.
- Retained the same seven Core optimization patches without changing their
  behavior, save semantics, or compatibility boundaries.
- Restored the normal clean-build initialization and fail-open logging.

### 0.1.7 - rc267 compatibility and post-combat diagnostics

- Updated the indexed best-equipment and compatible-equipment AI paths to
  apply Core's new rc267 equipment-policy filters. The manual equipment UI
  remains deliberately policy-unfiltered, matching Core's player-override
  behavior.
- Added 28 temporary, high-level timers for complete simulation ticks and the
  AI, needs, conditions, beings, objects, inventory, crafting, electricity,
  atmosphere, fire, motion, pathfinding, combat, NPC-ship, and ship-weapon
  systems.
- Kept instrumentation outside individual agents, goals, objects, and tiles so
  the save's 15,000-plus colony goals are not burdened with per-item timing.
- Reports aggregate timing by game speed only when the game state unloads. The
  timers do not change gameplay or save data and will be removed after the
  bottleneck is isolated.

### 0.1.8 - Complete simulation-tick attribution

- The first stress run measured complete simulation ticks at `42.155 ms`
  average and Core AI at `11.148 ms` average across 2,091 ticks.
- AI auction bidding averaged only `1.487 ms`, locating most measured AI cost
  outside the auction. Core's complete path pipeline averaged `0.012 ms` and
  peaked at `1.86 ms`, ruling pathfinding out as the recurring main-thread
  stutter in that reproduction despite its noisy recovery messages.
- Expanded diagnostics from 28 to 70 whole-method probes to cover the
  remaining Clock phases and simulation subscribers, especially Nanobots,
  regions, ship systems, statistics, and tick-driven UI handlers.
- Retained the no-per-agent, no-per-goal, and no-per-tile instrumentation rule.
  Version 0.1.8 changes no gameplay behavior beyond the rc267 policy
  compatibility already present in 0.1.7.

### 0.1.9 - Indexed AI colony-goal priority passes

- The second stress run measured Core AI at `10.446 ms` average, Nanobots at
  `4.520 ms`, and full simulation ticks at `30.029 ms` across 971 ticks.
- Isolated a speed-specific region rebuild cost: `Regions.OnTick` averaged only
  `0.020 ms` at 1x but `7.018 ms` at 3x while the damaged map was repeatedly
  marked dirty. No region rebuilds are suppressed because doing so could expose
  AI and path queries to stale passability data.
- Confirmed from the stress save that 15,497 of 15,657 colony goals were
  Nanobot-compatible. A compatible-goal index alone would therefore remove
  almost none of Nanobots' work and was deliberately not added.
- Replaced AI's six complete colony-goal dictionary passes with six cached
  priority buckets. The buckets retain Core's dictionary order within each
  priority and pass each goal's original global dictionary index into Core's
  existing batching logic.
- Bucket invalidation follows Core goal additions, removals, pending-removal
  cleanup, and priority changes. A goal-count consistency check rebuilds the
  cache if an unobserved lifecycle change occurs.
- Retained all 70 high-level timing probes for a direct 0.1.8-to-0.1.9 runtime
  comparison. No save data, auction rules, goal states, or priority values are
  changed.

### 0.1.10 - Nanobot phase diagnostics

- The 0.1.9 stress run reached 15,639 colony goals and 407 AI agents. Core AI
  averaged `10.046 ms`, down from `11.148 ms` in the closest like-sized 0.1.7
  run. Because the sessions were not a controlled A/B pair, this is evidence
  that the priority buckets improved scaling rather than a promised percentage.
- Full simulation ticks still averaged `47.003 ms` because `Nanobots.OnTick`
  rose to `19.939 ms`. Its initial 1x segment averaged `50.106 ms` before
  declining to `14.198 ms` at 3x as the post-combat backlog was consumed.
- The stress save contains 119 Nanobot Hives. Core currently builds candidate
  work items every tick, checks them against every working hive in a range job,
  then resolves every raw in-range pair on the main thread.
- Added diagnostic boundaries around hive preparation, candidate construction,
  range setup/job completion, hit resolution, and final work dispatch. The
  summary also reports average and maximum hubs, working hives, work items,
  active hauling operations, and raw in-range hive/work-item pairs.
- The added probes run only at the five outer phase boundaries. There are no
  per-goal, per-work-item, or per-hive timers, and no Nanobot selection,
  reachability, hauling, repair, work-rate, or save behavior is changed.

### 0.1.11 - Safe Nanobot phase diagnostic correction

- The first 0.1.10 live run exposed a defect in the diagnostic-only raw range-
  hit counter. It loaded `TryDequeue`'s output local as though it were the
  `NativeQueue`, causing a `NullReferenceException` after candidate creation,
  skipping Nanobot work, and leaving temporary job allocations undisposed for
  that session. The diagnostic did not add or rewrite save data.
- Removed the raw-hit counter and all access to `NativeQueue.Count`. The five
  phase boundaries and safe field-backed workload counts remain, so candidate
  construction, range setup/job completion, hit resolution, and work dispatch
  can still be separated without touching job-container internals.
- The partial run validly measured the work completed before the exception:
  107.2 hubs, 105.0 working hives, 3,872.5 candidate work items, and a
  `30.311 ms` average candidate scan. Full-tick, completed-Nanobot, and later
  phase figures from that interrupted run are invalid and are not used as a
  performance baseline.

### 0.1.12 - Nanobot-compatible goal-cache experiment

- Added a fail-open view of colony goals carrying Core's
  `NanobotCompatible` flag and changed only `NanobotSys.OnTick` candidate
  enumeration to use that view. Core retained every availability, state,
  hauling, reachability, and work decision in the original dictionary order.
- The live stress pass was clean, but the cache filtered only 160 of 15,657
  goals: 15,497 goals (99.0%) were Nanobot-compatible. The player observed no
  in-game difference, so the experiment did not meet the bar for retention.

### 0.1.13 - Retract negligible Nanobot cache

- Removed the 0.1.12 compatible-goal view, its `NanobotSys.OnTick` transpiler,
  and its compatibility-change hook. This returns Nanobot candidate
  enumeration to the clean 0.1.11 implementation while retaining the validated
  AI priority buckets and all seven permanent query patches.
- Offline inspection of the exact tested save found 15,656 of 15,657 colony
  goals already in active Core states, so an active-goal index would skip only
  one goal. The bottleneck is instead the dynamic construction backlog: 12,053
  `Haul Materials` goals, including 11,718 unfinished floor blueprints in
  construction waves. Only 2,410 goals were saved as Nanobot-workable.
- No availability-gate cache was added. Floor-wave, support-floor, assignment,
  and reachability changes can unlock work dynamically; delaying those updates
  would be a gameplay tradeoff rather than a transparent query optimization.

### 0.1.14 - Whole-tick phase diagnostics

- The clean 0.1.13 run captured 1,088 ticks at 3x and 117 at 1x, so Core's
  danger slowdown did not prevent a useful 3x sample. The slowdown coincided
  with a new raid alert and Core's disaster clock restriction.
- At 3x, `Clock.FullSimulationTick` reached `437.837 ms`, while the largest
  named contributors peaked at `61.634 ms` for `Nanobots.OnTick`, `49.441 ms`
  for `Regions.OnTick`, and `30.770 ms` for `AI.OnTick`. Whole-method averages
  cannot identify which outer Clock dispatch phase owns the remaining spike.
- Added fail-open timing boundaries around `Clock.IncrementTick` preamble,
  deferred `ExecuteAfter` callbacks, `OnTickEarly`, `EveryTickOrFrame`, main
  `OnTick`, combined `OnTickLate`/tick binding, and `OnNextTick`. These are
  seven outer boundaries per simulation tick; there are no per-subscriber,
  per-goal, per-object, or per-hive probes.
- The diagnostic does not alter clock speed, event ordering, callbacks, signal
  subscribers, tick progress, or save data. If the exact Core anchors are not
  present, the transpiler returns the untouched method.

### 0.1.15 - Correlated hitch attribution

- The 0.1.14 pass captured 858 ticks at 3x before Core's raid-alert restriction
  slowed the game to 1x. The worst complete simulation tick was `403.686 ms`;
  the main `OnTick` dispatch accounted for `403.116 ms` of it. All other outer
  Clock phases were negligible, with maxima from `0.002 ms` to `2.500 ms`.
- At 3x, Nanobots remained the largest average named subscriber at `28.212 ms`,
  followed by AI at `10.376 ms` and regions at `4.235 ms`. Their independent
  aggregate maxima still cannot show which systems overlapped on the exact
  `403 ms` tick or whether an uninstrumented callback owned the remainder.
- Removed the seven-boundary Clock transpiler after it localized the hitch to
  main `OnTick`. The 70 existing whole-method probes now correlate their direct
  `OnTick` subscriber durations within each complete simulation tick.
- For ticks lasting at least `100 ms`, the diagnostic retains only the 12 worst
  records. Each unload record reports full-tick time, summed tracked-subscriber
  time, the residual, and the six largest measured subscribers from that same
  tick. Nothing is logged during simulation, so diagnostic log I/O cannot add
  a new hitch to the measured tick.
- A large residual points to an uninstrumented subscriber, event-dispatch
  overhead, or a runtime pause such as garbage collection. A small residual
  with one dominant named subscriber identifies the next Core research target.
  This remains diagnostic-only and does not change gameplay or save data.

### 0.1.16 - Nanobot dirty-goal index

- Replaced Nanobots' every-tick eligibility evaluation of every compatible
  colony goal with a session-only candidate index. Core still performs its own
  availability, state, disabled-job, hauling, reachability, and work checks for
  every goal returned by the index.
- Goals stay in the immediate working set while Core reports them reachable by
  a working Nanobot Hive. Goal additions, removals, state changes, compatibility
  changes, Nanobot job toggles, construction-wave changes, and matching material
  production or consumption dirty the relevant index entries.
- Rechecks 256 dormant compatible goals per simulation tick. This rotating
  safety pass recovers gates changed by Core or mods without a known event, and
  a cheap structural reconciliation every 60 Nanobot ticks catches direct goal-
  dictionary mutations that bypass Core lifecycle methods.
- Accepted gameplay tradeoff: a Core or mod-provided action whose availability
  changes without one of the observed events can wait for the rotating safety
  pass instead of becoming Nanobot-eligible on that exact tick. At 256 dormant
  goals per Nanobot tick, a 15,000-goal backlog takes at most about 59 Nanobot
  ticks for one complete dormant pass; real time varies with game speed and
  simulation load. Ordinary colony AI is unchanged, so workers may still claim
  the task before Nanobots rediscover it.
- Preserves Core colony-goal order in the candidate view. No task is completed,
  cancelled, reprioritized, reserved, or assigned by the index, and no state is
  serialized.
- Any index exception, missing Core field, or failed transpiler anchor leaves
  Nanobots on Core's complete colony-goal view for the remainder of the session.
  The 70 stress probes and Nanobot phase diagnostics remain for one direct
  runtime validation pass and will be cleaned up afterward.

### 0.1.17 - Bound material wakeups and goal churn

- Runtime evidence confirmed that 0.1.16 remained healthy and reduced the 3x
  Nanobot average from `25.072 ms` to `17.596 ms`, but this was not a controlled
  workload match: average work items also fell from about 2,482 to 1,625.
  Candidate scanning still averaged `8.684 ms` at 3x and spiked to `116.179 ms`,
  leaving plainly visible hitches.
- Found that material production and consumption caused 4,908,674 immediate
  task dirties over 1,186 Nanobot ticks. Those broad event fan-outs raised the
  candidate view to 5,784.6 goals on average even though Core produced only
  1,625 work items on average.
- Coalesces material-production signals by material type and wakes at most 64
  matching dormant goals per Nanobot tick. Material consumption no longer
  performs a broad immediate wake because consuming stock does not normally
  make waiting haul requests more actionable. The independent 256-goal rotating
  fallback remains authoritative for everything not woken immediately.
- Maintains normal Core goal additions and removals incrementally. The 0.1.16
  run performed 1,185 structural reconciliations across 1,186 ticks because the
  task count changed constantly. Version 0.1.17 reserves the complete structural
  pass for the 60-tick integrity check or an actual lifecycle mismatch.
- Preserves the same accepted delay and safety contract: Core validates every
  returned goal, stale inclusions grant no work authority, unknown availability
  changes remain bounded by the fallback pass, and an index failure restores
  Core's complete goal list for that session.

### 0.1.18 - Dirty-index and panning diagnostic pass

- Runtime validation of 0.1.17 showed that its dirty index was active from the
  first Nanobot tick and settled the post-load candidate backlog within several
  ticks. The player reported that stationary being motion became substantially
  smoother after that settling period, while panning still exposed visible
  hitches.
- Added aggregate timing around six whole dirty-index phases: candidate
  preparation, 60-tick structural reconciliation, material wake selection,
  rotating fallback selection, candidate-view rebuild, and post-tick result
  processing. The summary also reports how many goals each bounded scan and
  view rebuild inspected.
- Added 12 temporary whole-method probes around camera update, camera motion and
  notification, map change application, map layer rendering, renderer updates,
  and per-camera renderer preparation. No being, object, goal, tile, hive, or
  renderer item is instrumented individually.
- Measures both the main-thread interval from Core's earliest camera `Update`
  to the render-pipeline boundary and the complete wall-clock interval from one
  camera `Update` to the next. Both are separated into camera-moving and idle
  frames by game speed. Only the 12 worst frames of at least 33 ms in each
  series are retained, with bounded garbage-collection generation deltas to
  help distinguish runtime pauses, CPU work, and render/presentation delays.
- This release changes no additional gameplay rules, task timing, camera
  behavior, rendering result, or save data. All new information is written as
  aggregate unload summaries; nothing is logged per frame during play.

### 0.1.19 - Component-tick residual diagnostics

- The 0.1.18 stress run captured 982 simulation ticks and 1,250 camera-moving
  frames at 3x, so the combat slowdown did not invalidate the test. Camera
  movement, camera notification, map updates, and renderer preparation all
  remained well below the visible hitch scale. Paused panning also remained
  smooth.
- Confirmed that Core simulation runs in `Bootstrap` at Unity execution order
  -20000, before `CameraControls.Update` at -10000. The complete update-to-update
  interval therefore sees stalls created by the earlier simulation even when
  the camera and post-simulation render preparation are individually cheap.
- Cleared the dirty index as the source of the remaining hitch. At 3x, complete
  candidate preparation averaged `0.436 ms`, its periodic structural pass
  averaged `0.772 ms` and peaked at `0.983 ms`, and candidate-view rebuilding
  averaged `0.368 ms`.
- Core Nanobots remained the largest known recurring subscriber at `19.918 ms`
  average at 3x, divided almost evenly between candidate construction and main-
  thread hive/work-hit resolution. Two full ticks nevertheless retained
  `194.945-218.977 ms` outside all 70 named direct subscribers, with no observed
  garbage collection and negligible camera/render work.
- Added only two whole-method probes: `ComponentState.OnTick`, which dispatches
  every registered `ITickListener` and the 15-tick rare-component pass, and
  `GameState.OnTick`, which flushes pending entity removals. Both are direct
  Core tick subscribers previously included only in the residual.
- The probe count is now 72. There are still no per-component or per-entity
  timers, no per-frame log writes, and no gameplay or save-data changes.

### 0.1.20 - Routine simulation-hitch attribution

- The 0.1.19 Imperial regression run showed that the optimized save's 3x
  simulation ticks averaged `25.708 ms` and peaked at `50.431 ms`. Those ticks
  could still disrupt a 60 FPS frame budget, but none reached the former
  `100 ms` retention threshold, leaving the routine hitches unattributed.
- Lowered only the bounded full-tick attribution threshold from `100 ms` to
  `33 ms`. The diagnostic still retains at most the 12 worst ticks and the six
  largest named subscribers from each retained tick, and still writes nothing
  during active simulation.
- The threshold change does not add probes or alter gameplay, tick ordering,
  task eligibility, camera behavior, save data, or the dirty-index safety
  contract. It is intended for another higher-stress Celesterra pass.

### 0.1.21 - Colony-task scheduler flow diagnostics

- Core already visits colony goals from visible priority 5 down to 0, but its
  bounded 64-goal auction queue subsequently uses hidden goal importance,
  temporary new-task boosts, waiting age, worker eligibility, personal needs,
  and a 256-bid-attempt budget. That makes the player-visible priority only one
  part of the final assignment order.
- Added five aggregate, fail-open hooks around the existing AI tick, colony-
  goal queue admission, exact auction dequeue, successful assignment, and goal
  state transitions. The diagnostic counts queue-capacity ticks, work left in
  the queue after an auction, auctions producing no winner, and cases where a
  lower visible priority is dequeued while a higher one remains admitted.
- Takes one read-only colony-goal state snapshot every 120 AI ticks. Per
  priority, the unload summary reports latest/average waiting counts, oldest
  observed bidding age, admissions, dequeues, winners, assignments,
  completions, failures, interruptions, cancellations, and boosted admissions.
- Nothing is logged per task or during active simulation. The diagnostic does
  not reorder tasks, change bid scores or budgets, interrupt workers, mutate
  goals, or alter eligibility, reachability, reservations, save data, or Core's
  final assignment authority.

### 0.1.22 - Area and navigational-region rebuild diagnostics

- The 24-minute 0.1.21 Celesterra run confirmed that visible scheduler
  priorities were honored in 99.543% of auction dequeues and that the auction
  averaged `2.439 ms`. It was a steady simulation cost, not the primary source
  of the recurring hitch.
- `Regions.OnTick` exceeded `8 ms` on 1,656 of 17,001 ticks, closely matching
  the 1,691 complete simulation ticks that exceeded `33 ms`. Eleven of the
  twelve worst recurring full-tick spikes included a roughly `11-12 ms`
  Region rebuild.
- Added eight aggregate, fail-open hooks around Core's pending Area-change
  processing, incremental completion, full flood scheduling, job completion,
  post-flood processing, Region dirty signal, passability update, and Region
  reconstruction methods.
- The unload summary distinguishes newly opened and already-coalesced Region
  dirty windows, Area/passability/other dirty sources, pending-change batch
  sizes, scheduled versus immediate Area floods, rebuild intervals, phase
  timing by speed, and the eight worst Area completions and Region rebuilds.
- No Core rebuild is delayed, suppressed, reordered, or replaced. Nothing is
  logged per tile, per region, per frame, or during active simulation, and no
  save data or topology state is changed.

### 0.1.23 - Passability source and actual-delta diagnostics

- The 44.8-minute 0.1.22 run measured 1,561,947 processed passability
  positions, 1,559,345 Region-dirty signals from passability, and 12,491
  synchronous Region rebuilds. Rebuilds averaged `11.728 ms`, occurred about
  4.64 times per real-time second, and consumed 146.5 seconds inside the
  measured rebuild call.
- Added ten aggregate, fail-open hooks around Core's passability signal
  receiver, queued batch, recursive PathNode refresh, and seven known Core
  signal senders. Direct or mod-originated signals that bypass those senders
  remain explicitly unattributed rather than being assigned to Core.
- The unload summary reports signal-source counts, same-position queue
  coalescing, queue batch sizes, forced path-job completions, root and recursive
  node refreshes, unchanged versus connectivity/traversal-only deltas, changed
  PathNode fields, batch timing, and the eight slowest batches.
- The diagnostic does not suppress a signal, remove a queued position, alter a
  PathNode, complete or defer a path job, change Region invalidation, or write
  save data. Nothing is logged per signal, position, node, batch, or frame while
  simulation is active.

### 0.1.24 - Hierarchical whole-frame and Region eligibility diagnostics

- Extended the passability investigation to the actual Region-invalidation
  boundary. Each root PathNode refresh now records whether the processed tree
  produced no meaningful change, a traversal-only change, a connectivity or
  Area change, or an unclassified external change. It also records whether
  `RegSys.UpdateRegDenyAt` opened a new dirty window or merely arrived while a
  rebuild was already pending.
- Added a temporary whole-frame hierarchy profiler covering concrete
  `Update`, `LateUpdate`, `FixedUpdate`, and `OnGUI` methods in Core and loaded
  gameplay-mod assemblies, plus the concrete methods actually registered in
  Core's game and system signal registries after initialization.
- Records inclusive and nested-exclusive time, call counts, average and maximum
  cost by game speed, assembly totals, frame intervals, garbage-collection
  deltas, and the ten largest measured contributors in each of the sixteen
  retained worst frames. Time outside the measured managed callbacks remains
  visible as an explicit engine, wait, or uncovered residual instead of being
  silently assigned to a nearby Core system.
- Signal dispatch itself is not replaced or rewritten. Listener lists,
  callback order, callback arguments, return values, and exception propagation
  remain untouched. A registered callback method can also be called directly;
  its diagnostic total therefore represents all calls to that concrete method,
  not only invocations originating from a signal.
- Discovery occurs once during mod initialization and once on the first
  game-ready frame. Its time is reported separately. Callback bookkeeping is
  also measured separately and excluded from nested target totals where the
  hierarchy permits. The profiler may itself add noticeable overhead on this
  unusually large save, so version 0.1.26 keeps it behind both diagnostic
  startup switches and disabled by default outside focused attribution passes.
- All additions are observation-only and session-only. No Region rebuild,
  signal, task, frame, callback, save record, or mod-owned gameplay state is
  delayed, suppressed, reordered, replaced, or persisted.

## Trap Floor indexed dispatcher in 0.1.26

Core registers one global `BeingPosChanged` callback for every Trap Floor. The
0.1.25 shadow pass measured 4,283,423 movement signals and an estimated
1,901,839,812 per-trap callbacks with 444 Trap Floors. Only 25,178 callbacks
identified a Trap Floor at the destination tile.

The indexed dispatcher replaces only those per-trap registrations with one
listener and a session-only position map. A movement normally performs one map
lookup and no Trap Floor callback. When the destination contains a trap, the
dispatcher invokes Core's original bound handler rather than reimplementing
its rules. Multiple candidates, if Core ever permits them, retain their
original reverse-registration dispatch order.

Every active indexed trap must retain Core's private subscribed state and match
its private position. Lifecycle hooks update the map immediately, while a
rotating audit checks the registry and signal listener list. On any uncertainty,
the dispatcher detaches itself and restores Core's original bound delegates for
the remainder of the session.

## Nanobot dirty-goal index in 0.1.22

Core's Nanobot loop previously reconsidered all 15,639 colony goals every
simulation tick even though the measured save produced about 2,482 actual
Nanobot work items on average. Compatibility and active-state indexes were not
useful because almost every goal already passed those broad filters. The dirty
index instead remembers Core's last reachability result and concentrates the
next tick on goals that were recently workable or whose inputs changed.

This is intentionally not an event-only cache. Unknown and mod-defined
availability gates are permitted, so dormant goals rotate back through Core in
fixed batches. The delay is bounded rather than permanent. Mods that change a
goal through Core's normal lifecycle or state APIs receive immediate index
invalidation; mods that mutate private state without a corresponding event may
see delayed Nanobot pickup until reconciliation reaches that goal.

Material production provides an additional bounded hint: repeated signals are
coalesced, and no more than 64 matching dormant goals join one tick's candidate
set. This prevents a common material from reactivating thousands of construction
requests at once. The rotating fallback remains separate, so the material cap
cannot starve requests later in the list.

The index is advisory only. Core remains responsible for all gameplay checks
and can reject any stale candidate. A stale inclusion costs extra work for one
pass but cannot authorize invalid work. The only accepted behavioral difference
is delayed discovery of a newly actionable dormant task when no observed event
announces that change.

## Current optimization behavior

### AI colony-goal priority enumeration

Optimization Mod no longer replaces this path. As of rc316, Core fills all
native priority lists during one pass over the colony-goal dictionary and then
processes those lists. The earlier six-bucket mod cache and its lifecycle hooks
were removed in 0.1.29 because they no longer provided a meaningful saving.
The Nanobot dirty-goal index is independent and remains active.

### Loose-object hauling

Core now shares one haulable-object snapshot among all callers in a simulation
tick, but builds that snapshot by walking every persistent object record. The
mod preserves the shared per-tick cache while building it from Core's smaller
`ActiveObjs` collection, then applies the same vanilla checks for:

- Active state
- Reservation state
- A live object and `StorableObj` component
- Player hauling prohibition
- Minimum spawn age
- Existing goals
- The storage target's own acceptance predicate

The cached candidates are revalidated for every caller because an earlier
caller in the same tick may reserve an object or assign its goal. The order
among otherwise equivalent loose objects can follow `ActiveObjs` rather than
the global object's dictionary order. Eligibility and capacity rules are
unchanged.

### Equipment lookup

Core's equipment AI scans the entire global object registry for each worker and
each retry. The mod builds a session-only index of objects that are structurally
equipment, grouped by equipment slot. Core object lifecycle hooks maintain that
index as persistent objects are added or deactivated, avoiding repeated full
rebuilds. A count and last-object-ID consistency check remains as a fail-safe.
Each query still applies all dynamic vanilla checks, including reservations,
player goals, species compatibility, per-agent blacklists, storage blacklists,
equipment policies, and `BetterItem`.

Best-equipment queries scan only the indexed candidates for the requested slot
in Core's original insertion order. Every eligible comparison is delegated to
Core's current `InventoryItemSlot.BetterItem`, preserving value, quality,
origin, wearer-mood, and future Core tie-breaking behavior without maintaining
a potentially stale duplicate ranking rule.

The related compatible-equipment queries used by Core and the equipment UI use
the same index. Core's new automated-equipment policy filter applies to AI
queries; the manual equipment panel remains unfiltered so the player can
override a policy, matching rc267 behavior.

### Stored-object UI sorting

Core normally sorts every persistent object before filtering the collection to
the selected storage unit. The mod filters by storage and search text first,
then sorts only the matching contents. Unsorted queries preserve Core's direct
enumeration path.

## Explicitly not changed

- No save data is added, rewritten, or deleted.
- Historical/disposed `BeingData` records are not removed.
- Core's per-tick object validation and count rebuild are not skipped.
- Auto-haul timing, priorities, storage rules, and task creation are unchanged.
- Atmosphere, fire, pathfinding, AI bidding, and needs are unchanged.
- The mod has no dependency on AUMF or any gameplay mod.

If a target Core method cannot be found after a game update, that patch is
skipped and the affected query remains on vanilla behavior. Query patches also
catch unexpected failures, report them to `Player.log`, and allow Core's
original method to run.

## Log verification

With diagnostics disabled, successful startup produces one normal status line:

`Optimization Mod 0.1.34 initialized successfully; diagnostic probes are disabled.`

No diagnostic startup details or unload summaries are expected. Any Trap Floor
index uncertainty is still reported as an error and states that Core's per-trap
listeners were restored.

When `DiagnosticsEnabled` is `true`, only the selected sub-profilers install
their timing hooks and unload summaries. `CoreProfilerSuite` also enables Trap
Floor dispatcher counters and the retained Core/index diagnostics.
`WholeFrameProfiler` adds the broad hierarchical profiler only when enabled
alongside the Core suite. `ExternalNanobotProfiler` reports each external
extension independently and does not alter either extension. For the 0.1.32
focused capture, startup should report all 10 AI phase hooks, every discovered
concrete AI action implementation, all eight focused AI-action hooks, and all
eight Area/Region hooks armed. Unload should include the existing AI phase and
action totals plus StoreObj branches and storage-list buckets, Reinstall phases,
RunAgent nested work, worst individual action spikes, and attached Harmony
owners for top actions.

## Runtime validation

Fully restart Stardeus after installing or replacing the DLL. Existing saves do
not require migration because the mod owns no serialized state. Version 0.1.34
keeps the master diagnostic switch disabled. The next ordinary load should show
the new version's successful startup with probes disabled.

Fresh in-game validation of the repaired scheduler counters is pending. During
the next requested investigation, enable `DiagnosticsEnabled` and
`CoreProfilerSuite` before starting a fresh game process. Startup should report
all seven scheduler hooks and all ten AI phase hooks. Let idle workers take
queued work, then return to the menu or exit. The unload summary should report
indexed scheduler flow rather than old queue/dequeue statistics, with no
missing-member exceptions or failed diagnostic hooks. Restore the master
switch to `false` after the capture. An extra capture is not required for
ordinary play with diagnostics disabled.
