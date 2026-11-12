// Controlled models of the inspected native contracts. The harness compiles
// the actual production corpse code; these models never load a game assembly.
using System.Reflection;
using Game.Components;
using Game.Data;
using Game.Systems.AI;
using Game.Systems.BodyParts;
using Game.Systems.Creatures;
using Game.Systems.Items;

namespace Game.CodeGen {
    internal static class SlotDesignationH { internal const int Disposer_Organic = 1, Disposer_Robotic = 2; }
    internal static class SpeciesIdH { internal const int Crawler = 99; }
}
namespace Game.Data {
    public class GameState {
        public int GameId = 10;
        public long Ticks;
        public Queries Query = new();
        public Systems Sys = new();
        public TestClock Clock = new();
    }
    public class Queries {
        public BeingQuery AllBeingData = new();
        public ObjectQuery GetAllObjs = new();
        public SurgeryQuery HasPendingSurgeries = new();
    }
    public class BeingQuery { public Dictionary<uint, BeingData> Items = new(); public Dictionary<uint, BeingData> Ask() => Items; }
    public class ObjectQuery { public Dictionary<uint, ObjData> Items = new(); public Dictionary<uint, ObjData> Ask() => Items; }
    public class SurgeryQuery { public HashSet<uint> Pending = new(); public bool Ask(BeingData d, object slot) => Pending.Contains(d.Id); }
    public class Systems { public TestAI AI = new(); }
    public class TestAI { public Dictionary<uint, AIGoal> ColonyGoals = new(); }
    public class TestClock { public TickSignal OnTickEarly = new(); }
    public class TickSignal {
        private readonly List<Action<long>> listeners = new();
        public void AddListener(Action<long> a) => listeners.Add(a);
        public void RemoveListener(Action<long> a) => listeners.Remove(a);
        public void Send(long tick) { for (int i = listeners.Count - 1; i >= 0; i--) { listeners[i](tick); } }
    }
    public class ComponentData {
        public Dictionary<string, int> Ints = new();
        public Dictionary<string, string> Strings = new();
        public Dictionary<string, bool> Bools = new();
        public Dictionary<string, long> Longs = new();
        public Dictionary<string, float> Floats = new();
        public Dictionary<string, int[]> IntSets = new();
        public Dictionary<string, string[]> StringSets = new();
        public Dictionary<string, float[]> FloatSets = new();
        public Dictionary<string, object> Mats = new();
        public void SetInt(string k, int v) => Ints[k] = v;
        public int GetInt(string k, int fallback) => Ints.TryGetValue(k, out int v) ? v : fallback;
        public void SetBool(string k, bool v) => Bools[k] = v;
        public bool GetBool(string k, bool fallback) => Bools.TryGetValue(k, out bool v) ? v : fallback;
        public void SetIntSet(string k, int[] v) => IntSets[k] = v;
        public int[] GetIntSet(string k, int[] fallback) => IntSets.TryGetValue(k, out int[] v) ? v : fallback;
        public void SetStringSet(string k, string[] v) => StringSets[k] = v;
        public string[] GetStringSet(string k, string[] fallback) => StringSets.TryGetValue(k, out string[] v) ? v : fallback;
    }
    public class Entity {
        public GameState S;
        public bool IsActive = true, IsRemoved;
        public bool IsCarriedByAny => Carrier != null || NanobotCarrierHiveId != 0;
        public Being Carrier;
        public int NanobotCarrierHiveId;
        public AIGoal Goal;
        public DisposableComp Disposable;
        public StorableObjComp Storable;
        public EggComp Egg;
        public bool TryGetComponent<T>(out T value) where T : class {
            value = Disposable as T ?? Storable as T ?? Egg as T;
            return value != null;
        }
    }
    public class Being : Entity {
        public BeingData Data;
        public bool IsAlive => Data.HasStateFlag(BeingState.Alive);
    }
    public class Def { public string Id; public string NameT; }
    public class Tile { public bool IsActive = true, IsConstructed = true; }
}
namespace Game.Systems.Creatures {
    [Flags] public enum BeingState { Alive = 1, Dead = 2, Hidden = 4, Expedition = 8, Stored = 16, InDevice = 32 }
    public class BeingData {
        public uint Id;
        public Being Being;
        public Species Species = new();
        public Body Body = new();
        public BeingState StateFlags = BeingState.Dead;
        public bool HasStateFlag(BeingState f) => (StateFlags & f) == f;
    }
    public class Species { public bool IsBiological = true; public int IdH; public string Id = "TestSpecies", Name = "Test species"; }
}
namespace Game.Systems.BodyParts {
    [Flags] public enum BodyFlags { None, Missing }
    [Flags] public enum BodyPartFlags { None, Removable, Optional }
    [Flags] public enum BodyPartStateFlags { None, Welded, Destroyed }
    public class Body {
        public BodyPartSlot[] Slots = { new() };
        public bool IsAlive, IsIncapacitated;
        public BodyFlags Flags;
        public bool HasRemovableParts => Slots.Any(s => !s.IsPartRemoved && s.InstalledDef != null
            && (s.Type.Flags & BodyPartFlags.Removable) != 0 && (s.State.Flags & BodyPartStateFlags.Welded) == 0);
    }
    public class BodyPartSlot {
        public bool IsPartRemoved, HoldsMind;
        public PartType Type = new();
        public BodyPartDef InstalledDef = new();
        public BodyPartState State = new();
    }
    public class PartType { public string Id = "Body"; public BodyPartFlags Flags; }
    public class BodyPartDef { public bool HoldsMind; public string SlotTypeId = "Body"; }
    public class BodyPartState { public BodyPartStateFlags Flags; }
}
namespace Game.Components {
    public enum DisposeCheck { BulkOrder }
    public class EggComp { }
    public class DisposableComp : IAIGoalProvider {
        private static uint next = 100;
        public Being Being;
        public Obj Obj;
        public Entity Entity => (Entity) Being ?? Obj;
        public GameState S => Entity.S;
        public bool IsBiological = true;
        public int SlotDesignation = 1;
        public bool NativeAllows = true;
        public int Orders;
        public bool TryOrderDispose(bool player, out string reason) {
            reason = null;
            if (!NativeAllows || Entity.Goal != null) { return false; }
            Orders++;
            var g = new AIGoal { Id = ++next, Target = this, IsByPlayer = player };
            Entity.Goal = g;
            S.Sys.AI.ColonyGoals.Add(g.Id, g);
            return true;
        }
        public bool CanOrderDispose(DisposeCheck check) => NativeAllows && Entity.Goal == null;
        public void OnGoalLoad(AIGoal goal) { }
    }
    public class DisposerComp {
        public int SlotDesignation = 1;
        public GameState S;
        public Tile Tile = new();
        public bool LoadDisposable(DisposableComp disposable, AIAgentComp agent) => true;
    }
    public class StorableObjComp { public bool IsForbidden; }
}
namespace Game.Systems.Items {
    public class Obj : Entity { public ObjData Data; }
    public class ObjData {
        public uint Id;
        public Obj Obj;
        public Def Def;
        public InventoryItemDef Item;
        public BodyPartDef BodyPart;
        public ObjReservation Reservation;
        public ItemOriginBucket Origin;
        public float Quality = 1f;
    }
    public class ObjReservation { public AIGoal Goal; }
    public class InventoryItemDef {
        public static readonly Dictionary<int, InventoryItemDef> All = new();
        public string SlotId;
        public Def Def;
    }
    public enum ItemOriginBucket { Untracked, New, SecondHand, DeadMans }
    public static class ItemOwnership { public static ItemOriginBucket BucketFor(ObjData data) => data.Origin; }
}
namespace Game.Systems.AI {
    public enum AIActionState { InProgress, Success, Failed, Cancelled }
    public struct AIActionResult {
        public AIActionState State;
        public string Message;
        public static AIActionResult Create(AIActionState state, string message = null) => new() { State = state, Message = message };
    }
    public class AIActionCarryToDisposer {
        public AIActionResult Execute(GameState state, AIAgentComp agent, AIGoal goal, long tick) => default;
    }
    public interface IAIGoalProvider { Entity Entity { get; } }
    public class AIGoal {
        public uint Id;
        public IAIGoalProvider Target;
        public AIAgentComp Agent;
        public bool IsByPlayer, IsForceAssigned, Cancelled;
        public ComponentData Vars;
        public Func<GameState, AIGoal, bool> AvailabilityGate;
        public AIGoal WithVar(string k, int v) { (Vars ??= new()).SetInt(k, v); return this; }
        public AIGoal WithVar(string k, uint v) => WithVar(k, unchecked((int) v));
        public bool TryGetVar(string k, out int v) { v = 0; return Vars?.Ints.TryGetValue(k, out v) == true; }
        public bool TryGetVar(string k, out uint v) { bool ok = TryGetVar(k, out int raw); v = unchecked((uint) raw); return ok; }
        public static bool IsCompleted(AIGoal goal) => false;
        public static bool IsCancelled(AIGoal goal) => goal.Cancelled;
        public static void Cancel(AIGoal goal, long tick, string reason, bool removeImmediately) {
            goal.Cancelled = true;
            if (goal.Agent != null) { goal.Agent.CancelledByNative = true; }
        }
    }
    public class AIAgentComp {
        public Being Being;
        public AIGoal Goal;
        public Blackboard BB = new();
        public bool CancelledByNative;
    }
    public class Blackboard { public Slot ReservedSlot; }
    public class Slot { public Being ReservedBy; public object Parent; }
}
namespace HarmonyLib {
    public class Harmony {
        public static string FailureMethod;
        public Harmony(string id) { }
        public void Patch(MethodInfo target, HarmonyMethod prefix, HarmonyMethod postfix, object transpiler, HarmonyMethod finalizer) {
            if (target.Name == FailureMethod || FailureMethod == "all") { throw new InvalidOperationException("injected patch failure"); }
        }
    }
    public class HarmonyMethod { public HarmonyMethod(Type t, string n) { } }
    public static class AccessTools { public static MethodInfo Method(Type t, string n, Type[] args) => t.GetMethod(n, args); }
}
namespace KL.Utils { public static class D { public static void Warn(string m) { } public static void LogEx(Exception e, string m) { } } }
namespace Game.Systems.IntelligentRecycling {
    public class TestPanel { public void Refresh() { } }
    public sealed partial class IntelligentRecyclingSys {
        internal const int SaveSchemaVersion = 15;
        public GameState S;
        private GameState loadedState;
        private int loadedSettingsGameId;
        private static IntelligentRecyclingSys activeInstance;
        internal static IntelligentRecyclingSys ApiInstance => activeInstance;
        private TestPanel panel = new();
        public bool Enabled = true;
        private readonly Dictionary<uint, uint> trackedGoalsByObjectId = new();
        internal bool IsAlienEggReminderBlocking { get; set; }
        private void MarkAlienEggReminderDirty() { }
        public int LastQueued, LastVanillaOrderRejected, BiologicalTasksQueued, RoboticTasksQueued;
        private class ProcessorContext { internal int BiologicalCount = 1, RoboticCount = 1; }
        private ProcessorContext processors = new();
        private ProcessorContext BuildProcessorContext() => processors;
        private static List<KeyValuePair<string, string>> SortCategories(Dictionary<string, string> entries) =>
            entries.OrderBy(p => p.Value).ToList();
        private sealed class ObjectEvaluation {
            internal DisposableComp Disposable;
            internal IntelligentRecyclingObjectSnapshotData Snapshot;
        }
        // Models the pre-existing IR admission gates; the new equipment and
        // egg predicates below are the linked production implementations.
        private ObjectEvaluation EvaluateObject(ObjData data, ProcessorContext context) {
            var snapshot = new IntelligentRecyclingObjectSnapshotData {
                Active = data.Obj.IsActive, Forbidden = data.Obj.Storable?.IsForbidden == true
            };
            if (RecyclingSelectionRules.IsAlienEgg(data.Def.Id)) {
                snapshot.TargetType = IntelligentRecyclingTargetType.AlienEgg;
                snapshot.IntelligentRecyclingWouldClaimUnderPolicy = Enabled && RecycleAlienEggs
                    && context.BiologicalCount > 0 && IsAlienEggReady(data.Obj, data.Obj.Disposable);
            } else {
                snapshot.TargetType = IntelligentRecyclingTargetType.Equipment;
                ApplyEquipmentSelection(data, snapshot);
                snapshot.IntelligentRecyclingWouldClaimUnderPolicy = Enabled && context.RoboticCount > 0
                    && snapshot.EquipmentOriginAllowed && snapshot.WeaponDefinitionAllowed
                    && float.IsFinite(data.Quality) && (snapshot.QualityCutoffBypassed || data.Quality <= .5f);
            }
            return new ObjectEvaluation { Snapshot = snapshot, Disposable = data.Obj.Disposable };
        }
        private void RequestScan() { }
        private static bool IsProtectedCategoryId(string s) => s == "Brain" || s == "Core";
        internal static IntelligentRecyclingSys Create(GameState state) {
            var sys = new IntelligentRecyclingSys { S = state, loadedState = state,
                loadedSettingsGameId = state.GameId, corpseSettingsLoaded = true,
                corpseInitializationReady = true, selectionSettingsLoaded = true,
                RecycleBiologicalCorpses = true, RecycleRoboticCorpses = true };
            activeInstance = sys;
            return sys;
        }
        internal void Scan() => ScanCorpses();
        internal bool Reconcile() => ReconcileCorpseGoals();
        internal ComponentData SaveForTest() { var d = new ComponentData(); SaveCorpseState(d); return d; }
        internal void LoadForTest(ComponentData d) { corpseSettingsLoaded = false; corpseLoadAttemptedGameId = S.GameId; LoadCorpseState(d); }
        internal bool AttemptedThisGame => corpseLoadAttemptedGameId == S.GameId;
        internal void SetReady(bool value) => corpseSettingsLoaded = value;
        internal int Tracked => corpseGoals.Count;
        internal void StartSafety(GameState s) => OnCorpseSafetyAfterLoadState(s);
        internal void NoProcessor() { processors.BiologicalCount = processors.RoboticCount = 0; }
        internal AIGoal QueueSelection(ObjData data, bool mark = true, bool player = false) {
            var evaluated = EvaluateObject(data, BuildProcessorContext());
            if (!evaluated.Snapshot.IntelligentRecyclingWouldClaimUnderPolicy
                    || !evaluated.Disposable.TryOrderDispose(player, out _)) { return null; }
            AIGoal goal = data.Obj.Goal;
            if (!player) {
                trackedGoalsByObjectId[data.Id] = goal.Id;
                if (mark) { MarkSelectionGoal(goal, data, evaluated.Snapshot); }
            }
            return goal;
        }
        internal ComponentData SaveSelection() { var d = new ComponentData(); SaveRecyclingSelection(d); return d; }
        internal void LoadSelection(ComponentData data) => LoadRecyclingSelection(data);
        private readonly HashSet<string> disabledEquipmentSlotIds = new();
        internal bool WeaponCategoryDisabled => disabledEquipmentSlotIds.Contains("Weapon");
        internal void SetSelectionReady(bool value) => selectionSettingsLoaded = value;
        internal void CheckSelectionTick() => OnRecyclingSelectionTickEarly(S.Ticks);
    }
}
