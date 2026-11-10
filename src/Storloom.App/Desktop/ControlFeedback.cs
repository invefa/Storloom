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
        control.IsEnabledChanged += (_, _) => { if (!control.IsEnabled) control.SetValue(PressedProperty, false); Paint(control, false); };
        control.IsKeyboardFocusWithinChanged += (_, _) => {
            if (control is TextBox || !control.IsKeyboardFocusWithin) control.SetValue(PressedProperty, false);
            Paint(control, control is not TextBox);
        };
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
            if (color.A == 0) color = ParentColor(control);
            double amount = (bool)control.GetValue(PressedProperty) ? .18 : .08;
            var ink = Color("Ink");
            // Filled buttons need a visible state change while preserving foreground contrast.
            if (color == Color("Accent")) {
                amount=(bool)control.GetValue(PressedProperty) ? .28 : .16;
                // Light fills darken noticeably; very dark fills brighten. The built-in
                // foreground pairs remain readable in both hover and pressed states.
                ink = color.R * 299 + color.G * 587 + color.B * 114 >= 64000 ? Colors.Black : Colors.White;
            }
            color = System.Windows.Media.Color.FromRgb((byte)(color.R + (ink.R - color.R) * amount),
                (byte)(color.G + (ink.G - color.G) * amount), (byte)(color.B + (ink.B - color.B) * amount));
        }
        Color previous = (frame.Background as SolidColorBrush)?.Color ?? color;
        // Transparent white interpolates through bright RGB values before it becomes opaque.
        // Fade the target hue in/out, and attach the brush only after its clock is initialized.
        if (previous.A == 0) previous = System.Windows.Media.Color.FromArgb(0, color.R, color.G, color.B);
        if (color.A == 0) color = System.Windows.Media.Color.FromArgb(0, previous.R, previous.G, previous.B);
        var brush = new SolidColorBrush(color);
        if (animate && !Theme.ReduceMotion && SystemParameters.ClientAreaAnimation)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(previous, color, TimeSpan.FromMilliseconds((bool)control.GetValue(PressedProperty) ? 65 : 100)));
        frame.Background = brush;
    }
    private static Color ParentColor(DependencyObject control) {
        for (var parent = VisualTreeHelper.GetParent(control); parent != null; parent = VisualTreeHelper.GetParent(parent)) {
            var brush = parent switch { Panel panel => panel.Background, Border border => border.Background, Control item => item.Background, _ => null };
            if (brush is SolidColorBrush { Color.A: > 0 } solid) return solid.Color;
        }
        return Color("Canvas");
    }
}
