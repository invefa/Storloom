using System.Text.Json.Nodes;
using Storloom.Core.Chats;

namespace Storloom.App;

internal sealed class VaultPasswordWindow : ThemeWindow {
    private readonly PasswordBox password=View.SecretInput(),confirmation=View.SecretInput();
    internal string? Password { get; private set; }
    internal void ClearSecret() => Password=null;
    internal bool IncludeMedia { get; private set; }=true;
    internal int Workers { get; private set; }=2;
    internal ChatProtectionSelection? Selection {get;private set;}
    public VaultPasswordWindow(bool creating,bool encrypting,string? directory=null) : base(L.T(creating?"VaultCreate":"VaultUnlock"),520,creating?480:encrypting?390:260) {
        MinWidth=500;MinHeight=240;SizeToContent=SizeToContent.Height;MaxHeight=SystemParameters.WorkArea.Height-40;ResizeMode=ResizeMode.NoResize;
        Caption.ShowMaximize=Caption.ShowMinimize=false;
        var layout=View.Grid(View.Fill,View.Auto);layout.Margin=new Thickness(20);Body.Children.Add(layout);var stack=new StackPanel();var scroll=View.FormScroll(stack);scroll.MaxHeight=SystemParameters.WorkArea.Height-180;View.Add(layout,scroll,0);
        stack.Children.Add(View.Text(L.T(creating?"VaultCreateHelp":"VaultUnlockHelp"),color:"Muted"));
        stack.Children.Add(View.Field("VaultPassword",password));
        if(creating)stack.Children.Add(View.Field("VaultConfirmPassword",confirmation));
        var media=new CheckBox{Content=L.T("VaultIncludeMedia"),IsChecked=true,Margin=new Thickness(0,12,0,0)};
        var workers=WorkerRecommendation.Selector(2,true,"encryption",()=>directory);
        if(encrypting){stack.Children.Add(media);stack.Children.Add(new Expander{Header=L.T("TaskDetails"),Content=View.Text(L.T("VaultPartialHelp"),color:"Muted"),Margin=new Thickness(0,8,0,8)});stack.Children.Add(View.Field("VaultWorkers",workers));}
        Func<ChatProtectionSelection?> selection=()=>null;
        if(creating&&encrypting&&directory!=null&&Directory.Exists(directory)){
            var scope=new CheckBox{Content=L.T("VaultSelectScope"),Margin=new Thickness(0,12,0,4)};stack.Children.Add(scope);
            var options=new StackPanel{Visibility=Visibility.Collapsed};stack.Children.Add(options);
            var conversations=new List<(string Id,CheckBox Check)>();
            try{string source=File.ReadAllText(Path.Combine(directory,"archive-index.js"));
                var index=JsonNode.Parse(source["window.archiveIndex=".Length..].TrimEnd().TrimEnd(';'))!;
                foreach(var chat in index["conversations"]!.AsArray()){
                    string id=chat!["id"]!.GetValue<string>();var name=View.Text(index["conversationNames"]?[id]?.GetValue<string>()??chat["name"]?.GetValue<string>()??id);name.Margin=new Thickness(0);var check=new CheckBox{Content=name};conversations.Add((id,check));
                }
            }catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or InvalidOperationException){ }
            options.Children.Add(View.Text(L.T("VaultScopeHelp"),color:"Muted"));
            var chatList=new StackPanel();foreach(var pair in conversations)chatList.Children.Add(pair.Check);
            var chatExpander=new Expander{Header=L.T("VaultConversations"),Content=new ScrollViewer{Content=chatList,MaxHeight=120,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};options.Children.Add(chatExpander);
            var kinds=new List<(string Id,CheckBox Check)>();var kindRow=new WrapPanel();
            foreach(string kind in new[]{"text","image","sticker","audio","video","file","reply","system","poke","recall","reaction","link","forward","unparsed"}){var check=new CheckBox{Content=L.T("ReaderKind_"+kind)};kinds.Add((kind,check));kindRow.Children.Add(check);}
            options.Children.Add(new Expander{Header=L.T("ReaderMessageType"),Content=kindRow});
            var content=View.Input();var sender=View.Input();var ids=View.Input();
            options.Children.Add(View.Field("ReaderContent",content));options.Children.Add(View.Field("ReaderSender",sender));options.Children.Add(View.Field("VaultMessageIds",ids));
            var strategy=new ComboBox{ItemsSource=new[]{L.T("VaultScopeTogether"),L.T("VaultScopeStrict")},SelectedIndex=0};options.Children.Add(strategy);options.Children.Add(View.Text(L.T("VaultStrategyHelp"),color:"Muted"));
            scope.Checked+=(_,_)=>options.Visibility=Visibility.Visible;scope.Unchecked+=(_,_)=>options.Visibility=Visibility.Collapsed;
            selection=()=>scope.IsChecked==true?new ChatProtectionSelection(conversations.Where(p=>p.Check.IsChecked==true).Select(p=>p.Id).ToArray(),kinds.Where(p=>p.Check.IsChecked==true).Select(p=>p.Id).ToArray(),content.Text,sender.Text,ids.Text.Split(new[]{',','，',';','；',' ','\r','\n'},StringSplitOptions.RemoveEmptyEntries),strategy.SelectedIndex==1):null;
        }
        var error=View.Text("",color:"Muted");stack.Children.Add(error);
        var row=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        var cancel=new Button{Content=L.T("Cancel"),IsCancel=true};cancel.Click+=(_,_)=>Close();row.Children.Add(cancel);
        var okay=new Button{Content=L.T(encrypting?"VaultEncrypt":"VaultUnlock"),IsDefault=true};okay.Click+=(_,_)=>{
            if(creating&&(password.Password.Length<8||password.Password!=confirmation.Password)){error.Text=L.T("VaultPasswordMismatch");return;}
            if(password.Password.Length==0)return;
            Selection=selection();Password=password.Password;IncludeMedia=media.IsChecked==true;Workers=workers.SelectedValue is int count&&count>0?count:WorkerRecommendation.For("encryption",directory);password.Clear();confirmation.Clear();DialogResult=true;
        };row.Children.Add(okay);View.Add(layout,row,1);Loaded+=(_,_)=>password.Focus();Closed+=(_,_)=>{password.Clear();confirmation.Clear();};
    }
}
