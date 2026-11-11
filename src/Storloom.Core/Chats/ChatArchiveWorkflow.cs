using System.Security.Cryptography;
using System.Text.Json;
namespace Storloom.Core.Chats;
public enum ChatArchiveProtectionMode { None, All, Selected }
public sealed record ChatArchiveProject(string Id,string Account,ChatPlatform Platform,string Source,string BackupRoot){
    public string? Manifest{get;init;}public string? Reading{get;init;}
    public DateTimeOffset? BackedUpAt{get;init;}public DateTimeOffset? ReadableAt{get;init;}
    public ChatArchiveProtectionMode Protection{get;init;}public bool IncludeMedia{get;init;}=true;public bool ProtectionKnown{get;init;}=true;
    public string? TaskFile{get;init;}
    public static string Identity(ChatPlatform platform,string source)=>JsonFiles.Hash(new{platform,source=PathPolicy.Normalize(source).ToUpperInvariant()});
}
public sealed record ChatArchiveTask(int Version,string ProjectId,string Work,string Generation,string? PreviousReading,string? Manifest,string Phase,bool Secure,bool BackupOnly=false,bool ProtectionOnly=false);
public sealed record ChatArchiveUpdateRequest(bool BackupOnly=false,bool Rebuild=false,ChatArchiveProtection? Protection=null,ChatArchiveProtectionMode? Mode=null,bool UseExistingBackup=false,bool ProtectionOnly=false);
/// <summary>Write immutable generations. Only the caller's verified catalog checkpoint switches the visible reader.</summary>
public static class ChatArchiveWorkflow {
    public static ChatArchiveProject Update(ChatArchiveProject project,string taskFile,byte[]? databaseKey,string? password,int workers,
        Action<ChatArchiveProject> checkpoint,ChatArchiveUpdateRequest? request=null,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default,bool? defaultDark=null,Func<string,int>? selectWorkers=null,bool checkRunningClients=true,bool downloadGroupAvatars=false){
        request??=new();if(!System.Text.RegularExpressions.Regex.IsMatch(project.Id,"^[a-f0-9]{64}$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)||project.Id!=ChatArchiveProject.Identity(project.Platform,project.Source)||string.IsNullOrEmpty(project.Account)||Path.GetFileName(project.Account)!=project.Account)throw new InvalidDataException("Invalid archive identity.");workers=Math.Clamp(workers,1,8);int backupWorkers=Math.Clamp(selectWorkers?.Invoke("backup")??workers,1,8),encryptionWorkers=Math.Clamp(selectWorkers?.Invoke("encryption")??workers,1,8);string accountRoot=Path.Combine(PathPolicy.Normalize(project.BackupRoot),project.Platform.ToString(),project.Account);
        if(PathPolicy.Under(accountRoot,project.Source)||PathPolicy.Under(project.Source,accountRoot)||accountRoot.Equals(project.Source,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Archive backup and source must be separate directories.");CreateDirectorySafe(accountRoot);PathPolicy.AssertNoLinks(Path.GetDirectoryName(Path.GetFullPath(taskFile))!);
        string lockPath=Path.Combine(accountRoot,".archive-project-"+project.Id+".lock");PathPolicy.AssertNoLinks(accountRoot);
        if(File.Exists(lockPath))PathPolicy.AssertNoLinks(lockPath);
        using var taskLease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        ChatArchiveTask state;
        if(File.Exists(taskFile)){
            PathPolicy.AssertNoLinks(taskFile);state=JsonFiles.Read<ChatArchiveTask>(taskFile);
            if(state.Version!=1||state.ProjectId!=project.Id||!PathPolicy.Under(state.Work,accountRoot)||!PathPolicy.Under(state.Generation,accountRoot)||Path.GetDirectoryName(state.Generation)!=accountRoot||Path.GetDirectoryName(state.Work)!=accountRoot)throw new InvalidDataException("Archive task identity or workspace boundary changed.");
            string workName=Path.GetFileName(state.Work);if(!workName.StartsWith(".archive-work-",StringComparison.Ordinal)||!Guid.TryParseExact(workName[14..],"N",out var generation)||state.Generation!=Path.Combine(accountRoot,"reading-"+generation.ToString("N")))throw new InvalidDataException("Archive generation identity changed.");
            if(state.PreviousReading!=project.Reading&&state.Phase!="published")throw new InvalidDataException("Archive task no longer matches the current reader.");
        }else{
            bool secure=!request.BackupOnly&&(project.Protection!=ChatArchiveProtectionMode.None||project.Reading!=null&&ChatVault.Exists(project.Reading)||request.Mode is ChatArchiveProtectionMode.All or ChatArchiveProtectionMode.Selected);
            string id=Guid.NewGuid().ToString("N"),work=Path.Combine(accountRoot,".archive-work-"+id),generation=Path.Combine(accountRoot,"reading-"+id);
            state=new(1,project.Id,work,generation,project.Reading,project.Manifest,request.UseExistingBackup&&project.Manifest!=null?"workspace":"backup",secure,request.BackupOnly,request.ProtectionOnly);
            JsonFiles.Write(taskFile,state);project=project with{TaskFile=taskFile};checkpoint(project);
        }
        void Save(string phase){state=state with{Phase=phase};JsonFiles.Write(taskFile,state);}
        ChatVault? previous=null;
        try{
            if(state.Secure&&!state.BackupOnly){
                if(string.IsNullOrEmpty(password))throw new InvalidOperationException("Unlock the archive before continuing this task.");
                if(state.PreviousReading!=null&&ChatVault.Exists(state.PreviousReading))previous=ChatVault.Open(state.PreviousReading,password.AsSpan());
            }
            if(!state.BackupOnly&&!Directory.Exists(state.Work)){
                if(state.Phase is not ("backup" or "workspace"))throw new IOException("The retained update workspace is missing. The previous reading generation is unchanged.");
                ChatArchiveProtection? initialSettings=request.Protection??(previous!=null?previous.ReadProtection():null);
                ChatArchiveWorkspace.Create(state.Work,state.Secure);JsonFiles.Write(Path.Combine(state.Work,"owner.json"),new{project.Id,state.Generation,state.PreviousReading});
                JsonFiles.Write(Path.Combine(state.Work,"policy.json"),new SavedPolicy(request.Mode??project.Protection,initialSettings??new(project.IncludeMedia),request.Rebuild,request.ProtectionOnly));
            }
            if(!state.BackupOnly)PathPolicy.AssertNoLinks(state.Work);
            if(state.Secure&&!state.BackupOnly){ChatArchiveWorkspace.AssertEncrypted(state.Work);VerifyWorkspacePassword(state.Work,password!);}
            if(state.Phase=="backup"){
                progress?.Report(new("archive-backup",0,0));OperationPause.Check(cancellation);
                string destination=Path.Combine(accountRoot,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]+"-raw");
                string[] processes=!checkRunningClients?[]:project.Platform==ChatPlatform.QQ?["QQ"]:["Weixin","WeChat"];
                TransferResult backup;
                if(project.Manifest!=null&&File.Exists(project.Manifest))backup=ChatIncrementalBackup.Create(project.Source,destination,project.Manifest,Path.Combine(accountRoot,"journals"),processes,progress,cancellation,backupWorkers).Backup;
                else{var service=new TransferService(backupWorkers);backup=service.Execute(service.Plan(project.Source,destination,processes:processes,cancellation:cancellation,progress:progress),Path.Combine(accountRoot,"journals"),progress,cancellation);}
                if(backup.Cancelled)throw new OperationCanceledException(cancellation);
                if(backup.Issues.Count>0)throw new IOException("Archive backup did not complete verification.");
                state=state with{Manifest=backup.Manifest};project=project with{Manifest=backup.Manifest,BackedUpAt=DateTimeOffset.UtcNow,TaskFile=taskFile};checkpoint(project);Save(state.BackupOnly?"backup-only-complete":"workspace");
            }
            if(state.Phase=="backup-only-complete"){project=project with{TaskFile=null};checkpoint(project);File.Delete(taskFile);return project;}
            if(state.Manifest==null)throw new InvalidDataException("Archive task has no verified backup.");
            if(state.Secure&&state.Phase is not ("publish" or "published"))ChatArchiveWorkspace.AssertEncrypted(state.Work);
            PathPolicy.AssertNoLinks(Path.Combine(state.Work,"policy.json"));
            var policy=JsonFiles.Read<SavedPolicy>(Path.Combine(state.Work,"policy.json"));string stage=Path.Combine(state.Work,"reader");
            bool sparseMedia=previous!=null&&!policy.Rebuild&&policy.Mode==ChatArchiveProtectionMode.All&&policy.Settings.IncludeMedia&&policy.Settings.Selection==null&&previous.ReadProtection() is {IncludeMedia:true,Selection:null};
            if(state.Phase=="workspace"){
                if(state.PreviousReading!=null&&!policy.Rebuild){
                    Directory.CreateDirectory(stage);
                    if(previous!=null)previous.RestoreTo(stage,workers,progress,cancellation,sparseMedia);
                    else CopyPlain(state.PreviousReading,stage,progress,cancellation);
                }
                Save("reading");
            }
            if(state.Phase=="reading"){
                progress?.Report(new("archive-reading",0,0));
                if(policy.ProtectionOnly){ }
                else if(state.PreviousReading!=null&&!policy.Rebuild)ChatReadingUpdate.Update(state.Manifest,stage,project.Platform,databaseKey,workers,true,true,progress,cancellation,downloadQQGroupAvatars:downloadGroupAvatars,originalReadingDirectory:state.PreviousReading);
                else{
                    bool resume=Directory.Exists(stage)&&File.Exists(Path.Combine(stage,"export-report.json"));
                    var result=new ChatExporter(workers,true,downloadGroupAvatars,defaultReaderDark:defaultDark).ExportVerifiedBackup(state.Manifest,stage,project.Platform,databaseKey,progress,cancellation,resume:resume);
                    if(result.CanResume){if(cancellation.IsCancellationRequested)throw new OperationCanceledException(cancellation);throw new IOException("Reading export is incomplete; continue the archive task.");}
                }
                Save("protection");
            }
            if(state.Phase=="protection"){
                progress?.Report(new("archive-protect",0,0));
                if(policy.Mode!=ChatArchiveProtectionMode.None){
                    if(string.IsNullOrEmpty(password))throw new InvalidOperationException("Enter the archive password to protect the update.");
                    using var next=ChatVault.Exists(stage)?ChatVault.Open(stage,password.AsSpan()):previous!=null?
                        ChatVault.CreateUpdated(stage,password.AsSpan(),previous,cancellation,progress,policy.Settings.Selection,sparseMedia):ChatVault.Create(stage,password.AsSpan(),cancellation,progress,policy.Settings.Selection);
                    var result=next.Encrypt(policy.Settings.IncludeMedia,encryptionWorkers,progress,cancellation);
                    if(policy.Settings.IncludeMedia?!result.SelectionComplete:!next.MetadataProtected)throw new IOException("Selected archive protection is incomplete.");
                }
                Save("publish");
            }
            if(state.Phase=="publish"){
                progress?.Report(new("archive-publish",0,0));OperationPause.Check(cancellation);
                if(!Directory.Exists(state.Generation)){VerifyGeneration(stage,policy.Mode,password);ChatArchiveWorkspace.PreparePublication(stage,state.Secure,cancellation);Directory.Move(stage,state.Generation);}
                // A crash after the directory move is recovered by committing this same generation.
                VerifyGeneration(state.Generation,policy.Mode,password);
                Save("published");
            }
            if(state.Phase!="published")throw new InvalidDataException("Unknown archive task phase.");
            VerifyGeneration(state.Generation,policy.Mode,password);
            project=project with{Manifest=state.Manifest,Reading=state.Generation,ReadableAt=DateTimeOffset.UtcNow,TaskFile=null,Protection=policy.Settings.Selection!=null&&policy.Mode!=ChatArchiveProtectionMode.None?ChatArchiveProtectionMode.Selected:policy.Mode,IncludeMedia=policy.Settings.IncludeMedia,ProtectionKnown=true};checkpoint(project);
            File.Delete(taskFile);return project;
        }finally{previous?.Dispose();}
    }
    private static void CreateDirectorySafe(string path){string? existing=path;while(existing!=null&&!Directory.Exists(existing)&&!File.Exists(existing))existing=Path.GetDirectoryName(existing);if(existing==null)throw new IOException("Missing archive volume.");PathPolicy.AssertNoLinks(existing);Directory.CreateDirectory(path);PathPolicy.AssertNoLinks(path);}
    private sealed record WorkspaceAuthentication(string Salt,string Proof);
    private static void VerifyWorkspacePassword(string directory,string password){
        string file=Path.Combine(directory,"auth.json");byte[] salt=File.Exists(file)?Convert.FromBase64String(JsonFiles.Read<WorkspaceAuthentication>(file).Salt):RandomNumberGenerator.GetBytes(32);
        byte[] derived=Rfc2898DeriveBytes.Pbkdf2(password,salt,600000,HashAlgorithmName.SHA256,32);byte[] proof=HMACSHA256.HashData(derived,"archive-workspace-auth-v1"u8);
        try{if(File.Exists(file)){var saved=JsonFiles.Read<WorkspaceAuthentication>(file);if(!CryptographicOperations.FixedTimeEquals(proof,Convert.FromBase64String(saved.Proof)))throw new CryptographicException("Archive workspace password mismatch.");}else JsonFiles.Write(file,new WorkspaceAuthentication(Convert.ToBase64String(salt),Convert.ToBase64String(proof)));}
        finally{CryptographicOperations.ZeroMemory(derived);CryptographicOperations.ZeroMemory(proof);}
    }
    private sealed record SavedPolicy(ChatArchiveProtectionMode Mode,ChatArchiveProtection Settings,bool Rebuild,bool ProtectionOnly);
    private static void VerifyGeneration(string directory,ChatArchiveProtectionMode mode,string? password){
        string[] required={"messages.jsonl","archive-index.js","messages.html","reader.js","reader.css","export-report.json"};
        if(mode==ChatArchiveProtectionMode.None){if(required.Any(p=>!File.Exists(Path.Combine(directory,p))))throw new IOException("Reading generation is missing required files.");if(!File.Exists(Path.Combine(directory,"messages.jsonl"))||JsonFiles.Read<ChatExportResult>(Path.Combine(directory,"export-report.json")).CanResume)throw new IOException("Reading generation is incomplete.");}
        else{using var vault=ChatVault.Open(directory,(password??"").AsSpan());if(required.Any(p=>!vault.Files.Contains(p))||!vault.MetadataProtected||vault.ReadProtection().IncludeMedia&&!vault.Status.SelectionComplete)throw new IOException("Protected generation is incomplete.");}
    }
    private static void CopyPlain(string source,string destination,IProgress<ScanProgress>? progress,CancellationToken cancellation){
        PathPolicy.AssertNoLinks(source);using var lease=new FileStream(Path.Combine(source,".export.lock"),FileMode.OpenOrCreate,FileAccess.Read,FileShare.Read);
        int done=0;foreach(string file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)){
            OperationPause.Check(cancellation);string relative=Path.GetRelativePath(source,file);if(relative==".export.lock")continue;PathPolicy.AssertNoLinks(file);
            string target=TransferService.Child(destination,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if(File.Exists(target)){PathPolicy.AssertNoLinks(target);using var a=File.OpenRead(file);using var b=File.OpenRead(target);if(!TransferService.Hash(a,cancellation).AsSpan().SequenceEqual(TransferService.Hash(b,cancellation)))throw new IOException("Copied workspace changed.");}
            else{var before=NativeFile.ReadStamp(file);string copyRoot=Path.Combine(Path.GetDirectoryName(destination)!,"restore");Directory.CreateDirectory(copyRoot);PathPolicy.AssertNoLinks(copyRoot);string part=Path.Combine(copyRoot,JsonFiles.Hash(relative)+".part");if(File.Exists(part)){PathPolicy.AssertNoLinks(part);if(NativeFile.ReadStamp(part).Links!=1)throw new IOException("Shared workspace copy.");}using(var input=File.OpenRead(file))using(var output=new FileStream(part,FileMode.Create,FileAccess.Write,FileShare.None)){byte[] buffer=new byte[1024*1024];try{int count;while((count=input.Read(buffer))>0){OperationPause.Check(cancellation);output.Write(buffer,0,count);}output.Flush(true);}finally{CryptographicOperations.ZeroMemory(buffer);}}File.Move(part,target,false);if(NativeFile.ReadStamp(file)!=before)throw new IOException("Reading source changed during copying.");using var a=File.OpenRead(file);using var b=File.OpenRead(target);if(!TransferService.Hash(a,cancellation).AsSpan().SequenceEqual(TransferService.Hash(b,cancellation)))throw new IOException("Reading workspace verification failed.");}
            progress?.Report(new("archive-workspace",++done,0));
        }
    }
}
