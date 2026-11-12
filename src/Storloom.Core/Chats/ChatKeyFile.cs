using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Storloom.Core.Chats;

/// <summary>Explicit, password-protected custody. Each file has independent salt and nonce.</summary>
public static class ChatKeyFile {
    private const int Iterations = 600_000;
    public sealed record Envelope(int Version, ChatPlatform Platform, byte[] Salt, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
    private static byte[] Aad(ChatPlatform platform) => Encoding.ASCII.GetBytes("Storloom.DatabaseKey.v1." + platform);
    public static void Save(string path, ReadOnlySpan<byte> secret, ChatPlatform platform, ReadOnlySpan<char> password) {
        if (password.Length < 8) throw new ArgumentException("Use at least 8 password characters.");
        if (secret.Length is not (16 or 32)) throw new InvalidDataException("Invalid database key length.");
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] wrapping = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        byte[] cipher = new byte[secret.Length], tag = new byte[16];
        try {
            using var aes = new AesGcm(wrapping, 16); aes.Encrypt(nonce, secret, cipher, tag, Aad(platform));
            PathPolicy.AssertNoLinks(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, new Envelope(1, platform, salt, nonce, cipher, tag), JsonFiles.Options); output.Flush(true);
        } finally { CryptographicOperations.ZeroMemory(wrapping); }
    }
    public static byte[] Open(string path, ChatPlatform platform, ReadOnlySpan<char> password) {
        PathPolicy.AssertNoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4096) throw new InvalidDataException("Invalid protected key file.");
        var item = JsonSerializer.Deserialize<Envelope>(input, JsonFiles.Options) ?? throw new InvalidDataException("Invalid protected key file.");
        if (item.Version != 1 || item.Platform != platform || item.Salt is not { Length: 16 } || item.Nonce is not { Length: 12 } ||
            item.Tag is not { Length: 16 } || item.Ciphertext is not { Length: 16 or 32 }) throw new InvalidDataException("Invalid protected key file.");
        byte[] wrapping = Rfc2898DeriveBytes.Pbkdf2(password, item.Salt, Iterations, HashAlgorithmName.SHA256, 32);
        byte[] secret = new byte[item.Ciphertext.Length];
        try { using var aes = new AesGcm(wrapping, 16); aes.Decrypt(item.Nonce, item.Ciphertext, item.Tag, secret, Aad(platform)); return secret; }
        catch { CryptographicOperations.ZeroMemory(secret); throw; }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }
}
