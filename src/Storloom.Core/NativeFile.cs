using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Storloom.Core;

public static class NativeFile {
    private const uint ReadAttributes = 0x80;
    private const uint DeleteAccess = 0x10000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
        public System.Runtime.InteropServices.ComTypes.FILETIME Access;
        public System.Runtime.InteropServices.ComTypes.FILETIME Write;
        public uint Volume;
        public uint SizeHigh, SizeLow, Links, IdHigh, IdLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,
        StringBuilder buffer, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle,
        int informationClass, ref byte delete, uint size);

    private static IOException Failure(string path) =>
        new($"{path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    private static SafeFileHandle Open(string path, bool delete, bool directory = false) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        var handle = CreateFileW(path, ReadAttributes | (delete ? DeleteAccess : 0),
            delete ? FileShare.Read : directory ? FileShare.ReadWrite :
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting,
            OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Failure(path); }
        return handle;
    }

    private static HandleInfo Info(SafeFileHandle handle, string expected, bool directory = false) {
        if (!GetFileInformationByHandle(handle, out var info)) throw Failure(expected);
        var attrs = (FileAttributes)info.Attributes;
        if ((attrs & FileAttributes.ReparsePoint) != 0 ||
            ((attrs & FileAttributes.Directory) != 0) != directory) {
            throw new IOException($"Unexpected file type or link: {expected}");
        }
        var buffer = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw Failure(expected);
        string actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\")) actual = actual[4..];
        if (!PathPolicy.Normalize(actual).Equals(PathPolicy.Normalize(expected),
            StringComparison.OrdinalIgnoreCase)) throw new IOException($"Resolved path changed: {expected}");
        return info;
    }

    private static long Ticks(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
        DateTime.FromFileTimeUtc(((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime).Ticks;
    private static FileStamp Stamp(HandleInfo info) => new(
        ((long)info.SizeHigh << 32) | info.SizeLow, Ticks(info.Creation), Ticks(info.Write),
        info.Volume, ((ulong)info.IdHigh << 32) | info.IdLow, info.Links);

    public static FileStamp ReadStamp(string path) {
        using var handle = Open(path, false);
        return Stamp(Info(handle, path));
    }

    public static void DeleteVerified(Candidate candidate) {
        var directoryHandles = new List<SafeFileHandle>();
        try {
            // Hold every ancestor without delete sharing to prevent directory replacement.
            var chain = new Stack<string>();
            for (string? path = Path.GetDirectoryName(candidate.Path); path != null;
                path = Path.GetDirectoryName(path)) chain.Push(path);
            foreach (string directory in chain) {
                var ancestor = Open(directory, false, true);
                directoryHandles.Add(ancestor);
                Info(ancestor, directory, true);
            }
            using var handle = Open(candidate.Path, true);
            FileStamp current = Stamp(Info(handle, candidate.Path));
            if (current != candidate.Stamp) throw new IOException("File changed since preview.");
            if (current.Links != 1) throw new IOException("Hard-linked files are not deleted.");
            byte delete = 1;
            if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw Failure(candidate.Path);
        } finally {
            foreach (var handle in directoryHandles) handle.Dispose();
        }
    }
}
