extern alias panelcecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using C = panelcecil::Mono.Cecil;
using IL = panelcecil::Mono.Cecil.Cil;

static partial class Program
{
    // These fields exist only for reflection schema checks; no instance is created.
#pragma warning disable CS0169, CS0649
    class ContractFieldBase { protected string Inherited; }
    sealed class ContractFields : ContractFieldBase
    {
        private string Text;
        public string PublicText;
        private static string StaticText;
        private readonly string ReadonlyText;
        private const string ConstantText = "fixture";
        private int WrongType;
    }
#pragma warning restore CS0169, CS0649

    sealed class ContractFixture
    {
        internal string Path;
        internal MethodInfo Method;
        internal AssemblyLoadContext Context;
    }

    static void MaterialPanelContractFingerprintTests()
    {
        int firstCheck = checks;
        var fingerprint = ModAssembly.GetType("Game.Systems.QuantumStorage.MaterialPanelContractFingerprint", true)
            .GetMethod("Hash", BindingFlags.NonPublic | BindingFlags.Static);
        Check(fingerprint != null, "compiled production canonical fingerprint entry point exists");
        string Fingerprint(MethodInfo method) => (string)fingerprint.Invoke(null, new object[] { method });
        // Keep executable metadata fixtures beside the harness in ignored output.
        var root = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Program).Assembly.Location),
            "QuantumPanelContract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixtures = new List<ContractFixture>();
        ContractFixture Fixture(string name, Action<C.ModuleDefinition, C.MethodDefinition> change = null, bool shifted = false,
            Action<byte[], int, C.MethodDefinition> corrupt = null)
        {
            var fixture = CreateContractFixture(root, name, change, shifted, corrupt);
            fixtures.Add(fixture);
            return fixture;
        }
        try
        {
            var baseline = Fixture("baseline");
            MaterialPanelSignatureProviderTests(baseline.Method.Module);
            string reviewed = Fingerprint(baseline.Method);
            MaterialPanelImplementationFlagsTests(baseline.Method,
                Fixture("authored-noinline", (_, m) => m.ImplAttributes |= C.MethodImplAttributes.NoInlining).Method);
            Check(reviewed != null && reviewed.Length == 64 && reviewed.All(c => "0123456789ABCDEF".Contains(c)),
                "supported fixture produces an uppercase SHA256 fingerprint");
            var shifted = Fixture("shifted", shifted: true);
            Check(baseline.Method.Module.FullyQualifiedName != shifted.Method.Module.FullyQualifiedName &&
                baseline.Method.Module.ModuleVersionId != shifted.Method.Module.ModuleVersionId &&
                baseline.Method.Module.Assembly.GetName().Version != shifted.Method.Module.Assembly.GetName().Version,
                "benign fixture independently changes path, MVID and assembly version");
            Check(!baseline.Method.GetMethodBody().GetILAsByteArray().SequenceEqual(shifted.Method.GetMethodBody().GetILAsByteArray()),
                "benign metadata padding actually changes the raw target body bytes");
            AssertShiftedFixtureTokens(baseline, shifted);
            Check(reviewed == Fingerprint(shifted.Method),
                "same contract survives independently shifted metadata tokens, path, MVID and version");

            void Changed(string name, Action<C.ModuleDefinition, C.MethodDefinition> change)
            {
                string actual = Fingerprint(Fixture(name, change).Method);
                Check(actual != null && actual != reviewed, "changed inspected contract has a distinct supported fingerprint: " + name);
            }
            Changed("literal", (_, m) => m.Body.Instructions.Single(i => i.OpCode.Code == IL.Code.Ldstr).Operand = "different quantity");
            Changed("integer", (_, m) => m.Body.Instructions.First(i => i.OpCode.Code == IL.Code.Ldc_I4).Operand = 12);
            Changed("called member", (_, m) => m.Body.Instructions.Single(i => i.Operand is C.MethodDefinition)
                .Operand = m.DeclaringType.Methods.Single(x => x.Name == "Other"));
            Changed("called overload", (module, m) => {
                var overload = new C.MethodDefinition("Keep", C.MethodAttributes.Public | C.MethodAttributes.Static, module.TypeSystem.Int32);
                overload.Parameters.Add(new C.ParameterDefinition(module.TypeSystem.Int64));
                overload.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldc_I4_0));
                overload.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
                m.DeclaringType.Methods.Add(overload);
                m.Body.Instructions.Single(i => i.Operand is C.MethodDefinition).Operand = overload;
            });
            Changed("called member owner", (module, m) => {
                var owner = new C.TypeDefinition("PanelFixtures", "OtherOwner", C.TypeAttributes.Public | C.TypeAttributes.Class, module.TypeSystem.Object);
                module.Types.Add(owner);
                var helper = new C.MethodDefinition("Keep", C.MethodAttributes.Public | C.MethodAttributes.Static, module.TypeSystem.Int32);
                helper.Parameters.Add(new C.ParameterDefinition(module.TypeSystem.Int32));
                helper.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldarg_0));
                helper.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
                owner.Methods.Add(helper);
                m.Body.Instructions.Single(i => i.Operand is C.MethodDefinition).Operand = helper;
            });
            Changed("framework type name in untrusted assembly", (module, m) => {
                // Same System.String/full member signature, but owned by this
                // fixture assembly instead of the trusted framework core library.
                var owner = new C.TypeDefinition("System", "String", C.TypeAttributes.Public | C.TypeAttributes.Class, module.TypeSystem.Object);
                module.Types.Add(owner);
                var helper = new C.MethodDefinition("IsNullOrEmpty", C.MethodAttributes.Public | C.MethodAttributes.Static, module.TypeSystem.Boolean);
                helper.Parameters.Add(new C.ParameterDefinition(module.TypeSystem.String));
                helper.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldc_I4_0));
                helper.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
                owner.Methods.Add(helper);
                m.Body.Instructions.Single(i => i.Operand is C.MethodReference call && call.Name == "IsNullOrEmpty").Operand = helper;
            });
            Changed("field identity", (_, m) => m.Body.Instructions.Single(i => i.OpCode.Code == IL.Code.Ldsfld)
                .Operand = m.DeclaringType.Fields.Single(x => x.Name == "OtherCounter"));
            Changed("field type", (module, m) => m.DeclaringType.Fields.Single(x => x.Name == "Counter").FieldType = module.TypeSystem.Int64);
            Changed("closed type argument", (module, m) => m.Body.Instructions.Single(i => i.Operand is C.GenericInstanceType)
                .Operand = module.ImportReference(typeof(List<string>)));
            Changed("closed method argument", (module, m) => ((C.GenericInstanceMethod)m.Body.Instructions
                .Single(i => i.Operand is C.GenericInstanceMethod).Operand).GenericArguments[0] = module.TypeSystem.String);
            Changed("branch target", (_, m) => m.Body.Instructions.Single(i => i.OpCode.Code == IL.Code.Brfalse)
                .Operand = m.Body.Instructions.Last(i => i.OpCode.Code == IL.Code.Ldc_I4));
            Changed("branch opcode encoding", (_, m) => m.Body.Instructions.Single(i => i.OpCode.Code == IL.Code.Brfalse).OpCode = IL.OpCodes.Brfalse_S);
            Changed("local type", (module, m) => m.Body.Variables[0].VariableType = module.TypeSystem.Int64);
            Changed("extra local", (module, m) => m.Body.Variables.Add(new IL.VariableDefinition(module.TypeSystem.Object)));
            Changed("pinned local", (_, m) => m.Body.Variables[0].VariableType = new C.PinnedType(m.Body.Variables[0].VariableType));
            Changed("InitLocals", (_, m) => m.Body.InitLocals = !m.Body.InitLocals);
            Changed("parameter signature", (module, m) => m.Parameters[0].ParameterType = module.TypeSystem.Int64);
            Changed("byref signature", (module, m) => m.Parameters[0].ParameterType = new C.ByReferenceType(module.TypeSystem.Int32));
            Changed("return signature", (module, m) => m.ReturnType = module.TypeSystem.Int64);
            Changed("method attributes", (_, m) => m.Attributes = (m.Attributes & ~C.MethodAttributes.MemberAccessMask) | C.MethodAttributes.Private);
            Changed("implementation flags", (_, m) => m.ImplAttributes |= C.MethodImplAttributes.NoInlining);
            Changed("named class becomes struct", (module, m) => {
                m.DeclaringType.BaseType = module.ImportReference(typeof(ValueType));
                m.DeclaringType.Attributes |= C.TypeAttributes.Sealed;
            });
            Changed("named class becomes interface", (_, m) => {
                m.DeclaringType.BaseType = null;
                m.DeclaringType.Attributes |= C.TypeAttributes.Interface | C.TypeAttributes.Abstract;
            });
            var changedMaxStack = Fixture("MaxStack", corrupt: (bytes, start, _) => {
                Check((bytes[start - 12] & 3) == 3 && (BitConverter.ToUInt16(bytes, start - 12) >> 12) == 3, "MaxStack fixture has its expected fat IL header");
                ushort original = BitConverter.ToUInt16(bytes, start - 10);
                BitConverter.GetBytes(checked((ushort)(original + 1))).CopyTo(bytes, start - 10);
            });
            Check(Fingerprint(changedMaxStack.Method) != null && Fingerprint(changedMaxStack.Method) != reviewed,
                "changed declared MaxStack cannot share the original contract");

            foreach (bool single in new[] { true, false })
            {
                var positiveZero = Fixture(single ? "single positive zero" : "double positive zero", (module, m) => MakeFloatingBody(module, m, single, false));
                var negativeZero = Fixture(single ? "single negative zero" : "double negative zero", (module, m) => MakeFloatingBody(module, m, single, true));
                Check(Fingerprint(positiveZero.Method) != null && Fingerprint(negativeZero.Method) != null &&
                    Fingerprint(positiveZero.Method) != Fingerprint(negativeZero.Method),
                    "floating-point operand bits preserve signed zero: " + (single ? "single" : "double"));
            }

            var switches = Fixture("switch", MakeSwitchBody);
            var reorderedSwitch = Fixture("switch reordered", (module, m) => {
                MakeSwitchBody(module, m);
                var targets = (IL.Instruction[])m.Body.Instructions.Single(i => i.OpCode.Code == IL.Code.Switch).Operand;
                (targets[0], targets[1]) = (targets[1], targets[0]);
            });
            Check(Fingerprint(switches.Method) != null && Fingerprint(reorderedSwitch.Method) != null &&
                Fingerprint(switches.Method) != Fingerprint(reorderedSwitch.Method), "switch target ordering is part of the contract");

            var catches = Fixture("catch", (module, m) => MakeExceptionBody(module, m, IL.ExceptionHandlerType.Catch));
            string catchHash = Fingerprint(catches.Method);
            Check(catchHash != null, "supported catch clauses can be fingerprinted without executing them");
            var differentCatch = Fixture("catch type", (module, m) => {
                MakeExceptionBody(module, m, IL.ExceptionHandlerType.Catch);
                m.Body.ExceptionHandlers[0].CatchType = module.ImportReference(typeof(InvalidOperationException));
            });
            Check(Fingerprint(differentCatch.Method) != null && Fingerprint(differentCatch.Method) != catchHash,
                "changed catch type cannot share the original contract");
            var differentTry = Fixture("try boundary", (module, m) => {
                MakeExceptionBody(module, m, IL.ExceptionHandlerType.Catch);
                m.Body.ExceptionHandlers[0].TryStart = m.Body.Instructions[1];
            });
            Check(Fingerprint(differentTry.Method) != null && Fingerprint(differentTry.Method) != catchHash,
                "changed exception boundary cannot share the original contract");
            var finalizer = Fixture("finally", (module, m) => MakeExceptionBody(module, m, IL.ExceptionHandlerType.Finally));
            var fault = Fixture("fault", (module, m) => MakeExceptionBody(module, m, IL.ExceptionHandlerType.Fault));
            Check(Fingerprint(finalizer.Method) != null && Fingerprint(fault.Method) != null &&
                Fingerprint(finalizer.Method) != Fingerprint(fault.Method), "exception handler kind is part of the contract");
            var filter = Fixture("filter", MakeFilterBody);
            var differentFilter = Fixture("filter boundary", (module, m) => {
                MakeFilterBody(module, m);
                m.Body.ExceptionHandlers[0].FilterStart = m.Body.Instructions[3];
            });
            Check(Fingerprint(filter.Method) != null && Fingerprint(differentFilter.Method) != null &&
                Fingerprint(filter.Method) != Fingerprint(differentFilter.Method), "filter boundary is part of the contract");

            Check(Fingerprint(null) == null, "missing method fails closed without throwing");
            var noBody = Fixture("no body", (_, m) => {
                m.DeclaringType.Attributes |= C.TypeAttributes.Abstract;
                m.Attributes = C.MethodAttributes.Public | C.MethodAttributes.Abstract | C.MethodAttributes.Virtual | C.MethodAttributes.NewSlot;
                m.HasThis = true;
                m.Body = null;
            });
            Check(Fingerprint(noBody.Method) == null, "abstract method without an IL body fails closed");
            var calli = Fixture("calli", (module, m) => {
                m.Body = new IL.MethodBody(m);
                m.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Calli, new C.CallSite(module.TypeSystem.Void)));
                m.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldc_I4_0));
                m.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
            });
            Check(Fingerprint(calli.Method) == null, "unsupported standalone calli signature fails closed without execution");
            var genericOwner = Fixture("generic member owner", (module, m) => MakeGenericOwnerBody(module, m, module.TypeSystem.Int32));
            Check(Fingerprint(genericOwner.Method) != null, "ordinary constructed generic MemberRef owner is supported");
            foreach (bool required in new[] { true, false })
            {
                C.TypeReference Modified(C.ModuleDefinition module, C.TypeReference type) => required ?
                    new C.RequiredModifierType(module.ImportReference(typeof(System.Runtime.CompilerServices.IsVolatile)), type) :
                    new C.OptionalModifierType(module.ImportReference(typeof(System.Runtime.CompilerServices.IsVolatile)), type);
                var modifiedLocal = Fixture(required ? "local modreq" : "local modopt", (module, m) =>
                    m.Body.Variables[0].VariableType = Modified(module, module.TypeSystem.Int32));
                Check(modifiedLocal.Method.GetMethodBody().LocalVariables.Count == 1,
                    "local modifier fixture has readable reflection metadata: " + (required ? "modreq" : "modopt"));
                Check(Fingerprint(modifiedLocal.Method) == null,
                    "unsupported local custom modifier fails closed: " + (required ? "modreq" : "modopt"));
                var nestedModifier = Fixture(required ? "nested local modreq" : "nested local modopt", (module, m) => {
                    var local = new C.GenericInstanceType(module.ImportReference(typeof(List<>)));
                    local.GenericArguments.Add(Modified(module, module.TypeSystem.Int32));
                    m.Body.Variables[0].VariableType = local;
                });
                Check(nestedModifier.Method.GetMethodBody().LocalVariables.Count == 1,
                    "nested local modifier fixture has readable reflection metadata: " + (required ? "modreq" : "modopt"));
                Check(Fingerprint(nestedModifier.Method) == null,
                    "unsupported nested generic local modifier fails closed: " + (required ? "modreq" : "modopt"));
                foreach (string site in new[] { "return", "parameter", "field" })
                {
                    var modifiedMember = Fixture("nested " + site + (required ? " modreq" : " modopt"), (module, m) => {
                        var type = new C.GenericInstanceType(module.ImportReference(typeof(List<>)));
                        type.GenericArguments.Add(Modified(module, module.TypeSystem.Int32));
                        if (site == "return") m.ReturnType = type;
                        else if (site == "parameter") m.Parameters[0].ParameterType = type;
                        else m.DeclaringType.Fields.Single(f => f.Name == "Counter").FieldType = type;
                    });
                    Check(modifiedMember.Method.GetMethodBody() != null,
                        "nested member modifier fixture has a readable body: " + site + (required ? " modreq" : " modopt"));
                    Check(Fingerprint(modifiedMember.Method) == null,
                        "unsupported nested member modifier fails closed: " + site + (required ? " modreq" : " modopt"));
                }
                var modifiedMethodSpec = Fixture(required ? "MethodSpec modreq" : "MethodSpec modopt", (module, m) =>
                    ((C.GenericInstanceMethod)m.Body.Instructions.Single(i => i.Operand is C.GenericInstanceMethod).Operand)
                        .GenericArguments[0] = Modified(module, module.TypeSystem.Int32));
                Check(Fingerprint(modifiedMethodSpec.Method) == null,
                    "unsupported MethodSpec argument modifier fails closed: " + (required ? "modreq" : "modopt"));
                var modifiedOwner = Fixture(required ? "MemberRef owner modreq" : "MemberRef owner modopt", (module, m) =>
                    MakeGenericOwnerBody(module, m, Modified(module, module.TypeSystem.Int32)));
                Check(Fingerprint(modifiedOwner.Method) == null,
                    "unsupported MemberRef parent TypeSpec modifier fails closed: " + (required ? "modreq" : "modopt"));
            }
            var invalidToken = Fixture("invalid token", corrupt: (bytes, start, m) => {
                var field = m.Body.Instructions.Single(i => i.OpCode.Code == IL.Code.Ldsfld);
                BitConverter.GetBytes(0x0400ffff).CopyTo(bytes, start + field.Offset + field.OpCode.Size);
            });
            Check(Fingerprint(invalidToken.Method) == null, "unresolvable field token fails closed without throwing");
            var invalidBranch = Fixture("invalid branch boundary", corrupt: (bytes, start, m) => {
                var branch = m.Body.Instructions.Single(i => i.OpCode.Code == IL.Code.Brfalse);
                // IL offset 1 is inside the first ldstr operand, never a boundary.
                BitConverter.GetBytes(1 - branch.Offset - branch.OpCode.Size - 4)
                    .CopyTo(bytes, start + branch.Offset + branch.OpCode.Size);
            });
            Check(Fingerprint(invalidBranch.Method) == null, "branch into an operand fails closed without execution");
            var unknownOpcode = Fixture("unknown opcode", corrupt: (bytes, start, _) => bytes[start] = 0x24);
            Check(Fingerprint(unknownOpcode.Method) == null, "unknown opcode fails closed without throwing");
            var truncatedOperand = Fixture("truncated operand", corrupt: (bytes, start, _) => {
                Check((bytes[start - 12] & 3) == 3 && (BitConverter.ToUInt16(bytes, start - 12) >> 12) == 3, "truncated fixture has its expected fat IL header");
                BitConverter.GetBytes(1).CopyTo(bytes, start - 8);
            });
            Check(Fingerprint(truncatedOperand.Method) == null, "truncated token operand fails closed without throwing");
            ContractFieldSchemaTests();
        }
        finally
        {
            foreach (var fixture in fixtures) fixture.Context.Unload();
            // Only files generated directly in this unique harness-owned directory.
            // A loaded-file handle may defer cleanup until this process exits.
            foreach (var fixture in fixtures)
                try { File.Delete(fixture.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"PASS: {checks - firstCheck} canonical fingerprint/schema checks. Metadata-only fixtures; no fixture or Core action invoked. Cross-runtime fingerprint parity requires its separate probe.");
    }

    static ContractFixture CreateContractFixture(string root, string label,
        Action<C.ModuleDefinition, C.MethodDefinition> change, bool shifted,
        Action<byte[], int, C.MethodDefinition> corrupt)
    {
        using var assembly = C.AssemblyDefinition.CreateAssembly(
            new C.AssemblyNameDefinition("PanelContractFixtures", shifted ? new Version(9, 8, 7, 6) : new Version(1, 0, 0, 0)),
            "PanelContractFixtures.dll", C.ModuleKind.Dll);
        var module = assembly.MainModule;
        module.Mvid = Guid.NewGuid();
        if (shifted)
        {
            var padding = new C.TypeDefinition("PanelFixtures", "Padding", C.TypeAttributes.Public | C.TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(padding);
            padding.Fields.Add(new C.FieldDefinition("Noise", C.FieldAttributes.Public | C.FieldAttributes.Static, module.TypeSystem.Int32));
            var pad = new C.MethodDefinition("Noise", C.MethodAttributes.Public | C.MethodAttributes.Static, module.TypeSystem.Void);
            padding.Methods.Add(pad);
            var il = pad.Body.Instructions;
            il.Add(IL.Instruction.Create(IL.OpCodes.Ldstr, "unrelated string padding")); il.Add(IL.Instruction.Create(IL.OpCodes.Pop));
            il.Add(IL.Instruction.Create(IL.OpCodes.Ldtoken, module.ImportReference(typeof(decimal)))); il.Add(IL.Instruction.Create(IL.OpCodes.Pop));
            il.Add(IL.Instruction.Create(IL.OpCodes.Ldtoken, module.ImportReference(typeof(List<string>)))); il.Add(IL.Instruction.Create(IL.OpCodes.Pop));
            il.Add(IL.Instruction.Create(IL.OpCodes.Call, EmptyArrayReference(module, module.TypeSystem.String))); il.Add(IL.Instruction.Create(IL.OpCodes.Pop));
            il.Add(IL.Instruction.Create(IL.OpCodes.Call, module.ImportReference(typeof(Environment).GetProperty("TickCount").GetMethod)));
            il.Add(IL.Instruction.Create(IL.OpCodes.Pop)); il.Add(IL.Instruction.Create(IL.OpCodes.Ret));
        }
        var subject = new C.TypeDefinition("PanelFixtures", "Subject", C.TypeAttributes.Public | C.TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(subject);
        var counter = new C.FieldDefinition("Counter", C.FieldAttributes.Public | C.FieldAttributes.Static, module.TypeSystem.Int32);
        subject.Fields.Add(counter);
        subject.Fields.Add(new C.FieldDefinition("OtherCounter", C.FieldAttributes.Public | C.FieldAttributes.Static, module.TypeSystem.Int32));
        foreach (string helperName in new[] { "Keep", "Other" })
        {
            var helper = new C.MethodDefinition(helperName, C.MethodAttributes.Public | C.MethodAttributes.Static | C.MethodAttributes.HideBySig, module.TypeSystem.Int32);
            helper.Parameters.Add(new C.ParameterDefinition("value", C.ParameterAttributes.None, module.TypeSystem.Int32));
            helper.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldarg_0));
            helper.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
            subject.Methods.Add(helper);
        }
        var target = new C.MethodDefinition("Panel", C.MethodAttributes.Public | C.MethodAttributes.Static | C.MethodAttributes.HideBySig, module.TypeSystem.Int32);
        target.Parameters.Add(new C.ParameterDefinition("value", C.ParameterAttributes.None, module.TypeSystem.Int32));
        subject.Methods.Add(target);
        target.Body.InitLocals = true;
        target.Body.Variables.Add(new IL.VariableDefinition(module.TypeSystem.Int32));
        var instructions = target.Body.Instructions;
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldstr, "quantity"));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Call, module.ImportReference(typeof(string).GetMethod("IsNullOrEmpty", new[] { typeof(string) }))));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Pop));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldtoken, module.ImportReference(typeof(DateTime)))); instructions.Add(IL.Instruction.Create(IL.OpCodes.Pop));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldtoken, module.ImportReference(typeof(List<int>)))); instructions.Add(IL.Instruction.Create(IL.OpCodes.Pop));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldtoken, subject)); instructions.Add(IL.Instruction.Create(IL.OpCodes.Pop));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Call, EmptyArrayReference(module, module.TypeSystem.Int32))); instructions.Add(IL.Instruction.Create(IL.OpCodes.Pop));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldsfld, counter));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldarg_0));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Call, subject.Methods.Single(m => m.Name == "Keep")));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Add)); instructions.Add(IL.Instruction.Create(IL.OpCodes.Stloc_0));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldloc_0));
        var zero = IL.Instruction.Create(IL.OpCodes.Ldc_I4, 11);
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Brfalse, zero));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldloc_0)); instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
        instructions.Add(zero); instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
        instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldc_I4, 22)); instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
        change?.Invoke(module, target);
        string path = System.IO.Path.Combine(root, label + ".dll");
        assembly.Write(path);
        if (corrupt != null)
        {
            // Change bytes only after Cecil emits a valid assembly. This creates
            // independently malformed inputs that its normal emitter disallows.
            byte[] bytes = File.ReadAllBytes(path);
            using var written = C.ModuleDefinition.ReadModule(new MemoryStream(bytes, writable: false));
            var writtenMethod = written.Types.Single(t => t.Name == "Subject").Methods.Single(m => m.Name == "Panel");
            int rva = writtenMethod.RVA;
            using (var pe = new PEReader(new MemoryStream(bytes, writable: false)))
            {
                var section = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData));
                int header = section.PointerToRawData + rva - section.VirtualAddress;
                int headerSize = (bytes[header] & 3) == 2 ? 1 : (BitConverter.ToUInt16(bytes, header) >> 12) * 4;
                corrupt(bytes, header + headerSize, writtenMethod);
            }
            File.WriteAllBytes(path, bytes);
        }
        var context = new AssemblyLoadContext("PanelContract-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        var loaded = context.LoadFromAssemblyPath(path);
        return new ContractFixture { Path = path, Context = context, Method = loaded.GetType("PanelFixtures.Subject", true)
            .GetMethod("Panel", BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance) };
    }

    static C.GenericInstanceMethod EmptyArrayReference(C.ModuleDefinition module, C.TypeReference element)
    {
        var method = new C.GenericInstanceMethod(module.ImportReference(typeof(Array).GetMethod("Empty", BindingFlags.Public | BindingFlags.Static)));
        method.GenericArguments.Add(element);
        return method;
    }

    static void AssertShiftedFixtureTokens(ContractFixture baseline, ContractFixture shifted)
    {
        using var before = C.ModuleDefinition.ReadModule(baseline.Path);
        using var after = C.ModuleDefinition.ReadModule(shifted.Path);
        var original = before.Types.Single(t => t.Name == "Subject").Methods.Single(m => m.Name == "Panel");
        var changed = after.Types.Single(t => t.Name == "Subject").Methods.Single(m => m.Name == "Panel");
        byte[] beforeBytes = baseline.Method.GetMethodBody().GetILAsByteArray();
        byte[] afterBytes = shifted.Method.GetMethodBody().GetILAsByteArray();
        void Shifted(string label, Func<IL.Instruction, bool> select)
        {
            var a = original.Body.Instructions.Single(select);
            var b = changed.Body.Instructions.Single(select);
            Check(BitConverter.ToInt32(beforeBytes, a.Offset + a.OpCode.Size) != BitConverter.ToInt32(afterBytes, b.Offset + b.OpCode.Size),
                "benign fixture actually shifts " + label + " token");
        }
        Shifted("UserString", i => i.OpCode.Code == IL.Code.Ldstr);
        Shifted("FieldDef", i => i.OpCode.Code == IL.Code.Ldsfld);
        Shifted("MethodDef", i => i.Operand is C.MethodReference m && m.Name == "Keep");
        Shifted("MemberRef", i => i.Operand is C.MethodReference m && m.Name == "IsNullOrEmpty");
        Shifted("TypeDef", i => i.Operand is C.TypeReference t && t.Name == "Subject");
        Shifted("TypeRef", i => i.Operand is C.TypeReference t && t.FullName == "System.DateTime");
        Shifted("TypeSpec", i => i.Operand is C.GenericInstanceType);
        Shifted("MethodSpec", i => i.Operand is C.GenericInstanceMethod);
    }

    static void MakeSwitchBody(C.ModuleDefinition module, C.MethodDefinition method)
    {
        method.Body = new IL.MethodBody(method);
        var zero = IL.Instruction.Create(IL.OpCodes.Ldc_I4_0);
        var one = IL.Instruction.Create(IL.OpCodes.Ldc_I4_1);
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldarg_0));
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Switch, new[] { zero, one }));
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldc_I4_2));
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
        method.Body.Instructions.Add(zero); method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
        method.Body.Instructions.Add(one); method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
    }

    static void MakeFloatingBody(C.ModuleDefinition module, C.MethodDefinition method, bool single, bool negative)
    {
        method.Body = new IL.MethodBody(method);
        method.Body.Instructions.Add(single ?
            IL.Instruction.Create(IL.OpCodes.Ldc_R4, negative ? BitConverter.Int32BitsToSingle(int.MinValue) : 0f) :
            IL.Instruction.Create(IL.OpCodes.Ldc_R8, negative ? BitConverter.Int64BitsToDouble(long.MinValue) : 0d));
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Pop));
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldc_I4_0));
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
    }

    static void MakeGenericOwnerBody(C.ModuleDefinition module, C.MethodDefinition method, C.TypeReference argument)
    {
        var owner = new C.GenericInstanceType(module.ImportReference(typeof(List<>)));
        owner.GenericArguments.Add(argument);
        var count = new C.MethodReference("get_Count", module.TypeSystem.Int32, owner) { HasThis = true };
        method.Body = new IL.MethodBody(method);
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldnull));
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Callvirt, count));
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
    }

    static void MakeExceptionBody(C.ModuleDefinition module, C.MethodDefinition method, IL.ExceptionHandlerType kind)
    {
        method.Body = new IL.MethodBody(method);
        var start = IL.Instruction.Create(IL.OpCodes.Nop);
        var second = IL.Instruction.Create(IL.OpCodes.Nop);
        var handler = IL.Instruction.Create(kind == IL.ExceptionHandlerType.Catch ? IL.OpCodes.Pop : IL.OpCodes.Nop);
        var exit = IL.Instruction.Create(IL.OpCodes.Ldc_I4_0);
        method.Body.Instructions.Add(start); method.Body.Instructions.Add(second);
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Leave, exit));
        method.Body.Instructions.Add(handler);
        method.Body.Instructions.Add(kind == IL.ExceptionHandlerType.Catch ?
            IL.Instruction.Create(IL.OpCodes.Leave, exit) : IL.Instruction.Create(IL.OpCodes.Endfinally));
        method.Body.Instructions.Add(exit); method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
        method.Body.ExceptionHandlers.Add(new IL.ExceptionHandler(kind) {
            TryStart = start, TryEnd = handler, HandlerStart = handler, HandlerEnd = exit,
            CatchType = kind == IL.ExceptionHandlerType.Catch ? module.ImportReference(typeof(Exception)) : null
        });
    }

    static void MakeFilterBody(C.ModuleDefinition module, C.MethodDefinition method)
    {
        method.Body = new IL.MethodBody(method);
        var start = IL.Instruction.Create(IL.OpCodes.Nop);
        var filter = IL.Instruction.Create(IL.OpCodes.Pop);
        var handler = IL.Instruction.Create(IL.OpCodes.Pop);
        var exit = IL.Instruction.Create(IL.OpCodes.Ldc_I4_0);
        method.Body.Instructions.Add(start); method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Leave, exit));
        method.Body.Instructions.Add(filter); method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ldc_I4_1));
        method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Endfilter));
        method.Body.Instructions.Add(handler); method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Leave, exit));
        method.Body.Instructions.Add(exit); method.Body.Instructions.Add(IL.Instruction.Create(IL.OpCodes.Ret));
        method.Body.ExceptionHandlers.Add(new IL.ExceptionHandler(IL.ExceptionHandlerType.Filter) {
            TryStart = start, TryEnd = filter, FilterStart = filter, HandlerStart = handler, HandlerEnd = exit
        });
    }

    static void ContractFieldSchemaTests()
    {
        var patch = ModAssembly.GetType("Game.Systems.QuantumStorage.QuantumMaterialPanelPatch", true);
        var schema = patch.GetMethod("IsExpectedField", BindingFlags.NonPublic | BindingFlags.Static);
        Check(schema != null, "compiled production declared field schema check exists");
        FieldInfo Field(string name) => typeof(ContractFields).GetField(name, BindingFlags.DeclaredOnly | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        bool Matches(FieldInfo field, Type owner, Type fieldType, FieldAttributes access) =>
            (bool)schema.Invoke(null, new object[] { field, owner, fieldType, access });
        Check(Matches(Field("Text"), typeof(ContractFields), typeof(string), FieldAttributes.Private), "exact declared private instance string field is accepted");
        Check(!Matches(null, typeof(ContractFields), typeof(string), FieldAttributes.Private), "missing field schema fails closed");
        Check(!Matches(Field("Text"), typeof(ContractFieldBase), typeof(string), FieldAttributes.Private), "wrong field owner fails closed");
        Check(!Matches(Field("WrongType"), typeof(ContractFields), typeof(string), FieldAttributes.Private), "wrong field type fails closed");
        Check(!Matches(Field("PublicText"), typeof(ContractFields), typeof(string), FieldAttributes.Private), "changed field accessibility fails closed");
        Check(!Matches(Field("StaticText"), typeof(ContractFields), typeof(string), FieldAttributes.Private), "static field cannot masquerade as instance state");
        Check(!Matches(Field("ReadonlyText"), typeof(ContractFields), typeof(string), FieldAttributes.Private), "readonly field cannot masquerade as mutable instance cache state");
        Check(!Matches(Field("ConstantText"), typeof(ContractFields), typeof(string), FieldAttributes.Private), "literal field cannot masquerade as mutable instance cache state");
        var inherited = typeof(ContractFields).GetField("Inherited", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(!Matches(inherited, typeof(ContractFields), typeof(string), FieldAttributes.Family), "inherited field cannot masquerade as an exact declaration");
    }
}
