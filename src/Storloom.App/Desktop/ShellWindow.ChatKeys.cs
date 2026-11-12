using System.Security.Cryptography;
using System.Text;
using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private async Task ImportChatKey() {
        var picker=new Microsoft.Win32.OpenFileDialog{Title=L.T("KeyImport"),Filter="All files|*.*"};
        if(PickerHistory.Show(picker,this,"database-key-import")!=true)return;
        await ImportChatKeyFrom(picker.FileName);
    }
    private async Task ImportChatKeyFrom(string path) {
        byte[]? candidate=null;
        try {
            if(new FileInfo(path).Length>4096)throw new InvalidOperationException(L.T("KeyFormatInvalid"));
            bool protectedFile=(await File.ReadAllTextAsync(path)).TrimStart().StartsWith("{",StringComparison.Ordinal);
            string? password=null;KeyPasswordWindow? dialog=null;
            try {
                if(protectedFile){dialog=new KeyPasswordWindow(false){Owner=this};if(dialog.ShowDialog()!=true)return;password=dialog.Password;dialog.ClearSecret();}
                await Run(async token=>{
                    candidate=await Task.Run(()=>{
                        try{return protectedFile?ChatKeyFile.Open(path,Account().Platform,password.AsSpan()):ChatDatabaseKey.Parse(File.ReadAllText(path),Account().Platform);}
                        catch(CryptographicException){throw new InvalidOperationException(L.T("ProtectedKeyInvalid"));}
                    },token);
                    if(!await Task.Run(()=>ChatDatabaseKey.Verify(ArchiveAccount().Database,candidate,Account().Platform),token))throw new InvalidOperationException(L.T("KeyDoesNotMatch"));
                    SetCapturedKey(candidate);candidate=null;if(protectedFile)RegisterKeyFile(path);RenderChatStage();status.Text=L.T("KeyReady");
                });
            }finally{password=null;dialog?.ClearSecret();}
        } catch(FormatException){throw new InvalidOperationException(L.T("KeyFormatInvalid"));}
        catch(CryptographicException){throw new InvalidOperationException(L.T("ProtectedKeyInvalid"));}
        finally{if(candidate!=null)CryptographicOperations.ZeroMemory(candidate);}
    }
    private void ManageChatKey() {
        if(capturedKey==null)return;
        string text=ChatDatabaseKey.Format(capturedKey,Account().Platform);
        var window=new ThemeWindow(L.T("KeyManage"),520,280){Owner=this,MinWidth=360,MinHeight=0,ResizeMode=ResizeMode.NoResize,SizeToContent=SizeToContent.Height};
        window.ResizeMode=ResizeMode.NoResize;
        window.Caption.ShowMaximize=window.Caption.ShowMinimize=false;
        var masked=View.SecretInput();masked.Password=text;
        var visible=View.Input("",true);visible.Visibility=Visibility.Collapsed;
        var reveal=new CheckBox{Content=L.T("KeyReveal")};
        reveal.Checked+=(_,_)=>{visible.Text=text;masked.Visibility=Visibility.Collapsed;visible.Visibility=Visibility.Visible;};
        reveal.Unchecked+=(_,_)=>{visible.Clear();visible.Visibility=Visibility.Collapsed;masked.Visibility=Visibility.Visible;};
        var actions=View.Row(View.Button("KeyCopy",()=>Clipboard.SetText(text)),View.Button("ProtectedKeySave",async()=>{
            var save=new Microsoft.Win32.SaveFileDialog{Title=L.T("ProtectedKeySave"),Filter="All files|*.*",AddExtension=false,FileName=""};
            if(PickerHistory.Show(save,window,"database-key-export")!=true)return;
            if(File.Exists(save.FileName)){Notice.Show(window,L.T("Error"),L.T("KeyNewFileRequired"));return;}
            var password=new KeyPasswordWindow(true){Owner=window};if(password.ShowDialog()!=true)return;
            byte[] owned=capturedKey!.ToArray();string? secret=password.Password;password.ClearSecret();
            try{await Task.Run(()=>ChatKeyFile.Save(save.FileName,owned,Account().Platform,secret.AsSpan()));RegisterKeyFile(save.FileName);Notice.Show(window,L.T("ProtectedKeySave"),L.T("ProtectedKeySaved"));}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){Notice.Show(window,L.T("Error"),L.T("KeySaveFailed"));}
            finally{CryptographicOperations.ZeroMemory(owned);secret=null;}
        }),View.Button("Close",()=>window.Close()));
        masked.Margin=new Thickness(0,8,0,10);reveal.Margin=new Thickness(0,0,0,14);actions.HorizontalAlignment=HorizontalAlignment.Right;
        var stack=View.Stack(View.Text(L.T("KeyCustodyHelp"),color:"Muted"),masked,visible,reveal,actions);stack.Margin=new Thickness(20);
        window.Body.Children.Add(stack);window.Closed+=(_,_)=>{masked.Clear();visible.Clear();text="";};window.ShowDialog();
    }
}
