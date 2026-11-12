namespace Storloom.Core;

public sealed record StorageEntry(string Path, long Bytes, long Files, int Depth);
public sealed record StorageScan(string Root, DateTimeOffset CreatedAt, List<StorageEntry> Directories,
    List<StorageEntry> LargeFiles, List<ScanIssue> Issues, long Files, long Bytes, bool Cancelled);

public sealed class StorageScanner {
    /// <summary>Count metadata only without retaining directory breakdowns or large-file candidates.</summary>
    public StorageScan Measure(string root, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default) => Scan(root, progress, cancellation, includeDetails: false);

    public StorageScan Scan(string root, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default, bool includeDetails = true) {
        root = PathPolicy.Normalize(root);
        PathPolicy.AssertNoLinks(root);
        progress?.Report(new("storage", 0, 0, Detail: root));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var directories = new Dictionary<string, StorageEntry>(StringComparer.OrdinalIgnoreCase);
        var largest = new PriorityQueue<StorageEntry, long>();
        var issues = new List<ScanIssue>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        long files = 0, bytes = 0;
        var reporting = System.Diagnostics.Stopwatch.StartNew();
        while (pending.TryPop(out var next) && !cancellation.IsCancellationRequested) {
            long ownBytes = 0, ownFiles = 0;
            try {
                OperationPause.Check(cancellation);
                PathPolicy.AssertNoLinks(next.Path);
                foreach (var item in new DirectoryInfo(next.Path).EnumerateFileSystemInfos()) {
                    if (cancellation.IsCancellationRequested) break;
                    OperationPause.Check(cancellation);
                    try {
                        if ((item.Attributes & FileAttributes.ReparsePoint) != 0) {
                            if (issues.Count < 5000) issues.Add(new("storage", item.FullName, "Reparse point skipped."));
                        } else if (item is DirectoryInfo) {
                            if (next.Depth < 128) pending.Push((item.FullName, next.Depth + 1));
                            else if (issues.Count < 5000) issues.Add(new("storage", item.FullName, "Depth limit reached."));
                        } else if (item is FileInfo file) {
                            ownBytes += file.Length;
                            ownFiles++;
                            if (includeDetails) {
                                var entry = new StorageEntry(file.FullName, file.Length, 1, next.Depth + 1);
                                largest.Enqueue(entry, file.Length);
                                if (largest.Count > 100) largest.Dequeue();
                            }
                        }
                    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        if (issues.Count < 5000) issues.Add(new("storage", item.FullName, ex.Message));
                    }
                    if (reporting.ElapsedMilliseconds >= 250) {
                        progress?.Report(new("storage", (int)Math.Min(files + ownFiles, int.MaxValue), bytes + ownBytes, Detail: next.Path)); reporting.Restart();
                    }
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                if (issues.Count < 5000) issues.Add(new("storage", next.Path, ex.Message));
            } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                // Retain metadata already counted in this directory for an honest partial result.
            }
            if (includeDetails) directories[next.Path] = new(next.Path, ownBytes, ownFiles, next.Depth);
            files += ownFiles;
            bytes += ownBytes;
        }
        progress?.Report(new("storage", (int)Math.Min(files, int.MaxValue), bytes, Detail: root));
        foreach (string path in directories.Values.OrderByDescending(d => d.Depth).Select(d => d.Path).ToArray()) {
            var entry = directories[path];
            string? parent = Path.GetDirectoryName(entry.Path);
            if (parent != null && directories.TryGetValue(parent, out var ancestor))
                directories[parent] = ancestor with { Bytes = ancestor.Bytes + entry.Bytes,
                    Files = ancestor.Files + entry.Files };
        }
        return new(root, DateTimeOffset.UtcNow, directories.Values.OrderByDescending(d => d.Bytes).ToList(),
            largest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Bytes).ToList(),
            issues, files, bytes, cancellation.IsCancellationRequested);
    }
}
