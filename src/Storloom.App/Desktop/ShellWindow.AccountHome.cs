using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private sealed record HomeAccountRow(ChatArchiveProject? Project=null,StoreEntry? Store=null,string? ConnectedReader=null){
        public string Id=>Project?.Id??Store?.Directory??ConnectedReader!;
        public string Account=>Project?.Account??Store?.Name??Path.GetFileName(ConnectedReader)??L.T("ConnectedReaderFormat");
        public bool NeedsStoreSync=>Store!=null&&Project?.Reading!=null&&(!Store.Complete||!Store.Source.Equals(Project.Reading,StringComparison.OrdinalIgnoreCase));
        public string? Reader=>Store!=null&&!NeedsStoreSync?Store.Directory:Project?.Reading??ConnectedReader;
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
        var grid=View.Grid(View.Auto,View.Fill,View.Auto);chatContent.Children.Add(grid);View.Add(grid,View.Text(L.T("AccountHomeHelp"),color:"Muted"),0);
        archiveTable=View.Table(("Account","Account",0),("ArchiveLastBackup","Backup",0),("ArchiveLastReading","Reading",0),("ReadingFormatProtection","Protection",0),("State","Task",0));archiveTable.Name="managed_archives";archiveTable.ItemsSource=rows;View.Add(grid,archiveTable,1);
        var detail=View.Text(L.T("ArchiveSelect"),color:"Muted");detail.Margin=new Thickness(0,12,0,0);View.Add(grid,detail,2);
        var open=AsyncButton("OpenReading",async()=>{
            var item=SelectedHomeAccount();if(item.Reader!=null){if(item.Project!=null)MigrateArchiveReaderPreferences(item.Project);new ChatReaderWindow(item.Reader,dataRoot,preferences.ReduceMotion,item.Project?.Id){Owner=this}.Show();}
            else if(item.Project!=null)await OpenAccountTasks(item.Project);
        },true);open.Name="archive_open";
        var update=AsyncButton("UpdateReading",async()=>{var item=SelectedHomeAccount();if(item.Project!=null)await OpenAccountTasks(item.Project);else if(item.Store!=null)await MigrateStore(item.Store.Source,item.Store.Directory);});update.Name="archive_update";
        var backup=AsyncButton("RawBackupHeading",async()=>{var item=SelectedHomeAccount();if(item.Project!=null){await OpenAccountTasks(item.Project);if(PendingState(item.Project)==null){directChatSource=false;chatStep=6;RenderChatStage();}}else if(item.Store!=null)await RunStoreAction("backup",item.Store);});backup.Name="archive_backup";
        var menu=new ContextMenu();var more=Button("AccountMaterials",()=>menu.IsOpen=true);more.ContextMenu=menu;menu.PlacementTarget=more;
        MenuItem Entry(string key,string name,Func<Task> action){var entry=new MenuItem{Header=L.T(key),Name=name};entry.Click+=async(_,_)=>{try{await action();}catch(Exception ex){Error(ex);}};menu.Items.Add(entry);return entry;}
        var protection=Entry("ArchiveProtection","archive_protection",async()=>{var item=SelectedHomeAccount();if(item.Project!=null)await ConfigureArchiveProtection(item.Project);else if(item.Store!=null)await RunStoreAction("password",item.Store);});
        var verify=Entry("VerifyBackupNow","archive_verify_now",async()=>{var item=SelectedHomeAccount();if(item.Project!=null)await VerifyArchiveBackup(item.Project);else if(item.Store!=null)await RunStoreAction("verify",item.Store);});
        var history=Entry("AccountHistory","account_history",()=>{ShowAccountHistory(SelectedHomeAccount());return Task.CompletedTask;});
        var export=Entry("StoreExportReader","account_export",async()=>{var item=SelectedHomeAccount();if(item.Store!=null)await RunStoreAction("export",item.Store);else if(item.Project!=null&&item.Project.Manifest!=null)await PrepareArchiveUpdate(item.Project,new(UseExistingBackup:true));});
        var migrate=Entry("ReadingStorageFormat","account_migrate",()=>{var item=SelectedHomeAccount();return MigrateStore(item.NeedsStoreSync?item.Project!.Reading:item.Reader,item.NeedsStoreSync?item.Store!.Directory:null);});
        var abandon=Entry("ArchiveAbandonTask","account_abandon",()=>{var project=SelectedArchive();if(project.TaskFile!=null&&Confirm(L.T("ArchiveAbandonConfirm"))){PathPolicy.AssertNoLinks(project.TaskFile);if(!PathPolicy.Under(project.TaskFile,Path.Combine(dataRoot,"archive-tasks")))throw new IOException("Unexpected task location.");File.Move(project.TaskFile,project.TaskFile+"."+Guid.NewGuid().ToString("N")+".abandoned",false);SaveArchive(project with{TaskFile=null});RenderChatStage();}return Task.CompletedTask;});
        foreach(var button in new[]{open,update,backup,more}){button.Margin=new Thickness(0,3,8,3);chatFooter.Children.Add(button);}
        void Selection(){
            bool selected=archiveTable.SelectedItem is HomeAccountRow;open.IsEnabled=update.IsEnabled=backup.IsEnabled=more.IsEnabled=selected;if(!selected)return;
            var item=SelectedHomeAccount();selectedArchiveId=item.Id;open.Content=L.T(item.Reader==null?"ReadingTaskHeading":"OpenReading");
            update.IsEnabled=item.Project!=null||item.Store!=null;update.Content=L.T(item.Project?.TaskFile!=null?"ArchiveContinue":"UpdateReading");
            backup.IsEnabled=item.Project!=null?Directory.Exists(item.Project.Source):item.Store!=null;backup.Content=L.T(item.Project==null?"StoreBackup":"RawBackupHeading");
            bool storeUnlockable=false;try{storeUnlockable=item.Store!=null&&ArchiveStore.Exists(item.Store.Directory)&&ArchiveStore.NeedsPassword(item.Store.Directory);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){ }
            if(item.Reader!=null&&!Directory.Exists(item.Reader))open.IsEnabled=false;
            protection.Header=L.T(item.Store!=null?"StoreChangePassword":"ArchiveProtection");protection.IsEnabled=item.Reader!=null&&item.ConnectedReader==null&&(item.Store==null||storeUnlockable);verify.IsEnabled=item.Store!=null||item.Project?.Manifest!=null;export.IsEnabled=verify.IsEnabled;
            history.IsEnabled=item.Project!=null;migrate.IsEnabled=item.Reader!=null&&(item.Store==null||item.NeedsStoreSync);migrate.Header=L.T(item.NeedsStoreSync?"SyncPackedReading":"ReadingStorageFormat");abandon.IsEnabled=item.Project?.TaskFile!=null;
            detail.Text=item.Project!=null?L.T("ArchiveDetail",item.Project.Source,item.Reader??L.T("ArchiveReadingPending")):L.T("ConnectedMaterialDetail",item.Reader??"");
        }
        archiveTable.SelectionChanged+=(_,_)=>Selection();archiveTable.SelectedItem=rows.FirstOrDefault(r=>r.Id==selectedArchiveId)??rows.FirstOrDefault();Selection();
    }
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
