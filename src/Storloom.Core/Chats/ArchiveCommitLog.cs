using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Storloom.Core.Chats;

/// <summary>
/// Immutable bounded delta storage. Payloads must already belong to one protection domain;
/// this layer deliberately never receives passwords or opens source databases.
/// </summary>
internal sealed class ArchiveCommitLog {
    internal const int MaximumPayloadBytes=64*1024*1024;
    internal sealed record Commit(int Version,string? Parent,string Payload,long Bytes,string? MetadataHash=null);
    internal sealed record Delta(string Id,Commit Metadata,byte[] Payload);
    private readonly string root,headName;
    internal ArchiveCommitLog(string directory,string headName="HEAD"){
        if(headName is not ("HEAD" or ".write-head"))throw new ArgumentException("Invalid head name.");this.headName=headName;
        root=PathPolicy.Normalize(directory);PathPolicy.AssertNoLinks(root);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);
    }
    private string Child(string name){PathPolicy.AssertNoLinks(root);string path=TransferService.Child(root,name);if(File.Exists(path)||Directory.Exists(path))PathPolicy.AssertNoLinks(path);return path;}
    private static bool IsId(string? id)=>id is{Length:64}&&id.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(ReadOnlySpan<byte> value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    internal string? Head {
        get{
            string file=Child(headName);if(!File.Exists(file))return null;
            if(new FileInfo(file).Length!=64)throw new InvalidDataException("Invalid archive head.");
            string id=File.ReadAllText(file,Encoding.ASCII);if(!IsId(id))throw new InvalidDataException("Invalid archive head.");return id;
        }
    }
    // One archive writer, with publication only after both immutable files are flushed.
    // Filesystem metadata durability across power loss still needs platform-specific validation.
    internal string Append(ReadOnlySpan<byte> payload,string? expectedHead,Action<string>? checkpoint=null){
        if(payload.Length is <1 or >MaximumPayloadBytes)throw new ArgumentOutOfRangeException(nameof(payload));
        using var lease=new FileStream(Child("writer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        string? parent=Head;if(parent!=expectedHead)throw new InvalidOperationException("Archive advanced since the import checkpoint.");
        string content=Hash(payload);WriteImmutable(content+".pack",payload);checkpoint?.Invoke("payload");
        byte[] descriptor=JsonSerializer.SerializeToUtf8Bytes(new Commit(ArchivePackCodec.IsBinary(payload)?2:1,parent,content,payload.Length,ArchivePackCodec.IsBinary(payload)?Hash(payload[..checked(16+System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload[8..]))]):null));
        string id=Hash(descriptor);WriteImmutable(id+".commit",descriptor);checkpoint?.Invoke("commit");
        WriteAtomic(headName,Encoding.ASCII.GetBytes(id));return id;
    }
    private void WriteImmutable(string name,ReadOnlySpan<byte> bytes){
        string target=Child(name);
        if(File.Exists(target)){
            using var existing=File.OpenRead(target);
            if(existing.Length!=bytes.Length||!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing),SHA256.HashData(bytes)))throw new InvalidDataException("Archive object changed.");
            return;
        }
        WriteAtomic(name,bytes,replace:false);
    }
    private void WriteAtomic(string name,ReadOnlySpan<byte> bytes,bool replace=true){
        string target=Child(name),temporary=Child("write-"+Guid.NewGuid().ToString("N")+".tmp");
        try{
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,64*1024,FileOptions.SequentialScan)){output.Write(bytes);output.Flush(true);}
            if(File.Exists(target)&&NativeFile.ReadStamp(target).Links!=1)throw new IOException("Shared archive metadata cannot be replaced.");
            File.Move(temporary,target,replace);
        }finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private byte[] ReadObject(string id,string extension,long expectedSize){
        if(!IsId(id)||expectedSize<1||expectedSize>MaximumPayloadBytes)throw new InvalidDataException("Invalid archive object reference.");
        using var input=File.OpenRead(Child(id+extension));
        if(input.Length!=expectedSize)throw new InvalidDataException("Archive object length mismatch.");
        byte[] bytes=new byte[(int)expectedSize];input.ReadExactly(bytes);
        if(Hash(bytes)!=id)throw new InvalidDataException("Archive object digest mismatch.");return bytes;
    }
    private Commit ReadCommit(string id){
        if(!IsId(id))throw new InvalidDataException("Invalid archive commit reference.");
        long length=new FileInfo(Child(id+".commit")).Length;
        if(length>1024)throw new InvalidDataException("Archive commit exceeds the supported limit.");
        var entry=JsonSerializer.Deserialize<Commit>(ReadObject(id,".commit",length))??throw new InvalidDataException("Missing archive commit.");
        if(entry.Version is not (1 or 2)||entry.Version==2&&!IsId(entry.MetadataHash)||(entry.Parent!=null&&!IsId(entry.Parent))||!IsId(entry.Payload)||entry.Bytes<1||entry.Bytes>MaximumPayloadBytes)throw new InvalidDataException("Invalid archive commit.");
        return entry;
    }
    internal FileStream? OpenProjection(string id){var entry=ReadCommit(id);if(entry.MetadataHash==null)return null;var input=OpenPayload(id);try{Span<byte> header=stackalloc byte[16];input.ReadExactly(header);int size=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[8..]);if(!ArchivePackCodec.IsBinary(header)||size<5||size>MaximumPayloadBytes-16||size>input.Length-16)throw new InvalidDataException("Invalid compact pack header.");byte[] metadata=new byte[16+size];header.CopyTo(metadata);input.ReadExactly(metadata.AsSpan(16));if(Hash(metadata)!=entry.MetadataHash)throw new InvalidDataException("Archive metadata digest mismatch.");input.Position=0;return input;}catch{input.Dispose();throw;}}
    internal FileStream OpenPayload(string id){var entry=ReadCommit(id);string path=Child(entry.Payload+".pack");var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);try{if(input.Length!=entry.Bytes)throw new InvalidDataException("Archive pack length mismatch.");return input;}catch{input.Dispose();throw;}}
    internal Delta Read(string id){var entry=ReadCommit(id);return new(id,entry,ReadObject(entry.Payload,".pack",entry.Bytes));}
    internal void PublishCopiedHead(string? expected,string? head){
        using var lease=new FileStream(Child("writer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(Head!=expected)throw new IOException("Backup destination advanced during publication.");
        if(head==null)return;_ = ReadCommit(head);WriteAtomic(headName,Encoding.ASCII.GetBytes(head));
    }
    // Walk only the suffix after the caller's applied commit, rather than flattening history.
    internal IEnumerable<(string Id,Commit Entry)> CommitsSince(string? applied,string? snapshot){
        if(applied!=null&&!IsId(applied))throw new InvalidDataException("Invalid query checkpoint.");
        if(snapshot!=null&&!IsId(snapshot))throw new InvalidDataException("Invalid archive snapshot.");
        var pending=new Stack<(string Id,Commit Entry)>();var seen=new HashSet<string>(StringComparer.Ordinal);
        string? current=snapshot;
        while(current!=applied){
            if(current==null)throw new InvalidDataException("Query checkpoint is not an ancestor of this archive.");
            if(!seen.Add(current))throw new InvalidDataException("Archive commit cycle.");
            var entry=ReadCommit(current);pending.Push((current,entry));current=entry.Parent;
        }
        foreach(var item in pending)yield return item;
    }
    internal IEnumerable<Delta> Since(string? applied,string? snapshot){foreach(var item in CommitsSince(applied,snapshot))yield return new(item.Id,item.Entry,ReadObject(item.Entry.Payload,".pack",item.Entry.Bytes));}
}
