using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private async void EncryptReaderArchive() {
        if(operation!=null)return;
        string directory=View.Folder(this);if(directory=="")return;
        bool creating=!ChatVault.Exists(directory);
        var password=new VaultPasswordWindow(creating,true){Owner=this};if(password.ShowDialog()!=true||password.Password==null)return;
        // This confirmation describes the concrete in-place mutation before any plaintext is removed.
        if(!Confirm(L.T("VaultEncryptConfirm",directory)))return;
        await RunPausable(async token=>{
            using var vault=await Task.Run(()=>creating?ChatVault.Create(directory,password.Password.AsSpan(),token):ChatVault.Open(directory,password.Password.AsSpan()),token);
            password.ClearSecret();
            var progress=Progress();var result=await Task.Run(()=>vault.Encrypt(password.IncludeMedia,password.Workers,progress,token),token);
            status.Text=L.T(result.Complete?"VaultComplete":"VaultPartial",result.ProtectedFiles,result.TotalFiles);
        });
    }
}
