using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class Program
{
    internal const int MaximumMetadataBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // Require a path boundary: the final letter in https:// is not a drive.
    private static readonly Regex MachinePath = new(
        @"(?<![A-Za-z0-9_:])[A-Za-z]:[\\/]|(?<![A-Za-z0-9_])/(?:Users|home)/[^\s/\\\""'<>]+|(?<!\\)\\\\[A-Za-z0-9][^\\/\s]*\\[A-Za-z0-9][^\\/\s]*",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".tga", ".ogg", ".wav", ".mp3", ".mp4", ".zip", ".7z", ".pdf", ".xlsx", ".docx", ".pptx" };

    private sealed class GuardFailure(string category) : Exception(category)
    {
        public string Category { get; } = category;
    }

    private sealed record Finding(string Path, string Category);
    private sealed record ByteSpan(int Offset, int Length)
    {
        public int End => checked(Offset + Length);
        public bool Overlaps(ByteSpan other) => Offset < other.End && other.Offset < End;
    }
    private sealed record DebugPayload(DebugDirectoryEntry Entry, ByteSpan Span);
    private sealed record PeAnalysis(bool Leaking, List<ByteSpan> AllowedSpans, int Documents,
        int PrivateDocuments, string MetadataSha256, string MethodBodiesSha256, Guid Mvid);
    private sealed record PngChunk(string Type, ByteSpan Span, int DataOffset, int DataLength, bool Remove);
    private sealed record PngAnalysis(List<PngChunk> Chunks, int RemovedChunks, int Width, int Height,
        byte BitDepth, byte ColorType, string IdatSha256);
    private sealed record Evidence(string Path, string Kind, bool Changed, string BeforeSha256,
        string AfterSha256, IReadOnlyList<ByteSpan>? ClearedSpans = null, int RemovedChunks = 0,
        string? MetadataSha256 = null, string? MethodBodiesSha256 = null, Guid? Mvid = null,
        string? IdatSha256 = null, int Width = 0, int Height = 0, int BitDepth = 0, int ColorType = 0);
    private sealed record Candidate(string RelativePath, string FullPath, byte[] Original, byte[] Result, Evidence Evidence);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new GuardFailure("usage-scan-sanitize-verify-self-test");
            if (args[0] == "self-test") return SelfTest();
            if (args[0] == "self-test-git") return SelfTestGit();
            if (args[0] == "capabilities") { Console.WriteLine("git-object-scan-v1"); return 0; }
            if (args[0] is "scan-index" or "scan-history" or "pre-push") return GitMode(args);
            string mode = args[0];
            if (mode is not ("scan" or "sanitize" or "verify")) throw new GuardFailure("unknown-mode");
            Dictionary<string, string> options = ParseOptions(args.Skip(1).ToArray());
            string root = ResolveRepository(options.GetValueOrDefault("--repo", Environment.CurrentDirectory));
            List<string> paths = TrackedPaths(root);
            string? evidencePath = options.GetValueOrDefault("--evidence");
            if (evidencePath is not null && IsWithin(root, Path.GetFullPath(evidencePath)))
                throw new GuardFailure("evidence-must-be-outside-repository");
            if (mode == "scan") return Scan(root, paths, evidencePath);
            if (mode == "verify")
            {
                if (!options.TryGetValue("--baseline-ref", out string? baseline))
                    throw new GuardFailure("verify-requires-baseline-ref");
                string commit = GitText(root, "rev-parse", "--verify", baseline + "^{commit}").Trim();
                if (!Regex.IsMatch(commit, "^[0-9a-fA-F]{40,64}$")) throw new GuardFailure("invalid-baseline-ref");
                return VerifyRepository(root, paths, commit, evidencePath);
            }
            return Sanitize(root, paths, evidencePath);
        }
        catch (GuardFailure error) { Console.Error.WriteLine("ERROR " + error.Category); return 2; }
        catch (Exception error)
        {
            // Exception messages can contain local absolute paths or private input values.
            Console.Error.WriteLine("ERROR " + error.GetType().Name);
            return 2;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--repo" or "--evidence" or "--baseline-ref") ||
                !options.TryAdd(args[i], args[i + 1])) throw new GuardFailure("invalid-options");
        }
        return options;
    }

    private static string ResolveRepository(string requested)
    {
        string root = Path.GetFullPath(requested).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string actual = Path.GetFullPath(GitText(root, "rev-parse", "--show-toplevel").Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(root, actual, StringComparison.OrdinalIgnoreCase)) throw new GuardFailure("repo-must-be-git-root");
        string normalized = root.Replace('\\', '/');
        if (normalized.Contains("/AppData/LocalLow/Kodo Linija/Stardeus/Mods", StringComparison.OrdinalIgnoreCase))
            throw new GuardFailure("live-mods-root-refused");
        RejectReparsePoints(root, root);
        return root;
    }

    private static bool IsWithin(string root, string path) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePoints(string root, string path)
    {
        string? current = path;
        while (current is not null && IsWithin(root, current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new GuardFailure("reparse-point-refused");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
    }

    private static string FullTrackedPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part is ".." or "."))
            throw new GuardFailure("unsafe-tracked-path");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(root, path)) throw new GuardFailure("tracked-path-escapes-root");
        RejectReparsePoints(root, path);
        return path;
    }

    private static byte[] GitBytes(string root, params string[] args)
    {
        ProcessStartInfo start = GitStart(root, args);
        using Process process = Process.Start(start) ?? throw new GuardFailure("git-start-failed");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using MemoryStream output = new();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit(); stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new GuardFailure("git-command-failed");
        return output.ToArray();
    }

    private static string GitText(string root, params string[] args) => Utf8.GetString(GitBytes(root, args));
    private static List<string> TrackedPaths(string root) => GitText(root, "ls-files", "--cached", "-z")
        .Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string HashSpan(byte[] bytes, ByteSpan span) => Convert.ToHexString(SHA256.HashData(bytes.AsSpan(span.Offset, span.Length)));
    private static bool ContainsMachinePath(string value) => MachinePath.IsMatch(value);
    private static bool IsWorkstationFilePath(string value)
    {
        string text = value.Trim();
        return Regex.IsMatch(text, @"^(?:[A-Za-z]:[\\/]|\\\\[^\\\s]+\\|/(?:Users|home)/)", RegexOptions.CultureInvariant);
    }

    private static int Scan(string root, List<string> paths, string? evidencePath)
    {
        List<Finding> findings = [];
        foreach (string path in paths)
        {
            string location = Hash(Utf8.GetBytes(path))[..16];
            try { InspectContent(path, File.ReadAllBytes(FullTrackedPath(root, path)), findings); }
            catch (GuardFailure error) { findings.Add(new(location, "validation-" + error.Category)); }
            catch (Exception error) { findings.Add(new(location, "validation-" + error.GetType().Name)); }
        }
        foreach (Finding finding in findings.Distinct()) Console.WriteLine("location " + finding.Path + ": " + finding.Category);
        WriteEvidence(evidencePath, new { Mode = "scan", TrackedFiles = paths.Count, Findings = findings });
        Console.WriteLine($"Scanned {paths.Count} tracked files; {findings.Count} findings.");
        return findings.Count == 0 ? 0 : 1;
    }
    private static string? TryDecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Contains((byte)0))
        {
            if (bytes.Length < 4 || bytes.Length % 2 != 0) return null;
            int evenZeros = 0, oddZeros = 0;
            for (int i = 0; i < bytes.Length; i += 2)
            { if (bytes[i] == 0) evenZeros++; if (bytes[i + 1] == 0) oddZeros++; }
            try
            {
                if (oddZeros > bytes.Length / 4 && evenZeros < bytes.Length / 8)
                    return new UnicodeEncoding(false, false, true).GetString(bytes);
                if (evenZeros > bytes.Length / 4 && oddZeros < bytes.Length / 8)
                    return new UnicodeEncoding(true, false, true).GetString(bytes);
            }
            catch (DecoderFallbackException) { }
            return null;
        }
        try { return bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? Utf8.GetString(bytes, 3, bytes.Length - 3) : Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool JsonContainsMachinePath(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            bool Inspect(JsonElement element) => element.ValueKind switch
            {
                JsonValueKind.String => ContainsMachinePath(element.GetString()!),
                JsonValueKind.Array => element.EnumerateArray().Any(Inspect),
                JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsMachinePath(property.Name) || Inspect(property.Value)),
                _ => false
            };
            return Inspect(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static int Sanitize(string root, List<string> paths, string? evidencePath)
    {
        List<Candidate> candidates = [];
        List<Evidence> evidence = [];
        // Analyze every target before replacing any file.
        foreach (string relative in paths.Where(IsArtifact))
        {
            string full = FullTrackedPath(root, relative);
            byte[] original = File.ReadAllBytes(full);
            (byte[] result, Evidence proof) = Transform(relative, original);
            VerifyTransformation(relative, original, result);
            evidence.Add(proof);
            if (proof.Changed) candidates.Add(new(relative, full, original, result, proof));
        }
        string staging = Path.Combine(Path.GetTempPath(), "RepositoryPrivacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate candidate = candidates[i];
                string output = Path.Combine(staging, i.ToString("D5") + ".candidate");
                File.WriteAllBytes(output, candidate.Result);
                VerifyTransformation(candidate.RelativePath, candidate.Original, File.ReadAllBytes(output));
            }
            // Guard concurrent edits across the complete set before starting replacement.
            foreach (Candidate candidate in candidates)
                if (!File.ReadAllBytes(candidate.FullPath).AsSpan().SequenceEqual(candidate.Original))
                    throw new GuardFailure("concurrent-artifact-change");
            foreach (Candidate candidate in candidates)
            {
                RejectReparsePoints(root, candidate.FullPath);
                string temporary = candidate.FullPath + ".privacy-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    { stream.Write(candidate.Result); stream.Flush(true); }
                    VerifyTransformation(candidate.RelativePath, candidate.Original, File.ReadAllBytes(temporary));
                    if (!File.ReadAllBytes(candidate.FullPath).AsSpan().SequenceEqual(candidate.Original))
                        throw new GuardFailure("concurrent-artifact-change");
                    File.Move(temporary, candidate.FullPath, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                Console.WriteLine(candidate.RelativePath + ": sanitized-" + candidate.Evidence.Kind);
            }
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(staging)) File.Delete(file);
            Directory.Delete(staging);
        }
        WriteEvidence(evidencePath, new { Mode = "sanitize", Artifacts = evidence, ChangedFiles = candidates.Count });
        Console.WriteLine($"Sanitized {candidates.Count} repository artifacts.");
        return 0;
    }

    private static bool IsArtifact(string path) => Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe" or ".png";

    private static int VerifyRepository(string root, List<string> paths, string baseline, string? evidencePath)
    {
        List<Evidence> evidence = [];
        foreach (string relative in paths.Where(IsArtifact))
        {
            byte[] original = GitBytes(root, "cat-file", "blob", baseline + ":" + relative);
            byte[] candidate = File.ReadAllBytes(FullTrackedPath(root, relative));
            try { evidence.Add(VerifyTransformation(relative, original, candidate)); }
            catch (GuardFailure error)
            {
                Console.Error.WriteLine(relative + ": " + error.Category);
                return 2;
            }
        }
        WriteEvidence(evidencePath, new { Mode = "verify", Baseline = baseline, Artifacts = evidence });
        Console.WriteLine($"Verified {evidence.Count} artifacts against original-derived permitted changes.");
        return 0;
    }

    private static void WriteEvidence(string? path, object value)
    {
        if (path is null) return;
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static (byte[] Result, Evidence Evidence) Transform(string relative, byte[] original)
    {
        if (Path.GetExtension(relative).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            PngAnalysis analysis = AnalyzePng(original);
            byte[] result;
            if (analysis.RemovedChunks == 0) result = original.ToArray();
            else
            {
                using MemoryStream stream = new(); stream.Write(PngSignature);
                foreach (PngChunk chunk in analysis.Chunks.Where(chunk => !chunk.Remove))
                    stream.Write(original, chunk.Span.Offset, chunk.Span.Length);
                result = stream.ToArray();
            }
            return (result, new(relative, "png-file-text", analysis.RemovedChunks != 0, Hash(original), Hash(result),
                RemovedChunks: analysis.RemovedChunks, IdatSha256: analysis.IdatSha256, Width: analysis.Width, Height: analysis.Height,
                BitDepth: analysis.BitDepth, ColorType: analysis.ColorType));
        }
        PeAnalysis pe = AnalyzePe(original);
        byte[] stripped = original.ToArray();
        if (pe.Leaking)
            foreach (ByteSpan span in pe.AllowedSpans) Array.Clear(stripped, span.Offset, span.Length);
        return (stripped, new(relative, "pe-debug", pe.Leaking, Hash(original), Hash(stripped),
            pe.Leaking ? pe.AllowedSpans : [], MetadataSha256: pe.MetadataSha256,
            MethodBodiesSha256: pe.MethodBodiesSha256, Mvid: pe.Mvid));
    }

    private static Evidence VerifyTransformation(string relative, byte[] original, byte[] candidate)
    {
        (byte[] expected, Evidence proof) = Transform(relative, original);
        if (!expected.AsSpan().SequenceEqual(candidate)) throw new GuardFailure("unexpected-artifact-bytes");
        if (proof.Kind == "pe-debug")
        {
            PeAnalysis before = AnalyzePe(original);
            PeAnalysis after = AnalyzePe(candidate);
            if (original.Length != candidate.Length || after.Leaking || before.MetadataSha256 != after.MetadataSha256 ||
                before.MethodBodiesSha256 != after.MethodBodiesSha256 || before.Mvid != after.Mvid)
                throw new GuardFailure("pe-preservation-failed");
            ProveOutsideSpans(original, candidate, before.Leaking ? before.AllowedSpans : []);
            if (before.Leaking && before.AllowedSpans.Any(span => candidate.AsSpan(span.Offset, span.Length).ContainsAnyExcept((byte)0)))
                throw new GuardFailure("debug-payload-not-cleared");
        }
        else
        {
            PngAnalysis before = AnalyzePng(original);
            PngAnalysis after = AnalyzePng(candidate);
            List<PngChunk> retained = before.Chunks.Where(chunk => !chunk.Remove).ToList();
            if (after.RemovedChunks != 0 || retained.Count != after.Chunks.Count || before.IdatSha256 != after.IdatSha256 ||
                before.Width != after.Width || before.Height != after.Height || before.ColorType != after.ColorType || before.BitDepth != after.BitDepth)
                throw new GuardFailure("png-preservation-failed");
            for (int i = 0; i < retained.Count; i++)
                if (!original.AsSpan(retained[i].Span.Offset, retained[i].Span.Length)
                    .SequenceEqual(candidate.AsSpan(after.Chunks[i].Span.Offset, after.Chunks[i].Span.Length)))
                    throw new GuardFailure("retained-png-chunk-changed");
        }
        // The transformation must be idempotent.
        if (!Transform(relative, candidate).Result.AsSpan().SequenceEqual(candidate)) throw new GuardFailure("not-idempotent");
        return proof;
    }

    private static void ProveOutsideSpans(byte[] original, byte[] candidate, IReadOnlyList<ByteSpan> permitted)
    {
        if (original.Length != candidate.Length) throw new GuardFailure("pe-length-changed");
        int position = 0;
        foreach (ByteSpan span in permitted.OrderBy(span => span.Offset))
        {
            if (!original.AsSpan(position, span.Offset - position).SequenceEqual(candidate.AsSpan(position, span.Offset - position)))
                throw new GuardFailure("bytes-outside-debug-changed");
            position = span.End;
        }
        if (!original.AsSpan(position).SequenceEqual(candidate.AsSpan(position))) throw new GuardFailure("bytes-outside-debug-changed");
    }

    private static ByteSpan Span(byte[] bytes, int offset, int length, bool allowEmpty = false)
    {
        if (offset < 0 || length < 0 || (!allowEmpty && length == 0) || (long)offset + length > bytes.Length)
            throw new GuardFailure("range-out-of-bounds");
        return new(offset, length);
    }
    private static int U32(byte[] bytes, int offset)
    {
        Span(bytes, offset, 4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        if (value > int.MaxValue) throw new GuardFailure("oversized-field");
        return (int)value;
    }
    private static int RvaOffset(PEHeaders headers, byte[] bytes, int rva, int size)
    {
        if (rva <= 0 || size <= 0) throw new GuardFailure("invalid-rva-range");
        List<int> matches = [];
        foreach (SectionHeader section in headers.SectionHeaders)
        {
            long delta = (long)rva - section.VirtualAddress;
            if (delta >= 0 && delta + size <= section.SizeOfRawData)
            {
                long offset = section.PointerToRawData + delta;
                if (offset > int.MaxValue) throw new GuardFailure("oversized-section-offset");
                Span(bytes, (int)offset, size); matches.Add((int)offset);
            }
        }
        if (matches.Count != 1) throw new GuardFailure("ambiguous-or-unbacked-rva");
        return matches[0];
    }

    private static PeAnalysis AnalyzePe(byte[] bytes)
    {
        try
        {
            using PEReader reader = new(new MemoryStream(bytes, false));
            PEHeaders headers = reader.PEHeaders;
            PEHeader pe = headers.PEHeader ?? throw new GuardFailure("missing-pe-header");
            CorHeader clr = headers.CorHeader ?? throw new GuardFailure("not-managed-pe");
            if (!reader.HasMetadata || (clr.Flags & CorFlags.ILOnly) == 0) throw new GuardFailure("unsupported-managed-pe");
            // Refuse reserved signing fields too: a zero size alone does not establish no signature.
            if ((clr.Flags & CorFlags.StrongNameSigned) != 0 || clr.StrongNameSignatureDirectory.Size != 0 ||
                clr.StrongNameSignatureDirectory.RelativeVirtualAddress != 0 || pe.CertificateTableDirectory.Size != 0 ||
                pe.CertificateTableDirectory.RelativeVirtualAddress != 0) throw new GuardFailure("signed-or-reserved-signature");
            int peOffset = U32(bytes, 0x3c);
            if (U32(bytes, peOffset) != 0x4550) throw new GuardFailure("invalid-pe-signature");
            int optional = checked(peOffset + 24);
            Span(bytes, optional, 2);
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(optional, 2));
            int directories = checked(optional + (magic switch { 0x10b => 96, 0x20b => 112, _ => throw new GuardFailure("unsupported-pe-magic") }));
            int debugField = checked(directories + 6 * 8);
            Span(bytes, debugField, 8);
            if (U32(bytes, debugField) != pe.DebugTableDirectory.RelativeVirtualAddress || U32(bytes, debugField + 4) != pe.DebugTableDirectory.Size)
                throw new GuardFailure("inconsistent-debug-directory");
            MetadataReader metadata = reader.GetMetadataReader();
            Guid mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            ByteSpan metadataSpan = Span(bytes, RvaOffset(headers, bytes, clr.MetadataDirectory.RelativeVirtualAddress, clr.MetadataDirectory.Size), clr.MetadataDirectory.Size);
            List<ByteSpan> protectedSpans = [new(0, pe.SizeOfHeaders), metadataSpan];
            if (pe.SizeOfHeaders <= debugField + 8 || pe.SizeOfHeaders > bytes.Length) throw new GuardFailure("invalid-header-range");
            // Protect every other advertised PE directory, including native version resources.
            DirectoryEntry[] otherDirectories = [pe.ExportTableDirectory, pe.ImportTableDirectory, pe.ResourceTableDirectory,
                pe.ExceptionTableDirectory, pe.BaseRelocationTableDirectory, pe.GlobalPointerTableDirectory,
                pe.ThreadLocalStorageTableDirectory, pe.LoadConfigTableDirectory, pe.BoundImportTableDirectory,
                pe.ImportAddressTableDirectory, pe.DelayImportTableDirectory, pe.CorHeaderTableDirectory];
            foreach (DirectoryEntry directory in otherDirectories)
                if (directory.Size != 0) protectedSpans.Add(Span(bytes, RvaOffset(headers, bytes, directory.RelativeVirtualAddress, directory.Size), directory.Size));
            foreach (DirectoryEntry directory in new[] { clr.ResourcesDirectory, clr.VtableFixupsDirectory, clr.ExportAddressTableJumpsDirectory, clr.ManagedNativeHeaderDirectory })
                if (directory.Size != 0) protectedSpans.Add(Span(bytes, RvaOffset(headers, bytes, directory.RelativeVirtualAddress, directory.Size), directory.Size));
            using IncrementalHash methodHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
            {
                MethodDefinition method = metadata.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0) continue;
                MethodBodyBlock body = reader.GetMethodBody(method.RelativeVirtualAddress);
                ByteSpan span = Span(bytes, RvaOffset(headers, bytes, method.RelativeVirtualAddress, body.Size), body.Size);
                protectedSpans.Add(span); methodHash.AppendData(bytes, span.Offset, span.Length);
            }
            string methods = Convert.ToHexString(methodHash.GetHashAndReset());
            if (pe.DebugTableDirectory.Size == 0)
            {
                if (pe.DebugTableDirectory.RelativeVirtualAddress != 0) throw new GuardFailure("empty-debug-directory-has-rva");
                return new(false, [], 0, 0, HashSpan(bytes, metadataSpan), methods, mvid);
            }
            if (pe.DebugTableDirectory.Size % 28 != 0) throw new GuardFailure("invalid-debug-table-size");
            ByteSpan table = Span(bytes, RvaOffset(headers, bytes, pe.DebugTableDirectory.RelativeVirtualAddress, pe.DebugTableDirectory.Size), pe.DebugTableDirectory.Size);
            List<ByteSpan> payloadSpans = [table];
            List<DebugPayload> payloads = [];
            var entries = reader.ReadDebugDirectory();
            if (entries.Length != table.Length / 28) throw new GuardFailure("inconsistent-debug-entry-count");
            foreach (DebugDirectoryEntry entry in entries)
            {
                if (entry.Type is not (DebugDirectoryEntryType.CodeView or DebugDirectoryEntryType.Reproducible or DebugDirectoryEntryType.EmbeddedPortablePdb or DebugDirectoryEntryType.PdbChecksum))
                    throw new GuardFailure("unexpected-debug-record-type");
                if (entry.Type == DebugDirectoryEntryType.Reproducible)
                {
                    if (entry.DataSize != 0 || entry.DataPointer != 0 || entry.DataRelativeVirtualAddress != 0)
                        throw new GuardFailure("invalid-reproducible-record");
                    continue;
                }
                ByteSpan span = Span(bytes, entry.DataPointer, entry.DataSize);
                if (RvaOffset(headers, bytes, entry.DataRelativeVirtualAddress, entry.DataSize) != entry.DataPointer)
                    throw new GuardFailure("debug-pointer-rva-mismatch");
                payloadSpans.Add(span); payloads.Add(new(entry, span));
            }
            List<ByteSpan> ordered = payloadSpans.OrderBy(span => span.Offset).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0 && ordered[i - 1].Overlaps(ordered[i])) throw new GuardFailure("overlapping-debug-spans");
                if (protectedSpans.Any(span => span.Overlaps(ordered[i]))) throw new GuardFailure("debug-overlaps-protected-data");
            }
            bool leaking = false; int documents = 0; int privateDocuments = 0;
            foreach (DebugPayload payload in payloads)
            {
                DebugDirectoryEntry entry = payload.Entry;
                if (entry.Type == DebugDirectoryEntryType.CodeView)
                {
                    if (payload.Span.Length < 25 || !bytes.AsSpan(payload.Span.Offset, 4).SequenceEqual("RSDS"u8))
                        throw new GuardFailure("invalid-codeview-signature");
                    int pathStart = payload.Span.Offset + 24;
                    int terminator = Array.IndexOf(bytes, (byte)0, pathStart, payload.Span.End - pathStart);
                    if (terminator < 0 || bytes.AsSpan(terminator, payload.Span.End - terminator).ContainsAnyExcept((byte)0))
                        throw new GuardFailure("invalid-codeview-path-termination");
                    leaking |= ContainsMachinePath(reader.ReadCodeViewDebugDirectoryData(entry).Path);
                }
                else if (entry.Type == DebugDirectoryEntryType.PdbChecksum)
                {
                    PdbChecksumDebugDirectoryData checksum = reader.ReadPdbChecksumDebugDirectoryData(entry);
                    int size = checksum.AlgorithmName switch { "SHA256" => 32, "SHA1" => 20, _ => throw new GuardFailure("unknown-pdb-checksum") };
                    if (checksum.Checksum.Length != size || payload.Span.Length != checksum.AlgorithmName.Length + 1 + size)
                        throw new GuardFailure("invalid-pdb-checksum-record");
                }
                else
                {
                    if (payload.Span.Length < 9 || !bytes.AsSpan(payload.Span.Offset, 4).SequenceEqual("MPDB"u8) ||
                        U32(bytes, payload.Span.Offset + 4) > MaximumMetadataBytes)
                        throw new GuardFailure("invalid-embedded-pdb-header");
                    using MetadataReaderProvider provider = reader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    MetadataReader pdb = provider.GetMetadataReader();
                    foreach (DocumentHandle handle in pdb.Documents)
                    {
                        documents++;
                        if (ContainsMachinePath(pdb.GetString(pdb.GetDocument(handle).Name))) { leaking = true; privateDocuments++; }
                    }
                }
            }
            List<ByteSpan> allowed = [new(debugField, 8), .. ordered];
            return new(leaking, allowed, documents, privateDocuments, HashSpan(bytes, metadataSpan), methods, mvid);
        }
        catch (GuardFailure) { throw; }
        catch (Exception error) when (error is BadImageFormatException or IOException or ArgumentException or OverflowException or InvalidOperationException)
        { throw new GuardFailure("invalid-managed-pe"); }
    }

    private static PngAnalysis AnalyzePng(byte[] bytes, List<Finding>? findings = null, string relative = "")
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature)) throw new GuardFailure("invalid-png-signature");
        List<PngChunk> chunks = []; int position = 8; bool ended = false; bool hasIdat = false; bool idatEnded = false;
        int width = 0, height = 0; byte depth = 0, color = 0; int removed = 0;
        using IncrementalHash idatHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (position < bytes.Length)
        {
            Span(bytes, position, 12);
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            if (rawLength > int.MaxValue - 12) throw new GuardFailure("oversized-png-chunk");
            int length = (int)rawLength; ByteSpan span = Span(bytes, position, checked(length + 12));
            ReadOnlySpan<byte> typeBytes = bytes.AsSpan(position + 4, 4);
            if (typeBytes.ContainsAnyExceptInRange((byte)'A', (byte)'z') || typeBytes.ToArray().Any(value => value is > (byte)'Z' and < (byte)'a'))
                throw new GuardFailure("invalid-png-chunk-type");
            string type = Encoding.ASCII.GetString(typeBytes);
            if ((typeBytes[2] & 32) != 0) throw new GuardFailure("invalid-png-reserved-bit");
            int dataOffset = position + 8;
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8 + length, 4));
            if (Crc32(bytes.AsSpan(position + 4, length + 4)) != expectedCrc) throw new GuardFailure("png-crc-mismatch");
            if (type == "dSIG") throw new GuardFailure("signed-png-refused");
            if (chunks.Count == 0 && type != "IHDR") throw new GuardFailure("png-ihdr-not-first");
            if (type == "IHDR")
            {
                if (chunks.Count != 0 || length != 13) throw new GuardFailure("invalid-png-ihdr");
                uint w = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset, 4));
                uint h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset + 4, 4));
                if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) throw new GuardFailure("invalid-png-dimensions");
                width = (int)w; height = (int)h; depth = bytes[dataOffset + 8]; color = bytes[dataOffset + 9];
                bool validDepth = color switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => depth is 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => false };
                if (!validDepth || bytes[dataOffset + 10] != 0 || bytes[dataOffset + 11] != 0 || bytes[dataOffset + 12] > 1)
                    throw new GuardFailure("unsupported-png-ihdr");
            }
            if (type == "IDAT")
            {
                if (idatEnded) throw new GuardFailure("nonconsecutive-png-idat");
                hasIdat = true; idatHash.AppendData(bytes, dataOffset, length);
            }
            else if (hasIdat) idatEnded = true;
            if (type == "IEND")
            {
                if (length != 0 || !hasIdat || span.End != bytes.Length) throw new GuardFailure("invalid-png-iend-or-trailing-data");
                ended = true;
            }
            bool remove = false;
            if (type is "tEXt" or "zTXt" or "iTXt")
            {
                (string keyword, string value) = DecodePngText(type, bytes.AsSpan(dataOffset, length));
                bool path = ContainsMachinePath(value);
                if (path) findings?.Add(new(relative, "png-" + type + "-machine-path"));
                remove = keyword == "File" && IsWorkstationFilePath(value);
                if (remove) removed++;
            }
            chunks.Add(new(type, span, dataOffset, length, remove));
            position = span.End;
        }
        if (!ended) throw new GuardFailure("png-missing-iend");
        return new(chunks, removed, width, height, depth, color, Convert.ToHexString(idatHash.GetHashAndReset()));
    }

    private static (string Keyword, string Value) DecodePngText(string type, ReadOnlySpan<byte> data)
    {
        int separator = data.IndexOf((byte)0);
        if (separator is < 1 or > 79) throw new GuardFailure("invalid-png-text-keyword");
        string keyword = Encoding.Latin1.GetString(data[..separator]);
        ReadOnlySpan<byte> content = data[(separator + 1)..];
        if (type == "tEXt") return (keyword, Encoding.Latin1.GetString(content));
        if (type == "zTXt")
        {
            if (content.Length < 2 || content[0] != 0) throw new GuardFailure("invalid-png-ztxt");
            return (keyword, Encoding.Latin1.GetString(InflateZlib(content[1..])));
        }
        if (content.Length < 4 || content[0] > 1 || content[1] != 0) throw new GuardFailure("invalid-png-itxt");
        bool compressed = content[0] == 1; content = content[2..];
        int languageEnd = content.IndexOf((byte)0);
        if (languageEnd < 0) throw new GuardFailure("invalid-png-itxt-language");
        content = content[(languageEnd + 1)..];
        int translatedEnd = content.IndexOf((byte)0);
        if (translatedEnd < 0) throw new GuardFailure("invalid-png-itxt-translated-keyword");
        _ = Utf8.GetString(content[..translatedEnd]);
        content = content[(translatedEnd + 1)..];
        return (keyword, Utf8.GetString(compressed ? InflateZlib(content) : content.ToArray()));
    }

    private static byte[] InflateZlib(ReadOnlySpan<byte> input)
    {
        try
        {
            using ZLibStream inflate = new(new MemoryStream(input.ToArray(), false), CompressionMode.Decompress);
            using MemoryStream output = new();
            byte[] buffer = new byte[8192]; int read;
            while ((read = inflate.Read(buffer)) != 0)
            {
                if (output.Length + read > MaximumMetadataBytes) throw new GuardFailure("png-text-inflation-limit");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { throw new GuardFailure("invalid-png-text-compression"); }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }

    private static int SelfTest()
    {
        int checks = 0;
        void Check(bool condition) { checks++; if (!condition) throw new GuardFailure("self-test-assertion"); }
        void Reject(Action action)
        {
            checks++;
            try { action(); } catch (GuardFailure) { return; }
            throw new GuardFailure("self-test-rejection-missed");
        }
        Check(!ContainsMachinePath("https://example.invalid/project"));
        Check(ContainsMachinePath("{\"File\":\"Q:" + "\\\\fixture\\\\source.cs\"}"));
        Check(ContainsMachinePath("/" + "home/fixture/source.cs"));
        Check(ContainsMachinePath(new string('\\', 2) + "fixture-host\\share\\source.cs"));
        Check(!ContainsMachinePath("PublicAuthor Compatibility.Owner"));
        Check(JsonContainsMachinePath("{\"File\":\"\\u0051:" + "\\u005c\\u005cfixture\"}"));
        string utf16Fixture = "Q:" + "\\fixture\\source.cs";
        Check(ContainsMachinePath(TryDecodeText(Encoding.Unicode.GetBytes(utf16Fixture))!));
        Check(ContainsMachinePath(TryDecodeText(Encoding.BigEndianUnicode.GetBytes(utf16Fixture))!));
        foreach (string kind in new[] { "tEXt", "zTXt", "iTXt" })
        {
            byte[] image = FixturePng(kind, false);
            var transformed = Transform("fixture.png", image);
            Check(transformed.Evidence.RemovedChunks == 1);
            VerifyTransformation("fixture.png", image, transformed.Result); checks++;
            Check(AnalyzePng(transformed.Result).Chunks.Any(chunk => chunk.Type == "tEXt"));
            byte[] changedPixel = transformed.Result.ToArray();
            PngChunk idat = AnalyzePng(changedPixel).Chunks.First(chunk => chunk.Type == "IDAT");
            changedPixel[idat.DataOffset] ^= 1;
            Reject(() => VerifyTransformation("fixture.png", image, changedPixel));
        }
        Reject(() => AnalyzePng(FixturePng("tEXt", true)));
        byte[] corrupt = FixturePng("tEXt", false); corrupt[^1] ^= 1;
        Reject(() => AnalyzePng(corrupt));
        Reject(() => AnalyzePng([.. FixturePng("tEXt", false), 0]));
        Reject(() => AnalyzePe([0, 1, 2, 3]));
        byte[] self = File.ReadAllBytes(typeof(Program).Assembly.Location);
        using (PEReader reader = new(new MemoryStream(self, false)))
        {
            PEHeaders headers = reader.PEHeaders;
            DebugDirectoryEntry cv = reader.ReadDebugDirectory().First(entry => entry.Type == DebugDirectoryEntryType.CodeView);
            byte[] leaking = self.ToArray();
            byte[] fakePath = Encoding.ASCII.GetBytes("Q:" + "\\fixture.pdb");
            Check(cv.DataSize - 24 > fakePath.Length);
            Array.Clear(leaking, cv.DataPointer + 24, cv.DataSize - 24);
            fakePath.CopyTo(leaking, cv.DataPointer + 24);
            Check(AnalyzePe(leaking).Leaking);
            var result = Transform("fixture.dll", leaking);
            Check(result.Evidence.Changed);
            VerifyTransformation("fixture.dll", leaking, result.Result); checks++;
            Check(!AnalyzePe(result.Result).Leaking);
            int clrOffset = RvaOffset(headers, self, headers.PEHeader!.CorHeaderTableDirectory.RelativeVirtualAddress, headers.PEHeader.CorHeaderTableDirectory.Size);
            byte[] signed = leaking.ToArray(); signed[clrOffset + 16] |= 8;
            Reject(() => Transform("signed.dll", signed));
            byte[] reserved = leaking.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(reserved.AsSpan(clrOffset + 36, 4), 4);
            Reject(() => Transform("reserved.dll", reserved));
            int table = RvaOffset(headers, self, headers.PEHeader.DebugTableDirectory.RelativeVirtualAddress, headers.PEHeader.DebugTableDirectory.Size);
            int index = reader.ReadDebugDirectory().IndexOf(cv); int record = table + index * 28;
            byte[] unknown = leaking.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(record + 12, 4), 123);
            Reject(() => Transform("unknown.dll", unknown));
            byte[] invalidCodeView = leaking.ToArray(); invalidCodeView[cv.DataPointer] ^= 1;
            Reject(() => Transform("invalid-signature.dll", invalidCodeView));
            byte[] outside = leaking.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(outside.AsSpan(record + 16, 4), int.MaxValue);
            Reject(() => Transform("outside.dll", outside));
            DebugDirectoryEntry checksum = reader.ReadDebugDirectory().First(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum);
            int checksumRecord = table + reader.ReadDebugDirectory().IndexOf(checksum) * 28;
            byte[] duplicate = leaking.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(checksumRecord + 16, 4), cv.DataSize);
            BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(checksumRecord + 20, 4), cv.DataRelativeVirtualAddress);
            BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(checksumRecord + 24, 4), cv.DataPointer);
            Reject(() => Transform("overlapping-debug.dll", duplicate));
            DebugDirectoryEntry embedded = reader.ReadDebugDirectory().First(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
            byte[] invalidEmbedded = leaking.ToArray(); invalidEmbedded[embedded.DataPointer] ^= 1;
            Reject(() => Transform("invalid-embedded-pdb.dll", invalidEmbedded));
            MetadataReader metadata = reader.GetMetadataReader();
            MethodDefinition method = metadata.MethodDefinitions.Select(metadata.GetMethodDefinition).First(method => method.RelativeVirtualAddress != 0 && reader.GetMethodBody(method.RelativeVirtualAddress).Size >= cv.DataSize);
            byte[] overlap = leaking.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(overlap.AsSpan(record + 20, 4), method.RelativeVirtualAddress);
            BinaryPrimitives.WriteInt32LittleEndian(overlap.AsSpan(record + 24, 4), RvaOffset(headers, self, method.RelativeVirtualAddress, cv.DataSize));
            Reject(() => Transform("overlap.dll", overlap));
            int optional = U32(self, 0x3c) + 24;
            int directories = optional + (BinaryPrimitives.ReadUInt16LittleEndian(self.AsSpan(optional, 2)) == 0x20b ? 112 : 96);
            byte[] certificate = leaking.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(certificate.AsSpan(directories + 4 * 8, 4), 1);
            Reject(() => Transform("certificate.dll", certificate));
            byte[] illegalChange = result.Result.ToArray(); illegalChange[0] ^= 1;
            Reject(() => VerifyTransformation("fixture.dll", leaking, illegalChange));
        }
        Console.WriteLine($"PASS {checks} structural, preservation, idempotence, and refusal self-tests.");
        return 0;
    }

    private static byte[] FixturePng(string textKind, bool signed)
    {
        using MemoryStream output = new(); output.Write(PngSignature);
        void Chunk(string type, byte[] data)
        {
            byte[] record = new byte[data.Length + 12];
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(0, 4), data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(record, 4); data.CopyTo(record, 8);
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(data.Length + 8, 4), Crc32(record.AsSpan(4, data.Length + 4)));
            output.Write(record);
        }
        byte[] ihdr = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), 1); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), 1); ihdr[8] = 8;
        Chunk("IHDR", ihdr);
        byte[] value = Encoding.ASCII.GetBytes("Q:" + "\\fixture\\source.png");
        byte[] Compress(byte[] data)
        {
            using MemoryStream stream = new();
            using (ZLibStream zip = new(stream, CompressionLevel.Optimal, true)) zip.Write(data);
            return stream.ToArray();
        }
        byte[] text = textKind switch
        {
            "tEXt" => [.. "File\0"u8.ToArray(), .. value],
            "zTXt" => [.. "File\0\0"u8.ToArray(), .. Compress(value)],
            _ => [.. "File\0\x01\0\0\0"u8.ToArray(), .. Compress(value)]
        };
        Chunk(textKind, text); Chunk("tEXt", "Author\0Public Author"u8.ToArray());
        if (signed) Chunk("dSIG", [1]);
        Chunk("IDAT", Compress([0, 127])); Chunk("IEND", []);
        return output.ToArray();
    }
}
