using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Storloom.Core.Chats;

public static class ChatReaderRepair {
    public static int RestoreResources(string directory, string source, CancellationToken cancellation = default,
        IProgress<ScanProgress>? progress = null, bool includeLocalConfiguration = false) {
        directory = PathPolicy.Normalize(directory); source = PathPolicy.Normalize(source);
        PathPolicy.AssertNoLinks(directory); PathPolicy.AssertNoLinks(source);
        var report = JsonFiles.Read<ChatExportResult>(Path.Combine(directory, "export-report.json"));
        if (report.CanResume) throw new InvalidOperationException("Finish the interrupted export before repairing its resources.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, ChatMedia>(StringComparer.OrdinalIgnoreCase);
        var reusable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool CanReuse(ChatMedia media) {
            if (reusable.TryGetValue(media.Path, out bool valid)) return valid;
            string path = TransferService.Child(directory, media.Path);
            valid = media.Quality != "thumbnail" && media.Path.StartsWith("assets/", StringComparison.Ordinal) && File.Exists(path);
            if (valid) {
                PathPolicy.AssertNoLinks(path); using var stream = File.OpenRead(path);
                valid = Convert.ToHexString(TransferService.Hash(stream, cancellation)).Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase);
            }
            reusable[media.Path] = valid; return valid;
        }
        string indexText = File.ReadAllText(Path.Combine(directory, "archive-index.js"));
        if (!indexText.StartsWith("window.archiveIndex=", StringComparison.Ordinal)) throw new InvalidDataException("Unsupported reader index.");
        var oldIndex = JsonNode.Parse(indexText["window.archiveIndex=".Length..].TrimEnd().TrimEnd(';'))!;
        if (oldIndex["resourceMedia"]?.Deserialize<Dictionary<string, ChatMedia>>() is {} previous)
            foreach (var pair in previous) if (CanReuse(pair.Value)) resources[pair.Key] = pair.Value;
        void Collect(IEnumerable<MessagePart> parts) {
            foreach (var part in parts) {
                foreach (string key in part.MediaKeys ?? []) keys.Add(key);
                foreach (var entry in part.Forwarded) Collect(entry.Parts);
            }
        }
        foreach (string line in File.ReadLines(Path.Combine(directory, "messages.jsonl"))) {
            cancellation.ThrowIfCancellationRequested();
            var message = JsonSerializer.Deserialize<ChatMessage>(line) ?? throw new InvalidDataException("Invalid archived message.");
            foreach (var media in message.Media) if (media.Key != null && CanReuse(media)) resources.TryAdd(media.Key, media);
            Collect((message.Id is {} id ? oldIndex["messageParts"]?[id]?.Deserialize<MessagePart[]>() : null)??message.Parts);
        }
        string stage = Path.Combine(directory, ".resource-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try {
            using (var resolver = new ChatArchiveWriter(source, stage, stage, [], null, cancellation,
                Path.GetFileName(source).StartsWith("wxid_", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(source) : Directory.GetParent(source)!.Name, includeLocalConfiguration)) {
                int done = 0;
                foreach (string key in keys) {
                    cancellation.ThrowIfCancellationRequested();
                    ChatMedia? resource = resources.ContainsKey(key) ? null : resolver.ResolveMedia(key);
                    if (resource != null) resources[key] = resource;
                    progress?.Report(new("chat-resource-repair", ++done, 0, 0, Detail: null, TotalFiles: keys.Count));
                }
            }
            string assets = Path.Combine(directory, "assets"); Directory.CreateDirectory(assets); PathPolicy.AssertNoLinks(assets);
            foreach (string path in Directory.Exists(Path.Combine(stage, "assets")) ? Directory.EnumerateFiles(Path.Combine(stage, "assets")) : []) {
                cancellation.ThrowIfCancellationRequested();
                string target = Path.Combine(assets, Path.GetFileName(path)); PathPolicy.AssertNoLinks(path);
                if (!File.Exists(target)) File.Move(path, target);
                else {
                    PathPolicy.AssertNoLinks(target);
                    using var old = File.OpenRead(target); using var fresh = File.OpenRead(path);
                    if (!SHA256.HashData(old).AsSpan().SequenceEqual(SHA256.HashData(fresh))) throw new InvalidDataException("Repaired asset conflicts with existing content.");
                }
            }
            Refresh(directory, resourceMedia: resources);
            return resources.Count;
        } finally { PathPolicy.AssertNoLinks(stage); Directory.Delete(stage, true); }
    }
    // Refresh presentation and optional resource associations without rewriting messages or pages.
    public static string Refresh(string directory, IReadOnlyDictionary<string, string>? contacts = null, string? emojiSource = null, bool includeInstalledEmoji = false, IReadOnlyDictionary<string, ChatMedia>? resourceMedia = null, IReadOnlyDictionary<string,string>? avatars = null, IReadOnlyDictionary<string,MessagePart[]>? messageParts = null) {
        directory = PathPolicy.Normalize(directory); PathPolicy.AssertNoLinks(directory);
        string reportPath = Path.Combine(directory, "export-report.json");
        var report = JsonFiles.Read<ChatExportResult>(reportPath);
        if (report.CanResume) throw new InvalidOperationException("An interrupted export must finish before its reader is refreshed.");
        string lockPath = Path.Combine(directory, ".export.lock");
        if (File.Exists(lockPath)) PathPolicy.AssertNoLinks(lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string indexPath = Path.Combine(directory, "archive-index.js"); PathPolicy.AssertNoLinks(indexPath);
        string text = File.ReadAllText(indexPath); const string prefix = "window.archiveIndex=";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.TrimEnd().EndsWith(';')) throw new InvalidDataException("Unsupported reader index.");
        var index = JsonNode.Parse(text[prefix.Length..].Trim().TrimEnd(';'))?.AsObject() ?? throw new InvalidDataException("Invalid reader index.");
        if(messageParts!=null){var parts=index["messageParts"] as JsonObject??new JsonObject();foreach(var pair in messageParts)parts[pair.Key]=JsonSerializer.SerializeToNode(pair.Value);index["messageParts"]=parts;}
        if(avatars!=null){
            var mapped=index["avatars"] as JsonObject??new JsonObject();
            foreach(var pair in avatars){if(!System.Text.RegularExpressions.Regex.IsMatch(pair.Value,"^assets/[a-f0-9]{64}\\.(png|jpg|jpeg|gif|webp|bmp)$"))throw new InvalidDataException("Invalid avatar image.");string path=TransferService.Child(directory,pair.Value);PathPolicy.AssertNoLinks(path);if(!File.Exists(path))throw new FileNotFoundException("Avatar image is missing.");mapped[pair.Key]=pair.Value;}
            index["avatars"]=mapped;
            var ids=index["conversations"]!.AsArray().Select(c=>c!["id"]!.GetValue<string>()).ToHashSet();var conversationAvatars=new Dictionary<string,string>();
            foreach(var pair in mapped){foreach(string native in new[]{pair.Key,"direct:"+pair.Key,"group:"+pair.Key}){string id=JsonFiles.Hash(native)[..24].ToLowerInvariant();if(ids.Contains(id))conversationAvatars[id]=pair.Value!.GetValue<string>();}}
            index["conversationAvatars"]=JsonSerializer.SerializeToNode(conversationAvatars);
        }
        if (resourceMedia != null) {
            var resources = index["resourceMedia"] as JsonObject ?? new JsonObject();
            foreach (var pair in resourceMedia) {
                string asset = TransferService.Child(directory, pair.Value.Path);
                if (!pair.Value.Path.StartsWith("assets/", StringComparison.Ordinal) || !File.Exists(asset)) throw new InvalidDataException("Invalid repaired resource.");
                PathPolicy.AssertNoLinks(asset);
                resources[pair.Key] = JsonSerializer.SerializeToNode(pair.Value);
            }
            index["resourceMedia"] = resources;
        }
        if(emojiSource!=null){var emoji=new BuiltInEmoji(emojiSource,directory,includeInstalledEmoji);emoji.ExportAll();index["builtInEmoji"]=JsonSerializer.SerializeToNode(emoji.Exported);}
        string language = index["language"]?.GetValue<string>() ?? "en";
        if (language is not ("en" or "zh-CN")) throw new InvalidDataException("Unsupported reader language.");
        var assembly = typeof(ChatMessage).Assembly;
        using (var input = assembly.GetManifestResourceStream("Storloom.Core.Localization.Locales." + language + ".json")!) {
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(input)!;
            index["labels"] = JsonSerializer.SerializeToNode(labels.Where(p => p.Key.StartsWith("Reader", StringComparison.Ordinal) || p.Key == "ChatArchive").ToDictionary(p => p.Key, p => p.Value));
        }
        if (contacts != null) {
            index["contactNames"] = JsonSerializer.SerializeToNode(contacts);
            var ids = index["conversations"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()).ToHashSet();
            var names = new Dictionary<string, string>();
            foreach (var pair in contacts) {
                if (pair.Key.Contains('/')) continue;
                string conversation = pair.Key.StartsWith("group:", StringComparison.Ordinal) ? pair.Key : "direct:" + pair.Key;
                string id = JsonFiles.Hash(conversation)[..24].ToLowerInvariant(); if (ids.Contains(id)) names[id] = pair.Value;
            }
            index["conversationNames"] = JsonSerializer.SerializeToNode(names);
        }
        var replacements = new Dictionary<string, byte[]>();
        foreach (string name in new[] { "messages.html", "reader.js", "reader.css" }) {
            using var input = assembly.GetManifestResourceStream("Storloom.Core.Chats.Reader." + name)!;
            using var output = new MemoryStream(); input.CopyTo(output); replacements[name] = output.ToArray();
        }
        replacements["archive-index.js"] = Encoding.UTF8.GetBytes(prefix + index.ToJsonString() + ";");
        replacements["export-report.json"] = JsonSerializer.SerializeToUtf8Bytes(report with { Contacts = contacts?.Count ?? report.Contacts }, JsonFiles.Options);
        foreach (string name in replacements.Keys) {
            string path = Path.Combine(directory, name); PathPolicy.AssertNoLinks(path);
            if (NativeFile.ReadStamp(path).Links != 1) throw new IOException("Shared reader files cannot be replaced.");
        }
        string backup = Path.Combine(directory, ".reader-backup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(backup);
        foreach (string name in replacements.Keys) File.Copy(Path.Combine(directory, name), Path.Combine(backup, name));
        var changed = new List<string>();
        try {
            foreach (var pair in replacements) { Replace(Path.Combine(directory, pair.Key), pair.Value); changed.Add(pair.Key); }
            JsonFiles.Write(Path.Combine(backup, "repair.json"), new { Version = 1, UpdatedUtc = DateTimeOffset.UtcNow,
                Contacts = contacts?.Count, Files = replacements.ToDictionary(p => p.Key, p => Convert.ToHexString(SHA256.HashData(p.Value))) });
        } catch {
            foreach (string name in changed) Replace(Path.Combine(directory, name), File.ReadAllBytes(Path.Combine(backup, name)));
            throw;
        }
        return backup;
    }
    private static void Replace(string path, byte[] bytes) {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) { output.Write(bytes); output.Flush(true); } File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
