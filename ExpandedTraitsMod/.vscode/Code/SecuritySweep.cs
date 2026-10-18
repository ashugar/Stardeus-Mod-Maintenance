using System;
using System.Collections.Generic;
using System.Linq;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.Creatures;
using Game.Utils;
using HarmonyLib;
using KL.Grid;
using KL.Utils;

namespace ExpandedTraitsMod {
    internal sealed class SecuritySweepPlan {
        internal readonly AIGoal Goal;
        internal readonly BeingData Being;
        internal readonly List<int> AreaIds;
        internal readonly List<int> VisitPositions;
        internal int Index;
        internal int Checked;
        internal int Skipped;
        internal bool StartLogged;

        internal SecuritySweepPlan(AIGoal goal, BeingData being,
                List<int> areaIds, List<int> visitPositions) {
            Goal = goal;
            Being = being;
            AreaIds = areaIds;
            VisitPositions = visitPositions;
        }
    }

    internal static class SecuritySweepService {
        internal const string ActionId = "ExpandedTraitsSecuritySweep";
        internal static readonly int WorldVar = Hashes.S("ExpandedTraits.SecuritySweepDone");
        internal static readonly int GoalKey = Hashes.S(ActionId);
        private static readonly Dictionary<uint, SecuritySweepPlan> Plans = new();

        internal static bool TryGet(uint beingId, out SecuritySweepPlan plan) =>
            Plans.TryGetValue(beingId, out plan);

        internal static void ScheduleAll(GameState state, long ticks) {
            if (state == null || ExpandedTraitsSystem.IsIncident(state)) return;
            int eligible = 0;
            int scheduled = 0;
            foreach (Being being in state.Beings.Alive) {
                BeingData data = being?.Data;
                if (data?.IsPlayer != true || data.Mind?.Traits == null
                        || !data.Mind.Traits.HasTrait(ExpandedTraitIds.ParanoidH)
                        || data.Body.IsIncapacitated || being.Motion.IsImmobilized) {
                    continue;
                }
                eligible++;
                if (Schedule(state, data, ticks)) scheduled++;
            }
            ValidationDiagnostics.Log(
                "Paranoid sweep scheduling pass completed: eligible={0}; scheduled={1}.",
                eligible, scheduled);
        }

