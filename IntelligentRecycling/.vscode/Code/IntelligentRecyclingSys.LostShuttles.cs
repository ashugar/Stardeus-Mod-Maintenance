using System;
using System.Collections.Generic;
using Game.Commands;
using Game.Components;
using Game.Components.Upgrades;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Creatures;
using Game.Systems.Story;
using Game.Utils;
using KL.Utils;

namespace Game.Systems.IntelligentRecycling {
    public sealed partial class IntelligentRecyclingSys {
        internal string BuildLostShuttleReplacementTooltip() {
            string chance = BuildLostShuttleReturnChanceText();
            return "When a Shuttle is permanently lost, deconstruct its "
                + "unusable landing pad and queue a replacement at the same "
                + "position and rotation. Installed upgrades are remembered "
                + "by slot and reordered through ordinary Install Upgrade "
                + "jobs, so real recovered or available modules are still "
                + "required. " + chance + " Enabling this option gives up "
                + "that return event for each pad IR starts replacing. "
                + "Disabling the option does not abandon a replacement "
                + "already in progress. This option is disabled by default.";
        }

        private string BuildLostShuttleReturnChanceText() {
            const string reference = "Core has no fixed overall return "
                + "percentage or deadline. Its built-in full-pool reference "
                + "is about 5.6% of an eligible Positive-event pick when all "
                + "18 Positive candidates qualify with equal weight. Actual "
                + "eligibility and event-history weights can change that share.";
            if (loadedState == null
                    || !DeserterUtils.HasReturnableDeserterWithShuttle(
                        loadedState, out _, out _)) {
                return reference;
            }

            StoryContext context = S.Query.GetStoryContext.Ask();
            StoryEventDef returnEvent = StoryEventDef.Get(
                DeserterShuttleReturnsStoryEvent.EventId);
            IStoryEventHandler handler = S.Query.StoryEventHandlerFor.Ask(
                StoryEventOutcome.Positive);
            List<StoryEventDef>[] considerable = StoryEventDef.Considerable;
            if (context == null || returnEvent == null || handler == null
                    || considerable == null
                    || considerable.Length <= (int) StoryEventOutcome.Positive) {
                return reference;
            }

            float returnWeight = handler.GetEventWeight(context, returnEvent,
                StoryPickIgnore.Schedule);
            if (returnWeight <= 0f) {
                return "Core's Shuttle-return event is not eligible right now; "
                    + "its eligibility can change later. Core provides no "
                    + "fixed deadline or lifetime percentage.";
            }

            float totalWeight = 0f;
            List<StoryEventDef> positiveEvents = considerable[
                (int) StoryEventOutcome.Positive];
            if (positiveEvents == null) {
                return reference;
            }
            foreach (StoryEventDef candidate in positiveEvents) {
                totalWeight += handler.GetEventWeight(context, candidate,
                    StoryPickIgnore.Schedule);
            }
            if (totalWeight <= 0f) {
                return reference;
            }

            float percent = Math.Min(100f,
                Math.Max(0f, returnWeight / totalWeight * 100f));
            return "Current conditional Shuttle-return chance: "
                + percent.ToString("0.#")
                + "% of the next eligible Positive-event pick. This is not a "
                + "per-day or lifetime guarantee; storyteller pacing, "
                + "cooldowns, eligibility, and other mods can change when that "
                + "pick occurs.";
        }

        internal static string MakeLostShuttleKey(int layer, int position,
                string definitionId) {
            return layer + ":" + position + ":" + definitionId;
        }

