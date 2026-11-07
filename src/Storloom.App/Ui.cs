using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Storloom.Core.Localization;

namespace Storloom.App;

public enum AppTheme { Light, OneDark }
internal static class Ui {
    public static AppTheme Theme { get; private set; }
    public static bool Dark => Theme == AppTheme.OneDark;
    public static Color Canvas => ColorTranslator.FromHtml(Dark ? "#23272e" : "#f7f7f5");
    public static Color Surface => ColorTranslator.FromHtml(Dark ? "#282c34" : "#ffffff");
    public static Color Sidebar => ColorTranslator.FromHtml(Dark ? "#1e2227" : "#eeeeeb");
    public static Color Ink => ColorTranslator.FromHtml(Dark ? "#abb2bf" : "#242424");
    public static Color Muted => ColorTranslator.FromHtml(Dark ? "#9098a7" : "#6b6b68");
    public static Color Accent => ColorTranslator.FromHtml(Dark ? "#4d78cc" : "#176b64");
    public static Color Line => ColorTranslator.FromHtml(Dark ? "#3b414d" : "#deded9");
    public static Color Selection => ColorTranslator.FromHtml(Dark ? "#34445d" : "#e2eeeb");
    public static Font BodyFont => new(L.Language == "zh-CN" ? "Microsoft YaHei UI" : "Segoe UI", 9.5f);
    public static void SetTheme(AppTheme theme) => Theme = theme;
    public static Label Text(string text, bool heading = false) => new() { Text = text,
        AutoSize = true, Tag = heading ? "heading" : null, ForeColor = heading ? Ink : Muted, Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 0, 8),
        Font = heading ? new Font(BodyFont.FontFamily, 17f) : BodyFont };
    public static Label FieldLabel(string text) { var label = Text(text); label.Margin = Padding.Empty; return label; }
    public static Button Button(string key, bool primary = false) => new RoundedButton(primary) {
        Text = L.T(key), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(96, 36), Padding = new Padding(12, 5, 12, 5),
        Margin = new Padding(0, 0, 8, 0), Anchor = AnchorStyles.Left,
        FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
        BackColor = primary ? Accent : Surface, ForeColor = primary ? Color.White : Ink };
    public static FlowLayoutPanel Flow(bool right = false) => new BufferedFlow {
        AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty,
        FlowDirection = right ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
    public static TableLayoutPanel Grid(params RowStyle[] rows) {
        var grid = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Canvas };
        foreach (var row in rows) grid.RowStyles.Add(row);
        grid.ColumnStyles.Add(new(SizeType.Percent, 100)); return grid;
    }
    public static RowStyle Auto() => new(SizeType.AutoSize);
    public static RowStyle Fill() => new(SizeType.Percent, 100);
    public static TextBox Input() => new() { Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 4, 8, 4), BackColor = Surface, ForeColor = Ink };
    public static BufferedListView List(params (string Key, int Width)[] columns) {
        var list = new BufferedListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HideSelection = false, ShowGroups = false, ShowItemToolTips = true, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Surface, ForeColor = Ink, Margin = Padding.Empty };
        foreach (var (key, width) in columns) list.Columns.Add(L.T(key), width);
        list.Resize += (_, _) => {
            if (list.Columns.Count == 0) return;
            int remainder = list.Columns.Cast<ColumnHeader>().Skip(1).Sum(c => c.Width);
            int width = Math.Max(120, list.ClientSize.Width - remainder - 1);
            if (list.Columns[0].Width != width) list.Columns[0].Width = width;
        };
        return list;
    }
    public static void Row(ListView list, object tag, params string[] values) =>
        list.Items.Add(new ListViewItem(values) { Tag = tag, ToolTipText = string.Join(Environment.NewLine, values) });
    public static string Size(long bytes) => bytes < 0 ? "-" + Size(checked(-bytes)) : bytes >= 1073741824 ? $"{bytes / 1073741824d:N2} GiB" :
        bytes >= 1048576 ? $"{bytes / 1048576d:N1} MiB" : bytes >= 1024 ? $"{bytes / 1024d:N1} KiB" : $"{bytes} B";
    public static string Folder(IWin32Window owner, string initial = "") {
        using var dialog = new FolderBrowserDialog { InitialDirectory = initial, UseDescriptionForTitle = true };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : "";
    }
    public static void ApplyTheme(Control root) {
        foreach (Control child in root.Controls) ApplyTheme(child);
        if (root is RoundedButton) { root.Invalidate(); return; }
        root.ForeColor = root is Label ? root.Tag as string == "heading" ? Ink : Muted : Ink;
        if (root is TextBoxBase or ListView or CheckedListBox or ComboBox) root.BackColor = Surface;
        else if (root is MenuStrip or StatusStrip) {
            root.BackColor = Surface; ((ToolStrip)root).Renderer = new ThemeRenderer();
            foreach (ToolStripItem item in ((ToolStrip)root).Items) item.ForeColor = Ink;
        } else if (root is Panel or TableLayoutPanel or FlowLayoutPanel or TabPage or TabControl)
            root.BackColor = root.Name == "sidebar" ? Sidebar : Canvas;
        if (root is TabPage page) page.UseVisualStyleBackColor = false;
        if (root is TabControl tabs) {
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.DrawItem -= DrawTab; tabs.DrawItem += DrawTab;
        }
        if (root is ComboBox combo) {
            combo.DrawMode = DrawMode.OwnerDrawFixed; combo.FlatStyle = FlatStyle.Flat;
            combo.DrawItem -= DrawCombo; combo.DrawItem += DrawCombo;
        }
        if (root is Form form && form.IsHandleCreated) { int dark = Dark ? 1 : 0; _ = DwmSetWindowAttribute(form.Handle, 20, ref dark, 4); }
    }
    private static void DrawTab(object? sender, DrawItemEventArgs e) {
        var tabs = (TabControl)sender!; using var brush = new SolidBrush(e.Index == tabs.SelectedIndex ? Surface : Canvas);
        e.Graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text,
            tabs.Font, e.Bounds, Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
    private static void DrawCombo(object? sender, DrawItemEventArgs e) {
        var combo = (ComboBox)sender!; using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Selection : Surface);
        e.Graphics.FillRectangle(brush, e.Bounds);
        string text = (e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) : combo.Text) ?? "";
        TextRenderer.DrawText(e.Graphics, text, combo.Font, Rectangle.Inflate(e.Bounds, -5, 0), Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attr, ref int value, int size);
    private sealed class ThemeRenderer() : ToolStripProfessionalRenderer(new ThemeColors()) {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Ink; base.OnRenderItemText(e); }
    }
    private sealed class ThemeColors : ProfessionalColorTable {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemBorder => Line;
        public override Color MenuItemPressedGradientBegin => Selection;
        public override Color MenuItemPressedGradientEnd => Selection;
    }
}
internal sealed class RoundedButton(bool primary) : Button {
    private bool hover;
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Ui.Canvas);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = new GraphicsPath(); const int diameter = 10;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure();
        using var fill = new SolidBrush(!Enabled ? Ui.Line : primary ? Ui.Accent : hover ? Ui.Selection : BackColor);
        using var pen = new Pen(primary && Enabled ? Ui.Accent : Ui.Line);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(ClientRectangle, -8, -3),
            !Enabled ? Ui.Muted : primary ? Color.White : ForeColor,
            TextFormatFlags.VerticalCenter | (TextAlign == ContentAlignment.MiddleLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), Ui.Ink, Color.Transparent);
    }
}
internal sealed class BufferedPanel : Panel { public BufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }
internal sealed class BufferedTabs : TabControl {
    public BufferedTabs() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Padding = new Point(12, 8);
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(Ui.Canvas);
        for (int i = 0; i < TabCount; i++) {
            var rect = GetTabRect(i); using var brush = new SolidBrush(i == SelectedIndex ? Ui.Surface : Ui.Canvas);
            e.Graphics.FillRectangle(brush, rect); TextRenderer.DrawText(e.Graphics, TabPages[i].Text, Font, rect,
                i == SelectedIndex ? Ui.Ink : Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (i == SelectedIndex) { using var pen = new Pen(Ui.Accent, 2); e.Graphics.DrawLine(pen, rect.Left + 8, rect.Bottom - 2, rect.Right - 8, rect.Bottom - 2); }
        }
    }
}
internal sealed class BufferedGrid : TableLayoutPanel { public BufferedGrid() { DoubleBuffered = true; } }
internal sealed class BufferedFlow : FlowLayoutPanel {
    private bool aligning;
    public BufferedFlow() { DoubleBuffered = true; }
    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        if (aligning || FlowDirection is FlowDirection.TopDown or FlowDirection.BottomUp) return;
        aligning = true;
        try { foreach (Control child in Controls) if (child.Visible) child.Top = Math.Max(0, (ClientSize.Height - child.Height) / 2); }
        finally { aligning = false; }
    }
}
internal sealed class BufferedListView : ListView {
    private readonly ImageList spacing = new() { ImageSize = new Size(1, 26), ColorDepth = ColorDepth.Depth32Bit };
    private bool resizePending;
    public BufferedListView() { DoubleBuffered = true; SmallImageList = spacing; OwnerDraw = true; }
    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) {
        using var brush = new SolidBrush(Ui.Canvas); e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Font, Rectangle.Inflate(e.Bounds, -6, 0), Ui.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
    protected override void OnDrawItem(DrawListViewItemEventArgs e) { }
    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) {
        if (e.Item == null || e.SubItem == null) return;
        var bounds = e.Bounds; using var brush = new SolidBrush(e.Item.Selected ? Ui.Selection : Ui.Surface);
        e.Graphics.FillRectangle(brush, bounds); int inset = 6;
        if (e.ColumnIndex == 0 && CheckBoxes) {
            var box = new Rectangle(bounds.Left + 5, bounds.Top + (bounds.Height - 14) / 2, 14, 14);
            using var fill = new SolidBrush(e.Item.Checked ? Ui.Accent : Ui.Surface); using var pen = new Pen(Ui.Line);
            e.Graphics.FillRectangle(fill, box); e.Graphics.DrawRectangle(pen, box);
            if (e.Item.Checked) { using var check = new Pen(Color.White, 1.8f); e.Graphics.DrawLines(check,
                new Point[] { new(box.Left + 3, box.Top + 7), new(box.Left + 6, box.Top + 10), new(box.Right - 2, box.Top + 3) }); }
            inset = 25;
        }
        bounds.X += inset; bounds.Width -= inset + 4;
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, bounds, Ui.Ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
    protected override void OnResize(EventArgs e) {
        int top = IsHandleCreated && Items.Count > 0 ? TopItem?.Index ?? 0 : 0;
        base.OnResize(e);
        // Reset the native viewport origin after a resize; preserve the user's logical top row.
        if (!IsHandleCreated || resizePending || IsDisposed) return;
        resizePending = true;
        BeginInvoke((Action)(() => { resizePending = false;
            if (IsDisposed || !IsHandleCreated) return;
            if (Items.Count > 0) TopItem = Items[Math.Min(top, Items.Count - 1)]; Invalidate();
        }));
    }
    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e); _ = SetWindowTheme(Handle, Ui.Dark ? "DarkMode_Explorer" : "Explorer", null);
    }
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(nint handle, string subApp, string? subId);
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
