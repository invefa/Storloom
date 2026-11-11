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
    private OperationPause? operationPause;
    private volatile bool debugEnabled;
    private bool operationIncomplete;
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
    private GridSplitter logDivider = null!;
    private GridSplitter sidebarDivider = null!;
    private Button sidebarToggle = null!;
    private Button? operationButton;
    private DockPanel statusFooter = null!;
    private bool sidebarAnimating;
    private int sidebarTransition;
    private string currentPage = "cleanup";
    public ShellWindow(string? dataDirectory = null) : base("Storloom", 1180, 760) {
        MinWidth = 640; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dataRoot = Path.GetFullPath(dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data"));
        PickerHistory.Attach(this,dataRoot);
        string prefs = Path.Combine(dataRoot, "preferences.json");
        if (File.Exists(prefs)) preferences = JsonFiles.Read<AppPreferences>(prefs);
        string backups = Path.Combine(dataRoot, "chat-backups.json");
        if (File.Exists(backups)) foreach (var pair in JsonFiles.Read<Dictionary<string, string>>(backups)) verifiedBackups[pair.Key] = pair.Value;
        L.SetLanguage(preferences.Language); Theme.Apply(preferences); ReloadEngine(); BuildUi();
        WindowGeometry.Attach(this,dataRoot,"main",640,480);
        Closing += HandleClosing; Closed += (_, _) => tray?.Dispose();
        if (preferences.CloseToTray) EnsureTray();
        InputBindings.Add(new KeyBinding(new ActionCommand(() => operationView.ToggleFollowing()), new KeyGesture(Key.L, ModifierKeys.Control | ModifierKeys.Shift)));
        InputBindings.Add(new KeyBinding(new ActionCommand(() => ShowHelp()), new KeyGesture(Key.F1)));
    }
    private void BuildUi() {
        Body.Children.Clear(); pages.Clear();
        var root = View.Grid(View.Auto, View.Fill, View.Auto, new GridLength(32), View.Auto);
        menu = new Menu { Padding = new Thickness(0), Margin = new Thickness(16, 0, 0, 0) };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(menu, true);
        var file = new MenuItem { Header = L.T("FileMenu") };
        void Item(MenuItem parent, string key, Action action) { var item = new MenuItem { Header = L.T(key) }; item.Click += (_, _) => Guard(action); parent.Items.Add(item); }
        Item(file, "ImportRules", ImportRules); Item(file, "ReloadRules", () => { ReloadEngine(); RenderRules(); });
        file.Items.Add(new Separator()); Item(file, "SavePlan", SaveCleanupPlan); Item(file, "LoadPlan", LoadCleanupPlan);
        Item(file, "OpenChatBackup", OpenChatBackup); Item(file, "OpenReaderArchive", OpenReaderArchive); Item(file,"VaultManage",EncryptReaderArchive);
        Item(file, "ExportLog", ExportOperationLog);
        file.Items.Add(new Separator()); Item(file, "OpenData", () => OpenFolder(dataRoot)); Item(file, "HideToTray", HideToTray); Item(file, "Exit", RequestExit);
        var tools = new MenuItem { Header = L.T("ToolsMenu") }; Item(tools, "Preferences", () => ShowPreferences());
        var help = new MenuItem { Header = L.T("HelpMenu") }; Item(help, "QuickStart", () => ShowHelp());
        Item(help, "OpenAiGuide", () => ShowHelp(document: File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", L.Language == "zh-CN" ? "ai-rules.zh-CN.md" : "ai-rules.md"))));
        Item(help, "About", () => new AboutWindow { Owner = this }.ShowDialog());
        menu.Items.Add(file); menu.Items.Add(tools); menu.Items.Add(help);
        var title = View.Text("Storloom", 12); title.Margin = new Thickness(0);
        Caption.Header = View.Row(title, menu);
        var center = new Grid(); sidebarColumn = new ColumnDefinition { Width = new GridLength(Math.Clamp(preferences.SidebarWidth, 128, 260)), MinWidth = 128, MaxWidth = 260 };
        center.ColumnDefinitions.Add(sidebarColumn); center.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        center.ColumnDefinitions.Add(new ColumnDefinition { Width = View.Fill });
        navigation = new ListBox { Name = "navigation", HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 0, 4, 4) };
        foreach (var (id, key, geometry) in new[] {
            ("overview", "NavOverview", "M 1,14 L 1,9 4,9 4,14 Z M 7,14 L 7,5 10,5 10,14 Z M 13,14 L 13,1 16,1 16,14 Z"),
            ("cleanup", "NavCleanup", "M 2,5 L 14,5 14,14 2,14 Z M 1,2 L 15,2 15,5 1,5 Z M 6,8 L 10,8"),
            ("chats", "NavChats", "M 1,1 L 15,1 15,11 8,11 4,15 4,11 1,11 Z M 4,5 L 12,5 M 4,8 L 9,8") }) {
            var label = View.Text(L.T(key)); label.Name = "nav_label"; label.Margin = new Thickness(0); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
            var contents = new DockPanel { Height = 20, VerticalAlignment = VerticalAlignment.Center }; var icon = View.Icon(geometry); icon.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(icon, Dock.Left); contents.Children.Add(icon); contents.Children.Add(label);
            navigation.Items.Add(new ListBoxItem { Content = contents, Tag = id, ToolTip = L.T(key) });
        }
        navigation.SelectionChanged += (_, _) => { if (navigation.SelectedItem is ListBoxItem selected) Navigate((string)selected.Tag); };
        var sidebar = View.Grid(View.Auto, View.Fill); sidebar.ClipToBounds = true; sidebar.SetResourceReference(Panel.BackgroundProperty, "Sidebar");
        var collapseSidebar = sidebarToggle = View.IconButton("M 1,3 L 15,3 M 1,8 L 15,8 M 1,13 L 15,13", "ToggleSidebar", ToggleSidebar);
        collapseSidebar.Name = "toggle_sidebar"; collapseSidebar.HorizontalAlignment = HorizontalAlignment.Left; collapseSidebar.Margin = new Thickness(8, 4, 0, 2);
        View.Add(sidebar, collapseSidebar, 0); View.Add(sidebar, navigation, 1);
        View.Add(center, sidebar, 0); sidebarDivider = SidebarSplitter("Canvas", HorizontalAlignment.Left); View.Add(center, sidebarDivider, 0, 1);
        ApplySidebar();
        pageHost = new Grid { Margin = new Thickness(12, 14, 18, 12) }; View.Add(center, pageHost, 0, 2); View.Add(root, center, 1);
        var logSplitter = logDivider = new GridSplitter { Height = 1, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Visibility = Visibility.Visible };
        var logTemplate = new ControlTemplate(typeof(GridSplitter)); var logHit = new FrameworkElementFactory(typeof(Grid));
        logHit.SetResourceReference(Panel.BackgroundProperty, "Canvas"); var logLine = new FrameworkElementFactory(typeof(Border));
        logLine.SetValue(FrameworkElement.HeightProperty, 1d); logLine.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        logLine.SetResourceReference(Border.BackgroundProperty, "Line"); logHit.AppendChild(logLine); logTemplate.VisualTree = logHit; logSplitter.Template = logTemplate;
        logSplitter.DragStarted += (_, _) => { logRow.BeginAnimation(RowDefinition.HeightProperty, null); };
        logSplitter.DragCompleted += (_, _) => { preferences = preferences with { LogHeight = logRow.ActualHeight }; };
        View.Add(root, logSplitter, 2); logRow = root.RowDefinitions[3];
        root.RowDefinitions[1].MinHeight = 320;
        double contentFloor=260;
        void ClampLog() {
            // Larger controls can raise the content grid's measured minimum. Reserve that
            // minimum before allocating the log, so expanding it cannot leave the viewport.
            if(operationView!=null&&operationView.IsVisible&&operationView.TranslatePoint(new Point(0,operationView.ActualHeight),Body).Y>Body.ActualHeight+.5)
                contentFloor=Math.Max(contentFloor,center.ActualHeight);
            logRow.MaxHeight = Math.Max(32, Math.Min(Body.ActualHeight,ActualHeight-Caption.ActualHeight) - contentFloor - root.RowDefinitions[0].ActualHeight - root.RowDefinitions[2].ActualHeight - root.RowDefinitions[4].ActualHeight);
            if (logRow.Height.IsAbsolute && logRow.Height.Value > logRow.MaxHeight) {
                logRow.BeginAnimation(RowDefinition.HeightProperty, null); logRow.Height = new GridLength(logRow.MaxHeight);
            }
        }
        root.LayoutUpdated += (_, _) => ClampLog();
        operationView = new OperationPanel(); View.Add(root, operationView, 3);
        debugEnabled=preferences.DebugLogging;operationView.DebugToggle.IsChecked = preferences.DebugLogging;
        operationView.DebugLoggingChanged += enabled => Guard(() => { debugEnabled=enabled;preferences = preferences with { DebugLogging = enabled }; JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences); });
        operationView.PauseRequested += () => {
            if(operationPause==null)return;
            if(operationPause.IsPaused){operationPause.Resume();operationView.PauseButton.Content=View.Icon("M 4,2 L 4,14 M 10,2 L 10,14",12);operationView.PauseButton.ToolTip=L.T("PauseOperation");}
            else{operationPause.Pause();operationView.PauseButton.Content=View.Icon("M 4,2 L 13,8 4,14 Z",12);operationView.PauseButton.ToolTip=L.T("ResumeOperation");}
            operationView.SetPaused(operationPause.IsPaused);
            operationView.AddLog(L.T(operationPause.IsPaused?"OperationPauseHelp":"OperationResumed"));
        };
        operationView.CancelRequested += ()=>operation?.Cancel();
        operationView.ExportRequested += () => Guard(ExportOperationLog);
        operationView.ExpansionChanged += expanded => {
            double target = expanded ? Math.Clamp(preferences.LogHeight, 80, Math.Max(80, ActualHeight * .55)) : 32;
            if (!expanded && logRow.ActualHeight > 80) preferences = preferences with { LogHeight = logRow.ActualHeight };
            AnimateLog(target);logSplitter.IsEnabled=expanded;logSplitter.Height=expanded?3:1;
            preferences=preferences with{LogExpanded=expanded};JsonFiles.Write(Path.Combine(dataRoot,"preferences.json"),preferences);
        };
        logSplitter.IsVisibleChanged += (_, _) => { if (logSplitter.IsVisible) logRow.MinHeight = 32; };
        // The persistent log/status panel is the sole status surface, including at startup.
        statusFooter=new DockPanel{Visibility=Visibility.Collapsed};cancel=Button("Cancel",()=>operation?.Cancel());cancel.Visibility=Visibility.Collapsed;
        status=operationView.StatusText;status.Text=L.T("CatalogSummary",engine.Catalog.Rules.Count);
        operationView.WrapToggle.IsChecked=preferences.LogWrap;operationView.AutoShowToggle.IsChecked=preferences.LogAutoShow;
        operationView.OptionsChanged+=()=>{preferences=preferences with{LogWrap=operationView.WrapToggle.IsChecked==true,LogAutoShow=operationView.AutoShowToggle.IsChecked==true};JsonFiles.Write(Path.Combine(dataRoot,"preferences.json"),preferences);};
        if(preferences.LogExpanded)operationView.ToggleExpanded();
        Body.Children.Add(root); BuildOverview(); BuildRules(); BuildChats();
        navigation.SelectedIndex = currentPage == "overview" ? 0 : currentPage == "chats" ? 2 : 1;
    }
    private static GridSplitter SidebarSplitter(string background = "Sidebar", HorizontalAlignment lineAlignment = HorizontalAlignment.Right) {
        var splitter = new GridSplitter { Name = "sidebar_splitter", Width = 10, Background = Brushes.Transparent, Cursor = Cursors.SizeWE,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        var template = new ControlTemplate(typeof(GridSplitter));
        var hitArea = new FrameworkElementFactory(typeof(Grid)); hitArea.SetResourceReference(Panel.BackgroundProperty, background);
        var line = new FrameworkElementFactory(typeof(Border)); line.SetValue(FrameworkElement.WidthProperty, 1d);
        line.SetValue(FrameworkElement.HorizontalAlignmentProperty, lineAlignment);
        line.SetResourceReference(Border.BackgroundProperty, "Line"); hitArea.AppendChild(line); template.VisualTree = hitArea;
        splitter.Template = template; return splitter;
    }
    private void ToggleSidebar() {
        preferences = preferences with { SidebarWidth = preferences.SidebarCollapsed || sidebarAnimating ? preferences.SidebarWidth : sidebarColumn.ActualWidth,
            SidebarCollapsed = !preferences.SidebarCollapsed }; ApplySidebar(true);
    }
    private void ApplySidebar(bool animate = false) {
        bool compact = preferences.SidebarCollapsed;
        double previous = sidebarColumn.ActualWidth, target = compact ? 44 : Math.Clamp(preferences.SidebarWidth, 128, 260);
        int transition = ++sidebarTransition; sidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
        sidebarAnimating = animate && !Theme.ReduceMotion && SystemParameters.ClientAreaAnimation;
        sidebarColumn.MinWidth = sidebarAnimating ? 44 : compact ? 44 : 128;
        sidebarColumn.MaxWidth = sidebarAnimating ? 260 : compact ? 44 : 260;
        sidebarColumn.Width = new GridLength(target); sidebarDivider.IsEnabled = !compact && !sidebarAnimating;
        UpdateSidebarContents(compact, sidebarAnimating);
        if (!sidebarAnimating) return;
        var animation = new GridLengthTransition(previous, target) { Duration = TimeSpan.FromMilliseconds(150), FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
        animation.Completed += (_, _) => {
            if (transition != sidebarTransition) return;
            sidebarAnimating = false; sidebarColumn.MinWidth = compact ? 44 : 128; sidebarColumn.MaxWidth = compact ? 44 : 260;
            sidebarDivider.IsEnabled = !compact; UpdateSidebarContents(compact, false);
        };
        sidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, animation);
    }
    private void UpdateSidebarContents(bool compact, bool moving) {
        sidebarToggle.HorizontalAlignment = compact && !moving ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        sidebarToggle.Margin = new Thickness(compact && !moving ? 0 : 8, 4, 0, 2);
        foreach (ListBoxItem item in navigation.Items) {
            item.HorizontalContentAlignment = compact && !moving ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            var contents = (DockPanel)item.Content; var label = contents.Children.OfType<TextBlock>().Single(); label.Visibility = compact || moving ? Visibility.Collapsed : Visibility.Visible;
            contents.Children.OfType<System.Windows.Shapes.Path>().Single().Margin = new Thickness(0, 0, compact ? 0 : 8, 0);
        }
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
        var header = View.Stack(View.Text(L.T(title), 21), View.Text(L.T(lead), color: "Muted"));
        header.Margin = new Thickness(0, 0, 0, 8); View.Add(grid, header, 0); pages[id] = grid; pageHost.Children.Add(grid); return grid;
    }
    private Button Button(string key, Action action, bool primary = false) => View.Button(key, () => Guard(action), primary);
    private Button AsyncButton(string key, Func<Task> action, bool primary = false) {
        var button = View.Button(key, () => { }, primary);
        button.Click += async (_, _) => {
            if (operation != null) { if (operationButton == button) operation.Cancel(); return; }
            operationButton = button;
            try { await action(); } catch (Exception ex) { Error(ex); } finally { operationButton = null; }
        }; return button;
    }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
    private void ExportOperationLog() {
        var picker = new Microsoft.Win32.SaveFileDialog { Title = L.T("ExportLog"), Filter = "Text log|*.log|Text file|*.txt", FileName = "Storloom-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log", AddExtension = true };
        if (PickerHistory.Show(picker,this,"log-export") == true) operationView.ExportLog(picker.FileName);
    }
    private void Error(Exception ex) {
        status.Text = L.T("OperationFailureLog",ex.Message); operationView.AddLog(status.Text);
        if(debugEnabled)operationView.AddException(ex);
        try { JsonFiles.Write(Path.Combine(dataRoot, "errors", $"error-{Guid.NewGuid():N}.json"), new { time = DateTimeOffset.UtcNow, error = ex.ToString() }); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        Notice.Show(this, L.T("Error"), ex.Message);
    }
    private bool Confirm(string message) => Notice.Show(this, L.T("Confirm"), message, true);
    private IProgress<ScanProgress> Progress() => new ProgressRelay(Dispatcher, value => { if (operation != null) operationView.Report(value); });
    private Task RunPausable(Func<CancellationToken, Task> work) => RunCore(work,true);
    private Task Run(Func<CancellationToken, Task> work) => RunCore(work,false);
    private async Task RunCore(Func<CancellationToken, Task> work,bool pausable) {
        if (operation != null) return;
        using var source = new CancellationTokenSource(); using var pauseState=new OperationPause(); using var pauseScope=pauseState.Enter(); operationPause=pauseState; operation = source; operationIncomplete = false;
        navigation.IsEnabled = menu.IsEnabled = false;
        bool previousEnabled = operationButton?.IsEnabled ?? true;
        if (operationButton != null) operationButton.IsEnabled = true;
        var disabled = Controls(pageHost).Where(c => c != operationButton && c.IsEnabled && c is System.Windows.Controls.Button or TextBox or ComboBox or CheckBox or PasswordBox or DataGrid).ToArray();
        foreach (var control in disabled) control.IsEnabled = false;
        object? previousContent = operationButton?.Content;
        if (operationButton != null) operationButton.Content = L.T("Cancel");
        operationView.Visibility = Visibility.Visible; logDivider.Visibility = Visibility.Visible;
        if(operationView.AutoShowToggle.IsChecked==true&&!operationView.Expanded)operationView.ToggleExpanded();
        AnimateLog(operationView.Expanded ? Math.Clamp(preferences.LogHeight,80,Math.Max(80,ActualHeight*.55)) : 32);
        operationView.Start(); operationView.PauseButton.Content=View.Icon("M 4,2 L 4,14 M 10,2 L 10,14",12);operationView.PauseButton.ToolTip=L.T("PauseOperation");operationView.PauseButton.Visibility=pausable?Visibility.Visible:Visibility.Collapsed; cancel.Visibility=Visibility.Collapsed;operationView.CancelButton.Visibility=Visibility.Visible;
        statusFooter.Visibility=Visibility.Collapsed;
        status.Text = L.T("OperationStarting");if(debugEnabled)operationView.AddDebugDetail("task.options",$"worker_mode: {(preferences.ChatWorkersAutomatic?"auto":"manual")} | manual_workers: {preferences.ChatParallelism} | page: {currentPage} | auto_open_log: {preferences.LogAutoShow}"); bool complete = false;
        try { await work(source.Token); complete = !source.IsCancellationRequested && !operationIncomplete; }
        catch (OperationCanceledException) { status.Text = L.T("Cancelled"); if(debugEnabled)operationView.AddDiagnostic(new("operation.cancelled")); }
        catch (Exception ex) { Error(ex); }
        finally { operationPause=null;operationView.PauseButton.Visibility=operationView.CancelButton.Visibility=Visibility.Collapsed; operation = null; navigation.IsEnabled = menu.IsEnabled = true;
            foreach (var control in disabled) control.IsEnabled = true;
            if (operationButton != null) { operationButton.Content = previousContent; operationButton.IsEnabled = previousEnabled; }
            cancel.Visibility = statusFooter.Visibility = Visibility.Collapsed; operationView.Finish(status.Text, complete); }
        if (exitAfterOperation) _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private static IEnumerable<Control> Controls(DependencyObject root) {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i); if (child is Control control) yield return control;
            foreach (var nested in Controls(child)) yield return nested;
        }
    }
    private void ReloadEngine() {
        string settings = Path.Combine(dataRoot, "settings.json");
        engine = new CleanupEngine(RuleSets.Load(rulesDirectory, preferences.EnabledRuleSets, Path.Combine(dataRoot, "rules.json"), Path.Combine(dataRoot, "rule-sets")),
            File.Exists(settings) ? JsonFiles.Read<CleanerSettings>(settings) : new()); cleanupPlan = null; excluded.Clear();
    }
    private void ShowPreferences(bool ruleSets = false) {
        var dialog = new PreferencesWindow(preferences, rulesDirectory, dataRoot, engine.Settings, ruleSets) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var history=operationView;
        preferences = dialog.Result; JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences);
        SetCapturedKey(null); L.SetLanguage(preferences.Language); Theme.Apply(preferences); ReloadEngine(); BuildUi();
        operationView.RestoreHistory(history);operationView.AddLog(L.T(preferences.DebugLogging?"DebugEnabled":"DebugDisabled"));
        if (preferences.CloseToTray) EnsureTray(); tray?.Refresh();
    }
    private void ShowHelp(int? topic = null, string? document = null) => new HelpWindow(topic ?? (currentPage == "chats" ? 2 : currentPage == "overview" ? 5 : 0), document) { Owner = this }.ShowDialog();
    private void ImportRules() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" }; if (PickerHistory.Show(dialog,this,"rule-import") != true) return;
        var catalog = JsonFiles.Read<RuleCatalog>(dialog.FileName); _ = new CleanupEngine(catalog, engine.Settings);
        string path = Path.Combine(dataRoot, "rules.json");
        var merged = File.Exists(path) ? JsonFiles.Read<RuleCatalog>(path).Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase) : [];
        foreach (var rule in catalog.Rules) merged[rule.Id] = rule;
        JsonFiles.Write(path, new RuleCatalog { Rules = merged.Values.ToList() }); ReloadEngine(); RenderRules(); Navigate("cleanup");
    }
    private void SaveCleanupPlan() {
        if (cleanupPlan == null) throw new InvalidOperationException(L.T("ScanFirst"));
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON|*.cleanup-plan.json" };
        if (PickerHistory.Show(dialog,this,"plan-save") == true) JsonFiles.Write(dialog.FileName, cleanupPlan with {
            Files = SelectedFiles(), Commands = cleanupPlan.Commands.Where(c => SelectedRules().Contains(c.RuleId)).ToList() });
    }
    private void LoadCleanupPlan() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.cleanup-plan.json" }; if (PickerHistory.Show(dialog,this,"plan-load") != true) return;
        cleanupPlan = JsonFiles.Read<CleanupPlan>(dialog.FileName); excluded.Clear(); RenderRules(); Navigate("cleanup");
    }
    private void OpenReaderArchive() {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Storloom reading copy|messages.html;vault.storloom.json" };
        if (PickerHistory.Show(dialog,this,"reader-open") == true) new ChatReaderWindow(Path.GetDirectoryName(dialog.FileName)!, dataRoot, preferences.ReduceMotion) { Owner = this }.ShowDialog();
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
