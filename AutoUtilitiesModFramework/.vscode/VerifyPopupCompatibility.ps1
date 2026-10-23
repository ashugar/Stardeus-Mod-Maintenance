# Metadata-only regression check: never loads or invokes the game or mod.
# Run against the rebuilt DLL and the exact installed managed dependencies.
param(
    [Parameter(Mandatory)][string]$CandidateDll,
    [string]$ManagedPath,
    [string]$CecilDll
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../build/LocalPaths.ps1')
$ManagedPath = Resolve-StardeusManagedPath -ManagedPath $ManagedPath
$CecilDll = Resolve-StardeusCecilPath -CecilPath $CecilDll
Add-Type -LiteralPath $CecilDll

$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($ManagedPath)
$parameters = [Mono.Cecil.ReaderParameters]::new()
$parameters.AssemblyResolver = $resolver
$candidate = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($CandidateDll, $parameters)
try {
    $signature = 'System.Void Game.UI.UIUtils::AfterShowPopup(UnityEngine.RectTransform,System.Boolean,System.Single,System.Boolean,System.Boolean,System.Boolean)'
    $references = @($candidate.MainModule.GetMemberReferences() | Where-Object {
        $_.DeclaringType.FullName -ceq 'Game.UI.UIUtils' -and $_.Name -ceq 'AfterShowPopup'
    })
    if ($references.Count -ne 1 -or $references[0].FullName -cne $signature) {
        throw 'Expected exactly the current six-parameter Core popup reference; rebuild against installed Core.'
    }
    $resolved = $references[0].Resolve()
    if (-not $resolved -or $resolved.FullName -cne $signature -or
        -not $resolved.IsPublic -or -not $resolved.IsStatic -or
        [IO.Path]::GetFullPath($resolved.Module.FileName) -ine
            [IO.Path]::GetFullPath((Join-Path $ManagedPath 'Game.dll'))) {
        throw 'Popup reference does not resolve to the exact installed Core contract.'
    }

    $panel = $candidate.MainModule.GetType('AutoUtilitiesModFramework.FrameworkPanel')
    $show = @($panel.Methods | Where-Object { $_.Name -ceq 'Show' -and $_.Parameters.Count -eq 0 })
    if ($show.Count -ne 1 -or -not $show[0].HasBody) { throw 'Expected FrameworkPanel.Show body.' }
    $calls = @($show[0].Body.Instructions | Where-Object {
        $_.OpCode.Name -ceq 'call' -and $_.Operand.FullName -ceq $signature
    })
    if ($calls.Count -ne 1) { throw 'Expected exactly one native popup call in FrameworkPanel.Show.' }
    # Reverse stack pushes: keepOffMouse=false, compensate=false, random=true,
    # distance=1f, center=true. The rectangle argument is supplied before these.
    $expected = @('ldc.i4.0', 'ldc.i4.0', 'ldc.i4.1', 'ldc.r4', 'ldc.i4.1')
    $instruction = $calls[0].Previous
    for ($i = 0; $i -lt $expected.Count; $i++) {
        if (-not $instruction -or $instruction.OpCode.Name -cne $expected[$i] -or
            ($i -eq 3 -and $instruction.Operand -ne [single]1)) {
            throw 'Popup placement flags changed: preserve centered/randomized placement and allow cursor overlap.'
        }
        $instruction = $instruction.Previous
    }

    [pscustomobject]@{
        Result = 'PASS'
        PopupSignature = $resolved.FullName
        KeepOffMouse = $false
        CandidateSHA256 = (Get-FileHash -LiteralPath $CandidateDll).Hash
        CoreSHA256 = (Get-FileHash -LiteralPath (Join-Path $ManagedPath 'Game.dll')).Hash
        RuntimeValidation = 'Not performed by this metadata check'
    } | ConvertTo-Json
} finally {
    $candidate.Dispose()
    $resolver.Dispose()
}
