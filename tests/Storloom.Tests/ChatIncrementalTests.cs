using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;

namespace Storloom.Tests;

public sealed class ChatIncrementalTests : IDisposable {
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
    public ChatIncrementalTests() => Directory.CreateDirectory(root);
    private string Account => Path.Combine(root, "account");
    private string DatabasePath(ChatPlatform platform) => Path.Combine(Account, platform == ChatPlatform.QQ ? "nt_msg.db" : "MSG0.db");
    private void Create(ChatPlatform platform) {
        Directory.CreateDirectory(Account);
        Sql(DatabasePath(platform), platform == ChatPlatform.QQ
            ? "CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\",\"40001\");INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'first',101),(3,4,1700000001,'untouched',201);"
            : "CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent,MsgSvrID);INSERT INTO MSG VALUES(1,1700000000,'one','first',101),(1,1700000001,'two','untouched',201);");
    }
    private TransferResult Backup(string name) { var service = new TransferService(); return service.Execute(service.Plan(Account, Path.Combine(root, name)), Path.Combine(root, "journals")); }
    private static ChatMessage[] Messages(string reading) => File.ReadLines(Path.Combine(reading, "messages.jsonl")).Select(s => JsonSerializer.Deserialize<ChatMessage>(s)!).ToArray();
    [Theory]
    [InlineData(ChatPlatform.QQ)]
    [InlineData(ChatPlatform.WeChat)]
    public void UpdateRetainsHistoryReplacesStableIdsAndOnlyRebuildsAffectedPages(ChatPlatform platform) {
        Create(platform); var first = Backup("raw"); string reading = Path.Combine(root, "reading");
        new ChatExporter().ExportVerifiedBackup(first.Manifest, reading, platform);
        var original = Messages(reading); string untouchedConversation = original.Single(m => m.ServerId == "201").Conversation;
        string page = Path.Combine(reading, "conversations", JsonFiles.Hash(untouchedConversation)[..24].ToLowerInvariant() + "-0.js");
        byte[] before = File.ReadAllBytes(page); var stamp = NativeFile.ReadStamp(page);
        Sql(DatabasePath(platform), platform == ChatPlatform.QQ
            ? "UPDATE c2c_msg_table SET \"40800\"='edited' WHERE \"40001\"=101;INSERT INTO c2c_msg_table VALUES(1,2,1700000002,'new',102);DELETE FROM c2c_msg_table WHERE \"40001\"=201;"
            : "UPDATE MSG SET StrContent='edited' WHERE MsgSvrID=101;INSERT INTO MSG VALUES(1,1700000002,'one','new',102);DELETE FROM MSG WHERE MsgSvrID=201;");
        var next = ChatIncrementalBackup.Create(Account, Path.Combine(root, "next"), first.Manifest, Path.Combine(root, "journals"));
        var result = ChatReadingUpdate.Update(next.Backup.Manifest, reading, platform);
        Assert.Equal(1, result.Added); Assert.Equal(1, result.Changed); Assert.Equal(1, result.Conversations);
        var messages = Messages(reading); Assert.Equal(3, messages.Length);
        Assert.Equal(original.Single(m => m.ServerId == "101").Id, messages.Single(m => m.ServerId == "101").Id);
        Assert.Equal("edited", messages.Single(m => m.ServerId == "101").Text);
        Assert.Contains(messages, m => m.ServerId == "201"); // Client retention must not erase archived history.
        Assert.Equal(before, File.ReadAllBytes(page)); Assert.Equal(stamp, NativeFile.ReadStamp(page));
        var transcriptStamp = NativeFile.ReadStamp(Path.Combine(reading, "messages.jsonl"));
        var noChange = ChatReadingUpdate.Update(next.Backup.Manifest, reading, platform);
        Assert.Equal(0, noChange.Added); Assert.Equal(0, noChange.Changed); Assert.Equal(0, noChange.Conversations);
        Assert.Equal(transcriptStamp, NativeFile.ReadStamp(Path.Combine(reading, "messages.jsonl")));
        Assert.Equal(3, Messages(reading).Length);
        Assert.Equal(before, File.ReadAllBytes(page)); Assert.Equal(stamp, NativeFile.ReadStamp(page));
        Assert.False(File.Exists(Path.Combine(reading, ".reading-update-pending.json")));
        Assert.Empty(Directory.GetDirectories(reading, ".reading-update-*"));
    }
    [Fact]
    public void LegacyReaderWithoutCheckpointCanAdoptItsVerifiedSiblingBackup() {
        Create(ChatPlatform.WeChat); var first = Backup("raw"); string reading = first.Destination + "-reading-legacy";
        new ChatExporter().ExportVerifiedBackup(first.Manifest, reading, ChatPlatform.WeChat);
        File.Delete(Path.Combine(reading,"export-progress.json"));
        var before = NativeFile.ReadStamp(Path.Combine(reading,"messages.jsonl"));
        var result = ChatReadingUpdate.Update(first.Manifest,reading,ChatPlatform.WeChat);
        Assert.Equal(0,result.Added);Assert.Equal(before,NativeFile.ReadStamp(Path.Combine(reading,"messages.jsonl")));
        Assert.True(File.Exists(Path.Combine(reading,".reading-update-state.json")));
    }
    [Fact]
    public void IncrementalGenerationsFlattenResourceReferencesAndAuthenticateReusedBytes() {
        Create(ChatPlatform.WeChat); File.WriteAllText(Path.Combine(Account, "resource.txt"), "original");
        var first = Backup("raw");
        var next = ChatIncrementalBackup.Create(Account, Path.Combine(root, "next"), first.Manifest, Path.Combine(root, "journals"));
        Assert.Equal(1, next.ReusedFiles); Assert.False(File.Exists(Path.Combine(next.Backup.Destination, "resource.txt")));
        var third = ChatIncrementalBackup.Create(Account, Path.Combine(root, "third"), next.Backup.Manifest, Path.Combine(root, "journals"));
        var journal = JsonFiles.Read<TransferJournal>(third.Backup.Manifest);
        var receipt = journal.Receipts.Single(r => r.RelativePath == "resource.txt");
        Assert.Equal(first.Destination, receipt.StorageRoot);
        Assert.Equal("original", File.ReadAllText(ChatIncrementalBackup.Resolve(third.Backup.Destination, receipt)));
        File.WriteAllText(Path.Combine(first.Destination, "resource.txt"), "tampered");
        Assert.Throws<InvalidDataException>(() => new ChatExporter().ExportVerifiedBackup(third.Backup.Manifest, Path.Combine(root, "tamper"), ChatPlatform.WeChat));
    }
    [Fact]
    public void ModifiedResourceGetsNewVerifiedBytesWithoutChangingItsOlderGeneration() {
        Create(ChatPlatform.QQ); File.WriteAllText(Path.Combine(Account, "resource.txt"), "old"); var first = Backup("raw");
        File.WriteAllText(Path.Combine(Account, "resource.txt"), "new value");
        var next = ChatIncrementalBackup.Create(Account, Path.Combine(root, "next"), first.Manifest, Path.Combine(root, "journals"));
        Assert.Equal(0, next.ReusedFiles); Assert.Equal("old", File.ReadAllText(Path.Combine(first.Destination, "resource.txt")));
        Assert.Equal("new value", File.ReadAllText(Path.Combine(next.Backup.Destination, "resource.txt")));
        Assert.Null(JsonFiles.Read<TransferJournal>(next.Backup.Manifest).Receipts.Single(r => r.RelativePath == "resource.txt").StorageRoot);
    }
    [Fact]
    public void CancellationAndForeignAccountLeaveThePublishedReadingCopyIntact() {
        Create(ChatPlatform.QQ); var first = Backup("raw"); string reading = Path.Combine(root, "reading");
        new ChatExporter().ExportVerifiedBackup(first.Manifest, reading, ChatPlatform.QQ);
        byte[] before = File.ReadAllBytes(Path.Combine(reading, "messages.jsonl"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ChatReadingUpdate.Update(first.Manifest, reading, ChatPlatform.QQ, cancellation: cancelled.Token));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(reading, "messages.jsonl")));
        Assert.Throws<InvalidDataException>(() => ChatReadingUpdate.Update(first.Manifest, reading, ChatPlatform.WeChat));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(reading, "messages.jsonl")));
        Assert.Empty(Directory.GetDirectories(reading, ".reading-update-*"));
    }
    [Fact]
    public void LockedPublicationRollsBackTheTranscriptAndChangedConversationPages() {
        Create(ChatPlatform.QQ);var first=Backup("raw");string reading=Path.Combine(root,"reading");
        new ChatExporter().ExportVerifiedBackup(first.Manifest,reading,ChatPlatform.QQ);
        byte[] before=File.ReadAllBytes(Path.Combine(reading,"messages.jsonl"));
        var pages=Directory.GetFiles(Path.Combine(reading,"conversations")).ToDictionary(p=>Path.GetFileName(p)!,p=>File.ReadAllBytes(p));
        Sql(DatabasePath(ChatPlatform.QQ),"INSERT INTO c2c_msg_table VALUES(1,2,1700000002,'new',102);");var next=Backup("next");
        using(var held=new FileStream(Path.Combine(reading,"archive-index.js"),FileMode.Open,FileAccess.Read,FileShare.Read))
            Assert.ThrowsAny<IOException>(()=>ChatReadingUpdate.Update(next.Manifest,reading,ChatPlatform.QQ));
        Assert.Equal(before,File.ReadAllBytes(Path.Combine(reading,"messages.jsonl")));
        foreach(var page in pages)Assert.Equal(page.Value,File.ReadAllBytes(Path.Combine(reading,"conversations",page.Key!)));
        Assert.False(File.Exists(Path.Combine(reading,".reading-update-pending.json")));
        var retry=ChatReadingUpdate.Update(next.Manifest,reading,ChatPlatform.QQ);Assert.Equal(1,retry.Added);Assert.Equal(3,Messages(reading).Length);
    }
    [Fact]
    public void InterruptedPublicationRestoresOriginalFilesBeforeTryingAnotherUpdate() {
        Create(ChatPlatform.QQ); var first = Backup("raw"); string reading = Path.Combine(root, "reading");
        new ChatExporter().ExportVerifiedBackup(first.Manifest, reading, ChatPlatform.QQ);
        string index = Path.Combine(reading, "archive-index.js"); byte[] original = File.ReadAllBytes(index);
        string work = ".reading-update-" + Guid.NewGuid().ToString("N"), saved = Path.Combine(reading, work, "rollback"); Directory.CreateDirectory(saved);
        File.Move(index, Path.Combine(saved, "archive-index.js")); File.WriteAllText(index, "interrupted");
        JsonFiles.Write(Path.Combine(reading, ".reading-update-pending.json"), new { work, files = new Dictionary<string, bool> { ["archive-index.js"] = true } });
        Assert.Throws<InvalidDataException>(() => ChatReadingUpdate.Update(first.Manifest, reading, ChatPlatform.WeChat));
        Assert.Equal(original, File.ReadAllBytes(index)); Assert.False(File.Exists(Path.Combine(reading, ".reading-update-pending.json")));
    }
    private static void Sql(string path, string sql) {
        Assert.Equal(0, sqlite3_open_v2(path, out nint db, 6, 0));
        try { Assert.Equal(0, sqlite3_exec(db, sql, 0, 0, 0)); } finally { sqlite3_close_v2(db); }
    }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint state, nint error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(nint db);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
