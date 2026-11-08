using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;
using Storloom.Core.Localization;
using Xunit;

namespace Storloom.Tests;

public sealed class StorageAndChatTests : IDisposable {
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
    public StorageAndChatTests() => Directory.CreateDirectory(root);
    private string Dir(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private string FileIn(string directory, string name, string content = "fixture") {
        string path = Path.Combine(directory, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content); return path;
    }

    [Fact]
    public void AnyFolderAnalysisAccumulatesNestedSizesAndRetainsEmptyFolders() {
        string source = Dir("source"); FileIn(source, "a", "abc"); FileIn(source, "sub/b", "12345");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        FileIn(source, "sub/deep/deeper/c", "1234567");
        var result = new StorageScanner().Scan(source);
        Assert.Equal(15, result.Bytes); Assert.Equal(3, result.Files);
        Assert.Equal(15, result.Directories.Single(e => e.Path == source).Bytes);
        Assert.Equal(12, result.Directories.Single(e => e.Path == Path.Combine(source, "sub")).Bytes);
        Assert.Contains(result.Directories, e => e.Path.EndsWith("empty") && e.Bytes == 0);
    }

    [Fact]
    public void QqDiscoveryAndBackupExportSeparateGuildFromMainChat() {
        string source = Dir("account");
        string guild = FileIn(source, "QQGuild/nt_db/nt_msg.db", "separately encrypted guild fixture");
        string main = Path.GetFullPath(Path.Combine(Dir("account/nt_qq/nt_db"), "nt_msg.db"));
        CreateDatabase(main, "CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'Fixture');");
        Assert.Equal(main, Assert.Single(ChatExporter.FindDatabases(source, ChatPlatform.QQ)));
        var location = Assert.Single(ChatDiscovery.Discover([source]));
        Assert.Equal(source, location.Directory); Assert.Equal(main, location.Database);
        Assert.Equal(2, location.AccountFiles);
        var service = new TransferService();
        var backup = service.Execute(service.Plan(source, Path.Combine(root, "raw")), Dir("journals"));
        Assert.Equal(Path.Combine(backup.Destination, "nt_qq", "nt_db", "nt_msg.db"), Assert.Single(ChatExporter.FindDatabases(backup.Destination, ChatPlatform.QQ)));
        var result = new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "reading"), ChatPlatform.QQ);
        Assert.Equal(1, result.Messages); Assert.Equal(1, result.Databases); Assert.False(result.Complete);
        Assert.Contains(result.Issues, issue => issue.Path == Path.GetRelativePath(source, guild) && issue.Reason.Contains("separate database key"));
        Assert.True(File.Exists(guild)); Assert.True(File.Exists(Path.Combine(backup.Destination, Path.GetRelativePath(source, guild))));
    }

    [Fact]
    public void DiscoveryPreservesMediaAlongsideMessageDirectories() {
        string selected = Dir("selected-account");
        FileIn(selected, "db_storage/message/message_0.db"); FileIn(selected, "media/fixture.txt");
        var location = Assert.Single(ChatDiscovery.Discover([selected]).Where(l => PathPolicy.Under(l.Database, selected)));
        Assert.Equal(selected, location.Directory);
        Assert.Equal(2, location.AccountFiles); Assert.True(location.SizeComplete);
        Assert.Equal(new FileInfo(location.Database).Length + new FileInfo(Path.Combine(selected, "media/fixture.txt")).Length, location.AccountBytes);
        var plan = new TransferService().Plan(location.Directory, Path.Combine(root, "backup"));
        Assert.Contains(plan.Files, f => f.RelativePath.EndsWith("fixture.txt"));
    }

    [Fact]
    public void DiscoveryResolvesTheSelectedMappedRootButDoesNotTraverseNestedLinks() {
        string physical=Dir("physical"), account=Dir("physical/account"), outside=Dir("outside");
        FileIn(account,"db_storage/message/message_0.db");FileIn(outside,"nt_msg.db");
        string mapped=Path.Combine(root,"mapped"), nested=Path.Combine(account,"nested-link");
        void Link(string link,string target) {
            using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe") {
                UseShellExecute=false,CreateNoWindow=true,ArgumentList={"-NoProfile","-Command",$"New-Item -ItemType Junction -Path '{link.Replace("'","''")}' -Target '{target.Replace("'","''")}' | Out-Null"}})!;
            Assert.True(process.WaitForExit(10000));Assert.Equal(0,process.ExitCode);
        }
        Link(mapped,physical);Link(nested,outside);
        try {
            var location=Assert.Single(ChatDiscovery.Discover([Path.Combine(mapped,"account")]));
            Assert.Equal(PathPolicy.Normalize(account),location.Directory);Assert.Equal(ChatPlatform.WeChat,location.Platform);Assert.Equal(1,location.AccountFiles);
            Assert.False(location.SizeComplete);
        } finally {Directory.Delete(nested);Directory.Delete(mapped);}
    }
    [Fact]
    public void UnsupportedMessageSchemaIsReportedIncomplete() {
        string source = Dir("account");
        CreateDatabase(Path.Combine(source, "nt_msg.db"), "CREATE TABLE c2c_msg_table(unknown TEXT);");
        var service = new TransferService(); var backup = service.Execute(service.Plan(source,
            Path.Combine(root, "backup")), Dir("journals"));
        var result = new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "export"), ChatPlatform.QQ);
        Assert.False(result.Complete); Assert.NotEmpty(result.Issues);
        Assert.True(File.Exists(Path.Combine(result.Directory, "export-report.json")));
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public void TransferVerifiesContentAndOnlyMoveRemovesOriginals(TransferMode mode) {
        string source = Dir("source"), target = Path.Combine(root, "archive");
        string first = FileIn(source, "sub/a[1].txt"), second = FileIn(source, "b.txt");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var service = new TransferService(); var plan = service.Plan(source, target, mode);
        var result = service.Execute(plan, Dir("journals"));
        Assert.Equal(2, result.VerifiedFiles); Assert.Empty(result.Issues);
        Assert.Equal(mode == TransferMode.Move ? 2 : 0, result.RemovedFiles);
        Assert.Equal(mode == TransferMode.Move ? 14 : 0, result.RemovedBytes);
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(target, "sub/a[1].txt")));
        Assert.Equal(mode == TransferMode.Copy, File.Exists(first));
        Assert.Equal(mode == TransferMode.Copy, File.Exists(second));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        var journal = JsonFiles.Read<TransferJournal>(result.Manifest);
        Assert.Equal("Completed", journal.State); Assert.Equal(2, journal.Receipts.Count);
    }

    [Fact]
    public void TransferRejectsOverlapCollisionTamperingAndChangedSource() {
        string source = Dir("source"), target = Path.Combine(root, "target");
        string file = FileIn(source, "a"); var service = new TransferService();
        Assert.Throws<InvalidDataException>(() => service.Plan(source, Path.Combine(source, "nested")));
        var plan = service.Plan(source, target, TransferMode.Move);
        File.AppendAllText(file, "changed");
        Assert.Throws<IOException>(() => service.Execute(plan, Dir("journals")));
        Assert.True(File.Exists(file)); Assert.False(Directory.Exists(target));
        plan = service.Plan(source, target);
        Assert.Throws<InvalidDataException>(() => service.Execute(plan with {
            Files = [new("..\\escape", plan.Files[0].Stamp)] }, Dir("journals")));
        Directory.CreateDirectory(target);
        Assert.Throws<IOException>(() => service.Execute(plan, Dir("journals")));
    }

    [Fact]
    public void CancelledCopyKeepsAllSourcesAndCannotBeUsedAsVerifiedBackup() {
        string source = Dir("source"), target = Path.Combine(root, "target"); FileIn(source, "a");
        var service = new TransferService(); var plan = service.Plan(source, target, TransferMode.Move);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = service.Execute(plan, Dir("journals"), cancellation: cancel.Token);
        Assert.True(result.Cancelled); Assert.True(File.Exists(Path.Combine(source, "a")));
        Assert.False(Directory.Exists(target));
        Assert.Throws<InvalidDataException>(() => new ChatExporter().ExportVerifiedBackup(
            result.Manifest, Path.Combine(root, "export"), ChatPlatform.QQ));
    }

    [Fact]
    public void AiTransferRulesMatchAndCannotUseTheDeletionExecutor() {
        string source = Dir("source"); FileIn(source, "a.log"); FileIn(source, "keep.txt");
        var catalog = new RuleCatalog { Rules = [new() { Id = "archive-logs", Name = "Archive",
            Action = CleanupAction.Copy, Roots = [source], DestinationRoot = Path.Combine(root, "archive"),
            Include = ["**/*.log"], MinimumAgeHours = 0 }] };
        var engine = new CleanupEngine(catalog); var candidates = engine.Scan();
        Assert.Single(candidates.Files);
        Assert.Throws<InvalidDataException>(() => engine.Apply(candidates, ["archive-logs"], Dir("journals")));
        var plan = Assert.Single(engine.CreateTransferPlans(candidates, ["archive-logs"]));
        Assert.Single(plan.Files); Assert.Equal("a.log", plan.Files[0].RelativePath);
        Assert.Equal(TransferMode.Copy, plan.Mode);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void QqCandidateAllowsNonzeroHeapMetadataButRejectsSubstrings(int length) {
        byte[] bytes = [0xff, .. Encoding.ASCII.GetBytes(new string('K', length)), 0];
        Assert.Equal(length, PassiveKeyReader.QqCandidateLength(bytes, 1));
        Assert.Equal(0, PassiveKeyReader.QqCandidateLength(bytes, 2));
        bytes[0] = (byte)'x'; Assert.Equal(0, PassiveKeyReader.QqCandidateLength(bytes, 1));
        bytes[0] = 0; bytes[^1] = 1; Assert.Equal(0, PassiveKeyReader.QqCandidateLength(bytes, 1));
    }

    [Fact]
    public void IncorrectKeyFailsBeforeHashingAttachmentsOrCreatingAnExport() {
        string source = Dir("encrypted");
        var parameters = new CipherParameters(1024, 48, HashAlgorithmName.SHA512, HashAlgorithmName.SHA1, 4000, false);
        byte[] secret = Encoding.ASCII.GetBytes("fixture-key-1234!");
        byte[] salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] page = EncryptPage(new byte[4096], secret, salt, 1, parameters);
        File.WriteAllBytes(Path.Combine(source, "nt_msg.db"), new byte[1024].Concat(page).ToArray());
        FileIn(source, "attachment.txt", "original");
        var service = new TransferService(); var backup = service.Execute(service.Plan(source, Path.Combine(root, "raw")), Dir("journals"));
        File.WriteAllText(Path.Combine(backup.Destination, "attachment.txt"), "modified");
        var progress = new InlineProgress(_ => throw new Exception("Hashing must not start"));
        Assert.Throws<CryptographicException>(() => new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "reading"), ChatPlatform.QQ, Encoding.ASCII.GetBytes("wrong-key-123456!"), progress));
        Assert.False(Directory.Exists(Path.Combine(root, "reading")));
    }

    [Fact]
    public void OptionalRecheckStillChecksStructureAndRecordsItsChoiceAndCanCancel() {
        string source = Dir("account");
        CreateDatabase(Path.Combine(source, "nt_msg.db"), "CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'Fixture');");
        FileIn(source, "unused.txt", "original");
        var service = new TransferService(); var backup = service.Execute(service.Plan(source, Path.Combine(root, "raw")), Dir("journals"));
        File.WriteAllText(Path.Combine(backup.Destination, "unused.txt"), "modified");
        Assert.Throws<InvalidDataException>(() => new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "strict"), ChatPlatform.QQ));
        var result = new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "fast"), ChatPlatform.QQ, verifyBackup: false);
        Assert.Equal(1, result.Messages); Assert.False(result.BackupReverified); Assert.Equal(0, result.VerifiedBackupBytes);
        var report = JsonFiles.Read<ChatExportResult>(Path.Combine(result.Directory, "export-report.json"));
        Assert.False(report.BackupReverified); Assert.Equal(0, report.VerifiedBackupBytes);
        File.WriteAllText(Path.Combine(backup.Destination, "unused.txt"), "original");
        var strict = new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "strict"), ChatPlatform.QQ);
        Assert.True(strict.BackupReverified); Assert.True(strict.VerifiedBackupBytes > 0);
        Assert.True(JsonFiles.Read<ChatExportResult>(Path.Combine(strict.Directory, "export-report.json")).BackupReverified);
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "cancelled"), ChatPlatform.QQ,
            progress: new InlineProgress(_ => cancellation.Cancel()), cancellation: cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(root, "cancelled")));
        File.AppendAllText(Path.Combine(backup.Destination, "unused.txt"), "longer");
        Assert.Throws<InvalidDataException>(() => new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "bad-size"), ChatPlatform.QQ, verifyBackup: false));
    }
    private sealed class InlineProgress(Action<ScanProgress> action) : IProgress<ScanProgress> { public void Report(ScanProgress value) => action(value); }

    [Theory]
    [InlineData(ChatPlatform.WeChat, true, 0, 80)]
    [InlineData(ChatPlatform.WeChat, false, 0, 80)]
    [InlineData(ChatPlatform.QQ, false, 1024, 48)]
    public void CipherAuthenticatesAndDecryptsEveryPage(ChatPlatform platform, bool raw, int wrapper, int reserve) {
        byte[] secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var parameters = new CipherParameters(wrapper, reserve, HashAlgorithmName.SHA512,
            platform == ChatPlatform.QQ ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA512,
            platform == ChatPlatform.QQ ? 4000 : 256000, raw);
        byte[] salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] plain = new byte[4096]; "SQLite format 3\0"u8.CopyTo(plain);
        for (int i = 16; i < 4096 - reserve; i++) plain[i] = (byte)(i % 251);
        byte[] page = EncryptPage(plain, secret, salt, 1, parameters);
        byte[] prefix = new byte[wrapper + page.Length]; page.CopyTo(prefix, wrapper);
        Assert.NotNull(ChatCipher.Detect(prefix, secret, platform));
        byte[] key = ChatCipher.Derive(secret, salt, parameters);
        Assert.Equal(plain[..(4096 - reserve)], ChatCipher.DecryptPage(page, key, salt, 1, parameters)[..(4096 - reserve)]);
        page[100] ^= 1;
        Assert.Throws<CryptographicException>(() => ChatCipher.DecryptPage(page, key, salt, 1, parameters));
    }

    [Fact]
    public void OfflineQqExportPreservesIdenticalMessagesAndEscapesHtml() {
        string source = Dir("account");
        string database = Path.Combine(source, "nt_msg.db");
        string script = "CREATE TABLE c2c_msg_table(\"40030\" INTEGER,\"40033\" INTEGER,\"40050\" INTEGER,\"40800\" TEXT);" +
            "INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'<script>alert(1)</script>');" +
            "INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'<script>alert(1)</script>');";
        CreateDatabase(database, script);
        var service = new TransferService(); var backup = service.Execute(service.Plan(source,
            Path.Combine(root, "backup")), Dir("journals"));
        var result = new ChatExporter().ExportVerifiedBackup(backup.Manifest,
            Path.Combine(root, "export"), ChatPlatform.QQ);
        Assert.Equal(2, result.Messages); Assert.False(result.Complete); Assert.Equal(2, result.UnsupportedMessages);
        string html = File.ReadAllText(Path.Combine(result.Directory, "messages.html"));
        Assert.DoesNotContain("<script>alert", html);
        string page = File.ReadAllText(Directory.GetFiles(Path.Combine(result.Directory, "conversations"), "*-?.js").Single());
        Assert.DoesNotContain("<script>", page); Assert.Contains("\\u003Cscript\\u003E", page);
        string reader = File.ReadAllText(Path.Combine(result.Directory, "reader.js"));
        Assert.Contains("node.textContent = text", reader); Assert.DoesNotContain("innerHTML", reader);
        Assert.Equal(2, File.ReadLines(Path.Combine(result.Directory, "messages.jsonl")).Count());
        Assert.True(File.Exists(database));
    }

    [Fact]
    public void ChangedBackupAndWrongKeysFailWithoutRemovingSource() {
        string source = Dir("account"); string database = Path.Combine(source, "nt_msg.db");
        CreateDatabase(database, "CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");");
        var service = new TransferService(); var backup = service.Execute(service.Plan(source,
            Path.Combine(root, "backup")), Dir("journals"));
        File.AppendAllText(Path.Combine(backup.Destination, "nt_msg.db"), "changed");
        Assert.Throws<InvalidDataException>(() => new ChatExporter().ExportVerifiedBackup(
            backup.Manifest, Path.Combine(root, "export"), ChatPlatform.QQ));
        Assert.True(File.Exists(database));
    }

    [Fact]
    public void VerifiedExportRejectsDatabasesAddedAfterTheBackup() {
        string source = Dir("account");
        CreateDatabase(Path.Combine(source,"nt_msg.db"), "CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");");
        var service = new TransferService(); var backup = service.Execute(service.Plan(source,
            Path.Combine(root,"backup")),Dir("journals"));
        string extra = Path.Combine(backup.Destination,"added"); Directory.CreateDirectory(extra);
        File.Copy(Path.Combine(source,"nt_msg.db"),Path.Combine(extra,"nt_msg.db"));
        Assert.Throws<InvalidDataException>(() => new ChatExporter().ExportVerifiedBackup(backup.Manifest,
            Path.Combine(root,"export"),ChatPlatform.QQ));
        Assert.False(Directory.Exists(Path.Combine(root,"export")));
        Assert.True(File.Exists(Path.Combine(source,"nt_msg.db")));
    }

    [Fact]
    public void EncryptedWechatDatabaseExportsWithoutPython() {
        string source = Dir("account"); string plain = Path.Combine(root, "plain.db");
        CreateDatabase(plain, "CREATE TABLE Name2Id(user_name TEXT);INSERT INTO Name2Id VALUES('fixture-sender');" +
            "CREATE TABLE Msg_fixture(local_type INTEGER,create_time INTEGER,real_sender_id INTEGER,message_content TEXT);" +
            "INSERT INTO Msg_fixture VALUES(1,1700000000,1,'fixture text');", 80);
        byte[] secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var parameters = new CipherParameters(0, 80, HashAlgorithmName.SHA512, HashAlgorithmName.SHA512, 256000, false);
        string encrypted = Path.Combine(source, "message_0.db");
        using (var output = File.Create(encrypted)) {
            byte[] data = File.ReadAllBytes(plain);
            Assert.Equal(80, data[20]);
            for (int i = 0; i < data.Length; i += 4096)
                output.Write(EncryptPage(data.AsSpan(i, 4096).ToArray(), secret, salt, (uint)(i / 4096 + 1), parameters));
        }
        var service = new TransferService(); var backup = service.Execute(service.Plan(source,
            Path.Combine(root, "backup")), Dir("journals"));
        var result = new ChatExporter().ExportVerifiedBackup(backup.Manifest, Path.Combine(root, "export"),
            ChatPlatform.WeChat, secret);
        Assert.True(result.Complete, string.Join(";", result.Issues.Select(i => i.Reason)));
        Assert.Equal(1, result.Messages);
        Assert.Contains("fixture text", File.ReadAllText(Path.Combine(result.Directory, "messages.jsonl")));
    }

    [Fact]
    public void ProtoUnknownPayloadsRemainExplicitRatherThanSilentlyDisappearing() {
        Assert.Contains("unparsed", ChatExporter.DecodeProto([0xff, 0x01]));
        ulong tag = ((ulong)45101 << 3) | 2;
        var bytes = new List<byte>();
        while (tag > 127) { bytes.Add((byte)((tag & 127) | 128)); tag >>= 7; }
        bytes.Add((byte)tag); bytes.Add(4); bytes.AddRange("text"u8.ToArray());
        Assert.Equal("text", ChatExporter.DecodeProto(bytes.ToArray()));
    }

    [Fact]
    public void EnglishAndChineseResourcesHaveMatchingKeysAndLocalizedRules() {
        var assembly = typeof(L).Assembly;
        Dictionary<string, string> Load(string locale) {
            using var stream = assembly.GetManifestResourceStream($"Storloom.Core.Localization.Locales.{locale}.json")!;
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        var english = Load("en"); var chinese = Load("zh-CN");
        Assert.Equal(english.Keys.Order(), chinese.Keys.Order());
        foreach (string key in english.Keys) {
            Assert.False(string.IsNullOrWhiteSpace(chinese[key]));
            string[] Fields(string value) => System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+(?:[^}]*)\}")
                .Select(m => m.Value).Order().ToArray();
            Assert.Equal(Fields(english[key]), Fields(chinese[key]));
        }
        L.SetLanguage("en"); Assert.Equal("Storage overview", L.T("Overview"));
        L.SetLanguage("zh-CN"); Assert.Equal("空间概览", L.T("Overview"));
    }

    [Fact]
    public void CancelledExportRetainsResumeProgressButRemovesDecryptedDatabases() {
        string source = Dir("account");
        CreateDatabase(Path.Combine(source, "nt_msg.db"),
            "CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");" +
            "WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<501) " +
            "INSERT INTO c2c_msg_table SELECT 1,2,x,'fixture' FROM seq;");
        var service = new TransferService(); var backup = service.Execute(service.Plan(source,
            Path.Combine(root, "backup")), Dir("journals"));
        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress(p => { if (p.RuleId == "chat-export") cancel.Cancel(); });
        string export = Path.Combine(root, "export");
        var result = new ChatExporter().ExportVerifiedBackup(backup.Manifest, export, ChatPlatform.QQ,
            progress: progress, cancellation: cancel.Token);
        Assert.False(result.Complete); Assert.Equal(0, result.Messages); Assert.True(result.CanResume);
        Assert.NotEmpty(result.Issues); Assert.True(File.Exists(Path.Combine(export, "export-report.json")));
        Assert.Empty(Directory.GetFiles(export, "*.db*", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(export, "export-progress.json")));
        Assert.True(File.Exists(Path.Combine(source, "nt_msg.db")));
    }

    [Fact]
    public void ResumeReusesParsedDatabasesAndDoesNotDuplicateMessages() {
        string source=Dir("account");
        CreateDatabase(Path.Combine(source,"nt_msg.db"),"CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<901) INSERT INTO c2c_msg_table SELECT x%32,2,x,'fixture' FROM seq;");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(root,"raw")),Dir("journals"));
        using var cancel=new CancellationTokenSource();string target=Path.Combine(root,"reading");
        var first=new ChatExporter().ExportVerifiedBackup(backup.Manifest,target,ChatPlatform.QQ,
            progress:new ImmediateProgress(p=>{if(p.RuleId=="chat-reader"&&p.Files>=300)cancel.Cancel();}),cancellation:cancel.Token);
        Assert.True(first.CanResume);Assert.Equal(300,first.Messages);
        var stages=new List<string>();
        var resumed=new ChatExporter().ExportVerifiedBackup(backup.Manifest,target,ChatPlatform.QQ,
            progress:new ImmediateProgress(p=>stages.Add(p.RuleId)),verifyBackup:false,resume:true);
        Assert.True(resumed.Complete);Assert.False(resumed.CanResume);Assert.Equal(901,resumed.Messages);
        Assert.DoesNotContain("chat-decrypt",stages);Assert.DoesNotContain("chat-export",stages);
        var records=File.ReadLines(Path.Combine(target,"messages.jsonl")).Select(line=>System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(line)!).ToArray();
        Assert.Equal(901,records.Select(m=>m.Id).Distinct().Count());Assert.Equal(901,records.Select(m=>m.Timestamp).Distinct().Count());
        Assert.Empty(Directory.GetDirectories(target,".work-*"));
    }

    [Fact]
    public void ResumeRejectsChangedParsedCheckpointFilesWithoutOverwritingThem() {
        string source=Dir("account");CreateDatabase(Path.Combine(source,"nt_msg.db"),"CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");INSERT INTO c2c_msg_table VALUES(1,2,1,'fixture');");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(root,"raw")),Dir("journals"));
        using var cancel=new CancellationTokenSource();string target=Path.Combine(root,"reading");
        new ChatExporter().ExportVerifiedBackup(backup.Manifest,target,ChatPlatform.QQ,
            progress:new ImmediateProgress(p=>{if(p.RuleId=="chat-reader")cancel.Cancel();}),cancellation:cancel.Token);
        string parsed=Directory.GetFiles(target,"0.jsonl",SearchOption.AllDirectories).Single();File.AppendAllText(parsed,"modified");
        string before=File.ReadAllText(parsed);
        Assert.Throws<InvalidDataException>(()=>new ChatExporter().ExportVerifiedBackup(backup.Manifest,target,ChatPlatform.QQ,verifyBackup:false,resume:true));
        Assert.Equal(before,File.ReadAllText(parsed));
    }

    [Fact]
    public void ResumeRefusesAnOldExportFolderWithoutCreatingFiles() {
        string source=Dir("account");CreateDatabase(Path.Combine(source,"nt_msg.db"),"CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(root,"raw")),Dir("journals"));
        string old=Dir("old-export");FileIn(old,"messages.jsonl","synthetic old export");
        Assert.Throws<InvalidDataException>(()=>new ChatExporter().ExportVerifiedBackup(backup.Manifest,old,ChatPlatform.QQ,verifyBackup:false,resume:true));
        Assert.Single(Directory.GetFiles(old));Assert.Equal("synthetic old export",File.ReadAllText(Path.Combine(old,"messages.jsonl")));
    }

    [Fact]
    public void ReaderCheckpointsRollbackUncommittedTailsAndKeepFinishedPages() {
        string source=Dir("source"),target=Dir("reading"),work=Dir("reading/work");
        ChatMessage Message(int i)=>new("QQ","group:"+(i%32),"fixture",i,1,"fixture message");
        using(var writer=new ChatArchiveWriter(source,target,work,new(),null,default,"fixture",resumable:true)) {
            for(int i=0;i<200;i++)writer.Add(Message(i));writer.SaveProgress();
            for(int i=200;i<300;i++)writer.Add(Message(i));
        }
        using var cancel=new CancellationTokenSource();
        using(var writer=new ChatArchiveWriter(source,target,work,new(),null,cancel.Token,"fixture",resumable:true)) {
            Assert.Equal(200,writer.ProcessedMessages);
            for(int i=200;i<500;i++)writer.Add(Message(i));
            Assert.Throws<OperationCanceledException>(()=>writer.Finish(new ImmediateProgress(p=>{if(p.RuleId=="chat-pages")cancel.Cancel();})));
            Assert.True(writer.SpoolWrites<64);
        }
        string page=Directory.GetFiles(Path.Combine(target,"conversations"),"*-0.js").Single();DateTime modified=File.GetLastWriteTimeUtc(page);
        using(var writer=new ChatArchiveWriter(source,target,work,new(),null,default,"fixture",resumable:true)) {
            Assert.Equal(500,writer.ProcessedMessages);writer.Finish(null);
        }
        Assert.Equal(modified,File.GetLastWriteTimeUtc(page));
        var records=File.ReadLines(Path.Combine(target,"messages.jsonl")).Select(line=>System.Text.Json.JsonSerializer.Deserialize<ChatMessage>(line)!).ToArray();
        Assert.Equal(500,records.Length);Assert.Equal(500,records.Select(m=>m.Id).Distinct().Count());
        Assert.Equal(32,Directory.GetFiles(Path.Combine(target,"conversations"),"*-0.js").Length);
    }

    [Theory]
    [InlineData(300,true)]
    [InlineData(4501,true)]
    [InlineData(300,false)]
    [InlineData(4501,false)]
    public void ReaderPaginationPreservesStableTimestampOrderForOrderedAndShuffledConversations(int count,bool ordered) {
        string source=Dir("source"),target=Dir("reading"),work=Dir("reading/work");
        var expected=Enumerable.Range(0,count).Select(i=>(Time:(long)(ordered?i/2:(count-i)/2),Id:"m"+(i+1))).ToArray();
        using(var writer=new ChatArchiveWriter(source,target,work,new(),null,default,"fixture",resumable:true)) {
            foreach(var item in expected)writer.Add(new("QQ","group:fixture","fixture",item.Time,1,"fixture"));
            writer.Finish(null);
        }
        var actual=Directory.GetFiles(Path.Combine(target,"conversations"),"*.js").Where(p=>!p.EndsWith("-refs.js"))
            .OrderBy(p=>int.Parse(Path.GetFileNameWithoutExtension(p).Split('-')[1])).SelectMany(path=>{
                string text=File.ReadAllText(path);return System.Text.Json.JsonSerializer.Deserialize<ChatMessage[]>(text[(text.IndexOf(",[",StringComparison.Ordinal)+1)..^2])!;
            }).Select(m=>(m.Timestamp,m.Id)).ToArray();
        Assert.Equal(expected.OrderBy(m=>m.Time).Select(m=>(m.Time,(string?)m.Id)),actual);
    }

    [Fact]
    public void ReaderResumesCheckpointsWithoutTheNewOrderingMetadata() {
        string source=Dir("source"),target=Dir("reading"),work=Dir("reading/work");
        using(var writer=new ChatArchiveWriter(source,target,work,new(),null,default,"fixture",resumable:true)) {
            writer.Add(new("QQ","group:fixture","fixture",20,1,"later"));writer.Add(new("QQ","group:fixture","fixture",10,1,"earlier"));writer.SaveProgress();
        }
        string checkpoint=Path.Combine(work,"reader-progress.json");var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(checkpoint))!;
        foreach(var item in node["conversations"]!.AsArray()){item!.AsObject().Remove("ordered");item.AsObject().Remove("lastTimestamp");}
        File.WriteAllText(checkpoint,node.ToJsonString());
        using(var writer=new ChatArchiveWriter(source,target,work,new(),null,default,"fixture",resumable:true))writer.Finish(null);
        string page=File.ReadAllText(Directory.GetFiles(Path.Combine(target,"conversations"),"*-0.js").Single());
        Assert.True(page.IndexOf("earlier",StringComparison.Ordinal)<page.IndexOf("later",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedEncryptedWalIsAppliedAndCorruptionIsRefused(bool corrupt) {
        byte[] secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var parameters = new CipherParameters(0, 80, HashAlgorithmName.SHA512, HashAlgorithmName.SHA512, 256000, true);
        byte[] original = new byte[4096]; "SQLite format 3\0"u8.CopyTo(original);
        byte[] latest = original.ToArray(); latest[100] = 42;
        string source = Path.Combine(root, "message_0.db"), target = Path.Combine(root, "plain.db");
        File.WriteAllBytes(source, EncryptPage(original, secret, salt, 1, parameters));
        byte[] wal = new byte[32 + 24 + 4096];
        void Big(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(wal.AsSpan(offset), value);
        Big(0, 0x377f0682); Big(4, 3007000); Big(8, 4096); Big(16, 1); Big(20, 2);
        (uint A, uint B) Sum(ReadOnlySpan<byte> bytes, (uint A, uint B) seed) {
            var (a, b) = seed;
            for (int i = 0; i < bytes.Length; i += 8) {
                a = unchecked(a + BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]) + b);
                b = unchecked(b + BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i + 4)..]) + a);
            }
            return (a, b);
        }
        var check = Sum(wal.AsSpan(0, 24), (0, 0)); Big(24, check.A); Big(28, check.B);
        Big(32, 1); Big(36, 1); Big(40, 1); Big(44, 2);
        EncryptPage(latest, secret, salt, 1, parameters).CopyTo(wal, 56);
        check = Sum(wal.AsSpan(32, 8), check); check = Sum(wal.AsSpan(56), check);
        Big(48, check.A); Big(52, check.B);
        if (corrupt) wal[100] ^= 1;
        File.WriteAllBytes(source + "-wal", wal);
        if (corrupt) {
            Assert.Throws<InvalidDataException>(() => ChatCipher.DecryptDatabase(source, target, secret, ChatPlatform.WeChat));
            Assert.False(File.Exists(target));
        } else {
            ChatCipher.DecryptDatabase(source, target, secret, ChatPlatform.WeChat);
            Assert.Equal(42, File.ReadAllBytes(target)[100]);
        }
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void QqPreallocatedZeroTailUsesAuthenticatedSqliteSizeAndExportsMessages() {
        byte[] secret=Encoding.ASCII.GetBytes("fixture-key-1234!"),salt=Enumerable.Range(1,16).Select(i=>(byte)i).ToArray();
        var parameters=new CipherParameters(1024,48,HashAlgorithmName.SHA1,HashAlgorithmName.SHA1,4000,false);
        string plain=Path.Combine(root,"plain.db");
        CreateDatabase(plain,"CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'Fixture');",48);
        byte[] data=File.ReadAllBytes(plain);
        string account=Dir("account"),encrypted=Path.Combine(account,"nt_msg.db");
        using(var output=File.Create(encrypted)) {
            output.Write(new byte[1024]);
            for(int offset=0;offset<data.Length;offset+=4096)output.Write(EncryptPage(data.AsSpan(offset,4096).ToArray(),secret,salt,(uint)(offset/4096+1),parameters));
            output.Write(new byte[4096*4]);
        }
        var service=new TransferService();var backup=service.Execute(service.Plan(account,Path.Combine(root,"raw")),Dir("journals"));
        var result=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(root,"reading"),ChatPlatform.QQ,secret);
        Assert.True(result.Complete);Assert.Equal(1,result.Databases);Assert.Equal(1,result.Messages);
        Assert.Equal(1024+data.Length+4096*4,new FileInfo(encrypted).Length);
    }

    [Theory]
    [InlineData("active-zero")]
    [InlineData("nonzero-tail")]
    [InlineData("stale-size")]
    [InlineData("missing-size")]
    [InlineData("truncated")]
    [InlineData("unwrapped")]
    [InlineData("cancel")]
    public void QqZeroTailSupportNeverBypassesActivePageAuthentication(string damage) {
        byte[] secret=Encoding.ASCII.GetBytes("fixture-key-1234!"),salt=Enumerable.Range(1,16).Select(i=>(byte)i).ToArray();
        int wrapper=damage=="unwrapped"?0:1024;
        var parameters=new CipherParameters(wrapper,48,HashAlgorithmName.SHA1,HashAlgorithmName.SHA1,4000,false);
        byte[] first=new byte[4096];"SQLite format 3\0"u8.CopyTo(first);
        BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(16),4096);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(24),7);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(28),damage=="missing-size"?0u:damage=="truncated"?3u:damage=="active-zero"?2u:1u);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(92),damage=="stale-size"?6u:7u);
        string source=Path.Combine(root,"cipher.db"),target=Path.Combine(root,"decrypted.db");
        using(var output=File.Create(source)) {
            output.Write(new byte[wrapper]);output.Write(EncryptPage(first,secret,salt,1,parameters));
            byte[] tail=new byte[4096*(damage=="cancel"?300:1)];if(damage=="nonzero-tail")tail[42]=1;output.Write(tail);
        }
        using var cancellation=new CancellationTokenSource();
        var progress=new ImmediateProgress(_=>cancellation.Cancel());
        Action decrypt=()=>ChatCipher.DecryptDatabase(source,target,secret,ChatPlatform.QQ,cancellation.Token,progress);
        if(damage=="cancel")Assert.Throws<OperationCanceledException>(decrypt);
        else if(damage=="truncated")Assert.Throws<InvalidDataException>(decrypt);
        else Assert.Throws<CryptographicException>(decrypt);
        Assert.False(File.Exists(target));Assert.True(File.Exists(source));
    }

    [Fact]
    public void QqLockPageExceptionIsLimitedToTheExactWrappedZeroPage() {
        byte[] zero = new byte[4096];
        Assert.True(ChatCipher.IsQqLockPlaceholder(zero, 262144, 1024, ChatPlatform.QQ));
        Assert.False(ChatCipher.IsQqLockPlaceholder(zero, 262145, 1024, ChatPlatform.QQ));
        Assert.False(ChatCipher.IsQqLockPlaceholder(zero, 262144, 0, ChatPlatform.QQ));
        Assert.False(ChatCipher.IsQqLockPlaceholder(zero, 262144, 1024, ChatPlatform.WeChat));
        zero[0] = 1; Assert.False(ChatCipher.IsQqLockPlaceholder(zero, 262144, 1024, ChatPlatform.QQ));
    }

    [Fact]
    public void UnsignedActiveCaptureExecutableIsRejected() {
        string file = FileIn(root, "Weixin.exe");
        Assert.Throws<CryptographicException>(() => WeChatKeyCapture.ValidateTencentSignature(file));
    }

    private sealed class ImmediateProgress(Action<ScanProgress> action) : IProgress<ScanProgress> {
        public void Report(ScanProgress value) => action(value);
    }

    [Fact]
    public void TargetTamperingBeforeBatchVerificationPreventsPublicationAndSourceRemoval() {
        string source=Dir("source"), destination=Path.Combine(root,"archive");
        FileIn(source,"first.bin","first fixture"); FileIn(source,"second.bin","second fixture");
        var service=new TransferService();var plan=service.Plan(source,destination,TransferMode.Move);
        bool changed=false;
        var progress=new ImmediateProgress(p=>{
            if(!changed && p.RuleId=="transfer" && p.Files==2) {
                changed=true;
                File.AppendAllText(Path.Combine(destination+".storloom-"+plan.Id.ToString("N")+".partial","first.bin"),"tampered");
            }
        });
        Assert.Throws<IOException>(()=>service.Execute(plan,Dir("journals"),progress));
        Assert.True(changed);Assert.False(Directory.Exists(destination));Assert.True(File.Exists(Path.Combine(source,"first.bin")));
        Assert.True(File.Exists(Path.Combine(source,"second.bin")));
    }

    [Fact]
    public void ReusedDatabaseCipherContextPreservesAuthenticatedPagesAndRecordsThroughput() {
        const int pages = 4096;
        byte[] secret = Enumerable.Range(0,32).Select(i => (byte)i).ToArray(), salt = Enumerable.Range(1,16).Select(i => (byte)i).ToArray();
        var parameters = new CipherParameters(0,80,HashAlgorithmName.SHA512,HashAlgorithmName.SHA512,256000,true);
        string encrypted = Path.Combine(root,"cipher-fixture.db");
        using(var output = File.Create(encrypted)) for(uint number=1;number<=pages;number++) {
            byte[] plain = new byte[4096]; if(number==1) "SQLite format 3\0"u8.CopyTo(plain);
            output.Write(EncryptPage(plain,secret,salt,number,parameters));
        }
        var baseline = new List<double>(); var reused = new List<double>();
        for(int run=0;run<3;run++) {
            string first=Path.Combine(root,$"page-context-{run}.db"), second=Path.Combine(root,$"database-context-{run}.db");
            var clock=System.Diagnostics.Stopwatch.StartNew();
            using(var input=File.OpenRead(encrypted)) using(var output=File.Create(first)) {
                byte[] page=new byte[4096];
                for(uint number=1;number<=pages;number++){input.ReadExactly(page);output.Write(ChatCipher.DecryptPage(page,secret,salt,number,parameters));}
                output.Flush(true);
            }
            baseline.Add(clock.Elapsed.TotalMilliseconds); clock.Restart();
            ChatCipher.DecryptDatabase(encrypted,second,secret,ChatPlatform.WeChat); reused.Add(clock.Elapsed.TotalMilliseconds);
            using var original=File.OpenRead(first);using var optimized=File.OpenRead(second);
            Assert.Equal(SHA256.HashData(original),SHA256.HashData(optimized));
        }
        DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
        Assert.NotNull(repo);
        JsonFiles.Write(Path.Combine(repo!.FullName,"artifacts/performance/cipher-context.json"),new {
            fixtureMiB=16, pages, runs=3, perPageMedianMs=baseline.Order().ElementAt(1), reusedMedianMs=reused.Order().ElementAt(1),
            outputHashesEqual=true, scope="Synthetic authenticated pages with file I/O; not a real-account benchmark." });
    }

    private static byte[] EncryptPage(byte[] plain, byte[] secret, byte[] salt, uint number,
        CipherParameters parameters) {
        int start = number == 1 ? 16 : 0, end = 4096 - parameters.Reserve;
        byte[] key = parameters.RawKey ? secret.ToArray() :
            Rfc2898DeriveBytes.Pbkdf2(secret, salt, parameters.Iterations, parameters.Kdf, 32);
        byte[] page = new byte[4096], iv = Enumerable.Repeat((byte)7, 16).ToArray();
        using var aes = Aes.Create(); aes.Key = key;
        aes.EncryptCbc(plain.AsSpan(start, end - start), iv, page.AsSpan(start, end - start), PaddingMode.None);
        if (number == 1) salt.CopyTo(page, 0);
        iv.CopyTo(page, end);
        byte[] macSalt = salt.Select(b => (byte)(b ^ 0x3a)).ToArray();
        byte[] macKey = Rfc2898DeriveBytes.Pbkdf2(key, macSalt, 2, parameters.Kdf, 32);
        byte[] authenticated = new byte[end - start + 20];
        page.AsSpan(start, end - start + 16).CopyTo(authenticated);
        BinaryPrimitives.WriteUInt32LittleEndian(authenticated.AsSpan(authenticated.Length - 4), number);
        byte[] mac = parameters.Mac == HashAlgorithmName.SHA1 ? HMACSHA1.HashData(macKey, authenticated) :
            HMACSHA512.HashData(macKey, authenticated);
        mac.CopyTo(page, end + 16); return page;
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        nint callback, nint state, nint error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_file_control(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int operation, ref int value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(nint db);

    private static void CreateDatabase(string path, string sql, int reserve = 0) {
        Assert.Equal(0, sqlite3_open_v2(path, out nint db, 6, 0));
        try {
            if (reserve > 0) Assert.Equal(0, sqlite3_file_control(db, "main", 38, ref reserve));
            Assert.Equal(0, sqlite3_exec(db, "PRAGMA page_size=4096;" + sql, 0, 0, 0));
        } finally { sqlite3_close_v2(db); }
    }

    public void Dispose() {
        string fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures"));
        if (!PathPolicy.Under(Path.GetFullPath(root), fixtures)) throw new IOException("Invalid fixture boundary.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
