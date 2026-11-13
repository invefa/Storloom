using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private Task PrepareIndependentReading(ChatLocation account){
        var id=ChatArchiveProject.Identity(account.Platform,account.Directory);
        var project=archiveProjects.GetValueOrDefault(id)??ChatArchiveProject.Create(account.Account,account.Platform,account.Directory,preferences.BackupRoot);
        if(string.IsNullOrWhiteSpace(project.ReadingRoot))project=project with{ReadingRoot=!string.IsNullOrWhiteSpace(preferences.ArchiveRoot)?preferences.ArchiveRoot:project.BackupRoot};
        SaveArchive(project);pendingArchive=project;pendingArchiveRequest=new(DirectReading:true);activeChat=account;directChatSource=true;preparedArchive=null;SetCapturedKey(null);chatStep=4;RenderChatStage();return Task.CompletedTask;
    }
    private async void ConnectStore(){try{var picker=new Microsoft.Win32.OpenFileDialog{Filter="Storloom archive|archive.json"};if(PickerHistory.Show(picker,this,"store-load")!=true)return;string directory=Path.GetDirectoryName(picker.FileName)!;if(ArchiveStore.IsNative(directory))await ConnectCompactNative(directory);else SaveStore(new("",directory,"",true));chatStep=-1;RenderChatStage();}catch(Exception ex){Error(ex);}}
    private void ConnectReader(){
        var picker=new Microsoft.Win32.OpenFileDialog{Filter="Storloom reading copy|messages.html;vault.storloom.json"};if(PickerHistory.Show(picker,this,"connect-reader")!=true)return;
        string path=Path.GetDirectoryName(picker.FileName)!;var locationsFile=Path.Combine(dataRoot,"connected-readers.json");var entries=File.Exists(locationsFile)?JsonFiles.Read<string[]>(locationsFile).ToList():[];
        if(!entries.Contains(path,StringComparer.OrdinalIgnoreCase))entries.Add(path);JsonFiles.Write(locationsFile,entries);chatStep=-1;RenderChatStage();
    }
}
