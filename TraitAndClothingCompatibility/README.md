# Trait and Clothing Compatibility Mod (TACC)

Version **1.1.5** — clothing automation and Washing Machine build for Stardeus **v0.15.0-rc344.3226**. Requires AUMF **1.4.0**.

**Validation status:** Version 1.1.5 refreshes the reviewed Core rc344.3226 assembly identities and adds exact recognition of Quantum Storage 1.3.10 equipment-routing hooks. The 52 Core trait references, 12 equipment entries, earlier reviewed optional identities, and all gameplay behavior remain unchanged. Source/binary review and build/static checks are recorded in `COMPATIBILITY-REVIEW-rc344.3226.md`; fresh startup, combined patch-order inspection, gameplay acceptance, and Workshop publication for 1.1.5 remain pending. The user accepted and published 1.1.4; that earlier acceptance does not validate the rebuilt 1.1.5 DLL.

TACC configures each living player colonist's clothing policy from their traits. It uses Stardeus's existing equipment-policy and replacement system, but ranks eligible clothing by the total mood result for that wearer before Core's ordinary item comparison. Its Washing Machine can replace the **Taken from the dead** origin with **Second-hand** after a completed wash job. TACC does not change clothing mood formulas or item quality.

## Default policies

| Wearer | Second-hand | Taken from the dead | Tattered |
|---|---|---|---|
| Thrifty | Allow | Forbid | Forbid |
| Fastidious | Forbid | Forbid | Forbid |
| Neither trait | Forbid | Forbid | Forbid |
| Mind without mental health | No added restrictions | No added restrictions | No added restrictions |

New clothing remains allowed, subject to existing item and quality choices. For TACC-managed beings, eligible clothing is compared by the total mood result of the complete worn loadout. Thrifty beings can therefore prefer second-hand clothing over new clothing when its bonus produces the better result. Optimist/Pessimist adjustments, corpse-origin penalties, tattered penalties, and Core's per-category item-count cap are included. If a mod forces both mutually incompatible clothing traits onto one being, TACC uses the more restrictive Fastidious profile.

## Washing Machine

The **Washing Machine** unlocks with **Storage** research. It occupies 2×2 tiles, draws **5 kW** while washing, and uses the same construction materials as Core's Freezer: four titanium plates, one plastic, one glass, one transistor, and three copper wire.

When **Automatic corpse-origin washing** is enabled on the Policies tab, powered idle machines look for stored corpse-origin clothing. Ordinary hauling, reservation, reachability, power, construction, and occupancy rules still apply. Turning the setting off prevents new automatic orders; manual Wash orders remain available.

Select a loose clothing item to use **Wash** from the item action bar. Multi-select queues as many eligible items as currently idle machines can accept and ignores non-clothing objects. A completed cycle removes **Taken from the dead**, adds **Second-hand**, and preserves the item's quality and unrelated flags. Hats, helmets, suits, clothes, and compatible custom worn slots qualify. Weapons do not.

The input item and wash progress persist through save/load. Removing the machine returns a loaded item without changing its origin. Relocation transfers a loaded item and its progress to the relocated machine.

The machine inherits Core's three standard upgrade slots. Each **Speed Upgrade** adds 15% to the washing rate. Each **Energy Efficiency Upgrade** adds 15% to the efficiency divisor used for active power: one module changes 5 kW to about 4.35 kW, while three change it to about 3.45 kW. Upgrade types can be mixed, take effect during an active cycle, and use Core's normal installation, removal, and save behavior.

Tattered clothing has quality at or below Core's `TatteredQualityThreshold` (40% in the checked build). TACC raises the equipment minimum to the next representable float above that threshold, keeping any stricter existing minimum. Its UI says **Above 40%**; Core's own rounded slider may display 40%. The cutoff excludes exactly 40% without unnecessarily excluding valid 40.x% clothing.

## Reviewed clothing effects

Base mood for one affected item, rechecked against Core **v0.15.0-rc337.3219**:

| Wearer | Second-hand | Corpse-origin | Tattered |
|---|---:|---:|---:|
| Thrifty | +3 | -3 | -3 |
| Fastidious | -8 | -10 | -6 |
| Other traits | -4 | -6 | -3 |

