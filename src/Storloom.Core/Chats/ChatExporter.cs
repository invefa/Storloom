using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Storloom.Core.Localization;

namespace Storloom.Core.Chats;

public sealed record ChatMessage(string Platform, string Conversation, string Sender,
    long Timestamp, long Type, string Text, string? RawBase64 = null) {
    public string? ConversationName { get; init; }
    public string? SenderName { get; init; }
    public bool IsSelf { get; init; }
    public ChatMedia[] Media { get; init; } = [];
}
public sealed record ChatExportResult(string Directory, long Messages, int Databases,
    List<ScanIssue> Issues, bool Complete) {
    public int Contacts { get; init; }
    public int MediaFiles { get; init; }
    public int UnresolvedMedia { get; init; }
    public double ElapsedSeconds { get; init; }
    public int Parallelism { get; init; }
    public Dictionary<string, double> PhaseSeconds { get; init; } = [];
    public long VerifiedBackupBytes { get; init; }
}

public sealed class ChatExporter(int maximumParallelism = 2) {
    private readonly int parallelism = maximumParallelism is >= 1 and <= 4 ? maximumParallelism : throw new ArgumentOutOfRangeException(nameof(maximumParallelism));
    public ChatExportResult Export(string source, string destination, ChatPlatform platform,
        byte[]? secret = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default) => ExportCore(source, destination, platform,
            secret, progress, cancellation, true, null);

