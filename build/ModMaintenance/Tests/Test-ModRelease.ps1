#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$toolRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $toolRoot '../..'))
. (Join-Path $toolRoot 'Common.ps1')
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('StardeusModReleaseTests-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixtureRoot)
$repo = Join-Path $fixtureRoot 'repository'
$null = [IO.Directory]::CreateDirectory($repo)
$script:assertions = 0
function Assert-Test([bool]$Condition, [string]$Name) { if (-not $Condition) { throw ('test-failed-' + $Name) }; $script:assertions++ }
function Assert-Refused([scriptblock]$Operation, [string]$Name) {
    $refused = $false
    try { $null = & $Operation } catch { $refused = $true }
    Assert-Test $refused $Name
}
function Write-Fixture([string]$Relative, [string]$Text) {
    $path = Join-Path $repo $Relative
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Text, $script:ModUtf8)
}
function Commit-Fixture([string]$Message) {
    $null = Invoke-ModGitBytes $repo @('add', '--all')
    $null = Invoke-ModGitBytes $repo @('-c', 'commit.gpgsign=false', 'commit', '-m', $Message)
    (Get-ModGitText $repo @('rev-parse', 'HEAD')).Trim()
}
function Write-Recipes($Value) { Write-Fixture 'build/ModMaintenance/ModRecipes.json' (($Value | ConvertTo-Json -Depth 15) + "`n") }
function Invoke-Package([string]$Sha, [string]$Out) {
    $null = Invoke-ModProcess -Program 'pwsh' -Arguments @('-NoProfile', '-File', (Join-Path $repo 'build/ModMaintenance/New-ModRelease.ps1'), '-Mod', 'Fixture', '-ReviewedCommit', $Sha, '-OutputRoot', $Out, '-RepoRoot', $repo)
}

try {
    $null = Invoke-ModGitBytes $repo @('init', '--initial-branch=main')
    $null = Invoke-ModGitBytes $repo @('config', 'user.name', 'Release Fixture')
    $null = Invoke-ModGitBytes $repo @('config', 'user.email', 'fixture@example.invalid')
    $null = Invoke-ModGitBytes $repo @('config', 'core.autocrlf', 'false')
    $null = Invoke-ModGitBytes $repo @('config', 'core.hooksPath', '.git/no-hooks')
    $null = Invoke-ModGitBytes $repo @('config', 'core.symlinks', 'false')
    $null = Invoke-ModGitBytes $repo @('remote', 'add', 'origin', 'https://github.com/ashugar/Stardeus-Mod-Maintenance.git')
    Write-Fixture 'LICENSE' 'Fixture license retained without changing terms.'
    foreach ($name in @('Common.ps1', 'New-ModRelease.ps1')) {
        $path = Join-Path $repo ('build/ModMaintenance/' + $name)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllBytes($path, [IO.File]::ReadAllBytes((Join-Path $toolRoot $name)))
    }
    foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'build/RepositoryPrivacy') -File | Where-Object { $_.Extension -cin @('.cs', '.csproj') })) {
        $path = Join-Path $repo ('build/RepositoryPrivacy/' + $file.Name)
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllBytes($path, [IO.File]::ReadAllBytes($file.FullName))
    }
    Write-Fixture 'Fixture/ModInfo.json' '{"Id":"Fixture.Public","Name":"Fixture Mod","ModVersion":"1.2.3","GameVersion":"0.15.0","DLLFiles":["Fixture.dll"],"Dependencies":["Core"]}'
    Write-Fixture 'Fixture/Settings.json' '{"Enabled":false}'
    Write-Fixture 'Fixture/README.md' '# Fixture release'
    Write-Fixture 'Fixture/SteamWorkshopItemId' '123456789'
    Write-Fixture 'Fixture/Definitions/Example.json' '{"Value":1}'
    Write-Fixture 'Fixture/.vscode/mod.csproj' '<Project><PropertyGroup><GenerateAssemblyInfo>false</GenerateAssemblyInfo><VersionPrefix>1.2.3.0</VersionPrefix></PropertyGroup></Project>'
    $bomProject = Join-Path $repo 'Fixture/.vscode/mod.csproj'
    [IO.File]::WriteAllBytes($bomProject, ([byte[]]@(0xef, 0xbb, 0xbf) + [IO.File]::ReadAllBytes($bomProject)))
    Write-Fixture 'Fixture/Code/AssemblyInfo.cs' @'
