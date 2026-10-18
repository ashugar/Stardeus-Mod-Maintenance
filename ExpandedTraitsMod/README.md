# Expanded Traits

Expanded Traits turns personality labels into choices, preferences, and situational mood effects. It is a standalone mod and does not require a framework mod.

## Existing traits

- Beautiful and Handsome add one Social skill level. Ugly subtracts one, to a minimum of zero.
- Carnivores and Vegans seek preferred food first. Survival Meals are neutral. Disliked food is reserved for critical hunger when no preferred or neutral meal is reachable.
- Coffee Addicts seek coffee first. Coffee gives them +10 mood and going without gives -10. Other beings retain the normal +3 coffee effect and receive no missing-coffee penalty.
- Minimalists favor knives, the standard spacesuit and helmet, functional clothing, and an empty cosmetic hat slot. Equipment policies and direct player choices remain authoritative.
- Curious beings favor research and research-station hauling, enjoy research discoveries, telescope scans, and returning from expeditions, and become restless after two days without novelty.
- Paranoid beings favor security construction and repairs, suffer extra stress during incidents, then inspect about 40% of reachable rooms in an outward spiral from their permanent bed. A completed sweep grants Area Secured relief.
- Cat Person, Dog Person, generated species affinities, and Animal Person improve pet bonding and grant non-stacking companionship mood effects. Bonded-animal injury, recovery, and death also matter.

## Pet compatibility

At load time the mod discovers every species marked as a pet and creates a stable affinity trait for species without a Core trait. Cat Person and Dog Person are reused rather than duplicated. Friendly Crawlers are treated as pets and can use Core's petting interaction.

Generated trait IDs use `ExpandedTraits.PetAffinity.<SpeciesId>`. Missing species definitions are recreated as orphan-safe traits when an old save still refers to one. The whole pet-affinity family shares one normalized rarity budget, and a specific affinity takes precedence over Animal Person for the same interaction.

Introverts without a pet affinity have a deterministic 50% chance to receive one as a fourth trait. They still socialize when approached, initiate social contact less often, favor pet recreation, enjoy quiet time, and can become socially drained.

## New traits

- Extrovert: gains fulfillment from social contact and dislikes prolonged isolation.
- Light Sleeper / Heavy Sleeper: changes disturbance chance and interrupted-sleep mood. Heavy sleepers delay a general siren response for up to 30 minutes only while asleep in a safe room.
- Iron Stomach / Sensitive Stomach: changes food-poisoning development and initial severity.
- Claustrophobic: dislikes small or crowded enclosed rooms and enjoys large rooms or open space.
- Daredevil / Cautious: changes danger mood and preference for otherwise equivalent hazardous work.
- Empath: reacts strongly to a close friend's injury, recovery, death, or breakdown. Psychic storms grant the Empath +4 mood, and social contact during the storm grants partners +2.
- Heat Adapted / Cold Adapted: shifts both temperature limits by five degrees, trading tolerance at one extreme for vulnerability at the other.

Opposite traits are mutually incompatible. Player priorities, job permissions, equipment policies, reservations, reachability, direct control, and emergency work continue to take precedence.

## Validation status

- Version 0.1.4 is rebuilt for Core rc327's property-backed skill levels and clamps appearance adjustments to Core's new maximum skill level of 25. It supports Core's shared story/sector storm lifecycle, applies appearance modifiers to the active Social job slot without persisting or stacking them, includes inherited work-cost actions, respects player-owned hat policies, and rolls back all patches if compatibility initialization fails.
- Temporary `[Expanded Traits][Validation]` log entries report Empath storm mood and cleanup, storm-time reassurance, appearance Social adjustments, Minimalist equipment decisions, and Paranoid sweep scheduling, progress, cancellation, and completion. These diagnostics can be removed after in-game validation.
- Release build: passes with zero warnings and zero errors.
- Trait and manifest JSON: parsed during the package audit.
- Assembly and manifest version: `0.1.4`.
- Version 0.1.3 loaded under Core rc327 but its pre-rc327 binary access to `JobSlot.Level` failed after Core changed that member from a field to a property. Version 0.1.4 requires a full game restart for runtime validation.
