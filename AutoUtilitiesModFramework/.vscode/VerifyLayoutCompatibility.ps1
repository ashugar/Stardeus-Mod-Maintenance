# Metadata-only regression check. Never loads or invokes game/mod code.
# Supply the candidate DLL and the exact Unity 6.6 managed dependencies.
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
    $signature = 'System.Void UnityEngine.UI.LayoutGroup::SetLayoutInputForAxis(System.Single,System.Single,System.Single,System.Single,System.Int32)'
    $references = @($candidate.MainModule.GetMemberReferences() | Where-Object {
        $_.DeclaringType.FullName -ceq 'UnityEngine.UI.LayoutGroup' -and
        $_.Name -ceq 'SetLayoutInputForAxis'
    })
    if ($references.Count -ne 1 -or $references[0].FullName -cne $signature) {
        throw 'Expected only the five-parameter Unity layout reference; the obsolete four-parameter call is incompatible.'
    }
    $resolved = $references[0].Resolve()
    if (-not $resolved -or $resolved.FullName -cne $signature -or
        -not $resolved.IsFamily -or $resolved.IsStatic -or
        [IO.Path]::GetFullPath($resolved.Module.FileName) -ine
            [IO.Path]::GetFullPath((Join-Path $ManagedPath 'UnityEngine.UI.dll'))) {
        throw 'Layout reference does not resolve to the exact installed Unity UI contract.'
    }
    $names = @('totalMin', 'totalMax', 'totalPreferred', 'totalFlexible', 'axis')
    for ($i = 0; $i -lt $names.Count; $i++) {
        if ($resolved.Parameters[$i].Name -cne $names[$i] -or
            $resolved.Parameters[$i].IsOptional) {
            throw 'Unity layout parameter contract changed; review the installed implementation.'
        }
    }

    $flow = $candidate.MainModule.GetType('AutoUtilitiesModFramework.ResponsiveFlowLayout')
    if (-not $flow -or $flow.BaseType.FullName -cne 'UnityEngine.UI.LayoutGroup') {
        throw 'Expected the native LayoutGroup extension.'
    }
    foreach ($axis in 0, 1) {
        $name = @('CalculateLayoutInputHorizontal', 'CalculateLayoutInputVertical')[$axis]
        $methods = @($flow.Methods | Where-Object { $_.Name -ceq $name -and $_.Parameters.Count -eq 0 })
        if ($methods.Count -ne 1 -or -not $methods[0].HasBody) { throw "Expected $name body." }
        $calls = @($methods[0].Body.Instructions | Where-Object {
            $_.OpCode.Name -ceq 'call' -and $_.Operand.FullName -ceq $signature
        })
        if ($calls.Count -ne 1) { throw "Expected exactly one native layout-input call in $name." }
        $axisPush = $calls[0].Previous
        $flexPush = $axisPush.Previous
        $preferredPush = $flexPush.Previous
        $maxPush = $preferredPush.Previous
        if ($axisPush.OpCode.Name -cne "ldc.i4.$axis" -or
            $flexPush.OpCode.Name -cne 'ldc.r4' -or
            $flexPush.Operand -ne @([single]-1, [single]0)[$axis] -or
            $maxPush.OpCode.Name -cne 'ldc.r4' -or
            -not [single]::IsPositiveInfinity([single]$maxPush.Operand)) {
            throw "Expected unbounded maximum, unchanged flexibility and axis in $name."
        }
        if ($axis -eq 0) {
            if ($preferredPush.OpCode.Name -cne 'ldc.r4' -or $preferredPush.Operand -ne [single]-1) {
                throw 'Horizontal preferred size changed.'
            }
        } elseif ($preferredPush.OpCode.Name -notlike 'ldloc*' -or
            $maxPush.Previous.OpCode.Name -cne $preferredPush.OpCode.Name -or
            [string]$maxPush.Previous.Operand -cne [string]$preferredPush.Operand) {
            throw 'Vertical minimum and preferred size must use the same computed height.'
        }
    }
    [pscustomobject]@{
        Result = 'PASS'
        LayoutSignature = $resolved.FullName
        MaximumSize = 'PositiveInfinity on both axes'
        CandidateSHA256 = (Get-FileHash -LiteralPath $CandidateDll).Hash
        UnityUiSHA256 = (Get-FileHash -LiteralPath (Join-Path $ManagedPath 'UnityEngine.UI.dll')).Hash
        RuntimeValidation = 'Not performed by this metadata check'
    } | ConvertTo-Json
} finally {
    $candidate.Dispose()
    $resolver.Dispose()
}
