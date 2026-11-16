using System.Security.Cryptography;
using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private async Task ExportConfiguration(){
        if(operation!=null)return;var options=new ConfigurationBackupWindow{Owner=this};if(options.ShowDialog()!=true)return;
        var save=new Microsoft.Win32.SaveFileDialog{Title=L.T("ConfigurationExport"),Filter="Storloom configuration|*.storloom-config",FileName="storloom-config-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".storloom-config"};if(PickerHistory.Show(save,this,"configuration-export")!=true)return;
        string? password=null;try{if(options.Encrypt){var dialog=new KeyPasswordWindow(true,true){Owner=this};if(dialog.ShowDialog()!=true)return;password=dialog.Password;dialog.ClearSecret();}
            await Run(async token=>{await Task.Run(()=>{token.ThrowIfCancellationRequested();var snapshot=ConfigurationBackup.Capture(dataRoot,options.Scope,token);ConfigurationBackup.Save(save.FileName,snapshot,password.AsSpan(),token);},token);status.Text=L.T("ConfigurationExportDone",save.FileName);});
        }finally{password=null;}
    }
    private async Task ImportConfiguration(){
        if(operation!=null)return;if(archiveProjects.Values.Any(p=>PendingState(p)!=null))throw new InvalidOperationException(L.T("ConfigurationPending"));
        var picker=new Microsoft.Win32.OpenFileDialog{Title=L.T("ConfigurationImport"),Filter="Storloom configuration|*.storloom-config"};if(PickerHistory.Show(picker,this,"configuration-import")!=true)return;
        ConfigurationSnapshot? snapshot=null;string? password=null;
        try{if(ConfigurationBackup.IsEncrypted(picker.FileName)){var unlock=new KeyPasswordWindow(false,true){Owner=this};if(unlock.ShowDialog()!=true)return;password=unlock.Password;unlock.ClearSecret();}
            bool opened=await Run(async token=>snapshot=await Task.Run(()=>ConfigurationBackup.Open(picker.FileName,password.AsSpan()),token));if(!opened||snapshot==null)return;
        }finally{password=null;}
        var prefs=snapshot.Items.SingleOrDefault(i=>i.Path=="preferences.json");if(prefs!=null){var candidate=JsonSerializer.Deserialize<AppPreferences>(prefs.Content,JsonFiles.Options)??throw new InvalidDataException(L.T("ConfigurationInvalid"));if(!Enum.IsDefined(candidate.Theme)||candidate.ChatParallelism is <1 or >32||!double.IsFinite(candidate.SidebarWidth)||candidate.SidebarWidth is <0 or >10000||!double.IsFinite(candidate.LogHeight)||candidate.LogHeight is <32 or >10000||candidate.ChatSearchRoots==null||candidate.ChatSearchRoots.Any(p=>p==null)||candidate.BackupRoot==null||candidate.ArchiveRoot==null||candidate.ChatWorkRoot==null)throw new InvalidDataException(L.T("ConfigurationInvalid"));}
        var review=new ConfigurationBackupWindow(dataRoot,snapshot){Owner=this};if(review.ShowDialog()!=true||review.Plan==null)return;
        var plan=review.Plan;bool restored=await Run(async token=>{var result=await Task.Run(()=>ConfigurationBackup.Restore(plan,token),token);status.Text=L.T("ConfigurationRestoreDone",result.Files,result.RecoveryDirectory);operationView.AddLog(status.Text);});if(!restored)return;
        // Update in-memory preferences too: otherwise closing the window would overwrite restored values.
        if(File.Exists(Path.Combine(dataRoot,"preferences.json")))preferences=JsonFiles.Read<AppPreferences>(Path.Combine(dataRoot,"preferences.json"));
        SetCapturedKey(null);accessDrafts.Clear();retainedChatSections.Clear();rawDraftOwner=null;accessDraftOwner=null;backupInput=null;readingRootInput=null;pendingArchive=null;
        verifiedBackups.Clear();string backups=Path.Combine(dataRoot,"chat-backups.json");if(File.Exists(backups))foreach(var entry in JsonFiles.Read<Dictionary<string,string>>(backups))verifiedBackups[entry.Key]=entry.Value;
        var history=operationView;activeChat=null;selectedArchiveId=keyAccountId=null;pendingArchiveRequest=null;LoadArchiveProjects();chatStep=-1;L.SetLanguage(preferences.Language);Theme.Apply(preferences);ReloadEngine();BuildUi();operationView.RestoreHistory(history);Navigate("chats");
    }
    private static bool NeedsArchiveSecret(string directory)=>ArchiveStore.Exists(directory)?ArchiveStore.NeedsPassword(directory):ChatVault.Exists(directory);
    private string? AskArchiveSecret(string directory){if(!NeedsArchiveSecret(directory))return "";var unlock=new VaultPasswordWindow(false,false){Owner=this,Title=L.T("VaultUnlock")+" · "+Path.GetFileName(directory)};unlock.Caption.Title=unlock.Title;if(unlock.ShowDialog()!=true)return null;string? secret=unlock.Password;unlock.ClearSecret();return secret;}
    private async Task CompareArchive(string? first=null,string? second=null){
        if(operation!=null)return;
        string? Pick(string title){var picker=new Microsoft.Win32.OpenFileDialog{Title=L.T(title),Filter="Storloom archive|archive.metadata;archive.storloom.json;archive.json;index.html;messages.html;vault.storloom.json"};return PickerHistory.Show(picker,this,"archive-comparison-input")==true?ArchiveMetadata.Inspect(picker.FileName).Directory:null;}
        first??=Pick("ArchiveCompareLeft");if(first==null)return;second??=Pick("ArchiveCompareRight");if(second==null)return;
        string? leftSecret=AskArchiveSecret(first),rightSecret=null;if(leftSecret==null)return;
        try{
            ArchiveOverview? left=null,right=null;bool reused=false;
            bool ready=await Run(async token=>{
                left=await Task.Run(()=>ArchiveDiagnostics.Overview(first,leftSecret.AsSpan(),Progress(),token),token);
                try{right=await Task.Run(()=>ArchiveDiagnostics.Overview(second,leftSecret.AsSpan(),Progress(),token),token);reused=true;}catch(Exception ex)when(ex is CryptographicException or ArgumentException){}
            });if(!ready||left==null)return;
            if(!reused){rightSecret=AskArchiveSecret(second);if(rightSecret==null)return;if(!await Run(async token=>right=await Task.Run(()=>ArchiveDiagnostics.Overview(second,rightSecret.AsSpan(),Progress(),token),token)))return;}
            if(right!=null)new ArchiveComparisonWindow(ArchiveDiagnostics.Compare(left,right)){Owner=this}.ShowDialog();
        }finally{leftSecret=rightSecret=null;}
    }
    private string IntegrityFile(string directory)=>Path.Combine(dataRoot,"archive-reports",JsonFiles.Hash(PathPolicy.Normalize(directory).ToUpperInvariant())+".json");
    private string IntegritySummary(string directory,bool original=false){
        try{string file=IntegrityFile(directory);if(!File.Exists(file))return L.T("ArchiveIntegrityNever");var report=JsonFiles.Read<ArchiveIntegrityReport>(file);bool same=report.Snapshot==(original?ArchiveDiagnostics.OriginalSnapshot(directory):ArchiveDiagnostics.Snapshot(directory));return L.T(same?"ArchiveIntegrityLast":"ArchiveIntegrityStale",L.T("ArchiveIntegrityOutcome"+report.Outcome),report.FinishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));}
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException){return L.T("ArchiveIntegrityNever");}
    }
    private async Task VerifyOriginalIntegrity(string manifest){
        ArchiveIntegrityReport? report=null;await RunPausable(async token=>{report=await Task.Run(()=>ArchiveDiagnostics.VerifyOriginal(manifest,Progress(),token),token);JsonFiles.Write(IntegrityFile(manifest),report);status.Text=L.T("ArchiveIntegrityOutcome"+report.Outcome);if(report.Outcome!="verified")operationIncomplete=true;});
        if(report!=null){ShowIntegrityReport(report);if(chatStep<0)RenderChatStage();}
    }
    private async Task VerifyReadingIntegrity(string directory){
        if(operation!=null)return;string? secret=AskArchiveSecret(directory);if(secret==null)return;ArchiveIntegrityReport? report=null;
        try{await RunPausable(async token=>{report=await Task.Run(()=>ArchiveDiagnostics.Verify(directory,secret.AsSpan(),Progress(),token),token);JsonFiles.Write(IntegrityFile(directory),report);status.Text=L.T("ArchiveIntegrityOutcome"+report.Outcome);if(report.Outcome!="verified")operationIncomplete=true;});}
        finally{secret=null;}
        if(report!=null){ShowIntegrityReport(report);if(chatStep<0)RenderChatStage();}
    }
    private void ShowIntegrityReport(ArchiveIntegrityReport report){
        var window=new ThemeWindow(L.T("ArchiveIntegrity"),800,560){Owner=this};var grid=View.Grid(View.Auto,View.Fill,View.Auto);grid.Margin=new Thickness(24);window.Body.Children.Add(grid);
        View.Add(grid,View.Text(L.T("ArchiveIntegrityReportHelp"),color:"Muted"),0);
        var table=View.Table(("ConfigurationItem","Item",190),("ConfigurationEffect","Value",0));table.ItemsSource=new[]{new{Item=L.T("Path"),Value=report.Directory},new{Item=L.T("ArchiveIntegrityResult"),Value=L.T("ArchiveIntegrityOutcome"+report.Outcome)},new{Item=L.T("ArchiveIntegrityScope"),Value=L.T("ArchiveIntegrityScope"+report.Scope)},new{Item=L.T("ArchiveHistoryRecordedAt"),Value=report.FinishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")},new{Item=L.T("ArchiveIntegrityFiles"),Value=report.CheckedFiles.ToString("N0")},new{Item=L.T("ArchiveResourceBytes"),Value=View.Size(report.CheckedBytes)},new{Item=L.T("ArchiveMissingAtExport"),Value=report.MissingMedia?.ToString("N0")??L.T("ArchiveOriginUnknown")},new{Item=L.T("ArchiveIntegrityDetail"),Value=report.Detail==null?L.T("ArchiveIntegrityNoDetail"):L.T("ArchiveIntegrityDetail"+report.Detail)}};View.Add(grid,table,1);
        var save=View.Button("ArchiveSaveReport",()=>{var picker=new Microsoft.Win32.SaveFileDialog{Filter="JSON|*.json",FileName="archive-integrity-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"};if(PickerHistory.Show(picker,window,"archive-integrity-report")==true)try{PathPolicy.AssertNoLinks(Path.GetDirectoryName(picker.FileName)!);if(File.Exists(picker.FileName)){PathPolicy.AssertNoLinks(picker.FileName);if(NativeFile.ReadStamp(picker.FileName).Links!=1)throw new IOException("Shared report file.");}JsonFiles.Write(picker.FileName,report);}catch(Exception ex){Notice.Show(window,L.T("Error"),ex.Message);}});
        var buttons=View.Row(save,View.Button("Close",window.Close));buttons.HorizontalAlignment=HorizontalAlignment.Right;View.Add(grid,buttons,2);window.ShowDialog();
    }
}