        private void ScanLostShuttles() {
            PruneSuppressedLostShuttles();
            if (!ReplaceLostShuttles || loadedState == null) {
                return;
            }

            shuttles.Clear();
            S.Components.FindAll(shuttles);
            foreach (ShuttleComp shuttle in shuttles) {
                Tile tile = shuttle?.Tile;
                if (tile == null || !shuttle.IsLost || !tile.IsActive
                        || !tile.IsConstructed
                        || suppressedLostShuttleEntityIds.Contains(tile.Id)) {
                    continue;
                }

                string key = MakeLostShuttleKey(tile.Definition.LayerId,
                    tile.PosIdx, tile.DefinitionId);
                if (lostShuttleReplacements.ContainsKey(key)
                        || tile.Goal != null
                        || !tile.Constructable.CanDeconstruct()
                        || !S.Research.IsDefUnlocked(tile.Definition)) {
                    continue;
                }

                string[] upgradeIds = SnapshotInstalledUpgrades(tile.Upgrades);
                AIGoal goal = tile.Constructable.CreateDeconstructGoal();
                if (goal == null || tile.Goal != goal) {
                    continue;
                }

                lostShuttleReplacements[key] =
                    new LostShuttleReplacementRecord {
                        OriginalEntityId = tile.Id,
                        CurrentEntityId = 0,
                        DeconstructionGoalId = goal.Id,
                        Position = tile.PosIdx,
                        Layer = tile.Definition.LayerId,
                        Rotation = tile.Transform.Rotation,
                        DefinitionId = tile.DefinitionId,
                        UpgradeIds = upgradeIds,
                        Phase = LostShuttleReplacementPhase.Deconstructing,
                        NextActionTick = 0
                    };
                LastQueued++;
                EquipmentTasksQueued++;
                LostShuttleReplacementsQueued++;
                D.Warn("[Intelligent Recycling] Lost Shuttle pad {0} at {1} "
                    + "was queued for ordinary deconstruction and replacement. "
                    + "This ends any chance for Core's stolen-shuttle return "
                    + "story event on that pad.", tile.Id, tile.PosIdx);
            }
        }

        private void PruneSuppressedLostShuttles() {
            if (suppressedLostShuttleEntityIds.Count == 0
                    || loadedState == null) {
                return;
            }
            List<int> stale = null;
            foreach (int entityId in suppressedLostShuttleEntityIds) {
                Tile tile = loadedState.FindEntity<Tile>(entityId);
                if (tile?.IsActive == true
                        && tile.TryGetComponent<ShuttleComp>(out var shuttle)
                        && shuttle.IsLost) {
                    continue;
                }
                stale ??= new List<int>();
                stale.Add(entityId);
            }
            if (stale == null) {
                return;
            }
            foreach (int entityId in stale) {
                suppressedLostShuttleEntityIds.Remove(entityId);
            }
            MarkApiRevision();
        }

        private bool ReconcileLostShuttleReplacements() {
            if (lostShuttleReplacements.Count == 0 || loadedState == null) {
                return false;
            }

            bool changed = false;
            List<string> completed = null;
            foreach (KeyValuePair<string, LostShuttleReplacementRecord> pair
                    in lostShuttleReplacements) {
                LostShuttleReplacementRecord record = pair.Value;
                bool remove = false;
                switch (record.Phase) {
                    case LostShuttleReplacementPhase.Deconstructing:
                        remove = ReconcileLostShuttleDeconstruction(record,
                            ref changed);
                        break;
                    case LostShuttleReplacementPhase.AwaitingBlueprint:
                        remove = TryPlaceLostShuttleReplacement(record,
                            ref changed);
                        break;
                    case LostShuttleReplacementPhase.AwaitingConstruction:
                        remove = ReconcileLostShuttleConstruction(record,
                            ref changed);
                        break;
                    default:
                        remove = true;
                        changed = true;
                        break;
                }
                if (remove) {
                    completed ??= new List<string>();
                    completed.Add(pair.Key);
                }
            }

            if (completed != null) {
                foreach (string key in completed) {
                    lostShuttleReplacements.Remove(key);
                }
            }
            return changed;
        }

        private bool ReconcileLostShuttleDeconstruction(
                LostShuttleReplacementRecord record, ref bool changed) {
            Tile original = loadedState.FindEntity<Tile>(record.OriginalEntityId);
            if (original?.IsActive != true) {
                record.Phase = LostShuttleReplacementPhase.AwaitingBlueprint;
                record.NextActionTick = 0;
                changed = true;
                return TryPlaceLostShuttleReplacement(record, ref changed);
            }

            if (original.Goal?.Id == record.DeconstructionGoalId
                    || original.Constructable.IsBrokenDown) {
                return false;
            }

            suppressedLostShuttleEntityIds.Add(record.OriginalEntityId);
            D.Warn("[Intelligent Recycling] Lost Shuttle pad replacement at "
                + "{0} was stopped because its deconstruction order was "
                + "canceled or replaced. This pad is suppressed until the "
                + "setting is toggled off and on.", record.Position);
            changed = true;
            return true;
        }