        private static bool Schedule(GameState state, BeingData data, long ticks) {
            if (Plans.ContainsKey(data.Id)) return false;
            if (data.Being?.AIAgent == null) {
                ValidationDiagnostics.Log(
                    "Paranoid sweep not scheduled: mind={0}; reason=no AI agent.", data.Mind);
                return false;
            }
            var bed = state.Query.FindBed.Ask(data, false);
            var slot = bed?.SlotFor(data);
            if (slot == null || slot.IsTemporary) {
                ValidationDiagnostics.Log(
                    "Paranoid sweep not scheduled: mind={0}; reason=no permanent bed.", data.Mind);
                return false;
            }

            int bedPos = slot.InPort;
            int bedArea = state.Sys.Areas.At(bedPos);
            int bedIsland = state.Sys.Areas.IslandAt(bedPos);
            if (bedArea <= 0 || bedIsland <= 0) {
                ValidationDiagnostics.Log(
                    "Paranoid sweep not scheduled: mind={0}; reason=bed is not in a reachable room; bedPos={1}.",
                    data.Mind, bedPos);
                return false;
            }

            var candidates = new List<(int areaId, int pos, int ring, double angle)>();
            Pos.ToXY(bedPos, out int bx, out int by);
            bool grounded = data.Being.Motion.IsGrounded;
            foreach (var pair in state.Sys.Areas.Areas) {
                int areaId = pair.Key;
                if (areaId <= 0 || state.Sys.Areas.DoorCheck.IsUnreachable(areaId)) continue;
                int pos = RepresentativePosition(state, data.Being.AIAgent, pair.Value,
                    bedIsland, grounded);
                if (pos == Pos.Invalid) continue;
                Pos.ToXY(pos, out int x, out int y);
                int dx = x - bx;
                int dy = y - by;
                int ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                double angle = Math.Atan2(dx, -dy);
                if (angle < 0) angle += Math.PI * 2;
                candidates.Add((areaId, pos, ring, angle));
            }
            if (candidates.Count == 0) {
                ValidationDiagnostics.Log(
                    "Paranoid sweep not scheduled: mind={0}; reason=no reachable rooms from bed; bedPos={1}.",
                    data.Mind, bedPos);
                return false;
            }

            candidates.Sort((a, b) => {
                if (a.areaId == bedArea && b.areaId != bedArea) return -1;
                if (b.areaId == bedArea && a.areaId != bedArea) return 1;
                int cmp = a.ring.CompareTo(b.ring);
                return cmp != 0 ? cmp : a.angle.CompareTo(b.angle);
            });
            int quota = Math.Max(1, (int)Math.Round(candidates.Count * 0.40,
                MidpointRounding.AwayFromZero));
            var selected = candidates.Take(quota).ToList();

            var agent = data.Being.AIAgent;
            if (agent.BB.TryNeedGoal(GoalKey, out var oldGoal)) {
                if (AIGoal.IsActive(oldGoal)) AIGoal.Cancel(oldGoal, ticks);
                agent.BB.NeedGoals.Remove(GoalKey);
            }
            var goal = AIGoal.CreatePersonal(JobTypeIdH.Need, IconId.CShield,
                    "Security sweep", agent, ticks)
                .WithPriority(4)
                .WithWorldToDesired(WorldVar, false, true)
                .WithInterrupt(1, Consts.TicksPer5Minutes);
            goal.GoalImportance = 1;
            agent.BB.SetNeedGoal(GoalKey, goal);
            Plans[data.Id] = new SecuritySweepPlan(goal, data,
                selected.Select(x => x.areaId).ToList(),
                selected.Select(x => x.pos).ToList());
            ValidationDiagnostics.Log(
                "Paranoid sweep scheduled: mind={0}; bedPos={1}; reachableRooms={2}; selectedRooms={3}; quotaPercent={4}; areaOrder={5}.",
                data.Mind, bedPos, candidates.Count, selected.Count,
                100.0 * selected.Count / candidates.Count,
                string.Join(",", selected.Select(x => x.areaId)));
            return true;
        }

        private static int RepresentativePosition(GameState state, AIAgentComp agent,
                HashSet<int> positions, int bedIsland, bool grounded) {
            foreach (int pos in positions) {
                if (state.Sys.Areas.IslandAt(pos) != bedIsland) continue;
                if (!state.Sys.Path.IsRoamable(pos, grounded)) continue;
                if (!agent.IsFeelingSafeAt(pos)) continue;
                return pos;
            }
            return Pos.Invalid;
        }

        internal static void Complete(GameState state, SecuritySweepPlan plan, long ticks) {
            if (plan == null) return;
            Plans.Remove(plan.Being.Id);
            plan.Goal.WithWorld(WorldVar, true);
            MoodService.Add(state, plan.Being, ticks, Consts.TicksPer4Hours,
                "Area secured", 4, IconId.CShield);
            ValidationDiagnostics.Log(
                "Paranoid sweep completed: mind={0}; plannedRooms={1}; checkedRooms={2}; skippedRooms={3}; mood=+4.",
                plan.Being.Mind, plan.VisitPositions.Count, plan.Checked, plan.Skipped);
        }

        internal static void CancelAll(long ticks, string reason = "requested") {
            int cancelled = Plans.Count;
            foreach (var plan in Plans.Values.ToArray()) {
                if (AIGoal.IsActive(plan.Goal)) AIGoal.Cancel(plan.Goal, ticks);
                plan.Being?.Being?.AIAgent?.BB.NeedGoals.Remove(GoalKey);
            }
            Plans.Clear();
            if (cancelled > 0) {
                ValidationDiagnostics.Log(
                    "Paranoid sweeps cancelled: count={0}; reason={1}.", cancelled, reason);
            }
        }
    }

    public sealed class AIActionSecuritySweep : AIAction {
        [SelfInit]
        private static void Register() => RegisterAction(new AIActionSecuritySweep());

