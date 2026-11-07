using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Storloom.App;

internal static class TableInteraction {
    public static void Configure(DataGrid table) {
        var original = table.Columns.ToDictionary(column => column, column => column.Width);
        foreach (var column in table.Columns) {
            var menu = new ContextMenu();
            void Command(string key, Action action) {
                var item = new MenuItem { Header = L.T(key) }; item.Click += (_, _) => action(); menu.Items.Add(item);
            }
            Command("FitColumn", () => column.Width = DataGridLength.Auto);
            Command("FitAllColumns", () => { foreach (var entry in table.Columns.Where(c => c.CanUserResize)) entry.Width = DataGridLength.Auto; });
            menu.Items.Add(new Separator());
            Command("ResetColumns", () => { foreach (var entry in original) entry.Key.Width = entry.Value; });
            var headerStyle = new Style(typeof(DataGridColumnHeader), (Style)Application.Current.FindResource(typeof(DataGridColumnHeader)));
            headerStyle.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, menu)); column.HeaderStyle = headerStyle;
            if (column is DataGridBoundColumn { Binding: Binding binding }) {
                var cellStyle = new Style(typeof(DataGridCell), (Style)Application.Current.FindResource(typeof(DataGridCell)));
                cellStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(binding.Path.Path)));
                cellStyle.Setters.Add(new Setter(ToolTipService.InitialShowDelayProperty, 400));
                cellStyle.Setters.Add(new Setter(ToolTipService.ShowDurationProperty, 30000)); column.CellStyle = cellStyle;
            }
        }
    }
    public static void EnableChecks(DataGrid table, string property) => table.PreviewMouseDoubleClick += (_, args) => {
        if (args.ChangedButton != System.Windows.Input.MouseButton.Left || args.OriginalSource is not DependencyObject source) return;
        var row = Ancestor<DataGridRow>(source); if (row == null || ItemsControl.ItemsControlFromItemContainer(row) != table) return;
        // The first checkbox click has already toggled it. Suppress its second click.
        if (Ancestor<CheckBox>(source) == null) {
            var selected = row.Item.GetType().GetProperty(property);
            if (selected?.PropertyType != typeof(bool) || !selected.CanWrite) return;
            selected.SetValue(row.Item, !(bool)selected.GetValue(row.Item)!);
            foreach (var check in Descendants(row).OfType<CheckBox>()) check.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }
        row.IsSelected = true; args.Handled = true;
    };
    private static T? Ancestor<T>(DependencyObject source) where T : DependencyObject {
        for (DependencyObject? current = source; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent) {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
