# Scoped contract comparison

`Compare-ModContracts.ps1` compares an explicit selection of methods, fields and type surfaces in two exact managed assemblies. Windows PowerShell 7.2+ and the .NET 10 SDK are required. It reads PE/CLI metadata and never loads or invokes game/mod code. Profiles, builds, canonical texts and reports remain private.

Copy the adjacent example to private storage, supply the exact subject mod root/Id and before/after files, and select only relevant contracts. The subject root must contain a strict `ModInfo.json` matching the configured Id. The game/dependency binaries can be outside that root; they are read-only inputs. Supply exact type names (nested types use `+`) and member names. Ambiguous overloads stay unresolved; `ParameterTypes` can select an exact overload using the canonical signature types (e.g. `Int32`, `String`, `Int32&`, or qualified class/value types).

```powershell
$ProfilePath = Read-Host 'Absolute private comparison profile'
pwsh -NoProfile -File build/ModMaintenance/Compare-ModContracts.ps1 -ProfilePath $ProfilePath
```

`contracts.json` contains the selected statuses, readable canonical text, hashes, input SHA256/MVID/full assembly identities, tool/source hashes and coverage. `summary.json` is the compact index. No file is automatically selected by date. Changed contracts are findings for review; there is no fingerprint approval file and nothing updates a runtime guard.

Method contracts include signature/calling convention/generic shape, authored flags, parameters/defaults/attributes, decoded instructions, exact literals, token identities, locals, stack/init flags and exception regions. Branch offsets become instruction ordinals; short/long branch encodings normalize. Own assembly version/MVID/path and incidental metadata token numbers are excluded from contract text but retained as provenance. Selected external assembly reference identities are retained, including the intrinsic runtime contract. Generated names are preserved. This is conservative structural comparison, not an optimizer or proof of semantic equivalence.

Field contracts cover signature, flags, constants, layout and metadata; dynamic initialization requires selecting its initializer separately. RVA-initialized data fields stay unresolved. Type contracts cover base/interfaces/layout/generics/attributes, ordered field contracts, declared method signatures/flags, explicit override mappings and property/event metadata. A type selection does not recursively compare every method body or nested-type body. Select those methods/types explicitly. Reflection/dynamic-code behavior and referenced implementations are outside the chosen scope.

States are `UnchangedInSelectedScope`, `ChangesFound`, `ComparedWithGaps`, and `Failed`. Missing bodies/members, ambiguous selectors, malformed metadata/operands and unsupported constructs cannot become an unchanged fingerprint. A fatal failure is retained as negative evidence. Reports cap selected contracts and retained text; child output/time are bounded. Comparisons are not complete IL verification: valid decoded structure does not establish stack correctness, patchability or gameplay safety.

Use `Type: Contracts` with the existing validation helper. Current evidence must bind exact artifacts, match subject/context and task boundary, and retain all input/support-source pins. Changed/gapped/failed reports keep `ContractComparison` open. Static comparison cannot satisfy Startup, Gameplay, SaveReload or UserAcceptance.

Run `Tests/Test-ContractComparison.ps1` only after relevant tool changes/failures. Fixtures compile isolated libraries and mutate byte copies for malformed-operand tests; fixture methods are never executed.
