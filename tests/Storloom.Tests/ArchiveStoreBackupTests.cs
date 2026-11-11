using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;
public sealed class ArchiveStoreBackupTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","archive-backup-"+Guid.NewGuid().ToString("N"));
    private sealed class Callback(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    private static ArchiveQueryCache.Message Message(string id)=>new(id,"room",1,"synthetic "+id,"{}");
    public ArchiveStoreBackupTests()=>Directory.CreateDirectory(root);
    [Theory][InlineData(false)][InlineData(true)]
    public void IncrementalBackupCopiesOnlyNewCommitFilesAndReopensWithoutSource(bool encrypted){
        string source=Path.Combine(root,"source"),backup=Path.Combine(root,"backup");using var store=ArchiveStore.Create(source,encrypted?"fixture-password":default);store.Upsert([Message("a")]);
        Assert.Equal(2,store.BackupTo(backup).CopiedFiles);var originals=Directory.GetFiles(backup,"*.pack").ToDictionary(p=>p,NativeFile.ReadStamp);
        Assert.Equal(0,store.BackupTo(backup).CopiedFiles);store.Upsert([Message("b")]);var result=store.BackupTo(backup);Assert.Equal(2,result.CopiedFiles);Assert.Equal(0,result.ReusedFiles);foreach(var entry in originals)Assert.Equal(entry.Value,NativeFile.ReadStamp(entry.Key));
        using var recovered=ArchiveStore.Open(backup,encrypted?"fixture-password":default);Assert.Equal(store.Id,recovered.Id);Assert.Equal(store.Head,recovered.Head);Assert.Equal(2,recovered.Page("room",10).Count);Assert.False(File.Exists(Path.Combine(backup,"backup-task.json")));
    }
    [Fact]public void InterruptedBackupResumesCapturedSnapshotBeforeCollectingLaterChanges(){
        using var store=ArchiveStore.Create(Path.Combine(root,"source"),"fixture-password");store.Upsert([Message("a")]);store.Upsert([Message("b")]);string? captured=store.Head;string backup=Path.Combine(root,"backup");using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>store.BackupTo(backup,new Callback(p=>{if(p.Files==1)stop.Cancel();}),stop.Token));Assert.True(File.Exists(Path.Combine(backup,"backup-task.json")));Assert.False(File.Exists(Path.Combine(backup,"HEAD")));
        store.Upsert([Message("after-checkpoint")]);var resumed=store.BackupTo(backup);Assert.Equal(captured,resumed.Head);Assert.True(resumed.ReusedFiles>=1);
        using(var old=ArchiveStore.Open(backup,"fixture-password"))Assert.Equal(2,old.Page("room",10).Count);
        var next=store.BackupTo(backup);Assert.Equal(store.Head,next.Head);using var newest=ArchiveStore.Open(backup,"fixture-password");Assert.Equal(3,newest.Page("room",10).Count);
    }
    [Fact]public void CorruptNewSourcePackNeverReplacesVisibleBackupHead(){
        string source=Path.Combine(root,"source"),backup=Path.Combine(root,"backup");using var store=ArchiveStore.Create(source);store.Upsert([Message("a")]);string? old=store.BackupTo(backup).Head;var existing=Directory.GetFiles(source,"*.pack").ToHashSet();store.Upsert([Message("b")]);string added=Directory.GetFiles(source,"*.pack").Single(p=>!existing.Contains(p));File.WriteAllBytes(added,new byte[new FileInfo(added).Length]);
        Assert.Throws<InvalidDataException>(()=>store.BackupTo(backup));using var prior=ArchiveStore.Open(backup);Assert.Equal(old,prior.Head);Assert.Single(prior.Page("room",10));
    }
    [Fact]public void UnrelatedDestinationsAreRejectedWithoutOverwritingTheirContents(){
        using var store=ArchiveStore.Create(Path.Combine(root,"source"));store.Upsert([Message("a")]);string unrelated=Path.Combine(root,"unrelated");Directory.CreateDirectory(unrelated);File.WriteAllText(Path.Combine(unrelated,"note"),"retain");Assert.Throws<IOException>(()=>store.BackupTo(unrelated));Assert.Equal("retain",File.ReadAllText(Path.Combine(unrelated,"note")));
        using var other=ArchiveStore.Create(Path.Combine(root,"other"));Assert.Throws<InvalidDataException>(()=>store.BackupTo(Path.Combine(root,"other")));
    }
    [Fact]public void IncrementalBackupDoesNotReadHistoricalSourcePacksOrCommits(){
        string source=Path.Combine(root,"source"),backup=Path.Combine(root,"backup");using var store=ArchiveStore.Create(source);store.Upsert([Message("historical")]);store.BackupTo(backup);
        foreach(string path in Directory.GetFiles(source).Where(p=>p.EndsWith(".pack",StringComparison.Ordinal)||p.EndsWith(".commit",StringComparison.Ordinal)))File.Move(path,path+".offline");
        store.Upsert([Message("new")]);Assert.Equal(2,store.BackupTo(backup).CopiedFiles);using var restored=ArchiveStore.Open(backup);Assert.Equal(2,restored.Page("room",10).Count);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
