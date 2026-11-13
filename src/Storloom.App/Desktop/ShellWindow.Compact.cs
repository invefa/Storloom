using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private CheckBox? compactNativeEncryption;
    private async Task ConnectCompactNative(string directory){
        string? secret=null;try{
            if(ArchiveStore.NeedsPassword(directory)){var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;secret=unlock.Password;unlock.ClearSecret();}
            NativeSnapshot? snapshot=null;await Run(async token=>{snapshot=await Task.Run(()=>{using var store=ArchiveStore.Open(directory,secret.AsSpan(),cancellation:token);return store.NativeCurrent();},token);});if(snapshot==null)return;
            string id=ChatArchiveProject.Identity(snapshot.Platform,snapshot.Source);var project=archiveProjects.GetValueOrDefault(id)??ChatArchiveProject.Create(Path.GetFileName(snapshot.Source),snapshot.Platform,snapshot.Source,preferences.BackupRoot) with{ReadingRoot=preferences.ArchiveRoot};
            SaveArchive(project with{NativeArchive=directory,BackupStorage=ChatStorageFormat.Compact,BackedUpAt=snapshot.Created});
        }finally{secret=null;}
    }
    private void CleanCompactInput(string snapshot,string projectId){
        string? parent=Path.GetDirectoryName(PathPolicy.Normalize(snapshot));string boundary=Path.Combine(dataRoot,"compact-input");
        if(parent==null||!PathPolicy.Under(parent,boundary)||!Guid.TryParseExact(Path.GetFileName(parent),"N",out _)||Path.GetFileName(snapshot)!="source")return;
        PathPolicy.AssertNoLinks(parent);string owner=Path.Combine(parent,"input-owner.json");if(!File.Exists(owner)||JsonFiles.Read<string>(owner)!=projectId)return;
        foreach(string path in Directory.EnumerateFileSystemEntries(parent,"*",SearchOption.AllDirectories))PathPolicy.AssertNoLinks(path);Directory.Delete(parent,true);
    }
    private async Task PrepareCompactBackupReading(ChatArchiveProject project){
        string? directory=project.NativeArchive;string? secret=null;string? restored=null;bool complete=false;
        try{
            bool encrypted=directory!=null&&ArchiveStore.NeedsPassword(directory);if(encrypted){var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;secret=unlock.Password;unlock.ClearSecret();}
            string parent=Path.Combine(dataRoot,"compact-input",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.GetDirectoryName(parent)!);ChatArchiveWorkspace.Create(parent,encrypted||project.Protection!=ChatArchiveProtectionMode.None);JsonFiles.Write(Path.Combine(parent,"input-owner.json"),project.Id);string target=Path.Combine(parent,"source");
            if(!Confirm(L.T("CompactBackupReadingConfirm",target)))return;var progress=Progress();
            await RunPausable(async token=>await Task.Run(()=>{if(directory==null){var manifest=JsonFiles.Read<TransferJournal>(project.Manifest!);if(ChatArchiveProject.Identity(project.Platform,manifest.Plan.Source)!=project.Id)throw new InvalidDataException("Backup belongs to another source.");restored=ChatRestore.Restore(project.Manifest!,target,project.Platform,progress,token);}else{using var store=ArchiveStore.Open(directory,secret.AsSpan(),progress,token);var snapshot=store.NativeCurrent();if(snapshot.Platform!=project.Platform||ChatArchiveProject.Identity(snapshot.Platform,snapshot.Source)!=project.Id)throw new InvalidDataException("Backup belongs to another source.");restored=store.RestoreNative(target,progress,token);}complete=true;},token));
            if(complete)await PrepareArchiveUpdate(project,new(DirectReading:true){SourceSnapshot=restored});
        }finally{secret=null;}
    }
    private FrameworkElement StorageSelector(ChatArchiveProject project,bool backup,FrameworkElement? workers=null){
        var selector=new ComboBox{Name=backup?"native_storage_format":"reading_storage_format",ItemsSource=new[]{L.T("StorageFolder"),L.T("StorageCompact")},SelectedIndex=(int)(backup?project.BackupStorage:project.ReadingStorage),Width=210,HorizontalAlignment=HorizontalAlignment.Left};
        selector.IsEnabled=PendingState(project)==null&&(backup||project.Reading==null);
        selector.SelectionChanged+=(_,_)=>{var current=archiveProjects[project.Id];if(backup&&backupInput!=null)current=current with{BackupRoot=backupInput.Text};if(!backup&&readingRootInput!=null)current=current with{ReadingRoot=readingRootInput.Text};current=backup?current with{BackupStorage=(ChatStorageFormat)selector.SelectedIndex}:current with{ReadingStorage=(ChatStorageFormat)selector.SelectedIndex};SaveArchive(current);pendingArchive=current;RenderChatStage();};
        selector.ToolTip=L.T(backup?"BackupFormatChoiceHelp":"StorageCompactHelp");if(workers!=null){selector.Margin=new Thickness(0,3,10,3);workers.Margin=new Thickness(0,3,0,3);}return View.Field(workers==null?"StorageFormatLabel":"StorageAndWorkers",workers==null?selector:View.Row(selector,workers));
    }
    private async Task StartCompactNative(ChatArchiveProject project){
        string output=PathPolicy.Normalize(backupInput!.Text);project=project with{BackupRoot=output};SaveArchive(project);string? secret=null;var keyMode=PendingState(project)?.KeyMode??ChatVaultKeyMode.Password;
        try{
            string? existing=project.NativeArchive??PendingState(project)?.Generation;string planned=Path.Combine(output,project.Platform.ToString(),project.Account,"native-compact-"+project.Id[..12]);if(existing==null&&ArchiveStore.Exists(planned))existing=planned;
            if(existing!=null&&ArchiveStore.Exists(existing)&&ArchiveStore.NeedsPassword(existing)){keyMode=ArchiveStore.ReadKeyMode(existing);var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;secret=unlock.Password;unlock.ClearSecret();}
            if((existing==null&&compactNativeEncryption?.IsChecked==true)||(existing!=null&&!ArchiveStore.Exists(existing)&&PendingState(project)?.Secure==true)){var create=new VaultPasswordWindow(true,false){Owner=this};if(create.ShowDialog()!=true)return;secret=create.Password;keyMode=create.KeyMode;create.ClearSecret();}
            if(!Confirm(L.T("CompactNativeConfirm",project.Source,output)))return;
            string task=project.TaskFile??Path.Combine(dataRoot,"archive-tasks",project.Id+".json");Directory.CreateDirectory(Path.GetDirectoryName(task)!);var progress=Progress();int workers=ChatWorkers("backup",output);
            await RunPausable(async token=>await Task.Run(()=>ChatArchiveWorkflow.Update(project,task,null,secret,workers,p=>Dispatcher.Invoke(()=>SaveArchive(p)),new(BackupOnly:true){KeyMode=keyMode},progress,token),token));
        }finally{secret=null;chatStep=-1;RenderChatStage();}
    }
    private async Task RestoreCompactNative(ChatArchiveProject project){
        string directory=project.NativeArchive??throw new InvalidOperationException(L.T("ArchiveNotYet"));string? secret=null;
        try{
            if(ArchiveStore.NeedsPassword(directory)){var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;secret=unlock.Password;unlock.ClearSecret();}
            var history=await Task.Run(()=>{using var store=ArchiveStore.Open(directory,secret.AsSpan());return store.NativeHistory();});if(history.Count==0)throw new IOException(L.T("ArchiveNotYet"));string snapshot=history[0].Id;
            if(history.Count>1){var dialog=new ThemeWindow(L.T("RestoreChatBackup"),580,240){Owner=this,ResizeMode=ResizeMode.NoResize,SizeToContent=SizeToContent.Height};dialog.Caption.ShowMaximize=dialog.Caption.ShowMinimize=false;var selector=new ComboBox{ItemsSource=history.Select(s=>new ComboBoxItem{Content=L.T("CompactSnapshotChoice",s.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"),s.Files,View.Size(s.Bytes)),Tag=s.Id}).ToArray(),SelectedIndex=0};var accept=View.Button("Apply",()=>dialog.DialogResult=true);var cancel=View.Button("Cancel",()=>dialog.Close());cancel.IsCancel=true;accept.IsDefault=true;var actions=View.Row(cancel,accept);actions.HorizontalAlignment=HorizontalAlignment.Right;actions.Margin=new Thickness(0,16,0,0);var form=View.Stack(View.Field("HubHistory",selector),actions);form.Margin=new Thickness(20);dialog.Body.Children.Add(form);if(dialog.ShowDialog()!=true)return;snapshot=(string)((ComboBoxItem)selector.SelectedItem).Tag;}
            string parent=View.Folder(this,purpose:"compact-native-restore");if(parent=="")return;string target=Path.Combine(parent,"restored-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
            if(!Confirm(L.T("CompactRestoreConfirm",target)))return;var progress=Progress();
            await RunPausable(async token=>await Task.Run(()=>{using var store=ArchiveStore.Open(directory,secret.AsSpan(),progress,token);store.RestoreNative(target,progress,token,snapshot);},token));
        }finally{secret=null;}
    }
    private async Task CompactStatistics(string directory){
        string? secret=null;try{
            if(ArchiveStore.NeedsPassword(directory)){var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;secret=unlock.Password;unlock.ClearSecret();}
            ArchiveStorageStatistics? stats=null;await Run(async token=>{stats=await Task.Run(()=>{using var store=ArchiveStore.Open(directory,secret.AsSpan(),cancellation:token);return store.Statistics();},token);});
            if(stats!=null)Notice.Show(this,L.T("CompactStatistics"),L.T("CompactStatisticsBody",stats.Messages,stats.LogicalFiles,View.Size(stats.ContentBytes),stats.Packs,View.Size(stats.PackBytes),View.Size(stats.IndexBytes),View.Size(stats.OtherBytes),L.T(stats.Pending?"StorePending":"StoreReady")));
        }finally{secret=null;}
    }
    private async Task RepackCompact(ChatArchiveProject project,bool? protect=null){
        if(operation!=null||project.Reading==null)return;string source=project.Reading;string? oldSecret=null,nextSecret=null;
        try{
            bool encrypted=ArchiveStore.NeedsPassword(source);var retained=PendingState(project);if(retained!=null&&!(retained.Version==2&&retained.ProtectionOnly))throw new InvalidOperationException(L.T("HubFinishPending"));
            if(encrypted){var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;oldSecret=unlock.Password;unlock.ClearSecret();}
            bool targetEncrypted=retained?.Secure??protect??encrypted;var keyMode=retained?.KeyMode??(encrypted?ArchiveStore.ReadKeyMode(source):ChatVaultKeyMode.Password);
            string target=retained?.Generation??Path.Combine(Path.GetDirectoryName(source)!,"reading-compact-"+Guid.NewGuid().ToString("N"));
            if(targetEncrypted){if(retained!=null){var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;nextSecret=unlock.Password;unlock.ClearSecret();}else if(encrypted)nextSecret=oldSecret;else{var create=new VaultPasswordWindow(true,false){Owner=this};if(create.ShowDialog()!=true)return;nextSecret=create.Password;keyMode=create.KeyMode;create.ClearSecret();}}
            if(!Confirm(L.T("CompactRepackConfirm",source,target)))return;
            string task=project.TaskFile??Path.Combine(dataRoot,"archive-tasks",project.Id+".json");Directory.CreateDirectory(Path.GetDirectoryName(task)!);
            JsonFiles.Write(task,new ChatArchiveTask(2,project.Id,source,target,source,null,"protection",targetEncrypted,ProtectionOnly:true){DirectReading=true,KeyMode=keyMode});project=project with{TaskFile=task};SaveArchive(project);var progress=Progress();int workers=ChatWorkers("encryption",target);
            await RunPausable(async token=>{await Task.Run(()=>{using var original=ArchiveStore.Open(source,oldSecret.AsSpan(),progress,token);using var destination=ArchiveStore.Exists(target)?ArchiveStore.Open(target,nextSecret.AsSpan(),progress,token):ArchiveStore.Create(target,nextSecret.AsSpan(),targetEncrypted?keyMode:ChatVaultKeyMode.Password);destination.Workers=workers;original.RepackTo(destination,progress,token);},token);SaveArchive(project with{Reading=target,TaskFile=null,Protection=targetEncrypted?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None,ProtectionKnown=true});File.Delete(task);});
        }finally{oldSecret=nextSecret=null;chatStep=-1;RenderChatStage();}
    }
}
