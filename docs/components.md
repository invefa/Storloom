# Desktop components

Open **Tools > Component library** for an interactive WPF gallery with example data. The gallery uses the same controls as the application and follows the global theme. It does not scan or delete files.

The first component library stays inside the desktop project: shared templates in `Desktop/CompactControls.xaml`, layout/control factories in `Desktop/View.cs`, and behavior components in `Desktop/Components`. Feature pages compose these controls instead of drawing their own selection indicators or duplicating bulk-selection behavior.

## Visual rules

- Resolve colors through theme resources: Canvas, Surface, Ink, Muted, Accent, OnAccent, Selection, Danger and ControlBorder. Do not bake a palette into an individual window.
- Use the existing compact controls, 32-DIP editor/action height and spacing in multiples of four. Keep labels aligned. Let narrow layouts wrap actions and scroll their content.
- Checkbox marks are vector shapes: an empty box for none, a check for all, and a centered rounded inset square for partial selection, using OnAccent for contrast. Use both shape and color to communicate state.
- A checkbox-only table column is 28 DIPs wide; its header has zero text padding. The 16-DIP glyph sits inside a centered 20-DIP control. Labels without content do not reserve space.
- Keep keyboard focus visible. Use action labels/tooltips and automation names for icon-only controls. A disabled action remains visibly distinct.

## Reusable behavior

`TableSelection` attaches to a table's checkbox column. Rows implement `ISelectableRow` (`Selected` plus `INotifyPropertyChanged`). It observes row changes and view resets, calculates the aggregate state and supplies `Set(true)`, `Set(false)` and `Invert()`. Header clicks and Space alternate between selecting all visible rows and clearing them; partial selection clicks select all. Filtering changes the aggregate state and operation scope while retaining hidden rows' choices. Replacing the source detaches old row notifications. Bulk operations raise one selection notification after completion.

Cleanup uses separate instances for rule selection and file selection. The gallery uses the same component with synthetic rows. Business logic owns scan targets, excluded file paths and execution authorization; the component only changes UI selection.

`AdaptiveTable` is used by `View.Table`. After population/display it measures native cell/header templates and samples up to 256 evenly spaced rows, including the last row, without realizing their controls. It fills the viewport, reduces the longest columns first and preserves short columns and minimum widths. Infeasible minimum widths allow scrolling. Dragging a column or explicitly fitting individual columns switches to manual widths with horizontal overflow; resizing or revisiting the same view retains them. A new ItemsSource re-enables automatic fitting; Reset columns also restores automatic viewport fitting. Sampling bounds opening cost for very large scans; an unsampled off-screen outlier may still be truncated and is available through its tooltip/manual resizing.

`StatusBanner` provides information, busy, success and error states through `Show(text, tone)`. It uses vector icons, shared palette resources, an indeterminate progress bar only while busy, and a polite automation live region. It does not execute work, change pages or clear input.

`View.Button`, `View.IconButton`, `View.Input`, `View.SecretInput`, `View.Field`, `View.Table`, `View.Card` and `View.FormScroll` remain the common building blocks. Prefer these and existing WPF templates before creating another wrapper. Put new shared behavior in the components folder when multiple features need it.

## Review

The gallery covers actions, input fields, three-state selection, filtered table selection, progress, completion and validation. UI tests verify selection transitions, filtering, detached rows and header glyph bounds in Light, Dark and OneDark. Screenshots use synthetic data only. Actual scan and cleanup tests additionally verify that scope selection does not bypass processing review or remove excluded files.
