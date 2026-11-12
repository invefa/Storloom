using Storloom.Core;
using Storloom.Core.Chats;

namespace Storloom.App;

internal sealed class ChatSettingsWindow : ThemeWindow {
    internal AppPreferences Result { get; private set; }
    internal ChatSettingsWindow(AppPreferences preferences) : base(L.T("ChatSettings"), 650, 510) {
        Result = preferences; MinWidth = 480; MinHeight = 380;
        var layout = View.Grid(View.Fill, View.Auto); layout.Margin = new Thickness(22); Body.Children.Add(layout);
        var stack = View.Stack(View.Text(L.T("ChatSearchHelp"), color:"Muted")); stack.Margin = new Thickness(0,0,12,0);
        var auto = new CheckBox { Content=L.T("ChatAutoDiscover"), IsChecked=preferences.ChatAutoDiscover, Margin=new Thickness(0,4,0,14) }; stack.Children.Add(auto);
        var roots = View.Input(string.Join(Environment.NewLine, preferences.ChatSearchRoots)); roots.Height=100; roots.AcceptsReturn=true; roots.VerticalContentAlignment=VerticalAlignment.Top; roots.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        stack.Children.Add(View.Text(L.T("ChatSearchRoots"))); stack.Children.Add(roots);
        stack.Children.Add(View.Row(View.Button("AddSearchRoot",()=>{string path=View.Folder(this,purpose:"chat-search-root");if(path!="")roots.Text=string.Join(Environment.NewLine,roots.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Append(path).Distinct(StringComparer.OrdinalIgnoreCase));})));
        TextBox Executable(string key, ChatPlatform platform, string? initial) {
            var input=View.Input(initial??"");var row=new DockPanel();var browse=View.Button("Browse",()=>{
                var picker=new Microsoft.Win32.OpenFileDialog{Filter="Executable|*.exe"};if(PickerHistory.Show(picker,this,"chat-executable-"+platform)==true)input.Text=picker.FileName;
            });browse.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);row.Children.Add(input);
            var title=View.Text(L.T(key),15);title.Margin=new Thickness(0,18,0,8);stack.Children.Add(title);stack.Children.Add(row);
            var detected=ChatPaths.Executable(platform,initial);stack.Children.Add(View.Text(detected??L.T("ClientAutoMissing"),color:"Muted"));return input;
        }
        var qq=Executable("QQExecutable",ChatPlatform.QQ,preferences.QQExecutable);var wx=Executable("WeChatExecutable",ChatPlatform.WeChat,preferences.WeChatExecutable);
        View.Add(layout,new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},0);
        var error=View.Text("",color:"Muted");stack.Children.Add(error);
        var buttons=View.Row(View.Button("Cancel",()=>Close()),View.Button("Apply",()=>{
            try {
                var paths=roots.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(PathPolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach(var pair in new[]{(qq.Text,ChatPlatform.QQ),(wx.Text,ChatPlatform.WeChat)})if(!string.IsNullOrWhiteSpace(pair.Item1)&&(!File.Exists(pair.Item1)||ChatPaths.Executable(pair.Item2,pair.Item1)!=Path.GetFullPath(pair.Item1)))throw new InvalidDataException(L.T("ClientPathInvalid"));
                Result=preferences with{ChatSearchRoots=paths,ChatAutoDiscover=auto.IsChecked==true,QQExecutable=string.IsNullOrWhiteSpace(qq.Text)?null:Path.GetFullPath(qq.Text),WeChatExecutable=string.IsNullOrWhiteSpace(wx.Text)?null:Path.GetFullPath(wx.Text)};DialogResult=true;
            }catch(Exception ex)when(ex is IOException or ArgumentException){error.Text=ex.Message;}
        },true));buttons.HorizontalAlignment=HorizontalAlignment.Right;buttons.Margin=new Thickness(0,16,0,0);View.Add(layout,buttons,1);
    }
}
