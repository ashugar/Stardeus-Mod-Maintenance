# Remember Facility Upgrades

Remember Facility Upgrades preserves the installed upgrade configuration of a
facility when Stardeus destroys and automatically rebuilds that facility, or
when Core's **Stealing Upgrades** mental breakdown removes a module.

The mod performs one reconciliation scan after a game loads. After that it
tracks normal upgrade, removal, relocation, and construction events. Just
before Core ejects upgrades from a damage-destroyed facility, the mod records
the exact upgrade ID in every occupied slot. It also captures other
auto-rebuild removal paths at Core's removal command.

When the matching replacement finishes construction, the mod calls Core's
normal `UpgradesComp.OrderUpgrade(slot, upgrade)` API. This creates ordinary
**Install Upgrade** jobs and consumes real upgrade modules; the mod does not
directly install upgrades or create free materials.

For upgrade theft, the mod observes Core's dedicated breakdown action without
blocking it. Core still ejects the module, removes it from inventory, and makes
the colonist carry it. The mod identifies the exact vacated slot and queues an
ordinary **Install Upgrade** job that waits for the same physical module to
become available again.

## Safety rules

- A replacement must match the original definition, layer, and position.
- Existing installed or pending upgrades are never overwritten.
- Intentional upgrade removal and deconstruction remain final.
- Missing, incompatible, or out-of-range upgrades are logged and left alone.
- Saved pending rebuild records survive load-time reconciliation.

## Runtime validation

Version 1.1.0 completed focused in-game acceptance on Stardeus 0.15.0. The
validated scenarios cover exact-slot recovery after damage destruction and
automatic rebuilding, saving and loading while recovery is unresolved,
intentional removal and deconstruction remaining final, and Core's
**Stealing Upgrades** breakdown.

During the theft test, Core removed and carried the real installed module. The
mod allowed that behavior to continue and queued an ordinary **Install
Upgrade** job for the exact vacated slot. The returned module was recovered
through Core's normal inventory, worker, and installation rules.

## Release history

### 1.1.0

- Adds exact-slot recovery for Core's **Stealing Upgrades** mental breakdown.
- Allows the theft and carrying behavior to complete unchanged, then uses the
  ordinary **Install Upgrade** job for the same physical module.
- Keeps intentional player removal and deconstruction final.
- Runtime-validated destruction recovery, unresolved save/load recovery,
  theft recovery, and the intentional-removal negative cases.

### 1.0.0

- Promotes the runtime-validated exact-slot destruction and save/load recovery
  behavior to the first public release.
- Adds the complete Steam Workshop description and themed release cover.
- Marks the package as a Steam Workshop mod.

### 0.1.1

- Preserves a captured recovery assignment while Core ejects installed modules
  from a damage-destroyed facility.
- Prevents same-tick or burst combat destruction from being mistaken for
  intentional upgrade removal.

### 0.1.0

- Initial exact-slot upgrade recovery for automatically rebuilt facilities.
