# Development tools only. These functions read local configuration and never deploy or start the game.
function Resolve-StardeusGameRoot {
    param([string]$GameRoot = $env:STARDEUS_GAME_ROOT)
    if (-not $GameRoot -or -not (Test-Path -LiteralPath (Join-Path $GameRoot 'Stardeus_Data/Managed/Game.dll') -PathType Leaf)) {
        throw 'Provide a verified Stardeus game root with -GameRoot or STARDEUS_GAME_ROOT. See BUILDING.md.'
    }
    [IO.Path]::GetFullPath($GameRoot)
}
function Resolve-StardeusManagedPath {
    param([string]$ManagedPath = $env:STARDEUS_MANAGED_PATH, [string]$GameRoot = $env:STARDEUS_GAME_ROOT)
    if (-not $ManagedPath) { $ManagedPath = Join-Path (Resolve-StardeusGameRoot -GameRoot $GameRoot) 'Stardeus_Data/Managed' }
    if (-not (Test-Path -LiteralPath (Join-Path $ManagedPath 'Game.dll') -PathType Leaf)) {
        throw 'The configured managed directory must contain Game.dll; no alternative installation is selected.'
    }
    [IO.Path]::GetFullPath($ManagedPath)
}
function Resolve-StardeusCoreRoot {
    param([string]$CoreRoot, [string]$GameRoot = $env:STARDEUS_GAME_ROOT)
    if (-not $CoreRoot) { $CoreRoot = Join-Path (Resolve-StardeusGameRoot -GameRoot $GameRoot) 'Stardeus_Data/StreamingAssets/Mods/Core' }
    if (-not (Test-Path -LiteralPath (Join-Path $CoreRoot 'ModInfo.json') -PathType Leaf)) {
        throw 'Provide the verified installed Core directory with -CoreRoot or configure STARDEUS_GAME_ROOT.'
    }
    [IO.Path]::GetFullPath($CoreRoot)
}
function Get-StardeusSdkCompiler {
    $sdkLines = @(& dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or $sdkLines.Count -eq 0) { throw 'An installed .NET SDK is required.' }
    if ($sdkLines[-1] -notmatch '^([^ ]+) \[(.+)\]$') { throw 'Could not locate the installed SDK compiler.' }
    $compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn/bincore/csc.dll'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The installed SDK compiler was not found.' }
    $compiler
}
function Resolve-StardeusCecilPath {
    param([string]$CecilPath = $env:STARDEUS_CECIL_DLL)
    if (-not $CecilPath) {
        $store = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet/tools/.store/ilspycmd'
        if (Test-Path -LiteralPath $store -PathType Container) {
            $CecilPath = Get-ChildItem -LiteralPath $store -Recurse -File -Filter 'Mono.Cecil.dll' |
                Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
    }
    if (-not $CecilPath -or -not (Test-Path -LiteralPath $CecilPath -PathType Leaf)) {
        throw 'Provide an installed Mono.Cecil.dll with -Cecil or STARDEUS_CECIL_DLL; see BUILDING.md.'
    }
    [IO.Path]::GetFullPath($CecilPath)
}
