# Corpse disposal regression harness

This portable .NET 10 harness compiles the production corpse policy, registry,
persistence and safety-hook code against controlled models of the inspected
Core contracts. It checks admission refusals, welded and mind slots, exact
ownership, save restoration, callback races, native result preservation,
manual-work isolation and incomplete-hook startup protection.

Run from this directory, keeping generated files in a private temporary folder:

```powershell
$TestArtifacts = Join-Path ([IO.Path]::GetTempPath()) 'IntelligentRecycling-CorpseTests'
dotnet build .\CorpseDisposal.Tests.csproj -c Release --artifacts-path $TestArtifacts
dotnet (Join-Path $TestArtifacts 'bin/CorpseDisposal.Tests/release/CorpseDisposal.Tests.dll')
```

No game assemblies, saved games or running game are used. Passing these checks
does not establish Harmony installation, native movement or cancellation
cleanup, in-game save/load, processor outputs, or gameplay acceptance.
