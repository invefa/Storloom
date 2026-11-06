using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Storloom.Core.Chats;

public enum ChatPlatform { WeChat, QQ }
public sealed record CipherParameters(int Wrapper, int Reserve, HashAlgorithmName Kdf,
    HashAlgorithmName Mac, int Iterations, bool RawKey);

// SQLCipher format and QQ compatibility informed by chatlog-keeper (MIT).
// See THIRD-PARTY-NOTICES.md. No Python runtime or upstream executable is used.
public static class ChatCipher {
    public const int PageSize = 4096;
    private static readonly byte[] Header = "SQLite format 3\0"u8.ToArray();

    public static CipherParameters? Detect(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> secret,
        ChatPlatform platform) {
        var candidates = new List<CipherParameters>();
        if (platform == ChatPlatform.WeChat) {
            candidates.Add(new(0, 80, HashAlgorithmName.SHA512, HashAlgorithmName.SHA512, 256000, true));
            candidates.Add(new(0, 80, HashAlgorithmName.SHA512, HashAlgorithmName.SHA512, 256000, false));
            candidates.Add(new(0, 48, HashAlgorithmName.SHA1, HashAlgorithmName.SHA1, 64000, false));
        } else {
            foreach (int wrapper in new[] { 1024, 0 })
                foreach (var kdf in new[] { HashAlgorithmName.SHA512, HashAlgorithmName.SHA1 })
                    foreach (int reserve in new[] { 48, 64, 80 }) {
                        candidates.Add(new(wrapper, reserve, kdf, HashAlgorithmName.SHA1, 4000, false));
                        if (reserve == 80) candidates.Add(new(wrapper, reserve, kdf,
                            HashAlgorithmName.SHA512, 4000, false));
                    }
        }
        foreach (var parameters in candidates) {
            if (prefix.Length < parameters.Wrapper + PageSize) continue;
            byte[] page = prefix.Slice(parameters.Wrapper, PageSize).ToArray();
            byte[] key = Derive(secret, page.AsSpan(0, 16), parameters);
            try {
                _ = DecryptPage(page, key, page.AsSpan(0, 16), 1, parameters);
                return parameters;
            } catch (CryptographicException) { }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        return null;
    }

    public static byte[] Derive(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> salt,
        CipherParameters parameters) => parameters.RawKey ? secret.ToArray() :
        Rfc2898DeriveBytes.Pbkdf2(secret, salt, parameters.Iterations, parameters.Kdf, 32);

    public static byte[] DecryptPage(ReadOnlySpan<byte> page, ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> salt, uint pageNumber, CipherParameters parameters) {
        if (page.Length != PageSize || key.Length != 32 || salt.Length != 16 || pageNumber == 0)
            throw new CryptographicException("Invalid encrypted page.");
        if (pageNumber == 1 && !page[..16].SequenceEqual(salt))
            throw new CryptographicException("Page-one salt differs from the authenticated database.");
        int start = pageNumber == 1 ? 16 : 0;
        int end = PageSize - parameters.Reserve;
        int digestSize = parameters.Mac == HashAlgorithmName.SHA512 ? 64 : 20;
        byte[] macSalt = salt.ToArray();
        for (int i = 0; i < macSalt.Length; i++) macSalt[i] ^= 0x3a;
        byte[] macKey = Rfc2898DeriveBytes.Pbkdf2(key, macSalt, 2, parameters.Kdf, 32);
        try {
            byte[] authenticated = new byte[end - start + 16 + 4];
            page.Slice(start, end - start + 16).CopyTo(authenticated);
            BinaryPrimitives.WriteUInt32LittleEndian(authenticated.AsSpan(authenticated.Length - 4), pageNumber);
            byte[] actual = parameters.Mac == HashAlgorithmName.SHA512 ?
                HMACSHA512.HashData(macKey, authenticated) : HMACSHA1.HashData(macKey, authenticated);
            if (!CryptographicOperations.FixedTimeEquals(actual, page.Slice(end + 16, digestSize)))
                throw new CryptographicException($"Encrypted page authentication failed: {pageNumber}");
            using var aes = Aes.Create();
            aes.Key = key.ToArray();
            byte[] plain = new byte[PageSize];
            aes.DecryptCbc(page.Slice(start, end - start), page.Slice(end, 16),
                plain.AsSpan(start, end - start), PaddingMode.None);
            page[end..].CopyTo(plain.AsSpan(end));
            if (pageNumber == 1) Header.CopyTo(plain, 0);
            return plain;
        } finally { CryptographicOperations.ZeroMemory(macKey); }
    }

    public static bool DecryptDatabase(string source, string destination, byte[] secret,
        ChatPlatform platform, CancellationToken cancellation = default) {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] prefix = new byte[Math.Min(input.Length, 1024 + PageSize)];
        input.ReadExactly(prefix);
        int plainOffset = prefix.AsSpan().StartsWith(Header) ? 0 :
            platform == ChatPlatform.QQ && prefix.Length >= 1040 && prefix.AsSpan(1024).StartsWith(Header) ? 1024 : -1;
        if (plainOffset >= 0) {
            input.Position = plainOffset;
            using var copy = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            input.CopyTo(copy);
            copy.Flush(true);
            foreach (string suffix in new[] { "-wal", "-shm" })
                if (File.Exists(source + suffix)) File.Copy(source + suffix, destination + suffix);
            return false;
        }
        var parameters = Detect(prefix, secret, platform) ??
            throw new CryptographicException("Key does not authenticate this database or its format is unsupported.");
        input.Position = parameters.Wrapper;
        byte[] page = new byte[PageSize];
        input.ReadExactly(page);
        byte[] salt = page.AsSpan(0, 16).ToArray();
        byte[] key = Derive(secret, salt, parameters);
        bool shiftedLockPage = false;
        try {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite);
            uint number = 1;
            while (true) {
                cancellation.ThrowIfCancellationRequested();
                if (IsQqLockPlaceholder(page, number, parameters.Wrapper, platform)) {
                    output.Write(page); shiftedLockPage = true;
                } else output.Write(DecryptPage(page, key, salt, number, parameters));
                number++;
                if (input.Position == input.Length) break;
                input.ReadExactly(page);
            }
            ApplyWal(source + "-wal", output, key, salt, parameters, cancellation);
            output.Flush(true);
            return shiftedLockPage;
        } catch {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        } finally { CryptographicOperations.ZeroMemory(key); }
    }

    // Only this exact Windows NTQQ wrapper-induced lock page is exempt from HMAC.
    public static bool IsQqLockPlaceholder(ReadOnlySpan<byte> page, uint number, int wrapper,
        ChatPlatform platform) => platform == ChatPlatform.QQ && wrapper == 1024 && number == 262144 &&
        page.Length == PageSize && page.IndexOfAnyExcept((byte)0) < 0;

    private static void ApplyWal(string path, FileStream output, byte[] key, byte[] salt,
        CipherParameters parameters, CancellationToken cancellation) {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
        using var wal = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] header = new byte[32];
        wal.ReadExactly(header);
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is not (0x377f0682 or 0x377f0683) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) != 3007000 ||
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) != PageSize)
            throw new InvalidDataException("Unsupported WAL header.");
        bool big = magic == 0x377f0683;
        var checksum = Checksum(header.AsSpan(0, 24), (0, 0), big);
        if (checksum.Item1 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24)) ||
            checksum.Item2 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28)))
            throw new InvalidDataException("WAL header checksum mismatch.");
        byte[] frame = new byte[24 + PageSize];
        long commitEnd = 0;
        uint commitPages = 0;
        while (wal.Position + frame.Length <= wal.Length) {
            cancellation.ThrowIfCancellationRequested();
            wal.ReadExactly(frame);
            if (!frame.AsSpan(8, 8).SequenceEqual(header.AsSpan(16, 8))) break;
            checksum = Checksum(frame.AsSpan(0, 8), checksum, big);
            checksum = Checksum(frame.AsSpan(24), checksum, big);
            if (checksum.Item1 != BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(16)) ||
                checksum.Item2 != BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(20)))
                throw new InvalidDataException("WAL frame checksum mismatch; export refused.");
            uint pageNumber = BinaryPrimitives.ReadUInt32BigEndian(frame);
            if (pageNumber == 0) throw new InvalidDataException("Invalid WAL page number.");
            _ = DecryptPage(frame.AsSpan(24), key, salt, pageNumber, parameters);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4));
            if (size != 0) { commitEnd = wal.Position; commitPages = size; }
        }
        if (commitEnd == 0) return;
        wal.Position = 32;
        while (wal.Position < commitEnd) {
            cancellation.ThrowIfCancellationRequested();
            wal.ReadExactly(frame);
            uint pageNumber = BinaryPrimitives.ReadUInt32BigEndian(frame);
            if (pageNumber > commitPages) continue;
            output.Position = ((long)pageNumber - 1) * PageSize;
            output.Write(DecryptPage(frame.AsSpan(24), key, salt, pageNumber, parameters));
        }
        output.SetLength((long)commitPages * PageSize);
    }

    private static (uint, uint) Checksum(ReadOnlySpan<byte> bytes, (uint A, uint B) seed, bool big) {
        uint a = seed.A, b = seed.B;
        for (int i = 0; i < bytes.Length; i += 8) {
            uint x = big ? BinaryPrimitives.ReadUInt32BigEndian(bytes[i..]) :
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]);
            uint y = big ? BinaryPrimitives.ReadUInt32BigEndian(bytes[(i + 4)..]) :
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i + 4)..]);
            a = unchecked(a + x + b);
            b = unchecked(b + y + a);
        }
        return (a, b);
    }
}
