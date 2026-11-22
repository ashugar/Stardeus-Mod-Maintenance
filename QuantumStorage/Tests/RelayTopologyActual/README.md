# Actual topology query and transaction fixture

Run with the .NET 10 SDK:

```powershell
dotnet run --project QuantumStorage/Tests/RelayTopologyActual/RelayTopologyActual.Tests.csproj
```

The project links the actual production `StorageRelayQueries.cs`, `StorageRelayTopology.cs` and `RelayPolicy.cs` files. It does not copy the optimized matching loop or graph transaction. Minimal substitutes provide native field/getter shapes, registered-node dictionaries and inert refresh/display callbacks. Registry wrapper methods use the actual production membership-revision helper; the real `Attach`, `Detach`, `Unload`, `OnTick` and Game/Unity lifecycle are not compiled into this fixture and require independent source/binary/runtime review.

The independently written breadth-first graph oracle checks connected groups across deterministic random worlds and state changes. Cases cover direct same-tick power, deficit, construction, relocation, health, position, range, network and identity changes; in-place component-array changes; object node rediscovery; same-count registry replacement and unreadable entries; nested scopes and exception cleanup. Transaction cases mutate earlier build facts from later getters, alter membership or public state while refreshing, throw original callback exceptions, read groups during a build, and read committed groups from display callbacks while an older scope exists. Redundant registration must retain the existing group instance.

The warmed matching-loop check uses a cached delegate to the actual private predicate and verifies zero managed allocations and zero key-hash calls. A separate output measures the actual forced metadata-update transaction: unchanged updates avoid allocation, while changed builds allocate. Desktop CLR timings and allocations are fixture observations, not Unity Mono timings, rendered FPS, gameplay, physical inventory recovery or save/load validation. The assertion total counts individual comparisons, not independent gameplay scenarios.

Two `CS0649` warnings can arise because the linked policy file also contains unused balancing-fixture fields. They do not indicate a production build result. No game is launched or stopped, no installed dependencies are loaded, and no player data is read or written.

Current unchanged-force coverage: two native/Active passes; no build refresh/UI; late public mutation triggers corrected build; nested callers refuse; missing/unreadable snapshot paths. Warmed forced unchanged updates allocate zero bytes in the desktop fixture; changed builds still allocate. See private fastpath final log and identity pins.

## Deferred query dependency fixture

```powershell
dotnet run --project QuantumStorage/Tests/RelayTopologyActual/LazyScope.Tests.csproj -c Release
```

This additional project links the actual material adapter, engine, topology matcher/builder and scope implementation. Inert Core substitutes supply policy, inventory candidates, provider callbacks and clocks. It proves that ordinary-only answers skip all global topology walks, including with 144 registered nodes; real Relay dependencies keep fresh initial/final checks. Cases include fresh reservation edits, earlier ordinary stock joining a later pool, indirect and nested provider dependencies, same-tick bridge changes, offline/missing/forced access, local-only semantics, different owners, out-of-order disposal, activation reentry, original exceptions and post-publication native display reads during a rejected repair attempt. Object-provider cases include stale pre-query topology that a callback restores before final validation. The real native provider implementation, Core lifecycle, Harmony and Unity UI are not executed here.

The diagnostic-enabled getter parity fixture remains separately runnable with `Diagnostic.Tests.csproj`. Its source matcher is actual production code; its clock/sink selection is inert. Recorder/caller trace contracts are in the sibling `RelayQueryBaseline` project.
