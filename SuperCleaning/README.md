# Super Cleaning 1.0.4

Super Cleaning keeps otherwise available Cleaning Bots from remaining idle
while safe, reachable dirt is waiting. When enabled, an idle bot starts the
same vanilla cleaning sweep used by Stardeus's **Clean Now** action. The mod
honors Auto Clean, Cleaning job priority, direct control, maintenance/device,
work-capability, safety, reservation, and reachability restrictions.

Idle Assist intentionally uses the same sweep as **Clean Now**, so it may search
beyond any finite assigned Bot Dock **Work Radius > Range**. A Range of **∞** is
already unrestricted. Normal dock-assigned tasks, including cleaning, continue
to obey the selected Range.

It also adds a manual **Queue Cleanup** action for ships where dirt is outpacing
the normal Cleaning Bot cycle. An optional **Colonist Assisted** setting adds a
separate manual queue button for early-game Colonists and other non-Cleaning Bot
workers who have the Cleaning job available. Nobody is automatically forced to
clean.

The action creates ordinary Stardeus floor-cleaning tasks. Each click requests
10% of the current dirt-mark count, with a minimum request of 24 when enough
dirt exists. The actual number created can be lower when dirt is unreachable,
already claimed, or stacked on fewer floor tiles. It does not interrupt a bot
doing other work, change Cleaning Bot cooldown settings, or continuously create
floor-task backlogs.

The Auto Utilities Mod Framework widget opens a two-tab panel:

- **Tasks** shows current dirt and cleaning-task totals, a live Cleaning Bot
  activity breakdown, separate vanilla/player and Super Cleaning queue counts,
  an expandable selectable idle-bot list with reasons, an hourly net dirt trend
  with limited 10-minute history, and a collapsed-on-demand mod task list. Other
  non-cleaning activities appear dynamically only while bots are doing them.
- **Settings** controls the master switch, independent Idle Assist, and optional
  Colonist Assisted manual queue. Work already accepted by Stardeus is left
  alone.

Framework-provided tabs and collapsible arrows follow the active Stardeus UI
theme through AUMF 1.4.0.

Queue totals include unfinished floor-cleaning tasks retained by Core, including
failed tasks waiting for retry and interrupted tasks waiting to resume. Super
Cleaning retains ownership through those states and across save/load. Ownership
ends when Core removes the task or it enters a completed, cancelled, or terminal
error state. This is task accounting only; Core still controls retries and work.
Ownership already discarded by an older build cannot be recovered reliably, so
the mod does not relabel existing vanilla/player tasks.

Pending Super Cleaning tasks can be cancelled explicitly. The cancellation
leaves pre-assigned and in-progress work plus every vanilla/player task alone.
Stardeus does not attach an originating event to dirt records, so the history
reports measured net changes rather than guessing at causes.

Requires Auto Utilities Mod Framework 1.4.0.

## Release history

### 1.0.4

- Makes the Idle Assist Bot Dock Range override explicit in the Settings tooltip
  and public documentation.
- Clarifies that the Clean Now sweep may search beyond any finite **Work Radius >
  Range** setting, while **∞** is already unrestricted.
- Confirms that normal dock-assigned tasks, including cleaning, still obey Range.
  No cleaning, task-selection, or execution logic changed in this release.

Compatibility was checked against Stardeus v0.15.0-rc344.3226 and AUMF 1.4.0.
The revised Idle Assist tooltip, including its wrapping and **∞** glyph, was
confirmed in game by the mod author on October 24, 2026, before the version
increment.

### 1.0.3

- Restores compatibility with Stardeus v0.15.0-rc321 after Core added a
  cleaning-area parameter to its reachable-dirt search.
- Keeps the bounded cleanup queue and Idle Assist searching all eligible dirt
  while preserving Core's safety, reservation, and reachability checks.
- Preserves the existing task-ownership accounting, queue limits, and native
  worker selection behavior.

Compatibility was checked against Stardeus v0.15.0-rc321 (October 6, 2026)
and AUMF 1.4.0. The exception-free cleanup queue and completed cleaning work
were confirmed in game by the mod author before the version increment.

### 1.0.2

- Retains mod ownership when Core keeps a failed or interrupted cleaning task
  for retry or recovery, including across save/load.
- Includes those waiting tasks in the total, Super Cleaning, and vanilla/player
  queue counters. Updates the related UI tooltips.
- Preserves native cleaning behavior and the existing cancellation limits.

Compatibility was checked against Stardeus v0.15.0-rc290 (September 12, 2026)
and AUMF 1.4.0. The ownership and counter correction was confirmed in game by
the mod author on September 12, 2026, before the version increment. The 1.0.2
release passed 102 isolated ownership regression checks and preserves the
validated cleaning logic.
