# Stardeus log triage

`Analyze-ModLog.ps1` reads explicitly selected logs, retains their exact captured bytes privately, and groups recognized diagnostic headers with source line numbers and bounded context. Requires Windows PowerShell 7.2+. No downloads, game assemblies or other installation are needed. It never changes input logs, mods, settings or saves, launches/stops the game, or publishes.

Copy `LogAnalyzer.profile.example.json` to private storage and replace every placeholder with verified absolute paths. The strict profile requires every documented key and rejects unknown or duplicate keys. Set `Subject.ModId` and `Subject.Root` to caller-selected scope; these do not attribute an issue to that mod. Select `LogRoot` and relative `LogFile` explicitly. No newest-log selection occurs. Outputs must be private, outside the checkout, Steam/game/player trees and all protected/input roots. Reparse points, traversal and output overlap are refused.

```powershell
$tool = Join-Path $RepoRoot 'build/ModMaintenance/Analyze-ModLog.ps1'
$result = & $tool -ProfilePath $PrivateProfile
$result
# Exact previous input is optional and must differ, within the configured LogRoot.
$result = & $tool -ProfilePath $PrivateProfile -PreviousLogPath $ExplicitPreviousLog
```

Use a fresh PowerShell process after helper changes. Errors return nonzero with `pwsh -NoProfile -File`; recognized diagnostics and evidence gaps are successful analyses whose results must be inspected.

## Native sessions and clocks

`Format: "Stardeus"` recognizes Unity `Initialize engine version:` as a session boundary and analyzes only the last recognized session, retaining all session boundaries and original source lines. More than 100 recognized sessions per input is refused. Earlier diagnostics and context are excluded. An incomplete last session is never replaced by an earlier session. Without a boundary the entire input is analyzed with unknown session attribution. The engine version is not the Stardeus version.

Native `[elapsed minutes m|game days d|ticks]` prefixes are stripped for grouping. `Stardeus version: v...` and `Stardeus Version: v... [build date]` are game version claims; build dates are retained separately. Elapsed minutes, game days and ticks provide no wall-clock startup time. Therefore these native logs have `Freshness: Unknown` even when a game version matches supplied expectations. Modification time never establishes freshness. Conflicting versions and version mismatch remain explicit states; `NotBeforeUtc` requires an explicit UTC/offset, describes the caller's current test boundary, and does not manufacture missing clock evidence.

`Format: "Auxiliary"` recognizes LocalLog/SessionLog timestamps `yyyy-MM-dd HH:mm:ss.fff`. Supply a verified .NET timezone ID or null. Invalid, ambiguous DST and missing timezone interpretations remain unknown. Auxiliary logger `started version=...` values are not game versions. Auxiliary sources have unknown game-session association, and cannot establish native freshness.

Optional `AuxiliaryInputs` is an explicit array of at most eight objects, each containing exactly `SourceLabel`, `LogRoot`, `LogFile`, and `TimestampTimeZoneId`. Each is analyzed separately, retained and hashed; no automatic joining or causal timeline is inferred. Duplicate sources are refused. Logs and reports can contain private paths, player names, inventories, hardware and settings. Never commit or upload them automatically.

## Findings and limits

Recognized patterns include explicit ERROR/WARN/FATAL levels, exception headers plus supported stack frames, compiler diagnostics, and selected leading load/parse/file failures. Settings containing Warning are not diagnostics merely because of that word. Fixed patterns can miss unfamiliar failure wording. Group identity preserves message values, paths, resource IDs and first stack frames. Nested exceptions may be separate headers; occurrence counts are header counts, not incident counts. Attribution remains unassigned.

Bounds cover bytes, lines, individual line length, groups, examples, occurrence locations, context and stack lines. Profiles are limited to 4 MiB and SHA256-pinned with a final change check. Tool and Common hashes are pinned before analysis and rechecked before output. `MaxAggregateInputBytes` limits all selected captures together to at most 256 MiB. `MaxAnalysisLineChars` defaults to 4096; longer lines are truncated only for analysis with an explicit gap, while captured source bytes remain intact. `MaxOutputTextChars` provides a shared 4 MiB default budget for retained diagnostic messages, signature frames, observations and context across all sources; exceeding it refuses the run. Structural JSON and duplicated issue/comparison views add bounded overhead. Oversized inputs or too many groups fail rather than silently tail-scan. Capped locations/examples/stacks retain exact header counts and record gaps. A two-pass hash/length/write-time capture check marks concurrent changes; saved bytes are the exact analyzed capture, not proof that a running log remained unchanged afterward.

`analysis.json` (schema 1, `Kind: StardeusLogAnalysis`) contains `AnalysisId`, `CreatedUtc`, `State`, caller `Subject`, uniform `Inputs` footprints, tool/Common/profile hashes, `SupportInputs` producer/Common file footprints, effective `Configuration`, `Current`, optional `Previous`, independent `Auxiliary`, presence/count `Comparison` and `Gaps`. Each analysis contains `Sessions`, `SelectedSession`, `Freshness`, `Groups` (also `Issues` for the predecessor structure), counts and numbered context. `summary.json` is a compact index with the full report path and SHA256. Pin the full report, never the summary alone.

Comparison states are NewInComparedInput, RepeatedInComparedInput and NotObservedInCurrentInput. Not observed does not mean resolved; scenario, duration, configuration and builds may differ. The current adapter does not recognize build commits and refuses `ExpectedBuildCommit`.

NoRecognizedIssues means only no pattern matched. Neither it, matching version headers, nor a completed analysis establishes successful startup, tested artifact identity, gameplay, save safety, user acceptance or publication. Keep these stages separate in validation records.

## Isolated regression suite

```powershell
pwsh -NoProfile -File build/ModMaintenance/Tests/Test-LogAnalyzer.ps1
```

The suite creates private temporary logs and outputs, never reads player logs or starts Stardeus, and retains a receipt with exact helper/Common/test hashes. Run it after tool changes or a concrete format concern, not for ordinary maintenance. The maintained implementation reuses predecessor evidence mechanics but never imports from the Cosmoteer installation.
