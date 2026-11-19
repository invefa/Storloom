using Storloom.Core;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private async Task ExecuteRules() {
        if (cleanupPlan == null) throw new InvalidOperationException(L.T("SelectionRequired"));
        var snapshot = cleanupPlan; var files = SelectedFiles(); var paths = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = SelectedRules(); var transfers = new List<TransferPlan>(); var deletes = new List<string>(); var commands = new List<string>();
        var transferNames = new Dictionary<Guid, string>();
        var review = new List<ReviewItem>(); var scheduled = new List<string>();
        foreach (string id in ids) {
            var rule = engine.Catalog.Rules.Single(r => r.Id == id); var matches = files.Where(f => f.RuleId == id).ToArray();
            if (rule.Action == CleanupAction.Command) {
                var command = snapshot.Commands.SingleOrDefault(c => c.RuleId == id);
                if (command == null) continue;
                commands.Add(id); scheduled.Add(id); review.Add(new(L.RuleName(rule), L.T("Command"), NativeSize(id), CommandText(command), L.T("NativeScope")) { Bytes = snapshot.Measurements.GetValueOrDefault(id)?.Bytes }); continue;
            }
            if (matches.Length == 0) continue;
            var choice = treatments.GetValueOrDefault(id);
            List<TransferPlan> plans = [];
            if (choice != Treatment.Rule) {
                string target = preferences.ArchiveRoot;
                if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException(L.T("ArchiveRequired"));
                plans = engine.CreateArchivePlans(snapshot, [id], target, choice == Treatment.Copy ? TransferMode.Copy : TransferMode.Move, paths);
            } else if (rule.Action is CleanupAction.Copy or CleanupAction.Move) plans = engine.CreateTransferPlans(snapshot, [id], paths);
            else deletes.Add(id);
            transfers.AddRange(plans);
            foreach (var plan in plans) transferNames[plan.Id] = id;
            scheduled.Add(id); review.Add(new(L.RuleName(rule), choice == Treatment.Rule ? L.T(rule.Action.ToString()) : L.T(choice == Treatment.Copy ? "ArchiveCopy" : "ArchiveMove"),
                View.Size(matches.Sum(f => f.Stamp.Bytes)), string.Join(Environment.NewLine, plans.Select(p => p.Source + " -> " + p.Destination)),
                string.Join(Environment.NewLine, matches.Select(f => f.Path).Take(30))) { Bytes = matches.Sum(f => f.Stamp.Bytes) });
        }
        if (review.Count == 0) throw new InvalidOperationException(L.T("SelectionRequired"));
        string warning = string.Join(Environment.NewLine, new[] { deletes.Count > 0 ? L.T("DeleteNotice") : "", commands.Count > 0 ? L.T("CommandNotice") : "" }.Where(s => s != ""));
        var dialog = new ReviewWindow(review, warning,dataRoot) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var results = review.Zip(scheduled).ToDictionary(p => p.Second, p => new CleanupItemResult(p.First.Name, p.First.Action, null, null, null, L.T("CleanupNotStarted")) { Detail = L.T("CleanupNotStartedHelp") });
        var remainingTransfers=transfers.GroupBy(p=>transferNames[p.Id]).ToDictionary(g=>g.Key,g=>g.Count());
        var transferFailures=new HashSet<string>();
        string journal = Path.Combine(dataRoot, "journals"), summary = Path.Combine(journal, $"cleanup-summary-{Guid.NewGuid():N}.json");
        string? current = null; bool attempted = false;
        await Run(async token => {
            int processed = 0, toolSuccess = 0, issues = 0;
            var progress = Progress();
            void Record(string id, CleanupItemResult result) { results[id] = result; operationView.AddLog(result.Name + ": " + result.State + " | " + result.Detail); if(result.Journal.Length>0)operationView.AddLog(L.T("CleanupJournalAt",result.Journal)); }
            try {
                foreach (var plan in transfers) {
                    token.ThrowIfCancellationRequested(); current = transferNames[plan.Id]; attempted = true;
                    operationView.AddLog(L.T("PhaseCopyVerify") + ": " + plan.Source);
                    var result = await Task.Run(() => new TransferService().Execute(plan, journal, progress, token));
                    var previous = results[current];
                    remainingTransfers[current]--;if(result.Issues.Count>0)transferFailures.Add(current);
                    Record(current, previous with { Freed = (previous.Freed ?? 0) + result.RemovedBytes, State = L.T(result.Cancelled ? "TaskStopped" : remainingTransfers[current]==0&&!transferFailures.Contains(current) ? "Complete" : "Incomplete"),
                        Detail = previous.Detail.Replace(L.T("CleanupNotStartedHelp"), "") + plan.Source+"\n"+L.T("CleanupTransferCounts",result.VerifiedFiles,result.RemovedFiles) + "\n" + string.Join("\n",result.Issues.Select(i=>i.Path+": "+i.Reason))+"\n"+L.T("CleanupJournalAt",result.Manifest)+"\n", Journal = result.Manifest });
                    processed += result.VerifiedFiles; issues += result.Issues.Count; token.ThrowIfCancellationRequested(); current = null;
                }
                bool toolFailed = false;
                foreach (string id in commands) {
                    token.ThrowIfCancellationRequested(); current = id; attempted = true;
                    var outcomes = await engine.ApplyCommands(snapshot, [id], journal, token, progress);
                    foreach (var result in outcomes) {
                        bool success = result.ExitCode == 0 && !result.Cancelled && !result.TimedOut;
                        Record(id, results[id] with { Before=result.BeforeBytes, After=result.AfterBytes, Freed=result.FreedBytes, State=L.T(result.Cancelled?"TaskStopped":success?"Complete":"Incomplete"), Journal=result.Journal,
                            Detail=L.T(result.Cancelled?"TaskStopped":result.TimedOut?"CleanupToolTimeout":success?result.FreedBytes==0?"CleanupToolNoChange":result.FreedBytes==null?"CleanupSizeUnknown":"CleanupToolDone":"CleanupToolFailed",result.ExitCode?.ToString()??"-")+"\n"+ToolJournalDetails(result.Journal) });
                        if(success)toolSuccess++;else{issues++;toolFailed=true;}
                        token.ThrowIfCancellationRequested();
                    }
                    current=null;if(toolFailed)break;
                }
                foreach (string id in deletes) {
                    if(toolFailed)break;
                    token.ThrowIfCancellationRequested(); current=id;attempted=true;
                    operationView.AddLog(L.T("LogDelete",L.RuleName(engine.Catalog.Rules.Single(r=>r.Id==id))));
                    var result=await Task.Run(()=>engine.Apply(snapshot,[id],journal,paths,progress,token));
                    processed+=result.Deleted;issues+=result.Skipped;
                    string detail=L.T("CleanupDeleteCounts",result.Deleted,result.Skipped,result.Pending)+"\n"+L.T(result.LogicalBytes==0&&result.Deleted>0?"CleanupEmptyFiles":result.Deleted==0?"CleanupNothingDeleted":"CleanupLogicalSize")+"\n"+
                        string.Join("\n",result.Failures.Select(f=>f.Path+"\n"+L.T("CleanupReason"+f.Code)+" / "+f.Reason+" (0x"+f.HResult.ToString("X8")+")"));
                    Record(id,results[id] with { Freed=result.LogicalBytes,State=L.T(result.Cancelled?"TaskStopped":result.Skipped>0?"Incomplete":"Complete"),Detail=detail,Journal=result.Journal });
                    token.ThrowIfCancellationRequested();current=null;
                }
                issues+=results.Values.Count(r=>r.State==L.T("CleanupNotStarted"));operationIncomplete=issues>0;
                status.Text=L.T("OperationDone",processed,toolSuccess,issues);operationView.AddLog(status.Text);
            } catch(Exception ex) {
                if(current!=null) {
                    var previous=results[current];
                    results[current]=previous with { State=L.T(ex is OperationCanceledException?"TaskStopped":"Incomplete"),Detail=previous.Detail.Replace(L.T("CleanupNotStartedHelp"),"")+"\n"+ex.Message };
                }
                throw;
            } finally {
                if(attempted) {
                    try {JsonFiles.Write(summary,results.Values.ToList());operationView.AddLog(L.T("CleanupJournalAt",summary));}
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){operationIncomplete=true;status.Text=L.T("CleanupReportSaveFailed",ex.Message);operationView.AddLog(status.Text);}
                    cleanupPlan=null;excluded.Clear();RenderRules();
                }
            }
        });
        if(attempted)new CleanupResultsWindow(results.Values.ToList(),summary){Owner=this}.ShowDialog();
    }
    private static string ToolJournalDetails(string path) {
        try {
            using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));var root=json.RootElement;
            var detail=new List<string>();foreach(string stage in new[]{"before","after"})if(root.TryGetProperty(stage,out var measurement)&&measurement.ValueKind==System.Text.Json.JsonValueKind.Object&&measurement.TryGetProperty("issues",out var entries))
                foreach(var issue in entries.EnumerateArray())detail.Add(stage+": "+issue.GetProperty("path").GetString()+" / "+issue.GetProperty("reason").GetString());
            if(root.TryGetProperty("output",out var output)){string text=output.GetString()??"";detail.Add(text.Length>16000?text[..16000]+"\n"+L.T("CleanupOutputTruncated"):text);}
            return string.Join("\n",detail);
        } catch(Exception ex) when(ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { return L.T("CleanupJournalAt",path); }
    }
}
