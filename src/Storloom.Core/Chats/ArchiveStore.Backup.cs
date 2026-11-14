using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Storloom.Core.Chats;

internal sealed record ArchiveBackupResult(string? Head,int CopiedFiles,int ReusedFiles,long ProcessedBytes);
internal sealed partial class ArchiveStore {
    // Only wrapped key metadata is retained, never a password or unwrapped key.
    private sealed record BackupTask(string Id,string? Head,string? Previous,string Configuration,string? Keyring,string Proof="");
    private string BackupProof(BackupTask task){byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(task with{Proof=""});if(keys==null)return Convert.ToHexStringLower(SHA256.HashData(bytes));byte[] secret=keys.DomainKey("backup/task");try{return Convert.ToHexStringLower(HMACSHA256.HashData(secret,bytes));}finally{CryptographicOperations.ZeroMemory(secret);}}
    internal ArchiveBackupResult BackupTo(string destination,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        using var sourceLease=Lease(readOnly:true);destination=ResolveRoot(destination);
        if(destination.Equals(root,StringComparison.OrdinalIgnoreCase)||PathPolicy.Under(destination,root)||PathPolicy.Under(root,destination))throw new InvalidOperationException("Backup directories must be separate.");
        PathPolicy.AssertNoLinks(Path.GetDirectoryName(destination)!);string taskPath=Path.Combine(destination,"backup-task.json");
        if(Directory.Exists(destination)){
            PathPolicy.AssertNoLinks(destination);string marker=Path.Combine(destination,"archive.json");
            if(File.Exists(marker)){PathPolicy.AssertNoLinks(marker);var config=JsonFiles.Read<Configuration>(marker);if(config.Id!=Id||config.Version is not (2 or 3)||config.Protected!=(keys!=null))throw new InvalidDataException("Backup belongs to another archive.");}
            else if(!File.Exists(taskPath))throw new IOException("Choose a new backup directory or this archive's existing backup.");
        }else Directory.CreateDirectory(destination);
        string lockPath=Path.Combine(destination,"operation.lock");if(File.Exists(lockPath))PathPolicy.AssertNoLinks(lockPath);
        using var destinationLease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(Directory.Exists(Path.Combine(root,"packs")))ArchiveCommitLog.CreateObjectDirectories(destination);var target=new ArchiveCommitLog(destination);BackupTask task;
        if(File.Exists(taskPath)){
            PathPolicy.AssertNoLinks(taskPath);if(new FileInfo(taskPath).Length>16384)throw new InvalidDataException("Invalid backup task size.");task=JsonFiles.Read<BackupTask>(taskPath);
            if(task.Id!=Id||task.Proof!=BackupProof(task))throw new InvalidDataException("Backup task identity or integrity check failed.");
        }else{
            string configurationPath=Path.Combine(root,"archive.json");PathPolicy.AssertNoLinks(configurationPath);
            if(new FileInfo(configurationPath).Length>4096)throw new InvalidDataException("Invalid archive configuration size.");
            string? keyring=null;if(keys!=null){string keyringPath=Path.Combine(root,"keyring.json");PathPolicy.AssertNoLinks(keyringPath);if(new FileInfo(keyringPath).Length>4096)throw new InvalidDataException("Invalid keyring size.");keyring=File.ReadAllText(keyringPath);}
            task=new(Id,log.Head,target.Head,File.ReadAllText(configurationPath),keyring);task=task with{Proof=BackupProof(task)};JsonFiles.Write(taskPath,task);
        }
        var taskConfig=JsonSerializer.Deserialize<Configuration>(task.Configuration,JsonFiles.Options)??throw new InvalidDataException("Invalid backup configuration.");
        if(taskConfig.Id!=Id||taskConfig.Version is not (2 or 3)||taskConfig.Protected!=(keys!=null)||(task.Keyring!=null)!=(keys!=null))throw new InvalidDataException("Invalid backup protection metadata.");
        // A paused task finishes its captured snapshot even if the source advanced meanwhile.
        _ = log.CommitsSince(task.Head,log.Head).Count();
        if(target.Head!=task.Previous&&target.Head!=task.Head)throw new IOException("Backup destination changed since its checkpoint.");
        var copies=new Dictionary<string,long>(StringComparer.Ordinal);
        foreach(var entry in log.CommitsSince(task.Previous,task.Head)){
            copies[entry.Id+".commit"]=new FileInfo(log.ObjectPath(entry.Id,".commit")).Length;
            copies[entry.Entry.Payload+".pack"]=entry.Entry.Bytes;
        }
        long total=0;foreach(var entry in copies)total=checked(total+entry.Value*(File.Exists(target.ObjectPath(entry.Key[..64],entry.Key[64..]))?1:2));
        int done=0,copied=0,reused=0;long bytes=0;
        void Report(string? path=null)=>progress?.Report(new("archive-store-backup",done,bytes,total,copies.Count,path));
        Report();
        foreach(var entry in copies){
            OperationPause.Check(cancellation);string source=log.ObjectPath(entry.Key[..64],entry.Key[64..]),targetPath=target.ObjectPath(entry.Key[..64],entry.Key[64..]);PathPolicy.AssertNoLinks(source);
            bool existed=File.Exists(targetPath);CopyImmutable(source,targetPath,entry.Key[..64],entry.Value,cancellation,n=>{bytes+=n;Report(entry.Key);});
            if(existed)reused++;else copied++;done++;Report(entry.Key);
        }
        OperationPause.Check(cancellation);
        PublishBackupText(destination,"archive.json",task.Configuration);if(task.Keyring!=null)PublishBackupText(destination,"keyring.json",task.Keyring);
        CopyBrowserSnapshot(destination,task.Head,progress,cancellation);
        target.PublishCopiedHead(target.Head,task.Head);File.Delete(taskPath);
        return new(task.Head,copied,reused,bytes);
    }
    private static void PublishBackupText(string root,string relative,string text){
        string target=TransferService.Child(root,relative),temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        if(File.Exists(target)){PathPolicy.AssertNoLinks(target);if(NativeFile.ReadStamp(target).Links!=1)throw new IOException("Shared backup metadata cannot be replaced.");}
        try{using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){var bytes=System.Text.Encoding.UTF8.GetBytes(text);output.Write(bytes);output.Flush(true);}File.Move(temporary,target,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static void CopyImmutable(string source,string destination,string digest,long length,CancellationToken cancellation,Action<int> advanced){
        string temporary=destination+".partial";
        if(File.Exists(destination)){
            PathPolicy.AssertNoLinks(destination);if(NativeFile.ReadStamp(destination).Links!=1)throw new IOException("Shared backup object.");
            using var input=File.OpenRead(destination);if(input.Length!=length||!Convert.ToHexStringLower(TransferService.Hash(input,cancellation,advanced)).Equals(digest,StringComparison.Ordinal))throw new InvalidDataException("Backup object verification failed.");return;
        }
        if(File.Exists(temporary)){PathPolicy.AssertNoLinks(temporary);if(NativeFile.ReadStamp(temporary).Links!=1)throw new IOException("Shared partial backup object.");}
        byte[] buffer=ArrayPool<byte>.Shared.Rent(1024*1024);
        try{
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using(var input=File.OpenRead(source))
            using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){
                if(input.Length!=length)throw new InvalidDataException("Archive object length changed.");
                int count;while((count=input.Read(buffer,0,buffer.Length))>0){OperationPause.Check(cancellation);output.Write(buffer,0,count);hash.AppendData(buffer,0,count);advanced(count);}output.Flush(true);
            }
            if(Convert.ToHexStringLower(hash.GetHashAndReset())!=digest)throw new InvalidDataException("Archive source object verification failed.");
            using(var input=File.OpenRead(temporary))if(Convert.ToHexStringLower(TransferService.Hash(input,cancellation,advanced))!=digest)throw new InvalidDataException("Backup copy verification failed.");
            OperationPause.Check(cancellation);File.Move(temporary,destination,false);
        }finally{ArrayPool<byte>.Shared.Return(buffer,true);}
    }
}