Second-hand, corpse-origin, and tattered clothing now produce **separate effects**, each with its own item count. For a nonzero base, each extra item in that category adds one point in the same direction, capped at two extra points. A corpse-origin item belongs to the corpse category, not also the second-hand category; tatters can apply independently to either. Zero-valued tunables disable that category's effect entirely.

For example, a Thrifty being wearing two second-hand items and one corpse-origin item, with no tatters, gets **+4 second-hand and -3 corpse-origin = +1 total**. Optimist adds +1 to each emitted effect; Pessimist subtracts 1 from each. New clothing emits no origin effect, and minds without mental health receive none of these mood effects.

Core now also makes Fastidious beings refuse used/corpse-origin gear when selecting equipment. That refusal does not by itself strip already worn items. It still applies if TACC's corresponding restriction toggles are disabled. TACC's existing conservative default policies remain appropriate: Thrifty still dislikes corpse-origin and tattered gear.

## Interface

Click the **clothing icon** in the top bar for TACC. The tooltip identifies the mod.

TACC's tabs and managed scrollbars follow the selected game theme and live theme reloads through AUMF 1.4.0. The existing `AddTabs` and `AddVerticalScroll` calls receive this automatically. Native text and controls retain Core's own theme styling.

- **Traits & log:** reference mood values for Thrifty, Fastidious, Optimist, Pessimist, and the ordinary response, followed by a resizable scrolling diagnostic log. Recheck, copy, and clear controls are provided.
- **Policies:** master enable switch, automatic washing toggle, a saved **Allow forbidden loose equipment** control, summaries of wearer-mood-first comparison and empty-slot acquisition priority, and separate Second-hand, Corpse-origin, and Tattered controls for Thrifty, Fastidious, and other traits. Includes measured duration of the last policy pass and a restore-defaults control.
- **Colonists:** searchable, scrolling list with each being's traits/profile, management status, and opt-out. Large colonies use 40-being pages to keep UI rebuilding bounded.

The log displays:

> This log is present for debugging purposes. A mod update may, or may not, be provided based on the contents of the log.

It records newly discovered, modified, or removed trait definitions; colonists gaining/losing traits; manual overrides; policy-range conflicts; and detected Core/clothing-patch changes. Changed definition entries include changed field names and compact before/after values. New trait entries include their current name and description. Entries are also written to Player.log with `[TACC]` prefixes.

Every comparison uses the **52-trait validated Core reference catalog shipped inside TACC**, the checked equipment-effect values, and the checked Core assembly identities. A save's last-seen catalog is never treated as approval. Translation/name changes can generate entries as well.

Expanded Traits remains optional. When either exact reviewed Expanded Traits 0.1.3 or 0.1.4 DLL and the unchanged reviewed set of 13 trait-definition files are loaded, TACC recognizes those static traits, the species-driven `ExpandedTraits.PetAffinity.*` family, and the reviewed Cat Person/Dog Person rarity normalization. An installation without Expanded Traits receives no missing-trait warning. A changed DLL, definition set, static trait field, generated-trait structure, pet-family rarity, Minimalist equipment patch, or sleep-mood patch becomes unvalidated again.

The optional static reference stores fingerprints of decoded string values rather than their authored text. Numeric reference values and JSON structure retain the existing comparison rules. This repository privacy change does not refresh the reviewed Core or optional-mod fingerprints.

Core's `SerializableProperty.IsUsed` flag is excluded from the comparison because a successful configuration read changes it; it is usage bookkeeping, not a trait value. All actual property values remain compared. This resolves the 17 false trait warnings in the supplied balance-update log without treating a real modifier change as validated.

**Clear diagnostic log** clears history and immediately checks again, even while paused. Any currently unknown trait, modified/missing validated trait, unknown/changed equipment-effect value, changed checked assembly, or unvalidated external patch returns to the log. Repeated observations and save/load never validate a finding. Only a TACC update containing a reviewed comparison can add that validation; simply increasing the version does not accept anything.

The scrolling log shows **current unvalidated findings** first, separately from history. Unchanged scans keep each finding visible without repeatedly writing it to Player.log. Clearing or loading reports current findings again; old duplicate copies are replaced. The newest **200 history entries** survive save/load. Current findings remain visible even when that history limit is reached. If a change is removed or the checked value restored, its warning leaves the current list and remains only in history. Copy includes both sections and their status.

