using System.Text.Json.Nodes;
using Storloom.Core.Chats;
namespace Storloom.App;
internal sealed class ArchiveProtectionWindow : ThemeWindow {
    internal ChatArchiveProtectionMode Mode{get;private set;}
    internal ChatArchiveProtection Settings{get;private set;}=new();
    public ArchiveProtectionWindow(ChatArchiveProject project,JsonObject index,ChatArchiveProtection? current=null):base(L.T("ArchiveProtection"),660,680){
        MinWidth=520;MinHeight=400;var layout=View.Grid(View.Fill,View.Auto);layout.Margin=new Thickness(20);Body.Children.Add(layout);
        var stack=View.Stack(View.Text(L.T("ArchiveProtectionHelp"),color:"Muted"));View.Add(layout,new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled},0);
        if(!project.ProtectionKnown&&current==null)stack.Children.Add(View.Text(L.T("ArchiveLegacyRules"),color:"Muted"));
        var mode=new ComboBox{ItemsSource=new[]{L.T("ArchiveProtectionNone"),L.T("ArchiveProtectionAll"),L.T("ArchiveProtectionSelected")},SelectedIndex=(int)project.Protection};stack.Children.Add(View.Field("ArchiveProtection",mode));
        var media=new CheckBox{Content=L.T("VaultIncludeMedia"),IsChecked=current?.IncludeMedia??project.IncludeMedia};stack.Children.Add(media);
        var future=View.Text(L.T("ArchiveFutureProtection"),color:"Muted");stack.Children.Add(future);
        var choices=new List<(string Id,CheckBox Check)>();var conversationList=new StackPanel();
        foreach(var c in index["conversations"]?.AsArray()??new JsonArray()){string id=c!["id"]!.GetValue<string>();var check=new CheckBox{IsChecked=current?.Selection?.Conversations?.Contains(id)==true,Content=index["conversationNames"]?[id]?.GetValue<string>()??c["name"]?.GetValue<string>()??id};choices.Add((id,check));conversationList.Children.Add(check);}
        var scope=new StackPanel();scope.Children.Add(new Expander{Header=L.T("VaultConversations"),Content=new ScrollViewer{Content=conversationList,MaxHeight=150,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        var kinds=new List<(string Id,CheckBox Check)>();var kindPanel=new WrapPanel();foreach(string id in new[]{"text","image","sticker","audio","video","file","reply","system","recall","reaction","forward","unparsed"}){var check=new CheckBox{Content=L.T("ReaderKind_"+id),IsChecked=current?.Selection?.Kinds?.Contains(id)==true};kinds.Add((id,check));kindPanel.Children.Add(check);}
        scope.Children.Add(new Expander{Header=L.T("ReaderMessageType"),Content=kindPanel});var content=View.Input(current?.Selection?.Content??"");var sender=View.Input(current?.Selection?.Sender??"");var ids=View.Input(string.Join(",",current?.Selection?.MessageIds??[]));scope.Children.Add(View.Field("ReaderContent",content));scope.Children.Add(View.Field("ReaderSender",sender));scope.Children.Add(View.Field("VaultMessageIds",ids));
        var strategy=new ComboBox{ItemsSource=new[]{L.T("VaultScopeTogether"),L.T("VaultScopeStrict")},SelectedIndex=current?.Selection?.Strict==true?1:0};scope.Children.Add(strategy);scope.Children.Add(View.Text(L.T("VaultStrategyHelp"),color:"Muted"));scope.Children.Add(View.Text(L.T("ArchiveFixedMessageSelection"),color:"Muted"));stack.Children.Add(scope);
        void Changed(){scope.Visibility=mode.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;media.IsEnabled=mode.SelectedIndex!=0;}mode.SelectionChanged+=(_,_)=>Changed();Changed();
        var error=View.Text("",color:"Muted");stack.Children.Add(error);
        var buttons=View.Row(View.Button("Cancel",()=>Close()),View.Button("ArchiveApplyProtection",()=>{Mode=(ChatArchiveProtectionMode)mode.SelectedIndex;if(Mode==ChatArchiveProtectionMode.Selected&&!choices.Any(c=>c.Check.IsChecked==true)&&!kinds.Any(k=>k.Check.IsChecked==true)&&string.IsNullOrWhiteSpace(content.Text)&&string.IsNullOrWhiteSpace(sender.Text)&&string.IsNullOrWhiteSpace(ids.Text)){error.Text=L.T("ArchiveChooseRule");return;}Settings=new(media.IsChecked==true,Mode==ChatArchiveProtectionMode.Selected?new(choices.Where(c=>c.Check.IsChecked==true).Select(c=>c.Id).ToArray(),kinds.Where(k=>k.Check.IsChecked==true).Select(k=>k.Id).ToArray(),content.Text,sender.Text,ids.Text.Split(new[]{',',';',' ','\r','\n'},StringSplitOptions.RemoveEmptyEntries),strategy.SelectedIndex==1):null);DialogResult=true;},true));buttons.HorizontalAlignment=HorizontalAlignment.Right;buttons.Margin=new Thickness(0,14,0,0);View.Add(layout,buttons,1);
    }
}
