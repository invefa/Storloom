using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Input;
using Storloom.Core;

namespace Storloom.App;

public sealed partial class ShellWindow : ThemeWindow {
    private readonly string dataRoot;
    private readonly string rulesDirectory = Path.Combine(AppContext.BaseDirectory, "rules");
    private AppPreferences preferences = new();
    private CleanupEngine engine = null!;
    private CleanupPlan? cleanupPlan;
    private CancellationTokenSource? operation;
    private byte[]? capturedKey;
    private readonly Dictionary<string, Grid> pages = [];
    private readonly Dictionary<string, string> verifiedBackups = new(StringComparer.OrdinalIgnoreCase);
    private Grid pageHost = null!;
    private ListBox navigation = null!;
    private TextBlock status = null!;
    private Menu menu = null!;
    private Button cancel = null!;
    private RowDefinition logRow = null!;
    private ColumnDefinition sidebarColumn = null!;
    private OperationPanel operationView = null!;
    private string currentPage = "cleanup";
    public ShellWindow(string? dataDirectory = null) : base("Storloom", 1320, 860) {
        MinWidth = 1080; MinHeight = 720; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dataRoot = Path.GetFullPath(dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data"));
        string prefs = Path.Combine(dataRoot, "preferences.json");
        if (File.Exists(prefs)) preferences = JsonFiles.Read<AppPreferences>(prefs);
        string backups = Path.Combine(dataRoot, "chat-backups.json");
        if (File.Exists(backups)) foreach (var pair in JsonFiles.Read<Dictionary<string, string>>(backups)) verifiedBackups[pair.Key] = pair.Value;
        L.SetLanguage(preferences.Language); Theme.Apply(preferences); ReloadEngine(); BuildUi();
        Closing += (_, e) => {
            if (operation != null) { operation.Cancel(); e.Cancel = true; status.Text = L.T("Busy"); }
            else {
                SetCapturedKey(null);
                preferences = preferences with { SidebarWidth = sidebarColumn.ActualWidth,
                    LogHeight = operationView.Expanded && logRow.ActualHeight > 80 ? logRow.ActualHeight : preferences.LogHeight };
                JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences);
            }
        };
        InputBindings.Add(new KeyBinding(new ActionCommand(() => operationView.ToggleFollowing()), new KeyGesture(Key.L, ModifierKeys.Control | ModifierKeys.Shift)));
        InputBindings.Add(new KeyBinding(new ActionCommand(() => ShowHelp()), new KeyGesture(Key.F1)));
    }
    private void BuildUi() {
        Body.Children.Clear(); pages.Clear();
        var root = View.Grid(View.Auto, View.Fill, View.Auto, new GridLength(0), View.Auto);
        menu = new Menu { Padding = new Thickness(12, 0, 0, 0) };
        var file = new MenuItem { Header = L.T("FileMenu") };
        void Item(MenuItem parent, string key, Action action) { var item = new MenuItem { Header = L.T(key) }; item.Click += (_, _) => Guard(action); parent.Items.Add(item); }
        Item(file, "ImportRules", ImportRules); Item(file, "ReloadRules", () => { ReloadEngine(); RenderRules(); });
        file.Items.Add(new Separator()); Item(file, "SavePlan", SaveCleanupPlan); Item(file, "LoadPlan", LoadCleanupPlan);
        Item(file, "OpenChatBackup", OpenChatBackup); Item(file, "OpenReaderArchive", OpenReaderArchive);
        file.Items.Add(new Separator()); Item(file, "OpenData", () => OpenFolder(dataRoot)); Item(file, "Exit", Close);
        var tools = new MenuItem { Header = L.T("ToolsMenu") }; Item(tools, "Preferences", () => ShowPreferences());
        var help = new MenuItem { Header = L.T("HelpMenu") }; Item(help, "QuickStart", () => ShowHelp());
        Item(help, "OpenAiGuide", () => ShowHelp(document: File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", L.Language == "zh-CN" ? "ai-rules.zh-CN.md" : "ai-rules.md"))));
        Item(help, "About", () => new AboutWindow { Owner = this }.ShowDialog());
        menu.Items.Add(file); menu.Items.Add(tools); menu.Items.Add(help); View.Add(root, menu, 0);
        var center = new Grid(); sidebarColumn = new ColumnDefinition { Width = new GridLength(Math.Clamp(preferences.SidebarWidth, 150, 260)), MinWidth = 150, MaxWidth = 260 };
        center.ColumnDefinitions.Add(sidebarColumn); center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = View.Fill });
        navigation = new ListBox { Name = "navigation", HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var (id, key) in new[] { ("overview", "NavOverview"), ("cleanup", "NavCleanup"), ("chats", "NavChats") })
            navigation.Items.Add(new ListBoxItem { Content = L.T(key), Tag = id });
        navigation.SelectionChanged += (_, _) => { if (navigation.SelectedItem is ListBoxItem selected) Navigate((string)selected.Tag); };
        View.Add(center, navigation, 0); View.Add(center, new GridSplitter { Width = 1, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext }, 0, 1);
        pageHost = new Grid { Margin = new Thickness(24, 20, 24, 18) }; View.Add(center, pageHost, 0, 2); View.Add(root, center, 1);
        var logSplitter = new GridSplitter { Height = 5, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Visibility = Visibility.Collapsed };
        logSplitter.DragStarted += (_, _) => { logRow.BeginAnimation(RowDefinition.HeightProperty, null); };
        logSplitter.DragCompleted += (_, _) => { preferences = preferences with { LogHeight = logRow.ActualHeight }; };
        View.Add(root, logSplitter, 2); logRow = root.RowDefinitions[3];
        operationView = new OperationPanel { Visibility = Visibility.Collapsed }; View.Add(root, operationView, 3);
        operationView.ExpansionChanged += expanded => {
            double target = expanded ? Math.Clamp(preferences.LogHeight, 130, Math.Max(130, ActualHeight * .55)) : 64;
            if (!expanded && logRow.ActualHeight > 80) preferences = preferences with { LogHeight = logRow.ActualHeight };
            AnimateLog(target); logSplitter.IsEnabled = expanded;
        };
        logSplitter.IsVisibleChanged += (_, _) => { if (logSplitter.IsVisible) logRow.MinHeight = 64; };
        var footer = new DockPanel { Margin = new Thickness(18, 5, 12, 5) };
        cancel = Button("Cancel", () => operation?.Cancel()); cancel.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(cancel);
        status = View.Text(L.T("CatalogSummary", engine.Catalog.Rules.Count), color: "Muted"); status.Margin = new Thickness(0);
        status.TextWrapping = TextWrapping.NoWrap; status.TextTrimming = TextTrimming.CharacterEllipsis; footer.Children.Add(status); View.Add(root, footer, 4);
        Body.Children.Add(root); BuildOverview(); BuildRules(); BuildChats();
        navigation.SelectedIndex = currentPage == "overview" ? 0 : currentPage == "chats" ? 2 : 1;
    }
    private void AnimateLog(double target) {
        double previous = logRow.ActualHeight; logRow.BeginAnimation(RowDefinition.HeightProperty, null); logRow.Height = new GridLength(target);
        if (!Theme.ReduceMotion && SystemParameters.ClientAreaAnimation) logRow.BeginAnimation(RowDefinition.HeightProperty,
            new GridLengthTransition(previous, target) { Duration = TimeSpan.FromMilliseconds(150), FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop }, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }
    public void Navigate(string id) {
        if (!pages.ContainsKey(id)) return; currentPage = id;
        foreach (var pair in pages) pair.Value.Visibility = pair.Key == id ? Visibility.Visible : Visibility.Collapsed;
        var item = navigation.Items.Cast<ListBoxItem>().Single(i => (string)i.Tag == id);
        if (navigation.SelectedItem != item) navigation.SelectedItem = item;
        Theme.Enter(pages[id]);
    }
    private Grid Page(string id, string title, string lead, params GridLength[] rows) {
        var grid = View.Grid([View.Auto, .. rows]);
        var header = View.Stack(View.Text(L.T(title), 24), View.Text(L.T(lead), color: "Muted"));
        header.Margin = new Thickness(0, 0, 0, 12); View.Add(grid, header, 0); pages[id] = grid; pageHost.Children.Add(grid); return grid;
    }
    private Button Button(string key, Action action, bool primary = false) => View.Button(key, () => Guard(action), primary);
    private Button AsyncButton(string key, Func<Task> action, bool primary = false) {
        var button = View.Button(key, () => { }, primary);
        button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Error(ex); } }; return button;
    }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
    private void Error(Exception ex) {
        status.Text = ex.Message; operationView.AddLog(ex.Message);
        try { JsonFiles.Write(Path.Combine(dataRoot, "errors", $"error-{Guid.NewGuid():N}.json"), new { time = DateTimeOffset.UtcNow, error = ex.ToString() }); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        Notice.Show(this, L.T("Error"), ex.Message);
    }
    private bool Confirm(string message) => Notice.Show(this, L.T("Confirm"), message, true);
    private IProgress<ScanProgress> Progress() => new ProgressRelay(Dispatcher, value => { if (operation != null) operationView.Report(value); });
    private async Task Run(Func<CancellationToken, Task> work) {
        if (operation != null) return;
        using var source = new CancellationTokenSource(); operation = source;
        pageHost.IsEnabled = navigation.IsEnabled = menu.IsEnabled = false;
        operationView.Visibility = Visibility.Visible; ((GridSplitter)((Grid)Body.Children[0]).Children[2]).Visibility = Visibility.Visible;
        AnimateLog(operationView.Expanded ? Math.Clamp(preferences.LogHeight, 130, ActualHeight * .55) : 64);
        operationView.Start(); cancel.Visibility = Visibility.Visible; status.Text = L.T("OperationStarting"); bool complete = false;
        try { await work(source.Token); complete = !source.IsCancellationRequested; }
        catch (OperationCanceledException) { status.Text = L.T("Cancelled"); }
        catch (Exception ex) { Error(ex); }
        finally { operation = null; pageHost.IsEnabled = navigation.IsEnabled = menu.IsEnabled = true;
            cancel.Visibility = Visibility.Collapsed; operationView.Finish(status.Text, complete); }
    }
    private void ReloadEngine() {
        string settings = Path.Combine(dataRoot, "settings.json");
        engine = new CleanupEngine(RuleSets.Load(rulesDirectory, preferences.EnabledRuleSets, Path.Combine(dataRoot, "rules.json")),
            File.Exists(settings) ? JsonFiles.Read<CleanerSettings>(settings) : new()); cleanupPlan = null; excluded.Clear();
    }
    private void ShowPreferences(bool ruleSets = false) {
        var dialog = new PreferencesWindow(preferences, rulesDirectory, dataRoot, engine.Settings, ruleSets) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        preferences = dialog.Result; JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences);
        SetCapturedKey(null); L.SetLanguage(preferences.Language); Theme.Apply(preferences); ReloadEngine(); BuildUi();
    }
    private void ShowHelp(int? topic = null, string? document = null) => new HelpWindow(topic ?? (currentPage == "chats" ? 2 : currentPage == "overview" ? 5 : 0), document) { Owner = this }.ShowDialog();
    private void ImportRules() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" }; if (dialog.ShowDialog(this) != true) return;
        var catalog = JsonFiles.Read<RuleCatalog>(dialog.FileName); _ = new CleanupEngine(catalog, engine.Settings);
        string path = Path.Combine(dataRoot, "rules.json");
        var merged = File.Exists(path) ? JsonFiles.Read<RuleCatalog>(path).Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase) : [];
        foreach (var rule in catalog.Rules) merged[rule.Id] = rule;
        JsonFiles.Write(path, new RuleCatalog { Rules = merged.Values.ToList() }); ReloadEngine(); RenderRules(); Navigate("cleanup");
    }
    private void SaveCleanupPlan() {
        if (cleanupPlan == null) throw new InvalidOperationException(L.T("ScanFirst"));
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON|*.cleanup-plan.json" };
        if (dialog.ShowDialog(this) == true) JsonFiles.Write(dialog.FileName, cleanupPlan with {
            Files = SelectedFiles(), Commands = cleanupPlan.Commands.Where(c => SelectedRules().Contains(c.RuleId)).ToList() });
    }
    private void LoadCleanupPlan() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.cleanup-plan.json" }; if (dialog.ShowDialog(this) != true) return;
        cleanupPlan = JsonFiles.Read<CleanupPlan>(dialog.FileName); excluded.Clear(); RenderRules(); Navigate("cleanup");
    }
    private void OpenReaderArchive() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Storloom HTML|messages.html" };
        if (dialog.ShowDialog(this) == true) new ChatReaderWindow(Path.GetDirectoryName(dialog.FileName)!, dataRoot, preferences.ReduceMotion) { Owner = this }.ShowDialog();
    }
    private void SetCapturedKey(byte[]? key) { if (capturedKey != null) CryptographicOperations.ZeroMemory(capturedKey); capturedKey = key; }
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); OpenFile(path); }
    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}

