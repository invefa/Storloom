using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;
public sealed partial class ShellWindow {
    private readonly Dictionary<string,ChatArchiveProject> archiveProjects=new(StringComparer.Ordinal);
    private string? selectedArchiveId;
    private DataGrid? archiveTable;
    private ChatArchiveProject? pendingArchive;
    private ChatArchiveUpdateRequest? pendingArchiveRequest;
    private PasswordBox? archivePassword;
    private bool archivePreparing;
    private string ArchiveCatalogPath=>Path.Combine(dataRoot,"chat-archives.json");
    private static string ProjectBackupRoot(TransferJournal journal,ChatPlatform platform){var account=Directory.GetParent(journal.Plan.Destination)!;return account.Name.Equals(Path.GetFileName(journal.Plan.Source),StringComparison.OrdinalIgnoreCase)&&account.Parent?.Name.Equals(platform.ToString(),StringComparison.OrdinalIgnoreCase)==true?account.Parent.Parent!.FullName:Path.Combine(account.FullName,"managed-archives");}
    private void LoadArchiveProjects(){
        archiveProjects.Clear();
        if(File.Exists(ArchiveCatalogPath))foreach(var item in JsonFiles.Read<ChatArchiveProject[]>(ArchiveCatalogPath))archiveProjects[item.Id]=item;
        bool imported=false;
        foreach(var item in verifiedBackups){try{
            var journal=JsonFiles.Read<TransferJournal>(item.Value);string root=journal.Plan.Destination;ChatPlatform platform=ChatExporter.FindDatabases(root,ChatPlatform.QQ).Any()?ChatPlatform.QQ:ChatPlatform.WeChat;
            string id=ChatArchiveProject.Identity(platform,journal.Plan.Source);if(archiveProjects.ContainsKey(id))continue;
            string accountRoot=Path.GetDirectoryName(root)!;string? reading=Directory.EnumerateDirectories(accountRoot).Where(p=>(Path.GetFileName(accountRoot)==Path.GetFileName(journal.Plan.Source)||Path.GetFileName(p).StartsWith(Path.GetFileName(root)+"-reading-",StringComparison.Ordinal))&&!Path.GetFileName(p).StartsWith('.')&&(File.Exists(Path.Combine(p,"messages.html"))||ChatVault.Exists(p))).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
            var project=new ChatArchiveProject(id,Path.GetFileName(journal.Plan.Source),platform,journal.Plan.Source,ProjectBackupRoot(journal,platform)){Manifest=item.Value,Reading=reading,BackedUpAt=File.GetLastWriteTimeUtc(item.Value),ReadableAt=reading==null?null:Directory.GetLastWriteTimeUtc(reading),ProtectionKnown=reading==null||!ChatVault.Exists(reading),Protection=reading!=null&&ChatVault.Exists(reading)?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None};archiveProjects[id]=project;imported=true;
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){}}
        if(imported)JsonFiles.Write(ArchiveCatalogPath,archiveProjects.Values.ToArray());
    }
    private void SaveArchive(ChatArchiveProject project){archiveProjects[project.Id]=project;JsonFiles.Write(ArchiveCatalogPath,archiveProjects.Values.ToArray());selectedArchiveId=project.Id;}
    private void RegisterCurrentArchive(){
        if(activeChat==null||activeManifest==null||!File.Exists(activeManifest))return;
        var journal=JsonFiles.Read<TransferJournal>(activeManifest);string id=ChatArchiveProject.Identity(activeChat.Platform,journal.Plan.Source);
        var project=archiveProjects.GetValueOrDefault(id)??new ChatArchiveProject(id,Path.GetFileName(journal.Plan.Source),activeChat.Platform,journal.Plan.Source,ProjectBackupRoot(journal,activeChat.Platform));
        SaveArchive(project with{BackupRoot=ProjectBackupRoot(journal,activeChat.Platform),Manifest=activeManifest,BackedUpAt=File.GetLastWriteTimeUtc(activeManifest),Reading=readingDirectory??project.Reading,ReadableAt=readingDirectory!=null?DateTimeOffset.UtcNow:project.ReadableAt,Protection=readingDirectory!=null&&ChatVault.Exists(readingDirectory)?ChatArchiveProtectionMode.All:project.Protection});
    }
    private sealed record ArchiveRow(ChatArchiveProject Project){
        public string Account=>Project.Account;public string Backup=>Project.BackedUpAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")??L.T("ArchiveNotYet");
        public string Reading=>Project.ReadableAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")??L.T("ArchiveReadingPending");
        public string Protection=>!Project.ProtectionKnown?L.T("ArchiveProtectionLocked"):L.T(Project.Protection==ChatArchiveProtectionMode.None?"ArchiveProtectionNone":Project.Protection==ChatArchiveProtectionMode.Selected?"ArchiveProtectionSelected":Project.IncludeMedia?"ArchiveProtectionAll":"ArchiveProtectionMetadata");
        public string Task=>L.T(Project.TaskFile!=null&&File.Exists(Project.TaskFile)?"ArchiveContinue":"ArchiveReady");
    }
    private ChatArchiveProject SelectedArchive()=>archiveTable?.SelectedItem is ArchiveRow row?archiveProjects[row.Project.Id]:throw new InvalidOperationException(L.T("ArchiveSelect"));
    private void MigrateArchiveReaderPreferences(ChatArchiveProject project){if(project.Reading==null)return;string root=Path.Combine(dataRoot,"reader-preferences"),old=Path.Combine(root,JsonFiles.Hash(PathPolicy.Normalize(project.Reading).ToUpperInvariant())+".json"),target=Path.Combine(root,JsonFiles.Hash(new{archive=project.Id})+".json");if(!File.Exists(target)&&File.Exists(old)){PathPolicy.AssertNoLinks(old);Directory.CreateDirectory(root);File.Copy(old,target,false);}}
    private void BuildArchiveHome(){
        var grid=View.Grid(View.Auto,View.Fill,View.Auto);chatContent.Children.Add(grid);View.Add(grid,View.Text(L.T("ArchiveHomeHelp"),color:"Muted"),0);
        archiveTable=View.Table(("Account","Account",180),("ArchiveLastBackup","Backup",160),("ArchiveLastReading","Reading",160),("ArchiveProtection","Protection",160),("State","Task",0));archiveTable.Name="managed_archives";archiveTable.ItemsSource=archiveProjects.Values.OrderBy(p=>p.Account).Select(p=>new ArchiveRow(p)).ToArray();View.Add(grid,archiveTable,1);
        var verify=new CheckBox{Content=L.T("VerifyBackupBeforeExport"),Name="archive_verify_before",Margin=new Thickness(0,8,0,4),ToolTip=L.T("VerifyBackupHelp")};
        var verifyNow=AsyncButton("VerifyBackupNow",()=>VerifyArchiveBackup(SelectedArchive()));verifyNow.Name="archive_verify_now";
        verify.Click+=(_,_)=>Guard(()=>{
            var p=SelectedArchive();bool enabled=verify.IsChecked==true;
            if(PendingState(p) is {} task){if(!PathPolicy.Under(p.TaskFile!,Path.Combine(dataRoot,"archive-tasks")))throw new IOException("Unexpected task location.");PathPolicy.AssertNoLinks(p.TaskFile!);JsonFiles.Write(p.TaskFile!,task with{VerifyBackupBeforeReading=enabled});}
            SaveArchive(p with{VerifyBackupBeforeReading=enabled});
        });
        var detail=View.Text(L.T("ArchiveSelect"),color:"Muted");detail.Margin=new Thickness(0,12,0,0);View.Add(grid,View.Stack(verify,detail),2);
        var open=Button("OpenReading",()=>{var p=SelectedArchive();if(p.Reading!=null){MigrateArchiveReaderPreferences(p);new ChatReaderWindow(p.Reading,dataRoot,preferences.ReduceMotion,p.Id){Owner=this}.Show();}});open.Name="archive_open";
        var update=AsyncButton("ArchiveUpdate",()=>PrepareArchiveUpdate(SelectedArchive()),true);update.Name="archive_update";
        var protection=AsyncButton("ArchiveProtection",()=>ConfigureArchiveProtection(SelectedArchive()));protection.Name="archive_protection";
        var more=new ContextMenu();var moreButton=Button("ArchiveMore",()=>more.IsOpen=true);more.PlacementTarget=moreButton;moreButton.ContextMenu=more;
        void Action(string key,ChatArchiveUpdateRequest request){var item=new MenuItem{Header=L.T(key)};item.Click+=async(_,_)=>{try{await PrepareArchiveUpdate(SelectedArchive(),request);}catch(Exception ex){Error(ex);}};more.Items.Add(item);}
        Action("ArchiveBackupOnly",new(BackupOnly:true));Action("ArchiveRebuild",new(Rebuild:true,UseExistingBackup:true));Action("ArchiveUseBackup",new(UseExistingBackup:true));
        var abandon=new MenuItem{Header=L.T("ArchiveAbandonTask")};abandon.Click+=(_,_)=>Guard(()=>{var p=SelectedArchive();if(p.TaskFile==null)return;if(!Confirm(L.T("ArchiveAbandonConfirm")))return;PathPolicy.AssertNoLinks(p.TaskFile);if(!PathPolicy.Under(p.TaskFile,Path.Combine(dataRoot,"archive-tasks")))throw new IOException("Unexpected task location.");File.Move(p.TaskFile,p.TaskFile+"."+Guid.NewGuid().ToString("N")+".abandoned",false);SaveArchive(p with{TaskFile=null});RenderChatStage();});more.Items.Add(abandon);
        chatFooter.Children.Add(open);chatFooter.Children.Add(update);chatFooter.Children.Add(protection);chatFooter.Children.Add(verifyNow);chatFooter.Children.Add(moreButton);
        void Selection(){bool selected=archiveTable.SelectedItem is ArchiveRow;open.IsEnabled=selected&&SelectedArchive().Reading!=null;update.IsEnabled=protection.IsEnabled=moreButton.IsEnabled=selected;verify.IsEnabled=selected;verifyNow.IsEnabled=selected&&SelectedArchive().Manifest!=null&&PendingState(SelectedArchive())==null;if(selected){var p=SelectedArchive();verify.IsChecked=PendingState(p)?.VerifyBackupBeforeReading??p.VerifyBackupBeforeReading;selectedArchiveId=p.Id;update.Content=L.T(p.TaskFile!=null&&File.Exists(p.TaskFile)?"ArchiveContinue":"ArchiveUpdate");detail.Text=L.T("ArchiveDetail",p.Source,p.Reading??L.T("ArchiveReadingPending"))+"\n"+L.T("ArchiveRawProtectionNotice");}}
        archiveTable.SelectionChanged+=(_,_)=>Selection();archiveTable.SelectedItem=archiveTable.Items.OfType<ArchiveRow>().FirstOrDefault(r=>r.Project.Id==selectedArchiveId)??archiveTable.Items.OfType<ArchiveRow>().FirstOrDefault();Selection();
    }
    private async Task VerifyArchiveBackup(ChatArchiveProject project){
        if(project.Manifest==null)return;
        var progress=Progress();bool completed=false;
        await RunPausable(async token=>{await Task.Run(()=>ChatExporter.VerifyBackup(project.Manifest,progress,token),token);token.ThrowIfCancellationRequested();completed=true;});
        if(completed){status.Text=L.T("VerifyBackupComplete");operationView.AddLog(status.Text);}
    }
    private ChatArchiveTask? PendingState(ChatArchiveProject project)=>project.TaskFile!=null&&File.Exists(project.TaskFile)?JsonFiles.Read<ChatArchiveTask>(project.TaskFile):null;
    private bool ArchiveNeedsPassword(ChatArchiveProject project,ChatArchiveUpdateRequest request)=>!request.BackupOnly&&(PendingState(project)?.Secure==true||project.Protection!=ChatArchiveProtectionMode.None||project.Reading!=null&&ChatVault.Exists(project.Reading));
    private bool ArchiveNeedsDatabase(ChatArchiveProject project,ChatArchiveUpdateRequest request)=>!request.BackupOnly&&!request.ProtectionOnly&&PendingState(project)?.ProtectionOnly!=true&&(PendingState(project)?.Phase is null or "backup" or "workspace" or "reading")&&NeedsKey();
    private async Task CreateArchiveFromSetup(){
        RegisterCurrentArchive();var project=archiveProjects[ChatArchiveProject.Identity(Account().Platform,Account().Directory)];
        project=project with{VerifyBackupBeforeReading=verifyChatBackup};SaveArchive(project);
        pendingArchive=project;pendingArchiveRequest=new(UseExistingBackup:true);string? password=null;
        try{if(encryptAfterExport){var dialog=new VaultPasswordWindow(true,true){Owner=this};if(dialog.ShowDialog()!=true)return;password=dialog.Password;dialog.ClearSecret();pendingArchiveRequest=new(Protection:new(dialog.IncludeMedia),Mode:ChatArchiveProtectionMode.All,UseExistingBackup:true);}
            await StartArchiveUpdate(password);
        }finally{password=null;}
    }
    private async Task PrepareArchiveUpdate(ChatArchiveProject project,ChatArchiveUpdateRequest? request=null){
        if(PendingState(project)!=null&&request!=null)throw new InvalidOperationException(L.T("ArchiveFinishTaskFirst"));
        if(archivePreparing||operation!=null)return;
        pendingArchive=project;pendingArchiveRequest=request??new();
        activeManifest=project.Manifest;readingDirectory=project.Reading;
        if(project.Manifest==null)throw new InvalidOperationException(L.T("ArchiveFirstBackup"));
        var journal=JsonFiles.Read<TransferJournal>(project.Manifest);string? database=ChatExporter.FindDatabases(journal.Plan.Destination,project.Platform).FirstOrDefault();if(database==null)throw new InvalidDataException(L.T("NoAccounts"));
        if(activeChat?.Directory!=project.Source||activeChat?.Platform!=project.Platform)SetCapturedKey(null);
        activeChat=new(project.Platform,project.Source,project.Account,TransferService.Child(project.Source,Path.GetRelativePath(journal.Plan.Destination,database)),new FileInfo(database).Length);preparedArchive=ReadChatArchive(project.Manifest,activeChat,journal);
        bool password=ArchiveNeedsPassword(project,pendingArchiveRequest);
        bool databaseAccess=ArchiveNeedsDatabase(project,pendingArchiveRequest)&&capturedKey==null;
        if(password||databaseAccess){chatStep=4;RenderChatStage();return;}
        await StartArchiveUpdate(null);
    }
    private void BuildArchiveAccess(){
        var project=pendingArchive??throw new InvalidOperationException();var request=pendingArchiveRequest??new();
        var stack=View.Stack(View.Text(L.T("ArchiveAccessTitle"),19),View.Text(L.T("ArchiveAccessHelp"),color:"Muted"));chatContent.Children.Add(new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        if(ArchiveNeedsDatabase(project,request)){
            stack.Children.Add(View.Text(L.T("ExportAccessSection"),15));var methods=View.Row(AsyncButton("ReadPassive",CapturePassive),AsyncButton("KeyImport",ImportChatKey),AsyncButton(Account().Platform==ChatPlatform.QQ?"ReadQQLogin":"ReadWeChatLogin",CaptureActive));if(capturedKey!=null)methods.Children.Add(Button("KeyManage",ManageChatKey));stack.Children.Add(methods);
            keyInput=View.SecretInput();keyInput.Name="archive_database_key";stack.Children.Add(View.Field("DatabaseKeyLabel",keyInput));stack.Children.Add(View.Text(L.T(capturedKey==null?"DatabaseKeyEntryHelp":"KeyReady"),color:"Muted"));
        }
        if(ArchiveNeedsPassword(project,request)){archivePassword=View.SecretInput();archivePassword.Name="archive_unlock";stack.Children.Add(View.Field("VaultPassword",archivePassword));stack.Children.Add(View.Text(L.T("ArchiveSecureWorkspaceHelp"),color:"Muted"));}
        stack.Children.Add(View.Field("ChatWorkers",ChatWorkerSelector("export",()=>project.BackupRoot)));
        chatFooter.Children.Add(Button("GoBack",()=>{keyInput?.Clear();archivePassword?.Clear();chatStep=-1;RenderChatStage();}));chatFooter.Children.Add(AsyncButton(project.TaskFile!=null?"ArchiveContinue":"ArchiveStart",async()=>{string? password=archivePassword?.Password;archivePassword?.Clear();await StartArchiveUpdate(password);password=null;},true));
    }
    private async Task StartArchiveUpdate(string? password){
        var project=pendingArchive??throw new InvalidOperationException();var request=pendingArchiveRequest??new();
        MigrateArchiveReaderPreferences(project);
        if(project.TaskFile==null&&project.Reading!=null&&ChatVault.Exists(project.Reading)&&request.Protection==null&&!request.BackupOnly){using var previous=ChatVault.Open(project.Reading,(password??"").AsSpan());var settings=previous.ReadProtection();project=project with{ProtectionKnown=true,Protection=settings.Selection==null?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.Selected,IncludeMedia=settings.IncludeMedia};SaveArchive(project);pendingArchive=project;}
        if(!Confirm(L.T("ArchiveUpdateConfirm",project.Account,project.Source,project.BackupRoot)+"\n"+L.T("ArchiveRawProtectionNotice")+((project.Protection!=ChatArchiveProtectionMode.None||request.Mode is ChatArchiveProtectionMode.All or ChatArchiveProtectionMode.Selected)?"\n"+L.T("ArchiveSecureWorkspaceHelp"):"")))return;
        byte[]? key=capturedKey?.ToArray();try{
            if(!string.IsNullOrEmpty(keyInput?.Password)){if(key!=null)CryptographicOperations.ZeroMemory(key);key=ChatDatabaseKey.Parse(keyInput.Password,project.Platform);}keyInput?.Clear();
            string taskFile=project.TaskFile??Path.Combine(dataRoot,"archive-tasks",project.Id+".json");Directory.CreateDirectory(Path.GetDirectoryName(taskFile)!);
            JsonFiles.Write(Path.Combine(dataRoot,"preferences.json"),preferences);bool automatic=preferences.ChatWorkersAutomatic;int manual=preferences.ChatParallelism;var progress=Progress();
            await RunPausable(async token=>{await Task.Run(()=>ChatArchiveWorkflow.Update(project,taskFile,key,password,ChatWorkers("export",project.BackupRoot),p=>Dispatcher.Invoke(()=>{SaveArchive(p);verifiedBackups[p.Source]=p.Manifest!;JsonFiles.Write(Path.Combine(dataRoot,"chat-backups.json"),verifiedBackups);}),request,progress,token,Theme.IsDark,phase=>automatic?WorkerRecommendation.For(phase,project.BackupRoot):manual,downloadGroupAvatars:preferences.DownloadQQGroupAvatars),token);});
        }finally{if(key!=null)CryptographicOperations.ZeroMemory(key);password=null;archivePassword?.Clear();if(operationIncomplete)SetCapturedKey(null);chatStep=-1;RenderChatStage();}
    }
    private async Task ConfigureArchiveProtection(ChatArchiveProject project){
        if(project.TaskFile!=null&&File.Exists(project.TaskFile))throw new InvalidOperationException(L.T("ArchiveFinishTaskFirst"));
        if(project.Reading==null)throw new InvalidOperationException(L.T("ArchiveReadingPending"));
        if(archivePreparing||operation!=null)return;archivePreparing=true;
        string? password=null;JsonObject index;ChatArchiveProtection? current=null;
        try{
            if(ChatVault.Exists(project.Reading)){
                var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;password=unlock.Password;unlock.ClearSecret();
                using var vault=await Task.Run(()=>ChatVault.Open(project.Reading,password.AsSpan()));try{current=vault.ReadProtection();}catch(InvalidOperationException){}using var input=vault.OpenRead("archive-index.js");using var reader=new StreamReader(input);string raw=await reader.ReadToEndAsync();index=JsonNode.Parse(raw["window.archiveIndex=".Length..].TrimEnd(';'))!.AsObject();
            }else{string raw=await File.ReadAllTextAsync(Path.Combine(project.Reading,"archive-index.js"));index=JsonNode.Parse(raw["window.archiveIndex=".Length..].TrimEnd(';'))!.AsObject();}
            if(current!=null)project=project with{ProtectionKnown=true,Protection=current.Selection==null?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.Selected,IncludeMedia=current.IncludeMedia};
            var settings=new ArchiveProtectionWindow(project,index,current){Owner=this};if(settings.ShowDialog()!=true)return;
            if(settings.Mode!=ChatArchiveProtectionMode.None&&password==null){var create=new VaultPasswordWindow(true,false){Owner=this};if(create.ShowDialog()!=true)return;password=create.Password;create.ClearSecret();}
            pendingArchive=project;pendingArchiveRequest=new(Protection:settings.Settings,Mode:settings.Mode,UseExistingBackup:true,ProtectionOnly:true);
            if(!Confirm(L.T(settings.Mode==ChatArchiveProtectionMode.None?"ArchiveRemoveProtectionConfirm":"ArchiveProtectConfirm")))return;
            await StartArchiveUpdate(password);
        }finally{password=null;archivePreparing=false;}
    }
}
