using System.Security.Cryptography;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;
public sealed class ArchiveKeyringTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","archive-keyring-"+Guid.NewGuid().ToString("N"));
    public ArchiveKeyringTests()=>Directory.CreateDirectory(root);
    [Fact]public void PasswordChangesRewrapTheSameMasterAndLeaveExistingCiphertextReadable(){
        byte[] cipher;string id;
        using(var keys=ArchiveKeyring.Create(root,"password-one")){id=keys.Id;cipher=keys.Protect("private fixture"u8.ToArray(),"messages/private");keys.ChangePassword("password-two");}
        Assert.ThrowsAny<CryptographicException>(()=>ArchiveKeyring.Open(root,"password-one"));
        using var reopened=ArchiveKeyring.Open(root,"password-two");Assert.Equal(id,reopened.Id);Assert.Equal("private fixture"u8.ToArray(),reopened.Unprotect(cipher,"messages/private"));
        Assert.ThrowsAny<CryptographicException>(()=>reopened.Unprotect(cipher,"media/private"));cipher[^1]^=1;Assert.ThrowsAny<CryptographicException>(()=>reopened.Unprotect(cipher,"messages/private"));
        string file=File.ReadAllText(Path.Combine(root,"keyring.json"));Assert.DoesNotContain("password-two",file);Assert.DoesNotContain("private fixture",file);
    }
    [Fact]public void DomainsAndArchivesCannotSwapEncryptedPacks(){
        string other=Path.Combine(root,"other");Directory.CreateDirectory(other);using var first=ArchiveKeyring.Create(root,"12345678");using var second=ArchiveKeyring.Create(other,"12345678");byte[] cipher=first.Protect("fixture"u8.ToArray(),"messages/private");Assert.NotEqual(first.Id,second.Id);Assert.ThrowsAny<CryptographicException>(()=>second.Unprotect(cipher,"messages/private"));
        using var stale=ArchiveKeyring.Open(root,"12345678");first.ChangePassword("abcdefgh");Assert.Throws<IOException>(()=>stale.ChangePassword("new-password"));
    }
    [Fact]public void EncryptedPacksAndQueryCacheContinueAfterPasswordRewrap(){
        const string marker="private-end-to-end-fixture";string cache=Path.Combine(root,"cache");Directory.CreateDirectory(cache);
        var log=new ArchiveCommitLog(root);byte[] queryKey;string head;
        using(var keys=ArchiveKeyring.Create(root,"12345678")){
            var message=new ArchiveQueryCache.Message("a","room",1,marker,"{\"raw\":\"preserved\"}");
            byte[] bytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[]{message});
            head=log.Append(keys.Protect(bytes,"messages/private"),null);queryKey=keys.DomainKey("query/private");
            using(var query=new ArchiveQueryCache(cache,true,queryKey)){query.Replay(log,head,b=>keys.Unprotect(b,"messages/private"));Assert.Single(query.Search(marker));}
            CryptographicOperations.ZeroMemory(queryKey);keys.ChangePassword("abcdefgh");
        }
        foreach(string path in Directory.GetFiles(root,"*",SearchOption.AllDirectories)){byte[] bytes=File.ReadAllBytes(path);Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(marker))<0);Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(marker))<0);}
        using var reopened=ArchiveKeyring.Open(root,"abcdefgh");queryKey=reopened.DomainKey("query/private");
        try{using var query=new ArchiveQueryCache(cache,true,queryKey);Assert.Equal(head,query.Applied);Assert.Equal(0,query.Replay(log,head,b=>reopened.Unprotect(b,"messages/private")));Assert.Contains("preserved",Assert.Single(query.Search(marker)).Json);}finally{CryptographicOperations.ZeroMemory(queryKey);}
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
