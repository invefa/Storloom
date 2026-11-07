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
        var pages = Directory.GetFiles(Path.Combine(result.Directory, "conversations"), "*.js").Order().ToArray(); Assert.Equal(2, pages.Length);
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
