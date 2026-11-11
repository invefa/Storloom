using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Storloom.Core.Chats;

public sealed record ChatReadingUpdateResult(long Added, long Changed, long Unchanged, int Conversations, string Directory);

/// <summary>Preserve historical messages, merge stable message IDs, regenerate affected conversations only.</summary>
public static class ChatReadingUpdate {
    private sealed record UpdateState(int Version, string Account, ChatPlatform Platform, FileStamp Transcript, string IndexSha256, Dictionary<string, string>? Databases = null);
    private sealed record Publication(string Work, Dictionary<string, bool> Files);
    private const string StateFile = ".reading-update-state.json", IndexFile = ".reading-update-index.db", PendingFile = ".reading-update-pending.json";
    private static string Key(ChatMessage m) => JsonFiles.Hash(new { m.Platform, m.Conversation,
        Identity = !string.IsNullOrEmpty(m.ServerId) && m.ServerId != "0" ? "server:" + m.ServerId :
            !string.IsNullOrEmpty(m.Sequence) && m.Sequence != "0" ? $"sequence:{m.Sequence}:{m.Sender}:{m.Timestamp}" : "payload:" + Fingerprint(m) });
    private static string Fingerprint(ChatMessage m) { m = MessagePresentation.ResolveSenderPrefix(m); return JsonFiles.Hash(new { m.Platform, m.Conversation, m.Sender, m.Timestamp, m.Type, m.Text, m.SourceText, m.RawBase64, m.ExtraRawBase64, m.ReactionsRawBase64, Keys = m.LocalMediaKeys.Order(StringComparer.Ordinal) }); }
    private static JsonObject ReadIndex(string root) {
        string text = File.ReadAllText(Path.Combine(root, "archive-index.js"));
        if (!text.StartsWith("window.archiveIndex=", StringComparison.Ordinal)) throw new InvalidDataException("Unsupported reading index.");
        return JsonNode.Parse(text["window.archiveIndex=".Length..].Trim().TrimEnd(';'))!.AsObject();
    }
    private static ChatMessage Message(string line) => JsonSerializer.Deserialize<ChatMessage>(line) ?? throw new InvalidDataException("Invalid archived message.");
    public static ChatReadingUpdateResult Update(string manifestPath, string directory, ChatPlatform platform, byte[]? secret = null,
        int parallelism = 2, bool verifyBackup = true, bool useLocalConfiguration = false,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default, bool downloadQQGroupAvatars = false) {
        directory = PathPolicy.Normalize(directory); PathPolicy.AssertNoLinks(directory);
        string lockPath = Path.Combine(directory, ".export.lock"); if (File.Exists(lockPath)) PathPolicy.AssertNoLinks(lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Recover(directory);
        var manifest = JsonFiles.Read<TransferJournal>(manifestPath);
        string account = PathPolicy.Normalize(manifest.Plan.Source);
        string transcript = Path.Combine(directory, "messages.jsonl"); PathPolicy.AssertNoLinks(transcript);
        var report = JsonFiles.Read<ChatExportResult>(Path.Combine(directory, "export-report.json"));
        if (report.CanResume) throw new InvalidOperationException("Resume the interrupted export before updating it.");
        string statePath = Path.Combine(directory, StateFile), indexPath = Path.Combine(directory, IndexFile);
        UpdateState? state = File.Exists(statePath) ? JsonFiles.Read<UpdateState>(statePath) : null;
        Dictionary<string, string>? previousDatabases = state?.Databases;
        if (state != null) {
            if (state.Version != 1 || state.Account != account || state.Platform != platform) throw new InvalidDataException("This reading copy belongs to a different account.");
        } else {
            string progressPath = Path.Combine(directory, "export-progress.json");
            string originalSource;
            if (File.Exists(progressPath)) {
                PathPolicy.AssertNoLinks(progressPath); var old = JsonFiles.Read<ChatExportProgress>(progressPath);
                if (old.Platform != platform || !old.Finished) throw new InvalidDataException("Select a completed reading copy of this account.");
                originalSource = old.Source;
            } else {
                // Older readers have no checkpoint. Their original sibling backup name is still unambiguous.
                int marker = Path.GetFileName(directory).LastIndexOf("-reading-", StringComparison.Ordinal);
                if (marker < 1 || report.Messages < 1) throw new InvalidDataException("Cannot identify the original verified backup of this older reader.");
                originalSource = Path.Combine(Path.GetDirectoryName(directory)!, Path.GetFileName(directory)[..marker]);
                var firstMessage = File.ReadLines(transcript).Select(Message).FirstOrDefault();
                if (firstMessage?.Platform != platform.ToString()) throw new InvalidDataException("Reader platform does not match the selected backup.");
            }
            string parent = Path.GetDirectoryName(originalSource) ?? throw new InvalidDataException("Missing original backup.");
            PathPolicy.AssertNoLinks(parent);
            var original = Directory.GetFiles(parent, Path.GetFileName(originalSource) + ".storloom-*.json")
                .Select(JsonFiles.Read<TransferJournal>).FirstOrDefault(j => j.Plan.Destination == originalSource && PathPolicy.Normalize(j.Plan.Source) == account);
            if (original == null) throw new InvalidDataException("Select a completed reading copy of this account.");
            previousDatabases = DatabaseHashes(original, platform);
        }
        var databaseHashes = DatabaseHashes(manifest, platform);
        if (databaseHashes.Count == 0) throw new InvalidDataException("No supported message database in the new backup.");
        var changedDatabases = databaseHashes.Where(p => previousDatabases == null || !previousDatabases.TryGetValue(p.Key, out var hash) || hash != p.Value).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(ChatVault.Exists(directory))throw new InvalidOperationException("Encrypted readers cannot be updated in place. Keep the verified raw backup and update a separate reading copy.");
        var oldIndex = ReadIndex(directory);
        ChatIdentityHistory.FreezeLegacy(oldIndex);
        var nativeKeys=oldIndex["conversationKeys"] as JsonObject??new JsonObject();oldIndex["conversationKeys"]=nativeKeys;
        var knownConversations=nativeKeys.Select(p=>p.Value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        void RememberConversation(string value){if(knownConversations.Add(value))nativeKeys[JsonFiles.Hash(value)[..24].ToLowerInvariant()]=value;}
        string workName = ".reading-update-" + Guid.NewGuid().ToString("N"), work = Path.Combine(directory, workName);
        Directory.CreateDirectory(work);
        string pendingIndex = Path.Combine(work, IndexFile);
        long added = 0, changed = 0, unchanged = 0;
        try {
            bool reuse = state != null && state.Transcript == NativeFile.ReadStamp(transcript) && File.Exists(indexPath);
            if (reuse) {
                PathPolicy.AssertNoLinks(indexPath); using var input = File.OpenRead(indexPath);
                reuse = Convert.ToHexString(TransferService.Hash(input, cancellation)) == state!.IndexSha256;
                if (reuse) File.Copy(indexPath, pendingIndex);
            }
            using (var index = new ChatUpdateIndex(pendingIndex)) {
                if (index.Query("PRAGMA quick_check").Single()[0] != "ok") throw new InvalidDataException("Invalid update index.");
                index.Execute("BEGIN");
                if (!reuse) {
                    long imported = 0;
                    foreach (string line in File.ReadLines(transcript)) {
                        OperationPause.Check(cancellation); var message = Message(line);RememberConversation(message.Conversation);
                        index.Execute("INSERT OR IGNORE INTO messages VALUES(?,?,?)", Key(message), Fingerprint(message), message.Id ?? throw new InvalidDataException("Missing archived message identity."));
                        if (++imported % 1000 == 0) progress?.Report(new("chat-update-index", (int)Math.Min(imported, int.MaxValue), 0, TotalFiles: (int)Math.Min(report.Messages, int.MaxValue)));
                    }
                }
                string delta = Path.Combine(work, "delta");
                var deltaResult = new ChatExporter(parallelism, useLocalConfiguration).ExportVerifiedBackup(manifestPath, delta, platform, secret, progress, cancellation, verifyBackup,
                    selectMessage: message => {
                        OperationPause.Check(cancellation); string key = Key(message), hash = Fingerprint(message);
                        var found = index.Query("SELECT h,id FROM messages WHERE k=?", key).SingleOrDefault();
                        if (found != null && found[0] == hash) { unchanged++; return null; }
                        string id = found?[1] ?? "u" + Guid.NewGuid().ToString("N");
                        if (found == null) added++; else changed++;
                        index.Execute("INSERT OR REPLACE INTO messages VALUES(?,?,?)", key, hash, id);
                        return message with { Id = id };
                    }, includeDatabase: relative => changedDatabases.Contains(relative));
                OperationPause.Check(cancellation);
                if (deltaResult.CanResume || (deltaResult.Databases != changedDatabases.Count) || deltaResult.Issues.Any(i => i.RuleId == "chat-export"))
                    throw new IOException("New database processing failed; the previous reading copy was retained.");
                foreach (string line in File.ReadLines(Path.Combine(delta, "messages.jsonl"))) {
                    var message = Message(line);RememberConversation(message.Conversation); index.Execute("INSERT OR REPLACE INTO changes VALUES(?,?)", Key(message), line);
                }
                index.Execute("COMMIT");
                if(downloadQQGroupAvatars&&platform==ChatPlatform.QQ){
                    var downloaded=ChatAvatars.DownloadQQGroups(knownConversations,delta,progress,cancellation,out int failures,parallelism);
                    var avatarMap=oldIndex["avatars"] as JsonObject??new JsonObject();oldIndex["avatars"]=avatarMap;
                    var conversationMap=oldIndex["conversationAvatars"] as JsonObject??new JsonObject();oldIndex["conversationAvatars"]=conversationMap;
                    foreach(var pair in downloaded){avatarMap[pair.Key]=pair.Value;conversationMap[JsonFiles.Hash(pair.Key)[..24].ToLowerInvariant()]=pair.Value;}
                    if(failures>0)progress?.Report(new("chat-avatars",downloaded.Count,0,TotalFiles:downloaded.Count+failures,Detail:"Some group avatars were unavailable."));
                }
                if (!index.Query("SELECT 1 FROM changes LIMIT 1").Any()) {
                    // The common repeat-click case does not rewrite the multi-GB transcript or any pages.
                    index.Dispose();
                    string emptyStage = Path.Combine(work, "merged"); Directory.CreateDirectory(emptyStage);
                    var latest = ReadIndex(delta); MergeMaps(oldIndex, latest); CopyAssets(delta, directory, cancellation);
                    File.WriteAllText(Path.Combine(emptyStage, "archive-index.js"), "window.archiveIndex=" + oldIndex.ToJsonString() + ";");
                    foreach (string asset in new[] { "messages.html", "reader.css", "reader.js" }) File.Copy(Path.Combine(delta, asset), Path.Combine(emptyStage, asset));
                    File.Move(pendingIndex, Path.Combine(emptyStage, IndexFile));
                    using (var stream = File.OpenRead(Path.Combine(emptyStage, IndexFile)))
                        JsonFiles.Write(Path.Combine(emptyStage, StateFile), new UpdateState(1, account, platform, NativeFile.ReadStamp(transcript), Convert.ToHexString(TransferService.Hash(stream, cancellation)), databaseHashes));
                    var noChange = new ChatReadingUpdateResult(0, 0, unchanged, 0, directory);
                    JsonFiles.Write(Path.Combine(emptyStage, "reading-update-report.json"), noChange);
                    OperationPause.Check(cancellation); Publish(directory, workName); return noChange;
                }
                string merged = Path.Combine(work, "merged"); Directory.CreateDirectory(merged);
                var affected = index.Query("SELECT data FROM changes").Select(row => Message(row[0]).Conversation).ToHashSet(StringComparer.Ordinal);
                string all = Path.Combine(work, "all-messages.jsonl"); long total = 0;
                using (var output = new StreamWriter(all, false, new UTF8Encoding(false), 1024 * 1024))
                using (var writer = new ChatArchiveWriter(manifest.Plan.Destination, merged, merged, [], [], cancellation, Path.GetFileName(account), pageParallelism: parallelism)) {
                    foreach (string line in File.ReadLines(transcript)) {
                        OperationPause.Check(cancellation); var message = Message(line);
                        var replacement = index.Query("SELECT data FROM changes WHERE k=?", Key(message)).SingleOrDefault();
                        if (replacement != null) {
                            var updated = Message(replacement[0]) with { Identity = message.Identity, IdentityVersion = message.IdentityVersion, SenderName = message.SenderName };
                            index.Execute("UPDATE changes SET data=? WHERE k=?", JsonSerializer.Serialize(updated), Key(message));
                            continue;
                        }
                        output.WriteLine(line); total++;
                        if (affected.Contains(message.Conversation)) writer.AddPrepared(message);
                        if (total % 1000 == 0) progress?.Report(new("chat-update-merge", (int)Math.Min(total, int.MaxValue), 0, TotalFiles: (int)Math.Min(report.Messages + added, int.MaxValue)));
                    }
                    foreach (var row in index.Query("SELECT data FROM changes ORDER BY k")) {
                        OperationPause.Check(cancellation); var message = Message(row[0]);
                        output.WriteLine(row[0]); total++; writer.AddPrepared(message);
                        oldIndex["messageParts"]?.AsObject().Remove(message.Id!);
                    }
                    writer.Finish(progress);
                }
                File.Move(all, Path.Combine(merged, "messages.jsonl"), true);
                var rebuilt = ReadIndex(merged); var deltaIndex = ReadIndex(delta);
                var conversations = oldIndex["conversations"]!.AsArray().ToDictionary(n => n!["id"]!.GetValue<string>(), n => n!.DeepClone());
                foreach (var item in rebuilt["conversations"]!.AsArray()) conversations[item!["id"]!.GetValue<string>()] = item.DeepClone();
                oldIndex["conversations"] = new JsonArray(conversations.Values.OrderBy(n => n["name"]!.GetValue<string>(), StringComparer.Ordinal).ToArray());
                MergeMaps(oldIndex, deltaIndex);
                oldIndex["labels"] = rebuilt["labels"]!.DeepClone();
                File.WriteAllText(Path.Combine(merged, "archive-index.js"), "window.archiveIndex=" + oldIndex.ToJsonString() + ";");
                JsonFiles.Write(Path.Combine(merged, "export-report.json"), report with { Directory = directory, Messages = total, Databases = databaseHashes.Count, Issues = report.Issues.Concat(deltaResult.Issues).Distinct().ToList(), Complete = report.Complete && (affected.Count == 0 || deltaResult.Complete), CanResume = false });
                var result = new ChatReadingUpdateResult(added, changed, unchanged, affected.Count, directory);
                JsonFiles.Write(Path.Combine(merged, "reading-update-report.json"), result);
                // Assets are immutable and addressed by their byte hash. Publishing an unused asset is harmless on cancellation.
                CopyAssets(delta, directory, cancellation);
                // Discard derived spools. Only the changed page files and common reader files are promoted.
                foreach (string spool in Directory.GetFiles(merged, "conversation-*.jsonl")) File.Delete(spool);
                index.Execute("DELETE FROM changes");
            }
            string stage = Path.Combine(work, "merged");
            File.Move(pendingIndex, Path.Combine(stage, IndexFile));
            using (var stream = File.OpenRead(Path.Combine(stage, IndexFile)))
                JsonFiles.Write(Path.Combine(stage, StateFile), new UpdateState(1, account, platform, NativeFile.ReadStamp(Path.Combine(stage, "messages.jsonl")), Convert.ToHexString(TransferService.Hash(stream, cancellation)), databaseHashes));
            OperationPause.Check(cancellation);
            Publish(directory, workName);
            return JsonFiles.Read<ChatReadingUpdateResult>(Path.Combine(directory, "reading-update-report.json"));
        } finally {
            // A pending publication must keep its rollback bytes until recovery succeeds.
            if (!File.Exists(Path.Combine(directory, PendingFile)) && Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
    private static void MergeMaps(JsonObject target, JsonObject latest) {
        foreach (string map in new[] { "builtInEmoji", "avatars", "conversationAvatars", "contactNames", "conversationNames", "conversationKeys", "identityVersions" }) {
            if (latest[map] is not JsonObject values) continue;
            if (target[map] is not JsonObject) target[map] = new JsonObject();
            foreach (var pair in values) {
                if (map == "identityVersions" && target[map]![pair.Key] != null) continue;
                target[map]![pair.Key] = pair.Value?.DeepClone();
            }
        }
        foreach(string setting in new[]{"preferenceScope","defaultPalette","defaultDark"})if(target[setting]==null&&latest[setting]!=null)target[setting]=latest[setting]!.DeepClone();
        if (latest["labels"] != null) target["labels"] = latest["labels"]!.DeepClone();
    }
    private static void CopyAssets(string delta, string directory, CancellationToken cancellation) {
                if (Directory.Exists(Path.Combine(delta, "assets"))) foreach (string asset in Directory.GetFiles(Path.Combine(delta, "assets"))) {
                    OperationPause.Check(cancellation); PathPolicy.AssertNoLinks(asset);
                    using (var stream = File.OpenRead(asset))
                        if (!Convert.ToHexString(TransferService.Hash(stream, cancellation)).Equals(Path.GetFileNameWithoutExtension(asset), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid derived resource.");
                    string target = Path.Combine(directory, "assets", Path.GetFileName(asset)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); PathPolicy.AssertNoLinks(Path.GetDirectoryName(target)!);
                    if (File.Exists(target)) { PathPolicy.AssertNoLinks(target); using var stream = File.OpenRead(target); if (!Convert.ToHexString(TransferService.Hash(stream, cancellation)).Equals(Path.GetFileNameWithoutExtension(target), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Existing resource changed."); }
                    else File.Move(asset, target);
                }
    }
    private static Dictionary<string, string> DatabaseHashes(TransferJournal journal, ChatPlatform platform) {
        var receipts = journal.Receipts.ToDictionary(r => r.RelativePath, StringComparer.OrdinalIgnoreCase);
        return ChatExporter.FindDatabases(journal.Plan.Destination, platform).Select(p => Path.GetRelativePath(journal.Plan.Destination, p)).ToDictionary(
            p => p, p => JsonFiles.Hash(new[] { p, p + "-wal", p + "-shm" }.Select(name => receipts.TryGetValue(name, out var r) ? r.Sha256 : "")), StringComparer.OrdinalIgnoreCase);
    }
    private static void Publish(string directory, string workName) {
        string work = TransferService.Child(directory, workName), stage = Path.Combine(work, "merged"), backup = Path.Combine(work, "rollback");
        var paths = Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(stage, p)).ToArray();
        var files = paths.ToDictionary(p => p, p => File.Exists(TransferService.Child(directory, p)), StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths) { PathPolicy.AssertNoLinks(TransferService.Child(stage, path)); if (files[path]) { string target = TransferService.Child(directory, path); PathPolicy.AssertNoLinks(target); if (NativeFile.ReadStamp(target).Links != 1) throw new IOException("Shared reading outputs cannot be replaced."); } }
        JsonFiles.Write(Path.Combine(directory, PendingFile), new Publication(workName, files));
        try {
            foreach (string path in paths.OrderBy(p => p == "archive-index.js" ? 1 : 0)) {
                string target = TransferService.Child(directory, path), saved = TransferService.Child(backup, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); PathPolicy.AssertNoLinks(Path.GetDirectoryName(target)!);
                if (files[path]) { Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Move(target, saved); }
                File.Move(TransferService.Child(stage, path), target);
            }
            File.Delete(Path.Combine(directory, PendingFile));
        } catch { Recover(directory); throw; }
    }
    private static void Recover(string directory) {
        string journal = Path.Combine(directory, PendingFile); if (!File.Exists(journal)) return;
        PathPolicy.AssertNoLinks(journal); var publication = JsonFiles.Read<Publication>(journal);
        if (!System.Text.RegularExpressions.Regex.IsMatch(publication.Work, "^\\.reading-update-[a-f0-9]{32}$")) throw new InvalidDataException("Invalid update recovery path.");
        string backup = Path.Combine(TransferService.Child(directory, publication.Work), "rollback");
        foreach (var pair in publication.Files.Reverse()) {
            string target = TransferService.Child(directory, pair.Key), saved = TransferService.Child(backup, pair.Key);
            if (File.Exists(target)) PathPolicy.AssertNoLinks(target);
            if (File.Exists(saved)) { PathPolicy.AssertNoLinks(saved); if (File.Exists(target)) File.Delete(target); File.Move(saved, target); }
            else if (!pair.Value && File.Exists(target)) File.Delete(target);
        }
        File.Delete(journal);
        string abandoned = TransferService.Child(directory, publication.Work);
        if (Directory.Exists(abandoned)) { PathPolicy.AssertNoLinks(abandoned); Directory.Delete(abandoned, true); }
    }
}
