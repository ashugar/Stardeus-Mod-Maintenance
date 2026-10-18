# Installed Core investigation, 2026-10-07

The installed Game.dll and its supplied reference source were inspected
read-only. Targeted decompilation confirms targeting, shot completion,
readiness and clock behavior in the compiled assembly. The candidate was built
against that assembly and the installed AUMF DLL.

Game.dll SHA256:
`38006FD473181DD301A8A0ADD9791415B9CA1AF7CA3650A625180C908FB75366`

AUMF DLL SHA256:
`6E76464569AAE84CEACCF961CE62FDE12B29E901C88390437C017004FF8B42E8`

## Concentration on one threat

`TurretsSys.TargetShipCombatTurrets` independently chooses a nearby target
for each turret. It keeps no shared assignment count. Several equivalent
launchers can therefore choose the same nearest threat. Automatic selection
is repeated, so targets can change while an earlier shot is still in flight.

## Startup delay

`ShipDevice.ShouldBeActive` returns the ship's combat state.
`ShipWeapon.Tick` resets a weapon to Inactive when it has neither a combat
activation condition nor a non-ship target. Losing the last threat therefore
discards readiness and ongoing recharge. A Rocket Pod's next acquired threat
triggers another 135-minute windup (plus native timing variation).

The armed-readiness patch changes that activation condition for automatic,
constructed, registered and switched-on player Rocket Pods and Interceptor
Turrets. Core continues
to own all weapon phases and their power updates.

## Accuracy and flight

`ShipWeapon.ShootNonShipTarget` calculates:
`Accuracy * Clamp(1 - evade, 0.01, 1)`.
Missile weapons use `TurretEvadePercent`; point-defense weapons use
`PointDefenseEvadePercent`. For meteoroids those values are 0.33 and 0.05.
For Hullpiercers they are 0.60 and 0.25. Rocket Pod accuracy is 0.85.
The 0.2.0 candidate assigns those objects to Interceptor Turrets, whose native
accuracy is 0.90 and whose native evasion rule already uses point defense.
Rocket Pods retain their ordinary evasion rule for remaining space targets.
This replaces the provisional Rocket Pod defense behavior in 0.1.0.

`BreachCapsuleFlyingComp` reports 0.50 turret evasion, reducing an ordinary
Rocket Pod shot to a 42.5% hit chance. Tractor Beam completion sets the
game-owned `IWithTrajectory.WasAligned` flag after the impact point reaches the
beam's target marker and then releases the projectile. At that point the path
is fixed and predictable. Version 0.3.1 uses this flag to guarantee defensive
hits against fully aligned trajectory objects. Targets still being redirected
and targets never aligned by a Tractor Beam retain the native probability.

Core predicts a target position from its current distance divided by weapon
speed, then recomputes animation duration from distance to that predicted
position. These times differ for a moving target. Hazard movement uses
`Velocity * Clock.TicksPerUpdate`, while `ShipWeaponSys.UpdateShots` uses
`GameTimer.deltaTime`. Meteor prediction also doubles its velocity, unlike
the actual movement expression. A correct constant-velocity intercept uses
one travel time and consistent clock units.

The native delayed damage closure reads `NonShipTarget` at completion rather
than capturing the selected object. A different current target can receive
the earlier shot's damage; a cleared target can discard an earlier successful
shot. The candidate captures the original target and checks its current
activity and health before applying native damage.

Shot success is decided by probability at launch. Visible flight does not
perform a projectile-versus-target collision test. The identified defects
explain poor defense without establishing a measured gameplay miss rate.

## Suspected raid Storage Capsules

In rc321, `StorageCapsuleComp` implements `IWithTrajectory` but does not
implement `IShipCombatTurretTarget`. It therefore cannot enter Core's turret
target list. A flying capsule exposes its current position and velocity through
`TryGetHitPoint`, preserves `WasAligned`, and remains trajectory-valid until it
lands or misses its ship. Version 0.4.0 wraps only capsules belonging to an
active suspected incident in a transient Rocket Pod target. No Core object or
save object is replaced.

The trigger is six simultaneously flying, trajectory-valid Storage Capsules.
Once triggered, the incident remains active until that count reaches zero.
This keeps the remaining five and fewer eligible after the first interception.
The trigger is intentionally independent of the story-event declaration because
`MultiStorageCapsuleRaid` creates the incoming capsules before they open. The
warning describes them as suspected raid transports rather than asserting that
their hidden payload is hostile.

