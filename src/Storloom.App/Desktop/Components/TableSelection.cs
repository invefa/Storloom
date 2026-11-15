using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls.Primitives;

namespace Storloom.App;

internal interface ISelectableRow : INotifyPropertyChanged {
    bool Selected { get; set; }
}

// Owns aggregate selection for the current view, including filtered rows and ItemsSource replacements.
internal sealed class TableSelection {
    private readonly DataGrid table;
    private readonly HashSet<ISelectableRow> observed = [];
    public CheckBox Header { get; }
    public bool IsChanging { get; private set; }
    public event Action? SelectionChanged;

    public TableSelection(DataGrid table, string name) {
        this.table = table;
        Header = new CheckBox { Name = name, Margin = new Thickness(0), Padding = new Thickness(0), MinWidth = 0,
            Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L.T("SelectAll"), IsThreeState = false };
        System.Windows.Automation.AutomationProperties.SetName(Header, L.T("SelectAll"));
        var style = new Style(typeof(DataGridColumnHeader), (Style)Application.Current.FindResource(typeof(DataGridColumnHeader)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        table.Columns[0].HeaderStyle = style; table.Columns[0].Header = Header;
        Header.Click += (_, _) => Set(!table.Items.OfType<ISelectableRow>().All(row => row.Selected));
        ((INotifyCollectionChanged)table.Items).CollectionChanged += (_, _) => ObserveRows();
        ObserveRows();
    }
    private void ObserveRows() {
        foreach (var row in observed) PropertyChangedEventManager.RemoveHandler(row, RowChanged, nameof(ISelectableRow.Selected));
        observed.Clear();
        foreach (var row in table.Items.OfType<ISelectableRow>()) {
            observed.Add(row); PropertyChangedEventManager.AddHandler(row, RowChanged, nameof(ISelectableRow.Selected));
        }
        Refresh(); SelectionChanged?.Invoke();
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs args) {
        if (IsChanging) return;
        Refresh(); SelectionChanged?.Invoke();
    }
    public void Refresh() {
        var rows = table.Items.OfType<ISelectableRow>().ToArray(); int selected = rows.Count(row => row.Selected);
        Header.IsEnabled = rows.Length > 0;
        Header.IsChecked = selected == 0 ? false : selected == rows.Length ? true : null;
    }
    public void Set(bool? selected) {
        IsChanging = true;
        try { foreach (var row in table.Items.OfType<ISelectableRow>()) row.Selected = selected ?? !row.Selected; }
        finally { IsChanging = false; Refresh(); SelectionChanged?.Invoke(); }
    }
    public void Invert() => Set(null);
}
