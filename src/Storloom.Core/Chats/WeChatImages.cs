using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Storloom.Core.Chats;

// Format references and upstream attribution are listed in docs/chat-exporters.md.
internal sealed class WeChatImages : IDisposable {
    private readonly List<(byte[] Aes, byte Xor)> keys = [];
    private readonly List<byte[]> emoticonKeys = [];
    public WeChatImages(string account, string source, bool useLocalConfiguration = false) {
        if (!account.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase)) return;
        string wxid = Regex.Replace(account, "^(wxid_[^_]+)(?:_.+)$", "$1");
        string[] roots = useLocalConfiguration ? [source, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tencent", "xwechat"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tencent", "xwechat")] : [source];
        var codes = new HashSet<uint>();
        foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase)) {
            try {
                var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0)); int visited = 0;
                while (pending.TryPop(out var item) && visited++ < 2000) {
                    if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0) continue;
                    if (Path.GetFileName(item.Path).Equals("kvcomm", StringComparison.OrdinalIgnoreCase)) {
                        foreach (string file in Directory.EnumerateFiles(item.Path, "key_*.statistic").Take(256)) {
                            var match = Regex.Match(Path.GetFileName(file), "^key_([0-9]+)_.+\\.statistic$", RegexOptions.IgnoreCase);
                            if (match.Success && uint.TryParse(match.Groups[1].Value, out uint code) && code > 0 && codes.Add(code)) {
                                keys.Add((Encoding.ASCII.GetBytes(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(code.ToString(System.Globalization.CultureInfo.InvariantCulture) + wxid))).ToLowerInvariant()[..16]), (byte)code));
                                emoticonKeys.Add(MD5.HashData(Encoding.UTF8.GetBytes(code.ToString(System.Globalization.CultureInfo.InvariantCulture)+wxid+"EMOTICON")));
                            }
                        }
                    } else if (item.Depth < 4) foreach (string directory in Directory.EnumerateDirectories(item.Path)) {
                        if (new[] { "msg", "attach", "FileStorage", "db_storage" }.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)) continue;
                        pending.Push((directory, item.Depth + 1));
                    }
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    public byte[]? Decode(byte[] data, out string extension) {
        extension = Format(data) ?? ""; if (extension.Length > 0) return data;
        if(data.Length>=16 && data.Length%16==0)foreach(byte[] key in emoticonKeys) {
            try {using var aes=Aes.Create();aes.Key=key;byte[] plain=aes.DecryptCbc(data,key,PaddingMode.PKCS7);if(Format(plain) is string format){extension=format;return plain;}}
            catch(CryptographicException){ }
        }
        if (data.AsSpan().StartsWith(new byte[] { 7, 8, 0x56, 0x32, 8, 7 })) {
            if (data.Length < 31) return null;
            uint head = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(6)), tail = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10));
            long aligned = (long)head + 16 - head % 16;
            if (head == 0 || aligned > data.Length - 15 || tail > data.Length - 15 - aligned) return null;
            foreach (var key in keys) {
                try {
                    using var aes = Aes.Create(); aes.Key = key.Aes;
                    byte[] prefix = aes.DecryptEcb(data.AsSpan(15, (int)aligned), PaddingMode.PKCS7);
                    if (prefix.Length != head || Format(prefix) == null) continue;
                    byte[] plain = new byte[prefix.Length + data.Length - 15 - (int)aligned]; prefix.CopyTo(plain, 0);
                    data.AsSpan(15 + (int)aligned).CopyTo(plain.AsSpan(prefix.Length));
                    for (int i = plain.Length - (int)tail; i < plain.Length; i++) plain[i] ^= key.Xor;
                    extension = Format(plain)!; return plain;
                } catch (CryptographicException) { }
            }
            return null;
        }
        if (data.AsSpan().StartsWith(new byte[] { 7, 8, 0x56, 0x31, 8, 7 })) {
            byte[] digest = MD5.HashData("0"u8);
            foreach (byte[] key in new[] { digest, Encoding.ASCII.GetBytes(Convert.ToHexString(digest).ToLowerInvariant()[..16]) }) {
                try {
                    using var aes = Aes.Create(); aes.Key = key;
                    byte[] plain = aes.DecryptEcb(data.AsSpan(6), PaddingMode.None);
                    if (Format(plain) is string format) { extension = format; return plain; }
                } catch (CryptographicException) { }
            }
            return null;
        }
        foreach (byte first in new byte[] { 0xff, 0x89, 0x47, 0x42, 0x52 }) {
            if (data.Length < 8) return null;
            byte xor = (byte)(data[0] ^ first); var probe = data[..Math.Min(16, data.Length)].Select(b => (byte)(b ^ xor)).ToArray();
            if (Format(probe) is not string format) continue;
            extension = format; return data.Select(b => (byte)(b ^ xor)).ToArray();
        }
        return null;
    }
    internal static string? Format(ReadOnlySpan<byte> data) => data.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) ? ".jpg" :
        data.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10 }) ? ".png" :
        data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8) ? ".gif" :
        data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..].StartsWith("WEBP"u8) ? ".webp" :
        data.StartsWith("wxgf"u8) ? ".wxgf" : null;
    public void Dispose() { foreach (var key in keys) CryptographicOperations.ZeroMemory(key.Aes); keys.Clear();foreach(byte[] key in emoticonKeys)CryptographicOperations.ZeroMemory(key);emoticonKeys.Clear(); }
}
