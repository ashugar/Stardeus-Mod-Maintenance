#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApiReportPath,[Parameter(Mandatory)][string]$ContractReportPath,[Parameter(Mandatory)][string]$NotBeforeUtc)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../Common.ps1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'));$work=New-ModPrivateDirectory $repo;$count=0
function Check($ok,[string]$why){if(!$ok){throw $why};$script:count++}
function Json([string]$p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json -Depth 64}
$tool=Join-Path $PSScriptRoot '../New-ModValidationRecord.ps1'
foreach($entry in @(@{Type='ApiAudit';Path=$ApiReportPath;Id='CheckId';Stage='ReferenceChecks'},@{Type='Contracts';Path=$ContractReportPath;Id='ComparisonId';Stage='ContractComparison'})){
 $report=Json $entry.Path;$artifacts=@();$index=0
 foreach($inputFile in $report.Inputs){$index++;$artifacts+=@{Key=('File'+$index);Path=$inputFile.Path;SHA256=$inputFile.SHA256;Role='Reference'}}
 $p=@{SchemaVersion=1;RecordId=('Handoff'+$entry.Type);Subject=@{Name=$report.Subject.ModId;ModId=$report.Subject.ModId;Version='fixture';CanonicalRoot=$report.Subject.Root;Task='Actual diagnostic producer handoff'};SubjectRoots=@();NotBeforeUtc=$NotBeforeUtc;Artifacts=$artifacts;Evidence=@(@{Key='Actual';Type=$entry.Type;Path=$entry.Path;SHA256=(Get-FileHash -LiteralPath $entry.Path).Hash;Scope='Current';Required=$true;ExpectedContextId=$report.($entry.Id);BindMode='ExactPathAndHash';RequiredArtifactKeys=@($artifacts|ForEach-Object Key)});Attestations=@();RequiredStages=@($entry.Stage,'Startup','Gameplay');OutputRoot=(Join-Path $work $entry.Type);ProtectedRoots=@();CopyEvidence=$true}
 $profilePath=Join-Path $work ($entry.Type+'-profile.json');$p|ConvertTo-Json -Depth 24|Set-Content -LiteralPath $profilePath
 $summary=& $tool -ProfilePath $profilePath -ReportOnly;$validation=Json $summary.RecordPath
 Check ($summary.AcceptedEvidence -eq 1 -and $summary.RejectedEvidence -eq 0) ('actual '+$entry.Type+' producer accepted')
 Check (@($validation.Stages|Where-Object {$_.Stage -eq $entry.Stage -and $_.Status -eq 'EvidenceRecorded'}).Count -eq 1) 'only intended diagnostic stage recorded'
 Check (@($validation.Stages|Where-Object {$_.Stage -in @('Startup','Gameplay') -and $_.Status -eq 'Pending'}).Count -eq 2) 'actual static producer does not elevate runtime'
 $p.RequiredStages=@($entry.Stage);$p|ConvertTo-Json -Depth 24|Set-Content -LiteralPath $profilePath
 $gate=& $tool -ProfilePath $profilePath -RequireComplete
 Check ($gate.State -eq 'Recorded') 'actual successful supported/unchanged scope completes only its explicit stage'
 $p.Evidence[0].ExpectedContextId='WrongContext';$p|ConvertTo-Json -Depth 24|Set-Content -LiteralPath $profilePath;$bad=& $tool -ProfilePath $profilePath -ReportOnly
 Check ($bad.RejectedEvidence -eq 1) 'actual producer wrong context rejected'
}
$result=@{State='Passed';Assertions=$count;CreatedUtc=[DateTime]::UtcNow.ToString('o');Root=$work;ApiReport=$ApiReportPath;ApiSHA256=(Get-FileHash -LiteralPath $ApiReportPath).Hash;ContractReport=$ContractReportPath;ContractSHA256=(Get-FileHash -LiteralPath $ContractReportPath).Hash;ToolSHA256=(Get-FileHash -LiteralPath $tool).Hash;TestSHA256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;Limitations='Explicit synthetic producer handoffs; no game/mod code execution or runtime acceptance.'}
$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $work 'result.json');$result
