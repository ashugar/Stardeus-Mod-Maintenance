#requires -Version 7.2
<# .SYNOPSIS
Read-only, bounded Stardeus log triage with explicit session/build provenance.
See LogAnalyzer.README.md. No gameplay or compatibility validation is performed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProfilePath,
    [string]$PreviousLogPath,
    [string]$NotBeforeUtc,
    [string]$ExpectedGameVersion,
    [string]$ExpectedBuildCommit
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$toolVersion='1.0.0'
. (Join-Path $PSScriptRoot 'Common.ps1')
$toolHash=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$commonHash=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Common.ps1') -Algorithm SHA256).Hash
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Absolute([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or ![IO.Path]::IsPathFullyQualified($Path)) { throw "Absolute local drive path required: $Path" }
    # Validate spelling before Windows normalization can remove trailing dots/spaces
    # or turn a device name into an alias of a different path.
    foreach ($part in $Path.Substring(3).Replace('/','\').TrimEnd([IO.Path]::DirectorySeparatorChar) -split '\\') {
        if (!$part -or $part -in @('.','..') -or $part -match '[. ]$' -or $part -match '[:*?\[\]]' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)') { throw "Unsafe path component: $Path" }
    }
    $full=[IO.Path]::GetFullPath($Path)
    if ($full -eq [IO.Path]::GetPathRoot($full)) { throw 'Drive roots are refused.' }
    foreach ($part in $full.Substring(3).TrimEnd([IO.Path]::DirectorySeparatorChar) -split '\\') {
        if (!$part -or $part -match '[. ]$' -or $part -match '[:*?\[\]]' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)') { throw "Unsafe path component: $Path" }
    }
    return $full.TrimEnd([IO.Path]::DirectorySeparatorChar)
}
function Inside([string]$Path,[string]$Root) { return $Path.Equals($Root,[StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase) }
function NoLinks([string]$Path) { Assert-ModNoReparsePath (Absolute $Path) }
function Match([string]$Text,[string]$Pattern) { [regex]::Match($Text,$Pattern,[Text.RegularExpressions.RegexOptions]::None,[timespan]::FromSeconds(2)) }
function Child([string]$Root,[string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative -match '[:*?\[\]]') { throw "Invalid relative file path: $Relative" }
    foreach($part in $Relative -split '[/\\]') { if (!$part -or $part -in @('.','..')) { throw 'Relative traversal refused.' } }
    $path=Absolute (Join-Path $Root $Relative)
    if (!(Inside $path $Root) -or $path -eq $Root) { throw 'Path escapes configured root.' }
    NoLinks $path
    return $path
}
function Optional($Object,[string]$Name,$Default=$null) { if ($Object -is [Collections.IDictionary] -and $Object.Contains($Name)) { return $Object[$Name] }; if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }; return $Default }
function OutputText([string]$Text) { $script:outputTextChars+=$Text.Length; if ($script:outputTextChars -gt $script:outputTextLimit) { throw 'Aggregate output text budget exceeded; narrow the selected inputs.' }; return $Text }
function Capture([string]$Path,$Limits) {
    NoLinks $Path
    $size=(Get-Item -LiteralPath $Path -Force).Length
    if ($script:aggregateBytes+$size -gt $Limits.MaxAggregateInputBytes) { throw 'Aggregate input byte limit exceeded.' }
    $read=ReadLog $Path $Limits.MaxInputBytes $Limits.MaxLines
    $script:aggregateBytes+=$read.Info.Bytes
    if ($script:aggregateBytes -gt $Limits.MaxAggregateInputBytes) { throw 'Aggregate input byte limit exceeded.' }; return $read
}
function TextHash([string]$Text) { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))) }
function Limit($Config,[string]$Name,[int]$Default,[int]$Min,[int]$Max) {
    $value=Optional $Config $Name $Default
    $parsed=0
    if (![int]::TryParse([string]$value,[ref]$parsed) -or $parsed -lt $Min -or $parsed -gt $Max) { throw "Invalid limit: $Name ($Min..$Max)." }
    return $parsed
}
function ReadUtc($Value) {
    # ConvertFrom-Json on supported PowerShell versions may materialize ISO dates.
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [datetime]) {
        if ($Value.Kind -eq [DateTimeKind]::Unspecified) { throw 'NotBeforeUtc requires an explicit Z/offset.' }
        return ([DateTimeOffset]$Value).ToUniversalTime()
    }
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    if ($Value -notmatch '(?:Z|[+-]\d{2}[:]\d{2})$') { throw 'NotBeforeUtc requires an explicit Z/offset.' }
    return [DateTimeOffset]::Parse($Value,[Globalization.CultureInfo]::InvariantCulture).ToUniversalTime()
}
function ReadLog([string]$Path,[int]$MaxBytes,[int]$MaxLines) {
    NoLinks $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Log file missing: $Path" }
    $before=Get-Item -LiteralPath $Path -Force
    $writeBefore=$before.LastWriteTimeUtc
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $length=$stream.Length
        if ($length -gt $MaxBytes) { throw "Log exceeds MaxInputBytes: $Path" }
        $bytes=[byte[]]::new([int]$length)
        $offset=0
        while ($offset -lt $bytes.Length) {
            $read=$stream.Read($bytes,$offset,$bytes.Length-$offset)
            if ($read -eq 0) { throw 'Log truncated during read; retry with a stable file.' }
            $offset+=$read
        }
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    } finally { $stream.Dispose() }
    $state='StableDuringRead'
    NoLinks $Path
    $after=Get-Item -LiteralPath $Path -Force
    if ($after.Length -ne $length -or $after.LastWriteTimeUtc -ne $writeBefore) { $state='ChangedDuringRead' }
    else {
        $verify=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            if ($verify.Length -ne $length) { $state='ChangedDuringRead' }
            else {
                $incremental=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                try {
                    $buffer=[byte[]]::new(65536); $remaining=$length
                    while ($remaining -gt 0) {
                        $n=$verify.Read($buffer,0,[int][Math]::Min($remaining,$buffer.Length))
                        if ($n -eq 0) { $state='ChangedDuringRead'; break }
                        $incremental.AppendData($buffer,0,$n); $remaining-=$n
                    }
                    if ($verify.Length -ne $length -or [Convert]::ToHexString($incremental.GetHashAndReset()) -cne $hash) { $state='ChangedDuringRead' }
                } finally { $incremental.Dispose() }
            }
        } finally { $verify.Dispose() }
    }
    $encoding=[Text.UTF8Encoding]::new($false,$true); $skip=0; $encodingName='UTF-8'
    if ($bytes.Length -ge 4 -and (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE -and $bytes[2] -eq 0 -and $bytes[3] -eq 0) -or ($bytes[0] -eq 0 -and $bytes[1] -eq 0 -and $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF))) { throw 'UTF-32 logs are unsupported; preserve the original and convert a separate copy.' }
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $skip=3 }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) { $encoding=[Text.UnicodeEncoding]::new($false,$false,$true); $skip=2; $encodingName='UTF-16LE' }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) { $encoding=[Text.UnicodeEncoding]::new($true,$false,$true); $skip=2; $encodingName='UTF-16BE' }
    $text=$encoding.GetString($bytes,$skip,$bytes.Length-$skip)
    if ($text.Contains([char]0)) { throw 'NUL/binary or BOM-less UTF-16 input is unsupported.' }
    # Count before splitting/allocating per-line objects; millions of blank lines
    # must not turn a small byte input into unbounded report memory.
    $lineCount=1
    for ($i=0; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq "`r" -or $text[$i] -eq "`n") {
            if ($text[$i] -eq "`r" -and $i+1 -lt $text.Length -and $text[$i+1] -eq "`n") { $i++ }
            if ($i+1 -lt $text.Length) { $lineCount++ }
            if ($lineCount -gt $MaxLines) { throw 'MaxLines exceeded; narrow the input or raise the reviewed limit.' }
        }
    }
    [string[]]$lines=@()
    if ($text.Length) { $lines=@([regex]::Split($text,"\r\n|\n|\r")) }
    if ($lines.Count -gt 0 -and $lines[-1] -eq '') { $lines=@($lines | Select-Object -SkipLast 1) }
    foreach ($line in $lines) { if ($line.Length -gt 1048576) { throw 'A log line exceeds the 1 MiB line limit.' } }
    return [pscustomobject]@{Info=[pscustomobject]@{Path=$Path; SHA256=$hash; Bytes=$length; Encoding=$encodingName; LastWriteUtc=$writeBefore.ToString('o'); ReadState=$state; CapturedUtc=[DateTime]::UtcNow.ToString('o'); LineCount=$lines.Count}; Lines=$lines; Bytes=$bytes}
}
function MessageLine([string]$Raw,[int]$Number,$Zone) {
    $message=$Raw; $stamp=$null; $utc=$null; $timeStatus='Absent'
    if ($Raw -match '^\[(?<elapsed>[-+]?\d+(?:\.\d+)?m)\|[-+]?\d+(?:\.\d+)?d\|[-+]?\d+\]\s*(?<text>.*)$') {
        $stamp=$Matches.elapsed; $message=$Matches.text; $timeStatus='ElapsedOnly'
    } elseif ($Raw -match '^(?<stamp>\d{4}-\d{2}-\d{2} \d{2}[:]\d{2}[:]\d{2}\.\d{3})\s+(?<text>.*)$') {
        $stamp=$Matches.stamp; $message=$Matches.text; $local=[datetime]::MinValue
        if (![datetime]::TryParseExact($stamp,'yyyy-MM-dd HH:mm:ss.fff',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$local)) { $timeStatus='Invalid' }
        elseif ($null -eq $Zone) { $timeStatus='TimeZoneUnknown' }
        elseif ($Zone.IsInvalidTime($local)) { $timeStatus='InvalidLocalTime' }
        elseif ($Zone.IsAmbiguousTime($local)) { $timeStatus='AmbiguousLocalTime' }
        else { $utc=[TimeZoneInfo]::ConvertTimeToUtc([datetime]::SpecifyKind($local,[DateTimeKind]::Unspecified),$Zone).ToString('o'); $timeStatus='Converted' }
    }
    return [pscustomobject]@{Number=$Number; Raw=$Raw; Message=$message; TimestampRaw=$stamp; TimestampUtc=$utc; TimestampStatus=$timeStatus}
}
function Diagnostic([string]$Message) {
    $m=Match $Message ('(?i)(?:^|\s)\[(?<level>FATAL|ERROR|ERR|WARNING|WARN|WRN)\](?:\s|$)|^(?<level>FATAL|ERROR|ERR|WARNING|WARN|WRN)\s*[:|]')
    if ($m.Success) {
        $level=$m.Groups['level'].Value.ToUpperInvariant()
        return [pscustomobject]@{Severity=$(if ($level -in @('WARN','WARNING','WRN')) {'Warning'} elseif ($level -eq 'FATAL') {'Fatal'} else {'Error'}); Category='ExplicitSeverity'}
    }
    if ($Message -match '(?i)(?:\([^\r\n]*\)|\.[a-z0-9]+)\s*:\s*(warning|error)\s+[A-Z]+\d+\s*:') { return [pscustomobject]@{Severity=$(if ($Matches[1] -ieq 'warning') {'Warning'} else {'Error'}); Category='CompilerDiagnostic'} }
    if ($Message -match '^(?:--->\s*)?(?:[A-Za-z_][\w+`]*\.)*(?:Exception|[A-Za-z_][\w+`]*Exception)\s*:' -or $Message -match '(?i)^(?:Unhandled exception\b|An unhandled exception\b|Exception thrown\b|Exception\s*:)') { return [pscustomobject]@{Severity='Error'; Category='Exception'} }
    if ($Message -match '(?i)^(?:Failed to (?:load|initialize|parse|deserialize|open|read|write|save)\b|Could not load\b|Unable to (?:load|initialize|parse|deserialize)\b|Version mismatch\s*:|File not found\b|.+\.dll not found!)') { return [pscustomobject]@{Severity='Error'; Category='FailureMessage'} }
        return $null
}
function Freshness($Session,$Info,$Expect,[string]$Format) {
    $reasons=[Collections.Generic.List[string]]::new(); $state='Unknown'
    if ($Format -ne 'Stardeus') { $reasons.Add('Auxiliary input has unknown game-session association; its clock and version are not native game evidence.') }
    if (!$Session.HasStartupMarker) { $reasons.Add('No recognized native startup boundary.') }
    if ($Session.VersionClaims.Count -ne 1) { $reasons.Add('Exactly one game-version claim was not observed.') }
    if ($Info.ReadState -ne 'StableDuringRead') { $reasons.Add('Input changed during capture.') }
    if (!$Expect.NotBeforeUtc -or !$Expect.GameVersion) { $reasons.Add('Expected game version and current NotBeforeUtc window are required.') }
    if (!$Session.StartUtc) { $reasons.Add('Native elapsed minutes, game days and ticks do not identify wall-clock startup time; file modification time is not used.') }
    if ($Session.InvalidTimestampLines) { $reasons.Add('Invalid or ambiguous timestamp evidence.') }
    if ($Session.VersionClaims.Count -gt 1) { $state='ConflictingSessionEvidence' }
    elseif ($Expect.GameVersion -and $Session.VersionClaims.Count -eq 1 -and $Expect.GameVersion -cne $Session.VersionClaims[0].Value) { $state='VersionMismatch'; $reasons.Add('Observed game version differs from supplied expectation.') }
    elseif ($Format -eq 'Stardeus' -and $Session.StartUtc -and [DateTimeOffset]::Parse($Session.StartUtc) -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) { $state='FutureTimestamp' }
    elseif ($Format -eq 'Stardeus' -and $Expect.NotBeforeUtc -and $Session.StartUtc -and [DateTimeOffset]::Parse($Session.StartUtc) -lt (ReadUtc $Expect.NotBeforeUtc)) { $state='OlderThanRequestedWindow' }
    elseif (!$reasons.Count) { $state='MatchesSuppliedWindowAndBuild' }
    return [pscustomobject]@{State=$state; Reasons=$reasons.ToArray(); Meaning='Only log-header/window relationships; no artifact, feature, startup-success or gameplay approval.'}
}
function Analyze($InputLog,$Config,$Limits,$Expect,$Zone) {
    $truncatedLines=0
    $rows=@(for ($i=0; $i -lt $InputLog.Lines.Count; $i++) {
        $line=$InputLog.Lines[$i]
        if ($line.Length -gt $Limits.MaxAnalysisLineChars) { $line=$line.Substring(0,$Limits.MaxAnalysisLineChars)+' [analysis line truncated]'; $truncatedLines++ }
        MessageLine $line ($i+1) $Zone
    })
    $markers=[Collections.Generic.List[object]]::new()
    if ($Config.Format -eq 'Stardeus') { foreach ($row in $rows) { if ($row.Message -match '^Initialize engine version[:]\s+\S+') { if ($markers.Count -ge 100) { throw 'At most 100 recognized startup sessions per input; narrow the selected input.' }; $markers.Add($row) } } }
    $sessions=[Collections.Generic.List[object]]::new()
    for ($i=0; $i -lt $markers.Count; $i++) {
        $sessions.Add([pscustomobject]@{Index=$i+1; StartLine=$markers[$i].Number; EndLine=$(if ($i+1 -lt $markers.Count) {$markers[$i+1].Number-1} else {$rows.Count}); StartupTimestampRaw=$markers[$i].TimestampRaw; StartupUtc=$markers[$i].TimestampUtc})
    }
    $start=if ($markers.Count) {$markers[-1].Number} else {1}
    $selected=@($rows | Where-Object Number -ge $start)
    $versions=@{}; $builds=@{}; $commits=@{}
    $observations=[Collections.Generic.List[object]]::new()
    foreach ($row in $selected) {
        if ($Config.Format -eq 'Stardeus') {
            $match=Match $row.Message ('(?i)^Stardeus version[:]\s*(?<version>v?\d+(?:\.\d+){2,3})(?:\s+\[(?<build>[^\]]+)\])?\s*$')
            if ($match.Success) { $versions[$match.Groups['version'].Value]=$row.Number; if ($match.Groups['build'].Success) { $builds[$match.Groups['build'].Value]=$row.Number } }
            if ($row.Message -match '^(?:Stardeus (?:Production|Development) build|Production build|Steamworks enabled|Steam platform enabled)$' -or $match.Success) {
                $observations.Add([pscustomobject]@{Line=$row.Number; Message=(OutputText $row.Message); Meaning='Reported event only; not startup-success, artifact or feature validation.'})
            }
        }
        if ($observations.Count -gt 10000) { throw 'Too many startup observations; narrow the input.' }
    }
    $invalid=@($selected | Where-Object TimestampStatus -in @('Invalid','InvalidLocalTime','AmbiguousLocalTime')).Count
    $session=[pscustomobject]@{
        Selection=$(if ($markers.Count) {'LastRecognizedStartup'} else {'WholeInputUnverifiedSession'})
        HasStartupMarker=($markers.Count -gt 0); StartLine=$start; EndLine=$rows.Count
        StartUtc=$(if ($markers.Count) {$markers[-1].TimestampUtc} else {$null})
        StartTimestampRaw=$(if ($markers.Count) {$markers[-1].TimestampRaw} else {$null})
        TimestampTimeZoneId=$Config.TimestampTimeZoneId
        VersionClaims=@($versions.Keys | Sort-Object | ForEach-Object { [pscustomobject]@{Value=$_; Line=$versions[$_]} })
        BuildClaims=@($builds.Keys | Sort-Object | ForEach-Object { [pscustomobject]@{Value=$_; Line=$builds[$_]} })
        CommitClaims=@($commits.Keys | Sort-Object | ForEach-Object { [pscustomobject]@{Value=$_; Line=$commits[$_]} })
        InvalidTimestampLines=$invalid; SessionCount=$sessions.Count; ExcludedEarlierLines=($start-1)
    }
    $groups=@{}
    foreach ($row in $selected) {
        $diagnostic=Diagnostic $row.Message
        if ($null -eq $diagnostic) { continue }
        $frames=[Collections.Generic.List[string]]::new(); $stackEnd=$row.Number; $stackTruncated=$false
        for ($j=$row.Number; $j -lt $rows.Count; $j++) {
            $next=$rows[$j].Message
            if ($next -notmatch '^\s*(?:at\s+|--- End of|--- End stack|\(wrapper\s+)') { break }
            if ($frames.Count -ge $Limits.MaxStackLines) { $stackTruncated=$true; break }
            $frames.Add($next.Trim()); $stackEnd=$j+1
        }
        # Only timestamp prefixes are stripped. Preserve numbers, paths, IDs and
        # first stack frames, so distinct resources/callsites are not merged away.
        $signature=$diagnostic.Severity+"`n"+$diagnostic.Category+"`n"+$row.Message.Trim()+"`n"+(@($frames | Select-Object -First 3) -join "`n")
        $key=TextHash $signature
        if (!$groups.ContainsKey($key)) {
            if ($groups.Count -ge $Limits.MaxGroups) { throw 'MaxGroups exceeded; narrow the input or raise the reviewed limit.' }
            $groups[$key]=[pscustomobject]@{Key=$key; Severity=$diagnostic.Severity; Category=$diagnostic.Category; Message=(OutputText $row.Message.Trim()); SignatureFrames=@($frames | Select-Object -First 3 | ForEach-Object {OutputText $_}); Count=0; FirstLine=$row.Number; LastLine=$row.Number; OccurrenceLines=@(); OmittedLocations=0; Examples=@(); Attribution='Unassigned; filenames, nearby events and stack frames are evidence, not proof of mod causality.'}
        }
        $group=$groups[$key]; $group.Count++; $group.LastLine=$row.Number
        if ($group.OccurrenceLines.Count -lt $Limits.MaxOccurrenceLines) { $group.OccurrenceLines+= $row.Number } else { $group.OmittedLocations++ }
        if ($group.Examples.Count -lt $Limits.MaxExamplesPerGroup) {
            $contextStart=[Math]::Max($start,$row.Number-$Limits.ContextBefore)
            $contextEnd=[Math]::Min($rows.Count,[Math]::Max($stackEnd,$row.Number+$Limits.ContextAfter))
            $context=@(for ($n=$contextStart; $n -le $contextEnd; $n++) { [pscustomobject]@{Line=$n; Text=(OutputText $rows[$n-1].Raw); TimestampRaw=$rows[$n-1].TimestampRaw; TimestampUtc=$rows[$n-1].TimestampUtc; TimestampStatus=$rows[$n-1].TimestampStatus} })
            $group.Examples+=[pscustomobject]@{Line=$row.Number; StackTruncated=$stackTruncated; Context=$context}
        }
    }
    $freshness=Freshness $session $InputLog.Info $Expect $Config.Format
    $gaps=[Collections.Generic.List[string]]::new()
    if ($truncatedLines) { $gaps.Add('Analysis line text truncated; diagnostic recognition/grouping/context may be incomplete. Exact captured bytes are retained.') }
    if (!$selected.Count) { $gaps.Add('Empty log input.') }
    if ($InputLog.Info.ReadState -ne 'StableDuringRead') { $gaps.Add('Input changed during capture; saved bytes are only the captured prefix.') }
    if (!$markers.Count) { $gaps.Add('No recognized game startup/session boundary; whole input analyzed without session attribution.') }
    if ($Config.Format -eq 'Stardeus' -and $session.VersionClaims.Count -ne 1) { $gaps.Add('Game version header evidence missing or conflicting.') }
    if ($markers.Count -and !$session.StartUtc) { $gaps.Add('Startup time has no unambiguous UTC interpretation.') }
    if ($invalid) { $gaps.Add('Invalid/ambiguous timestamp lines are present.') }
    foreach ($g in $groups.Values) { if ($g.OmittedLocations -gt 0 -or @($g.Examples | Where-Object StackTruncated).Count -gt 0 -or $g.Count -gt $g.Examples.Count) { $gaps.Add('Bounded diagnostic retention: inspect saved input for omitted occurrences/examples or stack context.'); break } }; if ($Config.Format -eq 'Auxiliary') { $gaps.Add('Auxiliary game-session association unknown; not merged with native input.') }; $issues=@($groups.Values | Sort-Object Severity,FirstLine)
    $occurrences=0; foreach ($group in $issues) { $occurrences+=$group.Count }
    return [pscustomobject]@{Input=$InputLog.Info; TruncatedAnalysisLines=$truncatedLines; State=$(if ($gaps.Count) {'AnalyzedWithGaps'} else {'Analyzed'}); Format=$Config.Format; SourceLabel=$Config.SourceLabel; SessionAssociation=$(if ($Config.Format -eq 'Auxiliary') {'Unknown'} else {'NativeBoundaryOnly'}); Sessions=$sessions.ToArray(); SelectedSession=$session; Expectations=$Expect; Freshness=$freshness; Gaps=$gaps.ToArray(); IssueState=$(if ($issues.Count) {'RecognizedIssuesFound'} else {'NoRecognizedIssues'}); RecognizedOccurrences=$occurrences; DistinctIssues=$issues.Count; Groups=$issues; Issues=$issues; StartupObservations=$observations.ToArray(); Recognition='Fixed diagnostic patterns; unrecognized warnings/errors may remain in the saved input. Absence of matches is not validation.'}
}
function CompareReports($Old,$New) {
    $context='UnverifiedContext'
    if ($Old.SelectedSession.VersionClaims.Count -eq 1 -and $New.SelectedSession.VersionClaims.Count -eq 1 -and $Old.SelectedSession.CommitClaims.Count -eq 1 -and $New.SelectedSession.CommitClaims.Count -eq 1) {
        $context=if ($Old.SelectedSession.VersionClaims[0].Value -ceq $New.SelectedSession.VersionClaims[0].Value -and $Old.SelectedSession.CommitClaims[0].Value -ceq $New.SelectedSession.CommitClaims[0].Value) {'SameReportedVersionAndCommit'} else {'DifferentReportedBuilds'}
    }
    $a=@{}; $b=@{}
    foreach ($group in $Old.Issues) { $a[$group.Key]=$group }
    foreach ($group in $New.Issues) { $b[$group.Key]=$group }
    $newIssues=@(); $repeated=@(); $absent=@()
    foreach ($group in $New.Issues) {
        if (!$a.ContainsKey($group.Key)) { $newIssues+=[pscustomobject]@{Key=$group.Key; Severity=$group.Severity; Message=$group.Message; CurrentCount=$group.Count; FirstLine=$group.FirstLine} }
        else { $repeated+=[pscustomobject]@{Key=$group.Key; Severity=$group.Severity; Message=$group.Message; PreviousCount=$a[$group.Key].Count; CurrentCount=$group.Count; CountDelta=($group.Count-$a[$group.Key].Count)} }
    }
    foreach ($group in $Old.Issues) { if (!$b.ContainsKey($group.Key)) { $absent+=[pscustomobject]@{Key=$group.Key; Severity=$group.Severity; Message=$group.Message; PreviousCount=$group.Count; PreviousFirstLine=$group.FirstLine} } }
    return [pscustomobject]@{Context=$context; PreviousSessionState=$Old.State; CurrentSessionState=$New.State; NewInComparedInput=$newIssues; RepeatedInComparedInput=$repeated; NotObservedInCurrentInput=$absent; Meaning='Presence/count comparison only. Not observed does not mean resolved; logs can cover different actions, durations, builds, or incomplete sessions.'}
}

