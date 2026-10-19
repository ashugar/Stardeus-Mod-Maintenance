[CmdletBinding()]
param([string]$InstallerDirectory = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Installer refusal/recovery tests require Windows.' }
. (Join-Path $InstallerDirectory 'Install-Internal.ps1')
$script:assertions = 0
$script:fixtureNumber = 0
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('stardeus-mod-install-tests-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($testRoot)
$originalPayload = (Get-Item Function:Set-InstallPayloadFile).ScriptBlock
$originalWrite = (Get-Item Function:Write-InstallNewFile).ScriptBlock
$originalProcess = (Get-Item Function:Assert-InstallGameClosed).ScriptBlock
$originalRecovery = (Get-Item Function:Invoke-ModInstallRecovery).ScriptBlock

function Assert-Test {
    param([bool]$Condition, [string]$Message)
    $script:assertions++
    if (-not $Condition) { throw ('ASSERT ' + $Message) }
}
function Assert-Refusal {
    param([scriptblock]$Operation, [string]$Category)
    $caught = $false
    try { $null = & $Operation } catch { $caught = $_.Exception.Message.Contains($Category); if (-not $caught) { throw } }
    Assert-Test $caught ('expected refusal ' + $Category)
}
function Write-TestFile {
    param([string]$Path, [string]$Text)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllBytes($Path, [Text.Encoding]::UTF8.GetBytes($Text))
}
function New-TestFixture {
    param([switch]$Fresh)
    $script:fixtureNumber++
    $root = Join-Path $testRoot ('fixture-' + $script:fixtureNumber)
    $repo = Join-Path $root 'Repo'
    $mods = Join-Path $root 'Mods'
    $rollback = Join-Path $root 'Rollback'
    $null = [IO.Directory]::CreateDirectory($repo)
    $null = [IO.Directory]::CreateDirectory($mods)
    $folder = 'Fixture(v0.15&Update)'
    $modRoot = Join-Path $mods $folder
    $content = [ordered]@{
        'Config/Rules.json' = '{"rule":"new"}'
        'Graphics/Deep/icon.png' = 'fixture-art-bytes'
        'Libraries/Fixture.dll' = 'validated-snapshot-fixture-new-dll'
        'ModInfo.json' = '{"Id":"DistinctFixtureIdentity","Name":"Fixture","ModVersion":"2.0.0","GameVersion":"0.15.0"}'
        'README.md' = 'Reviewed fixture release'
        'Settings.json' = '{"enabled":false}'
        'SteamWorkshopItemId' = '123456789'
    }
    $files = @(foreach ($entry in $content.GetEnumerator()) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($entry.Value)
        [pscustomobject]@{RelativePath=$entry.Key;Bytes=$bytes;Length=$bytes.Length;Sha256=(Get-InstallSha256 $bytes);InstallPolicy=$(if($entry.Key -eq 'Settings.json'){'create-only'}else{'replace'})}
    })
    $snapshot = [pscustomobject]@{SchemaVersion=1;Repository='https://github.com/ashugar/Stardeus-Mod-Maintenance';ReviewedCommit=('a'*40);Folder=$folder;Id='DistinctFixtureIdentity';Name='Fixture';ModVersion='2.0.0';GameVersion='0.15.0';Dependencies=@();WorkshopItemId='123456789';RecipeSha256=('b'*64);VersionEvidence=@();Files=$files}
    if (-not $Fresh) {
        Write-TestFile (Join-Path $modRoot 'ModInfo.json') '{"Id":"DistinctFixtureIdentity","Name":"Fixture","ModVersion":"1.0.0","GameVersion":"0.15.0"}'
        Write-TestFile (Join-Path $modRoot 'Libraries/Fixture.dll') 'old-dll'
        Write-TestFile (Join-Path $modRoot 'Config/Rules.json') '{"rule":"old"}'
        Write-TestFile (Join-Path $modRoot 'Settings.json') '{"enabled":"player-choice"}'
        Write-TestFile (Join-Path $modRoot 'SteamWorkshopItemId') '123456789'
        Write-TestFile (Join-Path $modRoot 'Logs/player.log') 'unrelated-player-evidence'
    }
    [pscustomobject]@{Root=$root;Repo=$repo;Mods=$mods;Rollback=$rollback;ModRoot=$modRoot;Snapshot=$snapshot;PlanPath=(Join-Path $root 'approved-plan.json')}
}
function Save-TestPlan {
    param($Fixture)
    $plan = New-ModInstallPlan $Fixture.Snapshot $Fixture.Repo $Fixture.Mods $Fixture.Rollback
    $bytes = Get-InstallJsonBytes $plan
    Write-InstallNewFile $Fixture.PlanPath $bytes
    [pscustomobject]@{Value=$plan;Sha256=(Get-InstallSha256 $bytes)}
}
function Get-TestTree {
    param([string]$Root)
    if (-not [IO.Directory]::Exists($Root)) { return '<absent>' }
    $rows = @(Get-ChildItem -LiteralPath $Root -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Root.Length + 1).Replace('\','/') + '=' + (Get-InstallFileState $_.FullName).Sha256
    })
    $rows -join "`n"
}
function Invoke-TestApply {
    param($Fixture,$Plan)
    Invoke-ModInstallApply $Fixture.Snapshot $Fixture.Repo $Fixture.PlanPath $Plan.Sha256
}
function Invoke-TestEntry {
    param([string]$EntryPoint,[string[]]$Arguments)
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in @('-NoProfile','-NonInteractive','-File',$EntryPoint)+$Arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try {
        $output=$process.StandardOutput.ReadToEndAsync();$errorOutput=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        [pscustomobject]@{ExitCode=$process.ExitCode;Output=$output.GetAwaiter().GetResult();Error=$errorOutput.GetAwaiter().GetResult()}
    } finally {$process.Dispose()}
}