Unknown trait behavior is not inferred from its name or tooltip. Known rules continue to apply; the log explicitly labels unfamiliar entries as unknown to TACC. Equipment comparisons cover reflected constants in ItemOwnership/WornItemMood, including the count cap, and clothing tunables including the complete `WornGear` family and the tattered cutoff. The reviewed equipment catalog has twelve entries: eight mood tunables, the count cap, the quality cutoff, and two provenance trade-price multipliers. Patch checks cover ownership methods (including the grouping/pricing helpers), worn-item mood methods, equipment-need methods, equipment-policy filtering, Core's best-item comparison, both equipment-search actions, the forbidden-marker getter, the tattered property, and the inspected mental-effect creation/application methods. This release recognizes only the exact reviewed Quantum Storage 1.1.12/1.3.0/1.3.5/1.3.10 routing hooks and Expanded Traits 0.1.3/0.1.4 Minimalist and sleep-mood hooks; other external patches remain flagged. Changed assembly fingerprints request a review; they do not prove a clothing formula changed. TACC cannot identify every possible equipment effect implemented by arbitrary code elsewhere in another mod.

Core rc294.3170 adds `TradeSecondHandPriceMul = 0.5` and `TradeDeadMansPriceMul = 0.25`. These multiply the trade price for the applicable clothing-origin category; they are not mood values or trait modifiers. New/untracked items use a multiplier of 1, and corpse-origin takes precedence over second-hand when assigning the category. Other pricing factors still apply. TACC observes these values because its ownership-term scan includes them, but does not change prices or use them to choose a clothing policy. Future changes to either value remain unvalidated after clearing or reloading.

## Policy ownership and player choices

- TACC changes only the ownership restrictions and, when selected, the minimum quality of **non-manual worn slots**. Core currently marks Hat, Helmet, Clothing, and Suit as worn; compatible custom worn slots are discovered from the being's inventory.
- For a TACC-managed worn slot, the candidate producing the highest total clothing mood across the complete equipped loadout wins. Thrifty beings value their second-hand bonus; other beings normally favor avoiding second-hand penalties. Corpse-origin and tattered effects count independently. Equal-mood candidates return to Core's normal comparison.
- Weapon choices, colors, specific-item filters, maximum quality, stricter minimum quality, and replacement settings are retained. Existing manual slots stay manual.
- Existing global/species settings for fields TACC does not manage are incorporated during later checks. Original individual overrides remain authoritative for those fields.
- If the player or another mod edits a TACC-managed individual policy, TACC automatically opts that being out and keeps the edited policy. Re-enable them in the Colonists tab when desired.
- An explicit opt-out or disabling TACC restores the prior policy when the current policy still matches TACC's last applied version. Later manual edits are not overwritten.
- Minds without mental health, guests, non-player beings, dead beings, and beings without a humanoid clothing inventory receive no new TACC restrictions. A managed mind that stops qualifying has its unchanged TACC-owned override restored.
- If an existing maximum quality is too low to exclude tatters, that explicit range is preserved. The affected slot is reported in the UI/log instead of silently creating an impossible range.

Individual policies belong to minds in Core, so TACC tracks its prior settings and opt-outs by mind ID. A mind transfer triggers reevaluation for the destination body's clothing slots and current mental-health eligibility.

## Timing and what to expect

After loading a new game or save, TACC configures the colony in one pass and commits policy changes once. The measured policy duration is available in the UI and the initial `[TACC]` Player.log message. Equipment searches and hauling are later Core work and are not included in that duration.

Being-added, mind-installed, trait-added/removed, and equipment-policy notifications request a coalesced check. A ten-second real-time fallback while the simulation advances covers other changes such as recruitment/faction transitions. Traits, equipment-effect values, and patches are checked about once per real-time minute while advancing. **Recheck traits and policies** and **Clear diagnostic log** run explicit comparisons even while paused. Assembly files are fingerprinted after loading and on either explicit check; periodic checks reuse those fingerprints. There are no continuous paused-game scans.

Existing incompatible gear follows the slot's native replacement action. Core's default **Keep until replaced** allows it to remain worn until suitable replacement equipment is available. Existing **Unequip now** choices retain Core's life-support safeguards. TACC does not forcibly strip colonists or create replacement equipment.

