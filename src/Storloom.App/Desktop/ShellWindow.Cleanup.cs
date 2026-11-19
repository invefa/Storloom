using System.ComponentModel;
using Storloom.Core;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private enum Treatment { Rule, Copy, Move }
    private readonly HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Treatment> treatments = new(StringComparer.OrdinalIgnoreCase);
    private DataGrid ruleList = null!, candidateList = null!, ruleIssues = null!;
    private TextBox filter = null!;
    private TextBlock ruleTitle = null!, ruleDescription = null!, selectionSummary = null!, candidateNotice = null!;
    private ComboBox treatment = null!;
    private Button reviewSelection = null!;
    private Button scanCleanup = null!, chooseScanScope = null!;
    private TableSelection ruleSelection = null!, fileSelection = null!;
    private TextBlock cleanupStage = null!;
    private readonly HashSet<string> scanTargets = new(StringComparer.OrdinalIgnoreCase);
    private bool rendering;
    private void BuildRules() {
        var layout = Page("cleanup", "CleanupTitle", "CleanupLead", View.Auto, View.Fill, View.Auto);
        scanCleanup = AsyncButton("ScanSelectedTargets", ScanRules, true); scanCleanup.Name = "scan_cleanup";
        chooseScanScope = Button("ChooseScanScope", () => { cleanupPlan = null; excluded.Clear(); RenderRules(); });
        chooseScanScope.Name = "choose_scan_scope";
        var invert = Button("InvertSelection", () => ruleSelection.Invert()); invert.Name = "invert_cleanup_rules";
        var bar = View.Row(scanCleanup, chooseScanScope, invert, Button("ChooseRuleSets", () => ShowPreferences(true))); bar.Margin = new Thickness(0, 0, 0, 10);
        var split = new Grid(); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.4,GridUnitType.Star), MinWidth = 180 });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.6,GridUnitType.Star), MinWidth = 200 });
        split.SizeChanged+=(_,_)=>split.ColumnDefinitions[0].Width=new GridLength(Math.Clamp(split.ActualWidth*.4,180,350));
        ruleList = View.Table(("Rule", "Name", 0), ("Action", "Action", 62), ("Bytes", "Size", 80)); View.Checks(ruleList); ruleList.Name = "cleanup_rules";
        ruleSelection = new TableSelection(ruleList, "select_all_cleanup_rules", L.T("SelectAll"));
        var left = View.Grid(View.Auto, View.Auto, View.Auto, View.Fill); var header = layout.Children[0]; layout.Children.RemoveAt(0);
        cleanupStage = View.Text("", color: "Muted"); cleanupStage.Name = "cleanup_stage";
        View.Add(left, header, 0); View.Add(left, bar, 1); View.Add(left, cleanupStage, 2); View.Add(left, ruleList, 3);
        View.Add(split, left, 0); var divider = SidebarSplitter("Canvas"); divider.Name = "cleanup_splitter"; View.Add(split, divider, 0, 1);
        var right = View.Grid(View.Auto, View.Auto, View.Auto, View.Fill); right.Margin = new Thickness(10, 0, 0, 0); ruleTitle = View.Text(L.T("NoRuleSelection"), 17); ruleDescription = View.Text("", color: "Muted");
        View.Add(right, ruleTitle, 0); View.Add(right, ruleDescription, 1);
        treatment = new ComboBox { Margin = new Thickness(12, 0, 0, 0), MinWidth = 140 };
        var treatmentRow = new DockPanel { Margin = new Thickness(0, 2, 0, 8) }; var treatmentLabel = View.Text(L.T("Treatment")); treatmentLabel.Margin = new Thickness(0); treatmentRow.Children.Add(treatmentLabel); treatmentRow.Children.Add(treatment);
        View.Add(right, treatmentRow, 2);
        var tabs = new TabControl(); var files = View.Grid(View.Auto, View.Fill);
        filter = View.Input(); filter.ToolTip = L.T("Filter"); filter.Margin = new Thickness(6, 6, 6, 6);
        candidateNotice = View.Text(L.T("NoCandidates"), color: "Muted"); candidateList = View.Table(("Path", "Path", 0), ("Bytes", "Size", 110)); View.Checks(candidateList); candidateList.Name = "cleanup_files";
        fileSelection = new TableSelection(candidateList, "select_all_cleanup_files", L.T("SelectAll"));
        var invertFiles = Button("InvertSelection", () => fileSelection.Invert()); invertFiles.Name = "invert_cleanup_files";
        var fileBar = new DockPanel(); DockPanel.SetDock(invertFiles, Dock.Right); fileBar.Children.Add(invertFiles); fileBar.Children.Add(filter);
        View.Add(files, fileBar, 0); View.Add(files, candidateList, 1);
        candidateNotice.IsHitTestVisible = false; candidateNotice.VerticalAlignment = VerticalAlignment.Center; candidateNotice.HorizontalAlignment = HorizontalAlignment.Center;
        candidateNotice.Margin = new Thickness(16); View.Add(files, candidateNotice, 1);
        tabs.Items.Add(new TabItem { Header = L.T("MatchedFiles"), Content = files }); ruleIssues = View.Table(("Path", "Path", 0), ("State", "Reason", 260));
        tabs.Items.Add(new TabItem { Header = L.T("Issues"), Content = ruleIssues }); View.Add(right, tabs, 3); View.Add(split, right, 0, 2);
        Grid.SetRowSpan(split, 3); View.Add(layout, split, 0);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        reviewSelection = AsyncButton("ReviewSelection", ExecuteRules, true); reviewSelection.Name = "review_cleanup"; reviewSelection.IsEnabled = false;
        DockPanel.SetDock(reviewSelection, Dock.Right); footer.Children.Add(reviewSelection); selectionSummary = View.Text(""); footer.Children.Add(selectionSummary); View.Add(layout, footer, 3);
        var lastResult=Button("CleanupLastReport",()=>{
            try { string directory=Path.Combine(dataRoot,"journals");string? path=Directory.Exists(directory)?Directory.EnumerateFiles(directory,"cleanup-summary-*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault():null;
                if(path==null){Notice.Show(this,L.T("CleanupResults"),L.T("CleanupNoReport"));return;}
                new CleanupResultsWindow(JsonFiles.Read<List<CleanupItemResult>>(path),path){Owner=this}.ShowDialog();
            }catch(Exception ex){Error(ex);}
        });lastResult.Name="cleanup_last_report";DockPanel.SetDock(lastResult,Dock.Right);footer.Children.Insert(1,lastResult);
        ruleList.SelectionChanged += (_, _) => RenderRuleDetails(); filter.TextChanged += (_, _) => ShowCandidates();
        treatment.SelectionChanged += (_, _) => {
            if (rendering || SelectedRule() is not CleanupRule rule || rule.Action == CleanupAction.Command) return;
            treatments[rule.Id] = (Treatment)Math.Max(0, treatment.SelectedIndex);
            if (ruleList.SelectedItem is RuleRow row) row.Action = EffectiveAction(rule); UpdateSelected();
        };
        ruleSelection.SelectionChanged += UpdateSelected; fileSelection.SelectionChanged += UpdateSelected;
        RenderRules();
    }
    private CleanupRule? SelectedRule() => (ruleList.SelectedItem as RuleRow)?.Rule;
    private string[] SelectedRules() => ruleList.Items.OfType<RuleRow>().Where(r => r.Selected).Select(r => r.Rule.Id).ToArray();
    private List<Candidate> SelectedFiles() {
        var selected = SelectedRules().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return cleanupPlan?.Files.Where(f => selected.Contains(f.RuleId) && !excluded.Contains(f.Path)).ToList() ?? [];
    }
    private void RenderRules() {
        if (cleanupPlan == null && ruleList.ItemsSource != null && scanCleanup.Visibility == Visibility.Visible) {
            scanTargets.Clear(); scanTargets.UnionWith(SelectedRules());
        }
        bool results = cleanupPlan != null;
        var resultIds = cleanupPlan == null ? [] : cleanupPlan.Files.Select(f => f.RuleId).Concat(cleanupPlan.Commands.Select(c => c.RuleId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visibleIds = resultIds.Concat(cleanupPlan?.Issues.Select(i => i.RuleId) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        scanCleanup.Visibility = results ? Visibility.Collapsed : Visibility.Visible;
        chooseScanScope.Visibility = results ? Visibility.Visible : Visibility.Collapsed;
        cleanupStage.Text = L.T(results ? "CleanupResultsHelp" : "CleanupScopeHelp");
        ruleList.ItemsSource = engine.Catalog.Rules.Where(r => r.Enabled && (!results || visibleIds.Contains(r.Id))).Select(rule => new RuleRow(rule, EffectiveAction(rule), rule.Action == CleanupAction.Command ?
            cleanupPlan == null ? "-" : cleanupPlan.Commands.Any(c => c.RuleId == rule.Id) ? NativeSize(rule.Id) : L.T("ToolNotAvailable") :
            cleanupPlan == null ? "-" : View.Size(cleanupPlan.Files.Where(f => f.RuleId == rule.Id).Sum(f => f.Stamp.Bytes)),
            cleanupPlan == null ? null : rule.Action == CleanupAction.Command ? cleanupPlan.Measurements.GetValueOrDefault(rule.Id)?.Bytes : cleanupPlan.Files.Where(f => f.RuleId == rule.Id).Sum(f => f.Stamp.Bytes), results ? resultIds.Contains(rule.Id) : scanTargets.Contains(rule.Id))).ToArray();
        if (ruleList.Columns.Any(c => c.SortDirection != null)) View.Sort(ruleList, "Bytes");
        if (ruleList.Items.Count > 0) ruleList.SelectedIndex = 0; RenderRuleDetails(); UpdateSelected();
    }
    private string EffectiveAction(CleanupRule rule) => rule.Action == CleanupAction.Command ? L.T("Command") : treatments.GetValueOrDefault(rule.Id) switch {
        Treatment.Copy => L.T("Copy"), Treatment.Move => L.T("Move"), _ => L.T(rule.Action.ToString()) };
    private string NativeSize(string id) => cleanupPlan?.Measurements.GetValueOrDefault(id) is CacheMeasurement m && m.Complete ? (m.Estimated ? "~" : "") + View.Size(m.Bytes) : L.T("SizeNotScanned");
    private void RenderRuleDetails() {
        if (SelectedRule() is not CleanupRule rule) {
            ruleTitle.Text = L.T("NoRuleSelection"); ruleDescription.Text = ""; treatment.Items.Clear();
            candidateList.ItemsSource = null; ruleIssues.ItemsSource = null;
            candidateNotice.Text = L.T("CleanupNoResults"); candidateNotice.Visibility = Visibility.Visible; return;
        }
        ruleTitle.Text = L.RuleName(rule); ruleDescription.Text = L.RuleDescription(rule);
        rendering = true; treatment.Items.Clear();
        if (rule.Action == CleanupAction.Command) { treatment.Items.Add(L.T("NativeScope")); treatment.SelectedIndex = 0; treatment.IsEnabled = false; }
        else { foreach (string label in new[] { L.T(rule.Action.ToString()) + " (" + L.T("UseRule") + ")", L.T("ArchiveCopy"), L.T("ArchiveMove") }) treatment.Items.Add(label);
            treatment.SelectedIndex = (int)treatments.GetValueOrDefault(rule.Id); treatment.IsEnabled = true; }
        rendering = false; ruleIssues.ItemsSource = cleanupPlan?.Issues.Where(i => i.RuleId == rule.Id).ToArray(); ShowCandidates();
    }
    private void ShowCandidates() {
        if (SelectedRule() is not CleanupRule rule) return;
        candidateList.ItemsSource = cleanupPlan?.Files.Where(f => f.RuleId == rule.Id && f.Path.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).Select(f =>
            new FileRow(f, !excluded.Contains(f.Path), selected => { if (selected) excluded.Remove(f.Path); else excluded.Add(f.Path); })).ToArray();
        candidateNotice.Text = rule.Action == CleanupAction.Command && cleanupPlan?.Commands.FirstOrDefault(c => c.RuleId == rule.Id) is ToolCommandPlan command ?
            L.T("NativeScope") + "\n" + CommandText(command) : L.T(cleanupPlan == null ? "NoCandidates" : candidateList.Items.Count == 0 ? "NoMatches" : "MatchedFiles");
        candidateNotice.Visibility = candidateList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (candidateList.Items.Count > 0) View.Sort(candidateList, "Bytes");
        fileSelection.Refresh();
    }
    private void UpdateSelected() {
        if (ruleSelection.IsChanging || fileSelection.IsChanging) return;
        ruleSelection.Refresh();
        fileSelection.Refresh();
        scanCleanup.IsEnabled = SelectedRules().Length > 0 || operation != null;
        var files = SelectedFiles(); var ids = SelectedRules(); int commands = cleanupPlan?.Commands.Count(c => ids.Contains(c.RuleId)) ?? 0;
        reviewSelection.IsEnabled = cleanupPlan != null && (files.Count > 0 || commands > 0);
        long native = cleanupPlan?.Measurements.Where(m => ids.Contains(m.Key) && m.Value.Complete).Sum(m => m.Value.Bytes) ?? 0;
        selectionSummary.Text = cleanupPlan == null ? L.T("ScanScopeSummary", ids.Length) : L.T("SummarySelection", files.Count, View.Size(files.Sum(f => f.Stamp.Bytes) + native), commands);
    }
    private async Task ScanRules() {
        var ids = SelectedRules(); if (ids.Length == 0) return;
        scanTargets.Clear(); scanTargets.UnionWith(ids);
        await Run(async token => {
        var progress = Progress(); var scanned = await Task.Run(() => engine.Scan(ids, progress: progress, cancellation: token));
        token.ThrowIfCancellationRequested(); cleanupPlan = scanned;
        excluded.Clear(); JsonFiles.Write(Path.Combine(dataRoot, "plans", $"rules-{DateTime.Now:yyyyMMdd-HHmmss}.cleanup-plan.json"), cleanupPlan);
        RenderRules(); status.Text = L.T("CleanupScanDone", ids.Length, cleanupPlan.Files.Count, cleanupPlan.Commands.Count);
        });
        UpdateSelected();
    }
    private static string CommandText(ToolCommandPlan command) => '"' + command.Executable + "\" " + string.Join(" ", command.Arguments.Select(a => a.Contains(' ') ? '"' + a + '"' : a));

    private sealed class RuleRow(CleanupRule rule, string action, string size, long? bytes, bool initiallySelected = false) : ISelectableRow {
        private bool selected = initiallySelected; private string effectiveAction = action;
        public CleanupRule Rule => rule; public string Name => L.RuleName(rule); public string Size => size;
        public long? Bytes => bytes;
        public string Action { get => effectiveAction; set { effectiveAction = value; PropertyChanged?.Invoke(this, new(nameof(Action))); } }
        public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed class FileRow(Candidate file, bool selected, Action<bool> changed) : ISelectableRow {
        public string Path => file.Path; public string Size => View.Size(file.Stamp.Bytes);
        public long Bytes => file.Stamp.Bytes;
        public bool Selected { get => selected; set { if (selected == value) return; selected = value; changed(value); PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
