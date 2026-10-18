using System;
using System.Collections.Generic;
using Game;
using Game.Components;
using Game.Data;
using Game.Systems;
using Game.Systems.AI;
using Game.Systems.Backstories;
using Game.Systems.Creatures;
using Game.Systems.Items;
using Game.Systems.Mental;
using Game.Systems.Traits;
using Game.Utils;
using KL.Utils;

namespace ProstheticsAndroidsExpanded.Systems
{
    /// <summary>
    /// Gives a newly initialized Android Quantum Nexus a small set of
    /// programmed traits or manufacturing defects.
    /// </summary>
    public sealed class QuantumNexusTraitsSys : GameSystem
    {
        public const string SysId = "QuantumNexusTraitsSys";

        private const string AndroidSpeciesId = "Androids";
        private const string QuantumNexusPartId = "Brain/QuantumNexus";

        private const float RoboticWeight = 2.0f;
        private const float EmergentWeight = 1.0f;
        private const float PhysiologicalWeight = 0.15f;

        private static readonly string[] RoboticTraits =
        {
            "Antisocial",
            "Bookworm",
            "CleaningOcd",
            "Clumsy",
            "Curious",
            "Enlightened",
            "Fast",
            "Lazy",
            "Minimalist",
            "Pacifist",
            "Paranoid",
            "Sedentary",
            "Slow",
            "Technophile",
            "Technophobe",
            "Workaholic"
        };

        private static readonly string[] EmergentTraits =
        {
            "Astrophile",
            "Astrophobe",
            "Charismatic",
            "Optimist",
            "Pessimist",
            "Psychopath",
            "ShadowDweller",
            "Shy",
            "Sociopath",
            "Stalker"
        };

        private static readonly string[] PhysiologicalTraits =
        {
            "AlwaysHungry",
            "BadAppetite",
            "FastMetabolism",
            "SlowMetabolism"
        };

        // These represent deliberately installed vocational packages rather
        // than a lived childhood or employment history.
        private static readonly string[] ProgrammedRoles =
        {
            "BotWhiz",
            "CommsOperator",
            "Cook",
            "Doctor",
            "Engineer",
            "FieldSurveyor",
            "FighterPilot",
            "Firefighter",
            "Mechanic",
            "Pilot",
            "PoliceOfficer",
            "Researcher",
            "Scientist",
            "SoftwareDeveloper",
            "Soldier"
        };

        private readonly Dictionary<TraitDef, float> weightedTraits =
            new Dictionary<TraitDef, float>();

        public override string Id => SysId;
        public override bool SkipInSandbox => false;

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register()
        {
            GameSystems.Register(SysId, () => new QuantumNexusTraitsSys());
        }

        protected override void OnInitialize()
        {
            S.Sig.MindInstalled.AddListener(OnMindInstalled);
            S.Sig.BeingAdded.AddListener(OnBeingAdded);
            S.Sig.AfterLoadState.AddListener(OnAfterLoadState);
        }

        private void OnMindInstalled(
            Mind installedMind,
            ObjData partData,
            BeingData newOwner)
        {
            if (partData?.BodyPart?.Id != QuantumNexusPartId)
            {
                return;
            }

            // MindInstalled is sent immediately before InstallPart commits the
            // Nexus to the body slot, so the event itself is our proof that the
            // mind is being initialized in a Quantum Nexus.
            TryInitializeTraits(
                newOwner,
                SeedFor(newOwner, partData.Id),
                nexusConfirmed: true);
        }

        private void OnBeingAdded(Being being)
        {
            var data = being?.Data;
            TryInitializeTraits(data, SeedFor(data, 0));
        }

        private void OnAfterLoadState(GameState state)
        {
            // AfterLoadState listener order is not guaranteed across systems.
            // BeingsSys rebuilds minds and relinks body slots in its own
            // listener, so defer migration until the loaded game is ready.
            Ready.WhenGame(
                this,
                "Initialize loaded Android Quantum Nexus traits",
                InitializeLoadedAndroidTraits);
        }

        private void InitializeLoadedAndroidTraits()
        {
            var allBeings = S.Query.AllBeingData.Ask();
            if (allBeings == null)
            {
                return;
            }

            foreach (var data in allBeings.Values)
            {
                TryInitializeTraits(data, SeedFor(data, 0));
            }
        }

        private void TryInitializeTraits(
            BeingData data,
            int seed,
            bool nexusConfirmed = false)
        {
            if (!HasQuantumNexus(data, nexusConfirmed))
            {
                return;
            }

            var rng = new Random(seed);
            TryInitializeProgrammedRole(data, rng);

            if (data.Mind?.Traits == null ||
                data.Mind.Traits.Slots.Count != 0)
            {
                return;
            }

            // GenBabyMind creates an empty BeingTraits directly, without the
            // owner link established by the full being generator. Trait slots
            // need that link before their effects can be applied.
            data.Mind.Traits = BeingTraits.BlankFor(data.Mind);

            BuildWeightedTraits(data);

            var requestedCount = RollTraitCount(rng);
            var addedCount = 0;

            while (addedCount < requestedCount && weightedTraits.Count > 0)
            {
                var trait = TakeWeightedTrait(rng);
                if (trait == null)
                {
                    break;
                }

                RemoveIncompatibleTraits(trait);
                data.Mind.Traits.AddTrait(trait);

                var slot = data.Mind.Traits.Slots[
                    data.Mind.Traits.Slots.Count - 1];
                slot.ApplyTrait(S);
                slot.ApplyModifiersAfterLoadBeing(S, data, false);
                addedCount++;
            }
        }

