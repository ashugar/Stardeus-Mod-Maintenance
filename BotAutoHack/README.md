# Bot Auto Hack

Bot Auto Hack queues eligible hostile robotic Beings and starts one ordinary
Stardeus `Hack` per simulation tick. Current Core hashes each robot's
persistent Being ID with its `Hacked.StartTick`, then applies a global
time-since-last-conversion policy. Bot Auto Hack preserves that seed, cooldown,
condition lifecycle, and failure damage while converting Core's harmless 30%
failure result into a transparent 30% conversion floor.

## Requirements

- Stardeus 0.15.0, validated against v0.15.19.3268
- Auto Utilities Mod Framework 1.4.0
- The existing Rogue AI **Signal Intrusion** research in the current save

The mod does not add, unlock, or bypass research. In rc313, Sandbox can display
Signal Intrusion as unlocked while Core's Hack action still checks the separate
unlock-effect flag. This is a Core issue; Bot Auto Hack does not override it.

Bot Auto Hack uses AUMF's stable native top-bar widget and `FrameworkPanel`
control builders. AUMF 1.4.0's automatic theme changes target tabs, managed
scrollbars, and collapsible arrows that this mod does not use, so no
`ThemeAwareUi` helper or optional feature gate is required.

## Controls

Open the Robot Eye top-bar widget.

The number below the widget is the current number of living, active robotic
Beings carrying Core's `Hacked` condition. It is intentionally separate from
the number of targets still waiting in the individual queue.

- **Automatically hack hostile bots** starts disabled on a new save.
- While main automation is on, **Remove working Firewall Chips** can be enabled
  separately. It also defaults off and is turned off whenever main automation
  is disabled.
- While automation is off, **Hack All Hostile Bots** queues every currently
  eligible target.
- Core's ordinary multi-selection **Hack** button remains available under its
  normal rules. Clicking it with multiple qualifying Beings redirects those
  targets into the same individual queue.

The queue starts at most one accepted Core Hack per simulation tick. It waits
while the simulation is paused.

## Optional Firewall removal

When both automation toggles are on, the scan can register Core's ordinary
`Remove` surgery operation for a Firewall Chip in an incapacitated hostile
robot's Implant slot. The mod never removes or damages the chip directly.

A removal is ordered only while all of these remain true at scan time:

- the Being is living, active, hostile, robotic, and incapacitated;
- the Implant slot contains a working Firewall Chip;
- the part is removable and not welded;
- no existing surgery affects that slot; and
- Core reports that a suitable vanilla surgery facility is available.

Core retains ownership of the visible operation, facility and operator
scheduling, removal success or failure, and resulting extracted part. Turning
either automation off stops new orders. An operation already accepted by Core
remains visible and cancelable through the vanilla Surgeries interface.

## Conversion policy and Core-owned consequences

Every dispatched target goes back through Core's current single-target Hack
eligibility and action. Core creates the outcome seed with a full-width hash of
`Hacked.StartTick` and the persistent Being ID. Bots started on the same or
nearby ticks therefore receive separate hashed seeds.

Current Core first gives a hostile bot a 30% harmless failure result. The
remaining 70% reaches a global curve based on days since any hostile bot last
converted: 0% at 0 days, 5% at 1 day, 20% at 3 days, and 35% at 7+ days or
before the first conversion. Bot Auto Hack promotes only that harmless 30%
branch to conversion and leaves the other 70% in Core.

The resulting effective outcomes are:

| Time since last conversion | Conversion | Core burnout |
| --- | ---: | ---: |
| 0 days | 30% | 70% |
| 1 day | 33.5% | 66.5% |
| 3 days | 44% | 56% |
| 7+ days or never | 54.5% | 45.5% |

Every conversion still updates Core's global timestamp. This keeps Core's
reduced-conversion pressure while removing the guaranteed zero-conversion
window during sustained raids. The override validates the exact audited Core
method, 30% tunable, and curve anchors on startup. If Core changes any of those
contracts, Bot Auto Hack remains inactive rather than silently advertising an
unconfirmed rate.

Bot Auto Hack does not change:

- Core's secondary conversion curve or global conversion timestamp;
- irreparable failure damage on the remaining Core burnout branch;
- Hacked-condition duration or progression;
- Firewall Chip protection;
- the requirement for a present mind slot/core; or
- the separate Virus outcome applied to player-owned robots.

## Scope and guardrails

The scanner enumerates only active `Being` entities and then requires a
non-biological species. Ships, colonies, biological Beings, dead or inactive
entities, friendly Beings, coreless bodies, targets already carrying the
`Hacked` condition, and bots protected by a working Firewall Chip remain
outside the queue. The same working-Firewall check makes Core's ordinary Hack
action unavailable for those bots.

Pending target IDs and their automatic/explicit source are stored with the
save. Eligibility is rechecked after loading and immediately before dispatch.
Legacy retry metadata from versions 1.0.3 through 1.0.6 is ignored and removed
the next time the mod saves. Turning automation off removes only pending
automatic entries; explicit manual or multi-selection requests remain queued.

## Version history

### 1.1.0

- Adapted to Core's global time-since-last-hack-conversion policy in Stardeus
  v0.15.19.3268.
- Reassigned Core's harmless 30% failure branch to conversion while preserving
  the remaining 70% Core curve, burnout, condition, and save-owned timestamp.
