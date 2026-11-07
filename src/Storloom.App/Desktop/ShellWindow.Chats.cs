using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private List<ChatLocation> locations = [];
    private ChatLocation? activeChat;
    private int chatStep;
    private Grid chatContent = null!;
    private WrapPanel chatFooter = null!;
    private DataGrid chatList = null!;
    private TextBlock[] chatSteps = [];
    private Button chatNext = null!;
    private TextBox? backupInput;
    private PasswordBox? keyInput;
    private string? activeManifest, readingDirectory, exportReport;
    private bool readingComplete;
    private void BuildChats() {
        var layout = Page("chats", "ChatTitle", "ChatLead", View.Auto, View.Fill, View.Auto);
        var steps = new Grid { Margin = new Thickness(0, 0, 0, 16) }; chatSteps = new TextBlock[3];
        string[] keys = ["ChatStepAccount", "ChatStepBackup", "ChatStepExport"];
        for (int i = 0; i < 3; i++) {
            steps.ColumnDefinitions.Add(new ColumnDefinition { Width = View.Fill });
            chatSteps[i] = View.Text(L.T(keys[i])); chatSteps[i].Padding = new Thickness(12, 10, 12, 10); chatSteps[i].Margin = new Thickness(0, 0, 8, 0);
            View.Add(steps, chatSteps[i], 0, i);
        }
        View.Add(layout, steps, 1); chatContent = new Grid(); View.Add(layout, View.Card(chatContent, 22), 2);
        chatFooter = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        View.Add(layout, chatFooter, 3); RenderChatStage();
    }
    private void RenderChatStage() {
        chatContent.Children.Clear(); chatFooter.Children.Clear(); backupInput = null; keyInput?.Clear(); keyInput = null;
        for (int i = 0; i < chatSteps.Length; i++) { chatSteps[i].SetResourceReference(TextBlock.BackgroundProperty, i == Math.Min(chatStep, 2) ? "Selection" : "Canvas"); }
        if (chatStep == 0) BuildChatAccounts(); else if (chatStep == 1) BuildChatBackup(); else if (chatStep == 2) BuildChatExport(); else BuildChatFinished();
        chatFooter.Children.Add(Button("QuickStart", () => ShowHelp(2))); Theme.Enter(chatContent);
    }
    private void BuildChatAccounts() {
        var grid = View.Grid(View.Auto, View.Auto, View.Fill); chatContent.Children.Add(grid);
        View.Add(grid, View.Text(L.T("AccountHelp"), color: "Muted"), 0);
        var actions = View.Row(AsyncButton("FindAccounts", () => DiscoverChats(null), true), AsyncButton("SelectAccountFolder", async () => {
            string path = View.Folder(this); if (path != "") await DiscoverChats(path);
        })); actions.Margin = new Thickness(0, 6, 0, 14); View.Add(grid, actions, 1);
        chatList = View.Table(("Account", "Account", 0), ("Platform", "Platform", 120), ("AccountSize", "Size", 150), ("Path", "Path", 0));
        chatList.Name = "chat_accounts"; chatList.ItemsSource = locations.Select(l => new AccountRow(l)).ToArray(); View.Add(grid, chatList, 2);
        chatNext = Button("Next", () => {
            if (chatList.SelectedItem is not AccountRow row) throw new InvalidOperationException(L.T("NoAccountSelected"));
            if (activeChat?.Directory != row.Location.Directory || activeChat?.Platform != row.Location.Platform) SetCapturedKey(null);
            activeChat = row.Location; activeManifest = null; readingDirectory = exportReport = null; chatStep = 1; RenderChatStage();
        }, true); chatNext.Name = "chat_next"; chatNext.IsEnabled = false;
        chatList.SelectionChanged += (_, _) => chatNext.IsEnabled = chatList.SelectedItem is AccountRow; chatFooter.Children.Add(chatNext);
    }
    private async Task DiscoverChats(string? root) => await Run(async token => {
        var progress = Progress(); locations = await Task.Run(() => ChatDiscovery.Discover(root == null ? null : [root], progress, token));
        chatList.ItemsSource = locations.Select(l => new AccountRow(l)).ToArray();
        if (locations.Count == 1) chatList.SelectedIndex = 0; status.Text = L.T(locations.Count == 0 ? "NoAccounts" : "SelectAccount");
    });
    private sealed record AccountRow(ChatLocation Location) {
        public string Account => Location.Account; public string Platform => Location.Platform.ToString(); public string Path => Location.Directory;
        public string Size => Location.AccountBytes is long bytes ? Location.SizeComplete ? View.Size(bytes) : L.T("AtLeastSize", View.Size(bytes)) : L.T("SizeNotScanned");
    }
    private void BuildChatBackup() {
        var account = Account();
        backupInput = View.Input(preferences.BackupRoot);
        var destination = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var browse = Button("Browse", () => { string path = View.Folder(this, backupInput.Text); if (path != "") backupInput.Text = path; });
        DockPanel.SetDock(browse, Dock.Right); destination.Children.Add(browse); destination.Children.Add(backupInput);
        chatContent.Children.Add(View.Stack(View.Text(L.T("BackupStepTitle"), 23), View.Text(L.T("BackupStepLead"), color: "Muted"),
            View.Input(account.Directory, true), View.Text(L.T("BackupTo")), destination));
        chatFooter.Children.Add(AsyncButton("BackupNow", BackupChat, true));
        if (verifiedBackups.TryGetValue(account.Directory, out string? previous) && File.Exists(previous))
            chatFooter.Children.Add(Button("UsePreviousBackup", () => { activeManifest = previous; _ = ArchiveDirectory(); chatStep = 2; RenderChatStage(); }));
        chatFooter.Children.Add(Button("GoBack", () => { chatStep = 0; RenderChatStage(); }));
    }
    private void BuildChatExport() {
        bool encrypted = NeedsKey();
        var stack = View.Stack(View.Text(L.T("ExportStepTitle"), 23), View.Text(L.T("ExportStepLead"), color: "Muted"),
            View.Input(ArchiveDirectory(), true), View.Text(L.T(!encrypted ? "PlainStep" : capturedKey != null ? "KeyReady" : "EncryptedStep")));
        chatContent.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (encrypted) {
            var methods = View.Row(AsyncButton("ReadPassive", CapturePassive));
            if (Account().Platform == ChatPlatform.WeChat) methods.Children.Add(AsyncButton("ReadWeChatLogin", CaptureActive));
            stack.Children.Add(methods);
            var manual = new CheckBox { Content = L.T("ManualKey"), Margin = new Thickness(0, 16, 0, 8) };
            keyInput = new PasswordBox { Visibility = Visibility.Collapsed, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Stretch };
            manual.Checked += (_, _) => { keyInput.Visibility = Visibility.Visible; UpdateExportEnabled(encrypted); };
            manual.Unchecked += (_, _) => { keyInput.Clear(); keyInput.Visibility = Visibility.Collapsed; UpdateExportEnabled(encrypted); };
            keyInput.PasswordChanged += (_, _) => UpdateExportEnabled(encrypted); stack.Children.Add(manual); stack.Children.Add(keyInput);
        }
        chatNext = AsyncButton("ExportReading", ExportChat, true); chatNext.Name = "chat_export"; chatNext.IsEnabled = !encrypted || capturedKey != null;
        chatFooter.Children.Add(chatNext); chatFooter.Children.Add(Button("FinishRawOnly", () => {
            chatStep = 3; readingDirectory = exportReport = null; SetCapturedKey(null); RenderChatStage();
        })); chatFooter.Children.Add(Button("GoBack", () => { chatStep = 1; RenderChatStage(); }));
    }
    private void UpdateExportEnabled(bool encrypted) => chatNext.IsEnabled = !encrypted || capturedKey != null ||
        (keyInput?.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(keyInput.Password));
    private void BuildChatFinished() {
        var stack = View.Stack(View.Text(L.T("ArchiveFinished"), 23),
            View.Text(L.T(readingDirectory == null ? "RawReady" : readingComplete ? "ReadingReady" : "ReadingPartial")), View.Input(ArchiveDirectory(), true));
        if (readingDirectory != null) stack.Children.Add(View.Input(readingDirectory, true));
        if (exportReport != null && File.Exists(exportReport)) {
            var report = JsonFiles.Read<ChatExportResult>(exportReport); stack.Children.Add(View.Text(L.T("ReadingCoverage", report.Contacts, report.MediaFiles, report.UnresolvedMedia, report.UnsupportedMessages)));
        }
        chatContent.Children.Add(stack);
        chatFooter.Children.Add(Button("NewChatArchive", () => { chatStep = 0; activeChat = null; activeManifest = null; SetCapturedKey(null); RenderChatStage(); }, true));
        chatFooter.Children.Add(Button("OpenBackup", () => OpenFolder(ArchiveDirectory())));
        if (readingDirectory != null) chatFooter.Children.Add(Button("OpenReading", () => new ChatReaderWindow(readingDirectory, dataRoot, preferences.ReduceMotion) { Owner = this }.ShowDialog()));
        if (exportReport != null) chatFooter.Children.Add(Button("OpenReport", () => OpenFile(exportReport)));
    }
}
