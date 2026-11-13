using System.Security.Cryptography;
using System.Text.Json;

namespace Storloom.Core.Chats;

// Internal integration surface while the migration/recovery format is being validated.
internal sealed partial class ArchiveStore:IDisposable {
    private sealed record Configuration(int Version,string Id,bool Protected,string Kind="reading");
    internal const int ChunkBytes=1024*1024;
    private readonly string root;
    private readonly ArchiveCommitLog log;
    private ArchiveQueryCache query;
    private FileStream? queryLease;
    private readonly ArchiveKeyring? keys;
    private readonly byte[]? objectKey;
    private bool disposed;
    private readonly int formatVersion;
    internal int Workers {get;set;}=2;
    internal string Id{get;}
    private string? publishedHead;
    internal string? Head=>publishedHead;
    internal ArchiveImportCursor? ImportCursor=>query.Cursor;
    private ArchiveStore(string root,Configuration config,ArchiveKeyring? keys,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        this.root=root;this.keys=keys;Id=config.Id;formatVersion=config.Version;using var projectionLease=LeaseProjection(root);log=new(root);publishedHead=log.Head;
        string cache=ProjectionDirectory(root,publishedHead);if(!Directory.Exists(cache))Directory.CreateDirectory(cache);PathPolicy.AssertNoLinks(cache);
        byte[]? queryKey=keys?.DomainKey("query/private");
        queryLease=LeaseQuery(cache);
        try{query=new(cache,config.Protected,queryKey);objectKey=keys?.DomainKey("objects/private");}
        catch{queryLease.Dispose();throw;}finally{if(queryKey!=null)CryptographicOperations.ZeroMemory(queryKey);}
        try{query.Replay(log,publishedHead,Decode,cancellation,progress,DecodeProjection);}catch{Dispose();throw;}
    }
    internal static ArchiveStore Create(string directory,ReadOnlySpan<char> password=default,ChatVaultKeyMode keyMode=ChatVaultKeyMode.Password){
        string root=PathPolicy.Normalize(directory);PathPolicy.AssertNoLinks(Path.GetDirectoryName(root)!);
        if(Directory.Exists(root)||File.Exists(root))throw new IOException("A new archive directory is required.");
        if(!password.IsEmpty||keyMode!=ChatVaultKeyMode.Password)ChatVault.ValidateCredential(password,keyMode);
        Directory.CreateDirectory(root);ArchiveKeyring? keys=null;
        try{
            if(!password.IsEmpty)keys=ArchiveKeyring.Create(root,password,keyMode);
            var config=new Configuration(3,keys?.Id??Guid.NewGuid().ToString("N"),keys!=null);JsonFiles.Write(Path.Combine(root,"archive.json"),config);
            return new(root,config,keys);
        }catch{keys?.Dispose();throw;}
    }
    internal static ArchiveStore Open(string directory,ReadOnlySpan<char> password=default,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        string root=PathPolicy.Normalize(directory);PathPolicy.AssertNoLinks(root);string marker=Path.Combine(root,"archive.json");PathPolicy.AssertNoLinks(marker);
        if(File.Exists(Path.Combine(root,"backup-task.json"))&&!File.Exists(Path.Combine(root,"HEAD")))throw new IOException("The first backup snapshot has not been published.");
        if(new FileInfo(marker).Length>4096)throw new InvalidDataException("Invalid archive marker.");
        var config=JsonFiles.Read<Configuration>(marker);if(config.Kind is not ("reading" or "native")||config.Version is not (2 or 3)||!Guid.TryParseExact(config.Id,"N",out _))throw new InvalidDataException("Unsupported archive format.");
        ArchiveKeyring? keys=config.Protected?ArchiveKeyring.Open(root,password):null;
        try{if(keys!=null&&keys.Id!=config.Id)throw new InvalidDataException("Archive keyring identity mismatch.");return new(root,config,keys,progress,cancellation);}catch{keys?.Dispose();throw;}
    }
    private FileStream Lease(bool readOnly=false){ObjectDisposedException.ThrowIf(disposed,this);string file=Path.Combine(root,"operation.lock");if(File.Exists(file))PathPolicy.AssertNoLinks(file);return new(file,FileMode.OpenOrCreate,readOnly?FileAccess.Read:FileAccess.ReadWrite,readOnly?FileShare.Read:FileShare.None);}
    private byte[] ProtectRecord(byte[] bytes,string purpose)=>keys==null?bytes.ToArray():keys.Protect(bytes,purpose);
    private byte[] UnprotectRecord(byte[] bytes,string purpose)=>keys==null?bytes.ToArray():keys.Unprotect(bytes,purpose);
    private ArchiveDelta DecodeProjection(Stream input)=>ArchivePackCodec.DecodeProjection(input,UnprotectRecord);
    private ArchiveDelta Decode(byte[] bytes){if(ArchivePackCodec.IsBinary(bytes))return ArchivePackCodec.Decode(bytes,UnprotectRecord);byte[] plain=keys==null?bytes:keys.Unprotect(bytes,"data/private");try{using var doc=JsonDocument.Parse(plain);return doc.RootElement.ValueKind==JsonValueKind.Array?new(1,JsonSerializer.Deserialize<ArchiveQueryCache.Message[]>(plain)!,[],[],[]):JsonSerializer.Deserialize<ArchiveDelta>(plain)??throw new InvalidDataException("Invalid archive delta.");}finally{CryptographicOperations.ZeroMemory(plain);}}
    private string ObjectId(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(objectKey==null?SHA256.HashData(bytes):HMACSHA256.HashData(objectKey,bytes));
    private void Commit(ArchiveDelta delta)=>CommitTo(delta,log,query);
    private void CommitTo(ArchiveDelta delta,ArchiveCommitLog target,ArchiveQueryCache projection,CancellationToken cancellation=default){
        projection.Validate(delta);
        byte[] plain=formatVersion>=3?[]:JsonSerializer.SerializeToUtf8Bytes(delta);
        try{
            byte[] encoded=formatVersion>=3?ArchivePackCodec.Encode(delta,ProtectRecord,Workers):keys==null?plain:keys.Protect(plain,"data/private");
            string head=target.Append(encoded,projection.Applied);projection.Replay(target,head,Decode,cancellation,projection:DecodeProjection);if(ReferenceEquals(target,log))publishedHead=head;
        }finally{CryptographicOperations.ZeroMemory(plain);foreach(var item in delta.Objects)CryptographicOperations.ZeroMemory(item.Bytes);}
    }
    internal int Upsert(IEnumerable<ArchiveQueryCache.Message> messages,CancellationToken cancellation=default){
        using var lease=Lease();query.Replay(log,log.Head,Decode,cancellation,projection:DecodeProjection);int changed=0;
        var batch=new List<ArchiveQueryCache.Message>();
        void Flush(){if(batch.Count==0)return;Commit(new(1,batch.ToArray(),[],[],[]));changed+=batch.Count;batch.Clear();}
        foreach(var message in messages){OperationPause.Check(cancellation);if(query.Find(message.Id)==message)continue;batch.Add(message);if(batch.Count==512)Flush();}
        Flush();return changed;
    }
    internal IReadOnlyList<ArchiveQueryCache.Message> Page(string conversation,int limit,long afterTime=long.MinValue,string afterId="")=>query.Page(conversation,limit,afterTime,afterId);
    internal IReadOnlyList<ArchiveQueryCache.Message> Search(string text,int limit=100)=>query.Search(text,limit);
    internal Stream OpenFile(string path){
        ObjectDisposedException.ThrowIf(disposed,this);ArchiveQueryCache.ValidatePath(path);
        ArchiveFileEntry entry;IReadOnlyList<string> parts;lock(readGate){entry=query.FileEntry(path)??throw new FileNotFoundException("Archive file not found.");parts=query.FileParts(entry.Version);}
        if(!entry.Complete)throw new IOException("Archive file migration is incomplete.");
        if(parts.Count!=entry.Chunks||(entry.Length+ChunkBytes-1)/ChunkBytes!=parts.Count)throw new InvalidDataException("Invalid archive file chunk count.");
        var reader=new PackReader(this);return new ChunkStream(entry.Length,parts,reader.Read,reader.Dispose);
    }
    private readonly object packGate=new();
    private readonly Dictionary<string,ArchivePackCodec.ObjectDirectory> objectDirectories=new(StringComparer.Ordinal);
    private readonly Dictionary<string,ArchiveDelta> packCache=new(StringComparer.Ordinal);
    private readonly LinkedList<string> packOrder=new();
    private long cachedPackBytes;
    private static long PackBytes(ArchiveDelta pack)=>pack.Objects.Sum(o=>(long)o.Bytes.Length);
    private static void ClearPack(ArchiveDelta pack){foreach(var item in pack.Objects)CryptographicOperations.ZeroMemory(item.Bytes);}
    private byte[] ReadObject(string id,ArchiveQueryCache? projection=null){
        ObjectDisposedException.ThrowIf(disposed,this);(string Commit,int Ordinal,long Bytes) location;lock(readGate)location=(projection??query).Locate(id);
        lock(packGate){
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!packCache.TryGetValue(location.Commit,out var pack)){
                using(var input=log.OpenPayload(location.Commit)){Span<byte> magic=stackalloc byte[8];input.ReadExactly(magic);input.Position=0;if(ArchivePackCodec.IsBinary(magic)){if(!objectDirectories.TryGetValue(location.Commit,out var directory)){directory=ArchivePackCodec.ReadDirectory(input,UnprotectRecord);if(objectDirectories.Count>=4)objectDirectories.Clear();objectDirectories.Add(location.Commit,directory);}byte[] record=ArchivePackCodec.ReadObject(input,location.Ordinal,id,location.Bytes,UnprotectRecord,directory);if(ObjectId(record)!=id){CryptographicOperations.ZeroMemory(record);throw new InvalidDataException("Archive resource authentication failed.");}return record;}}
                pack=Decode(log.Read(location.Commit).Payload);
                long size=PackBytes(pack);
                while(packOrder.First is{} oldest&&(packCache.Count>=2||cachedPackBytes+size>16L*1024*1024)){var previous=packCache[oldest.Value];cachedPackBytes-=PackBytes(previous);packCache.Remove(oldest.Value);packOrder.RemoveFirst();ClearPack(previous);}
                if(size<=16L*1024*1024){packCache.Add(location.Commit,pack);packOrder.AddLast(location.Commit);cachedPackBytes+=size;}
            }else{packOrder.Remove(location.Commit);packOrder.AddLast(location.Commit);}
            try{
                if(location.Ordinal<0||location.Ordinal>=pack.Objects.Length)throw new InvalidDataException("Invalid resource locator.");
                var item=pack.Objects[location.Ordinal];if(item.Id!=id||item.Bytes.LongLength!=location.Bytes||ObjectId(item.Bytes)!=id)throw new InvalidDataException("Archive resource authentication failed.");return item.Bytes.ToArray();
            }finally{if(!packCache.ContainsKey(location.Commit))ClearPack(pack);}
        }
    }
    private sealed class PackReader(ArchiveStore owner):IDisposable {
        internal byte[] Read(string id)=>owner.ReadObject(id);
        public void Dispose(){}
    }
    private sealed class ChunkStream(long length,IReadOnlyList<string> parts,Func<string,byte[]> read,Action release):Stream {
        private long position;private int current=-1;private byte[]? chunk;private bool disposed;

