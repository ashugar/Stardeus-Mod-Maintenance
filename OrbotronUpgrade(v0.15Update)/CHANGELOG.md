# Changelog

## 2.1.1 — 2026-10-24

- Revalidated the native recovery contracts for Stardeus v0.15.0-rc344.3226 and updated the exact supported Core identity so **Restore jobs** can register on this build. Other Core builds remain gated until reviewed.
- Kept the recovery transaction unchanged: only missing configured jobs are added; existing skill records, learned values and priorities are not reset. Native restrictions, new-job defaults, additive action refresh and failure handling remain unchanged.
- Rebuilt against the current installed assemblies. Current compiled preservation checks cover Skill Cap present and absent paths; fresh rc344 startup, UI recovery and separate-save save/load acceptance remain pending.
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
