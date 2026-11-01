#requires -Version 7.2
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$tool=Join-Path $PSScriptRoot '../Analyze-ModLog.ps1'
$common=Join-Path $PSScriptRoot '../Common.ps1'
. $common
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$work=New-ModPrivateDirectory $repo
$logs=Join-Path $work 'inputs'; $output=Join-Path $work 'outputs'; $subject=Join-Path $work 'subject'
$null=[IO.Directory]::CreateDirectory($logs); $null=[IO.Directory]::CreateDirectory($subject)
$utf8=[Text.UTF8Encoding]::new($false)
$checks=0
function Check([bool]$ok,[string]$message) { if (!$ok) {throw "FAIL: $message"}; $script:checks++ }
function Save([string]$name,[string]$text) {[IO.File]::WriteAllText((Join-Path $logs $name),$text,$utf8)}
function Profile {
    [ordered]@{SchemaVersion=1;AnalysisId='Fixture';Subject=@{ModId='FixtureMod';Root=$subject};Format='Stardeus';SourceLabel='Fixture';LogRoot=$logs;LogFile='current.log';OutputRoot=$output;ProtectedRoots=@($subject);TimestampTimeZoneId=$null;Expectations=@{NotBeforeUtc='2026-01-01T00:00:00Z';GameVersion='v0.15.2.3243'};Limits=@{MaxInputBytes=67108864;MaxLines=250000;MaxGroups=10000;MaxExamplesPerGroup=2;MaxOccurrenceLines=200;ContextBefore=2;ContextAfter=4;MaxStackLines=40;MaxAnalysisLineChars=4096;MaxOutputTextChars=4194304;MaxAggregateInputBytes=268435456};AuxiliaryInputs=@()}
}
function Run($p,$previous=$null) {
    $path=Join-Path $work ('profile-'+[guid]::NewGuid().ToString('N')+'.json')
    [IO.File]::WriteAllText($path,($p|ConvertTo-Json -Depth 16),$utf8)
    $result=if ($previous) {& $tool -ProfilePath $path -PreviousLogPath $previous} else {& $tool -ProfilePath $path}
    $script:lastSummary=$result
    Get-Content -LiteralPath $result.ReportPath -Raw | ConvertFrom-Json -Depth 64
}
function Reject($p,[string]$why) { $failed=$false;try {$null=Run $p} catch {$failed=$true};Check $failed $why }
$native="Initialize engine version: 6000.5.11f1 (fixture)`n[0.00m|0.00d|-1] Stardeus version: v0.15.2.3243`n"
Save 'current.log' ($native+"[1.00m|2.00d|3] System.InvalidOperationException: broken`n  at Fixture.One ()`n[2.00m|2.00d|4] System.InvalidOperationException: broken`n  at Fixture.One ()`n[2.01m|2.00d|4] ShowMissingPartsWarning: True`n[2.02m|2.00d|4] System.InvalidOperationException: other`n  at Fixture.Two ()`n")
$original=(Get-FileHash -LiteralPath (Join-Path $logs 'current.log')).Hash
$r=Run (Profile)
Check ($r.Kind -ceq 'StardeusLogAnalysis') 'kind'
Check ($r.SupportInputs.Count -eq 2 -and @($r.SupportInputs | Where-Object SHA256 -CEQ $r.ToolSHA256).Count -eq 1 -and @($r.SupportInputs | Where-Object SHA256 -CEQ $r.CommonSHA256).Count -eq 1) 'producer and Common support footprints'
Check ($r.Current.DistinctIssues -eq 2 -and $r.Current.RecognizedOccurrences -eq 3) 'repeat counts and unrelated message separation'
Check ($r.Current.Groups[0].Count -eq 2) 'repeat grouping'
Check ($r.Current.Freshness.State -eq 'Unknown') 'elapsed-only clock never fresh'
Check ($r.Current.SelectedSession.VersionClaims[0].Value -eq 'v0.15.2.3243') 'native version'
Check ($r.Inputs[0].SHA256 -eq $original) 'input pin'
Check ((Get-FileHash -LiteralPath (Join-Path $lastSummary.RunPath 'current.log')).Hash -eq $original) 'exact retained input'
Check ((Get-FileHash -LiteralPath (Join-Path $logs 'current.log')).Hash -eq $original) 'source unchanged'
Check ((Get-FileHash -LiteralPath $lastSummary.ReportPath).Hash -eq $lastSummary.ReportSHA256) 'full report pin'
Save 'previous.log' ($native+"ERROR: previous`n")
$r=Run (Profile) (Join-Path $logs 'previous.log')
Check ($r.Inputs.Count -eq 2) 'previous footprint'
Check ($r.Comparison.NewInComparedInput.Count -eq 2 -and $r.Comparison.NotObservedInCurrentInput.Count -eq 1) 'presence comparison'
Save 'current.log' ($native+"ERROR: earlier`n"+$native+"WARNING: latest`n")
$r=Run (Profile)
Check ($r.Current.Sessions.Count -eq 2 -and $r.Current.Groups.Count -eq 1) 'last session only'
Check ($r.Current.Groups[0].Message -eq 'WARNING: latest') 'earlier exclusion'
Check ($r.Current.Groups[0].Examples[0].Context[0].Line -ge $r.Current.SelectedSession.StartLine) 'context boundary'
Save 'current.log' ($native+"[0.01m|0.00d|-1] Stardeus Version: v0.15.2.3244 [2026.10.31]`n")
$r=Run (Profile);Check ($r.Current.Freshness.State -eq 'ConflictingSessionEvidence') 'conflicting versions'
Save 'current.log' ($native.Replace('3243','3244'))
$r=Run (Profile);Check ($r.Current.Freshness.State -eq 'VersionMismatch') 'wrong version'
Save 'current.log' "ERROR: no boundary`n"
$r=Run (Profile);Check (!$r.Current.SelectedSession.HasStartupMarker -and $r.State -eq 'AnalyzedWithGaps') 'unknown session'
Save 'current.log' ($native+"ERROR: cap`n at Fixture.One ()`n at Fixture.Two ()`nERROR: cap`n at Fixture.One ()`n at Fixture.Two ()`n")
$p=Profile;$p.Limits.MaxExamplesPerGroup=1;$p.Limits.MaxOccurrenceLines=1;$p.Limits.MaxStackLines=1
$r=Run $p
Check ($r.Current.Groups[0].Count -eq 2 -and $r.Current.Groups[0].OmittedLocations -eq 1) 'capped locations exact counts'
Check ($r.Current.Groups[0].Examples.Count -eq 1 -and $r.Current.Groups[0].Examples[0].StackTruncated) 'bounded stack/examples'
Check ($r.State -eq 'AnalyzedWithGaps') 'retention gaps explicit'
Save 'auxiliary.log' "2026-10-31 04:03:22.547 tick=0 started version=0.1.8`n2026-10-31 04:03:23.547 ERROR: auxiliary`n"
$p=Profile;$p.AuxiliaryInputs=@(@{SourceLabel='Logger';LogRoot=$logs;LogFile='auxiliary.log';TimestampTimeZoneId='UTC'})
$r=Run $p;Check ($r.Inputs.Count -eq 2 -and $r.Auxiliary.Count -eq 1) 'explicit auxiliary footprint'
Check ($r.Auxiliary[0].SelectedSession.VersionClaims.Count -eq 0 -and $r.Auxiliary[0].Freshness.State -eq 'Unknown') 'logger version not game version'
Check ($r.Current.Groups.Count -eq 1 -and $r.Auxiliary[0].Groups.Count -eq 1) 'auxiliary independent'
$p=Profile;$p.AuxiliaryInputs=@(@{SourceLabel='Duplicate';LogRoot=$logs;LogFile='current.log';TimestampTimeZoneId=$null});Reject $p 'duplicate source'
$p=Profile;$p.Extra='bad';Reject $p 'unknown key'
$p=Profile;$p.Remove('Subject');Reject $p 'missing key'
$p=Profile;$p.LogFile='../escape';Reject $p 'traversal'
$p=Profile;$p.OutputRoot=$logs;Reject $p 'input output overlap'
$p=Profile;$p.OutputRoot=$repo;Reject $p 'repository output refused'
$p=Profile;$p.Expectations.NotBeforeUtc='2026-01-01';Reject $p 'offset required'
$p=Profile;$p.Limits.MaxLines=1;Reject $p 'line bound'
$p=Profile;$p.Limits.MaxInputBytes=1;Reject $p 'byte bound'
$p=Profile;$p.Limits.MaxGroups=1
Save 'current.log' ($native+"ERROR: one`nERROR: two`n");Reject $p 'group bound'
$p=Profile;$p.SchemaVersion=2;Reject $p 'schema refused'
$dup=Join-Path $work 'duplicate.json';[IO.File]::WriteAllText($dup,'{"SchemaVersion":1,"schemaversion":1}',$utf8)
$failed=$false;try {$null=& $tool -ProfilePath $dup} catch {$failed=$true};Check $failed 'duplicate JSON keys'
# Directory junctions require no developer mode on Windows; retain them in private fixtures.
$link=Join-Path $work 'linked';$null=New-Item -ItemType Junction -Path $link -Target $logs
$p=Profile;$p.LogRoot=$link;Reject $p 'reparse input refused'
Save 'current.log' ($native+('x'*5000)+"`nERROR: retained`n")
$r=Run (Profile);Check ($r.Current.TruncatedAnalysisLines -eq 1 -and $r.State -eq 'AnalyzedWithGaps') 'long analysis line truncated with gap'
$p=Profile;$p.Limits.MaxOutputTextChars=1024
Save 'current.log' ($native+'ERROR: '+('x'*2000)+"`n");Reject $p 'output text budget'
$p=Profile;$p.Limits.MaxAggregateInputBytes=1
Save 'current.log' $native;Reject $p 'aggregate byte budget'
$p=Profile;$p.Limits.MaxAggregateInputBytes=([Text.Encoding]::UTF8.GetByteCount($native)+10);$p.AuxiliaryInputs=@(@{SourceLabel='Extra';LogRoot=$logs;LogFile='auxiliary.log';TimestampTimeZoneId='UTC'});Reject $p 'aggregate includes auxiliary'
$p=Profile;$p.Limits.MaxAnalysisLineChars=127;Reject $p 'analysis line bound validated'
$hugeProfile=Join-Path $work 'huge-profile.json';[IO.File]::WriteAllText($hugeProfile,(' '*4194305),$utf8)
$failed=$false;try {$null=& $tool -ProfilePath $hugeProfile} catch {$failed=$true};Check $failed 'profile byte bound'
Save 'current.log' $native
(Get-Item -LiteralPath (Join-Path $logs 'current.log')).LastWriteTimeUtc=[datetime]::new(2000,1,1,0,0,0,[DateTimeKind]::Utc)
$r=Run (Profile);Check ($r.Current.Freshness.State -eq 'Unknown') 'stale mtime not session time'
Save 'auxiliary.log' "2026-11-01 01:30:00.000 ERROR: ambiguous`n2026-03-08 02:30:00.000 ERROR: nonexistent`n2026-99-99 99:99:99.999 ERROR: invalid`n"
$p=Profile;$p.AuxiliaryInputs=@(@{SourceLabel='Clocks';LogRoot=$logs;LogFile='auxiliary.log';TimestampTimeZoneId='Central Standard Time'})
$r=Run $p;Check ($r.Auxiliary[0].SelectedSession.InvalidTimestampLines -eq 3) 'ambiguous invalid missing local clocks'
Check ($r.Auxiliary[0].Groups[0].Examples[0].Context[0].TimestampUtc -eq $null) 'ambiguous clock not guessed'
Save 'current.log' ''
$r=Run (Profile);Check ($r.State -eq 'AnalyzedWithGaps' -and $r.Current.IssueState -eq 'NoRecognizedIssues') 'empty is incomplete not clean pass'
Save 'current.log' ($native+"[0.01m|0.00d|-1] Stardeus Version: v0.15.2.3243 [2026.10.31]`n")
$r=Run (Profile);Check ($r.Current.SelectedSession.BuildClaims[0].Value -eq '2026.10.31') 'build date retained separately'
Check ($r.ProfileSHA256 -match '^[A-F0-9]{64}$') 'profile pinned'
Save 'current.log' ($native*101)
Reject (Profile) 'session inventory bound'
# Keep a synthetic input changing throughout both capture passes; never touch real logs.
Save 'current.log' ($native+((('x'*1000)+"`n")*20000))
$changingPath=Join-Path $logs 'current.log';$ready=Join-Path $work 'writer-ready'
$job=Start-Job -ArgumentList $changingPath,$ready -ScriptBlock {
    param($path,$signal)
    $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        [IO.File]::WriteAllText($signal,'ready');$until=[DateTime]::UtcNow.AddSeconds(30);$value=0
        while ([DateTime]::UtcNow -lt $until) { $stream.Position=$stream.Length-2;$stream.WriteByte([byte](120+($value%2)));$stream.Flush($true);$value++;[Threading.Thread]::Sleep(1) }
    } finally {$stream.Dispose()}
}
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while (!(Test-Path -LiteralPath $ready) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 20 }
    Check (Test-Path -LiteralPath $ready) 'synthetic writer ready'
    $r=Run (Profile)
    Check ($r.Current.Input.ReadState -eq 'ChangedDuringRead') 'changing source marked incomplete'
    Check ((Get-FileHash -LiteralPath (Join-Path $lastSummary.RunPath 'current.log')).Hash -eq $r.Inputs[0].SHA256) 'changing source exact captured bytes pinned'
} finally { Stop-Job $job;Remove-Job $job }
$receipt=[pscustomobject]@{Kind='StardeusLogAnalyzerTests';CreatedUtc=[DateTime]::UtcNow.ToString('o');State='Passed';Assertions=$checks;ToolSHA256=(Get-FileHash -LiteralPath $tool).Hash;CommonSHA256=(Get-FileHash -LiteralPath $common).Hash;TestSHA256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;WorkPath=$work;Limitations=@('Synthetic isolated fixtures only; no real startup/gameplay or player data validation.')}
$receiptPath=Join-Path $work 'result.json';[IO.File]::WriteAllText($receiptPath,($receipt|ConvertTo-Json -Depth 8),$utf8)
[pscustomobject]@{State='Passed';Assertions=$checks;ReceiptPath=$receiptPath;ReceiptSHA256=(Get-FileHash -LiteralPath $receiptPath).Hash}
