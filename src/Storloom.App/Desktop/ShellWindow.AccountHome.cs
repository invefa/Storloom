using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private sealed record HomeAccountRow(ChatArchiveProject? Project=null,StoreEntry? Store=null,string? ConnectedReader=null){
        public string? AccountAlias{get;init;}
        public string? LocationAlias{get;init;}
        public string DisplayAccount=>string.IsNullOrEmpty(AccountAlias)?Account:AccountAlias;
        public string AccountKey=>Project==null?"material:"+Id:"account:"+Project.Platform+":"+Account;
        public string Id=>Project?.Id??Store?.Directory??ConnectedReader!;
        public IReadOnlyList<HomeAccountRow>? Sources { get; init; }
        public int SourceNumber { get; init; }
        public string AccountLabel=>DisplayAccount+" / "+(Project?.Platform.ToString()??L.T("ConnectedReaderFormat"));
        public string SourceChoice=>(string.IsNullOrEmpty(LocationAlias)?L.T("HubSourceNumber",SourceNumber):LocationAlias)+" / "+Path.GetPathRoot(SourceLabel)+"..."+Path.DirectorySeparatorChar+Path.GetFileName(Path.GetDirectoryName(SourceLabel))+Path.DirectorySeparatorChar+Path.GetFileName(SourceLabel);
        public string SourceLabel=>Project?.Source??Reader??Account;
        public string GroupLabel=>DisplayAccount+"\n"+(string.IsNullOrEmpty(AccountAlias)?"":Account+" / ")+(Project?.Platform.ToString()??L.T("ConnectedReaderFormat"))+" / "+L.T("HubSourceCount",Sources?.Count??1);
        public string Account=>Project?.Account??Store?.Name??Path.GetFileName(ConnectedReader)??L.T("ConnectedReaderFormat");
        public bool NeedsStoreSync=>Store!=null&&Project?.Reading!=null&&(!Store.Complete||!Store.Source.Equals(Project.Reading,StringComparison.OrdinalIgnoreCase));
        public string? Reader=>Project?.Reading??Store?.Directory??ConnectedReader;
        public string Backup=>Project!=null?new ArchiveRow(Project).Backup:L.T("ArchiveNotYet");
        public string Reading=>Project!=null?new ArchiveRow(Project).Reading:Store?.State??L.T("ArchiveReady");
        public string Protection=>Store!=null?L.T("PackedReadingFormat"):Project!=null?new ArchiveRow(Project).Protection:L.T("ConnectedReaderFormat");
        public string Task=>Project?.TaskFile!=null?new ArchiveRow(Project).Task:NeedsStoreSync?L.T("PackedUpdateAvailable"):Project!=null&&!Directory.Exists(Project.Source)?L.T("AccountSourceOffline"):Project!=null?new ArchiveRow(Project).Task:Store?.State??L.T("ArchiveReady");
    }
    private HomeAccountRow? selectedHomeSource;
    private void BuildArchiveHome(){
        selectedHomeSource=null;
        LoadStores();var rows=new List<HomeAccountRow>();var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var project in archiveProjects.Values.OrderBy(p=>p.Account)){
            var store=stores.FirstOrDefault(s=>s.AccountId==project.Id||project.Reading!=null&&s.Source.Equals(project.Reading,StringComparison.OrdinalIgnoreCase));
            if(store!=null&&store.AccountId==null){store=store with{AccountId=project.Id};SaveStore(store);}
            if(store!=null)used.Add(store.Directory);rows.Add(new(project,store));
        }
        rows.AddRange(stores.Where(s=>!used.Contains(s.Directory)).Select(s=>new HomeAccountRow(Store:s)));
        string readers=Path.Combine(dataRoot,"connected-readers.json");if(File.Exists(readers))rows.AddRange(JsonFiles.Read<string[]>(readers).Where(p=>!rows.Any(r=>r.Reader?.Equals(p,StringComparison.OrdinalIgnoreCase)==true)).Select(p=>new HomeAccountRow(ConnectedReader:p)));
        var aliases=LoadAccountAliases();rows=rows.Select(r=>r with{AccountAlias=aliases.Accounts.GetValueOrDefault(r.AccountKey),LocationAlias=aliases.Sources.GetValueOrDefault(r.Id)}).ToList();
        var groups=rows.GroupBy(r=>r.AccountKey,StringComparer.Ordinal)
            .Select(g=>g.First() with{Sources=g.Select((r,index)=>r with{SourceNumber=index+1}).ToArray()}).ToArray();
        var grid=View.Grid(View.Auto,View.Fill);chatContent.Children.Add(grid);
        var add=Button("ArchiveCreate",()=>{chatStep=0;activeChat=null;pendingArchive=null;SetCapturedKey(null);RenderChatStage();});add.Name="account_add";
        var toolbar=View.Row(add);if(archiveUtilities.Parent is Panel old)old.Children.Remove(archiveUtilities);archiveUtilities.Margin=new Thickness(0,0,8,0);foreach(var b in archiveUtilities.Children.OfType<Button>())b.Margin=new Thickness(0,3,8,3);toolbar.Children.Add(archiveUtilities);toolbar.Children.Add(View.IconButton("M 6,0 A 6,6 0 1 1 5.9,0 M 4,4 Q 4,1 7,2 Q 10,4 6,6 L 6,8 M 6,10 L 6,11","QuickStart",()=>ShowHelp(2)));View.Add(grid,toolbar,0);toolbar.Margin=new Thickness(0,0,0,12);
        var split=new Grid();split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(238)});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(14)});split.ColumnDefinitions.Add(new ColumnDefinition());View.Add(grid,split,1);
        archiveTable=View.Table(("Account","GroupLabel",0));archiveTable.RowHeight=56;archiveTable.Name="managed_archives";archiveTable.ItemsSource=groups;
        var accountStyle=new Style(typeof(TextBlock));accountStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.NoWrap));accountStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));accountStyle.Setters.Add(new Setter(TextBlock.MarginProperty,new Thickness(0,6,0,6)));((DataGridTextColumn)archiveTable.Columns[0]).ElementStyle=accountStyle;View.Add(split,archiveTable,0);
        var right=View.Grid(View.Auto,View.Auto,View.Auto,View.Fill);View.Add(split,right,0,2);
        var picker=new ComboBox{ItemsSource=groups,DisplayMemberPath="AccountLabel",Name="compact_account_selector",Margin=new Thickness(0,0,0,10),Visibility=Visibility.Collapsed};View.Add(right,picker,0);
        var sourcePicker=new ComboBox{Name="account_source_selector",DisplayMemberPath="SourceChoice",MinHeight=32,Margin=new Thickness(0,0,0,6)};
        var sourcePath=View.Text("",12,"Muted");sourcePath.Name="account_source_path";sourcePath.Margin=new Thickness(0,0,0,10);
        var sourceHeading=View.Text(L.T("HubDataSource"),13);sourceHeading.Margin=new Thickness(0,0,0,6);
        sourceHeading.ToolTip=L.T("HubSourceScope");
        var editAliases=View.IconButton("M 1,10 L 1,13 L 4,13 L 13,4 L 10,1 Z M 8,3 L 11,6","AccountAliases",EditAccountAliases);editAliases.Name="account_aliases";editAliases.Margin=new Thickness(10,0,0,4);editAliases.IsEnabled=false;
        var headingRow=new DockPanel();DockPanel.SetDock(editAliases,Dock.Right);headingRow.Children.Add(editAliases);headingRow.Children.Add(sourceHeading);
        var sourceHeader=View.Stack(headingRow,sourcePicker,sourcePath);View.Add(right,sourceHeader,1);
        var taskBanner=new StackPanel();View.Add(right,taskBanner,2);
        var materialTabs=new TabControl{Name="account_material_tabs",Background=Brushes.Transparent};View.Add(right,materialTabs,3);
        var readingDetails=new StackPanel{Margin=new Thickness(2,4,2,2)};var backupDetails=new StackPanel{Margin=new Thickness(2,4,2,2)};
        var scroll=View.FormScroll(readingDetails);scroll.Name="account_details_scroll";
        materialTabs.Items.Add(new TabItem{Header=L.T("ReadingTaskHeading"),Content=scroll,Style=(Style)FindResource("SectionTab")});
        materialTabs.Items.Add(new TabItem{Header=L.T("RawBackupHeading"),Content=View.FormScroll(backupDetails),Style=(Style)FindResource("SectionTab")});
        var historyTab=new TabItem{Header=L.T("HubHistory"),Style=(Style)FindResource("SectionTab")};materialTabs.Items.Add(historyTab);
        materialTabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,materialTabs)&&selectedHomeSource!=null)materialTabsBySource[selectedHomeSource.Id]=materialTabs.SelectedIndex;if(ReferenceEquals(e.Source,materialTabs)&&materialTabs.SelectedItem==historyTab&&selectedHomeSource!=null)historyTab.Content=BuildAccountHistory(selectedHomeSource);};
        var details=readingDetails;
        bool changingSource=false;
        picker.SelectionChanged+=(_,_)=>{if(picker.SelectedItem!=null)archiveTable.SelectedItem=picker.SelectedItem;};
        split.SizeChanged+=(_,_)=>{bool compact=split.ActualWidth<620;split.ColumnDefinitions[0].Width=new GridLength(compact?0:238);split.ColumnDefinitions[1].Width=new GridLength(compact?0:14);archiveTable.Visibility=compact?Visibility.Collapsed:Visibility.Visible;picker.Visibility=compact?Visibility.Visible:Visibility.Collapsed;};
        void Selection(bool resetSource=true){
            if(resetSource){changingSource=true;var choices=(archiveTable.SelectedItem as HomeAccountRow)?.Sources;sourcePicker.ItemsSource=choices;sourcePicker.SelectedItem=choices?.FirstOrDefault(r=>r.Id==selectedArchiveId)??choices?.FirstOrDefault();changingSource=false;}
            readingDetails.Children.Clear();backupDetails.Children.Clear();taskBanner.Children.Clear();historyTab.Content=null;details=readingDetails;selectedHomeSource=null;if(sourcePicker.SelectedItem is not HomeAccountRow item){details.Children.Add(View.Text(L.T("ArchiveSelect"),color:"Muted"));return;}
            selectedHomeSource=item;selectedArchiveId=item.Id;picker.SelectedItem=archiveTable.SelectedItem;var project=item.Project;
            editAliases.IsEnabled=true;sourceHeading.Text=(string.IsNullOrEmpty(item.LocationAlias)?L.T("HubDataSource"):item.LocationAlias)+" / "+L.T(!Directory.Exists(project?.Source??item.Reader)?"AccountSourceOffline":"ArchiveReady");sourcePath.Text=item.SourceLabel;sourcePicker.ToolTip=item.SourceLabel;
            sourcePicker.Visibility=(archiveTable.SelectedItem as HomeAccountRow)?.Sources?.Count>1?Visibility.Visible:Visibility.Collapsed;
            picker.ToolTip=project?.Source??item.Reader;bool pending=project!=null&&PendingState(project)!=null;bool readable=item.Reader!=null&&Directory.Exists(item.Reader);bool local=project!=null&&Directory.Exists(project.Source);
            bool compactReader=readable&&ArchiveStore.Exists(item.Reader!);bool encrypted=false,directKey=false;try{encrypted=project?.Reading!=null?(compactReader?ArchiveStore.NeedsPassword(project.Reading):ChatVault.Exists(project.Reading)):item.Store!=null?ArchiveStore.NeedsPassword(item.Store.Directory):item.ConnectedReader!=null&&ChatVault.Exists(item.ConnectedReader);if(encrypted)directKey=(compactReader?ArchiveStore.ReadKeyMode(item.Reader!):ChatVault.ReadKeyMode(item.Reader!))==ChatVaultKeyMode.DirectKey;}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){readable=false;}
            void Section(string title,string status,params Button[] actions){var caption=View.Text(L.T(title),14);caption.Margin=new Thickness(0,0,0,3);var hint=View.Text(status,12,"Muted");hint.TextWrapping=TextWrapping.NoWrap;hint.TextTrimming=TextTrimming.CharacterEllipsis;hint.ToolTip=status;hint.Margin=new Thickness(0,0,0,4);var group=View.Stack(caption,hint,View.Row(actions));group.Margin=new Thickness(0,0,0,10);if(pending&&title is "HubReadingProtection" or "RawBackupHeading")foreach(var b in actions)b.Visibility=Visibility.Collapsed;if(title is "ReadingTaskHeading" or "RawBackupHeading" or "HubPackedBackup")caption.Visibility=Visibility.Collapsed;details.Children.Add(group);}
            if(pending){details=taskBanner;var task=PendingState(project!)!;var resume=AsyncButton("ArchiveContinue",()=>PrepareArchiveUpdate(project!));resume.Name="account_continue";var abandon=Button("ArchiveAbandonTask",()=>AbandonAccountTask(project!));abandon.Name="account_abandon";Section("HubCurrentTask",L.T("HubTaskStage",TaskStage(task)),resume,abandon);details=readingDetails;}
            var open=PreparationButton(readable?"OpenReading":"HubCreateReading",async()=>{if(readable){if(project!=null)MigrateArchiveReaderPreferences(project);new ChatReaderWindow(item.Reader!,dataRoot,preferences.ReduceMotion,project?.Id){Owner=this}.Show();}else if(project!=null)await PrepareReadingTask(project);},true);open.Name="archive_open";open.IsEnabled=readable||local&&!pending;
            var update=PreparationButton(item.Store!=null&&project==null?"SyncPackedReading":"UpdateReading",async()=>{if(project!=null)await PrepareReadingTask(project);else if(item.Store!=null)await MigrateStore(item.Store.Source,item.Store.Directory);});update.Name="archive_update";update.IsEnabled=!pending&&(project!=null&&project.Reading!=null&&local||project==null&&item.Store!=null);
            var fromBackup=AsyncButton("ReadingFromBackup",()=>PrepareReadingTask(project!,true));fromBackup.Name="account_from_backup";fromBackup.IsEnabled=!pending&&(project?.Manifest!=null||project?.NativeArchive!=null);fromBackup.Visibility=project?.Manifest!=null||project?.NativeArchive!=null?Visibility.Visible:Visibility.Collapsed;
            var export=AsyncButton("StoreExportReader",()=>RunStoreAction("export",item.Store??new StoreEntry("",item.Reader!,"",true)));export.Visibility=compactReader||item.Store!=null&&project==null?Visibility.Visible:Visibility.Collapsed;
            update.Visibility=readable?Visibility.Visible:Visibility.Collapsed;
            Section("ReadingTaskHeading",readable?item.Reading+(compactReader?" / "+L.T("StorageCompact"):""):L.T("ArchiveReadingPending"),open,update,fromBackup,export);
            var protect=AsyncButton(encrypted?"HubAdjustProtection":"HubEncrypt",async()=>{if(project!=null)await ConfigureArchiveProtection(project);else if(item.Store!=null)await RunStoreAction("password",item.Store);else await ProtectConnectedReader(item.Reader!);});protect.Name="archive_protection";if(compactReader&&encrypted)protect.Visibility=Visibility.Collapsed;protect.IsEnabled=readable&&!pending&&(project!=null||item.Store==null||encrypted);
            if(item.Store!=null&&project==null)protect.Content=L.T("StoreChangePassword");
            var decrypt=AsyncButton("HubDecrypt",async()=>{if(project!=null)await RemoveArchiveProtection(project);else await DecryptConnectedReader(item.Reader!);});decrypt.Name="archive_decrypt";decrypt.IsEnabled=readable&&encrypted&&!pending&&(project!=null||item.Store==null);
            if(project!=null&&project.Reading==null){

                var preset=new CheckBox{Content=L.T("HubEncryptWhenCreated"),IsChecked=project.Protection!=ChatArchiveProtectionMode.None,Margin=new Thickness(0,0,0,10),IsEnabled=!pending};preset.Click+=(_,_)=>Guard(()=>{SaveArchive(project with{Protection=preset.IsChecked==true?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None});chatStep=-1;RenderChatStage();});preset.ToolTip=L.T("HubProtectionPresetHelp");details.Children.Add(preset);
            }else {
                var change=AsyncButton("ReadingChangePassword",()=>ChangeReadingPassword(item.Reader!));change.Name="archive_change_password";change.IsEnabled=readable&&encrypted&&!pending&&!directKey;change.ToolTip=L.T(directKey?"VaultDirectKeyFixed":"ReadingPasswordChangeHelp");
                change.Visibility=encrypted&&(project!=null||item.Store==null)?Visibility.Visible:Visibility.Collapsed;
                Section("HubReadingProtection",project!=null?new ArchiveRow(project).Protection:L.T(encrypted?"ArchiveProtectionAll":"ArchiveProtectionNone"),protect,decrypt,change);
            }
            var backup=PreparationButton(project==null?"StoreBackup":"StartRawBackup",async()=>{if(project!=null)await PrepareRawTask(project);else if(item.Store!=null)await RunStoreAction("backup",item.Store);});backup.Name="archive_backup";backup.IsEnabled=!pending&&(local||project==null&&item.Store!=null);
            var verify=AsyncButton("VerifyBackupNow",async()=>{if(project!=null)await VerifyArchiveBackup(project);else if(item.Store!=null)await RunStoreAction("verify",item.Store);});verify.Name="archive_verify_now";verify.IsEnabled=project?.Manifest!=null||project?.NativeArchive!=null||item.Store!=null;
            var restore=AsyncButton("RestoreChatBackup",()=>project!.NativeArchive!=null?RestoreCompactNative(project):RestoreChatBackupFrom(project.Manifest));restore.Name="archive_restore";restore.IsEnabled=project?.Manifest!=null||project?.NativeArchive!=null;
            verify.Visibility=project?.Manifest!=null||project?.NativeArchive!=null||item.Store!=null?Visibility.Visible:Visibility.Collapsed;restore.Visibility=project?.Manifest!=null||project?.NativeArchive!=null?Visibility.Visible:Visibility.Collapsed;
            details=backupDetails;
            var copy=AsyncButton("CompactCopy",()=>RunStoreAction("backup",new StoreEntry("",project!.NativeArchive!,"",true)));copy.Visibility=project?.NativeArchive!=null?Visibility.Visible:Visibility.Collapsed;
            var nativeStats=AsyncButton("CompactStatistics",()=>CompactStatistics(project!.NativeArchive!));nativeStats.Visibility=project?.NativeArchive!=null?Visibility.Visible:Visibility.Collapsed;
            Section(project==null&&item.Store!=null?"HubPackedBackup":"RawBackupHeading",item.Backup+(project?.NativeArchive!=null?" / "+L.T("StorageCompact"):""),backup,verify,restore,copy,nativeStats);

            var format=AsyncButton(item.NeedsStoreSync?"SyncPackedReading":"ReadingStorageFormat",()=>MigrateStore(project?.Reading??item.Reader,item.NeedsStoreSync?item.Store?.Directory:null));format.IsEnabled=readable&&!pending&&(item.Store==null||item.NeedsStoreSync);
            var packed=Button("HubOpenPacked",()=>new ChatReaderWindow(item.Store!.Directory,dataRoot,preferences.ReduceMotion,project?.Id){Owner=this}.Show());packed.Visibility=item.Store!=null?Visibility.Visible:Visibility.Collapsed;packed.IsEnabled=item.Store?.Complete==true&&!item.NeedsStoreSync;
            format.Visibility=compactReader?Visibility.Collapsed:Visibility.Visible;
            var statistics=AsyncButton("CompactStatistics",()=>CompactStatistics(item.Reader!));statistics.Visibility=compactReader?Visibility.Visible:Visibility.Collapsed;
            var reorganize=AsyncButton("CompactRepack",()=>RepackCompact(project!));reorganize.Visibility=compactReader&&project!=null?Visibility.Visible:Visibility.Collapsed;reorganize.IsEnabled=!pending;
            var fresh=AsyncButton("CompactCreateFresh",()=>PrepareArchiveUpdate(project!,new(DirectReading:true){CreateCompact=true}));fresh.Visibility=project!=null&&readable&&!compactReader&&local?Visibility.Visible:Visibility.Collapsed;fresh.IsEnabled=!pending;fresh.ToolTip=L.T("CompactFreshHelp");
            var advancedActions=View.Row(format,packed,statistics,reorganize,fresh);advancedActions.Margin=new Thickness(0,8,0,0);
            var advanced=new Expander{Header=L.T("HubAdvanced"),Content=advancedActions,Margin=new Thickness(0,6,0,4),Visibility=readable?Visibility.Visible:Visibility.Collapsed};
            readingDetails.Children.Add(advanced);materialTabs.SelectedIndex=materialTabsBySource.GetValueOrDefault(item.Id);if(materialTabs.SelectedItem==historyTab)historyTab.Content=BuildAccountHistory(item);
        }
        sourcePicker.SelectionChanged+=(_,_)=>{if(!changingSource)Selection(false);};
        archiveTable.SelectionChanged+=(_,_)=>Selection();archiveTable.SelectedItem=groups.FirstOrDefault(g=>g.Sources!.Any(r=>r.Id==selectedArchiveId))??groups.FirstOrDefault();Selection();
    }
    private static string TaskStage(ChatArchiveTask task)=>L.T(task.Phase switch{"source"=>"ChatSourceLock","backup"=>"PhaseArchiveBackup","workspace"=>"PhaseArchiveWorkspace","reading"=>"PhaseArchiveReading","protection"=>"PhaseArchiveProtect","publish" or "published"=>"PhaseArchivePublish",_=>"ArchiveContinue"});
    private sealed record MaterialHistoryRow(string Kind,string Path,string? Manifest,DateTime Updated){public string Time=>Updated.ToString("yyyy-MM-dd HH:mm");}
    private FrameworkElement BuildAccountHistory(HomeAccountRow account){
        if(account.Project is not {} project)return View.Text(L.T("HubHistoryUnavailable"),color:"Muted");var rows=new List<MaterialHistoryRow>();var manifests=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var origin in archiveProjects.Values.Where(p=>p.Platform==project.Platform&&p.Account==project.Account)){if(origin.Manifest!=null&&File.Exists(origin.Manifest))manifests.Add(origin.Manifest);string parent=Path.Combine(origin.BackupRoot,origin.Platform.ToString(),origin.Account);if(Directory.Exists(parent))foreach(string file in Directory.GetFiles(parent,"*.storloom-*.json"))manifests.Add(file);}
        rows.AddRange(manifests.Select(path=>new MaterialHistoryRow(L.T("RawBackupHeading"),path,path,File.GetLastWriteTime(path))));
        var readings=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var origin in archiveProjects.Values.Where(p=>p.Platform==project.Platform&&p.Account==project.Account&&p.Reading!=null)){string parent=Path.GetDirectoryName(origin.Reading)!;if(Directory.Exists(parent))foreach(string path in Directory.GetDirectories(parent,"reading-*").Append(origin.Reading!))if(Directory.Exists(path)&&(File.Exists(Path.Combine(path,"messages.html"))||ChatVault.Exists(path)||ArchiveStore.Exists(path)&&File.Exists(Path.Combine(path,"HEAD"))))readings.Add(path);}
        rows.AddRange(readings.Select(p=>new MaterialHistoryRow(L.T("ReadingTaskHeading"),p,null,Directory.GetLastWriteTime(p))));
        var layout=View.Grid(View.Auto,View.Fill,View.Auto);layout.Margin=new Thickness(2,10,2,2);
        View.Add(layout,View.Text(L.T("HubHistoryScope"),color:"Muted"),0);var table=View.Table(("MaterialKind","Kind",110),("Updated","Time",140),("Path","Path",0));table.ItemsSource=rows.OrderByDescending(r=>r.Updated).ToArray();View.Add(layout,table,1);
        var read=View.Button("OpenReading",()=>{if(table.SelectedItem is MaterialHistoryRow {Manifest:null} row)new ChatReaderWindow(row.Path,dataRoot,preferences.ReduceMotion){Owner=this}.Show();});
        var restore=View.Button("RestoreChatBackup",async()=>{if(table.SelectedItem is MaterialHistoryRow {Manifest:{} manifest}){await RestoreChatBackupFrom(manifest);}});
        table.SelectionChanged+=(_,_)=>{read.IsEnabled=table.SelectedItem is MaterialHistoryRow{Manifest:null};restore.IsEnabled=table.SelectedItem is MaterialHistoryRow{Manifest:not null};};read.IsEnabled=restore.IsEnabled=false;
        var actions=View.Row(read,restore);actions.HorizontalAlignment=HorizontalAlignment.Right;actions.Margin=new Thickness(0,14,0,0);View.Add(layout,actions,2);return layout;
    }
}
