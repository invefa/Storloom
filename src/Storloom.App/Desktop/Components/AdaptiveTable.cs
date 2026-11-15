using System.Collections;
using System.Collections.Specialized;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Data;
using System.Globalization;

namespace Storloom.App;

// Automatic layout belongs to populated views. A user's explicit column resize takes precedence.
internal sealed class AdaptiveTable : DataGrid {
    private bool pending, fitting, manual;
    public AdaptiveTable() {
        SetResourceReference(StyleProperty, typeof(DataGrid));
        Loaded += (_, _) => RequestFit();
        IsVisibleChanged += (_, _) => { if (IsVisible) RequestFit(); };
        SizeChanged += (_, _) => { if (!fitting) RequestFit(); };
        AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, args) => {
            if (args.OriginalSource is DependencyObject source && HeaderAncestor(source)) UseManualWidths();
        }), true);
    }
    protected override void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue) {
        base.OnItemsSourceChanged(oldValue, newValue); manual = false; RequestFit();
    }
    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e) {
        base.OnItemsChanged(e); if (!fitting) RequestFit();
    }
    public void UseManualWidths() => manual = true;
    public void FitToViewport() { manual = false; RequestFit(); }
    private void RequestFit() {
        if (manual || pending || fitting || !IsLoaded || !IsVisible) return;
        pending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            pending = false; if (manual || !IsLoaded || !IsVisible || ActualWidth <= 0) return;
            Fit();
        }));
    }
    private void Fit() {
        var columns = Columns.Where(column => column.Visibility == Visibility.Visible).ToArray();
        if (columns.Length == 0) return;
        fitting = true;
        try {
            // Let WPF measure the actual cell/header templates, including current fonts and DPI.
            foreach (var column in columns.Where(column => column.CanUserResize)) column.Width = DataGridLength.Auto;
            UpdateLayout();
            var scroll = FindScroll(this);
            double viewport = scroll?.ViewportWidth ?? 0;
            if (!double.IsFinite(viewport) || viewport <= 0) viewport = ActualWidth - BorderThickness.Left - BorderThickness.Right;
            var desired = columns.Select(column => {
                double measured = column.Width.DesiredValue;
                if (!double.IsFinite(measured) || measured <= 0) measured = column.ActualWidth;
                measured = Math.Max(measured, SampleContentWidth(column));
                return Math.Clamp(measured, column.MinWidth, column.MaxWidth);
            }).ToArray();
            var widths = Distribute(Math.Max(0, Math.Floor(viewport) - 1), desired,
                columns.Select(c => c.MinWidth).ToArray(), columns.Select(c => c.MaxWidth).ToArray(), columns.Select(c => c.CanUserResize).ToArray());
            for (int i = 0; i < columns.Length; i++) if (columns[i].CanUserResize) columns[i].Width = new DataGridLength(widths[i]);
            UpdateLayout();
            scroll?.ScrollToHorizontalOffset(0);
        } finally { fitting = false; }
    }
    private double SampleContentWidth(DataGridColumn column) {
        if (!column.CanUserResize || column is not DataGridTextColumn { Binding: Binding { Converter: null } binding } ||
            binding.Path?.Path is not string path || path.Contains('.') || Items.Count == 0) return 0;
        // Include distant rows without realizing controls or measuring every file in a large scan.
        int count = Math.Min(Items.Count, 256); var longest = new List<string>();
        var properties = new Dictionary<Type, System.Reflection.PropertyInfo?>();
        for (int sample = 0; sample < count; sample++) {
            int index = count == 1 ? 0 : (int)((long)sample * (Items.Count - 1) / (count - 1)); var row = Items[index];
            if (row == null || ReferenceEquals(row, CollectionView.NewItemPlaceholder)) continue;
            var type = row.GetType();
            if (!properties.TryGetValue(type, out var property)) { property = type.GetProperty(path); properties[type] = property; }
            string text = Convert.ToString(property?.GetValue(row), CultureInfo.CurrentCulture) ?? "";
            if (text.Length > 2048) text = text[..2048];
            if (longest.Count < 3 || text.Length > longest.Min(value => value.Length)) {
                longest.Add(text); longest = longest.OrderByDescending(value => value.Length).Take(3).ToList();
            }
        }
        var face = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch); double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        return longest.Select(text => new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection, face, FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace + 24).DefaultIfEmpty(0).Max();
    }
    internal static double[] Distribute(double available, double[] desired, double[] minimum, double[] maximum, bool[] flexible) {
        var widths = desired.ToArray();
        double fixedWidth = Enumerable.Range(0, widths.Length).Where(i => !flexible[i]).Sum(i => widths[i]);
        var eligible = Enumerable.Range(0, widths.Length).Where(i => flexible[i]).ToArray();
        double budget = Math.Max(0, available - fixedWidth);
        if (eligible.Sum(i => widths[i]) > budget) {
            // Lower the tallest columns to a common cap, preserving shorter columns and minima.
            double low = 0, high = eligible.Select(i => widths[i]).DefaultIfEmpty(0).Max();
            for (int iteration = 0; iteration < 48; iteration++) {
                double cap = (low + high) / 2;
                if (eligible.Sum(i => Math.Max(minimum[i], Math.Min(widths[i], cap))) > budget) high = cap; else low = cap;
            }
            foreach (int i in eligible) widths[i] = Math.Max(minimum[i], Math.Min(widths[i], low));
        } else {
            double spare = budget - eligible.Sum(i => widths[i]);
            foreach (int i in eligible.OrderByDescending(i => widths[i])) {
                double extra = Math.Min(spare, maximum[i] - widths[i]); widths[i] += extra; spare -= extra;
                if (spare <= 0) break;
            }
        }
        return widths;
    }
    private static bool HeaderAncestor(DependencyObject child) {
        for (DependencyObject? node = child; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is DataGridColumnHeader) return true;
        return false;
    }
    private static ScrollViewer? FindScroll(DependencyObject root) {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer { Name: "DG_ScrollViewer" } scroll) return scroll;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
}
