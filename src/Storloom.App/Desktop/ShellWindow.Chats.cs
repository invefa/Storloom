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
    private TabControl chatSections=null!;
    private Grid chatWorkspace=null!;
    private bool syncingChatSections;
    private WrapPanel archiveUtilities=null!;
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
        var layout = Page("chats", "ChatTitle", "ChatLead", View.Fill);
        LoadArchiveProjects();
        var host=new Grid();View.Add(layout,host,1);
        chatSections=new TabControl{Name="archive_sections",Background=Brushes.Transparent};
        foreach(var pair in new[]{("AccountArchivesTab","archive_home_tab"),("KeyLibrary","database_keys_tab"),("AccountClientsTab","account_clients_tab")})chatSections.Items.Add(new TabItem{Header=L.T(pair.Item1),Name=pair.Item2,Style=(Style)FindResource("SectionTab")});
        host.Children.Add(chatSections);
        var entries=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Left};archiveUtilities=entries;
        var importMenu=new ContextMenu();var import=Button("ConnectExisting",()=>importMenu.IsOpen=true);importMenu.PlacementTarget=import;import.ContextMenu=importMenu;
        void ImportEntry(string key,Action action){var entry=new MenuItem{Header=L.T(key)};entry.Click+=(_,_)=>Guard(action);importMenu.Items.Add(entry);}
        ImportEntry("OpenChatBackup",OpenChatBackup);ImportEntry("ConnectReader",ConnectReader);ImportEntry("StoreLoad",ConnectStore);entries.Children.Add(import);
        var more=new ContextMenu();var moreButton=Button("ArchiveTools",()=>more.IsOpen=true);more.PlacementTarget=moreButton;moreButton.ContextMenu=more;
        void Entry(string key,Action action){var entry=new MenuItem{Header=L.T(key)};entry.Click+=(_,_)=>Guard(action);more.Items.Add(entry);}
        Entry("StoreMaintenance",()=>{chatStep=5;RenderChatStage();});Entry("OpenReaderArchive",OpenReaderArchive);Entry("VaultManage",EncryptReaderArchive);entries.Children.Add(moreButton);
        chatWorkspace=View.Grid(View.Auto,View.Fill,View.Auto);chatWorkspace.Margin=new Thickness(0,12,0,0);
        chatTaskTitle=View.Text("",14);chatTaskTitle.Margin=new Thickness(0,6,0,12);View.Add(chatWorkspace,chatTaskTitle,0);
        chatContent=new Grid();var chatCard=View.Card(chatContent,14);chatCard.Name="chat_task_card";View.Add(chatWorkspace,chatCard,1);
        chatFooter=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};View.Add(chatWorkspace,chatFooter,2);
        chatSections.SelectionChanged+=(_,e)=>{
            if(syncingChatSections||!ReferenceEquals(e.Source,chatSections))return;
            chatStep=chatSections.SelectedIndex==1?7:chatSections.SelectedIndex==2?8:-1;SetCapturedKey(null);
            RenderChatStage();
        };
        RenderChatStage();
    }
    private void RenderChatStage() {
        if(readingRootInput!=null&&pendingArchive!=null)pendingArchive=pendingArchive with{ReadingRoot=readingRootInput.Text};readingRootInput=null;
        archivePassword?.Clear();archivePasswordConfirmation?.Clear();archivePassword=null;archivePasswordConfirmation=null;
        chatContent.Children.Clear(); chatFooter.Children.Clear(); backupInput = null; keyInput?.Clear(); keyInput = null; chatResume = null; chatUpdate = null;
        syncingChatSections=true;
        try{
            int selected=chatStep==7?1:chatStep==8?2:0;
            foreach(TabItem item in chatSections.Items)item.Content=null;
            chatSections.SelectedIndex=selected;((TabItem)chatSections.Items[selected]).Content=chatWorkspace;
        }finally{syncingChatSections=false;}
        chatTaskTitle.Text=chatStep switch{7=>"",8=>"",0=>L.T("SelectAccount"),6=>L.T("RawTaskTitle",activeChat?.Account??""),2 or 4=>L.T("ReadingTaskTitle",activeChat?.Account??""),3=>L.T("ArchiveFinished"),5=>L.T("StoreMaintenance"),_=>""};
        chatTaskTitle.Visibility=chatStep<0||chatStep is 7 or 8?Visibility.Collapsed:Visibility.Visible;
        if(chatStep==7)BuildDatabaseKeys();else if(chatStep==8)BuildAccountClientSettings();else if(chatStep<0)BuildArchiveHome();else if(chatStep==5)BuildStores();else if(chatStep==4)BuildArchiveAccess();else if (chatStep == 0) BuildChatAccounts(); else if(chatStep==6)BuildRawBackup(); else if (chatStep == 2) BuildChatExport(); else BuildChatFinished();
        chatFooter.Margin=chatStep<0||chatStep is 7 or 8?new Thickness(0):new Thickness(0,16,0,0);
        if(chatStep>=0&&chatStep is not (7 or 8))chatFooter.Children.Add(Button("QuickStart", () => ShowHelp(2))); Theme.Enter(chatContent);
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
        chatNext = Button("RegisterAccount", () => {
            if (chatList.SelectedItem is not AccountRow row) throw new InvalidOperationException(L.T("NoAccountSelected"));
            if (activeChat?.Directory != row.Location.Directory || activeChat?.Platform != row.Location.Platform) SetCapturedKey(null);
            activeChat = row.Location;directChatSource=false;
            string id=ChatArchiveProject.Identity(row.Location.Platform,row.Location.Directory);var project=archiveProjects.GetValueOrDefault(id)??new ChatArchiveProject(id,row.Location.Account,row.Location.Platform,row.Location.Directory,preferences.BackupRoot){ReadingRoot=preferences.ArchiveRoot,ReadingStorage=ChatStorageFormat.Compact,BackupStorage=ChatStorageFormat.Compact};SaveArchive(project);pendingArchive=project;
            activeManifest=project.Manifest;readingDirectory=project.Reading;exportReport=null;chatStep=-1;RenderChatStage();
        }, true); chatNext.Name = "chat_next"; chatNext.IsEnabled = false;
        chatList.SelectionChanged += (_, _) => chatNext.IsEnabled = chatList.SelectedItem is AccountRow; chatFooter.Children.Add(chatNext);
        chatList.SelectionChanged+=(_,_)=>measureSelected.IsEnabled=operation==null&&chatList.SelectedItem is AccountRow;
        chatFooter.Children.Add(Button("GoBack",()=>{chatStep=-1;RenderChatStage();}));
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
        if(!archiveProjects.ContainsKey(id))SaveArchive(new(id,account.Account,account.Platform,account.Directory,preferences.BackupRoot));
        backupInput = View.Input(archiveProjects.GetValueOrDefault(id)?.BackupRoot??preferences.BackupRoot);
        backupInput.IsReadOnly=PendingState(archiveProjects[id])!=null||archiveProjects[id].NativeArchive!=null;backupInput.ToolTip=archiveProjects[id].NativeArchive;
        var destination = new DockPanel();
        var browse = Button("Browse", () => { string path = View.Folder(this, backupInput.Text,purpose:"raw-backup-root"); if (path != "") backupInput.Text = path; });
        browse.IsEnabled=!backupInput.IsReadOnly;browse.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); destination.Children.Add(browse); destination.Children.Add(backupInput);
        var fields=View.Stack(View.Text(L.T("RawBackupHeading"),18),View.Text(L.T("RawBackupTaskHelp"),color:"Muted"),View.Field("AccountFolderLabel",View.Input(account.Directory,true)),View.Field("BackupTo",destination));
        fields.Children.Add(StorageSelector(archiveProjects[id],true,ChatWorkerSelector("backup",()=>backupInput.Text)));
        chatContent.Children.Add(View.FormScroll(fields));
        var strategy=View.Stack(View.Text(L.T("RawBackupStrategyHelp"),color:"Muted"));compactNativeEncryption=null;if(archiveProjects[id].BackupStorage==ChatStorageFormat.Compact&&archiveProjects[id].NativeArchive==null&&PendingState(archiveProjects[id])==null){compactNativeEncryption=new CheckBox{Content=L.T("CompactNativeEncrypt"),Margin=new Thickness(0,8,0,0)};strategy.Children.Add(compactNativeEncryption);}
        fields.Children.Add(new Expander{Header=L.T("RawBackupStrategy"),Content=strategy,Margin=new Thickness(0,8,0,0)});
        chatFooter.Children.Add(AsyncButton("StartRawBackup",()=>archiveProjects[id].BackupStorage==ChatStorageFormat.Compact?StartCompactNative(archiveProjects[id]):verifiedBackups.TryGetValue(account.Directory,out var previous)&&File.Exists(previous)?IncrementalBackupChat():BackupChat(),true));
        var choices=new ContextMenu();var options=Button("RawBackupOptions",()=>choices.IsOpen=true);options.ContextMenu=choices;choices.PlacementTarget=options;
        var full=new MenuItem{Header=L.T("FullBackupOnce")};full.Click+=async(_,_)=>{try{await BackupChat();}catch(Exception ex){Error(ex);}};choices.Items.Add(full);
        var choose=new MenuItem{Header=L.T("ChooseIncrementalBase")};choose.Click+=async(_,_)=>{try{await IncrementalBackupChat(true);}catch(Exception ex){Error(ex);}};choices.Items.Add(choose);options.Visibility=archiveProjects[id].BackupStorage==ChatStorageFormat.Compact?Visibility.Collapsed:Visibility.Visible;chatFooter.Children.Add(options);
        chatFooter.Children.Add(Button("GoBack", () => { chatStep = -1; RenderChatStage(); }));
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
        })); chatFooter.Children.Add(Button("GoBack", () => { chatStep = -1; RenderChatStage(); }));
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
