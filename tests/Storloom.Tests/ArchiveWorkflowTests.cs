using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;
public sealed class ArchiveWorkflowTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","archive-flow-"+Guid.NewGuid().ToString("N"));
    private const string Password="fixture-archive-password";
    public ArchiveWorkflowTests()=>Directory.CreateDirectory(root);
    private ChatArchiveProject Project(){string source=Path.Combine(root,"account");Directory.CreateDirectory(source);Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','first message');");return new(ChatArchiveProject.Identity(ChatPlatform.WeChat,source),"account",ChatPlatform.WeChat,source,Path.Combine(root,"backups"));}
    private string TaskFile=>Path.Combine(root,"state","task.json");
    private ChatArchiveProject Update(ChatArchiveProject p,string? password=null,ChatArchiveUpdateRequest? request=null,IProgress<ScanProgress>? progress=null,CancellationToken token=default){Directory.CreateDirectory(Path.GetDirectoryName(TaskFile)!);return ChatArchiveWorkflow.Update(p,TaskFile,null,password,2,changed=>latest=changed,request,progress,token,checkRunningClients:false);}
    private ChatArchiveProject? latest;



    [Theory][InlineData(ChatVaultKeyMode.Password)][InlineData(ChatVaultKeyMode.DirectKey)]
    public void IncrementalProtectedReadersKeepTheDataKeyAfterPasswordWrappingChanges(ChatVaultKeyMode mode){
        string credential=mode==ChatVaultKeyMode.DirectKey?Convert.ToHexString(RandomNumberGenerator.GetBytes(32)):Password;
        var first=Update(Project(),credential,new(DirectReading:true,Mode:ChatArchiveProtectionMode.All){KeyMode=mode});
        if(mode==ChatVaultKeyMode.Password){using var vault=ChatVault.Open(first.Reading!,credential);vault.ChangePassword("changed-fixture-password");credential="changed-fixture-password";}
        Database(Path.Combine(first.Source,"message_0.db"),"INSERT INTO MSG VALUES(1,1700000003,'fixture','after key wrapping update');");
        var second=Update(first,credential,new(DirectReading:true));using var current=ChatVault.Open(second.Reading!,credential);Assert.Equal(mode,current.KeyMode);using var text=new StreamReader(current.OpenRead("messages.jsonl"));Assert.Contains("after key wrapping update",text.ReadToEnd());Assert.True(ChatVault.Exists(first.Reading!));
    }
    [Fact]public void IndependentProtectedReadingsKeepRawBackupAbsentAndRetainTheOldCiphertext(){
        const string password="fixture-independent-password";
        var first=Update(Project(),password,request:new(DirectReading:true,Mode:ChatArchiveProtectionMode.All));Assert.Null(first.Manifest);Assert.True(ChatVault.Exists(first.Reading!));
        string old=first.Reading!;byte[] before=File.ReadAllBytes(Path.Combine(old,"vault.storloom.json"));
        Database(Path.Combine(first.Source,"message_0.db"),"INSERT INTO MSG VALUES(1,1700000003,'fixture','independent protected message');");
        var second=Update(first,password,request:new(DirectReading:true));Assert.Null(second.Manifest);Assert.NotEqual(old,second.Reading);Assert.Equal(before,File.ReadAllBytes(Path.Combine(old,"vault.storloom.json")));
        using var vault=ChatVault.Open(second.Reading!,password);using var text=new StreamReader(vault.OpenRead("messages.jsonl"));Assert.Contains("independent protected message",text.ReadToEnd());Assert.False(File.Exists(Path.Combine(second.Reading!,"messages.jsonl")));
    }
    [Fact]public void ExplicitDecryptionCreatesAPlainGenerationWithoutRequiringTheOriginalAccount(){
        var encrypted=Update(Project(),Password,new(DirectReading:true,Mode:ChatArchiveProtectionMode.All));string original=encrypted.Reading!;byte[] marker=File.ReadAllBytes(Path.Combine(original,"vault.storloom.json"));Directory.Delete(encrypted.Source,true);
        var plain=Update(encrypted,Password,new(Protection:new(true),Mode:ChatArchiveProtectionMode.None,ProtectionOnly:true,DirectReading:true));
        Assert.Equal(ChatArchiveProtectionMode.None,plain.Protection);Assert.False(ChatVault.Exists(plain.Reading!));Assert.Contains("first message",File.ReadAllText(Path.Combine(plain.Reading!,"messages.jsonl")));Assert.Equal(marker,File.ReadAllBytes(Path.Combine(original,"vault.storloom.json")));Assert.Null(plain.Manifest);
    }
    [Fact]public void RawAndReadingLocationsRemainIndependentAcrossUpdates(){
        var project=Project() with{ReadingRoot=Path.Combine(root,"reading-location")};
        var first=Update(project,request:new(DirectReading:true));Assert.True(PathPolicy.Under(first.Reading!,project.ReadingRoot));Assert.Null(first.Manifest);
        var backed=Update(first,request:new(BackupOnly:true));Assert.NotNull(backed.Manifest);Assert.Equal(first.Reading,backed.Reading);Assert.Equal(project.BackupRoot,backed.BackupRoot);
        var raw=JsonFiles.Read<TransferJournal>(backed.Manifest!);Assert.True(PathPolicy.Under(raw.Plan.Destination,project.BackupRoot));
        Database(Path.Combine(project.Source,"message_0.db"),"INSERT INTO MSG VALUES(1,1700000002,'fixture','independent newer message');");
        var next=Update(backed,request:new(DirectReading:true));Assert.Equal(backed.Manifest,next.Manifest);Assert.Equal(backed.BackedUpAt,next.BackedUpAt);Assert.Equal(2,File.ReadLines(Path.Combine(next.Reading!,"messages.jsonl")).Count());
    }
    [Fact]public void IndependentReadingKeepsSourceDatabasesReadLockedUntilPublication(){
        var project=Project();bool checkedLock=false;
        var result=Update(project,request:new(DirectReading:true),progress:new Callback(p=>{if(p.RuleId=="chat-source-lock"&&!checkedLock){checkedLock=true;Assert.ThrowsAny<IOException>(()=>{using var writer=new FileStream(Path.Combine(project.Source,"message_0.db"),FileMode.Open,FileAccess.Write,FileShare.ReadWrite);});}}));
        Assert.True(checkedLock);Assert.NotNull(result.Reading);Assert.Null(result.Manifest);
    }
    [Fact]public void IndependentReadingUpdatesDoNotCreateRawBackupsAndPreserveArchivedHistory(){
        var first=Update(Project(),request:new(DirectReading:true));Assert.Null(first.Manifest);Assert.Null(first.BackedUpAt);Assert.NotNull(first.Reading);
        Database(Path.Combine(first.Source,"message_0.db"),"DELETE FROM MSG;INSERT INTO MSG VALUES(1,1700000002,'fixture','new direct message');");
        var second=Update(first,request:new(DirectReading:true));Assert.Null(second.Manifest);Assert.NotEqual(first.Reading,second.Reading);Assert.Equal(2,File.ReadLines(Path.Combine(second.Reading!,"messages.jsonl")).Count());Assert.Single(File.ReadLines(Path.Combine(first.Reading!,"messages.jsonl")));
        Assert.Empty(Directory.GetDirectories(first.BackupRoot,"*-raw",SearchOption.AllDirectories));
    }
    [Fact]public void IndependentReadingRefusesChangedDatabasesAfterInterruption(){
        using var stop=new CancellationTokenSource();var project=Project();
        Assert.ThrowsAny<OperationCanceledException>(()=>Update(project,request:new(DirectReading:true),progress:new Callback(p=>{if(p.RuleId=="chat-source-lock"&&p.Files>0&&p.Bytes==p.TotalBytes)stop.Cancel();}),token:stop.Token));
        Assert.NotNull(latest);Assert.NotNull(latest!.TaskFile);Database(Path.Combine(project.Source,"message_0.db"),"INSERT INTO MSG VALUES(1,1700000002,'fixture','changed after interruption');");
        Assert.Throws<IOException>(()=>Update(latest!));Assert.Null(latest!.Reading);Assert.True(File.Exists(latest.TaskFile));
    }
    [Fact]public void IncrementalUpdatesPublishNewGenerationsAndRetainPreviousReaders(){
        var first=Update(Project());string old=first.Reading!;byte[] transcript=File.ReadAllBytes(Path.Combine(old,"messages.jsonl"));
        Database(Path.Combine(first.Source,"message_0.db"),"INSERT INTO MSG VALUES(1,1700000001,'fixture','second message');");
        var next=Update(first);Assert.NotEqual(old,next.Reading);Assert.Equal(transcript,File.ReadAllBytes(Path.Combine(old,"messages.jsonl")));Assert.Equal(2,File.ReadLines(Path.Combine(next.Reading!,"messages.jsonl")).Count());Assert.Null(next.TaskFile);Assert.False(File.Exists(TaskFile));Assert.NotEqual(first.Manifest,next.Manifest);
    }
    [Fact]public void CancellationBeforePublicationKeepsVisibleReaderAndResumesCapturedBackup(){
        var first=Update(Project());Database(Path.Combine(first.Source,"message_0.db"),"INSERT INTO MSG VALUES(1,1700000001,'fixture','second message');");using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>Update(first,progress:new Callback(p=>{if(p.RuleId=="archive-publish")stop.Cancel();}),token:stop.Token));
        Assert.Equal(first.Reading,latest!.Reading);Assert.NotNull(latest.TaskFile);Assert.True(File.Exists(Path.Combine(first.Reading!,"messages.jsonl")));
        Database(Path.Combine(first.Source,"message_0.db"),"INSERT INTO MSG VALUES(1,1700000002,'fixture','after checkpoint');");
        var resumed=Update(latest);Assert.Equal(2,File.ReadLines(Path.Combine(resumed.Reading!,"messages.jsonl")).Count());var newest=Update(resumed);Assert.Equal(3,File.ReadLines(Path.Combine(newest.Reading!,"messages.jsonl")).Count());
    }
    [Fact]public void BackupOnlyLeavesReaderPointerAndHistoryUntouched(){var initial=Update(Project());var next=Update(initial,request:new(BackupOnly:true));Assert.Equal(initial.Reading,next.Reading);Assert.NotEqual(initial.Manifest,next.Manifest);Assert.Null(next.TaskFile);}
    [Fact]public void VerifiedGenerationCopyReusesIndexOnFollowingUpdates(){
        var first=Update(Project());var second=Update(first);var phases=new List<string>();
        var third=Update(second,progress:new Callback(p=>phases.Add(p.RuleId)));
        Assert.DoesNotContain("chat-update-index",phases);Assert.NotEqual(second.Reading,third.Reading);
        Assert.Equal(File.ReadAllBytes(Path.Combine(second.Reading!,"messages.jsonl")),File.ReadAllBytes(Path.Combine(third.Reading!,"messages.jsonl")));
    }
    [Fact]public void ModifiedCopiedIndexIsRebuiltInsteadOfTrusted(){
        var second=Update(Update(Project()));string index=Path.Combine(second.Reading!,".reading-update-index.db");File.WriteAllText(index,"invalid fixture cache");
        var phases=new List<string>();var third=Update(second,progress:new Callback(p=>phases.Add(p.RuleId)));
        Assert.Contains("chat-update-index",phases);Assert.Single(File.ReadLines(Path.Combine(third.Reading!,"messages.jsonl")));
    }
    [Fact]public void ProtectedUpdateUsesEncryptedWorkspaceAndPreservesOldCiphertext(){
        string efs=Path.Combine(root,"efs-capability");try{ChatArchiveWorkspace.Create(efs,true);}catch(IOException){var p=Project() with{Protection=ChatArchiveProtectionMode.All};Assert.Throws<IOException>(()=>Update(p,Password));Assert.False(Directory.EnumerateFiles(p.BackupRoot,"messages.jsonl",SearchOption.AllDirectories).Any());return;}
        var first=Update(Project(),Password,new(Mode:ChatArchiveProtectionMode.All));Assert.True(ChatVault.Exists(first.Reading!));
        using(var vault=ChatVault.Open(first.Reading!,Password)){Assert.True(vault.Status.Complete);Assert.True(vault.ReadProtection().IncludeMedia);}
        var original=Directory.GetFiles(Path.Combine(first.Reading!,".vault"),"*.enc").ToDictionary(p=>Path.GetFileName(p)!,File.ReadAllBytes);
        Database(Path.Combine(first.Source,"message_0.db"),"INSERT INTO MSG VALUES(1,1700000001,'fixture','protected addition');");
        var next=Update(first,Password);Assert.NotEqual(first.Reading,next.Reading);Assert.False(File.Exists(Path.Combine(next.Reading!,"messages.jsonl")));
        using(var vault=ChatVault.Open(next.Reading!,Password)){Assert.True(vault.Status.Complete);using var reader=new StreamReader(vault.OpenRead("messages.jsonl"));Assert.Contains("protected addition",reader.ReadToEnd());}
        Assert.Contains(original,p=>p.Key.Length==36&&File.Exists(Path.Combine(next.Reading!,".vault",p.Key))&&p.Value.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(next.Reading!,".vault",p.Key))));
        foreach(var entry in original)Assert.Equal(entry.Value,File.ReadAllBytes(Path.Combine(first.Reading!,".vault",entry.Key!)));
        Assert.DoesNotContain(Password,JsonSerializer.Serialize(latest));
    }
    [Fact]public void ProtectedCancellationRequiresTheSamePasswordAndNeverPublishesPlaintext(){
        string capability=Path.Combine(root,"efs");try{ChatArchiveWorkspace.Create(capability,true);}catch(IOException){return;}
        var original=Update(Project());using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>Update(original,Password,new(Mode:ChatArchiveProtectionMode.All,UseExistingBackup:true,ProtectionOnly:true),new Callback(p=>{if(p.RuleId=="archive-protect")stop.Cancel();}),stop.Token));
        Assert.Equal(original.Reading,latest!.Reading);var saved=JsonFiles.Read<ChatArchiveTask>(TaskFile);ChatArchiveWorkspace.AssertEncrypted(saved.Work);
        Assert.Throws<CryptographicException>(()=>Update(latest!,"wrong-password"));Assert.Equal(original.Reading,latest!.Reading);
        var completed=Update(latest!,Password);Assert.True(ChatVault.Exists(completed.Reading!));Assert.False(File.Exists(Path.Combine(completed.Reading!,"messages.jsonl")));Assert.True(File.Exists(Path.Combine(original.Reading!,"messages.jsonl")));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SavedConversationRulesAlsoProtectFutureMatchingMessages(bool strict){
        string capability=Path.Combine(root,"efs");try{ChatArchiveWorkspace.Create(capability,true);}catch(IOException){return;}
        var initial=Update(Project());string text=File.ReadAllText(Path.Combine(initial.Reading!,"archive-index.js"));using var index=JsonDocument.Parse(text["window.archiveIndex=".Length..].TrimEnd(';'));string id=index.RootElement.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        var protectedCopy=Update(initial,Password,new(Protection:new(true,new(Conversations:[id],Strict:strict)),Mode:ChatArchiveProtectionMode.Selected,UseExistingBackup:true,ProtectionOnly:true));
        Database(Path.Combine(initial.Source,"message_0.db"),"INSERT INTO MSG VALUES(1,1700000001,'fixture','future protected message');");
        var next=Update(protectedCopy,Password);Assert.Equal(ChatArchiveProtectionMode.Selected,next.Protection);
        using var vault=ChatVault.Open(next.Reading!,Password);Assert.Equal(id,Assert.Single(vault.ReadProtection().Selection!.Conversations!));Assert.True(vault.MetadataProtected);using var reader=new StreamReader(vault.OpenRead("messages.jsonl"));Assert.Contains("future protected message",reader.ReadToEnd());
        Assert.DoesNotContain("future protected message",string.Join("",Directory.EnumerateFiles(next.Reading!,"*.js",SearchOption.AllDirectories).Select(File.ReadAllText)));
    }
    [Fact]public void FullProtectionReusesMediaWithoutRestoringItsPlaintext(){
        string capability=Path.Combine(root,"efs");try{ChatArchiveWorkspace.Create(capability,true);}catch(IOException){return;}
        var plain=Update(Project());string media="assets/fixture-media.bin";Directory.CreateDirectory(Path.Combine(plain.Reading!,"assets"));byte[] bytes=Enumerable.Range(0,4096).Select(i=>(byte)i).ToArray();File.WriteAllBytes(Path.Combine(plain.Reading!,media),bytes);
        var protectedCopy=Update(plain,Password,new(Mode:ChatArchiveProtectionMode.All,UseExistingBackup:true,ProtectionOnly:true));
        using var stop=new CancellationTokenSource();Assert.ThrowsAny<OperationCanceledException>(()=>Update(protectedCopy,Password,progress:new Callback(p=>{if(p.RuleId=="archive-protect")stop.Cancel();}),token:stop.Token));
        var state=JsonFiles.Read<ChatArchiveTask>(TaskFile);Assert.False(File.Exists(Path.Combine(state.Work,"reader",media)));
        var next=Update(latest!,Password);using var vault=ChatVault.Open(next.Reading!,Password);using var input=vault.OpenRead(media);using var output=new MemoryStream();input.CopyTo(output);Assert.Equal(bytes,output.ToArray());Assert.True(vault.Status.Complete);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void DeferredVerificationDetectsChangedBackupWithoutReexporting(bool rebuild){
        var initial=Project();File.WriteAllText(Path.Combine(initial.Source,"unused.bin"),"original");
        var first=Update(initial with{VerifyBackupBeforeReading=false});
        var journal=JsonFiles.Read<TransferJournal>(first.Manifest!);
        string media=ChatIncrementalBackup.Resolve(journal.Plan.Destination,journal.Receipts.Single(r=>r.RelativePath=="unused.bin"));
        ChatExporter.VerifyBackup(first.Manifest!);
        File.WriteAllText(media,"modified");
        byte[] old=File.ReadAllBytes(Path.Combine(first.Reading!,"messages.jsonl"));
        var next=Update(first,request:new(Rebuild:rebuild,UseExistingBackup:true));
        Assert.False(next.VerifyBackupBeforeReading);
        Assert.Throws<InvalidDataException>(()=>ChatExporter.VerifyBackup(next.Manifest!));
        Assert.Equal(old,File.ReadAllBytes(Path.Combine(first.Reading!,"messages.jsonl")));
        Assert.True(File.Exists(Path.Combine(next.Reading!,"messages.html")));
        Assert.Throws<InvalidDataException>(()=>Update(next with{VerifyBackupBeforeReading=true},request:new(Rebuild:true,UseExistingBackup:true)));
    }
    [Fact]public void EnabledVerificationRunsDuringDailyUpdate(){
        var first=Update(Project());bool verified=false;
        Update(first,request:new(UseExistingBackup:true),progress:new Callback(p=>{if(p.RuleId=="chat-verify")verified=true;}));
        Assert.True(verified);
    }
    [Fact]public void ResumeRetainsVerificationChoiceAndStandaloneVerificationCanCancel(){
        var p=Project() with{VerifyBackupBeforeReading=false};using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>Update(p,progress:new Callback(x=>{if(x.RuleId=="archive-reading")stop.Cancel();}),token:stop.Token));
        Assert.False(JsonFiles.Read<ChatArchiveTask>(TaskFile).VerifyBackupBeforeReading);
        var resumed=Update(latest! with{VerifyBackupBeforeReading=true});
        Assert.False(JsonFiles.Read<ChatExportResult>(Path.Combine(resumed.Reading!,"export-report.json")).BackupReverified);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>ChatExporter.VerifyBackup(resumed.Manifest!,cancellation:cancelled.Token));
        ChatExporter.VerifyBackup(resumed.Manifest!);
        var journal=JsonFiles.Read<TransferJournal>(resumed.Manifest!);File.AppendAllText(ChatIncrementalBackup.Resolve(journal.Plan.Destination,journal.Receipts[0]),"changed length");
        Assert.Throws<InvalidDataException>(()=>ChatExporter.VerifyBackup(resumed.Manifest!));
    }
    [Fact]public void WorkspaceCopyReadsSourceOnceAndReportsVerificationBytes(){
        byte[] data=new byte[3*1024*1024+17];RandomNumberGenerator.Fill(data);string target=Path.Combine(root,"copy.bin"),partial=Path.Combine(root,"copy.part");int opens=0;long bytes=0;
        ChatWorkspaceCopy.File(()=>{opens++;return new MemoryStream(data,false);},target,partial,default,n=>bytes+=n);
        Assert.Equal(1,opens);Assert.Equal(data.Length*2L,bytes);Assert.Equal(data,File.ReadAllBytes(target));Assert.False(File.Exists(partial));
        byte[] modified=data.ToArray();modified[0]^=1;Assert.Throws<IOException>(()=>ChatWorkspaceCopy.File(()=>new MemoryStream(modified,false),target,partial,default,_=>{}));Assert.Equal(data,File.ReadAllBytes(target));
    }
    [Fact]public void WorkspaceCopyCancellationRetainsOnlyPartialAndCanResume(){
        byte[] data=new byte[3*1024*1024];RandomNumberGenerator.Fill(data);string target=Path.Combine(root,"resumed.bin"),partial=Path.Combine(root,"resumed.part");using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>ChatWorkspaceCopy.File(()=>new MemoryStream(data,false),target,partial,stop.Token,_=>stop.Cancel()));Assert.False(File.Exists(target));
        ChatWorkspaceCopy.File(()=>new MemoryStream(data,false),target,partial,default,_=>{});Assert.Equal(data,File.ReadAllBytes(target));
    }
    [Fact]public void WorkspaceProgressHasTotalsAndResumingDoesNotRecopyCompletedFiles(){
        var first=Update(Project());for(int n=0;n<32;n++)File.WriteAllBytes(Path.Combine(first.Reading!,"fixture-"+n+".bin"),new byte[1024+n]);
        using var stop=new CancellationTokenSource();var reports=new List<ScanProgress>();var gate=new object();
        Assert.ThrowsAny<OperationCanceledException>(()=>Update(first,request:new(UseExistingBackup:true),progress:new Callback(p=>{lock(gate)reports.Add(p);if(p.RuleId=="archive-workspace"&&p.Files>=1)stop.Cancel();}),token:stop.Token));
        var state=JsonFiles.Read<ChatArchiveTask>(TaskFile);string stage=Path.Combine(state.Work,"reader");var completed=Directory.GetFiles(stage,"*",SearchOption.AllDirectories).ToDictionary(p=>p,NativeFile.ReadStamp);
        Assert.NotEmpty(completed);Assert.Contains(reports,p=>p.RuleId=="archive-workspace"&&p.TotalFiles>=32&&p.TotalBytes>0);
        var next=Update(latest!);foreach(var pair in completed){string path=Path.Combine(next.Reading!,Path.GetRelativePath(stage,pair.Key));if(Path.GetFileName(path).StartsWith("fixture-",StringComparison.Ordinal))Assert.Equal(pair.Value,NativeFile.ReadStamp(path));}
    }
    [Fact]public void InvalidProjectIdentityCannotCreateWorkspace(){var p=Project() with{Id="../outside"};Assert.Throws<InvalidDataException>(()=>Update(p));Assert.False(Directory.Exists(p.BackupRoot));}
    private sealed class Callback(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    private static void Database(string path,string sql){Assert.Equal(0,sqlite3_open_v2(path,out nint handle,6,0));try{Assert.Equal(0,sqlite3_exec(handle,sql,0,0,0));}finally{sqlite3_close_v2(handle);}}
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint handle,int flags,nint vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_exec(nint handle,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint callback,nint state,nint error);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_close_v2(nint handle);
    public void Dispose(){if(!PathPolicy.Under(root,Path.Combine(AppContext.BaseDirectory,"fixtures")))throw new IOException("Fixture boundary changed.");Directory.Delete(root,true);}
}