Core normally gives a partially empty equipment loadout a priority-2 personal goal. A naked being gets priority 5, but that goal still waits behind ordinary work with the same interrupt level. For TACC-managed beings, an empty automatic worn slot becomes priority 5. A completely naked being can interrupt ordinary work after Core's existing grace period when compatible clothing is already available. Improvements to filled slots keep Core's low priority, so wardrobe optimization does not interrupt normal jobs.

Core normally ranks eligible equipment by market value multiplied by quality. Clothing mood is only a tiebreaker when those scores are within five percent, which is why a rare second-hand costume can beat a new uniform with the same temperature modifiers. For TACC-managed worn slots, TACC compares the complete outfit's mood result first. If two candidates have the same mood result, Core's value-and-quality comparison decides between them. With reviewed Expanded Traits installed, its Minimalist ranking runs only after TACC finds an equal mood result. Policy filters still decide eligibility before either comparison, so item, color, quality, and ownership restrictions remain authoritative.

Core normally excludes loose objects carrying the red forbidden marker from both ordinary and **Best Available** equipment searches. TACC's **Allow forbidden loose equipment** setting is enabled by default for managed colonists. It affects equipment selection only: the marker remains, ordinary hauling still respects it, and turning the setting off restores Core's exclusion. Core's separate **Forbid Dropped Weapon** policy controls whether an automatically dropped weapon receives that marker; it does not govern loose clothing.

Thrifty colonists can choose second-hand clothing ahead of new clothing when the second-hand bonus improves their total mood result. They still avoid a candidate when its combined corpse-origin or tattered penalties make the complete loadout worse. Core rc316 now maintains worn-gear mood as source-scoped permanent effects. Removing or replacing gear updates those effects on Core's next gear-mood pass instead of leaving the previous two-hour entry active. The numerical mood rules used by TACC's comparison are unchanged.

## Installation and removal

Requires the separately installed **Auto Utilities Mod Framework 1.4.0**. TACC declares the dependency and uses the four-argument compatibility handshake with its own version **1.1.5** and tested framework level **1.4.0**. Expanded Traits and Quantum Storage are optional. No AUMF, Core, Unity, Harmony, Newtonsoft, Expanded Traits, or Quantum Storage DLL is bundled.

The installed framework's [API usage guide](../AutoUtilitiesModFramework/USAGE.md) and [theme guide](../AutoUtilitiesModFramework/THEME_TESTING.md) confirm that these existing helpers retain their signatures and need no consumer UI migration. TACC does not call the new optional custom theme bindings or state-card APIs, so no new feature gate is needed. Its native pooled controls should not receive extra theme bindings.

Install the `TraitAndClothingCompatibility` folder under Stardeus's local `Mods` directory, then fully restart Stardeus and enable TACC in the mod list if necessary. The game must load TACC's DLL on startup.

The release manifest has `SteamWorkshop: true` and contains the formatted Workshop description. `ModCover.jpg` is the square cover titled **Traits and Clothing Compatibility**, matching the purple spacecraft style of the companion mods. The clothing uses a friendly cartoon style with simplified tears and dirt beneath the red prohibition symbol. Artwork preparation details and the generation/edit prompts are retained in `.vscode/COVER-PROMPT.md`.

To remove TACC and restore its prior policy settings, first **turn off Enable TACC while the save is loaded**, then save and remove/disable the mod. Removing TACC without that step leaves the ordinary Core policies it last assigned in the save; those can still be edited through Core's equipment-policy UI.

TACC stores its settings, originals, ownership snapshots, and diagnostic history in its own versioned game-system save record. It does not edit existing save files externally.

## Source/build and validation

Source project: `.vscode/mod.csproj`; source: `.vscode/Code`; embedded validated references: `.vscode/Resources/BaselineTraits.json`, `BaselineExpandedTraits.json`, and `BaselineEquipment.json`; runtime library: `Libraries/TraitAndClothingCompatibility.dll`. Source and automated checks remain repository development data. The curated Workshop package excludes `.vscode`, `Tests`, and generated build outputs.

Build with the installed Stardeus managed assemblies and the exact installed AUMF DLL. `StardeusManagedPath` and `AumfDll` MSBuild properties can override reference locations. Build-only references use `Private=False`.

