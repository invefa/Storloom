namespace Storloom.Core.Chats;

public sealed record ArchiveDescriptor(int Version, string Id, string Kind, string DataPath) {
    public ChatPlatform? Platform { get; init; }
    public string? Account { get; init; }
    public string? Name { get; init; }
    public int ExportProtocolVersion { get; init; } = 1;
    public int? StorageFormatVersion { get; init; }
    public string? ExporterVersion { get; init; }
    public ArchiveOrigin? Origin { get; init; }
    public ArchiveHistoryEntry[] History { get; init; } = [];
}
public sealed record ArchiveOrigin(string Kind, string? Location = null, string? ArchiveId = null, string? ProjectId = null,
    ChatPlatform? Platform = null, string? Account = null);
public sealed record ArchiveHistoryEntry(string Id, string Action, DateTimeOffset RecordedAt, ArchiveOrigin? Source = null,
    string? ExporterVersion = null, bool Recovered = false);
public sealed record ArchiveReference(string Directory, string DataDirectory, ArchiveDescriptor Metadata, bool HasMetadata);

// Paths in provenance are descriptive only: never use them as restore or update instructions.
public static class ArchiveMetadata {
    public const string FileName = "archive.metadata";
    public const string LegacyFileName = "archive.storloom.json";
    public const int CurrentExportProtocolVersion = 1;
    public const int MaximumMetadataBytes = 4 * 1024 * 1024;
    public static bool IsMetadataFile(string name) => name is FileName or LegacyFileName;
    private static string? MetadataPath(string directory) => new[] { FileName, LegacyFileName }.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
    public static ArchiveReference Inspect(string path) {
        path = PathPolicy.Normalize(path); PathPolicy.AssertNoLinks(path);
        bool file = File.Exists(path); string directory = file ? Path.GetDirectoryName(path)! : path;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (Path.GetFileName(directory).Equals(ArchiveStore.DataDirectory, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(directory, "archive.json"))) directory = Path.GetDirectoryName(directory)!;
        string descriptor = file && IsMetadataFile(Path.GetFileName(path)) ? path : MetadataPath(directory) ?? Path.Combine(directory, FileName);
        ArchiveDescriptor? metadata = null;
        if (File.Exists(descriptor)) {
            PathPolicy.AssertNoLinks(descriptor);
            if (new FileInfo(descriptor).Length > MaximumMetadataBytes) throw new InvalidDataException("Archive metadata is too large.");
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
            actual = new(2, id, kind, Path.GetRelativePath(directory, data).Replace('\\', '/')) { StorageFormatVersion = version };
        } else {
            if (!File.Exists(Path.Combine(directory, "messages.html")) && !File.Exists(Path.Combine(directory, "vault.storloom.json")))
                throw new InvalidDataException("Select an archive metadata file or a supported reading entry.");
            actual = new(2, metadata?.Id ?? Guid.NewGuid().ToString("N"), "reading", "."); data = directory;
        }
        if (metadata != null && (metadata.Id != actual.Id || metadata.Kind != actual.Kind || metadata.DataPath != actual.DataPath))
            throw new InvalidDataException("Archive metadata does not match the selected archive.");
        if (metadata?.StorageFormatVersion is {} storage && storage != actual.StorageFormatVersion) throw new InvalidDataException("Archive storage version does not match its metadata.");
        return new(directory, data, metadata == null ? actual : metadata with { StorageFormatVersion = actual.StorageFormatVersion }, metadata != null);
    }
    public static string Write(string path, ChatPlatform? platform = null, string? account = null, string? name = null, bool replaceIdentity = false) {
        var reference = Inspect(path); var metadata = reference.Metadata with { Version = 2,
            Platform = replaceIdentity ? platform : platform ?? reference.Metadata.Platform, Account = replaceIdentity ? account : account ?? reference.Metadata.Account, Name = name ?? reference.Metadata.Name };
        return Save(reference, metadata);
    }
    private static string Save(ArchiveReference reference, ArchiveDescriptor metadata) {
        Validate(metadata); string target = Path.Combine(reference.Directory, FileName);
        PathPolicy.AssertNoLinks(reference.Directory);
        if (File.Exists(target)) {
            PathPolicy.AssertNoLinks(target); if (NativeFile.ReadStamp(target).Links != 1) throw new IOException("Shared archive metadata cannot be replaced.");
            if (Inspect(target).Metadata.Id != reference.Metadata.Id) throw new InvalidDataException("Another metadata identity already exists at the archive entry.");
        }
        string legacy = Path.Combine(reference.Directory, LegacyFileName); FileStamp? legacyStamp = null;
        if (File.Exists(legacy)) { _ = Inspect(legacy); legacyStamp = NativeFile.ReadStamp(legacy); if (legacyStamp.Links != 1) throw new IOException("Shared legacy archive metadata cannot be replaced."); }
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(metadata, JsonFiles.Options).Length > MaximumMetadataBytes) throw new InvalidDataException("Archive history is too large.");
        JsonFiles.Write(target, metadata); _ = Inspect(target);
        if (legacyStamp != null) { if (NativeFile.ReadStamp(legacy) != legacyStamp) throw new IOException("Legacy metadata changed during migration."); File.Delete(legacy); }
        return target;
    }
    public static string? Existing(string path) {
        var reference = Inspect(path); return MetadataPath(reference.Directory);
    }
    public static string RecordExport(string path, ChatArchiveProject project, string action, string? sourceLocation = null, bool recovered = false) {
        Write(path, project.Platform, project.Account); var reference = Inspect(path);
        var origin = new ArchiveOrigin("client", project.Source, ProjectId: project.Id, Platform: project.Platform, Account: project.Account);
        var source = sourceLocation == null || sourceLocation == project.Source ? origin : new ArchiveOrigin("native-backup", sourceLocation, ProjectId: project.Id, Platform: project.Platform, Account: project.Account);
        string eventId = JsonFiles.Hash(new { action, reference.Metadata.Id, Revision = Revision(reference) });
        string? exporter = recovered ? null : typeof(ArchiveMetadata).Assembly.GetName().Version?.ToString(3);
        var entry = new ArchiveHistoryEntry(eventId, action, DateTimeOffset.UtcNow, source, exporter, recovered);
        var metadata = reference.Metadata with { Version = 2, ExporterVersion = reference.Metadata.ExporterVersion ?? exporter, Origin = reference.Metadata.Origin ?? origin,
            History = Merge(reference.Metadata.History, [entry]) };
        return Save(reference, metadata);
    }
    internal static void CopyIdentity(string source, string destination, string action = "copy") {
        string home = Path.GetFileName(source).Equals(ArchiveStore.DataDirectory, StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(source)! : source;
        var metadata = MetadataPath(home) != null ? Inspect(source).Metadata : null;
        Write(destination, metadata?.Platform, metadata?.Account, metadata?.Name);
        var reference = Inspect(destination);
        var origin = new ArchiveOrigin(metadata?.Kind == "native" ? "native-backup" : "reading", PathPolicy.Normalize(home), metadata?.Id);
        string revision = metadata == null ? "" : Revision(Inspect(source));
        string eventId = JsonFiles.Hash(new { action, reference.Metadata.Id, Source = metadata?.Id ?? origin.Location, Revision = revision });
        string? exporter = typeof(ArchiveMetadata).Assembly.GetName().Version?.ToString(3);
        Save(reference, reference.Metadata with { Origin = metadata?.Origin ?? reference.Metadata.Origin ?? origin, ExporterVersion = exporter,
            History = Merge(Merge(metadata?.History ?? [], reference.Metadata.History), [new(eventId, action, DateTimeOffset.UtcNow, origin, exporter)]) });
    }
    private static string Revision(ArchiveReference reference) {
        string head = Path.Combine(reference.DataDirectory, "HEAD"), transcript = Path.Combine(reference.Directory, "messages.jsonl");
        if (File.Exists(head)) { PathPolicy.AssertNoLinks(head); if (new FileInfo(head).Length > 4096) throw new InvalidDataException("Invalid archive head."); return File.ReadAllText(head); }
        if (File.Exists(transcript)) { PathPolicy.AssertNoLinks(transcript); return JsonFiles.Hash(NativeFile.ReadStamp(transcript)); }
        return "";
    }
    private static ArchiveHistoryEntry[] Merge(ArchiveHistoryEntry[] left, ArchiveHistoryEntry[] right) => left.Concat(right).DistinctBy(entry => entry.Id, StringComparer.Ordinal).OrderBy(entry => entry.RecordedAt).ToArray();
    private static void Validate(ArchiveDescriptor value) {
        if (value.Version is not (1 or 2) || value.ExportProtocolVersion != CurrentExportProtocolVersion) throw new InvalidDataException("Unsupported archive metadata or export protocol version.");
        if (!Guid.TryParseExact(value.Id, "N", out _) || value.Kind is not ("reading" or "native") ||
            value.DataPath is not ("." or "archive-data") || value.Platform.HasValue && !Enum.IsDefined(value.Platform.Value) ||
            value.Account is { } account && (string.IsNullOrWhiteSpace(account) || account.Length > 128 || account.Any(char.IsControl)) ||
            value.Name is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) ||
            value.Account != null && value.Platform == null || value.StorageFormatVersion is not (null or 2 or 3) || !Text(value.ExporterVersion, 64) ||
            value.History == null || value.History.Length > 4096) throw new InvalidDataException("Invalid portable archive metadata.");
        ValidateOrigin(value.Origin);
        foreach (var entry in value.History) {
            if (entry == null || !Text(entry.Id, 128) || string.IsNullOrWhiteSpace(entry.Id) ||
                entry.Action is not ("reading-export" or "native-backup" or "recovered" or "copy" or "import" or "export" or "repack" or "decrypt") ||
                entry.RecordedAt == default || !Text(entry.ExporterVersion, 64)) throw new InvalidDataException("Invalid archive history.");
            ValidateOrigin(entry.Source);
        }
    }
    private static bool Text(string? value, int maximum) => value == null || value.Length is > 0 && value.Length <= maximum && !value.Any(char.IsControl);
    private static void ValidateOrigin(ArchiveOrigin? origin) {
        if (origin == null) return;
        if (origin.Kind is not ("client" or "reading" or "native-backup") || !Text(origin.Location, 32767) || !Text(origin.ArchiveId, 128) ||
            !Text(origin.ProjectId, 128) || !Text(origin.Account, 128) || origin.Platform.HasValue && !Enum.IsDefined(origin.Platform.Value) ||
            origin.Account != null && origin.Platform == null) throw new InvalidDataException("Invalid archive origin.");
    }
}
