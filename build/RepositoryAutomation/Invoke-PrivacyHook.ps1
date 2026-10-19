#requires -Version 7.0
param([Parameter(Mandatory)][ValidateSet('pre-commit','pre-push')][string]$Mode)
$ErrorActionPreference = 'Stop'
try {
    $install = Split-Path -Parent $PSScriptRoot
    $manifest = Get-Content -LiteralPath (Join-Path $install 'installed.json') -Raw | ConvertFrom-Json
    $repo = (& git --no-replace-objects rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'repository-unavailable' }
    $currentGitDir = (& git --no-replace-objects -C $repo rev-parse --absolute-git-dir 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'repository-unavailable' }
    $currentGitDir = [IO.Path]::GetFullPath($currentGitDir)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [string]::Equals($currentGitDir,$manifest.WorktreeGitDirectory,$comparison)) { throw 'guard-belongs-to-another-worktree-run-Install-PrivacyHooks' }
    if (-not (Test-Path -LiteralPath $manifest.Checker -PathType Leaf)) { throw 'installed-checker-missing' }
    if ($Mode -eq 'pre-commit') {
        & dotnet $manifest.Checker scan-index --repo $repo
        exit $LASTEXITCODE
    }
    $updates = [Console]::In.ReadToEnd()
    $armedPath = Join-Path $install 'armed.json'
    $armed = if (Test-Path -LiteralPath $armedPath) { Get-Content -LiteralPath $armedPath -Raw | ConvertFrom-Json } else { $null }
    foreach ($line in ($updates -split "`n" | Where-Object { $_.Trim() })) {
        $fields = $line.TrimEnd("`r") -split ' '
        if ($fields.Count -ne 4 -or $fields[1] -notmatch '^[0-9a-fA-F]{40,64}$') { throw 'invalid-push-protocol' }
        if ($fields[1] -match '^0+$') { continue }
        if ($armed -and $fields[2] -ceq ('refs/heads/' + $armed.Branch) -and $fields[1] -cne $armed.Head) { throw 'auto-merge-armed-use-Manage-PullRequest-Update-first' }
        $fingerprints = foreach ($path in @('build/RepositoryPrivacy','build/RepositoryAutomation')) {
            $tree = & git --no-replace-objects -C $repo rev-parse --verify ($fields[1] + '^{commit}:' + $path) 2>$null
            if ($LASTEXITCODE -ne 0) { throw 'proposed-guard-source-missing' }
            $tree
        }
        if (($fingerprints -join ':') -cne $manifest.Fingerprint) { throw 'guard-outdated-review-then-run-Install-PrivacyHooks' }
    }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    foreach ($argument in @($manifest.Checker,'pre-push','--repo',$repo)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'checker-start-failed' }
    try {
        $process.StandardInput.Write($updates)
        $process.StandardInput.Close()
        $process.WaitForExit()
        $result = $process.ExitCode
    } finally { $process.Dispose() }
    exit $result
} catch {
    # Emit controlled categories only; paths, refs and private input are suppressed.
    $category = if ($_.Exception.Message -match '^[a-zA-Z0-9-]+$') { $_.Exception.Message } else { 'hook-operation-failed' }
    [Console]::Error.WriteLine('Privacy guard refused: ' + $category)
    exit 2
}