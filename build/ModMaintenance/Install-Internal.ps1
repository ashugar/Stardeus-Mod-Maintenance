# Private installer implementation. Entry points must obtain a reviewed Common.ps1 snapshot.
Set-StrictMode -Version Latest

function Get-InstallSha256 {
    param([byte[]]$Bytes)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-InstallJsonBytes {
    param($Value)
    [Text.UTF8Encoding]::new($false, $true).GetBytes((ConvertTo-Json -InputObject $Value -Depth 30 -Compress))
}

function Assert-InstallReviewedTooling {
    param([string]$RepoRoot, [string]$ReviewedCommit, [string]$EntryPoint)
    if ($ReviewedCommit -cnotmatch '^[0-9a-f]{40}(?:[0-9a-f]{24})?$') { throw 'install-full-reviewed-commit-required' }
    $directory = [IO.Path]::GetDirectoryName((Resolve-InstallPath $EntryPoint))
    foreach ($name in @('Install-Mod.ps1', 'Install-Internal.ps1', 'Common.ps1')) {
        $actual = Join-Path $directory $name
        Assert-InstallNoReparse $actual
        if (-not [IO.File]::Exists($actual)) { throw 'install-reviewed-tooling-required' }
        $committed = Invoke-ModGitBytes -RepoRoot $RepoRoot -Arguments @('show', ($ReviewedCommit + ':build/ModMaintenance/' + $name))
        if ((Get-InstallSha256 ([IO.File]::ReadAllBytes($actual))) -cne (Get-InstallSha256 $committed)) { throw 'install-reviewed-tooling-required' }
    }
}

function Assert-InstallRecoveryTooling {
    param([string]$EntryPoint)
    $root = Resolve-InstallPath ([IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($EntryPoint)) '../..')))
    Assert-InstallNoReparse $root
    $actualRoot = [Text.Encoding]::UTF8.GetString((Invoke-ModGitBytes -RepoRoot $root -Arguments @('rev-parse', '--show-toplevel'))).Trim()
    if ((Resolve-InstallPath $actualRoot) -ine $root) { throw 'install-recovery-checkout-required' }
    $origin = [Text.Encoding]::UTF8.GetString((Invoke-ModGitBytes -RepoRoot $root -Arguments @('remote', 'get-url', 'origin'))).Trim()
    if ($origin -cnotmatch '^(https://github\.com/|git@github\.com:)ashugar/Stardeus-Mod-Maintenance(?:\.git)?/?$') { throw 'install-recovery-canonical-repository-required' }
    $head = [Text.Encoding]::UTF8.GetString((Invoke-ModGitBytes -RepoRoot $root -Arguments @('rev-parse', '--verify', 'HEAD^{commit}'))).Trim()
    # Only the tools being executed must match committed HEAD; unrelated WIP must
    # not prevent emergency recovery of a transaction from an older source commit.
    Assert-InstallReviewedTooling $root $head $EntryPoint
}

function Get-InstallFileState {
    param([string]$Path)
    Assert-InstallNoReparse $Path
    if ([IO.Directory]::Exists($Path)) { throw 'install-file-is-directory' }
    if ([IO.File]::Exists($Path)) {
        $bytes = [IO.File]::ReadAllBytes($Path)
        return [pscustomobject]@{ Exists = $true; Sha256 = (Get-InstallSha256 $bytes); Bytes = $bytes }
    }
    [pscustomobject]@{ Exists = $false; Sha256 = $null; Bytes = [byte[]]@() }
}

