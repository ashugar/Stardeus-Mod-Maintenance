# Quantum Storage 1.3.7 — rc341 compatibility

## Current 1.3.7 validation scope — 2026-10-21

Target: Stardeus v0.15.0-rc341.3223. The player reports sustained 75–80 FPS with the General Storage Unit material list closed and 35–40 FPS open. The current log confirms that 1.3.6 rejected the changed Core panel contract and used the full native list. This establishes loss of the optional optimization, not a controlled measurement of its frame cost or proof of an exclusive cause.

Version 1.3.7 re-reviews the complete current builder and three native action callbacks, updating only their four approval fingerprints and release metadata. Core continues to own ejection, current quantity choices, tooltips, sorting and filter lifetimes. The row-reuse algorithm and unknown-contract safety fallback remain unchanged. No save migration, inventory, hauling, Relay, recipe, research or capacity change is introduced.

The rc341 release build passed with zero warnings/errors. The focused harness passed 10,360 checks against the actual 1.3.7 guard and row cache, including rejection of changed/missing/extra/duplicate callbacks, sorting, structural fallback and native forbidden-item notification. No Harmony detours, Core gameplay callbacks or Unity rendering were exercised.

Fresh 1.3.7 startup and gameplay acceptance remain pending. After installation, confirm the intended version loads without the material-panel fallback, compare the same busy scene with the material list closed/open, and check live quantity/sort changes, partial/full ejection, Eject All and filter/close-reopen behavior. External checks cannot establish Unity rendering or live control behavior. Keep a separate known-good save.

The remaining historical scenarios below are coverage limits, not newly reported failures or mandatory work for this narrow repair. The accepted 1.3.6 release and its publication remain recorded below; that acceptance does not certify 1.3.7 or rc341. Research/balance advisories remain deferred.

## Historical 1.3.6 scope

Target: Stardeus v0.15.0-rc337.3219. Version 1.3.6 narrowly restores the reviewed material-panel optimization; hauling and gameplay policies are unchanged. Fresh startup, the observed panel-performance result and user-controlled Workshop publication are recorded below. Detailed panel actions and broader gameplay checks remain individually unconfirmed. Keep a separate known-good save. No save migration is introduced.

## Recorded 1.3.6 acceptance — 2026-10-19

The player confirmed that opening the Quantum Storage Unit panel caused only a slight initial FPS dip before performance recovered to its starting level with the panel open. This accepts the narrow fix for the sustained open-panel slowdown in that observed test. It is not a controlled numeric benchmark, proof of internal row reuse, or confirmation of every panel action.

A fresh rc337.3219 log confirms Quantum Storage 1.3.6 loading from the live mod folder, with no matched Quantum Storage exception or material-panel fallback warning. This is focused startup/error evidence, not a claim that the whole log is error-free or every feature ran.

The player confirmed Workshop publication of 1.3.6. This documentation closeout leaves the accepted DLL and version unchanged. The remaining object-Hive, detailed UI, Relay and save/reload checks below are unconfirmed scenarios, not reported failures or a reopening of the accepted panel fix. Research-cost/tier advisories remain deferred to the separate Stardeus v1.0 balance review.

## Historical 1.3.5 acceptance — 2026-10-17

The player confirmed that the Quantum Storage Unit panel showed no visible desynchronization or FPS drop after the 1.3.5 fix. This establishes no visual/performance regression in the observed panel test, not a quantified gain, proof of the internal row-reuse path, or individual validation of every panel action. Object-Hive delivery, interruption/recovery, pending/active save-reload and optional-integration scenarios below remain unconfirmed.

The player subsequently confirmed Workshop publication of 1.3.5. Its observed panel acceptance does not cover later Core builds or the rebuilt 1.3.6 DLL; the separate 1.3.6 acceptance is recorded above.

A fresh rc335.3217 log reviewed during release preparation confirms 1.3.5 loading from the live mod folder, with no matched Quantum Storage exception or material-panel fallback warning. The earlier startup-file sharing and safe-mode messages are absent from this newer log. This is focused load/error evidence, not a claim that the entire log is error-free or that every feature was exercised.

## Object-Hive delivery

1. When separately validating object-Hive delivery, confirm the intended installed Quantum Storage version loads. With researched, fueled, powered Hives and eligible loose objects, observe actual deliveries to Quantum Weapons Lockers, Wardrobes, Organ Storage and Parts Storage. Check original object quality, condition and identity remain intact.
2. For the same object, provide a farther higher-priority eligible endpoint and a nearer lower-priority endpoint. The higher priority must win. Equal priorities prefer the nearer endpoint, with stable endpoint ID ties.
3. Compare separate ready hauling jobs: the selected policy ranks their geometric two-leg squared trip distance, then stable goal ID. Destination priority is not a new global job priority. This differs intentionally from the removed helper's weighted score.
4. Confirm no bypass of priority zero, full storage, rejected filters, disabled hauling, research, power/fuel, fire/safety, Hive range or area restrictions. Reserved, stored, carried, worker-assigned and removed objects must not be stolen or duplicated.
5. Interrupt an active transfer by removing its source, destination or Hive where practical, and by disabling relevant facilities. Check cancellation/retry and that only the operation's owned reservation/carrier claim is released. Verify remaining inventory before saving.
6. Save/reload with pending and active transfers. Confirm recovery and normal delivery without duplicated items, stranded reservations or repeated errors.
7. With native Hive work, Optimization Mod and Part Restoration Unit active, check that a Hive is not assigned twice. When TACC is present, check washable-clothing delivery/return; absent TACC must not prevent ordinary Quantum delivery. Do not remove dependencies from an important save merely to test this.

## Material panels

The observed 1.3.6 panel-performance result is accepted above. Detailed checks in [UI-PERFORMANCE-TESTING.md](UI-PERFORMANCE-TESTING.md) remain individually unconfirmed, including live quantity/type/sort changes, partial/full ejection, Eject All, filters, endpoint/tab changes, disposal/reopening and Relay power transitions. Retain every row and immediate updates. A controlled comparison of the same busy scene with the panel closed/open and the previous build would be needed to quantify an FPS gain.

## Evidence boundaries and recovery

External policy, metadata and callback-guard tests do not execute Unity widgets, live Harmony ordering, real transfers or save recovery. A clean startup alone does not prove a hauling candidate reached the changed selection code. Record the tested build and scenario separately from absence of errors.

Inspect a fresh Player.log for Quantum Storage initialization errors, missing methods, object-hauling session-disable messages and material-panel fallback. A future unreviewed panel contract should retain the complete native panel, not disable inventory.

Use the exact update's private installation plan and rollback journal to restore only its changed files, with Stardeus closed. Restoring 1.3.6 on rc341 restores its complete-native-panel fallback; rollback is not a repair for this performance regression. Retain the older 1.3.6 transaction separately. Do not overwrite the only known-good save during testing.
