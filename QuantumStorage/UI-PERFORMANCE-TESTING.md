# Material panel performance — 1.3.5

## Current rc335 compatibility update — 2026-10-17

Version 1.3.5 restores row reuse for the reviewed rc335.3217 panel and extends approval to its native action callbacks. Core still owns the complete list, current-count quantity choices, ejection, tooltips, sorting and filter lifetimes. Unknown root or callback bodies retain the full native panel. No rows are truncated, updates delayed or inventory records changed.

Reviewed Game.dll SHA256: `7122A8E390B8B34D9D3241960FCCF743EE022BB30B346E841C372CAC72FEC62B`.
Reviewed MatStorageTab.BuildContentsList body SHA256: `76FF13D9C8D9F78833C6CD7F0BC7263FA03E072C7B49B615817E6FAAB2F8CD54`.

On 2026-10-17, the player reviewed the Quantum Storage Unit panel on 1.3.5 and reported no visible desynchronization or FPS drop. This confirms no visual/performance regression in that observed test. It is not a numeric benchmark, proof of a particular internal optimization path, or confirmation of every control and edge case below. Detailed Unity interactions and controlled comparisons remain separately tracked; also see [COMPATIBILITY-TESTING.md](COMPATIBILITY-TESTING.md) for the separate object-Hive repair. Earlier acceptance and external check totals below apply only to their stated versions.

Rollback: `Mods/.release-backups/QuantumStorage-before-1.3.5-20261017`. Close the game before restoring the audited changed-file list; 1.3.4 falls back to the native panel on rc335 and retains its known removed hauling-helper reference.

## Historical rc332 compatibility update — 2026-10-15

The pre-update log loaded Quantum Storage 1.3.3 on Core rc332.3211 and recorded the intended complete-native-panel fallback (line 324). This was not an inventory initialization failure. Version 1.3.4 updates the reviewed panel fingerprint after source and installed-binary review, retaining the existing row policy and Core's current callbacks.

Current reviewed Game.dll SHA256: `EA888C03D577486A4F16CC1100C7C21A3748B64D773DE6F3FE98A3F960AAE16C`.
Current reviewed MatStorageTab.BuildContentsList body SHA256: `889E6373FFC4E722EEF65AB7A9FAFF92258BD3BF14E78AADB8F5DAEC797E8A53`.

Core still rebuilds the complete list on storage change. Its ejection menu reads current quantities at click time; its current callbacks avoid editing a pooled row after ejection triggers a rebuild. The optimization retains those native actions, the native filter-panel lifecycle and the Relay offline-button postfix. A different method body still disables only the optional UI optimization.

Fresh startup and gameplay validation of 1.3.4 are pending. Restart, confirm 1.3.4 loaded without a panel fallback, and run the checklist below. In particular, fully eject one material while other rows remain; verify the remaining rows and their actions stay correct. Repeatedly open/close the filter subpanel and switch endpoints to check Core's filter cleanup remains intact. No FPS improvement on rc332 is claimed before that test.

Current external checks: release build passed with 0 warnings/errors; 72,919 regression checks passed, including 10,330 panel-policy/body-guard/binary-contract checks. The production guard accepts the installed reviewed method and rejects null/unreviewed methods. Binary/reference/patch-target audit passed 673 checks; storage-warning audit passed 43; material-option audit passed 83 after updating its obsolete pre-rc332 zero/duplicate-offer expectation to the verified current Core behavior. These checks do not execute Unity rendering, in-game Harmony detours or actual button clicks.

Rollback for this update: `Mods/.release-backups/QuantumStorage-before-1.3.4-20261015` (complete previous 1.3.3 installation). Close the game before restoring it. No save schema changed; on rc332, restored 1.3.3 will use Core's complete panel again.

## Historical 1.3.3 result — 2026-10-11

The player reported: "I don't see any appreciable drop in FPS while having the UI panel open like I did before." This accepts the reported open-panel performance improvement; it does not claim every control/edge case below was separately exercised.

The log supplied at that time confirmed Quantum Storage 1.3.3 loaded (lines 112 and 180), with no material-panel optimization fallback or Quantum Storage exceptions recorded. This evidence does not validate the later 1.3.4 binary or rc332. No numeric UI frame-rate gain is asserted.

For future regressions, restart after any DLL change and use a separate test save to compare the same busy scene with the panel closed and open.

## What changed

Only presentation of material contents: Quantum Storage, Cabinet, Freezer, Fridge, Beverage Cooler, Pantry, and their material Relay counterparts. Core's initial full list remains authoritative. If material types stay the same, only changed quantity labels are written; sorting moves the original rows. New/removed material types trigger the full native rebuild. No item limit, pagination, delayed updates, or stock/reservation/capacity changes. Object-storage panels are unchanged.

## Targeted regression checklist

The 1.3.5 Quantum Storage Unit panel's visible synchronization and lack of noticeable FPS regression are user-confirmed. The detailed scenarios below remain a regression checklist, not a claim that each was individually exercised; specific sorting, ejection, filter, Relay and save/reload cases remain unconfirmed.

1. Open a busy Quantum Storage material panel during deposits and withdrawals. Confirm all materials and live quantities remain visible, and compare FPS with the previous build under the same conditions.
2. Test By Name, ascending quantity and descending quantity, including equal counts and changing order. Rows must still eject the material they display.
3. Hover tooltips; use several individual quantity choices; completely remove one type; add a new type. Confirm empty-to-stock and stock-to-empty transitions.
4. Eject All with the panel open, then allow items to return. Confirm the list/capacity agrees with contents throughout and there are no stranded or wrong-material buttons. Core's pile merging is unchanged.
5. Switch between endpoints, tabs, a vanilla storage and a Relay; close/reopen the panel and save/reload. No previous endpoint's rows or actions should remain.
6. In a material Relay chain, turn a bridge off/on. Check the full local/network list, capacity/status and disabled ejection buttons while offline.
7. Confirm filters, Auto Haul, priorities and headers work normally. Check fresh logs for Quantum Storage errors or a material-panel optimization fallback notice.

## Historical 1.3.3 static/external evidence

- Built against rc327.3205 Game.dll SHA256 `35ED34108F3C39CBE9EDC28083FF875693D828D56474513C6B51463C64F0FB42`.
- Reviewed native MatStorageTab.BuildContentsList body SHA256 `F5BAD74C0340E82C876BB5D8A5261766A77452E822772FEBE29D4EB4BE527B2E`; changes disable this optional patch, not inventory support.
- The row-policy test retains 1,000 entries and all row identities across 10,000 quantity updates, making 10,000 changed-label writes with no row replacements. Also covers sorting/ties, structural changes, stale rows and no partial updates on rejected reuse.
- Unity row capture, rendering and actual button callbacks cannot be exercised by the external test harness. The player's open-panel FPS observation is separate gameplay evidence; it does not certify each interaction in the checklist.

## Historical 1.3.3 rollback

Full previous 1.3.2 package: `Mods/.release-backups/QuantumStorage-before-1.3.3-20261011`. Close the game before restoring it. No save schema changed. No Workshop upload was performed.