        private static bool HasQuantumNexus(
            BeingData data,
            bool nexusConfirmed)
        {
            if (data?.Species?.Id != AndroidSpeciesId)
            {
                return false;
            }

            if (data.Mind == null)
            {
                return false;
            }

            return nexusConfirmed ||
                data.Body?.MindSlot?.InstalledDef?.Id == QuantumNexusPartId;
        }

        private void TryInitializeProgrammedRole(BeingData data, Random rng)
        {
            var experience = data.Mind.Experience;
            if (experience == null)
            {
                experience = Experience.BlankFor(data.Mind);
                data.Mind.Experience = experience;
            }
            if (experience.Roles == null)
            {
                experience.Roles =
                    new Dictionary<AgePhase, BeingRole>();
            }
            if (experience.Roles.Count != 0)
            {
                return;
            }

            var candidates = new List<BeingRole>();
            foreach (var roleId in ProgrammedRoles)
            {
                var role = BeingRole.Get(roleId);
                if (role != null &&
                    !HasTraitConflict(data.Mind.Traits, role))
                {
                    candidates.Add(role);
                }
            }
            if (candidates.Count == 0)
            {
                return;
            }

            var selected = candidates[rng.Next(candidates.Count)];
            experience.Roles.Add(AgePhase.None, selected);
            selected.ApplyModifiersAfterLoadBeing(S, data, false);
            if (data.Mind.Jobs != null)
            {
                GenBeingJobsDataStep.ApplyExperience(
                    data,
                    data.Mind.Jobs);
            }
        }

        private static bool HasTraitConflict(
            BeingTraits traits,
            BeingRole role)
        {
            if (traits?.Slots == null)
            {
                return false;
            }
            foreach (var slot in traits.Slots)
            {
                var trait = slot?.Trait;
                if (trait != null &&
                    (Contains(role.IncompatibleTraits, trait) ||
                     Intersects(role.LovesJobs, trait.IncapableJobs) ||
                     Intersects(role.IncapableJobs, trait.LovesJobs)))
                {
                    return true;
                }
            }
            return false;
        }

        private void BuildWeightedTraits(BeingData data)
        {
            weightedTraits.Clear();
            AddTraitGroup(data, RoboticTraits, RoboticWeight);
            AddTraitGroup(data, EmergentTraits, EmergentWeight);
            AddTraitGroup(data, PhysiologicalTraits, PhysiologicalWeight);
        }

        private void AddTraitGroup(
            BeingData data,
            string[] traitIds,
            float groupWeight)
        {
            foreach (var traitId in traitIds)
            {
                var trait = TraitDef.Get(traitId);
                if (trait == null || HasRoleConflict(data.Mind.Experience, trait))
                {
                    continue;
                }

                weightedTraits[trait] = trait.Rarity * groupWeight;
            }
        }

        private TraitDef TakeWeightedTrait(Random rng)
        {
            var totalWeight = 0.0;
            foreach (var pair in weightedTraits)
            {
                if (pair.Value > 0f)
                {
                    totalWeight += pair.Value;
                }
            }

            if (totalWeight <= 0.0)
            {
                return null;
            }

            var roll = rng.NextDouble() * totalWeight;
            TraitDef selected = null;

            foreach (var pair in weightedTraits)
            {
                selected = pair.Key;
                roll -= pair.Value;
                if (roll <= 0.0)
                {
                    break;
                }
            }

            if (selected != null)
            {
                weightedTraits.Remove(selected);
            }
            return selected;
        }

        private void RemoveIncompatibleTraits(TraitDef selected)
        {
            if (selected.IncompatibleTraits == null)
            {
                return;
            }

            foreach (var incompatible in selected.IncompatibleTraits)
            {
                if (incompatible != null)
                {
                    weightedTraits.Remove(incompatible);
                }
            }
        }

        private static int RollTraitCount(Random rng)
        {
            var roll = rng.NextDouble();
            if (roll < 0.70)
            {
                return 1;
            }
            if (roll < 0.95)
            {
                return 2;
            }
            return 3;
        }

        private static bool HasRoleConflict(
            Experience experience,
            TraitDef trait)
        {
            if (experience?.Roles == null)
            {
                return false;
            }

            foreach (var role in experience.Roles.Values)
            {
                if (role == null)
                {
                    continue;
                }

                if (Contains(role.IncompatibleTraits, trait) ||
                    Intersects(role.LovesJobs, trait.IncapableJobs) ||
                    Intersects(role.IncapableJobs, trait.LovesJobs))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Contains(TraitDef[] traits, TraitDef target)
        {
            if (traits == null)
            {
                return false;
            }

            foreach (var trait in traits)
            {
                if (trait == target)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Intersects(JobType[] first, JobType[] second)
        {
            if (first == null || second == null)
            {
                return false;
            }

            foreach (var a in first)
            {
                foreach (var b in second)
                {
                    if (a == b)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static int SeedFor(BeingData data, uint partDataId)
        {
            unchecked
            {
                var seed = 17;
                seed = seed * 31 + (int)(data?.Id ?? 0);
                seed = seed * 31 + (int)(data?.Mind?.Id ?? 0);
                seed = seed * 31 + (int)partDataId;
                return seed;
            }
        }

        public override void Unload()
        {
            S.Sig.MindInstalled.RemoveListener(OnMindInstalled);
            S.Sig.BeingAdded.RemoveListener(OnBeingAdded);
            S.Sig.AfterLoadState.RemoveListener(OnAfterLoadState);
            weightedTraits.Clear();
        }
    }
}
