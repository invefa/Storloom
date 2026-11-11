using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;

namespace Storloom.Tests;

public sealed class ChatReaderTests : IDisposable {
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7WQAAAAASUVORK5CYII=");
    public ChatReaderTests() => Directory.CreateDirectory(root);
    [Fact]
    public void ScopedIdentitySnapshotsKeepThreeNamesAndImmutableAvatarVersions() {
        string source=Path.Combine(root,"identity-source"),target=Path.Combine(root,"identity-reader"),work=Path.Combine(root,"identity-work");
        Directory.CreateDirectory(source);Directory.CreateDirectory(target);Directory.CreateDirectory(work);
        var first=new ChatIdentity("sender","group:fixture",Nickname:"QQ name",FriendRemark:"My remark",GroupCard:"Group name",Avatar:"assets/"+new string('a',64)+".png",ObservedAt:DateTimeOffset.UtcNow);
        var second=first with { Nickname="New QQ name",Avatar="assets/"+new string('b',64)+".png" };
        using(var writer=new ChatArchiveWriter(source,target,work,[],null,CancellationToken.None,"self")) {
            writer.AddPrepared(new("QQ","group:fixture","sender",1700000000,1,"old") {Identity=first});
            writer.AddPrepared(new("QQ","group:fixture","sender",1700000001,1,"new") {Identity=second});writer.Finish(null);
        }
        var messages=File.ReadLines(Path.Combine(target,"messages.jsonl")).Select(l=>JsonSerializer.Deserialize<ChatMessage>(l)!).ToArray();
        Assert.Equal(first.Version,messages[0].IdentityVersion);Assert.Equal(second.Version,messages[1].IdentityVersion);Assert.Null(messages[0].Identity);
        string text=File.ReadAllText(Path.Combine(target,"archive-index.js"));using var index=JsonDocument.Parse(text["window.archiveIndex=".Length..].TrimEnd(';'));
        var profiles=index.RootElement.GetProperty("identityVersions");Assert.Equal(2,profiles.EnumerateObject().Count());
        Assert.Equal("My remark",profiles.GetProperty(first.Version).GetProperty("FriendRemark").GetString());
        Assert.NotEqual(profiles.GetProperty(first.Version).GetProperty("Avatar").GetString(),profiles.GetProperty(second.Version).GetProperty("Avatar").GetString());
        var baseline=new System.Text.Json.Nodes.JsonObject{["avatars"]=new System.Text.Json.Nodes.JsonObject{["sender"]="old.png"}};
        ChatIdentityHistory.FreezeLegacy(baseline);baseline["avatars"]!["sender"]="new.png";ChatIdentityHistory.FreezeLegacy(baseline);
        Assert.Equal("old.png",baseline["identityBaseline"]!["avatars"]!["sender"]!.GetValue<string>());
    }
    [Fact]
    public void AvatarMetadataIsExportedAndReaderRepairRejectsInvalidPaths() {
        string source=Path.Combine(root,"account");Directory.CreateDirectory(source);
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','hello');");
        Database(Path.Combine(source,"head_image.db"),"CREATE TABLE head_image(username,image_buffer);INSERT INTO head_image VALUES('fixture',X'"+Convert.ToHexString(Png)+"'),('invalid',X'000102');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.WeChat);
        Assert.True(result.Issues.Count==0,string.Join(" | ",result.Issues.Select(i=>i.ToString())));string indexPath=Path.Combine(result.Directory,"archive-index.js");using var index=JsonDocument.Parse(File.ReadAllText(indexPath)["window.archiveIndex=".Length..].TrimEnd(';'));
        var avatars=index.RootElement.GetProperty("avatars");string path=avatars.GetProperty("fixture").GetString()!;Assert.Single(avatars.EnumerateObject());Assert.Equal(Png,File.ReadAllBytes(Path.Combine(result.Directory,path)));
        string[] before=Directory.GetFiles(Path.Combine(result.Directory,"assets"));ChatReaderRepair.Refresh(result.Directory,avatars:new Dictionary<string,string>{{"alias",path}});Assert.Equal(before,Directory.GetFiles(Path.Combine(result.Directory,"assets")));
        Assert.Throws<InvalidDataException>(()=>ChatReaderRepair.Refresh(result.Directory,avatars:new Dictionary<string,string>{{"bad","../image.jpg"}}));
        using var repaired=JsonDocument.Parse(File.ReadAllText(indexPath)["window.archiveIndex=".Length..].TrimEnd(';'));Assert.Equal(path,repaired.RootElement.GetProperty("avatars").GetProperty("alias").GetString());
    }
    [Fact]
    public void QQGroupAvatarsMapToGroupConversationsWithoutUsingTheSameNumberAsAPerson() {
        string source=Path.Combine(root,"group-account");Directory.CreateDirectory(source);
        Database(Path.Combine(source,"nt_msg.db"),"CREATE TABLE group_msg_table(\"40021\",\"40033\",\"40050\",\"40800\");INSERT INTO group_msg_table VALUES(12345,54321,1700000000,'group fixture');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"group-raw")),Path.Combine(root,"journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"group-reading"),ChatPlatform.QQ);
        string asset="assets/"+Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant()+".png";Directory.CreateDirectory(Path.Combine(result.Directory,"assets"));File.WriteAllBytes(Path.Combine(result.Directory,asset),Png);
        ChatReaderRepair.Refresh(result.Directory,avatars:new Dictionary<string,string>{{"group:12345",asset}});
        using var index=JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory,"archive-index.js"))["window.archiveIndex=".Length..].TrimEnd(';'));
        string id=JsonFiles.Hash("group:12345")[..24].ToLowerInvariant();Assert.Equal(asset,index.RootElement.GetProperty("conversationAvatars").GetProperty(id).GetString());
    }
    [Fact]
    public void WeChatTerminalPaddingAndLiteralMarkupKeepTheOriginalBody() {
        const string xml="<msg><appmsg><type>57</type><title>reply text</title><refermsg><type>1</type><content>quoted fixture</content><svrid>123</svrid></refermsg></appmsg></msg>\0\0";
        var message=new ChatMessage("WeChat","fixture","sender",1700000000,244813135921,xml);
        var part=Assert.Single(MessagePresentation.Parse(message));Assert.Equal("reply",part.Kind);Assert.Contains("quoted fixture",part.ReferenceText);Assert.Contains('\0',message.Text);
        foreach(string body in new[]{"<script>literal text</script>","{literal fixture}","<a href='fixture'>literal link</a>"}) {var text=Assert.Single(MessagePresentation.Parse(message with{Type=1,Text=body}));Assert.Equal("text",text.Kind);Assert.Equal(body,text.Text);}
    }
    [Fact]
    public void LocalSilkAndAmrReferencesDecodeWithoutAFileDownloadCard() {
        string source=Path.Combine(root,"voice-local"),destination=Path.Combine(root,"voice-reader"),work=Path.Combine(root,"voice-work");Directory.CreateDirectory(source);Directory.CreateDirectory(destination);Directory.CreateDirectory(work);
        byte[] wave=Convert.FromHexString("524946462600000057415645666D74201000000001000100C05D000080BB00000200100064617461020000000000");
        File.WriteAllBytes(Path.Combine(source,"voice.amr"),wave);File.WriteAllBytes(Path.Combine(source,"voice.silk"),wave);
        using var writer=new ChatArchiveWriter(source,destination,work,[],null,CancellationToken.None,"fixture");
        foreach(string key in new[]{"voice.amr","voice.silk"}){var media=writer.ResolveMedia(key);Assert.NotNull(media);Assert.Equal("audio",media.Kind);Assert.Equal(wave,File.ReadAllBytes(Path.Combine(destination,media.Path)));}
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ContactNamesLocalSenderIdsChronologicalPagingAndLegacyImagesSurviveParallelExport(int workers) {
        string source = Path.Combine(root, "wxid_self_device"); Directory.CreateDirectory(source);
        string friend = "wxid_fixture_friend", table = "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(friend))).ToLowerInvariant();
        const string mediaId = "0123456789abcdef0123456789abcdef";
        for (int shard = 0; shard < 2; shard++) {
            int peer = shard == 0 ? 1 : 2, self = shard == 0 ? 2 : 1;
            string sql = $"CREATE TABLE Name2Id(user_name TEXT); INSERT INTO Name2Id(rowid,user_name) VALUES({peer},'{friend}'),({self},'wxid_self');" +
                $"CREATE TABLE {table}(local_type INTEGER,create_time INTEGER,real_sender_id INTEGER,message_content TEXT);" +
                $"WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<200) INSERT INTO {table} " +
                $"SELECT CASE WHEN x=1 AND {shard}=0 THEN 3 ELSE 1 END,1700000000+x+{(shard == 0 ? 200 : 0)},CASE WHEN x%2=0 THEN {self} ELSE {peer} END," +
                $"CASE WHEN x=1 AND {shard}=0 THEN '<msg><img md5=\"{mediaId}\"/></msg>' ELSE 'fixture message' END FROM seq;";
            Database(Path.Combine(source, $"message_{shard}.db"), sql);
        }
        Database(Path.Combine(source, "contact.db"), $"CREATE TABLE contact(username,nick_name,remark); INSERT INTO contact VALUES('{friend}','Fixture nickname','Fixture remark'),('wxid_self','Self fixture','');");
        Directory.CreateDirectory(Path.Combine(source, "attach"));
        File.WriteAllBytes(Path.Combine(source, "attach", mediaId + ".dat"), Png.Select(b => (byte)(b ^ 0x53)).ToArray());
        var transfer = new TransferService(); var backup = transfer.Execute(transfer.Plan(source, Path.Combine(root, "raw")), Path.Combine(root, "journals"));
        var result = new ChatExporter(workers).ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "reading"), ChatPlatform.WeChat);
        Assert.True(result.Complete, string.Join(";", result.Issues)); Assert.Equal(400, result.Messages); Assert.Equal(2, result.Contacts);
        Assert.Equal(1, result.MediaFiles); Assert.Equal(0, result.UnresolvedMedia);
        var messages = File.ReadLines(Path.Combine(result.Directory, "messages.jsonl")).Select(line => JsonSerializer.Deserialize<ChatMessage>(line)!).ToArray();
        Assert.All(messages, m => Assert.Equal(friend, m.Conversation)); Assert.All(messages, m => Assert.Equal("Fixture remark", m.ConversationName));
        Assert.Equal(200, messages.Count(m => m.IsSelf));
        Assert.All(messages.Where(m => !m.IsSelf), m => Assert.Equal("Fixture remark", m.SenderName));
        var pages = Directory.GetFiles(Path.Combine(result.Directory, "conversations"), "*-?.js").Order().ToArray(); Assert.Equal(2, pages.Length);
        var ordered = pages.SelectMany(page => {
            string js = File.ReadAllText(page); return JsonSerializer.Deserialize<ChatMessage[]>(js[js.IndexOf('[')..(js.LastIndexOf(']') + 1)])!;
        }).ToArray();
        Assert.Equal(400, ordered.Length); Assert.Equal(ordered.Select(m => m.Timestamp).Order(), ordered.Select(m => m.Timestamp));
        string image = Directory.GetFiles(Path.Combine(result.Directory, "assets")).Single(); Assert.Equal(Png, File.ReadAllBytes(image));
        Assert.Empty(Directory.GetDirectories(result.Directory, ".work-*"));
    }
    [Fact]
    public void BoundedParallelismPreservesTheSameMessagesAtScaleAndRecordsPhaseTimes() {
        string source = Path.Combine(root, "scale-account"); Directory.CreateDirectory(source);
        for (int index=0; index<4; index++) Database(Path.Combine(source,$"message_{index}.db"),
            "CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);"+
            $"WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<3000) INSERT INTO MSG SELECT 1,1700000000+x+{index*3000},'fixture-peer','Synthetic benchmark text' FROM seq;");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
        var single=new ChatExporter(1).ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"one-worker"),ChatPlatform.WeChat);
        var parallel=new ChatExporter(2).ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"two-workers"),ChatPlatform.WeChat);
        Assert.True(single.Complete);Assert.True(parallel.Complete);Assert.Equal(12000,single.Messages);Assert.Equal(single.Messages,parallel.Messages);
        using var first=File.OpenRead(Path.Combine(single.Directory,"messages.jsonl"));using var second=File.OpenRead(Path.Combine(parallel.Directory,"messages.jsonl"));
        Assert.Equal(SHA256.HashData(first),SHA256.HashData(second));
        DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
        JsonFiles.Write(Path.Combine(repo!.FullName,"artifacts/performance/chat-parsing.json"),new { fixtureMessages=12000,
            singleSeconds=single.ElapsedSeconds, twoWorkerSeconds=parallel.ElapsedSeconds, singlePhases=single.PhaseSeconds,
            twoWorkerPhases=parallel.PhaseSeconds, outputHashesEqual=true, scope="Synthetic plaintext shards on the test volume, not a real-account or HDD benchmark." });
    }
    [Fact]
    public void UnknownEncryptedImageIsReportedWithoutAFalseReadableImage() {
        string source = Path.Combine(root, "account"); Directory.CreateDirectory(source);
        const string id = "0123456789abcdef0123456789abcdef";
        Database(Path.Combine(source, "message_0.db"), "CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);" +
            $"INSERT INTO MSG VALUES(3,1700000000,'fixture','<msg><img md5=\"{id}\"/></msg>');");
        File.WriteAllBytes(Path.Combine(source, id + ".dat"), Encoding.ASCII.GetBytes("V2MMWX-private-image-fixture"));
        var service = new TransferService(); var backup = service.Execute(service.Plan(source, Path.Combine(root, "raw")), Path.Combine(root, "journals"));
        var result = new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "reading"), ChatPlatform.WeChat);
        Assert.False(result.Complete); Assert.Equal(1, result.UnresolvedMedia); Assert.Equal(0, result.MediaFiles);
        Assert.True(File.Exists(Path.Combine(result.Directory, "messages.html")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackedLocalReferencesDecodeV2ImagesAndFallBackToLabelledThumbnails(bool thumbnail) {
        string source=Path.Combine(root,"wxid_fixture_device");Directory.CreateDirectory(source);
        const string local="fedcba9876543210fedcba9876543210", original="0123456789abcdef0123456789abcdef";
        byte[] key=Encoding.ASCII.GetBytes(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("21345wxid_fixture"))).ToLowerInvariant()[..16]);
        byte[] encrypted;
        using(var aes=Aes.Create()){aes.Key=key;encrypted=aes.EncryptEcb(Png.AsSpan(0,32),PaddingMode.PKCS7);}
        byte[] container=new byte[15+encrypted.Length+Png.Length-32];new byte[]{7,8,0x56,0x32,8,7}.CopyTo(container,0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(6),32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(10),8);
        encrypted.CopyTo(container,15);Png.AsSpan(32).CopyTo(container.AsSpan(15+encrypted.Length));
        for(int i=container.Length-8;i<container.Length;i++)container[i]^=unchecked((byte)21345);
        string config=Path.Combine(source,"config","kvcomm");Directory.CreateDirectory(config);File.WriteAllText(Path.Combine(config,"key_1_wrong.statistic"),"");File.WriteAllText(Path.Combine(config,"key_21345_fixture.statistic"),"");
        File.WriteAllBytes(Path.Combine(source,local+(thumbnail?"_t.dat":"_h.dat")),container);
        if(thumbnail)File.WriteAllBytes(Path.Combine(source,local+"_h.dat"),Encoding.ASCII.GetBytes("wxgf unsupported full size fixture"));
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE Msg_fixture(local_type,create_time,real_sender_id,message_content,server_id,packed_info_data);"+
            $"INSERT INTO Msg_fixture VALUES(3,1700000000,1,'<msg><img md5=\"{original}\"/></msg>',-2,X'00{Convert.ToHexString(Encoding.ASCII.GetBytes(local))}00');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.WeChat);
        Assert.Empty(result.Issues);Assert.Equal(!thumbnail,result.Complete);Assert.Equal(thumbnail?1:0,result.UnresolvedMedia);
        var message=JsonSerializer.Deserialize<ChatMessage>(File.ReadLines(Path.Combine(result.Directory,"messages.jsonl")).Single())!;
        Assert.Equal("18446744073709551614",message.ServerId);Assert.Contains(local,message.LocalMediaKeys);Assert.Equal(thumbnail?"thumbnail":null,Assert.Single(message.Media).Quality);
        Assert.Equal(Png,File.ReadAllBytes(Path.Combine(result.Directory,message.Media[0].Path.Replace('/',Path.DirectorySeparatorChar))));
        string refs=File.ReadAllText(Directory.GetFiles(Path.Combine(result.Directory,"conversations"),"*-refs.js").Single());Assert.Contains("18446744073709551614",refs);
        Assert.DoesNotContain(Encoding.ASCII.GetString(key),File.ReadAllText(Path.Combine(result.Directory,"messages.jsonl")));CryptographicOperations.ZeroMemory(key);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V1ImagesAcceptValidatedBinaryAndAsciiKeyVariants(bool ascii) {
        string source=Path.Combine(root,"v1-fixture");Directory.CreateDirectory(source);const string id="0123456789abcdef0123456789abcdef";
        byte[] digest=MD5.HashData("0"u8),key=ascii?Encoding.ASCII.GetBytes(Convert.ToHexString(digest).ToLowerInvariant()[..16]):digest;
        byte[] padded=new byte[(Png.Length+15)/16*16];Png.CopyTo(padded,0);byte[] encrypted;
        using(var aes=Aes.Create()){aes.Key=key;encrypted=aes.EncryptEcb(padded,PaddingMode.None);}
        File.WriteAllBytes(Path.Combine(source,id+".dat"),[7,8,0x56,0x31,8,7,..encrypted]);
        Database(Path.Combine(source,"message_0.db"),$"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(3,1700000000,'fixture','<msg><img md5=\"{id}\"/></msg>');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.WeChat);
        Assert.True(result.Complete);Assert.Equal(1,result.MediaFiles);Assert.Equal(padded,File.ReadAllBytes(Directory.GetFiles(Path.Combine(result.Directory,"assets")).Single()));
    }
    [Fact]
    public void LargeVideosCopyThroughTheStreamingMediaPath() {
        string source=Path.Combine(root,"video-fixture");Directory.CreateDirectory(source);const string id="0123456789abcdef0123456789abcdef";
        using(var video=File.Create(Path.Combine(source,id+".mp4"))){video.Write(Encoding.ASCII.GetBytes("isolated video-sized fixture"));video.SetLength(129L*1024*1024);}
        Database(Path.Combine(source,"message_0.db"),$"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(43,1700000000,'fixture','<msg><videomsg md5=\"{id}\"/></msg>');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.WeChat);
        Assert.True(result.Complete);Assert.Equal(129L*1024*1024,new FileInfo(Directory.GetFiles(Path.Combine(result.Directory,"assets")).Single()).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(result.Directory,"assets"),".pending-*"));
    }
    [Fact]
    public void QqSystemDirectionAndCachedForwardSnapshotsAreDisplayedWithoutInventingMissingContent() {
        string source=Path.Combine(root,"qq-fixture");Directory.CreateDirectory(source);
        static byte[] Field(int n,byte[] b) {var bytes=new List<byte>();void V(ulong v){while(v>=128){bytes.Add((byte)(v|128));v>>=7;}bytes.Add((byte)v);}V((ulong)((n<<3)|2));V((ulong)b.Length);bytes.AddRange(b);return bytes.ToArray();}
        byte[] cached=Field(40900,[..Field(40093,Encoding.UTF8.GetBytes("Forwarded friend")),..Field(40800,Field(45101,Encoding.UTF8.GetBytes("Embedded text")))]);
        Database(Path.Combine(source,"nt_msg.db"),"CREATE TABLE group_msg_table(\"40021\",\"40033\",\"40050\",\"40800\",\"40011\",\"40013\",\"40090\",\"40900\");"+
            "INSERT INTO group_msg_table VALUES('fixture',1,1700000000,'Alex invited Sam',5,3,NULL,NULL);"+
            $"INSERT INTO group_msg_table VALUES('fixture',2,1700000001,X'{Convert.ToHexString(Field(45101,Encoding.UTF8.GetBytes("forward")))}',8,0,'Group card',X'{Convert.ToHexString(cached)}');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.QQ);
        var messages=File.ReadLines(Path.Combine(result.Directory,"messages.jsonl")).Select(l=>JsonSerializer.Deserialize<ChatMessage>(l)!).ToArray();
        Assert.Equal("system",messages[0].Parts[0].Kind);Assert.Equal("Alex invited Sam",messages[0].Parts[0].Text);Assert.Equal("Group card",messages[1].SenderName);
        Assert.Equal("Embedded text",Assert.Single(messages[1].Parts[0].Forwarded).Parts[0].Text);
    }
    [Fact]
    public void AccountSpecificEmoticonCachesDecryptDuringExportAndResourceRepairLeavesMessagesUntouched() {
        string source=Path.Combine(root,"wxid_fixture_device");Directory.CreateDirectory(source);
        const string id="0123456789abcdef0123456789abcdef";
        string config=Path.Combine(source,"kvcomm");Directory.CreateDirectory(config);File.WriteAllText(Path.Combine(config,"key_12345_fixture.statistic"),"");
        byte[] key=MD5.HashData(Encoding.UTF8.GetBytes("12345wxid_fixtureEMOTICON"));
        using(var aes=Aes.Create()){aes.Key=key;File.WriteAllBytes(Path.Combine(source,id),aes.EncryptCbc(Png,key,PaddingMode.PKCS7));}
        string duplicate=Path.Combine(source,"cache/Persist");Directory.CreateDirectory(duplicate);File.Copy(Path.Combine(source,id),Path.Combine(duplicate,id));
        CryptographicOperations.ZeroMemory(key);
        Database(Path.Combine(source,"message_0.db"),$"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(47,1700000000,'fixture','<msg><emoji md5=\"{id}\"/></msg>');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.WeChat);
        var message=JsonSerializer.Deserialize<ChatMessage>(File.ReadLines(Path.Combine(result.Directory,"messages.jsonl")).Single())!;
        Assert.Equal(Png,File.ReadAllBytes(Path.Combine(result.Directory,Assert.Single(message.Media).Path)));
        string messages=Path.Combine(result.Directory,"messages.jsonl"), page=Directory.GetFiles(Path.Combine(result.Directory,"conversations"),"*-0.js").Single();
        byte[] messageBefore=File.ReadAllBytes(messages),pageBefore=File.ReadAllBytes(page);
        File.Delete(Path.Combine(result.Directory, message.Media[0].Path));
        Assert.Equal(1,ChatReaderRepair.RestoreResources(result.Directory,source));
        Assert.Equal(Png,File.ReadAllBytes(Path.Combine(result.Directory,message.Media[0].Path)));
        Assert.Equal(messageBefore,File.ReadAllBytes(messages));Assert.Equal(pageBefore,File.ReadAllBytes(page));
        Assert.Contains("resourceMedia",File.ReadAllText(Path.Combine(result.Directory,"archive-index.js")));
    }
    [Fact]
    public void DocumentAttachmentsAreIndexedAndCopiedAsFiles() {
        string source=Path.Combine(root,"documents");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"fixture.pdf"),"%PDF-fixture");
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(49,1700000000,'fixture','[attachment] fixture.pdf');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.WeChat);
        var message=JsonSerializer.Deserialize<ChatMessage>(File.ReadLines(Path.Combine(result.Directory,"messages.jsonl")).Single())!;
        Assert.Equal("file",Assert.Single(message.Media).Kind);Assert.Equal("%PDF-fixture",File.ReadAllText(Path.Combine(result.Directory,message.Media[0].Path)));
    }
    [Fact]
    public void VoiceDatabaseMatchesConversationAndUnsignedServerIdWithoutGuessingAnotherChat() {
        string source=Path.Combine(root,"voices");Directory.CreateDirectory(source);
        byte[] wave=Convert.FromHexString("524946462600000057415645666D74201000000001000100C05D000080BB00000200100064617461020000000000");
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent,MsgSvrID);INSERT INTO MSG VALUES(34,1700000000,'fixture','<msg><voicemsg voicelength=\"1000\"/></msg>',-1),(34,1700000000,'another','<msg><voicemsg/></msg>',-1);");
        Database(Path.Combine(source,"media_0.db"),"CREATE TABLE Name2Id(user_name TEXT);INSERT INTO Name2Id VALUES('fixture');CREATE TABLE VoiceInfo(chat_name_id,svr_id,voice_data);"+$"INSERT INTO VoiceInfo VALUES(1,-1,X'{Convert.ToHexString(wave)}');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(root,"raw")),Path.Combine(root,"journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.WeChat);
        Assert.Empty(result.Issues); Assert.Equal(2, result.Messages);
        var messages=File.ReadLines(Path.Combine(result.Directory,"messages.jsonl")).Select(l=>JsonSerializer.Deserialize<ChatMessage>(l)!).ToArray();
        Assert.Equal("audio",Assert.Single(messages[0].Media).Kind);Assert.Equal(wave,File.ReadAllBytes(Path.Combine(result.Directory,messages[0].Media[0].Path)));
        Assert.Empty(messages[1].Media);Assert.Equal(1,result.UnresolvedMedia);
    }
    [Fact]
    public void EmojiAliasesShareResourcesAndBecomeInlineForBothPlatforms() {
        string source=Path.Combine(root,"emoji"),destination=Path.Combine(root,"reader");Directory.CreateDirectory(source);Directory.CreateDirectory(destination);
        string qq=Path.Combine(source,"nt_qq/nt_data/Emoji/BaseEmojiSyastems/EmojiSystermResource");Directory.CreateDirectory(qq);
        File.WriteAllBytes(Path.Combine(qq,"75.png"),Png);File.WriteAllText(Path.Combine(qq,"default_config.json"),"[{\"emojiId\":\"75\",\"describe\":\"/斜眼笑\"}]");
        var emoji=new BuiltInEmoji(source,destination);emoji.AddWeChat(new(){["base-aliases.txt"]=Encoding.UTF8.GetBytes("[斜眼笑]"),["expression_1.png"]=Png});
        foreach(string platform in new[]{"QQ","WeChat"}) {
            string? resource=emoji.Use(platform+":/斜眼笑");Assert.NotNull(resource);Assert.Equal(resource,emoji.Use(platform+":[斜眼笑]"));
            Assert.True(Assert.Single(emoji.NormalizeParts(platform,[new("sticker",Title:"[斜眼笑]")])).Inline);
        }
        emoji.UseParts("WeChat",[new("text","/斜眼笑 [斜眼笑]")]);Assert.Contains("WeChat:/斜眼笑",emoji.Exported.Keys);
    }
    [Fact]
    public void WeChatLegacyAliasesFollowResourceOrderAcrossInsertedFacesAndGestures() {
        string destination=Path.Combine(root,"legacy-emoji");Directory.CreateDirectory(destination);
        string[] names=Enumerable.Range(1,110).Select(i=>"[fixture"+i+"]").ToArray();names[21]="[白眼]";names[30]="[疑问]";names[85]="[爱你]";names[87]="[飞吻]";
        var files=new Dictionary<string,byte[]>{["base-aliases.txt"]=Encoding.UTF8.GetBytes(string.Join('|',names))};
        foreach(int resource in new[]{23,33,88,92,103})files["expression_"+resource+".png"]=Png.Concat(new[]{(byte)resource}).ToArray();
        var emoji=new BuiltInEmoji(root,destination);emoji.AddWeChat(files);
        foreach(var pair in new[]{("白眼",23),("疑问",33),("爱你",88),("飞吻",92),("fixture99",103)}){
            string asset=emoji.Use("WeChat:["+pair.Item1+"]")!;Assert.NotNull(asset);Assert.Equal(pair.Item2,File.ReadAllBytes(Path.Combine(destination,asset))[^1]);
        }
        Assert.Null(emoji.Use("WeChat:[fixture100]"));
    }
    private static void Database(string path, string sql) {
        Assert.Equal(0, sqlite3_open_v2(path, out nint db, 6, 0));
        try { Assert.Equal(0, sqlite3_exec(db, sql, 0, 0, 0)); } finally { sqlite3_close_v2(db); }
    }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint state, nint error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(nint db);
    public void Dispose() {
        if (!PathPolicy.Under(root, Path.Combine(AppContext.BaseDirectory, "fixtures"))) throw new IOException("Fixture boundary changed.");
        Directory.Delete(root, true);
    }
}
