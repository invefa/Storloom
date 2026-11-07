using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Storloom.Core.Localization;

namespace Storloom.Core.Chats;

public sealed record ChatMessage(string Platform, string Conversation, string Sender,
    long Timestamp, long Type, string Text, string? RawBase64 = null);
public sealed record ChatExportResult(string Directory, long Messages, int Databases,
    List<ScanIssue> Issues, bool Complete);

public sealed class ChatExporter {
    public ChatExportResult Export(string source, string destination, ChatPlatform platform,
        byte[]? secret = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default) => ExportCore(source, destination, platform,
            secret, progress, cancellation, true);

    public ChatExportResult ExportVerifiedBackup(string manifestPath, string destination,
        ChatPlatform platform, byte[]? secret = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default) {
        var manifest = JsonFiles.Read<TransferJournal>(manifestPath);
        if (manifest.Version != 1 || manifest.State is not ("Verified" or "Completed" or
            "CopiedWithRetainedSources" or "CancelledAfterVerification") ||
            manifest.Receipts.Count != manifest.Plan.Files.Count)
            throw new InvalidDataException("The backup has not completed verification.");
        var expected = manifest.Plan.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var received = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Receipts) {
            cancellation.ThrowIfCancellationRequested();
            if (!received.Add(entry.RelativePath) || !expected.TryGetValue(entry.RelativePath, out var planned) ||
                planned.Stamp.Bytes != entry.Bytes)
                throw new InvalidDataException("Backup receipts do not match the planned files.");
            string path = TransferService.Child(manifest.Plan.Destination, entry.RelativePath);
            PathPolicy.AssertNoLinks(path);
            using var input = File.OpenRead(path);
            if (input.Length != entry.Bytes || Convert.ToHexString(TransferService.Hash(input, cancellation)) != entry.Sha256)
                throw new InvalidDataException("The archived copy changed since verification.");
        }
        foreach (string database in FindDatabases(manifest.Plan.Destination, platform))
            foreach (string file in new[] { database, database + "-wal", database + "-shm" }.Where(File.Exists))
                if (!received.Contains(Path.GetRelativePath(manifest.Plan.Destination, file)))
                    throw new InvalidDataException("An unverified message database or WAL was added to the archive.");
        return ExportCore(manifest.Plan.Destination, destination, platform, secret, progress, cancellation, false);
    }

    private ChatExportResult ExportCore(string source, string destination, ChatPlatform platform,
        byte[]? secret, IProgress<ScanProgress>? progress, CancellationToken cancellation,
        bool requireClosedClient) {
        source = PathPolicy.Normalize(source);
        destination = PathPolicy.Normalize(destination);
        if (PathPolicy.Under(destination, source) || PathPolicy.Under(source, destination) ||
            source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Export and source must be separate directories.");
        string[] processNames = platform == ChatPlatform.QQ ? ["QQ"] : ["Weixin", "WeChat"];
        if (requireClosedClient && CleanupEngine.RunningProcesses(new CleanupRule { Processes = processNames }).Length > 0)
            throw new IOException("Capture a key if needed, then close the chat client before exporting a consistent snapshot.");
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Export destination must be new.");
        string[] databases = FindDatabases(source, platform).ToArray();
        if (databases.Length == 0) throw new InvalidDataException("No supported message databases found.");
        Directory.CreateDirectory(destination);
        PathPolicy.AssertNoLinks(destination);
        var issues = new List<ScanIssue>();
        long messages = 0;
        int completeDatabases = 0;
        string jsonPath = Path.Combine(destination, "messages.jsonl");
        string htmlPath = Path.Combine(destination, "messages.html");
        string work = Path.Combine(destination, ".work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try {
            using var json = new StreamWriter(jsonPath, false, new UTF8Encoding(false));
            using var html = new StreamWriter(htmlPath, false, new UTF8Encoding(false));
            html.WriteLine($"<!doctype html><html lang=\"{L.Language}\"><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width\"><meta http-equiv=\"Content-Security-Policy\" " +
                $"content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>{L.T("ChatArchive")}</title>" +
                "<style>body{font:16px system-ui;max-width:960px;margin:32px auto;padding:0 20px}" +
                "article{border-bottom:1px solid #ccc;padding:12px 0}pre{white-space:pre-wrap;overflow-wrap:anywhere}" +
                $"small{{color:#666}}</style><h1>{L.T("ChatArchive")}</h1>" +
                $"<p>{L.T("ChatArchiveNotice")}</p>");
            foreach (string database in databases) {
                cancellation.ThrowIfCancellationRequested();
                string plain = Path.Combine(work, Guid.NewGuid().ToString("N") + ".db");
                try {
                    bool shiftedQQ = false;
                    PathPolicy.AssertNoLinks(database);
                    using (var original = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        byte[] head = new byte[16];
                        original.ReadExactly(head);
                        if (head.AsSpan().SequenceEqual("SQLite format 3\0"u8)) {
                            using var copied = new FileStream(plain, FileMode.CreateNew, FileAccess.Write);
                            original.Position = 0;
                            original.CopyTo(copied);
                            copied.Flush(true);
                            if (File.Exists(database + "-wal")) File.Copy(database + "-wal", plain + "-wal");
                            if (File.Exists(database + "-shm")) File.Copy(database + "-shm", plain + "-shm");
                        } else {
                            shiftedQQ = ChatCipher.DecryptDatabase(database, plain, secret ?? [], platform, cancellation);
                        }
                    }
                    using var reader = new SqliteReader(plain, shiftedQQ);
                    if (reader.Query("PRAGMA quick_check", cancellation).Any(r => r[0]?.ToString() != "ok"))
                        throw new InvalidDataException("SQLite integrity check failed.");
                    foreach (var message in ReadMessages(reader, platform, cancellation)) {
                        json.WriteLine(JsonSerializer.Serialize(message));
                        string time;
                        try { time = DateTimeOffset.FromUnixTimeSeconds(message.Timestamp).ToString("u"); }
                        catch (ArgumentOutOfRangeException) { time = message.Timestamp.ToString(); }
                        html.WriteLine($"<article><strong>{WebUtility.HtmlEncode(message.Conversation)}</strong> " +
                            $"<small>{WebUtility.HtmlEncode(time)} · {WebUtility.HtmlEncode(message.Sender)} · {message.Type}</small>" +
                            $"<pre>{WebUtility.HtmlEncode(message.Text)}</pre></article>");
                        messages++;
                        if (messages % 500 == 0) progress?.Report(new("chat-export", (int)Math.Min(messages, int.MaxValue), 0));
                    }
                    completeDatabases++;
                } catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or
                    UnauthorizedAccessException or FormatException or OverflowException or ZstdSharp.ZstdException) {
                    issues.Add(new("chat-export", Path.GetRelativePath(source, database), ex.Message));
                } finally {
                    foreach (string file in new[] { plain, plain + "-wal", plain + "-shm" })
                        if (File.Exists(file)) File.Delete(file);
                }
            }
            html.WriteLine("</html>");
            json.Flush(); html.Flush();
        } catch (OperationCanceledException) {
            issues.Add(new("chat-export", "", "Export cancelled; output is incomplete."));
        } catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or ZstdSharp.ZstdException) {
            issues.Add(new("chat-export", "", ex.Message));
        } finally {
            if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work);
        }
        bool complete = issues.Count == 0 && completeDatabases == databases.Length && messages > 0;
        var result = new ChatExportResult(destination, messages, completeDatabases, issues, complete);
        JsonFiles.Write(Path.Combine(destination, "export-report.json"), result);
        return result;
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
            foreach (string table in tables.Where(t => t.StartsWith("Msg_", StringComparison.Ordinal) || t == "MSG")) {
                var columns = Columns(reader, table);
                bool modern = columns.Contains("message_content");
                string[] required = modern ? ["local_type", "create_time", "real_sender_id", "message_content"] :
                    ["Type", "CreateTime", "StrTalker", "StrContent"];
                if (!required.All(columns.Contains)) throw new InvalidDataException("Unsupported WeChat message schema.");
                string sql = "SELECT " + string.Join(',', required.Select(SqliteReader.Identifier)) +
                    " FROM " + SqliteReader.Identifier(table) + " ORDER BY " + SqliteReader.Identifier(required[1]);
                foreach (var row in reader.Query(sql, cancellation)) {
                    long sender = modern ? Convert.ToInt64(row[2] ?? 0) : 0;
                    byte[]? raw = row[3] as byte[];
                    string text = DecodeBody(row[3]);
                    yield return new("WeChat", modern ? table : row[2]?.ToString() ?? table,
                        names.GetValueOrDefault(sender, sender.ToString()), Convert.ToInt64(row[1] ?? 0),
                        Convert.ToInt64(row[0] ?? 0), text, raw == null ? null : Convert.ToBase64String(raw));
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
