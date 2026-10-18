# Building and local configuration

Build from this checkout or its Git worktrees. The installed game, Core, Workshop files, and live mod folders are read-only build inputs. Each mod keeps its existing project, compiler options, version, reference list, and `Libraries` output layout. Some projects and their source files live inside `.vscode`.

## Game and framework references

Set the game installation for the current PowerShell process without committing the local value:

```powershell
$env:STARDEUS_GAME_ROOT = Read-Host 'Full path to the local Stardeus installation'
dotnet build QuantumStorage/mod.csproj -c Release
```

The installation must contain `Stardeus_Data/Managed/Game.dll`. Shared `Directory.Build.props` derives the managed directory from that root. To supply the managed directory directly, set `STARDEUS_MANAGED_PATH` or pass `-p:StardeusManagedPath="$env:STARDEUS_MANAGED_PATH"`. A command-line property takes precedence over environment defaults. The older `GameManagedPath` property is also supported.

Auto Utilities Mod Framework references default to the checked-in sibling `AutoUtilitiesModFramework/Libraries/AutoUtilitiesModFramework.dll`. They remain non-copying references. For a deliberate alternative, use `STARDEUS_AUMF_DLL` or `-p:AutoUtilitiesModFrameworkPath=...`; existing `AumfDll`, `AumfPath`, and `InstalledAumfPath` overrides remain supported. A missing explicit path fails instead of selecting a live or Workshop copy. Prefer one override name per invocation.

Use an appropriate .NET SDK and the framework reference package required by the selected project. Some projects use an existing NuGet cache and clear remote sources in their own `NuGet.Config`; a missing cached package is a dependency/setup failure, not a reason to change the mod. The repository privacy tool targets .NET 10. Test harness target frameworks remain as declared in their individual projects.

Before building, verify that the installed game assemblies match the mod's intended compatibility baseline. Path configuration cannot repair removed game APIs or approve a changed Core fingerprint. Keep compatibility repairs separate from privacy cleanup and report existing source failures precisely.

## Isolated validation outputs

Ordinary mod builds write to the repository's corresponding `Libraries` folder. For validation that must preserve the curated DLL, redirect both compiled output and intermediate/restore files to a private directory. Give each project a separate output root:

```powershell
$ValidationRoot = Join-Path ([IO.Path]::GetTempPath()) ('stardeus-validation-' + [Guid]::NewGuid().ToString('N'))
$ValidationRoot = $ValidationRoot.Replace('\', '/')
dotnet build QuantumStorage/mod.csproj -c Release `
    "-p:OutputPath=$ValidationRoot/bin/" `
    "-p:BaseIntermediateOutputPath=$ValidationRoot/obj/" `
    "-p:MSBuildProjectExtensionsPath=$ValidationRoot/obj/"
```

The shared root `PathMap` uses the whole checkout so source compiled from outside a `.vscode` project directory also receives neutral `/_/Stardeus-Mod-Maintenance/` filenames in debug symbols. Explicit caller mappings and Orbotron's existing mapping are preserved. If intermediates or linked source are outside the checkout and will be distributed, include those roots in an explicit source mapping and scan the resulting artifact.

Pure numeric harnesses do not require a game path. The investor harness needs the configured managed directory for its existing Newtonsoft.Json reference. Planning Overhaul's nearer test props explicitly import the repository defaults and retain its isolated `.build` outputs.

## Development tools

Path-bearing PowerShell tools use `build/LocalPaths.ps1` and keep their existing explicit path parameters. Configure `STARDEUS_GAME_ROOT` or the specific managed/Core parameter before running a game-dependent audit. These helpers do not start or stop Stardeus or copy files to live installations.

- AUMF's pooled-input test runner discovers the installed SDK compiler. Its optional `ReferenceDirectory` can still point to framework reference assemblies; otherwise it uses the configured game-managed references.
- Binary audits accept an installed `Mono.Cecil.dll`. Use the existing Cecil parameter or `STARDEUS_CECIL_DLL`; the market audit can also discover an installed ILSpy tool copy under the resolved user profile. Do not hardcode a profile name or tool version.
- The investor Mono quote probe requires its own `pwsh` process. It resolves the native Mono library from the configured game root, or accepts `MonoRuntimePath`, and retains its isolated execution and optimizer options.
- Auto Factory's source-art tool requires `GeneratedRoot` explicitly; original private generation folders are not repository inputs. `CoreDevices` can be supplied or derived from the configured Core installation.

## Privacy and provenance

Keep real account names, PC/profile paths, credentials, local logs, screenshots of private data, and validation/backup locations out of new commits and PR text. Resolve user paths through the OS profile, as described in `AGENTS.md`. Preserve public repository identity, original credits, and established compatibility/patch-owner identifiers.

Current historical mod DLLs can have private debug-symbol provenance removed without rebuilding gameplay code. That operation must preserve executable bytes, CLR metadata, method bodies, resources, identities, versions, and file length. Some original binaries may then lack source line diagnostics. New builds use neutral source mapping. PNG sanitation removes only workstation paths in `File` text metadata while preserving image data and other metadata.

Use the [repository privacy tool](build/RepositoryPrivacy/README.md) to scan tracked files before publication and verify sanitation against its original Git baseline. Its findings must identify repository-relative files and categories without printing private values. Build caches, backups, and private evidence stay outside tracked content.

This workflow cleans current files. Earlier Git commits retain their original snapshots; rewriting history requires a separate explicit request. A successful privacy check does not establish new gameplay compatibility or replace per-mod runtime validation.
