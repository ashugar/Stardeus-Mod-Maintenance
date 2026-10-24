# TACC 1.1.5 compatibility refresh: Core rc344.3226

Reviewed on **2026-10-24** against **Core v0.15.0-rc344.3226**, **Quantum Storage 1.3.10**, **AUMF 1.4.0** and optional **Expanded Traits 0.1.4**. This is a behavior-preserving reference refresh following the confirmed difference between TACC 1.1.4's reviewed identities and the current installed binaries. The user controls game launch, gameplay validation and Workshop publication.

## Scope and identities

Only the reviewed Core build label, two assembly fingerprints, four additional exact Quantum equipment-search patch snapshots, release metadata and documentation change. Earlier reviewed Quantum and Expanded Traits identities remain recognized. All embedded trait/equipment references, policy and equipment logic, washing, definitions, artwork, settings and save schema retain their existing contents.

| Installed dependency | Reviewed SHA-256 | Module MVID |
|---|---|---|
| Game.dll | `2B8D07A416BA89AA94C681D61B8EAA46CED5E2CD7B1E5CFA8C6D3B4391EB6BE1` | `bf5d4d92-ad65-4f59-a00e-14a764a9eceb` |
| Game.Constants.dll | `BDFC046E6D8F141E341D0BC12ED62E387BC124451E7FACEC04BF80C74A96968C` | `561301e4-8479-4f68-b728-322a04bf788f` |
| QuantumStorage.dll 1.3.10 | `3A2FBBDAA30E47651D751A6D1BAA3A52B881296E2D7850DAB97D615E8183298A` | `037db4c0-32d6-498a-9ee6-9c42fd03c77f` |
| AutoUtilitiesModFramework.dll 1.4.0 | `23A23397D8E23B714768E51645CC59261B7F08CE1D14B5CA9CD6C79BD67A68BD` | `9d7b53c1-437e-43f3-94a6-1f204581f7f8` |
| ExpandedTraitsMod.dll 0.1.4 | `5A9ACA692F52B41D845DECACFAE9EE4CF0886B2D155D0247616C1D2A0BADC46F` | `95e164e0-26dd-451e-984e-11e28c262fd0` |

The exact installed DLLs were inspected directly; preserved old Live source copies and other mods' repository DLLs were not assumed to match them. No other mod is changed. A metadata inventory covered 34 local and two Workshop manifests, with enabled settings and existing-log load evidence. Other mods received no general gameplay compatibility approval.

## Core and reference review

The current ownership/provenance, separate worn-clothing mood categories, equipment policy/refusal, native equipment need/search/retrieval, life-support restrictions, reservations and cleanup paths were reviewed in installed source and selected compiled methods. No behavior defect was demonstrated in these inspected paths.

All **12 equipment reference entries** match the current compiled defaults and Core configuration. All **52 Core trait IDs** are present and their authored fields and normalized English text match the embedded reference within the bounded comparison. A trailing ASCII space in ShortSleeper's raw English description is removed by Core's verified translation cleanup and is not an additional diagnostic change.

These comparisons do not recreate runtime-derived flags, every nested default, selected localization or the complete loaded modded registry. No archived exact rc337 binary was used to claim whole-method equivalence or that all binary differences are metadata-token changes. The compatibility approval is grounded in the reviewed current contracts and scoped comparisons, with fresh runtime acceptance pending.

## Optional integrations

The four newly recognized Quantum snapshots are the prefix/finalizer pairs on `AIActionFindEquipment.Execute(GameState, AIAgentComp, AIGoal, Int64)` and `AIActionFindBestEquipment.Execute(GameState, AIAgentComp, AIGoal, Int64)`, owner `jleas.quantumstorage`, patch class `QuantumObjectPositionRoutingContextPatch`, module MVID above, priority **400**, empty before/after lists. Targets, methods, owner, module, priority and ordering remain exact comparisons. Earlier reviewed 1.1.12/1.3.0/1.3.5 snapshots remain present; no version wildcard or observed-value auto-approval is introduced.

Quantum uses a scoped agent context and restores it in a finalizer. Its endpoint selection and temporary storage-owner routing retain native refusal, reachability, identity and matching-goal reservation constraints in inspected paths. TACC's forbidden-equipment scope uses separate state. Static ordering has no contradictory constraint; actual combined runtime order was not observed.

The late optional washing bindings were separately reviewed: `InputData`, `IsInputLoaded` and `LoadObjSpotIdx` are readable properties with the expected types, and `LoadObj`, `TryOrderWash` and `FinishWash` signatures match. Missing TACC remains an optional path. Binding errors disable optional transfers; both callbacks honor that disabled state. Sequential registration is not claimed to be transactional. Successful native orders, object identity, matching reservations, normal completion and failure unwinding remain required. This reference update does not change that integration.

Expanded Traits' exact installed DLL and 13-definition aggregate (`94A39E354BE0C29C0A3B9EF31B6DB12C27D644A8C4F3B3107CA144177F690DE2`) remain recognized. TACC's mood comparison still precedes Minimalist and yields on equal mood. AUMF retains the exact Boolean four-string registration API used by TACC; the required framework level remains 1.4.0 and the framework stays external.

## Validation and acceptance

The Release build against the exact installed Core and AUMF completed with **0 warnings and 0 errors**. Assembly/file versions are **1.1.5.0** and product version is **1.1.5**. A neutral source path map is used for embedded debug documents; no game/framework DLL is copied into the payload.

The public optional-reference harness passed **187 assertions** with its portable dependency. The local NuGet advisory-data lookup was unavailable; this did not prevent the test build or execution. A separate independent run with the installed Newtonsoft library also passed all 187 assertions.

An isolated runner exercised the actual candidate's comparison and diagnostic-journal methods: **19 checks passed**, covering recognized current identities, retained optional snapshots, changed Core hashes, unknown patch target/method/owner/MVID/priority/order, unknown traits/equipment values, warning return after clear, repeated-history deduplication and Expanded Traits absent/present recognition. It does not install patches or invoke gameplay callbacks. Candidate lookup/injection checks passed for all **six TACC patch classes / seven class-target entries**.

Independent metadata/IL comparison retains all **369 methods** and all three embedded references. Outside the catalog initializer's four added snapshot strings, method instructions, local signatures and exception regions are identical after normalizing only the intended release/build/hash literals. This supports the behavior-preserving scope; it does not substitute for current combined gameplay. Package and installed-file parity are checked separately from these build/static results.

Fresh startup, combined Harmony registry/order after late binding, diagnostic clear, clothing/provenance/mood choices, player opt-outs/manual policies, forbidden switch, Quantum/Relay retrieval, completed/cancelled washing and save/reload remain user-run acceptance. Optional-dependency-absent execution remains separately pending where safely supported. `RUNTIME-CHECKS.md` lists the current steps. Unknown traits, values, assemblies and external patch identities must continue to warn after clearing.

TACC **1.1.4** was accepted and published by the user. TACC **1.1.5** has no fresh startup/gameplay acceptance or Workshop publication confirmation at preparation time. Reverting this reference-only release does not require a save migration; deployment rollback still must preserve later file edits.
