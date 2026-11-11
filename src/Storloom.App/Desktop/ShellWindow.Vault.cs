using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private async void EncryptReaderArchive() {
        if(operation!=null)return;
        string directory=View.Folder(this);if(directory=="")return;
        var managed=archiveProjects.Values.FirstOrDefault(p=>p.Reading!=null&&Path.GetFullPath(p.Reading).Equals(Path.GetFullPath(directory),StringComparison.OrdinalIgnoreCase));if(managed!=null){try{await ConfigureArchiveProtection(managed);}catch(Exception ex){Error(ex);}return;}
        bool creating=!ChatVault.Exists(directory);
        var password=new VaultPasswordWindow(creating,true,creating?directory:null){Owner=this};if(password.ShowDialog()!=true||password.Password==null)return;
        // This confirmation describes the concrete in-place mutation before any plaintext is removed.
        if(!Confirm(L.T("VaultEncryptConfirm",directory))){password.ClearSecret();return;}
        try{await RunPausable(async token=>{
            var progress=Progress();progress.Report(new("chat-encryption-open",0,0));
            using var vault=await Task.Run(()=>creating?ChatVault.Create(directory,password.Password.AsSpan(),token,progress,password.Selection):ChatVault.Open(directory,password.Password.AsSpan()),token);
            password.ClearSecret();
            var result=await Task.Run(()=>vault.Encrypt(password.IncludeMedia,password.Workers,progress,token),token);
            status.Text=L.T(result.Complete?"VaultComplete":result.SelectionComplete?"VaultScopeComplete":"VaultPartial",result.ProtectedFiles,result.TotalFiles);
        });}finally{password.ClearSecret();}
    }
}
