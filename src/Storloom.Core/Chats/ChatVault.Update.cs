using System.Security.Cryptography;
using System.Text.Json;
namespace Storloom.Core.Chats;
public sealed record ChatArchiveProtection(bool IncludeMedia=true,ChatProtectionSelection? Selection=null);
public sealed partial class ChatVault {
    public ChatArchiveProtection ReadProtection(){
        string file=Path.Combine(store,"protection.enc");
        if(!File.Exists(file)){
            if(JsonFiles.Read<Configuration>(Path.Combine(root,Marker)).Version==2)throw new InvalidOperationException("Choose protection rules before updating this legacy partial vault.");
            return new(!entries.Any(e=>e.Path.StartsWith("assets/",StringComparison.Ordinal)&&!committed.ContainsKey(e.Id)));
        }
        byte[] bytes=ReadEnvelope(file,key,"protection");try{return JsonSerializer.Deserialize<ChatArchiveProtection>(bytes)??throw new InvalidDataException("Invalid protection policy.");}finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private void WriteProtection(ChatArchiveProtection policy){byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(policy);string target=Path.Combine(store,"protection.enc"),temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";try{if(File.Exists(target)){PathPolicy.AssertNoLinks(target);if(NativeFile.ReadStamp(target).Links!=1)throw new IOException("Shared protection metadata.");}WriteEnvelope(temporary,key,bytes,"protection");File.Move(temporary,target,true);}finally{CryptographicOperations.ZeroMemory(bytes);if(File.Exists(temporary))File.Delete(temporary);}}
    public bool MetadataProtected=>!UnplannedFiles().Any()&&entries.Where(e=>e.Selected&&!e.Path.StartsWith("assets/",StringComparison.Ordinal)).All(Protected);
    public IReadOnlyList<string> Files=>entries.Select(e=>e.Path).ToArray();
    internal void RestoreTo(string directory,int workers,IProgress<ScanProgress>? progress,CancellationToken cancellation,bool skipProtectedMedia=false){
        using var lease=new FileStream(Path.Combine(root,".export.lock"),FileMode.OpenOrCreate,FileAccess.Read,FileShare.Read);
        var selected=entries.Where(e=>!(skipProtectedMedia&&e.Path.StartsWith("assets/",StringComparison.Ordinal)&&committed.ContainsKey(e.Id))).ToArray();
        long total=checked(selected.Sum(e=>e.Stamp.Bytes)*2),bytes=0;int done=0;var reportGate=new object();
        void Report(string? detail=null){lock(reportGate)progress?.Report(new("archive-workspace",Volatile.Read(ref done),Interlocked.Read(ref bytes),total,selected.Length,detail));}
        string temporaryRoot=Path.Combine(Path.GetDirectoryName(directory)!,"restore");Directory.CreateDirectory(temporaryRoot);PathPolicy.AssertNoLinks(temporaryRoot);Report();
        Parallel.ForEach(selected,new ParallelOptions{MaxDegreeOfParallelism=Math.Clamp(workers,1,8),CancellationToken=cancellation},entry=>{
            OperationPause.Check(cancellation);string target=TransferService.Child(directory,entry.Path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            ChatWorkspaceCopy.File(()=>OpenRead(entry.Path),target,Path.Combine(temporaryRoot,entry.Id+".part"),cancellation,count=>{Interlocked.Add(ref bytes,count);if(entry.Stamp.Bytes>=1024*1024)Report();});
            Interlocked.Increment(ref done);Report(entry.Path);
        });
    }
    private void ImportProtectedMedia(ChatVault previous,IProgress<ScanProgress>? progress,CancellationToken cancellation){
        var added=new List<Entry>();foreach(var entry in previous.entries.Where(e=>e.Path.StartsWith("assets/",StringComparison.Ordinal)&&!paths.ContainsKey(e.Path))){
            OperationPause.Check(cancellation);if(!previous.committed.TryGetValue(entry.Id,out string? digest))throw new IOException("Unprotected media cannot be reused as ciphertext.");
            // The copied ciphertext must authenticate against the previous commit before it is accepted.
            // Reading the same source plaintext first adds another full decryption pass without a stronger check.
            PathPolicy.AssertNoLinks(previous.Cipher(entry));
            File.Copy(previous.Cipher(entry),Cipher(entry),false);using(var copied=OpenProtected(entry))if(Convert.ToHexString(TransferService.Hash(copied,cancellation))!=digest)throw new CryptographicException("Copied media authentication failed.");
            byte[] plain=JsonSerializer.SerializeToUtf8Bytes(new Commit(entry.Id,digest));try{byte[] record=Seal(key,plain,"commit");using var log=new FileStream(Path.Combine(store,"commits.enc"),FileMode.Append,FileAccess.Write,FileShare.Read);using var writer=new BinaryWriter(log);writer.Write(record.Length);writer.Write(record);writer.Flush();log.Flush(true);validLogLength=log.Length;}finally{CryptographicOperations.ZeroMemory(plain);}
            committed[entry.Id]=digest;added.Add(entry with{Selected=true,PublicFile=null,PublicStamp=null});progress?.Report(new("archive-reuse-encrypted",added.Count,0));
        }
        entries=entries.Concat(added).ToArray();paths=entries.ToDictionary(e=>e.Path,StringComparer.Ordinal);
    }
    private void ReuseCiphertext(ChatVault previous,IProgress<ScanProgress>? progress,CancellationToken cancellation){
        if(!CryptographicOperations.FixedTimeEquals(key,previous.key))throw new CryptographicException("Vault update password mismatch.");
        for(int n=0;n<entries.Length;n++){
            OperationPause.Check(cancellation);var entry=entries[n];
            if(!entry.Selected||!previous.paths.TryGetValue(entry.Path,out var old)||!previous.committed.TryGetValue(old.Id,out string? expected))continue;
            using(var input=File.OpenRead(Plain(entry)))if(Convert.ToHexString(TransferService.Hash(input,cancellation))!=expected)continue;
            PathPolicy.AssertNoLinks(previous.Cipher(old));
            var reused=entry with{Id=old.Id};string target=Cipher(reused);File.Copy(previous.Cipher(old),target,false);
            using(var copy=new ProtectedStream(target,entry.Path,entry.Stamp.Bytes,key))if(Convert.ToHexString(TransferService.Hash(copy,cancellation))!=expected)throw new CryptographicException("Reused ciphertext verification failed.");
            entries[n]=reused;byte[] plain=JsonSerializer.SerializeToUtf8Bytes(new Commit(reused.Id,expected));
            try{byte[] record=Seal(key,plain,"commit");using var log=new FileStream(Path.Combine(store,"commits.enc"),FileMode.Append,FileAccess.Write,FileShare.Read);using var writer=new BinaryWriter(log);writer.Write(record.Length);writer.Write(record);writer.Flush();log.Flush(true);validLogLength=log.Length;}finally{CryptographicOperations.ZeroMemory(plain);}
            committed[reused.Id]=expected;progress?.Report(new("archive-reuse-encrypted",n+1,0,TotalFiles:entries.Length));
        }
        paths=entries.ToDictionary(e=>e.Path,StringComparer.Ordinal);
    }
}
