using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private async void EncryptReaderArchive() {
        if(operation!=null)return;
        string directory=View.Folder(this);if(directory=="")return;
        try{await ProtectConnectedReader(directory);}catch(Exception ex){Error(ex);}
    }
    private async Task ProtectConnectedReader(string directory){
        var managed=archiveProjects.Values.FirstOrDefault(p=>p.Reading!=null&&Path.GetFullPath(p.Reading).Equals(Path.GetFullPath(directory),StringComparison.OrdinalIgnoreCase));if(managed!=null){try{await ConfigureArchiveProtection(managed);}catch(Exception ex){Error(ex);}return;}
        bool creating=!ChatVault.Exists(directory);
        var password=new VaultPasswordWindow(creating,true,creating?directory:null){Owner=this};if(password.ShowDialog()!=true||password.Password==null)return;
        // This confirmation describes the concrete in-place mutation before any plaintext is removed.
        if(!Confirm(L.T("VaultEncryptConfirm",directory))){password.ClearSecret();return;}
        try{await RunPausable(async token=>{
            var progress=Progress();progress.Report(new("chat-encryption-open",0,0));
            using var vault=await Task.Run(()=>creating?ChatVault.Create(directory,password.Password.AsSpan(),token,progress,password.Selection,password.KeyMode):ChatVault.Open(directory,password.Password.AsSpan()),token);
            password.ClearSecret();
            var result=await Task.Run(()=>vault.Encrypt(password.IncludeMedia,password.Workers,progress,token),token);
            status.Text=L.T(result.Complete?"VaultComplete":result.SelectionComplete?"VaultScopeComplete":"VaultPartial",result.ProtectedFiles,result.TotalFiles);
        });if(chatStep<0)RenderChatStage();}finally{password.ClearSecret();}
    }
    private async Task ChangeReadingPassword(string directory){
        if(ArchiveStore.Exists(directory)){await RunStoreAction("password",new("",directory,"",true));return;}
        if(archiveProjects.Values.Any(p=>p.Reading==directory&&PendingState(p)!=null))throw new InvalidOperationException(L.T("HubFinishPending"));
        var unlock=new VaultPasswordWindow(false,false){Owner=this};if(unlock.ShowDialog()!=true)return;string? oldSecret=unlock.Password;unlock.ClearSecret();
        try{
            using var vault=await Task.Run(()=>ChatVault.Open(directory,oldSecret.AsSpan()));oldSecret=null;
            if(vault.KeyMode==ChatVaultKeyMode.DirectKey)throw new InvalidOperationException(L.T("VaultDirectKeyFixed"));
            var change=new VaultPasswordWindow(true,false,passwordChange:true){Owner=this};if(change.ShowDialog()!=true)return;string? next=change.Password;change.ClearSecret();
            try{await Run(async token=>{token.ThrowIfCancellationRequested();await Task.Run(()=>vault.ChangePassword(next.AsSpan()),token);status.Text=L.T("ReadingPasswordChanged");operationView.AddLog(status.Text);});}finally{next=null;}
        }finally{oldSecret=null;}
    }
    private async Task DecryptConnectedReader(string directory){
        if(!ChatVault.Exists(directory))return;var dialog=new VaultPasswordWindow(false,false){Owner=this};if(dialog.ShowDialog()!=true)return;string? password=dialog.Password;dialog.ClearSecret();
        try{
            string parent=View.Folder(this,purpose:"reader-decrypt-output");if(parent=="")return;
            string target=Path.Combine(parent,"reading-decrypted-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);
            if(!Confirm(L.T("HubDecryptCopyConfirm",target)))return;bool complete=false;var decryptProgress=Progress();int decryptWorkers=ChatWorkers("encryption",parent);
            await RunPausable(async token=>{await Task.Run(()=>{
                if(Storloom.Core.PathPolicy.Under(target,directory)||Storloom.Core.PathPolicy.Under(directory,target))throw new IOException(L.T("StoreSeparatePaths"));
                string work=Path.Combine(parent,".decrypt-partial-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);Storloom.Core.PathPolicy.AssertNoLinks(work);string stage=Path.Combine(work,"reader");Directory.CreateDirectory(stage);
                using var vault=ChatVault.Open(directory,password.AsSpan());vault.RestoreTo(stage,decryptWorkers,decryptProgress,token);Storloom.Core.DirectoryPublication.Move(stage,target,token);complete=true;
            },token);});
            if(complete){string file=Path.Combine(dataRoot,"connected-readers.json");var entries=File.Exists(file)?Storloom.Core.JsonFiles.Read<string[]>(file).ToList():[];entries.Add(target);Storloom.Core.JsonFiles.Write(file,entries);selectedArchiveId=target;chatStep=-1;RenderChatStage();}
        }finally{password=null;dialog.ClearSecret();}
    }

}
