using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;
public sealed class CompactArchiveTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","compact-"+Guid.NewGuid().ToString("N"));
    public CompactArchiveTests()=>Directory.CreateDirectory(root);
    private string Dir(string name){string path=Path.Combine(root,name);Directory.CreateDirectory(path);return path;}
    private sealed class Callback(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    [Theory][InlineData(false)][InlineData(true)]public void ExternalQueryWorkspaceResumesAndPublishesASelfContainedArchive(bool encrypted){
        string archive=Path.Combine(root,"archive"),work=Dir("ssd-work"),proof=new string('a',64),password=encrypted?"fixture-password":"";
        using(var store=ArchiveStore.Create(archive,password)){store.WriteWorkspace=Path.Combine(work,"query-writing");using(var writer=store.BeginWrite(source:proof)){for(int n=0;n<600;n++)writer.Add(new("Fixture","room","sender",n,1,"workspace fixture "+n){Id="m"+n});writer.Flush();}Assert.Null(store.Head);Assert.True(File.Exists(Path.Combine(work,"query-writing","query.db")));}
        using(var published=ArchiveStore.Open(archive,password)){Assert.Empty(published.Search("workspace"));published.WriteWorkspace=Path.Combine(work,"query-writing");using var stop=new CancellationTokenSource();var reports=new List<ScanProgress>();using(var writer=published.BeginWrite(stop.Token,proof,new Callback(p=>{reports.Add(p);if(p.RuleId=="archive-query-publish"&&p.Bytes>0)stop.Cancel();}))){Assert.ThrowsAny<OperationCanceledException>(()=>writer.Complete(new(proof,"complete",0,0,600,0,0)));}Assert.Null(published.Head);Assert.Contains(reports,p=>p.RuleId=="archive-query-publish");}
        using(var store=ArchiveStore.Open(archive,password)){store.WriteWorkspace=Path.Combine(work,"query-writing");using var writer=store.BeginWrite(source:proof);writer.Add(new("Fixture","room","sender",601,1,"workspace fixture final"){Id="final"});writer.Complete(new(proof,"complete",0,0,601,0,0));Assert.True(store.Performance.Commits>0);}
        Directory.Delete(work,true);using var reopened=ArchiveStore.Open(archive,password);Assert.Equal(601,reopened.Page("Fixture/room",1000).Count);reopened.Verify();
    }
    [Theory][InlineData(ChatStorageFormat.Folder,false)][InlineData(ChatStorageFormat.Folder,true)][InlineData(ChatStorageFormat.Compact,false)][InlineData(ChatStorageFormat.Compact,true)]public void ReadingWorkRootIsConfigurableAndUnfinishedTasksRetainIt(ChatStorageFormat format,bool encrypted){
        string source=Dir("source"),work=Dir("ssd-work"),task=Path.Combine(root,"task.json"),db=Path.Combine(source,"message_0.db"),secret=encrypted?"fixture-password":"";Sql(db,"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','work root message');");var project=ChatArchiveProject.Create("source",ChatPlatform.WeChat,source,Dir("output")) with{ReadingStorage=format};ChatArchiveProject saved=project;using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>ChatArchiveWorkflow.Update(project,task,null,secret,32,p=>saved=p,new(DirectReading:true,Mode:encrypted?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None){WorkspaceRoot=work},new Callback(p=>{if(p.RuleId=="chat-reader")stop.Cancel();}),stop.Token,checkRunningClients:false));var checkpoint=JsonFiles.Read<ChatArchiveTask>(task);Assert.StartsWith(work,checkpoint.Work);if(encrypted)ChatArchiveWorkspace.AssertEncrypted(checkpoint.Work);
        var completed=ChatArchiveWorkflow.Update(saved,task,null,secret,32,_=>{},new(DirectReading:true){WorkspaceRoot=Dir("another-work-root")},checkRunningClients:false);Assert.NotNull(completed.Reading);if(format==ChatStorageFormat.Compact){using var archive=ArchiveStore.Open(completed.Reading!,secret);Assert.Single(archive.Search("work root"));}else if(encrypted){using var vault=ChatVault.Open(completed.Reading!,secret);using var text=new StreamReader(vault.OpenRead("messages.jsonl"));Assert.Contains("work root",text.ReadToEnd());}else Assert.Contains("work root",File.ReadAllText(Path.Combine(completed.Reading!,"messages.jsonl")));
    }
    [Fact]public void NewProjectsDefaultToCompactWhileOldProjectsAndExplicitChoicesRemainCompatible(){
        var project=ChatArchiveProject.Create("account",ChatPlatform.WeChat,Dir("account"),Dir("output"));Assert.Equal(ChatStorageFormat.Compact,project.ReadingStorage);Assert.Equal(ChatStorageFormat.Compact,project.BackupStorage);
        var legacy=JsonSerializer.SerializeToNode(project,JsonFiles.Options)!.AsObject();legacy.Remove("readingStorage");legacy.Remove("backupStorage");var loaded=legacy.Deserialize<ChatArchiveProject>(JsonFiles.Options)!;Assert.Equal(ChatStorageFormat.Folder,loaded.ReadingStorage);Assert.Equal(ChatStorageFormat.Folder,loaded.BackupStorage);var explicitFolder=project with{ReadingStorage=ChatStorageFormat.Folder,BackupStorage=ChatStorageFormat.Folder};Assert.Equal(explicitFolder,JsonSerializer.Deserialize<ChatArchiveProject>(JsonSerializer.Serialize(explicitFolder,JsonFiles.Options),JsonFiles.Options));
    }
    [Fact]public void DirectKeyReadingUpdatesCopiesExportsAndReorganizationsRetainTheirProtection(){
        string source=Dir("account"),db=Path.Combine(source,"message_0.db"),secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),task=Path.Combine(root,"task.json");
        Sql(db,"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','direct key original');");
        var project=ChatArchiveProject.Create("account",ChatPlatform.WeChat,source,Dir("output"));
        var first=ChatArchiveWorkflow.Update(project,task,null,secret,2,_=>{},new(DirectReading:true,Mode:ChatArchiveProtectionMode.All){KeyMode=ChatVaultKeyMode.DirectKey},checkRunningClients:false);
        Assert.Equal(ChatVaultKeyMode.DirectKey,ArchiveStore.ReadKeyMode(first.Reading!));
        Sql(db,"INSERT INTO MSG VALUES(1,1700000001,'fixture','direct key increment');");
        // No mode needs to be supplied for subsequent updates: the archive is authoritative.
        var second=ChatArchiveWorkflow.Update(first,task,null,secret,2,_=>{},new(DirectReading:true),checkRunningClients:false);
        using(var store=ArchiveStore.Open(second.Reading!,secret)){Assert.Equal(2,store.Search("direct key").Count);Assert.Throws<InvalidOperationException>(()=>store.ChangePassword("another-password"));store.Verify();store.BackupTo(Path.Combine(root,"copy"));store.ExportReader(Path.Combine(root,"html"));using var organized=ArchiveStore.Create(Path.Combine(root,"organized"),secret,ChatVaultKeyMode.DirectKey);store.RepackTo(organized);Assert.Equal(2,organized.Search("direct key").Count);organized.Verify();}
        using(var copy=ArchiveStore.Open(Path.Combine(root,"copy"),secret)){Assert.Equal(ChatVaultKeyMode.DirectKey,copy.KeyMode);Assert.Equal(2,copy.Search("direct key").Count);}
        Assert.Equal(2,File.ReadLines(Path.Combine(root,"html","messages.jsonl")).Count());
        var before=Directory.GetFiles(second.Reading!,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
        Assert.ThrowsAny<CryptographicException>(()=>ArchiveStore.Open(second.Reading!,Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));foreach(var entry in before)Assert.Equal(entry.Value,File.ReadAllBytes(entry.Key));
        foreach(string path in Directory.GetFiles(second.Reading!,"*",SearchOption.AllDirectories)){byte[] bytes=File.ReadAllBytes(path);Assert.True(bytes.AsSpan().IndexOf("direct key original"u8)<0);Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(secret))<0);}
    }
    [Fact]public void DirectKeyNativeBackupResumesWithItsSavedModeAndRestores(){
        string source=Dir("source"),secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),task=Path.Combine(root,"backup-task.json");File.WriteAllBytes(Path.Combine(source,"data"),RandomNumberGenerator.GetBytes(2*ArchiveStore.ChunkBytes));var project=ChatArchiveProject.Create("source",ChatPlatform.QQ,source,Dir("output"));ChatArchiveProject saved=project;using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>ChatArchiveWorkflow.Update(project,task,null,secret,2,p=>saved=p,new(BackupOnly:true){KeyMode=ChatVaultKeyMode.DirectKey},new Callback(p=>{if(p.RuleId=="archive-native-pack")stop.Cancel();}),stop.Token,checkRunningClients:false));Assert.Equal(ChatVaultKeyMode.DirectKey,JsonFiles.Read<ChatArchiveTask>(task).KeyMode);
        var complete=ChatArchiveWorkflow.Update(saved,task,null,secret,2,_=>{},new(BackupOnly:true),checkRunningClients:false);using var store=ArchiveStore.Open(complete.NativeArchive!,secret);Assert.Equal(ChatVaultKeyMode.DirectKey,store.KeyMode);store.Verify();string restored=store.RestoreNative(Path.Combine(root,"restored"));Assert.Equal(File.ReadAllBytes(Path.Combine(source,"data")),File.ReadAllBytes(Path.Combine(restored,"data")));
    }
    [Theory][InlineData(false)][InlineData(true)]public void NativeIndexesAndJournalsWorkBeyondTraditionalWindowsPathLimits(bool encrypted){
        string parent=Dir(new string('x',120)),archive=Path.Combine(parent,"deep-native-archive"),source=Dir("source");File.WriteAllText(Path.Combine(source,"one.txt"),"long path fixture");string password=encrypted?"fixture-password":"";
        using(var store=ArchiveStore.Create(archive,password)){store.BackupNative(source,ChatPlatform.QQ,checkClients:false);Assert.Contains(Directory.GetFiles(archive,"query.db",SearchOption.AllDirectories),p=>p.Length>260);store.Verify();}
        using var reopened=ArchiveStore.Open(archive,password);string target=reopened.RestoreNative(Path.Combine(root,"restored"));Assert.Equal("long path fixture",File.ReadAllText(Path.Combine(target,"one.txt")));
    }
    [Theory][InlineData(ChatStorageFormat.Folder)][InlineData(ChatStorageFormat.Compact)]public void MaterializedBackupsKeepLogicalAccountIdentityAcrossDifferentInputDirectories(ChatStorageFormat format){
        string source=Dir("original"),database=Path.Combine(source,"message_0.db");Sql(database,"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','first restored message');");var project=new ChatArchiveProject(ChatArchiveProject.Identity(ChatPlatform.WeChat,source),"original",ChatPlatform.WeChat,source,Dir("output")){ReadingStorage=format};string task=Path.Combine(root,"task.json"),firstInput=Dir("input-one");File.Copy(database,Path.Combine(firstInput,"message_0.db"));Sql(database,"INSERT INTO MSG VALUES(1,1700000001,'fixture','second restored message');");string secondInput=Dir("input-two");File.Copy(database,Path.Combine(secondInput,"message_0.db"));Directory.Delete(source,true);
        var first=ChatArchiveWorkflow.Update(project,task,null,null,2,_=>{},new(DirectReading:true){SourceSnapshot=firstInput},checkRunningClients:false);Directory.Delete(firstInput,true);var second=ChatArchiveWorkflow.Update(first,task,null,null,2,_=>{},new(DirectReading:true){SourceSnapshot=secondInput},checkRunningClients:false);
        if(format==ChatStorageFormat.Compact){using var store=ArchiveStore.Open(second.Reading!);Assert.Equal(2,store.Page("WeChat/fixture",100).Count);}else Assert.Equal(2,File.ReadLines(Path.Combine(second.Reading!,"messages.jsonl")).Count());
    }
    [Fact]public void FreshCompactCreationRetainsTheOldFolderWhilePausedAndResumesIntoTheSameTarget(){
        string source=Dir("account");Sql(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','current source');");string task=Path.Combine(root,"task.json");var project=new ChatArchiveProject(ChatArchiveProject.Identity(ChatPlatform.WeChat,source),"account",ChatPlatform.WeChat,source,Dir("output"));
        var old=ChatArchiveWorkflow.Update(project,task,null,null,2,_=>{},new(DirectReading:true),checkRunningClients:false);ChatArchiveProject latest=old;using var cancellation=new CancellationTokenSource();
        Assert.ThrowsAny<Exception>(()=>ChatArchiveWorkflow.Update(old,task,null,null,2,p=>latest=p,new(DirectReading:true){CreateCompact=true},new Callback(p=>{if(p.RuleId=="chat-reader")cancellation.Cancel();}),cancellation.Token,checkRunningClients:false));Assert.Equal(old.Reading,latest.Reading);Assert.Equal(ChatStorageFormat.Folder,latest.ReadingStorage);string target=JsonFiles.Read<ChatArchiveTask>(task).Generation;
        var compact=ChatArchiveWorkflow.Update(latest,task,null,null,2,_=>{},new(DirectReading:true){CreateCompact=true},checkRunningClients:false);Assert.Equal(target,compact.Reading);Assert.Equal(ChatStorageFormat.Compact,compact.ReadingStorage);Assert.True(File.Exists(Path.Combine(old.Reading!,"messages.html")));using var reader=ArchiveStore.Open(compact.Reading!);Assert.Single(reader.Search("current source"));
    }
    [Fact]public void PublishedReadersRetainTheirSnapshotAndUnusedQueryCopiesAreReclaimed(){
        string archive=Path.Combine(root,"archive"),proof=new string('a',64);using var store=ArchiveStore.Create(archive);
        void Add(int n){using var writer=store.BeginWrite();writer.Add(new("Fixture","room","sender",n,1,"message "+n){Id="m"+n});writer.Complete(new(proof,"complete",0,0,n,0,0));}
        Add(1);using(var old=ArchiveStore.Open(archive)){string head=old.Head!;Add(2);Assert.Equal(head,old.Head);Assert.Single(old.Page("Fixture/room",100));Assert.Equal(2,store.Page("Fixture/room",100).Count);Assert.Equal(2,Directory.GetDirectories(Path.Combine(archive,"query"),"stage-*").Length);}
        Add(3);Assert.Single(Directory.GetDirectories(Path.Combine(archive,"query"),"stage-*"));Assert.Equal(3,store.Verify().MessageRevisions);
    }
    [Fact]public void DirectUpdatesRetainIdenticalOccurrencesAcrossDatabases(){
        string source=Dir("account");foreach(string name in new[]{"message_0.db","message_1.db"})Sql(Path.Combine(source,name),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','same message');INSERT INTO MSG VALUES(1,1700000000,'fixture','same message');");
        var project=new ChatArchiveProject(ChatArchiveProject.Identity(ChatPlatform.WeChat,source),"account",ChatPlatform.WeChat,source,Dir("output")){ReadingStorage=ChatStorageFormat.Compact};
        ChatArchiveProject Update(ChatArchiveProject p)=>ChatArchiveWorkflow.Update(p,Path.Combine(root,"task.json"),null,null,2,_=>{},new(DirectReading:true),checkRunningClients:false);
        var first=Update(project);using(var store=ArchiveStore.Open(first.Reading!))Assert.Equal(4,store.Page("WeChat/fixture",100).Count);
        Sql(Path.Combine(source,"message_1.db"),"INSERT INTO MSG VALUES(1,1700000001,'fixture','new message');");var updated=Update(first);using var current=ArchiveStore.Open(updated.Reading!);Assert.Equal(5,current.Page("WeChat/fixture",100).Count);Assert.Equal(4,current.Search("same message").Count);
    }
    [Fact]public void VersionTwoJsonPacksRemainReadable(){
        string archive=Dir("old-format");JsonFiles.Write(Path.Combine(archive,"archive.json"),new{Version=2,Id=Guid.NewGuid().ToString("N"),Protected=false});var message=new ChatMessage("Fixture","room","sender",1,1,"legacy fixture"){Id="legacy"};var row=new ArchiveQueryCache.Message("legacy","Fixture/room",1,"legacy fixture",JsonSerializer.Serialize(message));new ArchiveCommitLog(archive).Append(JsonSerializer.SerializeToUtf8Bytes(new ArchiveDelta(1,[row],[],[],[])),null);using var store=ArchiveStore.Open(archive);Assert.Single(store.Search("legacy"));Assert.Equal(1,store.Verify().MessageRevisions);
    }
    [Fact]public void RepackPreservesNativeHistoryAndCanChangeProtectionWithoutTouchingTheSource(){
        string source=Dir("native"),archive=Path.Combine(root,"archive");File.WriteAllText(Path.Combine(source,"one"),"original");
        using var original=ArchiveStore.Create(archive);var first=original.BackupNative(source,ChatPlatform.QQ,checkClients:false);File.WriteAllText(Path.Combine(source,"one"),"changed");original.BackupNative(source,ChatPlatform.QQ,checkClients:false);string head=original.Head!;
        using(var encrypted=ArchiveStore.Create(Path.Combine(root,"encrypted"),"fixture-password")){original.RepackTo(encrypted);encrypted.ChangePassword("changed-password");encrypted.Verify();Assert.Equal(head,original.Head);Assert.Equal("original",File.ReadAllText(Path.Combine(encrypted.RestoreNative(Path.Combine(root,"history"),snapshotId:first.Id),"one")));Assert.Equal("changed",File.ReadAllText(Path.Combine(encrypted.RestoreNative(Path.Combine(root,"latest")),"one")));}
        Assert.ThrowsAny<Exception>(()=>ArchiveStore.Open(Path.Combine(root,"encrypted"),"fixture-password"));using var reopened=ArchiveStore.Open(Path.Combine(root,"encrypted"),"changed-password");Assert.True(reopened.Protected);
    }
    [Theory][InlineData(false)][InlineData(true)]public void ProjectionRebuildReadsMetadataAndResourcesAreIndividuallyVerified(bool encrypted){
        string archive=Path.Combine(root,"archive");byte[] data=RandomNumberGenerator.GetBytes(ArchiveStore.ChunkBytes*3);string password=encrypted?"fixture-password":"";
        using(var store=ArchiveStore.Create(archive,password))using(var writer=store.BeginWrite()){using var input=new MemoryStream(data);writer.Put("assets/fixture.bin",input);writer.Add(new("Fixture","room","sender",1,1,"searchable fixture"){Id="stable"});writer.Complete(new(new string('a',64),"complete",0,0,1,1,data.Length));}
        // The immutable payload's last byte belongs to a resource record, not metadata.
        string pack=Directory.GetFiles(archive,"*.pack").Single();using(var file=new FileStream(pack,FileMode.Open,FileAccess.ReadWrite)){file.Position=file.Length-1;int value=file.ReadByte();file.Position--;file.WriteByte((byte)(value^1));}
        Directory.Delete(Path.Combine(archive,"query"),true);
        using var rebuilt=ArchiveStore.Open(archive,password);Assert.Single(rebuilt.Search("searchable"));using var resource=rebuilt.OpenFile("assets/fixture.bin");byte[] first=new byte[16];resource.ReadExactly(first);Assert.Equal(data[..16],first);resource.Position=data.Length-1;Assert.ThrowsAny<Exception>(()=>resource.ReadByte());Assert.ThrowsAny<Exception>(()=>rebuilt.Verify());
    }
    [Fact]public void AbandonAndPostPublicationCrashKeepThePublishedSnapshot(){
        string archive=Path.Combine(root,"archive");using var store=ArchiveStore.Create(archive);string proof=new string('a',64);
        using(var writer=store.BeginWrite(source:proof)){writer.Add(new("Fixture","room","sender",1,1,"one"){Id="one"});writer.Complete(new(proof,"complete",0,0,1,0,0));}
        string head=store.Head!;using(var writer=store.BeginWrite(source:proof)){writer.Add(new("Fixture","room","sender",2,1,"pending"){Id="two"});writer.Flush();}
        Assert.Throws<IOException>(()=>store.BeginWrite(source:new string('b',64)));ArchiveStore.AbandonPending(archive);Assert.Equal(head,store.Head);
        using(var writer=store.BeginWrite(source:"restart")){writer.Add(new("Fixture","room","sender",3,1,"three"){Id="three"});writer.Complete(new(proof,"complete",0,0,2,0,0));}
        using var reopened=ArchiveStore.Open(archive);Assert.Equal(2,reopened.Page("Fixture/room",100).Count);
    }
    [Theory][InlineData(false)][InlineData(true)]public void NativeSnapshotsReuseChunksAndRestoreRemovedFilesFromHistory(bool encrypted){
        string source=Dir("source"),target=Path.Combine(root,"backup");Directory.CreateDirectory(Path.Combine(source,"empty"));for(int i=0;i<2000;i++)File.WriteAllText(Path.Combine(source,i+".txt"),"small fixture "+i);string password=encrypted?"fixture-password":"";
        using(var store=ArchiveStore.Create(target,password)){var first=store.BackupNative(source,ChatPlatform.QQ,checkClients:false);int packs=Directory.GetFiles(target,"*.pack").Length;Assert.True(packs<20);File.Delete(Path.Combine(source,"1.txt"));File.WriteAllText(Path.Combine(source,"changed.txt"),"new fixture");var second=store.BackupNative(source,ChatPlatform.QQ,checkClients:false);Assert.NotEqual(first.Id,second.Id);Assert.InRange(Directory.GetFiles(target,"*.pack").Length-packs,1,5);string current=store.RestoreNative(Path.Combine(root,"current"));Assert.False(File.Exists(Path.Combine(current,"1.txt")));Assert.Equal("new fixture",File.ReadAllText(Path.Combine(current,"changed.txt")));string old=store.RestoreNative(Path.Combine(root,"old"),snapshotId:first.Id);Assert.Equal("small fixture 1",File.ReadAllText(Path.Combine(old,"1.txt")));Assert.True(Directory.Exists(Path.Combine(old,"empty")));store.Verify();}
        using var reopened=ArchiveStore.Open(target,password);Assert.Equal(2000,reopened.NativeCurrent().Files.Length);
    }
    [Fact]public void PendingWriterNeverChangesThePublishedReaderAndCanResume(){
        string target=Path.Combine(root,"reader");using var store=ArchiveStore.Create(target);string proof=new string('a',64);
        using(var writer=store.BeginWrite(source:proof)){writer.Add(new("Fixture","room","sender",1,1,"published"){Id="one"});writer.Complete(new(proof,"complete",0,0,1,0,0));}
        string head=store.Head!;using(var writer=store.BeginWrite(source:proof)){writer.Add(new("Fixture","room","sender",2,1,"pending"){Id="two"});writer.Flush();using var reader=ArchiveStore.Open(target);Assert.Single(reader.Page("Fixture/room",100));Assert.Equal(head,reader.Head);}
        using(var writer=store.BeginWrite(source:proof))writer.Complete(new(proof,"complete",0,0,2,0,0));using var completed=ArchiveStore.Open(target);Assert.Equal(2,completed.Page("Fixture/room",100).Count);
    }
    [Theory][InlineData(false)][InlineData(true)]public void DirectReadersAvoidHtmlPagesAndReuseOldMessagesDuringIncrementalUpdates(bool encrypted){
        string source=Dir("account"),db=Path.Combine(source,"message_0.db");Sql(db,"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','first compact message');");var project=new ChatArchiveProject(ChatArchiveProject.Identity(ChatPlatform.WeChat,source),"account",ChatPlatform.WeChat,source,Dir("output")){ReadingStorage=ChatStorageFormat.Compact};string task=Path.Combine(root,"task.json"),password=encrypted?"fixture-password":"";
        ChatArchiveProject Update(ChatArchiveProject p)=>ChatArchiveWorkflow.Update(p,task,null,password,2,_=>{},new(DirectReading:true,Mode:encrypted?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None),checkRunningClients:false);
        var first=Update(project);Assert.True(ArchiveStore.Exists(first.Reading!));Assert.False(File.Exists(Path.Combine(first.Reading!,"messages.jsonl")));Assert.False(Directory.Exists(Path.Combine(first.Reading!,"assets")));string head;using(var before=ArchiveStore.Open(first.Reading!,password)){head=before.Head!;Assert.Single(before.Search("first compact"));}
        var unchanged=Update(first);using(var same=ArchiveStore.Open(unchanged.Reading!,password))Assert.Equal(head,same.Head);
        Sql(db,"INSERT INTO MSG VALUES(1,1700000001,'fixture','second compact message');");var second=Update(unchanged);Assert.Equal(first.Reading,second.Reading);using var store=ArchiveStore.Open(second.Reading!,password);Assert.Equal(2,store.Search("compact").Count);Assert.Equal(2,store.Page("WeChat/fixture",100).Count);store.Verify();
        string compatible=Path.Combine(root,"html-export");store.ExportReader(compatible);Assert.True(File.Exists(Path.Combine(compatible,"messages.html")));Assert.Equal(2,File.ReadLines(Path.Combine(compatible,"messages.jsonl")).Count());
    }
    [Theory][InlineData(false)][InlineData(true)]public void NativeWorkspaceRemainsFixedAcrossResumeAndCompletedBackupIsIndependent(bool custom){
        string source=Dir("source"),task=Path.Combine(root,"task.json"),workRoot=Dir("work");File.WriteAllBytes(Path.Combine(source,"data"),RandomNumberGenerator.GetBytes(ArchiveStore.ChunkBytes*2));var project=ChatArchiveProject.Create("source",ChatPlatform.QQ,source,Dir("output"));ChatArchiveProject saved=project;using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>ChatArchiveWorkflow.Update(project,task,null,"fixture-password",32,p=>saved=p,new(BackupOnly:true){WorkspaceRoot=custom?workRoot:null},new Callback(p=>{if(p.RuleId=="archive-native-pack")stop.Cancel();}),stop.Token,checkRunningClients:false));
        var retained=JsonFiles.Read<ChatArchiveTask>(task);Assert.Equal(custom?Path.Combine(workRoot,"Storloom",project.Id):null,retained.WorkspaceRoot);
        var complete=ChatArchiveWorkflow.Update(saved,task,null,"fixture-password",32,_=>{},new(BackupOnly:true){WorkspaceRoot=Dir("different-work")},checkRunningClients:false);
        if(custom)Assert.False(Directory.Exists(retained.Work));Assert.False(Directory.EnumerateFileSystemEntries(Path.Combine(root,"different-work")).Any());Directory.Delete(workRoot,true);
        using var store=ArchiveStore.Open(complete.NativeArchive!,"fixture-password");store.Verify();string restored=store.RestoreNative(Path.Combine(root,"restored"));Assert.Equal(File.ReadAllBytes(Path.Combine(source,"data")),File.ReadAllBytes(Path.Combine(restored,"data")));
    }
    [Fact]public void ProtectedWriteAheadJournalHidesPayloadAndCheckpointsForStandaloneCopies(){
        string directory=Dir("query"),marker="isolated-sensitive-wal-payload";byte[] key=RandomNumberGenerator.GetBytes(32);
        using(var sql=new ArchiveSqlite(Path.Combine(directory,"query.db"),key)){
            sql.EnableWriteAhead();sql.Execute("CREATE TABLE fixture(value TEXT)");sql.Execute("INSERT INTO fixture VALUES(?)",marker);Assert.True(File.Exists(Path.Combine(directory,"query.db-wal")));
            foreach(string file in Directory.GetFiles(directory)){using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var bytes=new MemoryStream();input.CopyTo(bytes);Assert.True(bytes.ToArray().AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(marker))<0);}
            sql.Checkpoint();using var source=new FileStream(Path.Combine(directory,"query.db"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var target=File.Create(Path.Combine(directory,"standalone.db"));source.CopyTo(target);
        }
        using var copy=new ArchiveSqlite(Path.Combine(directory,"standalone.db"),key);Assert.Equal(marker,copy.Query("SELECT value FROM fixture").Single()[0]);
    }
    [Theory][InlineData(false)][InlineData(true)]public void StandaloneSnapshotsTrackIncrementalCommitsAndKeepProtectedMetadataPrivate(bool encrypted){
        string archive=Path.Combine(root,"browser"),password=encrypted?"fixture-password":"";using var store=ArchiveStore.Create(archive,password);string proof=new string('a',64);
        void Add(string text){using var writer=store.BeginWrite(source:proof);writer.Add(new("Fixture","room","sender",1,1,text){Id="one",ConversationName="private conversation marker"});writer.Complete(new(proof,"complete",0,0,1,0,0));}
        Add("private browser payload");string entry=store.EnableStandalone();Assert.True(File.Exists(entry));Assert.Contains("connect-src 'none'",File.ReadAllText(entry));Assert.DoesNotContain("private browser payload",File.ReadAllText(entry));
        using(var roots=JsonDocument.Parse(File.ReadAllText(Path.Combine(archive,"browser","roots.json"))))Assert.Equal(store.Head,roots.RootElement.GetProperty("roots")[0].GetProperty("head").GetString());
        string copy=Path.Combine(root,"browser-copy");store.BackupTo(copy);Assert.True(File.Exists(Path.Combine(copy,ArchiveStore.StandaloneEntry)));using(var copied=ArchiveStore.Open(copy,password))Assert.Single(copied.Search("private browser"));
        byte[] disposableIndex=store.ReaderIndex();System.Security.Cryptography.CryptographicOperations.ZeroMemory(disposableIndex);Assert.StartsWith("window.archiveIndex=",System.Text.Encoding.UTF8.GetString(store.ReaderIndex()));
        string prior=store.Head!;Add("changed browser payload");Assert.NotEqual(prior,store.Head);using(var roots=JsonDocument.Parse(File.ReadAllText(Path.Combine(archive,"browser","roots.json"))))Assert.Equal(store.Head,roots.RootElement.GetProperty("roots")[0].GetProperty("head").GetString());
        using var metadata=JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(store.ReaderIndex())["window.archiveIndex=".Length..].TrimEnd(';'));Assert.Equal(1,metadata.RootElement.GetProperty("conversations")[0].GetProperty("count").GetInt32());Assert.Equal("private conversation marker",metadata.RootElement.GetProperty("conversations")[0].GetProperty("name").GetString());
        if(encrypted)foreach(string file in Directory.GetFiles(Path.Combine(archive,"browser")))Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf("private conversation marker"u8)<0);
        using var reorganized=ArchiveStore.Create(Path.Combine(root,"reorganized"),password);store.RepackTo(reorganized);Assert.True(File.Exists(Path.Combine(reorganized.DirectoryPath,ArchiveStore.StandaloneEntry)));reorganized.Verify();
    }
    [Fact]public void BundledReadingKeepsDataInsideAndReopensThroughItsPublicRoot(){
        string archive=Path.Combine(root,"bundle");using(var store=ArchiveStore.CreateBundle(archive,"fixture-password")){using(var writer=store.BeginWrite()){writer.Add(new("Fixture","room","sender",1,1,"bundle fixture"){Id="one"});writer.Complete(new(new string('a',64),"complete",0,0,1,0,0));}string entry=store.EnableStandalone();Assert.Equal(Path.Combine(archive,"index.html"),entry);Assert.True(File.Exists(entry));}
        Assert.Equal(new[]{"archive-data"},Directory.GetDirectories(archive).Select(Path.GetFileName));Assert.Equal(new[]{"index.html"},Directory.GetFiles(archive).Select(Path.GetFileName));Assert.True(ArchiveStore.Exists(archive));Assert.True(ArchiveStore.NeedsPassword(archive));using var reopened=ArchiveStore.Open(archive,"fixture-password");Assert.Single(reopened.Search("bundle"));Assert.Single(reopened.Page("Fixture/room",300));reopened.EnableStandalone();
    }
    [Fact]public void HtmlRuntimeRefreshNeedsNoSecretAndPreservesProtectedSnapshot(){
        string archive=Path.Combine(root,"runtime");using(var store=ArchiveStore.CreateBundle(archive,"fixture-password")){using(var writer=store.BeginWrite()){writer.Add(new("Fixture","room","sender",1,1,"private runtime fixture"){Id="one"});writer.Complete(new(new string('a',64),"complete",0,0,1,0,0));}store.EnableStandalone();}
        string data=ArchiveStore.ResolveRoot(archive),head=File.ReadAllText(Path.Combine(data,"HEAD"));byte[] keyring=File.ReadAllBytes(Path.Combine(data,"keyring.json")),snapshot=File.ReadAllBytes(Path.Combine(data,"browser",head+".snapshot"));File.WriteAllText(Path.Combine(archive,"index.html"),"stale runtime");
        ArchiveStore.RefreshStandaloneRuntime(archive);string entry=File.ReadAllText(Path.Combine(archive,"index.html"));Assert.Contains("standalone-remember",entry);Assert.Contains("storloom-reader-directories",entry);Assert.DoesNotContain("private runtime fixture",entry);Assert.Equal(head,File.ReadAllText(Path.Combine(data,"HEAD")));Assert.Equal(keyring,File.ReadAllBytes(Path.Combine(data,"keyring.json")));Assert.Equal(snapshot,File.ReadAllBytes(Path.Combine(data,"browser",head+".snapshot")));
        using var reopened=ArchiveStore.Open(archive,"fixture-password");Assert.Single(reopened.Search("private runtime fixture"));
    }
    [Fact]public void ChangedFileIdsRequireTheImmutableContentDigest(){
        byte[] bytes="immutable layout fixture"u8.ToArray();string path=Path.Combine(root,Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))+".pack");File.WriteAllBytes(path,bytes);var stamp=NativeFile.ReadStamp(path);ArchiveStore.VerifyMovedObject(path,stamp with{FileId=stamp.FileId^1});bytes[0]^=1;File.WriteAllBytes(path,bytes);stamp=NativeFile.ReadStamp(path);Assert.Throws<InvalidDataException>(()=>ArchiveStore.VerifyMovedObject(path,stamp with{FileId=stamp.FileId^1}));
    }
    [Fact]public void CategorizingObjectsPreservesIdentityAndCanResumeWithoutUnlocking(){
        string archive=Path.Combine(root,"flat-layout"),password="fixture-password";using(var store=ArchiveStore.Create(archive,password)){using(var writer=store.BeginWrite()){writer.Add(new("Fixture","room","sender",1,1,"layout fixture"){Id="one"});writer.Bytes("notes.txt","private layout resource"u8.ToArray());writer.Complete(new(new string('a',64),"complete",0,0,1,1,23));}store.EnableStandalone();Assert.Throws<IOException>(()=>ArchiveStore.OrganizeObjects(archive));}
        string head=File.ReadAllText(Path.Combine(archive,"HEAD"));var originals=Directory.GetFiles(archive).Where(p=>Path.GetExtension(p) is ".pack" or ".commit").ToDictionary(p=>Path.GetFileName(p)!,p=>NativeFile.ReadStamp(p));using var stop=new CancellationTokenSource();Assert.ThrowsAny<OperationCanceledException>(()=>ArchiveStore.OrganizeObjects(archive,new Callback(p=>{if(p.Files==1)stop.Cancel();}),stop.Token));
        using(var partial=ArchiveStore.Open(archive,password)){Assert.Single(partial.Search("layout fixture"));using var input=new StreamReader(partial.OpenFile("notes.txt"));Assert.Equal("private layout resource",input.ReadToEnd());}
        ArchiveStore.OrganizeObjects(archive);Assert.Empty(Directory.GetFiles(archive,"*.pack"));Assert.Empty(Directory.GetFiles(archive,"*.commit"));Assert.Equal(head,File.ReadAllText(Path.Combine(archive,"HEAD")));
        foreach(var entry in originals){string path=Path.Combine(archive,entry.Key!.EndsWith(".pack",StringComparison.Ordinal)?"packs":"commits",entry.Key);Assert.Equal(entry.Value,NativeFile.ReadStamp(path));}
        using var grouped=ArchiveStore.Open(archive,password);Assert.Single(grouped.Search("layout fixture"));string backup=Path.Combine(root,"categorized-backup");grouped.BackupTo(backup);using var copied=ArchiveStore.Open(backup,password);Assert.Single(copied.Search("layout fixture"));Assert.True(Directory.Exists(Path.Combine(backup,"packs")));Assert.True(File.Exists(Path.Combine(backup,"index.html")));
    }
    [Fact]public void BrowserBackupSupportsTheOriginalQueryDirectory(){
        string archive=Path.Combine(root,"browser-base-query");using var store=ArchiveStore.Create(archive);
        store.Upsert([new("one","Fixture/room",1,"base query browser fixture","{}")]);store.EnableStandalone();string backup=Path.Combine(root,"browser-base-copy");store.BackupTo(backup);
        using var copied=ArchiveStore.Open(backup);Assert.Single(copied.Search("base query"));Assert.True(File.Exists(Path.Combine(backup,ArchiveStore.StandaloneEntry)));
    }
    [Fact]public void BrowserSnapshotCancellationNeverAdvancesPublishedHeadAndResumeRepairsItsRoot(){
        string archive=Path.Combine(root,"browser");using var store=ArchiveStore.Create(archive);string proof=new string('a',64);
        using(var writer=store.BeginWrite(source:proof)){writer.Add(new("Fixture","room","sender",1,1,"published"){Id="one"});writer.Complete(new(proof,"complete",0,0,1,0,0));}store.EnableStandalone();string prior=store.Head!;using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>{using var writer=store.BeginWrite(stop.Token,proof,new Callback(p=>{if(p.RuleId=="browser-publish")stop.Cancel();}));writer.Add(new("Fixture","room","sender",2,1,"pending"){Id="two"});writer.Complete(new(proof,"complete",0,0,2,0,0));});Assert.Equal(prior,store.Head);
        using(var visible=ArchiveStore.Open(archive)){Assert.Single(visible.Page("Fixture/room",100));}
        using(var writer=store.BeginWrite(source:proof))writer.Complete(new(proof,"complete",0,0,2,0,0));Assert.Equal(2,store.Page("Fixture/room",100).Count);using var roots=JsonDocument.Parse(File.ReadAllText(Path.Combine(archive,"browser","roots.json")));Assert.Equal(store.Head,roots.RootElement.GetProperty("roots")[0].GetProperty("head").GetString());
    }
    private static void Sql(string path,string sql){if(sqlite3_open_v2(path,out nint handle,6,0)!=0)throw new Exception("fixture open");try{if(sqlite3_exec(handle,sql,0,0,0)!=0)throw new Exception("fixture SQL");}finally{sqlite3_close_v2(handle);}}
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint handle,int flags,nint vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_exec(nint handle,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint callback,nint state,nint error);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_close_v2(nint handle);
    public void Dispose()=>Directory.Delete(root,true);
}
