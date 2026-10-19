#requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Catalog.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('stardeus-catalog-test-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixture)
$checks = 0
function Assert-CatalogTest([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Set-FixtureManifest([string]$Folder, [string]$Id, [string]$Name, [string]$Version) {
    $mod = Join-Path $fixture $Folder
    $null = [IO.Directory]::CreateDirectory($mod)
    $manifest = [ordered]@{Id=$Id;Name=$Name;ModVersion=$Version;GameVersion='0.15.0';Dependencies=@('External|dependency')}
    [IO.File]::WriteAllText((Join-Path $mod 'ModInfo.json'), ($manifest | ConvertTo-Json))
}
try {
    Set-FixtureManifest 'ZMod(v0.15Update)' 'DifferentId' "Name|[x]`nnext" '1.2.3'
    Set-FixtureManifest 'AMod&More' 'FirstId' 'First' '2.0.0'
    $workshopPath = Join-Path $fixture 'ZMod(v0.15Update)/SteamWorkshopItemId'
    [IO.File]::WriteAllText($workshopPath, '1234567890')
    [IO.File]::WriteAllText((Join-Path $fixture 'AMod&More/SteamWorkshopItemId'), '2345678901')
    & git -c core.hooksPath= -C $fixture init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'fixture git init failed' }
    & git -c core.hooksPath= -C $fixture add -- .
    if ($LASTEXITCODE -ne 0) { throw 'fixture git add failed' }
    $trackedPaths = @('AMod&More/ModInfo.json', 'AMod&More/SteamWorkshopItemId', 'ZMod(v0.15Update)/ModInfo.json', 'ZMod(v0.15Update)/SteamWorkshopItemId')
    $generated = Get-ModCatalogArtifacts $fixture $trackedPaths
    Assert-CatalogTest ($generated.Count -eq 2) 'wrong mod count'
    Assert-CatalogTest ($generated.Catalog.Contains('Name&#124;&#91;x&#93; next')) 'unsafe Markdown'
    Assert-CatalogTest ($generated.Catalog.Contains('ZMod%28v0.15Update%29/')) 'folder link not encoded'
    Assert-CatalogTest ($generated.Catalog.Contains('External&#124;dependency')) 'unsafe dependency cell'
    Assert-CatalogTest ($generated.Catalog.Contains('?id=1234567890')) 'wrong Workshop ID source'
    Assert-CatalogTest ($generated.Catalog.IndexOf('[First]') -lt $generated.Catalog.IndexOf('Name&#124;')) 'nondeterministic order'
    $labels = $generated.Labels | ConvertFrom-Json -AsHashtable
    Assert-CatalogTest ($labels.Mods[1].Label -ceq 'mod:ZMod(v0.15Update)') 'folder identity not preserved'
    Assert-CatalogTest ($labels.Mods[1].Folder -ceq 'ZMod(v0.15Update)') 'literal folder mapping lost'
    $updater = Join-Path $PSScriptRoot '../Update-ModCatalog.ps1'
    & $updater -RepoRoot $fixture
    Assert-CatalogTest ([IO.File]::ReadAllText((Join-Path $fixture 'MODS.md')) -ceq $generated.Catalog) 'write differs from pure output'
    $global:LASTEXITCODE = 37
    & $updater -RepoRoot $fixture -Check
    Assert-CatalogTest ($LASTEXITCODE -eq 0) 'fresh check did not reset inherited native failure'
    $catalogPath = Join-Path $fixture 'MODS.md'
    [IO.File]::WriteAllText($catalogPath, $generated.Catalog, [Text.UTF8Encoding]::new($true))
    $bomStart = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $bomStart.UseShellExecute=$false; $bomStart.RedirectStandardOutput=$true; $bomStart.RedirectStandardError=$true
    foreach ($argument in @('-NoProfile','-File',$updater,'-RepoRoot',$fixture,'-Check')) { $bomStart.ArgumentList.Add($argument) }
    $bomProcess = [Diagnostics.Process]::Start($bomStart)
    try { $outRead=$bomProcess.StandardOutput.ReadToEndAsync();$errRead=$bomProcess.StandardError.ReadToEndAsync();$bomProcess.WaitForExit();$null=$outRead.GetAwaiter().GetResult();$null=$errRead.GetAwaiter().GetResult(); Assert-CatalogTest ($bomProcess.ExitCode -eq 1) 'BOM-only generated drift accepted' } finally {$bomProcess.Dispose()}
    & $updater -RepoRoot $fixture
    Set-FixtureManifest 'AMod&More' 'FirstId' 'First' '2.0.1'
    $before = [IO.File]::ReadAllText((Join-Path $fixture 'MODS.md'))
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    foreach ($argument in @('-NoProfile', '-File', $updater, '-RepoRoot', $fixture, '-Check')) { $start.ArgumentList.Add($argument) }
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process = [Diagnostics.Process]::Start($start)
    try { $outRead=$process.StandardOutput.ReadToEndAsync();$errRead=$process.StandardError.ReadToEndAsync();$process.WaitForExit();$null=$outRead.GetAwaiter().GetResult();$null=$errRead.GetAwaiter().GetResult(); Assert-CatalogTest ($process.ExitCode -eq 1) 'stale catalog accepted' } finally {$process.Dispose()}
    Assert-CatalogTest ([IO.File]::ReadAllText((Join-Path $fixture 'MODS.md')) -ceq $before) 'check mode wrote files'
    & $updater -RepoRoot $fixture
    Assert-CatalogTest ([IO.File]::ReadAllText((Join-Path $fixture 'MODS.md')).Contains('2.0.1')) 'new version not generated'
    $refused=$false; try { $null=Get-ModCatalogArtifacts $fixture @($trackedPaths | Where-Object { $_ -cne 'AMod&More/SteamWorkshopItemId' }) } catch {$refused=$true}
    Assert-CatalogTest $refused 'untracked Workshop identity accepted'
    [IO.File]::Delete((Join-Path $fixture 'AMod&More/SteamWorkshopItemId'))
    $refused=$false; try { $null=Get-ModCatalogArtifacts $fixture $trackedPaths } catch {$refused=$true}
    Assert-CatalogTest $refused 'missing Workshop identity accepted'
    [IO.File]::WriteAllText((Join-Path $fixture 'AMod&More/SteamWorkshopItemId'), '2345678901')
    [IO.File]::WriteAllText($workshopPath, 'bad')
    $refused=$false; try { $null=Get-ModCatalogArtifacts $fixture $trackedPaths } catch {$refused=$true}
    Assert-CatalogTest $refused 'invalid Workshop identity accepted'
    [IO.File]::WriteAllText($workshopPath, '1234567890')
    Set-FixtureManifest 'AMod&More' 'DifferentId' 'First' '2.0.1'
    $refused=$false; try { $null=Get-ModCatalogArtifacts $fixture $trackedPaths } catch {$refused=$true}
    Assert-CatalogTest $refused 'duplicate identity accepted'
    Write-Output "PASS catalog generation: $checks checks"
} finally {
    # This unique directory was created by this harness. Keep it for diagnosis on failure.
    # No live mod location is queried or used by this harness.
}

exit 0
