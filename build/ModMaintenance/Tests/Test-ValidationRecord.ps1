#requires -Version 7.2
[CmdletBinding()]
param([string]$TestRoot=(Join-Path ([IO.Path]::GetTempPath()) ('stardeus-validation-'+[guid]::NewGuid().ToString('N'))))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../Common.ps1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Assert-ModExternalPath $repo $TestRoot
if(Test-Path -LiteralPath $TestRoot){throw 'TestRoot must be new.'}
$null=[IO.Directory]::CreateDirectory($TestRoot)
$tool=Join-Path $PSScriptRoot '../New-ModValidationRecord.ps1'
$checks=0
function Check([bool]$ok,[string]$message){if(!$ok){throw $message};$script:checks++}
function WriteJson($value,[string]$path){$value | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $path -Encoding utf8}
function Clone($o){return ($o | ConvertTo-Json -Depth 64 | ConvertFrom-Json -Depth 64)}
function FixtureHash([string]$p){return (Get-FileHash -LiteralPath $p).Hash}
$mod=Join-Path $TestRoot 'mod';$game=Join-Path $TestRoot 'game';$null=[IO.Directory]::CreateDirectory($mod);$null=[IO.Directory]::CreateDirectory($game)
$manifest=Join-Path $mod 'ModInfo.json';WriteJson @{Id='Example';Name='Example';ModVersion='1.2.3';GameVersion='0.15.2';Atlas=@{MipMapBias=-0.2}} $manifest
[IO.File]::WriteAllText((Join-Path $game 'version.dll'),'Not a managed DLL')
$boundary=[DateTime]::UtcNow.AddMinutes(-2).ToString('o')
$bp=@{SchemaVersion=1;BaselineId='fixture';OutputRoot=(Join-Path $TestRoot 'baselines');Game=@{Root=$game;VersionAssembly='version.dll';VersionType=$null;VersionField=$null;Files=@('version.dll');Trees=@();ExcludeDirectories=@();ProcessNames=@()};Mods=@(@{Key='primary';Role='Primary';Root=$mod;ExpectedId='Example';Manifest='ModInfo.json';Files=@('ModInfo.json');Trees=@();ExcludeDirectories=@();DependencyKeys=@()})}
$bpp=Join-Path $TestRoot 'baseline-profile.json';WriteJson $bp $bpp
$baseline=& (Join-Path $PSScriptRoot '../Record-ModBaseline.ps1') -ProfilePath $bpp
$baselinePath=$baseline.SnapshotPath
Check ([bool]$baselinePath) 'actual baseline report missing'
$vp=@{SchemaVersion=1;CheckId='fixture';OutputRoot=(Join-Path $TestRoot 'versions');ProtectedRoots=@();Roots=@(@{Key='Mod';Path=$mod});Groups=@(@{Key='Release';Expected='1.2.3'});Surfaces=@(@{Key='Manifest';Group='Release';RootKey='Mod';Path='ModInfo.json';Storage='File';Type='JsonScalar';Pointer='/ModVersion';Required=$true;Normalization='Exact'})}
$vpp=Join-Path $TestRoot 'version-profile.json';WriteJson $vp $vpp
$vr=& (Join-Path $PSScriptRoot '../Test-ModVersions.ps1') -ProfilePath $vpp
$versionPath=$vr.ReportPath
Check (Test-Path -LiteralPath $versionPath) 'actual version report missing'
Check ($vr.State -eq 'Consistent') 'fractional manifest metadata must pass producer and validation handoff'
$profile=@{SchemaVersion=1;RecordId='fixture';Subject=@{Name='Example|<tag>';ModId='Example';Version='1.2.3';CanonicalRoot=$mod;Task='Fixture'};SubjectRoots=@();NotBeforeUtc=$boundary;Artifacts=@(@{Key='Manifest';Path=$manifest;SHA256=(FixtureHash $manifest);Role='Payload'});Evidence=@(@{Key='Versions';Type='Versions';Path=$versionPath;SHA256=(FixtureHash $versionPath);Scope='Current';Required=$true;ExpectedContextId='fixture';ReleaseGroupKey='Release';BindMode='ExactPathAndHash';RequiredArtifactKeys=@('Manifest')});Attestations=@();RequiredStages=@('VersionChecks');OutputRoot=(Join-Path $TestRoot 'records');ProtectedRoots=@($game);CopyEvidence=$true}
$pp=Join-Path $TestRoot 'validation-profile.json'
function Run($p,[switch]$Gate){WriteJson $p $pp;if($Gate){return & $tool -ProfilePath $pp -RequireComplete};return & $tool -ProfilePath $pp -ReportOnly}
function Reject($p,[string]$reason){$r=Run $p;Check ($r.RejectedEvidence -eq 1) $reason;return $r}
$r=Run $profile -Gate;Check ($r.State -eq 'Recorded') 'real version handoff failed'
Check ((Get-Content -Raw $r.MarkdownPath).Contains('Example&#124;&lt;tag&gt;')) 'Markdown escaping failed'
$p=Clone $profile;$p.Evidence=@(@{Key='Baseline';Type='Baseline';Path=$baselinePath;SHA256=(FixtureHash $baselinePath);Scope='Current';Required=$true;ExpectedContextId='fixture';BindMode='ExactPathAndHash';RequiredArtifactKeys=@('Manifest')});$p.RequiredStages=@('Baseline')
$r=Run $p;Check ($r.AcceptedEvidence -eq 1) 'real baseline handoff rejected';Check ($r.OpenRequiredStages -contains 'Baseline') 'gapped baseline promoted'
$p=Clone $profile;$p.NotBeforeUtc=[DateTime]::UtcNow.AddSeconds(1).ToString('o');$null=Reject $p 'stale evidence accepted'
$p=Clone $profile;$p.Evidence[0].ExpectedContextId='wrong';$null=Reject $p 'wrong context accepted'
$p=Clone $profile;$p.Subject.Version='9.9.9';$null=Reject $p 'wrong release subject accepted'
$p=Clone $profile;$p.Subject.ModId='Wrong';$null=Reject $p 'wrong mod identity accepted'
$p=Clone $profile;$p.Evidence[0].Scope='Historical';$r=Run $p;Check ($r.OpenRequiredStages -contains 'VersionChecks') 'historical evidence elevated current stage'
$p=Clone $profile;$p.Evidence[0].RequiredArtifactKeys=@();$null=Reject $p 'empty machine binds accepted'
$p=Clone $profile;$p.Evidence[0].RequiredArtifactKeys=@('DynamicUnknown');$null=Reject $p 'unknown artifact bind accepted'
$p=Clone $profile;$p.Evidence[0].BindMode='Implicit';$null=Reject $p 'implicit binding accepted'
$p=Clone $profile;$p.Evidence[0].BindMode='HashOnly';$null=Reject $p 'hash-only binding without reason accepted'
$p.Evidence[0] | Add-Member -NotePropertyName Reason -NotePropertyValue 'Reviewed identical bytes';$r=Run $p;Check ($r.AcceptedEvidence -eq 1) 'explained hash-only binding rejected'
$p=Clone $profile;$p.Evidence[0].Type='Transfer';$null=Reject $p 'unsupported adapter accepted'
$optional=Join-Path $mod 'optional.json';$ovp=Clone $vp;$ovp.CheckId='optional';$ovp.Surfaces+=@{Key='Optional';Group='Release';RootKey='Mod';Path='optional.json';Storage='File';Type='JsonScalar';Pointer='/version';Required=$false;Normalization='Exact'}
WriteJson $ovp $vpp;$ovr=& (Join-Path $PSScriptRoot '../Test-ModVersions.ps1') -ProfilePath $vpp
$p=Clone $profile;$p.Evidence[0].Path=$ovr.ReportPath;$p.Evidence[0].SHA256=FixtureHash $ovr.ReportPath;$p.Evidence[0].ExpectedContextId='optional';$r=Run $p;Check ($r.AcceptedEvidence -eq 1) 'optional omission rejected'
WriteJson @{version='9.9.9'} $optional;$null=Reject $p 'previously missing optional input appeared'

