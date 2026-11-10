using System.Security.Cryptography;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;

namespace Storloom.Tests;

public sealed class ParallelBackupTests : IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","parallel-"+Guid.NewGuid().ToString("N"));
    private string Source=>Path.Combine(root,"source");
    public ParallelBackupTests(){Directory.CreateDirectory(Path.Combine(Source,"empty"));for(int i=0;i<16;i++)File.WriteAllBytes(Path.Combine(Source,$"file-{i}.bin"),RandomNumberGenerator.GetBytes(32768+i));}
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)][InlineData(8)]
    public void EveryWorkerSettingProducesVerifiedIndependentAndIncrementalBackups(int workers){
        var service=new TransferService(workers);string target=Path.Combine(root,"copy");var plan=service.Plan(Source,target);
        var copy=service.Execute(plan,Path.Combine(root,"journals"));Assert.Equal(16,copy.VerifiedFiles);Assert.True(Directory.Exists(Path.Combine(target,"empty")));
        foreach(var entry in plan.Files){Assert.Equal(entry.Stamp,NativeFile.ReadStamp(TransferService.Child(Source,entry.RelativePath)));Assert.Equal(File.ReadAllBytes(TransferService.Child(Source,entry.RelativePath)),File.ReadAllBytes(TransferService.Child(target,entry.RelativePath)));}
        File.WriteAllText(Path.Combine(Source,"file-0.bin"),"changed synthetic resource");
        var update=ChatIncrementalBackup.Create(Source,Path.Combine(root,"incremental"),copy.Manifest,Path.Combine(root,"journals"),workers:workers);
        Assert.Equal(15,update.ReusedFiles);var journal=JsonFiles.Read<TransferJournal>(update.Backup.Manifest);Assert.Equal(16,journal.Receipts.Count);
        foreach(var receipt in journal.Receipts){using var input=File.OpenRead(ChatIncrementalBackup.Resolve(journal.Plan.Destination,receipt));Assert.Equal(receipt.Sha256,Convert.ToHexString(SHA256.HashData(input)));}
    }
    [Fact]
    public void ParallelMoveCancellationBeforeVerificationRetainsAllSources(){
        var service=new TransferService(8);var plan=service.Plan(Source,Path.Combine(root,"move"),TransferMode.Move);using var stop=new CancellationTokenSource();
        var result=service.Execute(plan,Path.Combine(root,"journals"),new Callback(p=>{if(p.Bytes>0)stop.Cancel();}),stop.Token);
        Assert.True(result.Cancelled);Assert.Equal(0,result.RemovedFiles);Assert.False(Directory.Exists(plan.Destination));
        foreach(var entry in plan.Files)Assert.Equal(entry.Stamp,NativeFile.ReadStamp(TransferService.Child(Source,entry.RelativePath)));
    }
    private sealed class Callback(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
