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
    public ChatIdentity? Identity { get; init; }
    public string? IdentityVersion { get; init; }
    public string? ReactionsRawBase64 { get; init; }
    public ChatReaction[] Reactions { get; init; } = [];
    public bool IsSelf { get; init; }
    public ChatMedia[] Media { get; init; } = [];
    public MessagePart[] Parts { get; init; } = [];
    public string? ExtraRawBase64 { get; init; }
    public string? SourceText { get; init; }
    public string? DisplayText { get; init; }
    public string? Id { get; init; }
    public string? ServerId { get; init; }
    public string? Sequence { get; init; }
    public string[] LocalMediaKeys { get; init; } = [];
}
public sealed record ChatExportResult(string Directory, long Messages, int Databases,
    List<ScanIssue> Issues, bool Complete) {
    public int Contacts { get; init; }
    public int MediaFiles { get; init; }
    public int UnresolvedMedia { get; init; }
    public int UnsupportedMessages { get; init; }
    public double ElapsedSeconds { get; init; }
    public int Parallelism { get; init; }
    public Dictionary<string, double> PhaseSeconds { get; init; } = [];
    public long VerifiedBackupBytes { get; init; }
    public bool BackupReverified { get; init; }
    public bool CanResume { get; init; }
}

public sealed class ChatExporter(int maximumParallelism = 2, bool useLocalImageConfiguration = false, bool downloadQQGroupAvatars = false, bool? defaultReaderDark = null) {
    private readonly int parallelism = maximumParallelism is >= 1 and <= 32 ? maximumParallelism : throw new ArgumentOutOfRangeException(nameof(maximumParallelism));
    internal ChatExportResult ExportReadLockedSource(ChatSourceLease source,string destination,ChatPlatform platform,
        byte[]? secret,IProgress<ScanProgress>? progress,CancellationToken cancellation,Func<ChatMessage,ChatMessage?>? selectMessage=null,Func<string,bool>? includeDatabase=null,bool resume=false)=>
        ExportCore(source.Root,destination,platform,secret,progress,cancellation,false,null,accountName:Path.GetFileName(source.IdentityRoot),resume:resume,selectMessage:selectMessage,includeDatabase:includeDatabase,sourceProof:JsonFiles.Hash(new{Pipeline=1,Language=L.Language,LocalImages=useLocalImageConfiguration,SourceIdentity=source.IdentityRoot,Databases=source.Hashes.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToArray()}),sourceIdentityRoot:source.IdentityRoot);
    internal ChatExportResult ExportCompact(ChatSourceLease source,string workspace,ChatPlatform platform,byte[]? secret,ArchiveStore.Writer writer,IProgress<ScanProgress>? progress,CancellationToken cancellation,Func<string,bool>? includeDatabase=null,string? accountName=null){
        string path=Path.Combine(workspace,"export-progress.json");if(File.Exists(path)&&JsonFiles.Read<ChatExportProgress>(path).Finished){PathPolicy.AssertNoLinks(path);File.Delete(path);}
        return ExportCore(source.Root,workspace,platform,secret,progress,cancellation,false,null,accountName:accountName,sourceProof:JsonFiles.Hash(source.Hashes.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToArray()),includeDatabase:includeDatabase,compact:writer,resume:File.Exists(path));
    }
    public ChatExportResult Export(string source, string destination, ChatPlatform platform,
        byte[]? secret = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default) => ExportCore(source, destination, platform,
            secret, progress, cancellation, true, null);

