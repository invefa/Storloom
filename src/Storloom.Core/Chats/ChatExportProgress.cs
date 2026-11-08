using System.Security.Cryptography;

namespace Storloom.Core.Chats;

public sealed record ExportFileProgress(string Path, long Bytes, string Sha256);
internal sealed record ParsedDatabaseProgress(long Messages, ExportFileProgress File);
internal sealed record ChatExportProgress {
    public int Version { get; init; } = 1;
    public string Source { get; init; } = "";
    public ChatPlatform Platform { get; init; }
    public string Identity { get; init; } = "";
    public string Work { get; init; } = "";
    public Dictionary<int, ParsedDatabaseProgress> Databases { get; init; } = [];
    public bool Finished { get; set; }
}

internal static class ExportProgressFiles {
    public static ExportFileProgress Proof(string root, string path) {
        PathPolicy.AssertNoLinks(path);
        if (NativeFile.ReadStamp(path).Links != 1) throw new IOException("Shared export checkpoint files are not writable.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        string digest = Convert.ToHexString(SHA256.HashData(input)); input.Flush(true);
        return new(System.IO.Path.GetRelativePath(root, path), input.Length, digest);
    }
    public static void Validate(string root, ExportFileProgress proof, bool allowTail, CancellationToken cancellation) {
        string path = TransferService.Child(root, proof.Path); PathPolicy.AssertNoLinks(path);
        if (NativeFile.ReadStamp(path).Links != 1) throw new IOException("Shared export checkpoint files cannot be resumed.");
        using var input = File.OpenRead(path);
        if (proof.Bytes < 0 || input.Length < proof.Bytes || !allowTail && input.Length != proof.Bytes)
            throw new InvalidDataException("Export checkpoint file size mismatch.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024]; long remaining = proof.Bytes;
        while (remaining > 0) { cancellation.ThrowIfCancellationRequested(); int count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (count == 0) throw new EndOfStreamException(); hash.AppendData(buffer, 0, count); remaining -= count; }
        if (Convert.ToHexString(hash.GetHashAndReset()) != proof.Sha256)
            throw new InvalidDataException("Export checkpoint file authentication failed; resume refused.");
    }
}

// Hashes append-only committed prefixes without rereading growing files at each checkpoint.
internal sealed class ExportAppendFile : IDisposable {
    public string Path { get; }
    public long Bytes { get; private set; }
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public ExportAppendFile(string path, ExportFileProgress? previous = null) {
        Path = path;
        if (previous == null) return;
        if (NativeFile.ReadStamp(path).Links != 1) throw new IOException("Shared export checkpoint files cannot be truncated.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        byte[] buffer = new byte[128 * 1024]; long remaining = previous.Bytes;
        while (remaining > 0) { int count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (count == 0) throw new EndOfStreamException(); hash.AppendData(buffer, 0, count); remaining -= count; }
        input.SetLength(previous.Bytes); Bytes = previous.Bytes;
    }
    public void Append(byte[] bytes) {
        PathPolicy.AssertNoLinks(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path)) {
            PathPolicy.AssertNoLinks(Path);
            if (NativeFile.ReadStamp(Path).Links != 1) throw new IOException("Shared export spool files are not writable.");
        }
        using var output = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 128 * 1024);
        if (output.Length != Bytes) throw new InvalidDataException("Export spool changed while writing.");
        output.Write(bytes); output.Flush(true); hash.AppendData(bytes); Bytes += bytes.Length;
    }
    public ExportFileProgress Proof(string root) => new(System.IO.Path.GetRelativePath(root, Path), Bytes, Convert.ToHexString(hash.GetCurrentHash()));
    public void Dispose() => hash.Dispose();
}
