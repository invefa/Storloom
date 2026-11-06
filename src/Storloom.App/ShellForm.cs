using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
using Storloom.Core.Chats;
using Storloom.Core.Localization;

namespace Storloom.App;

public sealed record AppPreferences {
    public string Language { get; init; } = L.Language;
    public string BackupRoot { get; init; } = "";
}

public sealed class ShellForm : Form {
    private readonly string dataRoot = Path.Combine(AppContext.BaseDirectory, "data");
    private readonly string builtin = Path.Combine(AppContext.BaseDirectory, "rules", "builtin.json");
    private AppPreferences preferences = new();
    private CleanupEngine engine = null!;
    private CleanupPlan? cleanupPlan;
    private StorageScan? storageScan;
    private TransferPlan? transferPlan;
    private readonly HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? operation;
    private readonly Label status = new() { Dock = DockStyle.Fill, Padding = new Padding(10), AutoEllipsis = true };
    private TabControl tabs = null!;
    private ComboBox language = null!;
    private Button cancel = null!, executeRules = null!, executeTransfer = null!;
    private TextBox rootInput = null!, sourceInput = null!, destinationInput = null!, backupInput = null!;
    private ListView directoryList = null!, largeList = null!, overviewIssues = null!;
    private ListView ruleList = null!, candidateList = null!, ruleIssues = null!, transferList = null!, chatList = null!;
    private ComboBox transferMode = null!;
    private TextBox filter = null!, keyInput = null!, ruleDescription = null!;
    private Label pageLabel = null!;
    private int candidatePage;
    private bool rendering;
    private byte[]? capturedKey;
    private List<ChatLocation> locations = [];
    private readonly Dictionary<string, string> verifiedBackups = new(StringComparer.OrdinalIgnoreCase);

    public ShellForm() {
        string prefs = Path.Combine(dataRoot, "preferences.json");
        if (File.Exists(prefs)) preferences = JsonFiles.Read<AppPreferences>(prefs);
        L.SetLanguage(preferences.Language);
        ReloadEngine();
        Text = "Storloom";
        ClientSize = new Size(1380, 860);
        MinimumSize = new Size(1140, 740);
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildUi();
        FormClosing += (_, e) => {
            if (operation != null) { operation.Cancel(); e.Cancel = true; status.Text = L.T("Busy"); }
            else if (capturedKey != null) CryptographicOperations.ZeroMemory(capturedKey);
        };
    }

