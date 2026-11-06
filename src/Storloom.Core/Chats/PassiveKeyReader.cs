using System.ComponentModel;
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
        CancellationToken cancellation = default) {
        using var oracle = new FileStream(location.Database, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        byte[] prefix = new byte[Math.Min(oracle.Length, 5120)];
        oracle.ReadExactly(prefix);
        string[] names = location.Platform == ChatPlatform.QQ ? ["QQ"] : ["Weixin", "WeChat"];
        var clock = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(45);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names) {
            foreach (var process in Process.GetProcessesByName(name)) {
                using (process) {
                    using var handle = OpenProcess(0x0410, false, process.Id);
                    if (handle.IsInvalid || !SameUser(handle)) continue;
                    ulong address = 0;
                    while (address < 0x7fffffffffff && clock.Elapsed < limit) {
                        cancellation.ThrowIfCancellationRequested();
                        if (VirtualQueryEx(handle, (nint)address, out var region,
                            (nuint)Marshal.SizeOf<MemoryRegion>()) == 0 || region.RegionSize == 0) break;
                        if (region.State == 0x1000 && (region.Protect & 0x101) == 0 &&
                            (region.Protect & 0xee) != 0) {
                            for (ulong offset = 0; offset < region.RegionSize && clock.Elapsed < limit;
                                offset += 1024 * 1024) {
                                cancellation.ThrowIfCancellationRequested();
                                int size = (int)Math.Min(1024 * 1024 + 256UL, region.RegionSize - offset);
                                byte[] bytes = new byte[size];
                                try {
                                    if (!ReadProcessMemory(handle, (nint)(region.BaseAddress + offset), bytes,
                                        (nuint)size, out nuint read) || read == 0) continue;
                                    for (int i = 0; i < (int)read && clock.Elapsed < limit; i++) {
                                        int count = 0;
                                        if (location.Platform == ChatPlatform.WeChat && i + 66 < (int)read &&
                                            bytes[i] == 'x' && bytes[i + 1] == '\'') {
                                            count = 64;
                                            if (!bytes.AsSpan(i + 2, count).ToArray().All(IsHex)) continue;
                                            byte[] candidate = Convert.FromHexString(System.Text.Encoding.ASCII.GetString(bytes, i + 2, 64));
                                            if (Verify(candidate)) return candidate;
                                            CryptographicOperations.ZeroMemory(candidate);
                                        } else if (location.Platform == ChatPlatform.QQ && (i == 0 || bytes[i - 1] == 0)) {
                                            while (i + count < (int)read && bytes[i + count] is >= 0x20 and <= 0x7e && count <= 32) count++;
                                            if (count is not (16 or 32) || i + count >= (int)read || bytes[i + count] != 0) continue;
                                            byte[] candidate = bytes.AsSpan(i, count).ToArray();
                                            if (Verify(candidate)) return candidate;
                                            CryptographicOperations.ZeroMemory(candidate);
                                            i += count;
                                        }
                                    }
                                } finally { CryptographicOperations.ZeroMemory(bytes); }
                            }
                        }
                        ulong next = region.BaseAddress + region.RegionSize;
                        if (next <= address) break;
                        address = next;
                    }
                }
            }
        }
        throw new InvalidOperationException("No HMAC-verified key was found. Newer clients may need an externally obtained key. No debugger fallback is run automatically.");

        bool Verify(byte[] candidate) {
            string digest = Convert.ToHexString(SHA256.HashData(candidate));
            return seen.Add(digest) && ChatCipher.Detect(prefix, candidate, location.Platform) != null;
        }
    }

    private static bool IsHex(byte value) => value is >= (byte)'0' and <= (byte)'9' or
        >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
}
