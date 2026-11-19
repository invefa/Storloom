using Storloom.Core;
namespace Storloom.App;

internal sealed class ConfigurationBackupWindow : ThemeWindow {
    internal ConfigurationScope Scope{get;private set;}
    internal bool Encrypt{get;private set;}
    internal ConfigurationRestorePlan? Plan{get;private set;}
    private sealed record Row(string Item,string Group,string Effect);
    public ConfigurationBackupWindow(string? root=null,ConfigurationSnapshot? snapshot=null):base(L.T(snapshot==null?"ConfigurationExport":"ConfigurationImport"),850,600){
        var layout=View.Grid(View.Auto,View.Auto,View.Fill,View.Auto,View.Auto,View.Auto);layout.Margin=new Thickness(24);Body.Children.Add(layout);
        View.Add(layout,View.Text(L.T(snapshot==null?"ConfigurationExportHelp":"ConfigurationImportHelp"),color:"Muted"),0);
        var registrations=new CheckBox{Content=L.T("ConfigurationRegistrations"),IsChecked=true,Name="config_registrations"};
        var preferences=new CheckBox{Content=L.T("ConfigurationPreferences"),IsChecked=true,Name="config_preferences"};
        var keys=new CheckBox{Content=L.T("ConfigurationKeys"),IsChecked=false,Name="config_keys"};
        if(snapshot!=null){registrations.IsEnabled=snapshot.Items.Any(i=>i.Scope==ConfigurationScope.Registrations);preferences.IsEnabled=snapshot.Items.Any(i=>i.Scope==ConfigurationScope.Preferences);keys.IsEnabled=snapshot.Items.Any(i=>i.Scope==ConfigurationScope.ProtectedKeys);}
        var encrypt=new CheckBox{Content=L.T("ConfigurationEncrypt"),IsChecked=true,Name="config_encrypt",Visibility=snapshot==null?Visibility.Visible:Visibility.Collapsed};
        var replace=new CheckBox{Content=L.T("ConfigurationReplaceRegistrations"),Name="config_replace",Visibility=snapshot==null?Visibility.Collapsed:Visibility.Visible};
        View.Add(layout,View.Stack(registrations,preferences,keys,encrypt,replace),1);
        var table=View.Table(("ConfigurationItem","Item",0),("ConfigurationGroup","Group",120),("ConfigurationEffect","Effect",160));table.Name="configuration_restore_preview";View.Add(layout,table,2);
        var locations=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=110};
        var details=new Expander{Header=L.T("ConfigurationPreviewLocations"),Content=locations,Visibility=snapshot==null?Visibility.Collapsed:Visibility.Visible,Margin=new Thickness(0,8,0,8)};View.Add(layout,details,3);
        var error=View.Text("",12,"Muted");View.Add(layout,error,4);
        var accept=View.Button(snapshot==null?"ConfigurationExport":"ConfigurationRestoreApply",()=>{
            Scope=Selected();Encrypt=encrypt.IsChecked==true||keys.IsChecked==true;DialogResult=true;
        },true);accept.Name="configuration_accept";
        ConfigurationScope Selected()=>(registrations.IsChecked==true&&registrations.IsEnabled?ConfigurationScope.Registrations:0)|(preferences.IsChecked==true&&preferences.IsEnabled?ConfigurationScope.Preferences:0)|(keys.IsChecked==true&&keys.IsEnabled?ConfigurationScope.ProtectedKeys:0);
        void Update(){
            if(keys.IsChecked==true)encrypt.IsChecked=true;encrypt.IsEnabled=keys.IsChecked!=true;
            try{if(snapshot!=null){Plan=ConfigurationBackup.Preview(root!,snapshot,Selected(),replace.IsChecked==true);table.ItemsSource=Plan.Changes.Select(c=>new Row(Label(c.Path),L.T("ConfigurationScope"+c.Scope),L.T("ConfigurationAction"+c.Action))).ToArray();var sources=Plan.Changes.SingleOrDefault(c=>c.Path=="chat-archives.json");locations.Text=sources==null?L.T("ConfigurationNoSourceChanges"):string.Join(Environment.NewLine,(System.Text.Json.JsonSerializer.Deserialize<Storloom.Core.Chats.ChatArchiveProject[]>(sources.Content,JsonFiles.Options)??[]).Select(p=>p.Account+" ("+p.Platform+") / "+p.Source+" -> "+p.Reading));accept.IsEnabled=Plan.Changes.Length>0;}else{var rows=new List<Row>();if(Selected().HasFlag(ConfigurationScope.Registrations))rows.Add(new(L.T("ConfigurationRegistrations"),"",L.T("ConfigurationActionmerge")));if(Selected().HasFlag(ConfigurationScope.Preferences))rows.Add(new(L.T("ConfigurationPreferences"),"",L.T("ConfigurationActionpreferences")));rows.Add(new(L.T("ConfigurationKeys"),"",keys.IsChecked==true?L.T("ConfigurationEncryptedOnly"):L.T("ConfigurationExcluded")));table.ItemsSource=rows;accept.IsEnabled=Selected()!=ConfigurationScope.None;}error.Text="";}
            catch(Exception ex)when(ex is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException){Plan=null;accept.IsEnabled=false;error.Text=L.T("ConfigurationInvalid");}
        }
        foreach(var check in new[]{registrations,preferences,keys,replace}){check.Checked+=(_,_)=>Update();check.Unchecked+=(_,_)=>Update();}
        var cancel=View.Button("Cancel",()=>DialogResult=false);cancel.IsCancel=true;var buttons=View.Row(cancel,accept);buttons.HorizontalAlignment=HorizontalAlignment.Right;View.Add(layout,buttons,5);Update();
    }
    private static string Label(string path)=>L.T(path switch{"chat-archives.json"=>"ConfigurationSources","archive-stores.json"=>"ConfigurationStores","connected-readers.json"=>"ConfigurationReaders","chat-backups.json"=>"ConfigurationBackups","account-aliases.json"=>"AccountAliases","preferences.json"=>"ConfigurationPreferences","database-key-library.json"=>"ConfigurationKeys",_=>path.StartsWith("imported-keys/",StringComparison.Ordinal)?"ConfigurationProtectedFile":"ConfigurationReaderPreferences"});
}
