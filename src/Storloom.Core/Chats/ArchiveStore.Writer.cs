using System.Security.Cryptography;
using System.Text.Json;
namespace Storloom.Core.Chats;

internal sealed partial class ArchiveStore {
    private sealed record Projection(string? Head,string Directory);
    private sealed record WriteTask(string? BaseHead,string Directory,string Source,string? Workspace=null);
    internal string? WriteWorkspace{get;set;}
    private static string ProjectionDirectory(string root,string? head){string marker=Path.Combine(root,"projection.json");if(File.Exists(marker)){PathPolicy.AssertNoLinks(marker);var entries=JsonFiles.Read<Projection[]>(marker);var match=entries.FirstOrDefault(e=>e.Head==head);if(match!=null){if(!System.Text.RegularExpressions.Regex.IsMatch(match.Directory,"^query/stage-[a-f0-9]{32}$"))throw new InvalidDataException("Invalid query projection path.");return TransferService.Child(root,match.Directory);}}return Path.Combine(root,"query");}

    // One bounded writer session; checkpoints and cache application share each commit.
    internal sealed class Writer:IDisposable {
        private readonly ArchiveStore store;private readonly FileStream lease;private readonly CancellationToken cancellation;private readonly ArchiveCommitLog log;private ArchiveQueryCache query=null!;private readonly WriteTask task;private bool completed;private readonly IProgress<ScanProgress>? progress;
        private readonly List<ArchiveQueryCache.Message> messages=[];private readonly List<ArchiveObject> objects=[];private readonly List<ArchiveFilePart> parts=[];private readonly List<ArchiveFileEntry> files=[];private readonly HashSet<string> pending=new(StringComparer.Ordinal);
        private readonly Dictionary<FileStamp,string> sourceAssets;private int buffered,messageCharacters;private bool disposed;
        internal ArchiveCommitLog Log=>log;internal ArchiveQueryCache Query=>query;
        internal long ChangedMessages{get;private set;}internal long ReusedFiles{get;private set;}internal long WrittenBytes{get;private set;}
        internal ArchivePerformance Performance=>store.Performance;
        internal Writer(ArchiveStore store,CancellationToken cancellation,string source,IProgress<ScanProgress>? progress){
            this.store=store;this.cancellation=cancellation;this.progress=progress;lease=store.Lease();log=new(store.root,".write-head");string taskPath=Path.Combine(store.root,".write-task.json");
            try{if(store.Head!=store.log.Head)throw new IOException("Archive advanced; reopen it before updating.");store.query.Replay(store.log,store.Head,store.Decode,cancellation,projection:store.DecodeProjection);
                if(File.Exists(taskPath)){PathPolicy.AssertNoLinks(taskPath);var retained=JsonFiles.Read<WriteTask>(taskPath);if(retained.BaseHead!=store.Head&&log.Head==store.Head&&ProjectionDirectory(store.root,store.Head)==TransferService.Child(store.root,retained.Directory))File.Delete(taskPath);}
                if(File.Exists(taskPath)){task=JsonFiles.Read<WriteTask>(taskPath);if(task.BaseHead!=store.Head||task.Source!=source)throw new IOException("The unfinished compact task belongs to a different source snapshot. Keep the published snapshot and restart the task explicitly.");}
                else{task=new(store.Head,"query/stage-"+Guid.NewGuid().ToString("N"),source,store.WriteWorkspace==null?null:PathPolicy.Normalize(store.WriteWorkspace));string headPath=Path.Combine(store.root,".write-head");if(File.Exists(headPath)){PathPolicy.AssertNoLinks(headPath);File.Delete(headPath);}if(store.Head!=null)log.PublishCopiedHead(null,store.Head);JsonFiles.Write(taskPath,task);}
                if(!System.Text.RegularExpressions.Regex.IsMatch(task.Directory,"^query/stage-[a-f0-9]{32}$"))throw new InvalidDataException("Invalid writer query path.");
                if(task.Workspace!=null&&(store.WriteWorkspace==null||task.Workspace!=PathPolicy.Normalize(store.WriteWorkspace)))throw new IOException("Continue this task using its original work directory.");
                string directory=task.Workspace??TransferService.Child(store.root,task.Directory);PathPolicy.AssertNoLinks(Path.GetDirectoryName(directory)!);Directory.CreateDirectory(directory);PathPolicy.AssertNoLinks(directory);string database=Path.Combine(directory,"query.db");
                if(!File.Exists(database)&&File.Exists(store.query.DatabasePath)){
                    store.query.Checkpoint();
                    string temporary=database+".copy";if(File.Exists(temporary)){PathPolicy.AssertNoLinks(temporary);if(NativeFile.ReadStamp(temporary).Links!=1)throw new IOException("Shared index copy.");}
                    using(var input=new FileStream(store.query.DatabasePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){byte[] buffer=new byte[1024*1024];long bytes=0;int count;progress?.Report(new("archive-query-copy",0,0,input.Length,1));while((count=input.Read(buffer))>0){OperationPause.Check(cancellation);output.Write(buffer,0,count);bytes+=count;progress?.Report(new("archive-query-copy",0,bytes,input.Length,1));}output.Flush(true);}File.Move(temporary,database,false);
                }
                byte[]? key=store.keys?.DomainKey("query/private");try{query=new(directory,store.keys!=null,key);}finally{if(key!=null)CryptographicOperations.ZeroMemory(key);}
                query.EnableWriteAhead();query.Replay(log,log.Head,store.Decode,cancellation,projection:store.DecodeProjection);sourceAssets=[];
            }catch{query?.Dispose();lease.Dispose();throw;}
        }
        internal Stream Open(string path){var file=query.FileEntry(path)??throw new FileNotFoundException(path);return new ChunkStream(file.Length,query.FileParts(file.Version),id=>store.ReadObject(id,query),()=>{});}
        internal string Metadata(){try{using var text=new StreamReader(Open("archive-index.js"));return text.ReadToEnd();}catch(FileNotFoundException){return "window.archiveIndex={};";}}
        internal bool TryReuse(ChatMessage native,string identity){
            var row=query.Find(JsonFiles.Hash(new{native.Platform,native.Conversation,Identity=identity}).ToLowerInvariant());if(row==null)return false;
            var old=JsonSerializer.Deserialize<ChatMessage>(row.Json);if(old==null||ChatReadingUpdate.Fingerprint(old)!=ChatReadingUpdate.Fingerprint(native)||old.Media.Any(m=>m.Quality=="thumbnail"))return false;
            bool Complete(IEnumerable<MessagePart> parts)=>parts.All(p=>p.MediaState is not ("missing" or "unsupported-format")&&p.Kind is not ("unsupported" or "unparsed")&&(p.Kind!="forward"||p.Forwarded.Length>0)&&p.Forwarded.All(f=>Complete(f.Parts)));
            return Complete(old.Parts);
        }
        internal void Add(ChatMessage message){
            OperationPause.Check(cancellation);string identity=message.Id??("native:"+message.ServerId+":"+message.Sequence+":"+message.Sender+":"+message.Timestamp);string id=JsonFiles.Hash(new{message.Platform,message.Conversation,Identity=identity}).ToLowerInvariant();
            var old=query.Find(id);if(old!=null){var previous=JsonSerializer.Deserialize<ChatMessage>(old.Json);if(previous!=null&&ChatReadingUpdate.Fingerprint(previous)==ChatReadingUpdate.Fingerprint(message))message=message with{Identity=previous.Identity,IdentityVersion=previous.IdentityVersion,SenderName=previous.SenderName};}
            var row=new ArchiveQueryCache.Message(id,message.Platform+"/"+message.Conversation,message.Timestamp,SearchableText(message),JsonSerializer.Serialize(message));if(old==row)return;messages.Add(row);messageCharacters=checked(messageCharacters+row.Json.Length);ChangedMessages++;if(messages.Count>=4096||messageCharacters>=2*1024*1024)Flush();
        }
        internal long MessageCount=>query.MessageCount;internal int AssetCount=>query.AssetCount;
        internal void AddStored(ArchiveQueryCache.Message row){OperationPause.Check(cancellation);if(query.Find(row.Id)==row)return;messages.Add(row);messageCharacters=checked(messageCharacters+row.Json.Length);ChangedMessages++;if(messages.Count>=4096||messageCharacters>=2*1024*1024)Flush();}
        internal string? AssetFor(FileStamp stamp)=>sourceAssets.GetValueOrDefault(stamp)??query.AssetFor(stamp);
        internal ArchiveFileEntry Put(string? name,Stream input,FileStamp? stamp=null,string extension="",Action<int>? advance=null){
            OperationPause.Check(cancellation);if(name!=null)ArchiveQueryCache.ValidatePath(name);
            if(name!=null&&query.FileEntry(name) is{Complete:true} previous&&stamp!=null&&previous.SourceStamp==stamp){ReusedFiles++;return previous;}
            if(name==null&&stamp!=null&&AssetFor(stamp) is{} asset&&query.FileEntry(asset) is{} known){ReusedFiles++;return known;}
            string version=JsonFiles.Hash(new{Name=name,Stamp=stamp,Length=input.Length,Nonce=stamp==null?Guid.NewGuid().ToString("N"):""}).ToLowerInvariant();long length=input.Length;int ordinal=0;using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while(input.Position<input.Length){OperationPause.Check(cancellation);byte[] chunk=new byte[(int)Math.Min(ChunkBytes,input.Length-input.Position)];input.ReadExactly(chunk);hash.AppendData(chunk);advance?.Invoke(chunk.Length);string id=store.ObjectId(chunk);
                if(!query.HasObject(id)&&pending.Add(id)){objects.Add(new(id,chunk));buffered+=chunk.Length;WrittenBytes+=chunk.Length;}else CryptographicOperations.ZeroMemory(chunk);
                parts.Add(new(version,ordinal++,id));if(buffered>=8*ChunkBytes||parts.Count>=4096)Flush();
            }
            string digest=Convert.ToHexStringLower(hash.GetHashAndReset());name??="assets/"+digest+extension;var file=new ArchiveFileEntry(name,version,length,ordinal,true,stamp,digest);files.Add(file);if(stamp!=null&&name.StartsWith("assets/",StringComparison.Ordinal))sourceAssets[stamp]=name;if(files.Count>=1024)Flush();return file;
        }
        internal void Bytes(string name,byte[] bytes){if(query.FileEntry(name) is{} existing&&existing.Sha256==Convert.ToHexStringLower(SHA256.HashData(bytes))){ReusedFiles++;return;}using var input=new MemoryStream(bytes,false);Put(name,input);}
        internal void Flush(ArchiveImportCursor? cursor=null){if(messages.Count+objects.Count+parts.Count+files.Count==0&&cursor==null)return;OperationPause.Check(cancellation);store.CommitTo(new(1,messages.ToArray(),objects.ToArray(),parts.ToArray(),files.ToArray(),cursor),log,query,cancellation);messages.Clear();objects.Clear();parts.Clear();files.Clear();pending.Clear();buffered=0;messageCharacters=0;store.readerConversations=null;}
        internal void Complete(ArchiveImportCursor cursor){
            Flush(cursor);OperationPause.Check(cancellation);
            if(task.Workspace!=null){
                string source=query.DatabasePath,directory=TransferService.Child(store.root,task.Directory);Directory.CreateDirectory(directory);PathPolicy.AssertNoLinks(directory);string target=Path.Combine(directory,"query.db"),temporary=target+".copy";
                query.Dispose();
                // Published readers always use the archive-local projection. The external
                // checkpoint remains available if copying or publication is interrupted.
                foreach(string path in new[]{target,temporary})if(File.Exists(path)){PathPolicy.AssertNoLinks(path);if(NativeFile.ReadStamp(path).Links!=1)throw new IOException("Shared index publication file.");File.Delete(path);}
                long copied=0,total=new FileInfo(source).Length*2;progress?.Report(new("archive-query-publish",0,0,total,1));ChatWorkspaceCopy.File(()=>File.OpenRead(source),target,temporary,cancellation,n=>{copied+=n;progress?.Report(new("archive-query-publish",0,copied,total,1));});
                byte[]? key=store.keys?.DomainKey("query/private");try{query=new(directory,store.keys!=null,key);}finally{if(key!=null)CryptographicOperations.ZeroMemory(key);}
                if(query.Applied!=log.Head)throw new IOException("Published query snapshot does not match its data.");
            }
            if(store.StandaloneRequested||store.HasStandalone){store.PublishBrowser(log.Head!,query,Open,progress,cancellation);store.WriteBrowserRuntime(cancellation);}
            string mapping=Path.Combine(store.root,"projection.json");var projections=new List<Projection>{new(log.Head,task.Directory)};
            if(File.Exists(mapping))projections.AddRange(JsonFiles.Read<Projection[]>(mapping).Where(p=>p.Head!=log.Head));JsonFiles.Write(mapping,projections.Take(16).ToArray());
            var nextLease=LeaseQuery(Path.GetDirectoryName(query.DatabasePath)!);try{store.log.PublishCopiedHead(task.BaseHead,log.Head);}catch{nextLease.Dispose();throw;}
            lock(store.readGate){store.query.Dispose();store.queryLease?.Dispose();store.query=query;store.queryLease=nextLease;store.publishedHead=log.Head;completed=true;store.readerConversations=null;}File.Delete(Path.Combine(store.root,".write-task.json"));store.ReclaimQueryCaches(task.Directory);
        }
        public void Dispose(){if(disposed)return;foreach(var item in objects)CryptographicOperations.ZeroMemory(item.Bytes);disposed=true;if(!completed)query.Dispose();lease.Dispose();}
    }
    internal Writer BeginWrite(CancellationToken cancellation=default,string source="",IProgress<ScanProgress>? progress=null)=>new(this,cancellation,source,progress);
    private void ReclaimQueryCaches(string current){
        // These are disposable query projections, never packs, source files or
        // snapshot manifests. An open reader retains its own cache lease.
        FileStream projectionLock;try{projectionLock=LeaseProjection(root,true);}catch(IOException){return;}using var guard=projectionLock;
        string boundary=Path.Combine(root,"query");foreach(string directory in Directory.EnumerateDirectories(boundary,"stage-*")){
            if(directory==TransferService.Child(root,current)||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory),"^stage-[a-f0-9]{32}$"))continue;
            try{PathPolicy.AssertNoLinks(directory);string[] files=Directory.GetFiles(directory);if(Directory.EnumerateDirectories(directory).Any()||files.Any(f=>Path.GetFileName(f) is not ("query.db" or "query.db-journal" or "query.db-wal" or "query.db-shm" or "query.db.copy" or ".index-use.lock")))continue;
                foreach(string file in files){PathPolicy.AssertNoLinks(file);if(NativeFile.ReadStamp(file).Links!=1)throw new IOException("Shared query cache.");}
                using(var exclusive=new FileStream(Path.Combine(directory,".index-use.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){foreach(string file in files)if(Path.GetFileName(file)!=".index-use.lock")File.Delete(file);}
                File.Delete(Path.Combine(directory,".index-use.lock"));Directory.Delete(directory,false);
            }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){/* A reader or external tool can keep a projection alive. */}
        }
    }
    internal static void AbandonPending(string directory){string root=PathPolicy.Normalize(directory);PathPolicy.AssertNoLinks(root);string lockPath=Path.Combine(root,"operation.lock");if(File.Exists(lockPath))PathPolicy.AssertNoLinks(lockPath);using var lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);string path=Path.Combine(root,".write-task.json");if(File.Exists(path)){PathPolicy.AssertNoLinks(path);File.Move(path,path+"."+Guid.NewGuid().ToString("N")+".abandoned");}}
}
