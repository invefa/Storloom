using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Storloom.App;

public static class ControlFeedback {
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ControlFeedback), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached("Pressed", typeof(bool), typeof(ControlFeedback), new PropertyMetadata(false));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) {
        if (target is not Control control || args.NewValue is not true) return;
        control.Loaded += (_, _) => Paint(control, false);
        control.MouseEnter += (_, _) => Paint(control, true);
        control.MouseLeave += (_, _) => { control.SetValue(PressedProperty, false); Paint(control, true); };
        control.PreviewMouseLeftButtonDown += (_, _) => { control.SetValue(PressedProperty, true); Paint(control, true); };
        control.PreviewMouseLeftButtonUp += (_, _) => { control.SetValue(PressedProperty, false); Paint(control, true); };
        control.PreviewKeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { control.SetValue(PressedProperty, true); Paint(control, true); } };
        control.PreviewKeyUp += (_, e) => { if (e.Key is Key.Space or Key.Enter) { control.SetValue(PressedProperty, false); Paint(control, true); } };
        control.IsEnabledChanged += (_, _) => Paint(control, false);
        control.IsKeyboardFocusWithinChanged += (_, _) => { control.SetValue(PressedProperty, false); Paint(control, false); };
        if (control is ToggleButton toggle) { toggle.Checked += (_, _) => Paint(control, true); toggle.Unchecked += (_, _) => Paint(control, true); }
        if (control is ListBoxItem item) { item.Selected += (_, _) => Paint(control, true); item.Unselected += (_, _) => Paint(control, true); }
    }
    public static void Refresh(DependencyObject root) {
        if (root is Control control && GetEnabled(control)) Paint(control, false);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Refresh(VisualTreeHelper.GetChild(root, i));
    }
    private static Border? Frame(DependencyObject root, string name) {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Border border && border.Name == name) return border;
            if (Frame(child, name) is Border nested) return nested;
        }
        return null;
    }
    private static Color Color(string key) => ((SolidColorBrush)Theme.Brush(key)).Color;
    private static void Paint(Control control, bool animate) {
        if (!GetEnabled(control)) return;
        string name = control is CheckBox ? "box" : control is ListBoxItem ? "item" : "frame";
        var frame = Frame(control, name); if (frame == null) return;
        var color = control is CheckBox check ? Color(check.IsChecked == true ? "Accent" : "Surface") :
            control is ListBoxItem item ? Color(item.IsSelected ? "Selection" : "Sidebar") :
            (control.Background as SolidColorBrush)?.Color ?? Color("Surface");
        bool passiveEditor = control is TextBox editor && (editor.AcceptsReturn || editor.IsReadOnly || editor.IsKeyboardFocusWithin);
        if (passiveEditor) animate = false;
        if (!passiveEditor && control.IsEnabled && (control.IsMouseOver || (bool)control.GetValue(PressedProperty))) {
            double amount = (bool)control.GetValue(PressedProperty) ? .18 : .08;
            var ink = Color("Ink");
            // Filled buttons need a visible state change while preserving foreground contrast.
            if (color == Color("Accent")) ink = Colors.White;
            color = System.Windows.Media.Color.FromRgb((byte)(color.R + (ink.R - color.R) * amount),
                (byte)(color.G + (ink.G - color.G) * amount), (byte)(color.B + (ink.B - color.B) * amount));
        }
        Color previous = (frame.Background as SolidColorBrush)?.Color ?? color;
        var brush = new SolidColorBrush(color); frame.Background = brush;
        if (animate && !Theme.ReduceMotion && SystemParameters.ClientAreaAnimation)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(previous, color, TimeSpan.FromMilliseconds((bool)control.GetValue(PressedProperty) ? 65 : 100)));
    }
}
