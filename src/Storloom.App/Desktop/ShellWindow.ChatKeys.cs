using System.Security.Cryptography;
using System.Text;
using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private async Task ImportChatKey() {
        var picker=new Microsoft.Win32.OpenFileDialog{Title=L.T("KeyImport"),Filter="All files|*.*"};
        if(PickerHistory.Show(picker,this,"database-key-import")!=true)return;
        byte[]? candidate=null;
        try {
            if(new FileInfo(picker.FileName).Length>1024)throw new InvalidOperationException(L.T("KeyFormatInvalid"));
            candidate=ChatDatabaseKey.Parse(await File.ReadAllTextAsync(picker.FileName),Account().Platform);
            var owned=candidate;
            await Run(async token=>{
                if(!await Task.Run(()=>ChatDatabaseKey.Verify(ArchiveAccount().Database,owned,Account().Platform),token))
                    throw new InvalidOperationException(L.T("KeyDoesNotMatch"));
                SetCapturedKey(owned);candidate=null;RenderChatStage();status.Text=L.T("KeyReady");
            });
        } catch(FormatException){throw new InvalidOperationException(L.T("KeyFormatInvalid"));}
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
        var actions=View.Row(View.Button("KeyCopy",()=>Clipboard.SetText(text)),View.Button("KeySave",()=>{
            var save=new Microsoft.Win32.SaveFileDialog{Title=L.T("KeySave"),Filter="All files|*.*",AddExtension=false,FileName=""};
            if(PickerHistory.Show(save,window,"database-key-export")!=true)return;
            if(!Notice.Show(window,L.T("KeySave"),L.T("KeySaveConfirm"),true))return;
            // Only an explicit user action writes a key. Never include its value in task logs.
            try{File.WriteAllText(save.FileName,text,new UTF8Encoding(false));}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Notice.Show(window,L.T("Error"),L.T("KeySaveFailed"));}
        }),View.Button("Close",()=>window.Close()));
        masked.Margin=new Thickness(0,8,0,10);reveal.Margin=new Thickness(0,0,0,14);actions.HorizontalAlignment=HorizontalAlignment.Right;
        var stack=View.Stack(View.Text(L.T("KeyCustodyHelp"),color:"Muted"),masked,visible,reveal,actions);stack.Margin=new Thickness(20);
        window.Body.Children.Add(stack);window.Closed+=(_,_)=>{masked.Clear();visible.Clear();text="";};window.ShowDialog();
    }
}
