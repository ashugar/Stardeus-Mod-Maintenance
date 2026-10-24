# Changelog

## 2.3.4

Updated the emergency Android raid fallback for the Stardeus v0.15.0-rc344 raid-selection changes. The compatibility hook now targets Core's `RaiderPicker.DrawSpecies` path, preserves forced-Human raids and normal weighted faction selection, and fails closed with a bounded diagnostic if Core changes the expected instruction shape again.

Automatic Install Missing Parts and Replace Bad Parts operations for Androids now respect the player's Forbidden setting on loose components. PAE retains its mixed biological and robotic compatibility rules while leaving manual part selection, reservations, delivery, cancellation, and item identity under Core control.

The repaired `2.3.3` candidate loaded cleanly on rc344 without the previous zero-replacement warning or Harmony errors, and the affected behavior was confirmed in game before promotion to this release.

## 2.3.3

Migrated Android population integration to the Stardeus v0.15.0-rc332 generation paths. New stasis sleepers, survivor groups, derelict inhabitants, selected gifts, and combat-raid fallbacks can once again include Androids at the intended share. Existing and preset stasis sleepers retain their saved identities instead of being regenerated.

Removed obsolete `BleedChance` properties from Power Hand and Entropy Exchanger definitions. Current Core derives bleeding from wound severity and damage type, so this is configuration cleanup without a gameplay balance change.

Added explicit method-signature and replacement-count checks to the remaining population transpilers so future Core changes fail visibly instead of becoming silent no-ops.

## 2.3.2

Updated the Android species definition for the Stardeus v0.15.0-rc294 mental-needs changes. Androids now explicitly receive Mental Health alongside Fun, allowing both needs to remain associated with the Android mind through body and Quantum Nexus transfers.

This removes Core's validation warning that Androids granted Fun without the required Mental Health need. Existing saves use Core's normal mind deserialization path and do not require a custom migration.

## 2.3.1

Restored Android faction raid generation for Stardeus v0.15.0-rc290. Androids remain eligible for Adventurer, Old Earth, Pirate, and Scavenger raids with the existing relative selection weight of 0.05. Core still controls raid points, equipment, and flight requirements. Forced-Human and bots-only selection paths are unchanged.

Removed the four obsolete faction JSON patches and moved the existing Android emergency fallback hook to CombatRaid.SpawnRaiders.

Android spawning in Pirate raids was confirmed in game. The other three factions use the same validated hook and selection policy, but were not separately observed in game.
