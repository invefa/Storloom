using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

internal sealed class StoreMigrationWindow:ThemeWindow {
    internal string SourceDirectory{get;private set;}="";
    internal string TargetDirectory{get;private set;}="";
    internal string? SourcePassword{get;private set;}
    internal string? TargetPassword{get;private set;}
    internal bool Encrypt{get;private set;}
    internal ChatVaultKeyMode TargetKeyMode{get;private set;}
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
        var sourcePasswordRow=View.Field("StoreSourcePassword",originalSecret);var passwordRow=View.Field("VaultPassword",secret);var confirmRow=View.Field("VaultConfirmPassword",confirm);
        var mode=new ComboBox{Name="store_key_mode",ItemsSource=new[]{L.T("VaultWrappedMode"),L.T("VaultDirectMode")},SelectedIndex=0,ToolTip=L.T("VaultKeyModeHelp")};var modeRow=View.Field("VaultKeyMode",mode);
        var stack=View.Stack(View.Text(L.T("StoreMigrationHelp"),color:"Muted"),View.Field("StoreSource",sourceRow),View.Field("StoreDestination",targetRow),sourcePasswordRow,encryption,modeRow,passwordRow,confirmRow);
        var notice=new Expander{Header=L.T("TaskDetails"),Content=View.Text(L.T("StoreMigrationConfirm"),color:"Muted")};notice.Margin=new Thickness(0,12,0,8);stack.Children.Add(notice);stack.Children.Add(error);
        var actions=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};
        var cancel=View.Button("Cancel",Close);cancel.IsCancel=true;actions.Children.Add(cancel);
        var okay=View.Button("StoreMigrateStart",()=>{
            try{
                string input=PathPolicy.Normalize(sourceInput.Text),output=PathPolicy.Normalize(targetInput.Text);PathPolicy.AssertNoLinks(input);
                if(!Directory.Exists(input))throw new IOException(L.T("StoreSourceRequired"));
                if(input.Equals(output,StringComparison.OrdinalIgnoreCase)||PathPolicy.Under(output,input)||PathPolicy.Under(input,output))throw new IOException(L.T("StoreSeparatePaths"));
                bool exists=ArchiveStore.Exists(output),protect=exists?ArchiveStore.NeedsPassword(output):encryption.IsChecked==true;
                if(ChatVault.Exists(input)&&!protect)throw new IOException(L.T("StoreProtectedSource"));
                var keyMode=exists?ArchiveStore.ReadKeyMode(output):(ChatVaultKeyMode)mode.SelectedIndex;
                if(protect){try{ChatVault.ValidateCredential(secret.Password,keyMode);}catch(ArgumentException){throw new IOException(L.T(keyMode==ChatVaultKeyMode.DirectKey?"VaultDirectKeyInvalid":"VaultPasswordMismatch"));}if(!exists&&secret.Password!=confirm.Password)throw new IOException(L.T(keyMode==ChatVaultKeyMode.DirectKey?"VaultDirectKeyMismatch":"VaultPasswordMismatch"));}
                if(ChatVault.Exists(input)&&originalSecret.Password.Length==0)throw new IOException(L.T("StoreSourcePasswordRequired"));
                if(!exists&&(Directory.Exists(output)||File.Exists(output)))throw new IOException(L.T("StoreNewDirectory"));
                SourceDirectory=input;TargetDirectory=output;SourcePassword=originalSecret.Password;TargetPassword=secret.Password;TargetKeyMode=keyMode;Encrypt=protect;DialogResult=true;
            }catch(Exception ex)when(ex is IOException or ArgumentException or UnauthorizedAccessException){error.Text=ex.Message;}
        },true);okay.Name="store_migrate_start";okay.IsDefault=true;actions.Children.Add(okay);
        void Refresh(){try{bool exists=ArchiveStore.Exists(targetInput.Text),protectedSource=ChatVault.Exists(sourceInput.Text);sourcePasswordRow.Visibility=protectedSource?Visibility.Visible:Visibility.Collapsed;bool protect=exists?ArchiveStore.NeedsPassword(targetInput.Text):protectedSource||encryption.IsChecked==true;if(exists||protectedSource)encryption.IsChecked=protect;encryption.IsEnabled=!exists&&!protectedSource;passwordRow.Visibility=protect?Visibility.Visible:Visibility.Collapsed;confirmRow.Visibility=protect&&!exists?Visibility.Visible:Visibility.Collapsed;modeRow.Visibility=protect&&!exists?Visibility.Visible:Visibility.Collapsed;bool direct=(exists?ArchiveStore.ReadKeyMode(targetInput.Text):(ChatVaultKeyMode)mode.SelectedIndex)==ChatVaultKeyMode.DirectKey;passwordRow.Children.OfType<Label>().Single().Content=L.T(exists?"VaultCredential":direct?"VaultDirectKey":"VaultPassword");confirmRow.Children.OfType<Label>().Single().Content=L.T(direct?"VaultConfirmKey":"VaultConfirmPassword");okay.Content=L.T(exists?"StoreContinueUpdate":"StoreMigrateStart");}catch(Exception ex)when(ex is IOException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException){error.Text=ex.Message;}}
        mode.SelectionChanged+=(_,_)=>{secret.Clear();confirm.Clear();Refresh();};
        sourceInput.TextChanged+=(_,_)=>Refresh();targetInput.TextChanged+=(_,_)=>Refresh();encryption.Click+=(_,_)=>Refresh();Refresh();
        var layout=View.Grid(View.Fill,View.Auto);layout.Margin=new Thickness(22);var scroll=View.FormScroll(stack);scroll.MaxHeight=SystemParameters.WorkArea.Height-190;View.Add(layout,scroll,0);View.Add(layout,actions,1);Body.Children.Add(layout);
        Closed+=(_,_)=>{originalSecret.Clear();secret.Clear();confirm.Clear();};
    }
}
