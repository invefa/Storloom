using System.Security.Cryptography;

namespace Storloom.Core.Chats;

/// <summary>Materialize a verified, self-contained client account into a new directory. Never merge live data.</summary>
public static class ChatRestore {
    public static string Restore(string manifestPath, string destination, ChatPlatform platform,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default) {
        PathPolicy.AssertNoLinks(manifestPath);
        var journal = JsonFiles.Read<TransferJournal>(manifestPath);
        if (journal.Version != 1 || journal.Plan.Mode != TransferMode.Copy || journal.State is not ("Completed" or "Verified" or "CancelledAfterVerification") ||
            journal.Receipts.Count != journal.Plan.Files.Count) throw new InvalidDataException("A completed raw backup is required.");
        destination = PathPolicy.Normalize(destination);
        if (destination.Length <= 3 || Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Restore into a new account directory.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expected = journal.Plan.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var roots = journal.Receipts.Select(r => r.StorageRoot ?? journal.Plan.Destination).Append(journal.Plan.Destination).Append(journal.Plan.Source).Select(PathPolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string root in roots) if (destination.Equals(root, StringComparison.OrdinalIgnoreCase) || PathPolicy.Under(destination, root) || PathPolicy.Under(root, destination)) throw new IOException("Restore and backup/source must be separate.");
        foreach (var receipt in journal.Receipts) {
            if (!names.Add(receipt.RelativePath) || !expected.TryGetValue(receipt.RelativePath, out var file) || file.Stamp.Bytes != receipt.Bytes || receipt.Sha256.Length != 64)
                throw new InvalidDataException("Invalid backup receipt.");
            _ = TransferService.Child(destination, receipt.RelativePath);
        }
        foreach (string relative in journal.Plan.EmptyDirectories) if (relative != ".") _ = TransferService.Child(destination, relative);
        if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < checked(journal.Receipts.Sum(r => r.Bytes) + 1024 * 1024)) throw new IOException("Insufficient space for restored client data.");
        string parent = Path.GetDirectoryName(destination)!; PathPolicy.AssertNoLinks(parent);
        string staging = destination + ".restore-" + Guid.NewGuid().ToString("N") + ".partial";
        Directory.CreateDirectory(staging);
        long total = checked(journal.Receipts.Sum(r => r.Bytes) * 2), bytes = 0; int files = 0;
        // A failed restore leaves a clearly marked partial directory for inspection. Source is untouched.
        foreach (var receipt in journal.Receipts) {
            OperationPause.Check(cancellation);
            string source = ChatIncrementalBackup.Resolve(journal.Plan.Destination, receipt);
            string target = TransferService.Child(staging, receipt.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); PathPolicy.AssertNoLinks(Path.GetDirectoryName(target)!);
            using (var input = File.OpenRead(source)) using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                if (input.Length != receipt.Bytes) throw new InvalidDataException("Backup resource length changed.");
                byte[] buffer = new byte[1024 * 1024]; int count;
                while ((count = input.Read(buffer)) > 0) { OperationPause.Check(cancellation); output.Write(buffer, 0, count); bytes += count; progress?.Report(new("chat-restore", files, bytes, total, journal.Receipts.Count, receipt.RelativePath)); }
                output.Flush(true);
            }
            using (var written = File.OpenRead(target)) if (!Convert.ToHexString(TransferService.Hash(written, cancellation, count => { bytes += count; progress?.Report(new("chat-restore", files, bytes, total, journal.Receipts.Count, receipt.RelativePath)); })).Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup resource verification failed.");
            files++;
        }
        foreach (string relative in journal.Plan.EmptyDirectories) if (relative != ".") Directory.CreateDirectory(TransferService.Child(staging, relative));
        OperationPause.Check(cancellation);
        PathPolicy.AssertNoLinks(parent); DirectoryPublication.Move(staging, destination,cancellation);
        progress?.Report(new("chat-restore", files, bytes, total, files)); return destination;
    }
}
