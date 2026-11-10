using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;

namespace Storloom.App;
public sealed partial class ShellWindow {
    private sealed record PreparedChatArchive(string Manifest, string Source, string Directory, ChatLocation Location, bool Encrypted);
    private PreparedChatArchive? preparedArchive;
    private ChatLocation Account() => activeChat ?? throw new InvalidOperationException(L.T("NoAccountSelected"));

    private async void OpenChatBackup() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Storloom backup manifest|*.json", Title = L.T("OpenChatBackup") };
        if (dialog.ShowDialog(this) != true) return;
        await Run(async token => {
        var selected = await Task.Run(() => {
        var manifest = JsonFiles.Read<TransferJournal>(dialog.FileName);
        string directory = PathPolicy.Normalize(manifest.Plan.Destination);
        if (directory.Length <= 3) throw new InvalidDataException("A backup must be a specific account directory.");
        PathPolicy.AssertNoLinks(directory);
        ChatPlatform platform = ChatPlatform.QQ;
        string? database = ChatExporter.FindDatabases(directory, platform).FirstOrDefault();
        if (database == null) { platform = ChatPlatform.WeChat; database = ChatExporter.FindDatabases(directory, platform).FirstOrDefault(); }
        if (database == null) throw new InvalidDataException("No supported message database found in this archive.");
        string source = PathPolicy.Normalize(manifest.Plan.Source);
        var account = new ChatLocation(platform, source, Path.GetFileName(source),
            TransferService.Child(source, Path.GetRelativePath(directory, database)), new FileInfo(database).Length);
        return (account, archive: ReadChatArchive(dialog.FileName, account, manifest));
        }, token);
        activeChat = selected.account; activeManifest = dialog.FileName; preparedArchive = selected.archive; SetCapturedKey(null);
        readingDirectory = exportReport = null; chatStep = 2; RenderChatStage(); Navigate("chats");
        });
    }

    private static PreparedChatArchive ReadChatArchive(string manifest, ChatLocation account, TransferJournal? journal = null) {
        journal ??= JsonFiles.Read<TransferJournal>(manifest);
        if (!journal.Plan.Source.Equals(account.Directory, StringComparison.OrdinalIgnoreCase) ||
            journal.State is not ("Verified" or "Completed" or "CopiedWithRetainedSources" or "CancelledAfterVerification") ||
            journal.Receipts.Count != journal.Plan.Files.Count)
            throw new InvalidDataException("The selected account has no matching verified raw backup.");
        if (PathPolicy.Normalize(journal.Plan.Destination).Length <= 3)
            throw new InvalidDataException("A backup must be a specific account directory.");
        string directory = PathPolicy.Normalize(journal.Plan.Destination);
        PathPolicy.AssertNoLinks(directory);
        string database = account.Platform == ChatPlatform.QQ ? ChatExporter.FindDatabases(directory, account.Platform).FirstOrDefault()
            ?? throw new InvalidDataException("No supported QQ message database found in this archive.")
            : TransferService.Child(directory, Path.GetRelativePath(account.Directory, account.Database));
        PathPolicy.AssertNoLinks(database);
        using var input = File.OpenRead(database); byte[] head = new byte[Math.Min(1040, input.Length)]; input.ReadExactly(head);
        bool encrypted = !head.AsSpan().StartsWith("SQLite format 3\0"u8) && !(account.Platform == ChatPlatform.QQ && head.Length >= 1040 && head.AsSpan(1024).StartsWith("SQLite format 3\0"u8));
        return new(manifest, account.Directory, directory, account with { Directory = directory, Database = database }, encrypted);
    }
    private PreparedChatArchive ChatArchive() {
        if (activeManifest == null) throw new InvalidOperationException(L.T("ScanFirst"));
        // UI entry points prepare this on a worker. Reuse the immutable view when rendering.
        if (preparedArchive?.Manifest != activeManifest || preparedArchive.Source != Account().Directory)
            preparedArchive = ReadChatArchive(activeManifest, Account());
        return preparedArchive;
    }
    private string ArchiveDirectory() => ChatArchive().Directory;
    private ChatLocation ArchiveAccount() => ChatArchive().Location;
    private bool NeedsKey() => ChatArchive().Encrypted;
    private async Task UseChatBackup(string manifest) => await Run(async token => {
        var account = Account(); var archive = await Task.Run(() => ReadChatArchive(manifest, account), token);
        activeManifest = manifest; preparedArchive = archive; chatStep = 2; RenderChatStage();
    });

    private async Task BackupChat() {
        var location = Account(); string root = backupInput?.Text.Trim() ?? "";
        if (root == "") throw new InvalidOperationException(L.T("NoRoot"));
        _ = PathPolicy.Normalize(root);
        string[] clients = location.Platform == ChatPlatform.QQ ? ["QQ"] : ["WeChat", "Weixin"];
        if (CleanupEngine.RunningProcesses(new CleanupRule { Processes = clients }).Length > 0)
            throw new InvalidOperationException(L.T("ClientMustClose", location.Platform == ChatPlatform.QQ ? "QQ" : "WeChat"));
        string destination = Path.Combine(root, location.Platform.ToString(), location.Account,
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + "-raw");
        if (!Confirm(L.T("ChatBackupConfirm", destination))) return;
        preferences = preferences with { BackupRoot = root }; JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences);
        await RunPausable(async token => {
            var progress = Progress();
            var result = await Task.Run(() => {
                var service = new TransferService(); var plan = service.Plan(location.Directory, destination, processes: clients, cancellation: token);
                return service.Execute(plan, Path.Combine(dataRoot, "journals"), progress, token);
            });
            if (result.Cancelled) { status.Text = L.T("Cancelled"); return; }
            if (result.Issues.Count > 0) throw new IOException("Raw backup was not completed. Inspect its journal.");
            activeManifest = result.Manifest; verifiedBackups[location.Directory] = result.Manifest;
            preparedArchive = await Task.Run(() => ReadChatArchive(result.Manifest, location), token);
            JsonFiles.Write(Path.Combine(dataRoot, "chat-backups.json"), verifiedBackups);
            chatStep = 2; RenderChatStage(); status.Text = L.T("BackupVerified");
        });
    }

    private async Task CapturePassive() => await Run(async token => {
        var archived = ArchiveAccount(); var diagnostics = ChatDiagnostics();
        byte[] key = await Task.Run(() => PassiveKeyReader.Read(archived, cancellation: token, diagnostics: diagnostics));
        SetCapturedKey(key); RenderChatStage(); status.Text = L.T("KeyReady");
    });

    private async Task CaptureActive() {
        var location = ArchiveAccount(); bool qq = location.Platform == ChatPlatform.QQ;
        if (!Confirm(L.T(qq ? "QQActiveKeyConfirm" : "ActiveKeyConfirm"))) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = qq ? "QQ.exe|QQ.exe" : "Weixin.exe|Weixin.exe" };
        if (dialog.ShowDialog(this) != true) return;
        await Run(async token => {
            byte[] key = await CaptureWorker.Capture(location, dialog.FileName, token, ChatDiagnostics());
            SetCapturedKey(key); RenderChatStage(); status.Text = L.T("KeyReady");
        });
    }
    private IProgress<ChatDiagnostic> ChatDiagnostics()=>new DiagnosticSink(()=>debugEnabled,new Progress<ChatDiagnostic>(value=>{if(operation!=null&&debugEnabled)operationView.AddDiagnostic(value);}));
    private sealed class DiagnosticSink(Func<bool> enabled,IProgress<ChatDiagnostic> target):IChatDiagnosticSwitch{
        public bool Enabled=>enabled();public void Report(ChatDiagnostic value){if(Enabled)target.Report(value);}
    }

    private async Task IncrementalBackupChat() {
        var location = Account(); string root = backupInput?.Text.Trim() ?? "";
        if (string.IsNullOrEmpty(root)) throw new InvalidOperationException(L.T("NoRoot"));
        string? previous = verifiedBackups.GetValueOrDefault(location.Directory);
        if (previous == null || !File.Exists(previous)) {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Storloom backup manifest|*.json", Title = L.T("ChooseIncrementalBase") };
            if (picker.ShowDialog(this) != true) return; previous = picker.FileName;
        }
        string destination = Path.Combine(PathPolicy.Normalize(root), location.Platform.ToString(), location.Account,
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + "-raw");
        if (!Confirm(L.T("IncrementalBackupConfirm", previous, destination))) return;
        await RunPausable(async token => {
            var progress = Progress();
            var result = await Task.Run(() => ChatIncrementalBackup.Create(location.Directory, destination, previous,
                Path.Combine(dataRoot, "journals"), location.Platform == ChatPlatform.QQ ? ["QQ"] : ["Weixin", "WeChat"], progress, token));
            activeManifest = result.Backup.Manifest; verifiedBackups[location.Directory] = activeManifest;
            preparedArchive = await Task.Run(() => ReadChatArchive(activeManifest, location), token);
            preferences = preferences with { BackupRoot = root }; JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences);
            JsonFiles.Write(Path.Combine(dataRoot, "chat-backups.json"), verifiedBackups);
            chatStep = 2; RenderChatStage(); status.Text = L.T("IncrementalBackupDone", result.ReusedFiles, View.Size(result.ReusedBytes), View.Size(result.Backup.Bytes));
        });
    }
    private async Task UpdateChatReading() {
        string directory = View.Folder(this);
        if (directory != "" && Confirm(L.T("UpdateReadingConfirm", directory))) await ExportChatTo(directory, update: true);
    }
    private Task ExportChat() => ExportChatTo(null);
    private async Task ResumeChat() {
        string directory = View.Folder(this);
        if (directory != "") await ExportChatTo(directory);
    }
    private async Task ExportChatTo(string? resumeDirectory, bool update = false) {
        var account = Account(); string raw = ArchiveDirectory();
        string target = resumeDirectory ?? raw + "-reading-" + Guid.NewGuid().ToString("N")[..6];
        VaultPasswordWindow? protection=null;
        if(encryptAfterExport){
            protection=new VaultPasswordWindow(true,true){Owner=this};
            if(protection.ShowDialog()!=true||protection.Password==null)return;
        }
        byte[]? key = capturedKey?.ToArray();
        try {
            if (keyInput?.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(keyInput.Password)) {
                if (key != null) CryptographicOperations.ZeroMemory(key);
                try{key=ChatDatabaseKey.Parse(keyInput.Password,account.Platform);}
                catch(FormatException){throw new InvalidOperationException(L.T("KeyFormatInvalid"));}
            }
            if (NeedsKey() && key == null) throw new InvalidOperationException(L.T("ExportKeyMissing"));
            keyInput?.Clear();
            JsonFiles.Write(Path.Combine(dataRoot,"preferences.json"),preferences);
            await RunPausable(async token => {
                var progress = Progress(); ChatReadingUpdateResult? updateResult = null;
                var result = await Task.Run(() => {
                    if (!update) return new ChatExporter(preferences.ChatParallelism, useLocalImageConfiguration: true, downloadQQGroupAvatars: preferences.DownloadQQGroupAvatars).ExportVerifiedBackup(
                        activeManifest!, target, account.Platform, key, progress, token, verifyBackup: verifyChatBackup, resume: resumeDirectory != null);
                    updateResult = ChatReadingUpdate.Update(activeManifest!, target, account.Platform, key, preferences.ChatParallelism,
                        verifyChatBackup, useLocalConfiguration: true, progress: progress, cancellation: token, downloadQQGroupAvatars: preferences.DownloadQQGroupAvatars);
                    return JsonFiles.Read<ChatExportResult>(Path.Combine(target, "export-report.json"));
                });
                latestExportResult=result;
                readingDirectory = target; exportReport = Path.Combine(target, "export-report.json"); readingComplete = result.Complete;
                operationIncomplete = !result.Complete;
                foreach (var issue in result.Issues) operationView.AddLog($"[{issue.RuleId}] {issue.Path}: {issue.Reason}");
                if(protection?.Password!=null && !result.CanResume){
                    using var vault=await Task.Run(()=>ChatVault.Create(target,protection.Password.AsSpan(),token,progress),token);
                    protection.ClearSecret();
                    await Task.Run(()=>vault.Encrypt(protection.IncludeMedia,protection.Workers,progress,token),token);
                }
                chatStep = 3; SetCapturedKey(null); RenderChatStage();
                status.Text = updateResult == null ? L.T("ExportDone", result.Messages, result.Databases, L.T(result.Complete ? "Complete" : "Incomplete"), exportReport)
                    : L.T("UpdateReadingDone", updateResult.Added, updateResult.Changed, updateResult.Unchanged, updateResult.Conversations);
            });
        } finally { protection?.ClearSecret(); if (key != null) CryptographicOperations.ZeroMemory(key); }
    }

}
