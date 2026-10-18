param(
    [Parameter(Mandatory)][string]$BaselineDll,
    [Parameter(Mandatory)][string]$CandidateDll,
    [Parameter(Mandatory)][string]$CecilDll,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedVersion = '1.4.0',
    [string]$ModsRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$CoreRoot = 'D:\Program Files (x86)\Steam\steamapps\common\Stardeus\Stardeus_Data\StreamingAssets\Mods\Core'
)
$ErrorActionPreference = 'Stop'
Add-Type -LiteralPath $CecilDll

function All-Types($Module) {
    function Expand-Type($Type) {
        $Type
        foreach ($child in $Type.NestedTypes) { Expand-Type $child }
    }
    foreach ($type in $Module.Types) { Expand-Type $type }
}

function Public-Surface($Module) {
    foreach ($type in (All-Types $Module)) {
        if (-not ($type.IsPublic -or $type.IsNestedPublic -or $type.IsNestedFamily)) { continue }
        "TYPE $($type.FullName) $($type.Attributes) $($type.BaseType.FullName)"
        foreach ($method in $type.Methods) {
            if ($method.IsPublic -or $method.IsFamily -or $method.IsFamilyOrAssembly) {
                "METHOD $($method.FullName) $($method.Attributes)"
                foreach ($parameter in $method.Parameters) {
                    "PARAM $($method.FullName) $($parameter.Index) $($parameter.Attributes) $($parameter.Constant)"
                }
            }
        }
        foreach ($field in $type.Fields) {
            if ($field.IsPublic -or $field.IsFamily -or $field.IsFamilyOrAssembly) {
                # A release may change this one version constant, not API shape
                # or other public constant values. Its actual value is checked below.
                $value = if ($field.FullName -eq 'System.String AutoUtilitiesModFramework.AutoUtilitiesApi::FrameworkVersion') {
                    '<framework-version>'
                } else { $field.Constant }
                "FIELD $($field.FullName) $($field.Attributes) $value"
            }
        }
    }
}

function Method-IL($Method) {
    foreach ($instruction in $Method.Body.Instructions) {
        "$($instruction.OpCode) $($instruction.Operand)"
    }
}

$baseline = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($BaselineDll)
$candidate = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($CandidateDll)
try {
    $before = @(Public-Surface $baseline.MainModule)
    $after = @(Public-Surface $candidate.MainModule)
    $removed = @($before | Where-Object { $_ -cnotin $after })
    if ($removed.Count) { throw "Existing public API changed:`n$($removed -join "`n")" }
    $assemblyVersion = $ExpectedVersion + '.0'
    if ($candidate.Name.Version.ToString() -ne $assemblyVersion) { throw 'Unexpected assembly version.' }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($CandidateDll)
    if ($version.FileVersion -ne $assemblyVersion -or $version.ProductVersion -ne $ExpectedVersion) {
        throw 'Unexpected file/product version.'
    }
    $apiType = $candidate.MainModule.Types | Where-Object Name -eq AutoUtilitiesApi
    $apiVersion = ($apiType.Fields | Where-Object Name -eq FrameworkVersion).Constant
    if ($apiVersion -cne $ExpectedVersion) { throw 'Compiled API version does not match the expected release.' }

    $oldFactory = $baseline.MainModule.Types | Where-Object Name -eq UIComponentFactory
    $newFactory = $candidate.MainModule.Types | Where-Object Name -eq UIComponentFactory
    $oldTab = $oldFactory.Methods | Where-Object Name -eq CreateTabButton
    $newTab = $newFactory.Methods | Where-Object Name -eq CreateTabButton
    if (((Method-IL $oldTab) -join "`n") -cne ((Method-IL $newTab) -join "`n")) {
        throw 'Legacy CreateTabButton IL changed; review consumer color override compatibility.'
    }

    $members = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($type in (All-Types $candidate.MainModule)) {
        foreach ($method in $type.Methods) { [void]$members.Add($method.FullName) }
        foreach ($field in $type.Fields) { [void]$members.Add($field.FullName) }
    }
    $audit = @()
    foreach ($folder in Get-ChildItem -LiteralPath $ModsRoot -Directory) {
        if ($folder.Name -eq 'AutoUtilitiesModFramework') { continue }
        $libraries = Join-Path $folder.FullName 'Libraries'
        if (-not (Test-Path -LiteralPath $libraries)) { continue }
        foreach ($dll in Get-ChildItem -LiteralPath $libraries -Filter '*.dll' -File) {
            $consumer = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll.FullName)
            try {
                $refs = @($consumer.MainModule.GetMemberReferences() | Where-Object {
                    $_.DeclaringType.Scope.Name -eq 'AutoUtilitiesModFramework'
                })
                if (-not $refs.Count) { continue }
                $missing = @($refs | Where-Object { -not $members.Contains($_.FullName) })
                if ($missing.Count) { throw "Unresolved AUMF members in $($dll.Name): $($missing.FullName -join ', ')" }
                $themeCalls = @($refs | Where-Object {
                    $_.Name -in 'AddTabs','CreateTabButton','AddVerticalScroll','AddCollapsibleHeader' -or
                    ($_.DeclaringType.Name -eq 'VerticalScrollView' -and $_.Name -eq 'Create')
                } | ForEach-Object { "$($_.DeclaringType.Name).$($_.Name)" } | Sort-Object -Unique)
                $audit += [pscustomobject]@{ Mod=$folder.Name; References=$refs.Count; ThemeSurfaces=$themeCalls }
            } finally { $consumer.Dispose() }
        }
    }

    $default = Get-Content -Raw -LiteralPath (Join-Path $CoreRoot 'Config\UITheme\Themes\Default.json') | ConvertFrom-Json
    $required = @{
        Images = @('PanelContent','PanelFooter','PanelHeader','TabPanelButtons')
        Texts = @('Unsized','UnsizedInverse')
        Buttons = @('Panel','Red','Tab','Tab_Active')
        Sliders = @('Accent')
        Scrollbars = @('Default')
        Colors = @('Accent','Green','Red','Yellow','Grey','Black','Bright')
    }
    foreach ($section in $required.Keys) {
        foreach ($id in $required[$section]) {
            if ($id -cnotin $default.$section.PSObject.Properties.Name) {
                throw "Missing Core fallback style: $section.$id"
            }
        }
    }
    $bindingTypes = @('Image','Text','Button','Slider','Scrollbar')
    foreach ($kind in $bindingTypes) {
        $type = $candidate.MainModule.Types | Where-Object Name -eq "FrameworkThemed$kind"
        if ($type.BaseType.FullName -ne "Game.Systems.UIUX.UIThemed$kind") {
            throw "Binding $kind is not included in Core's normal reload types."
        }
        if (@($type.Methods | Where-Object Name -in 'Update','LateUpdate','FixedUpdate').Count) {
            throw "Theme binding $kind introduced frame polling."
        }
    }
    [pscustomobject]@{
        Result='PASS'; PreservedPublicEntries=$before.Count
        LegacyTabIL='Unchanged'; CoreFallbackStyles='All present'
        ThemeReloadBindingTypes=$bindingTypes.Count
        AssemblyVersion=$candidate.Name.Version.ToString()
        FileVersion=$version.FileVersion; ProductVersion=$version.ProductVersion
        CandidateSHA256=(Get-FileHash -LiteralPath $CandidateDll).Hash
        ConsumerAudit=$audit
    } | ConvertTo-Json -Depth 5
} finally {
    $baseline.Dispose()
    $candidate.Dispose()
}
