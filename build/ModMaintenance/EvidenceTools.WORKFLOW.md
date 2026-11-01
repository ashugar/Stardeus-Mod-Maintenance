# Stardeus evidence helpers

These three local, read-only helpers replace repeated inventories, version searches and validation-table assembly. Use only those relevant to the task. Windows PowerShell 7.2+ (`pwsh`) is required; use a fresh PowerShell process after tool changes. No game dependencies, downloads or Cosmoteer installation are required. They do not build, install, launch/stop the game, publish, or change mods/settings/saves.

| Need | Helper | Review first | Full evidence |
| --- | --- | --- | --- |
| Record relevant source/live/game/dependency bytes or compare a saved baseline | [Record-ModBaseline.ps1](Record-ModBaseline.ps1) | [Baseline guide](BaselineRecorder.README.md) | `snapshot.json`; explicit optional `comparison.json` |
| Compare chosen version/identity surfaces with explicit expected groups | [Test-ModVersions.ps1](Test-ModVersions.ps1) | [Version guide](VersionChecker.README.md) | `versions.json` |
| Assemble exact pinned reports and attributed observations | [New-ModValidationRecord.ps1](New-ModValidationRecord.ps1) | [Validation guide](ValidationRecord.README.md) | `validation.md`, `validation.json` |

## Use in a maintenance chat

1. Establish authorized scope, exact roots and current game/dependency context under the root AGENTS instructions. Copy the needed example profiles into private storage outside the checkout, live player data, game and Steam libraries. Replace placeholders with verified paths and expectations; do not commit profiles or reports. Configure only relevant files/trees and dependencies. Broad whole-installation scans are not a routine prerequisite.
2. Capture a starting baseline when installation/source drift matters. Select the previous snapshot explicitly when comparing. Hash snapshots are evidence, not recoverable backups or compatibility approval. Preserve rollback copies separately for authorized changes. A missing/unknown full game build remains a gap; manifest compatibility and Windows file versions cannot substitute for it.
3. Run version checks on chosen candidate surfaces. Check actual DLL metadata for compiled mods, where applicable. Keep release, game requirement, framework/API, dependency, save schema and intentionally fixed assembly versions in separate expected groups. Source/project declarations do not prove a build occurred.
4. Review the compact returned object or `summary.json`, then inspect only relevant detailed findings. A summary file is an index, not full evidence. Do not repeat equivalent manual hashing/scans after a successful scoped tool result. Failures, optional omissions and unresolved coverage remain visible.
5. Assemble a validation record using exact full report paths, SHA256 pins, context IDs, task boundary and artifact bindings. Never select the newest report automatically. Starting baselines usually become Historical after edits; they do not satisfy current stages. Record current baseline evidence separately only when needed. Changing candidate/game/dependency bytes invalidates affected evidence; refreshing pins is not revalidation.
6. Baseline and version reports have interpreted adapters. Existing release receipts, installer plans/journals, build notes, logs and other reports can be pinned as **Attachment** evidence. Attachments are retained without schema interpretation and cannot automatically satisfy package, installation or runtime stages. Reported source review/build/startup/gameplay/save-load/acceptance/publication outcomes require explicit attribution and exact artifact support. Keep pending stages pending.

Example invocation from the repository checkout (profiles contain private absolute paths):

```powershell
$PrivateRoot = Read-Host 'Private folder containing reviewed profiles'
pwsh -NoProfile -File build/ModMaintenance/Record-ModBaseline.ps1 -ProfilePath (Join-Path $PrivateRoot 'baseline.json')
pwsh -NoProfile -File build/ModMaintenance/Test-ModVersions.ps1 -ProfilePath (Join-Path $PrivateRoot 'versions.json')
pwsh -NoProfile -File build/ModMaintenance/New-ModValidationRecord.ps1 -ProfilePath (Join-Path $PrivateRoot 'validation.json')
```

Run these sequentially only when all three apply. Copy exact output paths/pins into the validation profile after inspecting the preceding reports. `-RequireComplete` is an explicit gate for chosen required stages, not an instruction to invent completed checks. `-ReportOnly` retains failed/incomplete evidence for inspection; it grants no permission to proceed.

## Existing workflows remain authoritative

Use [release preparation and guarded installation](README.md), [repository automation](../../AUTOMATION.md), privacy guards, protected PR review, and the Stardeus release-audit skill where applicable. These helpers do not replace those checks or add deployment/publication authority. Existing release preparation verifies its reviewed recipe/ZIP; this version checker does not certify arbitrary ZIP packages. Fresh logs, startup, exercised gameplay, save/reload, user acceptance and publication remain distinct from static checks.

The log analyzer, reference checker and upstream comparator have not been ported. Unsupported report kinds are refused as interpreted evidence; retain them as explicitly unparsed attachments if useful. No Cosmoteer ownership/no-GitHub rules are imported.

## Tool maintenance and provenance

The three helpers reuse the scoped evidence mechanics of the existing Cosmoteer maintenance toolkit reviewed for this project. Maintained Stardeus copies live here and never import the originals. Baseline and validation have Stardeus adapters; the version engine retains its generic surface types. The historic predecessor assertion count is not evidence for these copies.

Run relevant isolated suites after a helper changes or a concrete failure/format concern arises. Normal mod maintenance does not rerun tooling suites. Tests create private temporary fixtures and retain result JSON with exact script hashes:

```powershell
pwsh -NoProfile -File build/ModMaintenance/Tests/Test-BaselineRecorder.ps1
pwsh -NoProfile -File build/ModMaintenance/Tests/Test-VersionChecker.ps1
pwsh -NoProfile -File build/ModMaintenance/Tests/Test-ValidationRecord.ps1
```

The validation suite exercises actual report handoffs and negative evidence. No fixture uses the real live mod tree or runs Stardeus. Static tool checks establish only their inspected scope, not mod compatibility, gameplay or save safety.
