using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private sealed record StoreEntry(string Id,string Directory,string Source,bool Complete){public string? AccountId{get;init;}public string Name=>Path.GetFileName(Directory);public string State=>L.T(Complete?"StoreReady":"StorePending");}
    private string StoreCatalog=>Path.Combine(dataRoot,"archive-stores.json");
    private List<StoreEntry> stores=[];private DataGrid? storeTable;
    private void LoadStores(){stores=File.Exists(StoreCatalog)?JsonFiles.Read<StoreEntry[]>(StoreCatalog).ToList():[];}
    private void SaveStore(StoreEntry entry){var previous=stores.FirstOrDefault(s=>s.Directory.Equals(entry.Directory,StringComparison.OrdinalIgnoreCase));entry=entry with{Id=string.IsNullOrEmpty(entry.Id)?previous?.Id??entry.Id:entry.Id,Source=string.IsNullOrEmpty(entry.Source)?previous?.Source??entry.Source:entry.Source,AccountId=entry.AccountId??previous?.AccountId??archiveProjects.Values.FirstOrDefault(p=>p.Reading?.Equals(entry.Source,StringComparison.OrdinalIgnoreCase)==true)?.Id};stores.RemoveAll(s=>s.Directory.Equals(entry.Directory,StringComparison.OrdinalIgnoreCase));stores.Add(entry);JsonFiles.Write(StoreCatalog,stores.ToArray());}
    private StoreEntry SelectedStore()=>storeTable?.SelectedItem as StoreEntry??throw new InvalidOperationException(L.T("StoreSelect"));
    private void BuildStores(){
        LoadStores();var grid=View.Grid(View.Auto,View.Auto,View.Fill);chatContent.Children.Add(grid);View.Add(grid,View.Text(L.T("StoreHomeHelp"),color:"Muted"),0);
        var create=AsyncButton("StoreMigrate",()=>MigrateStore());create.Name="store_new";
        var load=Button("StoreLoad",()=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="Storloom archive|archive.json"};if(PickerHistory.Show(dialog,this,"store-load")==true){string path=Path.GetDirectoryName(dialog.FileName)!;SaveStore(new("",path,"",true));chatStep=5;RenderChatStage();}});load.Name="store_load";View.Add(grid,View.Row(create,load),1);
        storeTable=View.Table(("StoreName","Name",190),("State","State",120),("Path","Directory",0));storeTable.Name="storage_archives";storeTable.ItemsSource=stores.OrderBy(s=>s.Name).ToArray();View.Add(grid,storeTable,2);
        var open=Button("OpenReading",()=>new ChatReaderWindow(SelectedStore().Directory,dataRoot,preferences.ReduceMotion){Owner=this}.Show());open.Name="store_open";
        var update=AsyncButton("StoreContinueUpdate",()=>{var item=SelectedStore();return MigrateStore(item.Source,item.Directory);});update.Name="store_update";
        var backup=AsyncButton("StoreBackup",()=>RunStoreAction("backup"));backup.Name="store_backup";
        var verify=AsyncButton("VerifyBackupNow",()=>RunStoreAction("verify"));verify.Name="store_verify";
        var export=AsyncButton("StoreExportReader",()=>RunStoreAction("export"));export.Name="store_export";
        var password=AsyncButton("StoreChangePassword",()=>RunStoreAction("password"));password.Name="store_password_change";
        var abandon=Button("ArchiveAbandonTask",()=>{if(!Confirm(L.T("ArchiveAbandonConfirm")))return;var item=SelectedStore();ArchiveStore.AbandonPending(item.Directory);SaveStore(item with{Complete=File.Exists(Path.Combine(ArchiveStore.ResolveRoot(item.Directory),"HEAD"))});RenderChatStage();});abandon.Visibility=Visibility.Collapsed;chatFooter.Children.Add(abandon);
        foreach(var button in new[]{open,update,backup,verify,export,password})chatFooter.Children.Add(button);
        storeTable.SelectionChanged+=(_,_)=>{abandon.Visibility=storeTable.SelectedItem is StoreEntry pending&&ArchiveStore.SnapshotReads(pending.Directory)&&File.Exists(Path.Combine(ArchiveStore.ResolveRoot(pending.Directory),".write-task.json"))?Visibility.Visible:Visibility.Collapsed;bool selected=storeTable.SelectedItem is StoreEntry;foreach(var button in new[]{open,update,backup,verify,export,password})button.IsEnabled=selected;if(storeTable.SelectedItem is StoreEntry item){password.IsEnabled=ArchiveStore.NeedsPassword(item.Directory)&&ArchiveStore.ReadKeyMode(item.Directory)!=ChatVaultKeyMode.DirectKey;password.ToolTip=password.IsEnabled?null:L.T("VaultDirectKeyFixed");}};
        storeTable.SelectedItem=storeTable.Items.OfType<StoreEntry>().FirstOrDefault();if(storeTable.SelectedItem==null)foreach(var button in new[]{open,update,backup,verify,export,password})button.IsEnabled=false;
    }
    private async Task MigrateStore(string? source=null,string? target=null){
        if(operation!=null)return;var dialog=new StoreMigrationWindow(dataRoot,source,target){Owner=this};if(dialog.ShowDialog()!=true)return;
        LoadStores();SaveStore(new("",dialog.TargetDirectory,dialog.SourceDirectory,false));
        try{await RunPausable(async token=>{
            var progress=Progress();using var store=await Task.Run(()=>ArchiveStore.Exists(dialog.TargetDirectory)?ArchiveStore.Open(dialog.TargetDirectory,dialog.TargetPassword.AsSpan(),progress,token):ArchiveStore.CreateBundle(dialog.TargetDirectory,dialog.Encrypt?dialog.TargetPassword.AsSpan():default,dialog.Encrypt?dialog.TargetKeyMode:ChatVaultKeyMode.Password),token);
            var result=await Task.Run(()=>store.ImportReader(dialog.SourceDirectory,progress,token,dialog.SourcePassword.AsSpan()),token);
            SaveStore(new(store.Id,dialog.TargetDirectory,dialog.SourceDirectory,result.Stage=="complete"));status.Text=L.T("StoreMigrationDone",result.Messages,result.Files,View.Size(result.Bytes));operationView.AddLog(status.Text);
        });}finally{dialog.ClearSecrets();chatStep=-1;RenderChatStage();}
    }
    private async Task RunStoreAction(string action,StoreEntry? selected=null){
        if(operation!=null)return;var entry=selected??SelectedStore();string? secret=null,newSecret=null,target=null,exportSecret=null;int exportWorkers=2;
        try{
            bool encrypted=ArchiveStore.NeedsPassword(entry.Directory);
            if(action=="password"&&!encrypted)throw new InvalidOperationException(L.T("StoreNotEncrypted"));if(action=="password"&&ArchiveStore.ReadKeyMode(entry.Directory)==ChatVaultKeyMode.DirectKey)throw new InvalidOperationException(L.T("VaultDirectKeyFixed"));
            if(encrypted){var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;secret=unlock.Password;unlock.ClearSecret();}
            if(action=="password"){var dialog=new VaultPasswordWindow(true,false,passwordChange:true){Owner=this};if(dialog.ShowDialog()!=true)return;newSecret=dialog.Password;dialog.ClearSecret();if(!Confirm(L.T("StorePasswordConfirm")))return;}
            if(action is "backup" or "export"){
                if(action=="backup"){string parent=PickerHistory.Folder(this,null,ArchiveStore.IsNative(entry.Directory)?"native-store-backup-parent":"store-backup-parent");if(parent=="")return;target=Path.Combine(parent,entry.Name+"-backup");}
                else{var dialog=new StoreExportWindow(dataRoot,entry.Name){Owner=this};if(dialog.ShowDialog()!=true)return;target=dialog.Destination;exportSecret=dialog.Password;exportWorkers=dialog.Workers;dialog.ClearSecret();}
                if(!Confirm(L.T(action=="backup"?"StoreBackupConfirm":"StoreExportConfirm",entry.Directory,target)))return;
            }
            await RunPausable(async token=>{
                var progress=Progress();using var store=await Task.Run(()=>ArchiveStore.Open(entry.Directory,secret.AsSpan(),progress,token),token);secret=null;
                if(action=="backup"){var result=await Task.Run(()=>store.BackupTo(target!,progress,token),token);status.Text=L.T("StoreBackupDone",result.CopiedFiles,result.ReusedFiles,View.Size(result.ProcessedBytes));}
                else if(action=="export"){await Task.Run(()=>store.ExportReader(target!,progress,token),token);if(exportSecret!=null){using var vault=await Task.Run(()=>ChatVault.Create(target!,exportSecret.AsSpan(),token,progress),token);await Task.Run(()=>vault.Encrypt(workers:exportWorkers,progress:progress,cancellation:token),token);}status.Text=L.T("StoreExportDone",target!);}
                else if(action=="password"){await Task.Run(()=>store.ChangePassword(newSecret.AsSpan()),token);status.Text=L.T("StorePasswordDone");}
                else{var result=await Task.Run(()=>store.Verify(progress,token),token);status.Text=L.T("StoreVerifyDone",result.Commits,result.MessageRevisions,result.ResourceChunks,View.Size(result.Bytes));}
                operationView.AddLog(status.Text);
            });
        }finally{secret=newSecret=exportSecret=null;}
    }
}
