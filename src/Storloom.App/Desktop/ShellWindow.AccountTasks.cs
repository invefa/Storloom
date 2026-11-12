using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private void BuildChatTasks(){
        var account=Account();var stack=View.Stack(View.Field("AccountFolderLabel",View.Input(account.Directory,true)));
        var cards=new Grid{Margin=new Thickness(0,16,0,0)};cards.ColumnDefinitions.Add(new ColumnDefinition());cards.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(16)});cards.ColumnDefinitions.Add(new ColumnDefinition());
        var reading=View.Stack(View.Text(L.T("ReadingTaskHeading"),17),View.Text(L.T("ReadingTaskHelp"),color:"Muted"));
        var local=AsyncButton("ReadingFromAccount",()=>PrepareIndependentReading(account));local.Name="choose_reading_task";local.IsEnabled=Directory.Exists(account.Directory);reading.Children.Add(local);
        string? manifest=activeManifest??verifiedBackups.GetValueOrDefault(account.Directory);
        var backupSource=AsyncButton("ReadingFromBackup",()=>UseChatBackup(manifest!));backupSource.IsEnabled=manifest!=null&&File.Exists(manifest);reading.Children.Add(backupSource);
        var raw=View.Stack(View.Text(L.T("RawBackupHeading"),17),View.Text(L.T("RawBackupTaskHelp"),color:"Muted"));
        var create=Button("RawBackupHeading",()=>{directChatSource=false;readingDirectory=null;chatStep=6;RenderChatStage();});create.Name="choose_raw_backup_task";create.IsEnabled=Directory.Exists(account.Directory);raw.Children.Add(create);
        foreach(var button in reading.Children.OfType<Button>().Concat(raw.Children.OfType<Button>()))button.Margin=new Thickness(0,6,0,0);
        var readingCard=View.Card(reading,16);var rawCard=View.Card(raw,16);View.Add(cards,readingCard,0);View.Add(cards,rawCard,0,2);stack.Children.Add(cards);
        stack.Children.Add(new Expander{Header=L.T("IndependentOutputsTitle"),Content=View.Text(L.T("IndependentOutputsHelp"),color:"Muted"),Margin=new Thickness(0,16,0,0)});
        chatContent.Children.Add(View.FormScroll(stack));
        chatFooter.Children.Add(Button("GoBack",()=>{chatStep=0;RenderChatStage();}));
    }
    private Task PrepareIndependentReading(ChatLocation account){
        var id=ChatArchiveProject.Identity(account.Platform,account.Directory);
        var project=archiveProjects.GetValueOrDefault(id)??new ChatArchiveProject(id,account.Account,account.Platform,account.Directory,preferences.BackupRoot);
        if(string.IsNullOrWhiteSpace(project.ReadingRoot))project=project with{ReadingRoot=!string.IsNullOrWhiteSpace(preferences.ArchiveRoot)?preferences.ArchiveRoot:project.BackupRoot};
        pendingArchive=project;pendingArchiveRequest=new(DirectReading:true);activeChat=account;directChatSource=true;preparedArchive=null;SetCapturedKey(null);chatStep=4;RenderChatStage();return Task.CompletedTask;
    }
    private async Task OpenAccountTasks(ChatArchiveProject project){
        if(PendingState(project)!=null){await PrepareArchiveUpdate(project);return;}
        string? database=await Task.Run(()=>Directory.Exists(project.Source)?ChatExporter.FindDatabases(project.Source,project.Platform).FirstOrDefault():null);
        if(database==null&&project.Manifest!=null){var manifest=JsonFiles.Read<TransferJournal>(project.Manifest);var saved=ChatExporter.FindDatabases(manifest.Plan.Destination,project.Platform).FirstOrDefault();if(saved!=null)database=TransferService.Child(project.Source,Path.GetRelativePath(manifest.Plan.Destination,saved));}
        activeChat=new(project.Platform,project.Source,project.Account,database??Path.Combine(project.Source,"unavailable.db"),0);activeManifest=project.Manifest;pendingArchive=project;readingDirectory=project.Reading;SetCapturedKey(null);chatStep=1;RenderChatStage();
    }
    private void ConnectStore(){var picker=new Microsoft.Win32.OpenFileDialog{Filter="Storloom archive|archive.json"};if(PickerHistory.Show(picker,this,"store-load")!=true)return;SaveStore(new("",Path.GetDirectoryName(picker.FileName)!,"",true));chatStep=-1;RenderChatStage();}
    private void ConnectReader(){
        var picker=new Microsoft.Win32.OpenFileDialog{Filter="Storloom reading copy|messages.html;vault.storloom.json"};if(PickerHistory.Show(picker,this,"connect-reader")!=true)return;
        string path=Path.GetDirectoryName(picker.FileName)!;var locationsFile=Path.Combine(dataRoot,"connected-readers.json");var entries=File.Exists(locationsFile)?JsonFiles.Read<string[]>(locationsFile).ToList():[];
        if(!entries.Contains(path,StringComparer.OrdinalIgnoreCase))entries.Add(path);JsonFiles.Write(locationsFile,entries);chatStep=-1;RenderChatStage();
    }
}
