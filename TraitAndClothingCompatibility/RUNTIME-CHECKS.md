# TACC 1.1.4: rc337 build repair and optional compatibility

Version 1.1.4 targets **Core v0.15.0-rc337.3219** and **AUMF 1.4.0**. Core renamed `StorableObjComp.IsHaulingForbidden` to `IsForbidden`; TACC's equipment-search-only override and candidate probe now use the current property. Static comparison reviewed 52 Core traits: Stoic and Wimp are new, Enlightened has changed breakdown thresholds, and the other 49 traits match. The 12 equipment-effect values remain unchanged. The Core trait reference contains only those three reviewed snapshot changes; policy defaults are unchanged. TACC recognizes the exact reviewed Quantum Storage 1.1.12/1.3.0/1.3.5 routing hooks and the exact optional Expanded Traits 0.1.3/0.1.4 definition, Minimalist, and sleep-mood identities. The production build completed with 0 warnings and 0 errors, and the public optional-reference harness passed all 187 assertions.

The automated suite and production source remain repository development data. The curated Workshop package excludes `Tests`, `.vscode`, and generated build outputs.

On **2026-09-23**, the user confirmed: "Validated in-game. Please update documentation as necessary. You can keep the version at `1.1.0`." This accepts the Washing Machine feature build, including the user-approved directional artwork and native Speed and Energy Efficiency Upgrade support, as the local release baseline. Workshop publication has not been reported for 1.1.0. This general acceptance does not claim every scenario below was separately retested.

The **2026-10-19** 1.1.4 build repairs the current Core API break, makes setup failure cleanup transactional, recognizes Quantum Storage 1.3.5, and adds identity-bound optional Expanded Traits 0.1.4 compatibility while retaining the reviewed 0.1.3 path. On **2026-10-19**, the user confirmed: "Validated in-game. TACC 1.1.4 is working and the diagnostic log is clear." This accepts the deployed 1.1.4 build for the reported installation and returns TACC to **maintenance mode**. It does not claim every retained scenario was separately exercised. The **2026-09-24** 1.1.1 build remains the earlier user-confirmed behavior baseline for forbidden loose-equipment pickup, wearer-specific mood ranking, and faster empty-slot acquisition. Items 1 and 6–10 remain retained regression scenarios.

Retained checklist for future maintenance:

1. **Accepted 2026-10-19:** the user confirmed the deployed TACC 1.1.4 build working in game with a clear diagnostic log. For future maintenance, retain startup and diagnostic-clear checks with the reviewed dependencies, plus the Expanded Traits absent-path check. The absent-path and individual regression scenarios were not separately reported in this confirmation. Changed Core or optional-mod identities and behavior must still return as unknown.
2. Complete Storage research. Confirm the Washing Machine appears under Facilities / Comfort, occupies 2×2 tiles, rotates, and costs four titanium plates, one plastic, one glass, one transistor, and three copper wire.
3. Build and power it. Confirm it requests **5 kW only while washing** and its overview distinguishes idle, searching, hauling/waiting, washing, and power problems.
4. Confirm the machine has three upgrade slots and accepts only compatible Core upgrades. Install one Speed Upgrade and confirm the cycle advances 15% faster. Install one Energy Efficiency Upgrade and confirm active demand falls from 5 kW to about 4.35 kW. Test a mixed set, uninstall one module, and save/reload with upgrades installed; Core should restore the same modules and effects without resetting active wash progress.
5. With **Automatic corpse-origin washing** enabled, store an unworn corpse-origin hat, helmet, suit, or clothing item. Confirm an ordinary hauling job takes it to an idle machine.
6. **Accepted 2026-09-24:** a TACC-managed colonist using **Best Available** can select and equip compatible loose equipment carrying the red forbidden marker when **Allow forbidden loose equipment** is enabled. Retain the checks that the marker remains, ordinary hauling still ignores it, and disabling the setting restores Core's exclusion.
7. **Accepted 2026-09-24:** wearer-specific mood ranking works for ordinary managed colonists. Retain the New Uniform versus second-hand Clown Costume scenario to confirm removal of the mood penalty outranks Core's market-value-and-quality result.
8. **Accepted 2026-09-24:** wearer-specific mood ranking works for Thrifty managed colonists. Retain the check that beneficial second-hand clothing beats new clothing when it produces the higher total clothing mood.
9. **Accepted 2026-09-24:** retain the mixed corpse-origin/tattered and equal-mood regression cases. Confirm all three mood categories affect the complete-loadout comparison, equal mood falls back to Core's ordinary value-and-quality result, and manual or policy-restricted candidates remain untouched.
10. **Accepted 2026-09-24:** the user confirmed that managed beings now equip themselves much faster. Retain this regression scenario: give a managed being an empty automatic Hat or Clothing slot while compatible equipment is available and they are busy. Confirm the equipment goal becomes the next personal task. Repeat with a completely naked new being: after Core's grace period it may interrupt ordinary work to obtain available clothing. Confirm upgrades to already-filled slots wait for a normal break, and manual slots never gain urgency.
11. Disable automatic washing before adding another eligible stored item. Confirm no new automatic order is created. Existing work may finish. Re-enable the setting and confirm searching resumes.
12. Select one loose corpse-origin clothing item and choose **Wash**. Repeat with a mixed multi-selection containing eligible clothing, ordinary clothing, and a non-clothing object. Confirm eligible items queue up to available idle-machine capacity and the rest are ignored.
13. After a completed cycle, confirm the tooltip changes from **Taken from the dead** to **Second-hand**, item quality is unchanged, and tattered/other unrelated flags are preserved.
14. Confirm worn clothing, reserved/unavailable items, ordinary second-hand clothing, and weapons cannot be washed.
15. Save and reload once while a haul is pending and once during washing. Confirm the exact item and progress recover without duplication. Remove a loaded machine and confirm the clothing is returned without changing origin; relocate one and confirm its loaded item and progress follow it.
16. Review Player.log for TACC errors, failed goals, duplicate items, or lost reservations. Exercise a power loss and unreachable input once to confirm ordinary Core recovery behavior.

