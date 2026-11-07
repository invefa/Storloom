using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Storloom.App;

internal static class HorizontalWheel {
    public static void Attach(Window window) => window.SourceInitialized += (_, _) => {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source == null) return;
        HwndSourceHook hook = (nint hwnd, int message, nint wParam, nint lParam, ref bool handled) => {
            if (message != 0x020e || !window.IsVisible) return 0;
            int delta = unchecked((short)(wParam.ToInt64() >> 16)); if (delta == 0) return 0;
            var screen = new Point(unchecked((short)lParam.ToInt64()), unchecked((short)(lParam.ToInt64() >> 16)));
            var hit = window.InputHitTest(window.PointFromScreen(screen)) as DependencyObject;
            for (var current = hit; current != null; current = Parent(current)) {
                if (current is not ScrollViewer viewer || viewer.ScrollableWidth <= 0) continue;
                if (!SystemParametersInfo(0x006c, 0, out uint characters, 0)) characters = 3;
                if (characters == 0) return 0;
                double characterWidth = new FormattedText("0", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(viewer.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    viewer.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(viewer).PixelsPerDip).WidthIncludingTrailingWhitespace;
                double distance = characters == uint.MaxValue ? viewer.ViewportWidth : characters * characterWidth;
                viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + delta / 120d * distance);
                for (var parent = Parent(viewer); parent != null; parent = Parent(parent))
                    if (parent is OperationPanel operation) { operation.PauseFollowing(); break; }
                handled = true; break;
            }
            return 0;
        };
        source.AddHook(hook); window.Closed += (_, _) => source.RemoveHook(hook);
    };
    private static DependencyObject? Parent(DependencyObject current) => current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
