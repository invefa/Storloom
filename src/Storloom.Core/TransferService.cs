using System.Security.Cryptography;

namespace Storloom.Core;

public enum TransferMode { Copy, Move }
public sealed record TransferFile(string RelativePath, FileStamp Stamp);
public sealed record TransferPlan {
    public int Version { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Computer { get; init; } = Environment.MachineName;
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public TransferMode Mode { get; init; }
    public string[] Processes { get; init; } = [];
    public List<TransferFile> Files { get; init; } = [];
    public List<string> EmptyDirectories { get; init; } = [];
}
public sealed record TransferReceipt(string RelativePath, long Bytes, string Sha256);
public sealed record TransferJournal {
    public int Version { get; init; } = 1;
    public TransferPlan Plan { get; init; } = new();
    public string State { get; init; } = "";
    public List<TransferReceipt> Receipts { get; init; } = [];
    public int RemovedFiles { get; init; }
    public List<ScanIssue> Issues { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ProgressLog { get; init; } = "";
}
public sealed record TransferResult(string Destination, int VerifiedFiles, int RemovedFiles,
    long Bytes, string Manifest, bool Cancelled, List<ScanIssue> Issues);

public sealed class TransferService {
    public TransferPlan Plan(string source, string destination, TransferMode mode = TransferMode.Copy,
        string[]? processes = null, CancellationToken cancellation = default) {
        source = PathPolicy.Normalize(source);
        destination = PathPolicy.Normalize(destination);
        ValidateBoundaries(source, destination, mode);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Destination must be a new directory; existing data is never merged.");
        AssertParents(destination);
        PathPolicy.AssertNoLinks(source);
        var files = new List<TransferFile>();
        var folders = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out string? directory)) {
            cancellation.ThrowIfCancellationRequested();
            PathPolicy.AssertNoLinks(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                folders.Add(Path.GetRelativePath(source, directory));
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                cancellation.ThrowIfCancellationRequested();
                FileAttributes attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Transfer cannot omit a linked entry: {path}");
                if ((attrs & FileAttributes.Directory) != 0) pending.Push(path);
                else {
                    string relative = Path.GetRelativePath(source, path);
                    if (!names.Add(relative)) throw new IOException("Case-insensitive destination collision.");
                    files.Add(new(relative, NativeFile.ReadStamp(path)));
                }
            }
        }
        return new() { Source = source, Destination = destination, Mode = mode,
            Processes = processes ?? [], Files = files, EmptyDirectories = folders };
    }

