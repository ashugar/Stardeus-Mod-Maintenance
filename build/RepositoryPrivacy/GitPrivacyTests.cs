using System.Diagnostics;
using System.IO.Compression;
using System.Text;

internal static partial class Program
{
    private static int SelfTestGit()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "RepositoryPrivacy-Git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        int checks = 0;
        try
        {
            string root = Path.Combine(temporary, "source"); Directory.CreateDirectory(root);
            Run(root, null, "init", "--quiet");
            Run(root, null, "config", "core.hooksPath", Path.Combine(temporary, "disabled-hooks"));
            Run(root, null, "config", "user.name", "Public Fixture Author");
            Run(root, null, "config", "user.email", "fixture@example.invalid");
            Run(root, null, "config", "commit.gpgsign", "false");
            Run(root, null, "config", "tag.gpgsign", "false");
            string file = Path.Combine(root, "fixture.txt");
            File.WriteAllText(file, "PublicAuthor Compatibility.Owner\n", Utf8);
            Run(root, null, "add", "--all");
            Run(root, null, "commit", "--quiet", "-m", "Public baseline");
            string clean = Revision(root);
            Check(Invoke("scan-index", root).Code == 0);
            Check(Invoke("scan-history", root, clean).Code == 0);
            File.WriteAllText(file, FixturePrivatePath(), Utf8);
            Run(root, null, "add", "--all");
            File.WriteAllText(file, "clean working bytes\n", Utf8);
            var staged = Invoke("scan-index", root);
            Check(staged.Code == 1 && staged.Output.Contains("text-machine-path", StringComparison.Ordinal));
            Check(!staged.Output.Contains(FixturePrivatePath(), StringComparison.Ordinal));
            Run(root, null, "reset", "--hard", "--quiet", clean);
            File.WriteAllText(file, FixturePrivatePath(), Utf8);
            File.WriteAllText(Path.Combine(root, "untracked-private.txt"), FixturePrivatePath(), Utf8);
            Check(Invoke("scan-index", root).Code == 0);
            Check(Invoke("scan-history", root, clean).Code == 0);
            File.Delete(Path.Combine(root, "untracked-private.txt"));
            Run(root, null, "reset", "--hard", "--quiet", clean);

            File.WriteAllText(file, FixturePrivatePath(), Utf8);
            Run(root, null, "add", "--all"); Run(root, null, "commit", "--quiet", "-m", "Synthetic private fixture");
            string bad = Revision(root);
            File.WriteAllText(file, "cleaned tip\n", Utf8);
            Run(root, null, "add", "--all"); Run(root, null, "commit", "--quiet", "-m", "Clean tip fixture");
            string cleaned = Revision(root);
            Check(Invoke("scan-history", root, cleaned).Code == 1);
            string zero = new('0', clean.Length);
            string Update(string id, string branch = "fixture") => $"refs/heads/{branch} {id} refs/heads/{branch} {zero}\n";
            Check(Invoke("pre-push", root, input: Update(clean)).Code == 0);
            Check(Invoke("pre-push", root, input: $"HEAD {clean} refs/heads/fixture {zero}\n").Code == 0);
            Check(Invoke("pre-push", root, input: $"HEAD~1 {clean} refs/heads/fixture {zero}\n").Code == 0);
            Check(Invoke("pre-push", root, input: $"{clean} {clean} refs/heads/fixture {zero}\n").Code == 0);
            Check(Invoke("pre-push", root, input: Update(cleaned)).Code == 1);
            Check(Invoke("pre-push", root, input: $"(delete) {zero} refs/heads/fixture {bad}\n").Code == 0);
            Check(Invoke("pre-push", root, input: Update(clean) + Update(cleaned, "other")).Code == 1);
            Check(Invoke("pre-push", root, input: $"(delete) {zero} refs/heads/fixture {bad}\n" + Update(cleaned, "other")).Code == 1);
            Check(Invoke("pre-push", root, input: "malformed\n").Code == 2);
            Check(Invoke("pre-push", root, input: Update(new string('a', clean.Length))).Code == 2);
            Check(Invoke("pre-push", root, input: Update(clean, "bad..ref")).Code == 2);
            Check(Invoke("scan-history", root, "HEAD").Code == 2);

            Run(root, null, "tag", "-a", "clean-tag", clean, "-m", "Public annotated tag");
            string tag = Run(root, null, "rev-parse", "refs/tags/clean-tag").Trim();
            Check(Invoke("scan-history", root, tag).Code == 0);
            Run(root, null, "tag", "-a", "private-tag", clean, "-m", FixturePrivatePath());
            tag = Run(root, null, "rev-parse", "refs/tags/private-tag").Trim();
            Check(Invoke("scan-history", root, tag).Code == 1);
            Run(root, null, "tag", "-a", "nested-tag", "clean-tag", "-m", "Public nested tag");
            tag = Run(root, null, "rev-parse", "refs/tags/nested-tag").Trim();
            Check(Invoke("scan-history", root, tag).Code == 0);
            Run(root, null, "checkout", "--quiet", "--detach", clean);
            Run(root, null, "commit", "--allow-empty", "--quiet", "-m", FixturePrivatePath());
            Check(Invoke("scan-history", root, Revision(root)).Code == 1);
            Run(root, null, "checkout", "--quiet", "--detach", clean);
            string mergeTree = Run(root, null, "rev-parse", clean + "^{tree}").Trim();
            string merge = Run(root, "Public merge fixture\n", "commit-tree", mergeTree, "-p", clean, "-p", bad).Trim();
            Check(Invoke("scan-history", root, merge).Code == 1);

            StageBytes("legacy.csv", [0x80, .. Encoding.ASCII.GetBytes(FixturePrivatePath())]);
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("legacy.csv", [0x80, .. " Public Author"u8.ToArray()]);
            Check(Invoke("scan-index", root).Code == 0);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("encoded.json", Utf8.GetBytes("{\"value\":\"\\u0051:\\u005cfixture\\u005cfile\"}"));
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("bom.json", [0xef, 0xbb, 0xbf, .. Utf8.GetBytes("{\"value\":\"Public Author\"}")]);
            Check(Invoke("scan-index", root).Code == 0);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("bom.json", [0xef, 0xbb, 0xbf, .. Utf8.GetBytes("{\"value\":\"\\u0051:\\u005cfixture\"}")]);
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("malformed.json", Utf8.GetBytes("{\"value\":\"\\u0051:\\u005cfixture\\u005cprivate\""));
            var malformed = Invoke("scan-index", root);
            Check(malformed.Code == 1 && malformed.Output.Contains("invalid-json", StringComparison.Ordinal));
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("utf16.txt", [0xff, 0xfe, .. Encoding.Unicode.GetBytes(FixturePrivatePath())]);
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);

