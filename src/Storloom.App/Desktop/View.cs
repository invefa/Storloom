using System.Windows.Data;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;

namespace Storloom.App;

internal static class View {
    public static TextBlock Text(string value, double size = 13, string color = "Ink") {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, color); return text;
    }
    public static TextBox Input(string value = "", bool readOnly = false) => new() { Text = value, IsReadOnly = readOnly, VerticalAlignment = VerticalAlignment.Center };
    public static StackPanel Stack(params UIElement[] elements) {
        var stack = new StackPanel(); foreach (var element in elements) stack.Children.Add(element); return stack;
    }
    public static WrapPanel Row(params UIElement[] elements) {
        var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; foreach (var element in elements) row.Children.Add(element); return row;
    }
    public static Grid Grid(params GridLength[] heights) {
        var grid = new Grid(); foreach (var height in heights) grid.RowDefinitions.Add(new RowDefinition { Height = height }); return grid;
    }
    public static readonly GridLength Auto = GridLength.Auto, Fill = new(1, GridUnitType.Star);
    public static void Add(Grid grid, UIElement element, int row, int column = 0) {
        System.Windows.Controls.Grid.SetRow(element, row); System.Windows.Controls.Grid.SetColumn(element, column); grid.Children.Add(element);
    }
    public static Button Button(string key, Action action, bool primary = false) {
        var button = new Button { Content = L.T(key) };
        button.MouseEnter += (_, _) => AnimateOpacity(button, .82);
        button.MouseLeave += (_, _) => AnimateOpacity(button, 1);
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        button.Click += (_, _) => action(); return button;
    }
    private static void AnimateOpacity(UIElement element, double opacity) {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!Theme.ReduceMotion && SystemParameters.ClientAreaAnimation)
            element.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(opacity, TimeSpan.FromMilliseconds(100)));
    }
    public static Border Card(UIElement content, int padding = 18) {
        var border = new Border { Child = content, Padding = new Thickness(padding), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "Surface"); border.SetResourceReference(Border.BorderBrushProperty, "Line"); return border;
    }
    public static DataGrid Table(params (string Key, string Property, double Width)[] columns) {
        var table = new DataGrid { IsReadOnly = true };
        foreach (var (key, property, width) in columns) table.Columns.Add(new DataGridTextColumn {
            Header = L.T(key), Binding = new Binding(property), Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width), MinWidth = 60 });
        return table;
    }
    public static void Checks(DataGrid table, string property = "Selected") {
        table.IsReadOnly = false;
        foreach (var column in table.Columns) column.IsReadOnly = true;
        var template = new DataTemplate(); var check = new FrameworkElementFactory(typeof(System.Windows.Controls.CheckBox));
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(0)); template.VisualTree = check;
        check.SetValue(FrameworkElement.MinWidthProperty, 0d); check.SetValue(FrameworkElement.WidthProperty, 20d);
        check.SetValue(Control.PaddingProperty, new Thickness(0));
        var cellStyle = new Style(typeof(DataGridCell), (Style)Application.Current.FindResource(typeof(DataGridCell)));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        table.Columns.Insert(0, new DataGridTemplateColumn { CellTemplate = template, CellStyle = cellStyle, Width = 36, MinWidth = 36, CanUserResize = false });
    }
    public static string Size(long bytes) {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"]; double number = bytes; int index = 0;
        while (number >= 1024 && index < units.Length - 1) { number /= 1024; index++; }
        return $"{number:N1} {units[index]}";
    }
    public static string Folder(Window owner, string? initial = null) {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : "";
    }
}

public class ThemeWindow : FluentWindow {
    public Grid Body { get; } = new();
    public ThemeWindow(string title, double width = 800, double height = 600) {
        Title = title; Width = width; Height = height; MinWidth = 620; MinHeight = 420;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None; Icon = ProductInfo.LoadIcon();
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink");
        var root = View.Grid(new GridLength(40), View.Fill);
        root.SetResourceReference(Panel.BackgroundProperty, "Canvas"); Body.SetResourceReference(Panel.BackgroundProperty, "Canvas");
        var titleBar = new TitleBar { Title = title, Height = 40, ShowMaximize = true, ShowMinimize = true };
        titleBar.SetResourceReference(BackgroundProperty, "Surface"); titleBar.SetResourceReference(ForegroundProperty, "Ink");
        titleBar.Icon = new ImageIcon { Source = Icon };
        View.Add(root, titleBar, 0); View.Add(root, Body, 1); Content = root;
        Loaded += (_, _) => Theme.Enter(Body);
    }
}

internal static class Notice {
    public static bool Show(Window owner, string title, string message, bool confirm = false) {
        var window = new ThemeWindow(title, 680, 460) { Owner = owner, ShowInTaskbar = false };
        var grid = View.Grid(View.Fill, View.Auto); grid.Margin = new Thickness(24);
        View.Add(grid, new ScrollViewer { Content = View.Text(message), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 0);
        var cancel = View.Button(confirm ? "GoBack" : "Close", () => window.DialogResult = false); cancel.IsCancel = true;
        var actions = View.Row(cancel); actions.HorizontalAlignment = HorizontalAlignment.Right;
        if (confirm) actions.Children.Add(View.Button("ConfirmExecute", () => window.DialogResult = true, true));
        View.Add(grid, actions, 1); window.Body.Children.Add(grid); return window.ShowDialog() == true;
    }
}
