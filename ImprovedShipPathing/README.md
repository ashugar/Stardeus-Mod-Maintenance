# Improved Ship Pathing

Improved Ship Pathing is a local Stardeus gameplay test candidate for intercepting moving ships.

## Behavior

- Applies when **Travel Here** or **Attack** has selected a ship with an active local course.
- Predicts the target along its current course using its current speed, wanted speed, acceleration, and endpoint.
- Recognizes Core's `Sector_Orbit` behavior and projects around the continuing circular course instead of treating each short chord waypoint as a final stop.
- Estimates the earliest standoff point the player ship can reach while accounting for its present motion, acceleration, maximum engine push, braking, and Core's stop-before-large-turn behavior.
- Uses a fly-through course for actions such as **Attack**, where Core starts the action on contact and force-stops the player ship; **Travel Here** continues to plan a stopped rendezvous.
- Recalculates while the target moves or changes course.
- Returns to unmodified Core following as soon as the target has no active course.

## Ownership and safety

The mod changes only the destination point used during a moving-ship flight. Core continues to own:

- action availability and the selected target;
- engine power, bridge operation, turning, acceleration, and braking;
- arrival, stopping, cancellation, and save/load;
- combat eligibility and combat start;
- target removal, hyperspace, derelict approach, and all non-ship destinations.

If the installed Core contract is unavailable or the solver faults, the Harmony patch is removed for the session and native ship navigation remains in control.

## Status

Version 0.1.1 is a local test candidate. Build and solver checks do not establish gameplay acceptance; see `VALIDATION.md`.
