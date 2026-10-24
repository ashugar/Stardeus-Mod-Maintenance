# Changelog

## 2.2.0 — 2026-10-24

- Added **Restore jobs** to multiple selected Beings. Selecting all Worker Robots offers one preview/confirmation for eligible, active, living player-owned **Orbotron minds** with missing configured jobs. Other selected beings and already-complete minds are skipped.
- Preserved the existing per-mind missing-only transaction, defaults, native restrictions, old skill records and priorities. Single-mind recovery remains available. Native jobs/abilities/curves, both patches and the authored spawn gate are unchanged.
- Copied selection membership, fixed the confirmed target subset, prevented duplicate/reentrant confirmations and revalidated each pending target. Changed selection/lifetime or serious cleanup/refresh failure stops remaining work; earlier successful additions remain. Clean local refusals/rollbacks are reported and other valid targets can continue.
- Added bounded preview/result summaries and one deferred completion, plus a portable regression harness for the production coordinator and current-Core compiled preservation checks with/without Skill Cap semantics.
- Baseline 2.1.1 existing-mind recovery and preservation after save/load were user-confirmed on rc344.3226. Fresh 2.2.0 batch UI, actual optional Harmony interaction, large-selection responsiveness and separate-save save/load acceptance remain pending.

## 2.1.1 — 2026-10-24

- Revalidated the native recovery contracts for Stardeus v0.15.0-rc344.3226 and updated the exact supported Core identity so **Restore jobs** can register on this build. Other Core builds remain gated until reviewed.
- Kept the recovery transaction unchanged: only missing configured jobs are added; existing skill records, learned values and priorities are not reset. Native restrictions, new-job defaults, additive action refresh and failure handling remain unchanged.
- Rebuilt against the current installed assemblies. Compiled preservation checks covered Skill Cap present and absent paths. Existing-mind recovery and preserved skills/priorities after separate-save save/load were subsequently user-confirmed for 2.1.1 on rc344.3226.
- Preserved the two additive patches, native jobs/abilities/curves, authored spawn requirements, original attribution and Workshop identity.

## 2.1.0 — 2026-10-17

- Added **Restore jobs** for one selected active, living, player-owned Orbotron mind. Preview and confirmation list only missing configured starter jobs.
- Preserved all existing job records, learned skill values, progress and priorities, including disabled or incapable jobs. No automatic load-time repair or global migration runs.
- Restored capable missing skills at level 1, with new public jobs off until the player chooses their priorities. Native trait/body restrictions remain; a newly missing internal Task retains its native enabled priority.
- Added only eligible native actions belonging to restored jobs, preserving existing action order and ongoing work.
- Confirmed existing-mind recovery and retained values/priorities after save/load through gameplay. Automated checks passed with and without Skill Cap Mod.
- Prepared portable debug paths, a clean runtime package and a separate source package. No gameplay code change was made during release preparation.

Tested on Stardeus v0.15.0-rc335.3217. Recovery is disabled on unreviewed Core builds until its native contracts are checked. Existing additive jobs/abilities, progression curves, original attribution, Workshop identity and spawn requirements remain unchanged.

## 2.0.1

- Corrected the Orbotron job and ability patches to add the mod's extensions while preserving native entries and progression curves.
- Preserved the existing spawn requirement operation. This definition correction applies to new minds; it does not rewrite existing saved minds.
