param(
    [string]$Managed = 'D:\Program Files (x86)\Steam\steamapps\common\Stardeus\Stardeus_Data\Managed',
    [string]$Assembly = (Join-Path $PSScriptRoot '..\Libraries\MarketBalancing.dll'),
    [string]$Cecil = 'C:\Users\jleas\.dotnet\tools\.store\ilspycmd\11.0.0.9375\ilspycmd\11.0.0.9375\tools\net10.0\any\Mono.Cecil.dll'
)
$ErrorActionPreference = 'Stop'
# Metadata only. This does not load or execute any game or mod code.
Add-Type -Path $Cecil
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($Managed)
$resolver.AddSearchDirectory((Split-Path -Parent $Assembly))
$parameters = [Mono.Cecil.ReaderParameters]::new()
$parameters.AssemblyResolver = $resolver
$module = [Mono.Cecil.ModuleDefinition]::ReadModule($Assembly, $parameters)
$game = [Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $Managed 'Game.dll'), $parameters)
try {
    $referenceCount = 0
    foreach ($member in $module.GetMemberReferences()) {
        if ($member.DeclaringType.Scope.Name -notmatch '^(Game($|\.)|KL|0Harmony$)') { continue }
        if ($null -eq $member.Resolve()) { throw "Unresolved member: $($member.FullName)" }
        $referenceCount++
    }
    $economy = $game.GetType('Game.Systems.EconomySys')
    $targets = @()
    foreach ($type in $module.Types) {
        foreach ($attribute in $type.CustomAttributes) {
            if ($attribute.AttributeType.FullName -ne 'HarmonyLib.HarmonyPatch') { continue }
            $argsList = $attribute.ConstructorArguments
            if ($argsList.Count -ne 3 -or $argsList[0].Value.FullName -ne $economy.FullName -or $argsList[2].Value.Count -ne 0) {
                throw "Unexpected patch signature: $($type.FullName)"
            }
            $targetName = [string]$argsList[1].Value
            $methods = @($economy.Methods | Where-Object { $_.Name -eq $targetName -and $_.Parameters.Count -eq 0 })
            if ($methods.Count -ne 1 -or $methods[0].IsStatic -or $methods[0].ReturnType.FullName -ne 'System.Void') {
                throw "Target no longer matches: $targetName"
            }
            $hooks = @($type.Methods | Where-Object { $_.CustomAttributes.AttributeType.FullName -contains 'HarmonyLib.HarmonyPrefix' })
            if ($hooks.Count -ne 1 -or -not $hooks[0].IsStatic -or $hooks[0].Parameters.Count -ne 0 -or $hooks[0].ReturnType.FullName -ne 'System.Void') {
                throw "Unexpected prefix binding: $($type.FullName)"
            }
            $targets += $targetName
        }
    }
    if (($targets | Sort-Object) -join ',' -ne 'ApplyMarketItems,InitializeMarket') { throw 'Expected two fixed patch targets' }
    $initialize = $economy.Methods | Where-Object { $_.Name -eq 'InitializeMarket' -and $_.Parameters.Count -eq 0 }
    $calls = @($initialize.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
    $validationIndex = [Array]::IndexOf($calls, 'ValidateMarketItems')
    $applyIndex = [Array]::IndexOf($calls, 'ApplyMarketItems')
    $wealthIndex = [Array]::IndexOf($calls, 'UpdateNetWorth')
    if ($validationIndex -lt 0 -or $applyIndex -le $validationIndex -or $wealthIndex -le $applyIndex) {
        throw 'Core no longer validates, applies prices, and calculates net worth in the reviewed order'
    }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $Assembly).Path)
    if ($module.Assembly.Name.Version.ToString() -ne '0.1.0.0' -or $fileVersion.ProductVersion -ne '0.1.0') { throw 'Version mismatch' }
    $report = [ordered]@{
        Scope = 'Metadata references, fixed Harmony signatures and Core caller order; not patch installation or gameplay.'
        AssemblyVersion = $module.Assembly.Name.Version.ToString()
        FileVersion = $fileVersion.FileVersion
        ProductVersion = $fileVersion.ProductVersion
        ModuleId = $module.Mvid.ToString()
        ModSha256 = (Get-FileHash -LiteralPath $Assembly).Hash
        GameModuleId = $game.Mvid.ToString()
        GameSha256 = (Get-FileHash -LiteralPath (Join-Path $Managed 'Game.dll')).Hash
        ResolvedGameHarmonyReferences = $referenceCount
        PatchTargets = $targets
        CallerOrder = 'ValidateMarketItems -> ApplyMarketItems -> UpdateNetWorth'
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'BinaryAudit.json') -Encoding utf8
    "BINARY_REFERENCES_PASS=$referenceCount; FIXED_PATCH_TARGETS_PASS=$($targets.Count); CORE_CALL_ORDER_PASS=True"
} finally {
    $module.Dispose()
    $game.Dispose()
    $resolver.Dispose()
}