        public override bool CanRead=>!disposed;public override bool CanSeek=>!disposed;public override bool CanWrite=>false;public override long Length=>length;
        public override long Position{get=>position;set=>Seek(value,SeekOrigin.Begin);}
        public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
        public override int Read(Span<byte> buffer){
            ObjectDisposedException.ThrowIf(disposed,this);int total=0;
            while(buffer.Length>0&&position<length){
                int index=checked((int)(position/ChunkBytes));
                if(index!=current){if(chunk!=null)CryptographicOperations.ZeroMemory(chunk);chunk=read(parts[index]);current=index;int expected=(int)Math.Min(ChunkBytes,length-(long)index*ChunkBytes);if(chunk.Length!=expected)throw new InvalidDataException("Archive chunk length mismatch.");}
                int start=(int)(position%ChunkBytes),count=Math.Min(buffer.Length,chunk!.Length-start);chunk.AsSpan(start,count).CopyTo(buffer);buffer=buffer[count..];position+=count;total+=count;
            }
            return total;
        }
        public override long Seek(long offset,SeekOrigin origin){ObjectDisposedException.ThrowIf(disposed,this);long next=checked((origin switch{SeekOrigin.Begin=>0,SeekOrigin.Current=>position,SeekOrigin.End=>length,_=>throw new ArgumentOutOfRangeException(nameof(origin))})+offset);if(next<0||next>length)throw new IOException("Archive seek is outside the file.");return position=next;}
        protected override void Dispose(bool disposing){if(chunk!=null){CryptographicOperations.ZeroMemory(chunk);chunk=null;}release();disposed=true;base.Dispose(disposing);}
        public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    private static FileStream LeaseProjection(string root,bool exclusive=false){string path=Path.Combine(root,"projection.lock");if(File.Exists(path))PathPolicy.AssertNoLinks(path);return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,exclusive?FileShare.None:FileShare.ReadWrite);}
    private static FileStream LeaseQuery(string directory){string path=Path.Combine(directory,".index-use.lock");if(File.Exists(path))PathPolicy.AssertNoLinks(path);return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite);}
    public void Dispose(){lock(readGate){if(disposed)return;disposed=true;query.Dispose();queryLease?.Dispose();lock(packGate){foreach(var pack in packCache.Values)ClearPack(pack);packCache.Clear();objectDirectories.Clear();packOrder.Clear();cachedPackBytes=0;keys?.Dispose();if(objectKey!=null)CryptographicOperations.ZeroMemory(objectKey);}}}
}
