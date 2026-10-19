using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class Program
{
    private const int MaximumGitEntries = 2_000_000;
    private static readonly Regex HighConfidenceSecret = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----|\bgh[pousr]_[A-Za-z0-9]{36}\b|\bgithub_pat_[A-Za-z0-9_]{70,255}\b|\bxox[baprs]-[A-Za-z0-9-]{20,255}\b|\bAIza[A-Za-z0-9_-]{35}\b|\bsk-(?:proj-|svcacct-)[A-Za-z0-9_-]{40,255}\b|https?://[^\s/:@]+:[^\s/@]{4,}@(?!example\.invalid(?=[:/?#\s]|$))[^\s/]+",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly HashSet<string> ContainerExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".tar", ".pdf", ".xlsx", ".docx", ".pptx", ".nupkg", ".snupkg" };
    private sealed record GitObject(string Type, byte[] Bytes);
    private sealed record GitFile(string ObjectId, string Path, string Mode);
    private sealed record ObjectFinding(string ObjectId, string PathId, string Category);

    private static int GitMode(string[] args)
    {
        string mode = args[0];
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--repo" or "--ref") ||
                !options.TryAdd(args[i], args[i + 1])) throw new GuardFailure("invalid-git-options");
        }
        if ((mode == "scan-history") != options.ContainsKey("--ref")) throw new GuardFailure("invalid-git-ref-option");
        string root = ResolveRepository(options.GetValueOrDefault("--repo", Environment.CurrentDirectory));
        int hashLength = GitText(root, "rev-parse", "--show-object-format").Trim() switch
        { "sha1" => 40, "sha256" => 64, _ => throw new GuardFailure("unsupported-git-object-format") };
        if (mode == "scan-index") return ScanIndex(root, hashLength);
        List<string> tips = [];
        if (mode == "scan-history")
        {
            string tip = options["--ref"];
            ValidateObjectId(tip, hashLength);
            tips.Add(tip.ToLowerInvariant());
        }
        else
        {
            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                if (line.Length > 4096) throw new GuardFailure("invalid-push-record");
                string[] fields = line.Split(' ');
                if (fields.Length != 4 || fields.Any(string.IsNullOrEmpty) ||
                    fields.Any(field => field.Any(char.IsControl))) throw new GuardFailure("invalid-push-record");
                ValidateObjectId(fields[1], hashLength, true);
                ValidateObjectId(fields[3], hashLength, true);
                if (!ValidRefName(fields[2])) throw new GuardFailure("invalid-push-ref");
                if (fields[1].All(c => c == '0')) {
                    if (fields[0] != "(delete)" && !ValidRefName(fields[0])) throw new GuardFailure("invalid-push-ref");
                    continue;
                }
                // Git may advertise HEAD or a revision expression here; the validated OID alone is authoritative.
                tips.Add(fields[1].ToLowerInvariant());
                if (tips.Count > MaximumGitEntries) throw new GuardFailure("push-record-limit");
            }
            if (tips.Count == 0) { Console.WriteLine("PASS no outgoing objects."); return 0; }
        }
        return ScanHistory(root, tips.Distinct(StringComparer.Ordinal).ToList(), hashLength);
    }

    private static bool ValidRefName(string name) => name.StartsWith("refs/", StringComparison.Ordinal) &&
        !name.Contains("..", StringComparison.Ordinal) && !name.Contains("@{", StringComparison.Ordinal) &&
        !name.EndsWith('/') && !name.EndsWith('.') &&
        !name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c)) &&
        name.Split('/').All(part => part.Length != 0 && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));

    private static void ValidateObjectId(string id, int length, bool allowZero = false)
    {
        if (id.Length != length || !id.All(Uri.IsHexDigit) || (!allowZero && id.All(c => c == '0')))
            throw new GuardFailure("invalid-object-id");
    }

    private static void EnsureCompleteHistory(string root)
    {
        if (GitText(root, "rev-parse", "--is-shallow-repository").Trim() != "false")
            throw new GuardFailure("shallow-history-refused");
        string grafts = GitText(root, "rev-parse", "--git-path", "info/grafts").Trim();
        string graftPath = Path.GetFullPath(Path.IsPathRooted(grafts) ? grafts : Path.Combine(root, grafts));
        if (File.Exists(graftPath) && new FileInfo(graftPath).Length != 0)
            throw new GuardFailure("grafted-history-refused");
        if (Environment.GetEnvironmentVariable("GIT_GRAFT_FILE") is string configured && configured.Length != 0)
            throw new GuardFailure("grafted-history-refused");
    }

    private static int ScanIndex(string root, int hashLength)
    {
        List<GitFile> files = [];
        foreach (string record in NullRecords(GitBytes(root, "ls-files", "--stage", "-z")))
        {
            int separator = record.IndexOf('\t');
            if (separator < 0) throw new GuardFailure("invalid-index-record");
            string[] header = record[..separator].Split(' ');
            if (header.Length != 3 || header[2] != "0") throw new GuardFailure("unmerged-index-refused");
            ValidateObjectId(header[1], hashLength);
            ValidateMode(header[0]);
            files.Add(new(header[1], record[(separator + 1)..], header[0]));
            if (files.Count > MaximumGitEntries) throw new GuardFailure("index-entry-limit");
        }
        using GitBatch batch = new(root, hashLength);
        List<ObjectFinding> findings = [];
        ScanGitFiles(batch, files, findings);
        return PrintObjectFindings("index", files.Count, findings);
    }

    private static string[] NullRecords(byte[] bytes)
    {
        if (bytes.Length == 0) return [];
        if (bytes[^1] != 0) throw new GuardFailure("invalid-null-records");
        return Utf8.GetString(bytes, 0, bytes.Length - 1).Split('\0');
    }

    private static void ValidateMode(string mode)
    {
        if (mode is not ("100644" or "100755")) throw new GuardFailure("unsupported-git-file-mode");
    }

    private static int ScanHistory(string root, List<string> tips, int hashLength)
    {
        EnsureCompleteHistory(root);
        using GitBatch batch = new(root, hashLength);
        List<ObjectFinding> findings = [];
        HashSet<string> commits = new(StringComparer.Ordinal);
        HashSet<string> tags = new(StringComparer.Ordinal);
        HashSet<string> roots = new(StringComparer.Ordinal);
        Queue<string> queue = new(tips);
        while (queue.TryDequeue(out string? id))
        {
            if (commits.Contains(id) || tags.Contains(id)) continue;
            if (commits.Count + tags.Count >= MaximumGitEntries) throw new GuardFailure("history-entry-limit");
            GitObject item = batch.Read(id);
            if (item.Type is not ("commit" or "tag")) throw new GuardFailure("unsupported-push-object");
            List<Finding> metadata = [];
            InspectText("metadata", DecodeCompatibleText(item.Bytes), metadata);
            foreach (Finding finding in metadata) findings.Add(new(id, "metadata", finding.Category));
            string text = Utf8.GetString(item.Bytes);
            int end = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (end < 0) throw new GuardFailure("invalid-git-metadata");
            string[] headers = text[..end].Split('\n');
            if (item.Type == "tag")
            {
                tags.Add(id);
                if (headers.Length < 2 || !headers[0].StartsWith("object ", StringComparison.Ordinal) ||
                    headers[1] is not ("type commit" or "type tag")) throw new GuardFailure("unsupported-tag-target");
                string target = headers[0][7..];
                ValidateObjectId(target, hashLength);
                queue.Enqueue(target);
            }
            else
            {
                commits.Add(id);
                if (!headers[0].StartsWith("tree ", StringComparison.Ordinal)) throw new GuardFailure("invalid-commit-tree");
                string tree = headers[0][5..]; ValidateObjectId(tree, hashLength); roots.Add(tree);
                foreach (string header in headers.Where(header => header.StartsWith("parent ", StringComparison.Ordinal)))
                {
                    string parent = header[7..]; ValidateObjectId(parent, hashLength); queue.Enqueue(parent);
                }
            }
        }

        List<GitFile> files = [];
        Dictionary<string, byte[]> trees = new(StringComparer.Ordinal);
        foreach (string tree in roots) ReadTree(tree, "", 0);
        ScanGitFiles(batch, files, findings);
        Console.WriteLine($"Selected history: {commits.Count} commits, {tags.Count} tags.");
        return PrintObjectFindings("history", files.Count, findings);

        void ReadTree(string treeId, string prefix, int depth)
        {
            if (depth > 128) throw new GuardFailure("tree-depth-limit");
            if (!trees.TryGetValue(treeId, out byte[]? bytes))
            {
                GitObject tree = batch.Read(treeId);
                if (tree.Type != "tree") throw new GuardFailure("invalid-tree-object");
                bytes = tree.Bytes;
                trees.Add(treeId, bytes);
            }
            int position = 0;
            while (position < bytes.Length)
            {
                int space = Array.IndexOf(bytes, (byte)' ', position);
                int nul = space < 0 ? -1 : Array.IndexOf(bytes, (byte)0, space + 1);
                int rawHashLength = hashLength / 2;
                if (space <= position || nul <= space + 1 || nul + 1L + rawHashLength > bytes.Length)
                    throw new GuardFailure("invalid-tree-record");
                string mode = Encoding.ASCII.GetString(bytes, position, space - position);
                string name = Utf8.GetString(bytes, space + 1, nul - space - 1);
                if (name is "." or ".." || name.Contains('/')) throw new GuardFailure("invalid-tree-name");
                string id = Convert.ToHexString(bytes.AsSpan(nul + 1, rawHashLength)).ToLowerInvariant();
                string path = prefix + name;
                if (mode == "40000") ReadTree(id, path + "/", depth + 1);
                else
                {
                    ValidateMode(mode);
                    files.Add(new(id, path, mode));
                    if (files.Count > MaximumGitEntries) throw new GuardFailure("tree-entry-limit");
                }
                position = nul + 1 + rawHashLength;
            }
        }
    }

    private static void ScanGitFiles(GitBatch batch, List<GitFile> files, List<ObjectFinding> findings)
    {
        Dictionary<string, List<GitFile>> blobs = new(StringComparer.Ordinal);
        foreach (GitFile file in files)
        {
            string pathId = Hash(Utf8.GetBytes(file.Path))[..16];
            List<Finding> pathFindings = [];
            InspectText(pathId, file.Path, pathFindings);
            if (file.Path.Any(char.IsControl) || file.Path.Contains('\\'))
                pathFindings.Add(new(pathId, "unsupported-file-name"));
            foreach (Finding finding in pathFindings) findings.Add(new(file.ObjectId, pathId, "path-" + finding.Category));
            if (!blobs.TryGetValue(file.ObjectId, out List<GitFile>? contexts)) blobs.Add(file.ObjectId, contexts = []);
            if (!contexts.Any(existing => string.Equals(Path.GetExtension(existing.Path), Path.GetExtension(file.Path), StringComparison.OrdinalIgnoreCase)))
                contexts.Add(file);
        }
        foreach ((string id, List<GitFile> contexts) in blobs)
        {
            GitObject blob = batch.Read(id);
            if (blob.Type != "blob") throw new GuardFailure("invalid-blob-object");
            foreach (GitFile file in contexts)
            {
                string pathId = Hash(Utf8.GetBytes(file.Path))[..16];
                List<Finding> local = [];
                InspectContent(file.Path, blob.Bytes, local);
                foreach (Finding finding in local) findings.Add(new(id, pathId, finding.Category));
            }
        }
    }

    private static int PrintObjectFindings(string mode, int entries, List<ObjectFinding> findings)
    {
        ObjectFinding[] unique = findings.Distinct().ToArray();
        foreach (ObjectFinding finding in unique)
            Console.WriteLine($"object {finding.ObjectId} location {finding.PathId}: {finding.Category}");
        Console.WriteLine($"Scanned {mode}: {entries} file entries; {unique.Length} findings.");
        return unique.Length == 0 ? 0 : 1;
    }

    private static string DecodeCompatibleText(byte[] bytes) => TryDecodeText(bytes) ?? Encoding.Latin1.GetString(bytes);

    private static void InspectText(string path, string text, List<Finding> findings)
    {
        if (ContainsMachinePath(text)) findings.Add(new(path, "text-machine-path"));
        if (HighConfidenceSecret.IsMatch(text)) findings.Add(new(path, "high-confidence-secret"));
    }

    private static void InspectContent(string path, byte[] bytes, List<Finding> findings)
    {
        string location = Hash(Utf8.GetBytes(path))[..16];
        try
        {
            string extension = Path.GetExtension(path);
            string basename = Path.GetFileName(path);
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                (basename.Equals("Game.dll", StringComparison.OrdinalIgnoreCase) || basename.StartsWith("Game.", StringComparison.OrdinalIgnoreCase) ||
                 basename.Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase) || basename.Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase) ||
                 basename.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) || basename.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase) ||
                 basename.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) || basename.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase) ||
                 basename.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || basename.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)))
                throw new GuardFailure("external-dependency-dll-refused");
            bool mz = bytes.Length >= 2 && bytes[0] == 'M' && bytes[1] == 'Z';
            bool png = bytes.AsSpan().StartsWith(PngSignature);
            if (ContainerExtensions.Contains(extension) || IsOpaqueContainer(bytes))
                throw new GuardFailure("opaque-container-refused");
            if (extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
                bytes.AsSpan().StartsWith("BSJB"u8) || bytes.AsSpan().StartsWith("Microsoft C/C++ MSF"u8))
                throw new GuardFailure("standalone-pdb-refused");
            if (mz || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                PeAnalysis pe = AnalyzePe(bytes);
                if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    findings.Add(new(location, "pe-extension-mismatch"));
                if (pe.Leaking) findings.Add(new(location, "pe-debug-machine-path"));
                // Tokens embedded as ASCII/UTF-16 literals/resources must not escape just because the file is a PE.
                InspectBinarySecrets(location, bytes, findings);
            }
            else if (png || extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                PngAnalysis analysis = AnalyzePng(bytes, findings, location);
                if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) findings.Add(new(location, "png-extension-mismatch"));
                foreach (PngChunk chunk in analysis.Chunks.Where(chunk => chunk.Type is "tEXt" or "zTXt" or "iTXt"))
                {
                    (string keyword, string value) = DecodePngText(chunk.Type, bytes.AsSpan(chunk.DataOffset, chunk.DataLength));
                    if (HighConfidenceSecret.IsMatch(keyword) || HighConfidenceSecret.IsMatch(value))
                        findings.Add(new(location, "png-text-secret"));
                }
            }
            else if (BinaryExtensions.Contains(extension))
            {
                if (!(extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) ||
                    !BaselineJpegSha256.Contains(Hash(bytes)))
                    throw new GuardFailure("unreviewed-opaque-binary");
                InspectBinarySecrets(location, bytes, findings);
            }
            else
            {
                string? text = TryDecodeText(bytes);
                if (text is null)
                {
                    if (bytes.Contains((byte)0)) throw new GuardFailure("unsupported-binary-content");
                    text = Encoding.Latin1.GetString(bytes);
                }
                if (text.Any(c => c < 32 && c is not ('\t' or '\r' or '\n' or '\f')))
                    throw new GuardFailure("unsupported-binary-content");
                InspectText(location, text, findings);
                if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
                        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                        Visit(document.RootElement);
                    }
                    catch (JsonException) { throw new GuardFailure("invalid-json"); }
                }
            }
        }
        catch (GuardFailure error) { findings.Add(new(location, "validation-" + error.Category)); }
        catch (Exception error) { findings.Add(new(location, "validation-" + error.GetType().Name)); }

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String) InspectText(location, element.GetString()!, findings);
            else if (element.ValueKind == JsonValueKind.Array) foreach (JsonElement item in element.EnumerateArray()) Visit(item);
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty property in element.EnumerateObject()) { InspectText(location, property.Name, findings); Visit(property.Value); }
        }
    }

    private static void InspectBinarySecrets(string location, byte[] bytes, List<Finding> findings)
    {
        if (HighConfidenceSecret.IsMatch(Encoding.Latin1.GetString(bytes)) ||
            HighConfidenceSecret.IsMatch(Encoding.Unicode.GetString(bytes, 0, bytes.Length - bytes.Length % 2)) ||
            (bytes.Length > 1 && HighConfidenceSecret.IsMatch(Encoding.Unicode.GetString(bytes, 1, (bytes.Length - 1) / 2 * 2))))
            findings.Add(new(location, "binary-high-confidence-secret"));
    }

    private static bool IsOpaqueContainer(byte[] bytes) =>
        bytes.AsSpan().StartsWith("PK\x03\x04"u8) || bytes.AsSpan().StartsWith("PK\x05\x06"u8) ||
        bytes.AsSpan().StartsWith("PK\x07\x08"u8) || bytes.AsSpan().StartsWith(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }) ||
        bytes.AsSpan().StartsWith("Rar!"u8) || bytes.AsSpan().StartsWith(new byte[] { 0x1f, 0x8b }) ||
        bytes.AsSpan().StartsWith("BZh"u8) || bytes.AsSpan().StartsWith(new byte[] { 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0 }) ||
        bytes.AsSpan().StartsWith("%PDF-"u8) || (bytes.Length >= 262 && bytes.AsSpan(257, 5).SequenceEqual("ustar"u8));

    private sealed class GitBatch : IDisposable
    {
        private readonly Process process;
        private readonly Task discardedError;
        private readonly int hashLength;
        private bool disposed;

        internal GitBatch(string root, int hashLength)
        {
            this.hashLength = hashLength;
            ProcessStartInfo start = GitStart(root, "cat-file", "--batch");
            start.RedirectStandardInput = true;
            process = Process.Start(start) ?? throw new GuardFailure("git-start-failed");
            discardedError = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        }

        internal GitObject Read(string id)
        {
            ValidateObjectId(id, hashLength);
            process.StandardInput.WriteLine(id);
            process.StandardInput.Flush();
            string header = ReadHeader(process.StandardOutput.BaseStream);
            string[] fields = header.Split(' ');
            if (fields.Length != 3 || fields[0] != id || fields[1] is not ("blob" or "tree" or "commit" or "tag") ||
                !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long length) ||
                length > MaximumMetadataBytes || length < 0) throw new GuardFailure("missing-or-invalid-git-object");
            byte[] bytes = new byte[(int)length];
            try { process.StandardOutput.BaseStream.ReadExactly(bytes); }
            catch (EndOfStreamException) { throw new GuardFailure("truncated-git-object"); }
            if (process.StandardOutput.BaseStream.ReadByte() != '\n') throw new GuardFailure("invalid-batch-framing");
            using IncrementalHash objectHash = IncrementalHash.CreateHash(hashLength == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
            objectHash.AppendData(Encoding.ASCII.GetBytes(fields[1] + " " + length.ToString(CultureInfo.InvariantCulture) + "\0"));
            objectHash.AppendData(bytes);
            if (!string.Equals(Convert.ToHexString(objectHash.GetHashAndReset()), id, StringComparison.OrdinalIgnoreCase))
                throw new GuardFailure("git-object-hash-mismatch");
            return new(fields[1], bytes);
        }

        private static string ReadHeader(Stream stream)
        {
            List<byte> bytes = [];
            int value;
            while ((value = stream.ReadByte()) >= 0 && value != '\n')
            {
                if (bytes.Count >= 160) throw new GuardFailure("invalid-batch-header");
                bytes.Add((byte)value);
            }
            if (value < 0) throw new GuardFailure("missing-git-object");
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            process.StandardInput.Close();
            if (!process.WaitForExit(5000)) { process.Kill(true); process.WaitForExit(); }
            discardedError.GetAwaiter().GetResult();
            int exitCode = process.ExitCode;
            process.Dispose();
            if (exitCode != 0) throw new GuardFailure("git-batch-failed");
        }
    }

    private static ProcessStartInfo GitStart(string root, params string[] args)
    {
        ProcessStartInfo start = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--no-replace-objects");
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(root);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        return start;
    }
}
