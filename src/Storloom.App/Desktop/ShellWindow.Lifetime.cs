using System.ComponentModel;
using Storloom.Core;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private DesktopTray? tray;
    private bool exitRequested, exitAfterOperation;
    private void EnsureTray() => tray ??= new DesktopTray(RestoreFromTray, () => Guard(RequestExit));
    private void RestoreFromTray() {
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
    private void HideToTray() { EnsureTray(); Hide(); }
    private void RequestExit() { exitRequested = true; Close(); }
    private void HandleClosing(object? sender, CancelEventArgs e) {
        if (!exitRequested && preferences.CloseToTray) {
            e.Cancel = true;
            if (operation == null || Confirm(L.T("ConfirmBackgroundTask"))) HideToTray();
            return;
        }
        if (operation != null) {
            e.Cancel = true;
            if (!exitAfterOperation && Confirm(L.T("ConfirmExitTask"))) {
                exitAfterOperation = exitRequested = true; RequestOperationStop();
            } else if (!exitAfterOperation) exitRequested = false;
            return;
        }
        SetCapturedKey(null);
        preferences = preferences with {
            SidebarWidth = preferences.SidebarCollapsed || sidebarAnimating || sidebarColumn==null ? preferences.SidebarWidth : sidebarColumn.ActualWidth
        };
        JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences);
    }
}

internal sealed class DesktopTray : IDisposable {
    private readonly System.Windows.Forms.NotifyIcon icon;
    private readonly System.Windows.Forms.ContextMenuStrip menu = new();
    private readonly System.Drawing.Icon image;
    private readonly Action show, exit;
    public DesktopTray(Action show, Action exit) {
        this.show = show; this.exit = exit;
        using var stream = typeof(ProductInfo).Assembly.GetManifestResourceStream("Storloom.App.Assets.storloom.ico")!;
        using var source = new System.Drawing.Icon(stream);
        image = (System.Drawing.Icon)source.Clone();
        icon = new() { Icon = image, Text = "Storloom", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => show(); Refresh();
    }
    public void Refresh() {
        foreach (var item in menu.Items.Cast<System.Windows.Forms.ToolStripItem>().ToArray()) item.Dispose();
        menu.Items.Clear(); menu.ShowImageMargin = false;
        var ink = ((SolidColorBrush)Theme.Brush("Ink")).Color;
        var surface = ((SolidColorBrush)Theme.Brush("Surface")).Color;
        menu.ForeColor = System.Drawing.Color.FromArgb(ink.R, ink.G, ink.B);
        menu.BackColor = System.Drawing.Color.FromArgb(surface.R, surface.G, surface.B);
        menu.Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new TrayColors());
        menu.Items.Add(L.T("ShowWindow"), null, (_, _) => show());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Exit"), null, (_, _) => exit());
    }
    public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); image.Dispose(); }
    private sealed class TrayColors : System.Windows.Forms.ProfessionalColorTable {
        private static System.Drawing.Color Color(string key) { var c = ((SolidColorBrush)Theme.Brush(key)).Color; return System.Drawing.Color.FromArgb(c.R, c.G, c.B); }
        public override System.Drawing.Color ToolStripDropDownBackground => Color("Surface");
        public override System.Drawing.Color MenuItemSelected => Color("Hover");
        public override System.Drawing.Color MenuItemBorder => Color("Line");
        public override System.Drawing.Color MenuBorder => Color("Line");
        public override System.Drawing.Color SeparatorDark => Color("Line");
        public override System.Drawing.Color SeparatorLight => Color("Surface");
    }
}
