using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private List<ChatLocation> locations = [];
    private bool directChatSource, discoveryStarted;
    private ChatLocation? activeChat;
    private int chatStep=-1;
    private Grid chatContent = null!;
    private WrapPanel chatFooter = null!;
    private DataGrid chatList = null!;
    private TextBlock chatTaskTitle=null!;
    private TextBlock accountNotice = null!;
    private Button chatNext = null!;
    private Button? chatResume, chatUpdate;
    private Button? measureAllAccounts;
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
        var add=Button("ArchiveCreate",()=>{chatStep=0;activeChat=null;pendingArchive=null;directChatSource=false;SetCapturedKey(null);RenderChatStage();},true);add.Name="account_add_nav";entries.Children.Add(add);
        var importMenu=new ContextMenu();var import=Button("ConnectExisting",()=>importMenu.IsOpen=true);importMenu.PlacementTarget=import;import.ContextMenu=importMenu;
        void ImportEntry(string key,Action action){var entry=new MenuItem{Header=L.T(key)};entry.Click+=(_,_)=>Guard(action);importMenu.Items.Add(entry);}
        ImportEntry("OpenChatBackup",OpenChatBackup);ImportEntry("ConnectReader",ConnectReader);ImportEntry("StoreLoad",ConnectStore);entries.Children.Add(import);
        var more=new ContextMenu();var moreButton=Button("ArchiveTools",()=>more.IsOpen=true);more.PlacementTarget=moreButton;moreButton.ContextMenu=more;
        void Entry(string key,Action action){var entry=new MenuItem{Header=L.T(key)};entry.Click+=(_,_)=>Guard(action);more.Items.Add(entry);}
        Entry("ChatSettings",ConfigureChats);Entry("KeyLibrary",OpenKeyLibrary);Entry("RestoreChatBackup",RestoreChatBackup);Entry("StoreMaintenance",()=>{chatStep=5;RenderChatStage();});Entry("OpenReaderArchive",OpenReaderArchive);Entry("VaultManage",EncryptReaderArchive);entries.Children.Add(moreButton);
        ((StackPanel)layout.Children[0]).Children.Add(entries);
        chatTaskTitle=View.Text("",14);chatTaskTitle.Margin=new Thickness(0,6,0,12);View.Add(layout,chatTaskTitle,1);
        chatContent = new Grid(); View.Add(layout, View.Card(chatContent, 20), 2);
        chatFooter = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        View.Add(layout, chatFooter, 3); RenderChatStage();
    }
    private void RenderChatStage() {
        if(readingRootInput!=null&&pendingArchive!=null)pendingArchive=pendingArchive with{ReadingRoot=readingRootInput.Text};readingRootInput=null;
        chatContent.Children.Clear(); chatFooter.Children.Clear(); backupInput = null; keyInput?.Clear(); keyInput = null; chatResume = null; chatUpdate = null;
        var home=Controls(pageHost).OfType<Button>().FirstOrDefault(b=>b.Name=="archive_home_nav");if(home!=null)home.Visibility=Visibility.Visible;
        var add=Controls(pageHost).OfType<Button>().FirstOrDefault(b=>b.Name=="account_add_nav");if(add!=null)add.SetResourceReference(FrameworkElement.StyleProperty,chatStep<0?"PrimaryButton":typeof(Button));
        chatTaskTitle.Text=chatStep switch{0=>L.T("SelectAccount"),1=>L.T("AccountTasksTitle",activeChat?.Account??""),6=>L.T("RawTaskTitle",activeChat?.Account??""),2 or 4=>L.T("ReadingTaskTitle",activeChat?.Account??""),3=>L.T("ArchiveFinished"),5=>L.T("StoreMaintenance"),_=>""};
        chatTaskTitle.Visibility=chatStep<0?Visibility.Collapsed:Visibility.Visible;
        if(chatStep<0)BuildArchiveHome();else if(chatStep==5)BuildStores();else if(chatStep==4)BuildArchiveAccess();else if (chatStep == 0) BuildChatAccounts(); else if(chatStep==6)BuildRawBackup();else if (chatStep == 1) BuildChatTasks(); else if (chatStep == 2) BuildChatExport(); else BuildChatFinished();
        chatFooter.Children.Add(Button("QuickStart", () => ShowHelp(2))); Theme.Enter(chatContent);
    }
    private void BuildChatAccounts() {
        var grid = View.Grid(View.Auto, View.Auto, View.Auto, View.Fill); chatContent.Children.Add(grid);
        grid.Loaded+=async(_,_)=>{if(automaticDiscovery&&!discoveryStarted&&preferences.ChatAutoDiscover&&operation==null){discoveryStarted=true;await DiscoverChats(null);}};
        View.Add(grid, View.Text(L.T("AccountHelp"), color: "Muted"), 0);
        var actions = View.Row(AsyncButton("FindAccounts", () => DiscoverChats(null), true), AsyncButton("SelectAccountFolder", async () => {
            string path = View.Folder(this,purpose:"chat-account-select"); if (path != "") await DiscoverChats(path);
        }), Button("ChatSettings",ConfigureChats)); actions.Margin = new Thickness(0, 6, 0, 14); View.Add(grid, actions, 1);
        var measureAll=AsyncButton("ScanAccountSizes",()=>MeasureAccountSizes(false));measureAllAccounts=measureAll;measureAll.Name="measure_all_accounts";measureAll.IsEnabled=locations.Count>0;
        var measureSelected=AsyncButton("ScanSelectedAccountSize",()=>MeasureAccountSizes(true));measureSelected.Name="measure_selected_account";measureSelected.IsEnabled=false;
        actions.Children.Add(measureAll);actions.Children.Add(measureSelected);
        foreach(var button in actions.Children.OfType<Button>())button.Margin=new Thickness(0,0,8,6);
        chatList = View.Table(("Account", "Account", 0), ("Platform", "Platform", 120), ("AccountSize", "Size", 150), ("Path", "Path", 0));
        accountNotice = View.Text(L.T("DiscoveryHelp"), color: "Muted"); accountNotice.Name = "account_discovery_notice"; View.Add(grid, accountNotice, 2);
        chatList.Name = "chat_accounts"; chatList.ItemsSource = locations.Select(l => new AccountRow(l)).ToArray(); View.Add(grid, chatList, 3);
        var sizeColumn=(DataGridTextColumn)chatList.Columns.Single(c=>c.SortMemberPath=="Bytes");
        var sizeStyle=new Style(typeof(TextBlock),sizeColumn.ElementStyle);sizeStyle.Setters.Add(new Setter(ToolTipProperty,new System.Windows.Data.Binding("SizeDetails")));sizeColumn.ElementStyle=sizeStyle;
        chatNext = Button("Next", () => {
            if (chatList.SelectedItem is not AccountRow row) throw new InvalidOperationException(L.T("NoAccountSelected"));
            if (activeChat?.Directory != row.Location.Directory || activeChat?.Platform != row.Location.Platform) SetCapturedKey(null);
            activeChat = row.Location;directChatSource=false;
            string id=ChatArchiveProject.Identity(row.Location.Platform,row.Location.Directory);var project=archiveProjects.GetValueOrDefault(id)??new ChatArchiveProject(id,row.Location.Account,row.Location.Platform,row.Location.Directory,preferences.BackupRoot){ReadingRoot=preferences.ArchiveRoot};SaveArchive(project);pendingArchive=project;
            activeManifest=project.Manifest;readingDirectory=project.Reading;exportReport=null;chatStep=1;RenderChatStage();
        }, true); chatNext.Name = "chat_next"; chatNext.IsEnabled = false;
        chatList.SelectionChanged += (_, _) => chatNext.IsEnabled = chatList.SelectedItem is AccountRow; chatFooter.Children.Add(chatNext);
        chatList.SelectionChanged+=(_,_)=>measureSelected.IsEnabled=operation==null&&chatList.SelectedItem is AccountRow;
    }
    private async Task DiscoverChats(string? root) => await Run(async token => {
        var progress = Progress(); locations = await Task.Run(() => ChatDiscovery.Discover(root == null ? ChatPaths.DataRoots().Concat(preferences.ChatSearchRoots) : [root], progress, token, measureSize:false));
        chatList.ItemsSource = locations.Select(l => new AccountRow(l)).ToArray();
        if(measureAllAccounts!=null)measureAllAccounts.IsEnabled=locations.Count>0;
        if (locations.Count == 1) chatList.SelectedIndex = 0; status.Text = L.T(locations.Count == 0 ? "NoAccounts" : "SelectAccount");
        accountNotice.Text = L.T(locations.Count == 0 ? "DiscoveryEmpty" : "DiscoveryFound", locations.Count);
        operationView.AddLog(L.T("LogAccountsDone",locations.Count));
        if (locations.Count > 0) View.Sort(chatList, "Bytes");
    });
    private sealed record AccountRow(ChatLocation Location, bool Scanning=false) {
        public string Account => Location.Account; public string Platform => Location.Platform.ToString(); public string Path => Location.Directory;
        public long? Bytes => Location.AccountBytes;
        public string Size => Scanning?L.T("AccountSizeScanning",View.Size(Location.AccountBytes??0)):Location.AccountBytes is long bytes ? Location.SizeComplete ? View.Size(bytes) : L.T("AtLeastSize", View.Size(bytes)) : L.T("SizeNotScanned");
        public string SizeDetails=>L.T("AccountSizeDetails",View.Size(Location.DatabaseBytes),Location.AccountFiles)+"\n"+(Location.SizeScannedAt is {} time?L.T("AccountSizeMeasuredAt",time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")):L.T("AccountSizeNotMeasuredHelp"));
    }
    private void BuildRawBackup() {
        var account = Account();
        string id=ChatArchiveProject.Identity(account.Platform,account.Directory);
        backupInput = View.Input(archiveProjects.GetValueOrDefault(id)?.BackupRoot??preferences.BackupRoot);
        var destination = new DockPanel();
        var browse = Button("Browse", () => { string path = View.Folder(this, backupInput.Text); if (path != "") backupInput.Text = path; });
        browse.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); destination.Children.Add(browse); destination.Children.Add(backupInput);
        var fields=View.Stack(View.Text(L.T("RawBackupHeading"),18),View.Text(L.T("RawBackupTaskHelp"),color:"Muted"),View.Field("AccountFolderLabel",View.Input(account.Directory,true)),View.Field("BackupTo",destination),View.Field("ChatWorkers",ChatWorkerSelector("backup",()=>backupInput.Text)));
        chatContent.Children.Add(View.FormScroll(fields));
        fields.Children.Add(new Expander{Header=L.T("RawBackupStrategy"),Content=View.Text(L.T("RawBackupStrategyHelp"),color:"Muted"),Margin=new Thickness(0,8,0,0)});
        chatFooter.Children.Add(AsyncButton("StartRawBackup",()=>verifiedBackups.TryGetValue(account.Directory,out var previous)&&File.Exists(previous)?IncrementalBackupChat():BackupChat(),true));
        var choices=new ContextMenu();var options=Button("RawBackupOptions",()=>choices.IsOpen=true);options.ContextMenu=choices;choices.PlacementTarget=options;
        var full=new MenuItem{Header=L.T("FullBackupOnce")};full.Click+=async(_,_)=>{try{await BackupChat();}catch(Exception ex){Error(ex);}};choices.Items.Add(full);
        var choose=new MenuItem{Header=L.T("ChooseIncrementalBase")};choose.Click+=async(_,_)=>{try{await IncrementalBackupChat(true);}catch(Exception ex){Error(ex);}};choices.Items.Add(choose);chatFooter.Children.Add(options);
        chatFooter.Children.Add(Button("GoBack", () => { chatStep = 1; RenderChatStage(); }));
    }
    private void BuildChatExport() {
        bool encrypted = NeedsKey();
        var stack = View.Stack(View.Field(directChatSource?"AccountFolderLabel":"RawBackupLabel", View.Input(ArchiveDirectory(), true)));
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
            methods.Children.Add(Button("KeyLibrary",OpenKeyLibrary));
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
        var verify = new CheckBox { Visibility=directChatSource?Visibility.Collapsed:Visibility.Visible, Content = L.T("VerifyBackupBeforeExport"), IsChecked = verifyChatBackup, Margin = new Thickness(0, 12, 0, 4) };
        verify.Checked += (_, _) => verifyChatBackup = true;
        verify.Unchecked += (_, _) => verifyChatBackup = false;
        stack.Children.Add(View.Field("ChatWorkers",ChatWorkerSelector("export",()=>readingDirectory??preferences.BackupRoot)));
        stack.Children.Add(verify);
        var help=new Expander{Header=L.T("ExportHelpTitle"),Margin=new Thickness(0,10,0,0),HorizontalContentAlignment=HorizontalAlignment.Stretch,
            Content=View.Stack(View.Text(L.T("VerifyBackupHelp"),color:"Muted"),View.Text(L.T("UpdateReadingHelp"),color:"Muted"),View.Text(L.T("ResumeReadingHelp"),color:"Muted"))};
        stack.Children.Add(help);
        chatNext = AsyncButton(directChatSource?"ExportDirect":"ArchiveCreate", ExportChat, true); chatNext.Name = "chat_export"; chatNext.IsEnabled = !encrypted || capturedKey != null;
        chatResume = AsyncButton("ResumeReading", ResumeChat); chatResume.IsEnabled = chatNext.IsEnabled;
        chatUpdate = AsyncButton("UpdateReading", UpdateChatReading); chatUpdate.IsEnabled = chatNext.IsEnabled;

        chatFooter.Children.Add(chatNext); if(!directChatSource){chatFooter.Children.Add(chatResume); chatFooter.Children.Add(chatUpdate);} if(!directChatSource)chatFooter.Children.Add(Button("FinishRawOnly", () => {
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
            View.Text(L.T(failed ? "ReadingFailed" : readingDirectory == null ? "RawReady" : readingComplete ? "ReadingReady" : "ReadingPartial")), View.Field(directChatSource?"AccountFolderLabel":"RawBackupLabel", View.Input(ArchiveDirectory(), true)));
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
        chatContent.Children.Add(View.FormScroll(stack));
        chatFooter.Children.Add(Button("NewChatArchive", () => { chatStep = -1; SetCapturedKey(null); RenderChatStage(); }, true));
        chatFooter.Children.Add(Button("OpenBackup", () => OpenFolder(ArchiveDirectory())));
        if (readingDirectory != null && !failed) chatFooter.Children.Add(Button("OpenReading", () => new ChatReaderWindow(readingDirectory, dataRoot, preferences.ReduceMotion) { Owner = this }.ShowDialog()));
        if (exportReport != null && File.Exists(exportReport)) chatFooter.Children.Add(Button("OpenReport", () => OpenFile(exportReport)));
    }
}