The Storage Capsule definition sets `ObjHealth.IsInvincible=true`; its default
health is 100. A Rocket Pod shot deals 500 explosive damage. For a successful
lethal interception, the mod calls `StorageCapsuleComp.Open(null)` while the
capsule still exists at the interception position, briefly permits native
damage, and then calls `ObjHealthComp.TakeDamage`. Core's opening method handles
both cargo families: online-order items spawn at the capsule's `PosIdx`, while
`CapsuleOpenHelper.Start` receives the same object position and the saved content
seed, rigged event, faction, raid points and raider IDs. This preserves native
item, Being and raid-payload creation instead of reconstructing cargo in the mod.
If a hit is not lethal, invincibility is restored immediately.

Because Core defines no evasion values for its untargetable Storage Capsule, the
wrapper uses the ordinary asteroid values: 0.10 turret evasion and 0.05 point-defense
evasion. Rocket Pods therefore have a 76.5% final chance against an unaligned
capsule and a guaranteed hit after completed Tractor Beam alignment.

The suspected-incident latch and target wrappers are runtime state. They contain
no player choices or cargo data. A save made during a group and reloaded after
the visible flying count has fallen below six will require a new six-capsule
trigger; this reload edge remains part of gameplay acceptance.

## Scope and cooperation

The 0.4.0 candidate modifies both weapon definitions on every ship. Interceptors
handle incoming missiles, Hullpiercers and meteoroids; Rocket Pods handle remaining eligible
targets. `HullpiercerComp` and `BreachCapsuleFlyingComp` both report Core's
`BreachCapsule` target category. The mod classifies their actual component
types to preserve the requested distinction.

Player weapons are placed buildings with `ShipWeaponComp`, `RotatingTurret`,
power and refill storage. Non-player weapons are abstract `ShipWeapon` devices
with no map component. `ShipWeaponSys.EnemyWeapons` contains every non-player
ship currently in view, including allies, and ticks those weapons through the
same phase machine. Their native `ShouldBeActive` condition previously reset
them outside combat. The candidate keeps supported non-player weapons active
while the ship is present and not derelict, allowing normal startup and recharge
to finish before combat. It does not force a newly arrived weapon directly to
Ready.

Shared coordination runs before
Core's space-target pass; the native pass then excludes managed Rocket Pods and Interceptors
and continues for other weapons. Core's `Shoot` still consumes ammunition,
enters the firing phase and submits the shot through its normal query.

The coordinator uses game-owned component and weapon registries. Only
transient missile coverage is tracked; no save ledger is introduced. Target
eligibility during ship combat remains native for Rocket Pods. Interceptors
can retain meteoroid defense during ship combat despite Core's ordinary hazard
exclusion. All Core and other mod files remain unchanged. The candidate contains
no continuous homing implementation.

`ShipCombatState.GetClosestAvailablePointDefense` chooses a Ready point-defense
weapon when an enemy launches a missile at the ship. `ShipWeapon.Shoot` decides
the interception outcome immediately, changes the defender to Firing and
consumes one ammunition unit. Subsequent missiles therefore choose another
Ready defender. Those missiles are not `IShipCombatTurretTarget` objects in the
component scan. The mod retains this native interception mechanism and filters
Interceptor selection on player, allied and hostile ships through arming,
health, ammunition, hacking, overload and flux checks. The target ship owns the
point-defense lookup, so Core's faction and opponent selection remain intact.

Space hazards and breach objects exist as map components around the player ship.
Only placed player buildings can target them, so shared hazard allocation and
corrected moving-target flight remain player-building behavior. Non-player ships
receive the equivalent behavior that Core exposes: persistent weapon readiness,
the Rocket Pod/Interceptor role split, and filtered incoming-missile defense.

Role gates also cover `ShipWeapon.CanEngage` and `ShipWeapon.Shoot`, preventing
manual or stale assignments from crossing responsibilities before ammunition
is consumed. The 0.3.1, 0.3.0, 0.2.0 and 0.1.0 candidates are preserved under
`Work/Backups`.

The official [Stardeus changelog](https://stardeusgame.com/changelog/) identifies
Rocket Pod as a missile weapon and Interceptor Turret as point defense. The
technical findings above come from the installed compiled Core and reference
source, not a public description of another build.
