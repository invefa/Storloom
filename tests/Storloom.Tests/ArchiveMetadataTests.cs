using System.Security.Cryptography;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;

public sealed class ArchiveMetadataTests : IDisposable {
    private readonly string root = Path.Combine(AppContext.BaseDirectory,"fixtures","archive-metadata-"+Guid.NewGuid().ToString("N"));
    public ArchiveMetadataTests() => Directory.CreateDirectory(root);
    [Fact] public void ProtectedArchiveCanBeRegisteredWithoutUnlockingAndTheReferenceSurvivesRelocation() {
        string archive=Path.Combine(root,"original");using(var store=ArchiveStore.CreateBundle(archive,"fixture-only-password")) {
            using var writer=store.BeginWrite();writer.Add(new("QQ","room","sender",1,1,"fixture private message"){Id="one"});writer.Complete(new(new string('a',64),"complete",0,0,1,0,0));
        }
        string data=Path.Combine(archive,"archive-data"),head=File.ReadAllText(Path.Combine(data,"HEAD"));byte[] keyring=SHA256.HashData(File.ReadAllBytes(Path.Combine(data,"keyring.json")));
        string marker=ArchiveMetadata.Write(data,ChatPlatform.QQ,"fixture-account");Assert.Equal(Path.Combine(archive,ArchiveMetadata.FileName),marker);
        var reference=ArchiveMetadata.Inspect(marker);Assert.Equal(archive,reference.Directory);Assert.Equal(data,reference.DataDirectory);Assert.Equal("fixture-account",reference.Metadata.Account);
        string text=File.ReadAllText(marker);Assert.DoesNotContain("fixture-only-password",text);Assert.DoesNotContain(root,text);Assert.DoesNotContain("fixture private message",text);Assert.Equal("archive-data",reference.Metadata.DataPath);
        Assert.Equal(head,File.ReadAllText(Path.Combine(data,"HEAD")));Assert.Equal(keyring,SHA256.HashData(File.ReadAllBytes(Path.Combine(data,"keyring.json"))));
        string moved=Path.Combine(root,"moved");Directory.Move(archive,moved);var relocated=ArchiveMetadata.Inspect(Path.Combine(moved,ArchiveMetadata.FileName));Assert.Equal(moved,relocated.Directory);Assert.Equal(reference.Metadata.Id,relocated.Metadata.Id);
        Assert.Equal(moved,ArchiveMetadata.Inspect(Path.Combine(moved,"archive-data","archive.json")).Directory);
    }
    [Fact] public void MetadataCannotRedirectOutsideTheArchiveOrClaimAnotherArchiveIdentity() {
        string archive=Path.Combine(root,"archive");using(var store=ArchiveStore.CreateBundle(archive)){}
        string file=ArchiveMetadata.Write(archive);var metadata=JsonFiles.Read<ArchiveDescriptor>(file);
        foreach(string path in new[]{"../outside","/outside",root,"query"}) {JsonFiles.Write(file,metadata with{DataPath=path});Assert.Throws<InvalidDataException>(()=>ArchiveMetadata.Inspect(file));}
        JsonFiles.Write(file,metadata with{Id=Guid.NewGuid().ToString("N")});Assert.Throws<InvalidDataException>(()=>ArchiveMetadata.Inspect(file));
        JsonFiles.Write(file,metadata with{Kind="native"});Assert.Throws<InvalidDataException>(()=>ArchiveMetadata.Inspect(file));
    }
    [Fact] public void LegacyFolderHasNoGuessedAccountAndCanBeAssociatedOrExplicitlyUnassigned() {
        string folder=Path.Combine(root,"QQ","fixture-account","reading-old");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"messages.html"),"synthetic reader");
        var legacy=ArchiveMetadata.Inspect(Path.Combine(folder,"messages.html"));Assert.False(legacy.HasMetadata);Assert.Null(legacy.Metadata.Account);Assert.Null(legacy.Metadata.Platform);
        string file=ArchiveMetadata.Write(folder,ChatPlatform.QQ,"fixture-account");var linked=ArchiveMetadata.Inspect(file);Assert.True(linked.HasMetadata);Assert.Equal("fixture-account",linked.Metadata.Account);Assert.Equal(".",linked.Metadata.DataPath);
        ArchiveMetadata.Write(folder,replaceIdentity:true);Assert.Null(ArchiveMetadata.Inspect(file).Metadata.Account);Assert.Null(ArchiveMetadata.Inspect(file).Metadata.Platform);
    }
    public void Dispose(){try{Directory.Delete(root,true);}catch(IOException){}}
}
