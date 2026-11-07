using System.Windows.Media.Animation;
using Storloom.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace Storloom.App;

public static class Theme {
    public static AppTheme Current { get; private set; }
    public static bool ReduceMotion { get; private set; }
    private static readonly string[] names = ["Canvas", "Surface", "Sidebar", "Ink", "Muted", "Line", "Accent", "OnAccent", "Selection", "Hover", "Danger", "TextSelection", "SelectedText"];
    public static void Apply(AppPreferences preferences) {
        Current = preferences.Theme; ReduceMotion = preferences.ReduceMotion;
        var resources = Application.Current.Resources;
        if (!resources.MergedDictionaries.OfType<ControlsDictionary>().Any()) {
            resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
            resources.MergedDictionaries.Add(new ControlsDictionary());
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Storloom;component/Desktop/Styles.xaml", UriKind.Relative) });
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Storloom;component/Desktop/CompactControls.xaml", UriKind.Relative) });
        }
        ApplicationThemeManager.Apply(Current == AppTheme.OneDark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.None, false);
        string[] colors = Current == AppTheme.OneDark ?
            ["#23272e", "#282c34", "#1e2227", "#abb2bf", "#9098a7", "#3b414d", "#4d78cc", "#ffffff", "#34445d", "#303640", "#e06c75", "#4e658a", "#ffffff"] :
            ["#f6f6f6", "#ffffff", "#eeeeee", "#242424", "#686868", "#dddddd", "#252525", "#ffffff", "#dddddd", "#e8e8e8", "#4b4b4b", "#aaaaaa", "#202020"];
        var palette = names.Zip(colors).ToDictionary(pair => pair.First, pair => (Color)ColorConverter.ConvertFromString(pair.Second));
        if (!string.IsNullOrWhiteSpace(preferences.CustomThemeFile)) {
            foreach (var pair in ReadCustom(preferences.CustomThemeFile)) palette[pair.Key] = pair.Value;
        }
        foreach (var pair in palette) { resources[pair.Key] = new SolidColorBrush(pair.Value); resources[pair.Key + "Color"] = pair.Value; }
        // Keep library templates and Windows caption controls on the same resource palette.
        void Map(string color, params string[] keys) {
            foreach (string key in keys) {
                resources[key] = palette[color];
                resources[key + "Brush"] = new SolidColorBrush(palette[color]);
            }
        }
        Map("Canvas", "ApplicationBackgroundColor", "SolidBackgroundFillColorBase", "LayerFillColorDefault", "ControlAltFillColorTertiary");
        Map("Surface", "ControlSolidFillColorDefault", "ControlFillColorDefault", "ControlFillColorInputActive", "CardBackgroundFillColorDefault", "SolidBackgroundFillColorSecondary", "LayerFillColorAlt", "FlyoutBackgroundColor");
        Map("Hover", "ControlFillColorSecondary", "SubtleFillColorSecondary", "ControlAltFillColorSecondary");
        Map("Selection", "ControlFillColorTertiary", "SubtleFillColorTertiary", "AccentFillColorSecondary");
        Map("Ink", "TextFillColorPrimary", "ControlStrongFillColorDefault");
        Map("Muted", "TextFillColorSecondary", "TextFillColorTertiary", "TextFillColorDisabled", "TextPlaceholderColor");
        Map("Line", "ControlStrokeColorDefault", "ControlStrokeColorSecondary", "SurfaceStrokeColorDefault", "DividerStrokeColorDefault", "CardStrokeColorDefault", "CardStrokeColorDefaultSolid");
        Map("Accent", "SystemAccentColor", "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorTertiary", "AccentFillColorDefault", "AccentTextFillColorPrimary", "AccentTextFillColorSecondary", "KeyboardFocusBorderColor", "FocusStrokeColorOuter");
        Map("OnAccent", "TextOnAccentFillColorPrimary", "TextOnAccentFillColorSecondary", "TextOnAccentFillColorSelectedText");
        resources["ApplicationBackgroundBrush"] = resources["Canvas"];
        resources["ApplicationForegroundBrush"] = resources["Ink"];
        resources[SystemColors.WindowBrushKey] = resources["Surface"];
        resources[SystemColors.ControlBrushKey] = resources["Canvas"];
        resources[SystemColors.WindowTextBrushKey] = resources["Ink"];
        resources[SystemColors.ControlTextBrushKey] = resources["Ink"];
        resources[SystemColors.HighlightBrushKey] = resources["Selection"];
        resources[SystemColors.HighlightTextBrushKey] = resources["Ink"];
        foreach (var window in Application.Current.Windows.OfType<ThemeWindow>()) { window.UpdateFrame(); ControlFeedback.Refresh(window); }
    }
    public static Dictionary<string, Color> ReadCustom(string path) {
        var input = JsonFiles.Read<Dictionary<string, string>>(path);
        if (input.Keys.Any(k => !names.Contains(k))) throw new InvalidDataException("Unknown theme color name.");
        var result = new Dictionary<string, Color>();
        foreach (var pair in input) {
            if (!System.Text.RegularExpressions.Regex.IsMatch(pair.Value, "^#[a-fA-F0-9]{6}$"))
                throw new InvalidDataException("Theme colors must use #RRGGBB.");
            result[pair.Key] = (Color)ColorConverter.ConvertFromString(pair.Value);
        }
        return result;
    }
    public static void Enter(FrameworkElement element) {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        // Never composite text through a translucent page: ClearType loses its stable background.
        element.Opacity = 1;
    }
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
