using System.Globalization;
using System.Security.Cryptography;

namespace Storloom.Core.Chats;

internal sealed class WeChatVoices : IDisposable {
    private readonly Dictionary<string, byte[]> payloads = new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguous = new(StringComparer.Ordinal);
    internal static string Identity(string conversation, string serverId) => conversation + "/" + serverId;
    public WeChatVoices(string source, string work, byte[]? secret, CancellationToken cancellation) {
        foreach (string database in Directory.EnumerateFiles(source, "media_*.db", SearchOption.AllDirectories)) {
            OperationPause.Check(cancellation); PathPolicy.AssertNoLinks(database);
            string temporary = Path.Combine(work, "voice-" + Guid.NewGuid().ToString("N") + ".db");
            try {
                bool shifted = ChatCipher.DecryptDatabase(database, temporary, secret ?? [], ChatPlatform.WeChat, cancellation);
                using var reader = new SqliteReader(temporary, shifted);
                var names = reader.Query("SELECT rowid,user_name FROM Name2Id", cancellation).ToDictionary(r => Convert.ToInt64(r[0]), r => r[1]?.ToString() ?? "");
                foreach (var row in reader.Query("SELECT chat_name_id,svr_id,voice_data FROM VoiceInfo", cancellation)) {
                    if (!names.TryGetValue(Convert.ToInt64(row[0]), out string? peer) || peer.Length == 0 || row[2] is not byte[] bytes || bytes.Length == 0) continue;
                    string id = unchecked((ulong)Convert.ToInt64(row[1])).ToString(CultureInfo.InvariantCulture); if (id == "0") continue;
                    string key = Identity(peer, id);
                    if (payloads.TryGetValue(key, out var previous) && !previous.AsSpan().SequenceEqual(bytes)) ambiguous.Add(key);
                    else payloads[key] = bytes;
                }
            } catch (Exception ex) when (ex is CryptographicException or InvalidDataException) { /* Missing resources remain reported by the message writer. */ }
            finally { foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(temporary + suffix)) File.Delete(temporary + suffix); }
        }
    }
    public byte[]? Resolve(ChatMessage message, CancellationToken cancellation) {
        if (message.ServerId == null) return null;
        string key = Identity(message.Conversation, message.ServerId);
        return !ambiguous.Contains(key) && payloads.TryGetValue(key, out var bytes) ? SilkAudio.Decode(bytes, cancellation) : null;
    }
    public void Dispose() { foreach (byte[] bytes in payloads.Values) CryptographicOperations.ZeroMemory(bytes); payloads.Clear(); }
}
