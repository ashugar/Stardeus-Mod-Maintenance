#requires -Version 7.2
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../Common.ps1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$work=New-ModPrivateDirectory $repo
$tool=Join-Path $PSScriptRoot '../New-ModValidationRecord.ps1'
$logTool=Join-Path $PSScriptRoot '../Analyze-ModLog.ps1'
$mod=Join-Path $work 'mod';$logs=Join-Path $work 'logs'
$null=[IO.Directory]::CreateDirectory($mod);$null=[IO.Directory]::CreateDirectory($logs)
$utf8=[Text.UTF8Encoding]::new($false);$checks=0
function Check([bool]$ok,[string]$why){if(!$ok){throw "FAIL: $why"};$script:checks++}
function SaveJson($o,[string]$p){[IO.File]::WriteAllText($p,($o|ConvertTo-Json -Depth 64),$utf8)}
function Clone($o){$o|ConvertTo-Json -Depth 64|ConvertFrom-Json -Depth 64}
function Hash([string]$p){(Get-FileHash -LiteralPath $p).Hash}
function StringHash([string]$s){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($s)))}
$manifest=Join-Path $mod 'ModInfo.json';SaveJson @{Id='FixtureMod';ModVersion='1.2.3'} $manifest
$dll=Join-Path $mod 'Fixture.dll';[IO.File]::WriteAllText($dll,'synthetic bytes; never executed',$utf8)
$support=Join-Path $work 'support-source.txt';[IO.File]::WriteAllText($support,'synthetic provenance',$utf8)
$log=Join-Path $logs 'Player.log';[IO.File]::WriteAllText($log,"Initialize engine version: fixture`n[0.00m|0.00d|-1] Stardeus version: v0.15.2.3243`n",$utf8)
$boundary=[DateTime]::UtcNow.AddMinutes(-2).ToString('o')
$logProfile=@{SchemaVersion=1;AnalysisId='FixtureLog';Subject=@{ModId='FixtureMod';Root=$mod};Format='Stardeus';SourceLabel='Synthetic';LogRoot=$logs;LogFile='Player.log';OutputRoot=(Join-Path $work 'log-reports');ProtectedRoots=@($mod);TimestampTimeZoneId=$null;Expectations=@{NotBeforeUtc=$boundary;GameVersion='v0.15.2.3243'};Limits=@{MaxInputBytes=67108864;MaxLines=250000;MaxGroups=10000;MaxExamplesPerGroup=2;MaxOccurrenceLines=200;ContextBefore=2;ContextAfter=4;MaxStackLines=40;MaxAnalysisLineChars=4096;MaxOutputTextChars=4194304;MaxAggregateInputBytes=268435456};AuxiliaryInputs=@()}
$lp=Join-Path $work 'log-profile.json';SaveJson $logProfile $lp
$lr=& $logTool -ProfilePath $lp
$logReport=Get-Content -LiteralPath $lr.ReportPath -Raw|ConvertFrom-Json -Depth 64
function Profile([string]$type,[string]$path,[string]$context,[string]$stage){
    $artifactPath=if($type -eq 'LogAnalysis'){$log}else{$dll}
    @{SchemaVersion=1;RecordId='DiagnosticFixture';Subject=@{Name='Fixture';ModId='FixtureMod';Version='1.2.3';CanonicalRoot=$mod;Task='Synthetic diagnostic handoffs'};SubjectRoots=@();NotBeforeUtc=$boundary;Artifacts=@(@{Key='Selected';Path=$artifactPath;SHA256=(Hash $artifactPath);Role=$(if($type -eq 'LogAnalysis'){'Reference'}else{'Payload'})});Evidence=@(@{Key='Diagnostic';Type=$type;Path=$path;SHA256=(Hash $path);Scope='Current';Required=$true;ExpectedContextId=$context;BindMode='ExactPathAndHash';RequiredArtifactKeys=@('Selected')});Attestations=@();RequiredStages=@($stage);OutputRoot=(Join-Path $work 'records');ProtectedRoots=@($logs);CopyEvidence=$true}
}
function Run($p){$path=Join-Path $work ('record-profile-'+[guid]::NewGuid().ToString('N')+'.json');SaveJson $p $path;$script:last=& $tool -ProfilePath $path -ReportOnly;Get-Content -LiteralPath $last.RecordPath -Raw|ConvertFrom-Json -Depth 64}
function Mutated($report,[string]$type,[string]$context,[string]$stage,[scriptblock]$change,[switch]$Reject){
    $copy=Clone $report;& $change $copy
    $path=Join-Path $work ('crafted-'+[guid]::NewGuid().ToString('N')+'.json');SaveJson $copy $path
    $r=Run (Profile $type $path $context $stage)
    if($Reject){Check ($last.RejectedEvidence -eq 1) 'crafted contradictory report rejected'}
    return $r
}
$p=Profile 'LogAnalysis' $lr.ReportPath 'FixtureLog' 'LogReview'
$r=Run $p
Check ($last.AcceptedEvidence -eq 1) 'actual log producer accepted'
Check ($r.OpenRequiredStages -contains 'LogReview') 'analysis never completes human log review'
Check (@($r.Stages|Where-Object {$_.Stage -eq 'LogReview' -and $_.Details -contains 'AnalysisRecorded'}).Count -eq 1) 'analysis recorded separately'
foreach($stage in @('Startup','Gameplay','SaveReload','UserAcceptance')){Check (@($r.Stages|Where-Object {$_.Stage -eq $stage -and $_.Status -eq 'Pending'}).Count -eq 1) 'log never promotes runtime stages'}
$p=Profile 'LogAnalysis' $lr.ReportPath 'FixtureLog' 'LogReview'
$p.Attestations=@(@{Stage='LogReview';Status='ReportedPassed';Details='Agent reviewed the exact captured native log; wall-clock freshness remains unknown. No startup or gameplay claim.';Reporter='FixtureAgent';ReporterRole='Agent';SourceNote='Synthetic exact capture review';ObservedUtc=[DateTime]::UtcNow.ToString('o');EvidenceKeys=@('Diagnostic');ArtifactBindings=@(@{Key='Selected';SHA256=(Hash $log)})})
$r=Run $p
Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages.Count -eq 0) 'explicit attributed log review completes review only'
foreach($stage in @('Startup','Gameplay')){Check (@($r.Stages|Where-Object {$_.Stage -eq $stage -and $_.Status -eq 'Pending'}).Count -eq 1) 'manual log review leaves runtime pending'}
$p.Attestations[0].Status='ReportedFailed'
$r=Run $p;Check ($r.OpenRequiredStages -contains 'LogReview') 'explicit failed log review retained open'
$p.Attestations[0].Status='ReportedPassed'
$p.Evidence[0].Type='Attachment';$p.Evidence[0].RequiredArtifactKeys=@()
$failed=$false;try{$null=Run $p}catch{$failed=$true};Check $failed 'arbitrary attachment cannot support log review'
$p=Profile 'LogAnalysis' $lr.ReportPath 'FixtureLog' 'LogReview'
$p.Artifacts[0].Role='Package'
# Package role avoids payload-root checks but cannot stand in for the reviewed log Reference.
$p.Attestations=@(@{Stage='LogReview';Status='ReportedPassed';Details='Review without required reference';Reporter='FixtureAgent';ReporterRole='Agent';SourceNote='Synthetic';ObservedUtc=[DateTime]::UtcNow.ToString('o');EvidenceKeys=@('Diagnostic');ArtifactBindings=@(@{Key='Selected';SHA256=(Hash $log)})})
$failed=$false;try{$null=Run $p}catch{$failed=$true};Check $failed 'log review needs a Reference artifact'
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.ToolSHA256='malformed'} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.ToolSHA256='A'*64} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.SupportInputs=@()} -Reject
$logSidecar=Join-Path $work 'log-support-sidecar.txt';[IO.File]::WriteAllText($logSidecar,'private synthetic log support',$utf8)
$withSupport=Clone $logReport;$withSupport.SupportInputs+=@([pscustomobject]@{Path=$logSidecar;SHA256=(Hash $logSidecar)})
$sidecarReport=Join-Path $work 'log-with-support.json';SaveJson $withSupport $sidecarReport
$null=Run (Profile 'LogAnalysis' $sidecarReport 'FixtureLog' 'LogReview');Check ($last.AcceptedEvidence -eq 1) 'unchanged additional log support accepted'
[IO.File]::AppendAllText($logSidecar,'changed',$utf8)
$null=Run (Profile 'LogAnalysis' $sidecarReport 'FixtureLog' 'LogReview');Check ($last.RejectedEvidence -eq 1) 'changed log support footprint rejected'
$p=Profile 'LogAnalysis' $lr.ReportPath 'WrongContext' 'LogReview';$null=Run $p;Check ($last.RejectedEvidence -eq 1) 'wrong context'
$p=Profile 'LogAnalysis' $lr.ReportPath 'FixtureLog' 'LogReview';$p.Evidence[0].SHA256='A'*64;$null=Run $p;Check ($last.RejectedEvidence -eq 1) 'wrong report pin'
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.Subject.ModId='Wrong'} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.Subject.Root=$logs} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.CreatedUtc='2000-01-01T00:00:00Z'} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.Configuration.EffectiveExpectations.NotBeforeUtc='2000-01-01T00:00:00Z'} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.Current.RecognizedOccurrences=100} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.State='Analyzed'} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.Inputs=@()} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.Inputs[0].Status='OptionalMissing'} -Reject
$null=Mutated $logReport 'LogAnalysis' 'FixtureLog' 'LogReview' {param($x)$x.Inputs+=@($x.Inputs[0])} -Reject
$p=Profile 'LogAnalysis' $lr.ReportPath 'FixtureLog' 'LogReview';$p.Evidence[0].RequiredArtifactKeys=@();$null=Run $p;Check ($last.RejectedEvidence -eq 1) 'log explicit binding required'
$p=Profile 'LogAnalysis' $lr.ReportPath 'FixtureLog' 'LogReview';$p.Evidence[0].Scope='Historical';$r=Run $p;Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'LogReview') 'historical log retained without elevation'
[IO.File]::AppendAllText($log,"ERROR: after report`n",$utf8)
$p=Profile 'LogAnalysis' $lr.ReportPath 'FixtureLog' 'LogReview';$null=Run $p;Check ($last.RejectedEvidence -eq 1) 'changed source footprint rejected'
[IO.File]::WriteAllBytes($log,[IO.File]::ReadAllBytes((Join-Path $lr.RunPath 'current.log')))
$inputs=@(@{Path=$manifest;Status='Present';SHA256=(Hash $manifest)},@{Path=$dll;Status='Present';SHA256=(Hash $dll)})
$provenance=@(@{Path=$support;SHA256=(Hash $support)})
$api=@{SchemaVersion=1;Kind='StardeusApiAudit';CheckId='FixtureApi';CreatedUtc=[DateTime]::UtcNow.ToString('o');State='CheckedSupportedScope';Subject=@{ModId='FixtureMod';Root=$mod};Inputs=$inputs;ProfileSHA256=(Hash $support);ToolSHA256=(Hash $support);RunnerSHA256=(Hash $support);SupportSHA256=(Hash $support);EngineSHA256=(Hash $support);EngineSources=$provenance;SupportInputs=$provenance;References=@(@{Assembly='Fixture';Kind='Type';Identity='Fixture.Type';Status='Resolved';Detail='synthetic'});Patches=@();Coverage=@{SubjectAssemblies=1;ScopedTypes=1;ScopedMembers=0;FixedTargets=0;BindingChecks=0;DynamicProviders=0;ManualRegistrationReferences=0;Transpilers=0};Gaps=@();Findings=@()}
$ap=Join-Path $work 'crafted-api.json';SaveJson $api $ap
$r=Run (Profile 'ApiAudit' $ap 'FixtureApi' 'ReferenceChecks');Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages.Count -eq 0) 'internally consistent API state completes only scoped reference stage'
foreach($stage in @('Startup','Gameplay')){Check (@($r.Stages|Where-Object {$_.Stage -eq $stage -and $_.Status -eq 'Pending'}).Count -eq 1) 'API does not promote runtime'}
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.Gaps=@('unsupported')} -Reject
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.Patches=@([pscustomobject]@{Patch='FixturePatch';Target='Fixture.Target';Status='Resolved';TargetStatus='Resolved';BindingStatus='NotRun';Detail='synthetic'});$x.Coverage.FixedTargets=1} -Reject
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.Patches=@([pscustomobject]@{Patch='FixturePatch';Target='Fixture.Target';Status='Resolved';TargetStatus='MissingTarget';BindingStatus='NotRun';Detail='synthetic'});$x.Coverage.FixedTargets=1} -Reject
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.Findings=@('missing')} -Reject
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.References[0].Status='MissingType'} -Reject
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.Coverage.ScopedTypes=2} -Reject
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.Coverage.Transpilers=1} -Reject
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.State='CheckedWithGaps'} -Reject
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.Inputs=@($x.Inputs[1])} -Reject
$null=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.Subject.ModId='Other'} -Reject
$r=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.State='CheckedWithGaps';$x.Gaps=@('unsupported')};Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'ReferenceChecks') 'gapped API accepted open'
$r=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.State='Failed';$x.Findings=@('binding failed')};Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'ReferenceChecks') 'failed API accepted open'
$r=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.References=@();$x.Coverage.ScopedTypes=0};Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'ReferenceChecks') 'empty API scope not completed'
$r=Mutated $api 'ApiAudit' 'FixtureApi' 'ReferenceChecks' {param($x)$x.State='Failed';$x.References=@();$x.Coverage=[pscustomobject]@{};$x.Gaps=@('BadImageFormatException')};Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'ReferenceChecks') 'global API failure retained'
$side=@{Status='Read';Text='contract';Hash=(StringHash 'contract');Reason=$null}
$contract=@{SchemaVersion=1;Kind='StardeusContractComparison';ComparisonId='FixtureContracts';CreatedUtc=[DateTime]::UtcNow.ToString('o');State='UnchangedInSelectedScope';Subject=@{ModId='FixtureMod';Root=$mod};Inputs=$inputs;ProfileSHA256=(Hash $support);ToolSHA256=(Hash $support);RunnerSHA256=(Hash $support);SupportSHA256=(Hash $support);EngineSHA256=(Hash $support);EngineSources=$provenance;SupportInputs=$provenance;Selections=@(@{Key='SelectedMethod';Kind='Method';Type='Fixture.Type';Status='Unchanged';Before=$side;After=$side});Coverage=@{Selected=1;Unchanged=1;Changed=0;Unresolved=0};Gaps=@();Findings=@()}
$cp=Join-Path $work 'crafted-contract.json';SaveJson $contract $cp
$r=Run (Profile 'Contracts' $cp 'FixtureContracts' 'ContractComparison');Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages.Count -eq 0) 'internally consistent selected contract accepted'
Check (@($r.Stages|Where-Object {$_.Stage -eq 'UpstreamComparison' -and $_.Status -eq 'Pending'}).Count -eq 1) 'contract comparison distinct from upstream semantics'
$null=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.Gaps=@('unsupported')} -Reject
$null=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.Findings=@('change')} -Reject
$null=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.Coverage.Selected=2} -Reject
$null=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.Selections[0].After.Hash='A'*64} -Reject
$null=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.Selections[0].After.Text='different';$x.Selections[0].After.Hash=StringHash 'different'} -Reject
$null=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.Selections=@()} -Reject
$null=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.Selections[0].Before.Status='Unresolved'} -Reject
$null=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.State='ChangesFound'} -Reject
$r=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.State='ChangesFound';$x.Selections[0].Status='Changed';$x.Selections[0].After.Text='different';$x.Selections[0].After.Hash=StringHash 'different';$x.Coverage.Unchanged=0;$x.Coverage.Changed=1;$x.Findings=@('SelectedMethod changed')};Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'ContractComparison') 'changed contracts retained open'
$r=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.State='ComparedWithGaps';$x.Selections[0].Status='Unresolved';$x.Selections[0].After.Status='Unresolved';$x.Selections[0].After.Text=$null;$x.Selections[0].After.Hash=$null;$x.Coverage.Unchanged=0;$x.Coverage.Unresolved=1;$x.Gaps=@('SelectedMethod missing')};Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'ContractComparison') 'unresolved contracts retained open'
$r=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.State='Failed';$x.Selections=@();$x.Coverage=[pscustomobject]@{};$x.Gaps=@('BadImageFormatException: malformed PE')};Check ($last.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'ContractComparison') 'fatal contract producer failure retained open'
$null=Mutated $contract 'Contracts' 'FixtureContracts' 'ContractComparison' {param($x)$x.State='Failed';$x.Selections=@();$x.Coverage=[pscustomobject]@{}} -Reject
[IO.File]::AppendAllText($support,'changed',$utf8)
$null=Run (Profile 'ApiAudit' $ap 'FixtureApi' 'ReferenceChecks');Check ($last.RejectedEvidence -eq 1) 'changed diagnostic engine source rejected'
[IO.File]::WriteAllText($support,'synthetic provenance',$utf8)
$result=[pscustomobject]@{Kind='StardeusDiagnosticHandoffTests';State='Passed';Assertions=$checks;CreatedUtc=[DateTime]::UtcNow.ToString('o');WorkPath=$work;ToolSHA256=(Hash $tool);SupportSHA256=(Hash (Join-Path $PSScriptRoot '../Common.ps1'));LogToolSHA256=(Hash $logTool);TestSHA256=(Hash $PSCommandPath);Limitations=@('Actual isolated log producer plus intentionally crafted metadata reports; actual metadata producer handoffs require separate evidence.','No real game, player data, startup or gameplay validation.')}
$receipt=Join-Path $work 'result.json';SaveJson $result $receipt
[pscustomobject]@{State='Passed';Assertions=$checks;ReceiptPath=$receipt;ReceiptSHA256=(Hash $receipt)}
