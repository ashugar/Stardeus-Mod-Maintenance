# Quest Request Preview feasibility research

Date: 2026-08-15

Authoritative inputs inspected:

- Installed Stardeus `Game.dll`, updated 2026-08-14.
- Installed Core `ReferenceSourceCode`.
- A current direct decompilation of the installed game assembly.

## Current Core behavior

`QuestsHandler.ShowNewQuest(Quest)` builds the incoming request popup. Its header
close callback disposes the popup and additionally sets Signal quests to
`QuestState.Rejected`. The popup footer contains the Accept action.

`QuestsWidget.LoadQuestFooter(...)` builds the actions in the main Quests
window. Available quests and Quest Hub offers receive Accept. Available main
quests that are not auto-added also receive Reject. `SubPanel.Close()` already
exists as a state-neutral UI operation, but it is not presented as a footer
choice.

Acceptance routes through Core's `AcceptQuest` or `AddQuest` queries. Rejection
calls `QuestData.SetState(QuestState.Rejected)`, which marks the quest ended and
can update NPC quest-offer delays. A neutral preview exit must call neither path.

## Initial narrow UI change

The preview behavior itself requires only narrow UI changes:

1. Replace the incoming-popup construction so its close callback only disposes
   the popup.
2. Add an explicit Reject action for Signal quests, preserving the vanilla state
   transition without hiding it behind the close control.
3. Add a neutral Close action to the main Quests window's available-offer
   footer; its callback invokes only `SubPanel.Close()`.

No objectives, provider ownership, rewards, timers, or custom save schema need
to be modified.

## Signal save/load finding

The supplied current and previous logs show the same mod build initializing
without a Quest Request Preview or Harmony error. In the previous session,
`Signal@7217` remained present after repeated preview interactions; after the
save was loaded in the current session, that Signal no longer appeared. Auto
Hauling failures in the same logs are unrelated and were excluded from this
analysis as requested.

Installed Core source explains the transition directly:

1. `QuestSys.SaveSpecial(...)` serializes only `AllPlayerQuests`.
2. A newly generated Signal offer remains `Undefined` and normally exists only
   in `QuestSys.ProviderQuests` until the player accepts it.
3. `QuestSys.RestoreData()` can therefore restore the Signal space object and
   its quest ID without restoring the offer's `QuestData`.
4. Core then calls `RemoveOrphanedSignalOffers()`, whose documented purpose is
   to delete exactly such a Signal.

The fix registers an undecided Signal in `AllPlayerQuests` without changing its
state or activating it. This lets Core serialize the full offer and restore both
the quest and provider lookup. Before Accept or explicit Reject, the mod removes
that temporary registration so Core's original action owns the resulting list
and no duplicate is introduced.

Saves made by an earlier build contain no recoverable copy of the original
offer. A prefix on `RemoveOrphanedSignalOffers()` prevents Core from deleting
the surviving Signal during load; responding to it lets Core clear the stale ID
and generate a replacement. This is best-effort recovery of the Signal itself,
not reconstruction of request details that were never written to the save.

## Final code audit

The launch audit made five internal improvements before the save/load defect
was reported:

1. Shared neutral-Close button construction now lives in one UI helper, keeping
   button style, tooltip, and behavior consistent across both patched screens.
2. Incoming Accept and explicit Signal rejection now have named helper methods,
   making the two state-changing paths distinct from every neutral close path.
3. The Quests-window patch uses Harmony field injection for Core's `footLife`
   and `ctx` fields instead of performing reflective field lookup on every open.
4. Quest Hub detection now mirrors Core's own `ctx.HubQuests != null` and
   `QuestState.Undefined` test, avoiding an action on unrelated undefined quests.
5. The initialization log reads the compiled assembly version instead of
   duplicating a manually maintained version string in gameplay source.

The save/load correction then added a focused persistence helper and legacy
Signal recovery patch. Every mod-defined method has an XML summary, with
parameter and return documentation where applicable. The mod still performs no
tick work, owns no custom state, and adds no custom save schema or dependency
surface.

## 1.0.1 runtime verification

Before the version-only promotion from 1.0.0 to 1.0.1, the identical gameplay
code was tested against Stardeus `v0.15.0-rc256.3125 [2026.08.14]`. The supplied
fresh `Player.log` confirms that `Signal@7217` survived save restoration with
its displayed `Transistor x24 → Glass x31` deal, remained interactable, and was
followed by a successful save-and-exit cycle. The log contains no Quest Request
Preview exception, Harmony failure, quest restore failure, duplicate-quest
warning, or provider-ID mismatch. The only explicit error is an unrelated Unity
DBuffer shader compatibility message during graphics initialization.

## One-shot personal request finding

The next supplied `Player.log` records Virginia asking for a personal favor and
Core enqueuing its mandatory `Respond / Ignore` dialog. After Respond opened the
quest details, the mod-added neutral action allowed the preview to close, but no
quest appeared in Current or Archive because the generating event had already
ended and removed its own log entry.

Installed Core source confirms this lifecycle across the complete category:

1. All ten `Random/Personal_*` quest definitions use `Colonist` as their quest
   provider and share only the broad `Random` and `Template` flags.
2. Every runtime construction of `ColonistQuestProvider` outside deserialization
   belongs to one of Core's colonist personal-favor story events.
3. Each event sets its state to `Ending` before showing the initial prompt,
   disables Escape on that `Respond / Ignore` dialog, and calls `HideLogEntry()`.
4. Respond invokes `ShowNewQuest(quest)`, but the ended event stores no callback,
   notification, or other entry point capable of reopening an unanswered quest.

The provider type is therefore the stable category marker. Quest Request
Preview now returns all `ColonistQuestProvider` offers to Core's original popup
without adding a neutral Close action. This intentionally prevents a misleading
promise rather than expanding the mod into a custom personal-request inbox or
changing those quests to Available. Signal and NPC-ship request handling is
unchanged.

## 1.0.2 runtime verification

The corrected personal-favor exclusion was accepted in game before the
version-only promotion from 1.0.1 to 1.0.2. The promoted build changes only the
manifest, assembly metadata, and release documentation; its gameplay source is
the same implementation that passed that check.

## Limits and risks

- Closing does not freeze the world. An undecided quest remains subject to any
  existing provider availability, deadlines, ship movement, or other Core rules.
- All target methods and the injected `QuestsWidget.footLife` and
  `QuestsWidget.ctx` fields are private UI implementation details. The Signal
  recovery patch also targets private `QuestSys.RemoveOrphanedSignalOffers()`.
  Harmony can access them, but a future Stardeus update may require a
  compatibility revision.
- If an older save already lost a Signal's unsaved `QuestData`, the original
  request and reward cannot be recovered from that save. The Signal is retained
  so Core can create a replacement when the player responds again.
- Static build inspection cannot prove pooled-UI layout, Escape routing, Signal
  state behavior, or repeated open/close lifecycle safety. Those require the
  runtime checklist in `README.md` and a fresh `Player.log` review.
