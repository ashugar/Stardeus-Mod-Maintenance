# Release preparation and local installation

Use a clean repository checkout, Git, PowerShell 7 and the .NET 10 SDK. Independently review the exact full commit before using it as `ReviewedCommit`. Release and installation read committed Git blobs through the same explicit per-mod recipe. They do not rebuild mods, download game dependencies, start or stop Stardeus, or publish to Workshop.

## Prepare one mod

Choose an existing private parent directory outside the checkout, live game data, game installation and Steam libraries. The release output directory itself must be new:

```powershell
$ReviewedCommit = (git rev-parse HEAD).Trim()
$PrivateRoot = Read-Host 'Existing private folder for prepared releases'
$ReleaseOutput = Join-Path $PrivateRoot 'QuantumStorage-prepared'
pwsh -NoProfile -File build/ModMaintenance/New-ModRelease.ps1 `
    -Mod QuantumStorage -ReviewedCommit $ReviewedCommit -OutputRoot $ReleaseOutput
```

The result contains `upload/<mod folder>/`, a ZIP of that exact tree, `release.json`, `RELEASE-NOTES.md`, `SHA256SUMS`, and the applicable repository license sidecar. The receipt records source commit, recipe hash, payload hashes, install policies and observed version surfaces. Preparation reopens the ZIP and checks its entries against the reviewed payload. An `INCOMPLETE.txt` marker means preparation failed; retain it for diagnosis and prepare a new output after correcting the cause.

The source privacy guard scans complete selected history before preparation. Generated sidecars contain constrained facts from that scanned revision and fixed text; this is not a separate arbitrary-archive privacy certification. The ZIP and reports belong outside tracked source. Package preparation validates curated committed DLLs and known source versions; it performs no rebuild, fresh startup or gameplay test. New mod binaries still need their applicable game-dependent build and acceptance checks before they are reviewed for distribution.

The GitHub **Prepare mod release** workflow offers the same preparation from `main`. Select the exact mod folder and independently reviewed full main SHA matching the workflow revision. It uploads prepared files for 14 days after all preparation checks pass. It uses read-only permissions and GitHub-hosted runners. Download the artifact and inspect the receipt/notes/checksums before using the upload folder; Workshop publication remains a separate authorized action. If `main` advances before dispatch, review the newly selected revision or run the already reviewed revision locally.

## Plan a local installation

Installation is an explicit Windows operation. Keep Stardeus closed throughout planning, application and recovery. The helper refuses when the game is running or its process state cannot be established. It never launches or terminates the game; another process can still start it after a check, so coordinate the closed period locally.

Resolve the intended player's profile through the OS. Keep the same private rollback root for later updates so its receipts can detect formerly managed files that require separately reviewed cleanup:

```powershell
$ProfileRoot = [Environment]::GetFolderPath('UserProfile')
$UserRoot = if ($env:STARDEUS_USER_ROOT) { $env:STARDEUS_USER_ROOT } else {
    Join-Path $ProfileRoot 'AppData/LocalLow/Kodo Linija/Stardeus'
}
$ModsRoot = Join-Path $UserRoot 'Mods'
$RollbackRoot = Read-Host 'Stable private rollback folder outside checkout and live game data'
$PlanPath = Join-Path $RollbackRoot 'QuantumStorage-plan.json'
pwsh -NoProfile -File build/ModMaintenance/Install-Mod.ps1 -Prepare `
    -Mod QuantumStorage -ReviewedCommit $ReviewedCommit -ModsRoot $ModsRoot `
    -RollbackRoot $RollbackRoot -PlanPath $PlanPath
```

Create the private rollback/plan parent directory first; use a new plan filename. Preparation writes a private plan and reports its SHA-256. It leaves destination files unchanged. Inspect the plan's source commit, intended mod identity, destination, before/after hashes and every action. It can explicitly plan a fresh installation when the target mod folder is absent. An existing folder must have the matching mod identity and compatible Workshop ID. Unexpected DLLs and removed previously managed payloads require reconciliation rather than blind copying.

## Apply the reviewed plan

After reviewing the exact plan bytes, use their reported digest for the one-command installation:

```powershell
$PlanSha256 = Read-Host 'SHA-256 of the exact plan just reviewed'
pwsh -NoProfile -File build/ModMaintenance/Install-Mod.ps1 -Apply `
    -ReviewedCommit $ReviewedCommit -PlanPath $PlanPath -PlanSha256 $PlanSha256
```

The helper recreates the plan from the same reviewed Git snapshot and current destination. Any changed source, plan, identity or preimage stops application. It verifies all rollback copies before replacing the first target file, then copies and checks only the planned files. Existing `LocalLogMod/Settings.json` and `OptimizationMod/Diagnostics.json` are preserved; absent defaults are seeded. Other unrelated files remain intact. No folder mirror or deletion synchronization runs.

Successful application reports its private transaction/journal location. Retain the plan digest and rollback records. Only file installation has been verified: fresh startup and relevant gameplay checks remain separate, and save migrations cannot be reversed merely by restoring an older DLL.

## Recover an interrupted or failed installation

If application fails, it attempts guarded recovery while the game remains closed. A persisted journal records incomplete work. For a retained transaction, inspect the original plan and journal, then run:

```powershell
$JournalPath = Read-Host 'Private transaction journal.json path'
pwsh -NoProfile -File build/ModMaintenance/Install-Mod.ps1 -Recover `
    -JournalPath $JournalPath -PlanSha256 $PlanSha256
```

Recovery verifies the copied plan, journal, backups and current file hashes. It restores only this transaction's files that still match its written bytes, and removes only new files created by that transaction when their bytes still match. Later edits are preserved and reported as conflicts. Keep backups and journals if recovery is incomplete; reconcile those files deliberately. An OS or filesystem failure can still require manual recovery. The original checkout directory must remain available for path validation; recovery does not require its old commit to remain checked out.

## Maintain recipes and catalog

`ModRecipes.json` explicitly records each mod's public folder/identity, runtime directories, own DLL, selected root documentation/artwork, create-only defaults, production project and narrowly named release-version sources. New files do not become release payload merely by appearing in a directory. Update and review the recipe when adding runtime roots, DLLs, product images or version declarations. Keep game/framework DLLs external to consumer packages. Data-only mods and the existing mod without a project have explicit coverage rather than fabricated build claims.

After changing manifests or Workshop IDs, stage new/removed input files and run `Update-ModCatalog.ps1`; commit its `MODS.md` and `.github/mod-labels.json` outputs. CI checks the exact generated bytes and runs portable release fixtures plus Windows-only installation fixtures against temporary fake mod roots. These tests never install into the real live mods.
