# Local compatibility regression harness

Requires .NET 10, a local Mono.Cecil assembly, and the installed game's
Managed directory (including its Harmony assembly). From the repository root:

    dotnet run --project AutoHauling/Tests/CompatibilityHarness -c Release -p:CecilPath="$CecilDll" -p:ManagedDir="$ManagedDir" -- "$CandidateDll" "$Baseline116Dll" "$ManagedDir" "$AumfDir"

All four paths must be absolute and exist. Use the preserved **1.1.6**
DLL as baseline and the exact installed game/framework dependencies. No game
or mod dependency assemblies are bundled.

The harness resolves compiled Core/framework references, checks native Forbidden
access and unreachable refusal paths, and compares unchanged system/bridge IL
with 1.1.6. It also invokes the real mod's Save/Load methods with isolated,
in-memory Core containers to check defaults, schema migration, settings, tracked
identities, and failure history. It never initializes a game or opens a save.

The Hive checks invoke the production transpiler against the installed game's
instruction stream without installing a patch. They verify both anchors,
unchanged native instructions, fail-closed mismatches and replay the inserted
comparisons with test distances. They also check the native fuel-component
classifier and saved preference. To check composition with a known, reviewed
enumeration-only transpiler, optionally append the absolute companion DLL path
and fully qualified transpiler type. This invokes that transpiler offline;
do not point the harness at untrusted code.

These are metadata, instruction replay and persistence checks, **not** live
haul dispatch/completion, inventory side-effect, pathfinding, or UI tests.
