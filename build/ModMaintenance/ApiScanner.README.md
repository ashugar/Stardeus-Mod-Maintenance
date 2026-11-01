# Scoped API and Harmony metadata diagnostics

`Test-ModApi.ps1` reads explicitly selected managed DLLs and dependency files. It uses the framework-only .NET 10 metadata engine; it never loads assemblies for execution, invokes patch providers, installs Harmony patches, probes dependency directories, or controls the game. PowerShell 7.2+ and a locally installed .NET 10 SDK are required. No package download is needed.

Copy `ApiScanner.profile.example.json` into private storage outside the checkout and protected game/player locations. Replace every placeholder with verified absolute local paths. `Subject.Root` must contain a matching `ModInfo.json`. List every subject DLL in `Assemblies`, exact dependency DLLs in `Dependencies`, and only relevant assembly simple names in `ReferenceAssemblyNames`. No automatic dependency discovery occurs. Duplicate assembly simple names are refused, even when their bytes match. References must match supplied dependency assembly identities exactly; hashes and MVIDs identify the actual inspected bytes separately.

```powershell
pwsh -NoProfile -File build/ModMaintenance/Test-ModApi.ps1 -ProfilePath $PrivateProfile
```

The returned compact object points to the full private `api-audit.json`, source/build evidence, and `summary.json`. `-ReportOnly` retains failures without throwing the final failure gate; it does not make the outcome pass. Reports and profiles can contain private paths/member names and must not be committed or uploaded automatically.

## Coverage and limits

The scanner checks selected TypeRef and non-generic MemberRef signatures against directly declared types, methods and fields in the exact supplied dependencies. Signatures retain return types, byrefs, modifiers, generic arity, calling convention and class/value-type distinctions. An inherited candidate, forwarded type, generic member owner, unsupported scope or missing resolution remains explicit review work. A changed field becoming a property cannot silently count as a resolved field.

Fixed `HarmonyPatch` constructors support target type/name, explicit parameter types, and normal/getter/setter/constructor/static-constructor method kinds. Name-only overload ambiguity is a finding. Direct conventional or attributed prefix/postfix/finalizer arguments are checked by original parameter name, exact primitive/value signature, result and directly declared injected field; exact instance type is recognized. Reference-type assignability, other special Harmony parameters, inherited fields and unsupported attribute forms remain gaps. Patch argument maps, access transformations, runtime preparation conditions, generic targets and arbitrary Harmony attribute combinations are not certified.

Hooks must be static and expose complete, unique parameter metadata. Prefix return types must be `void` or `bool`; non-void postfix and finalizer return contracts remain gaps, as do method-level targets without a recognizable hook role. The scanner refuses oversized output rather than reporting partial success: at most 20,000 result rows, fewer than 10,000 gaps or findings per list, 8,192 characters per identity/diagnostic, and a conservative aggregate text budget. Narrow the profile after a resource-limit failure.

Each fixed patch row retains target `Status` and explicit `TargetStatus` separately from `BindingStatus`: `CheckedSupportedBindings`, `RequiresReview`, `Failed` or `NotRun`. A resolved target without recognized prefix/postfix/finalizer hooks produces a gap and `NotRun`; resolving a target never implies that binding was checked.

Dynamic `TargetMethod`/`TargetMethods`, transpilers, and manual Harmony registration references/declarations are inventoried as gaps. Providers are never executed. These inventories are static clues, not exhaustive control-flow discovery; reflection/string-based registration can escape recognition. Resolving a transpiler's target does not validate its instruction pattern. Harmony owner/order/priority interactions and runtime behaviour require separate review.

`CheckedSupportedScope` means no findings or known gaps in the selected supported metadata scope. It does not establish that every relevant reference or hook was selected, that Harmony accepts the patch at runtime, or that gameplay, saves, startup or publication passed. `CheckedWithGaps` retains omissions; `Failed` retains findings or tool failures. None refreshes runtime guard approvals.

Run `Tests/Test-ApiScanner.ps1` after changing this helper. Its isolated synthetic assemblies are compiled and inspected but never executed. It does not scan the real game or live mods.
