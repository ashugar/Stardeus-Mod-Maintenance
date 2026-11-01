# Shared launcher for metadata-only diagnostic commands. No game assembly is loaded.
. (Join-Path $PSScriptRoot 'Common.ps1')
$script:MetadataRunnerSourcePath=$PSCommandPath

# Diagnostic children are our compiler/metadata reader, never a game process.
if(-not ('StardeusDiagnosticProcessV1' -as [type])){
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
public static class StardeusDiagnosticProcessV1 {
 static async Task<byte[]> ReadLimited(Stream stream,int limit) {
  using var output=new MemoryStream();var buffer=new byte[65536];int n;
  while((n=await stream.ReadAsync(buffer,0,buffer.Length).ConfigureAwait(false))!=0) {
   if(output.Length+n>limit)throw new InvalidDataException("Diagnostic child output limit exceeded.");
   output.Write(buffer,0,n);
  }return output.ToArray();
 }
 public static byte[] Run(string program,string[] arguments,string working,int limit,int milliseconds) {
  var start=new ProcessStartInfo(program){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=working};
  foreach(var argument in arguments)start.ArgumentList.Add(argument);
  using var child=Process.Start(start)??throw new InvalidOperationException("Diagnostic child failed to start.");
  var stdout=ReadLimited(child.StandardOutput.BaseStream,limit);var stderr=ReadLimited(child.StandardError.BaseStream,2097152);var timer=Stopwatch.StartNew();
  try {
   while(!child.HasExited||!stdout.IsCompleted||!stderr.IsCompleted) {
    if(stdout.IsFaulted||stderr.IsFaulted)throw new InvalidDataException("Diagnostic child output limit exceeded.");
    if(timer.ElapsedMilliseconds>milliseconds)throw new TimeoutException("Diagnostic child time limit exceeded.");
    Thread.Sleep(10);
   }
   var bytes=stdout.GetAwaiter().GetResult();var errors=stderr.GetAwaiter().GetResult();
   if(child.ExitCode!=0)throw new InvalidOperationException("Diagnostic child failed; exit "+child.ExitCode+".");
   return bytes;
  }catch {try{if(!child.HasExited){child.Kill(true);child.WaitForExit(1000);}}catch{}throw;}
 }
}
'@
}
function Invoke-DiagnosticChild {
 param([string]$Program,[string[]]$Arguments,[string]$WorkingDirectory,[int]$MaxBytes=33554432,[int]$TimeoutMilliseconds=180000)
 return ,([StardeusDiagnosticProcessV1]::Run($Program,$Arguments,$WorkingDirectory,$MaxBytes,$TimeoutMilliseconds))
}

