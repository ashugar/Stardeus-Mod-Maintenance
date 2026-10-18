# Part Restoration Unit

Part Restoration Unit adds a Tier 3 automated facility for restoring the
Condition of detached biological and robotic body parts.

Version 1.1.2 is a maintenance release that corrects save recovery for loaded
parts and in-flight Nanobot Hive deliveries. Recovery now waits until Core has
fully restored the save state, retains loaded inputs and repair progress, and
leaves Hives available for later deliveries. The correction was gameplay- and
log-validated across repeated save loads. Auto Utilities Mod Framework 1.4.0
remains the required and tested framework version.

## Operation

1. Research **Part Restoration** after **Medical Care** and **Maintenance**.
2. Build a **Part Restoration Unit** from the Facilities menu.
3. Store damaged detached body parts in any object storage. A powered idle
   unit with **Automatic Intake** enabled will queue the first eligible stored
   part automatically.

You can also select one or more loose damaged body parts and choose **Repair**
using the green Health icon, immediately after Dispose.

For a mixed multi-selection, the Repair action counts and processes only body
parts. Other selected objects are ignored. Each eligible part is assigned to a
currently idle unit, so a batch can start as many simultaneous repairs as there
are available units.

The selected physical part is reserved, delivered by an ordinary hauler or an
available Nanobot Hive, repaired while the machine has power, and ejected at
the output. The unit does not use an operator and grants no experience.

Automatic retrieval checks only while at least one enabled, powered unit is
idle, walks Core's authoritative object records once, and stops as soon as each
idle unit has one eligible stored part. It does not build or retain a full
candidate list. Searches run quickly while work is available and back off after
an empty search; completing a repair requests another search immediately.

## Nanobot Hive delivery

When the Nanobot **Hauling** job is enabled, a working Hive can take an
unassigned PRU delivery if the Hive can reach both the part and the unit. This
works for queued parts that begin loose or inside ordinary object storage and
uses Core's authoritative reservation, unstore, carrier, and device-loading
states throughout the trip.

Nanobot delivery is one-way: Hives move waiting inputs into the PRU but never
extract completed parts. The exact in-flight delivery is preserved across a
save and reload. If no suitable Hive is available, the original repair goal
remains available to ordinary haulers.

The building's native Overview tab provides a saved per-unit **Automatic
Intake** toggle and an automatic-intake filter for **Biological and robotic**,
**Biological only**, or **Robotic only** parts. These controls affect only
future automatic orders; manual Repair orders ignore the filter, and work
already waiting or loaded continues normally.

The Overview also reports whether the unit is searching, has no eligible
stored parts, is waiting for a hauler, is repairing, or is paused by its power
state. Its slot shows current Condition and the maximum condition Core permits.
The unit accepts Core's **Speed Upgrade** and **Energy Efficiency Upgrade**
using their standard 15% stacking behavior.

Core's native wound-healing routine determines what can be repaired. Permanent
limits remain permanent: a crippled part can finish at Core's configured maximum
condition rather than becoming stuck waiting for 100%. Quality is never changed.

## Compatibility

The mod is independent of Auto Hauling and Intelligent Recycling. Its public
API is read-only and optional.
Maintained compatibility updates for Auto Hauling and Intelligent Recycling use
reflection, so neither mod gains a hard dependency. Part Restoration yields when
Intelligent Recycling's quality policy claims a part and otherwise prevents Auto
Hauling from replacing a potential restoration task.

When Quantum Storage is installed, the two mods coordinate their active
Nanobot deliveries so one Hive is not assigned to both systems. This optional
coordination does not create a Quantum Storage dependency. No unrelated
third-party mod is patched or overridden.

Auto Utilities Mod Framework 1.4.0 supplies the shared consumer compatibility
lifecycle. Multi-selection actions, entity Overview content, and device
upgrades use Core's native extension contracts and continue to follow the
active Stardeus theme. PRU does not use AUMF's opt-in `ThemeAwareUi` helpers,
so no feature gate or custom UI migration is required. Nanobot delivery
likewise uses Core's Hive and hauling lifecycle because AUMF does not duplicate
that system.

## Version 1.1.2

- Corrects save-recovery timing so units retain their exact loaded parts and
  repair progress after reloading.
- Restores an in-flight Nanobot Hive delivery only after Core's saved objects
  and reservations are available.
- Safely releases stale delivery state without losing the part or disabling
  the Hive for later Part Restoration Unit work.
- Validated through repeated save loads with no early object-query,
  missing-input, Nanobot recovery, or null-reference errors.

## Version 1.1.1

- Declares and verifies consumer API compatibility with Auto Utilities Mod
  Framework 1.4.0.
- Keeps PRU's Core-native selection and Overview UI unchanged; no opt-in
  `ThemeAwareUi` integration is required.
- Makes no gameplay changes to the validated 1.1.0 release behavior.

## Version 1.1.0

- Added Nanobot Hive delivery for queued loose and stored body parts.
- Respects the Nanobot Hauling job, Hive availability, range, and reachable
  area at both ends of the delivery.
- Preserves in-flight deliveries through save and reload.
- Coordinates safely with Quantum Storage when it is installed.
- Leaves completed-part ejection and downstream hauling unchanged.
- Restores already-loaded parts only after Core's object registry is ready,
  avoiding false missing-input warnings during save loading.

## Requirements

- Stardeus 0.15.0 or later
- Auto Utilities Mod Framework 1.4.0