        private bool TryPlaceLostShuttleReplacement(
                LostShuttleReplacementRecord record, ref bool changed) {
            long ticks = loadedState.Ticks;
            if (ticks < record.NextActionTick) {
                return false;
            }

            Def definition = The.Defs.TryGet(record.DefinitionId);
            if (definition == null || !S.Research.IsDefUnlocked(definition)) {
                record.NextActionTick = ticks + ScanIntervalTicks;
                return false;
            }

            Tile prototype = EntityUtils.CreatePrototype<Tile>(definition);
            if (prototype == null) {
                record.NextActionTick = ticks + ScanIntervalTicks;
                return false;
            }
            prototype.Transform.Rotation = record.Rotation;
            prototype.Graphics.OnReady(false);
            if (!CmdPlaceTile.CanPlace(record.Position, prototype,
                    record.Layer, warn: false)) {
                record.NextActionTick = ticks + ScanIntervalTicks;
                return false;
            }

            CmdPlaceTile command = new CmdPlaceTile(record.Position, prototype,
                warn: false, instant: A.InstantBuild, skipValidation: false);
            command.Execute(loadedState);
            Tile placed = command.PlacedTile;
            if (!command.IsSuccess || placed?.IsActive != true) {
                record.NextActionTick = ticks + ScanIntervalTicks;
                return false;
            }

            record.CurrentEntityId = placed.Id;
            record.Phase = LostShuttleReplacementPhase.AwaitingConstruction;
            record.NextActionTick = 0;
            changed = true;
            D.Log("[Intelligent Recycling] Queued replacement Shuttle pad "
                + "construction at {0} with rotation {1}.", record.Position,
                record.Rotation);
            if (placed.IsConstructed) {
                return TryRestoreLostShuttleUpgrades(placed, record,
                    "replacement placement");
            }
            return false;
        }

        private bool ReconcileLostShuttleConstruction(
                LostShuttleReplacementRecord record, ref bool changed) {
            Tile replacement = loadedState.FindEntity<Tile>(record.CurrentEntityId);
            if (replacement?.IsActive != true) {
                D.Warn("[Intelligent Recycling] Stopped tracking replacement "
                    + "Shuttle pad at {0} because its construction blueprint "
                    + "was removed.", record.Position);
                changed = true;
                return true;
            }
            if (!replacement.IsConstructed) {
                return false;
            }
            if (loadedState.Ticks < record.NextActionTick) {
                return false;
            }
            bool complete = TryRestoreLostShuttleUpgrades(replacement, record,
                "reconciliation");
            if (!complete) {
                record.NextActionTick = loadedState.Ticks + ScanIntervalTicks;
            }
            changed = true;
            return complete;
        }

        private void CompleteLostShuttleReplacement(Tile tile) {
            if (tile?.IsActive != true || !tile.IsConstructed
                    || lostShuttleReplacements.Count == 0) {
                return;
            }
            string key = MakeLostShuttleKey(tile.Definition.LayerId,
                tile.PosIdx, tile.DefinitionId);
            if (!lostShuttleReplacements.TryGetValue(key, out var record)
                    || record.Phase
                        != LostShuttleReplacementPhase.AwaitingConstruction) {
                return;
            }

            record.CurrentEntityId = tile.Id;
            if (TryRestoreLostShuttleUpgrades(tile, record,
                    "construction completion")) {
                lostShuttleReplacements.Remove(key);
                MarkApiRevision();
                RefreshTopWidget();
                panel?.RefreshStatistics();
            } else {
                record.NextActionTick = loadedState.Ticks + ScanIntervalTicks;
            }
        }

