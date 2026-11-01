#requires -Version 7.2
<# .SYNOPSIS
Records scoped, read-only Stardeus installation evidence; compares saved baselines.
See BaselineRecorder.README.md. Does not load game assemblies or validate gameplay.
#>
[CmdletBinding()]
param(
    [ValidateSet('Capture','Compare')][string]$Mode='Capture',
    [string]$ProfilePath,
    [string]$SnapshotPath,
    [string]$PreviousSnapshot,
    [string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$toolVersion='1.0.0'
. (Join-Path $PSScriptRoot 'Common.ps1')
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Absolute([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:[\\/]' -or ![IO.Path]::IsPathFullyQualified($Path)) { throw "Absolute local drive path required: $Path" }
    foreach ($part in $Path.Substring(3).Replace('/','\').TrimEnd('\') -split '\\') {
        if (!$part -or $part -in @('.','..') -or $part -match '[. ]$|[:*?"<>|\x00-\x1f\x7f]' -or $part -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)') { throw 'Unsafe absolute path component.' }
    }
    $full=[IO.Path]::GetFullPath($Path)
    if ($full -eq [IO.Path]::GetPathRoot($full)) { throw 'Drive roots are refused.' }
    return $full.TrimEnd([IO.Path]::DirectorySeparatorChar)
}
function Inside([string]$Path,[string]$Root) {
    return $Path.Equals($Root,[StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)
}
function NoLinks([string]$Path) {
    Assert-ModNoReparsePath -Path $Path
    $cursor=Absolute $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $cursor" }
        }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
}
function Within([string]$Root,[string]$Relative,[bool]$AllowRoot=$false) {
    if ($AllowRoot -and $Relative -eq '.') { NoLinks $Root; return $Root }
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative -match '[:*?\[\]]') { throw "Invalid relative path: $Relative" }
    foreach ($part in ($Relative -split '[/\\]')) {
        if (!$part -or $part -in @('.','..') -or $part -match '[. ]$' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)') { throw "Unsafe path component: $Relative" }
    }
    $path=Absolute (Join-Path $Root $Relative)
    if (!(Inside $path $Root) -or $path -eq $Root) { throw 'Path escapes its configured root.' }
    NoLinks $path
    return $path
}
function LoadJson([string]$Path) {
    NoLinks $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 4194304) { throw 'JSON input missing or exceeds 4 MiB.' }
    $bytes=[IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -gt 4194304) { throw 'JSON input exceeds 4 MiB.' }
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes).TrimStart([char]0xfeff)
    $null=ConvertFrom-ModStrictJson ([Text.UTF8Encoding]::new($false,$true).GetBytes($text))
    $doc=[Text.Json.JsonDocument]::Parse($text)
    try { return JsonObject $doc.RootElement } finally { $doc.Dispose() }
}
function JsonObject([Text.Json.JsonElement]$Element) {
    switch ($Element.ValueKind.ToString()) {
        Object { $value=[ordered]@{}; foreach ($property in $Element.EnumerateObject()) { $value[$property.Name]=JsonObject $property.Value }; return [pscustomobject]$value }
        Array { $value=@(foreach ($child in $Element.EnumerateArray()) { JsonObject $child }); return ,$value }
        String { return $Element.GetString() }
        Number { return $Element.GetInt64() }
        True { return $true } False { return $false } Null { return $null }
    }
}
function TextHash([string]$Text) { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))) }
function Optional($Object,[string]$Name,$Default=$null) {
    if ($Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $Default
}
function Warnings([string]$Message) { $script:warnings.Add($Message) }
function Walk([string]$Root,[string]$Folder,$Exclusions) {
    foreach ($item in Get-ChildItem -LiteralPath $Folder -Force | Sort-Object Name) {
        $skip=$false
        foreach ($exclude in $Exclusions) { if (Inside $item.FullName $exclude) { $skip=$true; break } }
        if ($skip) { continue }
        NoLinks $item.FullName
        if ($item.PSIsContainer) { Walk $Root $item.FullName $Exclusions } else { $item.FullName }
    }
}
function FileRecord([string]$Entity,[string]$Root,[string]$Path) {
    NoLinks $Path
    $relative=[IO.Path]::GetRelativePath($Root,$Path).Replace('\','/')
    if (!(Test-Path -LiteralPath $Path)) {
        Warnings "Missing selected file: $Entity/$relative"
        return [pscustomobject]@{Entity=$Entity; Path=$relative; Status='Missing'; SHA256=$null; Length=$null; LastWriteUtc=$null; FileVersion=$null; ProductVersion=$null}
    }
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Expected file: $Path" }
    # Share.Read prevents cooperating writers from changing the bytes during this read.
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
        $length=$stream.Length
        $item=Get-Item -LiteralPath $Path -Force
        $fileVersion=$null; $productVersion=$null
        if ($item.Extension -in @('.exe','.dll')) {
            $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
            $fileVersion=$version.FileVersion; $productVersion=$version.ProductVersion
        }
        return [pscustomobject]@{Entity=$Entity; Path=$relative; Status='Present'; SHA256=$sha; Length=$length; LastWriteUtc=$item.LastWriteTimeUtc.ToString('o'); FileVersion=$fileVersion; ProductVersion=$productVersion}
    } finally { $stream.Dispose() }
}
function ReadVersionConstant([string]$Path,[string]$TypeName,[string]$FieldName) {
    NoLinks $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return [pscustomobject]@{Status='Unknown'; Value=$null; MVID=$null; Reason='Version assembly missing'} }
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $pe=$null
    try {
        $pe=[Reflection.PortableExecutable.PEReader]::new($stream)
        $reader=[Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $module=$reader.GetModuleDefinition()
        $mvid=$reader.GetGuid($module.Mvid).ToString()
        foreach ($typeHandle in $reader.TypeDefinitions) {
            $type=$reader.GetTypeDefinition($typeHandle)
            if (($reader.GetString($type.Namespace)+'.'+$reader.GetString($type.Name)) -cne $TypeName) { continue }
            foreach ($fieldHandle in $type.GetFields()) {
                $field=$reader.GetFieldDefinition($fieldHandle)
                if ($reader.GetString($field.Name) -cne $FieldName) { continue }
                $handle=$field.GetDefaultValue()
                if ($handle.IsNil) { break }
                $constant=$reader.GetConstant($handle)
                if ($constant.TypeCode -eq [Reflection.Metadata.ConstantTypeCode]::String) {
                    return [pscustomobject]@{Status='ObservedConstant'; Value=[Text.Encoding]::Unicode.GetString($reader.GetBlobBytes($constant.Value)); MVID=$mvid; Reason=$null}
                }
            }
        }
        return [pscustomobject]@{Status='Unknown'; Value=$null; MVID=$mvid; Reason='Configured string constant not found'}
    } catch {
        return [pscustomobject]@{Status='Unknown'; Value=$null; MVID=$null; Reason='Metadata could not be read: '+$_.Exception.Message}
    } finally { if ($null -ne $pe) { $pe.Dispose() }; $stream.Dispose() }
}

function Header([string]$Text,[string]$Format='StrictJson') {
    if ($Format -eq 'CoreJson') {
        $options=[Text.Json.JsonDocumentOptions]@{CommentHandling=[Text.Json.JsonCommentHandling]::Skip;AllowTrailingCommas=$true;MaxDepth=50}
        $document=[Text.Json.JsonDocument]::Parse($Text,$options)
        $buffer=[IO.MemoryStream]::new(); $writer=[Text.Json.Utf8JsonWriter]::new($buffer)
        try {
            $document.RootElement.WriteTo($writer);$writer.Flush()
            $Text=[Text.UTF8Encoding]::new($false,$true).GetString($buffer.ToArray())
        } finally { $writer.Dispose();$buffer.Dispose();$document.Dispose() }
    }
    $manifest=ConvertFrom-ModStrictJson ([Text.UTF8Encoding]::new($false,$true).GetBytes($Text))
    $fields=[ordered]@{}
    foreach ($name in @('Id','Name','ModVersion','GameVersion','Dependencies','DLLFiles')) {
        $value=$null; $status='NotFound'
        if ($manifest.Keys -ccontains $name) {
            $value=$manifest[$name]
            $valid=if ($name -in @('Dependencies','DLLFiles')) { $value -is [array] } else { $value -is [string] -and ![string]::IsNullOrWhiteSpace($value) }
            $status=if ($valid) {'Extracted'} else {'Unsupported'}
        }
        $fields[$name]=[pscustomobject]@{Status=$status; Value=$value}
    }
    return [pscustomobject]$fields
}
function SelectFiles([string]$Entity,[string]$Root,$Files,$Trees,$Exclusions) {
    if ($Files -isnot [array] -or $Trees -isnot [array] -or $Exclusions -isnot [array]) { throw 'Files, Trees and ExcludeDirectories must be JSON arrays.' }
    $paths=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $excluded=@(foreach ($relative in $Exclusions) { Within $Root $relative })
    foreach ($relative in $Files) {
        $path=Within $Root $relative
        foreach ($exclude in $excluded) { if (Inside $path $exclude) { throw "Explicit file is excluded: $relative" } }
        $paths[$path]=$path
    }
    foreach ($relative in $Trees) {
        $tree=Within $Root $relative $true
        foreach ($exclude in $excluded) { if (Inside $tree $exclude) { throw "Selected tree is excluded: $relative" } }
        if (!(Test-Path -LiteralPath $tree -PathType Container)) { throw "Selected tree missing: $tree" }
        foreach ($path in @(Walk $Root $tree $excluded)) { $paths[$path]=$path }
    }
    foreach ($path in $paths.Values | Sort-Object) { FileRecord $Entity $Root $path }
}
function EnsureOutput([string]$Root,$InputRoots) {
    $rootPath=Absolute $Root
    Assert-ModExternalPath -RepoRoot $repositoryRoot -Path $rootPath
    NoLinks $rootPath
    foreach ($inputRoot in $InputRoots) {
        if ((Inside $rootPath $inputRoot) -or (Inside $inputRoot $rootPath)) { throw 'OutputRoot must be separate from all game/mod input roots.' }
    }
    return $rootPath
}
function SaveRun([string]$Root,$Snapshot,$Comparison) {
    $run=Within $Root ('baseline-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[guid]::NewGuid().ToString('N'))
    NoLinks $run
    $null=[IO.Directory]::CreateDirectory($run)
    if ($null -ne $Snapshot) {
        [IO.File]::WriteAllText((Within $run 'snapshot.json'),($Snapshot | ConvertTo-Json -Depth 64),[Text.UTF8Encoding]::new($false))
    }
    if ($null -ne $Comparison) {
        [IO.File]::WriteAllText((Within $run 'comparison.json'),($Comparison | ConvertTo-Json -Depth 64),[Text.UTF8Encoding]::new($false))
    }
    $summary=[pscustomobject]@{
        State=$(if ($null -ne $Snapshot) {$Snapshot.CaptureStatus} else {'Compared'})
        RunPath=$run
        SnapshotPath=$(if ($null -ne $Snapshot) {Within $run 'snapshot.json'} else {$null})
        Files=$(if ($null -ne $Snapshot) {@($Snapshot.Files).Count} else {0})
        Warnings=$(if ($null -ne $Snapshot) {@($Snapshot.Warnings).Count} else {0})
        Comparison=$(if ($null -ne $Comparison) {$Comparison.State} else {'NotRequested'})
        Added=$(if ($null -ne $Comparison) {@($Comparison.Added).Count} else {0})
        Changed=$(if ($null -ne $Comparison) {@($Comparison.Changed).Count} else {0})
        Removed=$(if ($null -ne $Comparison) {@($Comparison.Removed).Count} else {0})
        MetadataChanges=$(if ($null -ne $Comparison) {@($Comparison.MetadataChanges).Count} else {0})
    }
    [IO.File]::WriteAllText((Within $run 'summary.json'),($summary | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    return $summary
}
function ValidateSnapshot($Snapshot) {
    if ($Snapshot.SchemaVersion -ne 1 -or $Snapshot.Kind -ne 'StardeusBaseline') { throw 'Unsupported snapshot schema/kind.' }
    if ([string]::IsNullOrWhiteSpace($Snapshot.BaselineId)) { throw 'Snapshot BaselineId missing.' }
    if ($Snapshot.CaptureStatus -notin @('Recorded','RecordedWithGaps') -or $Snapshot.ScopeHash -cne (TextHash ($Snapshot.Scope | ConvertTo-Json -Depth 48 -Compress))) { throw 'Snapshot scope hash/status is inconsistent.' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Snapshot.Files) {
        if (!$seen.Add($file.Entity+'/'+$file.Path)) { throw 'Duplicate snapshot file key.' }
        if ($file.Status -notin @('Present','Missing') -or ($file.Status -eq 'Present' -and $file.SHA256 -cnotmatch '^[A-F0-9]{64}$') -or ($file.Status -eq 'Missing' -and $null -ne $file.SHA256)) { throw 'Invalid snapshot file record.' }
    }
}
function CompareSnapshots($Old,$New) {
    ValidateSnapshot $Old; ValidateSnapshot $New
    if ($Old.BaselineId -cne $New.BaselineId) { throw 'Cannot compare different BaselineIds.' }
    $a=@{}; $b=@{}
    foreach ($file in $Old.Files) { $a[$file.Entity+'/'+$file.Path]=$file }
    foreach ($file in $New.Files) { $b[$file.Entity+'/'+$file.Path]=$file }
    $added=@(); $removed=@(); $changed=@()
    foreach ($key in @($b.Keys) | Sort-Object) {
        if (!$a.ContainsKey($key)) { $added+= $key }
        elseif ($a[$key].SHA256 -cne $b[$key].SHA256 -or $a[$key].Status -cne $b[$key].Status) {
            $changed+=[pscustomobject]@{File=$key; BeforeStatus=$a[$key].Status; AfterStatus=$b[$key].Status; BeforeHash=$a[$key].SHA256; AfterHash=$b[$key].SHA256}
        }
    }
    foreach ($key in @($a.Keys) | Sort-Object) { if (!$b.ContainsKey($key)) { $removed+=$key } }
    $metadata=@(); $details=@()
    foreach ($field in @('Game','Mods','Warnings','ToolVersion','ToolSHA256','CommonSHA256')) {
        if (($Old.$field | ConvertTo-Json -Depth 48 -Compress) -cne ($New.$field | ConvertTo-Json -Depth 48 -Compress)) {
            $metadata+=$field
            $details+=[pscustomobject]@{Field=$field; Before=$Old.$field; After=$New.$field}
        }
    }
    $scopeChanged=$Old.ScopeHash -cne $New.ScopeHash
    $state=if ($Old.CaptureStatus -ne 'Recorded' -or $New.CaptureStatus -ne 'Recorded') {'IncompleteEvidence'} elseif ($scopeChanged) {'ScopeChanged'} elseif ($added.Count+$removed.Count+$changed.Count+$metadata.Count -gt 0) {'ChangesFound'} else {'NoChangesInRecordedScope'}
    return [pscustomobject]@{State=$state; BaselineId=$New.BaselineId; BeforeCapturedUtc=$Old.CapturedUtc; AfterCapturedUtc=$New.CapturedUtc; ScopeChanged=$scopeChanged; ScopeBefore=$(if ($scopeChanged) {$Old.Scope} else {$null}); ScopeAfter=$(if ($scopeChanged) {$New.Scope} else {$null}); Added=$added; Removed=$removed; Changed=$changed; MetadataChanges=$metadata; MetadataDetails=$details; Meaning='File/metadata differences only; no compatibility, enabled-state, runtime or gameplay conclusion.'}
}

$script:warnings=[Collections.Generic.List[string]]::new()
if ($Mode -eq 'Compare') {
    if (!$SnapshotPath -or !$PreviousSnapshot -or !$OutputRoot) { throw 'Compare requires SnapshotPath, PreviousSnapshot and OutputRoot.' }
    $old=LoadJson (Absolute $PreviousSnapshot); $new=LoadJson (Absolute $SnapshotPath)
    $comparison=CompareSnapshots $old $new
    $inputs=@($old.Scope.Game.Root,$new.Scope.Game.Root)+@($old.Scope.Mods | ForEach-Object Root)+@($new.Scope.Mods | ForEach-Object Root)
    $output=EnsureOutput $OutputRoot $inputs
    SaveRun $output $null $comparison
    return
}
if (!$ProfilePath) { throw 'Capture requires ProfilePath.' }
$profile=LoadJson (Absolute $ProfilePath)
$profileHash=(Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash
if ($profile.SchemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace($profile.BaselineId)) { throw 'Profile schema 1 and BaselineId are required.' }
if ($profile.Mods -isnot [array] -or $profile.Game.ProcessNames -isnot [array]) { throw 'Mods and Game.ProcessNames must be JSON arrays.' }
$gameRoot=Absolute $profile.Game.Root
NoLinks $gameRoot
if (!(Test-Path -LiteralPath $gameRoot -PathType Container)) { throw 'Game root missing.' }
$keys=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$inputRoots=@($gameRoot)
foreach ($mod in $profile.Mods) {
    if ($mod.Key -notmatch '^[A-Za-z0-9_.-]+$' -or !$keys.Add($mod.Key)) { throw 'Mod keys must be unique simple names.' }
    if ($mod.Role -notin @('Primary','Dependency','Reference')) { throw 'Mod Role must be Primary, Dependency or Reference.' }
    if ($mod.DependencyKeys -isnot [array]) { throw 'DependencyKeys must be a JSON array.' }
    $format=Optional $mod 'ManifestFormat' 'StrictJson'
    if ($format -cnotin @('StrictJson','CoreJson') -or ($format -ceq 'CoreJson' -and ($mod.Role -cne 'Reference' -or $mod.ExpectedId -cne 'Core'))) { throw 'CoreJson is restricted to Reference role with ExpectedId Core.' }
    $root=Absolute $mod.Root; NoLinks $root
    if (!(Test-Path -LiteralPath $root -PathType Container)) { throw "Mod root missing: $root" }
    $inputRoots+=$root
}
foreach ($mod in $profile.Mods) {
    foreach ($key in $mod.DependencyKeys) { if (!$keys.Contains($key) -or $key -ieq $mod.Key) { throw "Unknown/self dependency key: $key" } }
}
if ($OutputRoot) { throw 'Capture uses profile.OutputRoot; OutputRoot is only for Compare.' }
$output=EnsureOutput $profile.OutputRoot $inputRoots
$allFiles=[Collections.Generic.List[object]]::new()
$gameFiles=@(SelectFiles 'Game' $gameRoot $profile.Game.Files $profile.Game.Trees $profile.Game.ExcludeDirectories)
foreach ($file in $gameFiles) { $allFiles.Add($file) }
$versionPath=Within $gameRoot $profile.Game.VersionAssembly
if (!($gameFiles.Path -contains [IO.Path]::GetRelativePath($gameRoot,$versionPath).Replace('\','/'))) { throw 'Game.VersionAssembly must be selected for hashing.' }
$version=ReadVersionConstant $versionPath ([string](Optional $profile.Game 'VersionType' '')) ([string](Optional $profile.Game 'VersionField' ''))
if ($version.Status -ne 'ObservedConstant') { Warnings ('Game version unknown: '+$version.Reason) }
$processes=@()
foreach ($name in $profile.Game.ProcessNames) {
    if ($name -notmatch '^[\w.-]+$' -or $name -match '\.exe$') { throw 'Use exact process names without .exe or wildcards.' }
    foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) { $processes+=[pscustomobject]@{Name=$process.ProcessName; PID=$process.Id} }
}
$game=[pscustomobject]@{Root=$gameRoot; Version=$version; VersionEvidence=[pscustomobject]@{Assembly=$profile.Game.VersionAssembly; Type=(Optional $profile.Game 'VersionType'); Field=(Optional $profile.Game 'VersionField')}; RunningProcessMatches=$processes; ProcessDetection='Names supplied by profile; no launch/stop or session validation'; RuntimeState='NotTested'}
$mods=@()
foreach ($mod in $profile.Mods) {
    $root=Absolute $mod.Root
    $files=@(SelectFiles ('Mod:'+ $mod.Key) $root $mod.Files $mod.Trees $mod.ExcludeDirectories)
    $manifestPaths=@($files | Where-Object { [IO.Path]::GetFileName($_.Path) -match '^(?i:ModInfo\.json)$' -and $_.Status -eq 'Present' } | ForEach-Object Path)
    $chosen=Optional $mod 'Manifest'
    $manifests=@()
    if ($chosen) {
        $chosen=[IO.Path]::GetRelativePath($root,(Within $root $chosen)).Replace('\','/')
        if ($chosen -notin $manifestPaths) { throw "Chosen manifest missing or excluded: $chosen" }
    }
    foreach ($relative in $manifestPaths) {
        $header=$null; $errorText=$null
        try { $manifestPath=Within $root $relative; if ((Get-Item -LiteralPath $manifestPath).Length -gt 4194304) { throw 'Manifest exceeds 4 MiB.' }; $header=Header ([Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($manifestPath)).TrimStart([char]0xfeff)) (Optional $mod 'ManifestFormat' 'StrictJson') }
        catch { $errorText=$_.Exception.Message; Warnings "Manifest metadata unreadable ($($mod.Key)/$relative): $errorText" }
        $manifests+=[pscustomobject]@{Path=$relative; Metadata=$header; Error=$errorText; Evidence=('Configured '+(Optional $mod 'ManifestFormat' 'StrictJson')+' manifest metadata; not a game load or dependency-resolution test')}
    }
    if ($chosen) {
        $selected=@($manifests | Where-Object Path -eq $chosen)[0]
        if ($null -eq $selected.Metadata -or $selected.Metadata.Id.Status -ne 'Extracted') { throw "Cannot identify chosen manifest: $chosen" }
        if ($selected.Metadata.Id.Value -cne $mod.ExpectedId) { throw "Chosen manifest identity mismatch: $($mod.Key)" }
        foreach ($field in @('ModVersion','GameVersion')) { if ($selected.Metadata.$field.Status -ne 'Extracted') { Warnings "Selected metadata $field not extracted: $($mod.Key)/$chosen" } }
    } else { Warnings "No manifest chosen by caller: $($mod.Key); runtime selection remains unknown." }
    foreach ($file in $files) { $allFiles.Add($file) }
    $mods+=[pscustomobject]@{Key=$mod.Key; Role=$mod.Role; Root=$root; ExpectedId=$mod.ExpectedId; CallerSelectedManifest=$chosen; RuntimeSelectedManifest='Unknown'; EnabledState='Unknown'; Manifests=$manifests; DependencyKeys=$mod.DependencyKeys; DependencyEvidence='Caller-configured relationships only; native/loader dependency declarations and resolution not interpreted'; Compatibility='NotTested'}
}
# Recheck recorded bytes before committing the report. This is not an atomic whole-
# installation snapshot; external writers must remain idle during capture.
foreach ($file in $allFiles) {
    $root=if ($file.Entity -eq 'Game') {$gameRoot} else { $entityKey=$file.Entity.Substring(4); Absolute (@($profile.Mods | Where-Object Key -eq $entityKey)[0].Root) }
    $check=FileRecord $file.Entity $root (Within $root $file.Path)
    if ($check.Status -cne $file.Status -or $check.SHA256 -cne $file.SHA256) { throw "Input changed during capture: $($file.Entity)/$($file.Path). Retry with writers idle." }
}
if ((Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash -cne $profileHash) { throw 'Profile changed during capture.' }
$scope=[pscustomobject]@{Game=$profile.Game; Mods=$profile.Mods}
$snapshot=[pscustomobject]@{SchemaVersion=1; Kind='StardeusBaseline'; ProfileSHA256=$profileHash; CommonSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Common.ps1') -Algorithm SHA256).Hash; ToolVersion=$toolVersion; ToolSHA256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash; BaselineId=$profile.BaselineId; CapturedUtc=[DateTime]::UtcNow.ToString('o'); CaptureStatus=$(if ($script:warnings.Count) {'RecordedWithGaps'} else {'Recorded'}); Scope=$scope; ScopeHash=(TextHash ($scope | ConvertTo-Json -Depth 48 -Compress)); Game=$game; Mods=$mods; Files=@($allFiles.ToArray() | Sort-Object Entity,Path); Warnings=@($script:warnings | Sort-Object -Unique); Limitations=@('Explicit configured file scope; excluded and unselected data not validated.','Caller manifest choice is not proof of runtime selection or enabled state.','Dependencies are configured relationships, not validated native/loader resolution.','Static evidence only; no parser, startup, gameplay, save safety, multiplayer or publication validation.','Not an atomic filesystem snapshot; pause external writers. No hash cache or reusable compatibility approval.')}
$comparison=$null
if ($PreviousSnapshot) { $comparison=CompareSnapshots (LoadJson (Absolute $PreviousSnapshot)) $snapshot }
SaveRun $output $snapshot $comparison