The user confirmed the initial **0.1.0** build working on **2026-09-05**. Versions **0.1.1** and **0.1.2** passed 56 automated checks and clean builds; AUMF's installed theme guide separately records successful TACC theme testing. Version **0.1.3** passed **64 automated checks**, including actual Core mood resolvers, Fastidious refusal, zero-valued effects, all eight tunable changes, and the 50-trait comparison before/after Core reads the 17 modifier definitions. The build completed with **0 warnings and 0 errors** against the exact installed AUMF 1.4.0 DLL.

On **2026-09-06**, the user confirmed the balance update working and supplied an in-game screenshot showing **0 current unvalidated findings**. This accepts 0.1.3 as the behavior baseline promoted to **1.0.0**. The 1.0.0 promotion changes release metadata, documentation, and cover artwork; policy logic, the reviewed reference catalogs, fingerprints, and save schema remain the accepted versions. `RUNTIME-CHECKS.md` records that acceptance and retains the checklists for future maintenance; it does not claim every checklist item was individually retested.

The historical **1.0.0 Release build** completed with **0 warnings and 0 errors** against the then-installed AUMF 1.4.0 DLL. Direct inspection confirmed assembly/file versions **1.0.0.0** and product version **1.0.0** (DLL SHA-256 `60E54A66C5858FF41F1A24099BDAD1796F71DF830E34A57FE82EF6BBCBD25B40`). All 16 source/reference/test files retained the accepted contents, with only the reported mod-version constant changed; both embedded catalogs matched their source files. The existing 64 checks belonged to the accepted 0.1.3 behavior review and were not rerun for that metadata/artwork promotion.

On **2026-09-17**, the **1.0.1** compatibility review checked the installed rc294.3170 compiled clothing, ownership, policy, mental-effect, and related trade-pricing code. All 50 trait snapshots and the existing ten equipment values remain unchanged. Only the two newly reviewed trade multipliers and the two reviewed assembly fingerprints were added/updated. The Release build and test build completed with **0 warnings and 0 errors** against the current installed dependencies; **69 automated checks passed, 0 failed**. The checks reproduce all four supplied findings against the previous reference and produce zero against the updated reference. They also confirm later price-value changes still reappear after clearing. See `COMPATIBILITY-REVIEW-rc294.3170.md`.

The user then confirmed **1.0.1 validated in-game** on **2026-09-17**. Their screenshot shows the **rc294.3170** reference header and **0 current unvalidated findings** after clearing. This is the accepted compatibility baseline. The isolated runner cannot exercise the game's Harmony runtime; the user's observed result supplies the in-game acceptance.

The **2026-09-06** balance review accounts for all 34 findings in the supplied log: 17 usage-flag false positives, seven old constants replaced by eight tunables, and two changed assembly fingerprints. All 50 trait definition snapshots matched the earlier reference before excluding the bookkeeping field. The new reference changes only that normalization, the reviewed equipment entries, and the two inspected assembly identities. See `BALANCE-REVIEW-rc283.3153.md` for the values and evidence. Future differences remain unvalidated until separately reviewed. The game's Harmony runtime cannot be exercised inside the separate .NET 10 runner; the user's clean in-game result supplies the runtime acceptance for this baseline.

For maintenance, review the changed definitions and applicable clothing/mood code before editing the validated references or fingerprint constants. Do not regenerate a reference merely to silence a warning. Saved observations must never become a validation source.

On **2026-09-19**, the **1.0.2** review rechecked the installed rc296.3172 compiled clothing, ownership, equipment-policy, mental-effect, trait-loading, and related pricing paths against the recorded rc294.3170 behavior. All **50 trait snapshots** and **12 equipment entries** match the shipped references; both reference JSON files are unchanged. Only the reviewed Core version and two assembly fingerprints advance. Release and test builds completed with **0 warnings and 0 errors**, and **70 checks passed, 0 failed**. A regression reproduces the supplied two assembly warnings against the published reference, confirms they return after clearing, and reports zero against the reviewed reference. See `COMPATIBILITY-REVIEW-rc296.3172.md`. The user subsequently confirmed in-game validation and Workshop publication, making 1.0.2 the accepted baseline in maintenance mode.

