using System.Text.Json;
using Storloom.Core;
using Xunit;

namespace Storloom.Tests;

public sealed class RuleAndToolTests : IDisposable {
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
    public RuleAndToolTests() => Directory.CreateDirectory(root);
    private string Dir(string name) { string p = Path.Combine(root, name); Directory.CreateDirectory(p); return p; }
    private static string Repository() {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Storloom.sln"))) current = current.Parent;
        return current?.FullName ?? throw new IOException("Missing repository fixture inputs.");
    }
    [Fact]
    public async Task JsonReplacementSurvivesABriefReaderLockWithoutPublishingPartialData() {
        string directory=Dir("atomic-json"),path=Path.Combine(directory,"state.json");JsonFiles.Write(path,new[]{1});
        var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        Task write=Task.Run(()=>JsonFiles.Write(path,new[]{2,3}));
        try {
            Assert.True(SpinWait.SpinUntil(()=>Directory.GetFiles(directory,"*.tmp").Length>0,TimeSpan.FromSeconds(3)));
            Assert.Equal(new[]{1},JsonFiles.Read<int[]>(path));await Task.Delay(60);Assert.False(write.IsCompleted);
        } finally { held.Dispose(); }
        await write;Assert.Equal(new[]{2,3},JsonFiles.Read<int[]>(path));Assert.Empty(Directory.GetFiles(directory,"*.tmp"));
    }

