using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;
public sealed class ArchiveStoreTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","archive-store-"+Guid.NewGuid().ToString("N"));
    private const string Password="fixture-password";
    private sealed class Callback(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    public ArchiveStoreTests()=>Directory.CreateDirectory(root);
    private string Reader(int messages=600){
        string path=Path.Combine(root,"reader");Directory.CreateDirectory(path);
        File.WriteAllLines(Path.Combine(path,"messages.jsonl"),Enumerable.Range(0,messages).Select(i=>JsonSerializer.Serialize(new ChatMessage("Fixture","room","sender",i,1,"synthetic 中文 "+i){Id="m"+i,RawBase64="AAEC",ServerId=(i+1).ToString()})[..^1]+",\"futureUnknown\":true}"),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path,"notes.txt"),"original fixture bytes\r\n",new UTF8Encoding(true));File.WriteAllBytes(Path.Combine(path,"empty.bin"),[]);return path;
    }
    private static byte[] Read(Stream input){using(input){using var output=new MemoryStream();input.CopyTo(output);return output.ToArray();}}
    [Theory][InlineData(false)][InlineData(true)]
    public void MigrationPreservesAllSourceBytesAndUnknownMessageFields(bool encrypted){
        string source=Reader(),target=Path.Combine(root,"archive");var originals=Directory.GetFiles(source).ToDictionary(p=>p,p=>(Stamp:NativeFile.ReadStamp(p),Bytes:File.ReadAllBytes(p)));
        using(var store=ArchiveStore.Create(target,encrypted?Password:default)){
            var result=store.ImportReader(source);Assert.Equal("complete",result.Stage);Assert.Equal(600,result.Messages);Assert.Equal(3,result.Files);Assert.Equal(originals.Sum(p=>p.Value.Bytes.LongLength),result.Bytes);
            Assert.Equal(600,store.Page("Fixture/room",1000).Count);Assert.Contains("futureUnknown",store.Page("Fixture/room",1)[0].Json);Assert.Contains("AAEC",store.Page("Fixture/room",1)[0].Json);
            foreach(var entry in originals){Assert.Equal(entry.Value.Bytes,Read(store.OpenFile(Path.GetFileName(entry.Key))));Assert.Equal(entry.Value.Stamp,NativeFile.ReadStamp(entry.Key));}
            string? head=store.Head;Assert.Equal(result,store.ImportReader(source));Assert.Equal(head,store.Head);
            Assert.False(File.Exists(Path.Combine(source,".export.lock")));
        }
        using var reopened=ArchiveStore.Open(target,encrypted?Password:default);Assert.Single(reopened.Search("synthetic 中文 599"));
        if(encrypted)foreach(string file in Directory.GetFiles(target,"*",SearchOption.AllDirectories)){using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var output=new MemoryStream();input.CopyTo(output);Assert.True(output.ToArray().AsSpan().IndexOf("synthetic"u8)<0);}
    }
    [Fact]public void MessageCheckpointResumesWithoutReimportingCommittedPrefix(){
        string source=Reader(1100),target=Path.Combine(root,"archive");using var stop=new CancellationTokenSource();
        using(var store=ArchiveStore.Create(target)){
            Assert.ThrowsAny<OperationCanceledException>(()=>store.ImportReader(source,new Callback(p=>{if(p.RuleId=="archive-migration-messages"&&p.Files==512)stop.Cancel();}),stop.Token));Assert.Equal(512,store.ImportCursor!.Messages);Assert.Equal(512,store.Page("Fixture/room",2000).Count);
        }
        var reports=new List<ScanProgress>();using var resumed=ArchiveStore.Open(target);Assert.Equal(1100,resumed.ImportReader(source,new Callback(reports.Add)).Messages);Assert.DoesNotContain(reports,p=>p.RuleId=="archive-migration-messages"&&p.Files<=512);Assert.Equal(1100,resumed.Page("Fixture/room",2000).Count);
    }
    [Fact]public void LargeFileResumesFromChunkBoundaryAndSupportsSeekAndDeduplication(){
        string source=Reader(2),assets=Path.Combine(source,"assets");Directory.CreateDirectory(assets);byte[] chunk=new byte[ArchiveStore.ChunkBytes];RandomNumberGenerator.Fill(chunk);
        string large=Path.Combine(assets,"large.bin");using(var output=File.Create(large)){for(int n=0;n<9;n++)output.Write(chunk);output.Write(chunk.AsSpan(0,37));}File.Copy(large,Path.Combine(assets,"same.bin"));
        string target=Path.Combine(root,"archive");using var stop=new CancellationTokenSource();
        using(var store=ArchiveStore.Create(target,Password)){
            Assert.ThrowsAny<OperationCanceledException>(()=>store.ImportReader(source,new Callback(p=>{if(p.RuleId=="archive-migration-files")stop.Cancel();}),stop.Token));Assert.Equal(8L*ArchiveStore.ChunkBytes,store.ImportCursor!.Offset);Assert.Throws<IOException>(()=>store.OpenFile("assets/large.bin"));
        }
        using var resumed=ArchiveStore.Open(target,Password);resumed.ImportReader(source);byte[] original=File.ReadAllBytes(large);Assert.Equal(original,Read(resumed.OpenFile("assets/large.bin")));Assert.Equal(original,Read(resumed.OpenFile("assets/same.bin")));
        using var stream=resumed.OpenFile("assets/large.bin");stream.Seek(ArchiveStore.ChunkBytes-5,SeekOrigin.Begin);byte[] range=new byte[20];Assert.Equal(20,stream.Read(range));Assert.Equal(original.AsSpan(ArchiveStore.ChunkBytes-5,20).ToArray(),range);stream.Seek(-37,SeekOrigin.End);Assert.Equal(chunk.AsSpan(0,37).ToArray(),ReadRemainder(stream));
        long packBytes=Directory.GetFiles(target,"*.pack").Sum(p=>new FileInfo(p).Length);Assert.True(packBytes<4L*ArchiveStore.ChunkBytes,$"Deduplication failed: {packBytes}");
    }
    private static byte[] ReadRemainder(Stream stream){using var output=new MemoryStream();stream.CopyTo(output);return output.ToArray();}
    [Fact]public void ChangedSourceCannotResumeAndIncrementalUpsertsDoNotRewriteOldPacks(){
        string source=Reader(),target=Path.Combine(root,"archive");using var stop=new CancellationTokenSource();using var store=ArchiveStore.Create(target);
        Assert.ThrowsAny<OperationCanceledException>(()=>store.ImportReader(source,new Callback(p=>{if(p.RuleId=="archive-migration-messages")stop.Cancel();}),stop.Token));File.AppendAllText(Path.Combine(source,"notes.txt"),"changed");Assert.Throws<InvalidDataException>(()=>store.ImportReader(source));
        var packs=Directory.GetFiles(target,"*.pack").ToDictionary(p=>p,NativeFile.ReadStamp);
        var message=new ArchiveQueryCache.Message("new-id","Fixture/room",9999,"incremental fixture","{\"unsupported\":true}");Assert.Equal(1,store.Upsert([message]));string? head=store.Head;Assert.Equal(0,store.Upsert([message]));Assert.Equal(head,store.Head);foreach(var entry in packs)Assert.Equal(entry.Value,NativeFile.ReadStamp(entry.Key));Assert.Single(store.Search("incremental fixture"));
    }
    [Fact]public void QueryCacheCanBeRebuiltFromPacksIncludingResourceLocators(){
        string source=Reader(7),target=Path.Combine(root,"archive");byte[] original=File.ReadAllBytes(Path.Combine(source,"messages.jsonl"));string? head;
        using(var store=ArchiveStore.Create(target,Password)){store.ImportReader(source);head=store.Head;}
        string cache=Path.Combine(target,"query");Directory.Move(cache,Path.Combine(root,"detached-cache"));
        using var recovered=ArchiveStore.Open(target,Password);Assert.Equal(head,recovered.Head);Assert.Equal("complete",recovered.ImportCursor!.Stage);Assert.Equal(7,recovered.Page("Fixture/room",10).Count);Assert.Equal(original,Read(recovered.OpenFile("messages.jsonl")));
    }
    [Fact]public void InvalidIncrementalInputIsRejectedBeforePublishingHead(){
        using var store=ArchiveStore.Create(Path.Combine(root,"archive"));var valid=new ArchiveQueryCache.Message("a","room",1,"valid","{}");store.Upsert([valid]);string? head=store.Head;
        Assert.Throws<InvalidDataException>(()=>store.Upsert([valid with{Id=""}]));Assert.Equal(head,store.Head);Assert.Single(store.Page("room",10));
    }
    [Fact]public void MigrationDoesNotCollapseRepeatedNativeIdsOrIdenticalUnidentifiedOccurrences(){
        string source=Reader(0);var identified=new ChatMessage("Fixture","room","sender",1,1,"same native id"){Id="first",ServerId="shared-native-id"};var anonymous=identified with{Id=null,ServerId=null,Text="same anonymous content"};
        File.WriteAllLines(Path.Combine(source,"messages.jsonl"),new[]{JsonSerializer.Serialize(identified),JsonSerializer.Serialize(identified with{Id="second"}),JsonSerializer.Serialize(anonymous),JsonSerializer.Serialize(anonymous)});
        using var store=ArchiveStore.Create(Path.Combine(root,"archive"));store.ImportReader(source);Assert.Equal(4,store.Page("Fixture/room",10).Count);Assert.Equal(2,store.Search("same native id").Count);Assert.Equal(2,store.Search("same anonymous content").Count);
    }
    [Fact]public void FileCheckpointAtPartialFinalChunkDoesNotLoseTheLastChunk(){
        string source=Reader(1);File.WriteAllBytes(Path.Combine(source,"a.bin"),new byte[ArchiveStore.ChunkBytes/2]);byte[] data=new byte[7*ArchiveStore.ChunkBytes+ArchiveStore.ChunkBytes/2+17];RandomNumberGenerator.Fill(data);File.WriteAllBytes(Path.Combine(source,"b.bin"),data);string target=Path.Combine(root,"archive");using var stop=new CancellationTokenSource();
        using(var store=ArchiveStore.Create(target)){
            Assert.ThrowsAny<OperationCanceledException>(()=>store.ImportReader(source,new Callback(p=>{if(p.RuleId=="archive-migration-files")stop.Cancel();}),stop.Token));Assert.Equal(data.LongLength,store.ImportCursor!.Offset);
        }
        using var resumed=ArchiveStore.Open(target);resumed.ImportReader(source);Assert.Equal(data,Read(resumed.OpenFile("b.bin")));
    }
    [Fact]public void IndependentVerificationReportsTotalsCanCancelAndDetectsPackCorruption(){
        string source=Reader(3),target=Path.Combine(root,"archive");using var store=ArchiveStore.Create(target,Password);store.ImportReader(source);var reports=new List<ScanProgress>();var result=store.Verify(new Callback(reports.Add));Assert.Equal(3,result.MessageRevisions);Assert.True(result.ResourceChunks>0);Assert.Equal(store.Head,result.Head);Assert.Equal(reports[^1].TotalBytes,reports[^1].Bytes);Assert.Equal(reports[^1].TotalFiles,reports[^1].Files);
        using var stop=new CancellationTokenSource();stop.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>store.Verify(cancellation:stop.Token));
        string pack=Directory.GetFiles(target,"*.pack").First();byte[] bytes=File.ReadAllBytes(pack);bytes[^1]^=1;File.WriteAllBytes(pack,bytes);Assert.Throws<InvalidDataException>(()=>store.Verify());
    }
    [Fact]public void EncryptedLegacyReaderMigratesThroughDecryptionStreamAndRemainsUntouched(){
        string source=Reader(9);JsonFiles.Write(Path.Combine(source,"export-report.json"),new ChatExportResult(source,9,1,[],true));byte[] original=File.ReadAllBytes(Path.Combine(source,"messages.jsonl"));
        using(var vault=ChatVault.Create(source,Password))vault.Encrypt(workers:2);
        Assert.False(File.Exists(Path.Combine(source,"messages.jsonl")));var files=Directory.GetFiles(source,"*",SearchOption.AllDirectories).ToDictionary(p=>p,NativeFile.ReadStamp);
        using(var unprotected=ArchiveStore.Create(Path.Combine(root,"unprotected")))Assert.Throws<InvalidOperationException>(()=>unprotected.ImportReader(source,sourcePassword:Password));
        using var destination=ArchiveStore.Create(Path.Combine(root,"protected"),"new-fixture-password");destination.ImportReader(source,sourcePassword:Password);Assert.Equal(original,Read(destination.OpenFile("messages.jsonl")));Assert.Equal(9,destination.Page("Fixture/room",10).Count);foreach(var entry in files)Assert.Equal(entry.Value,NativeFile.ReadStamp(entry.Key));Assert.False(File.Exists(Path.Combine(source,"messages.jsonl")));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void CurrentReaderExportResumesAndIncludesIncrementalChangesAndReferences(bool encrypted){
        string source=Reader(603),target=Path.Combine(root,"archive"),export=Path.Combine(root,"exported");
        File.WriteAllText(Path.Combine(source,"archive-index.js"),"window.archiveIndex={\"preferenceScope\":\"fixture-account\",\"labels\":{}};");
        using var store=ArchiveStore.Create(target,encrypted?Password:default);store.ImportReader(source);
        var inventory=store.FileInventory().Single(f=>f.Path=="notes.txt");var originalPacks=Directory.GetFiles(target,"*.pack").ToDictionary(p=>p,NativeFile.ReadStamp);
        var added=new ChatMessage("Fixture","room","sender",10000,1,"new searchable tail"){Id="m603",ServerId="604"};File.AppendAllText(Path.Combine(source,"messages.jsonl"),JsonSerializer.Serialize(added)+"\n");
        store.ImportReader(source);Assert.Equal(604,store.Page("Fixture/room",1000).Count);Assert.Equal(inventory,store.FileInventory().Single(f=>f.Path=="notes.txt"));foreach(var pack in originalPacks)Assert.Equal(pack.Value,NativeFile.ReadStamp(pack.Key));
        string index=Encoding.UTF8.GetString(store.ReaderIndex());using var doc=JsonDocument.Parse(index["window.archiveIndex=".Length..].TrimEnd(';'));string conversation=doc.RootElement.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        Assert.Contains("new searchable tail",Encoding.UTF8.GetString(store.ReaderPage(conversation,2)));Assert.Contains("\"604\":{\"page\":2,\"id\":\"m603\"}",Encoding.UTF8.GetString(store.ReaderReferences(conversation)));Assert.Equal("{\"page\":2,\"id\":\"m603\"}",Encoding.UTF8.GetString(store.ReaderReference(conversation,"604")));
        using var stop=new CancellationTokenSource();Assert.ThrowsAny<OperationCanceledException>(()=>store.ExportReader(export,new Callback(p=>{if(p.Files>=2)stop.Cancel();}),stop.Token));
        store.ExportReader(export);Assert.False(File.Exists(Path.Combine(export,".storloom-export.json")));Assert.Equal(604,File.ReadLines(Path.Combine(export,"messages.jsonl")).Count());Assert.Contains("new searchable tail",File.ReadAllText(Path.Combine(export,"conversations",conversation+"-2.js")));Assert.Contains("futureUnknown",File.ReadAllText(Path.Combine(export,"messages.jsonl")));Assert.Contains("\"nativeStore\":false",File.ReadAllText(Path.Combine(export,"archive-index.js")));Assert.Equal(604,JsonFiles.Read<ChatExportResult>(Path.Combine(export,"export-report.json")).Messages);
        Assert.Equal(File.ReadAllBytes(Path.Combine(source,"notes.txt")),File.ReadAllBytes(Path.Combine(export,"notes.txt")));Assert.Throws<IOException>(()=>store.ExportReader(export));
        store.ImportReader(export);Assert.Equal(604,store.Page("Fixture/room",1000).Count);
        File.WriteAllText(Path.Combine(source,"archive-index.js"),"window.archiveIndex={\"preferenceScope\":\"different-account\"};");Assert.Throws<InvalidDataException>(()=>store.ImportReader(source));
    }
    [Fact]public void NativeSearchPaginatesBothDirectionsWithoutDuplicatesAndHasCorrectPageLocations(){
        string source=Reader(215);using var store=ArchiveStore.Create(Path.Combine(root,"archive"));store.ImportReader(source);
        var ids=new List<string>();string? cursor=null;
        do{using var result=JsonDocument.Parse(store.ReaderSearch("synthetic",cursor));foreach(var row in result.RootElement.GetProperty("rows").EnumerateArray())ids.Add(row.GetProperty("message").GetProperty("Id").GetString()!);cursor=result.RootElement.GetProperty("next").GetString();}while(cursor!=null);
        Assert.Equal(215,ids.Count);Assert.Equal(215,ids.Distinct().Count());Assert.Equal("m0",ids[0]);Assert.Equal("m214",ids[^1]);
        using var reverse=JsonDocument.Parse(store.ReaderSearch("synthetic",reverse:true));Assert.Equal("m214",reverse.RootElement.GetProperty("rows")[0].GetProperty("message").GetProperty("Id").GetString());
    }
    [Fact]public void ModifiedExportFilesAndChangedSourceSnapshotsCannotBeSilentlyResumed(){
        string source=Reader(5),export=Path.Combine(root,"export");using var store=ArchiveStore.Create(Path.Combine(root,"archive"));store.ImportReader(source);using var stop=new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(()=>store.ExportReader(export,new Callback(p=>{if(p.Files>=2)stop.Cancel();}),stop.Token));File.AppendAllText(Path.Combine(export,"notes.txt"),"tampered");Assert.Throws<InvalidDataException>(()=>store.ExportReader(export));
        File.AppendAllText(Path.Combine(source,"messages.jsonl"),JsonSerializer.Serialize(new ChatMessage("Fixture","room","sender",7,1,"changed"){Id="new"})+"\n");store.ImportReader(source);Assert.Throws<InvalidDataException>(()=>store.ExportReader(export));
    }
    [Fact]public void InvalidRawJsonCannotPublishAnUnreadableCommit(){
        using var store=ArchiveStore.Create(Path.Combine(root,"archive"));Assert.ThrowsAny<JsonException>(()=>store.Upsert([new("bad","fixture",0,"fixture","invalid-json")]));Assert.Null(store.Head);Assert.Equal(1,store.Upsert([new("good","fixture",0,"fixture","{}") ]));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ConcurrentResourceReadsRemainCorrectAcrossSharedPackCacheEvictions(bool encrypted){
        string source=Reader(1),assets=Path.Combine(source,"assets");Directory.CreateDirectory(assets);var originals=new Dictionary<string,byte[]>();var random=new Random(218);
        for(int n=0;n<140;n++){byte[] data=new byte[128*1024];random.NextBytes(data);string path="assets/"+n.ToString("D4")+".bin";File.WriteAllBytes(Path.Combine(source,path),data);originals[path]=data;}
        using var store=ArchiveStore.Create(Path.Combine(root,"archive"),encrypted?Password:default);store.ImportReader(source);
        Parallel.ForEach(originals,new ParallelOptions{MaxDegreeOfParallelism=8},item=>{using var stream=store.OpenFile(item.Key);stream.Position=777;byte[] slice=new byte[99];stream.ReadExactly(slice);Assert.Equal(item.Value.AsSpan(777,99).ToArray(),slice);stream.Seek(-99,SeekOrigin.End);stream.ReadExactly(slice);Assert.Equal(item.Value[^99..],slice);});
        using var first=store.OpenFile("assets/0000.bin");Assert.Equal(originals["assets/0000.bin"],ReadRemainder(first));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
