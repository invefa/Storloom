namespace Storloom.Core.Chats;

public sealed record ArchiveDescriptor(int Version, string Id, string Kind, string DataPath) {
    public ChatPlatform? Platform { get; init; }
    public string? Account { get; init; }
    public string? Name { get; init; }
}
public sealed record ArchiveReference(string Directory, string DataDirectory, ArchiveDescriptor Metadata, bool HasMetadata);

// A portable, declarative entry point. It never contains credentials or machine-local paths.
public static class ArchiveMetadata {
    public const string FileName = "archive.storloom.json";
    public static ArchiveReference Inspect(string path) {
        path = PathPolicy.Normalize(path); PathPolicy.AssertNoLinks(path);
        bool file = File.Exists(path); string directory = file ? Path.GetDirectoryName(path)! : path;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (Path.GetFileName(directory).Equals(ArchiveStore.DataDirectory, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(directory, "archive.json"))) directory = Path.GetDirectoryName(directory)!;
        string descriptor = Path.Combine(directory, FileName);
        ArchiveDescriptor? metadata = null;
        if (File.Exists(descriptor)) {
            PathPolicy.AssertNoLinks(descriptor);
            if (new FileInfo(descriptor).Length > 16384) throw new InvalidDataException("Archive metadata is too large.");
            metadata = JsonFiles.Read<ArchiveDescriptor>(descriptor); Validate(metadata);
        }
        string data = ArchiveStore.ResolveRoot(directory); ArchiveDescriptor actual;
        if (File.Exists(Path.Combine(data, "archive.json"))) {
            string marker = Path.Combine(data, "archive.json"); PathPolicy.AssertNoLinks(marker);
            if (new FileInfo(marker).Length > 4096) throw new InvalidDataException("Invalid archive marker.");
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(marker)); var root = json.RootElement;
            int version = root.GetProperty("version").GetInt32(); string id = root.GetProperty("id").GetString()!;
            string kind = root.TryGetProperty("kind", out var value) ? value.GetString()! : "reading";
            if (version is not (2 or 3) || kind is not ("reading" or "native") || !Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid archive marker.");
            actual = new(1, id, kind, Path.GetRelativePath(directory, data).Replace('\\', '/'));
        } else {
            if (!File.Exists(Path.Combine(directory, "messages.html")) && !File.Exists(Path.Combine(directory, "vault.storloom.json")))
                throw new InvalidDataException("Select an archive metadata file or a supported reading entry.");
            actual = new(1, metadata?.Id ?? Guid.NewGuid().ToString("N"), "reading", "."); data = directory;
        }
        if (metadata != null && (metadata.Id != actual.Id || metadata.Kind != actual.Kind || metadata.DataPath != actual.DataPath))
            throw new InvalidDataException("Archive metadata does not match the selected archive.");
        return new(directory, data, metadata ?? actual, metadata != null);
    }
    public static string Write(string path, ChatPlatform? platform = null, string? account = null, string? name = null, bool replaceIdentity = false) {
        var reference = Inspect(path); var metadata = reference.Metadata with {
            Platform = replaceIdentity ? platform : platform ?? reference.Metadata.Platform, Account = replaceIdentity ? account : account ?? reference.Metadata.Account, Name = name ?? reference.Metadata.Name };
        Validate(metadata); string target = Path.Combine(reference.Directory, FileName);
        PathPolicy.AssertNoLinks(reference.Directory);
        if (File.Exists(target)) { PathPolicy.AssertNoLinks(target); if (NativeFile.ReadStamp(target).Links != 1) throw new IOException("Shared archive metadata cannot be replaced."); }
        JsonFiles.Write(target, metadata); return target;
    }
    public static string? Existing(string path) {
        var reference = Inspect(path); return reference.HasMetadata ? Path.Combine(reference.Directory, FileName) : null;
    }
    internal static void CopyIdentity(string source, string destination) {
        string home = Path.GetFileName(source).Equals(ArchiveStore.DataDirectory, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(source)! : source;
        var metadata = File.Exists(Path.Combine(home, FileName)) ? Inspect(source).Metadata : null;
        Write(destination, metadata?.Platform, metadata?.Account, metadata?.Name);
    }
    private static void Validate(ArchiveDescriptor value) {
        if (value.Version != 1 || !Guid.TryParseExact(value.Id, "N", out _) || value.Kind is not ("reading" or "native") ||
            value.DataPath is not ("." or "archive-data") || value.Platform.HasValue && !Enum.IsDefined(value.Platform.Value) ||
            value.Account is { } account && (string.IsNullOrWhiteSpace(account) || account.Length > 128 || account.Any(char.IsControl)) ||
            value.Name is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) ||
            value.Account != null && value.Platform == null) throw new InvalidDataException("Invalid portable archive metadata.");
    }
}
