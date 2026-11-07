using System.Text.RegularExpressions;

namespace Storloom.Core;

public sealed class RuleResolver {
    private readonly PathPolicy policy;
    private readonly Dictionary<string, string> tokens = new(StringComparer.OrdinalIgnoreCase) {
        ["LocalAppData"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["AppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ["UserProfile"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ["Windows"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["SystemDrive"] = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\')
    };

    public RuleResolver(PathPolicy policy, CleanerSettings? settings = null) {
        this.policy = policy;
        if (!string.IsNullOrWhiteSpace(settings?.BackupRoot)) tokens["BackupRoot"] = settings.BackupRoot;
    }

    public string Expand(string value) => Regex.Replace(value, "%([^%]+)%", match =>
        tokens.TryGetValue(match.Groups[1].Value, out string? replacement) ? replacement :
            throw new InvalidDataException($"Unknown path token: {match.Value}"));

    public IEnumerable<string> Resolve(string pattern, bool cleanupScope = true) {
        pattern = Expand(pattern);
        if (pattern.Contains("**")) throw new InvalidDataException("Root globs cannot contain **.");
        string full = PathPolicy.Normalize(pattern);
        string drive = Path.GetPathRoot(full)!;
        IEnumerable<string> current = [drive];
        foreach (string part in full[drive.Length..].Split('\\')) {
            var next = new List<string>();
            foreach (string parent in current) {
                PathPolicy.AssertNoLinks(parent);
                if (part.IndexOfAny(['*', '?']) >= 0) {
                    next.AddRange(Directory.EnumerateDirectories(parent, part,
                        SearchOption.TopDirectoryOnly));
                } else {
                    string child = Path.Combine(parent, part);
                    if (Directory.Exists(child)) next.Add(child);
                }
            }
            current = next;
        }
        foreach (string root in current) {
            string safeRoot = cleanupScope ? policy.ValidateRoot(root) : PathPolicy.Normalize(root);
            if (safeRoot.Length <= 3) throw new InvalidDataException("A rule root cannot be an entire drive.");
            PathPolicy.AssertNoLinks(safeRoot);
            yield return safeRoot;
        }
    }
}

public sealed class RuleMatcher {
    public CleanupRule Rule { get; }
    private readonly Regex[] includes;
    private readonly Regex[] excludes;

    public RuleMatcher(CleanupRule rule) {
        Rule = rule;
        if (!Regex.IsMatch(rule.Id, "^[a-z][a-z0-9-]{1,63}$") ||
            string.IsNullOrWhiteSpace(rule.Name) || (rule.Action != CleanupAction.Command && rule.Roots.Length == 0) ||
            rule.Include.Length == 0 || !double.IsFinite(rule.MinimumAgeHours) ||
            rule.MinimumAgeHours is < 0 or > 876000 || rule.MinimumBytes < 0 ||
            rule.MaximumBytes < rule.MinimumBytes || !Enum.IsDefined(rule.Risk) ||
            !Enum.IsDefined(rule.Action) || (rule.Action is CleanupAction.Copy or CleanupAction.Move &&
                string.IsNullOrWhiteSpace(rule.DestinationRoot)) ||
            (rule.Action == CleanupAction.Command && (rule.Tool == null || !Enum.IsDefined(rule.Tool.Value) ||
                rule.Roots.Length != 0 || rule.DestinationRoot != null || rule.Risk != RiskLevel.Review)) ||
            (rule.Action != CleanupAction.Command && rule.Tool != null)) {
            throw new InvalidDataException($"Invalid rule: {rule.Id}");
        }
        includes = rule.Include.Select(Glob.Compile).ToArray();
        excludes = rule.Exclude.Select(Glob.Compile).ToArray();
    }

    public bool Matches(string relativePath, FileStamp stamp, DateTimeOffset createdAt) {
        relativePath = relativePath.Replace('\\', '/');
        if (!Rule.Recursive && relativePath.Contains('/')) return false;
        return stamp.Links == 1 && stamp.Bytes >= Rule.MinimumBytes &&
            (Rule.MaximumBytes == null || stamp.Bytes <= Rule.MaximumBytes) &&
            stamp.WriteUtcTicks < createdAt.UtcDateTime.AddHours(-Rule.MinimumAgeHours).Ticks &&
            stamp.CreationUtcTicks < createdAt.UtcDateTime.AddHours(-Rule.MinimumAgeHours).Ticks &&
            includes.Any(r => r.IsMatch(relativePath)) && !excludes.Any(r => r.IsMatch(relativePath));
    }
}
