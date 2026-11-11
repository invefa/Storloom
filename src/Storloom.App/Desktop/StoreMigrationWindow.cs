using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

internal sealed class StoreMigrationWindow:ThemeWindow {
    internal string SourceDirectory{get;private set;}="";
    internal string TargetDirectory{get;private set;}="";
    internal string? SourcePassword{get;private set;}
    internal string? TargetPassword{get;private set;}
    internal bool Encrypt{get;private set;}
    internal void ClearSecrets(){SourcePassword=TargetPassword=null;}
    internal StoreMigrationWindow(string dataRoot,string? source=null,string? target=null):base(L.T("StoreMigrate"),660,520){
        PickerHistory.Attach(this,dataRoot);MinHeight=0;ResizeMode=ResizeMode.NoResize;Caption.ShowMaximize=false;SizeToContent=SizeToContent.Height;MaxHeight=SystemParameters.WorkArea.Height-36;
        TextBox sourceInput=View.Input(source??""),targetInput=View.Input(target??"");sourceInput.Name="store_source";targetInput.Name="store_target";
        PasswordBox originalSecret=View.SecretInput(),secret=View.SecretInput(),confirm=View.SecretInput();originalSecret.Name="store_source_password";secret.Name="store_password";
        var encryption=new CheckBox{Content=L.T("StoreEncrypt"),Margin=new Thickness(0,10,0,10)};encryption.Name="store_encrypt";
        var error=View.Text("",color:"Muted");
        DockPanel PathRow(TextBox input,Action browse){var dock=new DockPanel();var button=View.Button("Browse",browse);button.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(button,Dock.Right);dock.Children.Add(button);dock.Children.Add(input);return dock;}
        var sourceRow=PathRow(sourceInput,()=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="Storloom reading copy|messages.html;vault.storloom.json;export-report.json"};if(PickerHistory.Show(dialog,this,"store-migration-source")==true)sourceInput.Text=Path.GetDirectoryName(dialog.FileName)!;});
        var targetRow=PathRow(targetInput,()=>{var dialog=new Microsoft.Win32.SaveFileDialog{FileName="Archive-"+DateTime.Now.ToString("yyyyMMdd-HHmm"),Filter="Directory name|*",AddExtension=false,OverwritePrompt=false};if(PickerHistory.Show(dialog,this,"store-migration-target")==true)targetInput.Text=dialog.FileName;});
        FrameworkElement sourcePasswordRow=View.Field("StoreSourcePassword",originalSecret),passwordRow=View.Field("VaultPassword",secret),confirmRow=View.Field("VaultConfirmPassword",confirm);
        var stack=View.Stack(View.Text(L.T("StoreMigrationHelp"),color:"Muted"),View.Field("StoreSource",sourceRow),View.Field("StoreDestination",targetRow),sourcePasswordRow,encryption,passwordRow,confirmRow);
        var notice=View.Text(L.T("StoreMigrationConfirm"),color:"Muted");notice.Margin=new Thickness(0,12,0,8);stack.Children.Add(notice);stack.Children.Add(error);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};stack.Children.Add(actions);
        var cancel=View.Button("Cancel",Close);cancel.IsCancel=true;actions.Children.Add(cancel);
        var okay=View.Button("StoreMigrateStart",()=>{
            try{
                string input=PathPolicy.Normalize(sourceInput.Text),output=PathPolicy.Normalize(targetInput.Text);PathPolicy.AssertNoLinks(input);
                if(!Directory.Exists(input))throw new IOException(L.T("StoreSourceRequired"));
                if(input.Equals(output,StringComparison.OrdinalIgnoreCase)||PathPolicy.Under(output,input)||PathPolicy.Under(input,output))throw new IOException(L.T("StoreSeparatePaths"));
                bool exists=ArchiveStore.Exists(output),protect=exists?ArchiveStore.NeedsPassword(output):encryption.IsChecked==true;
                if(ChatVault.Exists(input)&&!protect)throw new IOException(L.T("StoreProtectedSource"));
                if(protect&&(secret.Password.Length<8||!exists&&secret.Password!=confirm.Password))throw new IOException(L.T("VaultPasswordMismatch"));
                if(ChatVault.Exists(input)&&originalSecret.Password.Length==0)throw new IOException(L.T("StoreSourcePasswordRequired"));
                if(!exists&&(Directory.Exists(output)||File.Exists(output)))throw new IOException(L.T("StoreNewDirectory"));
                SourceDirectory=input;TargetDirectory=output;SourcePassword=originalSecret.Password;TargetPassword=secret.Password;Encrypt=protect;DialogResult=true;
            }catch(Exception ex)when(ex is IOException or ArgumentException or UnauthorizedAccessException){error.Text=ex.Message;}
        },true);okay.Name="store_migrate_start";okay.IsDefault=true;actions.Children.Add(okay);
        void Refresh(){try{bool exists=ArchiveStore.Exists(targetInput.Text),protectedSource=ChatVault.Exists(sourceInput.Text);sourcePasswordRow.Visibility=protectedSource?Visibility.Visible:Visibility.Collapsed;bool protect=exists?ArchiveStore.NeedsPassword(targetInput.Text):protectedSource||encryption.IsChecked==true;if(exists||protectedSource)encryption.IsChecked=protect;encryption.IsEnabled=!exists&&!protectedSource;passwordRow.Visibility=protect?Visibility.Visible:Visibility.Collapsed;confirmRow.Visibility=protect&&!exists?Visibility.Visible:Visibility.Collapsed;okay.Content=L.T(exists?"StoreContinueUpdate":"StoreMigrateStart");}catch(Exception ex)when(ex is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException){error.Text=ex.Message;}}
        sourceInput.TextChanged+=(_,_)=>Refresh();targetInput.TextChanged+=(_,_)=>Refresh();encryption.Click+=(_,_)=>Refresh();Refresh();
        var scroll=new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Margin=new Thickness(22),MaxHeight=SystemParameters.WorkArea.Height-110};Body.Children.Add(scroll);
        Closed+=(_,_)=>{originalSecret.Clear();secret.Clear();confirm.Clear();};
    }
}
