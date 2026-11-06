using System.Diagnostics;
using System.Runtime.InteropServices;
using Storloom.Core;
using Xunit;

namespace Storloom.Tests;

public sealed class CleanupTests : IDisposable {
    private readonly string directory;
    private readonly string cache;
    private readonly CleanerSettings settings;

    public CleanupTests() {
        // Keep all fixture mutations under the test output directory on the workspace drive.
        directory = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        cache = Path.Combine(directory, "cache");
        Directory.CreateDirectory(cache);
        settings = new CleanerSettings { AllowedDrives = [Path.GetPathRoot(cache)!] };
    }

    private CleanupRule Rule => new() { Id = "fixture-cache", Name = "Fixture",
        Roots = [cache], MinimumAgeHours = 24, Risk = RiskLevel.Low };
    private CleanupEngine Engine(CleanupRule? rule = null) =>
        new(new RuleCatalog { Rules = [rule ?? Rule] }, settings);
    private string Old(string name, string content = "old") {
        string path = Path.Combine(cache, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        return path;
    }
    private ApplyResult Apply(CleanupEngine engine, CleanupPlan plan, ISet<string>? files = null,
        bool dryRun = false, CancellationToken cancellation = default) => engine.Apply(plan,
            ["fixture-cache"], Path.Combine(directory, "journals"), files,
            cancellation: cancellation, dryRun: dryRun);

    [Theory]
    [InlineData("one.log", "**/*.log", true)]
    [InlineData("sub/one.log", "**/*.log", true)]
    [InlineData("one.log", "*.log", true)]
    [InlineData("sub/one.log", "*.log", false)]
    [InlineData("pending/setup.exe", "pending/*.exe", true)]
    [InlineData("pending/sub/setup.exe", "pending/*.exe", false)]
    [InlineData("a1.tmp", "a?.tmp", true)]
    [InlineData("old[1].tmp", "old[1].tmp", true)]
    public void GlobsHaveSegmentSemantics(string path, string pattern, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(path, pattern));

    [Fact]
    public void ScanRespectsAgeIncludesExcludesAndSize() {
        Old("keep.tmp");
        Old("sub/old.tmp");
        Old("sub/old.log");
        Old("small.tmp", "");
        string copied = Old("copied.tmp");
        File.SetCreationTimeUtc(copied, DateTime.UtcNow);
        string fresh = Old("fresh.tmp");
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);
        var engine = Engine(Rule with { Include = ["**/*.tmp"], Exclude = ["keep.tmp"],
            MinimumBytes = 1 });
        var plan = engine.Scan();
        Assert.Single(plan.Files);
        Assert.EndsWith("sub\\old.tmp", plan.Files[0].Path);
    }

    [Fact]
    public void NonrecursiveRulesSkipChildren() {
        Old("a.log"); Old("sub/b.log");
        Assert.Single(Engine(Rule with { Recursive = false }).Scan().Files);
    }

