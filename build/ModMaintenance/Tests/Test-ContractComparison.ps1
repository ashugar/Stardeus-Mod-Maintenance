#requires -Version 7.2
[CmdletBinding()]
param([string]$TestRoot=(Join-Path ([IO.Path]::GetTempPath()) ('StardeusContractFixtures-'+[guid]::NewGuid().ToString('N'))))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../Common.ps1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'));Assert-ModExternalPath $repo $TestRoot
if(Test-Path -LiteralPath $TestRoot){throw 'New private TestRoot required.'}
$null=[IO.Directory]::CreateDirectory($TestRoot);$tool=Join-Path $PSScriptRoot '../Compare-ModContracts.ps1';$count=0
function Check($ok,[string]$msg){if(!$ok){throw $msg};$script:count++}
function WriteFixture([string]$p,[string]$s){$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p));[IO.File]::WriteAllText($p,$s)}
function Json($p){Get-Content -LiteralPath $p -Raw|ConvertFrom-Json -Depth 64}
function Build([string]$key,[string]$source){
 $root=Join-Path $TestRoot $key;WriteFixture (Join-Path $root 'Fixture.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>ContractFixture</AssemblyName><GenerateAssemblyInfo>false</GenerateAssemblyInfo><Nullable>enable</Nullable></PropertyGroup></Project>'
 WriteFixture (Join-Path $root 'Probe.cs') $source
 WriteFixture (Join-Path $root 'NuGet.Config') '<configuration><packageSources><clear /></packageSources></configuration>'
 $null=Invoke-ModProcess dotnet @('build',(Join-Path $root 'Fixture.csproj'),'-c','Release','--artifacts-path',(Join-Path $root 'artifacts'),'-o',(Join-Path $root 'out'),'-p:ImportDirectoryBuildProps=false','-p:ImportDirectoryBuildTargets=false','-p:ImportDirectoryPackagesProps=false','-p:NuGetAudit=false',('-p:RestoreConfigFile='+(Join-Path $root 'NuGet.Config'))) $root
 return Join-Path $root 'out/ContractFixture.dll'
}
$code=@'
using System;
using System.Reflection;
[assembly: AssemblyVersion("1.0.0.0")]
namespace Fixture {
public class Probe {
 public const int Limit=4;
 public int Value(int n)=>Other(n)+4;
 int Other(int n)=>n*2;
 public string Text()=>"alpha";
 public int Flow(int n){if(n>3)return 8;return 2;}
 public int Handle(int n){try{return 30/n;}catch(DivideByZeroException){return -1;}}
 public void Over(int n){} public void Over(string n){}
}
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct LayoutProbe { public int A; public long B; }
public abstract class AbstractProbe { public abstract int MissingBody(); }
}
'@
$before=Build 'before' $code
$same=Build 'same' ($code.Replace('1.0.0.0','2.0.0.0').Replace('public class Probe','public class Irrelevant { public int Noise()=>123; } public class Probe'))
$changed=Build 'changed' ($code.Replace('Limit=4','Limit=5').Replace('Other(n)+4','Other(n)+5').Replace('"alpha"','"beta"').Replace('n>3','n>4').Replace('catch(DivideByZeroException)','catch(ArgumentException)').Replace('public int A; public long B;','public long B; public int A;'))
$mod=Join-Path $TestRoot 'mod';WriteFixture (Join-Path $mod 'ModInfo.json') '{"Id":"Fixture","ModVersion":"1.0.0","GameVersion":"0.15.2"}'
$profile=@{SchemaVersion=1;ComparisonId='fixture';Subject=@{ModId='Fixture';Root=$mod};Before=$before;After=$same;Selections=@(@{Key='Value';Kind='Method';Type='Fixture.Probe';Name='Value'});OutputRoot=(Join-Path $TestRoot 'reports');ProtectedRoots=@()}
$pp=Join-Path $TestRoot 'profile.json'
function Run { $profile|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $pp; $summary=& $tool -ProfilePath $pp -ReportOnly;  $script:LastContractReportPath=$summary.ReportPath; return Json $summary.ReportPath }
$r=Run;$goodReportPath=$script:LastContractReportPath;Check ($r.State -eq 'UnchangedInSelectedScope') 'token/MVID/own version changes must not change selected method contract'
Check ($r.Inputs.Count -eq 3 -and @($r.Inputs|Where-Object {$_.PSObject.Properties.Name -contains 'MVID'}).Count -eq 2) 'exact input hashes and metadata identity retained'
Check ($r.Selections[0].Before.Hash -eq $r.Selections[0].After.Hash) 'normalized contract hashes match'
Check ($r.Selections[0].Before.Text -match 'Other') 'readable member identity retained'
$profile.After=$changed;$r=Run;Check ($r.State -eq 'ChangesFound') 'literal change detected'
$profile.Selections=@(@{Key='Text';Kind='Method';Type='Fixture.Probe';Name='Text'});Check ((Run).State -eq 'ChangesFound') 'string token values compared'
$profile.Selections=@(@{Key='Flow';Kind='Method';Type='Fixture.Probe';Name='Flow'});Check ((Run).State -eq 'ChangesFound') 'control flow/literal changes detected'
$profile.Selections=@(@{Key='EH';Kind='Method';Type='Fixture.Probe';Name='Handle'});Check ((Run).State -eq 'ChangesFound') 'exception-region changes detected'
$profile.Selections=@(@{Key='Field';Kind='Field';Type='Fixture.Probe';Name='Limit'});Check ((Run).State -eq 'ChangesFound') 'constant field value change detected'
$profile.After=$same;$profile.Selections=@(@{Key='Type';Kind='Type';Type='Fixture.Probe'});Check ((Run).State -eq 'UnchangedInSelectedScope') 'selected type shape unchanged'
$profile.Selections=@(@{Key='Over';Kind='Method';Type='Fixture.Probe';Name='Over'});Check ((Run).State -eq 'ComparedWithGaps') 'ambiguous overload is unresolved'
$profile.Selections=@(@{Key='Over';Kind='Method';Type='Fixture.Probe';Name='Over';ParameterTypes=@('Int32')});Check ((Run).State -eq 'UnchangedInSelectedScope') 'explicit exact overload supported'
$profile.Selections=@(@{Key='Body';Kind='Method';Type='Fixture.AbstractProbe';Name='MissingBody'});Check ((Run).State -eq 'ComparedWithGaps') 'missing managed body not treated as empty identical IL'
$profile.Selections=@(@{Key='Absent';Kind='Field';Type='Fixture.Probe';Name='NoSuchField'});Check ((Run).State -eq 'ComparedWithGaps') 'missing selection retained gap'
$profile.Selections=@(@{Key='Value';Kind='Method';Type='Fixture.Probe';Name='Value'});$profile.Extra='bad';$failed=$false;try{$null=Run}catch{$failed=$true};Check $failed 'unknown profile keys refused';$profile.Remove('Extra')
$profile.OutputRoot=$repo;$failed=$false;try{$null=Run}catch{$failed=$true};Check $failed 'repository output refused';$profile.OutputRoot=Join-Path $TestRoot 'reports'
$profile.Subject.ModId='Wrong';$failed=$false;try{$null=Run}catch{$failed=$true};Check $failed 'wrong subject identity refused';$profile.Subject.ModId='Fixture'
$profile.After=Join-Path $TestRoot 'invalid.dll';WriteFixture $profile.After 'invalid PE';$r=Run;Check ($r.State -eq 'Failed' -and $r.Gaps.Count -gt 0) 'malformed metadata retains failed report'
$profile.After=$same;$profile.Selections=@(@{Key='A';Kind='Method';Type='Fixture.Probe';Name='Value'},@{Key='A';Kind='Method';Type='Fixture.Probe';Name='Value'});$failed=$false;try{$null=Run}catch{$failed=$true};Check $failed 'duplicate selection refused'

# Malformed-IL fixtures mutate byte copies only; no fixture method is executed.
if(-not ('ContractFixtureMutatorV1' -as [type])){
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
public static class ContractFixtureMutatorV1 {
 public static void Mutate(string source,string destination,string method,string mutation) {
  byte[] bytes=File.ReadAllBytes(source);
  using(var stream=new MemoryStream(bytes,false))using(var pe=new PEReader(stream)){
   var r=pe.GetMetadataReader();var th=r.TypeDefinitions.Single(h=>r.GetString(r.GetTypeDefinition(h).Name)=="Probe");
   var mh=r.GetTypeDefinition(th).GetMethods().Single(h=>r.GetString(r.GetMethodDefinition(h).Name)==method);var m=r.GetMethodDefinition(mh);
   var section=pe.PEHeaders.SectionHeaders.Single(s=>m.RelativeVirtualAddress>=s.VirtualAddress&&m.RelativeVirtualAddress<s.VirtualAddress+Math.Max(s.VirtualSize,s.SizeOfRawData));
   int body=section.PointerToRawData+m.RelativeVirtualAddress-section.VirtualAddress;
   int header=(bytes[body]&3)==2?1:((BitConverter.ToUInt16(bytes,body)>>12)*4);int il=body+header;
   if(mutation=="StringToken"){if(bytes[il]!=0x72)throw new Exception("Expected ldstr fixture.");bytes[il+4]=0x01;}
   else if(mutation=="Argument"){if(bytes[il]!=0x03)throw new Exception("Expected ldarg.1 fixture.");bytes[il]=0x05;}
   else throw new Exception("Unknown fixture mutation.");
  }File.WriteAllBytes(destination,bytes);
 }
}
'@
}
$badString=Join-Path $TestRoot 'bad-string.dll';[ContractFixtureMutatorV1]::Mutate($before,$badString,'Text','StringToken')
$profile.Before=$badString;$profile.After=$badString;$profile.Selections=@(@{Key='BadString';Kind='Method';Type='Fixture.Probe';Name='Text'})
Check ((Run).State -eq 'ComparedWithGaps') 'malformed ldstr token table cannot compare unchanged'
$badArgument=Join-Path $TestRoot 'bad-argument.dll';[ContractFixtureMutatorV1]::Mutate($before,$badArgument,'Flow','Argument')
$profile.Before=$badArgument;$profile.After=$badArgument;$profile.Selections=@(@{Key='BadArgument';Kind='Method';Type='Fixture.Probe';Name='Flow'})
Check ((Run).State -eq 'ComparedWithGaps') 'out-of-range argument operand cannot compare unchanged'
$profile.Before=$before;$profile.After=$changed;$profile.Selections=@(@{Key='Layout';Kind='Type';Type='Fixture.LayoutProbe'})
Check ((Run).State -eq 'ChangesFound') 'sequential field declaration order is part of selected type contract'
$pwsh=(Get-Command pwsh).Source
$failed=$false;try{$null=Invoke-DiagnosticChild -Program $pwsh -Arguments @('-NoProfile','-Command',"[Console]::Write(('x'*8192))") -WorkingDirectory $TestRoot -MaxBytes 100}catch{$failed=$true}
Check $failed 'diagnostic stdout allocation is bounded'
$failed=$false;try{$null=Invoke-DiagnosticChild -Program $pwsh -Arguments @('-NoProfile','-Command','Start-Sleep -Seconds 10') -WorkingDirectory $TestRoot -TimeoutMilliseconds 50}catch{$failed=$true}
Check $failed 'owned diagnostic child has a wall-clock bound'

$result=@{State='Passed';Assertions=$count;CreatedUtc=[DateTime]::UtcNow.ToString('o');Root=$TestRoot;ToolSHA256=(Get-FileHash -LiteralPath $tool).Hash;RunnerSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../MetadataRunner.ps1')).Hash;UnchangedReportPath=$goodReportPath;EngineSources=@(Get-ChildItem (Join-Path $PSScriptRoot '../MetadataDiagnostics') -File|ForEach-Object {@{Name=$_.Name;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}});TestSHA256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;Limitations='Synthetic metadata only; fixture methods never executed.'}
$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $TestRoot 'test-results.json');$result
