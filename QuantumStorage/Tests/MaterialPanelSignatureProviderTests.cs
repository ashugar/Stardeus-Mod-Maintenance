using System;
using System.IO;
using System.Linq;
using System.Reflection;

static partial class Program
{
    sealed class SignatureModuleIdentity : Module
    {
        readonly Module original;
        readonly bool missingFile;
        internal SignatureModuleIdentity(Module original, bool missingFile)
        { this.original = original; this.missingFile = missingFile; }
        public override string FullyQualifiedName => missingFile ? original.FullyQualifiedName + ".missing" : original.FullyQualifiedName;
        public override Guid ModuleVersionId => missingFile ? original.ModuleVersionId : Guid.Empty;
    }

    static void MaterialPanelSignatureProviderTests(Module module)
    {
        var helper = ModAssembly.GetType("Game.Systems.QuantumStorage.MaterialPanelContractFingerprint", true);
        var bridge = helper.GetMethod("ReadMetadataSignature", BindingFlags.NonPublic | BindingFlags.Static);
        var validate = helper.GetMethod("CheckSignature", BindingFlags.NonPublic | BindingFlags.Static);
        Check(bridge != null && validate != null, "compiled signature provider and validator exist");
        Check((bridge.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining) != 0,
            "metadata reader retains its guarded no-inline dependency boundary");
        byte[] Read(Module source, int token, out int related)
        {
            object[] args = { source, token, 0 };
            var bytes = (byte[])bridge.Invoke(null, args);
            related = (int)args[2];
            return bytes;
        }
        void Refuses(Action action, string label, Type expected)
        {
            try { action(); }
            catch (TargetInvocationException ex) when (expected.IsInstanceOfType(ex.InnerException))
            { Check(true, label); return; }
            throw new InvalidOperationException(label);
        }
        // CoreCLR supports these three token kinds. Compare the compiled Mono
        // provider with that independent runtime reader; Unity Mono parity is a
        // separate exact-runtime probe, since its API rejects the same tokens.
        foreach (int table in new[] { 0x04000000, 0x06000000, 0x1b000000 })
        {
            int token = table | 1;
            Check(Read(module, token, out int related).SequenceEqual(module.ResolveSignature(token)) && related == 0,
                "metadata blob equals CoreCLR reflection for table " + table.ToString("X8"));
        }
        foreach (int table in new[] { 0x04000000, 0x06000000, 0x0a000000, 0x1b000000, 0x2b000000 })
        {
            Refuses(() => Read(module, table, out _), "provider refuses row zero", typeof(InvalidDataException));
            Refuses(() => Read(module, table | 0x00ffffff, out _), "provider refuses out-of-range row", typeof(InvalidDataException));
            Refuses(() => Read(new SignatureModuleIdentity(module, false), table | 1, out _),
                "provider refuses loaded/file MVID mismatch", typeof(InvalidDataException));
            Refuses(() => Read(new SignatureModuleIdentity(module, true), table | 1, out _),
                "provider refuses unavailable module file", typeof(FileNotFoundException));
        }
        foreach (int token in new[] { 0, 0x01000001, 0x02000001, 0x11000001, 0x70000001, unchecked((int)0xff000001) })
            Refuses(() => Read(module, token, out _), "bridge refuses unsupported token table", typeof(InvalidDataException));
        foreach (int token in new[] { 0, 0x01000001, 0x02000001, 0x70000001, unchecked((int)0xff000001) })
            Refuses(() => validate.Invoke(null, new object[] { module, token, 0 }),
                "signature validator refuses unsupported token table", typeof(InvalidDataException));
        validate.Invoke(null, new object[] { module, 0x11000001, 0 });
        Check(true, "supported standalone local signature retains reflection validation");
    }
}