    public ChatExportResult ExportVerifiedBackup(string manifestPath, string destination,
        ChatPlatform platform, byte[]? secret = null, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default, bool verifyBackup = true, bool resume = false, Func<ChatMessage, ChatMessage?>? selectMessage = null, Func<string, bool>? includeDatabase = null) {
        OperationPause.Check(cancellation);
        var manifest = JsonFiles.Read<TransferJournal>(manifestPath);
        var verificationClock = System.Diagnostics.Stopwatch.StartNew();
        var received = ValidateBackup(manifest, cancellation);
        var backupDatabases=FindDatabases(manifest.Plan.Destination,platform).ToArray();
        foreach (string database in backupDatabases)
            foreach (string file in new[] { database, database + "-wal", database + "-shm" }.Where(File.Exists))
                if (!received.Contains(Path.GetRelativePath(manifest.Plan.Destination, file)))
                    throw new InvalidDataException("An unverified message database or WAL was added to the archive.");
        // Fail on an incorrect key before reading/hash-checking potentially many GB of attachments.
        foreach (string database in backupDatabases) {
            OperationPause.Check(cancellation);
            using var input = File.OpenRead(database);
            byte[] prefix = new byte[Math.Min(input.Length, 5120)]; input.ReadExactly(prefix);
            bool plain = prefix.AsSpan().StartsWith("SQLite format 3\0"u8) ||
                (platform == ChatPlatform.QQ && prefix.Length >= 1040 && prefix.AsSpan(1024).StartsWith("SQLite format 3\0"u8));
            if (!plain && (secret == null || ChatCipher.Detect(prefix, secret, platform) == null))
                throw new CryptographicException(L.T("DatabaseKeyMismatch"));
        }
        if (verifyBackup) VerifyBackupContents(manifest, progress, cancellation);
        return ExportCore(manifest.Plan.Destination, destination, platform, secret, progress, cancellation, false,
            manifest.Receipts.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase), Path.GetFileName(manifest.Plan.Source), verificationClock.Elapsed.TotalSeconds, verifyBackup, resume, selectMessage, includeDatabase);
    }

    /// <summary>Recheck the immutable raw backup against its receipts without exporting or unlocking databases.</summary>
    public static void VerifyBackup(string manifestPath, IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default) {
        OperationPause.Check(cancellation);
        PathPolicy.AssertNoLinks(manifestPath);
        var manifest = JsonFiles.Read<TransferJournal>(manifestPath);
        ValidateBackup(manifest, cancellation);
        VerifyBackupContents(manifest, progress, cancellation);
    }

    private static HashSet<string> ValidateBackup(TransferJournal manifest, CancellationToken cancellation) {
        if (manifest.Version != 1 || manifest.State is not ("Verified" or "Completed" or
            "CopiedWithRetainedSources" or "CancelledAfterVerification") ||
            manifest.Receipts.Count != manifest.Plan.Files.Count)
            throw new InvalidDataException("The backup has not completed verification.");
        var expected = manifest.Plan.Files.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var received = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Receipts) {
            OperationPause.Check(cancellation);
            if (!received.Add(entry.RelativePath) || !expected.TryGetValue(entry.RelativePath, out var planned) ||
                planned.Stamp.Bytes != entry.Bytes)
                throw new InvalidDataException("Backup receipts do not match the planned files.");
            string path = ChatIncrementalBackup.Resolve(manifest.Plan.Destination, entry);
            PathPolicy.AssertNoLinks(path);
            if (new FileInfo(path).Length != entry.Bytes) throw new InvalidDataException("The archived copy changed since verification.");
        }
        return received;
    }

    private static void VerifyBackupContents(TransferJournal manifest, IProgress<ScanProgress>? progress, CancellationToken cancellation) {
        long verifiedBytes = 0, totalBytes = manifest.Receipts.Sum(e => e.Bytes);
        int verifiedFiles = 0;
        foreach (var entry in manifest.Receipts) {
            OperationPause.Check(cancellation);
            using var input = File.OpenRead(ChatIncrementalBackup.Resolve(manifest.Plan.Destination, entry));
            if (input.Length != entry.Bytes || Convert.ToHexString(TransferService.Hash(input, cancellation, count => {
                verifiedBytes += count;
                progress?.Report(new("chat-verify", verifiedFiles, verifiedBytes, totalBytes, manifest.Receipts.Count, entry.RelativePath));
            })) != entry.Sha256) throw new InvalidDataException("The archived copy changed since verification.");
            verifiedFiles++;
        }
        progress?.Report(new("chat-verify", verifiedFiles, verifiedBytes, totalBytes, manifest.Receipts.Count));
    }

    private ChatExportResult ExportCore(string source, string destination, ChatPlatform platform,
        byte[]? secret, IProgress<ScanProgress>? progress, CancellationToken cancellation,
        bool requireClosedClient, Dictionary<string, TransferReceipt>? verified, string? accountName = null, double verificationSeconds = 0, bool backupReverified = false, bool resume = false, Func<ChatMessage, ChatMessage?>? selectMessage = null, Func<string, bool>? includeDatabase = null, string? sourceProof=null,ArchiveStore.Writer? compact=null,string? sourceIdentityRoot=null) {
        bool checkpointing=verified!=null||sourceProof!=null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        source = PathPolicy.Normalize(source); destination = PathPolicy.Normalize(destination);
        if (PathPolicy.Under(destination, source) || PathPolicy.Under(source, destination) ||
            source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Export and source must be separate directories.");
        string[] processNames = platform == ChatPlatform.QQ ? ["QQ"] : ["Weixin", "WeChat"];
        if (requireClosedClient && CleanupEngine.RunningProcesses(new CleanupRule { Processes = processNames }).Length > 0)
            throw new IOException("Close the chat client before exporting a consistent snapshot.");
        if (compact == null && !resume && (Directory.Exists(destination) || File.Exists(destination))) throw new IOException("Export destination must be new.");
        if (resume && (!checkpointing || !Directory.Exists(destination))) throw new InvalidDataException("Resume requires an existing checkpoint and verified backup.");
        if (resume && !File.Exists(Path.Combine(destination, "export-progress.json")))
            throw new InvalidDataException("This export has no supported checkpoint. Exports started by older versions cannot be resumed.");
        string[] databases = FindDatabases(source, platform).Where(p => includeDatabase == null || includeDatabase(Path.GetRelativePath(source, p))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (databases.Length == 0 && includeDatabase == null) throw new InvalidDataException("No supported message databases found.");
        Directory.CreateDirectory(destination); PathPolicy.AssertNoLinks(destination);
        string lockPath = Path.Combine(destination, ".export.lock"); if (File.Exists(lockPath)) PathPolicy.AssertNoLinks(lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string progressPath = Path.Combine(destination, "export-progress.json");
        string identity = verified == null ? sourceProof??"" : JsonFiles.Hash(new { Pipeline = 1, Language = L.Language, LocalImages = useLocalImageConfiguration,
            Receipts = verified.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToArray() });
        var checkpoint = resume ? JsonFiles.Read<ChatExportProgress>(progressPath) : new ChatExportProgress {
            Source = source, SourceIdentityRoot=sourceIdentityRoot, Platform = platform, Identity = identity, Work = ".work-" + Guid.NewGuid().ToString("N") };
        if (checkpoint.Version != 1 || checkpoint.Finished || checkpoint.Source != source || checkpoint.Platform != platform || checkpoint.Identity != identity ||
            !Regex.IsMatch(checkpoint.Work, "^\\.work-[a-f0-9]{32}$")) throw new InvalidDataException("Export checkpoint does not match this backup or export format.");
        string work = TransferService.Child(destination, checkpoint.Work);
        if (resume) {
            progress?.Report(new("chat-resume", 0, 0));
            PathPolicy.AssertNoLinks(work);
            foreach (var pair in checkpoint.Databases) {
                if (pair.Key < 0 || pair.Key >= databases.Length || pair.Value.Messages < 0 || pair.Value.File.Path != Path.Combine(checkpoint.Work, $"{pair.Key}.jsonl"))
                    throw new InvalidDataException("Invalid database export checkpoint.");
                ExportProgressFiles.Validate(destination, pair.Value.File, false, cancellation);
            }
        } else { Directory.CreateDirectory(work); if (checkpointing) JsonFiles.Write(progressPath, checkpoint); }
        object checkpointGate = new(); bool readerFinished = false;
        var issues = new System.Collections.Concurrent.ConcurrentBag<ScanIssue>();
        if (platform == ChatPlatform.QQ)
            foreach (string database in EnumerateDatabases(source, platform).Where(p => IsQQGuildDatabase(source, p)))
                issues.Add(new("chat-export", Path.GetRelativePath(source, database),
                    "QQ Guild uses a separate database key and is not included in this reading export. Its files remain in the raw backup."));
        var contacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var avatars = new Dictionary<string,string>(StringComparer.Ordinal);
        var identities = new Dictionary<string,ChatIdentity>(StringComparer.Ordinal);
        int completed = checkpoint.Databases.Count; long messages = checkpoint.Databases.Values.Sum(d => d.Messages);
        var weights = new long[databases.Length]; var databaseBytes = databases.Select(p => new FileInfo(p).Length).ToArray();
        var decryptWeights = new long[databases.Length];
        long totalDatabaseBytes = databaseBytes.Sum();
        double databaseSeconds = 0, readerStarted = 0;
        ChatArchiveWriter? archive = null;
        bool Allowed(string p) => verified == null || verified.ContainsKey(Path.GetRelativePath(source, p));
        try {
            // Metadata databases are small and read once before workers start.
            foreach (string contact in FindMetadataDatabases(source).Where(Allowed).Where(p=>platform==ChatPlatform.WeChat||!new[]{"head_image.db","Misc.db"}.Contains(Path.GetFileName(p),StringComparer.OrdinalIgnoreCase))
                .Where(p => platform != ChatPlatform.QQ || !IsQQGuildDatabase(source, p))) {
                OperationPause.Check(cancellation); string plain = Path.Combine(work, Guid.NewGuid().ToString("N") + ".db");
                try {
                    bool shifted = PrepareDatabase(contact, plain, secret, platform, cancellation);
                    using var reader = new SqliteReader(plain, shifted);
                    ReadContacts(reader, contacts, cancellation, identities);
                    foreach(var avatar in ChatAvatars.Read(reader,destination,cancellation))avatars[avatar.Key]=avatar.Value;
                } catch (Exception ex) when (ExportFailure(ex)) { issues.Add(new("chat-contacts", Path.GetRelativePath(source, contact), ex.Message)); }
                finally { DeletePlain(plain); }
            }
            Parallel.For(0, databases.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellation }, index => {
                bool alreadyParsed; lock (checkpointGate) alreadyParsed = checkpoint.Databases.ContainsKey(index);
                if (alreadyParsed) { Interlocked.Exchange(ref weights[index], databaseBytes[index]); return; }
                string database = databases[index], plain = Path.Combine(work, $"{index}.db"), output = Path.Combine(work, $"{index}.jsonl");
                string pending = output + ".parsing";
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
                    using var writer = new StreamWriter(pending, false, new UTF8Encoding(false), 128 * 1024);
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
                    writer.Flush(); writer.Close(); File.Move(pending, output, true);
                    if (checkpointing) lock (checkpointGate) {
                        checkpoint.Databases[index] = new(parsedRows, ExportProgressFiles.Proof(destination, output)); JsonFiles.Write(progressPath, checkpoint);
                    }
                    Interlocked.Increment(ref completed);
                } catch (Exception ex) when (ExportFailure(ex)) { issues.Add(new("chat-export", Path.GetRelativePath(source, database), ex.Message)); }
                finally { DeletePlain(plain); if (File.Exists(pending)) File.Delete(pending); }
            });
            databaseSeconds = clock.Elapsed.TotalSeconds; readerStarted = databaseSeconds;
            if (checkpointing && completed != databases.Length) throw new InvalidDataException("Database preparation is incomplete; committed progress was retained for resume.");
            using var voices = platform == ChatPlatform.WeChat ? new WeChatVoices(source, work, secret, cancellation) : null;
            archive = new ChatArchiveWriter(source, destination, work, contacts, verified, cancellation, accountName ?? Path.GetFileName(source), useLocalImageConfiguration, checkpointing, parallelism, voices, avatars, downloadQQGroupAvatars, identities, defaultReaderDark, platform.ToString().ToLowerInvariant(),compact);
            progress?.Report(new("chat-reader", (int)Math.Min(archive.ProcessedMessages, int.MaxValue), 0, TotalFiles: (int)Math.Min(messages, int.MaxValue)));
            long assembled = 0;
            for (int index = 0; index < databases.Length; index++) {
                archive.SetCompactSource(Path.GetRelativePath(source,databases[index]));
                string output = Path.Combine(work, $"{index}.jsonl"); if (!File.Exists(output)) continue;
                foreach (string line in File.ReadLines(output)) {
                    OperationPause.Check(cancellation);
                    if (assembled++ < archive.ProcessedMessages) continue;
                    var message = JsonSerializer.Deserialize<ChatMessage>(line) ?? throw new InvalidDataException("Invalid parsed message.");
                    if (selectMessage != null) message = selectMessage(message);
                    if (message != null) archive.Add(message);
                    if (assembled % 300 == 0) progress?.Report(new("chat-reader", (int)Math.Min(assembled, int.MaxValue), 0,
                        TotalFiles: (int)Math.Min(messages, int.MaxValue), Detail: message?.Conversation){Performance=compact?.Performance});
                }
            }
            archive.Finish(progress);
            if(archive.AvatarDownloadFailures>0)issues.Add(new("chat-avatars","",L.T("GroupAvatarUnavailableCount",archive.AvatarDownloadFailures)));
            readerFinished = true;
        } catch (OperationCanceledException) {
            archive?.SaveSafeProgress();
            issues.Add(new("chat-export", "", "Export cancelled; output is incomplete."));
        }
        catch (Exception ex) when (ExportFailure(ex) || ex is AggregateException) { issues.Add(new("chat-export", "", ex.Message)); }
        finally {
            archive?.Dispose();
            // Retain committed JSON spools for resume, never decrypted temporary databases.
            foreach (string file in Directory.GetFiles(work, "*.db*")) { PathPolicy.AssertNoLinks(file); File.Delete(file); }
            if (checkpointing) { checkpoint.Finished = readerFinished && completed == databases.Length; JsonFiles.Write(progressPath, checkpoint); }
            if ((!checkpointing || checkpoint.Finished) && PathPolicy.Under(work, destination) && Directory.Exists(work)) Directory.Delete(work, true);
        }
        bool complete = issues.IsEmpty && completed == databases.Length && messages > 0 && (archive?.UnresolvedMedia ?? 0) == 0 && (archive?.UnsupportedMessages ?? 0) == 0;
        if (!complete) messages = archive?.ProcessedMessages ?? 0;
        var result = new ChatExportResult(destination, messages, completed, issues.ToList(), complete) {
            Contacts = contacts.Count, MediaFiles = archive?.MediaFiles ?? 0, UnresolvedMedia = archive?.UnresolvedMedia ?? 0,
            UnsupportedMessages = archive?.UnsupportedMessages ?? 0,
            ElapsedSeconds = clock.Elapsed.TotalSeconds + verificationSeconds, Parallelism = parallelism,
            BackupReverified = backupReverified, VerifiedBackupBytes = backupReverified ? verified?.Values.Sum(e => e.Bytes) ?? 0 : 0,
            CanResume = checkpointing && !checkpoint.Finished,
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
            while ((count = original.Read(buffer)) > 0) { OperationPause.Check(cancellation); copied.Write(buffer, 0, count);
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
                } else if (new[] { "contact.db", "MicroMsg.db", "nt_contact.db", "profile_info.db", "group_info.db", "head_image.db", "Misc.db" }.Contains(name, StringComparer.OrdinalIgnoreCase)) yield return path;
            }
        }
    }
    private static void ReadContacts(SqliteReader reader, Dictionary<string, string> contacts, CancellationToken cancellation, Dictionary<string,ChatIdentity>? identities = null) {
        foreach (var row in reader.Query("SELECT name FROM sqlite_master WHERE type='table'", cancellation).ToArray()) {
            string table = row[0]?.ToString() ?? "";
            if (table is "profile_info_v6" or "group_member3" or "group_list" or "group_detail_info_ver1") {
                var numeric = Columns(reader, table);
                string NumericExpr(string column) => numeric.Contains(column) ? SqliteReader.Identifier(column) : "NULL";
                bool group = table is "group_list" or "group_detail_info_ver1", member = table == "group_member3";
                string numericId = group ? "60001" : "1002"; if (!numeric.Contains(numericId)) continue;
                string numericSql = "SELECT " + string.Join(',', new[] { numericId, "1000", group ? "60007" : "20002", "20009", "60001" }.Select(NumericExpr)) + " FROM " + SqliteReader.Identifier(table);
                foreach (var value in reader.Query(numericSql, cancellation)) {
                    string key = value[0]?.ToString() ?? "", uid = value[1]?.ToString() ?? "";
                    string name = (group || member ? value[2] : value[3])?.ToString()?.Trim() ?? "";
                    if (name == "") name = value[2]?.ToString()?.Trim() ?? "";
                    if (key is "" or "0" || name == "") continue;
                    string prefix = group ? "group:" : member ? "group:" + value[4] + "/" : "";
                    contacts[prefix + key] = name;
                    if (!group && identities != null) {
                        var identity = new ChatIdentity(key, member ? "group:" + value[4] : "",
                            Nickname: member ? null : value[2]?.ToString(), FriendRemark: member ? null : value[3]?.ToString(),
                            GroupCard: member ? value[2]?.ToString() : null);
                        identities[prefix + key] = identity;
                        if (uid != "") identities[prefix + uid] = identity with { Sender = uid };
                    }
                    if (!group && uid != "") contacts[prefix + uid] = name;
                }
                continue;
            }
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
                if (key != "") {
                    contacts[key] = name;
                    if (identities != null) identities[key] = new(key, "", Nickname: contact[2]?.ToString(), FriendRemark: contact[1]?.ToString());
                }
            }
        }
    }

    public static Dictionary<string, string> ReadContactNames(string source, ChatPlatform platform, byte[]? secret, CancellationToken cancellation = default) {
        source = PathPolicy.Normalize(source); PathPolicy.AssertNoLinks(source);
        string work = Directory.CreateTempSubdirectory("storloom-contacts-").FullName;
        var contacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach (string path in FindMetadataDatabases(source).Where(p => platform != ChatPlatform.QQ || !IsQQGuildDatabase(source, p)).Where(p=>!new[]{"head_image.db","Misc.db"}.Contains(Path.GetFileName(p),StringComparer.OrdinalIgnoreCase))) {
                OperationPause.Check(cancellation); string plain = Path.Combine(work, Guid.NewGuid().ToString("N") + ".db");
                try { bool shifted = PrepareDatabase(path, plain, secret, platform, cancellation); using var reader = new SqliteReader(plain, shifted); ReadContacts(reader, contacts, cancellation); }
                finally { DeletePlain(plain); }
            }
            return contacts;
        } finally { Directory.Delete(work, true); }
    }

    public static IEnumerable<string> FindDatabases(string root, ChatPlatform platform) {
        // Main QQ and QQ Guild have identically named databases with independent keys.
        // Filesystem traversal order must never decide which key we authenticate.
        return EnumerateDatabases(root, platform)
            .Where(p => platform != ChatPlatform.QQ || !IsQQGuildDatabase(root, p))
            .OrderBy(p => platform == ChatPlatform.QQ && Path.GetRelativePath(root, p)
                .Replace('\\', '/').Equals("nt_qq/nt_db/nt_msg.db", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsQQGuildDatabase(string root, string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals("QQGuild", StringComparison.OrdinalIgnoreCase) ||
        Path.GetRelativePath(root, path).Split('\\', '/').Any(p => p.Equals("QQGuild", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateDatabases(string root, ChatPlatform platform) {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory)) {
            PathPolicy.AssertNoLinks(directory);
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos()) {
                string path=item.FullName;var attrs = item.Attributes;
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
                string Optional(params string[] choices) => choices.FirstOrDefault(columns.Contains) is string column ? SqliteReader.Identifier(column) : "NULL";
                string sql = "SELECT " + string.Join(',', required.Select(SqliteReader.Identifier)) +
                    "," + (!modern && columns.Contains("IsSender") ? SqliteReader.Identifier("IsSender") : "0") +
                    "," + (!modern && columns.Contains("CompressContent") ? SqliteReader.Identifier("CompressContent") : "NULL") +
                    "," + Optional("server_id", "MsgSvrID") + "," + Optional("packed_info_data", "BytesExtra") +
                    " FROM " + SqliteReader.Identifier(table) + " ORDER BY " + SqliteReader.Identifier(required[1]);
                foreach (var row in reader.Query(sql, cancellation)) {
                    long sender = modern ? Convert.ToInt64(row[2] ?? 0) : 0;
                    byte[]? raw = row[3] as byte[];
                    string text = DecodeBody(row[3]);
                    string sourceText = text;
                    byte[]? extra = row[5] as byte[];
                    if (extra is { Length: > 0 }) {
                        string? expanded = DecodeLegacyContent(extra); if (!string.IsNullOrWhiteSpace(expanded)) text = expanded;
                    }
                    yield return new("WeChat", modern ? conversations.GetValueOrDefault(table, table) : row[2]?.ToString() ?? table,
                        modern ? names.GetValueOrDefault(sender, sender.ToString()) : Convert.ToInt64(row[4] ?? 0) != 0 ? "self" : row[2]?.ToString() ?? "",
                        Convert.ToInt64(row[1] ?? 0), Convert.ToInt64(row[0] ?? 0), text, raw == null ? null : Convert.ToBase64String(raw)) {
                            IsSelf = !modern && Convert.ToInt64(row[4] ?? 0) != 0,
                            SourceText = sourceText == text ? null : sourceText,
                            ExtraRawBase64 = extra == null ? null : Convert.ToBase64String(extra),
                            ServerId = row[6] is long id && id < 0 ? unchecked((ulong)id).ToString(System.Globalization.CultureInfo.InvariantCulture) : row[6]?.ToString(), LocalMediaKeys = row[7] is byte[] packed ?
                                Regex.Matches(Encoding.Latin1.GetString(packed), "(?<![a-fA-F0-9])[a-fA-F0-9]{32}(?![a-fA-F0-9])").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [] };
                }
            }
        } else {
            foreach (string table in tables.Where(t => t is "c2c_msg_table" or "group_msg_table")) {
                bool group = table == "group_msg_table";
                var columns = Columns(reader, table);
                string peer = group ? "40021" : "40030";
                string[] required = [peer, "40033", "40050", "40800"];
                if (!required.All(columns.Contains)) throw new InvalidDataException("Unsupported NTQQ message schema.");
                string Optional(string name) => columns.Contains(name) ? SqliteReader.Identifier(name) : "NULL";
                string sql = "SELECT " + string.Join(',', required.Select(SqliteReader.Identifier)) +
                    "," + (columns.Contains("40011") ? "\"40011\"" : "0") +
                    "," + string.Join(',', new[] { "40001", "40003", "40013", "40090", "40093", "40850", "40900", "40062" }.Select(Optional)) +
                    " FROM " + SqliteReader.Identifier(table) + " ORDER BY \"40050\"";
                foreach (var row in reader.Query(sql, cancellation)) {
                    byte[]? raw = row[3] as byte[];
                    string body = raw == null ? row[3]?.ToString() ?? "" : DecodeProto(raw);
                    var parts = raw == null ? [] : QQMessageParser.Parse(raw, Convert.ToInt64(row[4] ?? 0));
                    if (row[11] is byte[] cached && parts.Any(p => p.Kind == "forward")) {
                        var forwarded = QQMessageParser.ParseForwardCache(cached);
                        parts = parts.Select(p => p.Kind == "forward" && p.Forwarded.Length == 0 ? p with { Forwarded = forwarded,
                            MediaKeys = [.. p.MediaKeys ?? [], .. forwarded.SelectMany(f => f.Parts).SelectMany(c => c.MediaKeys ?? [])] } : p).ToArray();
                    }
                    if (Convert.ToInt64(row[7] ?? 0) == 3 && !parts.Any(p => p.Kind == "recall")) parts = [new("system", Text: parts.Length == 0 ? body : string.Join("\n", parts.Select(p => p.Text).Where(t => t.Length > 0)))];
                    else if (group && row[10]?.ToString() is string reference && reference != "0")
                        parts = parts.Select(p => p.Kind == "reply" ? p with { ReferenceId = "seq:" + reference } : p).ToArray();
                    yield return new("QQ", (group ? "group:" : "direct:") + row[0], row[1]?.ToString() ?? "",
                        Convert.ToInt64(row[2] ?? 0), Convert.ToInt64(row[4] ?? 0), body, raw == null ? null : Convert.ToBase64String(raw)) {
                            ServerId = row[5]?.ToString(), Sequence = row[6]?.ToString(),
                            SenderName = new[] { row[8]?.ToString(), row[9]?.ToString() }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                            Identity = new(row[1]?.ToString() ?? "", (group ? "group:" : "direct:") + row[0],
                                Nickname: row[9]?.ToString(), GroupCard: group ? row[8]?.ToString() : null,
                                RecordedName: group ? null : row[8]?.ToString(),
                                MessageNickname: !string.IsNullOrWhiteSpace(row[9]?.ToString()), MessageGroupCard: group && !string.IsNullOrWhiteSpace(row[8]?.ToString())),
                            ReactionsRawBase64 = row[12] is byte[] reactions ? Convert.ToBase64String(reactions) : null,
                            Reactions = row[12] is byte[] likes ? QQMessageParser.ParseReactions(likes) : [],
                            IsSelf = Convert.ToInt64(row[7] ?? 0) is 1 or 2, Parts = parts };
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

    public static string? DecodeLegacyContent(byte[] bytes) {
        const int limit = 16 * 1024 * 1024;
        if (bytes.Length > limit) return null;
        try {
            if (bytes.AsSpan().StartsWith("<"u8)) return new UTF8Encoding(false, true).GetString(bytes);
            for (int size = Math.Min(limit, Math.Max(4096, bytes.Length * 4)); size <= limit; size = Math.Min(limit, size * 2)) {
                byte[] output = new byte[size];
                int length = K4os.Compression.LZ4.LZ4Codec.Decode(bytes, 0, bytes.Length, output, 0, size);
                if (length > 0) return new UTF8Encoding(false, true).GetString(output, 0, length);
                if (size == limit) break;
            }
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        return null;
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
                        else if (field < 40000 || field is 40800 or 47423) Parse(content, depth + 1);
                    } else break;
                }
            } catch (Exception ex) when (ex is InvalidDataException or OverflowException) { }
        }
        Parse(bytes, 0);
        return parts.Count > 0 ? string.Concat(parts) : "[unparsed message; original payload retained in JSONL]";
    }
}