            string token = "gh" + "p_" + new string('A', 36);
            StageBytes("credential.txt", Utf8.GetBytes(token));
            var secret = Invoke("scan-index", root);
            Check(secret.Code == 1 && secret.Output.Contains("high-confidence-secret", StringComparison.Ordinal));
            Check(!secret.Output.Contains(token, StringComparison.Ordinal));
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes(token + ".txt", "public content"u8.ToArray());
            var filename = Invoke("scan-index", root);
            Check(filename.Code == 1 && !filename.Output.Contains(token, StringComparison.Ordinal));
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("encoded-secret.json", Utf8.GetBytes("{\"value\":\"\\u0067" + token[1..] + "\"}"));
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("url.txt", Utf8.GetBytes("https" + "://fixture:synthetic-password@" + "example.invalid/"));
            Check(Invoke("scan-index", root).Code == 0);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("url.txt", Utf8.GetBytes("https" + "://fixture:synthetic-password@" + "example.invalid.real.example.com/"));
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("payload.txt", [.. "PK"u8.ToArray(), 3, 4, 0, 0]);
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("payload.zip", "not a zip"u8.ToArray());
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("payload.jpg", [0xff, 0xd8, 0xff, 0xd9]);
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            StageBytes("payload.txt", File.ReadAllBytes(typeof(Program).Assembly.Location));
            Check(Invoke("scan-index", root).Code == 1);
            Run(root, null, "reset", "--hard", "--quiet", clean);

