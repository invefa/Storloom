using Storloom.Core.Localization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Storloom.Core.Chats;

// Read-only, current-user client inspection. No process writes, injection, or key persistence.
public static class PassiveKeyReader {
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion {
        public ulong BaseAddress, AllocationBase;
        public uint AllocationProtect, Alignment;
        public ulong RegionSize;
        public uint State, Protect, Type, Alignment2;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address,
        out MemoryRegion region, nuint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address,
        byte[] buffer, nuint length, out nuint read);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access,
        out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information,
        nint buffer, int length, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(nint left, nint right);

    private static bool SameUser(SafeProcessHandle process) {
        using var self = OpenProcess(0x1000, false, Environment.ProcessId);
        if (self.IsInvalid) return false;
        // TOKEN_USER contains an in-buffer SID pointer; pin during the native call itself.
        bool ReadSid(SafeProcessHandle handle, Action<nint> action) {
            if (!OpenProcessToken(handle, 8, out var token)) return false;
            using (token) {
                _ = GetTokenInformation(token, 1, 0, 0, out int size);
                nint memory = Marshal.AllocHGlobal(size);
                try {
                    if (!GetTokenInformation(token, 1, memory, size, out _)) return false;
                    action(Marshal.ReadIntPtr(memory)); return true;
                } finally { Marshal.FreeHGlobal(memory); }
            }
        }
        bool equal = false;
        ReadSid(self, left => ReadSid(process, right => equal = EqualSid(left, right)));
        return equal;
    }

    public static byte[] Read(ChatLocation location, TimeSpan? timeout = null,
        CancellationToken cancellation = default, IProgress<ChatDiagnostic>? diagnostics = null) {
        using var oracle = new FileStream(location.Database, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        byte[] prefix = new byte[Math.Min(oracle.Length, 5120)];
        oracle.ReadExactly(prefix);
        string[] names = location.Platform == ChatPlatform.QQ ? ["QQ"] : ["Weixin", "WeChat"];
        var clock = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(45);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Root clients precede Chromium helpers; round-robin chunks keep every process progressing.
        var processes = names.SelectMany(Process.GetProcessesByName).OrderBy(CreationTime).ToArray();
        var scans = new List<MemoryScan>();
        long scannedBytes = 0; int regionCount = 0, candidateCount = 0; double lastReport = -1;
        void Report(string stage, bool force = false) {
            if (diagnostics == null || !force && clock.Elapsed.TotalSeconds - lastReport < 1) return;
            lastReport = clock.Elapsed.TotalSeconds;
            diagnostics.Report(new(stage, scannedBytes, scans.Count, regionCount, candidateCount, lastReport));
        }
        Report("key.passive.start", true);
        try {
            foreach (var process in processes) {
                cancellation.ThrowIfCancellationRequested();
                var handle = OpenProcess(0x0410, false, process.Id);
                if (handle.IsInvalid || !SameUser(handle)) { handle.Dispose(); continue; }
                var scan = new MemoryScan(handle); scans.Add(scan);
                ulong address = 0;
                while (address < 0x7fffffffffff && clock.Elapsed < limit) {
                    cancellation.ThrowIfCancellationRequested();
                    if (VirtualQueryEx(handle, (nint)address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) == 0 || region.RegionSize == 0) break;
                    if (region.State == 0x1000 && (region.Protect & 0x101) == 0 && (region.Protect & 0xee) != 0) { scan.Regions.Add(region); regionCount++; }
                    ulong next = region.BaseAddress + region.RegionSize;
                    if (next <= address) break;
                    address = next;
                }
                scan.Regions.Sort((left, right) => Priority(left).CompareTo(Priority(right)));
                Report("key.passive.regions", true);
            }
            while (clock.Elapsed < limit && scans.Any(s => s.Index < s.Regions.Count)) {
                foreach (var scan in scans) {
                    cancellation.ThrowIfCancellationRequested();
                    if (clock.Elapsed >= limit) break;
                    if (scan.Index >= scan.Regions.Count) continue;
                    var region = scan.Regions[scan.Index];
                    const ulong chunk = 1024 * 1024;
                    int size = (int)Math.Min(chunk + 256, region.RegionSize - scan.Offset);
                    byte[] bytes = new byte[size];
                    try {
                        _ = ReadProcessMemory(scan.Handle, (nint)(region.BaseAddress + scan.Offset), bytes, (nuint)size, out nuint read);
                        scannedBytes += (long)read; Report("key.passive.scan");
                        for (int i = 0; i < (int)read && clock.Elapsed < limit; i++) {
                            cancellation.ThrowIfCancellationRequested();
                            byte[]? candidate = null;
                            if (location.Platform == ChatPlatform.WeChat && i + 66 < (int)read && bytes[i] == 'x' && bytes[i + 1] == '\'') {
                                if (!bytes.AsSpan(i + 2, 64).ToArray().All(IsHex)) continue;
                                candidate = Convert.FromHexString(System.Text.Encoding.ASCII.GetString(bytes, i + 2, 64));
                            } else if (location.Platform == ChatPlatform.QQ) {
                                int count = QqCandidateLength(bytes.AsSpan(0, (int)read), i);
                                if (count == 0) continue;
                                candidate = bytes.AsSpan(i, count).ToArray(); i += count;
                            }
                            if (candidate == null) continue;
                            bool accepted = false;
                            try { accepted = Verify(candidate); if (accepted) return candidate; }
                            finally { if (!accepted) CryptographicOperations.ZeroMemory(candidate); }
                        }
                    } finally { CryptographicOperations.ZeroMemory(bytes); }
                    scan.Offset += chunk;
                    if (scan.Offset >= region.RegionSize) { scan.Offset = 0; scan.Index++; }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            Report(clock.Elapsed >= limit ? "key.passive.timeout" : "key.passive.exhausted", true);
            throw new InvalidOperationException(L.T(scans.Count == 0 ? "KeyClientMissing" : location.Platform == ChatPlatform.WeChat ? "WeChatPassiveUnavailable" : "KeyNotFound"));
        } finally {
            foreach (var scan in scans) scan.Handle.Dispose();
            foreach (var process in processes) process.Dispose();
        }

        bool Verify(byte[] candidate) {
            string digest = Convert.ToHexString(SHA256.HashData(candidate));
            if (!seen.Add(digest)) return false;
            candidateCount++; Report("key.passive.verify");
            bool accepted = ChatCipher.Detect(prefix, candidate, location.Platform) != null;
            if (accepted) Report("key.passive.verified", true);
            return accepted;
        }
    }

    private sealed class MemoryScan(SafeProcessHandle handle) {
        public SafeProcessHandle Handle { get; } = handle;
        public List<MemoryRegion> Regions { get; } = [];
        public int Index;
        public ulong Offset;
    }
    private static int Priority(MemoryRegion region) => region.Type == 0x20000 ? ((region.Protect & 0xcc) != 0 ? 0 : 1) : 2;

    private static DateTime CreationTime(Process process) {
        try { return process.StartTime; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return DateTime.MaxValue; }
    }

    internal static int QqCandidateLength(ReadOnlySpan<byte> bytes, int start) {
        static bool Printable(byte value) => value is >= 0x20 and <= 0x7e;
        if (start < 0 || start >= bytes.Length || (start > 0 && Printable(bytes[start - 1]))) return 0;
        int count = 0;
        while (start + count < bytes.Length && count <= 32 && Printable(bytes[start + count])) count++;
        return count is 16 or 32 && start + count < bytes.Length && bytes[start + count] == 0 ? count : 0;
    }

    private static bool IsHex(byte value) => value is >= (byte)'0' and <= (byte)'9' or
        >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
}