    [Fact]
    public void LiteralDeletionSurvivesJsonAndPreservesNeighbors() {
        string target = Old("file[1].tmp"), neighbor = Old("file1.tmp");
        var engine = Engine();
        string planFile = Path.Combine(directory, "plan.json");
        JsonFiles.Write(planFile, engine.Scan());
        var plan = JsonFiles.Read<CleanupPlan>(planFile);
        var result = Apply(engine, plan, new HashSet<string> { target });
        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(neighbor));
        Assert.Contains("\"status\":\"Deleted\"", File.ReadAllText(result.Journal));
    }

    [Fact]
    public void LockedAndChangedFilesYieldPartialSuccess() {
        string locked = Old("locked.tmp"), changed = Old("changed.tmp"), good = Old("good.tmp");
        var engine = Engine();
        var plan = engine.Scan();
        File.AppendAllText(changed, "change");
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = Apply(engine, plan);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(2, result.Skipped);
        Assert.False(File.Exists(good));
        Assert.True(File.Exists(changed));
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void SameSizeAndTimesDoNotHideAReplacementFile() {
        string path = Old("replace.tmp");
        var engine = Engine();
        var plan = engine.Scan();
        var stamp = plan.Files[0].Stamp;
        // Keep the original alive under another name to prevent file-ID reuse.
        File.Move(path, path + ".original");
        File.WriteAllText(path, "new");
        File.SetCreationTimeUtc(path, new DateTime(stamp.CreationUtcTicks, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(path, new DateTime(stamp.WriteUtcTicks, DateTimeKind.Utc));
        var result = Apply(engine, plan);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void RepeatingAPlanDoesNotDeleteNewFiles() {
        Old("a.tmp");
        var engine = Engine();
        var plan = engine.Scan();
        Assert.Equal(1, Apply(engine, plan).Deleted);
        string newFile = Old("new.tmp");
        Assert.Equal(1, Apply(engine, plan).Skipped);
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public void DryRunAndCancellationDoNotDeleteFiles() {
        string path = Old("a.tmp");
        var engine = Engine();
        var plan = engine.Scan();
        Assert.Equal(0, Apply(engine, plan, dryRun: true).Deleted);
        Assert.True(File.Exists(path));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = Apply(engine, plan, cancellation: cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PlanScopeAndCatalogTamperingFailBeforeAnyDeletion() {
        string path = Old("a.tmp");
        var engine = Engine();
        var plan = engine.Scan();
        string outside = Path.Combine(directory, "cache-other", "outside.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "keep");
        var malicious = plan with { Files = [plan.Files[0], plan.Files[0] with { Path = outside }] };
        Assert.Throws<InvalidDataException>(() => Apply(engine, malicious));
        Assert.True(File.Exists(path));
        Assert.Throws<InvalidDataException>(() => Apply(engine, plan with { CatalogHash = "bad" }));
        Assert.Throws<InvalidDataException>(() => Apply(engine, plan with { SettingsHash = "bad" }));
        Assert.Throws<InvalidDataException>(() => Apply(engine, plan with {
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) }));
    }

    [Fact]
    public void APlanCannotBypassGlobFiltersOrIntroduceRoots() {
        string path = Old("a.log"), excluded = Old("keep.txt");
        var engine = Engine(Rule with { Include = ["**/*.log"] });
        var plan = engine.Scan();
        var entry = plan.Files[0] with { Path = excluded, Stamp = NativeFile.ReadStamp(excluded) };
        Assert.Throws<InvalidDataException>(() => Apply(engine, plan with { Files = [entry] }));
        entry = plan.Files[0] with { Root = directory };
        Assert.Throws<InvalidDataException>(() => Apply(engine, plan with { Files = [entry] }));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ProtectedDirectoriesAndDisabledDrivesAreRejected() {
        var policy = new PathPolicy(settings);
        Assert.Throws<InvalidDataException>(() => policy.ValidateRoot(Path.GetPathRoot(cache)!));
        Assert.Throws<InvalidDataException>(() => policy.ValidateRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.Throws<InvalidDataException>(() => policy.ValidateFile(
            Path.Combine(cache, ".git", "objects", "a")));
        Assert.Throws<InvalidDataException>(() => new PathPolicy(new CleanerSettings {
            AllowedDrives = ["Z:\\"] }).ValidateFile(cache));
        Assert.Throws<InvalidDataException>(() => PathPolicy.Normalize("relative\\file"));
        Assert.Throws<InvalidDataException>(() => PathPolicy.Normalize(@"\\server\share\file"));
        Assert.Throws<InvalidDataException>(() => PathPolicy.Normalize(cache + "\\file:stream"));
    }

    [Fact]
    public void ReparsePointsAreSkippedAndReplacementIsRejected() {
        string outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.tmp"), "keep");
        string link = Path.Combine(cache, "linked");
        Junction(link, outside);
        var engine = Engine();
        Assert.Empty(engine.Scan().Files);
        Assert.Contains(engine.Scan().Issues, i => i.Reason.Contains("Reparse"));
        Directory.Delete(link);
        string path = Old("sub/a.tmp");
        var plan = engine.Scan();
        Directory.Move(Path.GetDirectoryName(path)!, Path.Combine(directory, "original"));
        Junction(Path.GetDirectoryName(path)!, outside);
        Assert.Equal(1, Apply(engine, plan).Skipped);
        Assert.True(File.Exists(Path.Combine(outside, "keep.tmp")));
        Directory.Delete(Path.GetDirectoryName(path)!);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existing, IntPtr security);

    [Fact]
    public void HardLinksAreNotCountedAsReclaimableFiles() {
        string original = Old("a.tmp");
        Assert.True(CreateHardLinkW(Path.Combine(cache, "b.tmp"), original, IntPtr.Zero));
        Assert.Empty(Engine().Scan().Files);
    }

    [Fact]
    public void RunningApplicationBlocksItsRule() {
        Old("a.tmp");
        var engine = Engine(Rule with { Processes = ["cmd"] });
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause") {
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            UseShellExecute = false })!;
        try {
            var result = Apply(engine, engine.Scan());
            Assert.Equal(0, result.Deleted);
            Assert.Equal(1, result.Skipped);
        } finally {
            process.StandardInput.WriteLine();
            if (!process.WaitForExit(3000)) process.Kill();
        }
    }

    [Fact]
    public void WildcardRootsAndOverlappingRulesDoNotDuplicateFiles() {
        Old("sub/a.tmp"); Old("other/b.tmp");
        var wildcard = Rule with { Roots = [Path.Combine(cache, "*")] };
        var engine = new CleanupEngine(new RuleCatalog { Rules = [wildcard,
            Rule with { Id = "second-cache" }] }, settings);
        Assert.Equal(2, engine.Scan().Files.Count);
    }

    [Fact]
    public void InvalidRulesAndUnknownJsonMembersFailEarly() {
        Assert.Throws<InvalidDataException>(() => Engine(Rule with { Include = ["../*"] }));
        Assert.Throws<InvalidDataException>(() => Engine(Rule with { MinimumAgeHours = -1 }));
        Assert.Throws<InvalidDataException>(() => Engine(Rule with { Roots = ["%Unknown%\\cache"] }));
        string json = Path.Combine(directory, "invalid.json");
        File.WriteAllText(json, "{\"version\":1,\"ruels\":[]}");
        Assert.Throws<System.Text.Json.JsonException>(() => JsonFiles.Read<RuleCatalog>(json));
        Assert.Throws<InvalidDataException>(() => Engine().Scan(["missing-rule"]));
    }

    private static void Junction(string link, string target) {
        // Use PowerShell end-to-end; cmd is only a paused fixture process above.
        using var process = Process.Start(new ProcessStartInfo("powershell.exe") {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-Command",
                $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null" }
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose() {
        string fixtureBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
        if (!PathPolicy.Under(Path.GetFullPath(directory), fixtureBase))
            throw new IOException("Unexpected fixture cleanup boundary.");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
