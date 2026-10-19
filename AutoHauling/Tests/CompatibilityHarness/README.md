# Local compatibility regression harness

Requires .NET 10 and a local Mono.Cecil assembly. From the repository root:

    dotnet run --project AutoHauling/Tests/CompatibilityHarness -c Release -p:CecilPath="$CecilDll" -- "$CandidateDll" "$Baseline115Dll" "$ManagedDir" "$AumfDir"

All four arguments must be absolute existing paths. Use the preserved **1.1.5**
DLL as baseline and the exact installed game/framework dependencies. No game
or mod dependency assemblies are bundled.

The harness resolves compiled Core/framework references, checks native Forbidden
access and unreachable refusal paths, and compares unchanged system/bridge IL
with 1.1.5. It also invokes the real mod's Save/Load methods with isolated,
in-memory Core containers to check defaults, schema migration, settings, tracked
identities, and failure history. It never initializes a game or opens a save.

These are metadata/control-flow and persistence checks, **not** live hauling,
native icon/inventory side-effect, pathfinding, or UI acceptance tests.
