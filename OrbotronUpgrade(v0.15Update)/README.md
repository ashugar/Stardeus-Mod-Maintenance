# Orbotron Upgrades 2.1.1

This Stardeus v0.15 compatibility update preserves the original Orbotron mod's additional jobs and abilities. Full credit for the original mod belongs to its original author: https://steamcommunity.com/sharedfiles/filedetails/?id=3318634071. The existing Workshop identity and compatibility-update attribution remain in ModInfo.json.

## Restore missing jobs on an existing mind

Select one active, living player-owned **Orbotron mind**. If any job in its current configured starter list is absent, the native action bar offers **Restore jobs**. Review the listed missing jobs and choose **Restore** or **Cancel**.

- Only genuinely missing job records are created. Present jobs keep the same records, learned levels, progress, daily XP, flags and priorities, including disabled/incapable jobs and Skill Cap Mod's exact learned values.
- New capable skills start at level **1**. Role/trait skill bonuses and old random generation rolls are not reapplied. Current native role/trait incapability remains level 0; native love/incapability rules still apply to new jobs.
- New public jobs start **off**, so choose their priorities in the normal Jobs interface. A newly missing internal **Task** is enabled at native priority 3; an existing Task is never changed.
- Missing or unusable body parts still prevent the corresponding work. Adding a skill does not grant an ability, replace a part, clear a restriction or force a task.
- Restored skills can affect passive combat bonuses even while new public jobs are off. Any conflicting remaining history for a genuinely missing job is replaced by its new initialization; unknown past experience is not reconstructed.
- Complete minds are unchanged. There is no automatic on-load repair or global sweep. Multiple selections, detached/stasis/dead/hidden minds, nonplayer minds and mismatched identity records are not recovery targets. An Orbotron mind transferred into another body can qualify; an unrelated mind in an Orbotron frame cannot.

The action uses Core's native selection, pooled button and confirmation popup. It does not use the developer console, Harmony, AUMF or a Skill Cap Mod API. Optional Skill Cap Mod receives new slots through its existing native Level setter hook; no other mod is changed. Switching selection or changing the target's jobs/restrictions before confirmation cancels the stale plan.

After recovery, only newly available actions belonging to the restored jobs are added to that being's native action cache. Existing actions, their order and the native running-action flag are preserved. The action index is marked for rebuilding without cancelling current goals or reservations.

## Compatibility and validation

Recovery's native contracts are reviewed for **v0.15.0-rc344.3226**, Game module `bf5d4d92-ad65-4f59-a00e-14a764a9eceb`. Version 2.1.1 accepts this exact module. An unreviewed Core module disables the recovery action with one startup warning; it does not rewrite minds or silently approve the new contract. The recovery algorithm, two additive JSON patches and spawn/progression operation are unchanged.

Historical gameplay validation of **2.1.0 on rc335.3217** confirmed restoration on an existing Orbotron mind in a separate save, preservation of existing skill values and job priorities, and persistence after saving and loading. That acceptance applies to the earlier binary and Core build. **Fresh startup, the native recovery interface and separate-save save/load acceptance for 2.1.1 on rc344 remain pending.**

Current compiled checks cover missing-only restoration, unchanged existing records and priorities, native restrictions, repeat/refusal/rollback cases, native synthetic persistence and action-cache preservation, with and without Skill Cap Mod. The released 2.1.0 DLL rejects current Core; the rebuilt candidate accepts it and still rejects an unknown Core identity. Synthetic tests use a private managed substitute for Unity's native string-hash primitive and directly invoke Skill Cap's new-slot setter integration; they do not exercise actual Harmony detours, the native interface or Skill Cap's full save component.

The build maps debugger document paths to portable paths instead of retaining private local build folders. The compiled recovery method bodies are compared with 2.1.0; only the supported module and runtime version messages change. Recovery does not reset existing values or override native load rules or other mods' normal UI/save processing. Real UI redraw and optional-mod save/load behavior remain part of runtime acceptance.

## Saves and rollback

Save separately before using recovery. Added jobs are saved normally by Core and optional Skill Cap Mod; removing the DLL or reverting configuration does **not** undo jobs already saved. Restore an untouched pre-recovery save for exact rollback. Do not delete newly restored jobs after training them or changing their priorities as an assumed lossless reversal.

A precommit insertion failure removes only this operation's own new records. An incomplete cleanup is reported explicitly. After structural success, a refresh failure is reported as **jobs restored, refresh failed**; committed records are retained because native observers may already have seen them. Existing player orders, reservations, inventory, roles/traits, species and ownership are not rewritten.

## Source and build

`Code/RecoveryService.cs` owns the narrow missing-only planner/transaction. `Code/RecoveryTarget.cs` checks current native state/selection and refreshes the actual owning body. `Code/OrbotronRecoveryAction.cs` registers only the user-triggered native action. `Code/AssemblyInfo.cs` contains explicit 2.1.1 assembly/file/informational metadata.

The runtime payload includes the DLL, definitions and public documentation. Maintained source, the four Code files, project and build script are in the repository; legacy source/build files in a live installation are not selected by the runtime installer.

`mod.csproj` targets net481/x64 against the installed Stardeus Managed assemblies with non-copying references. `Build.ps1` invokes the installed .NET SDK compiler with the same explicit references without NuGet restore, emitting only `Libraries/OrbotronUpgrade.dll` with embedded debugging. Both build routes map source paths to `/_/OrbotronUpgrade`; local account/build paths are not retained in release symbols. No Core, Unity, Skill Cap, Harmony or framework DLL is bundled. Build caches, tests, probes, backups and diagnostic evidence belong outside this mod directory. Build only while Stardeus is closed; the helper refuses a running game and never starts or stops it.

## Changes

2.1.1 revalidates the existing selected-mind recovery for rc344.3226. Existing data upgrades, author attribution, Workshop identity, current native arrays/curves and the existing spawn operation are preserved. Existing saved minds are changed only after their own explicit recovery confirmation. See CHANGELOG.md for release history.
