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
    [Fact] public void LegacyMetadataIsMigratedAndUnsupportedProtocolsCannotBeRegisteredOrOverwritten() {
        string archive=Path.Combine(root,"legacy");using(var store=ArchiveStore.CreateBundle(archive)){}
        string current=ArchiveMetadata.Write(archive,ChatPlatform.WeChat,"fixture-account");var metadata=ArchiveMetadata.Inspect(current).Metadata;
        string legacy=Path.Combine(archive,ArchiveMetadata.LegacyFileName);JsonFiles.Write(legacy,metadata with{Version=1,StorageFormatVersion=null});File.Delete(current);
        Assert.Equal("fixture-account",ArchiveMetadata.Inspect(legacy).Metadata.Account);
        Assert.Equal(current,ArchiveMetadata.Write(legacy));Assert.False(File.Exists(legacy));Assert.Equal(2,ArchiveMetadata.Inspect(current).Metadata.Version);
        JsonFiles.Write(current,metadata with{ExportProtocolVersion=ArchiveMetadata.CurrentExportProtocolVersion+1});byte[] original=File.ReadAllBytes(current);
        Assert.Throws<InvalidDataException>(()=>ArchiveMetadata.Inspect(current));Assert.Throws<InvalidDataException>(()=>ArchiveMetadata.Write(current));Assert.Equal(original,File.ReadAllBytes(current));
        JsonFiles.Write(legacy,metadata with{Version=1});Assert.Throws<InvalidDataException>(()=>ArchiveMetadata.Write(legacy));Assert.Equal(original,File.ReadAllBytes(current));Assert.True(File.Exists(legacy));
    }
    [Fact] public void ProvenanceAndHistorySurviveConversionCopyAndRepackWithoutDuplicatingRetriedEvents() {
        string source=Path.Combine(root,"reader");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"messages.html"),"synthetic reader");
        File.WriteAllText(Path.Combine(source,"messages.jsonl"),System.Text.Json.JsonSerializer.Serialize(new ChatMessage("QQ","room","sender",1,1,"fixture message"){Id="one"})+"\n");
        var project=ChatArchiveProject.Create("fixture-account",ChatPlatform.QQ,Path.Combine(root,"client"),Path.Combine(root,"backups"));
        ArchiveMetadata.RecordExport(source,project,"reading-export");var exported=ArchiveMetadata.Inspect(source).Metadata;ArchiveMetadata.RecordExport(source,project,"reading-export");
        Assert.Single(ArchiveMetadata.Inspect(source).Metadata.History);Assert.Equal(project.Source,exported.Origin!.Location);Assert.Equal(1,exported.ExportProtocolVersion);Assert.NotNull(exported.ExporterVersion);
        using var imported=ArchiveStore.CreateBundle(Path.Combine(root,"imported"));imported.ImportReader(source);
        var converted=ArchiveMetadata.Inspect(Path.Combine(root,"imported")).Metadata;Assert.Equal(exported.Origin,converted.Origin);Assert.Equal(new[]{"reading-export","import"},converted.History.Select(h=>h.Action));
        string copy=Path.Combine(root,"copied");imported.BackupTo(copy);var copied=ArchiveMetadata.Inspect(copy).Metadata;Assert.Equal(converted.Origin,copied.Origin);Assert.Equal(3,copied.History.Length);Assert.Equal("copy",copied.History[^1].Action);
        using var repacked=ArchiveStore.CreateBundle(Path.Combine(root,"repacked"));imported.RepackTo(repacked);
        var reorganized=ArchiveMetadata.Inspect(Path.Combine(root,"repacked")).Metadata;Assert.Equal(converted.Origin,reorganized.Origin);Assert.Equal("repack",reorganized.History[^1].Action);
        Assert.Contains(reorganized.History,h=>h.Source?.ArchiveId==converted.Id);Assert.NotEqual(converted.Id,reorganized.Id);
    }
    [Fact] public void RecoveryDoesNotInventAnOriginalExporterVersionOrExportDate() {
        string archive=Path.Combine(root,"recovered");using(var store=ArchiveStore.CreateBundle(archive)){}
        var project=ChatArchiveProject.Create("fixture-account",ChatPlatform.WeChat,Path.Combine(root,"client"),root);
        ArchiveMetadata.RecordExport(archive,project,"recovered",recovered:true);var metadata=ArchiveMetadata.Inspect(archive).Metadata;
        Assert.Null(metadata.ExporterVersion);var entry=Assert.Single(metadata.History);Assert.True(entry.Recovered);Assert.Null(entry.ExporterVersion);Assert.Equal("recovered",entry.Action);
    }
    [Fact] public void DiscoveryReadsProtectedMarkersWithoutUnlockingAndDoesNotTraverseArchiveInternals() {
        string archive=Path.Combine(root,"QQ","account","reader");Directory.CreateDirectory(Path.GetDirectoryName(archive)!);using(var store=ArchiveStore.CreateBundle(archive,"fixture-password")) {
            using var writer=store.BeginWrite();writer.Complete(new(new string('a',64),"complete",0,0,0,0,0));
        }
        ArchiveMetadata.Write(archive,ChatPlatform.QQ,"fixture-account");string metadata=Path.Combine(archive,ArchiveMetadata.FileName);byte[] original=File.ReadAllBytes(metadata);
        string nested=Path.Combine(archive,"archive-data","query","fake-reader");Directory.CreateDirectory(nested);File.WriteAllText(Path.Combine(nested,"messages.html"),"not a separate archive");
        string legacy=Path.Combine(root,"legacy");Directory.CreateDirectory(legacy);File.WriteAllText(Path.Combine(legacy,"messages.html"),"synthetic legacy reader");
        string unfinished=Path.Combine(root,"unfinished");Directory.CreateDirectory(unfinished);File.WriteAllText(Path.Combine(unfinished,"messages.html"),"unfinished reader");JsonFiles.Write(Path.Combine(unfinished,"export-progress.json"),new ChatExportProgress());
        string invalid=Path.Combine(root,"incompatible");using(var store=ArchiveStore.CreateBundle(invalid)){}
        string invalidFile=ArchiveMetadata.Write(invalid);var descriptor=ArchiveMetadata.Inspect(invalid).Metadata;JsonFiles.Write(invalidFile,descriptor with{ExportProtocolVersion=99});
        var result=ArchiveDiscovery.Scan(root);Assert.True(result.Complete);Assert.Equal(2,result.Archives.Length);Assert.Contains(result.Archives,r=>r.Metadata.Account=="fixture-account");
        Assert.Contains(result.Archives,r=>r.Directory==legacy&&!r.HasMetadata);Assert.Contains(result.Issues,i=>i.Path==invalid);Assert.Equal(original,File.ReadAllBytes(metadata));Assert.False(File.Exists(Path.Combine(legacy,ArchiveMetadata.FileName)));
        Assert.Contains(result.Issues,i=>i.Path==unfinished&&i.Reason=="incomplete");
        Assert.Single(ArchiveDiscovery.Scan(Path.Combine(archive,"archive-data")).Archives);
    }
    [Fact] public void DiscoveryReportsPartialScansAndHonorsCancellation() {
        Directory.CreateDirectory(Path.Combine(root,"one","two"));Assert.False(ArchiveDiscovery.Scan(root,maximumDepth:0).Complete);
        Assert.False(ArchiveDiscovery.Scan(root,maximumDirectories:1).Complete);
        using var stop=new CancellationTokenSource();stop.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>ArchiveDiscovery.Scan(root,stop.Token));
    }
    public void Dispose(){try{Directory.Delete(root,true);}catch(IOException){}}
}