## Prior 1.0.2 acceptance and maintenance checks

## rc296.3172 compatibility update: accepted and published

The 2026-09-19 review passes **70 automated checks** and clean Release/test builds. Both reported assembly findings are reproduced against the published 1.0.1 reference and resolved by the reviewed 1.0.2 reference in the isolated comparison. All 50 traits and 12 equipment values retain their validated reference contents.

On **2026-09-19**, the user confirmed: "Validated. Uploaded to Workshop. We can go back to maintenance mode. :)" This provides in-game acceptance and user-reported Workshop publication of **1.0.2**. It was the accepted published baseline before the 1.1.0 feature build. This does not claim that every individual maintenance scenario was separately retested.

Retained checklist for future changes:

1. Fully restart Stardeus with TACC **1.0.2** and AUMF **1.4.0**, then load the colony.
2. Open **Traits & log**. The header should identify **v0.15.0-rc296.3172** and show the same clothing mood values.
3. Click **Clear diagnostic log**. The two reviewed Game.dll/Game.Constants.dll findings should stay cleared. Zero current findings is expected for the reviewed build and values with no additional relevant external patches.
4. Confirm existing policies and opt-outs remain in place. Other unknown definitions, changed values, different Core builds, or inspected external patches must continue to return after clearing.

These checks are retained for future maintenance. Prior accepted releases and additional maintenance checklists follow.

## rc294.3170 compatibility update: accepted

The 2026-09-17 review passes **69 automated checks** and clean Release/test builds. The supplied four findings are reproduced against the old reference and resolved by the reviewed 1.0.1 reference in the isolated runner. This does not substitute for the running game's Harmony scan.

On **2026-09-17**, the user confirmed: "And clear. Update validated." The supplied in-game screenshot identifies **v0.15.0-rc294.3170** and shows **0 current unvalidated findings** after clearing. TACC **1.0.1** is accepted as the current compatibility baseline. This records the user's validation and observed diagnostic result; it does not claim every maintenance scenario below was separately retested.

Retained checklist for future changes:

1. Fully restart Stardeus with TACC **1.0.1** and AUMF **1.4.0**, then load the colony.
2. Open **Traits & log**. The reference header should identify **v0.15.0-rc294.3170**, with the same clothing mood values as before.
3. Click **Clear diagnostic log**. The two reviewed assembly findings and the two trade-price findings should stay cleared. Zero current findings is expected for this exact reviewed Core and unchanged values with no additional relevant external patches.
4. Confirm existing policies and individual opt-outs remain in place. Any additional unknown definitions, changed values, different Core build, or inspected external patches must still return after clearing.

The trade values are price multipliers, so no new trait/mood row or clothing-policy rule is introduced. The earlier accepted gameplay baseline is recorded below.

## Accepted 1.0.0 baseline

On **2026-09-06**, the user confirmed the 0.1.3 balance update working: "And we're clean. Everything checks out." The supplied in-game screenshot shows the rc283.3153 reference values, **0 current unvalidated findings**, and the functioning Traits & log panel. That accepted behavior is promoted to **1.0.0** with release metadata, documentation, and artwork updates. Policy logic, validation references, and save schema are unchanged.

