using Storloom.Core;
using Storloom.Core.Localization;

namespace Storloom.App;

public sealed partial class ShellForm {
    private enum Treatment { Rule, Copy, Move }
    private readonly HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Treatment> treatments = new(StringComparer.OrdinalIgnoreCase);
    private ListView ruleList = null!, candidateList = null!, ruleIssues = null!;
    private TextBox filter = null!;
    private Label ruleTitle = null!, ruleDescription = null!, pageLabel = null!, selectionSummary = null!, candidateEmpty = null!;
    private ComboBox treatment = null!;
    private Button reviewSelection = null!;
    private int candidatePage;
    private bool rendering;

    private void BuildRules() {
        var layout = Page("cleanup", "CleanupTitle", "CleanupLead", Ui.Auto(), Ui.Fill(), Ui.Auto());
        var bar = Ui.Flow(); bar.Margin = new Padding(0, 0, 0, 14);
        var scan = AsyncButton("ScanCaches", ScanRules, true); scan.Name = "scan-cleanup";
        bar.Controls.AddRange([scan, Button("ChooseRuleSets", () => ShowPreferences(true))]); layout.Controls.Add(bar, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, Width = 1100, SplitterWidth = 16, FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 300, Panel2MinSize = 380, SplitterDistance = 410, BackColor = Ui.Canvas, Margin = Padding.Empty };
        ruleList = Ui.List(("Rule", 220), ("Action", 85), ("Bytes", 85));
        ruleList.CheckBoxes = true; ruleList.MultiSelect = false; ruleList.Name = "cleanup-rules";
        split.Panel1.Controls.Add(ruleList);
        var right = Ui.Grid(Ui.Auto(), Ui.Auto(), Ui.Auto(), Ui.Fill());
        ruleTitle = Ui.Text(L.T("NoRuleSelection")); ruleTitle.Font = new Font(Font.FontFamily, 11.5f);
        ruleTitle.ForeColor = Ui.Ink; ruleTitle.Margin = new Padding(0, 0, 0, 8);
        ruleDescription = Ui.Text(""); ruleDescription.AutoSize = false; ruleDescription.Dock = DockStyle.Fill;
        ruleDescription.Height = 57; ruleDescription.Margin = new Padding(0, 0, 0, 10);
        right.Controls.Add(ruleTitle, 0, 0); right.Controls.Add(ruleDescription, 0, 1);
        var actionRow = new BufferedGrid { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 12) };
        actionRow.ColumnStyles.Add(new(SizeType.Absolute, 88)); actionRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        actionRow.MinimumSize = new Size(0, 36);
        actionRow.Controls.Add(Ui.FieldLabel(L.T("Treatment")), 0, 0);
        treatment = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty };
        actionRow.Controls.Add(treatment, 1, 0); right.Controls.Add(actionRow, 0, 2);
        var details = new BufferedTabs { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var filePage = new TabPage(L.T("MatchedFiles"));
        var fileLayout = Ui.Grid(Ui.Auto(), Ui.Fill(), Ui.Auto()); fileLayout.Padding = new Padding(6);
        candidateEmpty = Ui.Text(L.T("NoCandidates")); candidateEmpty.Margin = new Padding(8, 8, 0, 12);
        fileLayout.Controls.Add(candidateEmpty, 0, 0);
        candidateList = Ui.List(("Path", 600), ("Bytes", 100)); candidateList.CheckBoxes = true; candidateList.Name = "cleanup-files";
        fileLayout.Controls.Add(candidateList, 0, 1);
        var navigationRow = new BufferedGrid { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 10, 0, 0) };
        navigationRow.ColumnStyles.Add(new(SizeType.Percent, 100)); for (int i = 0; i < 3; i++) navigationRow.ColumnStyles.Add(new(SizeType.AutoSize));
        filter = Ui.Input(); filter.PlaceholderText = L.T("Filter");
        pageLabel = Ui.Text("1 / 1"); pageLabel.Margin = new Padding(4, 8, 0, 0);
        var previous = Button("<", () => { candidatePage = Math.Max(0, candidatePage - 1); ShowCandidates(); }); previous.MinimumSize = new Size(36, 32);
        var next = Button(">", () => { candidatePage++; ShowCandidates(); }); next.MinimumSize = new Size(36, 32);
        navigationRow.Controls.Add(filter, 0, 0); navigationRow.Controls.Add(previous, 1, 0);
        navigationRow.Controls.Add(next, 2, 0); navigationRow.Controls.Add(pageLabel, 3, 0);
        fileLayout.Controls.Add(navigationRow, 0, 2); filePage.Controls.Add(fileLayout);
        ruleIssues = Ui.List(("Path", 430), ("State", 300)); var issuePage = new TabPage(L.T("Issues")); issuePage.Controls.Add(ruleIssues);
        details.TabPages.AddRange([filePage, issuePage]); right.Controls.Add(details, 0, 3); split.Panel2.Controls.Add(right);
        layout.Controls.Add(split, 0, 2);
        var footer = new BufferedGrid { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 14, 0, 0) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        selectionSummary = Ui.Text(L.T("CatalogSummary", engine.Catalog.Rules.Count)); selectionSummary.Margin = new Padding(0, 8, 8, 0);
        reviewSelection = AsyncButton("ReviewSelection", ExecuteRules, true); reviewSelection.Enabled = false; reviewSelection.Name = "review-cleanup";
        footer.Controls.Add(selectionSummary, 0, 0); footer.Controls.Add(reviewSelection, 1, 0); layout.Controls.Add(footer, 0, 3);
        ruleList.SelectedIndexChanged += (_, _) => { candidatePage = 0; RenderRuleDetails(); };
        ruleList.ItemChecked += (_, _) => { if (!rendering) UpdateSelected(); };
        candidateList.ItemChecked += (_, e) => {
            if (rendering || e.Item.Tag is not Candidate file) return;
            if (e.Item.Checked) excluded.Remove(file.Path); else excluded.Add(file.Path); UpdateSelected();
        };
        treatment.SelectedIndexChanged += (_, _) => {
            if (rendering || SelectedRule() is not CleanupRule rule || rule.Action == CleanupAction.Command) return;
            treatments[rule.Id] = (Treatment)treatment.SelectedIndex;
            ruleList.SelectedItems[0].SubItems[1].Text = EffectiveAction(rule); UpdateSelected();
        };
        filter.TextChanged += (_, _) => { candidatePage = 0; ShowCandidates(); };
        RenderRules();
    }
    private CleanupRule? SelectedRule() => ruleList.SelectedItems.Count == 1 ? ruleList.SelectedItems[0].Tag as CleanupRule : null;
    private string[] SelectedRules() => ruleList.CheckedItems.Cast<ListViewItem>().Select(r => ((CleanupRule)r.Tag!).Id).ToArray();
    private List<Candidate> SelectedFiles() {
        var selected = SelectedRules().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return cleanupPlan?.Files.Where(f => selected.Contains(f.RuleId) && !excluded.Contains(f.Path)).ToList() ?? [];
    }
    private void RenderRules() {
        rendering = true; ruleList.BeginUpdate(); ruleList.Items.Clear();
        foreach (var rule in engine.Catalog.Rules.Where(r => r.Enabled)) {
            var files = cleanupPlan?.Files.Where(f => f.RuleId == rule.Id).ToArray() ?? [];
            bool command = cleanupPlan?.Commands.Any(c => c.RuleId == rule.Id) == true;
            Ui.Row(ruleList, rule, L.RuleName(rule), EffectiveAction(rule), rule.Action == CleanupAction.Command ?
                cleanupPlan == null ? "-" : command ? NativeSize(rule.Id) : L.T("ToolNotAvailable") : cleanupPlan == null ? "-" : Ui.Size(files.Sum(f => f.Stamp.Bytes)));
            ruleList.Items[^1].Checked = false;
        }
        ruleList.EndUpdate(); rendering = false;
        if (ruleList.Items.Count > 0) ruleList.Items[0].Selected = true;
        RenderRuleDetails(); UpdateSelected();
    }
    private string EffectiveAction(CleanupRule rule) => rule.Action == CleanupAction.Command ? L.T("Command") :
        treatments.GetValueOrDefault(rule.Id) switch { Treatment.Copy => L.T("Copy"), Treatment.Move => L.T("Move"),
            _ => L.T(rule.Action.ToString()) };
    private string NativeSize(string id) => cleanupPlan?.Measurements.GetValueOrDefault(id) is CacheMeasurement measurement && measurement.Complete ?
        (measurement.Estimated ? "~" : "") + Ui.Size(measurement.Bytes) : L.T("SizeNotScanned");
    private void RenderRuleDetails() {
        if (SelectedRule() is not CleanupRule rule) return;
        ruleTitle.Text = L.RuleName(rule); ruleDescription.Text = L.RuleDescription(rule);
        rendering = true; treatment.Items.Clear();
        if (rule.Action == CleanupAction.Command) { treatment.Items.Add(L.T("NativeScope")); treatment.SelectedIndex = 0; treatment.Enabled = false; }
        else {
            treatment.Items.AddRange([L.T(rule.Action.ToString()) + " (" + L.T("UseRule") + ")", L.T("ArchiveCopy"), L.T("ArchiveMove")]);
            treatment.SelectedIndex = (int)treatments.GetValueOrDefault(rule.Id); treatment.Enabled = true;
        }
        rendering = false;
        ruleIssues.BeginUpdate(); ruleIssues.Items.Clear();
        foreach (var issue in cleanupPlan?.Issues.Where(i => i.RuleId == rule.Id) ?? []) Ui.Row(ruleIssues, issue, issue.Path, issue.Reason);
        ruleIssues.EndUpdate(); ShowCandidates();
    }
    private void ShowCandidates() {
        if (SelectedRule() is not CleanupRule rule) return;
        var files = cleanupPlan?.Files.Where(f => f.RuleId == rule.Id && f.Path.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        int pages = Math.Max(1, (files.Count + 499) / 500); candidatePage = Math.Clamp(candidatePage, 0, pages - 1);
        rendering = true; candidateList.BeginUpdate(); candidateList.Items.Clear();
        foreach (var file in files.Skip(candidatePage * 500).Take(500)) {
            Ui.Row(candidateList, file, file.Path, Ui.Size(file.Stamp.Bytes)); candidateList.Items[^1].Checked = !excluded.Contains(file.Path);
        }
        if (rule.Action == CleanupAction.Command && cleanupPlan?.Commands.FirstOrDefault(c => c.RuleId == rule.Id) is ToolCommandPlan command)
            Ui.Row(candidateList, command, CommandText(command), L.T("ToolManaged"));
        candidateList.CheckBoxes = rule.Action != CleanupAction.Command; candidateList.EndUpdate(); rendering = false;
        candidateEmpty.Visible = cleanupPlan == null || files.Count == 0 || rule.Action == CleanupAction.Command;
        candidateEmpty.Text = L.T(rule.Action == CleanupAction.Command ? "NativeScope" : cleanupPlan == null ? "NoCandidates" : "NoMatches");
        pageLabel.Text = $"{candidatePage + 1} / {pages}";
    }
    private void UpdateSelected() {
        var selected = SelectedFiles(); var ids = SelectedRules();
        int commands = cleanupPlan?.Commands.Count(c => ids.Contains(c.RuleId)) ?? 0;
        reviewSelection.Enabled = cleanupPlan != null && (selected.Count > 0 || commands > 0);
        long nativeBytes = cleanupPlan?.Measurements.Where(m => ids.Contains(m.Key) && m.Value.Complete).Sum(m => m.Value.Bytes) ?? 0;
        selectionSummary.Text = L.T("SummarySelection", selected.Count, Ui.Size(selected.Sum(f => f.Stamp.Bytes) + nativeBytes), commands);
    }
    private async Task ScanRules() => await Run(async token => {
        var progress = Progress(); cleanupPlan = await Task.Run(() => engine.Scan(progress: progress, cancellation: token));
        excluded.Clear(); JsonFiles.Write(Path.Combine(dataRoot, "plans", $"rules-{DateTime.Now:yyyyMMdd-HHmmss}.cleanup-plan.json"), cleanupPlan);
        RenderRules(); status.Text = L.T("CatalogSummary", engine.Catalog.Rules.Count);
    });
    private static string CommandText(ToolCommandPlan command) => '"' + command.Executable + "\" " +
        string.Join(" ", command.Arguments.Select(a => a.Contains(' ') ? '"' + a + '"' : a));
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
                commands.Add(id); review.Add(new(L.RuleName(rule), L.T("Command"), NativeSize(id), CommandText(command), L.T("NativeScope"))); continue;
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
                Ui.Size(matches.Sum(f => f.Stamp.Bytes)), string.Join(Environment.NewLine, plans.Select(p => p.Source + " -> " + p.Destination)),
                string.Join(Environment.NewLine, matches.Select(f => f.Path).Take(30))));
        }
        if (review.Count == 0) throw new InvalidOperationException(L.T("SelectionRequired"));
        string warning = string.Join(Environment.NewLine, new[] { deletes.Count > 0 ? L.T("DeleteNotice") : "", commands.Count > 0 ? L.T("CommandNotice") : "" }.Where(s => s != ""));
        using var dialog = new ReviewForm(review, warning);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(async token => {
            int processed = 0, toolSuccess = 0, issues = 0;
            var results = new List<CleanupItemResult>();
            var progress = Progress(); string journal = Path.Combine(dataRoot, "journals");
            foreach (var plan in transfers) {
                operationView.AddLog(L.T("PhaseCopyVerify") + ": " + plan.Source);
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
                operationView.AddLog(L.T("Delete") + ": " + L.RuleName(engine.Catalog.Rules.Single(r => r.Id == id)));
                var result = await Task.Run(() => engine.Apply(snapshot, [id], journal, paths, progress, token));
                processed += result.Deleted; issues += result.Skipped;
                results.Add(new(L.RuleName(engine.Catalog.Rules.Single(r => r.Id == id)), L.T("Delete"), null, null,
                    result.LogicalBytes, L.T(result.Skipped == 0 ? "Complete" : "Incomplete")));
            }
            JsonFiles.Write(Path.Combine(journal, $"cleanup-summary-{Guid.NewGuid():N}.json"), results);
            cleanupPlan = null; excluded.Clear(); RenderRules(); status.Text = L.T("OperationDone", processed, toolSuccess, issues);
            using var report = new CleanupResultsForm(results); report.ShowDialog(this);
        });
    }
}
