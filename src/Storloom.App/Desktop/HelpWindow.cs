using System.Diagnostics;
using System.Windows.Documents;

namespace Storloom.App;

internal sealed class HelpWindow : ThemeWindow {
    private sealed record Topic(string Title,string Text,string Document);
    public HelpWindow(int topic=0,string? document=null):this(topic,document,null) { }
    public HelpWindow(int topic,string? document,string? dataRoot):base(L.T("QuickStart"),1050,760) {
        var grid=View.Grid(View.Auto,View.Fill,View.Auto);grid.Margin=new Thickness(22);Body.Children.Add(grid);
        var search=View.Input();search.Name="help_search";search.ToolTip=L.T("HelpSearch");System.Windows.Automation.AutomationProperties.SetName(search,L.T("HelpSearch"));
        View.Add(grid,View.Row(View.Text(L.T("HelpSearch")),search),0);search.MinWidth=320;
        var split=new Grid();split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(210)});split.ColumnDefinitions.Add(new ColumnDefinition());
        var navigation=new ListBox{Name="help_topics",Margin=new Thickness(0,6,14,0)};var content=new ContentControl{Name="help_content",Margin=new Thickness(0,6,0,0)};
        View.Add(split,navigation,0);View.Add(split,content,0,1);View.Add(grid,split,1);
        var topics=new List<Topic>();
        foreach(var (title,body,extra,file) in new[]{("HelpCleanupTitle","HelpCleanupBody","HelpCleanupDetailed","quickstart"),("HelpArchiveTitle","HelpArchiveBody","HelpArchiveDetailed","archive-library"),("HelpChatTitle","HelpChatBody","HelpChatDetailed","standalone-reader"),("HelpCommandsTitle","HelpCommandsBody","HelpCommandsDetailed","rules"),("HelpTroubleshootingTitle","HelpTroubleshootingBody","HelpTroubleshootingDetailed","quickstart"),("NavOverview","HelpStorageBody","HelpStorageDetailed","quickstart")})
            topics.Add(new(L.T(title),L.T(body)+"\n\n"+L.T(extra),file));
        if(document!=null){topics.Add(new(L.T("OpenAiGuide"),document,"ai-rules"));topic=topics.Count-1;}
        var manual=View.Button("HelpOpenManual",()=>{if(navigation.SelectedItem is Topic selected){string locale=L.Language=="zh-CN"?"zh-CN":"en";string name=selected.Document+(locale=="zh-CN"?".zh-CN":"")+".md";Open(Path.Combine(AppContext.BaseDirectory,"docs",name));}});
        var logs=View.Button("CleanupOpenLogFolder",()=>{if(dataRoot!=null){string path=Path.Combine(dataRoot,"journals");Directory.CreateDirectory(path);Open(path);}});logs.IsEnabled=dataRoot!=null;
        navigation.DisplayMemberPath=nameof(Topic.Title);navigation.SelectionChanged+=(_,_)=>{manual.IsEnabled=navigation.SelectedItem!=null;if(navigation.SelectedItem is Topic selected)content.Content=Document(selected.Title,selected.Text);};
        void Filter(){var chosen=navigation.SelectedItem as Topic;var visible=topics.Where(t=>(t.Title+" "+t.Text).Contains(search.Text.Trim(),StringComparison.CurrentCultureIgnoreCase)).ToArray();navigation.ItemsSource=visible;navigation.SelectedItem=visible.Contains(chosen)?chosen:visible.FirstOrDefault();if(visible.Length==0)content.Content=View.Text(L.T("HelpNoResults"),color:"Muted");}
        search.TextChanged+=(_,_)=>Filter();Filter();navigation.SelectedIndex=Math.Clamp(topic,0,topics.Count-1);
        View.Add(grid,View.Row(logs,manual,View.Button("Close",Close)),2);
        void Open(string path){try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch(Exception ex){Notice.Show(this,L.T("Error"),ex.Message);}}
    }
    private static FlowDocumentScrollViewer Document(string title,string text) {
        var document=new FlowDocument { FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI"),FontSize=14,PagePadding=new Thickness(24),LineHeight=24 };
        document.SetResourceReference(FlowDocument.ForegroundProperty,"Ink");document.SetResourceReference(FlowDocument.BackgroundProperty,"Surface");
        document.Blocks.Add(new Paragraph(new Run(title)){FontSize=25,Margin=new Thickness(0,0,0,22)});
        bool code=false;foreach(string line in text.Replace("\r","").Split('\n')) {
            if(line.StartsWith("```")){code=!code;continue;}if(string.IsNullOrWhiteSpace(line))continue;
            bool heading=line.StartsWith('#')||System.Text.RegularExpressions.Regex.IsMatch(line,@"^\d+\. ");
            var paragraph=new Paragraph(new Run(line.TrimStart('#').Trim())) { Margin=new Thickness(0,heading?12:0,0,10),FontWeight=heading?FontWeights.SemiBold:FontWeights.Normal };
            if(code)paragraph.FontFamily=new FontFamily("Consolas");document.Blocks.Add(paragraph);
        }
        return new FlowDocumentScrollViewer{Document=document,IsToolBarVisible=false,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
}
