using Storloom.Core;
using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private void ConfigureChats() {
        var dialog=new ChatSettingsWindow(preferences){Owner=this};if(dialog.ShowDialog()!=true)return;
        preferences=dialog.Result;JsonFiles.Write(Path.Combine(dataRoot,"preferences.json"),preferences);
        discoveryStarted=false;
        if(chatStep==0)RenderChatStage();
    }
    private async void RestoreChatBackup() {
        try {
            var picker=new Microsoft.Win32.OpenFileDialog{Title=L.T("RestoreChatBackup"),Filter="Storloom backup manifest|*.json"};
            if(PickerHistory.Show(picker,this,"chat-restore-manifest")!=true)return;
            var journal=JsonFiles.Read<TransferJournal>(picker.FileName);
            string parent=View.Folder(this,purpose:"chat-restore-parent");if(parent=="")return;
            string name=Path.GetFileName(PathPolicy.Normalize(journal.Plan.Source));
            if(string.IsNullOrWhiteSpace(name))throw new InvalidDataException(L.T("NoAccounts"));
            string destination=Path.Combine(parent,name);
            ChatPlatform platform=ChatExporter.FindDatabases(journal.Plan.Destination,ChatPlatform.QQ).Any()?ChatPlatform.QQ:ChatPlatform.WeChat;
            if(!Confirm(L.T("RestoreChatConfirm",destination)))return;
            bool complete=false;
            await RunPausable(async token=>{await Task.Run(()=>ChatRestore.Restore(picker.FileName,destination,platform,Progress(),token),token);complete=true;});
            if(complete){operationView.AddLog(L.T("RestoreChatDone",destination));OpenFolder(destination);}
        }catch(Exception ex){Error(ex);}
    }
    private sealed record KeyLibraryEntry(string Path, string Account, ChatPlatform Platform);
    private string KeyLibraryPath=>Path.Combine(dataRoot,"database-key-library.json");
    private List<KeyLibraryEntry> ReadKeyLibrary()=>File.Exists(KeyLibraryPath)?JsonFiles.Read<List<KeyLibraryEntry>>(KeyLibraryPath):[];
    private void RegisterKeyFile(string path) {
        var entries=ReadKeyLibrary();entries.RemoveAll(e=>e.Path.Equals(path,StringComparison.OrdinalIgnoreCase));
        entries.Add(new(Path.GetFullPath(path),Account().Account,Account().Platform));JsonFiles.Write(KeyLibraryPath,entries);
    }
    private void OpenKeyLibrary() {
        var window=new ThemeWindow(L.T("KeyLibrary"),680,410){Owner=this,MinWidth=480,MinHeight=320};
        var grid=View.Grid(View.Auto,View.Fill,View.Auto);grid.Margin=new Thickness(22);window.Body.Children.Add(grid);
        View.Add(grid,View.Text(L.T("KeyLibraryHelp"),color:"Muted"),0);
        var table=View.Table(("Account","Account",125),("Platform","Platform",90),("Path","Path",0));table.ItemsSource=ReadKeyLibrary();View.Add(grid,table,1);
        var unlock=View.Button("ProtectedKeyUnlock",async()=>{
            if(table.SelectedItem is not KeyLibraryEntry entry||activeChat==null)return;
            try{if(entry.Platform!=Account().Platform)throw new InvalidDataException(L.T("KeyDoesNotMatch"));window.Close();await ImportChatKeyFrom(entry.Path);}catch(Exception ex){Error(ex);}
        },true);unlock.IsEnabled=activeChat!=null&&chatStep is 2 or 4;
        var forget=View.Button("KeyLibraryForget",()=>{if(table.SelectedItem is not KeyLibraryEntry entry)return;var entries=ReadKeyLibrary();entries.RemoveAll(e=>e.Path==entry.Path);JsonFiles.Write(KeyLibraryPath,entries);table.ItemsSource=entries;});
        var buttons=View.Row(unlock,forget,View.Button("Close",()=>window.Close()));buttons.HorizontalAlignment=HorizontalAlignment.Right;buttons.Margin=new Thickness(0,16,0,0);View.Add(grid,buttons,2);window.ShowDialog();
    }
}
