using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Storloom.Core.Chats;

public static class ChatReaderRepair {
    // Refresh presentation only. Messages, reference maps and media remain untouched.
    public static string Refresh(string directory, IReadOnlyDictionary<string, string>? contacts = null, string? emojiSource = null, bool includeInstalledEmoji = false) {
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