    [Fact]
    public void MixedSeparatorTraversalCannotEscapeAPlanRoot() {
        Assert.Throws<InvalidDataException>(() => PathPolicy.Normalize(root + "\\cache/../outside"));
        string canonical = PathPolicy.Normalize(root.Replace('\\', '/'));
        Assert.Equal(root, canonical);
    }
    [Fact]
    public void DefaultRuleSetsAreGeneralAndOptionalSetsCanBeSelectedIndependently() {
        string rules = Path.Combine(Repository(), "rules");
        var defaults = RuleSets.Load(rules);
        Assert.Contains(defaults.Rules, r => r.Id == "user-temp");
        Assert.Contains(defaults.Rules, r => r.Id == "chrome-cache");
        Assert.DoesNotContain(defaults.Rules, r => r.Id.StartsWith("yy-") || r.Id.StartsWith("wechat-") || r.Id == "music-cache");
        var optional = RuleSets.Load(rules, ["communication-cn", "native-tools"]);
        Assert.Contains(optional.Rules, r => r.Id == "yy-cache");
        Assert.Contains(optional.Rules, r => r.Action == CleanupAction.Command);
        Assert.DoesNotContain(optional.Rules, r => r.Id == "user-temp");
        Assert.Throws<InvalidDataException>(() => RuleSets.Load(rules, ["unknown"]));
    }
    [Fact]
    public void ImportedRuleSetsRemainSeparateAndRespectSelectionOverridesAndValidation() {
        string rules=Path.Combine(Repository(),"rules"), custom=Dir("rule-sets"), local=Path.Combine(root,"rules.json");
        JsonFiles.Write(Path.Combine(custom,"sets.json"),new RuleSetIndex(1,[new("custom-fixture","fixture.json","Fixture","",[],[],true)]));
        JsonFiles.Write(Path.Combine(custom,"fixture.json"),new RuleCatalog{Rules=[new(){Id="imported-cache",Name="Imported cache",Roots=[Dir("cache")]}]});
        var info=RuleSets.ReadIndex(rules,custom).Sets.Single(s=>s.Id=="custom-fixture");Assert.True(info.IsCustom);
        Assert.Single(RuleSets.Load(rules,["custom-fixture"],customDirectory:custom).Rules);
        Assert.DoesNotContain(RuleSets.Load(rules,["system"],customDirectory:custom).Rules,r=>r.Id=="imported-cache");
        JsonFiles.Write(local,new RuleCatalog{Rules=[new(){Id="imported-cache",Name="Override",Roots=[Dir("other")]}]});
        Assert.Equal("Override",Assert.Single(RuleSets.Load(rules,["custom-fixture"],local,custom).Rules).Name);
        JsonFiles.Write(Path.Combine(custom,"fixture.json"),new RuleCatalog{Rules=[new(){Id="user-temp",Name="Duplicate"}]});
        Assert.Throws<InvalidDataException>(()=>RuleSets.Load(rules,["system","custom-fixture"],customDirectory:custom));
        JsonFiles.Write(Path.Combine(custom,"sets.json"),new RuleSetIndex(1,[new("custom-fixture","../escape.json","Fixture","",[],[],true)]));
        Assert.Throws<InvalidDataException>(()=>RuleSets.ReadIndex(rules,custom));
    }
    [Fact]
    public void LocalOverridesReplaceOnlyMatchingIds() {
        string rules = Path.Combine(Repository(), "rules"), local = Path.Combine(root, "rules.json");
        JsonFiles.Write(local, new RuleCatalog { Rules = [new() { Id = "user-temp", Name = "Private fixture cache", Roots = [Dir("cache")] }] });
        var catalog = RuleSets.Load(rules, ["system"], local);
        Assert.Single(catalog.Rules.Where(r => r.Id == "user-temp"));
        Assert.Equal("Private fixture cache", catalog.Rules.Single(r => r.Id == "user-temp").Name);
        Assert.Contains(catalog.Rules, r => r.Id == "windows-temp");
    }
    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public void FileCleanupCanBeArchivedWithNoncollidingRoots(TransferMode mode) {
        string first = Dir("a/cache"), second = Dir("b/cache");
        File.WriteAllText(Path.Combine(first, "one.log"), "one"); File.WriteAllText(Path.Combine(second, "two.log"), "two");
        var engine = new CleanupEngine(new() { Rules = [new() { Id = "fixture-cache", Name = "Cache", Roots = [first, second], MinimumAgeHours = 0 }] });
        var scan = engine.Scan(); Assert.Equal(2, scan.Files.Count);
        var plans = engine.CreateArchivePlans(scan, ["fixture-cache"], Dir("archive"), mode);
        Assert.Equal(2, plans.Count); Assert.Equal(2, plans.Select(p => p.Destination).Distinct().Count());
        foreach (var plan in plans) {
            var result = new TransferService().Execute(plan, Dir("journals"));
            Assert.Equal(1, result.VerifiedFiles); Assert.Equal(mode == TransferMode.Move ? 1 : 0, result.RemovedFiles);
        }
        Assert.Equal(mode == TransferMode.Copy, File.Exists(Path.Combine(first, "one.log")));
    }
    private (CleanupRule Rule, CleanerSettings Settings) FixtureTool(string mode) {
        string directory = Dir("tool");
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "tool-fixture")))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        string host = Path.Combine(directory, "dotnet.exe");
        File.Copy(Path.Combine(directory, "Storloom.ToolFixture.exe"), host);
        File.WriteAllText(Path.Combine(directory, "fixture-mode.txt"), mode);
        return (new() { Id = "fixture-tool", Name = "Isolated tool", Action = CleanupAction.Command, Tool = CleanupTool.NuGetHttpCache,
                Measurement = new() { Mode = CacheMeasureMode.Directories, Roots = [Dir("tool/cache")] } },
            new() { ToolPaths = new() { ["dotnet"] = host }, CommandTimeoutSeconds = 1 });
    }
    [Fact]
    public async Task ToolRecipeRejectsTamperedArgumentsBeforeStartingAnything() {
        var (rule, settings) = FixtureTool("success"); var plan = ToolCleanupService.Plan(rule, settings);
        Assert.Equal(new[] { "nuget", "locals", "http-cache", "--clear" }, plan.Arguments);
        await Assert.ThrowsAsync<InvalidDataException>(() => ToolCleanupService.Execute(plan with { Arguments = ["arbitrary", "shell"] },
            rule, settings, Dir("journals")));
        Assert.Empty(Directory.GetFiles(Dir("journals")));
        Assert.Throws<InvalidDataException>(() => new CleanupEngine(new() { Rules = [rule with { Tool = (CleanupTool)999 }] }));
        Assert.Throws<InvalidDataException>(() => new CleanupEngine(new() { Rules = [rule with { Action = CleanupAction.Delete, Roots = [Dir("cache")] }] }));
    }
    [Theory]
    [InlineData("success", 0)]
    [InlineData("fail", 37)]
    [InlineData("output", 0)]
    public async Task ToolExecutionRecordsExitStatusAndBoundsOutput(string mode, int expected) {
        var (rule, settings) = FixtureTool(mode);
        var engine = new CleanupEngine(new() { Rules = [rule] }, settings); var scan = engine.Scan();
        Assert.Single(scan.Commands); Assert.Empty(scan.Files);
        var results = await engine.ApplyCommands(scan, [rule.Id], Dir("journals"));
        var result = Assert.Single(results); Assert.Equal(expected, result.ExitCode);
        Assert.False(result.Cancelled); Assert.False(result.TimedOut);
        string journal = File.ReadAllText(result.Journal); Assert.Contains("isolated-tool-fixture", journal);
        Assert.True(journal.Length < 1100000);
        Assert.Contains(expected == 0 ? "Completed" : "Failed", journal);
    }
    [Fact]
    public async Task ToolTimeoutAndCancellationAreDistinctAndKeepTheirJournals() {
        var (rule, settings) = FixtureTool("wait");
        var result = await ToolCleanupService.Execute(ToolCleanupService.Plan(rule, settings), rule, settings, Dir("journals"));
        Assert.True(result.TimedOut); Assert.False(result.Cancelled); Assert.Contains("TimedOut", File.ReadAllText(result.Journal));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        result = await ToolCleanupService.Execute(ToolCleanupService.Plan(rule, settings), rule,
            settings with { CommandTimeoutSeconds = 10 }, Dir("journals"), cancellation.Token);
        Assert.True(result.Cancelled); Assert.False(result.TimedOut); Assert.Contains("Cancelled", File.ReadAllText(result.Journal));
    }
    [Fact]
    public void NativeSizeReportCanBeSelectedWithoutAcceptingArbitraryCommands() {
        var (original, settings) = FixtureTool("pip-info-fixture");
        string python = Path.Combine(root,"tool","python.exe");File.Copy(settings.ToolPaths["dotnet"],python);
        var rule=original with {Tool=CleanupTool.PipCache,Measurement=new(){Mode=CacheMeasureMode.ToolReport}};
        settings=settings with {ToolPaths=new(){["python"]=python}};
        var plan=new CleanupEngine(new(){Rules=[rule]},settings).Scan();var measured=plan.Measurements[rule.Id];
        Assert.True(measured.Complete);Assert.True(measured.Estimated);Assert.Equal(2700002000,measured.Bytes);
        Assert.Throws<InvalidDataException>(()=>new CleanupEngine(new(){Rules=[rule with {Tool=CleanupTool.UvPrune}]}));
    }
    [Fact]
    public async Task NativeCleanupMeasuresActualBeforeAndAfterAndDoesNotCountRetainedFiles() {
        var (rule, settings) = FixtureTool("clean-cache"); string cache = rule.Measurement!.Roots.Single();
        File.WriteAllBytes(Path.Combine(cache, "removable.cache"), new byte[512]);
        File.WriteAllBytes(Path.Combine(cache, "retained.cache"), new byte[32]);
        var engine = new CleanupEngine(new() { Rules = [rule] }, settings); var plan = engine.Scan();
        Assert.Equal(544, plan.Measurements[rule.Id].Bytes); Assert.True(plan.Measurements[rule.Id].Complete);
        var result = Assert.Single(await engine.ApplyCommands(plan, [rule.Id], Dir("journals")));
        Assert.Equal(544, result.BeforeBytes); Assert.Equal(32, result.AfterBytes); Assert.Equal(512, result.FreedBytes);
        Assert.True(File.Exists(Path.Combine(cache, "retained.cache")));
    }
    [Fact]
    public void CommandRulesRejectArbitraryJsonFieldsAndCannotUseFileExecutors() {
        string path = Path.Combine(root, "untrusted.json");
        File.WriteAllText(path, "{\"version\":1,\"rules\":[{\"id\":\"untrusted-tool\",\"name\":\"Tool\",\"action\":\"Command\",\"tool\":\"UvPrune\",\"script\":\"unsafe\"}]}");
        Assert.Throws<JsonException>(() => JsonFiles.Read<RuleCatalog>(path));
        var (rule, settings) = FixtureTool("success"); var engine = new CleanupEngine(new() { Rules = [rule] }, settings);
        var scan = engine.Scan();
        Assert.Throws<InvalidDataException>(() => engine.Apply(scan, [rule.Id], Dir("journals")));
        Assert.Throws<InvalidDataException>(() => engine.CreateArchivePlans(scan, [rule.Id], Dir("archive"), TransferMode.Move));
    }
    public void Dispose() {
        string fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
        if (!PathPolicy.Under(root, fixtures)) throw new IOException("Invalid fixture boundary.");
        Directory.Delete(root, true);
    }
}
