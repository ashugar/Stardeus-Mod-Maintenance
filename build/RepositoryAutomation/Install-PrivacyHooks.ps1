#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40,64}$')][string]$ReviewedCommit)
$ErrorActionPreference = 'Stop'
function Git-Value([string[]]$Arguments) {
    $value = & git --no-replace-objects @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Git operation failed; no hook configuration was changed.' }
    return ($value -join "`n").Trim()
}
try {
    $repo = Git-Value @('rev-parse','--show-toplevel')
    if ($repo.Replace('\','/').Contains('/AppData/LocalLow/Kodo Linija/Stardeus/Mods',[StringComparison]::OrdinalIgnoreCase)) { throw 'live-mods-root-refused' }
    $origin = Git-Value @('-C',$repo,'remote','get-url','origin')
    if ($origin -notmatch '^(https://github\.com/|git@github\.com:)ashugar/Stardeus-Mod-Maintenance(?:\.git)?/?$') { throw 'Unexpected repository origin.' }
    $head = Git-Value @('-C',$repo,'rev-parse','HEAD')
    if ($head -cne $ReviewedCommit.ToLowerInvariant()) { throw 'Reviewed commit must equal the current HEAD.' }
    $paths = @('build/RepositoryPrivacy','build/RepositoryAutomation')
    if (Git-Value (@('-C',$repo,'status','--porcelain','--untracked-files=all','--') + $paths)) { throw 'Commit and review privacy/automation source changes before installing them.' }
    $gitDir = [IO.Path]::GetFullPath((Git-Value @('-C',$repo,'rev-parse','--absolute-git-dir')))
    $commonGitDir = [IO.Path]::GetFullPath((Git-Value @('-C',$repo,'rev-parse','--path-format=absolute','--git-common-dir')))
    $install = Join-Path $gitDir 'repository-automation'
    $hooks = Join-Path $install 'hooks'
    $previous = & git -C $repo config --get core.hooksPath 2>$null
    if ($LASTEXITCODE -notin @(0,1)) { throw 'Cannot inspect existing hooks.' }
    $existingHooks = if ($previous) { [IO.Path]::GetFullPath($previous) } elseif (Test-Path -LiteralPath $hooks) { $hooks } else { $null }
    if ($existingHooks) {
        # Git may copy a main worktree's absolute hooksPath into a linked worktree.
        # Refresh/rebind only our intact guard; preserve all unknown or composed setups.
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        $commonPrefix = $commonGitDir.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $owned = $false
        $manifestPath = Join-Path (Split-Path -Parent $existingHooks) 'installed.json'
        if ($existingHooks.StartsWith($commonPrefix,$comparison) -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            $prior = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $expected = @('pre-commit','pre-push','Invoke-PrivacyHook.ps1')
            $entries = @(Get-ChildItem -LiteralPath $existingHooks -Force)
            if ($prior.Owner -ceq 'ashugar/Stardeus-Mod-Maintenance' -and
                [string]::Equals($prior.CommonGitDirectory,$commonGitDir,$comparison) -and $prior.HookHashes -and
                $entries.Count -eq 3 -and -not @($entries | Where-Object { $_.Name -notin $expected -or $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count) {
                $owned = $true
                foreach ($name in $expected) {
                    $recorded = $prior.HookHashes.PSObject.Properties[$name].Value
                    if (-not $recorded -or (Get-FileHash -LiteralPath (Join-Path $existingHooks $name) -Algorithm SHA256).Hash -cne $recorded) { $owned = $false }
                }
            }
        }
        if (-not $owned) { throw 'Existing hooks preserved. Integrate unknown or modified hooks explicitly before installation.' }
    }
    if (-not $previous) {
        $defaultHooks = Git-Value @('-C',$repo,'rev-parse','--git-path','hooks')
        if (-not [IO.Path]::IsPathRooted($defaultHooks)) { $defaultHooks = Join-Path $repo $defaultHooks }
        if (Test-Path -LiteralPath $defaultHooks) {
            if (Get-ChildItem -LiteralPath $defaultHooks -File | Where-Object { $_.Name -notlike '*.sample' }) { throw 'Existing Git hooks preserved. Integrate them explicitly before installation.' }
        }
    }
    # Only regular committed source files enter the installed snapshot.
    $entries = & git -C $repo ls-tree -r $head -- @paths
    if ($LASTEXITCODE -ne 0 -or -not $entries -or @($entries | Where-Object { $_ -notmatch '^100(?:644|755) blob ' }).Count) { throw 'Unsupported automation source tree.' }
    $fingerprint = @($paths | ForEach-Object { Git-Value @('-C',$repo,'rev-parse',($head + ':' + $_)) }) -join ':'
    $stage = Join-Path $install ('version-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $archive = Join-Path $stage 'source.zip'
    & git -C $repo archive --format=zip --output=$archive $head -- @paths
    if ($LASTEXITCODE -ne 0) { throw 'Cannot export reviewed source.' }
    $source = Join-Path $stage 'source'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive,$source)
    $project = Join-Path $source 'build/RepositoryPrivacy/RepositoryPrivacy.csproj'
    $artifacts = Join-Path $stage 'artifacts'
    # Prevent unreviewed checkout-wide build hooks/configuration from entering this snapshot build.
    $restoreConfig = Join-Path $stage 'NuGet.Config'
    [IO.File]::WriteAllText($restoreConfig,'<configuration><packageSources><clear /></packageSources></configuration>',[Text.UTF8Encoding]::new($false))
    Push-Location ([IO.Path]::GetTempPath())
    try {
        & dotnet build $project -c Release --artifacts-path $artifacts --nologo --verbosity quiet -noAutoResponse `
            -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false `
            "-p:RestoreConfigFile=$restoreConfig"
        $buildExit = $LASTEXITCODE
    } finally { Pop-Location }
    if ($buildExit -ne 0) { throw 'Privacy checker build failed; prior installation remains active.' }
    $checker = Join-Path $artifacts 'bin/RepositoryPrivacy/release/RepositoryPrivacy.dll'
    & dotnet $checker self-test
    if ($LASTEXITCODE -ne 0) { throw 'Privacy checker self-test failed; prior installation remains active.' }
    $manifest = @{ Version=1; Owner='ashugar/Stardeus-Mod-Maintenance'; CommonGitDirectory=$commonGitDir; WorktreeGitDirectory=$gitDir; ReviewedCommit=$head; Fingerprint=$fingerprint; Checker=$checker }
    New-Item -ItemType Directory -Path $hooks -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'build/RepositoryAutomation/Invoke-PrivacyHook.ps1') -Destination (Join-Path $hooks 'Invoke-PrivacyHook.ps1')
    $powerShell = (Get-Process -Id $PID).Path
    $shellPowerShell = "'" + $powerShell.Replace('\','/').Replace("'", "'\''") + "'"
    foreach ($mode in @('pre-commit','pre-push')) {
        $launcher = "#!/bin/sh`n" + 'hook_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd) || exit 2' + "`n" + "exec $shellPowerShell -NoLogo -NoProfile -NonInteractive -File " + '"$hook_dir/Invoke-PrivacyHook.ps1"' + " -Mode $mode`n"
        [IO.File]::WriteAllText((Join-Path $hooks $mode),$launcher,[Text.UTF8Encoding]::new($false))
        if (-not $IsWindows) { & chmod +x (Join-Path $hooks $mode); if ($LASTEXITCODE -ne 0) { throw 'Cannot make installed hooks executable.' } }
    }
    $manifest.HookHashes = @{}
    foreach ($name in @('pre-commit','pre-push','Invoke-PrivacyHook.ps1')) {
        $manifest.HookHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $hooks $name) -Algorithm SHA256).Hash
    }
    $manifestTemp = Join-Path $install 'installed.next.json'
    [IO.File]::WriteAllText($manifestTemp,($manifest | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $manifestTemp -Destination (Join-Path $install 'installed.json') -Force
    & git -C $repo config --local extensions.worktreeConfig true
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enable per-worktree configuration.' }
    & git -C $repo config --worktree core.hooksPath $hooks
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enable privacy hooks for this checkout.' }
    Write-Output ('Installed reviewed privacy guard at commit ' + $head + ' for this checkout.')
} catch { Write-Error ('Privacy-hook installation refused: ' + $_.Exception.Message); exit 2 }
