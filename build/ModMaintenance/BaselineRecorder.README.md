# Stardeus baseline recorder

`Record-ModBaseline.ps1` records scoped game/mod evidence without changing inputs, loading assemblies, controlling game processes, installing, publishing, or changing settings/saves. Requires Windows and PowerShell 7.2+. Reports and profiles are private: they contain local paths and process IDs.

Copy `BaselineRecorder.profile.example.json` to private storage and verify every root. Set stable `BaselineId`, explicit file/tree selections and dependency keys. Add relevant Core/framework/reference roots as separate mod entries. Missing explicit files produce `RecordedWithGaps`; missing trees and wrong selected manifest identities refuse capture. Empty arrays must remain JSON arrays. Core manifests without release-version fields retain explicit gaps rather than fabricated versions.

From PowerShell, set `$tool` to this script's absolute path:

```powershell
$PrivateRoot = Read-Host 'Private folder containing reviewed profiles'
$first = & $tool -ProfilePath (Join-Path $PrivateRoot 'baseline-profile.json')
$next = & $tool -ProfilePath (Join-Path $PrivateRoot 'baseline-profile.json') -PreviousSnapshot $first.SnapshotPath
& $tool -Mode Compare -SnapshotPath $next.SnapshotPath -PreviousSnapshot $first.SnapshotPath -OutputRoot (Join-Path $PrivateRoot 'comparisons')
```

Each unique run retains `snapshot.json`, optional `comparison.json`, and `summary.json`. Read the returned summary first. SHA-256 values, sizes, selected binary file/product resources, tool identity, exact scope/hash, module MVID, warnings and limitations remain visible. Timestamp-only changes do not imply byte changes. Scope changes, added/removed/changed files and incomplete evidence have separate comparison states. Prior baselines are never overwritten or automatically selected.

`ModInfo.json` candidates within explicitly selected files/trees are hashed and strictly parsed for `Id`, `Name`, `ModVersion`, `GameVersion`, `Dependencies` and `DLLFiles`. Duplicate/case-colliding JSON properties refuse a selected manifest/profile. The chosen manifest must be included in `Files` or selected `Trees` and must match `ExpectedId` exactly. There is no implicit recursive manifest discovery: selecting only root `ModInfo.json` does not enumerate the entire Core or mod tree. Dependencies are observed declarations plus caller-configured relationships; resolution, load order and compatibility are untested. Runtime selection and enabled state remain Unknown.

The example selects `Game.dll` for fresh hashing and metadata-only MVID inspection and also hashes `Game.Constants.dll` for build context. It intentionally leaves `VersionType`/`VersionField` null: installed Stardeus version/build values can originate from Unity resources and are not necessarily assembly constants. Full game build remains Unknown. A verified literal string constant may be configured explicitly; executable product/file versions are never substituted for the game release. Record additional verified version evidence separately; this tool does not parse Unity assets or execute `Resources.Load`.

Profiles/snapshots/manifests use strict bounded UTF-8 JSON (4 MiB each). Output must be separate from all input roots and outside the checkout, live player data, game roots and Steam directories; existing Common.ps1 guards apply. Absolute local drive paths are required. Traversal, unsafe components, links/reparse ancestors, excluded explicit selections and drive roots are refused. No network access or dependency discovery occurs.

This is not an atomic filesystem snapshot: pause external writers. Selected bytes are rehashed before saving, but a newly appearing file after inventory enumeration can escape that run; repeat capture after writers are idle. A hash or clean comparison establishes only the recorded scope, never source review, build, fresh startup, gameplay, save safety, user acceptance or publication. Reports can become stale immediately after inputs change. There is no cached compatibility approval.

Run focused synthetic fixtures outside repository/game/live storage:

```powershell
& 'ABSOLUTE_TOOL_FOLDER\Tests\Test-BaselineRecorder.ps1' -TestRoot (Join-Path $PrivateRoot 'baseline-tests')
```

Fixtures retain test results and script hashes and compile a tiny synthetic constant assembly. They never inspect or change real mods. Failures stop the suite; missing output does not mean a pass.
`ManifestFormat` defaults to `StrictJson`. Only an entry with `Role: Reference` and exact `ExpectedId: Core` may explicitly select `CoreJson`. This adapter accepts installed Core JSON comments and trailing commas, retains unrelated floating-point values during parsing, and still rejects duplicate/case-colliding properties recursively. It is not a general game configuration parser or a mod load test. The example includes a manifest-only Core reference; verify its actual installed root. Missing Core version fields remain gaps. Ordinary mod manifests remain strict JSON.

The example selects manifests only for mod roots. Add the mod's own DLL and relevant definitions/source trees explicitly when those bytes matter; do not include logs, settings, saves, test outputs or caches as stable evidence. No complete-tree scan is required.
