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
    private PasswordBox? archivePassword,archivePasswordConfirmation;
    private TextBox? readingRootInput;
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
        if(directChatSource&&activeChat!=null&&readingDirectory!=null){
            string directId=ChatArchiveProject.Identity(activeChat.Platform,activeChat.Directory);
            var directProject=archiveProjects.GetValueOrDefault(directId)??ChatArchiveProject.Create(activeChat.Account,activeChat.Platform,activeChat.Directory,preferences.BackupRoot);
            SaveArchive(directProject with{Reading=readingDirectory,ReadableAt=DateTimeOffset.UtcNow,Protection=ChatVault.Exists(readingDirectory)?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None});return;
        }
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
    private ChatArchiveProject SelectedArchive()=>selectedHomeSource is {Project:{} project}?archiveProjects[project.Id]:archiveTable?.SelectedItem is ArchiveRow row?archiveProjects[row.Project.Id]:throw new InvalidOperationException(L.T("ArchiveSelect"));
    private void MigrateArchiveReaderPreferences(ChatArchiveProject project){if(project.Reading==null)return;string root=Path.Combine(dataRoot,"reader-preferences"),old=Path.Combine(root,JsonFiles.Hash(PathPolicy.Normalize(project.Reading).ToUpperInvariant())+".json"),target=Path.Combine(root,JsonFiles.Hash(new{archive=project.Id})+".json");if(!File.Exists(target)&&File.Exists(old)){PathPolicy.AssertNoLinks(old);Directory.CreateDirectory(root);File.Copy(old,target,false);}}
    private async Task VerifyArchiveBackup(ChatArchiveProject project){
        if(project.NativeArchive!=null){await RunStoreAction("verify",new("",project.NativeArchive,"",true));return;}if(project.Manifest==null)return;
        var progress=Progress();bool completed=false;
        await RunPausable(async token=>{await Task.Run(()=>ChatExporter.VerifyBackup(project.Manifest,progress,token),token);token.ThrowIfCancellationRequested();completed=true;});
        if(completed){status.Text=L.T("VerifyBackupComplete");operationView.AddLog(status.Text);}
    }
    private ChatArchiveTask? PendingState(ChatArchiveProject project)=>project.TaskFile!=null&&File.Exists(project.TaskFile)?JsonFiles.Read<ChatArchiveTask>(project.TaskFile):null;
    private bool ArchiveNeedsPassword(ChatArchiveProject project,ChatArchiveUpdateRequest request)=>request.CreateCompact?(request.Mode??project.Protection)!=ChatArchiveProtectionMode.None:!request.BackupOnly&&(request.Mode is ChatArchiveProtectionMode.All or ChatArchiveProtectionMode.Selected||PendingState(project)?.Secure==true||project.Protection!=ChatArchiveProtectionMode.None||project.Reading!=null&&(ChatVault.Exists(project.Reading)||ArchiveStore.Exists(project.Reading)&&ArchiveStore.NeedsPassword(project.Reading)));
    private bool ArchiveNeedsDatabase(ChatArchiveProject project,ChatArchiveUpdateRequest request)=>!request.BackupOnly&&!request.ProtectionOnly&&PendingState(project)?.ProtectionOnly!=true&&(PendingState(project)?.Phase is null or "source" or "backup" or "workspace" or "reading")&&NeedsKey();
    private async Task CreateArchiveFromSetup(){
        RegisterCurrentArchive();var project=archiveProjects[ChatArchiveProject.Identity(Account().Platform,Account().Directory)];
        project=project with{VerifyBackupBeforeReading=verifyChatBackup};SaveArchive(project);
        pendingArchive=project;pendingArchiveRequest=new(UseExistingBackup:true);string? password=null;
        try{if(encryptAfterExport){var dialog=new VaultPasswordWindow(true,true){Owner=this};if(dialog.ShowDialog()!=true)return;password=dialog.Password;dialog.ClearSecret();pendingArchiveRequest=new(Protection:new(dialog.IncludeMedia),Mode:ChatArchiveProtectionMode.All,UseExistingBackup:true){KeyMode=dialog.KeyMode};}
            await StartArchiveUpdate(password);
        }finally{password=null;}
    }
    private async Task PrepareArchiveUpdate(ChatArchiveProject project,ChatArchiveUpdateRequest? request=null){
        if(PendingState(project)!=null&&request!=null)throw new InvalidOperationException(L.T("ArchiveFinishTaskFirst"));
        if(archivePreparing||operation!=null)return;
        if(archiveProjects.TryGetValue(project.Id,out var updated))project=updated;
        if(PendingState(project) is {Version:2,ProtectionOnly:true}){await RepackCompact(project);return;}
        archivePreparing=true;try{
        request??=new(BackupOnly:PendingState(project)?.BackupOnly==true,DirectReading:PendingState(project)?.DirectReading==true){SourceSnapshot=PendingState(project)?.SourceSnapshot,CreateCompact=PendingState(project)?.CreateCompact==true};
        if(request.BackupOnly){pendingArchive=project;pendingArchiveRequest=request;activeChat=new(project.Platform,project.Source,project.Account,Path.Combine(project.Source,"source.db"),0);activeManifest=project.Manifest;directChatSource=false;chatStep=project.BackupStorage==ChatStorageFormat.Compact?6:4;RenderChatStage();return;}
        directChatSource=request.DirectReading||PendingState(project)?.DirectReading==true; pendingArchive=project;pendingArchiveRequest=request;
        activeManifest=project.Manifest;readingDirectory=project.Reading;
        if(directChatSource){
            if(project.TaskFile==null&&string.IsNullOrWhiteSpace(project.ReadingRoot)){project=project with{ReadingRoot=!string.IsNullOrWhiteSpace(preferences.ArchiveRoot)?preferences.ArchiveRoot:project.BackupRoot};pendingArchive=project;}
            string? sourceDb=await Task.Run(()=>ChatExporter.FindDatabases(request.SourceSnapshot??project.Source,project.Platform).FirstOrDefault());if(windowClosed)return;if(sourceDb==null&&PendingState(project)?.Phase is not ("protection" or "publish" or "published"))throw new InvalidOperationException(L.T("NoAccounts"));sourceDb??=Path.Combine(project.Source,"unavailable.db");
            if(activeChat?.Directory!=(request.SourceSnapshot??project.Source)||activeChat.Platform!=project.Platform)SetCapturedKey(null);activeChat=new(project.Platform,request.SourceSnapshot??project.Source,project.Account,sourceDb,File.Exists(sourceDb)?new FileInfo(sourceDb).Length:0);chatStep=4;RenderChatStage();return;
        }
        if(project.Manifest==null){
            if(request?.UseExistingBackup==true)throw new InvalidOperationException(L.T("ArchiveFirstBackup"));
            string? liveDatabase=await Task.Run(()=>ChatExporter.FindDatabases(project.Source,project.Platform).FirstOrDefault());
            if(windowClosed)return;
            if(liveDatabase==null)throw new InvalidOperationException(L.T("NoAccounts"));
            activeChat=new(project.Platform,project.Source,project.Account,liveDatabase,new FileInfo(liveDatabase).Length);SetCapturedKey(null);
            directChatSource=request?.BackupOnly!=true;chatStep=directChatSource?2:1;RenderChatStage();return;
        }
        var journal=JsonFiles.Read<TransferJournal>(project.Manifest);string? database=await Task.Run(()=>ChatExporter.FindDatabases(journal.Plan.Destination,project.Platform).FirstOrDefault());if(windowClosed)return;if(database==null)throw new InvalidDataException(L.T("NoAccounts"));
        if(activeChat?.Directory!=project.Source||activeChat?.Platform!=project.Platform)SetCapturedKey(null);
        activeChat=new(project.Platform,project.Source,project.Account,TransferService.Child(project.Source,Path.GetRelativePath(journal.Plan.Destination,database)),new FileInfo(database).Length);preparedArchive=ReadChatArchive(project.Manifest,activeChat,journal);
        chatStep=4;RenderChatStage();return;
        }finally{archivePreparing=false;}
    }
    private void BuildArchiveAccess(){
        var project=pendingArchive??throw new InvalidOperationException();var request=pendingArchiveRequest??new();
        var draft=project.TaskFile==null?accessDrafts.GetValueOrDefault(project.Id):null;
        if(draft?.Request is {} savedRequest){request=request with{Mode=savedRequest.Mode,KeyMode=savedRequest.KeyMode,BrowserReading=savedRequest.BrowserReading};pendingArchiveRequest=request;}
        accessDraftOwner=project.Id;bool direct=request.DirectReading||PendingState(project)?.DirectReading==true;bool fresh=request.CreateCompact;
        var stack=View.Stack(View.Field(request.SourceSnapshot!=null?"RawBackupLabel":direct||request.BackupOnly?"AccountFolderLabel":"RawBackupLabel",View.Input(request.SourceSnapshot!=null?project.NativeArchive??project.Manifest??project.Source:direct||request.BackupOnly?project.Source:ArchiveDirectory(),true)));
        if(fresh)stack.Children.Insert(0,View.Text(L.T("StorageCompact"),13,"Muted"));
        chatContent.Children.Add(View.FormScroll(stack));
        if(!request.BackupOnly&&project.TaskFile==null){readingRootInput=View.Input(draft?.ReadingRoot??project.ReadingRoot);var path=new DockPanel();var browse=Button("Browse",()=>{string folder=View.Folder(this,readingRootInput.Text,"reading-output-root");if(folder!="")readingRootInput.Text=folder;});browse.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(browse,Dock.Right);path.Children.Add(browse);path.Children.Add(readingRootInput);stack.Children.Add(View.Field("ReadingOutputRoot",path));}
        TextBlock? databaseHint=null;bool databaseRequired=ArchiveNeedsDatabase(project,request);
        if(databaseRequired){
            var methods=View.Row(Button("KeyLibrary",OpenKeyLibrary,true),AsyncButton("KeyImport",ImportChatKey));
            var acquireMenu=new ContextMenu();var acquire=Button("AcquireDatabaseKey",()=>acquireMenu.IsOpen=true);acquireMenu.PlacementTarget=acquire;acquire.ContextMenu=acquireMenu;
            void Method(string key,Func<Task> action){var item=new MenuItem{Header=L.T(key)};item.Click+=async(_,_)=>{try{await action();}catch(Exception ex){Error(ex);}};acquireMenu.Items.Add(item);}
            Method("ReadPassive",CapturePassive);Method(Account().Platform==ChatPlatform.QQ?"ReadQQLogin":"ReadWeChatLogin",CaptureActive);methods.Children.Add(acquire);
            if(capturedKey!=null)methods.Children.Add(Button("KeyManage",ManageChatKey));stack.Children.Add(View.Field("ExportAccessSection",methods));
            keyInput=View.SecretInput();keyInput.Name="archive_database_key";
            stack.Children.Add(new Expander{Header=L.T("ManualKey"),Content=View.Field("DatabaseKeyLabel",keyInput),Margin=new Thickness(0,8,0,4)});
            databaseHint=View.Text(L.T(capturedKey==null?"DatabaseAccessHint":"KeyReady"),color:"Muted");stack.Children.Add(databaseHint);
        }

        bool existingProtected=!fresh&&project.Reading!=null&&(ChatVault.Exists(project.Reading)||ArchiveStore.Exists(project.Reading)&&ArchiveStore.NeedsPassword(project.Reading));
        bool needsPassword=ArchiveNeedsPassword(project,request);
        archivePassword=null;archivePasswordConfirmation=null;
        if(direct&&project.TaskFile==null&&(fresh||project.Reading==null||project.ReadingStorage==ChatStorageFormat.Folder)){
            archivePassword=View.SecretInput();archivePassword.Name="archive_unlock";archivePasswordConfirmation=existingProtected?null:View.SecretInput();
            var credentialField=View.Field(existingProtected?"VaultCredential":"VaultPassword",archivePassword);var confirmationField=View.Field("VaultConfirmPassword",archivePasswordConfirmation??View.SecretInput());var passwordFields=View.Stack(credentialField);
            if(!existingProtected){
                var protection=new ComboBox{Name="archive_key_mode",ItemsSource=new[]{L.T("ArchiveProtectionNone"),L.T("VaultWrappedMode"),L.T("VaultDirectMode")},SelectedIndex=needsPassword?(request.KeyMode==ChatVaultKeyMode.DirectKey?2:1):0,ToolTip=L.T("VaultKeyModeHelp")};
                stack.Children.Add(View.Field("HubReadingProtection",protection));
                void SetProtection(){bool encrypted=protection.SelectedIndex>0,raw=protection.SelectedIndex==2;pendingArchiveRequest=(pendingArchiveRequest??request) with{Mode=encrypted?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.None,KeyMode=raw?ChatVaultKeyMode.DirectKey:ChatVaultKeyMode.Password};credentialField.Children.OfType<Label>().Single().Content=L.T(raw?"VaultDirectKey":"VaultPassword");confirmationField.Children.OfType<Label>().Single().Content=L.T(raw?"VaultConfirmKey":"VaultConfirmPassword");passwordFields.Visibility=encrypted?Visibility.Visible:Visibility.Collapsed;}
                SetProtection();protection.SelectionChanged+=(_,_)=>{SetProtection();archivePassword.Clear();archivePasswordConfirmation?.Clear();};passwordFields.Children.Add(confirmationField);
            }else passwordFields.Visibility=needsPassword?Visibility.Visible:Visibility.Collapsed;
            stack.Children.Add(passwordFields);
        }else if(needsPassword){archivePassword=View.SecretInput();archivePassword.Name="archive_unlock";stack.Children.Add(View.Field("VaultCredential",archivePassword));}
        if(!direct&&!request.BackupOnly){
            var verify=new CheckBox{Content=L.T("VerifyBackupBeforeExport"),Name="archive_verify_before",Margin=new Thickness(0,10,0,8),ToolTip=L.T("VerifyBackupHelp")};
            verify.Click+=(_,_)=>Guard(()=>{var p=archiveProjects[project.Id];bool enabled=verify.IsChecked==true;if(PendingState(p) is {} task){if(!PathPolicy.Under(p.TaskFile!,Path.Combine(dataRoot,"archive-tasks")))throw new IOException("Unexpected task location.");PathPolicy.AssertNoLinks(p.TaskFile!);JsonFiles.Write(p.TaskFile!,task with{VerifyBackupBeforeReading=enabled});}p=p with{VerifyBackupBeforeReading=enabled};SaveArchive(p);pendingArchive=p;});
            verify.IsChecked=PendingState(project)?.VerifyBackupBeforeReading??project.VerifyBackupBeforeReading;stack.Children.Add(verify);
        }
        var workers=ChatWorkerSelector("export",()=>readingRootInput?.Text??(direct?project.ReadingRoot:project.BackupRoot));
        stack.Children.Add(direct&&project.Reading==null&&project.TaskFile==null?StorageSelector(project,false,workers):View.Field("ChatWorkers",workers));
        var taskDetails=View.Stack(WorkDirectoryField(project),View.Text(L.T(direct?"DirectReadingHelp":"ArchiveAccessHelp"),color:"Muted"));
        if(project.ReadingStorage==ChatStorageFormat.Compact&&!request.BackupOnly){var browser=new CheckBox{Content=L.T("StandaloneCreate"),IsChecked=request.BrowserReading||project.Reading!=null&&File.Exists(Path.Combine(project.Reading,ArchiveStore.StandaloneEntry)),ToolTip=L.T("StandaloneEnableHelp"),Margin=new Thickness(0,10,0,4),Name="archive_browser_reading"};browser.Click+=(_,_)=>pendingArchiveRequest=(pendingArchiveRequest??request) with{BrowserReading=browser.IsChecked==true};taskDetails.Children.Add(browser);}
        stack.Children.Add(new Expander{Header=L.T("TaskDetails"),Margin=new Thickness(0,8,0,0),Content=taskDetails});
        chatFooter.Children.Add(Button("GoBack",()=>{chatStep=-1;RenderChatStage();}));
        var begin=AsyncButton(project.TaskFile!=null?"ArchiveContinue":direct?"StartReadingTask":"ArchiveStart",async()=>{string? password=archivePassword?.Password;if(archivePasswordConfirmation?.IsVisible==true&&!string.IsNullOrEmpty(password)){try{ChatVault.ValidateCredential(password,pendingArchiveRequest?.KeyMode??ChatVaultKeyMode.Password);}catch(ArgumentException){throw new InvalidOperationException(L.T(pendingArchiveRequest?.KeyMode==ChatVaultKeyMode.DirectKey?"VaultDirectKeyInvalid":"VaultPasswordMismatch"));}if(password!=archivePasswordConfirmation.Password)throw new InvalidOperationException(L.T(pendingArchiveRequest?.KeyMode==ChatVaultKeyMode.DirectKey?"VaultDirectKeyMismatch":"VaultPasswordMismatch"));}await StartArchiveUpdate(password);password=null;},true);begin.Name="archive_start";chatFooter.Children.Add(begin);
        if(archivePassword!=null)archivePassword.Password=draft?.Password??"";if(archivePasswordConfirmation!=null)archivePasswordConfirmation.Password=draft?.Confirmation??"";if(keyInput!=null)keyInput.Password=draft?.DatabaseKey??"";
        var hint=databaseHint??View.Text("",12,"Muted");hint.Name="archive_required_fields";if(databaseHint==null){hint.Margin=new Thickness(0,8,0,0);stack.Children.Add(hint);}
        void Validate(){
            string? missing=null;bool required=existingProtected||PendingState(project)?.Secure==true||((pendingArchiveRequest??request).Mode??project.Protection)!=ChatArchiveProtectionMode.None;
            if(!string.IsNullOrWhiteSpace(preferences.ChatWorkRoot)&&!ValidFolderText(preferences.ChatWorkRoot))missing=L.T("RequiredFolder",L.T("ChatWorkRoot"));
            else if(!ValidFolderText(readingRootInput?.Text??project.ReadingRoot)&&!request.BackupOnly)missing=L.T("RequiredFolder",L.T("ReadingOutputRoot"));
            else if(databaseRequired&&capturedKey==null&&string.IsNullOrWhiteSpace(keyInput?.Password))missing=L.T("ExportKeyMissing");
            else if(databaseRequired&&capturedKey==null){try{byte[] candidate=ChatDatabaseKey.Parse(keyInput!.Password,project.Platform);System.Security.Cryptography.CryptographicOperations.ZeroMemory(candidate);}catch(FormatException){missing=L.T("KeyFormatInvalid");}}
            if(missing==null&&required&&!request.BackupOnly){try{var mode=existingProtected?(ArchiveStore.Exists(project.Reading!)?ArchiveStore.ReadKeyMode(project.Reading!):ChatVault.ReadKeyMode(project.Reading!)):PendingState(project)?.KeyMode??(pendingArchiveRequest??request).KeyMode;ChatVault.ValidateCredential((archivePassword?.Password??"").AsSpan(),mode);if(archivePasswordConfirmation!=null&&archivePassword?.Password!=archivePasswordConfirmation.Password)missing=L.T(mode==ChatVaultKeyMode.DirectKey?"VaultDirectKeyMismatch":"VaultPasswordMismatch");}catch(ArgumentException){missing=L.T("RequiredCredential");}}
            begin.IsEnabled=missing==null;hint.Text=missing??(databaseHint!=null?L.T(capturedKey==null?"DatabaseAccessHint":"KeyReady"):"");hint.Visibility=hint.Text.Length==0?Visibility.Collapsed:Visibility.Visible;begin.ToolTip=missing;
        }
        if(readingRootInput!=null)readingRootInput.TextChanged+=(_,_)=>Validate();if(keyInput!=null)keyInput.PasswordChanged+=(_,_)=>Validate();if(archivePassword!=null)archivePassword.PasswordChanged+=(_,_)=>Validate();if(archivePasswordConfirmation!=null)archivePasswordConfirmation.PasswordChanged+=(_,_)=>Validate();
        stack.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,new SelectionChangedEventHandler((_,_)=>Validate()));
        stack.AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler((_,_)=>Validate()));
        stack.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,new RoutedEventHandler((_,_)=>Validate()));Validate();
    }
    private async Task StartArchiveUpdate(string? password){
        var project=pendingArchive??throw new InvalidOperationException();var request=(pendingArchiveRequest??new()) with{WorkspaceRoot=preferences.ChatWorkRoot};
        if(readingRootInput!=null){if(string.IsNullOrWhiteSpace(readingRootInput.Text))throw new InvalidOperationException(L.T("NoRoot"));project=project with{ReadingRoot=PathPolicy.Normalize(readingRootInput.Text)};pendingArchive=project;}
        MigrateArchiveReaderPreferences(project);
        if(!request.CreateCompact&&project.TaskFile==null&&project.Reading!=null&&ChatVault.Exists(project.Reading)&&request.Protection==null&&!request.BackupOnly){using var previous=ChatVault.Open(project.Reading,(password??"").AsSpan());var settings=previous.ReadProtection();project=project with{ProtectionKnown=true,Protection=settings.Selection==null?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.Selected,IncludeMedia=settings.IncludeMedia};SaveArchive(project);pendingArchive=project;}
        if(!Confirm((request.CreateCompact?L.T("CompactFreshHelp")+"\n\n":"")+L.T(request.DirectReading?"DirectReadingConfirm":"ArchiveUpdateConfirm",project.Account,project.Source,request.DirectReading?project.ReadingRoot:project.BackupRoot)+"\n"+L.T("ArchiveRawProtectionNotice")+((project.Protection!=ChatArchiveProtectionMode.None||request.Mode is ChatArchiveProtectionMode.All or ChatArchiveProtectionMode.Selected)?"\n"+L.T("ArchiveSecureWorkspaceHelp"):"")))return;
        bool completed=false;byte[]? key=capturedKey?.ToArray();try{
            if(!string.IsNullOrEmpty(keyInput?.Password)){if(key!=null)CryptographicOperations.ZeroMemory(key);key=ChatDatabaseKey.Parse(keyInput.Password,project.Platform);}
            string taskFile=project.TaskFile??Path.Combine(dataRoot,"archive-tasks",project.Id+".json");Directory.CreateDirectory(Path.GetDirectoryName(taskFile)!);
            JsonFiles.Write(Path.Combine(dataRoot,"preferences.json"),preferences);bool automatic=preferences.ChatWorkersAutomatic;int manual=preferences.ChatParallelism;string outputRoot=request.BackupOnly?project.BackupRoot:!string.IsNullOrWhiteSpace(project.ReadingRoot)?project.ReadingRoot:project.BackupRoot;var progress=Progress();
            completed=await RunPausable(async token=>{await Task.Run(()=>ChatArchiveWorkflow.Update(project,taskFile,key,password,ChatWorkers("export",outputRoot),p=>Dispatcher.Invoke(()=>{SaveArchive(p);if(p.Manifest!=null)verifiedBackups[p.Source]=p.Manifest;JsonFiles.Write(Path.Combine(dataRoot,"chat-backups.json"),verifiedBackups);}),request,progress,token,Theme.IsDark,phase=>automatic?WorkerRecommendation.For(phase,phase=="backup"?project.BackupRoot:outputRoot):manual,downloadGroupAvatars:preferences.DownloadQQGroupAvatars),token);if(request.SourceSnapshot!=null){progress.Report(new("archive-input-cleanup",0,0));await Task.Run(()=>CleanCompactInput(request.SourceSnapshot,project.Id));}});
        }finally{if(key!=null)CryptographicOperations.ZeroMemory(key);password=null;if(completed){archivePassword?.Clear();archivePasswordConfirmation?.Clear();keyInput?.Clear();accessDrafts.Remove(project.Id);accessDraftOwner=null;chatStep=-1;RenderChatStage();}else{pendingArchive=archiveProjects.GetValueOrDefault(project.Id)??project;if(PendingState(pendingArchive)!=null&&readingRootInput!=null)readingRootInput.IsReadOnly=true;}}
    }
    private async Task ConfigureArchiveProtection(ChatArchiveProject project){
        if(project.Reading!=null&&ArchiveStore.Exists(project.Reading)){await RepackCompact(project,true);return;}
        if(project.TaskFile!=null&&File.Exists(project.TaskFile))throw new InvalidOperationException(L.T("ArchiveFinishTaskFirst"));
        if(project.Reading==null)throw new InvalidOperationException(L.T("ArchiveReadingPending"));
        if(archivePreparing||operation!=null)return;archivePreparing=true;
        string? password=null;var keyMode=ChatVaultKeyMode.Password;JsonObject index;ChatArchiveProtection? current=null;
        try{
            if(ChatVault.Exists(project.Reading)){
                var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;password=unlock.Password;unlock.ClearSecret();
                using var vault=await Task.Run(()=>ChatVault.Open(project.Reading,password.AsSpan()));try{current=vault.ReadProtection();}catch(InvalidOperationException){}using var input=vault.OpenRead("archive-index.js");using var reader=new StreamReader(input);string raw=await reader.ReadToEndAsync();index=JsonNode.Parse(raw["window.archiveIndex=".Length..].TrimEnd(';'))!.AsObject();
            }else{string raw=await File.ReadAllTextAsync(Path.Combine(project.Reading,"archive-index.js"));index=JsonNode.Parse(raw["window.archiveIndex=".Length..].TrimEnd(';'))!.AsObject();}
            if(current!=null)project=project with{ProtectionKnown=true,Protection=current.Selection==null?ChatArchiveProtectionMode.All:ChatArchiveProtectionMode.Selected,IncludeMedia=current.IncludeMedia};
            var settings=new ArchiveProtectionWindow(project,index,current){Owner=this};if(settings.ShowDialog()!=true)return;
            if(settings.Mode!=ChatArchiveProtectionMode.None&&password==null){var create=new VaultPasswordWindow(true,false){Owner=this};if(create.ShowDialog()!=true)return;password=create.Password;keyMode=create.KeyMode;create.ClearSecret();}
            if(string.IsNullOrWhiteSpace(project.ReadingRoot))project=project with{ReadingRoot=Path.GetDirectoryName(project.Reading)!};
            pendingArchive=project;pendingArchiveRequest=new(Protection:settings.Settings,Mode:settings.Mode,UseExistingBackup:true,ProtectionOnly:true,DirectReading:true){KeyMode=keyMode};
            if(!Confirm(L.T(settings.Mode==ChatArchiveProtectionMode.None?"ArchiveRemoveProtectionConfirm":"ArchiveProtectConfirm")))return;
            await StartArchiveUpdate(password);
        }finally{password=null;archivePreparing=false;}
    }
    private async Task RemoveArchiveProtection(ChatArchiveProject project){
        if(project.Reading!=null&&ArchiveStore.Exists(project.Reading)){await RepackCompact(project,false);return;}
        if(PendingState(project)!=null)throw new InvalidOperationException(L.T("HubFinishPending"));
        if(project.Reading==null||!ChatVault.Exists(project.Reading))return;
        var dialog=new VaultPasswordWindow(false,false){Owner=this};if(dialog.ShowDialog()!=true)return;string? password=dialog.Password;dialog.ClearSecret();
        try{
            using(var vault=await Task.Run(()=>ChatVault.Open(project.Reading,password.AsSpan()))){ }
            if(!Confirm(L.T("ArchiveRemoveProtectionConfirm")))return;
            if(string.IsNullOrWhiteSpace(project.ReadingRoot))project=project with{ReadingRoot=Path.GetDirectoryName(project.Reading)!};
            pendingArchive=project;pendingArchiveRequest=new(Protection:new(project.IncludeMedia),Mode:ChatArchiveProtectionMode.None,ProtectionOnly:true,DirectReading:true);await StartArchiveUpdate(password);
        }finally{password=null;dialog.ClearSecret();}
    }

}
