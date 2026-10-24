# Orbotron Upgrades 2.2.0

This Stardeus v0.15 compatibility update preserves the original Orbotron mod's additional jobs and abilities. Full credit for the original mod belongs to its original author: https://steamcommunity.com/sharedfiles/filedetails/?id=3318634071. The existing Workshop identity and compatibility-update attribution remain in ModInfo.json.

## Restore missing jobs on an existing mind

Select one active, living player-owned **Orbotron mind**. If any job in its current configured starter list is absent, the native action bar offers **Restore jobs**. Review the listed missing jobs and choose **Restore** or **Cancel**.

You can also select multiple Beings, including **Select all** in **Worker Robots**, and use the same **Restore jobs** action. One preview shows how many eligible Orbotron minds and missing job records will be restored. Other robots/beings and already-complete minds are counted and skipped. Eligibility follows the mind's species and player ownership, so another species' mind in an Orbotron frame is not a recovery target.

The confirmation applies only to the displayed subset of the captured selection. Changing membership, losing the initiating action or changing the game invalidates it; sorting the same selected beings does not add targets. Every confirmed mind is checked before recovery starts, then each pending mind is checked again. Existing skill values and priorities can change naturally without recovery overwriting them.

A batch keeps earlier successful restorations if a later mind cannot be restored. Clean local refusals/rollbacks are reported while other valid targets can continue. Incomplete cleanup or a failed native refresh stops the remainder with explicit recovery guidance. Results show actual restored jobs, refusals and unattempted minds. There is one confirmation and one result, rather than a popup per mind. Cancel performs no recovery.

- Only genuinely missing job records are created. Present jobs keep the same records, learned levels, progress, daily XP, flags and priorities, including disabled/incapable jobs and Skill Cap Mod's exact learned values.
- New capable skills start at level **1**. Role/trait skill bonuses and old random generation rolls are not reapplied. Current native role/trait incapability remains level 0; native love/incapability rules still apply to new jobs.
- New public jobs start **off**, so choose their priorities in the normal Jobs interface. A newly missing internal **Task** is enabled at native priority 3; an existing Task is never changed.
- Missing or unusable body parts still prevent the corresponding work. Adding a skill does not grant an ability, replace a part, clear a restriction or force a task.
- Restored skills can affect passive combat bonuses even while new public jobs are off. Any conflicting remaining history for a genuinely missing job is replaced by its new initialization; unknown past experience is not reconstructed.
- Complete minds are unchanged. There is no automatic on-load repair or global sweep. Detached/stasis/dead/hidden minds, nonplayer minds and mismatched identity records are not recovery targets. An Orbotron mind transferred into another body can qualify; an unrelated mind in an Orbotron frame cannot.

The action uses Core's native selection, pooled buttons and confirmation popup. It does not use the developer console, Harmony, AUMF or a Skill Cap Mod API. Optional Skill Cap Mod receives new slots through its existing native Level setter hook; no other mod is changed. The single and batch paths share a transient invocation guard. A batch confirmation can execute only once and cannot silently gain newly eligible targets after preview.

After recovery, only newly available actions belonging to the restored jobs are added to that being's native action cache. Existing actions, their order and the native running-action flag are preserved. The action index is marked for rebuilding without cancelling current goals or reservations.

## Compatibility and validation

Recovery's native contracts are reviewed for **v0.15.0-rc344.3226**, Game module `bf5d4d92-ad65-4f59-a00e-14a764a9eceb`. Version 2.2.0 accepts this exact module. An unreviewed Core module disables the recovery action with one startup warning; it does not rewrite minds or silently approve the new contract. The per-mind recovery algorithm, two additive JSON patches and spawn/progression operation are unchanged.

User gameplay validation of **2.1.1 on rc344.3226** confirmed restoration on an existing Orbotron mind in a separate save, preservation of existing skill values and job priorities, and persistence after saving and loading. Earlier 2.1.0 recovery was also accepted on rc335.3217. Those tests apply to the earlier binaries. **Fresh startup, mixed Worker Robots multi-selection, preview/cancel/restore, real optional UI/Harmony interaction, large-selection responsiveness and separate-save reload for 2.2.0 remain pending.**

The portable harness source-links the actual production coordinator and checks selection snapshots/filtering/deduplication, stale/consumed confirmation, reentrancy, per-target revalidation, every recovery outcome and bounded summaries. Callback fixtures do not prove native eligibility or Unity behavior. Separate current-Core compiled checks cover missing-only restoration, unchanged existing records/priorities, restrictions, rollback, native synthetic persistence and action-cache preservation, with current Skill Cap 0.1.3 semantics and without that dependency. Synthetic native tests use a private managed substitute for Unity's native string-hash primitive and directly invoke the compiled new-slot setter integration; they do not exercise actual Harmony, the native interface or Skill Cap's full save component.

The build maps debugger document paths to portable paths instead of retaining private local build folders. The per-mind service/default/rollback and native refresh method bodies are compared with 2.1.1; batch orchestration and selection validation are reviewed separately. Recovery does not reset existing values or override native load rules or other mods' normal UI/save processing. Repeated native job-matrix redraws can affect large-batch responsiveness; no in-game performance measurement is claimed.

## Saves and rollback

Save separately before using recovery. Added jobs are saved normally by Core and optional Skill Cap Mod; removing the DLL or reverting configuration does **not** undo jobs already saved. Restore an untouched pre-recovery save for exact rollback. Do not delete newly restored jobs after training them or changing their priorities as an assumed lossless reversal.

A precommit insertion failure removes only this operation's own new records. An incomplete cleanup is reported explicitly. After structural success, a refresh failure is reported as **jobs restored, refresh failed**; committed records are retained because native observers may already have seen them. Existing player orders, reservations, inventory, roles/traits, species and ownership are not rewritten.

## Source and build

`Code/RecoveryService.cs` owns the narrow missing-only planner/transaction. `Code/RecoveryTarget.cs` checks native state/identity and refreshes the actual owning body. `Code/RecoveryBatch.cs` adapts native selection/eligibility to the transient coordinator in `Code/BatchRecovery.cs`; `Code/RecoveryResult.cs` shares the unchanged per-mind result types. `Code/OrbotronRecoveryAction.cs` registers the user-triggered native action. `Code/AssemblyInfo.cs` contains explicit 2.2.0 assembly/file/informational metadata. `Tests/` contains the portable source-linked harness and is not runtime payload.

The runtime payload includes the DLL, definitions and public documentation. Maintained Code sources, project and build script are in the repository; legacy source/build files in a live installation are not selected by the runtime installer.

`mod.csproj` targets net481/x64 against the installed Stardeus Managed assemblies with non-copying references. `Build.ps1` invokes the installed .NET SDK compiler with the same explicit references without NuGet restore, emitting only `Libraries/OrbotronUpgrade.dll` with embedded debugging. Both build routes map source paths to `/_/OrbotronUpgrade`; local account/build paths are not retained in release symbols. No Core, Unity, Skill Cap, Harmony or framework DLL is bundled. Build caches, tests, probes, backups and diagnostic evidence belong outside this mod directory. Build only while Stardeus is closed; the helper refuses a running game and never starts or stops it.

## Changes

2.2.0 adds one multi-selection recovery confirmation while preserving the existing per-mind recovery behavior on rc344.3226. Existing data upgrades, author attribution, Workshop identity, current native arrays/curves and spawn operation are preserved. Saved minds change only after explicit recovery confirmation. See CHANGELOG.md for release history.
