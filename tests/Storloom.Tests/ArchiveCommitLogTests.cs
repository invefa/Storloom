using System.Text;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;
public sealed class ArchiveCommitLogTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","commit-log-"+Guid.NewGuid().ToString("N"));
    public ArchiveCommitLogTests()=>Directory.CreateDirectory(root);
    [Fact]public void IncrementalReplayReadsOnlyNewPacksAndPreservesOldBytes(){
        var log=new ArchiveCommitLog(root);string first=log.Append("first"u8,null);string oldPack=Directory.GetFiles(root,"*.pack").Single();byte[] original=File.ReadAllBytes(oldPack);
        string second=log.Append("second"u8,first);Assert.Equal(original,File.ReadAllBytes(oldPack));
        File.Move(oldPack,oldPack+".offline");var delta=Assert.Single(log.Since(first,second));Assert.Equal("second",Encoding.UTF8.GetString(delta.Payload));Assert.Empty(log.Since(second,second));
        Assert.Throws<FileNotFoundException>(()=>log.Since(null,second).ToArray());
    }
    [Theory][InlineData("payload")][InlineData("commit")]
    public void InterruptedPublicationRetainsPreviousHeadAndRetryDeduplicates(string point){
        var log=new ArchiveCommitLog(root);string first=log.Append("first"u8,null);
        Assert.Throws<IOException>(()=>log.Append("next"u8,first,p=>{if(p==point)throw new IOException("fixture");}));Assert.Equal(first,log.Head);
        string next=log.Append("next"u8,first);Assert.Equal(next,log.Head);Assert.Equal(2,Directory.GetFiles(root,"*.pack").Length);Assert.Equal(2,log.Since(null,next).Count());
    }
    [Fact]public void RejectsStaleWriterTamperedObjectsAndForeignCheckpoints(){
        var log=new ArchiveCommitLog(root);string first=log.Append("first"u8,null);
        Assert.Throws<InvalidOperationException>(()=>log.Append("stale"u8,null));
        Assert.Throws<InvalidDataException>(()=>log.Since(new string('a',64),first).ToArray());
        string pack=Directory.GetFiles(root,"*.pack").Single();File.WriteAllText(pack,"wrong");Assert.Throws<InvalidDataException>(()=>log.Since(null,first).ToArray());Assert.Throws<InvalidDataException>(()=>log.Append("first"u8,first));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
