using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

internal sealed class ArchiveComparisonWindow : ThemeWindow {
    public ArchiveComparisonWindow(ArchiveComparison result):base(L.T("ArchiveCompare"),1100,720){
        var grid=View.Grid(View.Auto,View.Auto,View.Fill,View.Auto);grid.Margin=new Thickness(24);Body.Children.Add(grid);
        View.Add(grid,View.Text(L.T(result.DifferentAccount?"ArchiveCompareDifferent":"ArchiveCompareHelp"),color:"Muted"),0);
        string Value(long? value)=>value?.ToString("N0")??L.T("ArchiveOriginUnknown");
        string Time(long? value){try{return value==null?L.T("ArchiveOriginUnknown"):DateTimeOffset.FromUnixTimeSeconds(value.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm");}catch(ArgumentOutOfRangeException){return L.T("ArchiveOriginUnknown");}}
        var summary=View.Table(("ConfigurationItem","Metric",180),("ArchiveCompareLeft","Left",0),("ArchiveCompareRight","Right",0));summary.MaxHeight=280;
        summary.ItemsSource=new[]{new{Metric=L.T("Path"),Left=result.Left.Directory,Right=result.Right.Directory},new{Metric=L.T("Account"),Left=result.Left.Account??L.T("ArchiveOriginUnknown"),Right=result.Right.Account??L.T("ArchiveOriginUnknown")},new{Metric=L.T("ArchiveMessageCount"),Left=Value(result.Left.Messages),Right=Value(result.Right.Messages)},new{Metric=L.T("ArchiveResourceCount"),Left=Value(result.Left.Resources),Right=Value(result.Right.Resources)},new{Metric=L.T("ArchiveResourceBytes"),Left=result.Left.ResourceBytes is {} l?View.Size(l):L.T("ArchiveOriginUnknown"),Right=result.Right.ResourceBytes is {} r?View.Size(r):L.T("ArchiveOriginUnknown")},new{Metric=L.T("ArchiveMissingAtExport"),Left=Value(result.Left.MissingMedia),Right=Value(result.Right.MissingMedia)},new{Metric=L.T("ArchiveUnsupportedCount"),Left=Value(result.Left.UnsupportedMessages),Right=Value(result.Right.UnsupportedMessages)},new{Metric=L.T("ArchiveFirstMessage"),Left=Time(result.Left.FirstTimestamp),Right=Time(result.Right.FirstTimestamp)},new{Metric=L.T("ArchiveLastMessage"),Left=Time(result.Left.LastTimestamp),Right=Time(result.Right.LastTimestamp)}};
        ((AdaptiveTable)summary).UseManualWidths();summary.Columns[0].Width=new DataGridLength(180);summary.Columns[1].Width=summary.Columns[2].Width=new DataGridLength(1,DataGridLengthUnitType.Star);
        View.Add(grid,summary,1);var table=View.Table(("ArchiveConversation","Conversation",0),("ArchiveCompareLeft","Left",85),("ArchiveCompareRight","Right",85),("ArchiveCountChange","Delta",100),("ArchiveCompareLeftTime","First",200),("ArchiveCompareRightTime","Last",200));table.Name="archive_comparison_conversations";
        table.ItemsSource=result.Conversations.Select(c=>new{c.Conversation,Left=c.LeftMessages,Right=c.RightMessages,Delta=c.RightMessages-c.LeftMessages,First=Time(c.LeftFirst)+" ~ "+Time(c.LeftLast),Last=Time(c.RightFirst)+" ~ "+Time(c.RightLast)}).ToArray();View.Add(grid,table,2);
        ((AdaptiveTable)table).UseManualWidths();table.Columns[0].Width=new DataGridLength(1,DataGridLengthUnitType.Star);table.Columns[4].Width=table.Columns[5].Width=new DataGridLength(1.5,DataGridLengthUnitType.Star);
        var save=View.Button("ArchiveSaveReport",()=>{var picker=new Microsoft.Win32.SaveFileDialog{Filter="JSON|*.json",FileName="archive-comparison-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"};if(PickerHistory.Show(picker,this,"archive-comparison-report")==true)try{PathPolicy.AssertNoLinks(Path.GetDirectoryName(picker.FileName)!);if(File.Exists(picker.FileName)){PathPolicy.AssertNoLinks(picker.FileName);if(NativeFile.ReadStamp(picker.FileName).Links!=1)throw new IOException("Shared report file.");}JsonFiles.Write(picker.FileName,result);}catch(Exception ex){Notice.Show(this,L.T("Error"),ex.Message);}});
        var buttons=View.Row(save,View.Button("Close",Close));buttons.HorizontalAlignment=HorizontalAlignment.Right;View.Add(grid,buttons,3);
    }
}
