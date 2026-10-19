using System.Text;

internal static partial class Program
{
    private static int SelfTestWorkshop(string temporary)
    {
        string root = Path.Combine(temporary, "workshop");
        Directory.CreateDirectory(root);
        Run(root, null, "init", "--quiet");
        Run(root, null, "config", "core.hooksPath", Path.Combine(temporary, "disabled-hooks"));
        Run(root, null, "config", "user.name", "Public Fixture Author");
        Run(root, null, "config", "user.email", "fixture@example.invalid");
        Run(root, null, "config", "commit.gpgsign", "false");
        Run(root, null, "config", "tag.gpgsign", "false");
        int checks = 0;
        Write("Public/ModInfo.json", "{\"Id\":\"Public\"}");
        Write("Public/SteamWorkshopItemId", "123456789\n");
        Write(".github/fixture.txt", "public workflow");
        Write("build/fixture.txt", "public build tool");
        string clean = Commit("Public Workshop fixture");
        Check(Invoke("scan-index", root).Code == 0);
        Check(Invoke("scan-workshop", root, clean).Code == 0);

        Run(root, null, "rm", "--cached", "--", "Public/SteamWorkshopItemId");
        Check(Invoke("scan-index", root).Code == 1); // Untracked working marker cannot admit staged content.
        Reset();
        Write("Public/SteamWorkshopItemId", "invalid");
        Run(root, null, "add", "--", "Public/SteamWorkshopItemId");
        Write("Public/SteamWorkshopItemId", "123456789");
        Check(Invoke("scan-index", root).Code == 1); // Good working bytes cannot replace bad staged bytes.
        Reset();
        Write("Public/SteamWorkshopItemId", "invalid");
        Check(Invoke("scan-index", root).Code == 0); // Dirty bytes cannot replace the admitted index either.
        Reset();
        Run(root, null, "rm", "--", "Public/SteamWorkshopItemId");
        Stage("Public/nested/SteamWorkshopItemId", "123456789");
        Check(Invoke("scan-index", root).Code == 1);
        Reset();
        Run(root, null, "rm", "--", "Public/ModInfo.json");
        Check(Invoke("scan-index", root).Code == 1);
        Reset();
        Stage("Private/Code/fixture.cs", "// public fixture bytes");
        Check(Invoke("scan-index", root).Code == 1); // Omitting a root manifest cannot hide a mod folder.
        Reset();
        foreach (string value in new[] { "", "0", "0123", "-1", "bad", "1.2", "123456789012345678901", "１２３", "123\n456" })
        {
            Stage("Public/SteamWorkshopItemId", value);
            Check(Invoke("scan-index", root).Code == 1);
        }
        foreach (string value in new[] { "1", "12345678901234567890", " 123\r\n" })
        {
            Stage("Public/SteamWorkshopItemId", value);
            Check(Invoke("scan-index", root).Code == 0);
        }
        Reset();
        string markerBlob = Run(root, "123456789", "hash-object", "-w", "--stdin").Trim();
        Run(root, null, "update-index", "--force-remove", "Public/SteamWorkshopItemId");
        Run(root, null, "update-index", "--add", "--cacheinfo", "100644", markerBlob, "Public/steamworkshopitemid");
        Check(Invoke("scan-index", root).Code == 1);
        Reset();
        Run(root, null, "update-index", "--add", "--cacheinfo", "100644", markerBlob, "Public/steamworkshopitemid");
        Check(Invoke("scan-index", root).Code == 1); // Exact marker plus a case alias is still refused.
        Reset();
        Run(root, null, "update-index", "--add", "--cacheinfo", "100644", markerBlob, "public/Other.txt");
        Check(Invoke("scan-index", root).Code == 1);
        Reset();
        Run(root, null, "update-index", "--add", "--cacheinfo", "120000", markerBlob, "Public/SteamWorkshopItemId");
        Check(Invoke("scan-index", root).Code == 2);
        Reset();
        Run(root, null, "update-index", "--add", "--cacheinfo", "100755", markerBlob, "Public/SteamWorkshopItemId");
        Check(Invoke("scan-index", root).Code == 0); // Both regular Git file modes are allowed.
        Reset();
        Run(root, $"160000 {clean}\tPublic/SteamWorkshopItemId\n", "update-index", "--index-info");
        Check(Invoke("scan-index", root).Code == 2);
        Reset();

        Stage("Private/ModInfo.json", "{\"Id\":\"Private\"}");
        string legacy = Commit("Synthetic legacy local mod");
        string cutoff = Commit("Synthetic reviewed legacy boundary");
        Run(root, null, "rm", "-r", "--", "Private");
        string removed = Commit("Remove synthetic local mod");
        Check(Invoke("scan-history", root, removed).Code == 0); // Historical privacy semantics stay unchanged.
        Check(Invoke("scan-workshop", root, removed).Code == 1); // Fixed real cutoff absent: no exemptions.
        Check(ScanFixture(removed, cutoff).Code == 0); // Internal-only fixture cutoff, never a CLI option.
        Check(ScanFixture(legacy, cutoff).Code == 1); // Missing cutoff cannot exempt an older bad tip.
        Check(ScanFixture(cutoff, cutoff).Code == 1); // Even the exempt cutoff is checked when proposed.
        Check(ScanFixture(removed, legacy).Code == 1); // Later bad commits cannot borrow ancestor exemption.

        string zero = new('0', clean.Length);
        string Update(string id, string branch = "fixture") => $"refs/heads/{branch} {id} refs/heads/{branch} {zero}\n";
        Check(Invoke("pre-push", root, input: Update(clean)).Code == 0);
        Check(Invoke("pre-push", root, input: Update(removed)).Code == 1); // Add-then-delete cannot hide a new private commit.
        Check(Invoke("pre-push", root, input: Update(clean) + Update(legacy, "other")).Code == 1);
        Check(Invoke("pre-push", root, input: $"(delete) {zero} refs/heads/fixture {legacy}\n").Code == 0);
        Check(Invoke("pre-push", root, input: $"(delete) {zero} refs/heads/fixture {legacy}\n" + Update(clean)).Code == 0);
        Run(root, null, "tag", "-a", "legacy-tag", cutoff, "-m", "Public legacy tag");
        Run(root, null, "tag", "-a", "nested-legacy-tag", "legacy-tag", "-m", "Public nested legacy tag");
        string tag = Run(root, null, "rev-parse", "refs/tags/nested-legacy-tag").Trim();
        Check(ScanFixture(tag, cutoff).Code == 1);
        Check(Invoke("pre-push", root, input: Update(tag)).Code == 1);
        Run(root, null, "tag", "-a", "clean-tag", removed, "-m", "Public clean tag");
        Run(root, null, "tag", "-a", "nested-clean-tag", "clean-tag", "-m", "Public nested clean tag");
        tag = Run(root, null, "rev-parse", "refs/tags/nested-clean-tag").Trim();
        Check(ScanFixture(tag, cutoff).Code == 0);

        Stage("NewPrivate/ModInfo.json", "{\"Id\":\"NewPrivate\"}");
        string newPrivate = Commit("Synthetic post-policy local mod");
        Run(root, null, "rm", "-r", "--", "NewPrivate");
        string cleaned = Commit("Remove synthetic post-policy local mod");
        Check(ScanFixture(cleaned, cutoff).Code == 1);
        string cleanTree = Run(root, null, "rev-parse", removed + "^{tree}").Trim();
        string merge = Run(root, "Public merge fixture\n", "commit-tree", cleanTree, "-p", removed, "-p", newPrivate).Trim();
        Check(ScanFixture(merge, cutoff).Code == 1); // Side-parent commits receive the same admission policy.
        Check(Invoke("scan-history", root, merge).Code == 0);
        string orphan = Run(root, "Public orphan fixture\n", "commit-tree", cleanTree).Trim();
        Check(ScanFixture(orphan, cutoff).Code == 0);
        Check(Invoke("scan-workshop", root, orphan).Code == 0);
        // An unrelated tip cannot gain exemption merely because another outgoing graph includes the cutoff.
        string badTree = Run(root, null, "rev-parse", legacy + "^{tree}").Trim();
        string orphanBad = Run(root, "Public orphan local fixture\n", "commit-tree", badTree).Trim();
        Check(ScanFixture(removed, cutoff, extraTip: orphanBad).Code == 1);
        string privateTree = Run(root, null, "rev-parse", cutoff + "^{tree}").Trim();
        string secretLegacy = Run(root, "Public legacy metadata " + FixturePrivatePath() + "\n", "commit-tree", privateTree).Trim();
        string secretClean = Run(root, "Public cleaned tree\n", "commit-tree", cleanTree, "-p", secretLegacy).Trim();
        Check(ScanFixture(secretClean, secretLegacy).Code == 0);
        Check(ScanFixture(secretClean, secretLegacy, privacy: true).Code == 1); // Admission exemption never exempts privacy.
        Check(MainCaptured(["scan-workshop", "--repo", root, "--ref", clean, "--baseline-ref", clean]).Code == 2);
        Check(Invoke("scan-workshop", root, "HEAD").Code == 2);
        Check(MainCaptured(["scan-workshop", "--repo", root]).Code == 2);
        return checks;

        void Check(bool value)
        {
            checks++;
            if (!value) throw new GuardFailure("workshop-self-test-assertion-" + checks);
        }
        void Write(string path, string value)
        {
            string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, value, Utf8);
        }
        void Stage(string path, string value) { Write(path, value); Run(root, null, "add", "--", path); }
        string Commit(string message)
        {
            Run(root, null, "add", "--all");
            Run(root, null, "commit", "--allow-empty", "--quiet", "-m", message);
            return Revision(root);
        }
        void Reset()
        {
            Run(root, null, "reset", "--hard", "--quiet", clean);
            Run(root, null, "clean", "-fd", "--quiet");
        }
        (int Code, string Output) ScanFixture(string tip, string baseline, bool privacy = false, string? extraTip = null)
        {
            List<string> tips = [tip];
            if (extraTip is not null) tips.Add(extraTip);
            return CaptureWorkshop(() => ScanHistory(root, tips, clean.Length, true, privacy, baseline));
        }
    }

    private static (int Code, string Output) MainCaptured(string[] args) => CaptureWorkshop(() => Main(args));

    private static (int Code, string Output) CaptureWorkshop(Func<int> action)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter capture = new();
        try
        {
            Console.SetOut(capture); Console.SetError(capture);
            try { return (action(), capture.ToString()); }
            catch (GuardFailure failure) { return (2, failure.Category); }
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }
}
