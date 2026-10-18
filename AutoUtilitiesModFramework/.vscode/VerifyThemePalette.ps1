param(
    [Parameter(Mandatory)][string]$BaselineDll,
    [Parameter(Mandatory)][string]$CandidateDll,
    [Parameter(Mandatory)][string]$CecilDll,
    [switch]$PaletteOnly,
    [string]$CoreRoot = 'D:\Program Files (x86)\Steam\steamapps\common\Stardeus\Stardeus_Data\StreamingAssets\Mods\Core'
)
$ErrorActionPreference = 'Stop'
Add-Type -LiteralPath $CecilDll
$baseline = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($BaselineDll)
$candidate = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($CandidateDll)
try {
    $palette = $candidate.MainModule.Types | Where-Object Name -eq FrameworkThemePalette
    $swatch = $candidate.MainModule.Types | Where-Object Name -eq FrameworkPaletteSwatch
    if (-not $palette -or -not $swatch -or $palette.IsPublic -or $swatch.IsPublic) {
        throw 'The palette must exist as internal demonstration types only.'
    }
    $initializer = $palette.Methods | Where-Object Name -eq '.cctor'
    $strings = @()
    $ids = @()
    $fixed = @()
    $redPanelThemes = @()
    foreach ($instruction in $initializer.Body.Instructions) {
        if ($instruction.OpCode.Name -eq 'ldstr') { $strings += [string]$instruction.Operand }
        if ($instruction.OpCode.Name -eq 'stsfld') {
            switch ($instruction.Operand.Name) {
                CoreColorIds { $ids = $strings; $strings = @() }
                RedPanelThemeIds { $redPanelThemes = $strings; $strings = @() }
                FixedColors { $fixed = $strings; $strings = @() }
            }
        }
    }
    $themes = Join-Path $CoreRoot 'Config\UITheme\Themes'
    $default = Get-Content -Raw -LiteralPath (Join-Path $themes 'Default.json') | ConvertFrom-Json
    $expected = @($default.Colors.PSObject.Properties.Name) + 'RedPanel'
    if ($ids.Count -ne ($ids | Sort-Object -Unique).Count -or
        @(Compare-Object $expected $ids -CaseSensitive).Count) {
        throw 'Compiled palette differs from the complete installed Core fallback color catalog.'
    }
    foreach ($file in Get-ChildItem -LiteralPath $themes -Filter '*.json' -File) {
        $theme = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
        $missing = @($theme.Colors.PSObject.Properties.Name | Where-Object { $_ -cnotin $ids })
        if ($missing.Count) { throw "Colors omitted from $($file.Name): $($missing -join ', ')" }
        $hasRedPanel = 'RedPanel' -cin $theme.Colors.PSObject.Properties.Name
        if ($hasRedPanel -ne ($file.BaseName -cin $redPanelThemes)) {
            throw "Theme-specific RedPanel availability changed in $($file.Name)."
        }
    }
    if ($fixed.Count -ne 32) { throw 'Expected 16 labeled fixed comparison swatches.' }
    $fixedMap = @{}
    for ($i = 0; $i -lt $fixed.Count; $i += 2) {
        if ($fixed[$i + 1] -cnotmatch '^#[0-9A-F]{6}$') { throw 'Invalid fixed color literal.' }
        $fixedMap[$fixed[$i]] = $fixed[$i + 1]
    }
    if ($fixedMap.Pink -cne '#FFC0CB' -or $fixedMap.Cyan -cne '#00FFFF') {
        throw 'Requested pink/cyan candidates are absent or changed.'
    }
    if ($swatch.BaseType.FullName -ne 'Game.Systems.UIUX.UIThemedImage' -or
        @($swatch.Methods | Where-Object Name -in 'Update','LateUpdate','FixedUpdate').Count) {
        throw 'Swatches must use native live theme reload without frame polling.'
    }
    # Default: retain the original palette-only implementation scope check.
    # Later framework revisions may instead verify that both palette types
    # themselves are unchanged from a baseline which already contains them.
    $preservedMethods = 0
    if ($PaletteOnly -and @($baseline.MainModule.Types | Where-Object {
        $_.Name -in 'FrameworkThemePalette','FrameworkPaletteSwatch'
    }).Count -ne 2) { throw 'PaletteOnly requires a baseline containing both palette types.' }
    foreach ($oldType in $baseline.MainModule.Types) {
        if ($oldType.Name -eq 'FrameworkUiTestPanel') { continue }
        if ($PaletteOnly -and $oldType.Name -notin 'FrameworkThemePalette','FrameworkPaletteSwatch') { continue }
        $newType = $candidate.MainModule.Types | Where-Object FullName -eq $oldType.FullName
        foreach ($oldMethod in $oldType.Methods | Where-Object HasBody) {
            $newMethod = $newType.Methods | Where-Object FullName -eq $oldMethod.FullName
            $oldIL = ($oldMethod.Body.Instructions | ForEach-Object ToString) -join "`n"
            $newIL = ($newMethod.Body.Instructions | ForEach-Object ToString) -join "`n"
            if (-not $newMethod -or $oldIL -cne $newIL) {
                throw "Scope-protected method changed: $($oldMethod.FullName)"
            }
            $preservedMethods++
        }
    }
    [pscustomobject]@{
        Result = 'PASS'; CoreColors = $ids.Count; FixedCandidates = $fixedMap.Count
        CoreThemes = @(Get-ChildItem -LiteralPath $themes -Filter '*.json' -File).Count
        ScopeMethodBodiesPreserved = $preservedMethods
        ThemeReload = 'Native UIThemedImage; no frame polling'
        ScopeCheck = $(if ($PaletteOnly) { 'Palette implementation unchanged' } else { 'Non-demo implementation unchanged' })
    } | ConvertTo-Json
} finally {
    $baseline.Dispose()
    $candidate.Dispose()
}
