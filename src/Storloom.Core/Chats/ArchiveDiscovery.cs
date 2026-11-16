namespace Storloom.Core.Chats;

public sealed record ArchiveDiscoveryIssue(string Path, string Reason);
public sealed record ArchiveDiscoveryResult(ArchiveReference[] Archives, ArchiveDiscoveryIssue[] Issues, bool Complete);

// Discovery only reads markers. It never opens message packs, unlocks archives or follows links.
public static class ArchiveDiscovery {
    public static ArchiveDiscoveryResult Scan(string directory, CancellationToken cancellation = default, int maximumDirectories = 20000, int maximumDepth = 8) {
        if (maximumDirectories < 1 || maximumDepth < 0) throw new ArgumentOutOfRangeException(nameof(maximumDirectories));
        directory = PathPolicy.Normalize(directory); PathPolicy.AssertNoLinks(directory);
        var found = new Dictionary<string, ArchiveReference>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<ArchiveDiscoveryIssue>(); var pending = new Stack<(string Path, int Depth)>(); pending.Push((directory, 0));
        int visited = 0; bool complete = true;
        while (pending.TryPop(out var item)) {
            cancellation.ThrowIfCancellationRequested();
            if (++visited > maximumDirectories) { issues.Add(new(item.Path, "limit")); complete = false; break; }
            try {
                PathPolicy.AssertNoLinks(item.Path);
                bool candidate = File.Exists(Path.Combine(item.Path, ArchiveMetadata.FileName)) || File.Exists(Path.Combine(item.Path, ArchiveMetadata.LegacyFileName)) ||
                    File.Exists(Path.Combine(item.Path, "archive.json")) || File.Exists(Path.Combine(item.Path, ArchiveStore.DataDirectory, "archive.json")) ||
                    File.Exists(Path.Combine(item.Path, "messages.html")) || File.Exists(Path.Combine(item.Path, "vault.storloom.json"));
                if (candidate) {
                    var reference = ArchiveMetadata.Inspect(item.Path);
                    if (File.Exists(Path.Combine(reference.Directory, ".reading-update-pending.json")) || File.Exists(Path.Combine(reference.Directory, ".storloom-export.json")) ||
                        File.Exists(Path.Combine(reference.Directory, "export-progress.json")) && !JsonFiles.Read<ChatExportProgress>(Path.Combine(reference.Directory, "export-progress.json")).Finished) {
                        issues.Add(new(item.Path, "incomplete")); continue;
                    }
                    if (reference.Metadata.Kind == "reading" && (reference.Metadata.DataPath == "." && !ArchiveStore.Exists(reference.Directory) || File.Exists(Path.Combine(reference.DataDirectory, "HEAD")))) found.TryAdd(reference.Directory, reference);
                    else issues.Add(new(item.Path, reference.Metadata.Kind == "native" ? "native" : "incomplete"));
                    continue;
                }
                if (item.Depth >= maximumDepth) { if (Directory.EnumerateDirectories(item.Path).Any()) { issues.Add(new(item.Path, "depth")); complete = false; } continue; }
                foreach (string child in Directory.EnumerateDirectories(item.Path)) { cancellation.ThrowIfCancellationRequested(); pending.Push((child, item.Depth + 1)); }
            } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException) {
                issues.Add(new(item.Path, "unavailable"));
            }
        }
        return new(found.Values.OrderBy(reference => reference.Directory, StringComparer.OrdinalIgnoreCase).ToArray(), issues.ToArray(), complete);
    }
}
