# TACC 1.1.6: Core v0.15.12.3259 compatibility review

Reviewed **8 November 2026**. The current native log identifies Core **v0.15.12.3259**; assembly versions are not used as a substitute for that full game build. The scoped baseline helper deliberately retains its unknown full-build field, with the native log as separate attributed evidence.

## Current Core and intended change

| Assembly | Reviewed SHA-256 |
|---|---|
| Game.dll | `FADEF88BAFEA06FA2C2F0B0FBA59E4DFCCC944DA221010E7ABFEE5823DF4F748` |
| Game.Constants.dll | `6C42D6A090573D55A500863B56350F6A58F10ED667AD4399F0091303F035DEE8` |

Selected current compiled ownership, worn-clothing mood, policy/refusal, both equipment-search, reservation cancellation, retrieval and equip-cleanup contracts were reviewed against installed source. Separate provenance/tattered categories, the count cap of two, native eligibility and reservation ownership remain suitable in the inspected scope. No gameplay defect was established.

Core itself changed three second-hand mood tunables. TACC already reads the native values for ranking and displays native effects; this update changes their recorded diagnostic reference only:

| Native reference | Previous | Current |
|---|---:|---:|
| Ordinary second-hand | -4 | -1 |
| Thrifty second-hand | +3 | +1 |
| Fastidious second-hand | -8 | -3 |

The current compiled initializer corroborates these and the other eight tunable entries; `WornItemMood` supplies the remaining count-cap entry. The other **nine equipment reference entries** remain unchanged. All **52 Core authored trait fields and normalized English values** match the existing reference, including verified native ASCII-space translation cleanup. Trait resources are not regenerated.

This is not a TACC rebalance: no native tunable, gameplay algorithm, policy default, manual choice, opt-out, forbidden-equipment scope, washing behavior, definition, artwork or saved schema is changed. The release changes reviewed build/hashes, the three recorded native values, version metadata and documentation. Optional approval changes, if supported by separate review, are documented below.

## Optional integrations

Quantum Storage 1.3.10 and AUMF retain their previously reviewed DLL identities; their existing exact approvals and framework requirement remain unchanged. Current Expanded Traits **0.1.5** is separately reviewed: DLL SHA-256 `03321A6FFE32E79ADC589EADE028549444FD1397FD31404A986E4F8FF576BBA3`, module MVID `6ef2abe7-89d9-444b-b630-37b6bffc6572`. Its 13-definition aggregate remains `94A39E354BE0C29C0A3B9EF31B6DB12C27D644A8C4F3B3107CA144177F690DE2`.

Current compiled Minimalist ranking/equal-rank fallback, manual-policy no-hat refusal, specific sleep-mood reasons and generated pet Mental/empty-effect/family-rarity normalization remain compatible with TACC's existing acceptance rules. The gameplay Harmony owner remains `ExpandedTraitsMod.Runtime.Bootstrap`; TACC's existing before-order is preserved. Only its exact DLL hash and the two exact Minimalist/sleep snapshots are appended (priority 400, empty before/after). Earlier optional identities and definition guards remain intact; no wildcard or automatic approval is introduced. Actual combined live order and optional-dependency-absent gameplay remain pending.

## Validation boundaries

The scoped comparisons do not reconstruct the complete loaded modded registry, runtime-derived flags, every nested default or selected localization. No old exact Core binary is used to claim whole-method equivalence or token-only drift. Current source/compiled contract review supports the stated maintenance scope, not universal compatibility.

The Release build against the exact installed dependencies passed with **0 warnings and 0 errors**. The public optional-reference harness passed **188 assertions**, including the new exact 0.1.5 hash and continued unknown-hash refusal. An isolated runner exercised actual production comparison/journal methods: **20 checks passed**, including all 12 current compiled equipment defaults matching the resource, current Core/Quantum recognition, optional absent/present recognition, unknown hash/patch owner/target/method/MVID/priority/order refusal, unknown traits/effects and warning return after clear. These tests do not create a running game registry or install gameplay patches. The scoped version helper reports all five selected manifest/project/assembly/file/product surfaces consistent with **1.1.6** (assembly/file **1.1.6.0**).

Build and automated/static results are separate from runtime acceptance. Fresh startup, diagnostic clear, actual combined Harmony ordering, clothing/provenance choices, manual policies/opt-outs, forbidden setting, Quantum/Relay retrieval, completed/cancelled washing and save/load remain user-run. Optional-dependency-absent gameplay remains separately pending where safely supported. Unknown hashes, values, traits, owners, module identities, methods and patch ordering must still warn after clearing.

The prior TACC 1.1.5 installation was user-confirmed working with a clear log. That acceptance does not transfer to the rebuilt **1.1.6** DLL. No game launch/stop, gameplay test or Workshop publication is performed by this task.
