#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '../..'),
    [switch]$Check
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Catalog.ps1')
try {
    $root = [IO.Path]::GetFullPath($RepoRoot)
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute=$false; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($argument in @('--no-replace-objects', '-C', $root, 'ls-files', '--cached', '-z')) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $errRead=$process.StandardError.ReadToEndAsync(); $tracked=$process.StandardOutput.ReadToEnd(); $process.WaitForExit(); $null=$errRead.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'catalog-git-inventory-failed' }
    } finally { $process.Dispose() }
    [string[]]$trackedPaths = $tracked.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
    $generated = Get-ModCatalogArtifacts -RepoRoot $root -TrackedPaths $trackedPaths
    $outputs = [ordered]@{ 'MODS.md' = $generated.Catalog; '.github/mod-labels.json' = $generated.Labels }
    # Check all destinations before writing either generated file.
    foreach ($relative in $outputs.Keys) { Assert-CatalogPath $root (Join-Path $root $relative) }
    foreach ($relative in $outputs.Keys) {
        $path = Join-Path $root $relative
        if ($Check) {
            $expected = [Text.UTF8Encoding]::new($false).GetBytes($outputs[$relative])
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($expected)) { throw 'catalog-stale-run-Update-ModCatalog' }
        } else {
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
            [IO.File]::WriteAllText($path, $outputs[$relative], [Text.UTF8Encoding]::new($false))
        }
    }
    Write-Output "PASS generated catalog and label map: $($generated.Count) mods"
    exit 0
} catch {
    # Do not echo parser input or machine paths into hosted logs.
    [Console]::Error.WriteLine('ERROR catalog-generation-or-freshness-check-failed')
    exit 1
}