On **2026-09-23**, the **1.1.0** review checked Core rc301.3177 before adding the Washing Machine. All 50 trait snapshots and 12 equipment values remain unchanged. The targeted compiled clothing, ownership, equipment-policy, mental-effect, and trait-definition types are byte-identical to rc296; the assembly changes are outside the reviewed clothing behavior. TACC advances the two reviewed assembly fingerprints and adds the Washing Machine feature described above. Clean builds and **77 automated checks** passed, followed by user in-game validation. The **2026-09-24** maintenance rebuild adds the scoped forbidden-equipment search setting and wearer-mood-first clothing comparison, and passes **82 automated checks**. Those paths were subsequently accepted in game as part of 1.1.1. Workshop publication remains unconfirmed.

On **2026-09-24**, the **1.1.1** review checked Core rc302.3178. All 50 trait snapshots, 12 equipment values, and the targeted clothing, policy, selection, and equipment-need implementations match rc301. The supplied two assembly findings are reproduced against the prior reference and resolved only by the reviewed rc302 hashes. The update also raises empty managed worn slots to priority 5 and permits a completely naked managed being with compatible clothing available to interrupt ordinary work after Core's grace period. Clean builds and **86 automated checks** passed. The user subsequently confirmed zero current findings after clearing, forbidden loose-equipment pickup, wearer-specific mood ranking, and much faster empty-slot acquisition. See `COMPATIBILITY-REVIEW-rc302.3178.md`.

On **2026-10-03**, the **1.1.2** review checked Core rc316.3193 and Quantum Storage 1.1.12. All 50 trait snapshots and 12 equipment values remain unchanged. Core moved worn-gear mood from refreshed timed entries to source-scoped permanent effects without changing the clothing values, category counts, or wearer adjustments. Equipment search gained blacklist feedback and cooldown backoff while retaining the methods TACC patches. TACC now monitors the permanent-effect path and accepts only the four exact reviewed Quantum Storage context hooks, including their module identity and Harmony ordering. A changed or rebuilt Quantum Storage assembly becomes unknown again. Clean production/test builds and **87 automated checks** passed. In-game diagnostic-clear validation remains pending. See `COMPATIBILITY-REVIEW-rc316.3193.md`.

On **2026-10-10**, the **1.1.3** review checked Core rc324.3201, Quantum Storage 1.3.0, and optional Expanded Traits 0.1.3. The 50 Core traits, 12 equipment values, and eleven targeted clothing/equipment types remain unchanged from rc316; only the two reviewed Core assembly identities advance. Quantum Storage's equipment hooks retain their scoped nearest-endpoint behavior while adding Storage Relay support elsewhere. TACC now recognizes the exact reviewed Expanded Traits catalog only when that DLL and definition set are present, including dynamic pet affinities and pet-family rarity normalization. TACC's mood-first comparison explicitly runs before Expanded Traits' Minimalist ranking. Clean production/test builds and **90 automated checks** passed. In-game diagnostic-clear validation remains pending. See `COMPATIBILITY-REVIEW-rc324.3201.md`.

On **2026-10-19**, the **1.1.4** review checked Core rc337.3219, Quantum Storage 1.3.5, and optional Expanded Traits 0.1.4 while retaining the exact reviewed 0.1.3 compatibility route. Core renamed the loose-item forbidden property from `IsHaulingForbidden` to `IsForbidden`; TACC now patches and reads that current property while keeping the same equipment-search-only override. Initialization after the AUMF handshake publishes the system only after successful setup. On failure it clears the active instance and attempts listener, UI, and Harmony cleanup independently before rethrowing the original error. The Core reference now includes Stoic and Wimp and the reviewed Enlightened threshold changes; its other 49 trait snapshots and all 12 equipment values are unchanged. The production build passed with **0 warnings and 0 errors**, and the public optional-reference harness passed **187 assertions**. The user subsequently confirmed 1.1.4 working in game with a clear diagnostic log on the same date. Runtime acceptance is recorded for that installation; TACC is back in maintenance mode. See `COMPATIBILITY-REVIEW-rc337.3219.md`.

## Changelog

### 1.1.5