    public ChatExportResult ExportVerifiedBackup(string manifestPath, string destination,
        ChatPlatform platform, byte[]? secret = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default) {
        var manifest = JsonFiles.Read<TransferJournal>(manifestPath);
        var verificationClock = System.Diagnostics.Stopwatch.StartNew();
        if (manifest.Version != 1 || manifest.State is not ("Verified" or "Completed" or
            "CopiedWithRetainedSources" or "CancelledAfterVerification") ||
            manifest.Receipts.Count != manifest.Plan.Files.Count)
            throw new InvalidDataException("The backup has not completed verification.");
        var expected = manifest.Plan.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var received = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long verifiedBytes = 0, totalBytes = manifest.Receipts.Sum(e => e.Bytes);
        int verifiedFiles = 0;
        foreach (var entry in manifest.Receipts) {
            cancellation.ThrowIfCancellationRequested();
            if (!received.Add(entry.RelativePath) || !expected.TryGetValue(entry.RelativePath, out var planned) ||
                planned.Stamp.Bytes != entry.Bytes)
                throw new InvalidDataException("Backup receipts do not match the planned files.");
            string path = TransferService.Child(manifest.Plan.Destination, entry.RelativePath);
            PathPolicy.AssertNoLinks(path);
            using var input = File.OpenRead(path);
            if (input.Length != entry.Bytes || Convert.ToHexString(TransferService.Hash(input, cancellation, count => { verifiedBytes += count;
                progress?.Report(new("chat-verify", verifiedFiles, verifiedBytes, totalBytes, manifest.Receipts.Count, entry.RelativePath)); })) != entry.Sha256)
                throw new InvalidDataException("The archived copy changed since verification.");
            verifiedFiles++;
        }
        foreach (string database in FindDatabases(manifest.Plan.Destination, platform))
            foreach (string file in new[] { database, database + "-wal", database + "-shm" }.Where(File.Exists))
                if (!received.Contains(Path.GetRelativePath(manifest.Plan.Destination, file)))
                    throw new InvalidDataException("An unverified message database or WAL was added to the archive.");
        return ExportCore(manifest.Plan.Destination, destination, platform, secret, progress, cancellation, false,
            manifest.Receipts.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase), Path.GetFileName(manifest.Plan.Source), verificationClock.Elapsed.TotalSeconds);
    }

    private ChatExportResult ExportCore(string source, string destination, ChatPlatform platform,
        byte[]? secret, IProgress<ScanProgress>? progress, CancellationToken cancellation,
        bool requireClosedClient, Dictionary<string, TransferReceipt>? verified, string? accountName = null, double verificationSeconds = 0) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        source = PathPolicy.Normalize(source); destination = PathPolicy.Normalize(destination);
        if (PathPolicy.Under(destination, source) || PathPolicy.Under(source, destination) ||
            source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Export and source must be separate directories.");
        string[] processNames = platform == ChatPlatform.QQ ? ["QQ"] : ["Weixin", "WeChat"];
        if (requireClosedClient && CleanupEngine.RunningProcesses(new CleanupRule { Processes = processNames }).Length > 0)
            throw new IOException("Close the chat client before exporting a consistent snapshot.");
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Export destination must be new.");
        string[] databases = FindDatabases(source, platform).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (databases.Length == 0) throw new InvalidDataException("No supported message databases found.");
        Directory.CreateDirectory(destination); PathPolicy.AssertNoLinks(destination);
        string work = Path.Combine(destination, ".work-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var issues = new System.Collections.Concurrent.ConcurrentBag<ScanIssue>();
        var contacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int completed = 0; long messages = 0;
        var weights = new long[databases.Length]; var databaseBytes = databases.Select(p => new FileInfo(p).Length).ToArray();
        var decryptWeights = new long[databases.Length];
        long totalDatabaseBytes = databaseBytes.Sum();
        double databaseSeconds = 0, readerStarted = 0;
        ChatArchiveWriter? archive = null;
        bool Allowed(string p) => verified == null || verified.ContainsKey(Path.GetRelativePath(source, p));
        try {
            // Metadata databases are small and read once before workers start.
            foreach (string contact in FindMetadataDatabases(source).Where(Allowed)) {
                cancellation.ThrowIfCancellationRequested(); string plain = Path.Combine(work, Guid.NewGuid().ToString("N") + ".db");
                try {
                    bool shifted = PrepareDatabase(contact, plain, secret, platform, cancellation);
                    using var reader = new SqliteReader(plain, shifted);
                    ReadContacts(reader, contacts, cancellation);
                } catch (Exception ex) when (ExportFailure(ex)) { issues.Add(new("chat-contacts", Path.GetRelativePath(source, contact), ex.Message)); }
                finally { DeletePlain(plain); }
            }
            Parallel.For(0, databases.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellation }, index => {
                string database = databases[index], plain = Path.Combine(work, $"{index}.db"), output = Path.Combine(work, $"{index}.jsonl");
                try {
                    long preparedBytes = 0; for (int i = 0; i < decryptWeights.Length; i++) preparedBytes += Interlocked.Read(ref decryptWeights[i]);
                    progress?.Report(new("chat-decrypt", Volatile.Read(ref completed), preparedBytes, totalDatabaseBytes, Detail: Path.GetFileName(database)));
                    var decryptProgress = new DirectProgress(p => {
                        Interlocked.Exchange(ref decryptWeights[index], p.Bytes);
                        long all = 0; for (int i = 0; i < decryptWeights.Length; i++) all += Interlocked.Read(ref decryptWeights[i]);
                        progress?.Report(new("chat-decrypt", Volatile.Read(ref completed), all, totalDatabaseBytes, Detail: Path.GetFileName(database)));
                    });
                    bool shifted = PrepareDatabase(database, plain, secret, platform, cancellation, decryptProgress);
                    using var reader = new SqliteReader(plain, shifted);
                    if (reader.Query("PRAGMA quick_check", cancellation).Any(r => r[0]?.ToString() != "ok")) throw new InvalidDataException("SQLite integrity check failed.");
                    long totalRows = CountMessages(reader, platform, cancellation), parsedRows = 0;
                    using var writer = new StreamWriter(output, false, new UTF8Encoding(false), 128 * 1024);
                    foreach (var message in ReadMessages(reader, platform, cancellation)) {
                        writer.WriteLine(JsonSerializer.Serialize(message));
                        long count = Interlocked.Increment(ref messages);
                        parsedRows++;
                        if (count % 500 == 0) {
                            Interlocked.Exchange(ref weights[index], (long)(databaseBytes[index] * Math.Min(1, parsedRows / (double)Math.Max(1, totalRows))));
                            long parsedBytes = 0; for (int i = 0; i < weights.Length; i++) parsedBytes += Interlocked.Read(ref weights[i]);
                            progress?.Report(new("chat-export", (int)Math.Min(count, int.MaxValue), parsedBytes,
                                totalDatabaseBytes, Detail: Path.GetFileName(database)));
                        }
                    }
                    Interlocked.Exchange(ref weights[index], databaseBytes[index]);
                    Interlocked.Increment(ref completed);
                } catch (Exception ex) when (ExportFailure(ex)) { issues.Add(new("chat-export", Path.GetRelativePath(source, database), ex.Message)); }
                finally { DeletePlain(plain); }
            });
            databaseSeconds = clock.Elapsed.TotalSeconds; readerStarted = databaseSeconds;
            archive = new ChatArchiveWriter(source, destination, work, contacts, verified, cancellation, accountName ?? Path.GetFileName(source));
            long assembled = 0;
            for (int index = 0; index < databases.Length; index++) {
                string output = Path.Combine(work, $"{index}.jsonl"); if (!File.Exists(output)) continue;
                foreach (string line in File.ReadLines(output)) {
                    cancellation.ThrowIfCancellationRequested();
                    var message = JsonSerializer.Deserialize<ChatMessage>(line) ?? throw new InvalidDataException("Invalid parsed message.");
                    archive.Add(message);
                    if (++assembled % 300 == 0) progress?.Report(new("chat-reader", (int)Math.Min(assembled, int.MaxValue), 0,
                        TotalFiles: (int)Math.Min(messages, int.MaxValue), Detail: message.Conversation));
                }
                File.Delete(output);
            }
            archive.Finish(progress);
        } catch (OperationCanceledException) {
            issues.Add(new("chat-export", "", "Export cancelled; output is incomplete."));
            if (!File.Exists(Path.Combine(destination, "messages.jsonl"))) {
                using var partial = new StreamWriter(Path.Combine(destination, "messages.jsonl"));
                foreach (string parsed in Directory.GetFiles(work, "*.jsonl").Where(p => int.TryParse(Path.GetFileNameWithoutExtension(p), out _)).Order())
                    foreach (string line in File.ReadLines(parsed)) partial.WriteLine(line);
            }
        }
        catch (Exception ex) when (ExportFailure(ex) || ex is AggregateException) { issues.Add(new("chat-export", "", ex.Message)); }
        finally {
            archive?.Dispose();
            // This invocation owns this exact fresh work directory, including decrypted temporary files.
            if (PathPolicy.Under(work, destination) && Directory.Exists(work)) Directory.Delete(work, true);
        }
        bool complete = issues.IsEmpty && completed == databases.Length && messages > 0 && (archive?.UnresolvedMedia ?? 0) == 0;
        if (!complete && File.Exists(Path.Combine(destination, "messages.jsonl")))
            messages = File.ReadLines(Path.Combine(destination, "messages.jsonl")).LongCount();
        var result = new ChatExportResult(destination, messages, completed, issues.ToList(), complete) {
            Contacts = contacts.Count, MediaFiles = archive?.MediaFiles ?? 0, UnresolvedMedia = archive?.UnresolvedMedia ?? 0,
            ElapsedSeconds = clock.Elapsed.TotalSeconds + verificationSeconds, Parallelism = parallelism,
            VerifiedBackupBytes = verified?.Values.Sum(e => e.Bytes) ?? 0,
            PhaseSeconds = new() { ["backupVerification"] = verificationSeconds, ["databaseProcessing"] = databaseSeconds,
                ["readerIndex"] = readerStarted > 0 ? clock.Elapsed.TotalSeconds - readerStarted : 0 } };
        JsonFiles.Write(Path.Combine(destination, "export-report.json"), result); return result;
    }
    private static bool ExportFailure(Exception ex) => ex is IOException or InvalidDataException or CryptographicException or
        UnauthorizedAccessException or FormatException or OverflowException or ZstdSharp.ZstdException or ArgumentException;
    private static long CountMessages(SqliteReader reader, ChatPlatform platform, CancellationToken cancellation) {
        var tables = reader.Query("SELECT name FROM sqlite_master WHERE type='table'", cancellation).Select(r => r[0]?.ToString() ?? "").ToArray();
        return tables.Where(t => platform == ChatPlatform.QQ ? t is "c2c_msg_table" or "group_msg_table" : t == "MSG" || t.StartsWith("Msg_", StringComparison.Ordinal))
            .Sum(t => Convert.ToInt64(reader.Query("SELECT count(*) FROM " + SqliteReader.Identifier(t), cancellation).Single()[0]));
    }
    private static void DeletePlain(string plain) {
        foreach (string path in new[] { plain, plain + "-wal", plain + "-shm" }) if (File.Exists(path)) File.Delete(path);
    }
    private sealed class DirectProgress(Action<ScanProgress> action) : IProgress<ScanProgress> { public void Report(ScanProgress value) => action(value); }
    private static bool PrepareDatabase(string database, string plain, byte[]? secret, ChatPlatform platform, CancellationToken cancellation, IProgress<ScanProgress>? progress = null) {
        PathPolicy.AssertNoLinks(database);
        using var original = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] head = new byte[16]; original.ReadExactly(head);
        if (!head.AsSpan().SequenceEqual("SQLite format 3\0"u8)) return ChatCipher.DecryptDatabase(database, plain, secret ?? [], platform, cancellation, progress);
        using (var copied = new FileStream(plain, FileMode.CreateNew, FileAccess.Write)) {
            original.Position = 0; byte[] buffer = new byte[1024 * 1024]; int count;
            while ((count = original.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); copied.Write(buffer, 0, count);
                progress?.Report(new("chat-decrypt", 0, original.Position, original.Length, Detail: Path.GetFileName(database))); }
        }
        foreach (string suffix in new[] { "-wal", "-shm" }) if (File.Exists(database + suffix)) {
            PathPolicy.AssertNoLinks(database + suffix); File.Copy(database + suffix, plain + suffix);
        }
        return false;
    }
    private static IEnumerable<string> FindMetadataDatabases(string root) {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory)) {
            PathPolicy.AssertNoLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                var attributes = File.GetAttributes(path); if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                string name = Path.GetFileName(path);
                if ((attributes & FileAttributes.Directory) != 0) {
                    if (!new[] { "attach", "FileStorage", "image", "video", "pic", "cache" }.Contains(name, StringComparer.OrdinalIgnoreCase)) pending.Push(path);
                } else if (new[] { "contact.db", "MicroMsg.db", "nt_contact.db" }.Contains(name, StringComparer.OrdinalIgnoreCase)) yield return path;
            }
        }
    }
    private static void ReadContacts(SqliteReader reader, Dictionary<string, string> contacts, CancellationToken cancellation) {
        foreach (var row in reader.Query("SELECT name FROM sqlite_master WHERE type='table'", cancellation).ToArray()) {
            string table = row[0]?.ToString() ?? "";
            if (!new[] { "contact", "rcontact", "friend", "friends" }.Contains(table, StringComparer.OrdinalIgnoreCase)) continue;
            var columns = Columns(reader, table);
            string? Pick(params string[] names) => names.FirstOrDefault(columns.Contains);
            string? id = Pick("username", "UserName", "uin", "uid"); if (id == null) continue;
            string Expr(string? column) => column == null ? "''" : SqliteReader.Identifier(column);
            string sql = "SELECT " + Expr(id) + "," + Expr(Pick("remark", "Remark", "remark_name")) + "," +
                Expr(Pick("nick_name", "NickName", "nickname", "nick")) + "," + Expr(Pick("alias", "Alias")) + " FROM " + SqliteReader.Identifier(table);
            foreach (var contact in reader.Query(sql, cancellation)) {
                string key = contact[0]?.ToString() ?? "";
                string name = contact.Skip(1).Select(v => v?.ToString()?.Trim()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? key;
                if (key != "") contacts[key] = name;
            }
        }
    }

    public static IEnumerable<string> FindDatabases(string root, ChatPlatform platform) {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory)) {
            PathPolicy.AssertNoLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                if ((attrs & FileAttributes.Directory) != 0) {
                    // Account structures are shallow. Do not descend media trees to locate databases.
                    string name = Path.GetFileName(path);
                    if (!new[] { "attach", "FileStorage", "image", "video", "pic", "cache" }
                        .Contains(name, StringComparer.OrdinalIgnoreCase)) pending.Push(path);
                } else if (platform == ChatPlatform.QQ ? Path.GetFileName(path).Equals("nt_msg.db",
                    StringComparison.OrdinalIgnoreCase) : Regex.IsMatch(Path.GetFileName(path),
                    "^(message_\\d+|MSG\\d*)\\.db$", RegexOptions.IgnoreCase)) yield return path;
            }
        }
    }

    private static IEnumerable<ChatMessage> ReadMessages(SqliteReader reader, ChatPlatform platform,
        CancellationToken cancellation) {
        var tables = reader.Query("SELECT name FROM sqlite_master WHERE type='table'", cancellation)
            .Select(r => r[0]?.ToString() ?? "").ToArray();
        if (platform == ChatPlatform.WeChat) {
            var names = tables.Contains("Name2Id") ? reader.Query("SELECT rowid,user_name FROM Name2Id", cancellation)
                .ToDictionary(r => Convert.ToInt64(r[0]), r => r[1]?.ToString() ?? "") : [];
            var conversations = names.Values.Distinct().ToDictionary(
                name => "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant(), name => name, StringComparer.OrdinalIgnoreCase);
            foreach (string table in tables.Where(t => t.StartsWith("Msg_", StringComparison.Ordinal) || t == "MSG")) {
                var columns = Columns(reader, table);
                bool modern = columns.Contains("message_content");
                string[] required = modern ? ["local_type", "create_time", "real_sender_id", "message_content"] :
                    ["Type", "CreateTime", "StrTalker", "StrContent"];
                if (!required.All(columns.Contains)) throw new InvalidDataException("Unsupported WeChat message schema.");
                string sql = "SELECT " + string.Join(',', required.Select(SqliteReader.Identifier)) +
                    "," + (!modern && columns.Contains("IsSender") ? SqliteReader.Identifier("IsSender") : "0") +
                    " FROM " + SqliteReader.Identifier(table) + " ORDER BY " + SqliteReader.Identifier(required[1]);
                foreach (var row in reader.Query(sql, cancellation)) {
                    long sender = modern ? Convert.ToInt64(row[2] ?? 0) : 0;
                    byte[]? raw = row[3] as byte[];
                    string text = DecodeBody(row[3]);
                    yield return new("WeChat", modern ? conversations.GetValueOrDefault(table, table) : row[2]?.ToString() ?? table,
                        modern ? names.GetValueOrDefault(sender, sender.ToString()) : Convert.ToInt64(row[4] ?? 0) != 0 ? "self" : row[2]?.ToString() ?? "",
                        Convert.ToInt64(row[1] ?? 0), Convert.ToInt64(row[0] ?? 0), text, raw == null ? null : Convert.ToBase64String(raw)) {
                            IsSelf = !modern && Convert.ToInt64(row[4] ?? 0) != 0 };
                }
            }
        } else {
            foreach (string table in tables.Where(t => t is "c2c_msg_table" or "group_msg_table")) {
                bool group = table == "group_msg_table";
                var columns = Columns(reader, table);
                string peer = group ? "40021" : "40030";
                string[] required = [peer, "40033", "40050", "40800"];
                if (!required.All(columns.Contains)) throw new InvalidDataException("Unsupported NTQQ message schema.");
                string sql = "SELECT " + string.Join(',', required.Select(SqliteReader.Identifier)) +
                    " FROM " + SqliteReader.Identifier(table) + " ORDER BY \"40050\"";
                foreach (var row in reader.Query(sql, cancellation)) {
                    byte[]? raw = row[3] as byte[];
                    string body = raw == null ? row[3]?.ToString() ?? "" : DecodeProto(raw);
                    yield return new("QQ", (group ? "group:" : "direct:") + row[0], row[1]?.ToString() ?? "",
                        Convert.ToInt64(row[2] ?? 0), 0, body, raw == null ? null : Convert.ToBase64String(raw));
                }
            }
        }
    }

    private static HashSet<string> Columns(SqliteReader reader, string table) =>
        reader.Query("PRAGMA table_info(" + SqliteReader.Identifier(table) + ")")
            .Select(r => r[1]?.ToString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string DecodeBody(object? value) {
        if (value is not byte[] bytes) return value?.ToString() ?? "";
        if (bytes.AsSpan().StartsWith(new byte[] { 0x28, 0xb5, 0x2f, 0xfd })) {
            using var decompressor = new ZstdSharp.Decompressor();
            bytes = decompressor.Unwrap(bytes, 64 * 1024 * 1024).ToArray();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    public static string DecodeProto(byte[] bytes) {
        var parts = new List<string>();
        void Parse(byte[] data, int depth) {
            if (depth > 6 || data.Length > 64 * 1024 * 1024) return;
            int offset = 0;
            ulong Varint() {
                ulong value = 0;
                for (int shift = 0; shift < 64 && offset < data.Length; shift += 7) {
                    byte b = data[offset++]; value |= (ulong)(b & 127) << shift;
                    if ((b & 128) == 0) return value;
                }
                throw new InvalidDataException("Malformed protobuf field.");
            }
            try {
                while (offset < data.Length) {
                    ulong tag = Varint(); int field = (int)(tag >> 3), wire = (int)(tag & 7);
                    if (wire == 0) _ = Varint();
                    else if (wire is 1 or 5) offset = checked(offset + (wire == 1 ? 8 : 4));
                    else if (wire == 2) {
                        int length = checked((int)Varint());
                        if (length < 0 || length > data.Length - offset) break;
                        byte[] content = data.AsSpan(offset, length).ToArray(); offset += length;
                        if (field == 45101) parts.Add(Encoding.UTF8.GetString(content));
                        else if (field == 45402) parts.Add("[attachment] " + Encoding.UTF8.GetString(content));
                        else if (field < 40000 || field == 47423) Parse(content, depth + 1);
                    } else break;
                }
            } catch (Exception ex) when (ex is InvalidDataException or OverflowException) { }
        }
        Parse(bytes, 0);
        return parts.Count > 0 ? string.Join("\n", parts) : "[unparsed message; original payload retained in JSONL]";
    }
}
