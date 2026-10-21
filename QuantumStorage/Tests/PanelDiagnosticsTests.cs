extern alias panelcecil;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Game.Constants;
using C = panelcecil::Mono.Cecil;
using IL = panelcecil::Mono.Cecil.Cil;

static partial class Program
{
    const BindingFlags DiagnosticInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags DiagnosticStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // These are inert identities only. Neither a MatType constructor nor any
    // definition, localization, Unity, storage or inventory method is invoked.
    static MatType DiagnosticKey() => (MatType)RuntimeHelpers.GetUninitializedObject(typeof(MatType));
    static Dictionary<MatType, int> DiagnosticContents(params (MatType Key, int Count)[] items)
    {
        var result = new Dictionary<MatType, int>(ReferenceEqualityComparer.Instance);
        foreach (var item in items) { result.Add(item.Key, item.Count); }
        return result;
    }

    sealed class DiagnosticRecorder
    {
        readonly Type type;
        internal readonly object Instance;
        internal DiagnosticRecorder()
        {
            type = ModAssembly.GetType("Game.Systems.QuantumStorage.PanelDiagnosticRecorder", true);
            Instance = Activator.CreateInstance(type, nonPublic: true);
        }
        internal T Get<T>(string field) => (T)type.GetField(field, DiagnosticInstance).GetValue(Instance);
        internal object Run(string method, params object[] args) => type.GetMethod(method, DiagnosticInstance).Invoke(Instance, args);
        internal void Raw(Dictionary<MatType, int> contents) => Run("Raw", contents);
        internal void Frame(double ms, int state) => Run("Frame", ms, state);
        internal void Call(double ms, bool native, string reason, int frame) => Run("Call", ms, native, reason, frame);
        internal void Probe(double ms, double seconds) => Run("AddProbe", ms, seconds);
    }

    static void PanelDiagnosticsTests()
    {
        int first = checks;
        PanelDiagnosticIdentityTests();
        PanelDiagnosticGapAndLimitTests();
        PanelDiagnosticFrameAndProbeTests();
        PanelDiagnosticExceptionTests();
        PanelDiagnosticOutcomeScopeTests();
        PanelDiagnosticSinkFaultTests();
        Console.WriteLine($"PASS: {checks-first} diagnostic checks; compiled recorder identity deltas, caps, timing buckets, privacy and isolated sink-fault guard parity. No Unity callbacks or diagnostic hooks executed.");
    }

    static void PanelDiagnosticIdentityTests()
    {
        var a = DiagnosticKey(); var b = DiagnosticKey(); var replacement = DiagnosticKey();
        Check(!ReferenceEquals(a, b), "diagnostic fixtures have distinct native material identities without initialization");
        var input = DiagnosticContents((a, 0), (b, 7));
        var r = new DiagnosticRecorder();
        r.Raw(input);
        Check(r.Get<int>("RawSamples") == 1 && r.Get<int>("Compared") == 0 && r.Get<int>("Work") == 2,
            "first raw snapshot establishes a baseline and charges each copied identity once");
        Check(input.Count == 2 && input[a] == 0 && input[b] == 7, "recorder leaves zero-valued keys and source quantities unchanged");
        input[b] = 9;
        r.Raw(input);
        Check(r.Get<int>("Changed") == 1 && r.Get<int>("QuantityOnly") == 1 && r.Get<int>("Membership") == 0,
            "quantity-only update is distinguished from membership churn");
        Check(r.Get<int>("Work") == 8, "comparison work includes current copy, current comparison and previous membership scan");
        input.Remove(b); input.Add(replacement, 9);
        r.Raw(input);
        Check(r.Get<int>("Added") == 1 && r.Get<int>("Removed") == 1 && r.Get<int>("Membership") == 1,
            "same-sized replacement is counted as one added and one removed material identity");
        Check(r.Get<int>("Changed") == 1, "replacement is not miscounted as a quantity change to the removed identity");
        r.Raw(DiagnosticContents((replacement, 9), (a, 0)));
        Check(r.Get<int>("Identical") == 1 && r.Get<int>("Added") == 1 && r.Get<int>("Removed") == 1,
            "dictionary enumeration order and replacement dictionary object do not create raw identity churn");
        input[a] = 4; r.Raw(input);
        Check(r.Get<int>("Changed") == 2 && r.Get<int>("QuantityOnly") == 2,
            "a retained zero-valued key becoming positive remains a quantity-only update");
        input.Remove(a); r.Raw(input);
        Check(r.Get<int>("Removed") == 2 && r.Get<int>("Membership") == 2,
            "actual removal of the previously zero-valued identity is structural");
        string changes = (string)r.Run("Changes");
        Check(changes.Contains("native_order=unavailable") && changes.Contains("timing=inclusive_not_exclusive"),
            "summary explicitly preserves native-order and inclusive-timing coverage limits");
    }

