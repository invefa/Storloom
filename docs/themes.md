# Themes and layout

Storloom's desktop frontend uses WPF + WPF UI. Theme resources cover the application title bar, navigation, controls, menus, dialogs, text selection, and scrollbars. Windows-owned folder/file pickers, UAC, and OS shell surfaces follow Windows settings.

The built-in Light theme is grayscale. Dark (OneDark) uses a dark blue-gray palette. Open Tools > Preferences to select a theme, reduce animation, or choose an optional private JSON theme file. The path belongs in local preferences, not public rule files. Clear it to return to the built-in palette. Never load external XAML or executable code as a theme.

A theme file overrides any subset of these colors with six-digit `#RRGGBB` strings:

```json
{
  "Canvas": "#f6f6f6",
  "Surface": "#ffffff",
  "Sidebar": "#eeeeee",
  "Ink": "#242424",
  "Muted": "#686868",
  "Line": "#dddddd",
  "Accent": "#252525",
  "OnAccent": "#ffffff",
  "Selection": "#dddddd",
  "Hover": "#e8e8e8",
  "Danger": "#4b4b4b"
}
```

Keep foreground/background combinations readable. Unknown keys and invalid colors are rejected. Theme switching updates resources dynamically; applying preferences rebuilds the current pages and invalidates any pending cleanup scan. Re-scan before confirming an operation.

The menus share the title bar. Buttons, inputs, and drop-downs use a compact 32-DIP height. Drag the sidebar divider (10-DIP hit area around a 1-DIP line) or the log's upper divider to resize. Sizes are saved in private preferences. Manual log scrolling, navigation keys, or text selection pause following. The Follow latest checkbox or Ctrl+Shift+L toggles following; enabling it returns to the newest line. Appending logs does not take keyboard focus. The live tail is trimmed only while following; structured operation journals remain available separately in local data.

Navigation changes immediately and menus open without popup animation. Hover/press feedback animates backgrounds only (65–100 ms), leaving text opaque and untransformed. Progress and log-panel transitions take approximately 120–150 ms. Reduce motion removes application transitions; the reader also honors the operating system's reduced-motion preference.
