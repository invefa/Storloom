using System.Diagnostics;

namespace Storloom.Core;

public static class CacheMeasurementService {
    public static CacheMeasurement Measure(ToolCommandPlan plan, CleanupRule rule, CleanerSettings settings,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default, bool exact = false) {
        var issues = new List<ScanIssue>(); long bytes = 0, files = 0;
        try {
            ToolCleanupService.Validate(plan, rule, settings);
            var spec = rule.Measurement ?? new();
            if (exact && spec.Mode == CacheMeasureMode.ToolReport) spec = spec with { Mode = CacheMeasureMode.Tool };
            IEnumerable<string> roots;
            if (spec.Mode == CacheMeasureMode.Directories) {
                var resolver = new RuleResolver(new PathPolicy(settings), settings);
                roots = spec.Roots.SelectMany(p => resolver.Resolve(p, false)).ToArray();
            } else {
                string[] args = plan.Tool switch {
                    CleanupTool.NuGetHttpCache => ["nuget", "locals", "http-cache", "--list"],
                    CleanupTool.NpmCache => [plan.Script!, "config", "get", "cache"],
                    CleanupTool.PipCache => ["-m", "pip", "cache", spec.Mode == CacheMeasureMode.ToolReport ? "info" : "dir"],
                    CleanupTool.UvPrune => ["cache", "dir"],
                    _ => throw new InvalidDataException("Unsupported size recipe.")
                };
                var start = new ProcessStartInfo(plan.Executable) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                if (plan.Tool == CleanupTool.PipCache) start.Environment["PYTHONIOENCODING"] = "utf-8";
                foreach (string arg in args) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Cannot query cache location.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(30, settings.CommandTimeoutSeconds)));
                var stdout = ReadBounded(process.StandardOutput, timeout.Token);
                var stderr = ReadBounded(process.StandardError, timeout.Token);
                try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); process.WaitForExit(); throw; }
                string output = stdout.GetAwaiter().GetResult(); _ = stderr.GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new IOException("Cache location query failed with exit code " + process.ExitCode);
                if (spec.Mode == CacheMeasureMode.ToolReport) {
                    var sizes = System.Text.RegularExpressions.Regex.Matches(output,
                        @"(?im)^(?:Package index page cache size|Locally built wheels size):\s*([0-9]+(?:\.[0-9]+)?)\s*(bytes?|kB|MB|GB|TB)\s*$");
                    if (sizes.Count != 2) throw new InvalidDataException("The native cache size report was not recognized.");
                    long reported = 0;
                    foreach (System.Text.RegularExpressions.Match match in sizes) {
                        decimal value = decimal.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        decimal multiplier = match.Groups[2].Value.ToLowerInvariant() switch {
                            "kb" => 1000m, "mb" => 1000000m, "gb" => 1000000000m, "tb" => 1000000000000m, _ => 1m };
                        reported = checked(reported + (long)(value * multiplier));
                    }
                    return new(reported, 0, true, "ToolReport", []) { Estimated = true };
                }
                string location = output.Trim();
                if (plan.Tool == CleanupTool.NuGetHttpCache) {
                    location = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("http-cache:", StringComparison.OrdinalIgnoreCase))
                        ?["http-cache:".Length..].Trim() ?? throw new InvalidDataException("Cache location was not recognized.");
                }
                if (plan.Tool == CleanupTool.NpmCache) location = Path.Combine(location, "_cacache");
                roots = [PathPolicy.Normalize(location)];
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.OrderBy(p => p.Length)) {
                cancellation.ThrowIfCancellationRequested();
                if (seen.Any(p => root.Equals(p, StringComparison.OrdinalIgnoreCase) || PathPolicy.Under(root, p))) continue;
                seen.Add(root);
                if (!Directory.Exists(root)) continue;
                var scan = new StorageScanner().Scan(root, progress, cancellation);
                cancellation.ThrowIfCancellationRequested();
                bytes += scan.Bytes; files += scan.Files; issues.AddRange(scan.Issues);
            }
            return new(bytes, files, issues.Count == 0, spec.Mode.ToString(), issues);
        } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
            issues.Add(new(rule.Id, "", "Cache size query timed out."));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException or FormatException) {
            issues.Add(new(rule.Id, "", ex.Message));
        }
        return new(bytes, files, false, "Unavailable", issues);
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken cancellation) {
        var text = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
            if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }
}