- Revalidated the relevant current Core clothing, provenance, equipment-policy and retrieval contracts for v0.15.0-rc344.3226; refreshed only the two reviewed assembly identities and reference build label.
- Added the four exact reviewed Quantum Storage 1.3.10 equipment-search context hooks, retaining earlier reviewed Quantum and optional Expanded Traits identities. Unknown builds, owners, priorities, ordering, traits and effects still warn.
- Retained all 52 trait references and 12 equipment entries, clothing policies, saved state, opt-outs, manual choices, forbidden-equipment behavior, washing and artwork.
- Rebuilt as 1.1.5 against installed Core and AUMF dependencies. Static/automated checks are separate from fresh startup, gameplay validation and Workshop publication, which remain pending with the user.

### 1.1.4

- Updated the forbidden loose-equipment integration for Core's current `StorableObjComp.IsForbidden` property without clearing the marker or changing ordinary hauling.
- Guarded post-handshake initialization: publish the system after successful setup, clear its handles on failure, and attempt every cleanup independently while preserving the original error.
- Reviewed all 52 Core traits against Core v0.15.0-rc337.3219: added Stoic and Wimp and updated Enlightened breakdown thresholds. The other 49 traits and all 12 equipment values match the prior reference; clothing policy defaults are unchanged.
- Added exact recognition for Quantum Storage 1.3.5 while retaining its reviewed 1.1.12 and 1.3.0 snapshots.
- Added exact optional recognition for Expanded Traits 0.1.4 while retaining 0.1.3, including both versions' reviewed Minimalist and sleep-mood hooks. Missing Expanded Traits remains a clean optional path; unknown builds still warn.
- Passed a clean production build and all 187 public optional-reference assertions. The user confirmed 1.1.4 working in game with a clear diagnostic log on 2026-10-19; returned to maintenance mode.

### 1.1.3

- Revalidated all 50 Core traits, 12 equipment values, and the relevant clothing, mood, policy, and equipment-search paths against Core v0.15.0-rc324.3201.
- Updated the reviewed `Game.dll` and `Game.Constants.dll` identities after reproducing the supplied rc324 findings against the 1.1.2 reference.
- Added optional, identity-bound recognition for Expanded Traits 0.1.3: 13 static traits, generated pet affinities, Cat/Dog/pet-family rarity normalization, and the exact Minimalist equipment hook. Expanded Traits remains unnecessary and produces no missing-trait warnings when absent.
- Ordered TACC's wearer-mood comparison before Expanded Traits' Minimalist equipment ranking; Minimalist preference still decides equal-mood choices.
- Validated the exact four Quantum Storage 1.3.0 equipment-search context hooks while retaining the reviewed 1.1.12 snapshots.
- Passed clean production/test builds and all 90 automated checks. Runtime diagnostic-clear validation remains pending.

### 1.1.2

- Revalidated all 50 traits, 12 equipment values, and the relevant clothing, mood, policy, and equipment-search paths against Core v0.15.0-rc316.3193.
- Updated the reviewed `Game.dll` and `Game.Constants.dll` identities after the supplied rc316 findings.
- Added diagnostic coverage for Core's new source-scoped permanent worn-gear mood path.
- Validated the exact four Quantum Storage 1.1.12 equipment-search context hooks used for nearest-endpoint routing. Any changed owner, target, patch method, module, priority, or ordering is reported again.
- Passed clean production/test builds and all 87 automated checks. Runtime diagnostic-clear validation remains pending.

### 1.1.1

- Revalidated the 50 traits, 12 equipment values, and targeted clothing/equipment code against Core v0.15.0-rc302.3178.
- Updated the reviewed `Game.dll` and `Game.Constants.dll` identities after reproducing the supplied two findings against the rc301 reference.
- Raised empty automatic worn slots to priority 5 for TACC-managed beings.
- Allowed a completely naked managed being to interrupt ordinary work after Core's grace period when compatible clothing is available; filled-slot improvements retain Core's low priority.
- Passed clean production/test builds and all 86 automated checks. The rc302 diagnostic result, forbidden-item pickup, wearer-specific mood ranking, and faster empty-slot acquisition were validated in game.

### 1.1.0

