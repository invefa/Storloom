using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Storloom.Core;
using Storloom.Core.Chats;
using System.Text;
using System.Globalization;

namespace Storloom.App;

public sealed class OperationPanel : Grid {
    public TextBox Log { get; } = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 0, BorderThickness = new Thickness(0), Padding = new Thickness(0, 8, 0, 0), VerticalContentAlignment = VerticalAlignment.Top };
    public CheckBox FollowToggle { get; } = new() { Content = L.T("FollowLog"), IsChecked = true, ToolTip = "Ctrl+Shift+L", VerticalAlignment = VerticalAlignment.Center };
    public CheckBox DebugToggle { get; } = new() { Content = L.T("DebugLog"), ToolTip = L.T("DebugLogHelp"), VerticalAlignment = VerticalAlignment.Center };
    public event Action? ExportRequested;
    public event Action? PauseRequested;
    public Button PauseButton { get; } = new() { Name="pause_operation", Content=L.T("PauseOperation"), Visibility=Visibility.Collapsed, Padding=new Thickness(8,2,8,2), Margin=new Thickness(0,0,8,0) };
    public event Action<bool>? DebugLoggingChanged;
    public bool Follow => FollowToggle.IsChecked == true;
    public bool Expanded { get; private set; } = true;
    public event Action<bool>? ExpansionChanged;
    private readonly TextBlock state = View.Text(""), timing = View.Text("", color: "Muted");
    private readonly ProgressBar progress = new() { Name = "operation_progress", Height = 6, MinHeight = 6, Maximum = 1, Margin = new Thickness(0, 5, 0, 3) };
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Button collapse;
    private ScanProgress? latest;
    private string phase = "", detail = "";
    private double phaseStart;
    private long phaseStartDone;
    private bool followPending;
    private readonly Queue<string> retained = new();
    private int retainedCharacters;
    private bool earlierLinesDropped;
    public OperationPanel() {
        Margin = new Thickness(16, 3, 16, 5); ControlFeedback.SetEnabled(Log, false);
        RowDefinitions.Add(new RowDefinition { Height = View.Auto }); RowDefinitions.Add(new RowDefinition { Height = View.Auto });
        RowDefinitions.Add(new RowDefinition { Height = View.Fill });
        var row = new DockPanel(); collapse = View.IconButton("M 1,5 L 7,11 13,5", "HideOperationLog", ToggleExpanded);
        collapse.Name = "toggle_log"; collapse.Margin = new Thickness(8, 0, 0, 0);
        var export = View.IconButton("M 7,1 L 7,10 M 3,6 L 7,10 11,6 M 1,11 L 1,14 13,14 13,11", "ExportLog", () => ExportRequested?.Invoke()); export.Name = "export_log";
        PauseButton.Click += (_,_) => PauseRequested?.Invoke();
        foreach (var control in new UIElement[] { collapse, export, FollowToggle, DebugToggle, PauseButton, timing }) { DockPanel.SetDock(control, Dock.Right); row.Children.Add(control); }
        state.TextTrimming = TextTrimming.CharacterEllipsis; state.TextWrapping = TextWrapping.NoWrap;
        state.Margin = new Thickness(0); timing.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(state); View.Add(this, row, 0); View.Add(this, progress, 1); View.Add(this, Log, 2);
        progress.SetResourceReference(Control.ForegroundProperty, "AccentText"); progress.SetResourceReference(Control.BackgroundProperty, "Line");
        progress.SetResourceReference(FrameworkElement.StyleProperty, "OperationProgress");
        FollowToggle.Checked += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (Follow) Log.ScrollToEnd(); }));
        Log.PreviewMouseWheel += (_, _) => PauseFollowing();
        Log.PreviewMouseLeftButtonDown += (_, _) => PauseFollowing();
        Log.PreviewKeyDown += (_, e) => { if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) PauseFollowing(); };
        timer.Tick += (_, _) => RefreshTiming(); Unloaded += (_, _) => timer.Stop();
        DebugToggle.Checked += (_, _) => { AddLog("[debug] debug.enabled"); DebugLoggingChanged?.Invoke(true); };
        DebugToggle.Unchecked += (_, _) => { AddLog("[debug] debug.disabled"); DebugLoggingChanged?.Invoke(false); };
    }
    public void ToggleFollowing() => FollowToggle.IsChecked = !Follow;
    public void PauseFollowing() => FollowToggle.IsChecked = false;
    public void ToggleExpanded() {
        Expanded = !Expanded; Log.Visibility = Expanded ? Visibility.Visible : Visibility.Collapsed;
        collapse.Content = View.Icon(Expanded ? "M 1,5 L 7,11 13,5" : "M 1,11 L 7,5 13,11");
        collapse.ToolTip = L.T(Expanded ? "HideOperationLog" : "ShowOperationLog");
        ExpansionChanged?.Invoke(Expanded); if (Expanded) Theme.Enter(Log);
    }
    public void AddLog(string message) {
        int start = Log.SelectionStart, length = Log.SelectionLength; double offset = Log.VerticalOffset;
        string line = $"{DateTime.Now:HH:mm:ss}  {message.Replace('\r', ' ').Replace('\n', ' ')}\n";
        const int limit = 16 * 1024 * 1024;
        if (line.Length > limit) { line = line[^limit..]; earlierLinesDropped = true; }
        retained.Enqueue(line); retainedCharacters += line.Length;
        while (retainedCharacters > limit && retained.TryDequeue(out string? removed)) { retainedCharacters -= removed.Length; earlierLinesDropped = true; }
        Log.AppendText(line);
        if (Log.SelectionStart != start || Log.SelectionLength != length) Log.Select(start, length);
        // Keep a bounded live tail. Operation journals remain on disk. Never trim while reading history.
        if (Follow && Log.Text.Length > 300000) Log.Text = Log.Text[^200000..];
        if (Follow && !followPending) {
            followPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { followPending = false; if (Follow) Log.ScrollToEnd(); }));
        } else if (!Follow) Log.ScrollToVerticalOffset(offset);
    }
    public void AddDiagnostic(ChatDiagnostic diagnostic) {
        if (DebugToggle.IsChecked != true) return;
        // Stage IDs are fixed code identifiers, never free-form native callback output.
        if (!System.Text.RegularExpressions.Regex.IsMatch(diagnostic.Stage, "^[a-z0-9.-]{1,64}$")) return;
        AddLog(string.Create(CultureInfo.InvariantCulture, $"[debug] {diagnostic.Stage}; processes={diagnostic.Processes}; regions={diagnostic.Regions}; scannedBytes={diagnostic.Bytes}; candidates={diagnostic.Candidates}; elapsedSeconds={diagnostic.ElapsedSeconds:F3}"));
    }
    public void AddException(Exception error) {
        if (DebugToggle.IsChecked == true) AddLog($"[debug] exceptionType={error.GetType().FullName}; hresult=0x{error.HResult:X8}; stack={error.StackTrace}");
    }
    public void ExportLog(string path) {
        using var output = new StreamWriter(path, false, new UTF8Encoding(true));
        output.WriteLine($"Storloom {ProductInfo.Version} | {DateTimeOffset.Now:O}");
        output.WriteLine($"Debug logging currently enabled: {DebugToggle.IsChecked == true}; earlier lines dropped: {earlierLinesDropped}");
        output.WriteLine("Scope: current operation; retained log tail is bounded to 16 Mi characters.");
        output.WriteLine(); foreach (string line in retained) output.Write(line);
    }
    private bool paused;
    public void SetPaused(bool value){paused=value;if(value)clock.Stop();else clock.Start();RefreshTiming();}
    public void Start() {
        paused=false;
        latest = null; phase = detail = ""; phaseStart = 0; Log.Clear(); retained.Clear(); retainedCharacters = 0; earlierLinesDropped = false; clock.Restart(); timer.Start();
        progress.BeginAnimation(ProgressBar.ValueProperty, null); progress.Value = 0;
        progress.Tag = Theme.ReduceMotion ? "reduced" : "animate";
        progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true; state.Text = L.T("OperationStarting"); AddLog(L.English("OperationStarting"));
        AddDiagnostic(new("operation.start"));
        if (DebugToggle.IsChecked == true) AddLog($"[debug] Storloom={ProductInfo.Version}; runtime={Environment.Version}; process64Bit={Environment.Is64BitProcess}");
    }
    public void Report(ScanProgress value) {
        latest = value;
        if (phase != value.RuleId) { phase = value.RuleId; phaseStart = clock.Elapsed.TotalSeconds; phaseStartDone = value.TotalBytes != null ? value.Bytes : value.Files; AddLog(L.English(PhaseKey(phase))); AddDiagnostic(new("phase." + phase, Bytes: value.Bytes, ElapsedSeconds: phaseStart)); }
        if (!string.IsNullOrWhiteSpace(value.Detail) && detail != value.Detail) { detail = value.Detail; AddLog(detail); }
        state.Text = Phase(phase) + (value.TotalBytes is long bytes ? $" | {View.Size(value.Bytes)} / {View.Size(bytes)}" : value.TotalFiles is int files ? $" | {value.Files:N0} / {files:N0}" : "");
        long total = value.TotalBytes ?? value.TotalFiles ?? 0, done = value.TotalBytes != null ? value.Bytes : value.Files;
        progress.IsIndeterminate = total <= 0;
        progress.Visibility = Visibility.Visible;
        if (total > 0) {
            double target = Math.Clamp(done / (double)total, 0, 1);
            state.Text += $" | {target:P0}";
            if (Theme.ReduceMotion) { progress.BeginAnimation(ProgressBar.ValueProperty, null); progress.Value = target; }
            else progress.BeginAnimation(ProgressBar.ValueProperty, new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(120)));
        }
        RefreshTiming();
    }
    private void RefreshTiming() {
        string elapsed = clock.Elapsed.ToString(@"hh\:mm\:ss");
        if(paused){timing.Text=L.T("PauseOperation")+" | "+L.T("ElapsedOnly",elapsed);return;}
        if (latest == null) { timing.Text = L.T("ElapsedOnly", elapsed); return; }
        double seconds = Math.Max(0.1, clock.Elapsed.TotalSeconds - phaseStart);
        long total = latest.TotalBytes ?? latest.TotalFiles ?? 0, done = latest.TotalBytes != null ? latest.Bytes : latest.Files;
        long advanced = Math.Max(0, done - phaseStartDone);
        string eta = total > done && advanced > 0 && seconds > 1 ? TimeSpan.FromSeconds(Math.Min(359999, (total - done) * seconds / advanced)).ToString(@"hh\:mm\:ss") : "--:--:--";
        timing.Text = L.T("OperationTiming", elapsed, eta, latest.TotalBytes != null ? View.Size((long)(advanced / seconds)) + "/s" : $"{advanced / seconds:N0} /s");
    }
    public void Finish(string summary, bool complete) {
        timer.Stop(); clock.Stop(); progress.IsIndeterminate = false; progress.BeginAnimation(ProgressBar.ValueProperty, null);
        progress.Visibility = Visibility.Collapsed;
        if (complete) progress.Value = 1; state.Text = summary; timing.Text = L.T("ElapsedOnly", clock.Elapsed.ToString(@"hh\:mm\:ss")); AddLog(complete ? "Operation completed." : "Operation stopped. Review the reported results.");
        AddDiagnostic(new(complete ? "operation.complete" : "operation.stopped", ElapsedSeconds: clock.Elapsed.TotalSeconds));
    }
    private static string Phase(string id) => L.T(PhaseKey(id));
    private static string PhaseKey(string id) => id switch {
        "transfer" => "PhaseCopyVerify", "transfer-remove" => "PhaseRemove", "chat-verify" => "PhaseBackupVerify", "chat-resume" => "PhaseResumeExport", "chat-discovery" => "FindAccounts",
        "chat-encrypt" => "Phase_chat-encrypt", "chat-avatars" => "PhaseGroupAvatars", "chat-update-index" => "PhaseUpdateIndex", "chat-update-merge" => "PhaseUpdateMerge", "chat-decrypt" => "PhaseDecrypt", "chat-export" => "PhaseParse", "chat-reader" or "chat-pages" => "PhaseReader", "native-tool" => "PhaseNative", _ => "PhaseScan" };
}

internal sealed class ActionCommand(Action action) : ICommand {
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
