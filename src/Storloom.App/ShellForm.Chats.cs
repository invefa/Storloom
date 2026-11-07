using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;
using Storloom.Core.Localization;

namespace Storloom.App;

public sealed partial class ShellForm {
    private List<ChatLocation> locations = [];
    private ChatLocation? activeChat;
    private int chatStep;
    private Panel chatContent = null!;
    private FlowLayoutPanel chatFooter = null!;
    private ListView chatList = null!;
    private Label[] chatSteps = [];
    private Button chatNext = null!;
    private TextBox? backupInput, keyInput;
    private string? activeManifest, readingDirectory, exportReport;
    private bool readingComplete;

    private void BuildChats() {
        var layout = Page("chats", "ChatTitle", "ChatLead", Ui.Auto(), Ui.Fill(), Ui.Auto());
        var steps = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 18) };
        chatSteps = new Label[3];
        string[] titles = ["ChatStepAccount", "ChatStepBackup", "ChatStepExport"];
        for (int i = 0; i < 3; i++) {
            steps.ColumnStyles.Add(new(SizeType.Percent, 33.333f));
            chatSteps[i] = Ui.Text(L.T(titles[i])); chatSteps[i].Dock = DockStyle.Fill;
            chatSteps[i].Padding = new Padding(12, 9, 12, 9); chatSteps[i].Margin = new Padding(0, 0, 8, 0);
            steps.Controls.Add(chatSteps[i], i, 0);
        }
        chatContent = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(20), Margin = Padding.Empty };
        chatFooter = Ui.Flow(true); chatFooter.Margin = new Padding(0, 16, 0, 0);
        layout.Controls.Add(steps, 0, 1); layout.Controls.Add(chatContent, 0, 2); layout.Controls.Add(chatFooter, 0, 3);
        chatStep = 0; RenderChatStage();
    }
    private void RenderChatStage() {
        foreach (Control control in chatContent.Controls.Cast<Control>().ToArray()) control.Dispose();
        foreach (Control control in chatFooter.Controls.Cast<Control>().ToArray()) control.Dispose();
        chatContent.Controls.Clear(); chatFooter.Controls.Clear(); backupInput = keyInput = null;
        for (int i = 0; i < chatSteps.Length; i++) {
            chatSteps[i].BackColor = i == Math.Min(chatStep, 2) ? Color.FromArgb(220, 240, 235) : Color.Transparent;
            chatSteps[i].ForeColor = i == Math.Min(chatStep, 2) ? Ui.Accent : Ui.Muted;
        }
        if (chatStep == 0) BuildChatAccounts(); else if (chatStep == 1) BuildChatBackup();
        else if (chatStep == 2) BuildChatExport(); else BuildChatFinished();
        chatFooter.Controls.Add(Button("QuickStart", () => { using var help = new HelpForm(2); help.ShowDialog(this); }));
    }
    private void BuildChatAccounts() {
        var body = Ui.Grid(Ui.Auto(), Ui.Auto(), Ui.Fill()); chatContent.Controls.Add(body);
        body.Controls.Add(Ui.Text(L.T("AccountHelp")), 0, 0);
        var actions = Ui.Flow(); actions.Margin = new Padding(0, 8, 0, 14);
        actions.Controls.AddRange([AsyncButton("FindAccounts", () => DiscoverChats(null), true), AsyncButton("SelectAccountFolder", async () => {
            string path = Ui.Folder(this); if (path != "") await DiscoverChats(path);
        })]); body.Controls.Add(actions, 0, 1);
        chatList = Ui.List(("Account", 180), ("Platform", 100), ("AccountSize", 125)); chatList.MultiSelect = false;
        chatList.Name = "chat-accounts"; body.Controls.Add(chatList, 0, 2);
        foreach (var location in locations) Ui.Row(chatList, location, location.Account, location.Platform.ToString(), Ui.Size(location.DatabaseBytes));
        chatNext = Button("Next", () => {
            if (chatList.SelectedItems.Count != 1) throw new InvalidOperationException(L.T("NoAccountSelected"));
            var selected = (ChatLocation)chatList.SelectedItems[0].Tag!;
            if (activeChat?.Directory != selected.Directory || activeChat?.Platform != selected.Platform) SetCapturedKey(null);
            activeChat = selected; activeManifest = null; readingDirectory = exportReport = null;
            chatStep = 1; RenderChatStage();
        }, true); chatNext.Enabled = false; chatNext.Name = "chat-next";
        chatList.SelectedIndexChanged += (_, _) => chatNext.Enabled = chatList.SelectedItems.Count == 1;
        chatFooter.Controls.Add(chatNext);
    }
    private async Task DiscoverChats(string? root) => await Run(async _ => {
        var found = await Task.Run(() => ChatDiscovery.Discover(root == null ? null : [root]));
        locations = found.GroupBy(l => (l.Platform, l.Directory)).Select(group => {
            var first = group.First(); return first with { DatabaseBytes = group.Sum(l => l.DatabaseBytes) };
        }).ToList();
        chatList.BeginUpdate(); chatList.Items.Clear();
        foreach (var location in locations) Ui.Row(chatList, location, location.Account, location.Platform.ToString(), Ui.Size(location.DatabaseBytes));
        chatList.EndUpdate(); status.Text = locations.Count == 0 ? L.T("NoAccounts") : L.T("SelectAccount");
        if (chatList.Items.Count == 1) chatList.Items[0].Selected = true;
    });
    private ChatLocation Account() => activeChat ?? throw new InvalidOperationException(L.T("NoAccountSelected"));
    private void OpenChatBackup() {
        using var dialog = new OpenFileDialog { Filter = "Storloom backup manifest|*.json", Title = L.T("OpenChatBackup") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
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
    private void BuildChatBackup() {
        var location = Account();
        var body = Ui.Grid(Ui.Auto(), Ui.Auto(), Ui.Auto(), Ui.Auto(), Ui.Fill()); chatContent.Controls.Add(body);
        body.Controls.Add(Ui.Text(L.T("BackupStepTitle"), true), 0, 0);
        body.Controls.Add(Ui.Text(L.T("BackupStepLead")), 0, 1);
        var source = new TextBox { Dock = DockStyle.Top, ReadOnly = true, Text = location.Directory,
            BackColor = Ui.Canvas, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 10, 0, 18) };
        body.Controls.Add(source, 0, 2);
        var destination = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        destination.ColumnStyles.Add(new(SizeType.Absolute, 110)); destination.ColumnStyles.Add(new(SizeType.Percent, 100));
        destination.ColumnStyles.Add(new(SizeType.AutoSize));
        destination.Controls.Add(Ui.Text(L.T("BackupTo")), 0, 0); backupInput = Ui.Input(); backupInput.Text = preferences.BackupRoot;
        destination.Controls.Add(backupInput, 1, 0);
        destination.Controls.Add(Button("Browse", () => { string path = Ui.Folder(this, backupInput.Text); if (path != "") backupInput.Text = path; }), 2, 0);
        body.Controls.Add(destination, 0, 3);
        chatFooter.Controls.Add(AsyncButton("BackupNow", BackupChat, true));
        if (verifiedBackups.TryGetValue(location.Directory, out string? previous) && File.Exists(previous))
            chatFooter.Controls.Add(Button("UsePreviousBackup", () => {
                activeManifest = previous; _ = ArchiveDirectory(); chatStep = 2; RenderChatStage();
            }));
        chatFooter.Controls.Add(Button("GoBack", () => { chatStep = 0; RenderChatStage(); }));
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
    private void BuildChatExport() {
        bool encrypted = NeedsKey();
        var body = Ui.Grid(Ui.Auto(), Ui.Auto(), Ui.Auto(), Ui.Auto(), Ui.Auto(), Ui.Fill()); chatContent.Controls.Add(body);
        body.Controls.Add(Ui.Text(L.T("ExportStepTitle"), true), 0, 0);
        body.Controls.Add(Ui.Text(L.T("ExportStepLead")), 0, 1);
        var path = new TextBox { Dock = DockStyle.Top, ReadOnly = true, Text = ArchiveDirectory(), BackColor = Ui.Canvas,
            Margin = new Padding(0, 10, 0, 18) }; body.Controls.Add(path, 0, 2);
        var keyState = Ui.Text(L.T(!encrypted ? "PlainStep" : capturedKey != null ? "KeyReady" : "EncryptedStep"));
        keyState.Margin = new Padding(0, 0, 0, 16); body.Controls.Add(keyState, 0, 3);
        var keyControls = Ui.Grid(Ui.Auto(), Ui.Auto(), Ui.Auto()); keyControls.AutoSize = true; keyControls.Dock = DockStyle.Top;
        if (encrypted) {
            var methods = new ContextMenuStrip();
            var passive = new ToolStripMenuItem(L.T("ReadPassive"));
            passive.Click += async (_, _) => { try { await CapturePassive(); } catch (Exception ex) { Error(ex); } }; methods.Items.Add(passive);
            if (Account().Platform == ChatPlatform.WeChat) {
                var active = new ToolStripMenuItem(L.T("ReadWeChatLogin")); active.Click += async (_, _) => { try { await CaptureActive(); } catch (Exception ex) { Error(ex); } }; methods.Items.Add(active);
            }
            var read = Button("ReadKey", () => { }); read.ContextMenuStrip = methods;
            read.Click += (_, _) => methods.Show(read, new Point(0, read.Height));
            keyControls.Controls.Add(read, 0, 0);
            var manual = new CheckBox { Text = L.T("ManualKey"), AutoSize = true, Margin = new Padding(0, 16, 0, 8) };
            keyInput = Ui.Input(); keyInput.UseSystemPasswordChar = true; keyInput.Visible = false;
            keyInput.Width = 500; keyInput.MaximumSize = new Size(600, 0);
            manual.CheckedChanged += (_, _) => { keyInput.Visible = manual.Checked; if (!manual.Checked) keyInput.Clear(); UpdateExportEnabled(encrypted); };
            keyInput.TextChanged += (_, _) => UpdateExportEnabled(encrypted);
            keyControls.Controls.Add(manual, 0, 1); keyControls.Controls.Add(keyInput, 0, 2);
        }
        body.Controls.Add(keyControls, 0, 4);
        chatNext = AsyncButton("ExportReading", ExportChat, true); chatNext.Name = "chat-export";
        chatNext.Enabled = !encrypted || capturedKey != null; chatFooter.Controls.Add(chatNext);
        chatFooter.Controls.Add(Button("FinishRawOnly", () => { chatStep = 3; readingDirectory = exportReport = null; SetCapturedKey(null); RenderChatStage(); }));
        chatFooter.Controls.Add(Button("GoBack", () => { chatStep = 1; RenderChatStage(); }));
    }
    private void UpdateExportEnabled(bool encrypted) => chatNext.Enabled = !encrypted || capturedKey != null ||
        (keyInput?.Visible == true && !string.IsNullOrWhiteSpace(keyInput.Text));
    private async Task CapturePassive() => await Run(async token => {
        var archived = ArchiveAccount(); byte[] key = await Task.Run(() => PassiveKeyReader.Read(archived, cancellation: token));
        SetCapturedKey(key); RenderChatStage(); status.Text = L.T("KeyReady");
    });
    private async Task CaptureActive() {
        var location = ArchiveAccount(); if (!Confirm(L.T("ActiveKeyConfirm"))) return;
        using var dialog = new OpenFileDialog { Filter = "Weixin.exe|Weixin.exe" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
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
            if (keyInput?.Visible == true && !string.IsNullOrWhiteSpace(keyInput.Text)) {
                if (key != null) CryptographicOperations.ZeroMemory(key);
                key = account.Platform == ChatPlatform.QQ ? Encoding.ASCII.GetBytes(keyInput.Text) : Convert.FromHexString(keyInput.Text.Trim());
            }
            if (NeedsKey() && key == null) throw new InvalidOperationException(L.T("ExportKeyMissing"));
            keyInput?.Clear();
            await Run(async token => {
                var progress = Progress(); var result = await Task.Run(() => new ChatExporter().ExportVerifiedBackup(
                    activeManifest!, target, account.Platform, key, progress, token));
                readingDirectory = target; exportReport = Path.Combine(target, "export-report.json"); readingComplete = result.Complete;
                chatStep = 3; SetCapturedKey(null); RenderChatStage();
                status.Text = L.T("ExportDone", result.Messages, result.Databases, L.T(result.Complete ? "Complete" : "Incomplete"), exportReport);
            });
        } finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
    }
    private void BuildChatFinished() {
        var body = Ui.Grid(Ui.Auto(), Ui.Auto(), Ui.Auto(), Ui.Fill()); chatContent.Controls.Add(body);
        body.Controls.Add(Ui.Text(L.T("ArchiveFinished"), true), 0, 0);
        body.Controls.Add(Ui.Text(L.T(readingDirectory == null ? "RawReady" : readingComplete ? "ReadingReady" : "ReadingPartial")), 0, 1);
        var paths = new TextBox { Dock = DockStyle.Top, Multiline = true, ReadOnly = true, Height = 95, ScrollBars = ScrollBars.Vertical,
            BackColor = Ui.Canvas, Text = ArchiveDirectory() + (readingDirectory == null ? "" : Environment.NewLine + readingDirectory), Margin = new Padding(0, 12, 0, 16) };
        body.Controls.Add(paths, 0, 2);
        chatFooter.Controls.Add(Button("NewChatArchive", () => { chatStep = 0; activeChat = null; activeManifest = null; SetCapturedKey(null); RenderChatStage(); }, true));
        chatFooter.Controls.Add(Button("OpenBackup", () => OpenFolder(ArchiveDirectory())));
        if (readingDirectory != null) chatFooter.Controls.Add(Button("OpenReading", () => OpenFile(Path.Combine(readingDirectory, "messages.html"))));
        if (exportReport != null) chatFooter.Controls.Add(Button("OpenReport", () => OpenFile(exportReport)));
    }
}
