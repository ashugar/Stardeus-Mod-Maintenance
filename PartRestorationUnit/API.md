# Part Restoration Unit read-only API

Part Restoration Unit 1.1.2 exposes contract version 1 at:

```csharp
Game.Systems.PartRestoration.PartRestorationApi
```

The API is optional and read-only. It does not create, cancel, reserve, load,
repair, or eject anything. Calls must be made on Stardeus's main simulation
thread.

## Entry points

```csharp
public static int ContractVersion { get; }
public static PartRestorationApiInfo GetApiInfo();
public static bool TryGetObjectSnapshot(
    uint objectId,
    out PartRestorationObjectSnapshot snapshot);
public static bool TryGetObjectOwnershipSnapshot(
    uint objectId,
    out PartRestorationOwnershipSnapshot snapshot);
```

`TryGetObjectSnapshot` is intended for general automation coordination. It
reports whether a body part is repairable, already owned, would be claimed by
an available unit, yielded to Intelligent Recycling, or released for external
automation.

`TryGetObjectOwnershipSnapshot` is the recursion-safe narrow entry point for
Intelligent Recycling. It reports only recognition and actual PRU ownership;
it never queries Intelligent Recycling policy.

Consumers should discover the API from the loaded assembly named
`PartRestorationUnit`, validate contract version 1, and treat failed calls as
no permission to mutate or replace a task.