internal sealed class ProgressRelay(System.Windows.Threading.Dispatcher dispatcher, Action<ScanProgress> receive) : IProgress<ScanProgress> {
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private ScanProgress? latest;
    private bool pending;
    public void Report(ScanProgress value) {
        lock (gate) {
            bool newPhase = latest?.RuleId != value.RuleId; latest = value;
            if (pending || !newPhase && clock.ElapsedMilliseconds < 120 && value.TotalBytes != value.Bytes && value.TotalFiles != value.Files) return;
            pending = true; clock.Restart();
        }
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => {
            ScanProgress? next; lock (gate) { next = latest; pending = false; }
            if (next != null) receive(next);
        }));
    }
}

internal sealed class GridLengthTransition(double from, double to) : System.Windows.Media.Animation.AnimationTimeline {
    public override Type TargetPropertyType => typeof(GridLength);
    protected override Freezable CreateInstanceCore() => new GridLengthTransition(from, to);
    public GridLengthTransition() : this(0, 0) { }
    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, System.Windows.Media.Animation.AnimationClock animationClock) {
        double fraction = animationClock.CurrentProgress ?? 1;
        return new GridLength(Math.Max(0, from + (to - from) * (1 - Math.Pow(1 - fraction, 3))));
    }
    public new Duration Duration { get => base.Duration; set => base.Duration = value; }
}
