using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Storloom.Core;
using Storloom.Core.Chats;
using Storloom.Core.Localization;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try {
    if (args.Length == 0 || args[0] is "help" or "--help") {
        Console.WriteLine("""
            Storloom — storage analysis, verified transfers, rules, and local chat archives.
            analyze --root FOLDER [--out REPORT.json]
            rules [--catalog FILE] [--language en|zh-CN]
            scan [--catalog FILE] [--settings FILE] [--rules ID,ID] [--out PLAN.json]
            apply --plan PLAN.json --rules ID,ID [--dry-run | --yes] [--allow-review]
            transfer-plan --source FOLDER --destination NEW_FOLDER [--mode Copy|Move] --out PLAN.json
            transfer-apply --plan PLAN.json --yes [--journal FOLDER]
            chat-discover [--root FOLDER]
            chat-export --platform QQ|WeChat (--root ACCOUNT | --backup MANIFEST.json)
                        --destination NEW_FOLDER [--key-file PRIVATE_FILE]
            Common options: --language en|zh-CN, --journal FOLDER, --sets ID,ID.
            No key text is accepted in command-line arguments. Chat export never deletes its source.
            """);
        return 0;
    }
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string[] flags = ["--yes", "--dry-run", "--allow-review"];
    string[] values = ["--catalog", "--settings", "--rules", "--out", "--plan", "--journal", "--root",
        "--source", "--destination", "--mode", "--platform", "--key-file", "--backup", "--language", "--sets"];
    for (int i = 1; i < args.Length; i++) {
        string key = args[i];
        if (flags.Contains(key)) options.Add(key, "true");
        else if (values.Contains(key) && i + 1 < args.Length) options.Add(key, args[++i]);
        else throw new ArgumentException($"Unknown or incomplete option: {key}");
    }
    string? Option(string key) => options.GetValueOrDefault(key);
    string Required(string key) => Option(key) ?? throw new ArgumentException($"{key} is required.");
    void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonFiles.Options));
    string data = Path.Combine(AppContext.BaseDirectory, "data");
    string journal = Option("--journal") ?? Path.Combine(data, "journals");
    string output = Option("--out") ?? Path.Combine(data, "plans", $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    L.SetLanguage(Option("--language") ?? "en");
    if (args[0] == "analyze") {
        var result = new StorageScanner().Scan(Required("--root"), cancellation: cancellation.Token);
        JsonFiles.Write(output, result); Console.WriteLine($"{result.Files} files, {result.Bytes} logical bytes. {output}");
        return result.Cancelled ? 130 : 0;
    }
    if (args[0] == "transfer-plan") {
        if (!Enum.TryParse<TransferMode>(Option("--mode") ?? "Copy", true, out var mode) || !Enum.IsDefined(mode))
            throw new ArgumentException("Mode must be Copy or Move.");
        var plan = new TransferService().Plan(Required("--source"), Required("--destination"), mode,
            cancellation: cancellation.Token);
        JsonFiles.Write(output, plan); Console.WriteLine($"{plan.Files.Count} files. Preview: {output}"); return 0;
    }
    if (args[0] == "transfer-apply") {
        if (!options.ContainsKey("--yes")) throw new ArgumentException("Review the plan and provide --yes.");
        var result = new TransferService().Execute(JsonFiles.Read<TransferPlan>(Required("--plan")),
            journal, cancellation: cancellation.Token); Print(result);
        return result.Cancelled ? 130 : result.Issues.Count > 0 ? 2 : 0;
    }
    if (args[0] == "chat-discover") {
        Print(ChatDiscovery.Discover(Option("--root") is string root ? [root] : null)); return 0;
    }
    if (args[0] == "chat-export") {
        if (!Enum.TryParse<ChatPlatform>(Required("--platform"), true, out var platform) || !Enum.IsDefined(platform))
            throw new ArgumentException("Platform must be QQ or WeChat.");
        byte[]? key = null;
        try {
            if (Option("--key-file") is string keyFile) {
                string value = File.ReadAllText(keyFile).Trim();
                key = platform == ChatPlatform.QQ ? Encoding.ASCII.GetBytes(value) : Convert.FromHexString(value);
            }
            var exporter = new ChatExporter();
            var result = Option("--backup") is string backup ? exporter.ExportVerifiedBackup(backup,
                Required("--destination"), platform, key, cancellation: cancellation.Token) :
                exporter.Export(Required("--root"), Required("--destination"), platform, key,
                    cancellation: cancellation.Token);
            Print(result); return result.Complete ? 0 : 2;
        } finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
    }
    var catalog = Option("--catalog") is string catalogFile ? JsonFiles.Read<RuleCatalog>(catalogFile) :
        RuleSets.Load(Path.Combine(AppContext.BaseDirectory, "rules"), Option("--sets")?.Split(',', StringSplitOptions.RemoveEmptyEntries));
    var settings = Option("--settings") is string settingsPath ? JsonFiles.Read<CleanerSettings>(settingsPath) : new();
    var engine = new CleanupEngine(catalog, settings);
    string[]? selected = Option("--rules")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (args[0] == "rules") {
        foreach (var rule in catalog.Rules)
            Console.WriteLine($"{rule.Id,-24} {rule.Action,-6} {rule.Risk,-7} {rule.MinimumAgeHours,5}h  {L.RuleName(rule)}");
    } else if (args[0] == "scan") {
        var plan = engine.Scan(selected, cancellation: cancellation.Token); JsonFiles.Write(output, plan);
        foreach (var group in plan.Files.GroupBy(f => f.RuleId))
            Console.WriteLine($"{group.Key,-24} {group.Count(),7} files  {group.Sum(f => f.Stamp.Bytes) / 1073741824d,8:F3} GiB");
        foreach (var command in plan.Commands) Print(command);
        Console.WriteLine($"Plan: {Path.GetFullPath(output)}\nIssues: {plan.Issues.Count}\nNo files deleted.");
    } else if (args[0] == "apply") {
        if (selected == null || selected.Length == 0) throw new ArgumentException("--rules is required.");
        bool dryRun = options.ContainsKey("--dry-run");
        if (!dryRun && !options.ContainsKey("--yes")) throw new ArgumentException("Review the plan and provide --yes.");
        if (!dryRun && !options.ContainsKey("--allow-review") && catalog.Rules.Any(r =>
            selected.Contains(r.Id, StringComparer.OrdinalIgnoreCase) && r.Risk == RiskLevel.Review))
            throw new ArgumentException("Review rules also require --allow-review.");
        var plan = JsonFiles.Read<CleanupPlan>(Required("--plan"));
        var ids = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Any(id => !catalog.Rules.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Unknown rule ID.");
        string[] deletes = catalog.Rules.Where(r => ids.Contains(r.Id) && r.Action == CleanupAction.Delete).Select(r => r.Id).ToArray();
        string[] commands = catalog.Rules.Where(r => ids.Contains(r.Id) && r.Action == CleanupAction.Command).Select(r => r.Id).ToArray();
        string[] transfers = ids.Except(deletes.Concat(commands), StringComparer.OrdinalIgnoreCase).ToArray();
        var transferPlans = transfers.Length == 0 ? [] : engine.CreateTransferPlans(plan, transfers);
        int exit = 0;
        foreach (var transfer in transferPlans) {
            if (dryRun) Print(transfer);
            else {
                var result = new TransferService().Execute(transfer, journal, cancellation: cancellation.Token); Print(result);
                if (result.Cancelled) return 130;
                if (result.Issues.Count > 0) exit = 2;
            }
        }
        if (commands.Length > 0) {
            if (dryRun) Print(plan.Commands.Where(c => commands.Contains(c.RuleId)));
            else foreach (var result in await engine.ApplyCommands(plan, commands, journal, cancellation.Token)) {
                Print(result); if (result.Cancelled) return 130;
                if (result.ExitCode != 0 || result.TimedOut) return 2;
            }
        }
        if (deletes.Length > 0) {
            var result = engine.Apply(plan, deletes, journal, cancellation: cancellation.Token, dryRun: dryRun); Print(result);
            if (result.Cancelled) return 130;
            if (result.Skipped > 0) exit = 2;
        }
        return exit;
    } else throw new ArgumentException($"Unknown command: {args[0]}");
    return 0;
} catch (OperationCanceledException) {
    Console.Error.WriteLine("Cancelled. Completed work is recorded in the journal."); return 130;
} catch (Exception ex) {
    Console.Error.WriteLine(ex.Message); return 1;
}
