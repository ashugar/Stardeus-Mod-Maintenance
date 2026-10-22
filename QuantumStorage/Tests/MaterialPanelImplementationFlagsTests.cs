using System;
using System.Globalization;
using System.IO;
using System.Reflection;

static partial class Program
{
    // Reflection-only fault model: never invoke a fixture or a game method.
    sealed class RuntimeFlagMethod : MethodInfo
    {
        readonly MethodInfo original;
        readonly MethodImplAttributes implementation;
        readonly MethodAttributes attributes;
        readonly Module module;
        readonly int token;
        internal RuntimeFlagMethod(MethodInfo original, MethodImplAttributes? implementation = null,
            MethodAttributes? attributes = null, Module module = null, int? token = null)
        {
            this.original = original;
            this.implementation = implementation ?? original.GetMethodImplementationFlags();
            this.attributes = attributes ?? original.Attributes;
            this.module = module ?? original.Module;
            this.token = token ?? original.MetadataToken;
        }
        public override string Name => original.Name;
        public override Type DeclaringType => original.DeclaringType;
        public override Type ReflectedType => original.ReflectedType;
        public override Type ReturnType => original.ReturnType;
        public override ParameterInfo ReturnParameter => original.ReturnParameter;
        public override ICustomAttributeProvider ReturnTypeCustomAttributes => original.ReturnTypeCustomAttributes;
        public override MethodAttributes Attributes => attributes;
        public override CallingConventions CallingConvention => original.CallingConvention;
        public override Module Module => module;
        public override int MetadataToken => token;
        public override RuntimeMethodHandle MethodHandle => original.MethodHandle;
        public override bool IsGenericMethod => original.IsGenericMethod;
        public override bool IsGenericMethodDefinition => original.IsGenericMethodDefinition;
        public override bool ContainsGenericParameters => original.ContainsGenericParameters;
        public override MethodInfo GetBaseDefinition() => original.GetBaseDefinition();
        public override MethodInfo GetGenericMethodDefinition() => original.GetGenericMethodDefinition();
        public override Type[] GetGenericArguments() => original.GetGenericArguments();
        public override ParameterInfo[] GetParameters() => original.GetParameters();
        public override MethodBody GetMethodBody() => original.GetMethodBody();
        public override MethodImplAttributes GetMethodImplementationFlags() => implementation;
        public override object[] GetCustomAttributes(bool inherit) => original.GetCustomAttributes(inherit);
        public override object[] GetCustomAttributes(Type type, bool inherit) => original.GetCustomAttributes(type, inherit);
        public override bool IsDefined(Type type, bool inherit) => original.IsDefined(type, inherit);
        public override object Invoke(object obj, BindingFlags flags, Binder binder, object[] args, CultureInfo culture) =>
            throw new NotSupportedException("Metadata-only fixture");
    }

    static void MaterialPanelImplementationFlagsTests(MethodInfo ordinary, MethodInfo authoredNoInline)
    {
        var helper = ModAssembly.GetType("Game.Systems.QuantumStorage.MaterialPanelContractFingerprint", true);
        var read = helper.GetMethod("ReadImplementationFlags", BindingFlags.Static | BindingFlags.NonPublic);
        var hash = helper.GetMethod("Hash", BindingFlags.Static | BindingFlags.NonPublic);
        Check(read != null && (read.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining) != 0,
            "authored implementation metadata remains behind a no-inline boundary");
        string HashFlags(MethodInfo method) => (string)hash.Invoke(null, new object[] { method });
        int Read(MethodInfo method) => (int)read.Invoke(null, new object[] { method });
        string baseline = HashFlags(ordinary);
        Check(baseline != null && Read(ordinary) == (int)ordinary.GetMethodImplementationFlags(),
            "ordinary authored flags retain the original canonical digest");
        string pinned = HashFlags(authoredNoInline);
        Check(pinned != null && pinned != baseline &&
            (Read(authoredNoInline) & (int)MethodImplAttributes.NoInlining) != 0,
            "authored NoInlining is retained and changes the digest");
        bool mono = typeof(object).Assembly.GetType("Mono.Runtime", false) != null;
        string runtimeOnly = HashFlags(new RuntimeFlagMethod(ordinary,
            ordinary.GetMethodImplementationFlags() | MethodImplAttributes.NoInlining));
        Check(mono ? runtimeOnly == baseline : runtimeOnly == null,
            "runtime-only NoInlining allowance is confined to Mono");
        Check(HashFlags(new RuntimeFlagMethod(authoredNoInline,
            authoredNoInline.GetMethodImplementationFlags() & ~MethodImplAttributes.NoInlining)) == null,
            "clearing an authored implementation flag is refused");
        foreach (var flag in new[] { MethodImplAttributes.NoOptimization, MethodImplAttributes.Synchronized,
            MethodImplAttributes.AggressiveInlining, (MethodImplAttributes)0x40000000 })
            Check(HashFlags(new RuntimeFlagMethod(ordinary, ordinary.GetMethodImplementationFlags() | flag)) == null,
                "unaccounted runtime implementation flag is refused: " + (int)flag);
        Check(HashFlags(new RuntimeFlagMethod(ordinary,
            attributes: ordinary.Attributes ^ MethodAttributes.Final)) == null,
            "runtime method attributes must match authored attributes exactly");
        void Refuses(MethodInfo method, string label)
        {
            try { Read(method); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException || ex.InnerException is FileNotFoundException)
            { Check(true, label); return; }
            throw new InvalidOperationException(label);
        }
        foreach (int token in new[] { 0x06000000, 0x06ffffff, 0x04000001, 0x2b000001 })
            Refuses(new RuntimeFlagMethod(ordinary, token: token), "authored flag reader refuses invalid table or row");
        Refuses(new RuntimeFlagMethod(ordinary, module: new SignatureModuleIdentity(ordinary.Module, false)),
            "authored flag reader refuses a file/loaded MVID mismatch");
        Refuses(new RuntimeFlagMethod(ordinary, module: new SignatureModuleIdentity(ordinary.Module, true)),
            "authored flag reader refuses an unavailable module file");
    }
}
