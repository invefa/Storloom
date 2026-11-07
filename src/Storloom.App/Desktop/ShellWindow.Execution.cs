using Storloom.Core;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private async Task ExecuteRules() {
        if (cleanupPlan == null) throw new InvalidOperationException(L.T("SelectionRequired"));
        var snapshot = cleanupPlan; var files = SelectedFiles(); var paths = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = SelectedRules(); var transfers = new List<TransferPlan>(); var deletes = new List<string>(); var commands = new List<string>();
        var transferNames = new Dictionary<Guid, string>();
        var review = new List<ReviewItem>();
        foreach (string id in ids) {
            var rule = engine.Catalog.Rules.Single(r => r.Id == id); var matches = files.Where(f => f.RuleId == id).ToArray();
            if (rule.Action == CleanupAction.Command) {
                var command = snapshot.Commands.SingleOrDefault(c => c.RuleId == id) ?? throw new InvalidDataException(L.T("ToolNotAvailable"));
                commands.Add(id); review.Add(new(L.RuleName(rule), L.T("Command"), NativeSize(id), CommandText(command), L.T("NativeScope")) { Bytes = snapshot.Measurements.GetValueOrDefault(id)?.Bytes }); continue;
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
            foreach (var plan in plans) transferNames[plan.Id] = L.RuleName(rule);
            review.Add(new(L.RuleName(rule), choice == Treatment.Rule ? L.T(rule.Action.ToString()) : L.T(choice == Treatment.Copy ? "ArchiveCopy" : "ArchiveMove"),
                View.Size(matches.Sum(f => f.Stamp.Bytes)), string.Join(Environment.NewLine, plans.Select(p => p.Source + " -> " + p.Destination)),
                string.Join(Environment.NewLine, matches.Select(f => f.Path).Take(30))) { Bytes = matches.Sum(f => f.Stamp.Bytes) });
        }
        if (review.Count == 0) throw new InvalidOperationException(L.T("SelectionRequired"));
        string warning = string.Join(Environment.NewLine, new[] { deletes.Count > 0 ? L.T("DeleteNotice") : "", commands.Count > 0 ? L.T("CommandNotice") : "" }.Where(s => s != ""));
        var dialog = new ReviewWindow(review, warning) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await Run(async token => {
            int processed = 0, toolSuccess = 0, issues = 0;
            var results = new List<CleanupItemResult>();
            var progress = Progress(); string journal = Path.Combine(dataRoot, "journals");
            foreach (var plan in transfers) {
                operationView.AddLog(L.English("PhaseCopyVerify") + ": " + plan.Source);
                var result = await Task.Run(() => new TransferService().Execute(plan, journal, progress, token));
                results.Add(new(transferNames[plan.Id], L.T(plan.Mode.ToString()), null, null,
                    result.RemovedBytes, result.Issues.Count == 0 ? L.T("Complete") : L.T("Incomplete")));
                processed += result.VerifiedFiles; issues += result.Issues.Count; token.ThrowIfCancellationRequested();
            }
            bool toolFailed = false;
            if (commands.Count > 0) {
                var outcomes = await engine.ApplyCommands(snapshot, commands, journal, token, progress);
                foreach (var result in outcomes) {
                    results.Add(new(L.RuleName(engine.Catalog.Rules.Single(r => r.Id == result.RuleId)), L.T("Command"),
                        result.BeforeBytes, result.AfterBytes, result.FreedBytes,
                        L.T(result.ExitCode == 0 && !result.Cancelled && !result.TimedOut ? "Complete" : "Incomplete")));
                    if (result.ExitCode == 0 && !result.Cancelled && !result.TimedOut) toolSuccess++;
                    else { issues++; toolFailed = true; }
                    token.ThrowIfCancellationRequested();
                }
                issues += commands.Count - outcomes.Count;
                JsonFiles.Write(Path.Combine(journal, $"batch-tools-{Guid.NewGuid():N}.json"), new {
                    outcomes, skippedRules = commands.Except(outcomes.Select(r => r.RuleId)).Concat(toolFailed ? deletes : []).ToArray() });
            }
            if (toolFailed) issues += deletes.Count;
            if (deletes.Count > 0 && !toolFailed) foreach (string id in deletes) {
                operationView.AddLog("Delete: " + engine.Catalog.Rules.Single(r => r.Id == id).Name);
                var result = await Task.Run(() => engine.Apply(snapshot, [id], journal, paths, progress, token));
                processed += result.Deleted; issues += result.Skipped;
                results.Add(new(L.RuleName(engine.Catalog.Rules.Single(r => r.Id == id)), L.T("Delete"), null, null,
                    result.LogicalBytes, L.T(result.Skipped == 0 ? "Complete" : "Incomplete")));
            }
            JsonFiles.Write(Path.Combine(journal, $"cleanup-summary-{Guid.NewGuid():N}.json"), results);
            cleanupPlan = null; excluded.Clear(); RenderRules(); status.Text = L.T("OperationDone", processed, toolSuccess, issues);
            operationView.AddLog($"Cleanup completed: files={processed}, nativeCommands={toolSuccess}, issues={issues}.");
            new CleanupResultsWindow(results) { Owner = this }.ShowDialog();
        });
    }
}
