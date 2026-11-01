#requires -Version 7.2
<# .SYNOPSIS
Assemble pinned, scoped evidence into a private validation record. See ValidationRecord.README.md.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProfilePath,[switch]$RequireComplete,[switch]$ReportOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$initialToolHash=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$initialSupportHash=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Common.ps1') -Algorithm SHA256).Hash
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
function ArrayField($o,[string]$name){if($o.PSObject.Properties.Name -cnotcontains $name -or $o.$name -isnot [array]){throw "Diagnostic $name must be an explicit array."};return ,$o.$name}
function Metric($o,[string]$name){$v=Get $o $name;if($v -isnot [long] -or $v -lt 0 -or $v -gt 10000000){throw "Diagnostic coverage $name must be a bounded nonnegative integer."};return $v}
function DiagnosticInputs($r){
    $inputs=ArrayField $r 'Inputs';if(!$inputs.Count -or $inputs.Count -gt 512){throw 'Diagnostic report requires bounded nonempty Inputs.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($f in $inputs){if($f.Status -cne 'Present'){throw 'Unknown diagnostic input status.'};$path=Absolute $f.Path;if(!$seen.Add($path)){throw 'Duplicate diagnostic input.'};Foot $path $f.SHA256}
}
function DiagnosticProvenance($r){
    foreach($name in @('ProfileSHA256','ToolSHA256','RunnerSHA256','SupportSHA256','EngineSHA256')){$null=Pin (Get $r $name '')}
    foreach($name in @('EngineSources','SupportInputs')){
        $sources=ArrayField $r $name;if(!$sources.Count -or $sources.Count -gt 256){throw 'Diagnostic source provenance must be bounded and nonempty.'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($source in $sources){$path=Absolute $source.Path;if(!$seen.Add($path)){throw 'Duplicate diagnostic source provenance.'};Foot $path $source.SHA256}
    }
}
function DiagnosticManifest($r){
    $manifests=@($r.Inputs | Where-Object { [IO.Path]::GetFileName($_.Path) -ceq 'ModInfo.json' -and [IO.Path]::GetDirectoryName((Absolute $_.Path)) -in $subjectRoots })
    if(!$manifests.Count){throw 'Current metadata diagnostic needs the checked subject manifest.'}
    foreach($manifest in $manifests){$m=Parse (ReadInput $manifest.Path $manifest.SHA256);if($m.Id -cne $profile.Subject.ModId){throw 'Diagnostic manifest identity differs from subject.'}}
}
function AdaptDiagnostic($e,$r,$state){
    $null=ArrayField $r 'Gaps';DiagnosticInputs $r
    if($e.Scope -eq 'Current'){SameSubject $r.Subject.ModId $r.Subject.Root}
    if($e.Type -eq 'LogAnalysis'){
        foreach($name in @('ProfileSHA256','ToolSHA256','CommonSHA256')){$null=Pin (Get $r $name '')}
        $supports=ArrayField $r 'SupportInputs';if($supports.Count -lt 2 -or $supports.Count -gt 32){throw 'Log support provenance must include bounded producer and Common inputs.'}
        $seenSupport=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($support in $supports){$path=Absolute $support.Path;if(!$seenSupport.Add($path)){throw 'Duplicate log support input.'};Foot $path $support.SHA256}
        if(!@($supports | Where-Object {[IO.Path]::GetFileName($_.Path) -ceq 'Analyze-ModLog.ps1' -and $_.SHA256 -ceq $r.ToolSHA256}).Count -or !@($supports | Where-Object {[IO.Path]::GetFileName($_.Path) -ceq 'Common.ps1' -and $_.SHA256 -ceq $r.CommonSHA256}).Count){throw 'Log producer/Common hashes do not match support inputs.'}
        $current=Get $r 'Current';if($null -eq $current){throw 'Log report has no current analysis.'}
        $groups=ArrayField $current 'Groups';$gaps=ArrayField $current 'Gaps';$aux=ArrayField $r 'Auxiliary'
        if(!@($r.Inputs | Where-Object {$_.Path -eq $current.Input.Path -and $_.SHA256 -ceq $current.Input.SHA256}).Count){throw 'Current log capture is absent from Inputs.'}
        $occurrences=0L
        foreach($group in $groups){$count=Metric $group 'Count';if(!$count){throw 'Diagnostic group has zero occurrences.'};$occurrences+=$count}
        if((Metric $current 'DistinctIssues') -ne $groups.Count -or (Metric $current 'RecognizedOccurrences') -ne $occurrences){throw 'Log counts contradict diagnostic groups.'}
        if($current.IssueState -cnotin @('NoRecognizedIssues','RecognizedIssuesFound') -or (($groups.Count -eq 0) -ne ($current.IssueState -ceq 'NoRecognizedIssues'))){throw 'Log issue state contradicts groups.'}
        if($current.State -cnotin @('Analyzed','AnalyzedWithGaps')){throw 'Unknown current log analysis state.'}
        if(($state -eq 'Analyzed' -and @($r.Gaps).Count) -or ($current.State -eq 'Analyzed' -and $gaps.Count)){throw 'Log state contradicts gaps.'}
        if($e.Scope -eq 'Current'){
            $window=Get $r.Configuration.EffectiveExpectations 'NotBeforeUtc'
            if($window -and (Time $window) -lt $notBefore){throw 'Log requested window predates record boundary.'}
        }
        # A completed parser run is retained evidence, never a human log review or runtime outcome.
        return 'AnalysisRecorded'
    }
    DiagnosticProvenance $r
    if($e.Scope -eq 'Current'){DiagnosticManifest $r}
    $gaps=ArrayField $r 'Gaps';$findings=ArrayField $r 'Findings'
    if($e.Type -eq 'ApiAudit'){
        $refs=ArrayField $r 'References';$patches=ArrayField $r 'Patches';$coverage=Get $r 'Coverage'
        if($state -eq 'Failed' -and $null -ne $coverage -and @($coverage.PSObject.Properties).Count -eq 0 -and ($gaps.Count -or $findings.Count)){return 'Failed'}
        $metrics=@{};foreach($name in @('SubjectAssemblies','ScopedTypes','ScopedMembers','FixedTargets','BindingChecks','DynamicProviders','ManualRegistrationReferences','Transpilers')){$metrics[$name]=Metric $coverage $name}
        if(($state -ne 'Failed' -and !$metrics.SubjectAssemblies) -or $refs.Count -ne ($metrics.ScopedTypes+$metrics.ScopedMembers) -or $patches.Count -gt $metrics.FixedTargets){throw 'API coverage contradicts selected results.'}
        foreach($patch in $patches){
            if($patch.Status -cne $patch.TargetStatus -or $patch.BindingStatus -cnotin @('CheckedSupportedBindings','RequiresReview','Failed','NotRun')){throw 'API patch target/binding status schema contradiction.'}
            if($patch.TargetStatus -cne 'Resolved' -and $patch.BindingStatus -cne 'NotRun'){throw 'Unresolved API patch target cannot have checked bindings.'}
            if($patch.BindingStatus -ceq 'Failed' -and !$findings.Count){throw 'API failed bindings have no global findings.'}
            if($patch.TargetStatus -ceq 'Resolved' -and $patch.BindingStatus -cin @('RequiresReview','NotRun') -and !$gaps.Count){throw 'API unresolved binding coverage has no global gaps.'}
            if($state -ceq 'CheckedSupportedScope' -and ($patch.TargetStatus -cne 'Resolved' -or $patch.BindingStatus -cne 'CheckedSupportedBindings')){throw 'API supported scope requires both resolved targets and checked supported bindings.'}
        }
        $unresolved=@($refs+$patches | Where-Object Status -CNE 'Resolved')
        if($state -eq 'CheckedSupportedScope'){
            if($gaps.Count -or $findings.Count -or $unresolved.Count -or $metrics.DynamicProviders -or $metrics.ManualRegistrationReferences -or $metrics.Transpilers -or $patches.Count -ne $metrics.FixedTargets){throw 'API success contradicts gaps, findings or unsupported coverage.'}
            if(!$refs.Count -and !$patches.Count){return 'CheckedEmptyScope'}
        }elseif($state -eq 'CheckedWithGaps'){
            if(!$gaps.Count -or $findings.Count){throw 'API gapped state contradicts gaps/findings.'}
        }elseif(!$gaps.Count -and !$findings.Count){throw 'Failed API report has no failure evidence.'}
        return $state
    }
    if($state -eq 'Failed'){if(!$gaps.Count -and !$findings.Count){throw 'Failed contract report has no failure evidence.'};return 'Failed'}
    $selections=ArrayField $r 'Selections';if(!$selections.Count -or $selections.Count -gt 1000){throw 'Contract report has no bounded selected results.'}
    $keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$counts=@{Unchanged=0L;Changed=0L;Unresolved=0L}
    foreach($selection in $selections){
        if($selection.Key -isnot [string] -or $selection.Key -cnotmatch '^[A-Za-z0-9_.-]{1,100}$' -or !$keys.Add($selection.Key) -or $selection.Kind -cnotin @('Method','Field','Type') -or $selection.Status -cnotin @('Unchanged','Changed','Unresolved')){throw 'Invalid/duplicate contract selection.'}
        foreach($name in @('Before','After')){
            $side=$selection.$name
            if($side.Status -ceq 'Read'){
                if($side.Text -isnot [string] -or $side.Text.Length -gt 1048576){throw 'Invalid bounded contract text.'}
                $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($side.Text)))
                if($hash -cne (Pin $side.Hash)){throw 'Contract text/hash contradiction.'}
            }elseif($side.Status -cne 'Unresolved'){throw 'Unknown contract side status.'}
        }
        if($selection.Status -eq 'Unresolved'){
            if($selection.Before.Status -ceq 'Read' -and $selection.After.Status -ceq 'Read'){throw 'Unresolved contract has both readable sides.'}
        }else{
            if($selection.Before.Status -cne 'Read' -or $selection.After.Status -cne 'Read'){throw 'Resolved contract has unreadable sides.'}
            if(($selection.Before.Hash -ceq $selection.After.Hash) -ne ($selection.Status -ceq 'Unchanged')){throw 'Contract comparison status contradicts fingerprints.'}
        }
        $counts[$selection.Status]++
    }
    if((Metric $r.Coverage 'Selected') -ne $selections.Count){throw 'Contract selected count contradicts results.'}
    foreach($name in @('Unchanged','Changed','Unresolved')){if((Metric $r.Coverage $name) -ne $counts[$name]){throw 'Contract coverage contradicts results.'}}
    if($state -eq 'UnchangedInSelectedScope' -and ($gaps.Count -or $findings.Count -or $counts.Changed -or $counts.Unresolved)){throw 'Unchanged contract claim contradicts selected results/gaps/findings.'}
    if($state -eq 'ChangesFound' -and (!$counts.Changed -or $counts.Unresolved -or $gaps.Count -or $findings.Count -lt $counts.Changed)){throw 'Contract changed state contradicts selected results.'}
    if($state -eq 'ComparedWithGaps' -and (!$gaps.Count -or !$counts.Unresolved)){throw 'Contract gapped state has no unresolved selected results/gaps.'}

    return $state
}
function Adapt($e,$r){
    if($r.SchemaVersion -ne 1){throw 'Unsupported report schema.'}
    $spec=@{
        Baseline=@('StardeusBaseline','BaselineId','CapturedUtc','CaptureStatus',@('Recorded','RecordedWithGaps'),'Baseline')
        Versions=@('ModVersionConsistency','CheckId','CreatedUtc','State',@('Failed','Unresolved','Consistent','ConsistentWithOptionalOmissions'),'VersionChecks')
        LogAnalysis=@('StardeusLogAnalysis','AnalysisId','CreatedUtc','State',@('Analyzed','AnalyzedWithGaps'),'LogReview')
        ApiAudit=@('StardeusApiAudit','CheckId','CreatedUtc','State',@('CheckedSupportedScope','CheckedWithGaps','Failed'),'ReferenceChecks')
        Contracts=@('StardeusContractComparison','ComparisonId','CreatedUtc','State',@('UnchangedInSelectedScope','ChangesFound','ComparedWithGaps','Failed'),'ContractComparison')
    }
    if(!$spec.ContainsKey($e.Type)){throw 'Unsupported evidence type.'};$s=$spec[$e.Type]
    if($r.Kind -cne $s[0] -or [string]$r.($s[1]) -cne [string]$e.ExpectedContextId){throw 'Wrong report kind/context ID.'}
    $when=Time $r.($s[2]);if($e.Scope -eq 'Current' -and $when -lt $notBefore){throw 'Current evidence predates task boundary.'}
    $state=[string](Get $r $s[3] '');if($state -cnotin $s[4]){throw 'Unknown report state.'}
    if($e.Type -in @('LogAnalysis','ApiAudit','Contracts')){
        $result=AdaptDiagnostic $e $r $state
        return [pscustomobject]@{Stage=$s[5];Result=$result;ObservedUtc=$when.ToString('o')}
    }
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
$stages=@{};foreach($s in @('Baseline','UpstreamComparison','ContractComparison','ReferenceChecks','VersionChecks','PackageChecks','LogReview','Installation','SourceReview','Build','Startup','Gameplay','SaveReload','Multiplayer','UserAcceptance','Publication')){$stages[$s]=[pscustomobject]@{Stage=$s;Status='Pending';Details=@();EvidenceKeys=@()}}
$evidenceRows=[Collections.Generic.List[object]]::new();$evidenceByKey=@{};$copies=@{}
foreach($e in $profile.Evidence){
    $k=Key $e.Key;if($evidenceByKey.ContainsKey($k)){throw 'Duplicate evidence key.'};if($e.Scope -notin @('Current','Historical') -or $e.Required -isnot [bool]){throw 'Evidence Scope/Required invalid.'}
    $row=[pscustomobject]@{Key=$k;Type=$e.Type;Scope=$e.Scope;Required=$e.Required;Path=$e.Path;SHA256=$e.SHA256;ExpectedContextId=(Get $e 'ExpectedContextId');BindMode=$e.BindMode;Reason=(Get $e 'Reason');RequiredArtifactKeys=@($e.RequiredArtifactKeys);Status='Rejected';Stage=$null;Result=$null;ObservedUtc=$null;Bindings=@();ReviewInput=$null;Detail=$null}
    try {
        $null=Pin $e.SHA256;$file=ReadInput $e.Path $e.SHA256;$copies[$k]=$file.Bytes;$footprints=[Collections.Generic.List[object]]::new();$missingFootprints=[Collections.Generic.List[string]]::new()
        if($e.Type -eq 'Attachment'){$adapted=[pscustomobject]@{Stage=$null;Result='Pinned attachment; no machine outcome';ObservedUtc=$null}}
        else{$parsed=Parse $file;$adapted=Adapt $e $parsed;if($e.Type -eq 'LogAnalysis'){$row.ReviewInput=[pscustomobject]@{Path=$parsed.Current.Input.Path;SHA256=$parsed.Current.Input.SHA256}}}
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
$manual=@('LogReview','SourceReview','Build','Startup','Gameplay','SaveReload','Multiplayer','UserAcceptance','Publication');$seenClaims=@{}
foreach($c in $profile.Attestations){
    if($c.Stage -notin $manual -or $seenClaims.ContainsKey($c.Stage)){throw 'Unknown/duplicate attestation stage.'};$seenClaims[$c.Stage]=$true
    if($c.Status -notin @('Pending','NotApplicable','ReportedPassed','ReportedFailed')){throw 'Unknown attestation status.'};$null=Text $c.Details
    if($c.Status -like 'Reported*'){
        $null=Text $c.Reporter;$null=Text $c.SourceNote;$when=Time $c.ObservedUtc;if($when -lt $notBefore){throw 'Attestation predates task boundary.'}
        if($c.ReporterRole -notin @('User','Agent') -or ($c.Stage -eq 'UserAcceptance' -and $c.ReporterRole -ne 'User')){throw 'Invalid reporter role.'}
        if(!@($c.EvidenceKeys).Count -or !@($c.ArtifactBindings).Count){throw 'Reported outcome needs evidence and artifact bindings.'}
        foreach($ek in $c.EvidenceKeys){if(!$evidenceByKey.ContainsKey($ek) -or $evidenceByKey[$ek].Status -ne 'Accepted' -or $evidenceByKey[$ek].Scope -ne 'Current'){throw 'Attestation evidence unavailable/current scope mismatch.'}}
        $bound=@{};foreach($b in $c.ArtifactBindings){if(!$artifacts.ContainsKey($b.Key) -or $bound.ContainsKey($b.Key) -or $artifacts[$b.Key].Status -ne 'Matches' -or $b.SHA256 -cne $artifacts[$b.Key].SHA256){throw 'Invalid attestation artifact binding.'};$bound[$b.Key]=$true}
        if($c.Stage -eq 'LogReview'){
            $logReports=@($c.EvidenceKeys | ForEach-Object {$evidenceByKey[$_]} | Where-Object {$_.Type -ceq 'LogAnalysis' -and $_.Status -ceq 'Accepted' -and $_.Scope -ceq 'Current'})
            if(!$logReports.Count){throw 'Reported LogReview requires accepted current LogAnalysis evidence.'}
            foreach($logReport in $logReports){
                $references=@($logReport.Bindings | ForEach-Object {$artifacts[$_]} | Where-Object {$_.Role -ceq 'Reference' -and $_.Path -eq $logReport.ReviewInput.Path -and $_.SHA256 -ceq $logReport.ReviewInput.SHA256 -and $bound.ContainsKey($_.Key)})
                if(!$references.Count){throw 'Reported LogReview must bind each reviewed current log through its declared Reference artifact.'}
            }
        }
        foreach($a in $artifactRows | Where-Object Role -eq 'Payload'){if(!$bound.ContainsKey($a.Key)){throw 'Reported outcome must bind every declared payload artifact.'}}
    }
    $detail=$c.Details;if($c.Status -like 'Reported*'){$detail+=" [Reported by $($c.ReporterRole) $($c.Reporter) at $($c.ObservedUtc); source: $($c.SourceNote)]"}
    $stages[$c.Stage].Status=$c.Status;$stages[$c.Stage].Details=@($detail);$stages[$c.Stage].EvidenceKeys=@(Get $c 'EvidenceKeys' @())
}
if((Hash $PSCommandPath) -cne $initialToolHash -or (Hash (Join-Path $PSScriptRoot 'Common.ps1')) -cne $initialSupportHash){throw 'Validation tool/support changed during recording.'}
# Recheck all explicit inputs after assembly. No recursive mod scan or implicit validation.
foreach($p in @($readFiles.Keys)){if((Hash $p) -cne $readFiles[$p]){throw 'An evidence/profile input changed during recording.'}}
foreach($missing in $readAbsent){NoLinks $missing;if(Test-Path -LiteralPath $missing){throw 'A missing producer input appeared during recording.'}}
foreach($a in $artifactRows){if($a.Status -eq 'Matches' -and (Hash $a.Path) -cne $a.SHA256){throw 'An artifact changed during recording.'}}
$open=[Collections.Generic.List[string]]::new()
foreach($s in $profile.RequiredStages){if(!$stages.ContainsKey($s)){throw "Unknown required stage $s"};$row=$stages[$s]
    $complete=$row.Status -in @('ReportedPassed','NotApplicable')
    if($row.Status -eq 'EvidenceRecorded'){
        $complete=$true;foreach($result in $row.Details){if($result -notin @('Recorded','Consistent','ConsistentWithOptionalOmissions','CheckedSupportedScope','UnchangedInSelectedScope')){$complete=$false}}
    }
    if(!$complete){$open.Add($s)}
}
foreach($r in $evidenceRows){if($r.Required -and ($r.Status -ne 'Accepted' -or $r.Scope -ne 'Current')){$open.Add('Evidence:'+ $r.Key)}}
$state=if($issues.Count){'RecordedWithEvidenceIssues'}elseif($open.Count){'RecordedWithOpenItems'}else{'Recorded'}
$run=Join-Path $out ('validation-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[Guid]::NewGuid().ToString('N'));NoLinks $run
$record=[pscustomobject]@{SchemaVersion=1;Kind='StardeusValidationRecord';ToolVersion='1.1.0';ToolSHA256=$initialToolHash;SupportSHA256=$initialSupportHash;RecordId=$profile.RecordId;CreatedUtc=[DateTime]::UtcNow.ToString('o');State=$state;Subject=$profile.Subject;SubjectRoots=@($subjectRoots);NotBeforeUtc=$profile.NotBeforeUtc;ProfileSHA256=$profileFile.SHA256;Artifacts=$artifactRows.ToArray();Evidence=$evidenceRows.ToArray();Stages=@($stages.Values | Sort-Object Stage);Attestations=@($profile.Attestations);RequiredStages=@($profile.RequiredStages);OpenRequiredStages=$open.ToArray();EvidenceIssues=$issues.ToArray();Limitations=@('Caller-scoped record, not approval, parser/game validation, or proof of full file coverage.','Hashes identify bytes, not authentic authorship or truthful outcomes. Attestations are caller-reported, not independently verified.','No automatic tool execution, installation, game control, publication, settings/save changes or network access.','Historical evidence cannot elevate current stages. Attachments have no interpreted outcome.','Declared artifacts and accepted current producer input/support footprints are rehashed; unselected installation state is not. Native release/install reports remain opaque attachments.','Private paths/logs/evidence require private retention; no automatic redaction or upload.')}
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