function Invoke-MetadataDiagnostic {
    [CmdletBinding()]
    param([ValidateSet('api','compare')][string]$Mode,[string]$ProfilePath,[switch]$ReportOnly)
    $ErrorActionPreference='Stop'
    $repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    function Full([string]$p) {
        if(!$p -or $p -notmatch '^[A-Za-z]:[\\/]' -or ![IO.Path]::IsPathFullyQualified($p)){throw 'Absolute local diagnostic paths required.'}
        foreach($part in $p.Substring(3).Replace('/','\').TrimEnd('\') -split '\\'){
            if(!$part -or $part -in @('.','..') -or $part -match '[. ]$|[:*?"<>|\x00-\x1f\x7f]' -or $part -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)'){throw 'Unsafe diagnostic path spelling.'}
        }
        $full=[IO.Path]::GetFullPath($p)
        if($full -eq [IO.Path]::GetPathRoot($full)){throw 'Drive/root paths refused.'}
        Assert-ModNoReparsePath $full
        return $full
    }
    $profilePath=Full $ProfilePath;Assert-ModExternalPath $repo $profilePath
    if((Get-Item -LiteralPath $profilePath).Length -gt 4194304){throw 'Profile exceeds 4 MiB.'}
    $profileBytes=[IO.File]::ReadAllBytes($profilePath)
    if($profileBytes.Length -gt 4194304){throw 'Profile exceeds 4 MiB.'}
    $p=ConvertFrom-ModStrictJson $profileBytes
    $idField=if($Mode -eq 'api'){'CheckId'}else{'ComparisonId'}
    if($p.SchemaVersion -ne 1 -or $p[$idField] -notmatch '^[A-Za-z0-9_.-]{1,100}$'){throw 'Schema 1 and simple context ID required.'}
    foreach($key in @('ProtectedRoots')){if($p[$key] -isnot [array]){throw "$key must be an array."}}
    $keys=if($Mode -eq 'api'){@('SchemaVersion','CheckId','Subject','Assemblies','Dependencies','ReferenceAssemblyNames','OutputRoot','ProtectedRoots')}else{@('SchemaVersion','ComparisonId','Subject','Before','After','Selections','OutputRoot','ProtectedRoots')}
    Assert-ModObjectKeys $p $keys
    Assert-ModObjectKeys $p.Subject @('ModId','Root')
    $wrapper=Join-Path $PSScriptRoot $(if($Mode -eq 'api'){'Test-ModApi.ps1'}else{'Compare-ModContracts.ps1'})
    $supportRows=@(foreach($file in @($wrapper,$script:MetadataRunnerSourcePath,(Join-Path $PSScriptRoot 'Common.ps1'))){Assert-ModNoReparsePath $file;[pscustomobject]@{Path=$file;SHA256=(Get-FileHash -LiteralPath $file).Hash}})
    $root=Full $p.Subject.Root
    if($p.Subject.ModId -isnot [string] -or [string]::IsNullOrWhiteSpace($p.Subject.ModId)){throw 'Subject ModId required.'}
    $manifest=Full (Join-Path $root 'ModInfo.json')
    if((Get-Item -LiteralPath $manifest).Length -gt 4194304){throw 'Subject manifest exceeds 4 MiB.'}
    $manifestBytes=[IO.File]::ReadAllBytes($manifest)
    if($manifestBytes.Length -gt 4194304){throw 'Subject manifest exceeds 4 MiB.'}
    $identity=ConvertFrom-ModStrictJson $manifestBytes
    if($identity.Id -cne $p.Subject.ModId){throw 'Subject manifest identity mismatch.'}
    $inputPaths=@($manifest)
    if($Mode -eq 'api'){
        foreach($key in @('Assemblies','Dependencies','ReferenceAssemblyNames')){if($p[$key] -isnot [array]){throw "$key must be an array."}}
        if(!$p.Assemblies.Count -or !$p.ReferenceAssemblyNames.Count){throw 'At least one assembly and explicit reference scope required.'}
        $inputPaths+=@($p.Assemblies)+@($p.Dependencies)
    }else{
        if($p.Selections -isnot [array] -or !$p.Selections.Count -or $p.Selections.Count -gt 1000){throw '1 to 1000 exact selections required.'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($s in $p.Selections){
            if($s -isnot [Collections.IDictionary]){throw 'Selection object required.'}
            $allowed=@('Key','Kind','Type','Name','ParameterTypes')
            foreach($key in $s.Keys){if($key -cnotin $allowed){throw 'Unknown selection field.'}}
            foreach($key in @('Key','Kind','Type')){if(!$s.Contains($key) -or $s[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($s[$key])){throw 'Selection Key/Kind/Type required.'}}
            if($s.Key -notmatch '^[A-Za-z0-9_.-]{1,100}$' -or !$seen.Add($s.Key) -or $s.Kind -cnotin @('Method','Field','Type')){throw 'Selection key/kind invalid.'}
            if($s.Kind -ne 'Type' -and (!$s.Contains('Name') -or [string]::IsNullOrWhiteSpace($s.Name))){throw 'Member Name required.'}
            if($s.Contains('ParameterTypes') -and ($s.Kind -ne 'Method' -or $s.ParameterTypes -isnot [array])){throw 'Method ParameterTypes must be an array.'}
        }
        $inputPaths+=@($p.Before,$p.After)
    }
    if($inputPaths.Count -gt 258){throw 'Input count limit exceeded.'}
    $inputPaths=@($inputPaths|ForEach-Object {Full $_}|Select-Object -Unique)
    $out=Full $p.OutputRoot;Assert-ModExternalPath $repo $out
    foreach($input in @($inputPaths)+@($root,$profilePath)+@($p.ProtectedRoots)){
        $protected=Full $input
        if(Test-ModPathOverlap $out $protected){throw 'Output overlaps subject/input/protected scope.'}
    }
    $engineRoot=Join-Path $PSScriptRoot 'MetadataDiagnostics'
    $sources=@(Get-ChildItem -LiteralPath $engineRoot -File | Where-Object Extension -in @('.cs','.csproj') | Sort-Object Name)
    if(!$sources.Count){throw 'Metadata diagnostic sources missing.'}
    $sourceRows=@(foreach($file in $sources){Assert-ModNoReparsePath $file.FullName;[pscustomobject]@{Path=$file.FullName;SHA256=(Get-FileHash -LiteralPath $file.FullName).Hash}})
    $totalInputBytes=0L
    $inputRows=@(foreach($file in $inputPaths){$i=Get-Item -LiteralPath $file;if($i.PSIsContainer -or $i.Length -gt 134217728){throw 'Input must be a bounded file (128 MiB maximum).'};$totalInputBytes+=$i.Length;if($totalInputBytes -gt 536870912){throw 'Combined inputs exceed 512 MiB.'};[pscustomobject]@{Path=$file;Status='Present';SHA256=(Get-FileHash -LiteralPath $file).Hash}})
    $run=Join-Path $out ($Mode+'-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[guid]::NewGuid().ToString('N'))
    Assert-ModNoReparsePath $run;$null=[IO.Directory]::CreateDirectory($run)
    [IO.File]::WriteAllBytes((Join-Path $run 'profile.json'),$profileBytes)
    $build=Join-Path $run 'engine-build';$null=[IO.Directory]::CreateDirectory($build)
    $config=Join-Path $build 'NuGet.Config';[IO.File]::WriteAllText($config,'<configuration><packageSources><clear /></packageSources></configuration>')
    $args=@('build',(Join-Path $engineRoot 'MetadataDiagnostics.csproj'),'-c','Release','--artifacts-path',$build,'--output',(Join-Path $build 'run'),('-p:BaseOutputPath='+$build+'/bin/'),('-p:BaseIntermediateOutputPath='+$build+'/obj/'),'-p:ImportDirectoryBuildProps=false','-p:ImportDirectoryBuildTargets=false','-p:ImportDirectoryPackagesProps=false','-p:NuGetAudit=false',('-p:RestoreConfigFile='+$config),'-noAutoResponse')
    $buildOutput=Invoke-DiagnosticChild -Program dotnet -Arguments $args -WorkingDirectory $build
    [IO.File]::WriteAllBytes((Join-Path $run 'build.log'),$buildOutput)
    $engine=Join-Path $build 'run/MetadataDiagnostics.dll'
    $bytes=Invoke-DiagnosticChild -Program dotnet -Arguments @($engine,$Mode,$profilePath) -WorkingDirectory $run
    $data=ConvertFrom-ModStrictJson $bytes
    $sdk=[Text.Encoding]::UTF8.GetString((Invoke-DiagnosticChild -Program dotnet -Arguments @('--version') -WorkingDirectory $build)).Trim()
    if($data.State -notin @('CheckedSupportedScope','CheckedWithGaps','Failed','UnchangedInSelectedScope','ChangesFound','ComparedWithGaps')){throw 'Unknown engine result state.'}
    foreach($row in @($sourceRows)+@($supportRows)){if((Get-FileHash -LiteralPath $row.Path).Hash -cne $row.SHA256){throw 'Engine source changed during diagnostic run.'}}
    foreach($row in $inputRows){if((Get-FileHash -LiteralPath $row.Path).Hash -cne $row.SHA256){throw 'Input changed during diagnostic run.'}}
    if((Get-ModSha256 ([IO.File]::ReadAllBytes($profilePath))) -cne (Get-ModSha256 $profileBytes)){throw 'Profile changed during diagnostic run.'}
    $engineInputs=@($data.Inputs)
    $seenInputs=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($engineInput in $engineInputs){
        if($engineInput.Status -cne 'Present' -or !$seenInputs.Add($engineInput.Path)){throw 'Invalid/duplicate engine input.'}
        $matched=@($inputRows | Where-Object {$_.Path -eq $engineInput.Path -and $_.SHA256 -ceq $engineInput.SHA256})
        if(!$matched.Count){throw 'Engine consumed undeclared or changed input.'}
        foreach($name in @('MVID','AssemblyIdentity')){if($engineInput.Contains($name)){$matched[0] | Add-Member -NotePropertyName $name -NotePropertyValue $engineInput[$name] -Force}}
    }
    if($data.State -ne 'Failed'){
        foreach($input in $inputPaths|Where-Object {$_ -ine $manifest}){if(!$seenInputs.Contains($input)){throw 'Engine omitted declared input.'}}
    }
    $report=[ordered]@{SchemaVersion=1;Kind=$(if($Mode -eq 'api'){'StardeusApiAudit'}else{'StardeusContractComparison'});ToolVersion='1.0.0';CreatedUtc=[DateTime]::UtcNow.ToString('o');Subject=$p.Subject;Configuration=$p;ProfileSHA256=([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($profileBytes)));ToolSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $(if($Mode -eq 'api'){'Test-ModApi.ps1'}else{'Compare-ModContracts.ps1'}))).Hash;RunnerSHA256=$supportRows[1].SHA256;SupportSHA256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Common.ps1')).Hash;SupportInputs=$supportRows;SdkVersion=$sdk;EngineSources=$sourceRows;EngineSHA256=(Get-FileHash -LiteralPath $engine).Hash;Inputs=$inputRows;Limitations=@('Metadata-only selected scope; no code execution, Harmony installation, gameplay, save-safety or publication validation.','Unchanged contracts are conservative structural fingerprints, not semantic-equivalence proof.','Coverage gaps never authorize automatically refreshing runtime guard approvals.')}
    $report[$idField]=$p[$idField]
    foreach($key in @('State','References','Patches','Coverage','Gaps','Findings','Selections')){if($data.Contains($key)){$report[$key]=$data[$key]}}
    if(!$report.Contains('Findings')){$report.Findings=@()}
    $name=if($Mode -eq 'api'){'api-audit.json'}else{'contracts.json'}
    $reportPath=Join-Path $run $name
    $report | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $reportPath -Encoding utf8
    $summary=[pscustomobject]@{State=$data.State;RunPath=$run;ReportPath=$reportPath;InputCount=$inputRows.Count;FindingCount=@($data.Findings).Count;GapCount=@($data.Gaps).Count}
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
    $summary
    if($data.State -eq 'Failed' -and !$ReportOnly){throw 'API diagnostic found failures; inspect the retained report.'}
}