    static void PanelDiagnosticGapAndLimitTests()
    {
        var a = DiagnosticKey(); var b = DiagnosticKey();
        var r = new DiagnosticRecorder();
        r.Raw(DiagnosticContents((a, 1))); r.Run("Gap"); r.Raw(DiagnosticContents((b, 2)));
        Check(r.Get<int>("Compared") == 0 && r.Get<int>("Added") == 0 && r.Get<int>("Removed") == 0,
            "explicit observation gap prevents invented deltas across missing evidence");
        r.Raw(null); r.Raw(DiagnosticContents((a, 3)));
        Check(r.Get<int>("RawSkipped") == 1 && r.Get<int>("Compared") == 0,
            "null contents are skipped and invalidate the comparison baseline");
        r.Run("Switch"); r.Raw(DiagnosticContents((b, 3))); r.Raw(DiagnosticContents((b, 3)));
        Check(r.Get<int>("Switches") == 1 && r.Get<int>("Compared") == 1 && r.Get<int>("Identical") == 1,
            "panel switch resets identity baseline while later comparisons remain available");

        var large = new Dictionary<MatType, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < 4097; i++) { large.Add(DiagnosticKey(), i); }
        r = new DiagnosticRecorder(); r.Raw(large);
        Check(r.Get<int>("RawSamples") == 0 && r.Get<int>("RawSkipped") == 1 && r.Get<int>("Work") == 0,
            "oversized dictionary is rejected before any raw enumeration or charged work");
        Check(r.Get<bool>("Detailed"), "one oversized snapshot does not retire otherwise bounded detailed capture");
        r.Raw(DiagnosticContents((a, 1))); r.Raw(large); r.Raw(DiagnosticContents((b, 1)));
        Check(r.Get<int>("Compared") == 0 && r.Get<int>("Added") == 0 && r.Get<int>("Removed") == 0,
            "oversize gap cannot fabricate cross-gap key additions or removals");

        var one = DiagnosticContents((a, 0));
        r = new DiagnosticRecorder();
        for (int i = 0; i < 512; i++) { r.Raw(one); }
        Check(r.Get<int>("RawSamples") == 512 && r.Get<bool>("Detailed") && r.Get<int>("Compared") == 511,
            "exact raw-sample limit remains usable until the next attempted sample");
        r.Raw(one);
        Check(!r.Get<bool>("Detailed") && r.Get<string>("DetailEnd") == "sample_cap" && r.Get<int>("RawSamples") == 512,
            "raw-sample limit retires detailed capture without exceeding the cap");
        Check(r.Get<int>("RawSkipped") == 1 && r.Get<int>("Work") == 1534,
            "sample-cap rejection charges no extra raw work");