$profilePathFull=Absolute $ProfilePath; NoLinks $profilePathFull
Assert-ModExternalPath $repoRoot $profilePathFull
if ((Get-Item -LiteralPath $profilePathFull).Length -gt 4194304) { throw 'Profile exceeds 4 MiB.' }
$profileBytes=[IO.File]::ReadAllBytes($profilePathFull); if ($profileBytes.Length -gt 4194304) { throw 'Profile exceeds 4 MiB.' }; $profileHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($profileBytes))
$profile=ConvertFrom-ModStrictJson $profileBytes
Assert-ModObjectKeys $profile @('SchemaVersion','AnalysisId','Subject','Format','SourceLabel','LogRoot','LogFile','OutputRoot','ProtectedRoots','TimestampTimeZoneId','Expectations','Limits','AuxiliaryInputs')
Assert-ModObjectKeys $profile.Subject @('ModId','Root')
Assert-ModObjectKeys $profile.Expectations @('NotBeforeUtc','GameVersion')
Assert-ModObjectKeys $profile.Limits @('MaxInputBytes','MaxLines','MaxGroups','MaxExamplesPerGroup','MaxOccurrenceLines','ContextBefore','ContextAfter','MaxStackLines','MaxAnalysisLineChars','MaxOutputTextChars','MaxAggregateInputBytes')
if ($profile.SchemaVersion -ne 1 -or $profile.AnalysisId -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,79}$') { throw 'Profile schema 1 and bounded AnalysisId required.' }
if ($profile.Format -cnotin @('Stardeus','Auxiliary') -or [string]::IsNullOrWhiteSpace($profile.SourceLabel) -or $profile.SourceLabel.Length -gt 200) { throw 'Unsupported format/source label.' }
if ($profile.Subject.ModId -notmatch '^[A-Za-z][A-Za-z0-9_.-]{0,159}$') { throw 'Explicit subject ModId required.' }
$subjectRoot=Absolute $profile.Subject.Root; NoLinks $subjectRoot
if ($profile.ProtectedRoots -isnot [array] -or $profile.AuxiliaryInputs -isnot [array] -or $profile.AuxiliaryInputs.Count -gt 8) { throw 'ProtectedRoots/AuxiliaryInputs arrays required; at most eight auxiliaries.' }
$logRoot=Absolute $profile.LogRoot; NoLinks $logRoot
if (!(Test-Path -LiteralPath $logRoot -PathType Container)) { throw 'LogRoot folder missing.' }
$logPath=Child $logRoot $profile.LogFile
$previous=$null
if ($PreviousLogPath) { $previous=Absolute $PreviousLogPath; NoLinks $previous; if (!(Inside $previous $logRoot) -or $previous -ieq $logPath) { throw 'Explicit previous must differ and be within LogRoot.' } }
$output=Absolute $profile.OutputRoot; NoLinks $output; Assert-ModExternalPath $repoRoot $output
$protected=@($logRoot,$subjectRoot)
foreach ($root in $profile.ProtectedRoots) { $r=Absolute $root; NoLinks $r; $protected+=$r }
$auxConfigs=@(); $sourcePaths=@($logPath); if ($previous) { $sourcePaths+=$previous }
foreach ($aux in $profile.AuxiliaryInputs) {
    Assert-ModObjectKeys $aux @('SourceLabel','LogRoot','LogFile','TimestampTimeZoneId')
    if ([string]::IsNullOrWhiteSpace($aux.SourceLabel) -or $aux.SourceLabel.Length -gt 200) { throw 'Auxiliary source label required.' }
    $ar=Absolute $aux.LogRoot; NoLinks $ar; $ap=Child $ar $aux.LogFile
    if ($ap -iin $sourcePaths) { throw 'Duplicate input source refused.' }
    $sourcePaths+=$ap; $protected+=$ar
    $auxConfigs+=@{Format='Auxiliary'; SourceLabel=$aux.SourceLabel; TimestampTimeZoneId=$aux.TimestampTimeZoneId; Path=$ap}
}
foreach ($root in $protected) { if (Test-ModPathOverlap $output $root) { throw 'Output overlaps protected/input roots.' } }
$zone=$null; if ($profile.TimestampTimeZoneId) { $zone=[TimeZoneInfo]::FindSystemTimeZoneById($profile.TimestampTimeZoneId) }
$limits=[pscustomobject]@{MaxInputBytes=(Limit $profile.Limits 'MaxInputBytes' 67108864 1 268435456); MaxLines=(Limit $profile.Limits 'MaxLines' 250000 1 1000000); MaxGroups=(Limit $profile.Limits 'MaxGroups' 10000 1 20000); MaxExamplesPerGroup=(Limit $profile.Limits 'MaxExamplesPerGroup' 2 1 5); MaxOccurrenceLines=(Limit $profile.Limits 'MaxOccurrenceLines' 200 1 1000); ContextBefore=(Limit $profile.Limits 'ContextBefore' 2 0 10); ContextAfter=(Limit $profile.Limits 'ContextAfter' 4 0 10); MaxStackLines=(Limit $profile.Limits 'MaxStackLines' 40 1 100); MaxAnalysisLineChars=(Limit $profile.Limits 'MaxAnalysisLineChars' 4096 128 65536); MaxOutputTextChars=(Limit $profile.Limits 'MaxOutputTextChars' 4194304 1024 16777216); MaxAggregateInputBytes=(Limit $profile.Limits 'MaxAggregateInputBytes' 268435456 1 268435456)}
$script:aggregateBytes=0; $script:outputTextChars=0; $script:outputTextLimit=$limits.MaxOutputTextChars
$threshold=if ($PSBoundParameters.ContainsKey('NotBeforeUtc')) {$NotBeforeUtc} else {$profile.Expectations.NotBeforeUtc}
$notBefore=ReadUtc $threshold
$expect=[pscustomobject]@{NotBeforeUtc=$(if ($null -ne $notBefore) {$notBefore.ToString('o')} else {$null}); GameVersion=$(if ($PSBoundParameters.ContainsKey('ExpectedGameVersion')) {$ExpectedGameVersion} else {$profile.Expectations.GameVersion}); BuildCommit=$null; Evidence='Caller-supplied expectations, not observed installation identity.'}
if ($ExpectedBuildCommit) { throw 'Stardeus adapter does not recognize build commits; do not supply one.' }
$currentInput=Capture $logPath $limits
$current=Analyze $currentInput $profile $limits $expect $zone
$priorInput=$null; $prior=$null; $comparison=$null
if ($previous) { $priorInput=Capture $previous $limits; $prior=Analyze $priorInput $profile $limits $expect $zone; $comparison=CompareReports $prior $current }
$auxResults=@(); $auxCaptures=@()
foreach ($ac in $auxConfigs) {
    $az=$null; if ($ac.TimestampTimeZoneId) { $az=[TimeZoneInfo]::FindSystemTimeZoneById($ac.TimestampTimeZoneId) }
    $capture=Capture $ac.Path $limits
    $auxCaptures+=,$capture; $auxResults+=,(Analyze $capture $ac $limits $expect $az)
}
$inputs=@($currentInput.Info); if ($priorInput) {$inputs+=,$priorInput.Info}; foreach ($capture in $auxCaptures) {$inputs+=,$capture.Info}
$footprints=@($inputs | ForEach-Object { [pscustomobject]@{Path=$_.Path; Status='Present'; SHA256=$_.SHA256} })
$allGaps=@($current.Gaps); if ($prior) {$allGaps+=@($prior.Gaps)}; foreach ($ar in $auxResults) {$allGaps+=@($ar.Gaps)}
NoLinks $profilePathFull
if ((Get-FileHash -LiteralPath $profilePathFull -Algorithm SHA256).Hash -cne $profileHash) { throw 'Profile changed during analysis.' }
NoLinks $PSCommandPath; NoLinks (Join-Path $PSScriptRoot 'Common.ps1')
if ((Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash -cne $toolHash -or (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Common.ps1') -Algorithm SHA256).Hash -cne $commonHash) { throw 'Tool/Common changed during analysis.' }
$state=if ($allGaps.Count) {'AnalyzedWithGaps'} else {'Analyzed'}
$report=[pscustomobject]@{SchemaVersion=1; Kind='StardeusLogAnalysis'; ToolVersion=$toolVersion; ToolSHA256=$toolHash; CommonSHA256=$commonHash; SupportInputs=@([pscustomobject]@{Path=[IO.Path]::GetFullPath($PSCommandPath);SHA256=$toolHash},[pscustomobject]@{Path=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Common.ps1'));SHA256=$commonHash}); AnalysisId=$profile.AnalysisId; CreatedUtc=[DateTime]::UtcNow.ToString('o'); State=$state; Subject=@{ModId=$profile.Subject.ModId; Root=$subjectRoot}; Inputs=$footprints; ProfileSHA256=$profileHash; Configuration=@{Profile=$profile; EffectiveExpectations=$expect; EffectiveLimits=$limits}; Current=$current; Previous=$prior; Auxiliary=$auxResults; Comparison=$comparison; Gaps=$allGaps; Limitations=@('Pattern triage cannot establish absence of errors, loading success, gameplay, save safety or publication.','Subject is caller-selected scope, not inferred causal attribution.','Native elapsed clocks never establish wall-clock freshness.','Auxiliary inputs are analyzed independently with unknown game-session association.','Private captured logs can contain paths, hardware, settings and player data.')}
$run=Child $output ('log-analysis-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[guid]::NewGuid().ToString('N'))
NoLinks $run; $null=[IO.Directory]::CreateDirectory($run)
[IO.File]::WriteAllBytes((Child $run 'current.log'),$currentInput.Bytes)
if ($priorInput) { [IO.File]::WriteAllBytes((Child $run 'previous.log'),$priorInput.Bytes) }
for ($i=0;$i -lt $auxCaptures.Count;$i++) { [IO.File]::WriteAllBytes((Child $run ('auxiliary-'+($i+1)+'.log')),$auxCaptures[$i].Bytes) }
[IO.File]::WriteAllText((Child $run 'analysis.json'),($report | ConvertTo-Json -Depth 64),[Text.UTF8Encoding]::new($false))
$summary=[pscustomobject]@{Kind='StardeusLogAnalysisSummary'; AnalysisId=$profile.AnalysisId; State=$state; RunPath=$run; ReportPath=(Child $run 'analysis.json'); ReportSHA256=(Get-FileHash -LiteralPath (Child $run 'analysis.json') -Algorithm SHA256).Hash; Freshness=$current.Freshness.State; IssueState=$current.IssueState; DistinctIssues=$current.DistinctIssues; RecognizedOccurrences=$current.RecognizedOccurrences; SessionCount=$current.SelectedSession.SessionCount; SelectedStartLine=$current.SelectedSession.StartLine; Gaps=$allGaps.Count; AuxiliarySources=$auxResults.Count; Meaning='Analysis only; no startup, gameplay, save safety or causal approval.'}
[IO.File]::WriteAllText((Child $run 'summary.json'),($summary | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$summary