The rc283.3153 review passed 64 automated checks and a build with 0 warnings/errors. The supplied log's 34 findings were accounted for against the installed definitions and compiled Core. The following checklists are retained for future maintenance; general user acceptance does not record each step as separately retested.

## Balance regression checklist

1. Confirm the current TACC release loads with AUMF 1.4.0. The Traits & log tab should identify rc296.3172, show Thrifty corpse-origin -3, and describe separate category effects.
2. Current findings for the reviewed Core/Constants DLLs, removed constants, and new mood tunables should be gone. The 17 `IsUsed` trait warnings should not return during ordinary play. Old entries can remain under History until cleared.
3. Click **Clear diagnostic log**. Those reviewed findings should stay cleared; any additional unreviewed definitions, values, or external patches should return as usual. Zero current findings is expected for the reviewed Core definitions with no additional relevant changes or external patches.
4. Confirm existing per-being policy settings and opt-outs remain in place. If observing the original Thrifty example, two second-hand items and one corpse-origin item yield +4 and -3 independently before Optimist/Pessimist; old mood entries may take time to expire.

The automated tests use isolated data and do not modify an actual colony. Gameplay timing, visual layout, and the running game's patch scan remain separate from compiled/build verification.

## AUMF 1.4.0 regression checklist

The installed AUMF API/theme documentation records prior successful TACC theme testing. TACC 1.0.2 retains the same tab, managed-scrollbar, and native-widget calls. Use the current build when these checks are needed after future changes:

1. Fully restart with AUMF 1.4.0 and the current TACC release. Confirm TACC opens and has no framework-version mismatch warning or new TACC/AUMF error.
2. Switch game themes with TACC open, then close and reopen it. Verify tab selection, hover/disabled readability, diagnostic and colonist scrollbars, scroll position, and search/input focus.
3. Confirm current clothing policies and unresolved diagnostic entries persist. A future unreviewed Core build should be reported; an AUMF update alone does not validate it.

## Diagnostic regression checklist

The user confirmed 0.1.0 working on 2026-09-05. The 0.1.1 diagnostic update passed 56 automated checks and a clean Release build. The accepted 0.1.3 update extends those checks to 64 and provides the clean in-game result recorded above. These unresolved-finding scenarios remain useful for future diagnostic changes:

1. Open **Traits & log**. Current unknown/changed traits or equipment-effect findings should be explicitly marked unknown/unvalidated, above older history.
2. With an unresolved finding present, click **Clear diagnostic log** while paused. The finding should immediately return. Repeat the clear and confirm it returns again.
3. Let the game advance through multiple one-minute diagnostic checks. The finding should stay visible without a new identical Player.log entry every minute. Save/reload: it should still be flagged and reported again without accumulating duplicate history copies.
4. Copy the log. It should identify current unvalidated findings separately from history and include the disclaimer. With no differences from the validated references, clearing should show zero current findings.

No save editing or test traits are required in a real colony. Automated tests supply synthetic unknowns and changed values in an isolated test process. A mod change that disappears or returns to the checked value should leave the current list; its prior warning may remain under history.

## Existing policy regression checklist

These are the original 0.1.0 checks, retained for future changes. The user's general acceptance does not record each item separately.

1. Fully restart Stardeus. Confirm TACC loads and no TACC/AUMF registration or patch errors appear in Player.log.
2. Click the clothing top-bar icon. Check all three tabs, panel resizing, scrolling, search, the log disclaimer, and Copy diagnostic log.
3. Inspect a Thrifty colonist's non-manual worn slots: second-hand allowed, corpse-origin forbidden, minimum strictly above the tattered cutoff unless an existing range conflicts.
4. Inspect an ordinary and a Fastidious colonist: both ownership restrictions enabled and tatters excluded. Check that an existing stricter quality limit and item/color choices remain intact.
5. Inspect a robot mind without mental health: existing policies retained with no extra mood restrictions. A human mind in a humanoid robot body should follow its mental-health/trait profile.
6. Watch existing bad clothing: it should follow Core's configured replacement action. Confirm needed life-support equipment is protected by Core, and suitable replacements are found through native equipment jobs.
7. Opt a being out, then back in. Edit one individual policy through Core and confirm TACC pauses that being instead of reversing the edit.
8. Save/reload and verify settings, opt-outs, prior-policy restoration, and log history persist. Turning TACC off should restore unchanged TACC-owned policies.
9. When traits change, a colonist joins, or a mind transfers, check that the appropriate profile is applied and relevant diagnostic entries appear without repeated unchanged-entry spam.

The measured initial policy-pass time is in Player.log and on the Policies tab. Do not use the separate automated planner tests as a claim about actual game startup time.
