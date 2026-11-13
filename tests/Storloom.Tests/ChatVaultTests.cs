using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;

namespace Storloom.Tests;

public sealed class ChatVaultTests : IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","vault-"+Guid.NewGuid().ToString("N"));
    private const string Password="synthetic-vault-password";
    public ChatVaultTests(){Directory.CreateDirectory(root);JsonFiles.Write(Path.Combine(root,"export-report.json"),new ChatExportResult(root,1,1,[],true));}
    private byte[] Add(string path,int count){byte[] bytes=RandomNumberGenerator.GetBytes(count);string file=Path.Combine(root,path);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file,bytes);return bytes;}
    private static byte[] Read(Stream stream){using(stream){using var output=new MemoryStream();stream.CopyTo(output);return output.ToArray();}}
    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(8)]
    public void PartialProtectionCanResumeWithPasswordAndSeekAcrossAuthenticatedChunks(int workers){
        byte[] content=Add("messages.jsonl",2*1024*1024+17),image=Add("assets/fixture.bin",512),empty=Add("empty",0);
        using(var vault=ChatVault.Create(root,Password)){
            var result=vault.Encrypt(false,workers);Assert.False(result.Complete);Assert.False(File.Exists(Path.Combine(root,"messages.jsonl")));Assert.True(File.Exists(Path.Combine(root,"assets/fixture.bin")));
            Assert.Equal(content,Read(vault.OpenRead("messages.jsonl")));Assert.Equal(image,Read(vault.OpenRead("assets/fixture.bin")));Assert.Equal(empty,Read(vault.OpenRead("empty")));
        }
        Assert.ThrowsAny<CryptographicException>(()=>ChatVault.Open(root,"incorrect-password"));
        using(var vault=ChatVault.Open(root,Password)){
            using var stream=vault.OpenRead("messages.jsonl");stream.Position=1024*1024-13;byte[] slice=new byte[40];stream.ReadExactly(slice);Assert.Equal(content.AsSpan(1024*1024-13,40).ToArray(),slice);
            Assert.Throws<FileNotFoundException>(()=>vault.OpenRead("../outside"));Assert.True(vault.Encrypt(true,workers).Complete);
        }
        using(var vault=ChatVault.Open(root,Password)){Assert.True(vault.Status.Complete);Assert.Equal(image,Read(vault.OpenRead("assets/fixture.bin")));}
        Assert.False(File.Exists(Path.Combine(root,"export-report.json")));
        Assert.DoesNotContain(Password,File.ReadAllText(Path.Combine(root,ChatVault.Marker)));
    }
    [Fact]
    public void CancellationKeepsPlaintextAndVerifiedChunksAndResumeDoesNotRewriteTheirPrefix(){
        byte[] content=Add("large.bin",4*1024*1024+73);using var cancellation=new CancellationTokenSource();
        using(var vault=ChatVault.Create(root,Password)){
            var progress=new Callback(p=>{if(p.Bytes>=2*1024*1024)cancellation.Cancel();});
            Assert.ThrowsAny<OperationCanceledException>(()=>vault.Encrypt(true,1,progress,cancellation.Token));
        }
        Assert.Equal(content,File.ReadAllBytes(Path.Combine(root,"large.bin")));
        string partial=Directory.GetFiles(Path.Combine(root,".vault"),"*.enc").Single(f=>new FileInfo(f).Length>1024*1024);byte[] prefix=File.ReadAllBytes(partial);long length=new FileInfo(partial).Length;
        using(var vault=ChatVault.Open(root,Password)){Assert.True(vault.Encrypt().Complete);Assert.Equal(content,Read(vault.OpenRead("large.bin")));}
        using var cipher=File.OpenRead(partial);byte[] reused=new byte[length];cipher.ReadExactly(reused);Assert.Equal(prefix,reused);
    }
    [Fact]
    public void CorruptedCiphertextFailsAuthenticationAndModifiedSourcesAreRetained(){
        Add("message.txt",512);using(var vault=ChatVault.Create(root,Password)){vault.Encrypt();}
        string cipher=Directory.GetFiles(Path.Combine(root,".vault"),"*.enc").Single(p=>new FileInfo(p).Length==52+512+28&&File.ReadAllBytes(p).AsSpan(0,8).SequenceEqual("SLVAULT1"u8));
        using(var edit=new FileStream(cipher,FileMode.Open,FileAccess.ReadWrite)){edit.Position=65;int value=edit.ReadByte();edit.Position=65;edit.WriteByte((byte)(value^1));}
        using(var vault=ChatVault.Open(root,Password)){Assert.ThrowsAny<CryptographicException>(()=>Read(vault.OpenRead("message.txt")));}
    }
    [Fact]
    public async Task PausedWorkersStayIdleResumeAndRemainCancellable(){
        using var pause=new OperationPause();using var scope=pause.Enter();using var reached=new ManualResetEventSlim();
        pause.Pause();var task=Task.Run(()=>{OperationPause.Check();reached.Set();});Assert.False(reached.Wait(100));pause.Resume();await task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation=new CancellationTokenSource();pause.Pause();var stopped=Task.Run(()=>OperationPause.Check(cancellation.Token));cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await stopped);
    }
    [Fact]
    public async Task RawBackupCopyStopsAtPauseAndResumesWithoutRestarting(){
        string source=Path.Combine(root,"source"),target=Path.Combine(root,"copy");Directory.CreateDirectory(source);File.WriteAllBytes(Path.Combine(source,"large.bin"),RandomNumberGenerator.GetBytes(4*1024*1024));
        var service=new TransferService();var plan=service.Plan(source,target);using var pause=new OperationPause();using var scope=pause.Enter();using var reached=new ManualResetEventSlim();
        long last=0;var progress=new Callback(p=>{Interlocked.Exchange(ref last,p.Bytes);if(p.Bytes>=1024*1024&&!reached.IsSet){pause.Pause();reached.Set();}});
        var task=Task.Run(()=>service.Execute(plan,Path.Combine(root,"journals"),progress));
        Assert.True(reached.Wait(3000));long before=Interlocked.Read(ref last);await Task.Delay(100);Assert.Equal(before,Interlocked.Read(ref last));Assert.False(task.IsCompleted);
        pause.Resume();var result=await task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(result.Cancelled);Assert.Equal(1,result.VerifiedFiles);
        Assert.Equal(File.ReadAllBytes(Path.Combine(source,"large.bin")),File.ReadAllBytes(Path.Combine(target,"large.bin")));
    }
    [Fact]
    public void SourceChangesDoNotDestroyData(){
        Add("message.txt",100);using(var vault=ChatVault.Create(root,Password)){File.AppendAllText(Path.Combine(root,"message.txt"),"changed");Assert.ThrowsAny<Exception>(()=>vault.Encrypt());Assert.True(File.Exists(Path.Combine(root,"message.txt")));}
    }
    [Fact]
    public void InterruptedJournalAppendIsDiscardedWithoutLosingCommittedEntries(){
        byte[] content=Add("message.txt",128),media=Add("assets/later.bin",300);
        using(var vault=ChatVault.Create(root,Password)){vault.Encrypt(false);}
        using(var log=new FileStream(Path.Combine(root,".vault","commits.enc"),FileMode.Append,FileAccess.Write)){log.Write(new byte[]{100,0,0,0,1,2});}
        using(var vault=ChatVault.Open(root,Password)){Assert.Equal(content,Read(vault.OpenRead("message.txt")));Assert.True(vault.Encrypt().Complete);}
        using(var vault=ChatVault.Open(root,Password)){Assert.Equal(media,Read(vault.OpenRead("assets/later.bin")));Assert.True(vault.Status.Complete);}
    }
    [Fact]
    public void NewPlaintextOutsideThePlanCannotBeReportedAsFullyProtected(){
        Add("message.txt",20);using(var vault=ChatVault.Create(root,Password)){vault.Encrypt();File.WriteAllText(Path.Combine(root,"added-later.txt"),"synthetic extra");Assert.False(vault.Status.Complete);Assert.Equal(vault.Status.ProtectedFiles+1,vault.Status.TotalFiles);Assert.ThrowsAny<IOException>(()=>vault.Encrypt());}
        Assert.True(File.Exists(Path.Combine(root,"added-later.txt")));
    }
    [Fact]
    public void TruncatedCiphertextDoesNotCountAsProtectedOrAllowSourceRemoval(){
        Add("message.txt",2*1024*1024);using var cancellation=new CancellationTokenSource();
        using(var vault=ChatVault.Create(root,Password)){
            Assert.ThrowsAny<OperationCanceledException>(()=>vault.Encrypt(true,1,new Callback(p=>{if(p.Bytes>1024*1024)cancellation.Cancel();}),cancellation.Token));
        }
        string cipher=Directory.GetFiles(Path.Combine(root,".vault"),"*.enc").Single(p=>new FileInfo(p).Length>1024*1024);
        using(var file=new FileStream(cipher,FileMode.Open,FileAccess.ReadWrite)){file.SetLength(file.Length-7);}
        using(var vault=ChatVault.Open(root,Password)){Assert.False(vault.Status.Complete);Assert.True(File.Exists(Path.Combine(root,"message.txt")));Assert.True(vault.Encrypt().Complete);}
    }
    private sealed class Callback(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private Dictionary<string,string> CipherHashes()=>Directory.GetFiles(Path.Combine(root,".vault")).ToDictionary(p=>Path.GetFileName(p),p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
    [Fact]public void PasswordChangesOnlyRewrapDataKeyAndRejectStaleWriters(){
        byte[] plain=Add("messages.jsonl",8192),media=Add("assets/later.bin",128);
        using(var initial=ChatVault.Create(root,Password)){Assert.True(initial.UsesWrappedKey);initial.Encrypt(false);}
        var before=CipherHashes();using var stale=ChatVault.Open(root,Password);
        using(var vault=ChatVault.Open(root,Password)){vault.ChangePassword("replacement-password");Assert.Equal(before,CipherHashes());Assert.Equal(plain,Read(vault.OpenRead("messages.jsonl")));}
        Assert.ThrowsAny<CryptographicException>(()=>ChatVault.Open(root,Password));Assert.Throws<IOException>(()=>stale.ChangePassword("stale-writer-password"));
        using(var vault=ChatVault.Open(root,"replacement-password")){Assert.True(vault.UsesWrappedKey);Assert.True(vault.Encrypt().Complete);Assert.Equal(media,Read(vault.OpenRead("assets/later.bin")));}
    }
    [Theory][InlineData(1)][InlineData(2)]public void LegacyReadersUpgradePasswordEnvelopeWithoutReencryptingContent(int version){
        byte[] plain=Add("messages.jsonl",8192),salt=RandomNumberGenerator.GetBytes(32),legacy=Rfc2898DeriveBytes.Pbkdf2(Password,salt,600000,HashAlgorithmName.SHA256,32);
        try{using(var vault=ChatVault.Create(root,Convert.ToHexString(legacy),keyMode:ChatVaultKeyMode.DirectKey)){vault.Encrypt();}
            JsonFiles.Write(Path.Combine(root,ChatVault.Marker),new{Version=version,Salt=Convert.ToBase64String(salt),Iterations=600000});var before=CipherHashes();
            using(var vault=ChatVault.Open(root,Password)){Assert.False(vault.UsesWrappedKey);vault.ChangePassword("upgraded-password");Assert.True(vault.UsesWrappedKey);}
            Assert.Equal(before,CipherHashes());using var reopened=ChatVault.Open(root,"upgraded-password");Assert.Equal(plain,Read(reopened.OpenRead("messages.jsonl")));Assert.ThrowsAny<CryptographicException>(()=>ChatVault.Open(root,Password));
        }finally{CryptographicOperations.ZeroMemory(legacy);}
    }
    [Fact]public void DirectKeysRemainDirectAndCannotBeChangedByPasswordRewrapping(){
        byte[] plain=Add("messages.jsonl",8192);string direct=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using(var vault=ChatVault.Create(root,direct,keyMode:ChatVaultKeyMode.DirectKey)){Assert.Equal(ChatVaultKeyMode.DirectKey,vault.KeyMode);vault.Encrypt();Assert.Throws<InvalidOperationException>(()=>vault.ChangePassword("next-password"));}
        using var reopened=ChatVault.Open(root,direct.ToLowerInvariant());Assert.Equal(plain,Read(reopened.OpenRead("messages.jsonl")));Assert.ThrowsAny<CryptographicException>(()=>ChatVault.Open(root,new string('0',64)));Assert.Throws<ArgumentException>(()=>ChatVault.Open(root,"short"));
    }
    [Theory][InlineData("KeyId")][InlineData("Wrapped")][InlineData("Iterations")][InlineData("Mode")]
    public void RejectsTamperedKeyEnvelopes(string field){
        Add("messages.jsonl",128);using(var vault=ChatVault.Create(root,Password))vault.Encrypt();string marker=Path.Combine(root,ChatVault.Marker);var config=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(marker))!.AsObject();
        config[char.ToLowerInvariant(field[0])+field[1..]]=field switch{"KeyId"=>Guid.NewGuid().ToString("N"),"Wrapped"=>Convert.ToBase64String(new byte[80]),"Iterations"=>System.Text.Json.Nodes.JsonValue.Create(1),_=>"direct-key"};File.WriteAllText(marker,config.ToJsonString());Assert.ThrowsAny<Exception>(()=>ChatVault.Open(root,Password));
    }

    [Fact]public void LegacyPartialRuleRequirementsSurviveEnvelopeUpgrade(){
        Add("messages.jsonl",128);byte[] salt=RandomNumberGenerator.GetBytes(32),legacy=Rfc2898DeriveBytes.Pbkdf2(Password,salt,600000,HashAlgorithmName.SHA256,32);
        try{using(var vault=ChatVault.Create(root,Convert.ToHexString(legacy),keyMode:ChatVaultKeyMode.DirectKey))vault.Encrypt();JsonFiles.Write(Path.Combine(root,ChatVault.Marker),new{Version=2,Salt=Convert.ToBase64String(salt),Iterations=600000});File.Delete(Path.Combine(root,".vault","protection.enc"));using var opened=ChatVault.Open(root,Password);Assert.Throws<InvalidOperationException>(()=>opened.ReadProtection());opened.ChangePassword("upgraded-partial-password");using var upgraded=ChatVault.Open(root,"upgraded-partial-password");Assert.Throws<InvalidOperationException>(()=>upgraded.ReadProtection());}finally{CryptographicOperations.ZeroMemory(legacy);}
    }

}
