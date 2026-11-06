using System.Diagnostics;
using System.Text.Json;

namespace Storloom.Core;

public sealed class CleanupEngine {
    public RuleCatalog Catalog { get; }
    public CleanerSettings Settings { get; }
    private readonly PathPolicy policy;
    private readonly RuleResolver resolver;
    private readonly Dictionary<string, RuleMatcher> matchers;

    public CleanupEngine(RuleCatalog catalog, CleanerSettings? settings = null) {
        if (catalog.Version != 1 || catalog.Rules.Count == 0)
            throw new InvalidDataException("Unsupported or empty rule catalog.");
        Catalog = catalog;
        Settings = settings ?? new CleanerSettings();
        policy = new PathPolicy(Settings);
        resolver = new RuleResolver(policy, Settings);
        matchers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in catalog.Rules) {
            if (!matchers.TryAdd(rule.Id, new RuleMatcher(rule)))
                throw new InvalidDataException($"Duplicate rule: {rule.Id}");
            foreach (string pattern in rule.Roots) {
                string expanded = resolver.Expand(pattern);
                if (expanded.Contains("**")) throw new InvalidDataException("Root glob cannot contain **.");
                // Validate non-wildcard anchors before a scan has any side effects.
                PathPolicy.Normalize(expanded);
            }
        }
    }

    public static string[] RunningProcesses(CleanupRule rule) {
        var running = new List<string>();
        foreach (string name in rule.Processes) {
            var processes = Process.GetProcessesByName(name);
            if (processes.Any(p => p.Id != Environment.ProcessId)) running.Add(name);
            foreach (var process in processes) process.Dispose();
        }
        return running.ToArray();
    }

    public CleanupPlan Scan(IEnumerable<string>? ruleIds = null,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default) {
        HashSet<string> selected = SelectRules(ruleIds, false);
        var plan = new CleanupPlan { CatalogHash = JsonFiles.Hash(Catalog),
            SettingsHash = JsonFiles.Hash(Settings) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var matcher in matchers.Values.Where(m => selected.Contains(m.Rule.Id))) {
            CleanupRule rule = matcher.Rule;
            string[] running = RunningProcesses(rule);
            if (running.Length > 0) plan.Issues.Add(new(rule.Id, "",
                "Close running applications before cleanup: " + string.Join(", ", running)));
            foreach (string pattern in rule.Roots) {
                cancellation.ThrowIfCancellationRequested();
                try {
                    foreach (string root in resolver.Resolve(pattern, rule.Action == CleanupAction.Delete)) {
                        var directories = new Stack<string>();
                        directories.Push(root);
                        while (directories.TryPop(out string? directory)) {
                            cancellation.ThrowIfCancellationRequested();
                            try {
                                PathPolicy.AssertNoLinks(directory);
                                foreach (string item in Directory.EnumerateFileSystemEntries(directory)) {
                                    cancellation.ThrowIfCancellationRequested();
                                    try {
                                        FileAttributes attrs = File.GetAttributes(item);
                                        if ((attrs & FileAttributes.ReparsePoint) != 0) {
                                            AddIssue(plan, rule.Id, item, "Reparse point skipped.");
                                            continue;
                                        }
                                        if (rule.Action == CleanupAction.Delete) policy.ValidateFile(item);
                                        if ((attrs & FileAttributes.Directory) != 0) {
                                            if (rule.Recursive) directories.Push(item);
                                            continue;
                                        }
                                        if ((attrs & (FileAttributes.System | FileAttributes.ReadOnly)) != 0)
                                            continue;
                                        FileStamp stamp = NativeFile.ReadStamp(item);
                                        if (!matcher.Matches(Path.GetRelativePath(root, item), stamp,
                                            plan.CreatedAt) || !seen.Add(item)) continue;
                                        if (plan.Files.Count >= Settings.MaximumCandidates)
                                            throw new InvalidOperationException("Candidate limit reached; narrow the rules.");
                                        plan.Files.Add(new(rule.Id, root, item, stamp));
                                        bytes += stamp.Bytes;
                                        if (plan.Files.Count % 100 == 0)
                                            progress?.Report(new(rule.Id, plan.Files.Count, bytes));
                                    } catch (Exception ex) when (Recoverable(ex)) {
                                        AddIssue(plan, rule.Id, item, ex.Message);
                                    }
                                }
                            } catch (Exception ex) when (Recoverable(ex)) {
                                AddIssue(plan, rule.Id, directory, ex.Message);
                            }
                        }
                    }
                } catch (Exception ex) when (Recoverable(ex)) {
                    AddIssue(plan, rule.Id, pattern, ex.Message);
                }
            }
            progress?.Report(new(rule.Id, plan.Files.Count, bytes));
        }
        return plan;
    }

    public ApplyResult Apply(CleanupPlan plan, IEnumerable<string> ruleIds, string journalDirectory,
        ISet<string>? filePaths = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default, bool dryRun = false) {
        HashSet<string> selected = SelectRules(ruleIds, true);
        if (selected.Any(id => matchers[id].Rule.Action != CleanupAction.Delete))
            throw new InvalidDataException("Copy/move rules require transfer plans, not the deletion executor.");
        ValidatePlan(plan);
        var files = plan.Files.Where(f => selected.Contains(f.RuleId) &&
            (filePaths == null || filePaths.Contains(f.Path))).ToList();
        var resolved = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) {
            RuleMatcher matcher = matchers[file.RuleId];
            policy.ValidateFile(file.Path);
            string root = policy.ValidateRoot(file.Root);
            if (!resolved.TryGetValue(file.RuleId, out var roots)) {
                roots = new(StringComparer.OrdinalIgnoreCase);
                foreach (string pattern in matcher.Rule.Roots)
                    foreach (string found in resolver.Resolve(pattern)) roots.Add(found);
                resolved.Add(file.RuleId, roots);
            }
            if (!roots.Contains(root) || !PathPolicy.Under(file.Path, root) ||
                !unique.Add(file.Path) || !matcher.Matches(Path.GetRelativePath(root, file.Path),
                    file.Stamp, plan.CreatedAt)) {
                throw new InvalidDataException($"Plan no longer satisfies its rule: {file.Path}");
            }
        }
        if (dryRun) return new(0, 0, 0, 0, "", false);

        using var mutex = new Mutex(false, @"Local\Storloom-" +
            JsonFiles.Hash(plan.UserProfile)[..16]);
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Another cleanup is already running.");
        try {
            Directory.CreateDirectory(journalDirectory);
            string journalPath = Path.Combine(journalDirectory,
                $"apply-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            using var stream = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read);
            using var journal = new StreamWriter(stream);
            void Log(object record) {
                journal.WriteLine(JsonSerializer.Serialize(record));
                journal.Flush();
                stream.Flush(true);
            }
            int deleted = 0, skipped = 0;
            long bytes = 0;
            long Free() => Settings.AllowedDrives.Sum(d => new DriveInfo(
                d.EndsWith(':') ? d + "\\" : d).AvailableFreeSpace);
            long freeBefore = Free();
            Log(new { status = "Started", plan.Id, time = DateTimeOffset.UtcNow,
                rules = selected, count = files.Count });
            var processCache = new Dictionary<string, (DateTimeOffset Time, string[] Names)>();
            foreach (var file in files) {
                if (cancellation.IsCancellationRequested) break;
                CleanupRule rule = matchers[file.RuleId].Rule;
                if (!processCache.TryGetValue(rule.Id, out var state) ||
                    DateTimeOffset.UtcNow - state.Time > TimeSpan.FromSeconds(1)) {
                    state = (DateTimeOffset.UtcNow, RunningProcesses(rule));
                    processCache[rule.Id] = state;
                }
                Log(new { status = "Attempt", file.RuleId, file.Path, file.Stamp.Bytes });
                string status, message = "";
                try {
                    if (state.Names.Length > 0) throw new IOException(
                        "Application is running: " + string.Join(", ", state.Names));
                    policy.ValidateFile(file.Path);
                    PathPolicy.AssertNoLinks(file.Path);
                    NativeFile.DeleteVerified(file);
                    deleted++;
                    bytes += file.Stamp.Bytes;
                    status = "Deleted";
                } catch (Exception ex) when (Recoverable(ex)) {
                    skipped++;
                    status = "Skipped";
                    message = ex.Message;
                }
                Log(new { status, file.RuleId, file.Path, file.Stamp.Bytes, message });
                if ((deleted + skipped) % 50 == 0)
                    progress?.Report(new(file.RuleId, deleted + skipped, bytes));
            }
            var result = new ApplyResult(deleted, skipped, bytes, Free() - freeBefore,
                journalPath, cancellation.IsCancellationRequested);
            Log(new { status = "Completed", result });
            return result;
        } finally { mutex.ReleaseMutex(); }
    }

    private void ValidatePlan(CleanupPlan plan) {
        if (plan.Version != 1 || plan.Computer != Environment.MachineName ||
            !plan.UserProfile.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                StringComparison.OrdinalIgnoreCase) || plan.CatalogHash != JsonFiles.Hash(Catalog) ||
            plan.SettingsHash != JsonFiles.Hash(Settings) || plan.CreatedAt > DateTimeOffset.UtcNow ||
            DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromHours(Settings.PlanLifetimeHours)) {
            throw new InvalidDataException("Expired, foreign, or incompatible plan. Scan again.");
        }
    }

    public List<TransferPlan> CreateTransferPlans(CleanupPlan plan, IEnumerable<string> ruleIds,
        ISet<string>? filePaths = null) {
        ValidatePlan(plan);
        var selected = SelectRules(ruleIds, true);
        var result = new List<TransferPlan>();
        foreach (var group in plan.Files.Where(f => selected.Contains(f.RuleId) &&
            (filePaths == null || filePaths.Contains(f.Path))).GroupBy(f => (f.RuleId, f.Root))) {
            var matcher = matchers[group.Key.RuleId];
            var rule = matcher.Rule;
            if (rule.Action == CleanupAction.Delete) throw new InvalidDataException("Select copy/move rules.");
            var roots = rule.Roots.SelectMany(p => resolver.Resolve(p, false))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!roots.Contains(group.Key.Root)) throw new InvalidDataException("Transfer root no longer matches its rule.");
            var files = new List<TransferFile>();
            foreach (var file in group) {
                if (!PathPolicy.Under(file.Path, file.Root) || !matcher.Matches(
                    Path.GetRelativePath(file.Root, file.Path), file.Stamp, plan.CreatedAt))
                    throw new InvalidDataException("Transfer candidate no longer matches its rule.");
                files.Add(new(Path.GetRelativePath(file.Root, file.Path), file.Stamp));
            }
            string target = resolver.Expand(rule.DestinationRoot!).Replace("{RootName}",
                Path.GetFileName(group.Key.Root), StringComparison.Ordinal);
            result.Add(new() { Source = group.Key.Root, Destination = PathPolicy.Normalize(target),
                Mode = rule.Action == CleanupAction.Copy ? TransferMode.Copy : TransferMode.Move,
                Processes = rule.Processes, Files = files });
        }
        return result;
    }

    private HashSet<string> SelectRules(IEnumerable<string>? ids, bool explicitRequired) {
        if (ids == null && explicitRequired) throw new InvalidDataException("Select rules explicitly.");
        var selected = new HashSet<string>(ids ?? Catalog.Rules.Where(r => r.Enabled).Select(r => r.Id),
            StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 || selected.Any(id => !matchers.ContainsKey(id)))
            throw new InvalidDataException("Select at least one valid rule.");
        return selected;
    }

    private static bool Recoverable(Exception ex) => ex is IOException or
        UnauthorizedAccessException or InvalidDataException or System.Security.SecurityException;
    private static void AddIssue(CleanupPlan plan, string rule, string path, string reason) {
        if (plan.Issues.Count < 5000) plan.Issues.Add(new(rule, path, reason));
    }
}