        private AIActionSecuritySweep() : base(SecuritySweepService.ActionId,
                () => "Security sweep") {
            Preconditions = AIState.With(SecuritySweepService.WorldVar, false);
            Outcomes = AIState.With(SecuritySweepService.WorldVar, true);
            WithRequiredAbilities(AbilityIdH.Move);
            MarkAsRest();
            MarkAsMobile();
        }

        public override void OnActivate(AIAgentComp agent, AIGoal goal, long ticks) {
            agent.BB.ClearMoveTarget();
            if (SecuritySweepService.TryGet(agent.BeingData.Id, out var plan)
                    && plan.Goal == goal && !plan.StartLogged) {
                plan.StartLogged = true;
                ValidationDiagnostics.Log(
                    "Paranoid sweep started: mind={0}; plannedRooms={1}.",
                    agent.BeingData.Mind, plan.VisitPositions.Count);
            }
        }

        public override AIActionResult Execute(GameState state, AIAgentComp agent,
                AIGoal goal, long ticks) {
            if (!SecuritySweepService.TryGet(agent.BeingData.Id, out var plan)
                    || plan.Goal != goal) {
                goal.WithWorld(SecuritySweepService.WorldVar, true);
                return Success;
            }
            if (ExpandedTraitsSystem.IsIncident(state)) {
                SecuritySweepService.CancelAll(ticks, "new emergency");
                return Failed("A new emergency interrupted the security sweep");
            }
            if (agent.Motion.IsBusy) return InProgress;

            while (plan.Index < plan.VisitPositions.Count) {
                int pos = plan.VisitPositions[plan.Index];
                int expectedArea = plan.AreaIds[plan.Index];
                if (state.Sys.Areas.At(pos) != expectedArea
                        || state.Sys.Areas.DoorCheck.IsUnreachable(expectedArea)) {
                    plan.Index++;
                    plan.Skipped++;
                    ValidationDiagnostics.Log(
                        "Paranoid sweep room skipped: mind={0}; area={1}; reason=room changed or became unreachable; progress={2}/{3}.",
                        agent.BeingData.Mind, expectedArea, plan.Index,
                        plan.VisitPositions.Count);
                    agent.BB.ClearMoveTarget();
                    continue;
                }
                if (state.Sys.Areas.At(agent.PosIdx) == expectedArea) {
                    plan.Index++;
                    plan.Checked++;
                    ValidationDiagnostics.Log(
                        "Paranoid sweep room checked: mind={0}; area={1}; progress={2}/{3}.",
                        agent.BeingData.Mind, expectedArea, plan.Index,
                        plan.VisitPositions.Count);
                    agent.BB.ClearMoveTarget();
                    continue;
                }
                if (!TryGoToOnce(agent, pos, false)) {
                    plan.Index++;
                    plan.Skipped++;
                    ValidationDiagnostics.Log(
                        "Paranoid sweep room skipped: mind={0}; area={1}; reason=path request failed; progress={2}/{3}.",
                        agent.BeingData.Mind, expectedArea, plan.Index,
                        plan.VisitPositions.Count);
                    agent.BB.ClearMoveTarget();
                    continue;
                }
                return InProgress;
            }

            SecuritySweepService.Complete(state, plan, ticks);
            return Success;
        }

        public override void OnDeactivate(AIAgentComp agent, AIGoal goal, long ticks) {
            agent.BB.ClearMoveTarget();
        }

        public override float CostFor(AIAgentComp agent, AIGoal goal, long ticks) => 0.0005f;
    }

    [HarmonyPatch(typeof(AIBreakdownActionSabotageDevice),
        nameof(AIBreakdownActionSabotageDevice.OnDeactivate), new[] {
            typeof(AIAgentComp), typeof(AIGoal), typeof(long)
        })]
    internal static class ParanoidSabotageIncidentPatch {
        private static void Prefix(AIAgentComp agent, long ticks) {
            if (agent?.BB.Bool1 == true) ExpandedTraitsSystem.Instance?.SignalIncident(ticks);
        }
    }
}
