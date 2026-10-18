# Research

Baseline inspected: installed Stardeus `v0.15.0-rc317.3194` and its bundled Core reference source.

## Native action path

- `SOActionTravelTo.BeginTravel` calls `ShipSys.SetDestination(SpaceObject)`.
- `SOActionAttackShip.Attack` calls the same overload with an arrival callback that starts combat.
- The shared `ShipSys` destination therefore preserves the original action and callback while allowing one navigation correction to cover both cases.

## Cause of trailing behavior

During `ShipSys.OnTickProgress`, Core first obtains the target ship's current nearby/standoff position. If the target has a local course, it then asks `NPCShip.GetFuturePosition(10)`, but adopts that point only when it is closer to the player than the ordinary nearby position. A fleeing ship's future point is normally farther away, so the lead is rejected and the player continues following the ship's present position.

## Selected seam

The mod applies an exact-signature postfix to private `ShipSys.OnTickProgress(float)` and replaces only `destinationLoc` after Core's moving-target update. It requires and validates the private fields it reads through Harmony field injection. No action button, NPC behavior, combat rule, engine component, or save data is replaced.

## Solver model

- Target motion follows the public `NPCShip` local course fields: `SO.Location`, `LocalTarget`, `LocalVelocity`, `WantedVelocity`, and Core's space-travel acceleration.
- Player maximum speed is derived from Core's current `engineForce`, `ShipMass`, `Equations.EnginePush`, and the current layer's native motion multiplier.
- Reach uses an accelerate/cruise/brake envelope, includes existing velocity along the candidate course, and reserves stop time before heading changes greater than 30 degrees because Core does the same.
- The earliest reachable target-center position is converted to the target's native standoff radius before becoming the destination.

## 0.1.1 pursuit correction

The first gameplay attempt exposed two assumptions that were too conservative:

- `NPCShipBehavior_Sector_Orbit` does not declare the full orbit as one route. It repeatedly calls `Maths.NextPointOnCircle` to create short two-unit chord waypoints. Treating `LocalTarget` as a final stop produced only a small lead and then moved that lead around the orbit. Version 0.1.1 recognizes this Core behavior and projects distance around the circle, including Core's ship-ID-based orbit direction.
- Attack does not require the player ship to finish a stopped arrival. Its native callback starts combat as soon as the player crosses the target's standoff envelope and then force-stops flight. Version 0.1.1 therefore solves Attack as earliest fly-through contact and places the navigation aim beyond the meeting point by the native braking distance. Travel Here still solves a stopped rendezvous.

This is a continuously corrected estimate, not a replacement flight simulator. Core remains authoritative on the actual trajectory.
