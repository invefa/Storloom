using System.Text.Json;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;
public sealed class ArchiveQueryCacheTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","query-cache-"+Guid.NewGuid().ToString("N"));
    public ArchiveQueryCacheTests()=>Directory.CreateDirectory(root);
    private (ArchiveCommitLog Log,string Cache) Store(){string log=Path.Combine(root,"log"),cache=Path.Combine(root,"cache");Directory.CreateDirectory(log);Directory.CreateDirectory(cache);return(new(log),cache);}
    private static byte[] Delta(params ArchiveQueryCache.Message[] messages)=>JsonSerializer.SerializeToUtf8Bytes(messages);
    [Fact]public void PersistentProjectionReplaysOnlyMissingCommitsAndRevisesSearchPostings(){
        var(log,cache)=Store();string first=log.Append(Delta(new ArchiveQueryCache.Message("a","room",1,"旧名字abc","{\"raw\":true}")),null);
        using(var query=new ArchiveQueryCache(cache,false)){Assert.Equal(1,query.Replay(log,first,b=>b));Assert.Single(query.Search("旧名字"));}
        string next=log.Append(Delta(new ArchiveQueryCache.Message("a","room",2,"新名字xyz","{}"),new ArchiveQueryCache.Message("b","room",2,"中文abc🙂","{}")),first);
        using(var query=new ArchiveQueryCache(cache,false)){
            Assert.Equal(first,query.Applied);Assert.Equal(1,query.Replay(log,next,b=>b));Assert.Empty(query.Search("旧名字"));Assert.Empty(query.Search("abcxyz"));Assert.Single(query.Search("新"));Assert.Single(query.Search("中文"));Assert.Single(query.Search("ABC🙂"));Assert.Single(query.Search("🙂"));Assert.Equal(0,query.Replay(log,next,b=>b));
            var page=Assert.Single(query.Page("room",1));Assert.Equal("a",page.Id);Assert.Equal("b",Assert.Single(query.Page("room",1,page.Timestamp,page.Id)).Id);
        }
        // Cache loss is recoverable solely from committed immutable records.
        string rebuilt=Path.Combine(root,"rebuilt");Directory.CreateDirectory(rebuilt);using var recovered=new ArchiveQueryCache(rebuilt,false);Assert.Equal(2,recovered.Replay(log,next,b=>b));Assert.Equal(2,recovered.Page("room",10).Count);
    }
    [Fact]public void FailedDeltaDoesNotExposeHalfAppliedMessagesOrAdvanceCursor(){
        var(log,cache)=Store();string first=log.Append(Delta(new ArchiveQueryCache.Message("a","room",1,"original","{}")),null);using var query=new ArchiveQueryCache(cache,false);query.Replay(log,first,b=>b);
        string invalid=log.Append(Delta(new ArchiveQueryCache.Message("b","room",2,"new","{}"),new ArchiveQueryCache.Message("","room",3,"invalid","{}")),first);
        Assert.Throws<InvalidDataException>(()=>query.Replay(log,invalid,b=>b));Assert.Equal(first,query.Applied);Assert.Equal("a",Assert.Single(query.Page("room",10)).Id);Assert.Empty(query.Search("new"));
    }
    [Fact]public void ProtectedProjectionRequiresExplicitKey(){var(_,cache)=Store();Assert.Throws<IOException>(()=>new ArchiveQueryCache(cache,true));Assert.False(File.Exists(Path.Combine(cache,"query.db")));}
    [Fact]public void EncryptedProjectionReopensWithItsKeyAndDoesNotStoreSearchTextInPlaintext(){
        var(log,cache)=Store();byte[] key=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        const string marker="sensitive-synthetic-search-marker";
        string head=log.Append(Delta(new ArchiveQueryCache.Message("a","room",1,marker,"{}")),null);
        using(var query=new ArchiveQueryCache(cache,true,key)){query.Replay(log,head,b=>b);Assert.Single(query.Search(marker));}
        byte[] database=File.ReadAllBytes(Path.Combine(cache,"query.db"));Assert.False(database.AsSpan().StartsWith("SQLite format 3"u8));Assert.False(database.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(marker))>=0);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>new ArchiveQueryCache(cache,true,new byte[32]));
        using var reopened=new ArchiveQueryCache(cache,true,key);Assert.Equal(head,reopened.Applied);Assert.Single(reopened.Search(marker));
    }
    [Fact]public void EncryptedRollbackJournalDoesNotExposeTransactionContents(){
        string cache=Path.Combine(root,"encrypted");Directory.CreateDirectory(cache);string file=Path.Combine(cache,"fixture.db");byte[] key=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        const string marker="synthetic-private-journal-marker";
        using var database=new ArchiveSqlite(file,key);database.Execute("CREATE TABLE fixture(value TEXT)");database.Execute("INSERT INTO fixture VALUES(?)",marker);
        database.Execute("BEGIN");database.Execute("UPDATE fixture SET value=?","modified");
        foreach(string path in Directory.GetFiles(cache)){using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var output=new MemoryStream();input.CopyTo(output);Assert.True(output.ToArray().AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(marker))<0);}
        database.Execute("ROLLBACK");Assert.Equal(marker,database.Query("SELECT value FROM fixture").Single()[0]);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
