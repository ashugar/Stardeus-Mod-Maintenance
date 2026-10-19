# Material panel compatibility checks

This optional local harness requires .NET 10, the installed Stardeus Managed assemblies, and a locally available Mono.Cecil DLL. It is not a portable CI harness and must not ship in the Workshop payload.

From the repository root, using locally resolved paths:

```powershell
dotnet build QuantumStorage/Tests/MaterialPanelTests.csproj -c Release "-p:StardeusManagedPath=$ManagedRoot" "-p:MonoCecilPath=$MonoCecilPath"
dotnet QuantumStorage/Tests/bin/Release/net10.0/MaterialPanelTests.dll "$ManagedRoot" "$RepoRoot/QuantumStorage/Libraries/QuantumStorage.dll"
```

The harness checks the supplied compiled mod's real preparation guard and row cache. It rejects missing, changed, duplicate, and additional callback fingerprints; checks full-list identity, quantities, sorting, and structural fallback; and inspects native callbacks without invoking them. The rc337 contract intentionally expects general forbidden-item policy and its inventory-index notification, rather than the old hauling-only policy.

No Harmony detours are installed, no game process is launched, and no Core gameplay callback runs. The printed hashes identify the exact binaries tested. Unity widget lifecycle, actual clicks, and open-panel frame rate still require user-controlled in-game validation.

