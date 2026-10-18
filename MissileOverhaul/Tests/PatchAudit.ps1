param([string]$ManagedPath = $env:STARDEUS_MANAGED_PATH, [string]$GameRoot = $env:STARDEUS_GAME_ROOT)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../build/LocalPaths.ps1')
$gameDll = Join-Path (Resolve-StardeusManagedPath -ManagedPath $ManagedPath -GameRoot $GameRoot) 'Game.dll'
$auditRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'Work\CoreAudit'
New-Item -ItemType Directory -Path $auditRoot -Force | Out-Null
# ILSpy reads assembly metadata and IL without executing Unity code.
$entries = @(
    @{ Type='Game.Systems.Ships.ShipDevice'; File='ShipDevice.cs'; Pattern='public bool ShouldBeActive => Ship.IsInCombat;' },
    @{ Type='Game.Systems.Turrets.TurretsSys'; File='TurretsSys.cs'; Pattern='private void TargetShipCombatTurrets(long ticks)' },
    @{ Type='Game.Systems.Ships.ShipCombatAI'; File='ShipCombatAI.cs'; Pattern='public static bool CanShootNow(ShipWeapon w, bool isPlayer)' },
    @{ Type='Game.Systems.Ships.ShipWeapon'; File='ShipWeapon.cs'; Pattern='private void ShootNonShipTarget(GameState s, Rng rng)' },
    @{ Type='Game.Systems.Ships.ShipCombatState'; File='ShipCombatState.cs'; Pattern='public ShipWeapon GetClosestAvailablePointDefense(Vector2 position)' },
    @{ Type='Game.Systems.Ships.ShipWeaponSys'; File='ShipWeaponSys.cs'; Pattern='public readonly List<ShipWeapon> EnemyWeapons' },
    @{ Type='KL.Clock.Clock'; File='Clock.cs'; Pattern='private float ExpectedTicksPerSecond()' },
    @{ Type='Game.Components.BreachCapsuleFlyingComp'; File='BreachCapsuleFlyingComp.cs'; Pattern='public bool WasAligned { get; set; }' },
    @{ Type='Game.Components.TractorBeamComp'; File='TractorBeamComp.cs'; Pattern='Projectile.WasAligned = true;' },
    @{ Type='Game.Components.StorageCapsuleComp'; File='StorageCapsuleComp.cs'; Pattern='public bool IsMidFlight => !hasLanded;' },
    @{ Type='Game.Components.ObjHealthComp'; File='ObjHealthComp.cs'; Pattern='public void TakeDamage(float amount, DamageType type)' },
    @{ Type='Game.Systems.Ships.ShipWeaponTargetTypeDef'; File='ShipWeaponTargetTypeDef.cs'; Pattern='LoadObjects<ShipWeaponTargetTypeDef>("Config/Ships/Devices/WeaponTargetTypes")' },
    @{ Type='Game.Systems.Notify.Notification'; File='Notification.cs'; Pattern='public static NotificationData BuildFor(Obj obj, string icon, string text, int durationTicks, NotificationType type = NotificationType.Warning)' }
)
foreach ($entry in $entries) {
    $source = & ilspycmd -t $entry.Type $gameDll
    if ($LASTEXITCODE -ne 0) { throw ('Decompilation failed: ' + $entry.Type) }
    $joined = $source -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $auditRoot $entry.File) -Value $joined
    if (!$joined.Contains($entry.Pattern)) { throw ('Installed API mismatch: ' + $entry.Pattern) }
    if ($entry.File -eq 'ShipWeapon.cs' -and
            (!$joined.Contains('public bool CanEngage(IShipCombatTurretTarget target)') -or
             !$joined.Contains('public bool Shoot(GameState S, Rng rng)'))) {
        throw 'Weapon role gate API mismatch'
    }
    if ($entry.File -eq 'Clock.cs' -and !$joined.Contains('private readonly float upsNormal;')) {
        throw 'Clock normal-speed field mismatch'
    }
    if ($entry.File -eq 'ShipWeaponSys.cs' -and !$joined.Contains('enemyWeapon.Tick(S, ticks)')) {
        throw 'Non-player weapon lifecycle mismatch'
    }
    if ($entry.File -eq 'BreachCapsuleFlyingComp.cs' -and !$joined.Contains('public float TurretEvadePercent => 0.5f;')) {
        throw 'Breach Capsule evasion mismatch'
    }
    if ($entry.File -eq 'StorageCapsuleComp.cs' -and
            (!$joined.Contains('public bool CanChangeTrajectory') -or
             !$joined.Contains('public bool WasAligned { get; set; }') -or
             !$joined.Contains('public void Open(Being opener)') -or
             !$joined.Contains('CapsuleOpenHelper.Start(') -or
             !$joined.Contains('public bool TryGetHitPoint(out Vector2 hitPoint, out Vector2 velocity)'))) {
        throw 'Storage Capsule trajectory or cargo-release API mismatch'
    }
    if ($entry.File -eq 'ObjHealthComp.cs' -and
            (!$joined.Contains('public bool IsInvincible;') -or
             !$joined.Contains('if (IsInvincible || !entity.IsActive)'))) {
        throw 'Storage Capsule damage-gate API mismatch'
    }
    Write-Output ('Verified: ' + $entry.Type)
}
Write-Output 'PASS: 13 patch and behavior targets, including storage-capsule trajectory, cargo release, native damage gate, target-definition loading and warning APIs, in installed IL. No gameplay methods executed.'
