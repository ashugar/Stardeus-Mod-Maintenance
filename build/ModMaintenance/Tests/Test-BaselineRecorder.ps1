#requires -Version 7.2
[CmdletBinding()]
param([string]$TestRoot=(Join-Path ([IO.Path]::GetTempPath()) 'StardeusBaselineFixtures'))
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../Common.ps1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
Assert-ModExternalPath $repo $TestRoot
$base=Join-Path $TestRoot ([guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($base)
$tool=Join-Path $PSScriptRoot '../Record-ModBaseline.ps1'
$count=0
function Assert($ok,[string]$message) { if (!$ok) { throw "FAILED: $message" }; $script:count++ }
function WriteFixture([string]$path,[string]$value) { $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)); [IO.File]::WriteAllText($path,$value) }
function Refuses([scriptblock]$action) { $failed=$false; try { $null=& $action } catch { $failed=$true }; Assert $failed 'expected refusal' }
$game=Join-Path $base 'game'; $mod=Join-Path $base 'mod'; $output=Join-Path $base 'reports'; $path=Join-Path $base 'profile.json'
$dll=Join-Path $game 'Managed/Fixture.dll'; $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dll))
$type='BaselineFixture.V'+[guid]::NewGuid().ToString('N')
Add-Type -TypeDefinition ('namespace BaselineFixture { public class '+$type.Split('.')[1]+' { public const string Version="test-build"; } }') -OutputAssembly $dll
$manifest=Join-Path $mod 'ModInfo.json'
$normal='{"Id":"Fixture","Name":"Fixture","ModVersion":"1.2.3","GameVersion":"0.15.2","Dependencies":["Other"],"DLLFiles":[]}'
WriteFixture $manifest $normal
WriteFixture (Join-Path $mod 'data/item.txt') 'one'
WriteFixture (Join-Path $mod 'obj/ignored.txt') 'excluded'
$p=[pscustomobject]@{SchemaVersion=1;BaselineId='fixture';OutputRoot=$output;Game=[pscustomobject]@{Root=$game;VersionAssembly='Managed/Fixture.dll';VersionType=$type;VersionField='Version';Files=@('Managed/Fixture.dll');Trees=@();ExcludeDirectories=@();ProcessNames=@('BaselineFixtureNonexistent')};Mods=@([pscustomobject]@{Key='primary';Role='Primary';Root=$mod;ExpectedId='Fixture';Manifest='ModInfo.json';Files=@('ModInfo.json');Trees=@('.');ExcludeDirectories=@('obj');DependencyKeys=@()})}
function Save { WriteFixture $path ($p|ConvertTo-Json -Depth 30) }
function Capture { & $tool -ProfilePath $path }
Save
$before=(Get-FileHash $manifest).Hash
$a=Capture; $snapshot=Get-Content $a.SnapshotPath -Raw|ConvertFrom-Json
Assert ($a.State -eq 'Recorded') 'clean capture'
Assert ($snapshot.Kind -eq 'StardeusBaseline') 'Stardeus kind'
Assert ($snapshot.Game.Version.Value -eq 'test-build') 'constant extracted'
Assert ($snapshot.Game.Version.MVID -match '^[a-f0-9-]{36}$') 'MVID'
Assert ($snapshot.Mods[0].EnabledState -eq 'Unknown') 'enabled unknown'
Assert ($snapshot.Mods[0].Manifests[0].Metadata.Dependencies.Value[0] -eq 'Other') 'dependency declaration retained'
Assert (@($snapshot.Files|Where-Object Path -like 'obj/*').Count -eq 0) 'excluded tree'
Assert ((Get-FileHash $manifest).Hash -eq $before) 'unchanged input'
$b=& $tool -ProfilePath $path -PreviousSnapshot $a.SnapshotPath
Assert ($b.Comparison -eq 'NoChangesInRecordedScope') 'unchanged comparison'
Assert ($b.RunPath -ne $a.RunPath) 'unique run'
(Get-Item $manifest).LastWriteTimeUtc=[datetime]::UtcNow.AddDays(-2)
$c=& $tool -ProfilePath $path -PreviousSnapshot $b.SnapshotPath
Assert ($c.Comparison -eq 'NoChangesInRecordedScope') 'timestamps do not establish byte changes'
WriteFixture (Join-Path $mod 'data/item.txt') 'two'
WriteFixture (Join-Path $mod 'data/new.txt') 'new'
$d=& $tool -ProfilePath $path -PreviousSnapshot $c.SnapshotPath
Assert ($d.Comparison -eq 'ChangesFound' -and $d.Added -eq 1 -and $d.Changed -eq 1) 'added/changed hashes'
$p.Mods[0].Files+= 'missing.txt'; Save
$e=& $tool -ProfilePath $path -PreviousSnapshot $d.SnapshotPath
Assert ($e.State -eq 'RecordedWithGaps' -and $e.Comparison -eq 'IncompleteEvidence') 'missing evidence remains gap'
$p.Mods[0].Files=@('ModInfo.json');$p.Mods[0].Trees=@();Save
$f=& $tool -ProfilePath $path -PreviousSnapshot $d.SnapshotPath
Assert ($f.Comparison -eq 'ScopeChanged') 'scope drift'
$p.Game.VersionType=$null;$p.Game.VersionField=$null;Save
$g=Capture; $gs=Get-Content $g.SnapshotPath -Raw|ConvertFrom-Json
Assert ($g.State -eq 'RecordedWithGaps' -and $gs.Game.Version.Status -eq 'Unknown' -and $gs.Game.Version.MVID) 'unknown build keeps module evidence'
WriteFixture $manifest '{"Id":"Fixture","id":"Fixture"}'
Refuses { Capture }
WriteFixture $manifest '{"Id":"Wrong","ModVersion":"1"}'
Refuses { Capture }
WriteFixture $manifest $normal
WriteFixture $path '{"SchemaVersion":1,"schemaversion":1}'
Refuses { Capture }
Save
$p.Mods[0].Files=@('ModInfo.json','obj/ignored.txt');Save
Refuses { Capture }
$p.Mods[0].Files=@('ModInfo.json');$p.Mods[0].Trees=@('missing');Save
Refuses { Capture }
$p.Mods[0].Trees=@();$p.Mods[0].Files=@('../escape');Save
Refuses { Capture }
$p.Mods[0].Files=@('ModInfo.json');$p.OutputRoot=Join-Path $mod 'reports';Save
Refuses { Capture }
$p.OutputRoot=Join-Path $repo 'baseline-test-output';Save
Refuses { Capture }
$p.OutputRoot=$output;$p.Game.Root=$game+'/../game';Save
Refuses { Capture }
$p.Game.Root=$game;Save
$compare=& $tool -Mode Compare -SnapshotPath $b.SnapshotPath -PreviousSnapshot $a.SnapshotPath -OutputRoot (Join-Path $base 'comparison-only')
Assert ($compare.State -eq 'Compared' -and $compare.Comparison -eq 'NoChangesInRecordedScope') 'standalone saved comparison'
$junction=Join-Path $base 'junction'
$null=New-Item -ItemType Junction -Path $junction -Target $mod
$p.Mods[0].Root=$junction;Save
Refuses { Capture }
$p.Mods[0].Root=$mod;$p.Mods[0].ExpectedId='Core';$p.Game.VersionType=$type;$p.Game.VersionField='Version';Save
WriteFixture $manifest '{"Id":"Core","Name":"Core"}'
$core=Capture;$coreSnapshot=Get-Content $core.SnapshotPath -Raw|ConvertFrom-Json
Assert ($core.State -eq 'RecordedWithGaps' -and $coreSnapshot.Mods[0].Manifests[0].Metadata.Id.Value -ceq 'Core') 'Core identity allowed with explicit missing version gaps'
$modified=Get-Content $a.SnapshotPath -Raw|ConvertFrom-Json
$modified.CommonSHA256='A'*64
$modifiedPath=Join-Path $base 'changed-common.json'
WriteFixture $modifiedPath ($modified|ConvertTo-Json -Depth 40)
$provenance=& $tool -Mode Compare -SnapshotPath $modifiedPath -PreviousSnapshot $a.SnapshotPath -OutputRoot (Join-Path $base 'provenance-comparison')
Assert ($provenance.Comparison -eq 'ChangesFound' -and $provenance.MetadataChanges -ge 1) 'shared Common provenance drift visible'
Assert (@($coreSnapshot.Files|Where-Object Entity -eq 'Mod:primary').Count -eq 1) 'explicit manifest-only scope omits unselected data'
$p.Mods[0].Files=@();Save
Refuses { Capture }
$p.Mods[0].Files=@('ModInfo.json');$p.Mods[0].Role='Reference';$p.Mods[0]|Add-Member -NotePropertyName ManifestFormat -NotePropertyValue 'CoreJson';Save
WriteFixture $manifest '{ // installed Core metadata permits comments
"Id":"Core", "Name":"Core", "MipMapBias":-0.2,
}'
$coreExtended=Capture
Assert ($coreExtended.State -eq 'RecordedWithGaps') 'explicit Core parser handles comments float and trailing comma'
WriteFixture $manifest '{"Id":"Core","Nested":{"Value":1,"value":2}}'
Refuses { Capture }
$p.Mods[0].Role='Primary';Save
Refuses { Capture }
$p.Mods[0].ManifestFormat='StrictJson';$p.Mods[0].ExpectedId='Fixture';Save
WriteFixture $manifest '{ // ordinary mods stay strict
"Id":"Fixture"}'
Refuses { Capture }
$result=[pscustomobject]@{Assertions=$count;State='Passed';RunPath=$base;ToolSHA256=(Get-FileHash $tool).Hash;TestSHA256=(Get-FileHash $PSCommandPath).Hash}
$result|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $base 'test-results.json') -Encoding utf8
$result
