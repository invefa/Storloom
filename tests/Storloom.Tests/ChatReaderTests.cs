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
