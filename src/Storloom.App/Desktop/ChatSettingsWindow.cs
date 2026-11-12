using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;
internal sealed class ChatSettingsWindow : ThemeWindow {
    internal AppPreferences Result {get;private set;}
    internal ChatSettingsWindow(AppPreferences preferences):base(L.T("ChatSettings"),650,510){
        Result=preferences;MinWidth=480;MinHeight=380;
        Body.Children.Add(new ChatSettingsEditor(this,preferences,p=>{Result=p;DialogResult=true;},Close){Margin=new Thickness(22)});
    }
}
internal sealed class ChatSettingsEditor:Grid {
    internal AppPreferences Result{get;private set;}
    internal ChatSettingsEditor(Window owner,AppPreferences preferences,Action<AppPreferences> save,Action cancel){
        Result = preferences;
        var layout = View.Grid(View.Fill, View.Auto); Children.Add(layout);
        var tabs=new TabControl();var stack=View.Stack();var clients=View.Stack();stack.Margin=clients.Margin=new Thickness(12);var searchTab=new TabItem{Header=L.T("LocationDataTab"),Content=stack};tabs.Items.Add(searchTab);tabs.Items.Add(new TabItem{Header=L.T("LocationClientTab"),Content=clients});
        stack.Children.Add(new Expander{Header=L.T("DiscoveryHelpTitle"),Content=View.Text(L.T("ChatSearchHelp"),color:"Muted"),Margin=new Thickness(0,0,0,12)});
        var auto = new CheckBox { Content=L.T("ChatAutoDiscover"), IsChecked=preferences.ChatAutoDiscover, Margin=new Thickness(0,4,0,14) }; stack.Children.Add(auto);
        var roots = View.Input(string.Join(Environment.NewLine, preferences.ChatSearchRoots)); roots.Height=130; roots.AcceptsReturn=true; roots.VerticalContentAlignment=VerticalAlignment.Top; roots.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        stack.Children.Add(View.Text(L.T("ChatSearchRoots"))); stack.Children.Add(roots);
        stack.Children.Add(View.Row(View.Button("AddSearchRoot",()=>{string path=View.Folder(owner,purpose:"chat-search-root");if(path!="")roots.Text=string.Join(Environment.NewLine,roots.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Append(path).Distinct(StringComparer.OrdinalIgnoreCase));})));
        TextBox Executable(string key, ChatPlatform platform, string? initial) {
            var input=View.Input(initial??"");var row=new DockPanel();var browse=View.Button("Browse",()=>{
                var picker=new Microsoft.Win32.OpenFileDialog{Filter="Executable|*.exe"};if(PickerHistory.Show(picker,owner,"chat-executable-"+platform)==true)input.Text=picker.FileName;
            });browse.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);row.Children.Add(input);
            var title=View.Text(L.T(key),15);title.Margin=new Thickness(0,18,0,8);clients.Children.Add(title);clients.Children.Add(row);
            var detected=ChatPaths.Executable(platform,initial);var info=View.Text(detected??L.T("ClientAutoMissing"),color:"Muted");info.TextWrapping=TextWrapping.NoWrap;info.TextTrimming=TextTrimming.CharacterEllipsis;info.ToolTip=info.Text;clients.Children.Add(info);return input;
        }
        var qq=Executable("QQExecutable",ChatPlatform.QQ,preferences.QQExecutable);var wx=Executable("WeChatExecutable",ChatPlatform.WeChat,preferences.WeChatExecutable);
        View.Add(layout,tabs,0);
        var error=View.Text("",color:"Muted");stack.Children.Add(error);
        var searchLayout=new Grid{Margin=new Thickness(12)};var children=stack.Children.Cast<UIElement>().ToArray();stack.Children.Clear();
        foreach(var child in children){int index=searchLayout.RowDefinitions.Count;searchLayout.RowDefinitions.Add(new RowDefinition{Height=ReferenceEquals(child,roots)?View.Fill:View.Auto});View.Add(searchLayout,child,index);}
        roots.Height=double.NaN;roots.MinHeight=0;roots.VerticalAlignment=VerticalAlignment.Stretch;roots.Padding=new Thickness(8,6,8,6);roots.Margin=new Thickness(0,0,0,10);roots.Name="account_search_roots";searchTab.Content=searchLayout;
        var buttons=View.Row(View.Button("Cancel",()=>cancel()),View.Button("Apply",()=>{
            try {
                var paths=roots.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(PathPolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach(var pair in new[]{(qq.Text,ChatPlatform.QQ),(wx.Text,ChatPlatform.WeChat)})if(!string.IsNullOrWhiteSpace(pair.Item1)&&(!File.Exists(pair.Item1)||ChatPaths.Executable(pair.Item2,pair.Item1)!=Path.GetFullPath(pair.Item1)))throw new InvalidDataException(L.T("ClientPathInvalid"));
                Result=preferences with{ChatSearchRoots=paths,ChatAutoDiscover=auto.IsChecked==true,QQExecutable=string.IsNullOrWhiteSpace(qq.Text)?null:Path.GetFullPath(qq.Text),WeChatExecutable=string.IsNullOrWhiteSpace(wx.Text)?null:Path.GetFullPath(wx.Text)};save(Result);
            }catch(Exception ex)when(ex is IOException or ArgumentException){error.Text=ex.Message;}
        },true));buttons.HorizontalAlignment=HorizontalAlignment.Right;buttons.Margin=new Thickness(0,16,0,0);View.Add(layout,buttons,1);
    }
}