        private bool TryRestoreLostShuttleUpgrades(Tile tile,
                LostShuttleReplacementRecord record, string trigger) {
            if (record.UpgradeIds == null || record.UpgradeIds.Length == 0) {
                D.Log("[Intelligent Recycling] Replacement Shuttle pad at "
                    + "{0} completed after {1}; no upgrades needed reordering.",
                    record.Position, trigger);
                return true;
            }
            UpgradesComp upgrades = tile.Upgrades;
            if (upgrades == null) {
                if (!record.UpgradeRetryLogged) {
                    D.ErrSoft("[Intelligent Recycling] Replacement Shuttle pad "
                        + "at {0} has no upgrade component; saved assignments "
                        + "remain pending.", record.Position);
                    record.UpgradeRetryLogged = true;
                }
                return false;
            }

            int ordered = 0;
            int alreadyAssigned = 0;
            int unresolved = 0;
            for (int slot = 0; slot < record.UpgradeIds.Length; slot++) {
                string upgradeId = record.UpgradeIds[slot];
                if (string.IsNullOrWhiteSpace(upgradeId)) {
                    continue;
                }
                if (slot >= upgrades.NumSlots) {
                    unresolved++;
                    continue;
                }

                string installed = upgrades.Upgrades.SafeGet(slot);
                string pending = upgrades.PendingUpgrades.SafeGet(slot);
                if (installed == upgradeId || pending == upgradeId) {
                    alreadyAssigned++;
                    continue;
                }
                if (installed != null || pending != null) {
                    D.Warn("[Intelligent Recycling] Left Shuttle upgrade slot "
                        + "{0} unchanged because it has a different player "
                        + "assignment.", slot);
                    alreadyAssigned++;
                    continue;
                }

                Upgrade upgrade = UpgradeRegistry.Get(upgradeId);
                if (upgrade == null || !upgrade.IsCompatibleWith(upgrades.Entity)
                        || !upgrade.CanInstallNow(upgrades.Entity)
                        || !upgrades.OrderUpgrade(slot, upgrade)) {
                    unresolved++;
                    continue;
                }
                ordered++;
            }

            if (unresolved > 0) {
                if (!record.UpgradeRetryLogged) {
                    D.ErrSoft("[Intelligent Recycling] Replacement Shuttle pad "
                        + "at {0} still has {1} unresolved saved upgrade "
                        + "assignment(s); IR will retry without changing occupied "
                        + "slots.", record.Position, unresolved);
                    record.UpgradeRetryLogged = true;
                }
                return false;
            }

            D.Log("[Intelligent Recycling] Replacement Shuttle pad at {0} "
                + "completed after {1}: {2} ordinary Install Upgrade order(s) "
                + "queued and {3} assignment(s) already present.",
                record.Position, trigger, ordered, alreadyAssigned);
            return true;
        }

        private static string[] SnapshotInstalledUpgrades(
                UpgradesComp upgrades) {
            int count = Math.Max(upgrades?.NumSlots ?? 0,
                upgrades?.Upgrades?.Length ?? 0);
            if (count == 0) {
                return Array.Empty<string>();
            }
            string[] result = new string[count];
            for (int i = 0; i < count; i++) {
                result[i] = upgrades.Upgrades.SafeGet(i);
            }
            return result;
        }

        private void SaveLostShuttleReplacementState(ComponentData data) {
            string[] records = new string[lostShuttleReplacements.Count];
            int index = 0;
            foreach (LostShuttleReplacementRecord record
                    in lostShuttleReplacements.Values) {
                records[index++] = record.Serialize();
            }
            Array.Sort(records, StringComparer.Ordinal);
            data.SetStringSet("LostShuttleReplacementRecords", records);

            int[] suppressed = new int[suppressedLostShuttleEntityIds.Count];
            suppressedLostShuttleEntityIds.CopyTo(suppressed);
            Array.Sort(suppressed);
            data.SetIntSet("SuppressedLostShuttleIds", suppressed);
        }

        private void LoadLostShuttleReplacementState(ComponentData data,
                int schemaVersion) {
            lostShuttleReplacements.Clear();
            suppressedLostShuttleEntityIds.Clear();
            if (schemaVersion < 13) {
                return;
            }

            string[] serializedRecords = data.GetStringSet(
                "LostShuttleReplacementRecords", Array.Empty<string>());
            foreach (string serialized in serializedRecords) {
                if (!LostShuttleReplacementRecord.TryDeserialize(serialized,
                        out var record)) {
                    D.ErrSoft("[Intelligent Recycling] Ignored an unreadable "
                        + "saved lost-Shuttle replacement record.");
                    continue;
                }
                lostShuttleReplacements[record.Key] = record;
            }

            int[] suppressed = data.GetIntSet(
                "SuppressedLostShuttleIds", Array.Empty<int>());
            foreach (int entityId in suppressed) {
                if (entityId > 0) {
                    suppressedLostShuttleEntityIds.Add(entityId);
                }
            }
        }
    }
}
