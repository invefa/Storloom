using System.Diagnostics;
using System.ComponentModel;
using Storloom.Core;
using Storloom.Core.Localization;

namespace Storloom.App;

internal sealed class OperationView : UserControl {
    private readonly Label state, timing;
    private readonly SmoothProgressBar bar;
    private readonly RichTextBox log;
    private readonly Stopwatch clock = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private ScanProgress? latest;
    private string phase = "", lastDetail = "";
    private double phaseStart;
    private readonly Dictionary<string, double> phaseTimes = [];
    public OperationView() {
        Dock = DockStyle.Bottom; Height = 174; Padding = new Padding(18, 8, 18, 8);
        BackColor = Ui.Surface; DoubleBuffered = true;
        var grid = Ui.Grid(new(SizeType.Absolute, 26), new(SizeType.Absolute, 8), Ui.Fill());
        var row = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        row.ColumnStyles.Add(new(SizeType.Percent, 58)); row.ColumnStyles.Add(new(SizeType.Percent, 42));
        row.ColumnStyles.Add(new(SizeType.Absolute, 65));
        state = Ui.Text(""); timing = Ui.Text("");
        state.AutoSize = timing.AutoSize = false; state.AutoEllipsis = timing.AutoEllipsis = true;
        state.Dock = timing.Dock = DockStyle.Fill;
        timing.TextAlign = ContentAlignment.MiddleRight; row.Controls.Add(state, 0, 0); row.Controls.Add(timing, 1, 0);
        bar = new SmoothProgressBar { Dock = DockStyle.Fill, Maximum = 1000, Margin = Padding.Empty };
        log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = Ui.Surface, ForeColor = Ui.Muted, Font = new Font("Consolas", 9f),
            DetectUrls = false, HideSelection = false, Margin = new Padding(0, 7, 0, 0) };
        var toggle = Ui.Button("HideOperationLog"); toggle.MinimumSize = new Size(60, 24); toggle.AutoSize = false;
        toggle.Size = new Size(60, 24); toggle.Padding = Padding.Empty;
        toggle.Click += (_, _) => { log.Visible = !log.Visible; Height = log.Visible ? 174 : 62; toggle.Text = L.T(log.Visible ? "HideOperationLog" : "ShowOperationLog"); };
        row.Controls.Add(toggle, 2, 0);
        grid.Controls.Add(row, 0, 0); grid.Controls.Add(bar, 0, 1); grid.Controls.Add(log, 0, 2); Controls.Add(grid);
        timer.Tick += (_, _) => RefreshTiming();
    }
    public void Start() {
        latest = null; phase = lastDetail = ""; log.Clear(); clock.Restart(); phaseStart = 0; phaseTimes.Clear();
        state.Text = L.T("OperationStarting"); timing.Text = ""; bar.Style = ProgressBarStyle.Marquee;
        Visible = true; timer.Start(); AddLog(L.T("OperationStarting"));
    }
    public void Report(ScanProgress value) {
        latest = value;
        if (value.SourceBytes is long source) AddLog(L.T("TransferWorkScope", Ui.Size(source), Ui.Size(value.TotalBytes ?? 0)));
        if (phase != value.RuleId) {
            phase = value.RuleId;
            if (!phaseTimes.TryGetValue(phase, out phaseStart)) phaseTimes[phase] = phaseStart = clock.Elapsed.TotalSeconds;
            AddLog(Phase(phase));
        }
        state.Text = Phase(value.RuleId) + (value.TotalBytes is long size ? $" | {Ui.Size(value.Bytes)} / {Ui.Size(size)}" :
            value.TotalFiles is int files ? $" | {value.Files:N0} / {files:N0}" : "");
        if (!string.IsNullOrWhiteSpace(value.Detail) && value.Detail != lastDetail) {
            lastDetail = value.Detail; AddLog(value.Detail);
        }
        long total = value.TotalBytes ?? value.TotalFiles ?? 0;
        long done = value.TotalBytes != null ? value.Bytes : value.Files;
        if (total > 0) { bar.Style = ProgressBarStyle.Continuous; bar.Value = (int)Math.Clamp(done * 1000d / total, 0, 1000); }
        else bar.Style = ProgressBarStyle.Marquee;
        RefreshTiming();
    }
    private void RefreshTiming() {
        if (!clock.IsRunning) return;
        double seconds = Math.Max(0.1, clock.Elapsed.TotalSeconds - phaseStart);
        var value = latest;
        string elapsed = clock.Elapsed.ToString(@"hh\:mm\:ss");
        if (value == null) { timing.Text = L.T("ElapsedOnly", elapsed); return; }
        long done = value.TotalBytes != null ? value.Bytes : value.Files;
        long total = value.TotalBytes ?? value.TotalFiles ?? 0;
        string eta = total > done && done > 0 && seconds > 1 ? TimeSpan.FromSeconds(Math.Min(359999, (total - done) * seconds / done)).ToString(@"hh\:mm\:ss") : "--:--:--";
        string rate = value.TotalBytes != null ? Ui.Size((long)(value.Bytes / seconds)) + "/s" : $"{value.Files / seconds:N0} /s";
        timing.Text = L.T("OperationTiming", elapsed, eta, rate);
    }
    public void Finish(string summary, bool completed) {
        timer.Stop(); clock.Stop(); bar.Style = ProgressBarStyle.Continuous; if (completed) bar.Value = 1000;
        state.Text = summary; timing.Text = L.T("ElapsedOnly", clock.Elapsed.ToString(@"hh\:mm\:ss")); AddLog(summary);
    }
    public void AddLog(string message) {
        int start = log.SelectionStart, length = log.SelectionLength;
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
        if (log.TextLength > 60000) log.Text = log.Text[^40000..];
        if (log.Focused) log.Select(Math.Min(start, log.TextLength), Math.Min(length, Math.Max(0, log.TextLength - start)));
        else { log.Select(log.TextLength, 0); log.ScrollToCaret(); }
    }
    private static string Phase(string phase) => L.T(phase switch {
        "transfer" => "PhaseCopyVerify", "transfer-remove" => "PhaseRemove", "chat-verify" => "PhaseBackupVerify",
        "chat-decrypt" => "PhaseDecrypt", "chat-export" => "PhaseParse", "chat-reader" or "chat-pages" => "PhaseReader",
        "native-tool" => "PhaseNative", _ => "PhaseScan"
    });
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}

internal sealed class SmoothProgressBar : Control {
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 40 };
    private int position, value;
    private ProgressBarStyle style;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Maximum { get; set; } = 1000;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public int Value { get => value; set { this.value = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public ProgressBarStyle Style { get => style; set {
        style = value; animation.Enabled = style == ProgressBarStyle.Marquee && Visible; Invalidate();
    } }
    public SmoothProgressBar() {
        DoubleBuffered = true; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        animation.Tick += (_, _) => { position = (position + 3) % 130; Invalidate(); };
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); animation.Enabled = Visible && Style == ProgressBarStyle.Marquee; }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(Ui.Line); using var brush = new SolidBrush(Ui.Accent);
        if (Style == ProgressBarStyle.Marquee) e.Graphics.FillRectangle(brush, new Rectangle((int)(Width * (position - 30) / 100d), 0, Math.Max(20, Width / 4), Height));
        else e.Graphics.FillRectangle(brush, new Rectangle(0, 0, (int)(Width * Math.Clamp(Value / (double)Math.Max(1, Maximum), 0, 1)), Height));
    }
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
}
