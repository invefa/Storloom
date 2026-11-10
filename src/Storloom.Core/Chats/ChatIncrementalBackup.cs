using System.Security.Cryptography;

namespace Storloom.Core.Chats;

public sealed record ChatBackupUpdateResult(TransferResult Backup, int ReusedFiles, long ReusedBytes);

/// <summary>New generation, sharing unchanged resources by manifest reference, never by writable links.</summary>
public static class ChatIncrementalBackup {
    public static string Resolve(string root, TransferReceipt receipt) {
        string storage = receipt.StorageRoot == null ? root : PathPolicy.Normalize(receipt.StorageRoot);
        if (storage.Length <= 3) throw new InvalidDataException("Invalid snapshot storage directory.");
        string path = TransferService.Child(storage, receipt.RelativePath);
        PathPolicy.AssertNoLinks(path);
        if (NativeFile.ReadStamp(path).Links != 1) throw new IOException("Shared backup files are not supported.");
        return path;
    }

    public static ChatBackupUpdateResult Create(string source, string destination, string previousManifest,
        string journalDirectory, string[]? processes = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default) {
        var service = new TransferService();
        var plan = service.Plan(source, destination, processes: processes, cancellation: cancellation);
        PathPolicy.AssertNoLinks(previousManifest);
        var previous = JsonFiles.Read<TransferJournal>(previousManifest);
        if (previous.Version != 1 || previous.Plan.Mode != TransferMode.Copy ||
            previous.State is not ("Completed" or "Verified" or "CancelledAfterVerification") ||
            !PathPolicy.Normalize(previous.Plan.Source).Equals(plan.Source, StringComparison.OrdinalIgnoreCase) ||
            previous.Receipts.Count != previous.Plan.Files.Count)
            throw new InvalidDataException("The previous backup must be a verified copy of this account.");
        var oldFiles = previous.Plan.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var oldReceipts = previous.Receipts.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var inherited = new Dictionary<string, TransferReceipt>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in plan.Files) {
            cancellation.ThrowIfCancellationRequested();
            // Keep databases/WAL physically together in each generation for SQLite and client compatibility.
            string name = Path.GetFileName(file.RelativePath);
            if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)) continue;
            if (!oldFiles.TryGetValue(file.RelativePath, out var old) || old.Stamp != file.Stamp ||
                !oldReceipts.TryGetValue(file.RelativePath, out var receipt) || receipt.Bytes != file.Stamp.Bytes) continue;
            string physical = Resolve(previous.Plan.Destination, receipt);
            if (new FileInfo(physical).Length != receipt.Bytes) throw new InvalidDataException("Previous backup resource changed.");
            // Store the root, not a dependency chain: every reference resolves to a real physical file.
            inherited[file.RelativePath] = receipt with { StorageRoot = receipt.StorageRoot ?? previous.Plan.Destination };
        }
        long copyBytes = plan.Files.Where(f => !inherited.ContainsKey(f.RelativePath)).Sum(f => f.Stamp.Bytes);
        if (new DriveInfo(Path.GetPathRoot(plan.Destination)!).AvailableFreeSpace < copyBytes + 1024 * 1024)
            throw new IOException("Not enough space for this incremental generation.");
        string staging = plan.Destination + ".storloom-" + plan.Id.ToString("N") + ".partial";
        string manifest = plan.Destination + ".storloom-" + plan.Id.ToString("N") + ".json";
        if (Directory.Exists(staging) || File.Exists(manifest)) throw new IOException("Snapshot staging already exists.");
        Directory.CreateDirectory(journalDirectory);
        string journal = Path.Combine(journalDirectory, $"transfer-{plan.Id:N}.json");
        var receipts = new List<TransferReceipt>();
        void Save(string state) => JsonFiles.Write(journal, new TransferJournal { Plan = plan, State = state, Receipts = receipts.ToList() });
        void Closed() {
            if (CleanupEngine.RunningProcesses(new CleanupRule { Processes = plan.Processes }).Length > 0)
                throw new IOException("Close the chat client before updating its backup.");
        }
        Closed(); Save("CreatingIncremental"); Directory.CreateDirectory(staging);
        long done = 0;
        try {
            foreach (var file in plan.Files) {
                cancellation.ThrowIfCancellationRequested(); Closed();
                string inputPath = TransferService.Child(plan.Source, file.RelativePath); PathPolicy.AssertNoLinks(inputPath);
                if (NativeFile.ReadStamp(inputPath) != file.Stamp) throw new IOException("Account changed during backup.");
                if (inherited.TryGetValue(file.RelativePath, out var receipt)) { receipts.Add(receipt); continue; }
                string target = TransferService.Child(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); PathPolicy.AssertNoLinks(Path.GetDirectoryName(target)!);
                using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    byte[] buffer = new byte[1024 * 1024]; int count;
                    while ((count = input.Read(buffer)) > 0) {
                        cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); done += count;
                        progress?.Report(new("transfer", receipts.Count, done, checked(copyBytes * 2), plan.Files.Count, file.RelativePath));
                    }
                    output.Flush(true);
                }
                if (NativeFile.ReadStamp(inputPath) != file.Stamp) throw new IOException("Account changed during copying.");
                string digest = Convert.ToHexString(hash.GetHashAndReset());
                using (var copied = File.OpenRead(target))
                    if (Convert.ToHexString(TransferService.Hash(copied, cancellation, bytes => { done += bytes; progress?.Report(new("transfer", receipts.Count, done, checked(copyBytes * 2), plan.Files.Count, file.RelativePath)); })) != digest)
                        throw new IOException("Incremental copy verification failed.");
                File.SetLastWriteTimeUtc(target, new DateTime(file.Stamp.WriteUtcTicks, DateTimeKind.Utc));
                receipts.Add(new(file.RelativePath, file.Stamp.Bytes, digest));
            }
            // Check even reused source files again before publishing this consistent generation.
            Closed();
            foreach (var file in plan.Files) {
                cancellation.ThrowIfCancellationRequested(); string path = TransferService.Child(plan.Source, file.RelativePath); PathPolicy.AssertNoLinks(path);
                if (NativeFile.ReadStamp(path) != file.Stamp) throw new IOException("Account changed before publishing.");
            }
            var published = new TransferJournal { Plan = plan, State = "Completed", Receipts = receipts };
            JsonFiles.Write(staging + ".manifest.json", published);
            cancellation.ThrowIfCancellationRequested(); PathPolicy.AssertNoLinks(Path.GetDirectoryName(plan.Destination)!);
            Directory.Move(staging, plan.Destination);
            File.Move(staging + ".manifest.json", manifest);
            Save("Completed");
            return new(new(plan.Destination, receipts.Count, 0, copyBytes, manifest, false, []), inherited.Count, inherited.Values.Sum(r => r.Bytes));
        } catch { Save("FailedSourcesRetained"); throw; }
    }
}
