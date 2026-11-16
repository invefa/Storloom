# Storloom.Wpf

A reusable Windows WPF library targeting .NET 10. It references only the Windows desktop framework: no Storloom application, archive engine, localization service, WPF-UI, or WebView2 dependency.

## Use in another application

Reference `Storloom.Wpf.csproj`, or create a local NuGet package with:

```powershell
dotnet pack src/Storloom.Wpf/Storloom.Wpf.csproj -c Release -o artifacts/packages
```

After creating the WPF `Application`, call:

```csharp
using Storloom.Wpf;
ComponentTheme.Apply(ComponentPalette.Dark, reduceMotion: false);
var banner = new StatusBanner("Loading", StatusTone.Busy);
// After awaiting background work:
banner.Show("Complete", StatusTone.Success);
```

`StatusBanner` also supports XAML, with bindable `Text`, `Tone`, and `ReduceMotion` dependency properties. Its spinner runs while busy and visible; completion, hiding, unloading, or reduced motion stops it. Run expensive work asynchronously to keep the dispatcher responsive.

## Controls and behaviors

- `AdaptiveTable`: measures populated content, fills the viewport, reduces the widest columns first, and preserves manual resize with horizontal overflow. `FitToViewport()` restores automatic fitting; `UseManualWidths()` suspends fitting.
- `ISelectableRow` / `TableSelection`: aggregate none/all/partial states for the current filtered view, plus invert selection. `ComponentVisuals.AddSelectionColumn(table)` installs the bound checkbox column. Pass translated select-all text into `TableSelection`; `TableInteraction.Configure(table, translate)` accepts translated header menu labels.
- `ControlFeedback`: reusable attached mouse/keyboard feedback, with reduced-motion support.
- `ComponentVisuals.Icon`: vector path icons without font glyph dependencies.
- Resource dictionaries: standard buttons, text/password inputs, checkboxes, tables, tabs, expanders, scrollbars and menus. The partial-selection mark is an inner rounded square.

All user-facing status text comes from the host. Selection rows need only implement `ISelectableRow`; no application model is required. Modify bound rows and control state on the UI dispatcher. Keep one `TableSelection` per table.

## Themes

`ComponentTheme.Apply` provides Light, Dark, and OneDark palettes. It installs implicit WPF styles application-wide, so call it deliberately. Hosts can override brush resources (`Canvas`, `Surface`, `Ink`, `Muted`, `Line`, `Accent`, `OnAccent`, `Selection`, `Hover`, `Danger`, and related text/scroll tokens) after applying. `ComponentTheme.SetReduceMotion(true)` updates existing status indicators. `StatusBanner.ReduceMotion` can override this per instance. No client theme or business settings are read.

For an existing theme system, merge `/Storloom.Wpf;component/Styles.xaml` followed by `/Storloom.Wpf;component/CompactControls.xaml` and provide the same resource tokens, as the Storloom host does. Native title bars belong to the host.

## Independent demo

```powershell
dotnet run --project samples/ComponentDemo
dotnet run --project samples/ComponentDemo -- --smoke
```

The demo references only this library. It demonstrates theme switching, editable inputs, three-state selection, adaptive columns, and animated status feedback. The smoke mode verifies dependency isolation, selection aggregation, animation progress and reduced motion.

See [README.zh-CN.md](README.zh-CN.md) for Chinese instructions. MIT license, as in the repository root.
