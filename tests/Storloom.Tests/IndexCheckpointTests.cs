using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;
public sealed class IndexCheckpointTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","index-checkpoint-"+Guid.NewGuid().ToString("N"));
    public IndexCheckpointTests()=>Directory.CreateDirectory(root);
    private sealed class Callback(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    [Fact]public void CursorResumesAtUtf8BoundariesAndHandlesBomCrLfAndFinalLine(){
        string file=Path.Combine(root,"lines");File.WriteAllText(file,"\uFEFFfirst\r\n中文🙂\nlast",new UTF8Encoding(false));long offset;
        using(var cursor=new Utf8LineCursor(file)){Assert.Equal("first",cursor.ReadLine(default));offset=cursor.Offset;Assert.Equal(10,offset);}
        using var resumed=new Utf8LineCursor(file,offset);Assert.Equal("中文🙂",resumed.ReadLine(default));Assert.Equal("last",resumed.ReadLine(default));Assert.Null(resumed.ReadLine(default));Assert.Equal(new FileInfo(file).Length,resumed.Offset);
    }
    [Fact]public void InterruptedBootstrapReplaysOnlyUncommittedBatch(){
        string file=Path.Combine(root,"messages"),db=Path.Combine(root,"index.db");File.WriteAllLines(file,Enumerable.Range(0,25).Select(i=>i+"中文"),new UTF8Encoding(false));
        using var stop=new CancellationTokenSource();int decoded=0;
        (string,string,string) Decode(string line){decoded++;return(line,line,line);}
        Assert.ThrowsAny<OperationCanceledException>(()=>ChatIndexBootstrap.Build(file,db,25,Decode,new Callback(p=>{if(p.Files==10)stop.Cancel();}),stop.Token,10));
        Assert.Equal(10,decoded);decoded=0;ChatIndexBootstrap.Build(file,db,25,Decode,null,default,10);Assert.Equal(15,decoded);
        using(var index=new ChatUpdateIndex(db))Assert.Equal("25",index.Query("SELECT count(*) FROM messages").Single()[0]);
        decoded=0;ChatIndexBootstrap.Build(file,db,25,Decode,null,default,10);Assert.Equal(0,decoded);
        File.AppendAllText(file,"changed");Assert.Throws<InvalidDataException>(()=>ChatIndexBootstrap.Build(file,db,25,Decode,null,default,10));
    }
    [Fact]public void FailureMidBatchDoesNotAdvanceCheckpoint(){
        string file=Path.Combine(root,"messages"),db=Path.Combine(root,"index.db");File.WriteAllLines(file,Enumerable.Range(0,25).Select(i=>i.ToString()));int decoded=0;
        Assert.Throws<IOException>(()=>ChatIndexBootstrap.Build(file,db,25,line=>{if(++decoded==14)throw new IOException("fixture");return(line,line,line);},null,default,10));
        decoded=0;ChatIndexBootstrap.Build(file,db,25,line=>{decoded++;return(line,line,line);},null,default,10);Assert.Equal(15,decoded);
    }
    [Fact]public void PreparedStatementsResetBindingsAndAllowNestedEnumeration(){
        using var index=new ChatUpdateIndex(Path.Combine(root,"index.db"));index.Execute("INSERT INTO messages VALUES(?,?,?)","a","one","id");
        for(int i=0;i<100;i++){Assert.Equal("one",index.Query("SELECT h FROM messages WHERE k=?","a").Single()[0]);Assert.Empty(index.Query("SELECT h FROM messages WHERE k=?","missing"));}
        foreach(var row in index.Query("SELECT k FROM messages"))Assert.Equal(row,index.Query("SELECT k FROM messages").Single());
        Assert.Throws<IOException>(()=>index.Execute("INSERT INTO messages VALUES(?,?,?)","a","duplicate","id"));
        index.Execute("INSERT INTO messages VALUES(?,?,?)","b","two","id2");Assert.Equal("2",index.Query("SELECT count(*) FROM messages").Single()[0]);
    }
    [Fact]public void InvalidIndexConstructionReleasesNativeHandle(){string path=Path.Combine(root,"invalid.db");File.WriteAllText(path,"invalid index");Assert.ThrowsAny<Exception>(()=>new ChatUpdateIndex(path));File.Move(path,path+".rejected");Assert.True(File.Exists(path+".rejected"));}
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
