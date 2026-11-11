using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Storloom.Core.Chats;

internal sealed partial class ArchiveStore {
    private sealed record ReaderExportTask(string Id,string? Head,string Proof="");
    private sealed record ExportFileProof(string Path,string Digest,string Proof="");
    private string ExportProof(ReaderExportTask task){
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(task with{Proof=""});if(keys==null)return Convert.ToHexStringLower(SHA256.HashData(bytes));byte[] key=keys.DomainKey("reader/export-task");try{return Convert.ToHexStringLower(HMACSHA256.HashData(key,bytes));}finally{CryptographicOperations.ZeroMemory(key);}
    }
    // Source data is never changed. A task is bound to one immutable source snapshot.
    internal void ExportReader(string destination,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        using var lease=Lease(readOnly:true);destination=PathPolicy.Normalize(destination);
        if(destination.Equals(root,StringComparison.OrdinalIgnoreCase)||PathPolicy.Under(destination,root)||PathPolicy.Under(root,destination))throw new IOException("Export and archive must be separate.");
        if(ImportCursor?.Stage!="complete")throw new IOException("Complete migration before exporting a reader.");
        PathPolicy.AssertNoLinks(Path.GetDirectoryName(destination)!);string taskPath=Path.Combine(destination,".storloom-export.json");ReaderExportTask task;
        if(Directory.Exists(destination)){
            PathPolicy.AssertNoLinks(destination);if(!File.Exists(taskPath))throw new IOException("Choose a new export directory or the interrupted export directory.");PathPolicy.AssertNoLinks(taskPath);
            task=JsonFiles.Read<ReaderExportTask>(taskPath);if(task.Id!=Id||task.Head!=Head||task.Proof!=ExportProof(task))throw new InvalidDataException("Export snapshot or task integrity differs from its checkpoint.");
        }else{Directory.CreateDirectory(destination);task=new(Id,Head);task=task with{Proof=ExportProof(task)};JsonFiles.Write(taskPath,task);}
        string lockPath=Path.Combine(destination,".export.lock");if(File.Exists(lockPath))PathPolicy.AssertNoLinks(lockPath);using var targetLease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        string journalPath=Path.Combine(destination,".storloom-export-files");if(File.Exists(journalPath))PathPolicy.AssertNoLinks(journalPath);
        var completed=new Dictionary<string,string>(StringComparer.Ordinal);
        string FileProof(ExportFileProof proof)=>ExportProof(new(Id,JsonSerializer.Serialize(new{Head,File=proof with{Proof=""}})));
        long validJournalBytes=0;
        if(File.Exists(journalPath)){
            using var tail=File.OpenRead(journalPath);long length=tail.Length;bool endsWithNewline=length==0||tail.Seek(-1,SeekOrigin.End)>=0&&tail.ReadByte()==10;
            using var lines=new Utf8LineCursor(journalPath);string? line;
            while((line=lines.ReadLine(cancellation))!=null){
                if(lines.Offset==length&&!endsWithNewline)break;
                var proof=JsonSerializer.Deserialize<ExportFileProof>(line)??throw new InvalidDataException("Invalid export checkpoint.");ArchiveQueryCache.ValidatePath(proof.Path);if(proof.Proof!=FileProof(proof))throw new InvalidDataException("Export checkpoint authentication failed.");completed[proof.Path]=proof.Digest;validJournalBytes=lines.Offset;
            }
        }
        // Complete records precede file publication. An incomplete tail is discarded on retry.
        using var journal=new FileStream(journalPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);journal.SetLength(validJournalBytes);journal.Position=validJournalBytes;
        long bytes=0;int done=0;
        var entries=FileInventory().Where(f=>f.Complete&&!Generated(f.Path)).ToArray();long? total=entries.Sum(f=>f.Length*(completed.ContainsKey(f.Path)?1:2));
        var index=JsonNode.Parse(Encoding.UTF8.GetString(ReaderIndex())["window.archiveIndex=".Length..].TrimEnd(';'))!.AsObject();index["nativeStore"]=false;
        var counts=query.Conversations().ToDictionary(c=>c.Key,c=>c.Count);
        int generated=readerConversations!.Sum(c=>checked((int)((counts[c.Value]+299)/300))+1)+6;
        void Report(string? path=null)=>progress?.Report(new("archive-reader-export",done,bytes,total,entries.Length+generated,path));
        void Write(string relative,Action<Stream> produce){
            OperationPause.Check(cancellation);ArchiveQueryCache.ValidatePath(relative);string target=TransferService.Child(destination,relative);if(File.Exists(target))PathPolicy.AssertNoLinks(target);
            if(File.Exists(target)&&completed.TryGetValue(relative,out var digest)){
                using var input=File.OpenRead(target);if(Convert.ToHexStringLower(TransferService.Hash(input,cancellation,n=>{bytes+=n;Report(relative);}))!=digest)throw new InvalidDataException("Completed export file changed.");done++;Report(relative);return;
            }
            if(File.Exists(target))throw new IOException("Unexpected existing export file.");EnsureParent(destination,relative);string temporary=target+".partial";if(File.Exists(temporary))PathPolicy.AssertNoLinks(temporary);if(File.Exists(temporary)&&NativeFile.ReadStamp(temporary).Links!=1)throw new IOException("Shared partial export file.");
            using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){produce(output);output.Flush(true);}
            OperationPause.Check(cancellation);string hash;using(var input=File.OpenRead(temporary))hash=Convert.ToHexStringLower(TransferService.Hash(input,cancellation,n=>{bytes+=n;Report(relative);}));
            // Save the expected digest first: interruption before rename can safely regenerate it.
            var proof=new ExportFileProof(relative,hash);proof=proof with{Proof=FileProof(proof)};byte[] record=JsonSerializer.SerializeToUtf8Bytes(proof);journal.Write(record);journal.WriteByte(10);journal.Flush(true);completed[relative]=hash;File.Move(temporary,target);done++;Report(relative);
        }
        void Bytes(string path,byte[] data)=>Write(path,output=>output.Write(data));
        Report();foreach(var file in entries)Write(file.Path,output=>{using var input=OpenFile(file.Path);byte[] buffer=new byte[1024*1024];int n;while((n=input.Read(buffer))>0){OperationPause.Check(cancellation);output.Write(buffer,0,n);bytes+=n;Report(file.Path);}});
        total=null; // Page/message generation is tracked by files rather than an estimated byte size.
        long messages=0;
        foreach(var pair in readerConversations!){
            int page=0;var batch=new List<string>(300);
            foreach(var row in ReaderRows(pair.Value)){
                OperationPause.Check(cancellation);messages++;batch.Add(row.Json);if(batch.Count==300){Bytes($"conversations/{pair.Key}-{page}.js",Encoding.UTF8.GetBytes("window.receivePage("+JsonSerializer.Serialize(pair.Key)+","+page+",["+string.Join(',',batch)+"]);"));page++;batch.Clear();}
            }
            if(batch.Count>0)Bytes($"conversations/{pair.Key}-{page}.js",Encoding.UTF8.GetBytes("window.receivePage("+JsonSerializer.Serialize(pair.Key)+","+page+",["+string.Join(',',batch)+"]);"));
            Write($"conversations/{pair.Key}-refs.js",output=>WriteReferences(output,pair.Key,pair.Value,cancellation));
        }
        Write("messages.jsonl",output=>{using var writer=new StreamWriter(output,new UTF8Encoding(false),65536,true);foreach(var pair in readerConversations!)foreach(var row in ReaderRows(pair.Value)){OperationPause.Check(cancellation);writer.WriteLine(row.Json);}});
        foreach(string asset in new[]{"messages.html","reader.js","reader.css"})Write(asset,output=>{using var input=typeof(ArchiveStore).Assembly.GetManifestResourceStream("Storloom.Core.Chats.Reader."+asset)??throw new IOException("Missing reader asset.");input.CopyTo(output);});
        Bytes("archive-index.js",Encoding.UTF8.GetBytes("window.archiveIndex="+index.ToJsonString()+";"));
        ChatExportResult report;try{using var input=OpenFile("export-report.json");report=JsonSerializer.Deserialize<ChatExportResult>(input,JsonFiles.Options)??throw new InvalidDataException("Invalid report.");}catch(FileNotFoundException){report=new(destination,messages,0,[],true);}
        Bytes("export-report.json",JsonSerializer.SerializeToUtf8Bytes(report with{Directory=destination,Messages=messages,CanResume=false},JsonFiles.Options));
        OperationPause.Check(cancellation);File.Delete(taskPath);journal.Dispose();File.Delete(journalPath);Report();
    }
    private static void EnsureParent(string destination,string relative){string current=destination;foreach(var part in relative.Split('/')[..^1]){current=Path.Combine(current,part);if(!Directory.Exists(current))Directory.CreateDirectory(current);PathPolicy.AssertNoLinks(current);}}
    private static bool Generated(string path)=>path.StartsWith('.')||path.StartsWith("conversations/",StringComparison.Ordinal)||path is "messages.html" or "reader.js" or "reader.css" or "archive-index.js" or "messages.jsonl" or "export-report.json";
}
