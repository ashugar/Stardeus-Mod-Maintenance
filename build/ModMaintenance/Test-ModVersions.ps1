#requires -Version 7.2
<# .SYNOPSIS
Read-only comparison of explicitly configured version surfaces and independent groups.
See VersionChecker.README.md. No builds, version edits, extraction or game execution.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProfilePath,[switch]$ReportOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$toolVersion='1.0.0'
function Absolute([string]$Path){
    if([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or ![IO.Path]::IsPathFullyQualified($Path)){throw "Absolute local drive path required: $Path"}
    foreach($part in $Path.Substring(3).Replace('/','\').TrimEnd([IO.Path]::DirectorySeparatorChar) -split '\\'){if(!$part -or $part -in @('.','..') -or $part -match '[. ]$' -or $part -match '[:*?\[\]\x00-\x1f]' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)'){throw "Unsafe path component: $Path"}}
    $full=[IO.Path]::GetFullPath($Path); if($full -eq [IO.Path]::GetPathRoot($full)){throw 'Drive roots are refused.'}; return $full.TrimEnd([IO.Path]::DirectorySeparatorChar)
}
function Inside([string]$Path,[string]$Root){return $Path.Equals($Root,[StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)}
function NoLinks([string]$Path){$cursor=Absolute $Path;while($cursor){if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Reparse point refused: $cursor"};$cursor=[IO.Path]::GetDirectoryName($cursor)}}
function Relative([string]$Path){
    if([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path.Contains('\') -or $Path -match '[:*?\[\]\x00-\x1f]'){throw "Invalid relative path: $Path"}
    foreach($part in $Path -split '/'){if(!$part -or $part -in @('.','..') -or $part -match '[. ]$' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)'){throw "Unsafe relative path: $Path"}};return $Path
}
function Child([string]$Root,[string]$Path){$null=Relative $Path;$full=Absolute (Join-Path $Root $Path);if(!(Inside $full $Root) -or $full -eq $Root){throw 'Path escapes root.'};NoLinks $full;return $full}
function Optional($Object,[string]$Name,$Default=$null){if($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name){return $Object.$Name};return $Default}
function Limit($Object,[string]$Name,[long]$Default,[long]$Min,[long]$Max){$n=0L;if(![long]::TryParse([string](Optional $Object $Name $Default),[ref]$n) -or $n -lt $Min -or $n -gt $Max){throw "Invalid limit: $Name"};return $n}
function HashFile([string]$Path){NoLinks $Path;$s=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);try{return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($s))}finally{$s.Dispose()}}
function ReadBounded([IO.Stream]$Stream,[long]$MaxBytes){
    if($Stream.CanSeek -and $Stream.Length -gt $MaxBytes){throw 'Input exceeds its byte limit.'}
    $out=[IO.MemoryStream]::new();try{$buffer=[byte[]]::new(65536);while(($n=$Stream.Read($buffer,0,$buffer.Length)) -gt 0){if($out.Length+$n -gt $MaxBytes){throw 'Input exceeds its byte limit.'};$out.Write($buffer,0,$n)};return ,$out.ToArray()}finally{$out.Dispose()}
}
function Decode([byte[]]$Bytes){
    if($Bytes.Length -gt $script:limits.MaxTextBytes){throw 'Text exceeds MaxTextBytes.'}
    $encoding=[Text.UTF8Encoding]::new($false,$true);$skip=0
    if($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xef -and $Bytes[1] -eq 0xbb -and $Bytes[2] -eq 0xbf){$skip=3}
    elseif($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xff -and $Bytes[1] -eq 0xfe){$encoding=[Text.UnicodeEncoding]::new($false,$false,$true);$skip=2}
    elseif($Bytes.Length -ge 2 -and $Bytes[0] -eq 0xfe -and $Bytes[1] -eq 0xff){$encoding=[Text.UnicodeEncoding]::new($true,$false,$true);$skip=2}
    $text=$encoding.GetString($Bytes,$skip,$Bytes.Length-$skip);if($text.Contains([char]0)){throw 'Binary/unsupported text encoding.'};return $text
}
function JsonValue([Text.Json.JsonElement]$Element){
    switch($Element.ValueKind.ToString()){
        'Object'{$value=[ordered]@{};$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);foreach($p in $Element.EnumerateObject()){if(!$seen.Add($p.Name)){throw 'Duplicate/case-colliding profile property.'};$value[$p.Name]=JsonValue $p.Value};return [pscustomobject]$value}
        'Array'{$items=@(foreach($item in $Element.EnumerateArray()){JsonValue $item});return ,$items}
        'String'{return $Element.GetString()}
        'Number'{$integer=0L;if($Element.TryGetInt64([ref]$integer)){return $integer};return $Element.GetDouble()}
        'True'{return $true}
        'False'{return $false}
        'Null'{return $null}
        default{throw 'Unsupported JSON profile value.'}
    }
}
function Result([string]$Status,$Value=$null,[int]$Line=0,[string]$Detail=''){return [pscustomobject]@{Status=$Status;Value=$Value;Line=$Line;Detail=$Detail}}
function SelectValue($Values,[string]$Missing='MissingField'){
    if(@($Values).Count -eq 0){return Result $Missing}
    if(@($Values).Count -ne 1){return Result 'AmbiguousField' $null 0 'Exactly one declaration/match is required, even when duplicates agree.'}
    return $Values[0]
}
function FileInput([string]$RootKey,[string]$Path){
    $full=Child $script:roots[$RootKey] $Path
    if($script:files.ContainsKey($full)){return $script:files[$full]}
    if($script:files.Count -ge $script:limits.MaxInputs){throw 'MaxInputs exceeded.'}
    if(Test-Path -LiteralPath $full -PathType Container){throw 'Expected an input file, found a directory.'}
    if(!(Test-Path -LiteralPath $full -PathType Leaf)){$record=[pscustomobject]@{Path=$full;Status='MissingFile';SHA256=$null;Bytes=[byte[]]@();Length=0L};$script:files[$full]=$record;return $record}
    $s=[IO.File]::Open($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{$bytes=ReadBounded $s $script:limits.MaxInputBytes}finally{$s.Dispose()}
    $script:inputBytes+=$bytes.Length;if($script:inputBytes -gt $script:limits.MaxTotalInputBytes){throw 'MaxTotalInputBytes exceeded.'}
    $record=[pscustomobject]@{Path=$full;Status='Present';SHA256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));Bytes=$bytes;Length=$bytes.Length}
    $script:files[$full]=$record;return $record
}
function SurfaceInput($Surface){
    $file=FileInput $Surface.RootKey $Surface.Path
    if($file.Status -ne 'Present'){return [pscustomobject]@{Status='MissingFile';Bytes=[byte[]]@();File=$file;EntrySHA256=$null;Detail='Configured input file is absent.'}}
    if($Surface.Storage -eq 'File'){return [pscustomobject]@{Status='Present';Bytes=$file.Bytes;File=$file;EntrySHA256=$null;Detail=''}}
    $key=$file.Path+'|'+$Surface.Entry
    if($script:entries.ContainsKey($key)){return $script:entries[$key]}
    if($script:entries.Count -ge $script:limits.MaxZipEntriesRead){throw 'MaxZipEntriesRead exceeded.'}
    $memory=[IO.MemoryStream]::new($file.Bytes,$false);$zip=$null
    try{
        # Before ZipArchive enumerates metadata, bound standard end-record counts.
        # ZIP64/multi-volume layouts are outside this version reader's scope.
        $tailSize=[Math]::Min($file.Bytes.Length,65557);$end=-1
        for($i=$file.Bytes.Length-22;$i -ge $file.Bytes.Length-$tailSize;$i--){if([BitConverter]::ToUInt32($file.Bytes,$i) -eq 0x06054b50 -and $i+22+[BitConverter]::ToUInt16($file.Bytes,$i+20) -eq $file.Bytes.Length){$end=$i;break}}
        if($end -lt 0){throw 'Standard ZIP end record missing.'}
        $count=[BitConverter]::ToUInt16($file.Bytes,$end+10)
        if($count -eq 65535 -or [BitConverter]::ToUInt32($file.Bytes,$end+12) -eq [uint32]::MaxValue -or [BitConverter]::ToUInt32($file.Bytes,$end+16) -eq [uint32]::MaxValue -or [BitConverter]::ToUInt16($file.Bytes,$end+4) -ne 0 -or [BitConverter]::ToUInt16($file.Bytes,$end+6) -ne 0){throw 'ZIP64/multi-volume archive is unsupported.'}
        if($count -gt $script:limits.MaxArchiveEntries){throw 'MaxArchiveEntries exceeded.'}
        $centralSize=[long][BitConverter]::ToUInt32($file.Bytes,$end+12);$centralOffset=[long][BitConverter]::ToUInt32($file.Bytes,$end+16)
        if($centralOffset+$centralSize -ne $end -or [BitConverter]::ToUInt16($file.Bytes,$end+8) -ne $count){throw 'Unsupported central directory layout.'}
        $position=$centralOffset
        for($entryIndex=0;$entryIndex -lt $count;$entryIndex++){
            if($position+46 -gt $end -or [BitConverter]::ToUInt32($file.Bytes,[int]$position) -ne 0x02014b50){throw 'Invalid central directory header.'}
            $position+=46+[BitConverter]::ToUInt16($file.Bytes,[int]$position+28)+[BitConverter]::ToUInt16($file.Bytes,[int]$position+30)+[BitConverter]::ToUInt16($file.Bytes,[int]$position+32)
            if($position -gt $end){throw 'Invalid central directory size.'}
        }
        if($position -ne $end){throw 'Unconsumed central directory metadata.'}
        $zip=[IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Read,$true)
        if($zip.Entries.Count -gt $script:limits.MaxArchiveEntries){throw 'MaxArchiveEntries exceeded.'}
        $matches=@($zip.Entries | Where-Object {$_.FullName.Equals($Surface.Entry,[StringComparison]::OrdinalIgnoreCase)})
        if(!$matches.Count){$record=[pscustomobject]@{Status='MissingFile';Bytes=[byte[]]@();File=$file;EntrySHA256=$null;Detail='Configured ZIP entry is absent.'}}
        elseif($matches.Count -ne 1){$record=[pscustomobject]@{Status='AmbiguousArchiveEntry';Bytes=[byte[]]@();File=$file;EntrySHA256=$null;Detail='Duplicate/case-colliding selected ZIP entry.'}}
        elseif($matches[0].FullName -cne $Surface.Entry){$record=[pscustomobject]@{Status='UnresolvedArchiveEntryCase';Bytes=[byte[]]@();File=$file;EntrySHA256=$null;Detail='ZIP entry casing differs from configured selection.'}}
        else{
            $entry=$matches[0];$null=Relative $entry.FullName
            if($entry.Length -gt $script:limits.MaxZipEntryBytes -or $entry.Length/[Math]::Max(1,$entry.CompressedLength) -gt $script:limits.MaxCompressionRatio){throw 'ZIP entry byte/compression limit exceeded.'}
            $s=$entry.Open();try{$bytes=ReadBounded $s $script:limits.MaxZipEntryBytes}finally{$s.Dispose()}
            $script:entryBytes+=$bytes.Length;if($script:entryBytes -gt $script:limits.MaxTotalZipEntryBytes){throw 'MaxTotalZipEntryBytes exceeded.'}
            $record=[pscustomobject]@{Status='Present';Bytes=$bytes;File=$file;EntrySHA256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));Detail='Selected decompressed entry only; package structure/integrity/privacy validation is separate.'}
        }
    }catch{if($_.Exception.Message -match 'MaxArchiveEntries|MaxTotalZipEntryBytes|limit exceeded'){throw};$record=[pscustomobject]@{Status='UnresolvedArchive';Bytes=[byte[]]@();File=$file;EntrySHA256=$null;Detail='Selected ZIP entry could not be read using the supported standard layout.'}}
    finally{if($null -ne $zip){$zip.Dispose()};$memory.Dispose()}
    $script:entries[$key]=$record;return $record
}
function Extract($Surface,$InputRecord){
    if($InputRecord.Status -ne 'Present'){return Result $InputRecord.Status $null 0 $InputRecord.Detail}
    $bytes=$InputRecord.Bytes
    try{
        switch($Surface.Type){
            'RulesScalar'{
                $tokens=@([StardeusVersionChecksV1.Lexer]::Read((Decode $bytes),[int]$script:limits.MaxTokens));$depth=0;$values=@()
                for($i=0;$i -lt $tokens.Count;$i++){
                    $t=$tokens[$i]
                    if($t.Kind -eq 'Symbol'){if($t.Value -in @('{','[')){$depth++};if($t.Value -in @('}',']')){$depth--;if($depth -lt 0){throw 'Unbalanced groups.'}};continue}
                    if($depth -ne 0 -or $t.Kind -ne 'Word' -or $t.Value -cne $Surface.Field -or $i+1 -ge $tokens.Count -or $tokens[$i+1].Value -ne '='){continue}
                    if($i+2 -ge $tokens.Count){$values+=Result 'UnresolvedExpression' $null $t.Line;continue}
                    $v=$tokens[$i+2];$expression=($v.Kind -notin @('Word','String') -or ($v.Kind -eq 'Word' -and $v.Value -notmatch '^[A-Za-z0-9_.+-]+$'))
                    if($i+3 -lt $tokens.Count -and $tokens[$i+3].Line -eq $v.EndLine -and $tokens[$i+3].Value -notin @('}',']') -and !($i+4 -lt $tokens.Count -and $tokens[$i+4].Value -eq '=')){$expression=$true}
                    $values+=Result $(if($expression){'UnresolvedExpression'}else{'Extracted'}) $(if($expression){$null}else{$v.Value}) $t.Line
                }
                if($depth -ne 0){throw 'Unbalanced groups.'};return SelectValue $values
            }
            'JsonScalar'{
                $options=[Text.Json.JsonDocumentOptions]::new();$options.MaxDepth=32;$doc=[Text.Json.JsonDocument]::Parse((Decode $bytes),$options)
                try{
                    $null=JsonValue $doc.RootElement
                    $node=$doc.RootElement
                    foreach($rawPart in $Surface.Pointer.Substring(1).Split('/')){
                        if($rawPart -match '~(?![01])'){throw 'Invalid JSON pointer escape.'};$part=$rawPart.Replace('~1','/').Replace('~0','~')
                        if($node.ValueKind -eq [Text.Json.JsonValueKind]::Object){$matches=@($node.EnumerateObject() | Where-Object {$_.Name -ceq $part});if(!$matches.Count){return Result 'MissingField'};if($matches.Count -ne 1){return Result 'AmbiguousField'};$node=$matches[0].Value}
                        elseif($node.ValueKind -eq [Text.Json.JsonValueKind]::Array -and $part -match '^(?:0|[1-9][0-9]*)$'){$index=0;if(![int]::TryParse($part,[ref]$index) -or $index -ge $node.GetArrayLength()){return Result 'MissingField'};$node=$node[$index]}
                        else{return Result 'UnresolvedJsonShape'}
                    }
                    if($node.ValueKind -eq [Text.Json.JsonValueKind]::String){return Result 'Extracted' $node.GetString()}
                    if($node.ValueKind -eq [Text.Json.JsonValueKind]::Number){return Result 'Extracted' $node.GetRawText()}
                    return Result 'UnresolvedJsonShape'
                }finally{$doc.Dispose()}
            }
            'XmlProperty'{
                $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
                $reader=[Xml.XmlReader]::Create([IO.StringReader]::new((Decode $bytes)),$settings)
                try{$doc=[Xml.Linq.XDocument]::Load($reader,[Xml.Linq.LoadOptions]::SetLineInfo)}finally{$reader.Dispose()}
                $nodes=@($doc.Descendants() | Where-Object {$_.Name.LocalName -ceq $Surface.Field -and $_.Parent.Name.LocalName -ceq 'PropertyGroup'})
                if(!$nodes.Count){return Result 'MissingField'};if($nodes.Count -ne 1){return Result 'AmbiguousField'}
                $node=$nodes[0];$line=([Xml.IXmlLineInfo]$node).LineNumber
                foreach($ancestor in @($node)+@($node.Ancestors())){if(@($ancestor.Attributes() | Where-Object Name -Match 'Condition$').Count){return Result 'UnresolvedConditionalProperty' $null $line}}
                if($node.HasElements -or $node.Value -match '[$@%]\('){return Result 'UnresolvedExpression' $null $line}
                return Result 'Extracted' $node.Value.Trim() $line
            }
            'TextPattern'{
                $text=Decode $bytes;$regex=[regex]::new($Surface.Pattern,([Text.RegularExpressions.RegexOptions]::Multiline -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant),[TimeSpan]::FromMilliseconds($script:limits.RegexTimeoutMs))
                # Eager calls preserve timeout exceptions; PowerShell can hide a
                # lazy MatchCollection.Count getter exception as a null value.
                $m=$regex.Match($text);if(!$m.Success){return Result 'MissingField'}
                if($m.NextMatch().Success -or $m.Groups['version'].Captures.Count -ne 1){return Result 'AmbiguousField'}
                $line=1+[regex]::Matches($text.Substring(0,$m.Groups['version'].Index),"`n").Count
                return Result 'Extracted' $m.Groups['version'].Value $line
            }
            'CSharpAttribute'{
                $tokens=@([StardeusVersionChecksV1.Lexer]::Read((Decode $bytes),[int]$script:limits.MaxTokens));$values=@()
                if(@($tokens | Where-Object {$_.Kind -eq 'Word' -and $_.Value -match '^#(?:if|elif|else|endif|define|undef)$'}).Count){return Result 'UnresolvedPreprocessor' $null 0 'CSharp conditional compilation is not evaluated; inspect built metadata separately.'}
                for($i=0;$i+4 -lt $tokens.Count;$i++){
                    if($tokens[$i].Value -ne '[' -or $tokens[$i+1].Value -cne 'assembly' -or $tokens[$i+2].Value -ne ':'){continue}
                    $name=$tokens[$i+3].Value
                    if($name -cnotin @($Surface.Field,($Surface.Field+'Attribute'),('System.Reflection.'+$Surface.Field),('System.Reflection.'+$Surface.Field+'Attribute'))){continue}
                    if($tokens[$i+4].Value -ne '('){continue}
                    if($i+7 -lt $tokens.Count -and $tokens[$i+5].Kind -eq 'String' -and $tokens[$i+6].Value -eq ')' -and $tokens[$i+7].Value -eq ']'){$values+=Result 'Extracted' $tokens[$i+5].Value $tokens[$i].Line}else{$values+=Result 'UnresolvedExpression' $null $tokens[$i].Line}
                }
                return SelectValue $values
            }
            'AssemblyMetadata'{
                $metadata=[StardeusVersionChecksV1.ManagedVersions]::Read($bytes)
                if(!$metadata.ContainsKey($Surface.Field)){return Result 'MissingField'}
                $values=@($metadata[$Surface.Field] | ForEach-Object {Result 'Extracted' $_ 0 'Read from PE/CLI metadata without loading the assembly.'})
                return SelectValue $values
            }
            'WindowsVersionResource'{
                if($Surface.Storage -ne 'File'){return Result 'UnresolvedStorage' $null 0 'Windows resource inspection requires an existing file; no ZIP extraction is performed.'}
                $info=[Diagnostics.FileVersionInfo]::GetVersionInfo($InputRecord.File.Path);$value=$info.($Surface.Field)
                if([string]::IsNullOrWhiteSpace($value)){return Result 'MissingField'};return Result 'Extracted' $value 0 'Windows FileVersionInfo metadata, not a game/release version inferred from defaults.'
            }
        }
    }catch{if($_.Exception.Message -match 'MaxTokens|MaxTextBytes'){throw};if($_.Exception.Message -match 'Duplicate/case-colliding'){return Result 'AmbiguousField' $null 0 'Duplicate/case-colliding JSON property.'};return Result 'UnresolvedExtraction' $null 0 ('Adapter could not establish one literal value: '+$_.Exception.GetBaseException().GetType().Name)}
}
function Normalize([string]$Value,[string]$Mode){
    if([string]::IsNullOrWhiteSpace($Value)){throw 'Empty values cannot establish a version.'}
    switch($Mode){
        'Exact'{return $Value}
        'NumericZeroPad'{
            if($Value -notmatch '^[0-9]+(?:\.[0-9]+){0,3}$'){throw 'NumericZeroPad requires one to four numeric components.'}
            $parts=@($Value.Split('.') | ForEach-Object {([uint64]::Parse($_,[Globalization.CultureInfo]::InvariantCulture)).ToString([Globalization.CultureInfo]::InvariantCulture)})
            while($parts.Count -lt 4){$parts+='0'};return $parts -join '.'
        }
        'IgnoreBuildMetadata'{
            if($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'){throw 'IgnoreBuildMetadata requires a three-component semantic-version-shaped value.'}
            return $Value.Split('+')[0]
        }
    }
}

$profileFull=Absolute $ProfilePath;NoLinks $profileFull;$profileHash=HashFile $profileFull
$profileOptions=[Text.Json.JsonDocumentOptions]::new();$profileOptions.MaxDepth=32
$profileDoc=[Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($profileFull),$profileOptions)
try{$profile=JsonValue $profileDoc.RootElement}finally{$profileDoc.Dispose()}
if((HashFile $profileFull) -cne $profileHash){throw 'Profile changed while reading.'}
if($profile.SchemaVersion -ne 1 -or $profile.CheckId -notmatch '^[A-Za-z0-9_.-]+$'){throw 'Schema 1 and simple CheckId required.'}
foreach($field in @('Roots','Groups','Surfaces','ProtectedRoots')){if($profile.$field -isnot [array]){throw "$field must be a JSON array."}}
if(!$profile.Groups.Count -or !$profile.Surfaces.Count){throw 'Groups and Surfaces must be nonempty.'}
if(!@($profile.Surfaces | Where-Object {$_.Required -eq $true}).Count){throw 'At least one required surface is needed; an all-optional profile cannot establish consistency.'}
$script:roots=@{}
foreach($root in $profile.Roots){if($root.Key -notmatch '^[A-Za-z0-9_.-]+$' -or $script:roots.ContainsKey($root.Key)){throw 'Root keys must be unique simple names.'};$p=Absolute $root.Path;NoLinks $p;if(!(Test-Path -LiteralPath $p -PathType Container)){throw 'Configured root missing.'};$script:roots[$root.Key]=$p}
$groups=@{}
foreach($group in $profile.Groups){if($group.Key -notmatch '^[A-Za-z0-9_.-]+$' -or $groups.ContainsKey($group.Key) -or $group.Expected -isnot [string] -or [string]::IsNullOrWhiteSpace($group.Expected)){throw 'Groups need unique keys and literal nonempty Expected strings.'};$groups[$group.Key]=$group.Expected}
$surfaceNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($surface in $profile.Surfaces){
    if($surface.Key -notmatch '^[A-Za-z0-9_.-]+$' -or !$surfaceNames.Add($surface.Key) -or !$groups.ContainsKey($surface.Group) -or !$script:roots.ContainsKey($surface.RootKey)){throw 'Surfaces need unique keys and known Group/RootKey.'}
    if($surface.Storage -notin @('File','ZipEntry') -or $surface.Type -notin @('RulesScalar','JsonScalar','XmlProperty','TextPattern','CSharpAttribute','AssemblyMetadata','WindowsVersionResource') -or $surface.Required -isnot [bool] -or $surface.Normalization -notin @('Exact','NumericZeroPad','IgnoreBuildMetadata')){throw 'Invalid surface storage/type/Required/Normalization.'}
    $null=Child $script:roots[$surface.RootKey] $surface.Path
    if($surface.Storage -eq 'ZipEntry'){$null=Relative $surface.Entry}
    if($surface.Type -in @('RulesScalar','XmlProperty') -and $surface.Field -notmatch '^[A-Za-z_][A-Za-z0-9_.]*$'){throw 'Invalid literal property field.'}
    if($surface.Type -eq 'JsonScalar' -and (!$surface.Pointer -or !$surface.Pointer.StartsWith('/'))){throw 'JsonScalar requires a non-root JSON pointer.'}
    if($surface.Type -eq 'CSharpAttribute' -and $surface.Field -notin @('AssemblyVersion','AssemblyFileVersion','AssemblyInformationalVersion')){throw 'Unsupported CSharpAttribute field.'}
    if($surface.Type -eq 'AssemblyMetadata' -and $surface.Field -notin @('AssemblyVersion','AssemblyFileVersion','AssemblyInformationalVersion')){throw 'Unsupported AssemblyMetadata field.'}
    if($surface.Type -eq 'WindowsVersionResource' -and $surface.Field -notin @('FileVersion','ProductVersion')){throw 'Unsupported WindowsVersionResource field.'}
    if($surface.Type -eq 'TextPattern'){$regex=[regex]::new($surface.Pattern);if($regex.GetGroupNames() -cnotcontains 'version'){throw 'TextPattern requires a named version capture.'}}
}
$output=Absolute $profile.OutputRoot;NoLinks $output;Assert-ModExternalPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) $output;$protected=@($script:roots.Values)
foreach($root in $profile.ProtectedRoots){$p=Absolute $root;NoLinks $p;$protected+=$p}
foreach($root in $protected){if((Inside $output $root) -or (Inside $root $output)){throw 'OutputRoot must be separate from all input/protected roots.'}}
$cfg=Optional $profile 'Limits'
$script:limits=[pscustomobject]@{MaxInputs=(Limit $cfg 'MaxInputs' 100 1 1000);MaxInputBytes=(Limit $cfg 'MaxInputBytes' 134217728 1 536870912);MaxTotalInputBytes=(Limit $cfg 'MaxTotalInputBytes' 268435456 1 1073741824);MaxTextBytes=(Limit $cfg 'MaxTextBytes' 4194304 1 16777216);MaxTokens=(Limit $cfg 'MaxTokens' 200000 1 1000000);MaxArchiveEntries=(Limit $cfg 'MaxArchiveEntries' 10000 1 50000);MaxZipEntriesRead=(Limit $cfg 'MaxZipEntriesRead' 100 1 1000);MaxZipEntryBytes=(Limit $cfg 'MaxZipEntryBytes' 67108864 1 268435456);MaxTotalZipEntryBytes=(Limit $cfg 'MaxTotalZipEntryBytes' 134217728 1 536870912);MaxCompressionRatio=(Limit $cfg 'MaxCompressionRatio' 1000 1 100000);RegexTimeoutMs=(Limit $cfg 'RegexTimeoutMs' 1000 1 5000);MaxSurfaces=(Limit $cfg 'MaxSurfaces' 500 1 5000)}
if($profile.Surfaces.Count -gt $script:limits.MaxSurfaces){throw 'MaxSurfaces exceeded.'}
if($null -eq ('StardeusVersionChecksV1.Lexer' -as [type])){Add-Type -TypeDefinition @'
namespace StardeusVersionChecksV1 {
    public sealed class Token { public string Kind; public string Value; public int Line; public int EndLine; }
    public static class Lexer {
        public static Token[] Read(string s, int max) {
            var result=new System.Collections.Generic.List<Token>();int i=0,line=1;
            while(i<s.Length) {
                char c=s[i];if(char.IsWhiteSpace(c)){if(c=='\n')line++;i++;continue;}
                if(c=='/'&&i+1<s.Length&&s[i+1]=='/'){i+=2;while(i<s.Length&&s[i]!='\n')i++;continue;}
                if(c=='/'&&i+1<s.Length&&s[i+1]=='*'){i+=2;bool done=false;while(i<s.Length){if(s[i]=='*'&&i+1<s.Length&&s[i+1]=='/'){i+=2;done=true;break;}if(s[i]=='\n')line++;i++;}if(!done)throw new System.IO.InvalidDataException("Unclosed comment.");continue;}
                if(result.Count>=max)throw new System.IO.InvalidDataException("MaxTokens exceeded.");
                int start=i,sl=line;bool verbatim=c=='@'&&i+1<s.Length&&s[i+1]=='"';
                if(c=='"'||verbatim) {
                    if(c=='"'&&i+2<s.Length&&s[i+1]=='"'&&s[i+2]=='"')throw new System.IO.InvalidDataException("Raw/triple strings unsupported.");
                    if(verbatim)i++;i++;bool done=false;var v=new System.Text.StringBuilder();
                    while(i<s.Length){char x=s[i++];if(x=='"'){if(verbatim&&i<s.Length&&s[i]=='"'){v.Append('"');i++;continue;}done=true;break;}if(x=='\n')line++;
                        if(!verbatim&&x=='\\'&&i<s.Length){x=s[i++];if(x=='n')x='\n';else if(x=='r')x='\r';else if(x=='t')x='\t';else if(x!='"'&&x!='\\')throw new System.IO.InvalidDataException("Unsupported escape.");}v.Append(x);}
                    if(!done)throw new System.IO.InvalidDataException("Unclosed string.");result.Add(new Token{Kind="String",Value=v.ToString(),Line=sl,EndLine=line});continue;
                }
                if(c=='\''){i++;bool done=false;while(i<s.Length){char x=s[i++];if(x=='\\'&&i<s.Length){i++;continue;}if(x=='\''){done=true;break;}if(x=='\n')line++;}if(!done)throw new System.IO.InvalidDataException("Unclosed character.");result.Add(new Token{Kind="Char",Value="",Line=sl,EndLine=line});continue;}
                if("{}[]=():,".IndexOf(c)>=0){i++;result.Add(new Token{Kind="Symbol",Value=c.ToString(),Line=line,EndLine=line});continue;}
                while(i<s.Length&&!char.IsWhiteSpace(s[i])&&"{}[]=():,\"'".IndexOf(s[i])<0){if(s[i]=='/'&&i+1<s.Length&&(s[i+1]=='/'||s[i+1]=='*'))break;i++;}
                if(i==start){i++;continue;}result.Add(new Token{Kind="Word",Value=s.Substring(start,i-start),Line=sl,EndLine=line});
            }
            return result.ToArray();
        }
    }
    public static class ManagedVersions {
        public static System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<string>> Read(byte[] bytes) {
            var values=new System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<string>>(System.StringComparer.Ordinal);
            using(var s=new System.IO.MemoryStream(bytes,false))using(var pe=new System.Reflection.PortableExecutable.PEReader(s)) {
                if(!pe.HasMetadata)throw new System.IO.InvalidDataException("No CLI metadata.");
                var r=System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);var a=r.GetAssemblyDefinition();
                values["AssemblyVersion"]=new System.Collections.Generic.List<string>{a.Version.ToString()};
                foreach(var h in a.GetCustomAttributes()) {
                    var attr=r.GetCustomAttribute(h);System.Reflection.Metadata.EntityHandle parent;
                    if(attr.Constructor.Kind==System.Reflection.Metadata.HandleKind.MemberReference)parent=r.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attr.Constructor).Parent;
                    else if(attr.Constructor.Kind==System.Reflection.Metadata.HandleKind.MethodDefinition)parent=r.GetMethodDefinition((System.Reflection.Metadata.MethodDefinitionHandle)attr.Constructor).GetDeclaringType();else continue;
                    string ns,name;
                    if(parent.Kind==System.Reflection.Metadata.HandleKind.TypeReference){var t=r.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)parent);ns=r.GetString(t.Namespace);name=r.GetString(t.Name);}
                    else if(parent.Kind==System.Reflection.Metadata.HandleKind.TypeDefinition){var t=r.GetTypeDefinition((System.Reflection.Metadata.TypeDefinitionHandle)parent);ns=r.GetString(t.Namespace);name=r.GetString(t.Name);}else continue;
                    if(ns!="System.Reflection"||(name!="AssemblyFileVersionAttribute"&&name!="AssemblyInformationalVersionAttribute"))continue;
                    var b=r.GetBlobReader(attr.Value);if(b.ReadUInt16()!=1)throw new System.IO.InvalidDataException("Invalid attribute prologue.");string value=b.ReadSerializedString();
                    string key=name.Substring(0,name.Length-9);if(!values.ContainsKey(key))values[key]=new System.Collections.Generic.List<string>();values[key].Add(value);
                }
            }
            return values;
        }
    }
}
'@}
$script:files=@{};$script:entries=@{};$script:inputBytes=0L;$script:entryBytes=0L;$records=@()
foreach($surface in $profile.Surfaces){
    $inputRecord=SurfaceInput $surface;$extracted=Extract $surface $inputRecord;$status=$extracted.Status;$normalized=$null;$expectedNormalized=$null
    if($status -eq 'Extracted'){
        try{$normalized=Normalize ([string]$extracted.Value) $surface.Normalization;$expectedNormalized=Normalize $groups[$surface.Group] $surface.Normalization;$status=if($normalized -ceq $expectedNormalized){'Match'}else{'Mismatch'}}catch{$status='UnresolvedNormalization'}
    }
    if(!$surface.Required -and $status -in @('MissingFile','MissingField')){$status='OptionalMissing'}
    $records+=[pscustomobject]@{Key=$surface.Key;Group=$surface.Group;Type=$surface.Type;Storage=$surface.Storage;RootKey=$surface.RootKey;Path=$surface.Path;Entry=$(if($surface.Storage -eq 'ZipEntry'){$surface.Entry}else{$null});Required=$surface.Required;Status=$status;Observed=$extracted.Value;Expected=$groups[$surface.Group];Normalization=$surface.Normalization;NormalizedObserved=$normalized;NormalizedExpected=$expectedNormalized;Line=$extracted.Line;Detail=$extracted.Detail;InputSHA256=$inputRecord.File.SHA256;EntrySHA256=$inputRecord.EntrySHA256}
}
foreach($file in $script:files.Values){NoLinks $file.Path;if($file.Status -eq 'MissingFile'){if(Test-Path -LiteralPath $file.Path){throw 'Input appeared during check.'}}elseif(!(Test-Path -LiteralPath $file.Path -PathType Leaf) -or (HashFile $file.Path) -cne $file.SHA256){throw 'Input changed during check; repeat with writers idle.'}}
if((HashFile $profileFull) -cne $profileHash){throw 'Profile changed during check.'}
$failures=@($records | Where-Object Status -in @('Mismatch','MissingFile','MissingField'));$unresolved=@($records | Where-Object {$_.Status -notin @('Match','Mismatch','MissingFile','MissingField','OptionalMissing')});$optional=@($records | Where-Object Status -eq OptionalMissing)
$state=if($failures.Count){'Failed'}elseif($unresolved.Count){'Unresolved'}elseif($optional.Count){'ConsistentWithOptionalOmissions'}else{'Consistent'}
$usedGroups=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);foreach($surface in $profile.Surfaces){$null=$usedGroups.Add($surface.Group)}
$unusedGroups=@($profile.Groups | Where-Object {!$usedGroups.Contains($_.Key)} | ForEach-Object Key)
$report=[pscustomobject]@{SchemaVersion=1;Kind='ModVersionConsistency';ToolVersion=$toolVersion;SupportSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Common.ps1')).Hash;ToolSHA256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;ProfileSHA256=$profileHash;CheckId=$profile.CheckId;CreatedUtc=[DateTime]::UtcNow.ToString('o');State=$state;Configuration=$profile;EffectiveLimits=$script:limits;Surfaces=$records;Inputs=@($script:files.Values | Sort-Object Path | Select-Object Path,Status,SHA256,Length);Limitations=@('Only explicit surfaces/groups are compared; no discovery of every version or decision about release numbering.','Literal XML/source declarations are not evaluated MSBuild outputs. Imports, conditions, constants, expressions and generated defaults need other evidence.','Assembly metadata is read without loading code; Windows resources and CLI attributes are distinct selectable surfaces.','Selected ZIP entries are read without extraction. This is not package safety, privacy, CRC/layout or complete payload verification; use the Stardeus release tooling for reviewed payloads.','Normalization is explicit per surface. Independent API/dependency/save-schema/assembly versions must be placed in deliberate separate groups.','No modification, build, deployment, game execution, compatibility/gameplay/save-safety or publication validation.','Private reports may contain paths and captured literals. No redaction/upload. Inputs are rechecked but not an atomic snapshot.')}
$run=Child $output ('version-check-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[guid]::NewGuid().ToString('N'));NoLinks $run;$null=[IO.Directory]::CreateDirectory($run)
$report | Add-Member -NotePropertyName UnusedGroups -NotePropertyValue $unusedGroups
[IO.File]::WriteAllText((Child $run 'versions.json'),($report | ConvertTo-Json -Depth 64),[Text.UTF8Encoding]::new($false))
$summary=[pscustomobject]@{State=$state;RunPath=$run;ReportPath=(Child $run 'versions.json');Surfaces=$records.Count;Matches=@($records | Where-Object Status -eq Match).Count;Failures=$failures.Count;Unresolved=$unresolved.Count;OptionalMissing=$optional.Count;ComparedGroups=$usedGroups.Count;ConfiguredGroups=$groups.Count;UnusedGroups=$unusedGroups.Count}
[IO.File]::WriteAllText((Child $run 'summary.json'),($summary | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false));$summary
if($state -in @('Failed','Unresolved') -and !$ReportOnly){throw "Version check $state. Review $($summary.ReportPath)"}
