using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Storloom.Core.Localization;
using Storloom.Core.Chats.NativeCapture.QQ;

namespace Storloom.Core.Chats;

public sealed record QQCaptureTarget(string Executable, string Wrapper, uint FunctionRva, byte[] CodePrefix);

public static class QQKeyCapture {
    public static QQCaptureTarget Inspect(string executable) {
        executable = PathPolicy.Normalize(executable);
        if (!Path.GetFileName(executable).Equals("QQ.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("QQSelectExecutable"));
        WeChatKeyCapture.ValidateTencentSignature(executable);
        string root = Path.GetDirectoryName(executable)!;
        var candidates = new List<string>();
        void Add(string path) { if (File.Exists(path)) candidates.Add(path); }
        Add(Path.Combine(root, "resources", "app", "wrapper.node"));
        foreach (string directory in new[] { Path.Combine(root, "versions"), Path.Combine(root, "resources", "app", "versions") }) {
            if (!Directory.Exists(directory)) continue;
            PathPolicy.AssertNoLinks(directory);
            foreach (string version in Directory.GetDirectories(directory)) {
                PathPolicy.AssertNoLinks(version);
                Add(Path.Combine(version, "resources", "app", "wrapper.node")); Add(Path.Combine(version, "wrapper.node"));
            }
        }
        var paths = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? direct = paths.FirstOrDefault(p => p.Equals(Path.Combine(root, "resources", "app", "wrapper.node"), StringComparison.OrdinalIgnoreCase));
        string versionText = FileVersionInfo.GetVersionInfo(executable).FileVersion?.Split(' ')[0] ?? "";
        var matching = paths.Where(p => Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar).Any(segment => segment.Replace('-', '.') == versionText)).ToArray();
        string wrapper = direct ?? (matching.Length == 1 ? matching[0] : paths.Length == 1 ? paths[0] : throw new InvalidDataException(L.T("QQSelectExecutable")));
        PathPolicy.AssertNoLinks(wrapper); WeChatKeyCapture.ValidateTencentSignature(wrapper);
        var function = QQKeyLocator.Find(File.ReadAllBytes(wrapper));
        return new(executable, wrapper, function.Rva, function.Prefix);
    }

    public static byte[] Capture(ChatLocation location, string executable, CancellationToken cancellation = default, IProgress<ChatDiagnostic>? diagnostics = null) {
        if (location.Platform != ChatPlatform.QQ) throw new InvalidOperationException("QQ capture requires a QQ archive.");
        if (CleanupEngine.RunningProcesses(new CleanupRule { Processes = ["QQ"] }).Length > 0) throw new IOException(L.T("ClientMustClose", "QQ"));
        var clock = Stopwatch.StartNew();
        if(ChatDiagnostic.Enabled(diagnostics))diagnostics!.Report(new("qq.capture.inspect"));
        var target = Inspect(executable);
        if(ChatDiagnostic.Enabled(diagnostics))diagnostics!.Report(new("qq.capture.signature-and-function-verified", ElapsedSeconds: clock.Elapsed.TotalSeconds));
        using var input = new FileStream(location.Database, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] prefix = new byte[Math.Min(input.Length, 5120)]; input.ReadExactly(prefix);
        cancellation.ThrowIfCancellationRequested();
        var extractor = new KeyExtractor(target.Executable, target.FunctionRva, target.Wrapper, target.CodePrefix,
            candidate => ChatCipher.Detect(prefix, candidate, ChatPlatform.QQ) != null,
            (stage, count) => {if(ChatDiagnostic.Enabled(diagnostics))diagnostics!.Report(new(stage, Processes: 1, Candidates: count, ElapsedSeconds: clock.Elapsed.TotalSeconds));}, 90);
        string found = extractor.ExtractKey();
        byte[] key = string.IsNullOrEmpty(found) ? [] : Encoding.ASCII.GetBytes(found);
        if (cancellation.IsCancellationRequested) { CryptographicOperations.ZeroMemory(key); cancellation.ThrowIfCancellationRequested(); }
        if (key.Length is not (16 or 32) || ChatCipher.Detect(prefix, key, ChatPlatform.QQ) == null) {
            CryptographicOperations.ZeroMemory(key); throw new CryptographicException(L.T("QQCaptureFailed"));
        }
        return key;
    }
}

// Resolve the string reference to an enclosing x64 runtime function. No version-specific fixed RVA.
internal static class QQKeyLocator {
    internal sealed record Function(uint Rva, byte[] Prefix);
    public static Function Find(byte[] image) {
        try {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            var headers = pe.PEHeaders;
            if (headers.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus) throw new InvalidDataException("QQ capture requires an x64 PE image.");
            var text = headers.SectionHeaders.SingleOrDefault(s => s.Name == ".text");
            var data = headers.SectionHeaders.SingleOrDefault(s => s.Name == ".rdata");
            int Offset(int rva, int length) {
                if (rva < 0 || length < 0) throw new InvalidDataException("Invalid QQ image range.");
                foreach (var section in headers.SectionHeaders) {
                    long delta = (long)rva - section.VirtualAddress;
                    long position = section.PointerToRawData + delta;
                    if (delta >= 0 && delta + length <= section.SizeOfRawData && position >= 0 && position + length <= image.Length) return (int)position;
                }
                throw new InvalidDataException("QQ image range is outside its sections.");
            }
            if (text.SizeOfRawData < 16 || data.SizeOfRawData == 0) throw new InvalidDataException("QQ image is missing its code or string section.");
            var strings = image.AsSpan(Offset(data.VirtualAddress, data.SizeOfRawData), data.SizeOfRawData);
            ReadOnlySpan<byte> anchor = "nt_sqlite3_key_v2: db=%p zDb=%s"u8;
            int marker = strings.IndexOf(anchor);
            if (marker < 0 || strings[(marker + anchor.Length)..].IndexOf(anchor) >= 0) throw new InvalidDataException("QQ key function marker is missing or ambiguous.");
            int target = checked(data.VirtualAddress + marker);
            int codeOffset = Offset(text.VirtualAddress, text.SizeOfRawData);
            var exceptions = headers.PEHeader.ExceptionTableDirectory;
            if (exceptions.Size == 0 || exceptions.Size % 12 != 0) throw new InvalidDataException("QQ image has no valid x64 function table.");
            int table = Offset(exceptions.RelativeVirtualAddress, exceptions.Size);
            var functions = new HashSet<uint>();
            for (int i = 0; i <= text.SizeOfRawData - 7; i++) {
                var instruction = image.AsSpan(codeOffset + i, 7);
                if ((instruction[0] & 0xf8) != 0x48 || instruction[1] != 0x8d || (instruction[2] & 0xc7) != 0x05) continue;
                int rva = checked(text.VirtualAddress + i);
                if ((long)rva + 7 + BinaryPrimitives.ReadInt32LittleEndian(instruction[3..]) != target) continue;
                var enclosing = new List<uint>();
                for (int entry = 0; entry < exceptions.Size; entry += 12) {
                    uint start = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(table + entry, 4));
                    uint end = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(table + entry + 4, 4));
                    if (start <= rva && rva < end) enclosing.Add(start);
                }
                if (enclosing.Count != 1) throw new InvalidDataException("QQ key reference has no unambiguous enclosing function.");
                functions.Add(enclosing[0]);
            }
            if (functions.Count != 1) throw new InvalidDataException("QQ key function is missing or ambiguous.");
            uint function = functions.Single();
            return new(function, image.AsSpan(Offset(checked((int)function), 16), 16).ToArray());
        } catch (Exception ex) when (ex is BadImageFormatException or OverflowException or ArgumentException or InvalidOperationException) {
            throw new InvalidDataException("Unsupported QQ key function layout.", ex);
        }
    }
}