try {
    # No Stardeus or live mod root is opened. All filesystem operations below use owned fixtures.
    Assert-InstallGameClosed
    $fixture = New-TestFixture
    $before = Get-TestTree $fixture.ModRoot
    $plan = Save-TestPlan $fixture
    Assert-Test (@($plan.Value.Files|Where-Object Action -eq 'preserve').Count -eq 1) 'player setting planned as preserved'
    $result = Invoke-TestApply $fixture $plan
    Assert-Test ($result.Status -eq 'installed') 'normal upgrade installed'
    foreach ($file in $fixture.Snapshot.Files) {
        $actual = Get-InstallFileState (Join-Path $fixture.ModRoot $file.RelativePath)
        if ($file.InstallPolicy -eq 'replace') { Assert-Test ($actual.Sha256 -ceq $file.Sha256) ('copied hash ' + $file.RelativePath) }
    }
    Assert-Test ([IO.File]::ReadAllText((Join-Path $fixture.ModRoot 'Settings.json')).Contains('player-choice')) 'player settings retained'
    Assert-Test ([IO.File]::ReadAllText((Join-Path $fixture.ModRoot 'Logs/player.log')) -eq 'unrelated-player-evidence') 'unrelated log retained'
    Assert-Test ((Read-InstallJson $result.JournalPath).State -eq 'complete') 'completed journal'
    $null = Invoke-ModInstallRecovery $result.JournalPath $plan.Sha256
    Assert-Test ((Get-TestTree $fixture.ModRoot) -ceq $before) 'explicit recovery restores exact file tree'
    Assert-Test (-not [IO.Directory]::Exists((Join-Path $fixture.ModRoot 'Graphics'))) 'only newly empty directories removed'
    $null = Invoke-ModInstallRecovery $result.JournalPath $plan.Sha256
    Assert-Test ((Get-TestTree $fixture.ModRoot) -ceq $before) 'recovery is idempotent'

    $fresh = New-TestFixture -Fresh
    $freshPlan = Save-TestPlan $fresh
    $freshResult = Invoke-TestApply $fresh $freshPlan
    Assert-Test ([IO.File]::ReadAllText((Join-Path $fresh.ModRoot 'Settings.json')) -eq '{"enabled":false}') 'missing setting seeded'
    $freshBefore = Get-TestTree $fresh.ModRoot
    $null = Invoke-ModInstallRecovery $freshResult.JournalPath $freshPlan.Sha256
    Assert-Test (-not [IO.Directory]::Exists($fresh.ModRoot)) 'fresh install rollback removes only created files and empty directories'

    $same = New-TestFixture -Fresh
    foreach($file in $same.Snapshot.Files) { Write-TestFile (Join-Path $same.ModRoot $file.RelativePath) ([Text.Encoding]::UTF8.GetString($file.Bytes)) }
    $sameBefore = Get-TestTree $same.ModRoot
    $samePlan = Save-TestPlan $same
    $sameResult = Invoke-TestApply $same $samePlan
    Assert-Test ($sameResult.Status -eq 'already-current') 'already current recognized'
    Assert-Test ((Get-TestTree $same.ModRoot) -ceq $sameBefore) 'already-current does not alter mod files'

    $identity = New-TestFixture
    Write-TestFile (Join-Path $identity.ModRoot 'ModInfo.json') '{"Id":"SomeoneElse"}'
    Assert-Refusal { New-ModInstallPlan $identity.Snapshot $identity.Repo $identity.Mods $identity.Rollback } 'install-mod-id-conflict'
    $workshop = New-TestFixture
    Write-TestFile (Join-Path $workshop.ModRoot 'SteamWorkshopItemId') '999999999'
    Assert-Refusal { New-ModInstallPlan $workshop.Snapshot $workshop.Repo $workshop.Mods $workshop.Rollback } 'install-workshop-id-conflict'
    $extra = New-TestFixture
    Write-TestFile (Join-Path $extra.ModRoot 'Libraries/Unreviewed.dll') 'unexpected'
    Assert-Refusal { New-ModInstallPlan $extra.Snapshot $extra.Repo $extra.Mods $extra.Rollback } 'install-unexpected-dll-refused'
    Assert-Refusal { New-ModInstallPlan $extra.Snapshot $extra.Repo $extra.Mods (Join-Path $extra.Mods 'Rollback') } 'install-roots-must-be-separate'

    $stale = New-TestFixture
    $stalePlan = Save-TestPlan $stale
    Assert-Refusal { Invoke-ModInstallApply $stale.Snapshot $stale.Repo $stale.PlanPath ('0'*64) } 'install-plan-digest-mismatch'
    Write-TestFile (Join-Path $stale.ModRoot 'Config/Rules.json') '{"rule":"later-player-edit"}'
    $staleBefore = Get-TestTree $stale.ModRoot
    Assert-Refusal { Invoke-TestApply $stale $stalePlan } 'install-plan-no-longer-matches'
    Assert-Test ((Get-TestTree $stale.ModRoot) -ceq $staleBefore) 'stale plan makes zero target changes'

    $running = New-TestFixture
    $runningPlan = Save-TestPlan $running
    $runningBefore = Get-TestTree $running.ModRoot
    Set-Item Function:Assert-InstallGameClosed { throw 'install-close-stardeus-first' }
    Assert-Refusal { Invoke-TestApply $running $runningPlan } 'install-close-stardeus-first'
    Set-Item Function:Assert-InstallGameClosed { throw 'install-process-state-unavailable' }
    Assert-Refusal { Invoke-TestApply $running $runningPlan } 'install-process-state-unavailable'
    Set-Item Function:Assert-InstallGameClosed $originalProcess
    Assert-Test ((Get-TestTree $running.ModRoot) -ceq $runningBefore -and -not [IO.Directory]::Exists($running.Rollback)) 'process refusals precede backup and target writes'

    $recheck = New-TestFixture
    $recheckPlan = Save-TestPlan $recheck
    $recheckBefore = Get-TestTree $recheck.ModRoot
    $script:processCalls=0
    Set-Item Function:Assert-InstallGameClosed { $script:processCalls++; if($script:processCalls -ge 2){throw 'install-close-stardeus-first'} }
    Assert-Refusal { Invoke-TestApply $recheck $recheckPlan } 'install-failed-recovery-required'
    Set-Item Function:Assert-InstallGameClosed $originalProcess
    Assert-Test ($script:processCalls -ge 2 -and (Get-TestTree $recheck.ModRoot) -ceq $recheckBefore) 'game state rechecked after backup before target changes'

    $started = New-TestFixture
    $startedPlan=Save-TestPlan $started
    $startedBefore=Get-TestTree $started.ModRoot
    $script:fixtureGameRunning=$false;$script:payloadWrites=0
    Set-Item Function:Assert-InstallGameClosed {if($script:fixtureGameRunning){throw 'install-close-stardeus-first'}}
    Set-Item Function:Set-InstallPayloadFile {
        param([string]$Path,[byte[]]$Bytes,[bool]$ExpectedExists,[AllowNull()][string]$ExpectedSha256,[string]$TransactionId)
        & $originalPayload $Path $Bytes $ExpectedExists $ExpectedSha256 $TransactionId
        if(Test-InstallWithin $started.ModRoot $Path){$script:payloadWrites++;$script:fixtureGameRunning=$true}
    }
    Assert-Refusal {Invoke-TestApply $started $startedPlan} 'install-failed-recovery-required'
    Set-Item Function:Set-InstallPayloadFile $originalPayload
    Assert-Test ($script:payloadWrites -eq 1) 'game starting after first payload prevents all later replacements'
    Assert-Test ((Get-InstallFileState (Join-Path $started.ModRoot 'Libraries/Fixture.dll')).Sha256 -ceq (Get-InstallSha256 ([Text.Encoding]::UTF8.GetBytes('old-dll')))) 'next payload remains original while game runs'
    $startedJournal=@(Get-ChildItem -LiteralPath $started.Rollback -Recurse -File -Filter journal.json)[0].FullName
    Assert-Test ((Read-InstallJson $startedJournal).State -eq 'applying') 'partial apply retains recoverable journal while automatic rollback is blocked'
    Set-Item Function:Assert-InstallGameClosed $originalProcess
    $null=Invoke-ModInstallRecovery $startedJournal $startedPlan.Sha256
    Assert-Test ((Get-TestTree $started.ModRoot) -ceq $startedBefore) 'partial apply recovers after game closes'

    $duringStage=New-TestFixture
    $duringStagePlan=Save-TestPlan $duringStage
    $duringStageBefore=Get-TestTree $duringStage.ModRoot
    $script:fixtureGameRunning=$false
    Set-Item Function:Assert-InstallGameClosed {if($script:fixtureGameRunning){throw 'install-close-stardeus-first'}}
    Set-Item Function:Write-InstallNewFile {
        param([string]$Path,[byte[]]$Bytes)
        & $originalWrite $Path $Bytes
        if(Test-InstallWithin $duringStage.ModRoot $Path){$script:fixtureGameRunning=$true}
    }
    Assert-Refusal {Invoke-TestApply $duringStage $duringStagePlan} 'install-failed-recovery-required'
    Set-Item Function:Write-InstallNewFile $originalWrite
    Assert-Test ((Get-InstallFileState (Join-Path $duringStage.ModRoot 'Config/Rules.json')).Sha256 -ceq (Get-InstallSha256 ([Text.Encoding]::UTF8.GetBytes('{"rule":"old"}')))) 'game starting during staging prevents atomic payload commit'
    Set-Item Function:Assert-InstallGameClosed $originalProcess
    $duringStageJournal=@(Get-ChildItem -LiteralPath $duringStage.Rollback -Recurse -File -Filter journal.json)[0].FullName
    $null=Invoke-ModInstallRecovery $duringStageJournal $duringStagePlan.Sha256
    Assert-Test ((Get-TestTree $duringStage.ModRoot) -ceq $duringStageBefore) 'staged temporary payload cleans up after game closes'

    foreach($withoutNewFile in @($false,$true)){
        $recoveryStarted=New-TestFixture
        if($withoutNewFile){$recoveryStarted.Snapshot.Files=@($recoveryStarted.Snapshot.Files|Where-Object RelativePath -ne 'Graphics/Deep/icon.png')}
        $recoveryStartedBefore=Get-TestTree $recoveryStarted.ModRoot
        $recoveryStartedPlan=Save-TestPlan $recoveryStarted
        $recoveryStartedResult=Invoke-TestApply $recoveryStarted $recoveryStartedPlan
        $script:fixtureGameRunning=$false;$script:payloadWrites=0
        Set-Item Function:Assert-InstallGameClosed {if($script:fixtureGameRunning){throw 'install-close-stardeus-first'}}
        Set-Item Function:Set-InstallPayloadFile {
            param([string]$Path,[byte[]]$Bytes,[bool]$ExpectedExists,[AllowNull()][string]$ExpectedSha256,[string]$TransactionId)
            & $originalPayload $Path $Bytes $ExpectedExists $ExpectedSha256 $TransactionId
            if(Test-InstallWithin $recoveryStarted.ModRoot $Path){$script:payloadWrites++;$script:fixtureGameRunning=$true}
        }
        Assert-Refusal {Invoke-ModInstallRecovery $recoveryStartedResult.JournalPath $recoveryStartedPlan.Sha256} 'install-close-stardeus-first'
        Set-Item Function:Set-InstallPayloadFile $originalPayload
        Assert-Test ($script:payloadWrites -eq 1) 'game starting after first restore prevents later rollback mutations'
        if(-not $withoutNewFile){Assert-Test ([IO.File]::Exists((Join-Path $recoveryStarted.ModRoot 'Graphics/Deep/icon.png'))) 'new file removal refuses while game runs'}
        else{Assert-Test ((Get-InstallFileState (Join-Path $recoveryStarted.ModRoot 'Libraries/Fixture.dll')).Sha256 -ceq @($recoveryStarted.Snapshot.Files|Where-Object RelativePath -eq 'Libraries/Fixture.dll')[0].Sha256) 'existing file rollback replacement refuses while game runs'}
        Set-Item Function:Assert-InstallGameClosed $originalProcess
        $null=Invoke-ModInstallRecovery $recoveryStartedResult.JournalPath $recoveryStartedPlan.Sha256
        Assert-Test ((Get-TestTree $recoveryStarted.ModRoot) -ceq $recoveryStartedBefore) 'paused recovery finishes after game closes'
    }

    $backupFailure = New-TestFixture
    $backupPlan = Save-TestPlan $backupFailure
    $backupBefore = Get-TestTree $backupFailure.ModRoot
    Set-Item Function:Write-InstallNewFile {
        param([string]$Path,[byte[]]$Bytes)
        if ($Path.Contains('\preimages\')) { throw 'fixture-backup-failure' }
        & $originalWrite $Path $Bytes
    }
    Assert-Refusal { Invoke-TestApply $backupFailure $backupPlan } 'fixture-backup-failure'
    Set-Item Function:Write-InstallNewFile $originalWrite
    Assert-Test ((Get-TestTree $backupFailure.ModRoot) -ceq $backupBefore) 'backup failure makes zero target changes'

    $midCopy = New-TestFixture
    $midPlan = Save-TestPlan $midCopy
    $midBefore = Get-TestTree $midCopy.ModRoot
    $script:injected = $false
    Set-Item Function:Set-InstallPayloadFile {
        param([string]$Path,[byte[]]$Bytes,[bool]$ExpectedExists,[AllowNull()][string]$ExpectedSha256,[string]$TransactionId)
        if (-not $script:injected -and $Path -ieq (Join-Path $midCopy.ModRoot 'Libraries/Fixture.dll')) { $script:injected=$true; throw 'fixture-mid-copy-failure' }
        & $originalPayload $Path $Bytes $ExpectedExists $ExpectedSha256 $TransactionId
    }
    Assert-Refusal { Invoke-TestApply $midCopy $midPlan } 'fixture-mid-copy-failure'
    Set-Item Function:Set-InstallPayloadFile $originalPayload
    Assert-Test $script:injected 'failure was injected after earlier payload replacements'
    Assert-Test ((Get-TestTree $midCopy.ModRoot) -ceq $midBefore) 'mid-copy failure automatically restores exact preimages'

    $crash = New-TestFixture
    $crashPlan = Save-TestPlan $crash
    $crashBefore = Get-TestTree $crash.ModRoot
    $script:injected = $false
    Set-Item Function:Set-InstallPayloadFile {
        param([string]$Path,[byte[]]$Bytes,[bool]$ExpectedExists,[AllowNull()][string]$ExpectedSha256,[string]$TransactionId)
        if (-not $script:injected -and $Path -ieq (Join-Path $crash.ModRoot 'Libraries/Fixture.dll')) { $script:injected=$true; throw 'fixture-interruption' }
        & $originalPayload $Path $Bytes $ExpectedExists $ExpectedSha256 $TransactionId
    }
    Set-Item Function:Invoke-ModInstallRecovery { throw 'fixture-recovery-deferred' }
    Assert-Refusal { Invoke-TestApply $crash $crashPlan } 'install-failed-recovery-required'
    Set-Item Function:Set-InstallPayloadFile $originalPayload
    Set-Item Function:Invoke-ModInstallRecovery $originalRecovery
    $crashJournal = @(Get-ChildItem -LiteralPath $crash.Rollback -Recurse -File -Filter journal.json)[0].FullName
    Assert-Test ((Get-TestTree $crash.ModRoot) -cne $crashBefore) 'fixture retained partial transaction for later recovery'
    $null = Invoke-ModInstallRecovery $crashJournal $crashPlan.Sha256
    Assert-Test ((Get-TestTree $crash.ModRoot) -ceq $crashBefore) 'later journal recovery restores interrupted transaction'

    foreach($editStage in @($false,$true)){
        $restoreCrash=New-TestFixture
        $restoreBefore=Get-TestTree $restoreCrash.ModRoot
        $restorePlan=Save-TestPlan $restoreCrash
        $restoreResult=Invoke-TestApply $restoreCrash $restorePlan
        $restoreJournal=Read-InstallJson $restoreResult.JournalPath
        $restoreTemporary=(Join-Path $restoreCrash.ModRoot 'Config/Rules.json')+'.stardeus-'+$restoreJournal.TransactionId+'.tmp'
        $script:restoreInterrupted=$false
        Set-Item Function:Write-InstallNewFile {
            param([string]$Path,[byte[]]$Bytes)
            & $originalWrite $Path $Bytes
            if(-not $script:restoreInterrupted -and $Path -ieq $restoreTemporary){$script:restoreInterrupted=$true;throw 'fixture-interrupted-restoration'}
        }
        Assert-Refusal {Invoke-ModInstallRecovery $restoreResult.JournalPath $restorePlan.Sha256} 'fixture-interrupted-restoration'
        Set-Item Function:Write-InstallNewFile $originalWrite
        Assert-Test ([IO.File]::Exists($restoreTemporary)) 'interrupted restore retains deterministic journal-owned stage'
        Assert-Test (@((Read-InstallJson $restoreResult.JournalPath).Items|Where-Object RelativePath -eq 'Config/Rules.json')[0].State -eq 'restoring') 'restore state persisted before old-byte staging'
        if($editStage){
            Write-TestFile $restoreTemporary 'later-user-stage-edit'
            Assert-Refusal {Invoke-ModInstallRecovery $restoreResult.JournalPath $restorePlan.Sha256} 'install-recovery-concurrent-changes-preserved'
            Assert-Test ([IO.File]::ReadAllText($restoreTemporary) -eq 'later-user-stage-edit') 'altered restore staging file preserved'
            Assert-Test ([IO.File]::ReadAllText((Join-Path $restoreCrash.ModRoot 'Config/Rules.json')) -eq '{"rule":"new"}') 'altered restore stage prevents target overwrite'
        }else{
            $null=Invoke-ModInstallRecovery $restoreResult.JournalPath $restorePlan.Sha256
            Assert-Test ((Get-TestTree $restoreCrash.ModRoot) -ceq $restoreBefore) 'interrupted restoration retry yields exact original tree without stray stage'
            Assert-Test (-not [IO.File]::Exists($restoreTemporary)) 'successful retry removes verified old-byte staging file'
        }
    }

    $conflict = New-TestFixture
    $conflictPlan = Save-TestPlan $conflict
    $conflictResult = Invoke-TestApply $conflict $conflictPlan
    Write-TestFile (Join-Path $conflict.ModRoot 'Libraries/Fixture.dll') 'later-user-change'
    Assert-Refusal { Invoke-ModInstallRecovery $conflictResult.JournalPath $conflictPlan.Sha256 } 'install-recovery-concurrent-changes-preserved'
    Assert-Test ([IO.File]::ReadAllText((Join-Path $conflict.ModRoot 'Libraries/Fixture.dll')) -eq 'later-user-change') 'recovery never overwrites later user edit'
    Assert-Test ((Read-InstallJson $conflictResult.JournalPath).State -eq 'recovery-conflicts') 'recovery conflict remains recorded'

    $corrupt = New-TestFixture
    $corruptPlan = Save-TestPlan $corrupt
    $corruptResult = Invoke-TestApply $corrupt $corruptPlan
    $corruptBefore = Get-TestTree $corrupt.ModRoot
    $backup = Join-Path ([IO.Path]::GetDirectoryName($corruptResult.JournalPath)) 'preimages/Config/Rules.json'
    Write-TestFile $backup 'corrupt-backup'
    Assert-Refusal { Invoke-ModInstallRecovery $corruptResult.JournalPath $corruptPlan.Sha256 } 'install-recovery-preimage-invalid'
    Assert-Test ((Get-TestTree $corrupt.ModRoot) -ceq $corruptBefore) 'corrupt backup is refused before rollback writes'

    $directories = New-TestFixture -Fresh
    $directoriesPlan=Save-TestPlan $directories
    $directoriesResult=Invoke-TestApply $directories $directoriesPlan
    $directoryJournal=Read-InstallJson $directoriesResult.JournalPath
    $directoryJournal.CreatedDirectories=@($directoryJournal.CreatedDirectories)+$directories.Mods
    [IO.File]::WriteAllBytes($directoriesResult.JournalPath,(Get-InstallJsonBytes $directoryJournal))
    Assert-Refusal {Invoke-ModInstallRecovery $directoriesResult.JournalPath $directoriesPlan.Sha256} 'install-recovery-directory-invalid'

    $removed = New-TestFixture
    $removedPlan = Save-TestPlan $removed
    $null = Invoke-TestApply $removed $removedPlan
    $removed.Snapshot.Files = @($removed.Snapshot.Files | Where-Object RelativePath -ne 'Config/Rules.json')
    Assert-Refusal { New-ModInstallPlan $removed.Snapshot $removed.Repo $removed.Mods $removed.Rollback } 'install-removed-owned-file-refused'

    foreach ($relative in @('../escape','Libraries/../outside','/rooted','folder/file:stream','file.','NUL.txt','a//b','a\b')) {
        Assert-Refusal { Assert-InstallRelativePath $relative } 'install-unsafe-relative-path'
    }
    Assert-Refusal { Resolve-InstallPath ((Join-Path $testRoot 'foo') + '\..\bar') } 'install-ambiguous-path'
    $junctionFixture = New-TestFixture
    $link = Join-Path $junctionFixture.ModRoot 'Config/link'
    $null = New-Item -ItemType Junction -Path $link -Target $junctionFixture.Repo
    try { Assert-Refusal { Assert-InstallNoReparse (Join-Path $link 'missing.json') } 'install-reparse-path-refused' }
    finally { Remove-Item -LiteralPath $link -Force }

    $locking = New-TestFixture
    $lockKey=Get-InstallSha256 ([Text.Encoding]::UTF8.GetBytes($locking.ModRoot.ToLowerInvariant()))
    $lockCode='$mutex=[Threading.Mutex]::new($false,''Global\StardeusModInstall-__KEY__'');$null=$mutex.WaitOne();[Console]::WriteLine(''ready'');$null=[Console]::ReadLine();$mutex.ReleaseMutex();$mutex.Dispose()'.Replace('__KEY__',$lockKey)
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($lockCode)))){$start.ArgumentList.Add($argument)}
    $holding=[Diagnostics.Process]::Start($start)
    try {
        Assert-Test ($holding.StandardOutput.ReadLine() -eq 'ready') 'separate process acquired target lock'
        Assert-Refusal {Enter-InstallLock $locking.ModRoot} 'install-target-locked'
    } finally {$holding.StandardInput.WriteLine('release');$holding.WaitForExit();$holding.Dispose()}

    # Exercise the production entry point with an isolated Common contract fixture.
    # Real Git revision/recipe validation belongs to Common.ps1's separate tests.
    $entryFixture=New-TestFixture
    $entryBefore=Get-TestTree $entryFixture.ModRoot
    $entryDirectory=Join-Path $entryFixture.Repo 'build/ModMaintenance'
    $null=[IO.Directory]::CreateDirectory($entryDirectory)
    foreach($name in @('Install-Mod.ps1','Install-Internal.ps1')){[IO.File]::Copy((Join-Path $InstallerDirectory $name),(Join-Path $entryDirectory $name))}
    $entryFixture.Snapshot|Export-Clixml -LiteralPath (Join-Path $entryFixture.Root 'snapshot.clixml')
    $shim=@'
