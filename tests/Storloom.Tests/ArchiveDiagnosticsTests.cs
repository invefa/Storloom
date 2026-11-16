using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;

public sealed class ArchiveDiagnosticsTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","archive-diagnostics-"+Guid.NewGuid().ToString("N"));
    public ArchiveDiagnosticsTests()=>Directory.CreateDirectory(root);
    private string Archive(string name,int count,long missing=0){string directory=Path.Combine(root,name);using var store=ArchiveStore.CreateBundle(directory,"fixture-password");using var writer=store.BeginWrite();for(int i=0;i<count;i++)writer.Add(new("QQ","room","sender",100+i,1,"fixture message"){Id="message-"+i});writer.Bytes("assets/fixture.bin",new byte[]{1,2,3});if(missing>0)writer.Bytes("export-report.json",JsonSerializer.SerializeToUtf8Bytes(new ChatExportResult(directory,count,1,[],false){UnresolvedMedia=(int)missing},JsonFiles.Options));writer.Complete(new(new string('a',64),"complete",0,0,count,1,3));ArchiveMetadata.Write(directory,ChatPlatform.QQ,"fixture-account");return directory;}
    [Fact]public void IndexedComparisonShowsCountsTimeRangesAndMissingMediaWithoutClaimingEquality(){
        string first=Archive("first",2),second=Archive("second",3,1);string head=File.ReadAllText(Path.Combine(first,"archive-data","HEAD"));var left=ArchiveDiagnostics.Overview(first,"fixture-password");var right=ArchiveDiagnostics.Overview(second,"fixture-password");var comparison=ArchiveDiagnostics.Compare(left,right);
        Assert.False(comparison.DifferentAccount);Assert.Equal(2,left.Messages);Assert.Equal(3,right.Messages);Assert.Equal(100,left.FirstTimestamp);Assert.Equal(102,right.LastTimestamp);Assert.Equal(1,right.MissingMedia);Assert.Equal(1,right.Resources);Assert.Equal(3,right.ResourceBytes);Assert.True(left.IndexOnly);Assert.Equal(1,Assert.Single(comparison.Conversations).RightMessages-Assert.Single(comparison.Conversations).LeftMessages);Assert.Equal(head,File.ReadAllText(Path.Combine(first,"archive-data","HEAD")));
        Assert.True(ArchiveDiagnostics.Compare(left,right with{Account="another-account"}).DifferentAccount);
    }
    [Fact]public void IntegrityDistinguishesVerifiedPartialLockedCancelledAndCorrupted(){
        string valid=Archive("valid",2),partial=Archive("partial",2,1);Assert.Equal("verified",ArchiveDiagnostics.Verify(valid,"fixture-password").Outcome);Assert.Equal("partial",ArchiveDiagnostics.Verify(partial,"fixture-password").Outcome);Assert.Equal("locked",ArchiveDiagnostics.Verify(valid,"wrong-password").Outcome);
        using var stopped=new CancellationTokenSource();stopped.Cancel();Assert.Equal("cancelled",ArchiveDiagnostics.Verify(valid,"fixture-password",cancellation:stopped.Token).Outcome);
        string pack=Directory.GetFiles(Path.Combine(valid,"archive-data"),"*.pack",SearchOption.AllDirectories).First();byte[] bytes=File.ReadAllBytes(pack);bytes[^1]^=1;File.WriteAllBytes(pack,bytes);
        Assert.Equal(2,ArchiveDiagnostics.Overview(valid,"fixture-password").Messages);var damaged=ArchiveDiagnostics.Verify(valid,"fixture-password");Assert.Equal("failed",damaged.Outcome);Assert.NotEqual("verified",damaged.Outcome);
    }
    [Fact]public void FolderReadabilityIsNeverReportedAsAuthenticatedVerification(){
        string folder=Path.Combine(root,"folder");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"messages.html"),"fixture reader");File.WriteAllText(Path.Combine(folder,"archive-index.js"),"window.archiveIndex={\"conversations\":[{\"id\":\"room\",\"count\":2}]};");ArchiveMetadata.Write(folder);
        var overview=ArchiveDiagnostics.Overview(folder);Assert.Null(overview.FirstTimestamp);Assert.Null(overview.MissingMedia);var report=ArchiveDiagnostics.Verify(folder);Assert.Equal("partial",report.Outcome);Assert.Equal("folder-readability",report.Scope);Assert.Equal("no-authenticated-baseline",report.Detail);
        JsonFiles.Write(Path.Combine(folder,"export-report.json"),new ChatExportResult(folder,2,1,[],true));
        using(var vault=ChatVault.Create(folder,"fixture-password"))Assert.True(vault.Encrypt().Complete);
        Assert.Equal("verified",ArchiveDiagnostics.Verify(folder,"fixture-password").Outcome);
    }
    [Fact]public void OriginalBackupReportsRecordedDigestsAndDetectsChangedFiles(){
        string source=Path.Combine(root,"client"),journals=Path.Combine(root,"journals");Directory.CreateDirectory(source);Directory.CreateDirectory(journals);File.WriteAllText(Path.Combine(source,"fixture.txt"),"fixture data");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),journals);
        var report=ArchiveDiagnostics.VerifyOriginal(backup.Manifest);Assert.Equal("verified",report.Outcome);Assert.Equal("original-receipts",report.Scope);Assert.Equal(1,report.CheckedFiles);
        File.AppendAllText(Path.Combine(backup.Destination,"fixture.txt"),"changed");Assert.Equal("failed",ArchiveDiagnostics.VerifyOriginal(backup.Manifest).Outcome);
    }
    public void Dispose(){try{Directory.Delete(root,true);}catch(IOException){}}
}
