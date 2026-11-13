using Microsoft.Data.Sqlite;

namespace Storloom.Core.Chats;

// Isolated from the system SQLite used for legacy indexes and source adapters.
internal sealed class ArchiveSqlite:IDisposable {
    private readonly SqliteConnection connection;
    private readonly Dictionary<string,SqliteCommand> commands=new(StringComparer.Ordinal);
    internal ArchiveSqlite(string path,byte[]? key){
        if(key!=null&&key.Length!=32)throw new ArgumentException("An archive query key must contain 32 bytes.",nameof(key));
        PathPolicy.AssertNoLinks(Path.GetDirectoryName(path)!);
        foreach(string file in new[]{path,path+"-journal",path+"-wal",path+"-shm"})if(File.Exists(file)){
            PathPolicy.AssertNoLinks(file);if(NativeFile.ReadStamp(file).Links!=1)throw new IOException("Shared query cache files cannot be modified.");
        }
        SQLitePCL.Batteries_V2.Init();
        connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());
        try{
            connection.Open();
            if(key!=null&&SQLitePCL.raw.sqlite3_key(connection.Handle,key)!=SQLitePCL.raw.SQLITE_OK)throw new IOException("Cannot initialize encrypted archive query cache.");
            Execute("PRAGMA trusted_schema=OFF");Execute("PRAGMA temp_store=MEMORY");Execute("PRAGMA cache_size=-65536");
            // Read the schema now so a wrong key fails before any projection writes.
            Query("SELECT count(*) FROM sqlite_master").ToArray();
        }catch{connection.Dispose();throw;}
    }
    private SqliteCommand Command(string sql,object[] values){
        if(!commands.TryGetValue(sql,out var command)){
            command=connection.CreateCommand();
            // All SQL is internal. Replace positional placeholders with named parameters.
            int parameter=0;command.CommandText=string.Concat(sql.Select(c=>c=='?'?"$p"+(parameter++):c.ToString()));
            for(int n=0;n<parameter;n++)command.Parameters.Add("$p"+n,SqliteType.Text);
            command.Prepare();commands.Add(sql,command);
        }
        if(command.Parameters.Count!=values.Length)throw new InvalidOperationException("Archive SQL parameter mismatch.");
        for(int n=0;n<values.Length;n++){command.Parameters[n].SqliteType=values[n] is byte[]?SqliteType.Blob:SqliteType.Text;command.Parameters[n].Value=values[n];}return command;
    }
    internal void Execute(string sql,params string[] values)=>Command(sql,values).ExecuteNonQuery();
    internal void ExecuteValues(string sql,params object[] values)=>Command(sql,values).ExecuteNonQuery();
    internal IReadOnlyList<string[]> Query(string sql,params string[] values){
        using var reader=Command(sql,values).ExecuteReader();var rows=new List<string[]>();
        while(reader.Read()){var row=new string[reader.FieldCount];for(int n=0;n<row.Length;n++)row[n]=reader.IsDBNull(n)?"":reader.GetValue(n) is byte[] blob?"!"+Convert.ToBase64String(blob):reader.GetValue(n).ToString()!;rows.Add(row);}return rows;
    }
    public void Dispose(){foreach(var command in commands.Values)command.Dispose();commands.Clear();connection.Dispose();}
}
