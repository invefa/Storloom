using System.Security.Cryptography;
using System.Text.Json;
namespace Storloom.Core.Chats;

internal static class CompactArchiveWorkflow {
    internal static ChatArchiveProject Backup(ChatArchiveProject project,string taskFile,string? password,int workers,Action<ChatArchiveProject> checkpoint,IProgress<ScanProgress>? progress,CancellationToken cancellation,bool checkClients,ChatVaultKeyMode keyMode=ChatVaultKeyMode.Password){
        string account=Path.Combine(PathPolicy.Normalize(project.BackupRoot),project.Platform.ToString(),project.Account);if(account.Equals(project.Source,StringComparison.OrdinalIgnoreCase)||PathPolicy.Under(account,project.Source)||PathPolicy.Under(project.Source,account))throw new IOException("Native source and output must be separate.");Directory.CreateDirectory(account);PathPolicy.AssertNoLinks(account);
        string target=project.NativeArchive??Path.Combine(account,"native-compact-"+project.Id[..12]);
        if(File.Exists(taskFile)){var task=JsonFiles.Read<ChatArchiveTask>(taskFile);if(task.Version!=2||task.ProjectId!=project.Id||!task.BackupOnly||task.Generation!=target)throw new IOException("Native backup task identity changed.");if(task.Secure&&string.IsNullOrEmpty(password))throw new IOException("Unlock the native backup task before continuing.");keyMode=task.KeyMode;}
        if(ArchiveStore.Exists(target))keyMode=ArchiveStore.ReadKeyMode(target);
        JsonFiles.Write(taskFile,new ChatArchiveTask(2,project.Id,target,target,project.Reading,null,"backup",!string.IsNullOrEmpty(password),BackupOnly:true){KeyMode=keyMode});project=project with{TaskFile=taskFile};checkpoint(project);
        using var store=ArchiveStore.Exists(target)?ArchiveStore.Open(target,password.AsSpan(),progress,cancellation):ArchiveStore.Create(target,password.AsSpan(),keyMode);store.Workers=workers;store.BackupNative(project.Source,project.Platform,progress,cancellation,checkClients);
        var completed=project with{NativeArchive=target,TaskFile=null,BackedUpAt=DateTimeOffset.UtcNow};checkpoint(completed);File.Delete(taskFile);return completed;
    }
    internal sealed record SourceProof(string Identity,Dictionary<string,string> Databases);
    internal static ChatArchiveProject Read(ChatArchiveProject project,string taskFile,byte[]? databaseKey,string? password,int workers,Action<ChatArchiveProject> checkpoint,ChatArchiveUpdateRequest request,IProgress<ScanProgress>? progress,CancellationToken cancellation,bool? defaultDark,bool checkClients,bool downloadAvatars){
        if(request.Protection?.Selection!=null)throw new InvalidOperationException("Fine-grained protection requires the compatible folder format.");
        if(project.Id!=ChatArchiveProject.Identity(project.Platform,project.Source))throw new InvalidDataException("Archive source identity changed.");
        string output=PathPolicy.Normalize(string.IsNullOrWhiteSpace(project.ReadingRoot)?project.BackupRoot:project.ReadingRoot),account=Path.Combine(output,project.Platform.ToString(),project.Account);
        if(PathPolicy.Under(account,project.Source)||PathPolicy.Under(project.Source,account)||account.Equals(project.Source,StringComparison.OrdinalIgnoreCase))throw new IOException("Source and archive must be separate.");Directory.CreateDirectory(account);PathPolicy.AssertNoLinks(account);
        ChatArchiveTask? retained=File.Exists(taskFile)?JsonFiles.Read<ChatArchiveTask>(taskFile):null;bool fresh=request.CreateCompact||retained?.CreateCompact==true;
        string target=retained?.Generation??(fresh?null:project.Reading)??Path.Combine(account,"reading-compact-"+project.Id[..12]+"-"+Guid.NewGuid().ToString("N")[..8]),work=Path.Combine(account,".compact-work-"+project.Id[..12]);bool secure=ArchiveStore.Exists(target)?ArchiveStore.NeedsPassword(target):(request.Mode??project.Protection)!=ChatArchiveProtectionMode.None;
        if(ArchiveStore.Exists(target)&&ArchiveStore.IsNative(target))throw new IOException("A native backup cannot be used as a reading archive.");
        if(secure&&string.IsNullOrEmpty(password))throw new InvalidOperationException("Unlock the compact archive before updating.");
        if(ArchiveStore.Exists(target)&&request.Mode is{} desired&&(desired!=ChatArchiveProtectionMode.None)!=secure)throw new InvalidOperationException("Change compact archive protection separately before updating.");
        if(request.ProtectionOnly)throw new InvalidOperationException("Use compact archive protection conversion for this operation.");
        if(!request.DirectReading)throw new InvalidOperationException("Materialize the selected native backup source before compact reading.");
        if(retained!=null&&(retained.Version!=2||retained.ProjectId!=project.Id||retained.Generation!=target||retained.Work!=work||retained.BackupOnly||retained.ProtectionOnly||!string.Equals(target,project.Reading,StringComparison.OrdinalIgnoreCase)&&(!string.Equals(Path.GetDirectoryName(target),account,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(target).StartsWith("reading-compact-"+project.Id[..12]+"-",StringComparison.Ordinal))||request.SourceSnapshot!=null&&request.SourceSnapshot!=retained.SourceSnapshot))throw new IOException("Compact reading task identity changed.");
        string? snapshotSource=request.SourceSnapshot??retained?.SourceSnapshot;bool rebuild=request.Rebuild||retained?.Rebuild==true;string sourceRoot=snapshotSource??project.Source;
        if(target.Equals(project.Source,StringComparison.OrdinalIgnoreCase)||PathPolicy.Under(target,project.Source)||PathPolicy.Under(project.Source,target))throw new IOException("Reading source and destination must be separate.");
        using var source=ChatSourceLease.Open(sourceRoot,project.Platform,cancellation,checkClients&&snapshotSource==null,progress,identityRoot:project.Source);
        var proof=new SourceProof(project.Id,source.Hashes);string identity=JsonFiles.Hash(new{proof.Identity,Databases=proof.Databases.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToArray()}).ToLowerInvariant();
        var keyMode=retained?.KeyMode??request.KeyMode;
        using var store=ArchiveStore.Exists(target)?ArchiveStore.Open(target,password.AsSpan(),progress,cancellation):ArchiveStore.Create(target,secure?password.AsSpan():default,secure?keyMode:ChatVaultKeyMode.Password);store.Workers=workers;
        SourceProof? prior=null;try{using var text=new StreamReader(store.OpenFile(".source.json"));prior=JsonSerializer.Deserialize<SourceProof>(text.ReadToEnd());}catch(FileNotFoundException){}
        if(prior?.Identity!=null&&prior.Identity!=project.Id)throw new IOException("Compact archive belongs to another source.");
        bool retryMedia=false;try{using var report=store.OpenFile("export-report.json");retryMedia=(JsonSerializer.Deserialize<ChatExportResult>(report,JsonFiles.Options)?.UnresolvedMedia??0)>0;}catch(FileNotFoundException){}
        bool Changed(string relative)=>retryMedia||rebuild||prior==null||!prior.Databases.TryGetValue(relative,out string? hash)||!source.Hashes.TryGetValue(relative,out string? value)||hash!=value||prior.Databases.GetValueOrDefault(relative+"-wal")!=source.Hashes.GetValueOrDefault(relative+"-wal");
        if(prior!=null&&retained==null&&!retryMedia&&!rebuild&&prior.Databases.OrderBy(p=>p.Key).SequenceEqual(source.Hashes.OrderBy(p=>p.Key))){var unchanged=project with{Reading=target,ReadingStorage=ChatStorageFormat.Compact,TaskFile=null,Protection=secure?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None,ProtectionKnown=true,IncludeMedia=true};checkpoint(unchanged);if(File.Exists(taskFile))File.Delete(taskFile);progress?.Report(new("archive-compact-unchanged",1,0,0,1));return unchanged;}
        JsonFiles.Write(taskFile,new ChatArchiveTask(2,project.Id,work,target,project.Reading,project.Manifest,"reading",secure){KeyMode=store.KeyMode,DirectReading=true,SourceSnapshot=snapshotSource,Rebuild=rebuild,CreateCompact=fresh});project=project with{TaskFile=taskFile};checkpoint(project);
        if(!Directory.Exists(work))ChatArchiveWorkspace.Create(work,secure);else{PathPolicy.AssertNoLinks(work);if(secure)ChatArchiveWorkspace.AssertEncrypted(work);}
        using(var writer=store.BeginWrite(cancellation,identity,progress)){
            var exporter=new ChatExporter(workers,true,downloadAvatars,defaultDark);var result=exporter.ExportCompact(source,work,project.Platform,databaseKey,writer,progress,cancellation,Changed,project.Account);
            if(result.CanResume){cancellation.ThrowIfCancellationRequested();throw new IOException("Compact export is incomplete; retained progress can be continued.");}
            writer.Flush();result=result with{Messages=writer.MessageCount,MediaFiles=writer.AssetCount};writer.Bytes("export-report.json",JsonSerializer.SerializeToUtf8Bytes(result,JsonFiles.Options));writer.Bytes(".source.json",JsonSerializer.SerializeToUtf8Bytes(proof));
            writer.Complete(new(identity,"complete",0,0,result.Messages,result.MediaFiles,writer.WrittenBytes,project.Id));
        }
        var completed=project with{Reading=target,ReadingStorage=ChatStorageFormat.Compact,ReadableAt=DateTimeOffset.UtcNow,TaskFile=null,Protection=secure?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None,ProtectionKnown=true,IncludeMedia=true};checkpoint(completed);if(File.Exists(taskFile))File.Delete(taskFile);
        // This directory contains only this task's derived spools and conversions.
        if(PathPolicy.Under(work,account)){foreach(string path in Directory.EnumerateFileSystemEntries(work,"*",SearchOption.AllDirectories))PathPolicy.AssertNoLinks(path);Directory.Delete(work,true);}return completed;
    }
}
