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
    private sealed record Conversation(string Id, string Name, string File, long Count, int Pages = 0);
    private readonly string source, destination, work;
    private readonly string account;
    private readonly Dictionary<string, string> contacts;
    private readonly Dictionary<string, TransferReceipt>? verified;
    private readonly CancellationToken cancellation;
    private readonly StreamWriter json;
    private readonly Dictionary<string, Conversation> conversations = [];
    private readonly Dictionary<string, StreamWriter> writers = [];
    private readonly Dictionary<string, List<string>> media = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChatMedia?> resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly WeChatImages images;
    private long messageId;
    public int MediaFiles { get; private set; }
    public int UnresolvedMedia { get; private set; }
    public int UnsupportedMessages { get; private set; }
    public ChatArchiveWriter(string source, string destination, string work, Dictionary<string, string> contacts,
        Dictionary<string, TransferReceipt>? verified, CancellationToken cancellation, string account, bool useLocalImageConfiguration = false) {
        this.source = source; this.destination = destination; this.work = work; this.contacts = contacts;
        this.verified = verified; this.cancellation = cancellation;
        this.account = account;
        images = new WeChatImages(account, source, useLocalImageConfiguration);
        json = new StreamWriter(Path.Combine(destination, "messages.jsonl"), false, new UTF8Encoding(false), 128 * 1024);
        IEnumerable<string> files = verified?.Keys ?? EnumerateMediaFiles(source);
        foreach (string relative in files) {
            cancellation.ThrowIfCancellationRequested();
            if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".dat", ".mp4", ".webm", ".mp3", ".wav", ".ogg", ".silk" }
                .Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase) && !Regex.IsMatch(Path.GetFileName(relative), "^[a-fA-F0-9]{32}$")) continue;
            foreach (string key in new[] { Path.GetFileName(relative), Path.GetFileNameWithoutExtension(relative) }.Distinct(StringComparer.OrdinalIgnoreCase)) {
                if (!media.TryGetValue(key, out var values)) media[key] = values = [];
                values.Add(relative);
            }
        }
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
        original = MessagePresentation.ResolveSenderPrefix(original);
        string peer = original.Conversation.StartsWith("direct:") ? original.Conversation[7..] :
            original.Conversation.StartsWith("group:") ? original.Conversation[6..] : original.Conversation;
        string name = contacts.GetValueOrDefault(peer, peer);
        var message = original with { Id = "m" + ++messageId, ConversationName = name, SenderName = contacts.GetValueOrDefault(original.Sender, original.SenderName ?? original.Sender),
            IsSelf = original.IsSelf || original.Sender == account || account.StartsWith(original.Sender + "_", StringComparison.Ordinal) };
        var attachments = new List<ChatMedia>();
        var parts = MessagePresentation.Parse(message);
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
            if (p.Kind is "image" or "video" or "audio" or "sticker" or "file") {
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
        json.WriteLine(JsonSerializer.Serialize(message));
        string id = JsonFiles.Hash(message.Conversation)[..24].ToLowerInvariant();
        if (!conversations.TryGetValue(id, out var conversation))
            conversations[id] = conversation = new(id, name, Path.Combine(work, "conversation-" + id + ".jsonl"), 0);
        conversations[id] = conversation with { Count = conversation.Count + 1 };
        if (!writers.TryGetValue(id, out var writer)) {
            if (writers.Count >= 24) { var oldest = writers.First(); oldest.Value.Dispose(); writers.Remove(oldest.Key); }
            writers[id] = writer = new StreamWriter(conversation.File, true, new UTF8Encoding(false), 32 * 1024);
        }
        // Raw data remains available behind an explicit collapsed source disclosure in the reader.
        writer.WriteLine(JsonSerializer.Serialize(message));
    }
    private ChatMedia? ResolveMedia(string key) {
        var candidates = new[] { key + "_h.dat", key + "_W.dat", key, key + ".dat", key + ".jpg", key + ".png", key + ".gif", key + ".webp", key + ".mp4", key + "_t.dat", key + "_t_W.dat", key + "_thumb.jpg" }
            .Where(media.ContainsKey).SelectMany(k => media[k]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0) return null;
        // Prefer the full image over its thumbnail; never guess between ambiguous full-size files.
        foreach (var group in candidates.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Thumbnail(g.First()) ? 2 : Path.GetFileNameWithoutExtension(g.First()).EndsWith("_h", StringComparison.OrdinalIgnoreCase) ? 0 : 1)) {
            if (group.Count() > 1 && (verified == null || group.Select(p => verified[p].Sha256).Distinct().Count() != 1)) continue;
            var result = ResolveFile(key, group.First()); if (result != null) return result;
        }
        return null;
    }
    private static bool Thumbnail(string path) => Regex.IsMatch(Path.GetFileNameWithoutExtension(path), "(_t|_t_W|_thumb)$", RegexOptions.IgnoreCase);
    private ChatMedia? ResolveFile(string key, string relative) {
        string path = TransferService.Child(source, relative); PathPolicy.AssertNoLinks(path);
        if (Path.GetExtension(path).Equals(".silk", StringComparison.OrdinalIgnoreCase)) return null;
        string originalExtension = Path.GetExtension(path).ToLowerInvariant();
        if (originalExtension is ".mp4" or ".webm" or ".mp3" or ".wav" or ".ogg") return CopyStreaming(key, relative, path, originalExtension);
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
        string assets = Path.Combine(destination, "assets"); Directory.CreateDirectory(assets); PathPolicy.AssertNoLinks(assets);
        string filename = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + extension;
        string target = Path.Combine(assets, filename);
        if (!File.Exists(target)) { using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write); output.Write(data); MediaFiles++; }
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
            return new("assets/" + filename, extension is ".mp4" or ".webm" ? "video" : "audio", Path.GetFileName(relative)) { Key = key };
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Finish(IProgress<ScanProgress>? progress) {
        foreach (var writer in writers.Values) writer.Dispose(); writers.Clear(); json.Flush();
        Directory.CreateDirectory(Path.Combine(destination, "conversations")); PathPolicy.AssertNoLinks(Path.Combine(destination, "conversations")); int done = 0;
        foreach (var entry in conversations.Values.ToArray()) {
            cancellation.ThrowIfCancellationRequested();
            // External merge keeps memory bounded for conversations with millions of messages.
            var runs = new List<string>(); var batch = new List<ChatMessage>(4000);
            void WriteRun() {
                string path = entry.File + ".run-" + runs.Count; runs.Add(path);
                using var output = new StreamWriter(path);
                foreach (var message in batch.OrderBy(m => m.Timestamp)) output.WriteLine(JsonSerializer.Serialize(message)); batch.Clear();
            }
            foreach (string line in File.ReadLines(entry.File)) { cancellation.ThrowIfCancellationRequested();
                batch.Add(JsonSerializer.Deserialize<ChatMessage>(line)!); if (batch.Count >= 4000) WriteRun(); }
            if (batch.Count > 0) WriteRun();
            var readers = runs.Select(File.OpenText).ToArray(); var queue = new PriorityQueue<(ChatMessage Message, int Run), (long Time, int Run)>();
            void Next(int index) { string? line = readers[index].ReadLine(); if (line != null) { var message = JsonSerializer.Deserialize<ChatMessage>(line)!; queue.Enqueue((message, index), (message.Timestamp, index)); } }
            int page = 0;
            using var references = new StreamWriter(Path.Combine(destination, "conversations", entry.Id + "-refs.js"), false, new UTF8Encoding(false));
            references.Write("window.receiveReferences(" + JsonSerializer.Serialize(entry.Id) + ",{"); bool firstReference = true;
            void Reference(string? key, ChatMessage message) {
                if (string.IsNullOrEmpty(key) || key == "0" || key == "seq:0") return;
                if (!firstReference) references.Write(','); firstReference = false;
                references.Write(JsonSerializer.Serialize(key) + ":" + JsonSerializer.Serialize(new { page, id = message.Id }));
            }
            void WritePage() {
                File.WriteAllText(Path.Combine(destination, "conversations", $"{entry.Id}-{page++}.js"),
                    "window.receivePage(" + JsonSerializer.Serialize(entry.Id) + "," + (page - 1) + "," + JsonSerializer.Serialize(batch) + ");", new UTF8Encoding(false)); batch.Clear();
            }
            try {
                for (int i = 0; i < readers.Length; i++) Next(i);
                while (queue.TryDequeue(out var item, out _)) { cancellation.ThrowIfCancellationRequested();
                    Reference(item.Message.ServerId, item.Message); if (item.Message.Sequence != null) Reference("seq:" + item.Message.Sequence, item.Message);
                    batch.Add(item.Message); Next(item.Run); if (batch.Count >= 300) WritePage(); }
                if (batch.Count > 0) WritePage();
            } finally { foreach (var reader in readers) reader.Dispose(); }
            references.Write("});");
            conversations[entry.Id] = entry with { Pages = page };
            File.Delete(entry.File); foreach (string run in runs) File.Delete(run);
            progress?.Report(new("chat-pages", ++done, 0, TotalFiles: conversations.Count, Detail: entry.Name));
        }
        var labels = L.Keys.Where(k => k.StartsWith("Reader", StringComparison.Ordinal)).Append("ChatArchive")
            .ToDictionary(k => k, k => L.T(k));
        File.WriteAllText(Path.Combine(destination, "archive-index.js"), "window.archiveIndex=" + JsonSerializer.Serialize(new {
            language = L.Language, labels, unresolvedMedia = UnresolvedMedia, unsupportedMessages = UnsupportedMessages,
            conversations = conversations.Values.OrderBy(c => c.Name).Select(c => new { id = c.Id, name = c.Name, count = c.Count, pages = c.Pages }) }) + ";");
        foreach (string asset in new[] { "messages.html", "reader.css", "reader.js" }) {
            using var stream = typeof(ChatArchiveWriter).Assembly.GetManifestResourceStream("Storloom.Core.Chats.Reader." + asset) ?? throw new IOException("Missing reader asset.");
            using var output = File.Create(Path.Combine(destination, asset)); stream.CopyTo(output);
        }
    }
    public void Dispose() { foreach (var writer in writers.Values) writer.Dispose(); writers.Clear(); json.Dispose(); images.Dispose(); }
}
