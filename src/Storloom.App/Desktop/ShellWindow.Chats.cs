using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private List<ChatLocation> locations = [];
    private ChatLocation? activeChat;
    private int chatStep=-1;
    private Grid chatContent = null!;
    private WrapPanel chatFooter = null!;
    private DataGrid chatList = null!;
    private TextBlock[] chatSteps = [];
    private Border[] chatStepFrames = [];
    private TextBlock accountNotice = null!;
    private Button chatNext = null!;
    private Button? chatResume, chatUpdate;
    private TextBox? backupInput;
    private PasswordBox? keyInput;
    private bool verifyChatBackup = true, encryptAfterExport;
    private string? activeManifest, readingDirectory, exportReport;
    private bool readingComplete;
    private ChatExportResult? latestExportResult;
    private void BuildChats() {
        var layout = Page("chats", "ChatTitle", "ChatLead", View.Auto, View.Fill, View.Auto);
        LoadArchiveProjects();
        var entries=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,8,0,0)};
        var home=Button("ArchiveHome",()=>{chatStep=-1;RenderChatStage();});home.Name="archive_home_nav";entries.Children.Add(home);
        var stored=Button("StoreHome",()=>{chatStep=5;RenderChatStage();});stored.Name="store_home_nav";entries.Children.Add(stored);
        entries.Children.Add(Button("ArchiveImport",OpenChatBackup));
        entries.Children.Add(Button("ArchiveCreate",()=>{chatStep=0;activeChat=null;SetCapturedKey(null);RenderChatStage();},true));
        var more=new ContextMenu();var moreButton=Button("ArchiveMore",()=>more.IsOpen=true);more.PlacementTarget=moreButton;moreButton.ContextMenu=more;
        void Entry(string key,Action action){var entry=new MenuItem{Header=L.T(key)};entry.Click+=(_,_)=>Guard(action);more.Items.Add(entry);}
        Entry("OpenChatBackup",OpenChatBackup);Entry("OpenReaderArchive",OpenReaderArchive);Entry("VaultManage",EncryptReaderArchive);entries.Children.Add(moreButton);
        ((StackPanel)layout.Children[0]).Children.Add(entries);
        var steps = new Grid { Margin = new Thickness(0, 0, 0, 10) }; chatSteps = new TextBlock[3]; chatStepFrames = new Border[3];
        string[] keys = ["ChatStepAccount", "ChatStepBackup", "ChatStepExport"];
        for (int i = 0; i < 3; i++) {
            steps.ColumnDefinitions.Add(new ColumnDefinition { Width = View.Fill });
            chatSteps[i] = View.Text(L.T(keys[i])); chatSteps[i].Margin = new Thickness(0);
            chatStepFrames[i] = new Border { Child = chatSteps[i], CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 6, 0) };
            View.Add(steps, chatStepFrames[i], 0, i);
        }
        View.Add(layout, steps, 1); chatContent = new Grid(); View.Add(layout, View.Card(chatContent, 22), 2);
        chatFooter = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        View.Add(layout, chatFooter, 3); RenderChatStage();
    }
    private void RenderChatStage() {
        chatContent.Children.Clear(); chatFooter.Children.Clear(); backupInput = null; keyInput?.Clear(); keyInput = null; chatResume = null; chatUpdate = null;
        var home=Controls(pageHost).OfType<Button>().FirstOrDefault(b=>b.Name=="archive_home_nav");if(home!=null)home.Visibility=chatStep<0?Visibility.Collapsed:Visibility.Visible;
        foreach(var frame in chatStepFrames)frame.Visibility=chatStep is >=0 and <=3?Visibility.Visible:Visibility.Collapsed;
        for (int i = 0; i < chatSteps.Length; i++) { chatStepFrames[i].SetResourceReference(Border.BackgroundProperty, i == Math.Min(chatStep, 2) ? "Selection" : "Canvas"); }
        if(chatStep<0)BuildArchiveHome();else if(chatStep==5)BuildStores();else if(chatStep==4)BuildArchiveAccess();else if (chatStep == 0) BuildChatAccounts(); else if (chatStep == 1) BuildChatBackup(); else if (chatStep == 2) BuildChatExport(); else BuildChatFinished();
        chatFooter.Children.Add(Button("QuickStart", () => ShowHelp(2))); Theme.Enter(chatContent);
    }
    private void BuildChatAccounts() {
        var grid = View.Grid(View.Auto, View.Auto, View.Auto, View.Fill); chatContent.Children.Add(grid);
        View.Add(grid, View.Text(L.T("AccountHelp"), color: "Muted"), 0);
        var actions = View.Row(AsyncButton("FindAccounts", () => DiscoverChats(null), true), AsyncButton("SelectAccountFolder", async () => {
            string path = View.Folder(this); if (path != "") await DiscoverChats(path);
        })); actions.Margin = new Thickness(0, 6, 0, 14); View.Add(grid, actions, 1);
        chatList = View.Table(("Account", "Account", 0), ("Platform", "Platform", 120), ("AccountSize", "Size", 150), ("Path", "Path", 0));
        accountNotice = View.Text(L.T("DiscoveryHelp"), color: "Muted"); accountNotice.Name = "account_discovery_notice"; View.Add(grid, accountNotice, 2);
        chatList.Name = "chat_accounts"; chatList.ItemsSource = locations.Select(l => new AccountRow(l)).ToArray(); View.Add(grid, chatList, 3);
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
        accountNotice.Text = L.T(locations.Count == 0 ? "DiscoveryEmpty" : "DiscoveryFound", locations.Count);
        operationView.AddLog(L.T("LogAccountsDone",locations.Count));
        if (locations.Count > 0) View.Sort(chatList, "Bytes");
    });
    private sealed record AccountRow(ChatLocation Location) {
        public string Account => Location.Account; public string Platform => Location.Platform.ToString(); public string Path => Location.Directory;
        public long? Bytes => Location.AccountBytes;
        public string Size => Location.AccountBytes is long bytes ? Location.SizeComplete ? View.Size(bytes) : L.T("AtLeastSize", View.Size(bytes)) : L.T("SizeNotScanned");
    }
    private void BuildChatBackup() {
        var account = Account();
        backupInput = View.Input(preferences.BackupRoot);
        var destination = new DockPanel();
        var browse = Button("Browse", () => { string path = View.Folder(this, backupInput.Text); if (path != "") backupInput.Text = path; });
        browse.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); destination.Children.Add(browse); destination.Children.Add(backupInput);
        chatContent.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = View.Stack(View.Text(L.T("BackupStepTitle"), 23), View.Text(L.T("BackupStepLead"), color: "Muted"),
                View.Field("AccountFolderLabel", View.Input(account.Directory, true)), View.Field("BackupTo", destination),View.Field("ChatWorkers",ChatWorkerSelector("backup",()=>backupInput.Text))) });
        chatFooter.Children.Add(AsyncButton("BackupNow", BackupChat, true));
        chatFooter.Children.Add(AsyncButton("IncrementalBackup", IncrementalBackupChat));
        if (verifiedBackups.TryGetValue(account.Directory, out string? previous) && File.Exists(previous))
            chatFooter.Children.Add(AsyncButton("UsePreviousBackup", () => UseChatBackup(previous)));
        chatFooter.Children.Add(Button("GoBack", () => { chatStep = 0; RenderChatStage(); }));
    }
    private void BuildChatExport() {
        bool encrypted = NeedsKey();
        var stack = View.Stack(View.Field("RawBackupLabel", View.Input(ArchiveDirectory(), true)));
        // The scrollbar owns a separate layout column; content is measured against the viewport.
        stack.Margin = new Thickness(0,0,12,0);
        chatContent.Children.Add(new ScrollViewer { Content=stack, VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, HorizontalContentAlignment=HorizontalAlignment.Stretch,
            Style=(Style)FindResource("EditorScrollViewer") });
        var accessTitle=View.Text(L.T("ExportAccessSection"),15);accessTitle.Margin=new Thickness(0,14,0,8);stack.Children.Add(accessTitle);
        stack.Children.Add(View.Text(L.T(!encrypted ? "PlainStep" : capturedKey != null ? "KeyReady" : "KeyAcquireHelp"),color:"Muted"));
        if (encrypted) {
            var methods = View.Row();
            methods.Children.Add(AsyncButton(Account().Platform == ChatPlatform.QQ ? "ReadQQLogin" : "ReadWeChatLogin", CaptureActive, true));
            methods.Children.Add(AsyncButton("ReadPassive", CapturePassive));
            methods.Children.Add(AsyncButton("KeyImport",ImportChatKey));
            if(capturedKey!=null)methods.Children.Add(Button("KeyManage",ManageChatKey));
            stack.Children.Add(methods);
            var manual = new CheckBox { Content = L.T("ManualKey"), Margin = new Thickness(0, 16, 0, 8) };
            keyInput = View.SecretInput(); keyInput.Name = "database_key";
            var manualFields = View.Stack(View.Field("DatabaseKeyLabel", keyInput), View.Text(L.T("DatabaseKeyEntryHelp"), color: "Muted"));
            manualFields.Visibility = Visibility.Collapsed; keyInput.Visibility = Visibility.Collapsed;
            manual.Checked += (_, _) => { manualFields.Visibility = keyInput.Visibility = Visibility.Visible; UpdateExportEnabled(encrypted); };
            manual.Unchecked += (_, _) => { keyInput.Clear(); manualFields.Visibility = keyInput.Visibility = Visibility.Collapsed; UpdateExportEnabled(encrypted); };
            keyInput.PasswordChanged += (_, _) => UpdateExportEnabled(encrypted); stack.Children.Add(manual); stack.Children.Add(manualFields);
        }
        var protection=new CheckBox{Content=L.T("EncryptAfterExport"),IsChecked=encryptAfterExport,Margin=new Thickness(0,12,0,4)};
        protection.Checked+=(_,_)=>encryptAfterExport=true;protection.Unchecked+=(_,_)=>encryptAfterExport=false;
        var optionsTitle=View.Text(L.T("ExportOptionsSection"),15);optionsTitle.Margin=new Thickness(0,18,0,8);stack.Children.Add(optionsTitle);stack.Children.Add(protection);
        stack.Children.Add(View.Text(L.T("EncryptAfterExportHelp"),color:"Muted"));
        if(Account().Platform==ChatPlatform.QQ){
            var avatars=new CheckBox{Content=L.T("DownloadQQGroupAvatars"),IsChecked=preferences.DownloadQQGroupAvatars,Margin=new Thickness(0,10,0,4),ToolTip=L.T("DownloadQQGroupAvatarsHelp")};
            avatars.Checked+=(_,_)=>preferences=preferences with{DownloadQQGroupAvatars=true};avatars.Unchecked+=(_,_)=>preferences=preferences with{DownloadQQGroupAvatars=false};stack.Children.Add(avatars);
        }
        var verify = new CheckBox { Content = L.T("VerifyBackupBeforeExport"), IsChecked = verifyChatBackup, Margin = new Thickness(0, 12, 0, 4) };
        verify.Checked += (_, _) => verifyChatBackup = true;
        verify.Unchecked += (_, _) => verifyChatBackup = false;
        stack.Children.Add(View.Field("ChatWorkers",ChatWorkerSelector("export",()=>readingDirectory??preferences.BackupRoot)));
        stack.Children.Add(verify);
        var help=new Expander{Header=L.T("ExportHelpTitle"),Margin=new Thickness(0,10,0,0),HorizontalContentAlignment=HorizontalAlignment.Stretch,
            Content=View.Stack(View.Text(L.T("VerifyBackupHelp"),color:"Muted"),View.Text(L.T("UpdateReadingHelp"),color:"Muted"),View.Text(L.T("ResumeReadingHelp"),color:"Muted"))};
        stack.Children.Add(help);
        chatNext = AsyncButton("ArchiveCreate", ExportChat, true); chatNext.Name = "chat_export"; chatNext.IsEnabled = !encrypted || capturedKey != null;
        chatResume = AsyncButton("ResumeReading", ResumeChat); chatResume.IsEnabled = chatNext.IsEnabled;
        chatUpdate = AsyncButton("UpdateReading", UpdateChatReading); chatUpdate.IsEnabled = chatNext.IsEnabled;

        chatFooter.Children.Add(chatNext); chatFooter.Children.Add(chatResume); chatFooter.Children.Add(chatUpdate); chatFooter.Children.Add(Button("FinishRawOnly", () => {
            chatStep = 3; readingDirectory = exportReport = null; SetCapturedKey(null); RenderChatStage();
        })); chatFooter.Children.Add(Button("GoBack", () => { chatStep = 1; RenderChatStage(); }));
    }
    private int ChatWorkers(string task,string? path)=>preferences.ChatWorkersAutomatic?WorkerRecommendation.For(task,path):Math.Clamp(preferences.ChatParallelism,1,8);
    private ComboBox ChatWorkerSelector(string task,Func<string?> path){var choice=WorkerRecommendation.Selector(preferences.ChatParallelism,preferences.ChatWorkersAutomatic,task,path);choice.SelectionChanged+=(_,_)=>{if(choice.Tag is not true&&choice.SelectedValue is int count)preferences=preferences with{ChatParallelism=count>0?count:preferences.ChatParallelism,ChatWorkersAutomatic=count==0};};return choice;}
    private void UpdateExportEnabled(bool encrypted) {
        chatNext.IsEnabled = !encrypted || capturedKey != null || (keyInput?.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(keyInput.Password));
        if (chatResume != null) chatResume.IsEnabled = chatNext.IsEnabled;
        if (chatUpdate != null) chatUpdate.IsEnabled = chatNext.IsEnabled;
    }
    private void BuildChatFinished() {
        RegisterCurrentArchive();
        var report = exportReport != null && File.Exists(exportReport) ? JsonFiles.Read<ChatExportResult>(exportReport) : readingDirectory != null ? latestExportResult : null;
        bool failed = report is { Databases: 0 };
        var stack = View.Stack(View.Text(L.T(failed ? "ReadingFailedTitle" : report is { Complete: false } ? "ReadingPartialTitle" : "ArchiveFinished"), 23),
            View.Text(L.T(failed ? "ReadingFailed" : readingDirectory == null ? "RawReady" : readingComplete ? "ReadingReady" : "ReadingPartial")), View.Field("RawBackupLabel", View.Input(ArchiveDirectory(), true)));
        if (readingDirectory != null) stack.Children.Add(View.Field("ReadingCopyLabel", View.Input(readingDirectory, true)));
        if (report != null) {
            var coverage = View.Text(L.T("ReadingCoverage", report.Contacts, report.MediaFiles, report.UnresolvedMedia, report.UnsupportedMessages), color: "Muted");
            coverage.Margin = new Thickness(0, 4, 0, 0); stack.Children.Add(coverage);
            foreach (var issue in report.Issues.Take(5)) stack.Children.Add(View.Text(issue.Reason, color: "Muted"));
            if (report.CanResume) {
                stack.Children.Add(View.Text(L.T("ResumeReadingHelp"), color: "Muted"));
                chatFooter.Children.Add(Button("ResumeReading", () => { chatStep = 2; RenderChatStage(); }));
            }
        }
        chatContent.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        chatFooter.Children.Add(Button("NewChatArchive", () => { chatStep = 0; activeChat = null; activeManifest = null; SetCapturedKey(null); RenderChatStage(); }, true));
        chatFooter.Children.Add(Button("OpenBackup", () => OpenFolder(ArchiveDirectory())));
        if (readingDirectory != null && !failed) chatFooter.Children.Add(Button("OpenReading", () => new ChatReaderWindow(readingDirectory, dataRoot, preferences.ReduceMotion) { Owner = this }.ShowDialog()));
        if (exportReport != null && File.Exists(exportReport)) chatFooter.Children.Add(Button("OpenReport", () => OpenFile(exportReport)));
    }
}
