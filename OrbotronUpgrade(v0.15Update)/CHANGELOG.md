# Changelog

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
