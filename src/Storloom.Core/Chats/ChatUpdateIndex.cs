using System.Runtime.InteropServices;
using System.Text;

namespace Storloom.Core.Chats;

// Writable SQLite is confined to the derived update index, never opened against a client database.
internal sealed class ChatUpdateIndex : IDisposable {
    private nint database;
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(nint db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(nint db, byte[] sql, int bytes, out nint statement, nint tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(nint statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(nint statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(nint statement, int index, byte[] text, int bytes, nint destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_column_text(nint statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(nint statement);
    public ChatUpdateIndex(string path) {
        if (sqlite3_open_v2(SqliteReader.NativePath(path), out database, 6, 0) != 0) { Dispose(); throw new IOException("Cannot create update index."); }
        Execute("PRAGMA trusted_schema=OFF");
        Execute("CREATE TABLE IF NOT EXISTS messages(k TEXT PRIMARY KEY,h TEXT NOT NULL,id TEXT NOT NULL) WITHOUT ROWID");
        Execute("CREATE TABLE IF NOT EXISTS changes(k TEXT PRIMARY KEY,data TEXT NOT NULL) WITHOUT ROWID");
        Execute("DELETE FROM changes");
    }
    public IEnumerable<string[]> Query(string sql, params string[] values) {
        byte[] query = Encoding.UTF8.GetBytes(sql + "\0");
        if (sqlite3_prepare_v2(database, query, query.Length, out nint statement, 0) != 0) throw new InvalidDataException("Invalid update index.");
        try {
            for (int i = 0; i < values.Length; i++) {
                byte[] text = Encoding.UTF8.GetBytes(values[i]);
                if (sqlite3_bind_text(statement, i + 1, text, text.Length, -1) != 0) throw new IOException("Cannot bind update index.");
            }
            int code;
            while ((code = sqlite3_step(statement)) == 100) {
                var row = new string[sqlite3_column_count(statement)];
                for (int i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i)) ?? "";
                yield return row;
            }
            if (code != 101) throw new IOException("Cannot read or write update index.");
        } finally { sqlite3_finalize(statement); }
    }
    public void Execute(string sql, params string[] values) { foreach (var _ in Query(sql, values)) { } }
    public void Dispose() { if (database != 0) { sqlite3_close_v2(database); database = 0; } }
}
