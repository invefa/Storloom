using System.Globalization;
using System.Text.Json;

namespace Storloom.Core.Chats;

// The cursor and rows commit together. An interrupted batch is replayed, never skipped.
internal static class ChatIndexBootstrap {
    internal static void Build(string transcript,string database,long total,Func<string,(string Key,string Hash,string Id)> decode,
        IProgress<ScanProgress>? progress,CancellationToken cancellation,int batchSize=10000) {
        if(batchSize<1)throw new ArgumentOutOfRangeException(nameof(batchSize));
        PathPolicy.AssertNoLinks(transcript);PathPolicy.AssertNoLinks(Path.GetDirectoryName(database)!);
        foreach(string file in new[]{database,database+"-journal",database+"-wal",database+"-shm"})if(File.Exists(file)){
            PathPolicy.AssertNoLinks(file);if(NativeFile.ReadStamp(file).Links!=1)throw new IOException("Shared bootstrap files cannot be modified.");
        }
        var stamp=NativeFile.ReadStamp(transcript);
        using var sourceLease=new FileStream(transcript,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var index=new ChatUpdateIndex(database);
        if(index.Query("PRAGMA quick_check").Single()[0]!="ok")throw new InvalidDataException("Invalid bootstrap index.");
        string snapshot=JsonSerializer.Serialize(stamp);
        var stored=index.Query("SELECT v FROM metadata WHERE k='snapshot'").SingleOrDefault();
        if(stored!=null&&stored[0]!=snapshot)throw new InvalidDataException("Transcript changed since the index checkpoint. Start a new reading update workspace.");
        long Number(string name)=>long.Parse(index.Query("SELECT v FROM metadata WHERE k=?",name).SingleOrDefault()?[0]??"0",CultureInfo.InvariantCulture);
        long offset=Number("offset"),count=Number("count");
        using var cursor=new Utf8LineCursor(transcript,offset);
        void Save(string name,string value)=>index.Execute("INSERT OR REPLACE INTO metadata VALUES(?,?)",name,value);
        void Report()=>progress?.Report(new("chat-update-index",(int)Math.Min(count,int.MaxValue),cursor.Offset,stamp.Bytes,(int)Math.Min(total,int.MaxValue)));
        Report();
        while(true){
            OperationPause.Check(cancellation);index.Execute("BEGIN");
            int batch=0;
            try{
                string? line;
                while(batch<batchSize&&(line=cursor.ReadLine(cancellation))!=null){
                    var row=decode(line);index.Execute("INSERT OR IGNORE INTO messages VALUES(?,?,?)",row.Key,row.Hash,row.Id);count++;batch++;
                }
                if(NativeFile.ReadStamp(transcript)!=stamp)throw new IOException("Transcript changed during indexing.");
                Save("snapshot",snapshot);Save("offset",cursor.Offset.ToString(CultureInfo.InvariantCulture));Save("count",count.ToString(CultureInfo.InvariantCulture));
                index.Execute("COMMIT");
            }catch{index.Execute("ROLLBACK");throw;}
            Report();if(batch<batchSize)break;
        }
    }
}
