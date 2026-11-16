namespace Storloom.Wpf;

public enum ComponentPalette { Light, Dark, OneDark }

/// <summary>Optional application-wide defaults. Hosts may override individual brush resources.</summary>
public static class ComponentTheme {
    public static bool ReduceMotion => Application.Current?.TryFindResource("ReduceMotion") is true;
    public static Brush Brush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    public static void SetReduceMotion(bool value) {
        if (Application.Current is not { } app) throw new InvalidOperationException("Create the WPF Application first.");
        app.Resources["ReduceMotion"] = value;
    }
    public static void Apply(ComponentPalette palette = ComponentPalette.Light, bool reduceMotion = false) {
        if (Application.Current is not { } app) throw new InvalidOperationException("Create the WPF Application first.");
        string[] keys = ["Canvas", "Surface", "Sidebar", "Ink", "Muted", "Line", "Accent", "OnAccent", "Selection", "Hover", "Danger", "TextSelection", "SelectedText", "AccentText", "ControlBorder"];
        string[] colors = palette switch {
            ComponentPalette.Dark => ["#202020", "#2a2a2a", "#191919", "#eeeeee", "#b5b5b5", "#414141", "#dedede", "#202020", "#424242", "#353535", "#ef8d96", "#666666", "#ffffff", "#eeeeee", "#414141"],
            ComponentPalette.OneDark => ["#23272e", "#282c34", "#1e2227", "#c4ccd8", "#aeb8c8", "#343a44", "#426bb7", "#ffffff", "#34445d", "#303640", "#ef8d96", "#4e658a", "#ffffff", "#a5c2ff", "#343a44"],
            _ => ["#f6f6f6", "#ffffff", "#eeeeee", "#242424", "#5d5d5d", "#dddddd", "#252525", "#ffffff", "#dddddd", "#e8e8e8", "#b42318", "#aaaaaa", "#202020", "#252525", "#dddddd"]
        };
        foreach (var (key, color) in keys.Zip(colors)) app.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        Color surface = ((SolidColorBrush)Brush("Surface")).Color, ink = ((SolidColorBrush)Brush("Ink")).Color;
        SolidColorBrush Blend(Color from, double amount) => new(Color.FromRgb((byte)(from.R + (ink.R-from.R)*amount), (byte)(from.G+(ink.G-from.G)*amount), (byte)(from.B+(ink.B-from.B)*amount)));
        app.Resources["SelectionHover"] = Blend(((SolidColorBrush)Brush("Selection")).Color, .09);
        app.Resources["ScrollGrip"] = Blend(surface,.20); app.Resources["ScrollGripHover"] = Blend(surface,.33); app.Resources["ScrollGripDragging"] = Blend(surface,.45);
        SetReduceMotion(reduceMotion);
        foreach (string name in new[] { "Styles.xaml", "CompactControls.xaml" }) {
            var uri = new Uri("/Storloom.Wpf;component/" + name, UriKind.Relative);
            if (!app.Resources.MergedDictionaries.Any(d => d.Source == uri)) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
        }
        foreach (Window window in app.Windows) ControlFeedback.Refresh(window);
    }
}
