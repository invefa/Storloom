using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private sealed record HomeAccountRow(ChatArchiveProject? Project=null,StoreEntry? Store=null,string? ConnectedReader=null){
        public string Id=>Project?.Id??Store?.Directory??ConnectedReader!;
        public string Account=>Project?.Account??Store?.Name??Path.GetFileName(ConnectedReader)??L.T("ConnectedReaderFormat");
        public bool NeedsStoreSync=>Store!=null&&Project?.Reading!=null&&(!Store.Complete||!Store.Source.Equals(Project.Reading,StringComparison.OrdinalIgnoreCase));
        public string? Reader=>Project?.Reading??Store?.Directory??ConnectedReader;
        public string Backup=>Project!=null?new ArchiveRow(Project).Backup:L.T("ArchiveNotYet");
        public string Reading=>Project!=null?new ArchiveRow(Project).Reading:Store?.State??L.T("ArchiveReady");
        public string Protection=>Store!=null?L.T("PackedReadingFormat"):Project!=null?new ArchiveRow(Project).Protection:L.T("ConnectedReaderFormat");
        public string Task=>Project?.TaskFile!=null?new ArchiveRow(Project).Task:NeedsStoreSync?L.T("PackedUpdateAvailable"):Project!=null&&!Directory.Exists(Project.Source)?L.T("AccountSourceOffline"):Project!=null?new ArchiveRow(Project).Task:Store?.State??L.T("ArchiveReady");
    }
    private HomeAccountRow SelectedHomeAccount()=>archiveTable?.SelectedItem as HomeAccountRow??throw new InvalidOperationException(L.T("ArchiveSelect"));
    private void BuildArchiveHome(){
        LoadStores();var rows=new List<HomeAccountRow>();var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var project in archiveProjects.Values.OrderBy(p=>p.Account)){
            var store=stores.FirstOrDefault(s=>s.AccountId==project.Id||project.Reading!=null&&s.Source.Equals(project.Reading,StringComparison.OrdinalIgnoreCase));
            if(store!=null&&store.AccountId==null){store=store with{AccountId=project.Id};SaveStore(store);}
            if(store!=null)used.Add(store.Directory);rows.Add(new(project,store));
        }
        rows.AddRange(stores.Where(s=>!used.Contains(s.Directory)).Select(s=>new HomeAccountRow(Store:s)));
        string readers=Path.Combine(dataRoot,"connected-readers.json");if(File.Exists(readers))rows.AddRange(JsonFiles.Read<string[]>(readers).Where(p=>!rows.Any(r=>r.Reader?.Equals(p,StringComparison.OrdinalIgnoreCase)==true)).Select(p=>new HomeAccountRow(ConnectedReader:p)));
        var grid=View.Grid(View.Auto,View.Fill);chatContent.Children.Add(grid);
        var add=Button("ArchiveCreate",()=>{chatStep=0;activeChat=null;pendingArchive=null;SetCapturedKey(null);RenderChatStage();});add.Name="account_add";
        var toolbar=View.Row(add);if(archiveUtilities.Parent is Panel old)old.Children.Remove(archiveUtilities);toolbar.Children.Add(archiveUtilities);toolbar.Children.Add(View.IconButton("M 6,0 A 6,6 0 1 1 5.9,0 M 4,4 Q 4,1 7,2 Q 10,4 6,6 L 6,8 M 6,10 L 6,11","QuickStart",()=>ShowHelp(2)));View.Add(grid,toolbar,0);toolbar.Margin=new Thickness(0,0,0,12);
        var split=new Grid();split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(210)});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(14)});split.ColumnDefinitions.Add(new ColumnDefinition());View.Add(grid,split,1);
        archiveTable=View.Table(("Account","Account",0));archiveTable.Name="managed_archives";archiveTable.ItemsSource=rows;View.Add(split,archiveTable,0);
        var right=View.Grid(View.Auto,View.Fill);View.Add(split,right,0,2);
        var picker=new ComboBox{ItemsSource=rows,DisplayMemberPath="Account",Name="compact_account_selector",Margin=new Thickness(0,0,0,10),Visibility=Visibility.Collapsed};View.Add(right,picker,0);
        var details=new StackPanel();var scroll=View.FormScroll(details);scroll.Name="account_details_scroll";View.Add(right,scroll,1);
        picker.SelectionChanged+=(_,_)=>{if(picker.SelectedItem!=null)archiveTable.SelectedItem=picker.SelectedItem;};
        split.SizeChanged+=(_,_)=>{bool compact=split.ActualWidth<620;split.ColumnDefinitions[0].Width=new GridLength(compact?0:210);split.ColumnDefinitions[1].Width=new GridLength(compact?0:14);archiveTable.Visibility=compact?Visibility.Collapsed:Visibility.Visible;picker.Visibility=compact?Visibility.Visible:Visibility.Collapsed;};
        void Selection(){
            details.Children.Clear();if(archiveTable.SelectedItem is not HomeAccountRow item){details.Children.Add(View.Text(L.T("ArchiveSelect"),color:"Muted"));return;}
            selectedArchiveId=item.Id;picker.SelectedItem=item;var project=item.Project;
            picker.ToolTip=project?.Source??item.Reader;bool pending=project!=null&&PendingState(project)!=null;bool readable=item.Reader!=null&&Directory.Exists(item.Reader);bool local=project!=null&&Directory.Exists(project.Source);
            bool encrypted=false;try{encrypted=project?.Reading!=null?ChatVault.Exists(project.Reading):item.Store!=null?ArchiveStore.NeedsPassword(item.Store.Directory):item.ConnectedReader!=null&&ChatVault.Exists(item.ConnectedReader);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){readable=false;}
            void Section(string title,string status,params Button[] actions){var caption=View.Text(L.T(title),14);caption.Margin=new Thickness(0,0,0,3);var hint=View.Text(status,12,"Muted");hint.TextWrapping=TextWrapping.NoWrap;hint.TextTrimming=TextTrimming.CharacterEllipsis;hint.ToolTip=status;hint.Margin=new Thickness(0,0,0,4);var group=View.Stack(caption,hint,View.Row(actions));group.Margin=new Thickness(0,0,0,10);if(pending&&title is "HubReadingProtection" or "RawBackupHeading")foreach(var b in actions)b.Visibility=Visibility.Collapsed;details.Children.Add(group);}
            if(pending){var task=PendingState(project!)!;var resume=AsyncButton("ArchiveContinue",()=>PrepareArchiveUpdate(project!));resume.Name="account_continue";var abandon=Button("ArchiveAbandonTask",()=>AbandonAccountTask(project!));abandon.Name="account_abandon";Section("HubCurrentTask",L.T("HubTaskStage",TaskStage(task)),resume,abandon);}
            var open=AsyncButton(readable?"OpenReading":"HubCreateReading",async()=>{if(readable){if(project!=null)MigrateArchiveReaderPreferences(project);new ChatReaderWindow(item.Reader!,dataRoot,preferences.ReduceMotion,project?.Id){Owner=this}.Show();}else if(project!=null)await PrepareReadingTask(project);},true);open.Name="archive_open";open.IsEnabled=readable||local&&!pending;
            var update=AsyncButton(item.Store!=null&&project==null?"SyncPackedReading":"UpdateReading",async()=>{if(project!=null)await PrepareReadingTask(project);else if(item.Store!=null)await MigrateStore(item.Store.Source,item.Store.Directory);});update.Name="archive_update";update.IsEnabled=!pending&&(project!=null&&project.Reading!=null&&local||project==null&&item.Store!=null);
            var fromBackup=AsyncButton("ReadingFromBackup",()=>PrepareReadingTask(project!,true));fromBackup.Name="account_from_backup";fromBackup.IsEnabled=!pending&&project?.Manifest!=null;fromBackup.Visibility=project?.Manifest!=null?Visibility.Visible:Visibility.Collapsed;
            var export=AsyncButton("StoreExportReader",()=>RunStoreAction("export",item.Store));export.Visibility=item.Store!=null&&project==null?Visibility.Visible:Visibility.Collapsed;
            Section("ReadingTaskHeading",readable?item.Reading+" | "+(project?.Source??item.Reader)+" | "+item.Reader:L.T("ArchiveReadingPending"),open,update,fromBackup,export);
            var protect=AsyncButton(encrypted?"HubAdjustProtection":"HubEncrypt",async()=>{if(project!=null)await ConfigureArchiveProtection(project);else if(item.Store!=null)await RunStoreAction("password",item.Store);else await ProtectConnectedReader(item.Reader!);});protect.Name="archive_protection";protect.IsEnabled=readable&&!pending&&(project!=null||item.Store==null||encrypted);
            if(item.Store!=null&&project==null)protect.Content=L.T("StoreChangePassword");
            var decrypt=AsyncButton("HubDecrypt",async()=>{if(project!=null)await RemoveArchiveProtection(project);else await DecryptConnectedReader(item.Reader!);});decrypt.Name="archive_decrypt";decrypt.IsEnabled=readable&&encrypted&&!pending&&(project!=null||item.Store==null);
            if(project!=null&&project.Reading==null){
                Section("HubReadingProtection",new ArchiveRow(project).Protection);
                var preset=new CheckBox{Content=L.T("HubEncryptWhenCreated"),IsChecked=project.Protection!=ChatArchiveProtectionMode.None,Margin=new Thickness(0,0,0,10),IsEnabled=!pending};preset.Click+=(_,_)=>Guard(()=>{SaveArchive(project with{Protection=preset.IsChecked==true?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None});chatStep=-1;RenderChatStage();});details.Children.Add(preset);
            }else Section("HubReadingProtection",project!=null?new ArchiveRow(project).Protection:L.T(encrypted?"ArchiveProtectionAll":"ArchiveProtectionNone"),protect,decrypt);
            var backup=AsyncButton(project==null?"StoreBackup":"StartRawBackup",async()=>{if(project!=null)PrepareRawTask(project);else if(item.Store!=null)await RunStoreAction("backup",item.Store);});backup.Name="archive_backup";backup.IsEnabled=!pending&&(local||project==null&&item.Store!=null);
            var verify=AsyncButton("VerifyBackupNow",async()=>{if(project!=null)await VerifyArchiveBackup(project);else if(item.Store!=null)await RunStoreAction("verify",item.Store);});verify.Name="archive_verify_now";verify.IsEnabled=project?.Manifest!=null||item.Store!=null;
            var restore=AsyncButton("RestoreChatBackup",()=>RestoreChatBackupFrom(project!.Manifest));restore.Name="archive_restore";restore.IsEnabled=project?.Manifest!=null;
            Section(project==null&&item.Store!=null?"HubPackedBackup":"RawBackupHeading",item.Backup,backup,verify,restore);

            var history=Button("AccountHistory",()=>ShowAccountHistory(item));history.Name="account_history";history.IsEnabled=project!=null;
            var format=AsyncButton(item.NeedsStoreSync?"SyncPackedReading":"ReadingStorageFormat",()=>MigrateStore(project?.Reading??item.Reader,item.NeedsStoreSync?item.Store?.Directory:null));format.IsEnabled=readable&&!pending&&(item.Store==null||item.NeedsStoreSync);
            var packed=Button("HubOpenPacked",()=>new ChatReaderWindow(item.Store!.Directory,dataRoot,preferences.ReduceMotion,project?.Id){Owner=this}.Show());packed.Visibility=item.Store!=null?Visibility.Visible:Visibility.Collapsed;packed.IsEnabled=item.Store?.Complete==true&&!item.NeedsStoreSync;
            var advanced=new Expander{Header=L.T("HubAdvanced"),Content=View.Row(format,packed),Margin=new Thickness(0,4,0,0)};
            details.Children.Add(View.Row(history,advanced));
        }
        archiveTable.SelectionChanged+=(_,_)=>Selection();archiveTable.SelectedItem=rows.FirstOrDefault(r=>r.Id==selectedArchiveId)??rows.FirstOrDefault();Selection();
    }
    private static string TaskStage(ChatArchiveTask task)=>L.T(task.Phase switch{"source"=>"ChatSourceLock","backup"=>"PhaseArchiveBackup","workspace"=>"PhaseArchiveWorkspace","reading"=>"PhaseArchiveReading","protection"=>"PhaseArchiveProtect","publish" or "published"=>"PhaseArchivePublish",_=>"ArchiveContinue"});
    private sealed record MaterialHistoryRow(string Kind,string Path,string? Manifest,DateTime Updated){public string Time=>Updated.ToString("yyyy-MM-dd HH:mm");}
    private void ShowAccountHistory(HomeAccountRow account){
        var project=account.Project!;var rows=new List<MaterialHistoryRow>();var manifests=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(project.Manifest!=null&&File.Exists(project.Manifest)){manifests.Add(project.Manifest);string parent=Path.Combine(project.BackupRoot,project.Platform.ToString(),project.Account);if(Directory.Exists(parent))foreach(string file in Directory.GetFiles(parent,"*.storloom-*.json"))manifests.Add(file);}
        rows.AddRange(manifests.Select(path=>new MaterialHistoryRow(L.T("RawBackupHeading"),path,path,File.GetLastWriteTime(path))));
        if(project.Reading!=null){string parent=Path.GetDirectoryName(project.Reading)!;if(Directory.Exists(parent))rows.AddRange(Directory.GetDirectories(parent,"reading-*").Where(p=>File.Exists(Path.Combine(p,"messages.html"))||ChatVault.Exists(p)).Append(project.Reading).Distinct(StringComparer.OrdinalIgnoreCase).Select(p=>new MaterialHistoryRow(L.T("ReadingTaskHeading"),p,null,Directory.GetLastWriteTime(p))));}
        var window=new ThemeWindow(L.T("AccountHistory"),760,450){Owner=this,MinWidth=520,MinHeight=340};var layout=View.Grid(View.Auto,View.Fill,View.Auto);layout.Margin=new Thickness(20);window.Body.Children.Add(layout);
        View.Add(layout,View.Text(L.T("HistoryHelp"),color:"Muted"),0);var table=View.Table(("MaterialKind","Kind",110),("Updated","Time",140),("Path","Path",0));table.ItemsSource=rows.OrderByDescending(r=>r.Updated).ToArray();View.Add(layout,table,1);
        var read=View.Button("OpenReading",()=>{if(table.SelectedItem is MaterialHistoryRow {Manifest:null} row)new ChatReaderWindow(row.Path,dataRoot,preferences.ReduceMotion){Owner=this}.Show();});
        var restore=View.Button("RestoreChatBackup",async()=>{if(table.SelectedItem is MaterialHistoryRow {Manifest:{} manifest}){window.Close();await RestoreChatBackupFrom(manifest);}});
        table.SelectionChanged+=(_,_)=>{read.IsEnabled=table.SelectedItem is MaterialHistoryRow{Manifest:null};restore.IsEnabled=table.SelectedItem is MaterialHistoryRow{Manifest:not null};};read.IsEnabled=restore.IsEnabled=false;
        var actions=View.Row(read,restore,View.Button("Close",window.Close));actions.HorizontalAlignment=HorizontalAlignment.Right;actions.Margin=new Thickness(0,14,0,0);View.Add(layout,actions,2);window.ShowDialog();
    }
}
