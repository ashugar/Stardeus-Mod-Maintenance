using System;
using System.Collections.Generic;
using System.Linq;

namespace TraitAndClothingCompatibility {
    // Independent of Unity/Core so the actual policy transformations can be tested.
    public enum WearerProfile { Ordinary, Thrifty, Fastidious, NoMentalHealth }

    public sealed class ProfileRule {
        public bool ForbidSecondHand;
        public bool ForbidDeadMans;
        public bool AvoidTattered;

        public static ProfileRule DefaultFor(WearerProfile profile) => new ProfileRule {
            ForbidSecondHand = profile == WearerProfile.Ordinary || profile == WearerProfile.Fastidious,
            ForbidDeadMans = profile != WearerProfile.NoMentalHealth,
            AvoidTattered = profile != WearerProfile.NoMentalHealth
        };
    }

    public sealed class SlotPolicy {
        public string Slot;
        public int Mode;
        public float QualityMin;
        public float QualityMax;
        public string ColorId;
        public string ItemDefId;
        public int WeaponKind;
        public bool ForbidWhenDropped;
        public int Incompatible;
        public bool ForbidSecondHand;
        public bool ForbidDeadMans;

        public SlotPolicy Clone() => (SlotPolicy)MemberwiseClone();
        public bool Same(SlotPolicy other) => other != null && Slot == other.Slot
            && Mode == other.Mode && QualityMin.Equals(other.QualityMin)
            && QualityMax.Equals(other.QualityMax) && ColorId == other.ColorId
            && ItemDefId == other.ItemDefId && WeaponKind == other.WeaponKind
            && ForbidWhenDropped == other.ForbidWhenDropped && Incompatible == other.Incompatible
            && ForbidSecondHand == other.ForbidSecondHand && ForbidDeadMans == other.ForbidDeadMans;
    }

    public sealed class PolicyState {
        public List<SlotPolicy> Slots = new List<SlotPolicy>();
        public PolicyState Clone() => new PolicyState {
            Slots = (Slots ?? new List<SlotPolicy>()).Where(s => s != null).Select(s => s.Clone()).ToList()
        };
        public void Put(SlotPolicy slot) {
            Slots ??= new List<SlotPolicy>();
            int i = Slots.FindIndex(s => s != null && s.Slot == slot.Slot);
            if (i < 0) Slots.Add(slot); else Slots[i] = slot;
        }
        public static bool Same(PolicyState a, PolicyState b) {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            var aa = (a.Slots ?? new List<SlotPolicy>()).Where(s => s != null).OrderBy(s => s.Slot, StringComparer.Ordinal).ToList();
            var bb = (b.Slots ?? new List<SlotPolicy>()).Where(s => s != null).OrderBy(s => s.Slot, StringComparer.Ordinal).ToList();
            return aa.Count == bb.Count && aa.Zip(bb, (x, y) => x.Same(y)).All(v => v);
        }
    }

    public sealed class PolicyPlan {
        public PolicyState Policy;
        public readonly List<string> Conflicts = new List<string>();
    }

    public static class PolicyEngine {
        public static WearerProfile Profile(bool hasMentalHealth, bool thrifty, bool fastidious) {
            if (!hasMentalHealth) return WearerProfile.NoMentalHealth;
            // These traits are incompatible in Core. If a mod forces both, use the safer restrictions.
            if (fastidious) return WearerProfile.Fastidious;
            return thrifty ? WearerProfile.Thrifty : WearerProfile.Ordinary;
        }

        public static float MinimumAbove(float threshold) {
            if (float.IsNaN(threshold) || float.IsInfinity(threshold) || threshold < 0f)
                throw new ArgumentOutOfRangeException(nameof(threshold), "Invalid tattered quality threshold.");
            if (threshold == 0f) return float.Epsilon;
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(threshold), 0);
            float result = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0);
            if (float.IsInfinity(result)) throw new ArgumentOutOfRangeException(nameof(threshold));
            return result;
        }

        public static bool WasManuallyEdited(bool ownsPolicy, PolicyState current, PolicyState lastApplied)
            => ownsPolicy && !PolicyState.Same(current, lastApplied);

        public static bool CanRestore(PolicyState current, PolicyState lastApplied)
            => PolicyState.Same(current, lastApplied);

        public static PolicyPlan Build(PolicyState original, IEnumerable<SlotPolicy> wornBaselines,
                WearerProfile profile, ProfileRule rule, float tatteredThreshold) {
            var result = new PolicyPlan { Policy = original?.Clone() };
            if (profile == WearerProfile.NoMentalHealth) return result;
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            float minimum = rule.AvoidTattered ? MinimumAbove(tatteredThreshold) : 0f;
            foreach (SlotPolicy baseline in wornBaselines) {
                if (baseline == null || string.IsNullOrEmpty(baseline.Slot)) continue;
                // A manually managed slot stays under the player's control.
                if (baseline.Mode == 0) continue;
                SlotPolicy slot = baseline.Clone();
                slot.ForbidSecondHand = rule.ForbidSecondHand;
                slot.ForbidDeadMans = rule.ForbidDeadMans;
                if (rule.AvoidTattered) {
                    float desired = Math.Max(slot.QualityMin, minimum);
                    bool bounded = slot.QualityMax > 0f && slot.QualityMax < 1f;
                    if (bounded && desired > slot.QualityMax) {
                        result.Conflicts.Add(slot.Slot);
                        // Preserve the explicit range; the UI/log explains that this slot cannot avoid tatters.
                    } else {
                        slot.QualityMin = desired;
                    }
                }
                result.Policy ??= new PolicyState();
                result.Policy.Put(slot);
            }
            return result;
        }
    }
}
