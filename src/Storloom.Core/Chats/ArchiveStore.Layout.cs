using System.Security.Cryptography;
namespace Storloom.Core.Chats;

internal sealed partial class ArchiveStore {
    // Reads accept either location, so an interrupted same-volume layout change
    // can resume. exFAT may change file IDs on rename; verify the content address then.
    internal static void VerifyMovedObject(string path,FileStamp previous,CancellationToken cancellation=default){
        var current=NativeFile.ReadStamp(path);if(current==previous)return;
        if(current.Bytes!=previous.Bytes||current.Volume!=previous.Volume||current.Links!=1)throw new IOException("Archive object changed during reorganization.");
        using var input=File.OpenRead(path);if(!Convert.ToHexStringLower(TransferService.Hash(input,cancellation)).Equals(Path.GetFileNameWithoutExtension(path),StringComparison.Ordinal))throw new InvalidDataException("Archive object digest changed during reorganization.");
    }
    internal static int OrganizeObjects(string directory,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        string root=ResolveRoot(directory),marker=Path.Combine(root,"archive.json");PathPolicy.AssertNoLinks(marker);if(new FileInfo(marker).Length>4096)throw new InvalidDataException("Invalid archive marker.");var config=JsonFiles.Read<Configuration>(marker);
        if(config.Version is not (2 or 3)||config.Kind is not ("reading" or "native")||!Guid.TryParseExact(config.Id,"N",out _))throw new InvalidDataException("Unsupported archive format.");
        string operation=Path.Combine(root,"operation.lock");if(File.Exists(operation))PathPolicy.AssertNoLinks(operation);using var lease=new FileStream(operation,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);using var projectionLease=LeaseProjection(root,true);var readers=new List<FileStream>();
        string writerLock=Path.Combine(root,"writer.lock");if(File.Exists(writerLock))PathPolicy.AssertNoLinks(writerLock);using var writerLease=new FileStream(writerLock,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        try{
            string queryRoot=Path.Combine(root,"query");if(Directory.Exists(queryRoot)){PathPolicy.AssertNoLinks(queryRoot);foreach(string cache in new[]{queryRoot}.Concat(Directory.EnumerateDirectories(queryRoot,"stage-*",SearchOption.TopDirectoryOnly))){PathPolicy.AssertNoLinks(cache);string path=Path.Combine(cache,".index-use.lock");if(File.Exists(path)){PathPolicy.AssertNoLinks(path);readers.Add(new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None));}}}
            var log=new ArchiveCommitLog(root);string? head=log.Head;ArchiveCommitLog.CreateObjectDirectories(root);var moves=new List<(string Source,string Target,FileStamp Stamp)>();
            foreach(string path in Directory.EnumerateFiles(root,"*",SearchOption.TopDirectoryOnly)){
                string name=Path.GetFileName(path),extension=Path.GetExtension(name);if(extension is not (".pack" or ".commit")||!ArchiveQueryCache.ValidId(Path.GetFileNameWithoutExtension(name)))continue;
                OperationPause.Check(cancellation);PathPolicy.AssertNoLinks(path);var stamp=NativeFile.ReadStamp(path);if(stamp.Links!=1)throw new IOException("Shared archive objects cannot be reorganized.");string target=Path.Combine(root,extension==".pack"?"packs":"commits",name);if(File.Exists(target)||Directory.Exists(target))throw new IOException("Conflicting categorized archive object.");moves.Add((path,target,stamp));
            }
            if(config.Version==3&&config.Kind=="reading"&&File.Exists(Path.Combine(root,"browser","roots.json")))WriteBrowserRuntime(root,config.Id,cancellation);
            long bytes=0,total=moves.Sum(m=>m.Stamp.Bytes);int done=0;progress?.Report(new("archive-layout",0,0,total,moves.Count));
            foreach(var move in moves){OperationPause.Check(cancellation);if(NativeFile.ReadStamp(move.Source)!=move.Stamp)throw new IOException("Archive object changed before reorganization.");File.Move(move.Source,move.Target,false);VerifyMovedObject(move.Target,move.Stamp,cancellation);bytes+=move.Stamp.Bytes;progress?.Report(new("archive-layout",++done,bytes,total,moves.Count));}
            if(log.Head!=head)throw new IOException("Archive snapshot changed during reorganization.");return done;
        }finally{foreach(var reader in readers)reader.Dispose();}
    }
}
