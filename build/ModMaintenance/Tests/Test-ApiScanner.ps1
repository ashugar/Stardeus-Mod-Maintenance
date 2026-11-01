#requires -Version 7.2
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('StardeusApiFixtures-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory $root
$tool=Join-Path $PSScriptRoot '../MetadataDiagnostics/MetadataDiagnostics.csproj'
$config=Join-Path $root 'NuGet.Config';Set-Content $config '<configuration><packageSources><clear /></packageSources></configuration>'
$count=0
function Check($ok,[string]$message){if(!$ok){throw "Assertion failed: $message"};$script:count++}
function Build([string]$name,[string]$source,[string]$assembly,[string]$reference=''){
 $dir=Join-Path $root $name;$null=New-Item -ItemType Directory $dir
 $referenceXml=if($reference){'<ItemGroup><Reference Include="FixtureCore"><HintPath>'+[Security.SecurityElement]::Escape($reference)+'</HintPath></Reference></ItemGroup>'}else{''}
 Set-Content (Join-Path $dir 'Fixture.csproj') ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>'+$assembly+'</AssemblyName><Nullable>enable</Nullable></PropertyGroup>'+$referenceXml+'</Project>')
 Set-Content (Join-Path $dir 'Fixture.cs') $source
 $log=& dotnet build (Join-Path $dir 'Fixture.csproj') --artifacts-path (Join-Path $dir 'artifacts') --output (Join-Path $dir 'out') ('-p:RestoreConfigFile='+$config) -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:NuGetAudit=false --nologo 2>&1
 if($LASTEXITCODE){throw ($log -join "`n")};return Join-Path $dir ('out/'+$assembly+'.dll')
}
$core=Build core 'namespace FixtureCore { public class Api { public int Value; public static int Read(int value)=>value; public static string Read(string value)=>value; public int Get(int value)=>value; public int Property {get;set;} public Api(){} public static ref readonly int Ref(in int value)=>ref value; } }' FixtureCore
$consumerSource=@'
using System;
namespace HarmonyLib {
 public enum MethodType {Normal,Getter,Setter,Constructor,StaticConstructor}
 [AttributeUsage(AttributeTargets.Class|AttributeTargets.Method,AllowMultiple=true)] public class HarmonyPatch:Attribute {public HarmonyPatch(Type t,string n){} public HarmonyPatch(Type t,string n,Type[] a){} public HarmonyPatch(Type t,MethodType m){} public HarmonyPatch(Type t,string n,MethodType m){} }

}
public static class Calls {public static int Go(FixtureCore.Api a)=>FixtureCore.Api.Read(a.Value); public static ref readonly int Ref(in int v)=>ref FixtureCore.Api.Ref(in v);}
[HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Get",new Type[]{typeof(int)})] public static class GoodPatch {public static void Prefix(int value,ref int __result,ref int ___Value){} }
[HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),HarmonyLib.MethodType.Constructor)] public static class ConstructorPatch {public static void Postfix(){} }
[HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Property",HarmonyLib.MethodType.Getter)] public static class GetterPatch {public static void Postfix(int __result){} }
[HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Property",HarmonyLib.MethodType.Setter)] public static class SetterPatch {public static void Prefix(int value){} }
'@
$consumer=Build consumer $consumerSource FixtureConsumer $core
$engineOut=Join-Path $root 'engine'
$log=& dotnet build $tool --artifacts-path (Join-Path $root 'engine-artifacts') --output $engineOut ('-p:RestoreConfigFile='+$config) -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:NuGetAudit=false --nologo 2>&1
if($LASTEXITCODE){throw ($log -join "`n")}
function Run([string]$assembly,[string[]]$deps){$p=Join-Path $root ('profile-'+[guid]::NewGuid().ToString('N')+'.json');@{SchemaVersion=1;CheckId='Fixture';Assemblies=@($assembly);Dependencies=@($deps);ReferenceAssemblyNames=@('FixtureCore')}|ConvertTo-Json -Depth 10|Set-Content $p;$json=& dotnet (Join-Path $engineOut 'MetadataDiagnostics.dll') api $p;return $json|ConvertFrom-Json}
$r=Run $consumer @($core)
Check ($r.State -eq 'CheckedSupportedScope') ('good scope status '+($r.Gaps -join ';')+($r.Findings -join ';'))
Check ($r.Inputs.Count -eq 2) 'all explicit consumed inputs recorded'
Check (@($r.Inputs|Where-Object {$_.SHA256 -notmatch '^[A-F0-9]{64}$'}).Count -eq 0) 'input hashes'
Check (@($r.Inputs|Where-Object {!$_.MVID}).Count -eq 0) 'MVIDs recorded'
Check ($r.Coverage.FixedTargets -eq 4) 'four fixed Harmony targets'
Check ($r.Coverage.BindingChecks -eq 4) 'binding checks recorded'
Check (@($r.References|Where-Object {$_.Identity -match 'modreq'}).Count -gt 0) 'readonly signature modifier preserved'
Check (@($r.Patches|Where-Object {$_.Status -ne 'Resolved'}).Count -eq 0) 'constructor getter setter and exact targets resolve'
Check (@($r.Patches|Where-Object {$_.TargetStatus -ne 'Resolved' -or $_.BindingStatus -ne 'CheckedSupportedBindings'}).Count -eq 0) 'target and binding statuses separately successful'
$noHook=Build noHook 'using System;namespace HarmonyLib {[AttributeUsage(AttributeTargets.Class)]public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string n){}}}[HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Get")]public static class EmptyPatch{}' FixtureNoHook $core
$noHookReport=Run $noHook @($core)
Check ($noHookReport.State -eq 'CheckedWithGaps') 'resolved target with no hooks remains a gap'
Check ($noHookReport.Patches[0].TargetStatus -eq 'Resolved' -and $noHookReport.Patches[0].BindingStatus -eq 'NotRun') 'missing hooks never imply checked bindings'
$r=Run $consumer @()
Check ($r.State -eq 'Failed') 'missing explicit dependency fails'
Check (($r.Findings -join ';') -match 'dependency missing') 'missing dependency distinguished'
$bad=Build bad ($consumerSource.Replace('void Prefix(int value,ref int __result,ref int ___Value)','void Prefix(string value,int badname,ref int __result,ref int ___Value)')) FixtureBad $core
$r=Run $bad @($core)
Check ($r.State -eq 'Failed') 'bad bindings fail'
Check (($r.Findings -join ';') -match 'Binding type mismatch') 'wrong binding type'
Check (($r.Findings -join ';') -match 'Unknown original parameter') 'bad original argument name'
$amb=Build ambiguous 'using System; namespace HarmonyLib {[AttributeUsage(AttributeTargets.Class)]public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string n){}}} [HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Read")] public static class Ambiguous { public static void Prefix(){} }' FixtureAmb $core
$r=Run $amb @($core)
Check ($r.State -eq 'Failed') 'name-only overloaded target fails'
Check (@($r.Patches|Where-Object Status -eq AmbiguousTarget).Count -eq 1) 'ambiguity explicit'
$unsupported=Build unsupported 'using System; namespace HarmonyLib {[AttributeUsage(AttributeTargets.Class)]public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string n){}}public class Harmony{public void Patch(){}}} [HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Get")]public static class Dynamic {public static object TargetMethod()=>new object();public static void Transpiler(){}public static void Register()=>new HarmonyLib.Harmony().Patch();}' FixtureUnsupported $core
$r=Run $unsupported @($core)
Check ($r.State -eq 'CheckedWithGaps') 'unsupported patch coverage remains gaps'
Check ($r.Coverage.DynamicProviders -eq 1) 'dynamic provider counted'
Check ($r.Coverage.Transpilers -eq 1) 'transpiler counted'
Check ($r.Coverage.ManualRegistrationReferences -eq 1) 'manual registration counted'
$changed=Build changed 'namespace FixtureCore { public class Api {public int Value {get;set;} public static string Read(string value)=>value; public int Get(int value)=>value; public int Property{get;set;} public Api(){} public static ref readonly int Ref(in int value)=>ref value;} }' FixtureCore
$r=Run $consumer @($changed)
Check ($r.State -in @('CheckedWithGaps','Failed')) 'changed field/method cannot pass'
Check (($r.Gaps -join ';') -match 'inherited resolution') 'inherited member limitation explicit'
$r=Run $consumer @($core,$changed)
Check ($r.State -eq 'Failed') 'duplicate identities fail'
Check (($r.Gaps -join ';') -match 'Duplicate assembly') 'duplicate identity reason retained'
$genericCore=Build genericCore 'namespace FixtureCore { public class Generic<T>{public T Value=default!; public T Read()=>Value;} }' FixtureGenericCore
$generic=Build generic 'public static class Calls {public static int Read(FixtureCore.Generic<int> value)=>value.Read();}' FixtureGeneric $genericCore
$p=Join-Path $root 'generic-profile.json';@{SchemaVersion=1;CheckId='Generic';Assemblies=@($generic);Dependencies=@($genericCore);ReferenceAssemblyNames=@('FixtureGenericCore')}|ConvertTo-Json|Set-Content $p
$r=(& dotnet (Join-Path $engineOut 'MetadataDiagnostics.dll') api $p)|ConvertFrom-Json
Check ($r.State -eq 'CheckedWithGaps') 'generic owner remains unsupported'
Check (($r.Gaps -join ';') -match 'generic member owner') 'generic gap explicit'
$unselectedGeneric=Build unselectedGeneric 'public static class Calls {public static System.Action<FixtureCore.Api> Create()=>new System.Action<FixtureCore.Api>(Handle);public static void Handle(FixtureCore.Api value){}}' FixtureUnselectedGeneric $core
$r=Run $unselectedGeneric @($core)
Check ($r.State -eq 'CheckedSupportedScope') 'unselected generic owner carrying selected type adds no scope noise'
Check (@($r.Gaps).Count -eq 0) 'generic argument assembly does not select declaring owner'
$staticInstance=Build staticInstance ($consumerSource.Replace('void Prefix(int value,ref int __result,ref int ___Value)','void Prefix(int value,FixtureCore.Api __instance,ref int __result,ref int ___Value)').Replace('typeof(FixtureCore.Api),"Get",new Type[]{typeof(int)}','typeof(FixtureCore.Api),"Read",new Type[]{typeof(int)}')) FixtureStatic $core
$r=Run $staticInstance @($core)
Check (($r.Findings -join ';') -match '__instance used for static') 'instance parameter refused on static method'
$instance=Build instance ($consumerSource.Replace('void Prefix(int value,ref int __result,ref int ___Value)','void Prefix(int value,FixtureCore.Api __instance,ref int __result,ref int ___Value)')) FixtureInstance $core
$r=Run $instance @($core)
Check ($r.State -eq 'CheckedSupportedScope') 'exact instance type supported'
$voidResult=Build voidResult ($consumerSource.Replace('public static void Prefix(int value){}','public static void Prefix(int value,int __result){}')) FixtureVoidResult $core
$r=Run $voidResult @($core)
Check (($r.Findings -join ';') -match '__result used for void') 'result parameter refused on void setter'
$attributed=Build attributed 'using System; namespace HarmonyLib {[AttributeUsage(AttributeTargets.Class)]public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string n){}}[AttributeUsage(AttributeTargets.Method)]public class HarmonyTargetMethod:Attribute{}}[HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Get")]public static class Patch{[HarmonyLib.HarmonyTargetMethod]public static object Resolve()=>new object();}' FixtureAttributed $core
$r=Run $attributed @($core)
Check ($r.State -eq 'CheckedWithGaps') 'attributed provider remains a gap'
Check ($r.Coverage.DynamicProviders -eq 1) 'nonconventional attributed provider counted'
$mapped=Build mapped 'using System; namespace HarmonyLib {[AttributeUsage(AttributeTargets.Class)]public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string n){}}[AttributeUsage(AttributeTargets.Parameter)]public class HarmonyArgument:Attribute{public HarmonyArgument(string n){}}[AttributeUsage(AttributeTargets.Class)]public class HarmonyPriority:Attribute{public HarmonyPriority(int n){}}}[HarmonyLib.HarmonyPriority(100)][HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Get")]public static class Patch{public static void Prefix([HarmonyLib.HarmonyArgument("value")]int renamed){}}' FixtureMapped $core
$r=Run $mapped @($core)
Check ($r.State -eq 'CheckedWithGaps') 'mapped argument and order remain gaps'
Check (($r.Gaps -join ';') -match 'HarmonyArgument mapping') 'mapping gap explicit'
Check (($r.Gaps -join ';') -match 'lifecycle/order') 'order gap explicit'
$intPrefix=Build intPrefix ($consumerSource.Replace('public static void Prefix(int value,ref int __result,ref int ___Value){}','public static int Prefix(int value,ref int __result,ref int ___Value)=>1;')) FixtureIntPrefix $core
$r=Run $intPrefix @($core)
Check ($r.State -eq 'Failed') 'invalid integer Prefix return fails'
Check (($r.Findings -join ';') -match 'Invalid Prefix return') 'invalid Prefix reason explicit'
$boolPrefix=Build boolPrefix ($consumerSource.Replace('public static void Prefix(int value,ref int __result,ref int ___Value){}','public static bool Prefix(int value,ref int __result,ref int ___Value)=>true;')) FixtureBoolPrefix $core
$r=Run $boolPrefix @($core)
Check ($r.State -eq 'CheckedSupportedScope') 'boolean Prefix supported'
$returnPostfix=Build returnPostfix ($consumerSource.Replace('public static void Postfix(int __result){}','public static string Postfix(int __result)=>"unsupported";')) FixturePostfixReturn $core
$r=Run $returnPostfix @($core)
Check ($r.State -eq 'CheckedWithGaps') 'nonvoid Postfix is not approved'
Check (($r.Gaps -join ';') -match 'Postfix return/pass-through') 'Postfix return gap explicit'
$unknownRole=Build unknownRole ($consumerSource.Replace('[HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Get",new Type[]{typeof(int)})] public static class GoodPatch {public static void Prefix(int value,ref int __result,ref int ___Value){} }','public static class GoodPatch {[HarmonyLib.HarmonyPatch(typeof(FixtureCore.Api),"Get",new Type[]{typeof(int)})] public static void Arbitrary(int value){} }')) FixtureUnknownRole $core
$r=Run $unknownRole @($core)
Check ($r.State -eq 'CheckedWithGaps') 'unknown method-level hook role stays open'
Check (($r.Gaps -join ';') -match 'Unknown method-level patch hook role') 'unknown hook role explicit'
$manifestRoot=Join-Path $root 'subject';$null=New-Item -ItemType Directory $manifestRoot
Set-Content (Join-Path $manifestRoot 'ModInfo.json') '{"Id":"FixtureConsumer"}'
$wrapperProfile=Join-Path $root 'wrapper-profile.json'
@{SchemaVersion=1;CheckId='Wrapper';Subject=@{ModId='FixtureConsumer';Root=$manifestRoot};Assemblies=@($consumer);Dependencies=@($core);ReferenceAssemblyNames=@('FixtureCore');OutputRoot=(Join-Path $root 'reports');ProtectedRoots=@()}|ConvertTo-Json -Depth 10|Set-Content $wrapperProfile
$summary=& (Join-Path $PSScriptRoot '../Test-ModApi.ps1') -ProfilePath $wrapperProfile
$report=Get-Content -LiteralPath $summary.ReportPath -Raw|ConvertFrom-Json
Check ($report.Kind -eq 'StardeusApiAudit') 'wrapper kind'
Check ($report.State -eq 'CheckedSupportedScope') 'wrapper supported scope'
Check ($report.Inputs.Count -eq 3) 'wrapper binds manifest plus DLL inputs'
Check ($report.EngineSources.Count -ge 4) 'wrapper retains engine source pins'
$result=[pscustomobject]@{Assertions=$count;State='Passed';FixtureRoot=$root;ToolSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../MetadataDiagnostics/ApiAudit.cs')).Hash;CreatedUtc=[DateTime]::UtcNow.ToString('o');WrapperReportPath=$summary.ReportPath;TestSHA256=(Get-FileHash -LiteralPath $PSCommandPath).Hash;WrapperSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../Test-ModApi.ps1')).Hash;RunnerSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../MetadataRunner.ps1')).Hash;CommonSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../Common.ps1')).Hash;EngineSources=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../MetadataDiagnostics') -File | Where-Object Extension -in @('.cs','.csproj') | Sort-Object Name | ForEach-Object {[pscustomobject]@{Name=$_.Name;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}});Limitations='Synthetic metadata fixtures only; no game or mod code executed.'}
$result|ConvertTo-Json|Set-Content (Join-Path $root 'suite-result.json');$result
