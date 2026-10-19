# Quantum Storage 1.3.6 — rc337 acceptance

Target: Stardeus v0.15.0-rc337.3219. Version 1.3.6 narrowly restores the reviewed material-panel optimization; hauling and gameplay policies are unchanged. Fresh startup, panel actions and open/closed FPS comparison remain pending. This document separates historical acceptance from remaining gameplay checks. Keep a separate known-good save. No save migration is introduced.

## Historical 1.3.5 acceptance — 2026-10-17

The player confirmed that the Quantum Storage Unit panel showed no visible desynchronization or FPS drop after the 1.3.5 fix. This establishes no visual/performance regression in the observed panel test, not a quantified gain, proof of the internal row-reuse path, or individual validation of every panel action. Object-Hive delivery, interruption/recovery, pending/active save-reload and optional-integration scenarios below remain unconfirmed.

The player subsequently confirmed Workshop publication of 1.3.5. Its observed panel acceptance does not cover later Core builds or the rebuilt 1.3.6 DLL. Workshop publication of 1.3.6 is not yet confirmed.

A fresh rc335.3217 log reviewed during release preparation confirms 1.3.5 loading from the live mod folder, with no matched Quantum Storage exception or material-panel fallback warning. The earlier startup-file sharing and safe-mode messages are absent from this newer log. This is focused load/error evidence, not a claim that the entire log is error-free or that every feature was exercised.

## Object-Hive delivery

1. Restart after installation and confirm Quantum Storage 1.3.6 loads. With researched, fueled, powered Hives and eligible loose objects, observe actual deliveries to Quantum Weapons Lockers, Wardrobes, Organ Storage and Parts Storage. Check original object quality, condition and identity remain intact.
2. For the same object, provide a farther higher-priority eligible endpoint and a nearer lower-priority endpoint. The higher priority must win. Equal priorities prefer the nearer endpoint, with stable endpoint ID ties.
3. Compare separate ready hauling jobs: the selected policy ranks their geometric two-leg squared trip distance, then stable goal ID. Destination priority is not a new global job priority. This differs intentionally from the removed helper's weighted score.
4. Confirm no bypass of priority zero, full storage, rejected filters, disabled hauling, research, power/fuel, fire/safety, Hive range or area restrictions. Reserved, stored, carried, worker-assigned and removed objects must not be stolen or duplicated.
5. Interrupt an active transfer by removing its source, destination or Hive where practical, and by disabling relevant facilities. Check cancellation/retry and that only the operation's owned reservation/carrier claim is released. Verify remaining inventory before saving.
6. Save/reload with pending and active transfers. Confirm recovery and normal delivery without duplicated items, stranded reservations or repeated errors.
7. With native Hive work, Optimization Mod and Part Restoration Unit active, check that a Hive is not assigned twice. When TACC is present, check washable-clothing delivery/return; absent TACC must not prevent ordinary Quantum delivery. Do not remove dependencies from an important save merely to test this.

## Material panels

Run [UI-PERFORMANCE-TESTING.md](UI-PERFORMANCE-TESTING.md), including live quantity/type/sort changes, partial/full ejection, Eject All, filters, endpoint/tab changes, disposal/reopening and Relay power transitions. Retain every row and immediate updates. Compare the same busy scene with the panel closed/open and the previous build before claiming an FPS gain.

## Evidence boundaries and recovery

External policy, metadata and callback-guard tests do not execute Unity widgets, live Harmony ordering, real transfers or save recovery. A clean startup alone does not prove a hauling candidate reached the changed selection code. Record the tested build and scenario separately from absence of errors.

Inspect a fresh Player.log for Quantum Storage initialization errors, missing methods, object-hauling session-disable messages and material-panel fallback. A future unreviewed panel contract should retain the complete native panel, not disable inventory.

Use the private 1.3.6 installation plan and rollback journal to restore only this update's changed files, with Stardeus closed. Restoring 1.3.5 on rc337 also restores its complete-native-panel fallback; rollback is not a repair for this performance regression. Do not overwrite the only known-good save during testing.
