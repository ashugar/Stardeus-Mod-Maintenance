# Repository privacy checker

This dependency-free .NET 10 utility checks Git-tracked content, including hidden project files, for hardcoded machine paths. Findings contain repository-relative filenames and categories, without matched private values. It checks decoded text and JSON strings, PE CodeView data, embedded Portable PDB document names, and structured PNG text metadata. It does not inspect visible text in image pixels or rewrite Git history. Existing author credits and compatibility identifiers are not path findings.

Build into a private temporary directory from the repository root:

```powershell
$RepoRoot = (git rev-parse --show-toplevel).Trim()
$ToolBuild = Join-Path ([IO.Path]::GetTempPath()) 'StardeusRepositoryPrivacy'
dotnet build build/RepositoryPrivacy/RepositoryPrivacy.csproj -c Release `
    "-p:BaseOutputPath=$ToolBuild/bin/" `
    "-p:BaseIntermediateOutputPath=$ToolBuild/obj/"
$Checker = Join-Path $ToolBuild 'bin/Release/net10.0/RepositoryPrivacy.dll'
dotnet $Checker self-test
dotnet $Checker scan --repo $RepoRoot
```

`scan` returns 0 for a clean scan, 1 for findings, and 2 for an operation failure. Invalid or unsupported artifact structures are reported as findings. UTF-8 and UTF-16 text are supported. PNG text is decompressed only within structured text chunks; compressed image bytes are not treated as text. Embedded PDBs are decompressed and parsed rather than searched as compressed bytes.

The explicit `sanitize` mode modifies only tracked DLL/EXE debug provenance and offending PNG `File` text chunks. It does not edit textual source/configuration paths. It refuses signed or ambiguous artifacts, derives and validates every allowed PE byte range, rejects overlap with method bodies and other protected data, and stages and verifies all candidates before replacement. Unchanged clean artifacts are left byte-for-byte intact. Removing current DLL symbols loses source-file/line debugging while preserving executable bytes, metadata, identities, and resources. No game process is started or stopped. Live mod deployment folders are refused.

```powershell
# Review scan findings and preserve the original Git revision before mutation.
dotnet $Checker sanitize --repo $RepoRoot
dotnet $Checker verify --repo $RepoRoot --baseline-ref $OriginalCommit
```

`verify` recomputes permitted changes from the specified original Git commit. It requires exact equality to that transformation, checks unchanged PE bytes outside debug spans, metadata/MVID and method-body hashes, retained PNG chunks and ordered IDAT data, image dimensions/type, and idempotence. Preserving every nonremoved PNG chunk proves the decoder inputs and pixels unchanged; an independent image-decoding check can provide an additional cross-check.

All modes accept optional `--evidence $PrivateReportPath`, which must be outside the repository. JSON evidence includes relative paths, categories, hashes, and permitted byte spans, never matched private values. Sanitation and verification return 2 on refusal/failure. Existing runtime compatibility is not re-certified by these preservation checks.
