using Storloom.Core.Chats;
namespace Storloom.App;

internal sealed class StoreExportWindow:ThemeWindow {
    internal string Destination{get;private set;}="";
    internal string? Password{get;private set;}
    internal int Workers{get;private set;}=2;
    internal void ClearSecret()=>Password=null;
    internal StoreExportWindow(string dataRoot,string name):base(L.T("StoreExportReader"),600,400){
        PickerHistory.Attach(this,dataRoot);MinWidth=540;MinHeight=0;ResizeMode=ResizeMode.NoResize;SizeToContent=SizeToContent.Height;Caption.ShowMaximize=false;
        var input=View.Input();input.Name="store_export_directory";var browse=View.Button("Browse",()=>{var picker=new Microsoft.Win32.SaveFileDialog{Filter="Directory name|*",FileName=name+"-reading",AddExtension=false,OverwritePrompt=false};if(PickerHistory.Show(picker,this,"store-export-target")==true)input.Text=picker.FileName;});browse.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(browse,Dock.Right);var row=new DockPanel();row.Children.Add(browse);row.Children.Add(input);
        var encrypt=new CheckBox{Content=L.T("EncryptAfterExport"),Margin=new Thickness(0,8,0,12)};encrypt.Name="store_export_encrypt";
        PasswordBox secret=View.SecretInput(),confirm=View.SecretInput();var workers=WorkerRecommendation.Selector(2,true,"encryption",()=>input.Text);
        var settings=View.Stack(View.Field("VaultPassword",secret),View.Field("VaultConfirmPassword",confirm),View.Field("VaultWorkers",workers));settings.Visibility=Visibility.Collapsed;encrypt.Checked+=(_,_)=>settings.Visibility=Visibility.Visible;encrypt.Unchecked+=(_,_)=>settings.Visibility=Visibility.Collapsed;
        var error=View.Text("",color:"Muted");var stack=View.Stack(View.Field("ReadingCopyLabel",row),encrypt,settings,View.Text(L.T("StoreExportProtectionHelp"),color:"Muted"),error);stack.Margin=new Thickness(22);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};var cancel=View.Button("Cancel",Close);cancel.IsCancel=true;actions.Children.Add(cancel);var start=View.Button("StoreExportReader",()=>{try{Destination=Storloom.Core.PathPolicy.Normalize(input.Text);if(encrypt.IsChecked==true){if(secret.Password.Length<8||secret.Password!=confirm.Password){error.Text=L.T("VaultPasswordMismatch");return;}Password=secret.Password;}Workers=workers.SelectedValue is int count&&count>0?count:WorkerRecommendation.For("encryption",Destination);DialogResult=true;}catch(Exception ex)when(ex is IOException or ArgumentException){error.Text=ex.Message;}},true);start.Name="store_export_start";start.IsDefault=true;actions.Children.Add(start);stack.Children.Add(actions);Body.Children.Add(stack);Closed+=(_,_)=>{secret.Clear();confirm.Clear();};
    }
}
