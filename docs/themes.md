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
  "Muted": "#5d5d5d",
  "Line": "#dddddd",
  "Accent": "#252525",
  "OnAccent": "#ffffff",
  "Selection": "#dddddd",
  "Hover": "#e8e8e8",
  "Danger": "#4b4b4b",
  "TextSelection": "#aaaaaa",
  "SelectedText": "#202020",
  "AccentText": "#252525",
  "ControlBorder": "#888888"
}
```

Keep foreground/background combinations readable. Unknown keys and invalid colors are rejected. Theme switching updates resources dynamically; applying preferences rebuilds the current pages and invalidates any pending cleanup scan. Re-scan before confirming an operation.

The menus share the title bar. Buttons, inputs, and drop-downs use a compact 32-DIP height. Drag the sidebar divider (10-DIP hit area around a 1-DIP line) or the log's upper divider to resize. Sizes are saved in private preferences. Manual log scrolling, navigation keys, or text selection pause following. The Follow latest checkbox or Ctrl+Shift+L toggles following; enabling it returns to the newest line. Appending logs does not take keyboard focus. The live tail is trimmed only while following; structured operation journals remain available separately in local data.

Navigation changes immediately and menus open without popup animation. Multiline and read-only text fields have no hover/press animation; focused editors keep a stable background. Hover/press feedback on other controls animates backgrounds only (65–100 ms), leaving text opaque and untransformed. Progress and log-panel transitions take approximately 120–150 ms. Reduce motion removes application transitions; the reader also honors the operating system's reduced-motion preference.

Sidebar transitions keep icon positions fixed. The log divider is constrained to the visible window and reserves room for the current page. Long unwrapped logs have separate horizontal and vertical scrollbars. Enable Close button → Hide to the notification area in Preferences to keep tasks running with the window hidden; the tray menu restores the window or exits. Exiting during a task requires confirmation and waits for cancellation to finish.

Confirmation and information notices use compact, non-resizable windows sized to their content, with a bounded scrolling message area for lengthy details and actions at the bottom right. Chat backup and result forms label account, raw-backup and reading-copy paths on the left with consistent row spacing. Custom scrollbar tracks and thumbs share the same minimum-size metrics so long documents do not clip the thumb at either end.

Built-in palettes separate accent fills from accent-colored text and control outlines. Text pairs are checked at a contrast ratio of at least 4.5, with input outlines at least 3 against their surfaces. Primary-button press feedback moves the background away from its foreground color. Reader checks cover Default, WeChat and QQ in light/dark modes, including secondary text in sent-message bubbles and placeholders on their own backgrounds. Custom overrides remain user-defined; the built-in contrast checks do not certify arbitrary custom palettes.
