using System.Diagnostics;

namespace Storloom.App;

internal sealed record CleanupItemResult(string Name, string Action, long? Before, long? After, long? Freed, string State) {
    public string Detail { get; init; } = "";
    public string Journal { get; init; } = "";
}
internal sealed class CleanupResultsWindow : ThemeWindow {
    public CleanupResultsWindow(List<CleanupItemResult> results, string? summary = null) : base(L.T("CleanupResults"), 1080, 720) {
        var grid=View.Grid(View.Auto,View.Fill,View.Auto,View.Auto,View.Auto);grid.Margin=new Thickness(24);Body.Children.Add(grid);
        int known=results.Count(r=>r.Freed.HasValue),unknown=results.Count-known;
        string total=known==0?L.T("CleanupTotalUnknown"):L.T("CleanupKnownTotal",View.Size(results.Sum(r=>r.Freed??0)),unknown);
        View.Add(grid,View.Text(total,21),0);
        var rows=results.Select(r=>new { Result=r,r.Name,r.Action,Before=r.Before is long before?View.Size(before):"-",After=r.After is long after?View.Size(after):"-",Freed=r.Freed is long freed?View.Size(freed):L.T("CleanupUnknown"),BeforeBytes=r.Before,AfterBytes=r.After,FreedBytes=r.Freed,r.State }).ToArray();
        var list=View.Table(("Rule","Name",0),("Action","Action",110),("BeforeSize","Before",100),("AfterSize","After",100),("FreedSize","Freed",100),("State","State",120));list.Name="cleanup_result_table";list.ItemsSource=rows;View.Add(grid,list,1);
        var details=new TextBox { Name="cleanup_result_details",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalContentAlignment=VerticalAlignment.Top,Padding=new Thickness(10),Height=170,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,12,0,8) };View.Add(grid,details,2);
        var journal=View.Button("CleanupOpenItemLog",()=>Open(rows[list.SelectedIndex].Result.Journal));journal.IsEnabled=false;
        list.SelectionChanged+=(_,_)=>{var item=list.SelectedItem==null?null:rows.Single(r=>ReferenceEquals(r,list.SelectedItem)).Result;details.Text=item==null?"":item.Name+" / "+item.State+"\n\n"+item.Detail+(item.Journal.Length>0?"\n\n"+L.T("CleanupJournalAt",item.Journal):"");journal.IsEnabled=item?.Journal.Length>0;};
        View.Add(grid,View.Text(L.T("FreedNotice"),color:"Muted"),3);
        var report=View.Button("CleanupOpenReport",()=>Open(summary!));report.IsEnabled=summary!=null&&File.Exists(summary);
        var folder=View.Button("CleanupOpenLogFolder",()=>Open(Path.GetDirectoryName(summary!)!));folder.IsEnabled=summary!=null&&Directory.Exists(Path.GetDirectoryName(summary));
        View.Add(grid,View.Row(journal,report,folder,View.Button("Close",Close)),4);if(rows.Length>0)list.SelectedIndex=0;
    }
    private void Open(string path) { try { Process.Start(new ProcessStartInfo(path){UseShellExecute=true}); } catch(Exception ex){Notice.Show(this,L.T("Error"),ex.Message);} }
}