            StageBytes("Game.dll", File.ReadAllBytes(typeof(Program).Assembly.Location));
            var dependency = Invoke("scan-index", root);
            Check(dependency.Code == 1 && dependency.Output.Contains("external-dependency-dll-refused", StringComparison.Ordinal));
            Run(root, null, "reset", "--hard", "--quiet", clean);
            // A single blob must receive every applicable analysis, even when reused under multiple paths.
            byte[] shared = FixturePng("tEXt", false);
            string blobId = Run(root, shared, "hash-object", "-w", "--stdin").Trim();
            Run(root, null, "update-index", "--add", "--cacheinfo", "100644", blobId, "public.txt");
            Run(root, null, "update-index", "--add", "--cacheinfo", "100644", blobId, "fixture.png");
            var both = Invoke("scan-index", root);
            Check(both.Code == 1 && both.Output.Contains("png-tEXt-machine-path", StringComparison.Ordinal));
            Run(root, null, "reset", "--hard", "--quiet", clean);
            Run(root, null, "update-index", "--add", "--cacheinfo", "120000", blobId, "link");
            Check(Invoke("scan-index", root).Code == 2);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            string tree = Run(root, null, "write-tree").Trim();
            Check(Invoke("scan-history", root, tree).Code == 2);
            Run(root, $"160000 {clean}\tmodule\n", "update-index", "--index-info");
            Check(Invoke("scan-index", root).Code == 2);
            Run(root, null, "reset", "--hard", "--quiet", clean);
            string baseBlob = Run(root, null, "rev-parse", clean + ":fixture.txt").Trim();
            Run(root, $"100644 {baseBlob} 1\tfixture.txt\n100644 {baseBlob} 2\tfixture.txt\n", "update-index", "--index-info");
            Check(Invoke("scan-index", root).Code == 2);
            Run(root, null, "reset", "--hard", "--quiet", clean);

            // Replacement refs must never hide the bad ancestry.
            Run(root, null, "replace", cleaned, clean);
            Check(Invoke("scan-history", root, cleaned).Code == 1);
            Run(root, null, "replace", "-d", cleaned);
            string gitDirectory = Run(root, null, "rev-parse", "--absolute-git-dir").Trim();
            File.WriteAllText(Path.Combine(gitDirectory, "shallow"), clean + "\n", Utf8);
            Check(Invoke("scan-history", root, clean).Code == 2);
            File.Delete(Path.Combine(gitDirectory, "shallow"));
            Directory.CreateDirectory(Path.Combine(gitDirectory, "info"));
            File.WriteAllText(Path.Combine(gitDirectory, "info", "grafts"), cleaned + "\n", Utf8);
            Check(Invoke("scan-history", root, clean).Code == 2);
            File.Delete(Path.Combine(gitDirectory, "info", "grafts"));
            string missing = Run(root, "tree " + new string('a', clean.Length) + "\nauthor Public Fixture Author <fixture@example.invalid> 0 +0000\ncommitter Public Fixture Author <fixture@example.invalid> 0 +0000\n\nMissing tree fixture\n", "hash-object", "-t", "commit", "-w", "--stdin").Trim();
            Check(Invoke("scan-history", root, missing).Code == 2);

            Check(Invoke("scan-index", root).Code == 0);
            string loose = Path.Combine(gitDirectory, "objects", baseBlob[..2], baseBlob[2..]);
            byte[] originalLoose = File.ReadAllBytes(loose);
            FileAttributes originalAttributes = File.GetAttributes(loose);
            try
            {
                using MemoryStream substituted = new();
                byte[] payload = "different public bytes\n"u8.ToArray();
                using (ZLibStream compress = new(substituted, CompressionLevel.Optimal, true))
                {
                    compress.Write(Encoding.ASCII.GetBytes("blob " + payload.Length + "\0"));
                    compress.Write(payload);
                }
                File.SetAttributes(loose, FileAttributes.Normal);
                File.WriteAllBytes(loose, substituted.ToArray());
                var corruption = Invoke("scan-history", root, clean);
                Check(corruption.Code == 2 && corruption.Output.Contains("git-object-hash-mismatch", StringComparison.Ordinal));
            }
            finally { File.WriteAllBytes(loose, originalLoose); File.SetAttributes(loose, originalAttributes); }