        large.Remove(large.Keys.Last());
        r = new DiagnosticRecorder();
        for (int i = 0; i < 22; i++) { r.Raw(large); }
        Check(r.Get<int>("RawSamples") == 22 && r.Get<int>("Work") == 262144 && r.Get<bool>("Detailed"),
            "4096-key snapshots account exactly for the full bounded comparison-work budget");
        r.Raw(large);
        Check(!r.Get<bool>("Detailed") && r.Get<string>("DetailEnd") == "work_cap" && r.Get<int>("Work") == 262144,
            "next snapshot retires at work cap before copying or comparing extra identities");
        r.Call(0.5, true, null, 1); r.Frame(20, 1); r.Raw(one);
        Check(r.Get<int>("Calls") == 1 && r.Get<int>("Frames") == 1 && r.Get<int>("RawSkipped") == 2,
            "coarse builder and frame counters remain usable after detailed work retirement");
        r.Run("Clear");
        Check(r.Get<object>("previous") == null && r.Get<object>("current") == null,
            "terminal clear drops both dictionaries and their retained material references");
    }

    static void PanelDiagnosticFrameAndProbeTests()
    {
        var r = new DiagnosticRecorder();
        r.Frame(16.667, 1); r.Frame(25, 2); r.Frame(33.334, 0);
        r.Frame(50, 1); r.Frame(100, 2); r.Frame(101, 0);
        r.Frame(double.NaN, 1); r.Frame(-1, 1); r.Frame(double.PositiveInfinity, 1);
        Check(r.Get<int>("Frames") == 6 && r.Get<int>("OpenFrames") == 2 && r.Get<int>("ClosedFrames") == 2 && r.Get<int>("UnknownFrames") == 2,
            "frame state categories remain distinct and invalid intervals are ignored");
        Check(r.Get<int[]>("frameBuckets").SequenceEqual(new[] { 1, 1, 1, 1, 1, 1 }),
            "frame histogram includes exact documented upper boundaries and the over-100ms bucket");
        Check(Math.Abs(r.Get<double>("FrameMs") - 326.001) < 0.000001 && r.Get<double>("FrameMaxMs") == 101,
            "frame totals and maximum retain measured duration independently of builder refreshes");
        r.Call(1, true, null, 10); r.Call(2, false, "reused", 10); r.Call(3, true, "structural-refused", 10);
        r.Call(4, true, "exception", 11); r.Call(5, true, "capture_success", 12);
        r.Call(6, true, "captured", 13);
        Check(r.Get<int>("Calls") == 6 && r.Get<int>("Native") == 5 && r.Get<int>("Skipped") == 1 && r.Get<int>("Reused") == 1,
            "native execution, skipped original and observed Quantum reuse are separately counted");
        Check(r.Get<int>("Refused") == 1 && r.Get<int>("Exceptions") == 1 && r.Get<int>("SameFrameCalls") == 2,
            "capture success is not a refusal and duplicate same-frame calls do not count as frames");
        Check(r.Get<double>("CallMs") == 21 && r.Get<double>("CallMaxMs") == 6 && r.Get<int>("Frames") == 6,
            "builder durations do not alter independently sampled frame statistics");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(((string)r.Run("Summary", 1.25, "fixture")).Contains("elapsed_s=1.25"),
                "diagnostic numeric summary remains culture independent");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        r = new DiagnosticRecorder();
        for (int i = 0; i < 2050; i++) { r.Call(1, true, null, 17); }
        Check(r.Get<int>("Calls") == 2048 && r.Get<double>("CallMs") == 2048 && r.Get<int>("SameFrameCalls") == 2047,
            "call cap bounds recorded builder samples and duplicate-call counters exactly");

        r = new DiagnosticRecorder(); r.Probe(2, 0);
        Check(r.Get<bool>("Detailed"), "a probe exactly at 2ms does not exceed the single-observation threshold");
        r.Probe(2.001, 0);
        Check(!r.Get<bool>("Detailed") && r.Get<string>("DetailEnd") == "probe_2ms" && r.Get<long>("ProbeSamples") == 2,
            "single-observation overhead above 2ms retires detailed capture");
        Check(r.Get<double>("ProbeMaxMs") == 2.001 && Math.Abs(r.Get<double>("ProbeMs") - 4.001) < 0.000001,
            "probe overhead retains the retiring observation instead of hiding it");
        r.Call(0.25, true, null, 18); r.Frame(10, 1);
        Check(r.Get<int>("Calls") == 1 && r.Get<int>("Frames") == 1,
            "coarse telemetry survives overhead retirement");
        r = new DiagnosticRecorder();
        for (int i = 0; i < 20; i++) { r.Probe(1, 2); }
        Check(r.Get<bool>("Detailed") && r.Get<double>("ProbeMs") == 20,
            "exactly one percent cumulative overhead does not exceed threshold");
        r.Probe(1, 2);
        Check(!r.Get<bool>("Detailed") && r.Get<string>("DetailEnd") == "probe_1pct",
            "sustained overhead beyond one percent retires detailed capture");
        r = new DiagnosticRecorder();
        for (int i = 0; i < 30; i++) { r.Probe(1, 1.99); }
        Check(r.Get<bool>("Detailed"), "cumulative overhead threshold waits for the minimum elapsed window");
        r.Probe(0, 2);
        Check(!r.Get<bool>("Detailed") && r.Get<string>("DetailEnd") == "probe_1pct",
            "the minimum window activates the cumulative threshold without a synthetic duration spike");
    }

    sealed class DiagnosticMessageTrap : Exception
    {
        internal static int MessageReads;
        internal DiagnosticMessageTrap(int code, Exception inner = null) : base("private path must not be read", inner) { HResult = code; }
        public override string Message { get { MessageReads++; throw new InvalidOperationException("message access forbidden"); } }
    }

    static void PanelDiagnosticExceptionTests()
    {
        var diagnostics = ModAssembly.GetType("Game.Systems.QuantumStorage.QuantumPanelDiagnostics", true);
        var code = diagnostics.GetMethod("ExceptionCode", DiagnosticStatic);
        string Format(Exception exception) => (string)code.Invoke(null, new object[] { exception });
        Check(Format(null) == "", "null exception produces no invented exception evidence");
        var tail = new DiagnosticMessageTrap(unchecked((int)0xD1A00004));
        var third = new DiagnosticMessageTrap(unchecked((int)0xD1A00003), tail);
        var second = new DiagnosticMessageTrap(unchecked((int)0xD1A00002), third);
        var first = new DiagnosticMessageTrap(unchecked((int)0xD1A00001), second);
        string text = Format(first);
        Check(text == "OtherException:D1A00001/OtherException:D1A00002/OtherException:D1A00003",
            "exception formatting uses a fixed whitelist and at most three numeric identities");
        Check(DiagnosticMessageTrap.MessageReads == 0 && !text.Contains(nameof(DiagnosticMessageTrap)),
            "unknown exception type names and potentially private or throwing messages are not inspected");
        string loaderPath = Path.Combine(Path.GetTempPath(), "private-loader-fixture");
        text = Format(new TypeInitializationException(loaderPath, new FileNotFoundException(loaderPath)));
        Check(text.StartsWith("TypeInitializationException:", StringComparison.Ordinal) && text.Contains("/FileNotFoundException:") &&
            !text.Contains("private") && !text.Contains(loaderPath), "known nested loader errors are categorized without their message, path or type-initializer name");
        var hashType = ModAssembly.GetType("Game.Systems.QuantumStorage.MaterialPanelContractFingerprint", true);
        var hash = hashType.GetMethod("Hash", DiagnosticStatic);
        var diagnosticHash = hashType.GetMethod("HashDiagnostic", DiagnosticStatic);
        var core = typeof(Game.Systems.UIUX.MatStorageTab).GetMethod("BuildContentsList", DiagnosticInstance);
        object result = diagnosticHash.Invoke(null, new object[] { core });
        string Value(object value, string name) => (string)value.GetType().GetField(name, DiagnosticInstance).GetValue(value);
        Check(Value(result, "Hash") == (string)hash.Invoke(null, new object[] { core }) && Value(result, "Reason") == "hashed",
            "detailed production hash preserves the ordinary reviewed contract fingerprint");
        result = diagnosticHash.Invoke(null, new object[] { null });
        Check(Value(result, "Hash") == null && Value(result, "Reason") == "missing-method",
            "detailed missing-method evidence preserves the ordinary fail-closed null result");
        // DynamicMethod has no inspectable MethodBody here. It is never emitted,
        // delegated or executed; this tests a real reflection refusal boundary.
        var unavailable = new System.Reflection.Emit.DynamicMethod("DiagnosticMetadataRefusal", typeof(void), Type.EmptyTypes);
        result = diagnosticHash.Invoke(null, new object[] { unavailable });
        Check(Value(result, "Hash") == null && Value(result, "Reason") == "exception" && Value(result, "Stage") == "body" &&
            Value(result, "ExceptionType") == "InvalidOperationException",
            "unavailable reflection body produces bounded failure category and stage without becoming accepted");
        Check((string)hash.Invoke(null, new object[] { unavailable }) == null,
            "detailed reflection refusal preserves the ordinary exception-to-null contract");
        Check((string)hash.Invoke(null, new object[] { core }) == Value(diagnosticHash.Invoke(null, new object[] { core }), "Hash"),
            "prior refusal evidence cannot contaminate a later successful hash");
        Check(!(bool)hashType.GetField("tracing", DiagnosticStatic).GetValue(null),
            "diagnostic hash restores thread-local tracing after success and refusal");
    }

    static void PanelDiagnosticSinkFaultTests()
    {
        // Fault-inject the logging boundary in a temporary test COPY. No production
        // file changes, real log sink, Harmony detours or Unity methods are used.
        string root = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "PanelDiagnosticSink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string fixturePath = Path.Combine(root, "QuantumStorage.dll");
        var context = new AssemblyLoadContext("PanelDiagnosticSink-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using (var module = C.ModuleDefinition.ReadModule(ModAssembly.Location))
            {
                var diagnostics = module.Types.Single(t => t.FullName == "Game.Systems.QuantumStorage.QuantumPanelDiagnostics");
                var write = diagnostics.Methods.Single(m => m.Name == "Write");
                write.Body = new IL.MethodBody(write);
                write.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldstr, "diagnostic sink fault fixture"));
                write.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Newobj,
                    module.ImportReference(typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }))));
                write.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Throw));
                module.Write(fixturePath);
            }
            context.Resolving += (_, name) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name.Name);
            var fixture = context.LoadFromAssemblyPath(fixturePath);
            var diagnosticsType = fixture.GetType("Game.Systems.QuantumStorage.QuantumPanelDiagnostics", true);
            diagnosticsType.GetField("enabled", DiagnosticStatic).SetValue(null, true);
            bool threw = false;
            try { diagnosticsType.GetMethod("Write", DiagnosticStatic).Invoke(null, new object[] { "fixture", false }); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { threw = true; }
            Check(threw, "test-copy sink failure is real and does not invoke the game's logging boundary");
            diagnosticsType.GetMethod("Guard", DiagnosticStatic).Invoke(null, new object[] { "fixture=guard" });
            diagnosticsType.GetMethod("GuardException", DiagnosticStatic).Invoke(null, new object[] { "fixture", new DiagnosticMessageTrap(-1) });
            Check((int)diagnosticsType.GetField("startupLines", DiagnosticStatic).GetValue(null) == 2,
                "both guard wrappers contain an actual throwing diagnostic sink");
            var patch = fixture.GetType("Game.Systems.QuantumStorage.QuantumMaterialPanelPatch", true);
            Check((bool)patch.GetMethod("Prepare", DiagnosticStatic).Invoke(null, null),
                "reviewed installed Core remains accepted when every attempted guard diagnostic write throws");
            Check(!(bool)patch.GetMethod("IsReviewedBody", DiagnosticStatic).Invoke(null, new object[] { null }),
                "sink fault cannot turn a missing native contract into acceptance");
            var unrelated = typeof(Program).GetMethod("PrintUiHash", DiagnosticStatic);
            Check(!(bool)patch.GetMethod("IsReviewedBody", DiagnosticStatic).Invoke(null, new object[] { unrelated }),
                "sink fault cannot approve an unrelated native body");
            for (int i = 0; i < 20; i++) { diagnosticsType.GetMethod("Guard", DiagnosticStatic).Invoke(null, new object[] { "fixture=bounded" }); }
            Check((int)diagnosticsType.GetField("startupLines", DiagnosticStatic).GetValue(null) == 8,
                "regular startup attempts cap at eight even on sink failure, reserving two records for actual capture census and state");
            Check(DiagnosticMessageTrap.MessageReads == 0,
                "guard fault isolation never reads arbitrary nested exception messages");
        }
        finally { context.Unload(); }
    }

    static void PanelDiagnosticOutcomeScopeTests()
    {
        var type = ModAssembly.GetType("Game.Systems.QuantumStorage.QuantumPanelDiagnostics", true);
        string[] names = { "running", "depth", "outcomes", "observing", "observedTabs", "recorder" };
        var fields = names.ToDictionary(name => name, name => type.GetField(name, DiagnosticStatic));
        Check(fields.Values.All(field => field != null), "production observer retains explicitly scoped thread-local tab identities");
        var before = names.ToDictionary(name => name, name => fields[name].GetValue(null));
        try
        {
            var outer = (Game.Systems.UIUX.MatStorageTab)RuntimeHelpers.GetUninitializedObject(typeof(Game.Systems.UIUX.MatStorageTab));
            var other = (Game.Systems.UIUX.MatStorageTab)RuntimeHelpers.GetUninitializedObject(typeof(Game.Systems.UIUX.MatStorageTab));
            var inner = (Game.Systems.UIUX.MatStorageTab)RuntimeHelpers.GetUninitializedObject(typeof(Game.Systems.UIUX.MatStorageTab));
            var reasons = new[] { "outer-fixture", "inner-fixture", null, null };
            var observing = new[] { true, true, false, false };
            var tabs = new[] { outer, inner, null, null };
            var recorder = new DiagnosticRecorder();
            fields["running"].SetValue(null, true); fields["depth"].SetValue(null, 1);
            fields["outcomes"].SetValue(null, reasons); fields["observing"].SetValue(null, observing);
            fields["observedTabs"].SetValue(null, tabs); fields["recorder"].SetValue(null, recorder.Instance);
            var outcome = type.GetMethod("Outcome", DiagnosticStatic);
            outcome.Invoke(null, new object[] { other, "runtime-disable" });
            Check(reasons[0] == "outer-fixture", "unobserved nested tab cannot overwrite the outer managed panel reason");
            outcome.Invoke(null, new object[] { outer, "reused" });
            Check(reasons[0] == "reused", "matching observed tab can report its own reuse outcome");
            fields["depth"].SetValue(null, 2);
            outcome.Invoke(null, new object[] { outer, "runtime-disable" });
            Check(reasons[1] == "inner-fixture" && reasons[0] == "reused",
                "managed outer tab cannot overwrite a different active nested tab reason");
            outcome.Invoke(null, new object[] { inner, "captured" });
            Check(reasons[1] == "captured" && recorder.Get<int>("Captures") == 1,
                "matching nested tab reports capture exactly once in the active depth");
            outcome.Invoke(null, new object[] { other, "captured" });
            Check(recorder.Get<int>("Captures") == 1, "unobserved tab cannot inflate capture counters");
            fields["depth"].SetValue(null, 5);
            outcome.Invoke(null, new object[] { outer, "runtime-disable" });
            fields["depth"].SetValue(null, 0);
            outcome.Invoke(null, new object[] { outer, "runtime-disable" });
            Check(reasons[0] == "reused" && reasons[1] == "captured",
                "zero or excessive depth safely ignores outcomes without touching an array slot");
        }
        finally { foreach (string name in names) { fields[name].SetValue(null, before[name]); } }
    }
}