$p=Clone $profile;$p.Evidence[0].Type='Attachment';$p.Evidence[0].RequiredArtifactKeys=@();$r=Run $p;Check ($r.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'VersionChecks') 'opaque attachment promoted stage'
$p=Clone $profile;$p.Artifacts[0].SHA256=('A'*64);$r=Run $p;Check ($r.EvidenceIssues -gt 0) 'changed artifact ignored'
$p=Clone $profile;$p.Artifacts=@();$failed=$false;try{$null=Run $p}catch{$failed=$true};Check $failed 'empty artifacts accepted'
$p=Clone $profile;$p.OutputRoot=$mod;$failed=$false;try{$null=Run $p}catch{$failed=$true};Check $failed 'subject output overlap accepted'
$p=Clone $profile;$p.OutputRoot=$repo;$failed=$false;try{$null=Run $p}catch{$failed=$true};Check $failed 'repository output accepted'
WriteJson $profile $pp;$raw=Get-Content -Raw $pp;$raw=$raw.Replace('"SchemaVersion": 1','"SchemaVersion": 1, "schemaversion": 1');Set-Content -LiteralPath $pp -Value $raw
$failed=$false;try{$null=& $tool -ProfilePath $pp}catch{$failed=$true};Check $failed 'case duplicate JSON accepted'
$report=Get-Content -Raw $versionPath | ConvertFrom-Json -Depth 64
$report.State='Failed';$report.Surfaces[0].Status='Mismatch';$fake=Join-Path $TestRoot 'failed-versions.json';WriteJson $report $fake
$p=Clone $profile;$p.Evidence[0].Path=$fake;$p.Evidence[0].SHA256=FixtureHash $fake;$r=Run $p;Check ($r.AcceptedEvidence -eq 1 -and $r.OpenRequiredStages -contains 'VersionChecks') 'negative evidence lost or promoted'
$before=@(Get-ChildItem -LiteralPath $profile.OutputRoot -Directory).Count;$failed=$false;try{$null=Run $p -Gate}catch{$failed=$true};$after=@(Get-ChildItem -LiteralPath $profile.OutputRoot -Directory).Count;Check ($failed -and $after -eq $before+1) 'gate failure did not retain report'
$report.State='Consistent';WriteJson $report $fake;$p.Evidence[0].SHA256=FixtureHash $fake;$null=Reject $p 'contradictory consistency accepted'
$p=Clone $profile;$p.Attestations=@(@{Stage='UserAcceptance';Status='ReportedPassed';Details='fixture';Reporter='Agent';ReporterRole='Agent';SourceNote='fixture';ObservedUtc=[DateTime]::UtcNow.ToString('o');EvidenceKeys=@('Versions');ArtifactBindings=@(@{Key='Manifest';SHA256=(FixtureHash $manifest)})});$failed=$false;try{$null=Run $p}catch{$failed=$true};Check $failed 'agent self acceptance allowed'
$p=Clone $profile;$p.Evidence=@(@{Key='Baseline';Type='Baseline';Path=$baselinePath;SHA256=(FixtureHash $baselinePath);Scope='Current';Required=$true;ExpectedContextId='fixture';BindMode='ExactPathAndHash';RequiredArtifactKeys=@('Manifest')})
[IO.File]::WriteAllText((Join-Path $game 'version.dll'),'Changed reference')
$null=Reject $p 'changed non-artifact producer footprint accepted'
$r=Run $profile;$record=Get-Content -Raw $r.RecordPath | ConvertFrom-Json -Depth 64
foreach($stage in @('Installation','PackageChecks','LogReview','ReferenceChecks','UpstreamComparison','Multiplayer')){Check (@($record.Stages | Where-Object {$_.Stage -eq $stage -and $_.Status -eq 'Pending'}).Count -eq 1) ('missing pending stage '+$stage)}
Check ((FixtureHash $manifest) -eq $profile.Artifacts[0].SHA256) 'fixture mod mutated'
$result=[pscustomobject]@{State='Passed';Assertions=$checks;TestRoot=$TestRoot;CreatedUtc=[DateTime]::UtcNow.ToString('o');ToolSHA256=(FixtureHash $tool);SupportSHA256=(FixtureHash (Join-Path $PSScriptRoot '../Common.ps1'));TestSHA256=(FixtureHash $PSCommandPath);BaselineToolSHA256=(FixtureHash (Join-Path $PSScriptRoot '../Record-ModBaseline.ps1'));VersionToolSHA256=(FixtureHash (Join-Path $PSScriptRoot '../Test-ModVersions.ps1'));Limitations='Temporary fake roots only; no game, installation or gameplay checks.'}
WriteJson $result (Join-Path $TestRoot 'test-results.json')
$result
