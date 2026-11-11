using System.Security.Cryptography;
using System.Text.Json;

namespace Storloom.Core.Chats;

internal sealed partial class ArchiveStore {
    private static readonly JsonSerializerOptions MessageReadOptions=new(){PropertyNameCaseInsensitive=true};
    private sealed record SourceFile(string Path,FileStamp Stamp);
    private sealed class MigrationSource:IDisposable {
        private readonly string root;private readonly FileStream? lease;private readonly ChatVault? vault;
        internal SourceFile[] Files{get;}
        internal MigrationSource(string directory,ReadOnlySpan<char> password,bool protectedDestination,IProgress<ScanProgress>? progress,CancellationToken cancellation){
            root=PathPolicy.Normalize(directory);PathPolicy.AssertNoLinks(root);string lockPath=Path.Combine(root,".export.lock");if(File.Exists(lockPath))PathPolicy.AssertNoLinks(lockPath);
            if(File.Exists(lockPath))lease=new(lockPath,FileMode.Open,FileAccess.Read,FileShare.Read);
            try{
                if(ChatVault.Exists(root)){
                    if(!protectedDestination)throw new InvalidOperationException("Protected source requires a protected destination.");
                    progress?.Report(new("archive-migration-unlock",0,0));
                    vault=ChatVault.Open(root,password);Files=vault.SnapshotFiles.Select(e=>new SourceFile(e.Path.Replace('\\','/'),e.Stamp)).OrderBy(e=>e.Path,StringComparer.Ordinal).ToArray();
                }else{
                    var files=new List<SourceFile>();var pending=new Stack<string>();pending.Push(root);long bytes=0;
                    while(pending.TryPop(out string? folder)){
                        OperationPause.Check(cancellation);PathPolicy.AssertNoLinks(folder);
                        foreach(string entry in Directory.EnumerateFileSystemEntries(folder)){
                            OperationPause.Check(cancellation);PathPolicy.AssertNoLinks(entry);
                            if(Directory.Exists(entry)){pending.Push(entry);continue;}
                            string relative=Path.GetRelativePath(root,entry).Replace('\\','/');if(relative==".export.lock")continue;
                            var stamp=NativeFile.ReadStamp(entry);files.Add(new(relative,stamp));bytes=checked(bytes+stamp.Bytes);
                            if(files.Count==1||files.Count%256==0)progress?.Report(new("archive-migration-plan",files.Count,bytes,Detail:relative));
                        }
                    }
                    Files=files.OrderBy(e=>e.Path,StringComparer.Ordinal).ToArray();
                }
                foreach(var file in Files)ArchiveQueryCache.ValidatePath(file.Path);
                progress?.Report(new("archive-migration-plan",Files.Length,Files.Sum(f=>f.Stamp.Bytes),TotalFiles:Files.Length));
                if(Files.Any(e=>e.Path is ".reading-update-pending.json" or ".storloom-export.json"))throw new IOException("Finish recovering the previous reader update before migration.");
            }catch{vault?.Dispose();lease?.Dispose();throw;}
        }
        internal Stream Open(SourceFile file){
            if(vault!=null)return vault.OpenRead(file.Path);
            string path=TransferService.Child(root,file.Path);PathPolicy.AssertNoLinks(path);
            var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,64*1024,FileOptions.SequentialScan);
            try{if(NativeFile.ReadStamp(path)!=file.Stamp)throw new IOException("Migration source changed since planning.");return input;}catch{input.Dispose();throw;}
        }
        internal void Check(SourceFile file){if(vault==null&&NativeFile.ReadStamp(TransferService.Child(root,file.Path))!=file.Stamp)throw new IOException("Migration source changed during reading.");}
        public void Dispose(){vault?.Dispose();lease?.Dispose();}
    }
    // The source is never modified or removed. Each committed batch includes its exact cursor.
    internal ArchiveImportCursor ImportReader(string directory,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default,ReadOnlySpan<char> sourcePassword=default){
        directory=PathPolicy.Normalize(directory);
        if(directory.Equals(root,StringComparison.OrdinalIgnoreCase)||PathPolicy.Under(root,directory)||PathPolicy.Under(directory,root))throw new InvalidOperationException("Source and archive directories must be separate.");
        using var operation=Lease();query.Replay(log,log.Head,Decode,cancellation,progress);
        using var source=new MigrationSource(directory,sourcePassword,keys!=null,progress,cancellation);
        var transcript=source.Files.SingleOrDefault(f=>f.Path=="messages.jsonl")??throw new InvalidDataException("Reader has no message transcript.");
        var report=source.Files.SingleOrDefault(f=>f.Path=="export-report.json");long? messageTotal=null;
        if(report!=null){using var input=source.Open(report);var status=JsonSerializer.Deserialize<ChatExportResult>(input,JsonFiles.Options)??throw new InvalidDataException("Invalid reader report.");if(status.CanResume)throw new InvalidOperationException("Complete the interrupted export before migrating.");messageTotal=status.Messages;}
        string? sourceIdentity=null;
        var metadata=source.Files.SingleOrDefault(f=>f.Path=="archive-index.js");
        if(metadata!=null){using var text=new StreamReader(source.Open(metadata));string raw=text.ReadToEnd();const string prefix="window.archiveIndex=";if(!raw.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("Invalid source reader metadata.");using var doc=JsonDocument.Parse(raw[prefix.Length..].Trim().TrimEnd(';'));if(doc.RootElement.TryGetProperty("sourceIdentity",out var storedIdentity)&&storedIdentity.ValueKind==JsonValueKind.String)sourceIdentity=storedIdentity.GetString();else if(doc.RootElement.TryGetProperty("preferenceScope",out var scope))sourceIdentity=scope.GetString();}
        sourceIdentity??="source:"+JsonFiles.Hash(directory.ToUpperInvariant());
        string snapshot=JsonFiles.Hash(source.Files).ToLowerInvariant();
        var cursor=query.Cursor;
        if(cursor?.SourceIdentity is{} originalIdentity&&originalIdentity!=sourceIdentity)throw new InvalidDataException("Source reader belongs to another archive identity.");
        if(cursor!=null&&cursor.Snapshot!=snapshot){if(cursor.Stage!="complete")throw new InvalidDataException("Migration source snapshot differs from the saved checkpoint.");cursor=null;}
        cursor??=new(snapshot,"messages",0,0,0,0,0,sourceIdentity);
        if(cursor.File<0||cursor.File>source.Files.Length)throw new InvalidDataException("Invalid migration file cursor.");
        if(cursor.Stage=="complete")return cursor;
        if(cursor.Stage=="messages"){
            using var lines=new Utf8LineCursor(source.Open(transcript),cursor.Offset);var messages=new List<ArchiveQueryCache.Message>();long count=cursor.Messages;int characters=0;
            void FlushMessages(string stage){
                source.Check(transcript);cursor=new(snapshot,stage,0,stage=="messages"?lines.Offset:0,count,0,0,sourceIdentity);
                Commit(new(1,messages.ToArray(),[],[],[],cursor));messages.Clear();characters=0;
                progress?.Report(new("archive-migration-messages",(int)Math.Min(count,int.MaxValue),lines.Offset,transcript.Stamp.Bytes,messageTotal==null?null:(int)Math.Min(messageTotal.Value,int.MaxValue)));
            }
            string? line;
            while(true){
                long start=lines.Offset;line=lines.ReadLine(cancellation);if(line==null)break;
                var message=JsonSerializer.Deserialize<ChatMessage>(line,MessageReadOptions)??throw new InvalidDataException("Invalid archived message.");
                if(string.IsNullOrEmpty(message.Platform)||string.IsNullOrEmpty(message.Conversation))throw new InvalidDataException("Missing archived conversation identity.");
                // A completed legacy reader's occurrence IDs retain duplicate native-ID events.
                string identity=!string.IsNullOrEmpty(message.Id)?"legacy:"+message.Id:"occurrence:"+start;
                string id=JsonFiles.Hash(new{message.Platform,message.Conversation,Identity=identity}).ToLowerInvariant();
                var candidate=new ArchiveQueryCache.Message(id,message.Platform+"/"+message.Conversation,message.Timestamp,SearchableText(message),line);
                if(query.Find(id)!=candidate)messages.Add(candidate);count++;characters=checked(characters+line.Length);
                if(messages.Count>=512||characters>=2*1024*1024)FlushMessages("messages");
            }
            FlushMessages("files");
        }
        long totalBytes=source.Files.Sum(f=>f.Stamp.Bytes);var objects=new List<ArchiveObject>();var parts=new List<ArchiveFilePart>();var files=new List<ArchiveFileEntry>();var pendingIds=new HashSet<string>(StringComparer.Ordinal);int buffered=0;
        void FlushFiles(){
            Commit(new(1,[],objects.ToArray(),parts.ToArray(),files.ToArray(),cursor));objects.Clear();parts.Clear();files.Clear();pendingIds.Clear();buffered=0;
            progress?.Report(new("archive-migration-files",(int)Math.Min(cursor!.Files,int.MaxValue),cursor.Bytes,totalBytes,source.Files.Length));
        }
        try{
        for(int n=cursor!.File;n<source.Files.Length;n++){
            OperationPause.Check(cancellation);var file=source.Files[n];
            var previous=query.FileEntry(file.Path);
            if(previous is{Complete:true}&&previous.SourceStamp==file.Stamp){source.Check(file);cursor=cursor with{File=n+1,Offset=0,Files=cursor.Files+1,Bytes=cursor.Bytes+file.Stamp.Bytes};if((n+1)%128==0)FlushFiles();continue;}
            long offset=n==cursor.File?cursor.Offset:0;
            if(offset<0||offset>file.Stamp.Bytes||offset%ChunkBytes!=0&&offset!=file.Stamp.Bytes)throw new InvalidDataException("Invalid media migration cursor.");
            string version=JsonFiles.Hash(new{snapshot,file.Path}).ToLowerInvariant();int ordinal=checked((int)((offset+ChunkBytes-1)/ChunkBytes));
            using var input=source.Open(file);input.Position=offset;
            while(offset<file.Stamp.Bytes){
                OperationPause.Check(cancellation);int length=(int)Math.Min(ChunkBytes,file.Stamp.Bytes-offset);byte[] bytes=new byte[length];input.ReadExactly(bytes);
                string id=ObjectId(bytes);if(!query.HasObject(id)&&pendingIds.Add(id))objects.Add(new(id,bytes));else CryptographicOperations.ZeroMemory(bytes);
                parts.Add(new(version,ordinal++,id));offset+=length;buffered+=length;
                cursor=cursor with{File=n,Offset=offset,Bytes=cursor.Bytes+length};
                files.Add(new(file.Path,version,file.Stamp.Bytes,ordinal,false,file.Stamp));
                if(buffered>=8*ChunkBytes){source.Check(file);FlushFiles();}
            }
            source.Check(file);files.Add(new(file.Path,version,file.Stamp.Bytes,ordinal,true,file.Stamp));cursor=cursor with{File=n+1,Offset=0,Files=cursor.Files+1};
            if(files.Count>=128)FlushFiles();
        }
        cursor=cursor with{Stage="complete"};FlushFiles();return cursor;
        }finally{foreach(var item in objects)CryptographicOperations.ZeroMemory(item.Bytes);}
    }
}
