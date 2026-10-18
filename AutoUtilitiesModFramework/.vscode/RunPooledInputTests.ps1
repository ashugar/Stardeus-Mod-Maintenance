param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Compiler = 'C:/Program Files/dotnet/sdk/10.0.401/Roslyn/bincore/csc.dll',
    [string]$ReferenceDirectory = 'C:/Users/jleas/.nuget/packages/microsoft.netframework.referenceassemblies.net481/1.0.3/build/.NETFramework/v4.8.1'
)
$ErrorActionPreference = 'Stop'
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
