# Version-consistency checker

`Test-ModVersions.ps1` compares explicitly configured version fields with literal expectations in independent groups. It supports Stardeus manifests, JSON, literal project XML, targeted documentation markers, C# assembly attributes, built managed metadata, Windows version resources, and selected ZIP entries. Inputs remain read-only. It does not discover every version, choose release numbers, edit metadata, build, extract, deploy, load game/mod code, or publish.

Requires Windows and **PowerShell 7.2+** (`pwsh`), without extra packages or network access. Small trusted in-memory helpers tokenize declarations and read PE/CLI metadata. Compiled assemblies are **not loaded**, and custom attribute constructors are not instantiated. Start a fresh PowerShell process after updating the tool so previously loaded helpers do not persist. Changes to these shared helpers require focused tooling review and relevant regression tests.

## Routine agent workflow

1. Read applicable project/mod guidance. Identify the exact maintained/staged/package/live artifacts relevant to the task; folder access and consistency results do not grant mutation/deployment authority. Use `VersionChecker.profile.example.json` in an authorized private work area.
2. Configure Roots, Groups and Surfaces explicitly. Each group has a simple Key and a literal nonempty Expected string. Use separate groups for release version, mod identity, intentionally fixed assembly versions, APIs, save schemas, dependencies and game compatibility claims. **Do not force independent versions to the release number.** The checker never selects the highest/newest version or derives intended policy from version differences.
3. Each surface has Key, Group, RootKey, relative Path, Storage (`File` or `ZipEntry`), Type, Required boolean, and Normalization. ZIP selections also require exact forward-slash Entry. At least one required surface is mandatory. Only list applicable surfaces: data-only mods do not need invented DLL/build checks. A working-stage profile can omit package/live surfaces not yet relevant; absence from scope is not evidence they were checked.
4. Use precise fields/patterns. Documentation should have an explicit current-version marker, or a reviewed pattern anchored to the intended current section—not a broad search matching old release history. The template expects `Current version: 1.2.3`; adapt the profile to actual maintained documentation rather than adding/changing documentation just to satisfy the template. Duplicate declarations/matches are ambiguous even when their values agree.
5. For compiled mods, inspect the **actual built DLL** using AssemblyMetadata and, where needed, WindowsVersionResource. Project VersionPrefix and explicit source attributes are declarations, not proof of built outputs. `GenerateAssemblyInfo=false`, imports, custom build properties and generated versions can keep those different. Preserve deliberate assembly-version policy and compare the right binary field/group. Source attributes/project files alone cannot establish what was compiled.
6. Set OutputRoot to authorized private storage separate from all Roots/ProtectedRoots, game-loadable mods and public repositories. Configure ProtectedRoots for the game and relevant live/reference mods. Shared work-area use still requires project administration authorization. Reports contain local paths and captured values; no redaction/upload is performed.
7. Run the checker. Review the summary, then versions.json mismatches, missing fields, ambiguities/unresolved cases, optional omissions and exact artifact hashes as needed. Use this evidence instead of repeating equivalent manual version searches. Correct versions only within an authorized implementation task; this tool never bumps or rewrites anything.
8. Use the existing [Stardeus release and installation tools](README.md) for reviewed committed payloads, ZIP verification and guarded installation. External ZIP certification is outside this helper. Consistent values do not prove matching bytes or correct behavior. Changes/rebuilds require a fresh check. Source review, build, runtime, user acceptance and publication remain separate statuses.

From PowerShell 7, set `$tool` to the absolute path of `Test-ModVersions.ps1`:

```powershell
$ErrorActionPreference = 'Stop'
$PrivateRoot = Read-Host 'Private folder containing reviewed profiles'
$result = & $tool -ProfilePath (Join-Path $PrivateRoot 'version-profile.json')
$result

# Inspect a failed/unresolved candidate without the final terminating error:
$inspection = & $tool -ProfilePath (Join-Path $PrivateRoot 'version-profile.json') -ReportOnly
Get-Content -LiteralPath $inspection.ReportPath -Raw
```

Failed and Unresolved results are written, then throw by default. With `pwsh -NoProfile -File ...` they return nonzero process status. Use terminating-error handling with `&`. ReportOnly is for inspection, not permission to proceed. Configuration, path/resource and input-change failures can stop before a completed report exists; missing output is not success.

## Surface adapters