- Added a 2×2 Washing Machine unlocked by Storage research, using 5 kW while active and the Freezer's construction materials.
- Added automatic stored-clothing washing with a saved global toggle, plus manual single-selection and multi-selection Wash actions.
- Added native Speed and Energy Efficiency Upgrade support through the machine's three inherited Core upgrade slots.
- Completed washes replace Taken from the dead with Second-hand while preserving quality and unrelated item flags.
- Restricted washing to worn-slot clothing; no weapon-restoration behavior is included.
- Added a saved, default-on **Allow forbidden loose equipment** setting for TACC-managed colonists. It affects equipment searches without clearing the red marker or changing ordinary hauling.
- Added wearer-specific clothing comparison for managed colonists. The complete loadout's mood result takes priority, so Thrifty colonists can prefer beneficial second-hand clothing; equal-mood candidates use Core's normal value-and-quality comparison.
- Revalidated the clothing compatibility reference against Core v0.15.0-rc301.3177 without changing the 50 trait or 12 equipment-value references.
- Passed clean production/test builds and all 82 automated checks. The Washing Machine build was validated in game; the two equipment-selection paths were later accepted with 1.1.1.
- Moved the development-only test suite to staging and excluded the entire `Tests` folder from the live mod and Workshop package.

### 1.0.2

- Revalidated the relevant clothing and ownership code against Core v0.15.0-rc296.3172 and refreshed the two reviewed assembly fingerprints.
- Preserved the trait/equipment reference values, clothing rules, UI, saved settings, and unresolved-finding behavior.
- Added a regression for the two reported assembly findings and clearing behavior; all 70 checks pass.
- Updated version metadata and compatibility/maintenance documentation; AUMF remains external at required level 1.4.0.

### 1.0.1

- Revalidated clothing and ownership behavior against Core v0.15.0-rc294.3170 and refreshed the two reviewed assembly fingerprints.
- Added the reviewed second-hand (0.5) and corpse-origin (0.25) trade-price multipliers to compatibility comparisons.
- Retained all trait references, clothing mood values, default policies, player settings, and unresolved-finding behavior.
- Added five compatibility checks; all 69 tests pass. Updated release metadata and maintenance documentation.

### 1.0.0

- Promoted the user-accepted 0.1.3 behavior and clean compatibility comparison to the first public release.
- Synchronized manifest, project, assembly, and reported mod versions; retained the AUMF 1.4.0 requirement.
- Enabled the Workshop manifest flag and added the formatted feature description and themed cover artwork.
- Updated validation records and maintenance documentation. Clothing policies, reviewed effect values, diagnostics, and save data retain their accepted behavior.

### 0.1.3

- Revalidated the rc283.3153 clothing balance, including independent origin categories, Thrifty's -3 corpse penalty, and native Fastidious used-gear refusal.
- Replaced the old clothing constants in the equipment reference with eight reviewed tunables; refreshed the reviewed Core/Constants fingerprints.
- Excluded only Core's mutable property-usage flag from trait snapshots; retained comparison of actual values and unknown traits.
- Updated the known-effects UI and added equipment-policy filtering to patch monitoring. Existing policy defaults and save schema remain unchanged.
- Added eight balance/diagnostic regression checks; all 64 tests pass.

### 0.1.2

- Declares tested AUMF compatibility level 1.4.0 and rebuilds against the exact installed framework DLL.
- Documents the automatic theme support for existing tabs and managed scrollbars; no TACC UI migration is required.
- Synchronizes manifest, assembly/project, source version, and installation documentation.
- Keeps clothing policies and persistent unvalidated-findings behavior unchanged. Core reference fingerprints remain anchored to the reviewed build.

### 0.1.1

- Unknown or changed traits and equipment effects remain unvalidated against the shipped reference on every comparison.
- Clear now immediately rechecks and restores current warnings; save/load re-reports unresolved findings without duplicating saved history.
- Added a current-findings section unaffected by history limits, explicit unknown labels, equipment-value comparisons, and broader clothing/mood patch inspection.
- Retained the gameplay-validated clothing-policy behavior from 0.1.0.

### 0.1.0

- Added per-being trait-based clothing policies with the requested defaults.
- Added configurable profile rules, master control, per-being opt-outs, and prior-policy restoration.
- Added load/event/fallback reconciliation through Core's native policy API.
- Added known-effect display, a scrolling persisted diagnostic log, first-run reference comparison, and copy/recheck controls.
