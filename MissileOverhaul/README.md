# Missile Overhaul

Version 0.4.0 is a local gameplay test candidate for Stardeus 0.15.0-rc321. Requires
Auto Utilities Mod Framework 1.4.0. Tractor-Beam-aligned ordinary Breach Capsule
interception has passed gameplay validation; broader acceptance remains in progress.

Both Interceptor Turrets and Rocket Pods receive shared space-threat targeting,
armed readiness, and corrected shot timing. Matching weapons on allied and
hostile ships receive the same readiness and missile-defense rules. Their roles
are separate:

| Weapon | Responsibility |
| --- | --- |
| Interceptor Turret | Incoming missiles, Hullpiercers, meteoroids |
| Rocket Pod | Remaining eligible targets, including asteroids, ordinary breach capsules, suspected raid Storage Capsules and enemy ships |

The division applies to these two weapon definitions on every ship and includes
manual player shots. A missing or unavailable weapon does not transfer its
targets to the other role. Other weapon types retain Core behavior.
Hullpiercers and ordinary breach capsules are distinguished by their actual
components, since Core groups them under the same target category.

## Behavior

Available player buildings cover separate eligible space threats within their
roles before surplus buildings support targets already covered. A shot in flight
counts toward coverage, so newly arriving threats receive attention. The allocation considers each
launcher's disabled target types and range. Higher configured priority wins
between otherwise available targets; covering an additional threat comes first.
Ready buildings are considered before those still charging or starting.

Hostile ship missiles use Core's interception mechanism at missile launch.
The nearest eligible Interceptor is selected; Core immediately spends its
ammunition and enters Firing, so another simultaneous missile must use another
ready defender. Selection checks Automatic mode for player buildings, along
with ammunition, power, health, hacking, overload and the configured flux
threshold. The same selection applies when allied or hostile ships defend
against incoming missiles. The mod does not create a second missile simulation
or duplicate interception attempts.

Automatic mode arms the overhaul. A powered building completes its normal
startup, then remains Ready between threats and finishes recharge even when
its previous target disappears. Armed, loaded buildings use their native active
energy requirement continuously: 25 for an Interceptor Turret and 50 for a
Rocket Pod, compared with a passive demand of 5 each. Interceptor startup and
recharge remain 20 and 45 game minutes; Rocket Pod startup and recharge remain
135 and 90 game minutes. Native timing variation and speed upgrades still
apply. Immediate response requires finished startup or recharge.

Allied and hostile ships use Core's abstract combat weapons rather than placed
buildings. Their Rocket Pods and Interceptors charge while the ship is present,
remain ready between combats, and use native health, ammunition, flux, hacking,
reload and recharge rules. Core still decides factions, opponents, focus and
offensive targets. Newly arrived ships must complete normal startup before they
receive immediate-response behavior.

Unaligned targets retain the correct native weapon rules. Interceptors use
point-defense evasion: 85.5% against meteoroids and 67.5% against Hullpiercers,
with 90% native interception chance against incoming ship missiles. Rocket Pods
use ordinary turret evasion for their remaining space targets, including 76.5%
against asteroids and 42.5% against ordinary breach capsules. Once a Tractor
Beam finishes aligning a trajectory to its target marker, the path is fixed and
Missile Overhaul treats a defensive shot at that object as a guaranteed hit.
Ship targeting retains Core's usual evasion and accuracy rules. Damage and
health handling remain native, with the narrow Storage Capsule handling below.

Five or fewer flying Storage Capsules are ignored. Detecting six simultaneously
activates a suspected-raid incident, displays **Incoming Storage Capsules are
suspected raid transports.**, and makes every capsule in that flight group a
Rocket Pod target. The incident remains active as the count falls below six so
the defense does not abandon the remaining capsules; it resets when no eligible
capsule remains in flight. The warning deliberately says “suspected” because the
game may not declare the raid until after the capsules land.

Storage Capsules are normally invincible and do not implement Core's turret-target
interface. Missile Overhaul exposes only the active suspected group to Rocket
Pods. On a successful lethal hit, it first invokes the capsule's native opening
path at its current position, which releases a stored Being, item, online order,
or raid payload there. It then temporarily removes invincibility for that hit and
applies the Rocket Pod's normal damage through Core. A surviving capsule has its
invincibility restored immediately. Ordinary groups of one through five remain
untargeted and unchanged. An unaligned Storage Capsule has the same 76.5% final
hit chance used for ordinary asteroid defense; completed Tractor Beam alignment
raises it to the same guaranteed hit used for other aligned trajectories.

A launched space-target shot retains the object selected at launch. A later
assignment cannot redirect its damage to another object. Removed, destroyed or landed
targets receive no delayed damage. Completion runs once, including Core's
existing save-and-exit completion of active shots.

Flight uses a constant-velocity intercept calculation. Hazard velocity is
converted from simulation ticks to the scaled seconds used by weapon effects.
Space-target projectile speed receives the same clock conversion, preserving
normal speed travel relative to the simulation at faster speeds. A threat without
a reachable intercept is excluded from automatic defense.

Manual fire mode does not receive automatic targeting or continuous arming,
but shots still respect the building's role and retain their original target.
Interceptors can defend against meteoroids during ship combat. Rocket Pods keep
Core's combat eligibility for their remaining targets, including ship priority
over ordinary asteroids. Existing Rocket Pod battle-station availability,
weapon hacking, overload and flux limits still apply. Interceptors retain
Core's station-free operation. Native refill jobs replenish both ammunition types.

The intercept is calculated at launch. Subsequent ship movement, clock changes
or an incomplete Tractor Beam trajectory change may separate the visible flight
path from the target. Fully aligned trajectories receive the guaranteed-hit
rule above. This candidate does not introduce physical collision or continuous
visual homing.

## Validation

Build, isolated policy checks and the rc321 compatibility audit pass. Player startup behavior has partial
gameplay evidence from 0.2.0. Allied and hostile readiness, actual interception,
the new Storage Capsule incident, save/load and final Player.log acceptance remain pending. See
[VALIDATION.md](VALIDATION.md) and [RESEARCH.md](RESEARCH.md).

Enable Missile Overhaul in the Mods menu, restart Stardeus, and load a test save.
Allow both automatic building types to finish startup before evaluating
response time. Existing priority choices remain saved. A disabled target type
remains disabled within its allowed role; priorities cannot transfer a target
to the other building's responsibility.

To revert, disable Missile Overhaul and restart. The candidate adds no custom
save data or buildings. Existing weapons, ammunition and settings
remain game-owned. No Workshop publication has been made.

The complete 0.3.1, 0.3.0, 0.2.0 and 0.1.0 candidates are preserved under
`Work/Backups` for rollback.

## Build

Build against the installed game and installed external AUMF DLL:

```powershell
dotnet build .\mod.csproj -c Release
dotnet run --project .\Tests\Defense.Tests.csproj -c Release
& .\Tests\PatchAudit.ps1
```

The last command uses the installed ilspycmd tool to check patch targets through
decompilation. It does not install Harmony patches or run gameplay methods.
