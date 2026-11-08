using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Storloom.Core.Localization;

namespace Storloom.Core.Chats;

public sealed record ChatMedia(string Path, string Kind, string Name) {
    public string? Key { get; init; }
    public string? Quality { get; init; }
}
internal sealed class ChatArchiveWriter : IDisposable {
    private sealed record Conversation(string Id, string Name, string File, long Count, int Pages = 0,
        bool? Ordered = null, long? LastTimestamp = null);
    private readonly string source, destination, work;
    private readonly string account;
    private readonly Dictionary<string, string> contacts;
    private readonly Dictionary<string, TransferReceipt>? verified;
    private readonly CancellationToken cancellation;
    private readonly ExportAppendFile json;
    private readonly StringBuilder jsonBuffer = new();
    private readonly Dictionary<string, Conversation> conversations = [];
    private readonly Dictionary<string, StringBuilder> buffers = [];
    private readonly Dictionary<string, ExportAppendFile> spools = [];
    private readonly Dictionary<string, string> conversationIds = [];
    private readonly Dictionary<string, ExportFileProgress[]> generated = [];
    private long bufferedCharacters;
    private DateTime checkpointTime = DateTime.UtcNow;
    private readonly bool resumable;
    private readonly int pageParallelism;
    private string ProgressPath => Path.Combine(work, "reader-progress.json");
    private sealed record WriterProgress(long Messages, int Media, int Missing, int Unsupported,
        Conversation[] Conversations, ExportFileProgress Main, ExportFileProgress[] Spools,
        Dictionary<string, ExportFileProgress[]> Generated, Dictionary<string,string>? Emoji = null);
    public long ProcessedMessages => messageId;
    internal int SpoolWrites { get; private set; }
    private readonly Dictionary<string, List<string>> media = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChatMedia?> resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly WeChatImages images;
    private readonly BuiltInEmoji emoji;
    private readonly WeChatVoices? voices;
    private long messageId;
    private bool adding;
    public int MediaFiles { get; private set; }
    public int UnresolvedMedia { get; private set; }
    public int UnsupportedMessages { get; private set; }
    public ChatArchiveWriter(string source, string destination, string work, Dictionary<string, string> contacts,
        Dictionary<string, TransferReceipt>? verified, CancellationToken cancellation, string account, bool useLocalImageConfiguration = false, bool resumable = false, int pageParallelism = 1, WeChatVoices? voices = null) {
        this.voices = voices;
        this.pageParallelism = pageParallelism is >= 1 and <= 4 ? pageParallelism : throw new ArgumentOutOfRangeException(nameof(pageParallelism));
        source = PathPolicy.Normalize(source); destination = PathPolicy.Normalize(destination); work = PathPolicy.Normalize(work);
        this.source = source; this.destination = destination; this.work = work; this.contacts = contacts;
        this.verified = verified; this.cancellation = cancellation;
        this.account = account;
        this.resumable = resumable;
        images = new WeChatImages(account, source, useLocalImageConfiguration);
        emoji = new BuiltInEmoji(source,destination,useLocalImageConfiguration);
        WriterProgress? previous = resumable && File.Exists(ProgressPath) ? JsonFiles.Read<WriterProgress>(ProgressPath) : null;
        if (previous != null) {
            emoji.Restore(previous.Emoji);
            if (previous.Main.Path != "messages.jsonl") throw new InvalidDataException("Invalid reader checkpoint output.");
            if (previous.Messages < 0 || previous.Conversations.Sum(c => c.Count) != previous.Messages)
                throw new InvalidDataException("Invalid reader checkpoint counters.");
            foreach (var proof in previous.Spools.Append(previous.Main)) ExportProgressFiles.Validate(destination, proof, true, cancellation);
            foreach (var proof in previous.Generated.Values.SelectMany(p => p)) ExportProgressFiles.Validate(destination, proof, false, cancellation);
            string assets = Path.Combine(destination, "assets");
            if (Directory.Exists(assets)) foreach (string path in Directory.EnumerateFiles(assets)) {
                cancellation.ThrowIfCancellationRequested(); PathPolicy.AssertNoLinks(path);
                if (Regex.IsMatch(Path.GetFileName(path), "^\\.pending-[a-f0-9]{32}$")) { File.Delete(path); continue; }
                string digest = Path.GetFileNameWithoutExtension(path);
                using var input = File.OpenRead(path);
                if (!Regex.IsMatch(digest, "^[a-f0-9]{64}$") || Convert.ToHexString(TransferService.Hash(input, cancellation)).ToLowerInvariant() != digest)
                    throw new InvalidDataException("Exported media changed; resume refused.");
            }
            messageId = previous.Messages; MediaFiles = previous.Media; UnresolvedMedia = previous.Missing; UnsupportedMessages = previous.Unsupported;
            if (Directory.Exists(assets)) MediaFiles = Directory.EnumerateFiles(assets).Count();
            foreach (var c in previous.Conversations) {
                if (!Regex.IsMatch(c.Id, "^[a-f0-9]{24}$") || c.File != "conversation-" + c.Id + ".jsonl")
                    throw new InvalidDataException("Invalid reader checkpoint conversation.");
                conversations[c.Id] = c with { File = Path.Combine(work, c.File) };
            }
            foreach (var p in previous.Generated) generated[p.Key] = p.Value;
            foreach (var proof in previous.Spools) {
                string path = TransferService.Child(destination, proof.Path);
                string id = Path.GetFileNameWithoutExtension(path)["conversation-".Length..];
                if (!conversations.ContainsKey(id) || path != conversations[id].File) throw new InvalidDataException("Invalid reader spool checkpoint.");
                spools[id] = new ExportAppendFile(path, proof);
            }
            var committedPaths = spools.Values.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(work, "conversation-*.jsonl"))
                if (!committedPaths.Contains(path)) { PathPolicy.AssertNoLinks(path); File.Delete(path); }
        }
        json = new ExportAppendFile(Path.Combine(destination, "messages.jsonl"), previous?.Main);
        if (previous == null) json.Append([]);
        IEnumerable<string> files = verified?.Keys ?? EnumerateMediaFiles(source);
        foreach (string relative in files) {
            cancellation.ThrowIfCancellationRequested();
            if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".dat", ".mp4", ".webm", ".mp3", ".wav", ".ogg", ".silk", ".m4a", ".pdf", ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".zip", ".7z", ".rar", ".txt", ".md", ".csv" }
                .Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase) && !Regex.IsMatch(Path.GetFileName(relative), "^[a-fA-F0-9]{32}$")) continue;
            foreach (string key in new[] { Path.GetFileName(relative), Path.GetFileNameWithoutExtension(relative) }.Distinct(StringComparer.OrdinalIgnoreCase)) {
                if (!media.TryGetValue(key, out var values)) media[key] = values = [];
                values.Add(relative);
            }
        }
        if (resumable && previous == null) SaveProgress();
    }
    private static IEnumerable<string> EnumerateMediaFiles(string root) {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? directory)) {
            PathPolicy.AssertNoLinks(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                var attr = File.GetAttributes(path); if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                if ((attr & FileAttributes.Directory) != 0) pending.Push(path); else yield return Path.GetRelativePath(root, path);
            }
        }
    }
    public void Add(ChatMessage original) {
        cancellation.ThrowIfCancellationRequested();
        adding = true;
        original = MessagePresentation.ResolveSenderPrefix(original);
        string peer = original.Conversation.StartsWith("direct:") ? original.Conversation[7..] :
            original.Conversation.StartsWith("group:") ? original.Conversation[6..] : original.Conversation;
        string name = contacts.GetValueOrDefault(original.Conversation, contacts.GetValueOrDefault(peer, original.ConversationName ?? peer));
        var message = original with { Id = "m" + (messageId + 1), ConversationName = name, SenderName = contacts.GetValueOrDefault(original.Conversation + "/" + original.Sender, contacts.GetValueOrDefault(original.Sender, original.SenderName ?? original.Sender)),
            IsSelf = original.IsSelf || original.Sender == account || account.StartsWith(original.Sender + "_", StringComparison.Ordinal) };
        var attachments = new List<ChatMedia>();
        var parts = MessagePresentation.Parse(message);
        if (parts.Any(p => p.Kind == "audio") && voices?.Resolve(message, cancellation) is byte[] wave) {
            string voiceKey = "voice:" + WeChatVoices.Identity(message.Conversation, message.ServerId!);
            string filename = Convert.ToHexString(SHA256.HashData(wave)).ToLowerInvariant() + ".wav";
            string assets = Path.Combine(destination, "assets"); Directory.CreateDirectory(assets); PathPolicy.AssertNoLinks(assets);
            string path = Path.Combine(assets, filename); if (File.Exists(path)) PathPolicy.AssertNoLinks(path);
            if (!File.Exists(path)) { File.WriteAllBytes(path, wave); MediaFiles++; }
            else { using var existing = File.OpenRead(path); if (!SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(wave))) throw new InvalidDataException("Exported voice changed."); }
            resolved[voiceKey] = new("assets/" + filename, "audio", "voice.wav") { Key = voiceKey };
            parts = parts.Select(p => p.Kind == "audio" ? p with { MediaKeys = [.. p.MediaKeys ?? [], voiceKey] } : p).ToArray();
        }
        parts = emoji.NormalizeParts(message.Platform,parts);
        int emojiBefore=emoji.CreatedFiles;emoji.UseParts(message.Platform,parts);MediaFiles+=emoji.CreatedFiles-emojiBefore;
        if (parts.Count(p => p.Kind is "image" or "video" or "audio" or "sticker") == 1 && message.LocalMediaKeys.Length > 0)
            parts = parts.Select(p => p.Kind is "image" or "video" or "audio" or "sticker" ? p with { MediaKeys = [.. p.MediaKeys ?? [], .. message.LocalMediaKeys] } : p).ToArray();
        var keys = Regex.Matches(message.Text, "(?:md5|cdnthumbmd5)=[\"']([a-fA-F0-9]{32})[\"']")
            .Select(m => m.Groups[1].Value).ToList();
        IEnumerable<MessagePart> Flatten(IEnumerable<MessagePart> items) { foreach (var p in items) { yield return p; foreach (var child in Flatten(p.Forwarded.SelectMany(f => f.Parts))) yield return child; } }
        keys.AddRange(Flatten(parts).SelectMany(p => p.MediaKeys ?? []));
        if (message.Text.StartsWith("[attachment] ", StringComparison.Ordinal)) keys.Add(Path.GetFileName(message.Text[13..].Trim()));
        foreach (string key in keys.Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (!resolved.TryGetValue(key, out ChatMedia? item)) { item = ResolveMedia(key); resolved[key] = item; }
            if (item != null) attachments.Add(item);
        }
        bool missing = false, unsupported = false;
        MessagePart State(MessagePart p) {
            if (p.Kind == "system") p = p with { Text = Regex.Replace(p.Text, "wxid_[A-Za-z0-9_-]+", match => contacts.GetValueOrDefault(match.Value, match.Value)) };
            if (p.Kind is "unsupported" or "unparsed" || p.Kind == "forward" && p.Forwarded.Length == 0) unsupported = true;
            if (p.Kind is "image" or "video" or "audio" or "sticker" or "file" && !p.Inline) {
                var available = (p.MediaKeys ?? []).Select(k => resolved.GetValueOrDefault(k)).OfType<ChatMedia>().ToArray();
                if (available.Length == 0 || available.All(m => m.Quality == "thumbnail")) missing = true;
                p = p with { MediaState = available.Length > 0 ? "available" :
                    (p.MediaKeys ?? []).Any(k => media.ContainsKey(k) || media.ContainsKey(k + ".dat") || media.ContainsKey(k + "_h.dat")) ? "unsupported-format" : "missing" };
            }
            return p with { Forwarded = p.Forwarded.Select(f => f with { Parts = f.Parts.Select(State).ToArray() }).ToArray() };
        }
        parts = parts.Select(State).ToArray();
        if (missing) UnresolvedMedia++; if (unsupported) UnsupportedMessages++;
        message = message with { Media = attachments.ToArray(), Parts = parts };
        string line = JsonSerializer.Serialize(message);
        jsonBuffer.AppendLine(line);
        if (jsonBuffer.Length >= 512 * 1024) FlushJson();
        if (!conversationIds.TryGetValue(message.Conversation, out string? id))
            conversationIds[message.Conversation] = id = JsonFiles.Hash(message.Conversation)[..24].ToLowerInvariant();
        if (!conversations.TryGetValue(id, out var conversation))
            conversations[id] = conversation = new(id, name, Path.Combine(work, "conversation-" + id + ".jsonl"), 0, Ordered: true);
        conversations[id] = conversation with { Count = conversation.Count + 1,
            Ordered = conversation.Ordered == true && (conversation.LastTimestamp == null || message.Timestamp >= conversation.LastTimestamp),
            LastTimestamp = message.Timestamp };
        if (!buffers.TryGetValue(id, out var buffer)) buffers[id] = buffer = new StringBuilder();
        int before = buffer.Length; buffer.AppendLine(line); bufferedCharacters += buffer.Length - before;
        if (buffer.Length >= 512 * 1024) FlushSpool(id);
        while (bufferedCharacters >= 8 * 1024 * 1024) FlushSpool(buffers.MaxBy(b => b.Value.Length).Key);
        messageId++;
        adding = false;
        if (resumable && DateTime.UtcNow - checkpointTime >= TimeSpan.FromSeconds(30)) SaveProgress();
    }
    private void FlushJson() { if (jsonBuffer.Length == 0) return; json.Append(Encoding.UTF8.GetBytes(jsonBuffer.ToString())); jsonBuffer.Clear(); }
    private void FlushSpool(string id) {
        if (!buffers.TryGetValue(id, out var buffer) || buffer.Length == 0) return;
        if (!spools.TryGetValue(id, out var file)) spools[id] = file = new ExportAppendFile(conversations[id].File);
        file.Append(Encoding.UTF8.GetBytes(buffer.ToString())); SpoolWrites++;
        bufferedCharacters -= buffer.Length; buffers.Remove(id);
    }
    internal void SaveProgress() {
        FlushJson(); foreach (string id in buffers.Keys.ToArray()) FlushSpool(id);
        if (resumable) JsonFiles.Write(ProgressPath, new WriterProgress(messageId, MediaFiles, UnresolvedMedia, UnsupportedMessages,
            conversations.Values.Select(c => c with { File = Path.GetFileName(c.File) }).ToArray(), json.Proof(destination), spools.Values.Select(f => f.Proof(destination)).ToArray(), generated,new(emoji.Exported)));
        checkpointTime = DateTime.UtcNow;
    }
    internal void SaveSafeProgress() { if (!adding) SaveProgress(); }
    internal ChatMedia? ResolveMedia(string key) {
        var candidates = new[] { key + "_h.dat", key + "_W.dat", key, key + ".dat", key + ".jpg", key + ".png", key + ".gif", key + ".webp", key + ".mp4", key + "_t.dat", key + "_t_W.dat", key + "_thumb.jpg" }
            .Where(media.ContainsKey).SelectMany(k => media[k]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0) return null;
        // Prefer the full image over its thumbnail; never guess between ambiguous full-size files.
        foreach (var group in candidates.GroupBy(p => Path.GetFileName(p) + "/" + Thumbnail(p), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Thumbnail(g.First()) ? 2 : Path.GetFileNameWithoutExtension(g.First()).EndsWith("_h", StringComparison.OrdinalIgnoreCase) ? 0 : 1)) {
            if (group.Count() > 1) {
                string Digest(string relative) {
                    if (verified != null) return verified[relative].Sha256;
                    string path = TransferService.Child(source, relative); PathPolicy.AssertNoLinks(path);
                    using var input = File.OpenRead(path); return Convert.ToHexString(TransferService.Hash(input, cancellation));
                }
                if (group.Select(Digest).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() != 1) continue;
            }
            var result = ResolveFile(key, group.First()); if (result != null) return result;
        }
        return null;
    }
    private static bool Thumbnail(string path) => Regex.IsMatch(Path.GetFileNameWithoutExtension(path), "(_t|_t_W|_thumb)$", RegexOptions.IgnoreCase) ||
        path.Replace('\\', '/').Split('/').Any(segment => segment.Equals("Thumb", StringComparison.OrdinalIgnoreCase) || segment.Equals("ThumbStore", StringComparison.OrdinalIgnoreCase));
    private ChatMedia? ResolveFile(string key, string relative) {
        string path = TransferService.Child(source, relative); PathPolicy.AssertNoLinks(path);
        if (Path.GetExtension(path).Equals(".silk", StringComparison.OrdinalIgnoreCase)) return null;
        string originalExtension = Path.GetExtension(path).ToLowerInvariant();
        if (originalExtension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".dat" or "")) return CopyStreaming(key, relative, path, originalExtension);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 128 * 1024 * 1024) return null;
        var data = new byte[(int)input.Length]; input.ReadExactly(data);
        if (verified != null && (!verified.TryGetValue(relative, out var receipt) ||
            Convert.ToHexString(SHA256.HashData(data)) != receipt.Sha256)) throw new InvalidDataException("Archived media changed after verification.");
        string extension = Path.GetExtension(path).ToLowerInvariant(); string kind = "image";
        if (extension is ".dat" or "") {
            byte[]? decoded = images.Decode(data, out extension); if (decoded == null) return null; data = decoded;
        } else if (extension is ".mp4" or ".webm") kind = "video";
        else if (extension is ".mp3" or ".wav" or ".ogg") kind = "audio";
        if(kind=="image")extension=WeChatImages.Format(data)??extension;
        if (extension == ".wxgf") {
            byte[]? converted = WeChatAnimation.Convert(data, cancellation);
            if (converted == null) return null;
            data = converted; extension = ".gif";
        }
        string assets = Path.Combine(destination, "assets"); Directory.CreateDirectory(assets); PathPolicy.AssertNoLinks(assets);
        string filename = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + extension;
        string target = Path.Combine(assets, filename);
        if (!File.Exists(target)) { using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write); output.Write(data); MediaFiles++; }
        else { PathPolicy.AssertNoLinks(target); using var existing = File.OpenRead(target); if (Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant() != Path.GetFileNameWithoutExtension(target)) throw new InvalidDataException("Exported media changed; resume refused."); }
        return new("assets/" + filename, kind, Path.GetFileName(relative)) { Key = key, Quality = Thumbnail(relative) ? "thumbnail" : null };
    }
    private ChatMedia CopyStreaming(string key, string relative, string path, string extension) {
        string assets = Path.Combine(destination, "assets"); Directory.CreateDirectory(assets); PathPolicy.AssertNoLinks(assets);
        string temporary = Path.Combine(assets, ".pending-" + Guid.NewGuid().ToString("N"));
        try {
            string digest;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)) {
                var buffer = new byte[1024 * 1024]; int length;
                while ((length = input.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, length); output.Write(buffer, 0, length); }
                digest = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (verified != null && (!verified.TryGetValue(relative, out var receipt) || digest != receipt.Sha256))
                throw new InvalidDataException("Archived media changed after verification.");
            string filename = digest.ToLowerInvariant() + extension, target = Path.Combine(assets, filename);
            if (!File.Exists(target)) { File.Move(temporary, target); MediaFiles++; }
            else { PathPolicy.AssertNoLinks(target); using var existing = File.OpenRead(target); if (Convert.ToHexString(TransferService.Hash(existing, cancellation)) != digest) throw new InvalidDataException("Exported media changed; resume refused."); }
            return new("assets/" + filename, extension is ".mp4" or ".webm" ? "video" : extension is ".mp3" or ".wav" or ".ogg" or ".m4a" ? "audio" : "file", Path.GetFileName(relative)) { Key = key };
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Finish(IProgress<ScanProgress>? progress) {
        SaveProgress();
        try { FinishCore(progress); }
        catch { SaveProgress(); throw; }
        SaveProgress();
    }
    private void FinishCore(IProgress<ScanProgress>? progress) {
        Directory.CreateDirectory(Path.Combine(destination, "conversations")); PathPolicy.AssertNoLinks(Path.Combine(destination, "conversations")); int done = 0;
        object checkpointGate = new();
        var entries = conversations.Values.ToArray();
        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = pageParallelism, CancellationToken = cancellation }, entry => {
            cancellation.ThrowIfCancellationRequested();
            bool alreadyGenerated; lock (checkpointGate) alreadyGenerated = generated.ContainsKey(entry.Id);
            if (alreadyGenerated) { lock (checkpointGate) progress?.Report(new("chat-pages", ++done, 0, TotalFiles: entries.Length)); return; }
            // External merge keeps memory bounded for conversations with millions of messages.
            var runs = new List<string>(); var batch = new List<ChatMessage>(4000);
            ChatMessage[]? sorted = null;
            void WriteRun() {
                string path = entry.File + ".run-" + runs.Count; runs.Add(path);
                using var output = new StreamWriter(path);
                foreach (var message in batch.OrderBy(m => m.Timestamp)) output.WriteLine(JsonSerializer.Serialize(message)); batch.Clear();
            }
            // Most single-database QQ conversations already arrive in chronological order.
            // Older checkpoints have no ordering proof and safely use the sorting path.
            if (entry.Ordered != true) {
                foreach (string line in File.ReadLines(entry.File)) { cancellation.ThrowIfCancellationRequested();
                    batch.Add(JsonSerializer.Deserialize<ChatMessage>(line)!); if (batch.Count >= 4000) WriteRun(); }
                if (runs.Count == 0) { sorted = batch.OrderBy(m => m.Timestamp).ToArray(); batch.Clear(); }
                else if (batch.Count > 0) WriteRun();
            }
            var readers = runs.Select(File.OpenText).ToArray(); var queue = new PriorityQueue<(ChatMessage Message, int Run), (long Time, int Run)>();
            void Next(int index) { string? line = readers[index].ReadLine(); if (line != null) { var message = JsonSerializer.Deserialize<ChatMessage>(line)!; queue.Enqueue((message, index), (message.Timestamp, index)); } }
            IEnumerable<ChatMessage> OrderedMessages() {
                if (entry.Ordered == true) {
                    long? previous = null;
                    foreach (string line in File.ReadLines(entry.File)) {
                        cancellation.ThrowIfCancellationRequested();
                        var message = JsonSerializer.Deserialize<ChatMessage>(line) ?? throw new InvalidDataException("Invalid conversation spool.");
                        if (previous != null && message.Timestamp < previous) throw new InvalidDataException("Conversation ordering checkpoint is inconsistent.");
                        previous = message.Timestamp; yield return message;
                    }
                } else if (sorted != null) { foreach (var message in sorted) yield return message; }
                else {
                    for (int i = 0; i < readers.Length; i++) Next(i);
                    while (queue.TryDequeue(out var item, out _)) { yield return item.Message; Next(item.Run); }
                }
            }
            int page = 0;
            string referencesPath = Path.Combine(destination, "conversations", entry.Id + "-refs.js");
            PrepareOutput(referencesPath);
            using var references = new StreamWriter(referencesPath, false, new UTF8Encoding(false));
            references.Write("window.receiveReferences(" + JsonSerializer.Serialize(entry.Id) + ",{"); bool firstReference = true;
            void Reference(string? key, ChatMessage message) {
                if (string.IsNullOrEmpty(key) || key == "0" || key == "seq:0") return;
                if (!firstReference) references.Write(','); firstReference = false;
                references.Write(JsonSerializer.Serialize(key) + ":" + JsonSerializer.Serialize(new { page, id = message.Id }));
            }
            var proofs = new List<ExportFileProgress>();
            void WritePage() {
                string path = Path.Combine(destination, "conversations", $"{entry.Id}-{page++}.js"); PrepareOutput(path);
                File.WriteAllText(path,
                    "window.receivePage(" + JsonSerializer.Serialize(entry.Id) + "," + (page - 1) + "," + JsonSerializer.Serialize(batch) + ");", new UTF8Encoding(false)); batch.Clear();
                if (resumable) proofs.Add(ExportProgressFiles.Proof(destination, path));
            }
            try {
                foreach (var message in OrderedMessages()) { cancellation.ThrowIfCancellationRequested();
                    Reference(message.ServerId, message); if (message.Sequence != null) Reference("seq:" + message.Sequence, message);
                    batch.Add(message); if (batch.Count >= 300) WritePage(); }
                if (batch.Count > 0) WritePage();
            } finally { foreach (var reader in readers) reader.Dispose(); }
            references.Write("});");
            foreach (string run in runs) File.Delete(run);
            references.Flush(); references.Close();
            if (resumable) proofs.Add(ExportProgressFiles.Proof(destination, referencesPath));
            lock (checkpointGate) {
                conversations[entry.Id] = entry with { Pages = page };
                if (resumable) generated[entry.Id] = proofs.ToArray();
                if (DateTime.UtcNow - checkpointTime >= TimeSpan.FromSeconds(30)) SaveProgress();
                progress?.Report(new("chat-pages", ++done, 0, TotalFiles: entries.Length, Detail: entry.Name));
            }
        });
        var labels = L.Keys.Where(k => k.StartsWith("Reader", StringComparison.Ordinal)).Append("ChatArchive")
            .ToDictionary(k => k, k => L.T(k));
        File.WriteAllText(Path.Combine(destination, "archive-index.js"), "window.archiveIndex=" + JsonSerializer.Serialize(new {
            language = L.Language, labels, unresolvedMedia = UnresolvedMedia, unsupportedMessages = UnsupportedMessages, builtInEmoji = emoji.Exported,
            conversations = conversations.Values.OrderBy(c => c.Name).Select(c => new { id = c.Id, name = c.Name, count = c.Count, pages = c.Pages }) }) + ";");
        foreach (string asset in new[] { "messages.html", "reader.css", "reader.js" }) {
            using var stream = typeof(ChatArchiveWriter).Assembly.GetManifestResourceStream("Storloom.Core.Chats.Reader." + asset) ?? throw new IOException("Missing reader asset.");
            using var output = File.Create(Path.Combine(destination, asset)); stream.CopyTo(output);
        }
    }
    private static void PrepareOutput(string path) {
        PathPolicy.AssertNoLinks(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) {
            PathPolicy.AssertNoLinks(path);
            if (NativeFile.ReadStamp(path).Links != 1) throw new IOException("Shared output files cannot be overwritten.");
        }
    }
    public void Dispose() { foreach (var file in spools.Values) file.Dispose(); json.Dispose(); images.Dispose(); }
}