    private void BuildUi() {
        Controls.Clear();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 66));
        Controls.Add(layout);
        var top = Flow();
        top.Controls.Add(new Label { Text = "Storloom", AutoSize = true,
            Font = new Font(Font.FontFamily, 13, FontStyle.Bold), Margin = new Padding(8, 8, 18, 4) });
        language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        language.Items.AddRange(["English", "简体中文"]);
        language.SelectedIndex = L.Language == "en" ? 0 : 1;
        language.SelectedIndexChanged += (_, _) => {
            if (operation != null) return;
            preferences = preferences with { Language = language.SelectedIndex == 0 ? "en" : "zh-CN" };
            JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences);
            L.SetLanguage(preferences.Language); BuildUi();
        };
        top.Controls.Add(language);
        top.Controls.Add(Button("OpenData", () => OpenFolder(dataRoot)));
        cancel = Button("Cancel", () => operation?.Cancel()); cancel.Enabled = false;
        top.Controls.Add(cancel);
        layout.Controls.Add(top, 0, 0);
        tabs = new TabControl { Dock = DockStyle.Fill };
        layout.Controls.Add(tabs, 0, 1);
        layout.Controls.Add(status, 0, 2);
        BuildOverview(); BuildRules(); BuildTransfers(); BuildChats();
        status.Text = L.T("Ready");
    }

    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Top, AutoSize = true,
        Padding = new Padding(7), WrapContents = true };
    private TabPage Page(string key) { var page = new TabPage(L.T(key)); tabs.TabPages.Add(page); return page; }
    private static ListView List(params (string Key, int Width)[] columns) {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HideSelection = false, ShowItemToolTips = true };
        foreach (var (key, width) in columns) list.Columns.Add(L.T(key), width);
        return list;
    }
    private Button Button(string key, Action action) {
        var button = new Button { Text = L.T(key), AutoSize = true, Padding = new Padding(5, 2, 5, 2) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { Error(ex); } };
        return button;
    }
    private Button AsyncButton(string key, Func<Task> action) {
        var button = new Button { Text = L.T(key), AutoSize = true, Padding = new Padding(5, 2, 5, 2) };
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Error(ex); } };
        return button;
    }
    private static Label Hint(string key) => new() { Text = L.T(key), Dock = DockStyle.Top,
        AutoSize = false, Height = 64, Padding = new Padding(8), AutoEllipsis = true };
    private static void Row(ListView list, object tag, params string[] values) {
        var row = new ListViewItem(values) { Tag = tag, ToolTipText = string.Join("\n", values) };
        list.Items.Add(row);
    }
    private static string FormatSize(long bytes) => $"{bytes / 1073741824d:N3} GiB";
    private static string Folder(string? initial = null) {
        using var dialog = new FolderBrowserDialog { InitialDirectory = initial ?? "", UseDescriptionForTitle = true };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : "";
    }
    private static void OpenFolder(string path) {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private void Error(Exception exception) {
        status.Text = exception.Message;
        MessageBox.Show(this, exception.Message, L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    private bool Confirm(string text) => MessageBox.Show(this, text, L.T("Confirm"),
        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    private IProgress<ScanProgress> Progress() => new Progress<ScanProgress>(p =>
        status.Text = L.T("Progress", p.Files, p.Bytes / 1073741824d));
    private async Task Run(Func<CancellationToken, Task> work) {
        if (operation != null) return;
        using var source = new CancellationTokenSource();
        operation = source; tabs.Enabled = false; language.Enabled = false; cancel.Enabled = true;
        try { await work(source.Token); }
        catch (OperationCanceledException) { status.Text = L.T("Cancelled"); }
        catch (Exception ex) { Error(ex); }
        finally { operation = null; tabs.Enabled = true; language.Enabled = true; cancel.Enabled = false; }
    }

    private void BuildOverview() {
        var page = Page("Overview");
        rootInput = new TextBox { Width = 560, Text = Path.GetPathRoot(Environment.SystemDirectory) };
        var bar = Flow();
        bar.Controls.AddRange([rootInput, Button("Browse", () => { string p = Folder(rootInput.Text); if (p != "") rootInput.Text = p; }),
            AsyncButton("Scan", ScanStorage), Button("Back", () => {
                if (directoryList.Tag is string current) ShowDirectory(Path.GetDirectoryName(current) ?? current);
            })]);
        var sections = new TabControl { Dock = DockStyle.Fill };
        directoryList = List(("Path", 780), ("Bytes", 160), ("Files", 110), ("Share", 90));
        largeList = List(("Path", 930), ("Bytes", 170));
        overviewIssues = List(("Path", 740), ("State", 460));
        foreach (var (title, control) in new[] { ("Directories", directoryList),
            ("LargestFiles", largeList), ("Issues", overviewIssues) }) {
            var tab = new TabPage(L.T(title)); tab.Controls.Add(control); sections.TabPages.Add(tab);
        }
        directoryList.DoubleClick += (_, _) => {
            if (directoryList.SelectedItems.Count == 1 && directoryList.SelectedItems[0].Tag is StorageEntry entry)
                ShowDirectory(entry.Path);
        };
        var actions = Flow(); actions.Dock = DockStyle.Bottom;
        actions.Controls.Add(Button("Transfer", () => {
            if (directoryList.SelectedItems.Count == 1 && directoryList.SelectedItems[0].Tag is StorageEntry entry) {
                sourceInput.Text = entry.Path; transferPlan = null; tabs.SelectedIndex = 2;
            }
        }));
        page.Controls.Add(sections); page.Controls.Add(actions); page.Controls.Add(Hint("RootHint")); page.Controls.Add(bar);
    }

    private async Task ScanStorage() {
        string root = rootInput.Text;
        await Run(async token => {
            var progress = Progress();
            storageScan = await Task.Run(() => new StorageScanner().Scan(root, progress, token));
            JsonFiles.Write(Path.Combine(dataRoot, "scans", $"storage-{DateTime.Now:yyyyMMdd-HHmmss}.json"), storageScan);
            ShowDirectory(storageScan.Root);
            largeList.Items.Clear(); overviewIssues.Items.Clear();
            foreach (var entry in storageScan.LargeFiles) Row(largeList, entry, entry.Path, FormatSize(entry.Bytes));
            foreach (var issue in storageScan.Issues) Row(overviewIssues, issue, issue.Path, issue.Reason);
            status.Text = L.T("ScanSummary", storageScan.Files, storageScan.Bytes / 1073741824d, storageScan.Issues.Count);
        });
    }
    private void ShowDirectory(string path) {
        if (storageScan == null || !storageScan.Directories.Any(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        var current = storageScan.Directories.Single(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        directoryList.Tag = path; rootInput.Text = path;
        directoryList.BeginUpdate(); directoryList.Items.Clear();
        foreach (var entry in storageScan.Directories.Where(d =>
            Path.GetDirectoryName(d.Path)?.Equals(path, StringComparison.OrdinalIgnoreCase) == true))
            Row(directoryList, entry, entry.Path, FormatSize(entry.Bytes), entry.Files.ToString("N0"),
                current.Bytes == 0 ? "0%" : $"{100d * entry.Bytes / current.Bytes:N1}%");
        directoryList.EndUpdate();
    }

    private void ReloadEngine() {
        string custom = Path.Combine(dataRoot, "rules.json");
        string settings = Path.Combine(dataRoot, "settings.json");
        engine = new CleanupEngine(JsonFiles.Read<RuleCatalog>(File.Exists(custom) ? custom : builtin),
            File.Exists(settings) ? JsonFiles.Read<CleanerSettings>(settings) : null);
        cleanupPlan = null; excluded.Clear();
    }
    private void BuildRules() {
        var page = Page("Cleanup"); var bar = Flow();
        executeRules = AsyncButton("Execute", ExecuteRules); executeRules.Enabled = false;
        bar.Controls.AddRange([AsyncButton("Scan", ScanRules), executeRules,
            Button("ImportRules", ImportRules), Button("EditRules", EditRules), Button("ReloadRules", () => {
                ReloadEngine(); RenderRules(); status.Text = L.T("RuleLoaded", engine.Catalog.Rules.Count);
            }), Button("SavePlan", SaveCleanupPlan), Button("LoadPlan", LoadCleanupPlan),
            Button("AiPrompt", ShowAiGuide)]);
        var split = new SplitContainer { Dock = DockStyle.Fill, Width = 1360,
            Panel1MinSize = 480, Panel2MinSize = 450, SplitterDistance = 510 };
        ruleList = List(("Rule", 220), ("Action", 70), ("Files", 60), ("Bytes", 100), ("Risk", 100));
        ruleList.CheckBoxes = true; ruleList.MultiSelect = false;
        split.Panel1.Controls.Add(ruleList);
        var right = new Panel { Dock = DockStyle.Fill };
        ruleDescription = new TextBox { Dock = DockStyle.Top, Multiline = true, ReadOnly = true, Height = 72 };
        var details = new TabControl { Dock = DockStyle.Fill };
        candidateList = List(("Path", 650), ("Bytes", 130)); candidateList.CheckBoxes = true;
        ruleIssues = List(("Rule", 150), ("Path", 370), ("State", 520));
        var filePage = new TabPage(L.T("Files")); filePage.Controls.Add(candidateList);
        var navigation = Flow(); navigation.Dock = DockStyle.Bottom;
        filter = new TextBox { Width = 230, PlaceholderText = L.T("Filter") };
        pageLabel = new Label { AutoSize = true, Margin = new Padding(6, 10, 6, 0) };
        navigation.Controls.AddRange([filter, Button("‹", () => { candidatePage = Math.Max(0, candidatePage - 1); ShowCandidates(); }),
            Button("›", () => { candidatePage++; ShowCandidates(); }), pageLabel]);
        filePage.Controls.Add(navigation);
        var issuePage = new TabPage(L.T("Issues")); issuePage.Controls.Add(ruleIssues);
        details.TabPages.AddRange([filePage, issuePage]); right.Controls.Add(details); right.Controls.Add(ruleDescription);
        split.Panel2.Controls.Add(right);
        ruleList.SelectedIndexChanged += (_, _) => { candidatePage = 0; ShowCandidates(); };
        ruleList.ItemChecked += (_, _) => { if (!rendering) UpdateSelected(); };
        candidateList.ItemChecked += (_, e) => {
            if (rendering || e.Item.Tag is not Candidate candidate) return;
            if (e.Item.Checked) excluded.Remove(candidate.Path); else excluded.Add(candidate.Path);
            UpdateSelected();
        };
        filter.TextChanged += (_, _) => { candidatePage = 0; ShowCandidates(); };
        page.Controls.Add(split); page.Controls.Add(bar);
        RenderRules();
    }
    private void RenderRules() {
        rendering = true; ruleList.BeginUpdate(); ruleList.Items.Clear();
        foreach (var rule in engine.Catalog.Rules) {
            var matches = cleanupPlan?.Files.Where(f => f.RuleId == rule.Id).ToArray() ?? [];
            Row(ruleList, rule, L.RuleName(rule), L.T(rule.Action.ToString()), matches.Length.ToString("N0"),
                FormatSize(matches.Sum(f => f.Stamp.Bytes)), L.T(rule.Risk.ToString()));
            ruleList.Items[^1].Checked = matches.Length > 0 && rule.Risk == RiskLevel.Low &&
                CleanupEngine.RunningProcesses(rule).Length == 0;
        }
        ruleList.EndUpdate(); rendering = false;
        ruleIssues.Items.Clear();
        foreach (var issue in cleanupPlan?.Issues ?? []) Row(ruleIssues, issue, issue.RuleId, issue.Path, issue.Reason);
        if (ruleList.Items.Count > 0) ruleList.Items[0].Selected = true;
        ShowCandidates(); UpdateSelected();
    }
    private string[] SelectedRules() => ruleList.CheckedItems.Cast<ListViewItem>()
        .Select(r => ((CleanupRule)r.Tag!).Id).ToArray();
    private List<Candidate> SelectedFiles() {
        string[] selected = SelectedRules();
        return cleanupPlan?.Files.Where(f => selected.Contains(f.RuleId) && !excluded.Contains(f.Path)).ToList() ?? [];
    }
    private void UpdateSelected() {
        executeRules.Enabled = cleanupPlan != null && SelectedFiles().Count > 0;
        if (cleanupPlan == null) return;
        var files = SelectedFiles(); status.Text = L.T("SelectedSummary", files.Count,
            files.Sum(f => f.Stamp.Bytes) / 1073741824d, cleanupPlan.Issues.Count);
    }
    private void ShowCandidates() {
        if (ruleList.SelectedItems.Count == 0) return;
        var rule = (CleanupRule)ruleList.SelectedItems[0].Tag!;
        ruleDescription.Text = L.RuleName(rule) + Environment.NewLine + L.RuleDescription(rule) +
            Environment.NewLine + L.T("KeepAge", rule.MinimumAgeHours, string.Join(", ", rule.Include));
        var files = cleanupPlan?.Files.Where(f => f.RuleId == rule.Id && f.Path.Contains(filter.Text,
            StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        int pages = Math.Max(1, (files.Count + 499) / 500); candidatePage = Math.Clamp(candidatePage, 0, pages - 1);
        rendering = true; candidateList.BeginUpdate(); candidateList.Items.Clear();
        foreach (var candidate in files.Skip(candidatePage * 500).Take(500)) {
            Row(candidateList, candidate, candidate.Path, FormatSize(candidate.Stamp.Bytes));
            candidateList.Items[^1].Checked = !excluded.Contains(candidate.Path);
        }
        candidateList.EndUpdate(); rendering = false; pageLabel.Text = $"{candidatePage + 1}/{pages} · {files.Count:N0}";
    }
    private async Task ScanRules() => await Run(async token => {
        var progress = Progress(); cleanupPlan = await Task.Run(() => engine.Scan(progress: progress, cancellation: token));
        excluded.Clear(); JsonFiles.Write(Path.Combine(dataRoot, "plans", $"rules-{DateTime.Now:yyyyMMdd-HHmmss}.cleanup-plan.json"), cleanupPlan);
        RenderRules();
    });
    private async Task ExecuteRules() {
        if (cleanupPlan == null || SelectedFiles().Count == 0) throw new InvalidOperationException(L.T("ScanFirst"));
        var selected = SelectedFiles(); var paths = selected.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] ids = SelectedRules();
        string[] deletes = ids.Where(id => engine.Catalog.Rules.Single(r => r.Id == id).Action == CleanupAction.Delete).ToArray();
        string[] transfers = ids.Except(deletes).ToArray();
        var transferPlans = transfers.Length == 0 ? [] : engine.CreateTransferPlans(cleanupPlan, transfers, paths);
        string destinations = string.Join(Environment.NewLine, transferPlans.Select(p => $"{p.Source} → {p.Destination}"));
        if (!Confirm(L.T("DeleteConfirm", selected.Count, selected.Sum(f => f.Stamp.Bytes) / 1073741824d) + "\n" + destinations)) return;
        var snapshot = cleanupPlan;
        await Run(async token => {
            var progress = Progress(); var messages = new List<string>();
            foreach (var plan in transferPlans) {
                var result = await Task.Run(() => new TransferService().Execute(plan, Path.Combine(dataRoot, "journals"), progress, token));
                messages.Add(L.T("TransferDone", result.VerifiedFiles, result.RemovedFiles, result.Issues.Count, result.Manifest));
                token.ThrowIfCancellationRequested();
            }
            if (deletes.Length > 0) {
                var result = await Task.Run(() => engine.Apply(snapshot, deletes, Path.Combine(dataRoot, "journals"), paths, progress, token));
                messages.Add(L.T("CleanupDone", result.Deleted, result.Skipped, result.FreeSpaceChange / 1073741824d));
            }
            cleanupPlan = null; RenderRules(); status.Text = string.Join("  ", messages);
        });
    }
    private void ImportRules() {
        using var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var catalog = JsonFiles.Read<RuleCatalog>(dialog.FileName); _ = new CleanupEngine(catalog, engine.Settings);
        JsonFiles.Write(Path.Combine(dataRoot, "rules.json"), catalog); ReloadEngine(); RenderRules(); status.Text = L.T("Imported");
    }
    private void EditRules() {
        Directory.CreateDirectory(dataRoot); string path = Path.Combine(dataRoot, "rules.json");
        if (!File.Exists(path)) File.Copy(builtin, path);
        Process.Start(new ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
    }
    private void SaveCleanupPlan() {
        if (cleanupPlan == null) throw new InvalidOperationException(L.T("ScanFirst"));
        using var dialog = new SaveFileDialog { Filter = "JSON|*.cleanup-plan.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK) JsonFiles.Write(dialog.FileName, cleanupPlan with { Files = SelectedFiles() });
    }
    private void LoadCleanupPlan() {
        using var dialog = new OpenFileDialog { Filter = "JSON|*.cleanup-plan.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK) { cleanupPlan = JsonFiles.Read<CleanupPlan>(dialog.FileName); excluded.Clear(); RenderRules(); }
    }
    private void ShowAiGuide() {
        string path = Path.Combine(AppContext.BaseDirectory, "docs", "ai-rules.md");
        using var dialog = new Form { Text = L.T("AiPrompt"), Width = 850, Height = 700,
            StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Both, Text = File.Exists(path) ? File.ReadAllText(path) : L.T("RulesGuide") });
        dialog.ShowDialog(this);
    }

    private void BuildTransfers() {
        var page = Page("Transfer"); var controls = new TableLayoutPanel { Dock = DockStyle.Top,
            AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
        controls.ColumnStyles.Add(new(SizeType.Absolute, 180)); controls.ColumnStyles.Add(new(SizeType.Percent, 100));
        controls.ColumnStyles.Add(new(SizeType.AutoSize));
        sourceInput = new TextBox { Dock = DockStyle.Fill }; destinationInput = new TextBox { Dock = DockStyle.Fill };
        controls.Controls.Add(new Label { Text = L.T("Source"), AutoSize = true }, 0, 0); controls.Controls.Add(sourceInput, 1, 0);
        controls.Controls.Add(Button("Browse", () => { string p = Folder(sourceInput.Text); if (p != "") sourceInput.Text = p; }), 2, 0);
        controls.Controls.Add(new Label { Text = L.T("Destination"), AutoSize = true }, 0, 1); controls.Controls.Add(destinationInput, 1, 1);
        controls.Controls.Add(Button("Browse", () => { string p = Folder(preferences.BackupRoot); if (p != "")
            destinationInput.Text = Path.Combine(p, Path.GetFileName(sourceInput.Text.TrimEnd('\\')) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); }), 2, 1);
        transferMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        transferMode.Items.AddRange([L.T("Copy"), L.T("Move")]); transferMode.SelectedIndex = 0;
        executeTransfer = AsyncButton("Execute", ExecuteTransfer); executeTransfer.Enabled = false;
        var bar = Flow(); bar.Controls.AddRange([transferMode, AsyncButton("Plan", PreviewTransfer), executeTransfer]);
        controls.Controls.Add(bar, 1, 2);
        transferList = List(("RelativePath", 930), ("Bytes", 170));
        sourceInput.TextChanged += (_, _) => { transferPlan = null; executeTransfer.Enabled = false; };
        destinationInput.TextChanged += (_, _) => { transferPlan = null; executeTransfer.Enabled = false; };
        transferMode.SelectedIndexChanged += (_, _) => { transferPlan = null; executeTransfer.Enabled = false; };
        page.Controls.Add(transferList); page.Controls.Add(Hint("TransferHint")); page.Controls.Add(controls);
    }
    private async Task PreviewTransfer() {
        string source = sourceInput.Text, destination = destinationInput.Text;
        if (source == "" || destination == "") throw new InvalidOperationException(L.T("NoRoot"));
        var mode = transferMode.SelectedIndex == 0 ? TransferMode.Copy : TransferMode.Move;
        await Run(async token => {
            transferPlan = await Task.Run(() => new TransferService().Plan(source, destination, mode, cancellation: token));
            JsonFiles.Write(Path.Combine(dataRoot, "plans", $"transfer-{transferPlan.Id:N}.json"), transferPlan);
            transferList.BeginUpdate(); transferList.Items.Clear();
            foreach (var file in transferPlan.Files.Take(10000)) Row(transferList, file, file.RelativePath, FormatSize(file.Stamp.Bytes));
            transferList.EndUpdate(); executeTransfer.Enabled = true; status.Text = L.T("PlanReady", transferPlan.Files.Count, transferPlan.Files.Sum(f => f.Stamp.Bytes) / 1073741824d);
        });
    }
    private async Task ExecuteTransfer() {
        var snapshot = transferPlan ?? throw new InvalidOperationException(L.T("ScanFirst"));
        if (!Confirm(L.T("TransferConfirm", L.T(snapshot.Mode.ToString()), snapshot.Source, snapshot.Destination,
            snapshot.Files.Count, snapshot.Files.Sum(f => f.Stamp.Bytes) / 1073741824d))) return;
        await Run(async token => {
            var progress = Progress(); var result = await Task.Run(() => new TransferService().Execute(snapshot,
                Path.Combine(dataRoot, "journals"), progress, token));
            transferPlan = null; executeTransfer.Enabled = false; status.Text = L.T("TransferDone", result.VerifiedFiles, result.RemovedFiles,
                result.Issues.Count, result.Manifest);
        });
    }

    private void BuildChats() {
        var page = Page("Chats"); var bar = Flow();
        bar.Controls.AddRange([AsyncButton("Discover", () => DiscoverChats(null)), AsyncButton("AddFolder", async () => {
            string path = Folder(); if (path != "") await DiscoverChats(path);
        }), AsyncButton("CaptureKey", CapturePassive), AsyncButton("ActiveKey", CaptureActive)]);
        var settings = Flow();
        backupInput = new TextBox { Width = 540, Text = preferences.BackupRoot, PlaceholderText = L.T("BackupRoot") };
        keyInput = new TextBox { Width = 340, UseSystemPasswordChar = true, PlaceholderText = L.T("Key") };
        settings.Controls.AddRange([backupInput, Button("Browse", () => {
            string path = Folder(backupInput.Text); if (path == "") return;
            backupInput.Text = path; preferences = preferences with { BackupRoot = path };
            JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences);
        }), keyInput, AsyncButton("Backup", BackupChat), AsyncButton("ExportChat", ExportChat)]);
        chatList = List(("Platform", 120), ("Account", 170), ("Source", 600), ("Bytes", 140)); chatList.MultiSelect = false;
        page.Controls.Add(chatList); page.Controls.Add(Hint("ExportNotice")); page.Controls.Add(settings);
        page.Controls.Add(Hint("ChatHint")); page.Controls.Add(bar);
        foreach (var location in locations.GroupBy(l => (l.Platform, l.Directory)).Select(g => g.First()))
            Row(chatList, location, location.Platform.ToString(), location.Account, location.Directory, FormatSize(location.DatabaseBytes));
    }
    private ChatLocation SelectedChat() => chatList.SelectedItems.Count == 1 ?
        (ChatLocation)chatList.SelectedItems[0].Tag! : throw new InvalidOperationException(L.T("SelectAccount"));
    private async Task DiscoverChats(string? root) => await Run(async _ => {
        locations = await Task.Run(() => ChatDiscovery.Discover(root == null ? null : [root])); chatList.Items.Clear();
        foreach (var location in locations.GroupBy(l => (l.Platform, l.Directory)).Select(g => g.First()))
            Row(chatList, location, location.Platform.ToString(), location.Account, location.Directory, FormatSize(location.DatabaseBytes));
    });
    private async Task CapturePassive() {
        var location = SelectedChat();
        await Run(async token => {
            byte[] key = await Task.Run(() => PassiveKeyReader.Read(location, cancellation: token)); SetKey(key);
        });
    }
    private async Task CaptureActive() {
        var location = SelectedChat();
        if (!Confirm(L.T("ActiveKeyConfirm"))) return;
        using var dialog = new OpenFileDialog { Filter = "Weixin.exe|Weixin.exe" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(async token => { byte[] key = await CaptureWorker.Capture(location, dialog.FileName, token); SetKey(key); });
    }
    private void SetKey(byte[] key) {
        if (capturedKey != null) CryptographicOperations.ZeroMemory(capturedKey);
        capturedKey = key; keyInput.Clear(); status.Text = L.T("KeyReady");
    }
    private string ChatTarget(ChatLocation location, string suffix) {
        if (string.IsNullOrWhiteSpace(backupInput.Text)) throw new InvalidOperationException(L.T("NoRoot"));
        return Path.Combine(backupInput.Text, location.Platform.ToString(), location.Account,
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + suffix);
    }
    private async Task BackupChat() {
        var location = SelectedChat(); string target = ChatTarget(location, "raw");
        if (!Confirm(L.T("ChatBackupConfirm", target))) return;
        await Run(async token => {
            var progress = Progress(); var result = await Task.Run(() => {
                var service = new TransferService(); var plan = service.Plan(location.Directory, target,
                    processes: location.Platform == ChatPlatform.QQ ? ["QQ"] : ["WeChat", "Weixin"], cancellation: token);
                return service.Execute(plan, Path.Combine(dataRoot, "journals"), progress, token);
            });
            status.Text = L.T("TransferDone", result.VerifiedFiles, result.RemovedFiles, result.Issues.Count, result.Manifest);
            if (!result.Cancelled && result.Issues.Count == 0) verifiedBackups[location.Directory] = result.Manifest;
        });
    }
    private async Task ExportChat() {
        var location = SelectedChat(); string target = ChatTarget(location, "readable");
        byte[]? key = capturedKey?.ToArray();
        if (!string.IsNullOrWhiteSpace(keyInput.Text)) key = location.Platform == ChatPlatform.QQ ?
            Encoding.ASCII.GetBytes(keyInput.Text) : Convert.FromHexString(keyInput.Text.Trim());
        keyInput.Clear();
        try {
            await Run(async token => {
                var progress = Progress(); var result = await Task.Run(() =>
                    verifiedBackups.TryGetValue(location.Directory, out string? manifest) ?
                    new ChatExporter().ExportVerifiedBackup(manifest, target, location.Platform, key, progress, token) :
                    new ChatExporter().Export(location.Directory, target, location.Platform, key, progress, token));
                status.Text = L.T("ExportDone", result.Messages, result.Databases,
                    L.T(result.Complete ? "Complete" : "Incomplete"), Path.Combine(result.Directory, "export-report.json"));
            });
        } finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
    }
}
