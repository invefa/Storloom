using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;
public sealed partial class ShellWindow {
    private string? keyAccountId;
    private sealed record KeyAccount(ChatArchiveProject Project,string? AccountAlias=null,string? SourceAlias=null){public string Label=>(string.IsNullOrEmpty(AccountAlias)?Project.Account:AccountAlias+" / "+Project.Account)+" ("+Project.Platform+") / "+(string.IsNullOrEmpty(SourceAlias)?"":SourceAlias+" / ")+Project.Source;}
    private void BuildAccountClientSettings(){
        chatContent.Children.Add(new ChatSettingsEditor(this,preferences,p=>{preferences=p;JsonFiles.Write(Path.Combine(dataRoot,"preferences.json"),preferences);discoveryStarted=false;status.Text=L.T("HubSettingsSaved");},()=>RenderChatStage()));
    }
    private void BuildDatabaseKeys(){
        var grid=View.Grid(View.Auto,View.Auto,View.Fill,View.Auto);chatContent.Children.Add(grid);
        View.Add(grid,View.Text(L.T("KeyLibraryHelp"),color:"Muted"),0);
        var aliases=LoadAccountAliases();var accounts=archiveProjects.Values.OrderBy(p=>p.Account).Select(p=>new KeyAccount(p,aliases.Accounts.GetValueOrDefault("account:"+p.Platform+":"+p.Account),aliases.Sources.GetValueOrDefault(p.Id))).ToArray();var selector=new ComboBox{ItemsSource=accounts,DisplayMemberPath="Label",Name="key_account_selector"};
        selector.SelectedItem=accounts.FirstOrDefault(p=>p.Project.Id==(keyAccountId??selectedArchiveId))??accounts.FirstOrDefault();
        selector.SelectionChanged+=(_,_)=>{keyAccountId=(selector.SelectedItem as KeyAccount)?.Project.Id;SetCapturedKey(null);RenderChatStage();};
        var table=View.Table(("Account","Account",0),("Platform","Platform",90),("Path","Path",0));table.Name="database_key_files";table.ItemsSource=ReadKeyLibrary();View.Add(grid,table,2);
        async Task WithAccount(Func<Task> action){if(selector.SelectedItem is not KeyAccount selected)throw new InvalidOperationException(L.T("NoAccountSelected"));keyAccountId=selected.Project.Id;if(await PrepareKeyAccount(selected.Project))await action();}
        var passive=AsyncButton("ReadPassive",()=>WithAccount(CapturePassive));var login=AsyncButton("AcquireDatabaseKey",()=>WithAccount(CaptureActive));var import=AsyncButton("KeyImport",()=>WithAccount(ImportChatKey));
        var retain=Button("KeyManage",()=>{ManageChatKey();RenderChatStage();});retain.IsEnabled=capturedKey!=null&&selector.SelectedItem is KeyAccount ka&&activeChat?.Directory==ka.Project.Source;
        foreach(var b in new[]{passive,login,import})b.IsEnabled=accounts.Length>0;
        View.Add(grid,View.Stack(View.Field("Account",selector),View.Row(passive,login,import,retain)),1);
        var unlock=AsyncButton("ProtectedKeyUnlock",async()=>{if(table.SelectedItem is KeyLibraryEntry entry)await WithAccount(()=>ImportChatKeyFrom(entry.Path));});unlock.IsEnabled=false;
        var forget=Button("KeyLibraryForget",()=>{if(table.SelectedItem is not KeyLibraryEntry entry)return;var entries=ReadKeyLibrary();entries.RemoveAll(e=>e.Path==entry.Path);JsonFiles.Write(KeyLibraryPath,entries);table.ItemsSource=entries;});forget.IsEnabled=false;
        table.SelectionChanged+=(_,_)=>{unlock.IsEnabled=table.SelectedItem is KeyLibraryEntry&&accounts.Length>0;forget.IsEnabled=table.SelectedItem is KeyLibraryEntry;};
        TableInteraction.Activate(table,unlock);
        var actions=View.Row(unlock,forget);actions.Margin=new Thickness(0,12,0,0);View.Add(grid,actions,3);
    }
    private async Task<bool> PrepareKeyAccount(ChatArchiveProject project){
        bool ready=false;await Run(async token=>{var context=await Task.Run(()=>{
            var database=Directory.Exists(project.Source)?ChatExporter.FindDatabases(project.Source,project.Platform).FirstOrDefault():null;
            if(database!=null)return (Location:new ChatLocation(project.Platform,project.Source,project.Account,database,new FileInfo(database).Length),Archive:(PreparedChatArchive?)null);
            if(project.Manifest==null)throw new IOException(L.T("AccountSourceOffline"));
            var journal=JsonFiles.Read<TransferJournal>(project.Manifest);var saved=ChatExporter.FindDatabases(journal.Plan.Destination,project.Platform).FirstOrDefault()??throw new IOException(L.T("NoAccounts"));
            var location=new ChatLocation(project.Platform,project.Source,project.Account,TransferService.Child(project.Source,Path.GetRelativePath(journal.Plan.Destination,saved)),0);
            return (Location:location,Archive:(PreparedChatArchive?)ReadChatArchive(project.Manifest,location,journal));
        },token);
        if(activeChat?.Directory!=project.Source)SetCapturedKey(null);activeChat=context.Location;activeManifest=project.Manifest;preparedArchive=context.Archive;directChatSource=context.Archive==null;status.Text=L.T("HubAccountConnected",project.Account);ready=true;});return ready;
    }
    private Task PrepareReadingTask(ChatArchiveProject project,bool fromBackup=false){
        if(PendingState(project)!=null)throw new InvalidOperationException(L.T("HubFinishPending"));
        if(fromBackup&&(project.NativeArchive!=null||project.ReadingStorage==ChatStorageFormat.Compact&&project.Manifest!=null))return PrepareCompactBackupReading(project);
        if(fromBackup){activeChat=new(project.Platform,project.Source,project.Account,Path.Combine(project.Source,"source.db"),0);return UseChatBackup(project.Manifest??throw new InvalidOperationException(L.T("ArchiveFirstBackup")));}
        if(!Directory.Exists(project.Source))throw new IOException(L.T("AccountSourceOffline"));
        return PrepareArchiveUpdate(project,new(DirectReading:true));
    }
    private async Task PrepareRawTask(ChatArchiveProject project){
        if(archivePreparing||operation!=null)return;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        if(windowClosed)return;
        if(PendingState(project)!=null)throw new InvalidOperationException(L.T("HubFinishPending"));
        activeChat=new(project.Platform,project.Source,project.Account,Path.Combine(project.Source,"source.db"),0);activeManifest=project.Manifest;pendingArchive=project;readingDirectory=project.Reading;directChatSource=false;SetCapturedKey(null);chatStep=6;RenderChatStage();
    }
    private void AbandonAccountTask(ChatArchiveProject project){
        if(project.TaskFile==null||!Confirm(L.T("ArchiveAbandonConfirm")))return;
        PathPolicy.AssertNoLinks(project.TaskFile);if(!PathPolicy.Under(project.TaskFile,Path.Combine(dataRoot,"archive-tasks")))throw new IOException("Unexpected task location.");
        var state=PendingState(project);if(state?.Version==2){if(ArchiveStore.Exists(state.Generation))ArchiveStore.AbandonPending(state.Generation);if(state.Work!=state.Generation&&Path.GetFileName(state.Work).StartsWith(".compact-work-",StringComparison.Ordinal)&&Directory.Exists(state.Work)){PathPolicy.AssertNoLinks(state.Work);Directory.Move(state.Work,state.Work+"."+Guid.NewGuid().ToString("N")+".abandoned");}}
        File.Move(project.TaskFile,project.TaskFile+"."+Guid.NewGuid().ToString("N")+".abandoned",false);SaveArchive(project with{TaskFile=null});chatStep=-1;RenderChatStage();
    }
}
