using System.Runtime.InteropServices;
using Storloom.Core.Localization;

namespace Storloom.App;

internal static class Ui {
    public static readonly Color Canvas = Color.FromArgb(245, 247, 249);
    public static readonly Color Ink = Color.FromArgb(32, 43, 53);
    public static readonly Color Muted = Color.FromArgb(91, 107, 119);
    public static readonly Color Accent = Color.FromArgb(16, 105, 112);
    public static readonly Color Line = Color.FromArgb(221, 227, 232);
    public static Font BodyFont => new(L.Language == "zh-CN" ? "Microsoft YaHei UI" : "Segoe UI", 9.5f);
    public static Label Text(string text, bool heading = false) => new() { Text = text,
        AutoSize = true, ForeColor = heading ? Ink : Muted, Margin = new Padding(0, 0, 0, 8),
        Font = heading ? new Font(BodyFont.FontFamily, 17f, FontStyle.Regular) : BodyFont };
    public static Button Button(string key, bool primary = false) {
        var button = new Button { Text = L.T(key), AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(96, 34),
        Padding = new Padding(12, 4, 12, 4), Margin = new Padding(0, 0, 8, 0),
        FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderColor = primary ? Accent : Line },
        BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
        UseVisualStyleBackColor = false };
        if (primary) button.EnabledChanged += (_, _) => {
            button.BackColor = button.Enabled ? Accent : Line;
            button.ForeColor = button.Enabled ? Color.White : Muted;
            button.FlatAppearance.BorderColor = button.Enabled ? Accent : Line;
        };
        return button;
    }
    public static FlowLayoutPanel Flow(bool right = false) => new() { AutoSize = true,
        Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty,
        FlowDirection = right ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
    public static TableLayoutPanel Grid(params RowStyle[] rows) {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent };
        foreach (var row in rows) grid.RowStyles.Add(row);
        grid.ColumnStyles.Add(new(SizeType.Percent, 100)); return grid;
    }
    public static RowStyle Auto() => new(SizeType.AutoSize);
    public static RowStyle Fill() => new(SizeType.Percent, 100);
    public static TextBox Input() => new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 8, 4) };
    public static BufferedListView List(params (string Key, int Width)[] columns) {
        var list = new BufferedListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HideSelection = false, ShowItemToolTips = true, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White, ForeColor = Ink, Margin = Padding.Empty };
        foreach (var (key, width) in columns) list.Columns.Add(L.T(key), width);
        list.Resize += (_, _) => {
            if (list.Columns.Count == 0) return;
            int remainder = list.Columns.Cast<ColumnHeader>().Skip(1).Sum(c => c.Width);
            list.Columns[0].Width = Math.Max(160, list.ClientSize.Width - remainder - 25);
        };
        return list;
    }
    public static void Row(ListView list, object tag, params string[] values) =>
        list.Items.Add(new ListViewItem(values) { Tag = tag, ToolTipText = string.Join(Environment.NewLine, values) });
    public static string Size(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:N2} GiB" :
        bytes >= 1048576 ? $"{bytes / 1048576d:N1} MiB" : bytes >= 1024 ? $"{bytes / 1024d:N1} KiB" : $"{bytes} B";
    public static string Folder(IWin32Window owner, string initial = "") {
        using var dialog = new FolderBrowserDialog { InitialDirectory = initial, UseDescriptionForTitle = true };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : "";
    }
}

internal sealed class BufferedListView : ListView {
    private readonly ImageList spacing = new() { ImageSize = new Size(1, 27), ColorDepth = ColorDepth.Depth32Bit };
    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint handle, uint message, nint mask, nint value);
    public BufferedListView() { DoubleBuffered = true; SmallImageList = spacing; }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        _ = SendMessageW(Handle, 0x1036, 0x10000, 0x10000); // LVS_EX_DOUBLEBUFFER
    }
    protected override void Dispose(bool disposing) { if (disposing) spacing.Dispose(); base.Dispose(disposing); }
}

internal sealed class ThrottledProgress(Action<Storloom.Core.ScanProgress> update) : IProgress<Storloom.Core.ScanProgress> {
    private readonly SynchronizationContext context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private long last = Environment.TickCount64 - 200;
    public void Report(Storloom.Core.ScanProgress value) {
        long before = Interlocked.Read(ref last), now = Environment.TickCount64;
        if (now - before < 150 || Interlocked.CompareExchange(ref last, now, before) != before) return;
        context.Post(_ => update(value), null);
    }
}
