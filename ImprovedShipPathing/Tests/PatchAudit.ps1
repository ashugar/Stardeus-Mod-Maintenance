param([string]$ManagedPath = $env:STARDEUS_MANAGED_PATH, [string]$GameRoot = $env:STARDEUS_GAME_ROOT)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../build/LocalPaths.ps1')

$gameDll = Join-Path (Resolve-StardeusManagedPath -ManagedPath $ManagedPath -GameRoot $GameRoot) 'Game.dll'
$shipSys = (& ilspycmd -t Game.Systems.ShipSys $gameDll) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw 'ShipSys decompilation failed.' }

$requiredShipFragments = @(
    'private void OnTickProgress(float progress)',
    'private SpaceObject destination;',
    'private Vector2 destinationLoc;',
    'private Vector2 currentLoc',
    'private Vector2 velocity;',
    'private float engineForce;',
    'private bool hasDestination;',
    'private bool isApproachFlight;',
    'private Action onReachShip;',
    'destinationLoc = destination.Ship.NearbyPosFrom',
    'destination.Ship.GetFuturePosition(10)'
)
foreach ($fragment in $requiredShipFragments) {
    if (!$shipSys.Contains($fragment)) { throw ('ShipSys contract mismatch: ' + $fragment) }
}

$npcShip = (& ilspycmd -t Game.Systems.Ships.NPCShip $gameDll) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw 'NPCShip decompilation failed.' }
$requiredNpcFragments = @(
    'public Vector2 LocalTarget;',
    'public bool HasLocalTarget;',
    'public float LocalVelocity;',
    'public float WantedVelocity;',
    'public Vector2 NearbyPosFrom(Vector2 pos, out bool isNear, float extraDist = 0f)',
    'public Vector2 GetFuturePosition(int afterTicks)'
)
foreach ($fragment in $requiredNpcFragments) {
    if (!$npcShip.Contains($fragment)) { throw ('NPCShip contract mismatch: ' + $fragment) }
}

$orbit = (& ilspycmd -t Game.Systems.Ships.NPCShipBehavior_Sector_Orbit $gameDll) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw 'Orbit behavior decompilation failed.' }
if ((-not $orbit.Contains('Maths.NextPointOnCircle')) -or
        (-not $orbit.Contains('ship.Id % 2 != 1) ? 1 : (-1)'))) {
    throw 'Core orbit direction/radius contract mismatch.'
}

Write-Output 'PASS: moving-ship destination/callback seam, native lead behavior, NPC course data, standoff API, and Core circular-orbit contract verified in installed Game.dll. No gameplay methods executed.'
