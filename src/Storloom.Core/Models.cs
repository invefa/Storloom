using System.Text.Json;
using System.Text.Json.Serialization;

namespace Storloom.Core;

public enum RiskLevel { Low, Review }
public enum CleanupAction { Delete, Copy, Move }

public sealed record CleanupRule {
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public Dictionary<string, string> Names { get; init; } = [];
    public Dictionary<string, string> Descriptions { get; init; } = [];
    public CleanupAction Action { get; init; }
    public string? DestinationRoot { get; init; }
    public string[] Roots { get; init; } = [];
    public string[] Include { get; init; } = ["**"];
    public string[] Exclude { get; init; } = [];
    public string[] Processes { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public bool Recursive { get; init; } = true;
    public RiskLevel Risk { get; init; } = RiskLevel.Review;
    public double MinimumAgeHours { get; init; } = 168;
    public long MinimumBytes { get; init; }
    public long? MaximumBytes { get; init; }
}

public sealed record RuleCatalog {
    [JsonPropertyName("$schema")] public string? Schema { get; init; }
    public int Version { get; init; } = 1;
    public List<CleanupRule> Rules { get; init; } = [];
}

public sealed record CleanerSettings {
    public string[] AllowedDrives { get; init; } = DriveInfo.GetDrives()
        .Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.Name).ToArray();
    public string? BackupRoot { get; init; }
    public string[] ProtectedRoots { get; init; } = [];
    public int PlanLifetimeHours { get; init; } = 24;
    public int MaximumCandidates { get; init; } = 500_000;
}

public sealed record FileStamp(
    long Bytes, long CreationUtcTicks, long WriteUtcTicks,
    uint Volume, ulong FileId, uint Links);
public sealed record Candidate(string RuleId, string Root, string Path, FileStamp Stamp);
public sealed record ScanIssue(string RuleId, string Path, string Reason);
public sealed record ScanProgress(string RuleId, int Files, long Bytes);
public sealed record CleanupPlan {
    public int Version { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Computer { get; init; } = Environment.MachineName;
    public string UserProfile { get; init; } = Environment.GetFolderPath(
        Environment.SpecialFolder.UserProfile);
    public string CatalogHash { get; init; } = "";
    public string SettingsHash { get; init; } = "";
    public List<Candidate> Files { get; init; } = [];
    public List<ScanIssue> Issues { get; init; } = [];
}

public sealed record ApplyResult(int Deleted, int Skipped, long LogicalBytes,
    long FreeSpaceChange, string Journal, bool Cancelled);

public static class JsonFiles {
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ??
        throw new InvalidDataException($"Empty JSON document: {path}");

    public static void Write<T>(string path, T value) {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        } finally {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string Hash<T>(T value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Options)));
}
