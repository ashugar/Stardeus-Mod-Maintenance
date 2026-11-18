using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Metadata, hook instruction replay and isolated real persistence. Never
// patches or initializes the game, runs an order, or reads player saves.
if (args.Length is < 4 or > 6 || args.Length == 5
        || args.Take(args.Length == 6 ? 5 : 4).Any(p => !Path.IsPathFullyQualified(p)))
    throw new ArgumentException("Expected absolute candidate DLL, baseline 1.1.6 DLL, Managed directory, and AUMF directory; optionally a companion DLL and its transpiler type.");
string candidatePath = args[0], baselinePath = args[1], managed = args[2], aumf = args[3];
int checks = 0;
void Check(bool ok, string message) {
    if (!ok) throw new Exception("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
resolver.AddSearchDirectory(aumf);
using var current = ModuleDefinition.ReadModule(candidatePath, new ReaderParameters { AssemblyResolver = resolver });
using var baseline = ModuleDefinition.ReadModule(baselinePath);
Check(current.Assembly.Name.Version == new Version(1, 2, 0, 0), "candidate assembly is 1.2.0.0");
Check(baseline.Assembly.Name.Version == new Version(1, 1, 6, 0), "comparison baseline is 1.1.6.0");
var refs = current.GetMemberReferences().ToArray();
Check(!refs.Any(r => r.Name.Contains("IsHaulingForbidden")), "removed Forbidden members absent");
int resolved = 0;
foreach (var member in refs) {
    string scope = member.DeclaringType.Scope?.Name ?? "";
    if (!(scope == "Game" || scope.StartsWith("Game.") || scope.StartsWith("KL")
            || scope == "AutoUtilitiesModFramework" || scope == "0Harmony" || scope.StartsWith("Unity"))) continue;
    object? result = member switch { MethodReference m => m.Resolve(), FieldReference f => f.Resolve(), _ => member };
    if (result == null) throw new Exception("Unresolved: " + member.FullName);
    resolved++;
}
Check(resolved > 0, $"all {resolved} direct Core/framework references resolve");
var sys = current.GetType("Game.Systems.AutoHauling.AutoHaulingSys");
var oldSys = baseline.GetType(sys.FullName);
Check((int)sys.Fields.Single(f => f.Name == "SaveSchemaVersion").Constant == 3, "save schema remains 3");
var calls = sys.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Select(i => i.Operand).OfType<MethodReference>().ToArray();
Check(calls.Count(m => m.Name == "get_IsForbidden") == 8 && calls.Count(m => m.Name == "set_IsForbidden") == 3,
    "eight Forbidden reads and three native writes migrated");
Check(calls.Count(m => m.Name.Contains("Unreachable")) == 2
    && calls.Where(m => m.Name.Contains("Unreachable")).All(m => m.Name == "get_IsMarkedUnreachable"),
    "only two unreachable reads; no marker-clearing calls");
var manual = sys.Methods.Single(m => m.Name == "ProcessForbiddenLooseCargo");
Check(Body(manual) == Body(oldSys.Methods.Single(m => m.Name == manual.Name)),
    "manual preview/clear IL unchanged");
Check(!manual.Body.Instructions.Any(i => i.Operand is MethodReference m &&
    (m.Name.Contains("Unreachable") || m.Name.Contains("OrderStor") || m.Name == "Cancel")),
    "manual pass cannot clear unreachable state or create/cancel tasks");
var scan = sys.Methods.Single(m => m.Name == "ScanLooseCargo");
var stale = sys.Methods.Single(m => m.Name == "TryResolveStaleStoreTask");
Guard(scan, "TryOrderStore");
Guard(stale, "HasCompatibleStorage");
foreach (var type in current.Types.Where(t => t.Namespace == sys.Namespace)) {
    if (type.Name == "HiveRefillRankingPatch") continue;
    var oldType = baseline.GetType(type.FullName);
    Check(oldType != null, "baseline type exists: " + type.Name);
    foreach (var method in type.Methods.Where(m => m.HasBody)) {
        // Hive settings/lifecycle and the TMP compatibility line changed.
        // Both existing cargo selection routines must now match 1.1.6.
        if (type == sys && (method.Name is "OnInitialize" or "Unload" or "Load" or "Save" or "ResetToDefaults"
                    || method.Name.Contains("PrioritizeHiveRefills") || method.Name.Contains("HiveRefillRanking"))
                || type.Name == "AutoHaulingPanel" && method.Name == "BuildControls"
                || type.Name == "FailureTextLinkHandler" && method.Name == "Initialize") continue;
        var old = oldType!.Methods.SingleOrDefault(m => GeneratedName(m.FullName) == GeneratedName(method.FullName));
        Check(old != null && Body(old) == Body(method), "unchanged IL: " + type.Name + "." + method.Name);
    }
}

void Guard(MethodDefinition method, string nativeName) {
    var code = method.Body.Instructions;
    int at = code.ToList().FindIndex(i => i.Operand is MethodReference m && m.Name == "get_IsMarkedUnreachable");
    Check(at >= 0, method.Name + " checks native unreachable marker");
    var branch = code[at + 1];
    Check(branch.OpCode.Code is Code.Brfalse or Code.Brfalse_S or Code.Brtrue or Code.Brtrue_S,
        method.Name + " branches on marker");
    bool onTrue = branch.OpCode.Code is Code.Brtrue or Code.Brtrue_S;
    var whenTrue = onTrue ? (Instruction)branch.Operand : branch.Next;
    var whenFalse = onTrue ? branch.Next : (Instruction)branch.Operand;
    // Stop backwards branches at the next scan iteration; do not confuse an
    // order for a later pile with permission to order this suppressed pile.
    bool Reaches(Instruction start) {
        var pending = new Stack<Instruction>();
        var seen = new HashSet<Instruction>();
        pending.Push(start);
        while (pending.Count > 0) {
            var i = pending.Pop();
            if (i == null || code.IndexOf(i) <= at || !seen.Add(i)) continue;
            if (i.Operand is MethodReference m && m.Name == nativeName) return true;
            if (i.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw) continue;
            if (i.Operand is Instruction target) pending.Push(target);
            if (i.OpCode.FlowControl != FlowControl.Branch && i.Next != null) pending.Push(i.Next);
        }
        return false;
    }
    Check(!Reaches(whenTrue) && Reaches(whenFalse),
        method.Name + ": unreachable path cannot reach native order/storage check; allowed path can");
    if (method == scan) {
        var i = whenTrue;
        bool unavailable = false;
        while (i != null && i.OpCode.FlowControl != FlowControl.Branch) {
            unavailable |= i.Operand is MethodReference m && m.Name == "set_LastUnavailable";
            i = i.Next;
        }
        Check(unavailable, "scan counts unreachable as unavailable");
    }
}

AssemblyLoadContext.Default.Resolving += (_, name) => {
    foreach (string dir in new[] { managed, aumf, Path.GetDirectoryName(candidatePath)! }) {
        string path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(managed, "Game.dll"));
var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(candidatePath);
var systemType = mod.GetType(sys.FullName)!;
var dataType = core.GetType("Game.Components.ComponentData")!;
var stateType = core.GetType("Game.Data.GameState")!;
object SystemInstance() {
    var instance = Activator.CreateInstance(systemType)!;
    systemType.GetField("S")!.SetValue(instance, RuntimeHelpers.GetUninitializedObject(stateType));
    return instance;
}
object Data() => Activator.CreateInstance(dataType, new object[] { 0, "AutoHaulingSys" })!;
void Set(object data, string method, string key, object value) => dataType.GetMethod(method, new[] { typeof(string), value.GetType() })!.Invoke(data, new[] { key, value });
T Get<T>(object data, string method, string key, T fallback) => (T)dataType.GetMethod(method, new[] { typeof(string), typeof(T) })!.Invoke(data, new object?[] { key, fallback })!;
object Save(object instance) => systemType.GetMethod("Save")!.Invoke(instance, null)!;
void Load(object instance, object? data) => systemType.GetMethod("Load")!.Invoke(instance, new[] { data });
var fresh = Save(SystemInstance());
Check(Get(fresh, "GetInt", "SaveSchemaVersion", -1) == 3 && !Get(fresh, "GetBool", "Enabled", true)
    && Get(fresh, "GetBool", "StaleStoreRefreshEnabled", false) && !Get(fresh, "GetBool", "IdleHaulingBidAssistEnabled", true),
    "real defaults: schema3, master off, stale refresh on, retired assist off");
Check(!Get(fresh, "GetBool", "PrioritizeHiveRefills", true), "new save Hive preference defaults off");
for (int schema = 0; schema <= 3; schema++) {
    foreach (bool legacyEnabled in new[] { false, true }) {
        var instance = SystemInstance();
        var data = Data();
        Set(data, "SetInt", "SaveSchemaVersion", schema);
        Set(data, "SetBool", "Enabled", true);
        Set(data, "SetBool", "IdleHaulingBidAssistEnabled", legacyEnabled);
        Set(data, "SetBool", "StaleStoreRefreshEnabled", true);
        Set(data, "SetLong", "LastIdleHaulingBidAssistTick", 9876543210L);
        Set(data, "SetInt", "LastIdleHaulingAssignments", 19);
        Set(data, "SetStringSet", "DisabledCategoryIds", new[] { "material:Ore" });
        Set(data, "SetStringSet", "EnabledItemOverrides", new[] { "object:Example" });
        Set(data, "SetStringSet", "DisabledItemOverrides", new[] { "material:Carbon" });
        Set(data, "SetIntSet", "TrackedGoalIds", new[] { -1 });
        Set(data, "SetIntSet", "TrackedObjectIds", new[] { -2 });
        Set(data, "SetIntSet", "TrackedFailureTotals", new[] { 7 });
        Set(data, "SetStringSet", "TrackedItemNames", new[] { "Example" });
        Set(data, "SetStringSet", "TrackedItemIds", new[] { "Obj/Example" });
        Set(data, "SetStringSet", "TrackedPositions", new[] { "(1.0, 2.0)" });
        string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        string record = $"123|4294967295|{B64("Example")}|{B64("Obj/Example")}|{B64("(1.0, 2.0)")}|{B64("No Storage Unit")}|{B64("Failed")}|7|1|1";
        Set(data, "SetStringSet", "FailureHistory", new[] { record });
        Load(instance, data);
        var saved = Save(instance);
        string label = $"schema{schema}/legacy{legacyEnabled}";
        Check(!Get(saved, "GetBool", "PrioritizeHiveRefills", true), label + ": missing Hive preference defaults off");
        Check(ReferenceEquals(saved, data), label + ": original container retained");
        Check(Get(saved, "GetBool", "IdleHaulingBidAssistEnabled", false) == (schema >= 3 && legacyEnabled), label + ": retired preference migration");
        // Stale refresh was introduced in schema 2; older saves opt out.
        Check(Get(saved, "GetBool", "Enabled", false) && Get(saved, "GetBool", "StaleStoreRefreshEnabled", false) == (schema >= 2)
            && Get(saved, "GetLong", "LastIdleHaulingBidAssistTick", 0L) == 9876543210L && Get(saved, "GetInt", "LastIdleHaulingAssignments", 0) == 19,
            label + ": controls and historical counters");
        Check(Get(saved, "GetStringSet", "DisabledCategoryIds", Array.Empty<string>()).SequenceEqual(new[] { "material:Ore" })
            && Get(saved, "GetStringSet", "EnabledItemOverrides", Array.Empty<string>()).SequenceEqual(new[] { "object:Example" })
            && Get(saved, "GetStringSet", "DisabledItemOverrides", Array.Empty<string>()).SequenceEqual(new[] { "material:Carbon" }),
            label + ": category/item choices");
        Check(Get(saved, "GetIntSet", "TrackedGoalIds", Array.Empty<int>()).SequenceEqual(new[] { -1 })
            && Get(saved, "GetIntSet", "TrackedObjectIds", Array.Empty<int>()).SequenceEqual(new[] { -2 })
            && Get(saved, "GetIntSet", "TrackedFailureTotals", Array.Empty<int>()).SequenceEqual(new[] { 7 })
            && Get(saved, "GetStringSet", "FailureHistory", Array.Empty<string>()).SequenceEqual(new[] { record }),
            label + ": tracked identities, attempts and failures");
        Load(instance, null);
        var reset = Save(instance);
        Check(!Get(reset, "GetBool", "Enabled", true) && Get(reset, "GetBool", "StaleStoreRefreshEnabled", false)
            && Get(reset, "GetLong", "LastIdleHaulingBidAssistTick", 0L) == 0L
            && Get(reset, "GetStringSet", "FailureHistory", Array.Empty<string>()).Length == 0,
            label + ": reset clears previous save");
    }
}
var hiveSys = SystemInstance();
var hiveData = Data();
Set(hiveData, "SetInt", "SaveSchemaVersion", 3);
Set(hiveData, "SetBool", "PrioritizeHiveRefills", true);
Load(hiveSys, hiveData);
Check(Get(Save(hiveSys), "GetBool", "PrioritizeHiveRefills", false)
    && !Get(Save(hiveSys), "GetBool", "Enabled", true), "Hive preference persists independently of cargo switch");
var systemFields = BindingFlags.NonPublic | BindingFlags.Instance;
var boundState = systemType.GetField("S")!.GetValue(hiveSys);
var useRanking = systemType.GetMethod("UsesHiveRefillRanking", systemFields)!;
bool Uses(object? state) => (bool)useRanking.Invoke(hiveSys, new[] { state })!;
HiveRankingChecks.SetInstanceField(systemType.GetField("loadedState", systemFields)!, hiveSys, boundState);
Check(!Uses(boundState), "no ranking before AUMF is ready");
HiveRankingChecks.SetInstanceField(systemType.GetField("frameworkReady", systemFields)!, hiveSys, true);
Check(Uses(boundState) && !Uses(null) && !Uses(RuntimeHelpers.GetUninitializedObject(stateType)),
    "ranking honors the opt-in only for the bound world, with cargo off");
Set(hiveData, "SetBool", "PrioritizeHiveRefills", false);
Load(hiveSys, hiveData);
Check(!Get(Save(hiveSys), "GetBool", "PrioritizeHiveRefills", true) && !Uses(boundState), "explicit off survives reload");
Set(hiveData, "SetBool", "PrioritizeHiveRefills", true);
Load(hiveSys, hiveData);
Load(hiveSys, null);
Check(!Get(Save(hiveSys), "GetBool", "PrioritizeHiveRefills", true) && !Uses(boundState), "reset clears a previously enabled Hive preference");
HiveRankingChecks.Run(core, mod, args.Skip(4).ToArray(), Check);
Console.WriteLine($"PASS_COUNT={checks}; FAIL_COUNT=0");

static string GeneratedName(string value) =>
    Regex.Replace(value, @"(b__|<>9__|<>c__DisplayClass)\d+_", "$1#_");

static string Body(MethodDefinition method) {
    var code = method.Body.Instructions;
    string Operand(object? value) => value switch {
        null => "", Instruction i => "@" + code.IndexOf(i),
        Instruction[] targets => string.Join(",", targets.Select(i => code.IndexOf(i))),
        MemberReference member => GeneratedName(member.FullName),
        _ => value.ToString()?.Replace("1.1.6", "1.2.0") ?? ""
    };
    return string.Join("|", code.Select(i => i.OpCode.Code + ":" + Operand(i.Operand)));
}
