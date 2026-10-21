using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Game.Systems.QuantumStorage
{
    // A deliberately conservative description of reviewed IL, not a semantic optimizer.
    // Used only during patch preparation. Tokens, build IDs and file paths are excluded;
    // the instructions and the identities they refer to remain part of the contract.
    internal static class MaterialPanelContractFingerprint
    {
        private static readonly HashSet<string> CoreFrameworkTypes = new HashSet<string>(new[]
        {
            typeof(void), typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double),
            typeof(decimal), typeof(char), typeof(string), typeof(object), typeof(IntPtr), typeof(UIntPtr),
            typeof(Type), typeof(RuntimeTypeHandle), typeof(RuntimeMethodHandle), typeof(RuntimeFieldHandle),
            typeof(Array), typeof(Enum), typeof(ValueType), typeof(Delegate), typeof(MulticastDelegate),
            typeof(Action), typeof(Action<>), typeof(Func<>), typeof(Func<,>), typeof(Nullable<>),
            typeof(IDisposable), typeof(Exception), typeof(ArgumentException), typeof(InvalidOperationException),
            typeof(System.Collections.IEnumerable), typeof(System.Collections.IEnumerator),
            typeof(IEnumerable<>), typeof(IEnumerator<>), typeof(ICollection<>), typeof(IList<>),
            typeof(IDictionary<,>), typeof(List<>), typeof(Dictionary<,>), typeof(KeyValuePair<,>)
        }.Select(type => type.FullName), StringComparer.Ordinal);
        private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null))
            .ToDictionary(opcode => unchecked((ushort)opcode.Value));

        private sealed class Instruction
        {
            internal int Offset;
            internal OpCode Opcode;
            internal object Operand;
        }

        internal static string Hash(MethodInfo method)
        {
            try
            {
                if (method == null) { return null; }
                var body = method.GetMethodBody();
                var bytes = body?.GetILAsByteArray();
                if (bytes == null || bytes.Length == 0) { return null; }
                CheckSignature(method.Module, method.MetadataToken);
                if (body.LocalSignatureMetadataToken != 0) { CheckSignature(method.Module, body.LocalSignatureMetadataToken); }
                var instructions = Decode(bytes);
                var indices = instructions.Select((instruction, index) => new { instruction.Offset, Index = index })
                    .ToDictionary(item => item.Offset, item => item.Index);
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
                {
                    writer.Write("QuantumStorage.PanelContract/1");
                    WriteMethod(writer, method);
                    writer.Write((int)method.Attributes);
                    writer.Write((int)method.GetMethodImplementationFlags());
                    writer.Write(body.InitLocals);
                    writer.Write(body.MaxStackSize);
                    writer.Write(body.LocalVariables.Count);
                    foreach (var local in body.LocalVariables)
                    {
                        writer.Write(local.LocalIndex);
                        WriteType(writer, local.LocalType);
                        writer.Write(local.IsPinned);
                    }
                    writer.Write(instructions.Count);
                    foreach (var instruction in instructions)
                    {
                        writer.Write(unchecked((ushort)instruction.Opcode.Value));
                        WriteOperand(writer, method, instruction, indices);
                    }
                    writer.Write(body.ExceptionHandlingClauses.Count);
                    foreach (var clause in body.ExceptionHandlingClauses)
                    {
                        if (clause.TryLength <= 0 || clause.HandlerLength <= 0) { throw new InvalidDataException(); }
                        writer.Write((int)clause.Flags);
                        writer.Write(Boundary(indices, bytes.Length, clause.TryOffset, false));
                        writer.Write(Boundary(indices, bytes.Length, checked(clause.TryOffset + clause.TryLength), true));
                        writer.Write(Boundary(indices, bytes.Length, clause.HandlerOffset, false));
                        writer.Write(Boundary(indices, bytes.Length, checked(clause.HandlerOffset + clause.HandlerLength), true));
                        if (clause.Flags == ExceptionHandlingClauseOptions.Clause) { WriteType(writer, clause.CatchType); }
                        else if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                        { writer.Write(Boundary(indices, bytes.Length, clause.FilterOffset, false)); }
                        else if (clause.Flags != ExceptionHandlingClauseOptions.Finally && clause.Flags != ExceptionHandlingClauseOptions.Fault)
                        { throw new InvalidDataException(); }
                    }
                    writer.Flush();
                    using (var sha = SHA256.Create())
                    { return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", ""); }
                }
            }
            // Missing metadata, unsupported IL or failed resolution is an unverified
            // contract. It must never produce a permissive or partial fingerprint.
            catch (Exception) { return null; }
        }

        private static List<Instruction> Decode(byte[] bytes)
        {
            var result = new List<Instruction>();
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                while (stream.Position < stream.Length)
                {
                    int offset = checked((int)stream.Position);
                    ushort code = reader.ReadByte();
                    if (code == 0xfe) { code = (ushort)(0xfe00 | reader.ReadByte()); }
                    if (!Opcodes.TryGetValue(code, out var opcode) || opcode.OpCodeType == OpCodeType.Nternal)
                    { throw new InvalidDataException(); }
                    object operand;
                    switch (opcode.OperandType)
                    {
                        case OperandType.InlineNone: operand = null; break;
                        case OperandType.ShortInlineI: operand = reader.ReadByte(); break;
                        case OperandType.InlineI:
                        case OperandType.ShortInlineR:
                        case OperandType.InlineField:
                        case OperandType.InlineMethod:
                        case OperandType.InlineType:
                        case OperandType.InlineTok:
                        case OperandType.InlineString: operand = reader.ReadInt32(); break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR: operand = reader.ReadInt64(); break;
                        case OperandType.ShortInlineVar: operand = (ushort)reader.ReadByte(); break;
                        case OperandType.InlineVar: operand = reader.ReadUInt16(); break;
                        case OperandType.ShortInlineBrTarget:
                            int shortDelta = reader.ReadSByte();
                            operand = checked((int)stream.Position + shortDelta);
                            break;
                        case OperandType.InlineBrTarget:
                            int delta = reader.ReadInt32();
                            operand = checked((int)stream.Position + delta);
                            break;
                        case OperandType.InlineSwitch:
                            int count = reader.ReadInt32();
                            if (count < 0 || count > (stream.Length - stream.Position) / 4) { throw new InvalidDataException(); }
                            int targetBase = checked((int)stream.Position + checked(count * 4));
                            var targets = new int[count];
                            for (int i = 0; i < count; i++) { targets[i] = checked(targetBase + reader.ReadInt32()); }
                            operand = targets;
                            break;
                        // Standalone calli signatures need a separate signature decoder.
                        // None occur in the approved contract; unknown forms fail closed.
                        default: throw new InvalidDataException();
                    }
                    result.Add(new Instruction { Offset = offset, Opcode = opcode, Operand = operand });
                }
            }
            return result;
        }

        private static int Boundary(Dictionary<int, int> indices, int length, int offset, bool allowEnd)
        {
            if (allowEnd && offset == length) { return indices.Count; }
            if (!indices.TryGetValue(offset, out var index)) { throw new InvalidDataException(); }
            return index;
        }

        private static void WriteOperand(BinaryWriter writer, MethodInfo method, Instruction instruction,
            Dictionary<int, int> indices)
        {
            var typeContext = method.DeclaringType?.GetGenericArguments();
            var methodContext = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int Token() => (int)instruction.Operand;
            switch (instruction.Opcode.OperandType)
            {
                case OperandType.InlineNone: return;
                case OperandType.ShortInlineI: writer.Write((byte)instruction.Operand); return;
                case OperandType.InlineI:
                case OperandType.ShortInlineR: writer.Write((int)instruction.Operand); return;
                case OperandType.InlineI8:
                case OperandType.InlineR: writer.Write((long)instruction.Operand); return;
                case OperandType.ShortInlineVar:
                case OperandType.InlineVar: writer.Write((ushort)instruction.Operand); return;
                case OperandType.ShortInlineBrTarget:
                case OperandType.InlineBrTarget:
                    writer.Write(Boundary(indices, -1, (int)instruction.Operand, false)); return;
                case OperandType.InlineSwitch:
                    var targets = (int[])instruction.Operand;
                    writer.Write(targets.Length);
                    foreach (int target in targets) { writer.Write(Boundary(indices, -1, target, false)); }
                    return;
                case OperandType.InlineString: writer.Write(method.Module.ResolveString(Token())); return;
                case OperandType.InlineMethod:
                    CheckSignature(method.Module, Token());
                    WriteMethod(writer, method.Module.ResolveMethod(Token(), typeContext, methodContext)); return;
                case OperandType.InlineField:
                    CheckSignature(method.Module, Token());
                    WriteField(writer, method.Module.ResolveField(Token(), typeContext, methodContext)); return;
                case OperandType.InlineType:
                    if ((Token() & unchecked((int)0xff000000)) == 0x1b000000) { CheckSignature(method.Module, Token()); }
                    WriteType(writer, method.Module.ResolveType(Token(), typeContext, methodContext)); return;
                case OperandType.InlineTok:
                    int table = Token() & unchecked((int)0xff000000);
                    if (table != 0x01000000 && table != 0x02000000) { CheckSignature(method.Module, Token()); }
                    var member = method.Module.ResolveMember(Token(), typeContext, methodContext);
                    if (member is Type resolvedType) { writer.Write("type"); WriteType(writer, resolvedType); }
                    else if (member is MethodBase resolvedMethod) { writer.Write("method"); WriteMethod(writer, resolvedMethod); }
                    else if (member is FieldInfo resolvedField) { writer.Write("field"); WriteField(writer, resolvedField); }
                    else { throw new InvalidDataException(); }
                    return;
                default: throw new InvalidDataException();
            }
        }

        private static void WriteMethod(BinaryWriter writer, MethodBase method)
        {
            if (method == null || method.DeclaringType == null) { throw new InvalidDataException(); }
            writer.Write(method is ConstructorInfo ? "constructor" : method is MethodInfo ? "method" : throw new InvalidDataException());
            WriteType(writer, method.DeclaringType);
            writer.Write(method.Name);
            writer.Write(method.IsStatic);
            writer.Write((int)method.CallingConvention);
            var genericArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
            WriteTypes(writer, genericArguments);
            if (method is MethodInfo info)
            {
                WriteType(writer, info.ReturnType);
                WriteModifiers(writer, info.ReturnParameter);
            }
            else { WriteType(writer, typeof(void)); }
            var parameters = method.GetParameters();
            writer.Write(parameters.Length);
            foreach (var parameter in parameters)
            {
                WriteType(writer, parameter.ParameterType);
                writer.Write((int)parameter.Attributes);
                WriteModifiers(writer, parameter);
            }
        }

        // Reflection erases local/nested custom modifiers and explicit array bounds.
        // Reject signature forms we cannot represent rather than silently equating
        // them. This validates metadata only; no referenced method is invoked.
        private static void CheckSignature(Module module, int token, int depth = 0)
        {
            if (depth > 64) { throw new InvalidDataException(); }
            int table = token & unchecked((int)0xff000000);
            int related = 0;
            var signature = new Signature(module, table == 0x2b000000 || table == 0x0a000000 ?
                ReadMetadataSignature(module, token, out related) : module.ResolveSignature(token), depth);
            if (related != 0) { CheckSignature(module, related, depth + 1); }
            if (table == 0x1b000000) { signature.Type(false); }
            else if (table == 0x2b000000)
            {
                if (signature.Byte() != 0x0a) { throw new InvalidDataException(); }
                signature.Types(signature.Number());
            }
            else if (table == 0x11000000)
            {
                if (signature.Byte() != 0x07) { throw new InvalidDataException(); }
                int count = signature.Number();
                for (int i = 0; i < count; i++)
                {
                    if (signature.Peek() == 0x45) { signature.Byte(); }
                    signature.Type(false);
                }
            }
            else if (table == 0x04000000 || table == 0x06000000 || table == 0x0a000000)
            {
                int flags = signature.Byte();
                if (flags == 0x06) { signature.Type(false); }
                else
                {
                    // Only ordinary, optionally generic, instance/static calls.
                    if ((flags & ~0x70) != 0 || ((flags & 0x40) != 0 && (flags & 0x20) == 0))
                    { throw new InvalidDataException(); }
                    if ((flags & 0x10) != 0) { signature.Number(); }
                    int count = signature.Number();
                    signature.Type(true);
                    signature.Types(count);
                }
            }
            else { throw new InvalidDataException(); }
            if (!signature.AtEnd) { throw new InvalidDataException(); }
        }

        // Reflection does not expose MethodSpec blobs or MemberRef owner TypeSpecs.
        // Use the game's public metadata reader. Keep its types behind a non-inlined
        // call so a missing reader is caught by Hash, leaving the native UI usable.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] ReadMetadataSignature(Module module, int token, out int related)
        {
            related = 0;
            using (var stream = File.Open(module.FullyQualifiedName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var pe = new System.Reflection.PortableExecutable.PEReader(stream))
            {
                var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                if (reader.GetGuid(reader.GetModuleDefinition().Mvid) != module.ModuleVersionId)
                { throw new InvalidDataException(); }
                int row = token & 0x00ffffff;
                if (row == 0) { throw new InvalidDataException(); }
                if ((token & unchecked((int)0xff000000)) == 0x2b000000)
                {
                    if (row > System.Reflection.Metadata.Ecma335.MetadataReaderExtensions.GetTableRowCount(reader,
                        System.Reflection.Metadata.Ecma335.TableIndex.MethodSpec)) { throw new InvalidDataException(); }
                    var specification = reader.GetMethodSpecification(System.Reflection.Metadata.Ecma335.MetadataTokens.MethodSpecificationHandle(row));
                    related = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(specification.Method);
                    int targetTable = related & unchecked((int)0xff000000);
                    if (targetTable != 0x06000000 && targetTable != 0x0a000000) { throw new InvalidDataException(); }
                    return reader.GetBlobBytes(specification.Signature);
                }
                if ((token & unchecked((int)0xff000000)) != 0x0a000000 ||
                    row > System.Reflection.Metadata.Ecma335.MetadataReaderExtensions.GetTableRowCount(reader,
                        System.Reflection.Metadata.Ecma335.TableIndex.MemberRef)) { throw new InvalidDataException(); }
                var member = reader.GetMemberReference(System.Reflection.Metadata.Ecma335.MetadataTokens.MemberReferenceHandle(row));
                if (member.Parent.Kind == System.Reflection.Metadata.HandleKind.TypeSpecification)
                { related = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(member.Parent); }
                else if (member.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeDefinition &&
                    member.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference)
                { throw new InvalidDataException(); }
                return reader.GetBlobBytes(member.Signature);
            }
        }

        private sealed class Signature
        {
            private readonly Module module;
            private readonly byte[] bytes;
            private readonly int depth;
            private int position;
            internal Signature(Module module, byte[] bytes, int depth)
            { this.module = module; this.bytes = bytes; this.depth = depth; }
            internal bool AtEnd => position == bytes.Length;
            internal int Peek() => position < bytes.Length ? bytes[position] : throw new InvalidDataException();
            internal int Byte() { int value = Peek(); position++; return value; }
            internal int Number()
            {
                int first = Byte();
                if ((first & 0x80) == 0) { return first; }
                if ((first & 0xc0) == 0x80)
                {
                    int value = ((first & 0x3f) << 8) | Byte();
                    return value >= 0x80 ? value : throw new InvalidDataException();
                }
                if ((first & 0xe0) != 0xc0) { throw new InvalidDataException(); }
                int large = ((first & 0x1f) << 24) | (Byte() << 16) | (Byte() << 8) | Byte();
                return large >= 0x4000 ? large : throw new InvalidDataException();
            }
            internal void Types(int count)
            {
                if (count > bytes.Length - position) { throw new InvalidDataException(); }
                for (int i = 0; i < count; i++) { Type(false); }
            }
            internal void Type(bool allowVoid, int nesting = 0)
            {
                if (nesting + depth > 64) { throw new InvalidDataException(); }
                int kind = Byte();
                switch (kind)
                {
                    case 0x01: if (!allowVoid) { throw new InvalidDataException(); } return;
                    case 0x02: case 0x03: case 0x04: case 0x05: case 0x06: case 0x07:
                    case 0x08: case 0x09: case 0x0a: case 0x0b: case 0x0c: case 0x0d:
                    case 0x0e: case 0x16: case 0x18: case 0x19: case 0x1c: return;
                    case 0x0f: Type(true, nesting + 1); return;
                    case 0x10: case 0x1d: Type(false, nesting + 1); return;
                    case 0x11: case 0x12: TypeToken(); return;
                    case 0x13: case 0x1e: Number(); return;
                    case 0x14:
                        Type(false, nesting + 1);
                        if (Number() == 0 || Number() != 0 || Number() != 0) { throw new InvalidDataException(); }
                        return;
                    case 0x15:
                        int definition = Byte();
                        if (definition != 0x11 && definition != 0x12) { throw new InvalidDataException(); }
                        TypeToken();
                        int count = Number();
                        if (count == 0 || count > bytes.Length - position) { throw new InvalidDataException(); }
                        for (int i = 0; i < count; i++) { Type(false, nesting + 1); }
                        return;
                    default: throw new InvalidDataException();
                }
            }
            private void TypeToken()
            {
                int coded = Number();
                int row = coded >> 2;
                int tag = coded & 3;
                if (row == 0 || row > 0x00ffffff || tag == 3) { throw new InvalidDataException(); }
                if (tag == 2) { CheckSignature(module, 0x1b000000 | row, depth + 1); }
            }
        }

        private static void WriteField(BinaryWriter writer, FieldInfo field)
        {
            if (field == null || field.DeclaringType == null) { throw new InvalidDataException(); }
            WriteType(writer, field.DeclaringType);
            writer.Write(field.Name);
            WriteType(writer, field.FieldType);
            writer.Write((int)field.Attributes);
            WriteTypes(writer, field.GetRequiredCustomModifiers());
            WriteTypes(writer, field.GetOptionalCustomModifiers());
        }

        private static void WriteModifiers(BinaryWriter writer, ParameterInfo parameter)
        {
            WriteTypes(writer, parameter.GetRequiredCustomModifiers());
            WriteTypes(writer, parameter.GetOptionalCustomModifiers());
        }

        private static void WriteTypes(BinaryWriter writer, Type[] types)
        {
            writer.Write(types.Length);
            foreach (var type in types) { WriteType(writer, type); }
        }

        private static void WriteType(BinaryWriter writer, Type type)
        {
            if (type == null) { throw new InvalidDataException(); }
            if (type.IsByRef) { writer.Write("byref"); WriteType(writer, type.GetElementType()); }
            else if (type.IsPointer) { writer.Write("pointer"); WriteType(writer, type.GetElementType()); }
            else if (type.IsArray)
            {
                writer.Write("array"); writer.Write(type.GetArrayRank());
                // Distinguish vectors from one-dimensional arrays with explicit bounds.
                writer.Write(type == type.GetElementType().MakeArrayType());
                WriteType(writer, type.GetElementType());
            }
            else if (type.IsGenericParameter)
            {
                writer.Write(type.DeclaringMethod != null ? "method-parameter" : "type-parameter");
                writer.Write(type.GenericParameterPosition);
                writer.Write(type.DeclaringType?.FullName ?? "");
                writer.Write(type.DeclaringMethod?.Name ?? "");
            }
            else
            {
                var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
                writer.Write("named");
                WriteScope(writer, definition);
                writer.Write(definition.FullName ?? throw new InvalidDataException());
                writer.Write(definition.IsValueType);
                writer.Write(definition.IsInterface);
                WriteTypes(writer, type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes);
            }
        }

        private static void WriteScope(BinaryWriter writer, Type definition)
        {
            var assembly = definition.Assembly.GetName();
            string token = BitConverter.ToString(assembly.GetPublicKeyToken() ?? new byte[0]).Replace("-", "").ToLowerInvariant();
            // Explicit, signed forwarding pairs used by this contract. Do not alias
            // arbitrary System.* assemblies or compare assembly-qualified generic names.
            bool neutralCulture = string.IsNullOrEmpty(assembly.CultureName);
            if (neutralCulture && CoreFrameworkTypes.Contains(definition.FullName) &&
                ((assembly.Name == "mscorlib" && token == "b77a5c561934e089") ||
                (assembly.Name == "System.Private.CoreLib" && token == "7cec85d7bea7798e")))
            { writer.Write("framework-core"); return; }
            if (neutralCulture && (definition.FullName == typeof(Enumerable).FullName ||
                definition.FullName == typeof(IOrderedEnumerable<>).FullName) &&
                ((assembly.Name == "System.Core" && token == "b77a5c561934e089") ||
                (assembly.Name == "System.Linq" && token == "b03f5f7f11d50a3a")))
            { writer.Write("framework-linq"); return; }
            writer.Write("assembly");
            writer.Write(assembly.Name ?? throw new InvalidDataException());
            writer.Write(token);
            writer.Write(assembly.CultureName ?? "");
        }
    }
}