- Published the confirmed effective conversion range: 30% at 0 days, 33.5% at
  1 day, 44% at 3 days, and 54.5% at 7+ days or before the first conversion.
- Added a fail-closed contract check for the audited Core method, first-roll
  branch, 30% tunable, and curve anchors.
- Added focused formula tests and updated the controls panel and Workshop text
  to distinguish the mod's conversion floor from Core-owned consequences.

### 1.0.7

- Updated for Stardeus v0.15.0-rc313.3190 after Core adopted a native hash of
  each Hacked condition's start tick and persistent Being ID.
- Removed the obsolete Hacked-condition RNG transpiler, retry-outcome tracker,
  persisted retry ordinals, and all custom seed handling.
- Kept Core's 50% roll, condition progression, conversion, and failure damage
  fully native.
- Revalidated current Hack eligibility and multi-selection methods, Firewall
  detection, surgery registration, save/load signals, and AUMF 1.4.0 usage.
- Advanced the save schema to 4. Existing settings, pending targets, counters,
  and Core-owned surgery operations remain intact; legacy retry keys are
  discarded on the next save.
- Documented the separate rc313 Sandbox research-state mismatch as a Core issue
  without adding a mod-side bypass.

### 1.0.6

- Fixed Signal Intrusion remaining marked as required after loading a Sandbox
  game where Core treats all research as unlocked.
- Added Core's authoritative research-state check while retaining the existing
  Signal Intrusion unlock-effect flag as a compatibility fallback.
- Kept the normal-game research requirement unchanged and made no changes to
  Core research state.

### 1.0.5

- Validated and rebuilt against the installed Auto Utilities Mod Framework
  1.4.0 release.
- Updated the permanent compatibility handshake and documented requirement
  from AUMF 1.3.0 to 1.4.0.
- Confirmed from AUMF's API and Theme documentation that Bot Auto Hack uses no
  changed tab, managed-scroll, collapsible, or custom theme-helper surface;
  its native widget and panel controls retain their existing theme behavior.

### 1.0.4

- Promoted the independent retry formula after multiple raids produced a more
  balanced distribution of successful conversions in live play.
- Removed the temporary Player.log seed, roll, outcome, and batch-summary
  probes used to validate the first-attempt and retry behavior.
- Retained the persisted per-bot retry ordinal and actual-outcome tracking
  needed to advance retries only after a failed Core roll.

### 1.0.3 (temporary diagnostics)

- Preserved the validated Being-ID plus StartTick formula for every bot's first
  attempt.
- Added persisted per-bot retry ordinals for failed attempts that leave the bot
  alive; cancellations do not consume an ordinal, while conversion or death
  clears it.
- Removed StartTick from retry seeds and applied a full-width MurmurHash3
  avalanche finalizer to the Being-ID plus retry-ordinal key.
- Extended Player.log probes with retry ordinals and distinct
  `MIXED_FIRST_ATTEMPT` / `AVALANCHE_RETRY` seed modes.
- Deferred diagnostic batch assignment until Core has populated StartTick,
  correcting the zero-tick batch metadata seen in 1.0.2.

### 1.0.2 (temporary diagnostics)

- Added behavior-neutral Player.log probes for each hostile robot's seed mode,
  mixed seed, first RNG value, predicted and actual Core outcome, final faction,
  and survival state.
- Added quiet-period summaries that group consecutive hack starts into raid-like
  batches and report rolled, successful, failed, cancelled, converted, and dead
  totals with the observed success rate.
- Left the 1.0.1 seed formula and Core's 50% comparison unchanged so the logs
  measure the suspected behavior without influencing it.

### 1.0.1

- Mixed each hostile robot's persistent Being ID with its Hacked start tick
  before Core's existing 50% outcome roll.
- Eliminated the consecutive-seed bands that could make an entire raid fail or
  convert together despite individual one-per-tick dispatch.
- Preserved vanilla behavior for biological and friendly Beings and retained
  Core's condition timing, faction change, and failure damage.

### 1.0.0

- Promoted the accepted automatic hacking, individual dispatch, Firewall Chip
  handling, and live active-hack count to the completed 1.0.0 release.
- Added the full Workshop description and enabled the Steam Workshop manifest
  flag.

### 0.2.1

- Changed the top-bar badge from pending queue length to the current number of
  bots carrying Core's `Hacked` condition.
- Added the active-hack count to the widget tooltip and controls panel while
  retaining the pending queue count separately.

### 0.2.0

- Added a separate, default-off Firewall-removal automation that depends on
  main automatic hacking.
- Added strict living, hostile, robotic, incapacitated, working-chip,
  removable-slot, duplicate-operation, and surgery-facility checks.
- Registered only Core's vanilla single-slot Remove surgery operation; no
  direct implant removal was added.
- Added save persistence and panel status for the new setting and orders.

### 0.1.1

- Made Core's Hack action unavailable for bots protected by a working Firewall
  Chip.
- Excluded protected bots consistently from automatic scans, the manual
  all-bots queue, multi-selection counts, and the displayed eligible count.

### 0.1.0

- Initial local release.
- Added Signal Intrusion-gated automatic and manual hostile-bot queues.
- Added one-Core-Hack-per-tick dispatch.
- Redirected multi-selection Hack clicks into the individual queue.
- Added save persistence, target revalidation, and AUMF 1.3.0 controls.
