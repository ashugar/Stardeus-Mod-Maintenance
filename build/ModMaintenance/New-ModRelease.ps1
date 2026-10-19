#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Mod,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}(?:[0-9a-f]{24})?$')][string]$ReviewedCommit,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$RepoRoot = (Join-Path $PSScriptRoot '../..')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

function Write-NewReleaseFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)
    Assert-ModNoReparsePath $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length) } finally { $stream.Dispose() }
}

try {
    $root = Assert-ModReviewedRepository $RepoRoot $ReviewedCommit
    $scriptBytes = Invoke-ModGitBytes $root @('show', ($ReviewedCommit + ':build/ModMaintenance/New-ModRelease.ps1'))
    if ((Get-ModSha256 $scriptBytes) -cne (Get-ModSha256 ([IO.File]::ReadAllBytes($PSCommandPath)))) { throw 'reviewed-release-tooling-required' }
    if (-not [IO.Path]::IsPathFullyQualified($OutputRoot)) { throw 'absolute-output-root-required' }
    $output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd([char[]]@('/', '\'))
    Assert-ModNoReparsePath $output
    if ([IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) { throw 'new-output-directory-required' }
    $parent = [IO.Path]::GetDirectoryName($output)
    if (-not [IO.Directory]::Exists($parent)) { throw 'existing-output-parent-required' }
    Assert-ModExternalPath $root $output
    $snapshot = Get-ModReleaseSnapshot -RepoRoot $root -ReviewedCommit $ReviewedCommit -Mod $Mod
    $sourceTree = Get-ModCommitTree $root $ReviewedCommit
    $licenseBytes = Get-ModBlob $root $sourceTree 'LICENSE'
    # All authority, source, privacy and version checks finish before creating output.
    $null = [IO.Directory]::CreateDirectory($output)
    $pending = Join-Path $output 'INCOMPLETE.txt'
    Write-NewReleaseFile $pending $script:ModUtf8.GetBytes("Release preparation is incomplete. Do not upload or install this directory.`n")
    $upload = Join-Path $output ('upload/' + $snapshot.Folder)
    $null = [IO.Directory]::CreateDirectory($upload)
    foreach ($file in $snapshot.Files) {
        $destination = Join-Path $upload $file.RelativePath
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Write-NewReleaseFile $destination $file.Bytes
        if ((Get-ModSha256 ([IO.File]::ReadAllBytes($destination))) -cne $file.Sha256) { throw 'payload-write-verification-failed' }
    }
    $zipName = $snapshot.Folder + '-' + $snapshot.ModVersion + '.zip'
    $zipPath = Join-Path $output $zipName
    $zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zip = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $snapshot.Files) {
                $entry = $zip.CreateEntry(($snapshot.Folder + '/' + $file.RelativePath), [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entry.ExternalAttributes = 0
                $entryStream = $entry.Open()
                try { $entryStream.Write($file.Bytes, 0, $file.Bytes.Length) } finally { $entryStream.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $zipStream.Dispose() }
    $checkZip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        if ($checkZip.Entries.Count -ne $snapshot.Files.Count) { throw 'archive-parity-failed' }
        for ($index = 0; $index -lt $snapshot.Files.Count; $index++) {
            $file = $snapshot.Files[$index]
            $entry = $checkZip.Entries[$index]
            if ($entry.FullName -cne ($snapshot.Folder + '/' + $file.RelativePath) -or $entry.Length -ne $file.Length) { throw 'archive-parity-failed' }
            $stream = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try { $stream.CopyTo($memory); if ((Get-ModSha256 $memory.ToArray()) -cne $file.Sha256) { throw 'archive-parity-failed' } } finally { $memory.Dispose(); $stream.Dispose() }
        }
    } finally { $checkZip.Dispose() }
    $report = [ordered]@{
        SchemaVersion = 1; Repository = $snapshot.Repository; ReviewedCommit = $snapshot.ReviewedCommit
        Folder = $snapshot.Folder; Id = $snapshot.Id; Name = $snapshot.Name; ModVersion = $snapshot.ModVersion
        GameVersion = $snapshot.GameVersion; Dependencies = @($snapshot.Dependencies); WorkshopItemId = $snapshot.WorkshopItemId
        RecipeSha256 = $snapshot.RecipeSha256; VersionEvidence = @($snapshot.VersionEvidence)
        Validation = @('Reviewed Git objects and complete selected source history privacy scan passed.', 'Payload versions and extracted archive hashes passed.', 'No rebuild, game startup, gameplay validation, installation, or Workshop publication was performed.')
        Files = @($snapshot.Files | Select-Object RelativePath, Length, Sha256, InstallPolicy)
        Archive = [ordered]@{ Name = $zipName; Sha256 = Get-ModSha256 ([IO.File]::ReadAllBytes($zipPath)) }
        RepositoryLicense = [ordered]@{ Name = 'REPOSITORY-LICENSE'; Sha256 = Get-ModSha256 $licenseBytes }
    }
    Write-NewReleaseFile (Join-Path $output 'release.json') $script:ModUtf8.GetBytes((($report | ConvertTo-Json -Depth 15) + "`n"))
    $notes = @(
        ('# ' + $snapshot.Name + ' ' + $snapshot.ModVersion), '',
        ('Prepared from reviewed repository commit `' + $ReviewedCommit + '`.'), '',
        ('Game version declared by the manifest: ' + $snapshot.GameVersion + '.'),
        ('Dependencies: ' + $(if ($snapshot.Dependencies.Count) { $snapshot.Dependencies -join ', ' } else { 'none declared' }) + '.'), '',
        'The upload directory preserves the reviewed mod files, identity, attribution and existing release history.',
        'Consult its README, CHANGELOG, API and USAGE files where present for existing features and release details.', '',
        'Preparation checked committed source privacy, known version surfaces, intended package contents and archive checksums.',
        'The included DLLs are the reviewed committed binaries. No rebuild, fresh startup or gameplay validation was performed.',
        'Installation and Workshop publication remain separate user-controlled actions.', '',
        'The existing repository license is retained byte-for-byte in REPOSITORY-LICENSE outside the upload folder.',
        'See release.json for the exact file list and install policies. Existing create-only settings must be preserved during updates.', ''
    ) -join "`n"
    Write-NewReleaseFile (Join-Path $output 'RELEASE-NOTES.md') $script:ModUtf8.GetBytes($notes)
    Write-NewReleaseFile (Join-Path $output 'REPOSITORY-LICENSE') $licenseBytes
    $sums = @()
    foreach ($name in @($zipName, 'release.json', 'RELEASE-NOTES.md', 'REPOSITORY-LICENSE')) { $sums += (Get-ModSha256 ([IO.File]::ReadAllBytes((Join-Path $output $name)))) + '  ' + $name }
    Write-NewReleaseFile (Join-Path $output 'SHA256SUMS') $script:ModUtf8.GetBytes((($sums -join "`n") + "`n"))
    $null = Assert-ModReviewedRepository $root $ReviewedCommit
    [IO.File]::Delete($pending)
    [Console]::WriteLine('PASS mod release prepared; source privacy, version and package parity checks passed. Runtime validation remains separate.')
    exit 0
} catch {
    $category = $_.Exception.Message
    if ($category -cnotmatch '^[a-z][a-z0-9-]+$') { $category = 'mod-release-preparation-failed' }
    [Console]::Error.WriteLine('ERROR ' + $category)
    exit 1
}
