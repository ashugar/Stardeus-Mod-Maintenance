#requires -Version 7.2
<# .SYNOPSIS
Assemble pinned, scoped evidence into a private validation record. See ValidationRecord.README.md.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProfilePath,[switch]$RequireComplete,[switch]$ReportOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Get($o,[string]$n,$default=$null){if($null -ne $o -and $o.PSObject.Properties.Name -contains $n){return $o.$n};return $default}
function Absolute([string]$p){
    if($p -notmatch '^[A-Za-z]:[\\/]' -or ![IO.Path]::IsPathFullyQualified($p)){throw "Absolute local drive path required: $p"}
    foreach($part in $p.Substring(3).Replace('/','\').TrimEnd('\') -split '\\'){if(!$part -or $part -in @('.','..') -or $part -match '[. ]$|[:*?\[\]\x00-\x1f]' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)'){throw "Unsafe path: $p"}}
    $p=[IO.Path]::GetFullPath($p).TrimEnd('\');if($p -eq [IO.Path]::GetPathRoot($p).TrimEnd('\')){throw 'Drive roots refused.'};return $p
}
function Inside([string]$p,[string]$root){return $p.Equals($root,[StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)}
function NoLinks([string]$p){$p=Absolute $p;while($p){if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Reparse point refused: $p"};$p=[IO.Path]::GetDirectoryName($p)}}
function Child([string]$root,[string]$rel){
    if(!$rel -or [IO.Path]::IsPathRooted($rel) -or $rel.Contains('\')){throw 'Expected slash-separated relative path.'}
    $p=Absolute (Join-Path $root $rel);if(!(Inside $p $root) -or $p -eq $root){throw 'Path escapes root.'};NoLinks $p;return $p
}
function Pin([string]$h){if($h -cnotmatch '^[0-9A-F]{64}$'){throw 'An exact uppercase SHA256 pin is required.'};return $h}
function Hash([string]$p){NoLinks $p;return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function Key([string]$s){if($s -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,79}$'){throw "Unsafe key: $s"};return $s}
function Text([string]$s){if([string]::IsNullOrWhiteSpace($s) -or $s.Length -gt 8192){throw 'Nonempty bounded text required.'};return $s}
function Time([string]$s){$t=[DateTimeOffset]::MinValue;if($s -notmatch '(Z|[+-]\d\d[:]\d\d)$' -or ![DateTimeOffset]::TryParse($s,[ref]$t)){throw 'Timestamp needs explicit UTC/offset.'};if($t -gt [DateTimeOffset]::UtcNow.AddMinutes(5)){throw 'Future timestamp refused.'};return $t}
function JsonValue([Text.Json.JsonElement]$e){
    switch($e.ValueKind.ToString()){
        Object{$v=[ordered]@{};$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);foreach($p in $e.EnumerateObject()){if(!$seen.Add($p.Name)){throw 'Duplicate/case-colliding JSON property.'};$v[$p.Name]=JsonValue $p.Value};return [pscustomobject]$v}
        Array{$a=@(foreach($i in $e.EnumerateArray()){JsonValue $i});return ,$a}
        String{return $e.GetString()} Number{$integer=0L;if($e.TryGetInt64([ref]$integer)){return $integer};return $e.GetDouble()} True{return $true} False{return $false} Null{return $null} default{throw 'Unsupported JSON value.'}
    }
}
function ReadInput([string]$p,[string]$expected=''){
    $p=Absolute $p;NoLinks $p
    if(!(Test-Path -LiteralPath $p -PathType Leaf)){throw "Input missing: $p"}
    if((Get-Item -LiteralPath $p).Length -gt 33554432){throw 'Input exceeds 32 MiB.'}
    $bytes=[IO.File]::ReadAllBytes($p);$script:total+=$bytes.Length;if($script:total -gt 268435456){throw 'Combined inputs exceed 256 MiB.'}
    $h=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));if($expected -and $h -cne (Pin $expected)){throw "Input pin mismatch: $p"}
    $script:readFiles[$p]=$h;return [pscustomobject]@{Path=$p;SHA256=$h;Bytes=$bytes}
}
function Parse($inputFile){
    $s=[Text.UTF8Encoding]::new($false,$true).GetString($inputFile.Bytes).TrimStart([char]0xfeff)
    $doc=[Text.Json.JsonDocument]::Parse($s,[Text.Json.JsonDocumentOptions]@{MaxDepth=64});try{return JsonValue $doc.RootElement}finally{$doc.Dispose()}
}
function Foot([string]$p,[string]$h){$p=Absolute $p;$null=Pin $h;$script:footprints.Add([pscustomobject]@{Path=$p;SHA256=$h})}
function RootMap($roots){$map=@{};foreach($r in $roots){if($map.ContainsKey($r.Key)){throw 'Duplicate report root key.'};$map[$r.Key]=Absolute $r.Path};return $map}
function SameSubject([string]$id,[string]$root){if($id -cne $profile.Subject.ModId){throw 'Evidence mod ID differs from subject.'};$root=Absolute $root;if($root -notin $subjectRoots){throw 'Evidence root is outside declared subject roots.'}}
function Adapt($e,$r){
    if($r.SchemaVersion -ne 1){throw 'Unsupported report schema.'}
    $spec=@{
        Baseline=@('StardeusBaseline','BaselineId','CapturedUtc','CaptureStatus',@('Recorded','RecordedWithGaps'),'Baseline')
        Versions=@('ModVersionConsistency','CheckId','CreatedUtc','State',@('Failed','Unresolved','Consistent','ConsistentWithOptionalOmissions'),'VersionChecks')
    }
    if(!$spec.ContainsKey($e.Type)){throw 'Unsupported evidence type.'};$s=$spec[$e.Type]
    if($r.Kind -cne $s[0] -or [string]$r.($s[1]) -cne [string]$e.ExpectedContextId){throw 'Wrong report kind/context ID.'}
    $when=Time $r.($s[2]);if($e.Scope -eq 'Current' -and $when -lt $notBefore){throw 'Current evidence predates task boundary.'}
    $state=[string](Get $r $s[3] '');if($state -cnotin $s[4]){throw 'Unknown report state.'}
    if($e.Type -eq 'Baseline'){
        if(!@($r.Files).Count -or !@($r.Mods).Count){throw 'Baseline has no files/mods.'}
        $roots=@{Game=$r.Scope.Game.Root};foreach($m in $r.Scope.Mods){$roots['Mod:'+$m.Key]=$m.Root}
        if($e.Scope -eq 'Current'){$matches=@($r.Mods | Where-Object { $_.ExpectedId -ceq $profile.Subject.ModId -and $_.Root -in $subjectRoots });if(!$matches.Count){throw 'Baseline does not contain subject.'}}
        foreach($f in $r.Files){if($f.Status -eq 'Present'){Foot (Child $roots[$f.Entity] $f.Path) $f.SHA256}elseif($f.Status -eq 'Missing'){$missingFootprints.Add((Child $roots[$f.Entity] $f.Path))}else{throw 'Unknown baseline file state.'}}
        if($state -eq 'Recorded' -and (@($r.Warnings).Count -or @($r.Files | Where-Object Status -ne 'Present').Count)){throw 'Baseline state contradicts gaps.'}
    }else{
        if(!@($r.Surfaces).Count -or !@($r.Inputs).Count){throw 'Version report has no checked surfaces/inputs.'}
        if($e.Scope -eq 'Current'){
            # A release number alone does not identify a Stardeus mod. Bind the
            # producer's checked manifest to the declared subject without loading code.
            $manifests=@($r.Inputs | Where-Object { $_.Status -eq 'Present' -and [IO.Path]::GetFileName($_.Path) -ceq 'ModInfo.json' -and [IO.Path]::GetDirectoryName((Absolute $_.Path)) -in $subjectRoots })
            if(!$manifests.Count){throw 'Current version report needs a checked subject ModInfo.json input.'}
            foreach($manifest in $manifests){$metadata=Parse (ReadInput $manifest.Path $manifest.SHA256);if($metadata.Id -cne $profile.Subject.ModId){throw 'Version manifest identity differs from subject.'}}
            $group=@($r.Configuration.Groups | Where-Object Key -CEQ $e.ReleaseGroupKey)
            if($group.Count -ne 1 -or $group[0].Expected -cne $profile.Subject.Version){throw 'Version release group differs from subject.'}
            if(!@($r.Surfaces | Where-Object {$_.Group -ceq $e.ReleaseGroupKey -and $_.Required -eq $true}).Count){throw 'Release group has no required checked surface.'}
        }
        foreach($f in $r.Inputs){if($f.Status -eq 'Present'){Foot $f.Path $f.SHA256}elseif($f.Status -eq 'MissingFile'){$missingFootprints.Add((Absolute $f.Path))}else{throw 'Unknown version input state.'}}
        if($state -like 'Consistent*' -and @($r.Surfaces | Where-Object Status -notin @('Match','OptionalMissing')).Count){throw 'Version state contradicts surfaces.'}
    }
    return [pscustomobject]@{Stage=$s[5];Result=$state;ObservedUtc=$when.ToString('o')}
}
function MarkdownText([string]$s){return ($s.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;').Replace('|','&#124;').Replace('`','&#96;').Replace("`r",' ').Replace("`n",' '))}
$total=0L;$readFiles=@{};$readAbsent=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);Assert-ModExternalPath -RepoRoot $repoRoot -Path $ProfilePath
$profileFile=ReadInput $ProfilePath;$profile=Parse $profileFile
foreach($field in @('SubjectRoots','Artifacts','Evidence','Attestations','RequiredStages','ProtectedRoots')){if($profile.$field -isnot [array]){throw "$field must be a JSON array."}}
if($profile.SchemaVersion -ne 1){throw 'Unsupported profile schema.'};$null=Key $profile.RecordId
if((Get $profile 'CopyEvidence' $true) -isnot [bool]){throw 'CopyEvidence must be a JSON boolean.'}
foreach($n in @('Name','ModId','Version','Task')){$null=Text $profile.Subject.$n}
$canonical=Absolute $profile.Subject.CanonicalRoot;$subjectRoots=@($canonical)+@($profile.SubjectRoots | ForEach-Object {Absolute $_}) | Select-Object -Unique
$notBefore=Time $profile.NotBeforeUtc;$out=Absolute $profile.OutputRoot;NoLinks $out
Assert-ModExternalPath -RepoRoot $repoRoot -Path $out
foreach($inputPath in @($ProfilePath)+@($profile.Artifacts | ForEach-Object Path)+@($profile.Evidence | ForEach-Object Path)){if((Inside $out (Absolute $inputPath)) -or (Inside (Absolute $inputPath) $out)){throw 'Output overlaps an explicit input.'}}
foreach($root in @($subjectRoots)+@($profile.ProtectedRoots)){$root=Absolute $root;NoLinks $root;if((Inside $out $root) -or (Inside $root $out)){throw 'Output overlaps subject/protected root.'}}
if(@($profile.Artifacts).Count -gt 256 -or @($profile.Evidence).Count -gt 128 -or @($profile.Attestations).Count -gt 32){throw 'Profile item limit exceeded.'}
$artifacts=@{};$artifactRows=[Collections.Generic.List[object]]::new();$issues=[Collections.Generic.List[string]]::new()
foreach($a in $profile.Artifacts){$k=Key $a.Key;if($artifacts.ContainsKey($k)){throw 'Duplicate artifact key.'};$p=Absolute $a.Path;NoLinks $p;$null=Pin $a.SHA256;if($a.Role -notin @('Payload','Package','Reference')){throw 'Unknown artifact role.'}
    if($a.Role -eq 'Payload' -and !@($subjectRoots | Where-Object {Inside $p $_}).Count){throw 'Payload artifact outside subject roots.'}
    $status='Matches';$actual=$null;try{$actual=Hash $p;if($actual -cne $a.SHA256){$status='Changed'}}catch{$status='Unavailable'}
    if($status -ne 'Matches'){$issues.Add("Artifact $k is $status")}
    $row=[pscustomobject]@{Key=$k;Path=$p;Role=$a.Role;SHA256=$a.SHA256;ActualSHA256=$actual;Status=$status};$artifacts[$k]=$row;$artifactRows.Add($row)
}
if(!$artifacts.Count){throw 'At least one explicitly pinned artifact required.'}
$stages=@{};foreach($s in @('Baseline','UpstreamComparison','ReferenceChecks','VersionChecks','PackageChecks','LogReview','Installation','SourceReview','Build','Startup','Gameplay','SaveReload','Multiplayer','UserAcceptance','Publication')){$stages[$s]=[pscustomobject]@{Stage=$s;Status='Pending';Details=@();EvidenceKeys=@()}}
$evidenceRows=[Collections.Generic.List[object]]::new();$evidenceByKey=@{};$copies=@{}
foreach($e in $profile.Evidence){
    $k=Key $e.Key;if($evidenceByKey.ContainsKey($k)){throw 'Duplicate evidence key.'};if($e.Scope -notin @('Current','Historical') -or $e.Required -isnot [bool]){throw 'Evidence Scope/Required invalid.'}
    $row=[pscustomobject]@{Key=$k;Type=$e.Type;Scope=$e.Scope;Required=$e.Required;Path=$e.Path;SHA256=$e.SHA256;ExpectedContextId=(Get $e 'ExpectedContextId');BindMode=$e.BindMode;Reason=(Get $e 'Reason');RequiredArtifactKeys=@($e.RequiredArtifactKeys);Status='Rejected';Stage=$null;Result=$null;ObservedUtc=$null;Bindings=@();Detail=$null}
    try {
        $null=Pin $e.SHA256;$file=ReadInput $e.Path $e.SHA256;$copies[$k]=$file.Bytes;$footprints=[Collections.Generic.List[object]]::new();$missingFootprints=[Collections.Generic.List[string]]::new()
        if($e.Type -eq 'Attachment'){$adapted=[pscustomobject]@{Stage=$null;Result='Pinned attachment; no machine outcome';ObservedUtc=$null}}
        else{$adapted=Adapt $e (Parse $file)}
        if($e.BindMode -notin @('ExactPathAndHash','HashOnly')){throw 'Explicit binding mode required.'}
        if($e.BindMode -eq 'HashOnly'){$null=Text (Get $e 'Reason' '')}
        if($e.Scope -eq 'Current'){foreach($missing in $missingFootprints){NoLinks $missing;if(Test-Path -LiteralPath $missing){throw 'Previously missing producer input appeared.'};$null=$readAbsent.Add($missing)};foreach($footprint in $footprints){if((Hash $footprint.Path) -cne $footprint.SHA256){throw 'Current producer input changed.'};$readFiles[$footprint.Path]=$footprint.SHA256}}
        if($e.Scope -eq 'Current' -and $e.Type -notin @('Attachment') -and !@($e.RequiredArtifactKeys).Count){throw 'Current machine evidence needs artifact bindings.'}
        foreach($ak in $e.RequiredArtifactKeys){if(!$artifacts.ContainsKey($ak)){throw "Unknown artifact key $ak"};$a=$artifacts[$ak];if($a.Status -ne 'Matches'){throw "Artifact $ak no longer matches"}
            $matches=@($footprints | Where-Object { $_.SHA256 -ceq $a.SHA256 -and ($e.BindMode -eq 'HashOnly' -or $_.Path -eq $a.Path) })
            if(!$matches.Count){throw "Report does not bind artifact $ak"};$row.Bindings+= $ak
        }
        $row.Status='Accepted';$row.Stage=$adapted.Stage;$row.Result=$adapted.Result;$row.ObservedUtc=$adapted.ObservedUtc
        if($row.Scope -eq 'Current' -and $row.Stage){$s=$stages[$row.Stage];$s.Status='EvidenceRecorded';$s.Details+= $row.Result;$s.EvidenceKeys+= $k}
    }catch{$row.Detail=$_.Exception.Message;$issues.Add("Evidence $k rejected: $($row.Detail)")}
    $evidenceRows.Add($row);$evidenceByKey[$k]=$row
}
$manual=@('SourceReview','Build','Startup','Gameplay','SaveReload','Multiplayer','UserAcceptance','Publication');$seenClaims=@{}
foreach($c in $profile.Attestations){
    if($c.Stage -notin $manual -or $seenClaims.ContainsKey($c.Stage)){throw 'Unknown/duplicate attestation stage.'};$seenClaims[$c.Stage]=$true
    if($c.Status -notin @('Pending','NotApplicable','ReportedPassed','ReportedFailed')){throw 'Unknown attestation status.'};$null=Text $c.Details
    if($c.Status -like 'Reported*'){
        $null=Text $c.Reporter;$null=Text $c.SourceNote;$when=Time $c.ObservedUtc;if($when -lt $notBefore){throw 'Attestation predates task boundary.'}
        if($c.ReporterRole -notin @('User','Agent') -or ($c.Stage -eq 'UserAcceptance' -and $c.ReporterRole -ne 'User')){throw 'Invalid reporter role.'}
        if(!@($c.EvidenceKeys).Count -or !@($c.ArtifactBindings).Count){throw 'Reported outcome needs evidence and artifact bindings.'}
        foreach($ek in $c.EvidenceKeys){if(!$evidenceByKey.ContainsKey($ek) -or $evidenceByKey[$ek].Status -ne 'Accepted' -or $evidenceByKey[$ek].Scope -ne 'Current'){throw 'Attestation evidence unavailable/current scope mismatch.'}}
        $bound=@{};foreach($b in $c.ArtifactBindings){if(!$artifacts.ContainsKey($b.Key) -or $bound.ContainsKey($b.Key) -or $artifacts[$b.Key].Status -ne 'Matches' -or $b.SHA256 -cne $artifacts[$b.Key].SHA256){throw 'Invalid attestation artifact binding.'};$bound[$b.Key]=$true}
        foreach($a in $artifactRows | Where-Object Role -eq 'Payload'){if(!$bound.ContainsKey($a.Key)){throw 'Reported outcome must bind every declared payload artifact.'}}
    }
    $detail=$c.Details;if($c.Status -like 'Reported*'){$detail+=" [Reported by $($c.ReporterRole) $($c.Reporter) at $($c.ObservedUtc); source: $($c.SourceNote)]"}
    $stages[$c.Stage].Status=$c.Status;$stages[$c.Stage].Details=@($detail);$stages[$c.Stage].EvidenceKeys=@(Get $c 'EvidenceKeys' @())
}
# Recheck all explicit inputs after assembly. No recursive mod scan or implicit validation.
foreach($p in @($readFiles.Keys)){if((Hash $p) -cne $readFiles[$p]){throw 'An evidence/profile input changed during recording.'}}
foreach($missing in $readAbsent){NoLinks $missing;if(Test-Path -LiteralPath $missing){throw 'A missing producer input appeared during recording.'}}
foreach($a in $artifactRows){if($a.Status -eq 'Matches' -and (Hash $a.Path) -cne $a.SHA256){throw 'An artifact changed during recording.'}}
$open=[Collections.Generic.List[string]]::new()
foreach($s in $profile.RequiredStages){if(!$stages.ContainsKey($s)){throw "Unknown required stage $s"};$row=$stages[$s]
    $complete=$row.Status -in @('ReportedPassed','NotApplicable')
    if($row.Status -eq 'EvidenceRecorded'){
        $complete=$true;foreach($result in $row.Details){if($result -notin @('Recorded','Consistent','ConsistentWithOptionalOmissions')){$complete=$false}}
    }
    if(!$complete){$open.Add($s)}
}
foreach($r in $evidenceRows){if($r.Required -and ($r.Status -ne 'Accepted' -or $r.Scope -ne 'Current')){$open.Add('Evidence:'+ $r.Key)}}
$state=if($issues.Count){'RecordedWithEvidenceIssues'}elseif($open.Count){'RecordedWithOpenItems'}else{'Recorded'}
$run=Join-Path $out ('validation-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[Guid]::NewGuid().ToString('N'));NoLinks $run
$record=[pscustomobject]@{SchemaVersion=1;Kind='StardeusValidationRecord';ToolVersion='1.0.1';ToolSHA256=(Hash $PSCommandPath);SupportSHA256=(Hash (Join-Path $PSScriptRoot 'Common.ps1'));RecordId=$profile.RecordId;CreatedUtc=[DateTime]::UtcNow.ToString('o');State=$state;Subject=$profile.Subject;SubjectRoots=@($subjectRoots);NotBeforeUtc=$profile.NotBeforeUtc;ProfileSHA256=$profileFile.SHA256;Artifacts=$artifactRows.ToArray();Evidence=$evidenceRows.ToArray();Stages=@($stages.Values | Sort-Object Stage);Attestations=@($profile.Attestations);RequiredStages=@($profile.RequiredStages);OpenRequiredStages=$open.ToArray();EvidenceIssues=$issues.ToArray();Limitations=@('Caller-scoped record, not approval, parser/game validation, or proof of full file coverage.','Hashes identify bytes, not authentic authorship or truthful outcomes. Attestations are caller-reported, not independently verified.','No automatic tool execution, installation, game control, publication, settings/save changes or network access.','Historical evidence cannot elevate current stages. Attachments have no interpreted outcome.','Only explicitly declared artifacts are rehashed. Native release/install reports are opaque pinned attachments.','Private paths/logs/evidence require private retention; no automatic redaction or upload.')}
$null=New-Item -ItemType Directory -Path $run
$record | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath (Join-Path $run 'validation.json') -Encoding utf8
$lines=[Collections.Generic.List[string]]::new();$lines.Add('# Validation record: '+(MarkdownText $profile.Subject.Name));$lines.Add('');$lines.Add('State: '+$state);$lines.Add('');$lines.Add('Mod: '+(MarkdownText $profile.Subject.ModId)+' / '+(MarkdownText $profile.Subject.Version));$lines.Add('Task: '+(MarkdownText $profile.Subject.Task));$lines.Add('');$lines.Add('Caller-scoped evidence record. Reported outcomes are attributed claims; this record grants no release or deployment approval.');$lines.Add('');$lines.Add('| Stage | Status | Details |');$lines.Add('| --- | --- | --- |')
foreach($s in $record.Stages){$lines.Add('| '+(MarkdownText $s.Stage)+' | '+(MarkdownText $s.Status)+' | '+(MarkdownText ($s.Details -join '; '))+' |')}
$lines.Add('');$lines.Add('| Evidence | Scope | Status | Result / rejection |');$lines.Add('| --- | --- | --- | --- |')
foreach($e in $evidenceRows){$lines.Add('| '+(MarkdownText $e.Key)+' | '+$e.Scope+' | '+$e.Status+' | '+(MarkdownText ([string]$e.Result+' '+[string]$e.Detail))+' |')}
$lines.Add('');$lines.Add('Open required stages: '+(MarkdownText ($open -join ', ')));$lines.Add('');$lines.Add('See validation.json for exact paths, hashes, bindings, attribution and limitations. No unconfigured stage is implied passed.');$lines | Set-Content -LiteralPath (Join-Path $run 'validation.md') -Encoding utf8
if(Get $profile 'CopyEvidence' $true){$dir=Join-Path $run 'evidence';$null=New-Item -ItemType Directory -Path $dir;foreach($k in $copies.Keys){[IO.File]::WriteAllBytes((Join-Path $dir ($k+'.bin')),$copies[$k])}}
$summary=[pscustomobject]@{State=$state;RunPath=$run;RecordPath=(Join-Path $run 'validation.json');MarkdownPath=(Join-Path $run 'validation.md');AcceptedEvidence=@($evidenceRows | Where-Object Status -eq 'Accepted').Count;RejectedEvidence=@($evidenceRows | Where-Object Status -eq 'Rejected').Count;EvidenceIssues=$issues.Count;OpenRequiredStages=$open.ToArray()}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
$summary
if(!$ReportOnly -and ($issues.Count -or ($RequireComplete -and $open.Count))){throw "Validation record retained at $run; evidence issues or required-stage gaps prevent the requested gate."}