    public TransferResult Execute(TransferPlan plan, string journalDirectory,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default) {
        if (plan.Version != 1 || plan.Computer != Environment.MachineName ||
            plan.CreatedAt > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromHours(24))
            throw new InvalidDataException("Expired or foreign transfer plan. Plan again.");
        ValidateBoundaries(plan.Source, plan.Destination, plan.Mode);
        if (Directory.Exists(plan.Destination) || File.Exists(plan.Destination))
            throw new IOException("Destination already exists. Choose a new destination.");
        CheckProcesses(plan.Processes);
        AssertParents(plan.Destination);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files) {
            string source = Child(plan.Source, file.RelativePath);
            _ = Child(plan.Destination, file.RelativePath);
            if (!names.Add(file.RelativePath)) throw new InvalidDataException("Duplicate transfer path.");
            PathPolicy.AssertNoLinks(source);
            if (NativeFile.ReadStamp(source) != file.Stamp) throw new IOException($"Source changed: {source}");
        }
        foreach (string directory in plan.EmptyDirectories)
            if (directory != ".") _ = Child(plan.Destination, directory);
        long needed = plan.Files.Sum(f => f.Stamp.Bytes);
        if (new DriveInfo(Path.GetPathRoot(plan.Destination)!).AvailableFreeSpace < needed + 1024 * 1024)
            throw new IOException("Not enough destination space for the verified copy.");
        string staging = plan.Destination + ".storloom-" + plan.Id.ToString("N") + ".partial";
        if (Directory.Exists(staging) || File.Exists(staging))
            throw new IOException("A partial transfer already exists; inspect its manifest before retrying.");
        Directory.CreateDirectory(journalDirectory);
        string manifestPath = Path.Combine(journalDirectory, $"transfer-{plan.Id:N}.json");
        if (File.Exists(manifestPath)) throw new IOException("This transfer already has a journal; plan again.");
        string archiveManifest = plan.Destination + $".storloom-{plan.Id:N}.json";
        if (File.Exists(archiveManifest) || Directory.Exists(archiveManifest))
            throw new IOException("Archive manifest already exists; plan again.");
        var receipts = new List<TransferReceipt>();
        var issues = new List<ScanIssue>();
        int removed = 0;
        long bytes = 0;
        bool published = false;
        string eventsPath = manifestPath + ".jsonl";
        using var eventsStream = new FileStream(eventsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var events = new StreamWriter(eventsStream);
        void Event(string state, object details) {
            events.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { state, details, time = DateTimeOffset.UtcNow }));
            events.Flush(); eventsStream.Flush(true);
        }
        void Save(string state) {
            var record = new TransferJournal { Plan = plan, State = state, Receipts = receipts,
                RemovedFiles = removed, Issues = issues, ProgressLog = eventsPath };
            JsonFiles.Write(manifestPath, record);
            if (published) JsonFiles.Write(archiveManifest, record);
        }
        Save("Planned");
        Directory.CreateDirectory(staging);
        try {
            foreach (var entry in plan.Files) {
                cancellation.ThrowIfCancellationRequested();
                CheckProcesses(plan.Processes);
                string source = Child(plan.Source, entry.RelativePath);
                string target = Child(staging, entry.RelativePath);
                PathPolicy.AssertNoLinks(source);
                AssertParents(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    if (NativeFile.ReadStamp(source) != entry.Stamp) throw new IOException("Source changed before copying.");
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        byte[] buffer = new byte[1024 * 1024];
                        int count;
                        while ((count = input.Read(buffer)) > 0) {
                            cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, count);
                        }
                        output.Flush(true);
                    }
                    input.Position = 0;
                    byte[] sourceHash = Hash(input, cancellation);
                    using var copied = File.OpenRead(target);
                    if (!CryptographicOperations.FixedTimeEquals(sourceHash, Hash(copied, cancellation)) ||
                        input.Length != entry.Stamp.Bytes || NativeFile.ReadStamp(source) != entry.Stamp)
                        throw new IOException("Copy verification failed.");
                    receipts.Add(new(entry.RelativePath, entry.Stamp.Bytes, Convert.ToHexString(sourceHash)));
                }
                File.SetLastWriteTimeUtc(target, new DateTime(entry.Stamp.WriteUtcTicks, DateTimeKind.Utc));
                bytes += entry.Stamp.Bytes;
                Event("Copied", receipts[^1]);
                progress?.Report(new("transfer", receipts.Count, bytes));
            }
            foreach (string directory in plan.EmptyDirectories)
                Directory.CreateDirectory(directory == "." ? staging : Child(staging, directory));
            cancellation.ThrowIfCancellationRequested();
            // Verify the entire target again before accepting it or removing any source.
            foreach (var entry in receipts) {
                cancellation.ThrowIfCancellationRequested();
                using var target = File.OpenRead(Child(staging, entry.RelativePath));
                if (Convert.ToHexString(Hash(target, cancellation)) != entry.Sha256)
                    throw new IOException("Destination changed before publishing.");
            }
            AssertParents(plan.Destination);
            Directory.Move(staging, plan.Destination);
            published = true;
            Save("Verified");
            if (plan.Mode == TransferMode.Move) {
                var expectedHashes = receipts.ToDictionary(r => r.RelativePath, r => r.Sha256,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var entry in plan.Files) {
                    if (cancellation.IsCancellationRequested) break;
                    try {
                        CheckProcesses(plan.Processes);
                        string source = Child(plan.Source, entry.RelativePath);
                        string target = Child(plan.Destination, entry.RelativePath);
                        PathPolicy.AssertNoLinks(source);
                        PathPolicy.AssertNoLinks(target);
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                            FileShare.Read | FileShare.Delete);
                        using var backup = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                        string expected = expectedHashes[entry.RelativePath];
                        if (Convert.ToHexString(Hash(input, cancellation)) != expected ||
                            Convert.ToHexString(Hash(backup, cancellation)) != expected)
                            throw new IOException("Source or destination changed before removal.");
                        Event("RemovingSource", new { entry.RelativePath });
                        NativeFile.DeleteVerified(new("transfer", plan.Source, source, entry.Stamp));
                        removed++;
                        Event("SourceRemoved", new { entry.RelativePath });
                    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        issues.Add(new("transfer", entry.RelativePath, ex.Message));
                        Event("SourceRetained", issues[^1]);
                    }
                }
            }
            Save(cancellation.IsCancellationRequested ? "CancelledAfterVerification" :
                issues.Count > 0 ? "CopiedWithRetainedSources" : "Completed");
        } catch (OperationCanceledException) { Save(published ? "CancelledAfterVerification" : "CancelledBeforeVerification");
        } catch {
            Save(published ? "FailedAfterVerification" : "FailedSourcesRetained");
            throw;
        }
        return new(plan.Destination, receipts.Count, removed, bytes, published ? archiveManifest : manifestPath,
            cancellation.IsCancellationRequested, issues);
    }

    public static string Child(string root, string relative) {
        if (Path.IsPathRooted(relative) || relative.Split('\\', '/').Any(p => p is ".." or "."))
            throw new InvalidDataException("Invalid relative transfer path.");
        string full = PathPolicy.Normalize(Path.Combine(root, relative));
        if (!PathPolicy.Under(full, root)) throw new InvalidDataException("Transfer path escaped its root.");
        return full;
    }

    public static byte[] Hash(Stream input, CancellationToken cancellation = default) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        int count;
        while ((count = input.Read(buffer)) > 0) {
            cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count);
        }
        return hash.GetHashAndReset();
    }

    internal static void ValidateBoundaries(string source, string destination, TransferMode mode) {
        source = PathPolicy.Normalize(source);
        destination = PathPolicy.Normalize(destination);
        if (source.Length <= 3 || source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            PathPolicy.Under(destination, source) || PathPolicy.Under(source, destination))
            throw new InvalidDataException("Source and destination must be separate, non-nested directories.");
        if (mode == TransferMode.Move) {
            string[] protectedRoots = [Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)];
            foreach (string root in protectedRoots.Where(p => p.Length > 0))
                if (source.Equals(root, StringComparison.OrdinalIgnoreCase) || PathPolicy.Under(root, source) ||
                    (root != Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) &&
                    root != Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) &&
                    PathPolicy.Under(source, root))) throw new InvalidDataException("Protected move source.");
            if (source.Split('\\').Any(p => new[] { ".git", ".ssh", ".codex" }
                .Contains(p, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("Protected move source.");
        }
    }

    private static void CheckProcesses(string[] names) {
        if (CleanupEngine.RunningProcesses(new CleanupRule { Processes = names }).Length > 0)
            throw new IOException("Close the source applications before backup or transfer.");
    }

    private static void AssertParents(string path) {
        string? current = path;
        while (current != null && !Directory.Exists(current) && !File.Exists(current))
            current = Path.GetDirectoryName(current);
        if (current != null) PathPolicy.AssertNoLinks(current);
    }
}