| Type | Configuration and meaning |
|---|---|
| RulesScalar | `Field: "Version"` or `"ID"` (or another explicit scalar). Reads exactly one simple top-level assignment, ignoring comments/nested groups. This is a restricted metadata reader, not a .rules parser. Expressions, ambiguous declarations, unsupported syntax/escapes and non-scalar values cannot establish a value. |
| JsonScalar | `Pointer: "/ModVersion"` or a nested RFC-6901-style pointer such as `/metadata/versions/0`. Exact property names; duplicate selected properties are ambiguous. Strings/numbers are supported; objects/arrays/booleans/null are not version scalars. `~0`/`~1` escaping and array indices are supported. No comments/JSON extensions. |
| XmlProperty | `Field: "VersionPrefix"`, `AssemblyVersion`, etc. Reads exactly one element with that name under PropertyGroup, namespace-independent. Conditions and property/item expansions stay unresolved; imports/effective MSBuild values are not evaluated. DTD/external entities are prohibited. |
| TextPattern | `Pattern` must include named capture `(?<version>...)`. Multiline/culture-invariant, bounded timeout, exactly one match and one version capture. Stops after enough matches to establish ambiguity; does not scan/select an arbitrary latest version. |
| CSharpAttribute | `Field` is AssemblyVersion, AssemblyFileVersion or AssemblyInformationalVersion. Supports literal `[assembly: ...]` attributes, unqualified/System.Reflection-qualified names (with optional Attribute suffix), ordinary/verbatim strings and comments. Constants/expressions, conditional preprocessor constructs, aliases and unsupported C# forms are not evaluated. This reads declarations, not actual compiled values. |
| AssemblyMetadata | Same three Field names. AssemblyVersion comes from the assembly definition; file/informational strings come from assembly-level custom-attribute metadata. Reads PE bytes without loading the assembly or constructing attributes. Not valid for arbitrary native binaries or assembly modules lacking supported metadata. |
| WindowsVersionResource | `Field: "FileVersion"` or `"ProductVersion"`. Uses Windows FileVersionInfo on an existing file. Native resource values and managed attribute values are distinct selectable surfaces. Not supported directly inside a ZIP; no extraction fallback occurs. Empty/default values are not substituted for a game's own release version. |

For example, add a required built-DLL surface with Type AssemblyMetadata, Field AssemblyFileVersion, and Normalization NumericZeroPad in the Release group if that binary version should track `1.2.3` as `1.2.3.0`. Keep a deliberately frozen AssemblyVersion in its own group instead. InformationalVersion including a commit can use IgnoreBuildMetadata only when that policy is intentional.

The checker does not validate semantic version syntax in Exact mode. Literal labels, including date-shaped tags, remain strings. Source declarations such as auto-version patterns are not a substitute for checking concrete built metadata. A matching XML/source declaration does not establish effective build order, defaults, selected configuration or generated values.

## Explicit normalization

| Mode | Comparison |
|---|---|
| Exact | Ordinal, case-sensitive equality, with no implicit prefix/suffix stripping or numeric padding. |
| NumericZeroPad | One to four unsigned numeric components, normalized/padded to four. For example `1.2.3` equals `1.2.3.0`. No wildcard, prerelease or build suffix is accepted. Leading zeroes are normalized numerically because this policy was explicitly selected. |
| IgnoreBuildMetadata | Three-component semantic-version-shaped values only. Removes the `+...` build part and preserves prerelease text/casing. Thus `1.2.3+commit` can match `1.2.3`, but `1.2.3-beta+commit` does not become `1.2.3`. |

Normalization applies to both observed and expected values for that surface. Invalid normalization inputs are unresolved, not coerced or matched using a shortened prefix. It never changes source files or chooses a group's expected version.

## ZIP and I/O scope

ZipEntry storage reads the configured entry directly from a bounded in-memory standard ZIP, retaining archive and decompressed-entry SHA-256 separately. Missing entries fail when required. Duplicate/case-colliding selected entries and wrong entry casing stay unresolved. Standard single-volume, non-ZIP64 layout/counts and declared/actual entry byte/compression limits are checked before/while reading. Unsupported/unreadable/encrypted payloads cannot produce a clean version result.

This is **not the package checker**: unselected ZIP entries, local/central integrity details, complete payload parity, CRC coverage and privacy are not validated here. A version match is not approval to extract/install/publish the ZIP. Use the existing [release tooling](README.md) for its supported reviewed payload checks; external ZIP certification remains outside this helper. No archive is extracted to temporary or live directories. AssemblyMetadata can inspect a selected packaged DLL directly; WindowsVersionResource requires a regular file.

