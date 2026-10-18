param(
    [string]$ManagedPath = $env:STARDEUS_MANAGED_PATH
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/LocalPaths.ps1')
$ManagedPath = Resolve-StardeusManagedPath -ManagedPath $ManagedPath
if (@(Get-Process -Name 'Stardeus' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close Stardeus before replacing the mod DLL. This helper never stops the game.'
}
$projectRoot = $PSScriptRoot
$sdkLines = @(& dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or $sdkLines.Count -eq 0) { throw 'An installed .NET SDK is required.' }
$sdkLine = $sdkLines[-1]
if ($sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'Cannot identify the installed SDK compiler.' }
$compilerPath = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
$references = @('Game','Game.CodeGen','Game.Constants','Game.Utils','KLUnityUtils','UnityEngine.CoreModule','UnityEngine.UI','UnityEngine.UIModule','mscorlib','netstandard','System','System.Core')
$arguments = @($compilerPath,'/nologo','/noconfig','/nostdlib+','/target:library','/platform:x64','/langversion:9.0','/optimize+','/deterministic+','/debug:embedded','/utf8output')
$arguments += '/pathmap:' + $projectRoot + '=/_/OrbotronUpgrade'
New-Item -ItemType Directory -Path (Join-Path $projectRoot 'Libraries') -Force | Out-Null
$arguments += '/out:' + (Join-Path $projectRoot 'Libraries\OrbotronUpgrade.dll')
foreach ($reference in $references) {
    $path = Join-Path $ManagedPath ($reference + '.dll')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing installed reference: $path" }
    $arguments += '/reference:' + $path
}
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Code') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
# Uses the installed SDK compiler with the same explicit non-copying references as mod.csproj.
# This route needs no NuGet restore/cache and emits only the intended mod DLL.
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Compiler failed with exit code $LASTEXITCODE" }
Get-FileHash -LiteralPath (Join-Path $projectRoot 'Libraries\OrbotronUpgrade.dll') -Algorithm SHA256
