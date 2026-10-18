# Tractor Beam Overhaul

Tractor Beam Overhaul evenly coordinates the incoming-object selections made
by automatic Tractor Beams. When eligible objects appear after beams have
already locked on, surplus automatic beams are reassigned until the load is as
even as possible. Counts differ by no more than one unless fixed manual
assignments make that impossible. Targetable meteoroids and large incoming
asteroids are diverted toward their nearest map edge instead of being guided
into the ship.

## Version 1.1.1 AUMF compatibility update

- Built and tested against Auto Utilities Mod Framework 1.4.0
- Declares AUMF 1.4.0 through the permanent four-argument consumer handshake
- Keeps AUMF as an external `Private=False` dependency
- Adds no Theme-aware UI integration because this mod has no interface yet
- Makes no targeting, diversion, energy, assignment, or save-data changes

## Version 1.1.0 release scope

- Stable nearest-edge diversion for `MeteorComp` and `AsteroidComp` hazards
- Each hazard keeps its first selected exit edge to prevent route oscillation
- Tractor Beam visuals point toward the selected exit edge
- Core pull strength, energy cost, and multiple-beam stacking remain intact
- Hullpiercers, capsules, and other trajectory objects retain normal marker
  landing behavior
- Event-driven stable even-load targeting
- Late-arriving objects trigger automatic rebalancing
- Existing automatic assignments move only when they are surplus
- Manual beams are never reassigned
- Manual assignments count toward each object's load
- Core's release and automatic-assignment lifecycle performs every move
- Valid assignments are retained whenever they already fit the balanced result
- Deterministic choices prevent oscillation after balance is achieved
- No manual coordination pass
- No interface or persistent setting yet

## Roadmap

### Post-1.0.0 research

- Investigate an optional higher-power mode where increased energy consumption
  provides a larger pulling coefficient, allowing Tractor Beams to drag targets
  more effectively toward their designated landing spots.
- Research the current Core energy and trajectory contracts before proposing a
  design. The released default pull strength, energy cost, target-marker
  ownership, multiple-beam stacking, and projectile physics remain unchanged
  unless a later update is separately approved.

## Requirements

- Stardeus 0.15.x
- Auto Utilities Mod Framework 1.4.0
- Tractor Beam Auto Connect 1.2.1 (tested companion version)

## Explicit exclusions

This update does not change Tractor Beam Target assignments, pull strength,
energy costs, speed, or saved target-marker relationships. A valid powered
target marker remains required. Tractor Beam Auto Connect remains the sole
owner of missing target-marker recovery.

Only targetable `MeteorComp` and `AsteroidComp` hazards receive edge-diversion
behavior. Hullpiercers, capsules, and all other trajectory objects continue to
use Core's ordinary marker destination. Micro-Meteoroid Strike applies abstract
direct event damage and does not create an object a Tractor Beam can target.

## Validation status

Version 1.1.1 is a compatibility-only update. AUMF's documentation, canonical
feature catalog, source API, and compiled DLL agree that `ThemeAwareUi` was
introduced in 1.4.0 while the four-argument `TryRegisterConsumer` handshake
remains stable. Tractor Beam Overhaul uses only that handshake, references AUMF
externally, and contains no current UI to migrate to the opt-in Theme helpers.
All 17 policy tests pass, the project builds with zero errors, the current Core
hooks still resolve, and the compiled 1.1.1 DLL calls the four-argument
handshake with compatibility level 1.4.0. Full-start runtime compatibility was
confirmed in game, making version 1.1.1 the accepted maintenance baseline.

Version 1.1.0 has passed in-game runtime acceptance on Stardeus 0.15.0-rc280.
The previously released stable even-load behavior remains accepted, including
late-arrival redistribution. Meteoroid Shower objects were confirmed to be
repelled toward the map edge, and the large Asteroid event was confirmed to be
diverted as well. The fresh runtime log loaded Tractor Beam Overhaul 1.1.0,
initialized both coordination and nearest-edge diversion, and contained no
Tractor Beam Overhaul exception or fail-open message.

All 17 load-balancing and edge-selection policy tests pass, the project builds,
and all balance, trajectory, lifecycle, and beam-visual hooks resolve against
the accepted Core build.

Manual-assignment combinations, active-shower save/load, target and power loss,
Hullpiercer/capsule marker regression, and sustained large-shower performance
remain useful optional edge-case tests.