function Resolve-InstallPath {
    param([string]$Path)
    if (-not $IsWindows) { throw 'install-windows-required' }
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'install-explicit-path-required' }
    $value = $Path.Replace('/', '\')
    if ($value -notmatch '^[A-Za-z]:\\' -or $value.StartsWith('\\')) { throw 'install-local-absolute-path-required' }
    foreach ($part in $value.Substring(3).Split('\')) {
        if (-not $part -or $part -in @('.', '..') -or $part -match '[. ]$|[<>:"|?*\x00-\x1f]' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'install-ambiguous-path' }
    }
    [IO.Path]::GetFullPath($value).TrimEnd('\')
}

function Assert-InstallRelativePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('\') -or [IO.Path]::IsPathRooted($Path)) { throw 'install-unsafe-relative-path' }
    foreach ($part in $Path.Split('/')) {
        if (-not $part -or $part -in @('.', '..') -or $part -match '[. ]$|[<>:"|?*\x00-\x1f]' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'install-unsafe-relative-path' }
    }
}

function Test-InstallWithin {
    param([string]$Root, [string]$Path)
    [string]::Equals($Root, $Path, [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-InstallNoReparse {
    param([string]$Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        try {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'install-reparse-path-refused' }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
        $next = [IO.Path]::GetDirectoryName($cursor)
        if ($next -eq $cursor) { break }
        $cursor = $next
    }
}

function Assert-InstallGameClosed {
    if (-not $IsWindows) { throw 'install-windows-required' }
    $processes = $null
    try {
        $processes = [Diagnostics.Process]::GetProcesses()
        foreach ($process in $processes) {
            try { $name = $process.ProcessName } catch { throw 'install-process-state-unavailable' }
            if ($name -ieq 'Stardeus') { throw 'install-close-stardeus-first' }
        }
    } catch {
        if ($_.Exception.Message -eq 'install-close-stardeus-first') { throw }
        throw 'install-process-state-unavailable'
    } finally {
        foreach ($process in @($processes)) { if ($null -ne $process) { $process.Dispose() } }
    }
}

function Enter-InstallLock {
    param([string]$ModRoot)
    $key = Get-InstallSha256 ([Text.Encoding]::UTF8.GetBytes($ModRoot.ToLowerInvariant()))
    $mutex = [Threading.Mutex]::new($false, ('Global\StardeusModInstall-' + $key))
    try {
        try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { throw 'install-target-locked' }
        $mutex
    } catch { $mutex.Dispose(); throw }
}

function Exit-InstallLock {
    param($Lock)
    if ($null -ne $Lock) { try { $Lock.ReleaseMutex() } finally { $Lock.Dispose() } }
}

function Assert-InstallRoots {
    param([string]$RepoRoot, [string]$ModsRoot, [string]$RollbackRoot)
    foreach ($path in @($RepoRoot, $ModsRoot, $RollbackRoot)) { Assert-InstallNoReparse $path }
    if (-not [IO.Directory]::Exists($RepoRoot) -or -not [IO.Directory]::Exists($ModsRoot)) { throw 'install-existing-roots-required' }
    if ([IO.Path]::GetFileName($ModsRoot) -ine 'Mods' -or $ModsRoot.Replace('\', '/') -match '(?i)/Stardeus_Data/|/steamapps/workshop/') { throw 'install-local-mods-root-required' }
    if ((Test-InstallWithin $RepoRoot $ModsRoot) -or (Test-InstallWithin $ModsRoot $RepoRoot) -or
        (Test-InstallWithin $RepoRoot $RollbackRoot) -or (Test-InstallWithin $RollbackRoot $RepoRoot) -or
        (Test-InstallWithin $ModsRoot $RollbackRoot) -or (Test-InstallWithin $RollbackRoot $ModsRoot)) { throw 'install-roots-must-be-separate' }
    Assert-InstallPrivatePath $RepoRoot $ModsRoot $RollbackRoot
}

function Assert-InstallPrivatePath {
    param([string]$RepoRoot, [string]$ModsRoot, [string]$Path)
    $private = Resolve-InstallPath $Path
    Assert-InstallNoReparse $private
    $playerProfile = [Environment]::GetFolderPath('UserProfile')
    if (-not $playerProfile) { throw 'install-player-profile-unavailable' }
    $forbidden = @($RepoRoot, $ModsRoot, (Join-Path $playerProfile 'AppData/LocalLow/Kodo Linija/Stardeus'))
    foreach ($override in @($env:STARDEUS_USER_ROOT, $env:STARDEUS_GAME_ROOT, $env:STARDEUS_MANAGED_PATH)) {
        if ($override) {
            if (-not [IO.Path]::IsPathFullyQualified($override)) { throw 'install-absolute-local-override-required' }
            $forbidden += [IO.Path]::GetFullPath($override).TrimEnd('\')
        }
    }
    foreach ($protected in $forbidden) {
        if ((Test-InstallWithin $protected $private) -or (Test-InstallWithin $private $protected)) { throw 'install-private-protected-path-refused' }
    }
    if ($private.Replace('\', '/') -match '(?i)(?:^|/)(?:steamapps|Stardeus_Data)(?:/|$)') { throw 'install-private-game-or-workshop-path-refused' }
}

function Get-InstallReceiptPath {
    param([string]$RollbackRoot, [string]$ModRoot)
    $key = Get-InstallSha256 ([Text.Encoding]::UTF8.GetBytes($ModRoot.ToLowerInvariant()))
    Join-Path $RollbackRoot ('receipts/' + $key + '.json')
}

function Read-InstallJson {
    param([string]$Path)
    $state = Get-InstallFileState $Path
    if (-not $state.Exists) { throw 'install-json-file-required' }
    try { [Text.UTF8Encoding]::new($false, $true).GetString($state.Bytes) | ConvertFrom-Json -Depth 30 -ErrorAction Stop } catch { throw 'install-json-invalid' }
}

function Assert-InstallTargetIdentity {
    param($Snapshot, [string]$ModRoot)
    Assert-InstallNoReparse $ModRoot
    if ([IO.File]::Exists($ModRoot)) { throw 'install-mod-root-is-file' }
    if ([IO.Directory]::Exists($ModRoot)) {
        $manifestPath = Join-Path $ModRoot 'ModInfo.json'
        if (-not [IO.File]::Exists($manifestPath)) {
            if (@(Get-ChildItem -LiteralPath $ModRoot -Force).Count -ne 0) { throw 'install-existing-manifest-required' }
        } else {
            $manifest = Read-InstallJson $manifestPath
            if ($manifest.Id -cne $Snapshot.Id) { throw 'install-mod-id-conflict' }
        }
        $workshop = Get-InstallFileState (Join-Path $ModRoot 'SteamWorkshopItemId')
        if ($workshop.Exists) {
            $id = [Text.UTF8Encoding]::new($false, $true).GetString($workshop.Bytes).Trim()
            if ($id -notmatch '^\d+$' -or $id -cne [string]$Snapshot.WorkshopItemId) { throw 'install-workshop-id-conflict' }
        }
        $libraries = Join-Path $ModRoot 'Libraries'
        Assert-InstallNoReparse $libraries
        if ([IO.Directory]::Exists($libraries)) {
            $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $observed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($file in @($Snapshot.Files)) { if ($file.RelativePath -match '(?i)^Libraries/.+\.dll$') { $null = $allowed.Add($file.RelativePath) } }
            foreach ($file in Get-ChildItem -LiteralPath $libraries -Recurse -Force) {
                Assert-InstallNoReparse $file.FullName
                if ($file.Extension -ieq '.dll') {
                    $relative = $file.FullName.Substring($ModRoot.Length + 1).Replace('\', '/')
                    if (-not $allowed.Contains($relative) -or -not $observed.Add($relative)) { throw 'install-unexpected-dll-refused' }
                }
            }
        }
    }
}

function New-ModInstallPlan {
    param($Snapshot, [string]$RepoRoot, [string]$ModsRoot, [string]$RollbackRoot)
    $repo = Resolve-InstallPath $RepoRoot
    $mods = Resolve-InstallPath $ModsRoot
    $rollback = Resolve-InstallPath $RollbackRoot
    Assert-InstallRoots $repo $mods $rollback
    Assert-InstallRelativePath $Snapshot.Folder
    if ($Snapshot.Folder.Contains('/')) { throw 'install-one-folder-required' }
    $modRoot = Join-Path $mods $Snapshot.Folder
    Assert-InstallTargetIdentity $Snapshot $modRoot
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = @(foreach ($file in @($Snapshot.Files)) {
        Assert-InstallRelativePath $file.RelativePath
        if (-not $paths.Add($file.RelativePath) -or $file.InstallPolicy -notin @('replace', 'create-only') -or
            $file.Sha256 -cne (Get-InstallSha256 $file.Bytes) -or $file.Length -ne $file.Bytes.Length) { throw 'install-payload-invalid' }
        $state = Get-InstallFileState (Join-Path $modRoot $file.RelativePath)
        $action = if ($file.InstallPolicy -eq 'create-only' -and $state.Exists) { 'preserve' } elseif ($state.Exists -and $state.Sha256 -ceq $file.Sha256) { 'same' } else { 'copy' }
        [ordered]@{ RelativePath = $file.RelativePath; InstallPolicy = $file.InstallPolicy; PayloadLength = $file.Length; PayloadSha256 = $file.Sha256; BeforeExists = $state.Exists; BeforeSha256 = $state.Sha256; Action = $action }
    })
    if ($files.Count -eq 0) { throw 'install-empty-payload' }
    $receiptPath = Get-InstallReceiptPath $rollback $modRoot
    $receiptState = Get-InstallFileState $receiptPath
    if ($receiptState.Exists) {
        $receipt = Read-InstallJson $receiptPath
        if ($receipt.SchemaVersion -ne 1 -or $receipt.Id -cne $Snapshot.Id -or $receipt.ModRoot -ine $modRoot) { throw 'install-receipt-identity-conflict' }
        foreach ($file in @($receipt.Files)) {
            Assert-InstallRelativePath $file.RelativePath
            if (-not $paths.Contains($file.RelativePath)) { throw 'install-removed-owned-file-refused' }
        }
    }
    [ordered]@{ SchemaVersion = 1; Repository = $Snapshot.Repository; ReviewedCommit = $Snapshot.ReviewedCommit; RecipeSha256 = $Snapshot.RecipeSha256; Folder = $Snapshot.Folder; Id = $Snapshot.Id; ModVersion = $Snapshot.ModVersion; RepoRoot = $repo; ModsRoot = $mods; ModRoot = $modRoot; RollbackRoot = $rollback; ReceiptBeforeSha256 = $receiptState.Sha256; Files = $files; MissingDirectories = @(Get-InstallMissingDirectories $modRoot $files) }
}

function Write-InstallNewFile {
    param([string]$Path, [byte[]]$Bytes)
    Assert-InstallNoReparse $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes); $stream.Flush($true) } finally { $stream.Dispose() }
}

function Set-InstallAtomicFile {
    param([string]$Path, [byte[]]$Bytes, [bool]$ExpectedExists, [AllowNull()][string]$ExpectedSha256, [string]$TransactionId)
    Assert-InstallNoReparse $Path
    $temporary = $Path + '.stardeus-' + $TransactionId + '.tmp'
    Write-InstallNewFile $temporary $Bytes
    if ((Get-InstallFileState $temporary).Sha256 -cne (Get-InstallSha256 $Bytes)) { throw 'install-staged-hash-mismatch' }
    $current = Get-InstallFileState $Path
    if ($current.Exists -ne $ExpectedExists -or [string]$current.Sha256 -cne [string]$ExpectedSha256) { throw 'install-concurrent-file-change' }
    if ($ExpectedExists) { [IO.File]::Replace($temporary, $Path, [Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temporary, $Path, $false) }
    if ((Get-InstallFileState $Path).Sha256 -cne (Get-InstallSha256 $Bytes)) { throw 'install-written-hash-mismatch' }
}

function Set-InstallPayloadFile {
    param([string]$Path, [byte[]]$Bytes, [bool]$ExpectedExists, [AllowNull()][string]$ExpectedSha256, [string]$TransactionId)
    # Target mutations always check the real process state. Private journal writes use
    # Set-InstallAtomicFile so a stopped transaction can still retain its recovery state.
    Assert-InstallGameClosed
    Assert-InstallNoReparse $Path
    $temporary = $Path + '.stardeus-' + $TransactionId + '.tmp'
    Write-InstallNewFile $temporary $Bytes
    if ((Get-InstallFileState $temporary).Sha256 -cne (Get-InstallSha256 $Bytes)) { throw 'install-staged-hash-mismatch' }
    $current = Get-InstallFileState $Path
    if ($current.Exists -ne $ExpectedExists -or [string]$current.Sha256 -cne [string]$ExpectedSha256) { throw 'install-concurrent-file-change' }
    Assert-InstallGameClosed
    if ($ExpectedExists) { [IO.File]::Replace($temporary, $Path, [Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temporary, $Path, $false) }
    if ((Get-InstallFileState $Path).Sha256 -cne (Get-InstallSha256 $Bytes)) { throw 'install-written-hash-mismatch' }
}

function Save-InstallJournal {
    param([string]$Path, $Journal)
    $bytes = Get-InstallJsonBytes $Journal
    $state = Get-InstallFileState $Path
    Set-InstallAtomicFile $Path $bytes $state.Exists $state.Sha256 ([Guid]::NewGuid().ToString('N'))
}

function Remove-InstallOwnedFile {
    param([string]$Path, [string]$ExpectedSha256)
    $current = Get-InstallFileState $Path
    if (-not $current.Exists -or $current.Sha256 -cne $ExpectedSha256) { throw 'install-concurrent-file-change' }
    Assert-InstallGameClosed
    [IO.File]::Delete($Path)
}

function Get-InstallMissingDirectories {
    param([string]$ModRoot, $Files)
    $missing = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($Files)) {
        if ($file.Action -ne 'copy') { continue }
        $directory = [IO.Path]::GetDirectoryName((Join-Path $ModRoot $file.RelativePath))
        while ((Test-InstallWithin $ModRoot $directory) -and -not [IO.Directory]::Exists($directory)) {
            $null = $missing.Add($directory)
            $directory = [IO.Path]::GetDirectoryName($directory)
        }
    }
    @($missing | Sort-Object Length, { $_ })
}

function Invoke-ModInstallApply {
    param($Snapshot, [string]$RepoRoot, [string]$PlanPath, [string]$PlanSha256)
    if ($PlanSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'install-plan-sha256-required' }
    $planState = Get-InstallFileState (Resolve-InstallPath $PlanPath)
    if (-not $planState.Exists -or $planState.Sha256 -cne $PlanSha256) { throw 'install-plan-digest-mismatch' }
    $plan = Read-InstallJson $PlanPath
    Assert-InstallPrivatePath (Resolve-InstallPath $RepoRoot) (Resolve-InstallPath $plan.ModsRoot) (Resolve-InstallPath $PlanPath)
    $lock = $null
    $journalPath = $null
    try {
        $lock = Enter-InstallLock $plan.ModRoot
        Assert-InstallGameClosed
        $fresh = New-ModInstallPlan $Snapshot $RepoRoot $plan.ModsRoot $plan.RollbackRoot
        if ((Get-InstallSha256 (Get-InstallJsonBytes $fresh)) -cne $PlanSha256) { throw 'install-plan-no-longer-matches' }
        $alreadyCurrent = @($fresh.Files | Where-Object Action -eq 'copy').Count -eq 0
        $transactionId = [Guid]::NewGuid().ToString('N')
        $transactionRoot = Join-Path $fresh.RollbackRoot $transactionId
        Assert-InstallNoReparse $transactionRoot
        if (Test-Path -LiteralPath $transactionRoot) { throw 'install-transaction-already-exists' }
        $null = [IO.Directory]::CreateDirectory($transactionRoot)
        $preimages = Join-Path $transactionRoot 'preimages'
        $null = [IO.Directory]::CreateDirectory($preimages)
        Write-InstallNewFile (Join-Path $transactionRoot 'plan.json') $planState.Bytes
        $items = @(foreach ($file in @($fresh.Files | Where-Object Action -eq 'copy')) {
            $current = Get-InstallFileState (Join-Path $fresh.ModRoot $file.RelativePath)
            if ($current.Exists -ne $file.BeforeExists -or $current.Sha256 -cne $file.BeforeSha256) { throw 'install-concurrent-file-change' }
            if ($current.Exists) {
                $backup = Join-Path $preimages $file.RelativePath
                $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))
                Write-InstallNewFile $backup $current.Bytes
                if ((Get-InstallFileState $backup).Sha256 -cne $file.BeforeSha256) { throw 'install-backup-verification-failed' }
            }
            [pscustomobject]@{ RelativePath = $file.RelativePath; State = 'prepared' }
        })
        $receiptPath = Get-InstallReceiptPath $fresh.RollbackRoot $fresh.ModRoot
        $previousReceipt = Get-InstallFileState $receiptPath
        if ($previousReceipt.Sha256 -cne $fresh.ReceiptBeforeSha256) { throw 'install-receipt-changed' }
        if ($previousReceipt.Exists) { Write-InstallNewFile (Join-Path $transactionRoot 'receipt-preimage.json') $previousReceipt.Bytes }
        $newReceipt = [ordered]@{ SchemaVersion = 1; Id = $fresh.Id; ModRoot = $fresh.ModRoot; ReviewedCommit = $fresh.ReviewedCommit; Files = @($fresh.Files | Where-Object InstallPolicy -eq 'replace' | ForEach-Object { [ordered]@{ RelativePath = $_.RelativePath; Sha256 = $_.PayloadSha256 } }) }
        $receiptBytes = Get-InstallJsonBytes $newReceipt
        Write-InstallNewFile (Join-Path $transactionRoot 'receipt-new.json') $receiptBytes
        $journal = [pscustomobject]@{ SchemaVersion = 1; TransactionId = $transactionId; PlanSha256 = $PlanSha256; State = 'prepared'; Items = $items; CreatedDirectories = @($fresh.MissingDirectories); ReceiptState = 'prepared'; NewReceiptSha256 = (Get-InstallSha256 $receiptBytes) }
        $journalPath = Join-Path $transactionRoot 'journal.json'
        Save-InstallJournal $journalPath $journal
        Assert-InstallGameClosed
        $again = New-ModInstallPlan $Snapshot $RepoRoot $plan.ModsRoot $plan.RollbackRoot
        if ((Get-InstallSha256 (Get-InstallJsonBytes $again)) -cne $PlanSha256) { throw 'install-plan-no-longer-matches' }
        $journal.State = 'applying'
        Save-InstallJournal $journalPath $journal
        foreach ($directory in @($journal.CreatedDirectories)) { Assert-InstallNoReparse $directory; Assert-InstallGameClosed; $null = [IO.Directory]::CreateDirectory($directory) }
        foreach ($item in $journal.Items) {
            Assert-InstallRoots $fresh.RepoRoot $fresh.ModsRoot $fresh.RollbackRoot
            $file = @($Snapshot.Files | Where-Object RelativePath -CEQ $item.RelativePath)[0]
            $row = @($fresh.Files | Where-Object RelativePath -CEQ $item.RelativePath)[0]
            $item.State = 'writing'
            Save-InstallJournal $journalPath $journal
            Set-InstallPayloadFile (Join-Path $fresh.ModRoot $item.RelativePath) $file.Bytes $row.BeforeExists $row.BeforeSha256 $transactionId
            $item.State = 'written'
            Save-InstallJournal $journalPath $journal
        }
        foreach ($row in @($fresh.Files)) {
            $expected = if ($row.Action -eq 'preserve') { $row.BeforeSha256 } else { $row.PayloadSha256 }
            if ((Get-InstallFileState (Join-Path $fresh.ModRoot $row.RelativePath)).Sha256 -cne $expected) { throw 'install-final-verification-failed' }
        }
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($receiptPath))
        $journal.ReceiptState = 'writing'
        Save-InstallJournal $journalPath $journal
        Set-InstallPayloadFile $receiptPath $receiptBytes $previousReceipt.Exists $previousReceipt.Sha256 $transactionId
        $journal.ReceiptState = 'written'
        $journal.State = 'complete'
        Save-InstallJournal $journalPath $journal
        [pscustomobject]@{ Status = $(if ($alreadyCurrent) { 'already-current' } else { 'installed' }); JournalPath = $journalPath; PlanSha256 = $PlanSha256 }
    } catch {
        $failure = $_
        if ($journalPath -and [IO.File]::Exists($journalPath)) {
            try { $null = Invoke-ModInstallRecovery -JournalPath $journalPath -PlanSha256 $PlanSha256 -LockAlreadyHeld } catch { throw ('install-failed-recovery-required cause=' + $failure.Exception.Message + '; recovery=' + $_.Exception.Message + '; journal=' + $journalPath) }
        }
        throw $failure
    } finally { Exit-InstallLock $lock }
}

function Invoke-ModInstallRecovery {
    param([string]$JournalPath, [string]$PlanSha256, [switch]$LockAlreadyHeld)
    $journalFile = Resolve-InstallPath $JournalPath
    if ($PlanSha256 -cnotmatch '^[0-9a-f]{64}$' -or [IO.Path]::GetFileName($journalFile) -cne 'journal.json') { throw 'install-recovery-identity-invalid' }
    $transactionRoot = [IO.Path]::GetDirectoryName($journalFile)
    $planFile = Join-Path $transactionRoot 'plan.json'
    if ((Get-InstallFileState $planFile).Sha256 -cne $PlanSha256) { throw 'install-recovery-plan-invalid' }
    $plan = Read-InstallJson $planFile
    $journal = Read-InstallJson $journalFile
    $repo = Resolve-InstallPath $plan.RepoRoot
    $mods = Resolve-InstallPath $plan.ModsRoot
    $rollback = Resolve-InstallPath $plan.RollbackRoot
    Assert-InstallRoots $repo $mods $rollback
    Assert-InstallPrivatePath $repo $mods $journalFile
    Assert-InstallPrivatePath $repo $mods $planFile
    Assert-InstallRelativePath $plan.Folder
    if ($plan.Folder.Contains('/') -or $plan.ModRoot -ine (Join-Path $mods $plan.Folder) -or
        $journal.SchemaVersion -ne 1 -or $journal.TransactionId -cnotmatch '^[0-9a-f]{32}$' -or
        $transactionRoot -ine (Join-Path $rollback $journal.TransactionId) -or $journal.PlanSha256 -cne $PlanSha256) { throw 'install-recovery-identity-invalid' }
    if ($journal.State -notin @('prepared', 'applying', 'complete', 'recovering', 'recovery-conflicts', 'rolled-back') -or
        $journal.ReceiptState -notin @('prepared', 'writing', 'written', 'restoring', 'restored')) { throw 'install-recovery-state-invalid' }
    $lock = $null
    try {
        if (-not $LockAlreadyHeld) { $lock = Enter-InstallLock $plan.ModRoot }
        Assert-InstallGameClosed
        $currentManifest = Join-Path $plan.ModRoot 'ModInfo.json'
        if ([IO.File]::Exists($currentManifest) -and (Read-InstallJson $currentManifest).Id -cne $plan.Id) { throw 'install-recovery-mod-id-conflict' }
        $copies = @($plan.Files | Where-Object Action -eq 'copy')
        if (@($journal.Items).Count -ne $copies.Count) { throw 'install-recovery-items-invalid' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($item in @($journal.Items)) {
            Assert-InstallRelativePath $item.RelativePath
            if (-not $seen.Add($item.RelativePath) -or $item.State -notin @('prepared', 'writing', 'written', 'restoring', 'restored')) { throw 'install-recovery-items-invalid' }
            $rows = @($copies | Where-Object RelativePath -CEQ $item.RelativePath)
            if ($rows.Count -ne 1) { throw 'install-recovery-items-invalid' }
            $row = $rows[0]
            if ($row.BeforeExists -and (Get-InstallFileState (Join-Path (Join-Path $transactionRoot 'preimages') $row.RelativePath)).Sha256 -cne $row.BeforeSha256) { throw 'install-recovery-preimage-invalid' }
        }
        $receiptPath = Get-InstallReceiptPath $rollback $plan.ModRoot
        if ($plan.ReceiptBeforeSha256 -and (Get-InstallFileState (Join-Path $transactionRoot 'receipt-preimage.json')).Sha256 -cne $plan.ReceiptBeforeSha256) { throw 'install-recovery-receipt-preimage-invalid' }
        $expectedReceipt = [ordered]@{ SchemaVersion = 1; Id = $plan.Id; ModRoot = $plan.ModRoot; ReviewedCommit = $plan.ReviewedCommit; Files = @($plan.Files | Where-Object InstallPolicy -eq 'replace' | ForEach-Object { [ordered]@{ RelativePath = $_.RelativePath; Sha256 = $_.PayloadSha256 } }) }
        $expectedReceiptSha = Get-InstallSha256 (Get-InstallJsonBytes $expectedReceipt)
        if ($expectedReceiptSha -cne $journal.NewReceiptSha256 -or (Get-InstallFileState (Join-Path $transactionRoot 'receipt-new.json')).Sha256 -cne $expectedReceiptSha) { throw 'install-recovery-receipt-invalid' }
        if ((Get-InstallSha256 (Get-InstallJsonBytes @($journal.CreatedDirectories))) -cne (Get-InstallSha256 (Get-InstallJsonBytes @($plan.MissingDirectories)))) { throw 'install-recovery-directory-invalid' }
        foreach ($directory in @($journal.CreatedDirectories)) {
            $canonical = Resolve-InstallPath $directory
            if (-not (Test-InstallWithin $plan.ModRoot $canonical)) { throw 'install-recovery-directory-invalid' }
        }
        Assert-InstallGameClosed
        $journal.State = 'recovering'
        Save-InstallJournal $journalFile $journal
        $conflicts = [Collections.Generic.List[string]]::new()
        foreach ($item in @($journal.Items)) {
            $row = @($copies | Where-Object RelativePath -CEQ $item.RelativePath)[0]
            $path = Join-Path $plan.ModRoot $row.RelativePath
            $temporary = $path + '.stardeus-' + $journal.TransactionId + '.tmp'
            if ($item.State -eq 'restoring') {
                $stagedRestore = Get-InstallFileState $temporary
                if ($stagedRestore.Exists) {
                    if ($row.BeforeExists -and $stagedRestore.Sha256 -ceq $row.BeforeSha256) { Remove-InstallOwnedFile $temporary $row.BeforeSha256 }
                    else { $conflicts.Add($row.RelativePath); continue }
                }
            }
            if ($item.State -in @('writing', 'written', 'restoring', 'restored')) {
                $current = Get-InstallFileState $path
                if ($current.Exists -eq $row.BeforeExists -and $current.Sha256 -ceq $row.BeforeSha256) { $item.State = 'restored' }
                elseif ($current.Exists -and $current.Sha256 -ceq $row.PayloadSha256) {
                    if ($row.BeforeExists) {
                        $preimage = Get-InstallFileState (Join-Path (Join-Path $transactionRoot 'preimages') $row.RelativePath)
                        if ($preimage.Sha256 -cne $row.BeforeSha256) { throw 'install-recovery-preimage-invalid' }
                        $item.State = 'restoring'
                        Save-InstallJournal $journalFile $journal
                        Set-InstallPayloadFile $path $preimage.Bytes $true $row.PayloadSha256 $journal.TransactionId
                    } else { Remove-InstallOwnedFile $path $row.PayloadSha256 }
                    $item.State = 'restored'
                } else { $conflicts.Add($row.RelativePath) }
            }
            $tempState = Get-InstallFileState $temporary
            if ($tempState.Exists) {
                if ($tempState.Sha256 -ceq $row.PayloadSha256) { Remove-InstallOwnedFile $temporary $row.PayloadSha256 } else { $conflicts.Add($row.RelativePath) }
            }
            Save-InstallJournal $journalFile $journal
        }
        if ($journal.ReceiptState -eq 'restoring') {
            $temporary = $receiptPath + '.stardeus-' + $journal.TransactionId + '.tmp'
            $stagedRestore = Get-InstallFileState $temporary
            if ($stagedRestore.Exists) {
                if ($plan.ReceiptBeforeSha256 -and $stagedRestore.Sha256 -ceq $plan.ReceiptBeforeSha256) { Remove-InstallOwnedFile $temporary $plan.ReceiptBeforeSha256 }
                else { throw 'install-recovery-receipt-stage-conflict' }
            }
        }
        if ($journal.ReceiptState -in @('writing', 'written', 'restoring', 'restored')) {
            $current = Get-InstallFileState $receiptPath
            if ($current.Sha256 -ceq $plan.ReceiptBeforeSha256) { $journal.ReceiptState = 'restored' }
            elseif ($current.Exists -and $current.Sha256 -ceq $journal.NewReceiptSha256) {
                if ($plan.ReceiptBeforeSha256) {
                    $preimage = Get-InstallFileState (Join-Path $transactionRoot 'receipt-preimage.json')
                    if ($preimage.Sha256 -cne $plan.ReceiptBeforeSha256) { throw 'install-recovery-receipt-preimage-invalid' }
                    $journal.ReceiptState = 'restoring'
                    Save-InstallJournal $journalFile $journal
                    Set-InstallPayloadFile $receiptPath $preimage.Bytes $true $journal.NewReceiptSha256 $journal.TransactionId
                } else { Remove-InstallOwnedFile $receiptPath $journal.NewReceiptSha256 }
                $journal.ReceiptState = 'restored'
            } else { $conflicts.Add('receipt') }
        }
        $receiptTemporary = $receiptPath + '.stardeus-' + $journal.TransactionId + '.tmp'
        $receiptStage = Get-InstallFileState $receiptTemporary
        if ($receiptStage.Exists) {
            if ($receiptStage.Sha256 -ceq $journal.NewReceiptSha256) { Remove-InstallOwnedFile $receiptTemporary $journal.NewReceiptSha256 }
            else { $conflicts.Add('receipt') }
        }
        foreach ($directory in @($journal.CreatedDirectories | Sort-Object Length -Descending)) {
            Assert-InstallNoReparse $directory
            if ([IO.Directory]::Exists($directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Assert-InstallGameClosed; [IO.Directory]::Delete($directory, $false) }
        }
        $journal.State = if ($conflicts.Count) { 'recovery-conflicts' } else { 'rolled-back' }
        Save-InstallJournal $journalFile $journal
        if ($conflicts.Count) { throw 'install-recovery-concurrent-changes-preserved' }
        [pscustomobject]@{ Status = 'rolled-back'; JournalPath = $journalFile; PlanSha256 = $PlanSha256 }
    } finally { Exit-InstallLock $lock }
}
