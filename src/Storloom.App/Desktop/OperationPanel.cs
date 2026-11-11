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
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 0, BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 4), VerticalContentAlignment = VerticalAlignment.Top };
    public CheckBox FollowToggle { get; } = new() { Content = L.T("FollowLog"), IsChecked = true, ToolTip = "Ctrl+Shift+L", VerticalAlignment = VerticalAlignment.Center };
    public CheckBox DebugToggle { get; } = new() { Content = L.T("DebugLog"), ToolTip = L.T("DebugLogHelp"), VerticalAlignment = VerticalAlignment.Center };
    public event Action? ExportRequested;
    public event Action? PauseRequested;
    public event Action? CancelRequested;
    public Button PauseButton { get; }
    public Button CancelButton { get; }
    public event Action<bool>? DebugLoggingChanged;
    public bool Follow => FollowToggle.IsChecked == true;
    public bool Expanded { get; private set; }
    public TextBlock StatusText=>state;
    public CheckBox WrapToggle {get;}=new();
    public CheckBox AutoShowToggle {get;}=new(){IsChecked=true};
    public event Action? OptionsChanged;
    public event Action<bool>? ExpansionChanged;
    private readonly TextBlock state = View.Text(""), timing = View.Text("", color: "Muted");
    private readonly ProgressBar progress = new() { Name = "operation_progress", Height = 2, MinHeight = 2, Maximum = 1, Margin = new Thickness(0),VerticalAlignment=VerticalAlignment.Bottom,IsHitTestVisible=false,Visibility=Visibility.Collapsed };
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Button collapse;
    private ScanProgress? latest;
    private string phase = "", detail = "";
    private readonly HashSet<string> seenPhases=new(StringComparer.Ordinal);
    private double phaseStart;
    private long phaseStartDone;
    private bool followPending;
    private readonly Queue<string> retained = new();
    private int retainedCharacters;
    private bool earlierLinesDropped;
    public OperationPanel() {
        PauseButton=View.IconButton("M 4,2 L 4,14 M 10,2 L 10,14","PauseOperation",()=>PauseRequested?.Invoke());PauseButton.Name="pause_operation";PauseButton.Visibility=Visibility.Collapsed;
        CancelButton=View.IconButton("M 3,3 L 13,13 M 13,3 L 3,13","Cancel",()=>CancelRequested?.Invoke());CancelButton.Name="cancel_operation";CancelButton.Visibility=Visibility.Collapsed;
        Margin = new Thickness(10,0,8,0);ControlFeedback.SetEnabled(Log,false);Log.Visibility=Visibility.Collapsed;
        RowDefinitions.Add(new RowDefinition{Height=new GridLength(32)});RowDefinitions.Add(new RowDefinition{Height=new GridLength(0)});RowDefinitions.Add(new RowDefinition{Height=View.Fill});
        var row=View.Grid(View.Fill);row.ColumnDefinitions.Add(new ColumnDefinition{Width=View.Fill});row.ColumnDefinitions.Add(new ColumnDefinition{Width=View.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=View.Auto});
        collapse=View.IconButton("M 1,9 L 6,4 11,9","ShowOperationLog",ToggleExpanded);collapse.Name="toggle_log";
        var export=View.IconButton("M 6,0 L 6,9 M 2,5 L 6,9 10,5 M 0,10 L 0,13 12,13 12,10","ExportLog",()=>ExportRequested?.Invoke());export.Name="export_log";
        void ToggleIcon(CheckBox control,string geometry,string key,string name){var icon=View.Icon(geometry,12);icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(CheckBox),1)});control.Content=icon;control.ToolTip=L.T(key);control.Name=name;control.SetResourceReference(FrameworkElement.StyleProperty,"LogIconToggle");System.Windows.Automation.AutomationProperties.SetName(control,L.T(key));}
        ToggleIcon(FollowToggle,"M 0,2 L 12,2 M 0,6 L 12,6 M 6,9 L 9,12 12,9 M 9,7 L 9,12","FollowLog","follow_log");
        ToggleIcon(DebugToggle,"M 3,3 L 9,3 9,10 3,10 Z M 4,0 L 5,3 M 8,0 L 7,3 M 0,5 L 3,5 M 9,5 L 12,5 M 0,9 L 3,9 M 9,9 L 12,9","DebugLog","debug_log");
        ToggleIcon(WrapToggle,"M 0,2 L 12,2 M 0,6 L 9,6 Q 14,6 11,10 L 5,10 M 7,8 L 5,10 7,12 M 0,10 L 2,10","WrapLog","wrap_log");
        ToggleIcon(AutoShowToggle,"M 1,3 L 11,3 11,12 1,12 Z M 3,0 L 9,0 M 6,0 L 6,7 M 4,5 L 6,7 8,5","AutoShowLog","auto_show_log");
        var actions=View.Row(PauseButton,CancelButton,DebugToggle,WrapToggle,FollowToggle,AutoShowToggle,export,collapse);actions.Margin=new Thickness(4,0,0,0);
        state.TextTrimming=TextTrimming.CharacterEllipsis;state.TextWrapping=TextWrapping.NoWrap;state.FontSize=13;state.Margin=new Thickness(0);timing.FontSize=12;timing.Margin=new Thickness(8,0,4,0);
        View.Add(row,state,0);View.Add(row,timing,0,1);View.Add(row,actions,0,2);View.Add(this,row,0);View.Add(this,progress,0);View.Add(this,Log,2);
        void WrapChanged(){Log.TextWrapping=WrapToggle.IsChecked==true?TextWrapping.Wrap:TextWrapping.NoWrap;Log.HorizontalScrollBarVisibility=WrapToggle.IsChecked==true?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;OptionsChanged?.Invoke();}
        WrapToggle.Checked+=(_,_)=>WrapChanged();WrapToggle.Unchecked+=(_,_)=>WrapChanged();AutoShowToggle.Checked+=(_,_)=>OptionsChanged?.Invoke();AutoShowToggle.Unchecked+=(_,_)=>OptionsChanged?.Invoke();
        progress.SetResourceReference(Control.ForegroundProperty, "AccentText"); progress.SetResourceReference(Control.BackgroundProperty, "Line");
        progress.SetResourceReference(FrameworkElement.StyleProperty, "OperationProgress");
        FollowToggle.Checked += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (Follow) Log.ScrollToEnd(); }));
        Log.PreviewMouseWheel += (_, _) => PauseFollowing();
        Log.PreviewMouseLeftButtonDown += (_, _) => PauseFollowing();
        Log.PreviewKeyDown += (_, e) => { if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) PauseFollowing(); };
        timer.Tick += (_, _) => RefreshTiming(); Unloaded += (_, _) => timer.Stop();
        DebugToggle.Checked += (_, _) => { AddLog(L.T("DebugEnabled")); DebugLoggingChanged?.Invoke(true); };
        DebugToggle.Unchecked += (_, _) => { AddLog(L.T("DebugDisabled")); DebugLoggingChanged?.Invoke(false); };
    }
    public void ToggleFollowing() => FollowToggle.IsChecked = !Follow;
    public void PauseFollowing() => FollowToggle.IsChecked = false;
    public void ToggleExpanded() {
        Expanded = !Expanded; Log.Visibility = Expanded ? Visibility.Visible : Visibility.Collapsed;
        collapse.Content = View.Icon(Expanded ? "M 1,4 L 6,9 11,4" : "M 1,9 L 6,4 11,9",12);
        collapse.ToolTip = L.T(Expanded ? "HideOperationLog" : "ShowOperationLog");
        ExpansionChanged?.Invoke(Expanded); if (Expanded) Theme.Enter(Log);
    }
    public void AddLog(string message)=>AddEntry("INFO",message);
    private void AddEntry(string level,string message) {
        int start = Log.SelectionStart, length = Log.SelectionLength; double offset = Log.VerticalOffset;
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {message.Replace('\r', ' ').Replace('\n', ' ')}\n";
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
    public void RestoreHistory(OperationPanel previous){
        Log.Text=previous.Log.Text;retained.Clear();foreach(string line in previous.retained)retained.Enqueue(line);
        retainedCharacters=previous.retainedCharacters;earlierLinesDropped=previous.earlierLinesDropped;
    }
    public void AddDiagnostic(ChatDiagnostic diagnostic) {
        if (DebugToggle.IsChecked != true) return;
        // Stage IDs are fixed code identifiers, never free-form native callback output.
        if (!System.Text.RegularExpressions.Regex.IsMatch(diagnostic.Stage, "^[a-z0-9.-]{1,64}$")) return;
        var fields=new List<string>();
        if(diagnostic.Processes>0)fields.Add("processes: "+diagnostic.Processes.ToString(CultureInfo.InvariantCulture));
        if(diagnostic.Regions>0)fields.Add("regions: "+diagnostic.Regions.ToString(CultureInfo.InvariantCulture));
        if(diagnostic.Bytes>0)fields.Add("bytes: "+diagnostic.Bytes.ToString("N0",CultureInfo.InvariantCulture));
        if(diagnostic.Candidates>0)fields.Add("candidates: "+diagnostic.Candidates.ToString(CultureInfo.InvariantCulture));
        if(diagnostic.ElapsedSeconds>0)fields.Add("elapsed: "+diagnostic.ElapsedSeconds.ToString("F2",CultureInfo.InvariantCulture)+"s");
        AddEntry("DEBUG",diagnostic.Stage.PadRight(30)+string.Join(" | ",fields));
    }
    private static string Ascii(string value){var result=new StringBuilder();foreach(char c in value){if(c is >= ' ' and <= '~')result.Append(c);else if(c=='\t')result.Append("    ");else result.Append("\\u"+((int)c).ToString("x4",CultureInfo.InvariantCulture));}return result.ToString();}
    public void AddDebugDetail(string stage,string value){if(DebugToggle.IsChecked==true)AddEntry("DEBUG",Ascii(stage).PadRight(30)+Ascii(value));}
    public void AddException(Exception error) {
        if(DebugToggle.IsChecked!=true)return;
        if(error is AggregateException aggregate){foreach(var inner in aggregate.Flatten().InnerExceptions.Take(8))AddException(inner);return;}
        AddDebugDetail("exception",error.GetType().FullName+" | hresult: 0x"+error.HResult.ToString("X8"));
        foreach(string frame in (error.StackTrace??"").Split('\n',StringSplitOptions.RemoveEmptyEntries).Take(16))AddDebugDetail("stack",frame.Trim());
    }
    public void ExportLog(string path) {
        using var output = new StreamWriter(path, false, new UTF8Encoding(true));
        output.WriteLine($"Storloom {ProductInfo.Version} | {DateTimeOffset.Now:O}");
        output.WriteLine($"Debug logging currently enabled: {DebugToggle.IsChecked == true}; earlier lines dropped: {earlierLinesDropped}");
        output.WriteLine("Scope: retained session history; retained log tail is bounded to 16 Mi characters.");
        output.WriteLine(); foreach (string line in retained) output.Write(line);
    }
    private double lastHeartbeat;
    private bool paused;
    public void SetPaused(bool value){paused=value;if(value)clock.Stop();else clock.Start();RefreshTiming();}
    public void Start() {
        paused=false;lastHeartbeat=-2;seenPhases.Clear();
        latest = null; phase = detail = ""; phaseStart = 0; clock.Restart(); timer.Start();
        progress.BeginAnimation(ProgressBar.ValueProperty, null); progress.Value = 0;
        progress.Tag = Theme.ReduceMotion ? "reduced" : "animate";
        progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true; state.Text = L.T("OperationStarting"); AddLog(L.T("OperationStarting"));
        if(DebugToggle.IsChecked==true)AddDiagnostic(new("operation.start"));
        if (DebugToggle.IsChecked == true) AddDebugDetail("runtime",$"Storloom: {ProductInfo.Version} | .NET: {Environment.Version} | x64: {Environment.Is64BitProcess}");
    }
    public void Report(ScanProgress value) {
        latest = value;
        if (phase != value.RuleId) { phase = value.RuleId; phaseStart = clock.Elapsed.TotalSeconds; phaseStartDone = value.TotalBytes != null ? value.Bytes : value.Files; if(seenPhases.Add(phase))AddLog(L.T(PhaseKey(phase))); if(DebugToggle.IsChecked==true)AddDiagnostic(new("phase." + phase, Bytes: value.Bytes, ElapsedSeconds: phaseStart)); }
        if (!string.IsNullOrWhiteSpace(value.Detail) && detail != value.Detail) { detail = value.Detail; if(DebugToggle.IsChecked==true)AddDebugDetail("file."+phase,detail); }
        state.Text = Phase(phase) + (value.TotalBytes is long bytes ? $" | {View.Size(value.Bytes)} / {View.Size(bytes)}" : value.TotalFiles is int files ? $" | {value.Files:N0} / {files:N0}" : value.Files>0||value.Bytes>0 ? " | "+L.T("ProgressSoFar",value.Files,View.Size(value.Bytes)) : "");
        long total = value.TotalBytes ?? value.TotalFiles ?? 0, done = value.TotalBytes != null ? value.Bytes : value.Files;
        progress.IsIndeterminate = total <= 0;
        progress.Visibility = Visibility.Visible;
        if (total > 0) {
            double target = Math.Clamp(done / (double)total, 0, 1);
            state.Text += $" | {target:P0}";
            if (Theme.ReduceMotion) { progress.BeginAnimation(ProgressBar.ValueProperty, null); progress.Value = target; }
            else progress.BeginAnimation(ProgressBar.ValueProperty, new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(120)));
        }
        if(DebugToggle.IsChecked==true&&clock.Elapsed.TotalSeconds-lastHeartbeat>=2){lastHeartbeat=clock.Elapsed.TotalSeconds;
            AddDebugDetail("progress."+phase,$"files: {value.Files.ToString(CultureInfo.InvariantCulture)}/{value.TotalFiles?.ToString(CultureInfo.InvariantCulture)??"?"} | bytes: {value.Bytes.ToString(CultureInfo.InvariantCulture)}/{value.TotalBytes?.ToString(CultureInfo.InvariantCulture)??"?"} | elapsed: {lastHeartbeat.ToString("F2",CultureInfo.InvariantCulture)}s");
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
        if (complete) progress.Value = 1; state.Text = summary; timing.Text = L.T("ElapsedOnly", clock.Elapsed.ToString(@"hh\:mm\:ss")); AddLog(L.T(complete?"OperationCompletedLog":"OperationStoppedLog"));
        if(DebugToggle.IsChecked==true)AddDiagnostic(new(complete ? "operation.complete" : "operation.stopped", ElapsedSeconds: clock.Elapsed.TotalSeconds));
    }
    private static string Phase(string id) => L.T(PhaseKey(id));
    private static string PhaseKey(string id) => id switch {
        "chat-encryption-open"=>"PhaseEncryptionOpen","chat-encryption-check"=>"PhaseEncryptionCheck","chat-encryption-select"=>"PhaseEncryptionSelect","chat-encryption-plan"=>"PhaseEncryptionPlan","chat-encryption-derive"=>"PhaseEncryptionDerive","chat-encryption-save-plan"=>"PhaseEncryptionSavePlan",
        "transfer-plan"=>"PhaseTransferPlan", "transfer" => "PhaseCopyVerify", "transfer-remove" => "PhaseRemove", "chat-verify" => "PhaseBackupVerify", "chat-resume" => "PhaseResumeExport", "chat-discovery" => "FindAccounts",
        "chat-encrypt" => "Phase_chat-encrypt", "chat-avatars" => "PhaseGroupAvatars", "chat-update-index" => "PhaseUpdateIndex", "chat-update-merge" => "PhaseUpdateMerge", "chat-decrypt" => "PhaseDecrypt", "chat-export" => "PhaseParse", "chat-reader" or "chat-pages" => "PhaseReader", "native-tool" => "PhaseNative", _ => "PhaseScan" };
}

internal sealed class ActionCommand(Action action) : ICommand {
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
