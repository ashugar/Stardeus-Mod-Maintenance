# Repository privacy checker

This dependency-free .NET 10 checker detects hardcoded machine paths and selected high-confidence secrets without renaming public author credits or compatibility identifiers. Git-object findings use categories and safe object/path identifiers, never matched private values. See [repository automation](../../AUTOMATION.md) for installation and PR workflow.

Build into private temporary output from the repository root:

```powershell
$RepoRoot = (git rev-parse --show-toplevel).Trim()
$ToolBuild = Join-Path ([IO.Path]::GetTempPath()) ('StardeusPrivacy-' + [Guid]::NewGuid().ToString('N'))
dotnet build build/RepositoryPrivacy/RepositoryPrivacy.csproj -c Release --artifacts-path $ToolBuild
$Checker = Join-Path $ToolBuild 'bin/RepositoryPrivacy/release/RepositoryPrivacy.dll'
dotnet $Checker self-test
dotnet $Checker self-test-git
dotnet $Checker scan-index --repo $RepoRoot
dotnet $Checker scan-history --repo $RepoRoot --ref (git rev-parse HEAD)
```

`scan-index` reads exact stage-zero index blobs. `scan-history --ref` accepts a full commit or annotated-tag object ID and scans its complete reachable graph. `pre-push --repo` reads Git's four-field ref-update records on standard input and scans every non-deletion proposed tip. No mode fetches unrelated refs. Incomplete/shallow history, missing objects, unsupported structures, and malformed input fail closed. Return codes are 0 clean, 1 findings, 2 operation failure/refusal. These modes are read-only.

Coverage includes UTF-8/UTF-16 and ASCII-compatible legacy single-byte text, decoded JSON strings, PE debug provenance and embedded Portable PDB document names, and structured/decompressed PNG text. Opaque containers and unreviewed binary content are refused by the Git-object policy. Existing binary exceptions are explicit reviewed hashes, not permission to upload arbitrary new opaque files. Policy changes need deliberate review. The tool does not perform OCR, inspect arbitrary steganographic payloads, or guarantee detection of all credentials/personal information. GitHub secret scanning and manual review remain useful independent controls.

The legacy `scan --repo` inspects tracked working files for machine paths and remains a diagnostic convenience; it is not the pre-push boundary. Its evidence includes relative filenames, so keep reports private. Optional `--evidence` on legacy scan/sanitize/verify must point outside the repository.

The explicit `sanitize` mode modifies only tracked DLL/EXE debug provenance and offending PNG `File` text chunks. It does not edit text source/configuration paths or rewrite Git history. It refuses signed or ambiguous artifacts, validates every allowed PE byte range, rejects overlap with method bodies/protected data, and validates all candidates before replacement. Unchanged artifacts remain byte-identical. It is never run by hooks or CI. Live mod deployment folders are refused.

```powershell
# Review findings and preserve an original revision before any authorized mutation.
dotnet $Checker sanitize --repo $RepoRoot
dotnet $Checker verify --repo $RepoRoot --baseline-ref $OriginalCommit
```

`verify` recomputes the permitted transformation and checks exact equality, unchanged PE bytes outside allowed spans, CLR metadata/MVID and method-body hashes, retained PNG chunks and ordered image data, dimensions/type, and idempotence. Symbol sanitation loses source-file/line debugging while preserving executable bytes, identities, resources, and pixels. These preservation checks do not re-certify runtime compatibility.