function Get-ModReleaseSnapshot {
    param([string]$RepoRoot,[string]$ReviewedCommit,[string]$Mod)
    $snapshot=Import-Clixml -LiteralPath (Join-Path $PSScriptRoot '../../../snapshot.clixml')
    if($snapshot.ReviewedCommit -cne $ReviewedCommit -or $snapshot.Folder -cne $Mod){throw 'fixture-reviewed-snapshot-mismatch'}
    $snapshot
}
function Invoke-ModGitBytes {
    param([string]$RepoRoot,[string[]]$Arguments)
    if($Arguments[0] -eq 'show'){
        $name=($Arguments[1] -split '/')[-1]
        return ,([IO.File]::ReadAllBytes((Join-Path $PSScriptRoot ('../../../reviewed-tooling/'+$name))))
    }
    if($Arguments[0] -eq 'remote'){return ,([Text.Encoding]::UTF8.GetBytes('https://github.com/ashugar/Stardeus-Mod-Maintenance'))}
    if($Arguments[0] -eq 'rev-parse' -and $Arguments[1] -eq '--show-toplevel'){return ,([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))}
    if($Arguments[0] -eq 'rev-parse'){return ,([Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../../fixture-head.txt'))))}
    throw 'fixture-git-contract-refused'
}
'@
    Write-TestFile (Join-Path $entryDirectory 'Common.ps1') $shim
    $toolingAuthority=Join-Path $entryFixture.Root 'reviewed-tooling'
    $null=[IO.Directory]::CreateDirectory($toolingAuthority)
    foreach($name in @('Install-Mod.ps1','Install-Internal.ps1','Common.ps1')){[IO.File]::Copy((Join-Path $entryDirectory $name),(Join-Path $toolingAuthority $name))}
    Write-TestFile (Join-Path $entryFixture.Root 'fixture-head.txt') $entryFixture.Snapshot.ReviewedCommit
    $entry=Join-Path $entryDirectory 'Install-Mod.ps1'
    foreach($name in @('Install-Mod.ps1','Install-Internal.ps1','Common.ps1')){
        $actual=Join-Path $entryDirectory $name
        [IO.File]::AppendAllText($actual,"`n# unreviewed copied tooling`n")
        $unreviewed=Invoke-TestEntry $entry @('-Prepare','-Mod',$entryFixture.Snapshot.Folder,'-ReviewedCommit',$entryFixture.Snapshot.ReviewedCommit,'-ModsRoot',$entryFixture.Mods,'-RollbackRoot',$entryFixture.Rollback,'-PlanPath',$entryFixture.PlanPath)
        Assert-Test ($unreviewed.ExitCode -ne 0 -and $unreviewed.Error.Contains('install-reviewed-tooling-required') -and -not [IO.File]::Exists($entryFixture.PlanPath)) 'prepare rejects actual modified entry/internal/common copy'
        [IO.File]::WriteAllBytes($actual,[IO.File]::ReadAllBytes((Join-Path $toolingAuthority $name)))
    }
    foreach($segment in @('Stardeus_Data/StreamingAssets/Mods/Core','steamapps/workshop/content/1380910')){
        $forbidden=Join-Path $entryFixture.Root $segment
        $null=[IO.Directory]::CreateDirectory($forbidden)
        Assert-Refusal {New-ModInstallPlan $entryFixture.Snapshot $entryFixture.Repo $entryFixture.Mods (Join-Path $forbidden 'Rollback')} 'install-private-game-or-workshop-path-refused'
        $forbiddenPlan=Join-Path $forbidden 'plan.json'
        $badPrepare=Invoke-TestEntry $entry @('-Prepare','-RepoRoot',$entryFixture.Repo,'-Mod',$entryFixture.Snapshot.Folder,'-ReviewedCommit',$entryFixture.Snapshot.ReviewedCommit,'-ModsRoot',$entryFixture.Mods,'-RollbackRoot',$entryFixture.Rollback,'-PlanPath',$forbiddenPlan)
        Assert-Test ($badPrepare.ExitCode -ne 0 -and $badPrepare.Error.Contains('install-private-game-or-workshop-path-refused') -and -not [IO.File]::Exists($forbiddenPlan)) 'prepare refuses game/Core/Workshop plan output'
    }
    $savedEnvironment=@{}
    try{
        foreach($variable in @('STARDEUS_USER_ROOT','STARDEUS_GAME_ROOT','STARDEUS_MANAGED_PATH')){
            $savedEnvironment[$variable]=[Environment]::GetEnvironmentVariable($variable,'Process')
            $fakeProtected=Join-Path $entryFixture.Root ('Protected-'+$variable)
            $null=[IO.Directory]::CreateDirectory($fakeProtected)
            [Environment]::SetEnvironmentVariable($variable,$fakeProtected,'Process')
        }
        foreach($variable in @('STARDEUS_USER_ROOT','STARDEUS_GAME_ROOT','STARDEUS_MANAGED_PATH')){
            $fakeProtected=[Environment]::GetEnvironmentVariable($variable,'Process')
            Assert-Refusal {New-ModInstallPlan $entryFixture.Snapshot $entryFixture.Repo $entryFixture.Mods (Join-Path $fakeProtected 'Rollback')} 'install-private-protected-path-refused'
            $forbiddenPlan=Join-Path $fakeProtected 'plan.json'
            $badPrepare=Invoke-TestEntry $entry @('-Prepare','-RepoRoot',$entryFixture.Repo,'-Mod',$entryFixture.Snapshot.Folder,'-ReviewedCommit',$entryFixture.Snapshot.ReviewedCommit,'-ModsRoot',$entryFixture.Mods,'-RollbackRoot',$entryFixture.Rollback,'-PlanPath',$forbiddenPlan)
            Assert-Test ($badPrepare.ExitCode -ne 0 -and $badPrepare.Error.Contains('install-private-protected-path-refused') -and -not [IO.File]::Exists($forbiddenPlan)) 'prepare refuses configured game/managed/user state output'
        }
    }finally{foreach($variable in $savedEnvironment.Keys){[Environment]::SetEnvironmentVariable($variable,$savedEnvironment[$variable],'Process')}}
    $prepareResult=Invoke-TestEntry $entry @('-Prepare','-RepoRoot',$entryFixture.Repo,'-Mod',$entryFixture.Snapshot.Folder,'-ReviewedCommit',$entryFixture.Snapshot.ReviewedCommit,'-ModsRoot',$entryFixture.Mods,'-RollbackRoot',$entryFixture.Rollback,'-PlanPath',$entryFixture.PlanPath)
    Assert-Test ($prepareResult.ExitCode -eq 0) ('entry prepare: '+$prepareResult.Error)
    Assert-Test ((Get-TestTree $entryFixture.ModRoot) -ceq $entryBefore) 'entry prepare is read-only for destination'
    $entryDigest=(Get-InstallFileState $entryFixture.PlanPath).Sha256
    $actualInternal=Join-Path $entryDirectory 'Install-Internal.ps1'
    [IO.File]::AppendAllText($actualInternal,"`n# unreviewed apply helper`n")
    $unreviewedApply=Invoke-TestEntry $entry @('-Apply','-ReviewedCommit',$entryFixture.Snapshot.ReviewedCommit,'-PlanPath',$entryFixture.PlanPath,'-PlanSha256',$entryDigest)
    Assert-Test ($unreviewedApply.ExitCode -ne 0 -and $unreviewedApply.Error.Contains('install-reviewed-tooling-required') -and (Get-TestTree $entryFixture.ModRoot) -ceq $entryBefore) 'apply rejects actual modified helper before destination writes'
    [IO.File]::WriteAllBytes($actualInternal,[IO.File]::ReadAllBytes((Join-Path $toolingAuthority 'Install-Internal.ps1')))
    $wrongRevision=Invoke-TestEntry $entry @('-Apply','-RepoRoot',$entryFixture.Repo,'-ReviewedCommit',('c'*40),'-PlanPath',$entryFixture.PlanPath,'-PlanSha256',$entryDigest)
    Assert-Test ($wrongRevision.ExitCode -ne 0 -and $wrongRevision.Error.Contains('install-reviewed-commit-mismatch')) 'entry rejects different reviewed commit'
    $applyResult=Invoke-TestEntry $entry @('-Apply','-RepoRoot',$entryFixture.Repo,'-ReviewedCommit',$entryFixture.Snapshot.ReviewedCommit,'-PlanPath',$entryFixture.PlanPath,'-PlanSha256',$entryDigest)
    Assert-Test ($applyResult.ExitCode -eq 0) ('entry apply: '+$applyResult.Error)
    $entryJournal=@(Get-ChildItem -LiteralPath $entryFixture.Rollback -Recurse -File -Filter journal.json)[0].FullName
    $forbiddenJournalRoot=Join-Path $entryFixture.Root 'Stardeus_Data/private-journal'
    $null=[IO.Directory]::CreateDirectory($forbiddenJournalRoot)
    foreach($name in @('journal.json','plan.json')){[IO.File]::Copy((Join-Path ([IO.Path]::GetDirectoryName($entryJournal)) $name),(Join-Path $forbiddenJournalRoot $name))}
    Assert-Refusal {Invoke-ModInstallRecovery (Join-Path $forbiddenJournalRoot 'journal.json') $entryDigest} 'install-private-game-or-workshop-path-refused'
    Write-TestFile (Join-Path $entryFixture.Root 'fixture-head.txt') ('d'*40)
    Write-TestFile (Join-Path $entryFixture.Repo 'unrelated-mod-work-in-progress.txt') 'unrelated WIP must not block recovery'
    $recoverResult=Invoke-TestEntry $entry @('-Recover','-JournalPath',$entryJournal,'-PlanSha256',$entryDigest)
    Assert-Test ($recoverResult.ExitCode -eq 0) ('entry recovers older transaction with current committed tools and unrelated WIP: '+$recoverResult.Error)
    Assert-Test ((Get-TestTree $entryFixture.ModRoot) -ceq $entryBefore) 'entry recovery restores preimages'

    foreach ($file in @('Install-Mod.ps1','Install-Internal.ps1')) {
        $errors=$null;$tokens=$null
        $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $InstallerDirectory $file),[ref]$tokens,[ref]$errors)
        Assert-Test (@($errors).Count -eq 0) ('production syntax ' + $file)
    }
    $command = Get-Command (Join-Path $InstallerDirectory 'Install-Mod.ps1')
    Assert-Test (@($command.ParameterSets).Count -eq 3) 'prepare/apply/recover parameter sets only'
    Assert-Test (-not $command.Parameters.ContainsKey('SkipProcessCheck')) 'no shipping process bypass switch'
    [Console]::WriteLine("PASS installer fixtures: $script:assertions assertions; only owned temporary fake Mods roots used.")
} finally {
    Set-Item Function:Set-InstallPayloadFile $originalPayload
    Set-Item Function:Write-InstallNewFile $originalWrite
    Set-Item Function:Assert-InstallGameClosed $originalProcess
    Set-Item Function:Invoke-ModInstallRecovery $originalRecovery
    $canonical = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $canonical.StartsWith($temporaryRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($canonical) -notmatch '^stardeus-mod-install-tests-[0-9a-f]{32}$') { throw 'Unsafe fixture cleanup path.' }
    Assert-InstallNoReparse $canonical
    foreach($item in Get-ChildItem -LiteralPath $canonical -Recurse -Force) { Assert-InstallNoReparse $item.FullName }
    Remove-Item -LiteralPath $canonical -Recurse -Force
}
