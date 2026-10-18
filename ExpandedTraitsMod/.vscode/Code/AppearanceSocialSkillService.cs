using System;
using System.Collections.Generic;
using Game.CodeGen;
using Game.Data;
using Game.Systems.Creatures;
using Game.Systems.Mental;
using HarmonyLib;

namespace ExpandedTraitsMod {
    /// <summary>
    /// Applies appearance modifiers to the Social JobSlot that current Core gameplay reads.
    /// The adjustment is kept out of save data so existing saves migrate once in memory and
    /// never gain another point on every reload.
    /// </summary>
    internal static class AppearanceSocialSkillService {
        private sealed class AppliedAdjustment {
            internal Mind Mind;
            internal JobSlot Slot;
            internal sbyte Desired;
            internal sbyte Applied;
        }

        internal sealed class SaveSuspension {
            internal Mind Mind;
            internal bool Resume;
        }

        private static readonly Dictionary<uint, AppliedAdjustment> Applied = new();

        internal static void Reconcile(GameState state) {
            if (state == null) return;
            var beings = state.Sys.Get<BeingsSys>(BeingsSys.SysId);
            if (beings?.Minds == null) return;

            var present = new HashSet<uint>();
            foreach (Mind mind in beings.Minds.Values) {
                if (mind == null || mind.Id == 0) continue;
                present.Add(mind.Id);
                EnsureFor(mind);
            }

            var stale = new List<uint>();
            foreach (uint id in Applied.Keys) {
                if (!present.Contains(id)) stale.Add(id);
            }
            foreach (uint id in stale) Revert(id);
        }

        internal static void EnsureFor(Mind mind, bool logValidation = true) {
            if (mind == null || mind.Id == 0) return;
            sbyte desired = DesiredFor(mind);
            JobSlot social = null;
            mind.Jobs?.TryJob(JobTypeIdH.Social, out social);

            if (Applied.TryGetValue(mind.Id, out AppliedAdjustment existing)) {
                if (ReferenceEquals(existing.Mind, mind)
                        && ReferenceEquals(existing.Slot, social)
                        && existing.Desired == desired
                        && social != null && !social.Incapable) {
                    return;
                }
                Revert(mind.Id);
            }

            if (desired == 0 || social == null || social.Incapable) return;
            int baseLevel = social.Level;
            // rc327 caps skills at JobSlot.MaxLevel (25). Clamping to byte.MaxValue would
            // let an appearance adjustment create an invalid level above Core's skill cap.
            int effective = Math.Max(0,
                Math.Min(JobSlot.MaxLevel, baseLevel + desired));
            sbyte applied = (sbyte)(effective - baseLevel);
            if (applied == 0) return;

            SetLevel(social, effective);
            Applied[mind.Id] = new AppliedAdjustment {
                Mind = mind,
                Slot = social,
                Desired = desired,
                Applied = applied
            };
            if (logValidation) {
                string trait = desired > 0
                    ? (mind.Traits.HasTrait(ExpandedTraitIds.BeautifulH)
                        ? ExpandedTraitIds.Beautiful : ExpandedTraitIds.Handsome)
                    : ExpandedTraitIds.Ugly;
                ValidationDiagnostics.Once(
                    $"appearance:{mind.Id}:{trait}:{baseLevel}:{effective}",
                    "Appearance Social modifier applied: mind={0}; trait={1}; base={2}; effective={3}; delta={4}.",
                    mind, trait, baseLevel, effective, applied);
            }
        }

        internal static void RevertAll() {
            var ids = new List<uint>(Applied.Keys);
            foreach (uint id in ids) Revert(id);
        }

        internal static SaveSuspension SuspendForSave(Mind mind) {
            if (mind == null || !Applied.ContainsKey(mind.Id)) return null;
            Revert(mind.Id);
            return new SaveSuspension { Mind = mind, Resume = true };
        }

        internal static void ResumeAfterSave(SaveSuspension suspension) {
            if (suspension?.Resume != true) return;
            suspension.Resume = false;
            EnsureFor(suspension.Mind, false);
        }

        private static void Revert(uint mindId) {
            if (!Applied.TryGetValue(mindId, out AppliedAdjustment entry)) return;
            Applied.Remove(mindId);
            if (entry.Slot == null) return;
            int baseLevel = Math.Max(0,
                Math.Min(JobSlot.MaxLevel, entry.Slot.Level - entry.Applied));
            SetLevel(entry.Slot, baseLevel);
        }

        private static sbyte DesiredFor(Mind mind) {
            var traits = mind?.Traits;
            if (traits == null) return 0;
            if (traits.HasAnyTrait(ExpandedTraitIds.BeautifulH,
                    ExpandedTraitIds.HandsomeH)) return 1;
            return traits.HasTrait(ExpandedTraitIds.UglyH) ? (sbyte)-1 : (sbyte)0;
        }

        private static void SetLevel(JobSlot slot, int level) {
            byte value = (byte)level;
            if (slot.Level == value) return;
            slot.Level = value;
            slot.ChangedB.Invoke();
        }
    }

    [HarmonyPatch(typeof(MindData), nameof(MindData.Serialize), new[] { typeof(Mind) })]
    internal static class AppearanceSocialSkillSavePatch {
        private static void Prefix(Mind mind,
                out AppearanceSocialSkillService.SaveSuspension __state) {
            __state = AppearanceSocialSkillService.SuspendForSave(mind);
        }

        private static Exception Finalizer(Exception __exception,
                AppearanceSocialSkillService.SaveSuspension __state) {
            AppearanceSocialSkillService.ResumeAfterSave(__state);
            return __exception;
        }
    }
}
