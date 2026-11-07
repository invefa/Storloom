using System.Runtime.InteropServices;
using System.Text;

namespace Storloom.Core.Chats;

public sealed class SqliteReader : IDisposable {
    private IntPtr database;
    private static readonly ReaderWriterLockSlim Gate = new();
    private bool locked, exclusive;
    private uint? previousPendingByte;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int bytes,
        out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_test_control(int operation, uint value);

    public SqliteReader(string path, bool shiftedQQ = false) {
        exclusive = shiftedQQ && new FileInfo(path).Length > 0x3ffff000;
        if (exclusive) Gate.EnterWriteLock(); else Gate.EnterReadLock();
        locked = true;
        try {
            if (shiftedQQ && new FileInfo(path).Length > 0x3ffff000) {
                previousPendingByte = unchecked((uint)sqlite3_test_control(11, 0x3ffffc00));
                if (previousPendingByte != 0x40000000)
                    throw new InvalidDataException("SQLite pending-byte state is not pristine.");
            }
            if (sqlite3_open_v2(path, out database, 1, IntPtr.Zero) != 0)
                throw new InvalidDataException("Cannot open the decrypted database as read-only SQLite.");
            _ = Query("PRAGMA query_only=ON").ToList();
            _ = Query("PRAGMA trusted_schema=OFF").ToList();
        } catch { Dispose(); throw; }
    }

    public IEnumerable<object?[]> Query(string sql, CancellationToken cancellation = default) {
        byte[] text = Encoding.UTF8.GetBytes(sql + "\0");
        int code = sqlite3_prepare_v2(database, text, text.Length, out var statement, IntPtr.Zero);
        if (code != 0) throw new InvalidDataException($"SQLite query preparation failed ({code}).");
        try {
            while ((code = sqlite3_step(statement)) == 100) {
                cancellation.ThrowIfCancellationRequested();
                var row = new object?[sqlite3_column_count(statement)];
                for (int i = 0; i < row.Length; i++) {
                    int type = sqlite3_column_type(statement, i);
                    if (type == 1) row[i] = sqlite3_column_int64(statement, i);
                    else if (type == 3) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i));
                    else if (type == 4) {
                        int length = sqlite3_column_bytes(statement, i);
                        if (length > 64 * 1024 * 1024) throw new InvalidDataException("Oversized message blob.");
                        var data = new byte[length];
                        if (length > 0) Marshal.Copy(sqlite3_column_blob(statement, i), data, 0, length);
                        row[i] = data;
                    }
                }
                yield return row;
            }
            if (code != 101) throw new InvalidDataException($"SQLite read failed ({code}).");
        } finally { sqlite3_finalize(statement); }
    }

    public static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    public void Dispose() {
        if (database != IntPtr.Zero) { sqlite3_close_v2(database); database = IntPtr.Zero; }
        if (previousPendingByte is uint previous) {
            sqlite3_test_control(11, previous); previousPendingByte = null;
        }
        if (locked) { locked = false; if (exclusive) Gate.ExitWriteLock(); else Gate.ExitReadLock(); }
    }
}
