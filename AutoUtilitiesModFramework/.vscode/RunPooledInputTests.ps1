param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Compiler,
    [string]$ReferenceDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../build/LocalPaths.ps1')
if (-not $Compiler) { $Compiler = Get-StardeusSdkCompiler }
if (-not $ReferenceDirectory) { $ReferenceDirectory = Resolve-StardeusManagedPath }
if (-not (Test-Path -LiteralPath (Join-Path $ReferenceDirectory 'mscorlib.dll') -PathType Leaf)) { throw 'ReferenceDirectory must contain the requested framework reference assemblies.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$assembly = Join-Path $OutputDirectory 'PooledInputCommitBindingTests.exe'
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll') | ForEach-Object {
    '/reference:' + (Join-Path $ReferenceDirectory $_)
}
$binding = Join-Path (Split-Path -Parent $PSScriptRoot) 'Code/UI/Input/PooledInputCommitBinding.cs'
$tests = Join-Path (Split-Path -Parent $PSScriptRoot) 'Tests/PooledInputCommitBindingTests.cs'
& dotnet $Compiler /nologo /noconfig /nostdlib+ /langversion:latest /target:exe /optimize+ "/out:$assembly" @references $binding $tests
if ($LASTEXITCODE -ne 0) { throw 'Isolated input test compilation failed.' }
& $assembly
if ($LASTEXITCODE -ne 0) { throw 'Isolated input lifecycle tests failed.' }
