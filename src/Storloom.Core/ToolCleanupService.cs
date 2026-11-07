using System.Diagnostics;
using System.Security.Cryptography;

namespace Storloom.Core;

public sealed record ToolCommandPlan(string RuleId, CleanupTool Tool, string Executable,
    string Sha256, string[] Arguments, string? Script = null, string? ScriptSha256 = null);
public sealed record ToolCommandResult(string RuleId, int? ExitCode, bool Cancelled, bool TimedOut, string Journal);

// Recipes are intentionally owned by the application, never interpolated into a shell.
public static class ToolCleanupService {
    public static ToolCommandPlan Plan(CleanupRule rule, CleanerSettings settings) {
        if (rule.Action != CleanupAction.Command || rule.Tool is not CleanupTool tool || !Enum.IsDefined(tool))
            throw new InvalidDataException("Select a supported native cleanup tool.");
        string name = tool switch { CleanupTool.NuGetHttpCache => "dotnet", CleanupTool.NpmCache => "node",
            CleanupTool.PipCache => "python", CleanupTool.UvPrune => "uv", _ => throw new InvalidDataException("Unknown tool.") };
        string executable = Resolve(name, settings);
        string? script = tool == CleanupTool.NpmCache ? Path.Combine(Path.GetDirectoryName(executable)!,
            "node_modules", "npm", "bin", "npm-cli.js") : null;
        string[] arguments = tool switch {
            CleanupTool.NuGetHttpCache => ["nuget", "locals", "http-cache", "--clear"],
            CleanupTool.NpmCache => [script!, "cache", "clean", "--force"],
            CleanupTool.PipCache => ["-m", "pip", "cache", "purge"],
            CleanupTool.UvPrune => ["cache", "prune"],
            _ => throw new InvalidDataException("Unknown tool.")
        };
        return new(rule.Id, tool, executable, Digest(executable), arguments, script,
            script == null ? null : Digest(script));
    }

    private static string Resolve(string name, CleanerSettings settings) {
        if (settings.ToolPaths.TryGetValue(name, out string? configured)) {
            string path = PathPolicy.Normalize(configured);
            if (!Path.GetFileName(path).Equals(name + ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Tool path must name the expected executable: " + name + ".exe");
            PathPolicy.AssertNoLinks(path); return path;
        }
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string candidate = Path.Combine(directory.Trim('"'), name + ".exe");
            if (File.Exists(candidate)) { PathPolicy.AssertNoLinks(candidate); return PathPolicy.Normalize(candidate); }
        }
        throw new FileNotFoundException("Tool not found. Install it or configure toolPaths: " + name);
    }

    private static string Digest(string path) {
        PathPolicy.AssertNoLinks(path);
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    public static void Validate(ToolCommandPlan plan, CleanupRule rule, CleanerSettings settings) {
        if (JsonFiles.Hash(plan) != JsonFiles.Hash(Plan(rule, settings)))
            throw new InvalidDataException("Tool, recipe, or executable changed since preview. Scan again.");
    }

    public static async Task<ToolCommandResult> Execute(ToolCommandPlan plan, CleanupRule rule,
        CleanerSettings settings, string journalDirectory, CancellationToken cancellation = default) {
        Validate(plan, rule, settings);
        if (settings.CommandTimeoutSeconds is < 1 or > 3600) throw new InvalidDataException("Invalid command timeout.");
        if (CleanupEngine.RunningProcesses(rule).Length > 0)
            throw new IOException("Close applications listed by the cleanup rule before running its tool.");
        cancellation.ThrowIfCancellationRequested();
        Directory.CreateDirectory(journalDirectory);
        string journal = Path.Combine(journalDirectory, $"tool-{Guid.NewGuid():N}.json");
        using var executableLock = new FileStream(plan.Executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var scriptLock = plan.Script == null ? null : new FileStream(plan.Script, FileMode.Open, FileAccess.Read, FileShare.Read);
        Validate(plan, rule, settings);
        var start = new ProcessStartInfo(plan.Executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetFullPath(journalDirectory), RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in plan.Arguments) start.ArgumentList.Add(argument);
        JsonFiles.Write(journal, new { state = "Starting", plan });
        using var process = new Process { StartInfo = start };
        bool cancelled = false, timedOut = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.CommandTimeoutSeconds));
        var output = new System.Text.StringBuilder();
        object gate = new();
        async Task Drain(StreamReader reader) {
            char[] buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
                lock (gate) if (output.Length < 1024 * 1024) output.Append(buffer, 0, Math.Min(count, 1024 * 1024 - output.Length));
        }
        try {
            if (!process.Start()) throw new IOException("The cleanup tool did not start.");
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            try {
                await process.WaitForExitAsync(deadline.Token);
                await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) {
                cancelled = cancellation.IsCancellationRequested; timedOut = !cancelled;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.StandardOutput.Dispose(); process.StandardError.Dispose();
                try { await Task.WhenAll(stdout, stderr); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            var result = new ToolCommandResult(rule.Id, process.ExitCode, cancelled, timedOut, journal);
            JsonFiles.Write(journal, new { state = cancelled ? "Cancelled" : timedOut ? "TimedOut" :
                process.ExitCode == 0 ? "Completed" : "Failed", plan, result, output = output.ToString() });
            return result;
        } catch (Exception ex) {
            JsonFiles.Write(journal, new { state = "Failed", plan, error = ex.Message }); throw;
        }
    }
}
