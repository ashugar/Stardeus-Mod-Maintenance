using System.Reflection;
using Game.CodeGen;
using Game.Components;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.IntelligentRecycling;
using HarmonyLib;

internal static class Program {
    private static int checks;
    private static readonly Type Guards = typeof(CorpseDisposalGuards);
    private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private static void Check(bool value, string description) {
        checks++;
        if (!value) { throw new Exception(description); }
    }
    private static void Install(string failure = null) {
        Guards.GetField("attempted", Hidden).SetValue(null, false);
        Harmony.FailureMethod = failure;
        CorpseDisposalGuards.Install();
    }
    private static (IntelligentRecyclingSys sys, BeingData data) Setup(bool robot = false) {
        Install();
        var state = new GameState();
        var sys = IntelligentRecyclingSys.Create(state);
        var data = new BeingData { Id = 1 };
        data.Species.IsBiological = !robot;
        var being = new Being { Data = data, S = state };
        data.Being = being;
        being.Disposable = new DisposableComp { Being = being, IsBiological = !robot, SlotDesignation = robot ? 2 : 1 };
        state.Query.AllBeingData.Items.Add(data.Id, data);
        return (sys, data);
    }
    private static AIAgentComp Assign(BeingData data) {
        var goal = data.Being.Goal;
        var worker = new AIAgentComp { Being = new Being { S = data.Being.S }, Goal = goal };
        goal.Agent = worker;
        var device = new DisposerComp { S = data.Being.S, SlotDesignation = data.Being.Disposable.SlotDesignation };
        // Native CarryToDisposer selects the BB slot without Slot.Reserve.
        worker.BB.ReservedSlot = new Slot { Parent = device };
        return worker;
    }
    private static AIActionResult Execute(BeingData data, AIAgentComp worker, Action body = null,
            AIActionState nativeState = AIActionState.Success) {
        AIGoal goal = worker.Goal;
        object[] args = { data.Being.S, worker, goal, default(AIActionResult), null };
        bool run = (bool) Guards.GetMethod("BeforeExecute", Hidden).Invoke(null, args);
        object scope = args[4];
        AIActionResult result = (AIActionResult) args[3];
        try {
            if (run) { body?.Invoke(); result = AIActionResult.Create(nativeState); }
            object[] after = { result, scope };
            Guards.GetMethod("AfterExecute", Hidden).Invoke(null, after);
            return (AIActionResult) after[0];
        } finally {
            Guards.GetMethod("FinishExecute", Hidden).Invoke(null, new[] { null, scope });
        }
    }
    private static (bool run, bool result) Load(BeingData data, AIAgentComp worker, DisposerComp device = null) {
        device ??= (DisposerComp) worker.BB.ReservedSlot.Parent;
        object[] args = { device, data.Being.Disposable, worker, true };
        bool run = (bool) Guards.GetMethod("BeforeLoad", Hidden).Invoke(null, args);
        return (run, (bool) args[3]);
    }
    private static void Admission() {
        foreach (bool robot in new[] { false, true }) {
            var (sys, data) = Setup(robot);
            sys.Scan(); sys.Scan();
            Check(sys.Tracked == 1 && data.Being.Disposable.Orders == 1, "one native goal per corpse");
            Check(robot ? sys.RoboticCorpseTasksQueued == 1 : sys.BiologicalCorpseTasksQueued == 1, "correct family totals");
            Check(IntelligentRecyclingSys.TryReadCorpseMarker(data.Being.Goal, out var k, out uint id)
                && id == data.Id && (int) k == (robot ? 2 : 1), "exact persisted marker");
        }
        Action<IntelligentRecyclingSys, BeingData>[] blocks = {
            (s,d) => s.Enabled = false,
            (s,d) => s.RecycleBiologicalCorpses = false,
            (s,d) => s.SetReady(false),
            (s,d) => d.StateFlags = BeingState.Alive,
            (s,d) => d.StateFlags |= BeingState.Alive,
            (s,d) => d.Body.IsAlive = true,
            (s,d) => d.Species.IdH = SpeciesIdH.Crawler,
            (s,d) => d.Being.IsActive = false,
            (s,d) => d.Being.IsRemoved = true,
            (s,d) => d.StateFlags |= BeingState.Hidden,
            (s,d) => d.StateFlags |= BeingState.Expedition,
            (s,d) => d.StateFlags |= BeingState.Stored,
            (s,d) => d.StateFlags |= BeingState.InDevice,
            (s,d) => d.Body.Flags |= BodyFlags.Missing,
            (s,d) => d.Body.Slots[0].Type.Flags = BodyPartFlags.Removable,
            (s,d) => d.Body.Slots[0].Type.Flags = BodyPartFlags.Removable | BodyPartFlags.Optional,
            (s,d) => { d.Body.Slots[0].Type.Flags = BodyPartFlags.Removable; d.Body.Slots[0].State.Flags = BodyPartStateFlags.Destroyed; },
            (s,d) => d.Body.Slots[0].HoldsMind = true,
            (s,d) => d.Body.Slots[0].Type.Id = "Core",
            (s,d) => d.Body.Slots[0].InstalledDef.HoldsMind = true,
            (s,d) => d.Body.Slots[0].State = null,
            (s,d) => d.Body.Slots = new BodyPartSlot[] { null },
            (s,d) => d.Being.Goal = new AIGoal { Id = 77 },
            (s,d) => d.Being.Carrier = new Being(),
            (s,d) => d.Being.NanobotCarrierHiveId = 1,
            (s,d) => d.Being.Storable = new StorableObjComp { IsForbidden = true },
            (s,d) => s.S.Query.HasPendingSurgeries.Pending.Add(d.Id),
            (s,d) => s.NoProcessor(),
            (s,d) => d.Being.Disposable.IsBiological = false,
            (s,d) => d.Being.Disposable.SlotDesignation = 2,
            (s,d) => d.Being.Disposable.NativeAllows = false
        };
        foreach (var block in blocks) { var (s,d) = Setup(); block(s,d); s.Scan(); Check(s.Tracked == 0, "admission protection " + checks); }
        var (w, wd) = Setup();
        wd.Body.Slots[0].HoldsMind = true;
        wd.Body.Slots[0].Type.Flags = BodyPartFlags.Removable;
        wd.Body.Slots[0].State.Flags = BodyPartStateFlags.Welded;
        w.Scan(); Check(w.Tracked == 1, "actual welded mind slot permitted");
        var (r, rd) = Setup();
        rd.Body.Slots[0].Type.Flags = BodyPartFlags.Removable;
        rd.Body.Slots[0].IsPartRemoved = true;
        r.Scan(); Check(r.Tracked == 1, "removed slot no longer blocks");
    }
    private static void Lifecycle() {
        var (sys, data) = Setup(); sys.Scan(); var worker = Assign(data);
        data.Being.Carrier = worker.Being;
        Check(sys.ValidateCorpseExecution(sys.S, worker.Goal, worker), "native selected unreserved slot permitted");
        Check(Execute(data, worker, () => Check(Load(data, worker).run,
            "native selected unreserved slot reaches final load")).State == AIActionState.Success,
            "native unreserved slot execution permitted");
        worker.BB.ReservedSlot.ReservedBy = worker.Being;
        Check(sys.ValidateCorpseExecution(sys.S, worker.Goal, worker), "own exact carrier/reservation permitted");
        Check(Execute(data, worker, () => Check(Load(data, worker).run, "valid final load remains native")).State == AIActionState.Success, "valid execution");
        Check(Execute(data, worker, () => Check(Load(data, worker).run,
            "ordinary native refusal is not intercepted"), AIActionState.Failed).State == AIActionState.Failed,
            "ordinary native capacity/failure result preserved");
        var wrongWorker = new AIAgentComp { Goal = worker.Goal, Being = new Being { S = sys.S } };
        Check(!sys.ValidateCorpseExecution(sys.S, worker.Goal, wrongWorker), "foreign worker cannot own carried corpse");
        int game = sys.S.GameId; sys.S.GameId++;
        Check(Execute(data, worker).State == AIActionState.Cancelled, "wrong game identity denied");
        sys.S.GameId = game;
        data.Being.Carrier = new Being();
        Check(Execute(data, worker).State == AIActionState.Cancelled, "foreign carrier cancels");
        data.Being.Carrier = null;
        worker.BB.ReservedSlot.ReservedBy = new Being();
        Check(Execute(data, worker).State == AIActionState.Cancelled, "foreign reservation cancels");
        worker.BB.ReservedSlot.ReservedBy = worker.Being;
        var result = Execute(data, worker, () => {
            sys.RecycleBiologicalCorpses = false;
            Check(!Load(data, worker).run, "final callback policy change prevents load");
        });
        Check(result.State == AIActionState.Cancelled, "only policy denial maps final failure to Cancelled");
        sys.RecycleBiologicalCorpses = true;
        result = Execute(data, worker, () => {
            var manual = new BeingData { Id = 22, Being = new Being { S = sys.S } };
            manual.Being.Data = manual;
            manual.Being.Disposable = new DisposableComp { Being = manual.Being };
            Check(Load(manual, worker).run, "nested unrelated manual load passes unchanged");
        });
        Check(result.State == AIActionState.Success, "unrelated callback does not poison scope");
        AIGoal original = data.Being.Goal;
        result = Execute(data, worker, () => {
            data.Being.Goal = new AIGoal { Id = 999, Target = data.Being.Disposable, IsByPlayer = true };
            Check(!Load(data, worker).run, "replacement goal cannot escape retained scope");
        });
        Check(result.State == AIActionState.Cancelled && !data.Being.Goal.Cancelled, "replacement manual goal untouched");
        data.Being.Goal = original;
        original.Vars.Ints.Remove(IntelligentRecyclingSys.CorpseMarker);
        Check(Execute(data, worker).State == AIActionState.Cancelled, "registry-owned missing marker fails closed");
        original.Vars.Strings[IntelligentRecyclingSys.CorpseMarker] = "bad";
        Check(Execute(data, worker).State == AIActionState.Cancelled, "wrong marker type fails closed");
        original.Vars.Strings.Clear(); original.WithVar(IntelligentRecyclingSys.CorpseMarker, 1);
        sys.Enabled = false;
        Check(sys.Reconcile() && original.Cancelled && worker.CancelledByNative, "clock uses native cancellation");
        Check(!Load(data, worker).run, "marked direct load outside Execute blocked");
    }
    private static void PersistenceAndFailure() {
        var (sys,data) = Setup(); sys.Scan(); var worker = Assign(data);
        ComponentData saved = sys.SaveForTest();
        var replacement = IntelligentRecyclingSys.Create(sys.S);
        replacement.LoadForTest(saved);
        Check(replacement.ValidateCorpseExecution(sys.S, worker.Goal, worker), "registry/settings restoration preserves exact goal");
        replacement.LoadForTest(new ComponentData());
        Check(!replacement.RecycleBiologicalCorpses && !replacement.RecycleRoboticCorpses, "old save defaults off");
        Check(!replacement.ValidateCorpseExecution(sys.S, worker.Goal, worker), "orphan persisted marker blocked");
        saved.IntSets["CorpseKinds"] = Array.Empty<int>();
        replacement.LoadForTest(saved);
        Check(!replacement.ValidateCorpseExecution(sys.S, worker.Goal, worker), "partial registry load fails closed");
        saved.SetInt("SaveSchemaVersion", 15);
        replacement.LoadForTest(saved);
        Check(!replacement.ValidateCorpseExecution(sys.S, worker.Goal, worker)
            && !replacement.RecycleBiologicalCorpses && !replacement.RecycleRoboticCorpses,
            "unknown future corpse schema fails closed");
        foreach (string failure in new[] { "Execute", "LoadDisposable", "OnGoalLoad", "all" }) {
            (sys, data) = Setup(); sys.Scan(); worker = Assign(data);
            Install(failure);
            Check(!CorpseDisposalGuards.Ready, "incomplete hook set never ready " + failure);
            sys.StartSafety(sys.S);
            sys.S.Clock.OnTickEarly.Send(1);
            Check(data.Being.Goal.Cancelled && worker.CancelledByNative, "independent early sentinel cancels restored goals " + failure);
        }
        (sys, data) = Setup(); sys.Scan(); worker = Assign(data);
        sys.SetReady(false); sys.StartSafety(sys.S); sys.S.Clock.OnTickEarly.Send(1);
        Check(data.Being.Goal.Cancelled, "settings load failure protected by early sentinel");
        var manualGoal = new AIGoal { Id = 66, Target = data.Being.Disposable, IsByPlayer = true };
        data.Being.Goal = manualGoal; worker.Goal = manualGoal;
        Check(Execute(data, worker).State == AIActionState.Success, "unmarked manual execution unchanged");
        Install();
    }
    public static void Main() {
        Admission(); Lifecycle(); PersistenceAndFailure();
        Console.WriteLine($"PASS: {checks} production-code corpse policy/ownership/guard checks. Controlled Core models; no gameplay validation.");
    }
}
