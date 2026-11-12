using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;

namespace Storloom.Tests;

public sealed class ChatToolsTests : IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","chat-tools-"+Guid.NewGuid().ToString("N"));
    public ChatToolsTests()=>Directory.CreateDirectory(root);
    [Fact]
    public void QuickDiscoveryFindsRedirectedAccountsWithoutSizingMediaOrFollowingMediaTrees() {
        string data=Path.Combine(root,"Documents","Tencent Files","123456","nt_qq","nt_db");Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"nt_msg.db"),"database fixture");
        string media=Path.Combine(root,"Documents","Tencent Files","123456","pic","nested");Directory.CreateDirectory(media);File.WriteAllText(Path.Combine(media,"nt_msg.db"),"not an account database");
        var result=Assert.Single(ChatDiscovery.Discover([root],measureSize:false));
        Assert.Equal("123456",result.Account);Assert.Null(result.AccountBytes);Assert.False(result.SizeComplete);
        Assert.Equal(Encoding.UTF8.GetByteCount("database fixture"),result.DatabaseBytes);
    }
    [Fact]
    public void ProtectedKeyUsesIndependentPasswordsAuthenticatesAndNeverOverwrites() {
        byte[] secret=Encoding.ASCII.GetBytes("0123456789abcdef");string path=Path.Combine(root,"ordinary.dat");
        ChatKeyFile.Save(path,secret,ChatPlatform.QQ,"password-one");
        Assert.DoesNotContain("0123456789abcdef",File.ReadAllText(path));
        Assert.Equal(secret,ChatKeyFile.Open(path,ChatPlatform.QQ,"password-one"));
        Assert.ThrowsAny<CryptographicException>(()=>ChatKeyFile.Open(path,ChatPlatform.QQ,"password-two"));
        Assert.Throws<InvalidDataException>(()=>ChatKeyFile.Open(path,ChatPlatform.WeChat,"password-one"));
        Assert.Throws<IOException>(()=>ChatKeyFile.Save(path,secret,ChatPlatform.QQ,"password-one"));
        Assert.Throws<ArgumentException>(()=>ChatKeyFile.Save(Path.Combine(root,"short"),secret,ChatPlatform.QQ,"short"));
        var envelope=JsonFiles.Read<ChatKeyFile.Envelope>(path);envelope.Ciphertext[0]^=1;JsonFiles.Write(path,envelope);
        Assert.ThrowsAny<CryptographicException>(()=>ChatKeyFile.Open(path,ChatPlatform.QQ,"password-one"));
    }
    [Fact]
    public void RestoreMaterializesIncrementalReferencesAndRefusesExistingOrChangedData() {
        string source=Path.Combine(root,"account");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"nt_msg.db"),"database fixture");File.WriteAllText(Path.Combine(source,"photo.dat"),"retained resource");
        var service=new TransferService();var first=service.Execute(service.Plan(source,Path.Combine(root,"first")),Path.Combine(root,"journals"));
        File.WriteAllText(Path.Combine(source,"nt_msg.db"),"new database fixture");
        var next=ChatIncrementalBackup.Create(source,Path.Combine(root,"next"),first.Manifest,Path.Combine(root,"journals"));
        var journal=JsonFiles.Read<TransferJournal>(next.Backup.Manifest);Assert.Contains(journal.Receipts,r=>r.StorageRoot!=null);
        string target=Path.Combine(root,"restored");ChatRestore.Restore(next.Backup.Manifest,target,ChatPlatform.QQ);
        Assert.Equal("retained resource",File.ReadAllText(Path.Combine(target,"photo.dat")));Assert.Equal("new database fixture",File.ReadAllText(Path.Combine(target,"nt_msg.db")));
        Assert.Throws<IOException>(()=>ChatRestore.Restore(next.Backup.Manifest,target,ChatPlatform.QQ));
        var resource=journal.Receipts.Single(r=>r.RelativePath=="photo.dat");File.WriteAllText(ChatIncrementalBackup.Resolve(journal.Plan.Destination,resource),"tampered resource");
        string rejected=Path.Combine(root,"rejected");Assert.Throws<InvalidDataException>(()=>ChatRestore.Restore(next.Backup.Manifest,rejected,ChatPlatform.QQ));Assert.False(Directory.Exists(rejected));
    }

    [Fact]
    public void MetadataMeasurementDoesNotReadLockedFileContentsOrRetainDetailedTrees() {
        string directory=Path.Combine(root,"measure");Directory.CreateDirectory(Path.Combine(directory,"nested"));
        string locked=Path.Combine(directory,"locked.bin");File.WriteAllBytes(locked,new byte[4096]);File.WriteAllText(Path.Combine(directory,"nested","a"),"abc");
        using var held=new FileStream(locked,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        var result=new StorageScanner().Measure(directory);
        Assert.Equal(4099,result.Bytes);Assert.Equal(2,result.Files);Assert.Empty(result.Directories);Assert.Empty(result.LargeFiles);Assert.Empty(result.Issues);Assert.False(result.Cancelled);
        using var cancellation=new CancellationTokenSource();
        var cancelled=new StorageScanner().Measure(directory,new CancelMeasurement(cancellation),cancellation.Token);
        Assert.True(cancelled.Cancelled);Assert.Equal(0,cancelled.Bytes);Assert.True(File.Exists(locked));
    }
    private sealed class CancelMeasurement(CancellationTokenSource cancellation) : IProgress<ScanProgress> { public void Report(ScanProgress value)=>cancellation.Cancel(); }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
