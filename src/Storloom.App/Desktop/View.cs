using System.Windows.Data;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using PasswordBox = System.Windows.Controls.PasswordBox;
using DataGrid = System.Windows.Controls.DataGrid;

namespace Storloom.App;

internal static class View {
    public static TextBlock Text(string value, double size = 13, string color = "Ink") {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, color); return text;
    }
    public static TextBox Input(string value = "", bool readOnly = false) => new() { Text = value, IsReadOnly = readOnly, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    public static PasswordBox SecretInput() => new() { MinHeight = 32, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
    public static Grid Field(string key, FrameworkElement control, double labelWidth = 112) {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var label = new Label { Content = L.T(key), Target = control, Padding = new Thickness(0), Margin = new Thickness(0, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center };
        label.SetResourceReference(Control.ForegroundProperty, "Ink"); row.Children.Add(label); Add(row, control, 0, 1); return row;
    }
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
        if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        button.Click += (_, _) => action(); return button;
    }
    public static Border Card(UIElement content, int padding = 14) {
        var border = new Border { Child = content, Padding = new Thickness(padding), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "Surface"); border.SetResourceReference(Border.BorderBrushProperty, "Line"); return border;
    }
    public static DataGrid Table(params (string Key, string Property, double Width)[] columns) {
        var table = new DataGrid { IsReadOnly = true };
        var textStyle = new Style(typeof(TextBlock)); textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap)); textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(TextBlock.ToolTipProperty, new Binding("Text") { RelativeSource = RelativeSource.Self }));
        foreach (var (key, property, width) in columns) table.Columns.Add(new DataGridTextColumn {
            Header = L.T(key), Binding = new Binding(property), SortMemberPath = property switch { "Size" or "Amount" => "Bytes", "Share" => "Fraction", "Before" => "BeforeBytes", "After" => "AfterBytes", "Freed" => "FreedBytes", _ => property },
            ElementStyle = textStyle,
            Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width), MinWidth = 48 });
        TableInteraction.Configure(table);
        return table;
    }
    public static Button IconButton(string geometry, string tooltip, Action action) {
        var button = Button(tooltip, action); button.Content = Icon(geometry); button.ToolTip = L.T(tooltip);
        button.Width = 28; button.Height = button.MinHeight = 28; button.MinWidth = 28; button.Padding = new Thickness(5); button.Margin = new Thickness(0);
        button.BorderThickness = new Thickness(0); button.Background = Brushes.Transparent;
        System.Windows.Automation.AutomationProperties.SetName(button, L.T(tooltip)); return button;
    }
    public static void Sort(DataGrid table, string member, System.ComponentModel.ListSortDirection direction = System.ComponentModel.ListSortDirection.Descending) {
        var column = table.Columns.FirstOrDefault(c => c.SortDirection != null) ?? table.Columns.First(c => c.SortMemberPath == member);
        direction = column.SortDirection ?? direction;
        var view = CollectionViewSource.GetDefaultView(table.ItemsSource); if (view == null || !view.CanSort) return;
        view.SortDescriptions.Clear(); view.SortDescriptions.Add(new(column.SortMemberPath, direction)); column.SortDirection = direction;
    }
    public static System.Windows.Shapes.Path Icon(string geometry, double size = 16) {
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = size, Height = size, Stretch = Stretch.Uniform,
            StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "Ink"); return icon;
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
        table.Columns.Insert(0, new DataGridTemplateColumn { CellTemplate = template, CellStyle = cellStyle, Width = 28, MinWidth = 28, CanUserResize = false, CanUserSort = false });
        TableInteraction.EnableChecks(table, property);
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
    public TitleBar Caption { get; }
    public ThemeWindow(string title, double width = 800, double height = 600) {
        Title = title; Width = width; Height = height; MinWidth = 620; MinHeight = 420;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None; Icon = ProductInfo.LoadIcon();
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink");
        var root = View.Grid(new GridLength(36), View.Fill);
        root.SetResourceReference(Panel.BackgroundProperty, "Canvas"); Body.SetResourceReference(Panel.BackgroundProperty, "Canvas");
        var titleBar = Caption = new TitleBar { Title = title, Height = 36, ShowMaximize = true, ShowMinimize = true };
        titleBar.SetResourceReference(BackgroundProperty, "Surface"); titleBar.SetResourceReference(ForegroundProperty, "Ink");
        titleBar.SetResourceReference(TitleBar.ButtonsForegroundProperty, "Ink"); titleBar.SetResourceReference(TitleBar.ButtonsBackgroundProperty, "Hover");
        titleBar.Icon = new ImageIcon { Source = Icon };
        View.Add(root, titleBar, 0); View.Add(root, Body, 1); Content = root;
        Loaded += (_, _) => Theme.Enter(Body);
        SourceInitialized += (_, _) => UpdateFrame(); Activated += (_, _) => UpdateFrame(); Deactivated += (_, _) => UpdateFrame();
        HorizontalWheel.Attach(this);
    }
    protected override void SetWindowChrome() => System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome {
        CaptionHeight = 0, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = ResizeMode == ResizeMode.NoResize ? new Thickness(0) : new Thickness(4), UseAeroCaptionButtons = false });
    public void UpdateFrame() {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle; if (handle == 0) return;
        var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
        if (source == null || Application.Current.TryFindResource("CanvasColor") is not Color color) return;
        source.CompositionTarget.BackgroundColor = color;
        int dark = Theme.IsDark ? 1 : 0, border = color.R | color.G << 8 | color.B << 16;
        _ = DwmSetWindowAttribute(source.Handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(source.Handle, 34, ref border, sizeof(int));
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

internal static class Notice {
    public static bool Show(Window owner, string title, string message, bool confirm = false) {
        double maxHeight = Math.Min(SystemParameters.WorkArea.Height * .85, owner.ActualHeight > 0 ? owner.ActualHeight * .9 : 600);
        var window = new ThemeWindow(title, 460, 180) { Owner = owner, ShowInTaskbar = false,
            MinWidth = 340, MinHeight = 0, MaxHeight = maxHeight, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height };
        window.Caption.ShowMaximize = window.Caption.ShowMinimize = false;
        var grid = View.Grid(View.Auto, View.Auto);
        var content = new Grid { Margin = new Thickness(20) }; content.ColumnDefinitions.Add(new ColumnDefinition { Width = View.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition());
        var symbol = new TextBlock { Text = confirm ? "?" : "i", FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Child = symbol, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(Border.BackgroundProperty, "Accent"); content.Children.Add(icon);
        var text = View.Text(message); text.Margin = new Thickness(0); text.VerticalAlignment = VerticalAlignment.Top;
        var scroll = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(60, maxHeight - 140) };
        View.Add(content, scroll, 0, 1); View.Add(grid, content, 0);
        var cancel = View.Button(confirm ? "Cancel" : "NoticeAccept", () => window.DialogResult = !confirm); cancel.IsCancel = true; cancel.IsDefault = !confirm;
        var actions = View.Row(cancel); actions.HorizontalAlignment = HorizontalAlignment.Right;
        if (confirm) actions.Children.Add(View.Button("ConfirmExecute", () => window.DialogResult = true, true));
        ((Button)actions.Children[^1]).Margin = new Thickness(0);
        var footer = new Border { Child = actions, Padding = new Thickness(16, 12, 16, 12) }; footer.SetResourceReference(Border.BackgroundProperty, "Canvas");
        View.Add(grid, footer, 1); window.Body.Children.Add(grid); window.Loaded += (_, _) => cancel.Focus(); return window.ShowDialog() == true;
    }
}
