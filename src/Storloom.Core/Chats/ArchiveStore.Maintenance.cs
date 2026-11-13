using System.Text.Json;
namespace Storloom.Core.Chats;

internal sealed record ArchiveStorageStatistics(long Messages,int LogicalFiles,long ContentBytes,int Packs,long PackBytes,long IndexBytes,long OtherBytes,bool Pending);
internal sealed partial class ArchiveStore {
    internal ArchiveStorageStatistics Statistics(){
        long packs=0,index=0,other=0;int count=0;foreach(string path in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)){PathPolicy.AssertNoLinks(path);long bytes=new FileInfo(path).Length;if(path.EndsWith(".pack",StringComparison.Ordinal)){packs+=bytes;count++;}else if(PathPolicy.Under(path,Path.Combine(root,"query")))index+=bytes;else other+=bytes;}
        var files=query.Files();return new(query.MessageCount,files.Count,files.Sum(f=>f.Length),count,packs,index,other,File.Exists(Path.Combine(root,".write-task.json")));
    }
    // Explicit streaming copy. The source, including its history, is retained. Only
    // the destination's complete head is published; interrupted copies can resume.
    internal void RepackTo(ArchiveStore destination,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        if(root.Equals(destination.root,StringComparison.OrdinalIgnoreCase)||PathPolicy.Under(root,destination.root)||PathPolicy.Under(destination.root,root))throw new IOException("Repack source and destination must be separate.");
        using var lease=Lease(readOnly:true);using var writer=destination.BeginWrite(cancellation,"repack:"+Id+":"+Head,progress);var files=query.Files();long total=files.Sum(f=>f.Length),bytes=0;int done=0;
        foreach(var (conversation,_) in query.Conversations())foreach(var row in ReaderRows(conversation)){OperationPause.Check(cancellation);writer.AddStored(row);if(++done%512==0)progress?.Report(new("archive-repack-messages",done,0,TotalFiles:(int)Math.Min(int.MaxValue,query.MessageCount)));}
        var versions=new Dictionary<string,ArchiveFileEntry>(StringComparer.Ordinal);var snapshots=new List<NativeSnapshot>();
        foreach(var file in files.Where(f=>f.Path.StartsWith("native-snapshots/",StringComparison.Ordinal))){using var input=OpenFile(file.Path);snapshots.Add(ReadNativeSnapshot(input));}
        foreach(var snapshot in snapshots)foreach(var file in snapshot.Files){OperationPause.Check(cancellation);if(versions.ContainsKey(file.Version))continue;using var input=new ChunkStream(file.Length,query.FileParts(file.Version),id=>ReadObject(id),()=>{});versions[file.Version]=writer.Put("native-history/"+file.Version,input,file.SourceStamp);}
        done=0;foreach(var file in files){OperationPause.Check(cancellation);if(file.Path=="native-current.json"||file.Path.StartsWith("native-snapshots/",StringComparison.Ordinal)||file.Path.StartsWith("native-history/",StringComparison.Ordinal)||file.Path.StartsWith("native-manifest-pages/",StringComparison.Ordinal))continue;using var input=OpenFile(file.Path);writer.Put(file.Path,input,file.SourceStamp,advance:n=>{bytes+=n;progress?.Report(new("archive-repack-files",done,bytes,total,files.Count,file.Path));});done++;}
        foreach(var snapshot in snapshots){var updated=snapshot with{Files=snapshot.Files.Select(f=>versions[f.Version] with{Path=f.Path}).ToArray()};WriteNativeSnapshot(writer,updated,files.Any(f=>f.Path=="native-current.json")&&NativeCurrent().Id==snapshot.Id);}
        writer.Complete(new(JsonFiles.Hash(new{Id,Head}).ToLowerInvariant(),"complete",files.Count,0,query.MessageCount,files.Count,total,ImportCursor?.SourceIdentity));
        if(snapshots.Count>0)destination.MarkNative();progress?.Report(new("archive-repack-files",files.Count,total,total,files.Count));
    }
}
