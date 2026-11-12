using Storloom.Core;
using Storloom.Core.Chats;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private string? measuringAccount;
    private void RefreshAccountRows(bool sortSizes = false) {
        var selected=(chatList.SelectedItem as AccountRow)?.Location;
        bool nextEnabled=chatNext.IsEnabled;
        chatList.ItemsSource=locations.Select(l=>new AccountRow(l,l.Directory.Equals(measuringAccount,StringComparison.OrdinalIgnoreCase))).ToArray();
        chatList.SelectedItem=chatList.Items.OfType<AccountRow>().FirstOrDefault(r=>selected!=null&&r.Location.Platform==selected.Platform&&r.Location.Directory.Equals(selected.Directory,StringComparison.OrdinalIgnoreCase));
        chatNext.IsEnabled=nextEnabled;
        if(sortSizes){foreach(var column in chatList.Columns)column.SortDirection=null;View.Sort(chatList,"Bytes");}
    }
    private async Task MeasureAccountSizes(bool selectedOnly) {
        var selected=(chatList.SelectedItem as AccountRow)?.Location;
        var requested=locations.Where(l=>!selectedOnly||l==selected).ToArray();if(requested.Length==0)return;
        try { await RunPausable(async token=>{
            long totalBytes=0,totalFiles=0;int finished=0,issues=0;
            var operationProgress=Progress();
            foreach(var account in requested){
                if(token.IsCancellationRequested)break;
                measuringAccount=account.Directory;bool acceptingProgress=true;
                void Update(long bytes,long files){
                    int index=locations.FindIndex(l=>l.Platform==account.Platform&&l.Directory.Equals(account.Directory,StringComparison.OrdinalIgnoreCase));
                    if(index>=0)locations[index]=locations[index] with{AccountBytes=bytes,AccountFiles=files,SizeComplete=false,SizeScannedAt=null};
                    RefreshAccountRows();
                }
                Update(0,0);
                var progress=new ProgressRelay(Dispatcher,value=>{
                    if(!acceptingProgress)return;
                    Update(value.Bytes,value.Files);
                    accountNotice.Text=L.T("AccountSizeScanProgress",finished+1,requested.Length,account.Account,value.Files,View.Size(value.Bytes));
                    operationProgress.Report(value with{RuleId="chat-account-size",Files=(int)Math.Min(int.MaxValue,totalFiles+value.Files),Bytes=totalBytes+value.Bytes});
                });
                StorageScan result;
                try{result=await Task.Run(()=>new StorageScanner().Measure(account.Directory,progress,token));}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){
                    result=new(account.Directory,DateTimeOffset.UtcNow,[],[],[new("chat-account-size",account.Directory,ex.Message)],0,0,token.IsCancellationRequested);
                }finally{acceptingProgress=false;}
                measuringAccount=null;
                int rowIndex=locations.FindIndex(l=>l.Platform==account.Platform&&l.Directory.Equals(account.Directory,StringComparison.OrdinalIgnoreCase));
                if(rowIndex>=0)locations[rowIndex]=locations[rowIndex] with{AccountBytes=result.Bytes,AccountFiles=result.Files,SizeComplete=!result.Cancelled&&result.Issues.Count==0,SizeScannedAt=result.CreatedAt};
                totalBytes+=result.Bytes;totalFiles+=result.Files;issues+=result.Issues.Count;if(!result.Cancelled)finished++;
                RefreshAccountRows(sortSizes:true);
                operationView.AddLog(L.T("AccountSizeScanResult",account.Account,View.Size(result.Bytes),result.Files,L.T(result.Cancelled||result.Issues.Count>0?"AccountSizePartial":"Complete")));
                foreach(var issue in result.Issues.Take(20))operationView.AddLog(L.T("LogIssue",issue.RuleId,issue.Path,issue.Reason));
                if(result.Cancelled)break;
            }
            measuringAccount=null;
            accountNotice.Text=L.T("AccountSizeScanDone",finished,requested.Length,totalFiles,View.Size(totalBytes),issues);
            status.Text=accountNotice.Text;
            if(token.IsCancellationRequested||issues>0)operationIncomplete=true;
        }); } finally { measuringAccount=null;if(chatStep==0)RefreshAccountRows(sortSizes:true); }
    }
}