using System.Reflection;
[assembly: AssemblyVersion("1.2.3.0")]
[assembly: AssemblyFileVersion("1.2.3.0")]
[assembly: AssemblyInformationalVersion("1.2.3-test")]
'@
    Write-Fixture 'Fixture/Code/Version.cs' @'
class VersionHolder {
 public const string ModVersion = "1.2.3";
 public const string RequiredUtilitiesVersion = "9.0.0";
}
'@
    Write-Fixture 'Data(Folder)/ModInfo.json' '{"Id":"Data.Public","Name":"Data Fixture","ModVersion":"2.0.0","GameVersion":"0.15.0"}'
    Write-Fixture 'Data(Folder)/Definitions/Data.json' '{}'
    $dll = Join-Path $repo 'Fixture/Libraries/Fixture.dll'
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dll))
    $binaryRoot = Join-Path $fixtureRoot 'binary-source'
    $null = [IO.Directory]::CreateDirectory($binaryRoot)
    [IO.File]::WriteAllText((Join-Path $binaryRoot 'Fixture.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Fixture</AssemblyName><Version>1.2.3</Version><InformationalVersion>1.2.3-test</InformationalVersion><DebugType>none</DebugType></PropertyGroup></Project>', $script:ModUtf8)
    [IO.File]::WriteAllText((Join-Path $binaryRoot 'Example.cs'), 'public class ReleaseFixture { public static int Value() { return 1; } }', $script:ModUtf8)
    $neutralConfig = Join-Path $binaryRoot 'NuGet.Config'
    [IO.File]::WriteAllText($neutralConfig, '<configuration><packageSources><clear /></packageSources></configuration>', $script:ModUtf8)
    $binaryRun = Join-Path $fixtureRoot 'binary-run'
    $null = Invoke-ModProcess -Program 'dotnet' -Arguments @('build', (Join-Path $binaryRoot 'Fixture.csproj'), '--configuration', 'Release', '--output', $binaryRun, '-noAutoResponse', '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false', '-p:ImportDirectoryPackagesProps=false', ('-p:RestoreConfigFile=' + $neutralConfig)) -WorkingDirectory $binaryRoot
    [IO.File]::WriteAllBytes($dll, [IO.File]::ReadAllBytes((Join-Path $binaryRun 'Fixture.dll')))
    $recipes = [ordered]@{ SchemaVersion = 1; Mods = @(
        [ordered]@{Folder='Fixture';Id='Fixture.Public';Project='.vscode/mod.csproj';Dlls=@('Fixture.dll');IncludeFiles=@('ModInfo.json','README.md','Settings.json','SteamWorkshopItemId');IncludeDirectories=@('Definitions');CreateOnlyFiles=@('Settings.json');VersionSources=@(@{Path='Code/AssemblyInfo.cs';Kind='assembly-attributes';Name=''},@{Path='Code/Version.cs';Kind='constant';Name='ModVersion'})},
        [ordered]@{Folder='Data(Folder)';Id='Data.Public';Project=$null;Dlls=@();IncludeFiles=@('ModInfo.json');IncludeDirectories=@('Definitions');CreateOnlyFiles=@();VersionSources=@()}
    ) }
    Write-Recipes $recipes
    $sha = Commit-Fixture 'Initial fixture'
    $snapshot = Get-ModReleaseSnapshot $repo $sha 'Fixture'
    Assert-Test ($snapshot.Id -ceq 'Fixture.Public' -and $snapshot.Folder -ceq 'Fixture') 'distinct-id-and-folder'
    Assert-Test ($snapshot.Files.Count -eq 6) 'exact-payload-count'
    Assert-Test (@($snapshot.Files | Where-Object InstallPolicy -CEQ 'create-only').Count -eq 1) 'settings-create-only'
    Assert-Test ($snapshot.WorkshopItemId -ceq '123456789') 'workshop-identity'
    Assert-Test (@($snapshot.VersionEvidence | Where-Object { $_.Value -ceq '1.2.3-test' }).Count -eq 2) 'informational-suffix'
    Assert-Test (@($snapshot.VersionEvidence | Where-Object { $_.Value -ceq '9.0.0' }).Count -eq 0) 'dependency-version-distinct'
    Assert-Test (@($snapshot.VersionEvidence | Where-Object { $_.Surface -ceq '.vscode/mod.csproj:InformationalVersion' -and $_.Status -ceq 'not-present' -and $null -eq $_.Value }).Count -eq 1) 'optional-project-property-omission-recorded'
    Assert-Test (@($snapshot.VersionEvidence | Where-Object { $_.Surface -ceq '.vscode/mod.csproj:VersionPrefix' -and $_.Status -ceq 'observed' -and $_.Value -ceq '1.2.3.0' }).Count -eq 1) 'utf8-bom-project-version-observed'
    Assert-Test (@($snapshot.Files | Where-Object RelativePath -CLike 'Code/*').Count -eq 0) 'source-excluded'
    $data = Get-ModReleaseSnapshot $repo $sha 'Data(Folder)'
    Assert-Test ($data.Files.Count -eq 2 -and $null -eq $data.WorkshopItemId) 'data-only-no-project-or-workshop'
    Assert-Test (@($data.VersionEvidence | Where-Object { $_.Surface -ceq 'ProductionProject' -and $_.Status -ceq 'no-project' -and $null -eq $_.Value }).Count -eq 1) 'data-only-project-omission-recorded'
    Write-Fixture 'Fixture/ignored-decoy.log' 'decoy'
    $withDecoy = Get-ModReleaseSnapshot $repo $sha 'Fixture'
    Assert-Test ($withDecoy.Files.Count -eq $snapshot.Files.Count) 'untracked-decoy-excluded'
    [IO.File]::Delete((Join-Path $repo 'Fixture/ignored-decoy.log'))
    $first = Join-Path $fixtureRoot 'release-one'
    $second = Join-Path $fixtureRoot 'release-two'
    Invoke-Package $sha $first
    Invoke-Package $sha $second
    Assert-Test ((Get-ModSha256 ([IO.File]::ReadAllBytes((Join-Path $first 'Fixture-1.2.3.zip')))) -ceq (Get-ModSha256 ([IO.File]::ReadAllBytes((Join-Path $second 'Fixture-1.2.3.zip'))))) 'deterministic-archive'
    Assert-Test ((Get-ModSha256 ([IO.File]::ReadAllBytes((Join-Path $first 'release.json')))) -ceq (Get-ModSha256 ([IO.File]::ReadAllBytes((Join-Path $second 'release.json'))))) 'deterministic-report'
    Assert-Test (-not [IO.File]::Exists((Join-Path $first 'INCOMPLETE.txt'))) 'successful-output-finalized'
    $report = Get-Content -LiteralPath (Join-Path $first 'release.json') -Raw | ConvertFrom-Json
    Assert-Test ($report.ReviewedCommit -ceq $sha -and $report.Files.Count -eq 6) 'report-pins-reviewed-payload'
    Assert-Test ((Get-ModSha256 ([IO.File]::ReadAllBytes((Join-Path $first 'REPOSITORY-LICENSE')))) -ceq (Get-ModSha256 ([IO.File]::ReadAllBytes((Join-Path $repo 'LICENSE'))))) 'repository-license-sidecar-parity'
    Assert-Test ((Get-Content -LiteralPath (Join-Path $first 'SHA256SUMS')).Count -eq 4) 'sidecar-checksum-coverage'
    foreach ($file in $snapshot.Files) { Assert-Test ((Get-ModSha256 ([IO.File]::ReadAllBytes((Join-Path $first ('upload/Fixture/' + $file.RelativePath))))) -ceq $file.Sha256) 'upload-parity' }
    Assert-Refused { Invoke-Package $sha $first } 'existing-output-refused'
    Assert-Refused { Invoke-Package $sha (Join-Path $repo 'release-output') } 'source-overlap-refused'
    Assert-Refused { Get-ModReleaseSnapshot $repo ('0' * 40) 'Fixture' } 'wrong-reviewed-sha'
    Assert-Refused { Get-ModReleaseSnapshot $repo $sha 'fixture' } 'folder-case-refused'
    $staleCommon = Join-Path $fixtureRoot 'stale-Common.ps1'
    [IO.File]::WriteAllText($staleCommon, ([IO.File]::ReadAllText((Join-Path $toolRoot 'Common.ps1')) + "`n# External stale fixture copy.`n"), $script:ModUtf8)
    $externalRunner = Join-Path $fixtureRoot 'Test-ExternalCommon.ps1'
    [IO.File]::WriteAllText($externalRunner, 'param([string]$Common,[string]$Root,[string]$Sha); $ErrorActionPreference="Stop"; . $Common; $null = Get-ModReleaseSnapshot -RepoRoot $Root -ReviewedCommit $Sha -Mod Fixture', $script:ModUtf8)
    Assert-Refused { Invoke-ModProcess -Program 'pwsh' -Arguments @('-NoProfile','-File',$externalRunner,'-Common',$staleCommon,'-Root',$repo,'-Sha',$sha) } 'external-stale-common-refused'
    foreach ($path in @('../other', '/absolute', 'one\two', 'one//two', 'a/CON.txt', 'a/COM¹.txt', 'trailing.', 'a:b', 'a/.git/config')) { Assert-Refused { Assert-ModSafeRelativePath $path } 'unsafe-path-refused' }
    Assert-Refused { ConvertFrom-ModStrictJson $script:ModUtf8.GetBytes('{"Id":"a","id":"b"}') } 'duplicate-json-key-refused'
    $unsafeTree = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $unsafeTree.Add('Fixture/README.md', [pscustomobject]@{Mode='120000';Type='blob';ObjectId=('0' * 40)})
    Assert-Refused { Get-ModBlob $repo $unsafeTree 'Fixture/README.md' } 'symlink-mode-refused'
    $unsafeTree['Fixture/README.md'] = [pscustomobject]@{Mode='160000';Type='commit';ObjectId=('0' * 40)}
    Assert-Refused { Get-ModBlob $repo $unsafeTree 'Fixture/README.md' } 'gitlink-mode-refused'
    $caseRepo = Join-Path $fixtureRoot 'case-repository'
    $null = [IO.Directory]::CreateDirectory($caseRepo)
    $null = Invoke-ModGitBytes $caseRepo @('init', '--initial-branch=main')
    $caseBlob = (Get-ModGitText $caseRepo @('hash-object', '-w', '--', (Join-Path $repo 'Fixture/README.md'))).Trim()
    $null = Invoke-ModGitBytes $caseRepo @('-c', 'core.ignorecase=false', 'update-index', '--add', '--cacheinfo', ('100644,' + $caseBlob + ',Case/one'))
    $null = Invoke-ModGitBytes $caseRepo @('-c', 'core.ignorecase=false', 'update-index', '--add', '--cacheinfo', ('100644,' + $caseBlob + ',case/two'))
    $null = Invoke-ModGitBytes $caseRepo @('-c', 'user.name=Release Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false', '-c', 'core.hooksPath=.git/no-hooks', 'commit', '-m', 'Case aliases fixture')
    $caseSha = (Get-ModGitText $caseRepo @('rev-parse', 'HEAD')).Trim()
    Assert-Refused { Get-ModCommitTree $caseRepo $caseSha } 'directory-case-alias-refused'
    $link = Join-Path $fixtureRoot 'linked-parent'
    $null = New-Item -ItemType $(if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }) -Path $link -Target $repo
    Assert-Refused { Assert-ModNoReparsePath (Join-Path $link 'missing-descendant') } 'reparse-ancestor-refused'
    $previousGameRoot = $env:STARDEUS_GAME_ROOT
    try {
        $env:STARDEUS_GAME_ROOT = Join-Path $fixtureRoot 'fake-game'
        Assert-Refused { Assert-ModExternalPath $repo (Join-Path $env:STARDEUS_GAME_ROOT 'output') } 'configured-game-output-refused'
    } finally { $env:STARDEUS_GAME_ROOT = $previousGameRoot }
    Write-Fixture 'Fixture/README.md' 'changed tracked bytes'
    Assert-Refused { Get-ModReleaseSnapshot $repo $sha 'Fixture' } 'dirty-tracked-tree-refused'
    Write-Fixture 'Fixture/README.md' '# Fixture release'
    $originalProject = [IO.File]::ReadAllBytes((Join-Path $repo 'Fixture/.vscode/mod.csproj'))
    Write-Fixture 'Fixture/.vscode/mod.csproj' '<Project><PropertyGroup><VersionPrefix>1.2.4</VersionPrefix></PropertyGroup></Project>'
    $bad = Commit-Fixture 'Fixture version mismatch'
    Assert-Refused { Get-ModReleaseSnapshot $repo $bad 'Fixture' } 'project-version-mismatch'
    [IO.File]::WriteAllBytes((Join-Path $repo 'Fixture/.vscode/mod.csproj'), $originalProject)
    $sha = Commit-Fixture 'Restore fixture project'
    $recipes.Mods[0].Extra = 'unknown'
    Write-Recipes $recipes
    $bad = Commit-Fixture 'Unknown recipe field'
    Assert-Refused { Get-ModReleaseSnapshot $repo $bad 'Fixture' } 'unknown-recipe-field'
    $recipes.Mods[0].Remove('Extra')
    Write-Recipes $recipes
    $sha = Commit-Fixture 'Restore recipe schema'
    $recipes.Mods[0].IncludeFiles += 'Definitions/Missing.json'
    Write-Recipes $recipes
    $bad = Commit-Fixture 'Missing selected payload'
    Assert-Refused { Get-ModReleaseSnapshot $repo $bad 'Fixture' } 'missing-payload-refused'
    $recipes.Mods[0].IncludeFiles = @($recipes.Mods[0].IncludeFiles | Where-Object { $_ -cne 'Definitions/Missing.json' })
    Write-Recipes $recipes
    $sha = Commit-Fixture 'Restore payload recipe'
    $recipes.Mods[0].Dlls += 'Game.dll'
    Write-Recipes $recipes
    $bad = Commit-Fixture 'Unexpected dependency selection'
    Assert-Refused { Get-ModReleaseSnapshot $repo $bad 'Fixture' } 'unexpected-dependency-dll-refused'
    $recipes.Mods[0].Dlls = @('Fixture.dll')
    Write-Recipes $recipes
    $sha = Commit-Fixture 'Restore own DLL selection'
    $recipes.Mods[0].Project = $null
    Write-Recipes $recipes
    $originalAttributes = [IO.File]::ReadAllBytes((Join-Path $repo 'Fixture/Code/AssemblyInfo.cs'))
    Write-Fixture 'Fixture/Code/AssemblyInfo.cs' "using System.Reflection;`n[assembly: AssemblyVersion(`"1.2.3.0`")]`n[assembly: AssemblyFileVersion(`"1.2.3.0`")]`n"
    $sha = Commit-Fixture 'Compiled mod without project or optional source metadata'
    $withoutProject = Get-ModReleaseSnapshot $repo $sha 'Fixture'
    Assert-Test (@($withoutProject.VersionEvidence | Where-Object { $_.Surface -ceq 'ProductionProject' -and $_.Status -ceq 'no-project' }).Count -eq 1) 'compiled-project-omission-recorded'
    Assert-Test (@($withoutProject.VersionEvidence | Where-Object { $_.Surface -ceq 'Code/AssemblyInfo.cs:AssemblyInformationalVersion' -and $_.Status -ceq 'not-present' -and $null -eq $_.Value }).Count -eq 1) 'optional-assembly-attribute-omission-recorded'
    Assert-Test (@($withoutProject.VersionEvidence | Where-Object { $_.Surface -ceq 'Libraries/Fixture.dll:ProductVersion' -and $_.Status -ceq 'observed' }).Count -eq 1) 'binary-observation-retained-with-source-omission'
    $recipes.Mods[0].Project = '.vscode/mod.csproj'
    Write-Recipes $recipes
    [IO.File]::WriteAllBytes((Join-Path $repo 'Fixture/Code/AssemblyInfo.cs'), $originalAttributes)
    $sha = Commit-Fixture 'Restore compiled fixture project and metadata'
    $null = Invoke-ModGitBytes $repo @('remote', 'set-url', 'origin', 'https://example.invalid/wrong')
    Assert-Refused { Get-ModReleaseSnapshot $repo $sha 'Fixture' } 'wrong-origin-refused'
    $null = Invoke-ModGitBytes $repo @('remote', 'set-url', 'origin', 'https://github.com/ashugar/Stardeus-Mod-Maintenance.git')
    $privatePath = ([char]67).ToString() + ':' + [char]92 + 'Users' + [char]92 + 'FixturePrivate' + [char]92 + 'file'
    Write-Fixture 'Fixture/README.md' $privatePath
    $bad = Commit-Fixture 'Private fixture payload'
    Assert-Refused { Get-ModReleaseSnapshot $repo $bad 'Fixture' } 'source-privacy-refused'
    Write-Fixture 'Fixture/README.md' '# Fixture release'
    $bad = Commit-Fixture 'Removed private fixture content'
    Assert-Refused { Get-ModReleaseSnapshot $repo $bad 'Fixture' } 'earlier-private-history-still-refused'
    [Console]::WriteLine('PASS mod release fixtures: ' + $script:assertions + ' assertions; no real installation or game execution.')
    exit 0
} catch {
    Write-Verbose $_.Exception.Message
    Write-Verbose $_.ScriptStackTrace
    $category = $_.Exception.Message
    if ($category -cnotmatch '^[a-z][a-z0-9-]+$') { $category = 'mod-release-fixture-failed' }
    [Console]::Error.WriteLine('ERROR ' + $category)
    exit 1
}
