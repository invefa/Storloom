using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;

namespace Storloom.App;
public sealed partial class ShellWindow {
    private ChatLocation Account() => activeChat ?? throw new InvalidOperationException(L.T("NoAccountSelected"));

    private void OpenChatBackup() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Storloom backup manifest|*.json", Title = L.T("OpenChatBackup") };
        if (dialog.ShowDialog(this) != true) return;
        var manifest = JsonFiles.Read<TransferJournal>(dialog.FileName);
        string directory = PathPolicy.Normalize(manifest.Plan.Destination);
        if (directory.Length <= 3) throw new InvalidDataException("A backup must be a specific account directory.");
        PathPolicy.AssertNoLinks(directory);
        ChatPlatform platform = ChatPlatform.QQ;
        string? database = ChatExporter.FindDatabases(directory, platform).FirstOrDefault();
        if (database == null) { platform = ChatPlatform.WeChat; database = ChatExporter.FindDatabases(directory, platform).FirstOrDefault(); }
        if (database == null) throw new InvalidDataException("No supported message database found in this archive.");
        string source = PathPolicy.Normalize(manifest.Plan.Source);
        activeChat = new(platform, source, Path.GetFileName(source),
            TransferService.Child(source, Path.GetRelativePath(directory, database)), new FileInfo(database).Length);
        activeManifest = dialog.FileName; _ = ArchiveDirectory(); SetCapturedKey(null);
        readingDirectory = exportReport = null; chatStep = 2; RenderChatStage(); Navigate("chats");
    }

    private string ArchiveDirectory() {
        if (activeManifest == null) throw new InvalidOperationException(L.T("ScanFirst"));
        var journal = JsonFiles.Read<TransferJournal>(activeManifest);
        if (!journal.Plan.Source.Equals(Account().Directory, StringComparison.OrdinalIgnoreCase) ||
            journal.State is not ("Verified" or "Completed" or "CopiedWithRetainedSources" or "CancelledAfterVerification") ||
            journal.Receipts.Count != journal.Plan.Files.Count)
            throw new InvalidDataException("The selected account has no matching verified raw backup.");
        if (PathPolicy.Normalize(journal.Plan.Destination).Length <= 3)
            throw new InvalidDataException("A backup must be a specific account directory.");
        PathPolicy.AssertNoLinks(journal.Plan.Destination); return journal.Plan.Destination;
    }

    private ChatLocation ArchiveAccount() {
        string archived = ArchiveDirectory(); var account = Account();
        return account with { Directory = archived, Database = TransferService.Child(archived,
            Path.GetRelativePath(account.Directory, account.Database)) };
    }

    private bool NeedsKey() {
        var location = ArchiveAccount(); using var input = File.OpenRead(location.Database);
        byte[] head = new byte[Math.Min(1040, input.Length)]; input.ReadExactly(head);
        return !head.AsSpan().StartsWith("SQLite format 3\0"u8) && !(location.Platform == ChatPlatform.QQ &&
            head.Length >= 1040 && head.AsSpan(1024).StartsWith("SQLite format 3\0"u8));
    }

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
        await Run(async token => {
            var progress = Progress();
            var result = await Task.Run(() => {
                var service = new TransferService(); var plan = service.Plan(location.Directory, destination, processes: clients, cancellation: token);
                return service.Execute(plan, Path.Combine(dataRoot, "journals"), progress, token);
            });
            if (result.Cancelled) { status.Text = L.T("Cancelled"); return; }
            if (result.Issues.Count > 0) throw new IOException("Raw backup was not completed. Inspect its journal.");
            activeManifest = result.Manifest; verifiedBackups[location.Directory] = result.Manifest;
            JsonFiles.Write(Path.Combine(dataRoot, "chat-backups.json"), verifiedBackups);
            chatStep = 2; RenderChatStage(); status.Text = L.T("BackupVerified");
        });
    }

    private async Task CapturePassive() => await Run(async token => {
        var archived = ArchiveAccount(); byte[] key = await Task.Run(() => PassiveKeyReader.Read(archived, cancellation: token));
        SetCapturedKey(key); RenderChatStage(); status.Text = L.T("KeyReady");
    });

    private async Task CaptureActive() {
        var location = ArchiveAccount(); if (!Confirm(L.T("ActiveKeyConfirm"))) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Weixin.exe|Weixin.exe" };
        if (dialog.ShowDialog(this) != true) return;
        await Run(async token => {
            byte[] key = await CaptureWorker.Capture(location, dialog.FileName, token);
            SetCapturedKey(key); RenderChatStage(); status.Text = L.T("KeyReady");
        });
    }

    private async Task ExportChat() {
        var account = Account(); string raw = ArchiveDirectory();
        string target = raw + "-reading-" + Guid.NewGuid().ToString("N")[..6];
        byte[]? key = capturedKey?.ToArray();
        try {
            if (keyInput?.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(keyInput.Password)) {
                if (key != null) CryptographicOperations.ZeroMemory(key);
                key = account.Platform == ChatPlatform.QQ ? Encoding.ASCII.GetBytes(keyInput.Password) : Convert.FromHexString(keyInput.Password.Trim());
            }
            if (NeedsKey() && key == null) throw new InvalidOperationException(L.T("ExportKeyMissing"));
            keyInput?.Clear();
            await Run(async token => {
                var progress = Progress(); var result = await Task.Run(() => new ChatExporter(preferences.ChatParallelism, useLocalImageConfiguration: true).ExportVerifiedBackup(
                    activeManifest!, target, account.Platform, key, progress, token));
                readingDirectory = target; exportReport = Path.Combine(target, "export-report.json"); readingComplete = result.Complete;
                chatStep = 3; SetCapturedKey(null); RenderChatStage();
                status.Text = L.T("ExportDone", result.Messages, result.Databases, L.T(result.Complete ? "Complete" : "Incomplete"), exportReport);
            });
        } finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
    }

}
