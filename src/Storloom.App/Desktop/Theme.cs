using System.Windows.Media.Animation;
using Storloom.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace Storloom.App;

public static class Theme {
    public static AppTheme Current { get; private set; }
    public static bool ReduceMotion { get; private set; }
    public static bool IsDark => Current is AppTheme.Dark or AppTheme.OneDark;
    private static readonly string[] names = ["Canvas", "Surface", "Sidebar", "Ink", "Muted", "Line", "Accent", "OnAccent", "Selection", "Hover", "Danger", "TextSelection", "SelectedText", "AccentText", "ControlBorder"];
    public static void Apply(AppPreferences preferences) {
        Current = preferences.Theme; ReduceMotion = preferences.ReduceMotion;
        var resources = Application.Current.Resources;
        // Track uses half these metrics as its minimum thumb length. Match the 24-DIP templates.
        resources[SystemParameters.VerticalScrollBarButtonHeightKey] = 48d;
        resources[SystemParameters.HorizontalScrollBarButtonWidthKey] = 48d;
        if (!resources.MergedDictionaries.OfType<ControlsDictionary>().Any()) {
            resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
            resources.MergedDictionaries.Add(new ControlsDictionary());
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Storloom;component/Desktop/Styles.xaml", UriKind.Relative) });
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Storloom;component/Desktop/CompactControls.xaml", UriKind.Relative) });
        }
        ApplicationThemeManager.Apply(IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.None, false);
        string[] colors = Current == AppTheme.Dark ?
            ["#202020", "#2a2a2a", "#191919", "#eeeeee", "#b5b5b5", "#414141", "#dedede", "#202020", "#424242", "#353535", "#eeeeee", "#666666", "#ffffff", "#eeeeee", "#414141"] : Current == AppTheme.OneDark ?
            ["#23272e", "#282c34", "#1e2227", "#c4ccd8", "#aeb8c8", "#3b414d", "#426bb7", "#ffffff", "#34445d", "#303640", "#ef8d96", "#4e658a", "#ffffff", "#a5c2ff", "#3b414d"] :
            ["#f6f6f6", "#ffffff", "#eeeeee", "#242424", "#5d5d5d", "#dddddd", "#252525", "#ffffff", "#dddddd", "#e8e8e8", "#4b4b4b", "#aaaaaa", "#202020", "#252525", "#dddddd"];
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
        Map("Line", "SurfaceStrokeColorDefault", "DividerStrokeColorDefault", "CardStrokeColorDefault", "CardStrokeColorDefaultSolid");
        Map("ControlBorder", "ControlStrokeColorDefault", "ControlStrokeColorSecondary");
        Map("Accent", "SystemAccentColor", "SystemAccentColorPrimary", "SystemAccentColorSecondary", "SystemAccentColorTertiary", "AccentFillColorDefault");
        Map("AccentText", "AccentTextFillColorPrimary", "AccentTextFillColorSecondary", "KeyboardFocusBorderColor", "FocusStrokeColorOuter");
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
