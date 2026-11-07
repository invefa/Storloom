using Microsoft.Win32;

namespace Storloom.Core.Chats;

public sealed record ChatLocation(ChatPlatform Platform, string Directory, string Account,
    string Database, long DatabaseBytes) {
    public long? AccountBytes { get; init; }
    public long AccountFiles { get; init; }
    public bool SizeComplete { get; init; }
}

public static class ChatDiscovery {
    public static List<ChatLocation> Discover(IEnumerable<string>? additionalRoots = null,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default) {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (additionalRoots == null) {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string root in new[] { Path.Combine(documents, "Tencent Files"),
            Path.Combine(documents, "WeChat Files"), Path.Combine(documents, "xwechat_files"),
            Path.Combine(profile, "xwechat_files"), Path.Combine(profile, "Documents", "xwechat_files") }) roots.Add(root);
        if (OperatingSystem.IsWindows()) {
            foreach (string client in new[] { "WeChat", "Weixin" }) {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\" + client);
                if (key?.GetValue("FileSavePath") is string saved && Path.IsPathFullyQualified(saved)) {
                    roots.Add(saved);
                    roots.Add(Path.Combine(saved, "WeChat Files"));
                    roots.Add(Path.Combine(saved, "xwechat_files"));
                }
            }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            foreach (string name in new[] { "Tencent Files", "WeChat Files", "xwechat_files" })
                roots.Add(Path.Combine(drive.RootDirectory.FullName, name));
        }
        foreach (string root in additionalRoots ?? []) roots.Add(root);
        var results = new List<ChatLocation>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string requestedRoot in roots.Where(Directory.Exists)) {
            cancellation.ThrowIfCancellationRequested();
            try {
                // Resolve only the chosen/default root. Database traversal still skips nested links.
                string root = ResolveRoot(requestedRoot);
                if (!visited.Add(root)) continue;
                progress?.Report(new("chat-discovery", 0, 0, Detail: root));
                foreach (var platform in new[] { ChatPlatform.QQ, ChatPlatform.WeChat })
                    foreach (string database in ChatExporter.FindDatabases(root, platform)) {
                        cancellation.ThrowIfCancellationRequested();
                        if (results.Any(r => r.Database.Equals(database, StringComparison.OrdinalIgnoreCase))) continue;
                        var info = new FileInfo(database);
                        // Preserve the selected account, not just its message DB subdirectory.
                        string accountRoot = root;
                        string relative = Path.GetRelativePath(root, database);
                        string first = relative.Split('\\', '/')[0];
                        if (relative.IndexOfAny(['\\', '/']) >= 0) accountRoot = Path.Combine(root, first);
                        for (DirectoryInfo? current = Directory.GetParent(database); current != null;
                            current = current.Parent) {
                            if (current.Name.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase) ||
                                (current.Name.Length >= 5 && current.Name.All(char.IsAsciiDigit))) {
                                accountRoot = current.FullName; break;
                            }
                            if (new[] { "db_storage", "Msg", "nt_qq" }.Contains(current.Name,
                                StringComparer.OrdinalIgnoreCase) && current.Parent != null &&
                                (current.Parent.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                                PathPolicy.Under(current.Parent.FullName, root))) accountRoot = current.Parent.FullName;
                            if (current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                        }
                        results.Add(new(platform, accountRoot, Path.GetFileName(accountRoot), database, info.Length));
                    }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return results.GroupBy(r => (r.Platform, r.Directory)).Select(group => {
            cancellation.ThrowIfCancellationRequested();
            var first = group.First();
            var size = new StorageScanner().Scan(first.Directory, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return first with { DatabaseBytes = group.Sum(r => r.DatabaseBytes), AccountBytes = size.Bytes,
                AccountFiles = size.Files, SizeComplete = size.Issues.Count == 0 };
        }).ToList();
    }
    private static string ResolveRoot(string root) {
        string normalized = PathPolicy.Normalize(root), resolved = Path.GetPathRoot(normalized)!;
        foreach (string part in normalized[resolved.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries)) {
            var directory = new DirectoryInfo(Path.Combine(resolved, part));
            resolved = (directory.Attributes & FileAttributes.ReparsePoint) == 0 ? directory.FullName :
                PathPolicy.Normalize(directory.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Cannot resolve account discovery root."));
        }
        PathPolicy.AssertNoLinks(resolved); return resolved;
    }
}
