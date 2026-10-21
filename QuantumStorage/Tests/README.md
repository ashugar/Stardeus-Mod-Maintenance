# Material panel compatibility checks

This optional local harness requires .NET 10, the installed Stardeus Managed assemblies, and a locally available Mono.Cecil DLL. It is not a portable CI harness and must not ship in the Workshop payload.

From the repository root, using locally resolved paths:

```powershell
dotnet build QuantumStorage/Tests/MaterialPanelTests.csproj -c Release "-p:StardeusManagedPath=$ManagedRoot" "-p:MonoCecilPath=$MonoCecilPath"
dotnet QuantumStorage/Tests/bin/Release/net10.0/MaterialPanelTests.dll "$ManagedRoot" "$RepoRoot/QuantumStorage/Libraries/QuantumStorage.dll"
```

The harness checks the supplied compiled mod's real preparation guard and row cache. It rejects missing, changed, duplicate, and additional callback fingerprints; checks full-list identity, quantities, sorting, and structural fallback; and inspects native callbacks without invoking them. The reviewed rc342 contract retains the general forbidden-item policy and its inventory-index notification introduced before the accepted rc337 baseline.

Metadata-only Cecil fixtures exercise the compiled canonical fingerprint routine. The benign pair independently shifts field, method, member, type, generic-instantiation and user-string tokens, changes assembly version/MVID/path, proves that raw body bytes differ, and requires equal canonical fingerprints. Separate fixtures require changed fingerprints for literals (including floating-point signed-zero bits), member owners/overloads, field identities/types, closed generic arguments, class/struct/interface kinds, opcodes, branch/switch targets, locals, initialization/stack declarations, signatures, method flags, and exception clauses. A same-named framework type owned by an untrusted assembly must remain distinct. Missing bodies, unresolved tokens, unknown opcodes, truncated operands, branches into operands, unsupported `calli` signatures, and unsupported local or nested member custom modifiers must fail closed. Reflection-only schema tests reject changed field owners, types, accessibility, staticness, readonly/literal flags, and inherited declarations. Fixture methods are never invoked; fixture files are temporary and are not release payload.

These local fixture results do not establish fingerprint parity between .NET 10 and the production Mono runtime. That requires a separate probe of the exact installed Core/candidate binaries on the supported runtime. The tests also do not establish arbitrary semantic equivalence or unchanged behavior in every transitive native helper.

The production guard reads raw signature metadata using Stardeus's existing `System.Reflection.Metadata` library through a non-copying reference. Unsupported signature forms, including modifiers that reflection erases, fail closed. Fixtures cover required/optional modifiers on locals, nested return/parameter/field types, MethodSpec arguments, and generic MemberRef owners. Actual acceptance in the game's Mono runtime still requires a fresh user-controlled game session with the exact candidate.

No Harmony detours are installed, no game process is launched, and no Core gameplay callback runs. The printed hashes identify the exact binaries tested. Unity widget lifecycle, actual clicks, and open-panel frame rate still require user-controlled in-game validation.

Diagnostic regression checks invoke the compiled pure recorder with inert material identities. They cover retained zero-valued keys, same-count identity replacement, quantity-only and identical updates, observation gaps and panel switches, oversized snapshots, exact sample/key/work/call limits, coarse telemetry after detailed capture retires, independent frame/call timing, histogram boundaries, culture-independent output, probe-overhead retirement, and terminal reference release. Nested observer-state fixtures verify that only the matching tab at the active depth can report an outcome. Exception checks require fixed categories, bounded nesting and no access to private or throwing messages; diagnostic fingerprinting must preserve ordinary success and fail-closed results.

One ignored, collectible copy of the supplied candidate DLL replaces only its private diagnostic writer with a deterministic exception. This fault-injection fixture proves that guard logging failure preserves reviewed acceptance and invalid-contract rejection, while regular startup write attempts stop at eight and reserve two records for the actual capture census and state. The production DLL and real logging sink remain untouched. The harness does not install diagnostic hooks or exercise marker discovery, the real sink, sampler duration/output caps, observer callback ordering, Unity frame/layout costs, or gameplay. Those remain separate fresh-session checks; passing this harness does not demonstrate an FPS improvement.