            string sha256 = Path.Combine(temporary, "sha256"); Directory.CreateDirectory(sha256);
            Run(sha256, null, "init", "--quiet", "--object-format=sha256");
            Run(sha256, null, "config", "user.name", "Public Fixture Author");
            Run(sha256, null, "config", "user.email", "fixture@example.invalid");
            Run(sha256, null, "config", "commit.gpgsign", "false");
            Run(sha256, null, "config", "core.hooksPath", Path.Combine(temporary, "disabled-hooks"));
            File.WriteAllText(Path.Combine(sha256, "public.txt"), "public bytes\n", Utf8);
            Run(sha256, null, "add", "--all"); Run(sha256, null, "commit", "--quiet", "-m", "Public SHA256 fixture");
            Check(Invoke("scan-history", sha256, Revision(sha256)).Code == 0);
            Check(Invoke("scan-index", sha256).Code == 0);
            string target = Path.Combine(temporary, "remote.git");
            Run(root, null, "init", "--bare", "--quiet", target);
            string hooks = Path.Combine(temporary, "hooks"); Directory.CreateDirectory(hooks);
            Run(root, null, "config", "core.hooksPath", hooks);
            string launcher = "#!/bin/sh\nexec dotnet " + ShellQuote(typeof(Program).Assembly.Location.Replace('\\', '/')) +
                " pre-push --repo " + ShellQuote(root.Replace('\\', '/')) + "\n";
            string hook = Path.Combine(hooks, "pre-push");
            File.WriteAllText(hook, launcher, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Check(Push(root, target, "HEAD:refs/heads/clean") == 0);
            Run(root, null, "checkout", "--quiet", "--detach", cleaned);
            Check(Push(root, target, "HEAD:refs/heads/bad") != 0);
            Check(Run(target, null, "for-each-ref", "--format=%(refname)", "refs/heads/bad").Length == 0);
            Run(root, null, "checkout", "--quiet", "--detach", clean);
            Check(Push(root, target, "HEAD:refs/heads/repeated") == 0);
            Check(Run(root, null, "status", "--porcelain").Length == 0);
            Console.WriteLine($"PASS {checks} Git object, history, encoding, diagnostics, and refusal integration tests.");
            return 0;

            void StageBytes(string name, byte[] content)
            {
                File.WriteAllBytes(Path.Combine(root, name), content);
                Run(root, null, "add", "--", name);
            }
        }
        finally
        {
            string expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string actual = Path.GetFullPath(temporary);
            if (string.Equals(Path.GetDirectoryName(actual), expectedParent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(actual).StartsWith("RepositoryPrivacy-Git-", StringComparison.Ordinal))
            {
                foreach (string temporaryFile in Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories)) File.SetAttributes(temporaryFile, FileAttributes.Normal);
                Directory.Delete(actual, true);
            }
        }

        void Check(bool success)
        {
            checks++;
            if (!success) throw new GuardFailure("git-self-test-assertion-" + checks);
        }
    }

    private static string FixturePrivatePath() => "Q:" + "\\fixture\\private-file";

    private static (int Code, string Output) Invoke(string mode, string root, string? reference = null, string? input = null)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        TextReader originalIn = Console.In;
        using StringWriter capture = new();
        try
        {
            Console.SetOut(capture); Console.SetError(capture); Console.SetIn(new StringReader(input ?? ""));
            List<string> args = [mode, "--repo", root];
            if (reference is not null) { args.Add("--ref"); args.Add(reference); }
            int result = Main(args.ToArray());
            return (result, capture.ToString());
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); Console.SetIn(originalIn); }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static int Push(string root, string target, string refspec)
    {
        ProcessStartInfo start = GitStart(root, "push", "--quiet", target, refspec);
        using Process process = Process.Start(start) ?? throw new GuardFailure("git-push-test-start-failed");
        Task discardedOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        Task discardedError = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        process.WaitForExit();
        discardedOutput.GetAwaiter().GetResult(); discardedError.GetAwaiter().GetResult();
        return process.ExitCode;
    }
    private static string Revision(string root) => Run(root, null, "rev-parse", "HEAD").Trim();

    private static string Run(string root, object? input, params string[] args)
    {
        ProcessStartInfo start = GitStart(root, args);
        start.RedirectStandardInput = true;
        using Process process = Process.Start(start) ?? throw new GuardFailure("git-test-start-failed");
        Task output = Task.CompletedTask;
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (input is byte[] bytes) process.StandardInput.BaseStream.Write(bytes);
        else if (input is string text) process.StandardInput.Write(text);
        process.StandardInput.Close();
        string result = process.StandardOutput.ReadToEnd();
        process.WaitForExit(); error.GetAwaiter().GetResult(); output.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new GuardFailure("git-test-command-failed-" + args[0]);
        return result;
    }
}
