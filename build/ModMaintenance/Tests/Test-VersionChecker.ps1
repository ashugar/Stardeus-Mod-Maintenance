#requires -Version 7.2
[CmdletBinding()]
param([string]$TestRoot=(Join-Path ([IO.Path]::GetTempPath()) 'Stardeus-version-checker-tests'))
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$tool=Join-Path $PSScriptRoot '../Test-ModVersions.ps1'
. (Join-Path $PSScriptRoot '../Common.ps1')
Assert-ModExternalPath ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))) ([IO.Path]::GetFullPath($TestRoot))
$base=[IO.Path]::GetFullPath($TestRoot);$cursor=$base
while($cursor){if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'TestRoot contains a link.'};$cursor=[IO.Path]::GetDirectoryName($cursor)}
$base=Join-Path $base ([guid]::NewGuid().ToString('N'));$null=[IO.Directory]::CreateDirectory($base);$script:assertions=0
function Assert($Condition,[string]$Message){if(!$Condition){throw "FAILED: $Message"};$script:assertions++}
function WriteText([string]$Path,[string]$Text){$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path));[IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))}
function Json([string]$Path){Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 64}
function Refuses([scriptblock]$Action,[string]$Pattern){$message=$null;try{$null=& $Action}catch{$message=$_.Exception.Message};Assert ($null -ne $message -and $message -match $Pattern) "Expected refusal '$Pattern', got '$message'"}
function Surface([string]$Key,[string]$Path,[string]$Type,[string]$Field,[string]$Group='Release',[string]$Normalization='Exact'){
    [pscustomobject]@{Key=$Key;Group=$Group;RootKey='Stage';Path=$Path;Storage='File';Type=$Type;Field=$Field;Required=$true;Normalization=$Normalization}
}
function Fixture([string]$Name){
    $root=Join-Path $base $Name;$stage=Join-Path $root 'stage';$packages=Join-Path $root 'packages';$null=[IO.Directory]::CreateDirectory($packages)
    WriteText (Join-Path $stage 'mod.rules') "/* Version = wrong */`nID = fixture.mod`nVersion = 1.2.3`nOther { Version = 9.9 }"
    WriteText (Join-Path $stage 'README.md') "Current version: 1.2.3`nHistory: 1.0.0"
    WriteText (Join-Path $stage 'release.json') '{"Version":"1.2.3","API":1,"meta":{"versions":["1.2.3"]}}'
    WriteText (Join-Path $stage 'mod.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix><GenerateAssemblyInfo>false</GenerateAssemblyInfo></PropertyGroup></Project>'
    WriteText (Join-Path $stage 'AssemblyInfo.cs') @'
// [assembly: AssemblyVersion("9.9.9.9")]
/* [assembly: AssemblyVersion("8.8.8.8")] */
[assembly: System.Reflection.AssemblyVersionAttribute("1.2.3.0")]
[assembly: AssemblyFileVersion("1.2.3.0")]
[assembly: AssemblyInformationalVersion("1.2.3+fixture")]
'@
    $surfaces=@((Surface 'Identity' 'mod.rules' 'RulesScalar' 'ID' 'Identity'),(Surface 'Manifest' 'mod.rules' 'RulesScalar' 'Version'),(Surface 'Xml' 'mod.csproj' 'XmlProperty' 'VersionPrefix'),(Surface 'SourceAssembly' 'AssemblyInfo.cs' 'CSharpAttribute' 'AssemblyVersion' 'Release' 'NumericZeroPad'),(Surface 'SourceFile' 'AssemblyInfo.cs' 'CSharpAttribute' 'AssemblyFileVersion' 'Binary'))
    $readme=Surface 'Readme' 'README.md' 'TextPattern' '';$readme|Add-Member -NotePropertyName Pattern -NotePropertyValue '^Current version:\s*(?<version>[^\s]+)\s*$';$surfaces+=$readme
    $json=Surface 'Json' 'release.json' 'JsonScalar' '';$json|Add-Member -NotePropertyName Pointer -NotePropertyValue '/Version';$surfaces+=$json
    $profile=[pscustomobject]@{SchemaVersion=1;CheckId='fixture';Roots=@([pscustomobject]@{Key='Stage';Path=$stage},[pscustomobject]@{Key='Packages';Path=$packages});Groups=@([pscustomobject]@{Key='Identity';Expected='fixture.mod'},[pscustomobject]@{Key='Release';Expected='1.2.3'},[pscustomobject]@{Key='Binary';Expected='1.2.3.0'},[pscustomobject]@{Key='API';Expected='1'});Surfaces=$surfaces;OutputRoot=(Join-Path $root 'reports');ProtectedRoots=@((Join-Path $root 'game'))}
    $path=Join-Path $root 'profile.json';WriteText $path ($profile|ConvertTo-Json -Depth 32)
    [pscustomobject]@{Root=$root;Stage=$stage;Packages=$packages;Profile=$profile;Path=$path;Output=$profile.OutputRoot}
}
function SaveProfile($Fixture){WriteText $Fixture.Path ($Fixture.Profile|ConvertTo-Json -Depth 32)}
function Run($Fixture){& $tool -ProfilePath $Fixture.Path -ReportOnly}
function Field($Result,[string]$Key){return @((Json $Result.ReportPath).Surfaces|Where-Object Key -eq $Key)[0]}
function Only($Fixture,$Surface){$Fixture.Profile.Surfaces=@($Surface);SaveProfile $Fixture}
function Zip($Fixture,[string[]]$Names=@('Example/mod.rules'),[string]$Text='ID = fixture.mod',[byte[]]$Payload=$null){
    $path=Join-Path $Fixture.Packages 'mod.zip';$s=[IO.File]::Open($path,[IO.FileMode]::Create);$z=[IO.Compression.ZipArchive]::new($s,[IO.Compression.ZipArchiveMode]::Create,$true)
    try{foreach($name in $Names){$e=$z.CreateEntry($name);$out=$e.Open();try{$bytes=if($null -ne $Payload){$Payload}else{[Text.Encoding]::UTF8.GetBytes($Text)};$out.Write($bytes,0,$bytes.Length)}finally{$out.Dispose()}}}finally{$z.Dispose();$s.Dispose()}
}
function ZipSurface([string]$Key='ZipIdentity',[string]$Entry='Example/mod.rules'){$s=Surface $Key 'mod.zip' 'RulesScalar' 'ID' 'Identity';$s.RootKey='Packages';$s.Storage='ZipEntry';$s|Add-Member -NotePropertyName Entry -NotePropertyValue $Entry;return $s}

$f=Fixture 'normal';$before=(Get-FileHash -LiteralPath (Join-Path $f.Stage 'mod.rules')).Hash;$r=Run $f
Assert ($r.State -eq 'Consistent' -and $r.Matches -eq 7) 'literal surfaces and explicit numeric padding agree'
Assert ((Field $r 'Manifest').Observed -eq '1.2.3') 'root manifest scalar ignores comments/nested version'
Assert ((Field $r 'SourceAssembly').Observed -eq '1.2.3.0') 'fully-qualified explicit CSharp source attribute'
Assert ((Field $r 'Readme').Line -eq 1) 'targeted documentation marker, not history version'
Assert ((Get-FileHash -LiteralPath (Join-Path $f.Stage 'mod.rules')).Hash -eq $before) 'inputs unchanged'
Assert ((Json $r.ReportPath).Inputs.Count -eq 5) 'repeated file surfaces share input evidence'
Assert ($r.ComparedGroups -eq 3 -and $r.UnusedGroups -eq 1) 'unused groups are not presented as checked'
$f=Fixture 'mismatch';WriteText (Join-Path $f.Stage 'mod.rules') "ID = fixture.mod`nVersion = 1.2.4";$r=Run $f
Assert ($r.State -eq 'Failed' -and (Field $r 'Manifest').Status -eq 'Mismatch') 'stale expected release fails'
Refuses {& $tool -ProfilePath $f.Path} 'Version check Failed'
$f=Fixture 'exact-no-padding';$f.Profile.Surfaces[3].Normalization='Exact';SaveProfile $f
Assert ((Field (Run $f) 'SourceAssembly').Status -eq 'Mismatch') 'numeric padding never implicit'
$f=Fixture 'separate-api';$s=Surface 'Api' 'release.json' 'JsonScalar' '' 'API';$s|Add-Member -NotePropertyName Pointer -NotePropertyValue '/API';$f.Profile.Surfaces+=$s;SaveProfile $f
Assert ((Run $f).State -eq 'Consistent') 'API version independent of release version'
$f=Fixture 'build-metadata';$s=Surface 'Info' 'AssemblyInfo.cs' 'CSharpAttribute' 'AssemblyInformationalVersion' 'Release' 'IgnoreBuildMetadata';Only $f $s
Assert ((Run $f).State -eq 'Consistent') 'explicit build metadata normalization'
WriteText (Join-Path $f.Stage 'AssemblyInfo.cs') '[assembly: AssemblyInformationalVersion("1.2.3-beta.1+fixture")]'
Assert ((Run $f).State -eq 'Failed') 'build normalization preserves prerelease suffix'
$f=Fixture 'unresolved-normalization';$f.Profile.Surfaces[1].Normalization='NumericZeroPad';WriteText (Join-Path $f.Stage 'mod.rules') "ID = fixture.mod`nVersion = 1.2.3-beta";SaveProfile $f
Assert ((Field (Run $f) 'Manifest').Status -eq 'UnresolvedNormalization') 'numeric mode cannot discard prerelease'
$f=Fixture 'duplicate-rules';WriteText (Join-Path $f.Stage 'mod.rules') "ID = fixture.mod`nVersion = 1.2.3`nVersion = 1.2.3";$r=Run $f
Assert ($r.State -eq 'Unresolved' -and (Field $r 'Manifest').Status -eq 'AmbiguousField') 'duplicate declarations remain ambiguous even equal'
Refuses {& $tool -ProfilePath $f.Path} 'Version check Unresolved'
$f=Fixture 'computed-rules';WriteText (Join-Path $f.Stage 'mod.rules') "ID = fixture.mod`nVersion = &Other";$r=Run $f
Assert ((Field $r 'Manifest').Status -eq 'UnresolvedExpression') 'manifest expression not evaluated'
$f=Fixture 'missing-field';WriteText (Join-Path $f.Stage 'mod.rules') 'ID = fixture.mod'
Assert ((Field (Run $f) 'Manifest').Status -eq 'MissingField') 'required absent version fails'
$f=Fixture 'missing-file';[IO.File]::Delete((Join-Path $f.Stage 'README.md'))
Assert ((Field (Run $f) 'Readme').Status -eq 'MissingFile') 'required absent file fails'
$f.Profile.Surfaces[-2].Required=$false;SaveProfile $f
Assert ((Run $f).State -eq 'ConsistentWithOptionalOmissions') 'optional absence explicit'
$f=Fixture 'text-ambiguous';WriteText (Join-Path $f.Stage 'README.md') "Current version: 1.2.3`nCurrent version: 1.2.3"
Assert ((Field (Run $f) 'Readme').Status -eq 'AmbiguousField') 'duplicate documentation matches not silently selected'
$f=Fixture 'text-pattern-invalid';$f.Profile.Surfaces[-2].Pattern='(?<other>.*)';SaveProfile $f
Refuses {Run $f} 'named version capture'
$f=Fixture 'xml-conditional';WriteText (Join-Path $f.Stage 'mod.csproj') '<Project><PropertyGroup Condition="true"><VersionPrefix>1.2.3</VersionPrefix></PropertyGroup></Project>'
Assert ((Field (Run $f) 'Xml').Status -eq 'UnresolvedConditionalProperty') 'MSBuild conditions not evaluated'
$f=Fixture 'xml-computed';WriteText (Join-Path $f.Stage 'mod.csproj') '<Project><PropertyGroup><VersionPrefix>$(ReleaseVersion)</VersionPrefix></PropertyGroup></Project>'
Assert ((Field (Run $f) 'Xml').Status -eq 'UnresolvedExpression') 'MSBuild property expansion not evaluated'
$f=Fixture 'xml-duplicates';WriteText (Join-Path $f.Stage 'mod.csproj') '<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix><VersionPrefix>1.2.3</VersionPrefix></PropertyGroup></Project>'
Assert ((Field (Run $f) 'Xml').Status -eq 'AmbiguousField') 'duplicate XML properties ambiguous'
$f=Fixture 'xml-dtd';WriteText (Join-Path $f.Stage 'mod.csproj') '<!DOCTYPE Project [<!ENTITY x SYSTEM "file:///{FIXTURE_DRIVE}/private.txt">]><Project><PropertyGroup><VersionPrefix>&x;</VersionPrefix></PropertyGroup></Project>'
Assert ((Field (Run $f) 'Xml').Status -eq 'UnresolvedExtraction') 'DTD/external entity expansion blocked'
$f=Fixture 'json-pointer';$s=Surface 'Nested' 'release.json' 'JsonScalar' '';$s|Add-Member -NotePropertyName Pointer -NotePropertyValue '/meta/versions/0';Only $f $s
Assert ((Run $f).State -eq 'Consistent') 'explicit nested JSON pointer/array index'
$f=Fixture 'json-duplicate';WriteText (Join-Path $f.Stage 'release.json') '{"Version":"1.2.3","Version":"1.2.3"}'
Assert ((Field (Run $f) 'Json').Status -eq 'AmbiguousField') 'JSON duplicate key not silently overwritten'
$f=Fixture 'json-shape';WriteText (Join-Path $f.Stage 'release.json') '{"Version":["1.2.3"]}'
Assert ((Field (Run $f) 'Json').Status -eq 'UnresolvedJsonShape') 'non-scalar JSON value is not guessed'
$f=Fixture 'date-tag';$f.Profile.Groups[1].Expected='2026-11-01';$s=Surface 'Date' 'release.json' 'JsonScalar' '';$s|Add-Member -NotePropertyName Pointer -NotePropertyValue '/Version';WriteText (Join-Path $f.Stage 'release.json') '{"Version":"2026-11-01"}';Only $f $s
Assert ((Run $f).State -eq 'Consistent') 'date-shaped literal tags never converted to dates'
$f=Fixture 'csharp-computed';WriteText (Join-Path $f.Stage 'AssemblyInfo.cs') '[assembly: AssemblyVersion(Versions.Current)]'
Assert ((Field (Run $f) 'SourceAssembly').Status -eq 'UnresolvedExpression') 'source version constants never executed/evaluated'
$f=Fixture 'csharp-conditional';WriteText (Join-Path $f.Stage 'AssemblyInfo.cs') "#if false`n[assembly: AssemblyVersion(`"1.2.3.0`")]`n#endif";Only $f (Surface 'Conditional' 'AssemblyInfo.cs' 'CSharpAttribute' 'AssemblyVersion' 'Binary')
Assert ((Field (Run $f) 'Conditional').Status -eq 'UnresolvedPreprocessor') 'inactive source attribute cannot establish actual version'
$f=Fixture 'csharp-comment';WriteText (Join-Path $f.Stage 'AssemblyInfo.cs') '/* [assembly: AssemblyVersion("9.9")] */ [assembly: AssemblyVersion(@"1.2.3.0")]';Only $f (Surface 'Attr' 'AssemblyInfo.cs' 'CSharpAttribute' 'AssemblyVersion' 'Binary')
Assert ((Run $f).State -eq 'Consistent') 'verbatim CSharp literal and comment handling'

$f=Fixture 'compiled';$marker=Join-Path $f.Root 'attribute-executed.txt';$ns='VersionFixture'+[guid]::NewGuid().ToString('N');$escaped=$marker.Replace('\','\\');$code=@"
using System;
using System.Reflection;
[assembly: AssemblyVersion("1.2.3.0")]
[assembly: AssemblyFileVersion("1.2.3.0")]
[assembly: AssemblyInformationalVersion("1.2.3+fixture")]
[assembly: $ns.Trap("$escaped")]
namespace $ns {
public sealed class TrapAttribute : Attribute { public TrapAttribute(string target) { System.IO.File.WriteAllText(target,"executed"); } }
public sealed class Sample { }
}
"@
$dll=Join-Path $f.Stage 'Fixture.dll';Add-Type -TypeDefinition $code -OutputAssembly $dll
$compiledDll=$dll
Assert (!(Test-Path -LiteralPath $marker)) 'synthetic assembly construction does not execute trap attribute'
$f.Profile.Surfaces=@((Surface 'Assembly' 'Fixture.dll' 'AssemblyMetadata' 'AssemblyVersion' 'Binary'),(Surface 'File' 'Fixture.dll' 'AssemblyMetadata' 'AssemblyFileVersion' 'Binary'),(Surface 'Info' 'Fixture.dll' 'AssemblyMetadata' 'AssemblyInformationalVersion' 'Release' 'IgnoreBuildMetadata'));SaveProfile $f;$r=Run $f
Assert ($r.State -eq 'Consistent' -and $r.Matches -eq 3) 'actual compiled metadata versions inspected'
Assert (!(Test-Path -LiteralPath $marker)) 'metadata inspection never instantiates custom attributes'
$nativeValue=[Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
if(![string]::IsNullOrWhiteSpace($nativeValue)){$f.Profile.Groups+=[pscustomobject]@{Key='Native';Expected=$nativeValue};$f.Profile.Surfaces+=Surface 'Resource' 'Fixture.dll' 'WindowsVersionResource' 'FileVersion' 'Native';SaveProfile $f;Assert ((Run $f).State -eq 'Consistent') 'Windows version resource adapter independent of managed attributes'}else{$f.Profile.Surfaces=@(Surface 'Resource' 'Fixture.dll' 'WindowsVersionResource' 'FileVersion' 'Binary');SaveProfile $f;Assert ((Run $f).State -eq 'Failed') 'absent Windows resource is explicit rather than invented'}
$f=Fixture 'native-not-managed';WriteText (Join-Path $f.Stage 'native.dll') 'not a managed assembly';Only $f (Surface 'Binary' 'native.dll' 'AssemblyMetadata' 'AssemblyVersion' 'Binary')
Assert ((Run $f).State -eq 'Unresolved') 'unsupported binary metadata is not guessed'
$f=Fixture 'packed-dll';Zip $f @('Example/Fixture.dll') '' ([IO.File]::ReadAllBytes($compiledDll));$s=ZipSurface 'PackedAssembly' 'Example/Fixture.dll';$s.Type='AssemblyMetadata';$s.Field='AssemblyVersion';$s.Group='Binary';Only $f $s
Assert ((Run $f).State -eq 'Consistent') 'compiled metadata can be read directly from a selected ZIP entry'

$f=Fixture 'zip';Zip $f;Only $f (ZipSurface);$zipHash=(Get-FileHash -LiteralPath (Join-Path $f.Packages 'mod.zip')).Hash;$r=Run $f
Assert ($r.State -eq 'Consistent' -and (Field $r 'ZipIdentity').EntrySHA256 -match '^[A-F0-9]{64}$') 'ZIP manifest read with separate archive/entry hashes'
Assert ((Get-FileHash -LiteralPath (Join-Path $f.Packages 'mod.zip')).Hash -eq $zipHash -and !(Test-Path -LiteralPath (Join-Path $f.Root 'Example'))) 'ZIP input unchanged and not extracted'
$f=Fixture 'zip-missing';Zip $f @('Other/mod.rules');Only $f (ZipSurface)
Assert ((Run $f).State -eq 'Failed') 'missing configured archive entry fails'
$f=Fixture 'zip-duplicate';Zip $f @('Example/mod.rules','Example/mod.rules');Only $f (ZipSurface)
Assert ((Field (Run $f) 'ZipIdentity').Status -eq 'AmbiguousArchiveEntry') 'duplicate selected ZIP entry rejected'
$f=Fixture 'zip-case';Zip $f @('EXAMPLE/mod.rules');Only $f (ZipSurface)
Assert ((Field (Run $f) 'ZipIdentity').Status -eq 'UnresolvedArchiveEntryCase') 'archive path casing explicit'
$f=Fixture 'zip-corrupt';WriteText (Join-Path $f.Packages 'mod.zip') 'not ZIP';Only $f (ZipSurface)
Assert ((Run $f).State -eq 'Unresolved') 'unreadable archive is not clean version evidence'
$f=Fixture 'zip-resource';Zip $f;$s=ZipSurface;$s.Type='WindowsVersionResource';$s.Field='FileVersion';Only $f $s
Assert ((Field (Run $f) 'ZipIdentity').Status -eq 'UnresolvedStorage') 'Windows resource adapter never extracts ZIP payload'
$f=Fixture 'output-overlap';$f.Profile.OutputRoot=Join-Path $f.Stage 'reports';SaveProfile $f
Refuses {Run $f} 'OutputRoot must be separate'
$f=Fixture 'protected-output';$f.Profile.OutputRoot=Join-Path $f.Root 'game/reports';SaveProfile $f
Refuses {Run $f} 'OutputRoot must be separate'
foreach($bad in @('../escape.rules',([IO.Path]::GetPathRoot($base)+'outside.rules'),'file:stream','*.rules','NUL.rules','file.','a\b')){$f=Fixture ([guid]::NewGuid().ToString('N'));$f.Profile.Surfaces[0].Path=$bad;SaveProfile $f;Refuses {Run $f} 'Invalid|Unsafe'}
$f=Fixture 'unsafe-zip-selection';Zip $f;Only $f (ZipSurface 'Unsafe' '../mod.rules')
Refuses {Run $f} 'Unsafe relative'
$f=Fixture 'duplicate-surface';$f.Profile.Surfaces[1].Key=$f.Profile.Surfaces[0].Key;SaveProfile $f
Refuses {Run $f} 'unique keys'
$f=Fixture 'all-optional';foreach($s in $f.Profile.Surfaces){$s.Required=$false};SaveProfile $f
Refuses {Run $f} 'At least one required'
$f=Fixture 'input-directory';[IO.File]::Delete((Join-Path $f.Stage 'README.md'));$null=[IO.Directory]::CreateDirectory((Join-Path $f.Stage 'README.md'))
Refuses {Run $f} 'found a directory'
$f=Fixture 'regex-timeout';WriteText (Join-Path $f.Stage 'README.md') (('a'*50000)+'!');$s=Surface 'SlowRegex' 'README.md' 'TextPattern' '';$s|Add-Member -NotePropertyName Pattern -NotePropertyValue '(?<version>(a+)+)$';Only $f $s;$f.Profile|Add-Member -NotePropertyName Limits -NotePropertyValue ([pscustomobject]@{RegexTimeoutMs=1});SaveProfile $f
Assert ((Run $f).State -eq 'Unresolved') 'regex timeouts never become clean matches'
$f=Fixture 'unknown-group';$f.Profile.Surfaces[0].Group='not-declared';SaveProfile $f
Refuses {Run $f} 'known Group'
$f=Fixture 'junction';$outside=Join-Path $f.Root 'outside';WriteText (Join-Path $outside 'mod.rules') 'Version = 1.2.3';$null=New-Item -ItemType Junction -Path (Join-Path $f.Stage 'linked') -Target $outside;$f.Profile.Surfaces[1].Path='linked/mod.rules';SaveProfile $f
Refuses {Run $f} 'Reparse point'
$f=Fixture 'max-input';$f.Profile|Add-Member -NotePropertyName Limits -NotePropertyValue ([pscustomobject]@{MaxInputBytes=1});SaveProfile $f
Refuses {Run $f} 'byte limit'
$f=Fixture 'max-surfaces';$f.Profile|Add-Member -NotePropertyName Limits -NotePropertyValue ([pscustomobject]@{MaxSurfaces=1});SaveProfile $f
Refuses {Run $f} 'MaxSurfaces'
$f=Fixture 'profile-bom';[IO.File]::WriteAllText($f.Path,($f.Profile|ConvertTo-Json -Depth 32),[Text.Encoding]::Unicode);$r=Run $f
Assert ((Json $r.ReportPath).ProfileSHA256 -eq (Get-FileHash -LiteralPath $f.Path).Hash) 'profile hash preserves exact encoded bytes'

# Stardeus-specific manifests and private output boundary.
$f=Fixture 'stardeus-manifest'
WriteText (Join-Path $f.Stage 'ModInfo.json') '{"Id":"fixture.mod","ModVersion":"1.2.3","GameVersion":"0.15.0","Dependencies":[]}'
$s=Surface 'Manifest' 'ModInfo.json' 'JsonScalar' ''; $s|Add-Member -NotePropertyName Pointer -NotePropertyValue '/ModVersion'; Only $f $s
Assert ((Run $f).State -eq 'Consistent') 'Stardeus ModVersion is checked without edits'
WriteText (Join-Path $f.Stage 'ModInfo.json') '{"Id":"fixture.mod","ModVersion":"1.2.3","Atlas":{"MipMapBias":-0.2}}'
Assert ((Run $f).State -eq 'Consistent') 'unselected valid fractional metadata does not block version checking'
WriteText (Join-Path $f.Stage 'ModInfo.json') '{"Id":"fixture.mod","ModVersion":"1.2.3","modversion":"9.9.9"}'
Assert ((Run $f).State -eq 'Unresolved') 'case-colliding manifest properties cannot establish a version'
WriteText (Join-Path $f.Stage 'ModInfo.json') '{"Id":"fixture.mod","ModVersion":"1.2.3","Nested":{"Key":1,"Key":2}}'
Assert ((Run $f).State -eq 'Unresolved') 'duplicate nested properties remain unresolved'
$f=Fixture 'repository-output';$f.Profile.OutputRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../forbidden-output'));SaveProfile $f
Refuses {Run $f} 'private-source-overlap-refused'
$f=Fixture 'player-data-output';$f.Profile.OutputRoot=Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData/LocalLow/Kodo Linija/Stardeus/Mods/forbidden-output';SaveProfile $f
Refuses {Run $f} 'private-live-or-game-overlap-refused'
$result=[pscustomobject]@{State='Passed';Assertions=$script:assertions;FixtureRoot=$base;LiveModsTouched=$false;PowerShell=$PSVersionTable.PSVersion.ToString();ToolHash=(Get-FileHash -LiteralPath $tool).Hash;TestHash=(Get-FileHash -LiteralPath $PSCommandPath).Hash;CompletedUtc=[DateTime]::UtcNow.ToString('o')}
WriteText (Join-Path $base 'test-results.json') ($result|ConvertTo-Json);$result