Regular text supports strict UTF-8 (with/without BOM) and BOM-marked UTF-16LE/BE. Binary/NUL or unsupported encoding stays unresolved. JSON profiles preserve string values rather than converting date-shaped versions into dates; duplicate/case-colliding profile properties are refused. Missing optional files/fields are explicitly OptionalMissing. Existing directories are not treated as absent optional files, and unsupported/ambiguous values are not silently excused by Required false.

All path arguments use standard absolute local drive paths, with contained forward-slash relative surface paths/ZIP entries. Traversal, drive/device/stream aliases, wildcards, control characters, trailing dots/spaces, drive roots, UNC/device paths and links in inspected ancestors are refused. Named roots/protected output checks are caller-configured safeguards, not an OS permission boundary or expanded chat authority.

## Results and reports

Each completed run creates a unique `version-check-UTC-GUID` folder:

- versions.json: tool/profile hashes, exact configuration/limits, each extraction status/value/expected value/normalization/line, input and entry hashes, unused groups, and scope limitations.
- summary.json: concise match/failure/unresolved/optional counts, compared/configured/unused group counts and report path; also returned to the caller. Written last; a directory without it may be an interrupted output write.

| State | Meaning |
|---|---|
| Consistent | Every configured surface was extracted and matched its explicit group/policy. |
| ConsistentWithOptionalOmissions | Required surfaces matched; explicitly optional files/fields were absent. Their absence remains visible. |
| Failed | At least one mismatch or missing required file/field. Other unresolved findings are still recorded. |
| Unresolved | No definitive mismatch/missing-required failure, but at least one ambiguous, computed, unsupported, timed-out or otherwise unestablished value remains. |

Consistency is confined to the configured fields. Unused groups are not presented as checked. A pass does not prove a mod/package contains every required file, that bytes match source/live, that the right runtime variant is enabled, or that build/gameplay/save safety/multiplayer/publication passed. Optional omissions do not establish facts about omitted files. Reports are private trusted local evidence, not signed records.

Defaults are 100 input files, 128 MiB/input, 256 MiB total input bytes, 4 MiB/text, 200,000 tokens/source, 10,000 standard ZIP metadata entries, 100 selected ZIP entries, 64 MiB/selected entry, 128 MiB total selected-entry bytes, 1000:1 maximum compression ratio, a 1000 ms regex timeout, and 500 surfaces. Limits have hard upper bounds. Input bytes are cached only within the run and rehashed/rechecked before saving; no approvals/hashes are reused across runs. Keep builders/editors/Steam and other writers idle. This is not an atomic snapshot, and later changes invalidate the evidence.

## Tests

```powershell
& 'ABSOLUTE_SCRIPT_FOLDER\Tests\Test-VersionChecker.ps1' `
    -TestRoot (Join-Path $PrivateRoot 'version-checker-tests')
```

The default is private temporary storage. Unique synthetic manifest/doc/project/assembly/ZIP fixtures are retained with test-results.json and exact script hashes. A compiled custom-attribute trap verifies metadata inspection does not instantiate user attributes. No live mods are changed. Junction tests require host permission; do not silently skip them. Re-run after tool changes or relevant failures/format concerns, not before every ordinary version check.

## Stardeus profile

The adjacent example checks `/Id` and `/ModVersion` in `ModInfo.json`. Copy it to private storage and supply the exact expected release, source root, output root, and relevant protected roots. `GameVersion` is a declared compatibility surface, not the installed game build. Keep it in its own group if checked. Select actual `Libraries/*.dll` metadata for compiled mods; add only the project/source/documentation surfaces applicable to that mod. Do not change mod files to satisfy an example.

The tool rejects duplicate/case-colliding JSON properties, including nested ones. Reports use `ModVersionConsistency` schema 1 and add the supporting `Common.ps1` hash. The helper uses the existing Stardeus private-output guard in addition to configured input/protected roots. Neither profiles nor generated reports belong in the checkout, live player data, game, or Steam libraries.

Run the regression suite only after a checker change or a relevant failure:

```powershell
pwsh -NoProfile -File build/ModMaintenance/Tests/Test-VersionChecker.ps1
```

Fixtures use private temporary roots and never build or load a real mod. The retained generic `RulesScalar` adapter is legacy capability; Stardeus profiles use `JsonScalar`. The scripts have no runtime dependency on Cosmoteer or its installation.
