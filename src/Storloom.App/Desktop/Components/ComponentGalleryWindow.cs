using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace Storloom.App;

internal sealed class ComponentGalleryWindow : ThemeWindow {
    public ComponentGalleryWindow() : base(L.T("ComponentGallery"), 1040, 1040) {
        Name = "component_gallery"; ShowInTaskbar = false;
        var page = View.Stack(View.Text(L.T("ComponentGallery"), 25), View.Text(L.T("GalleryLead"), color: "Muted"));
        page.Margin = new Thickness(24, 20, 24, 24);
        var tokens = new WrapPanel { Margin = new Thickness(0, 4, 0, 16) };
        foreach (string key in new[] { "Canvas", "Surface", "Ink", "Muted", "Accent", "Selection", "Danger" }) {
            var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 7, 0), BorderThickness = new Thickness(1) };
            swatch.SetResourceReference(BackgroundProperty, key); swatch.SetResourceReference(BorderBrushProperty, "ControlBorder");
            var label = View.Text(key, 11, "Muted"); label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center;
            tokens.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6), Children = { swatch, label } });
        }
        page.Children.Add(tokens);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = View.Auto });
        void Card(string title, string detail, UIElement content, int row, int column) {
            var card = View.Card(View.Stack(View.Text(L.T(title), 17), View.Text(L.T(detail), 12, "Muted"), content), 18);
            card.Margin = new Thickness(column == 0 ? 0 : 8, 0, column == 0 ? 8 : 0, 16); View.Add(grid, card, row, column);
        }
        var feedback = new StatusBanner(L.T("GalleryReady")); feedback.Name = "gallery_feedback";
        var primary = View.Button("GalleryPrimary", () => feedback.Show(L.T("GalleryActionDone"), StatusTone.Success), true);
        var standard = View.Button("GallerySecondary", () => feedback.Show(L.T("GalleryReady")));
        var disabled = View.Button("GalleryDisabled", () => { }); disabled.IsEnabled = false;
        var icon = View.IconButton("M 3,8 L 13,8 M 8,3 L 8,13", "GalleryIconAction", () => feedback.Show(L.T("GalleryActionDone"), StatusTone.Success));
        Card("GalleryActions", "GalleryActionsHelp", View.Stack(View.Row(primary, standard, disabled, icon), feedback), 0, 0);
        var input = View.Input(L.T("GallerySampleName")); input.Name = "gallery_input";
        var selector = new ComboBox { ItemsSource = new[] { L.T("GalleryChoiceCompact"), L.T("GalleryChoiceFolder") }, SelectedIndex = 0 };
        var secret = View.SecretInput(); secret.Password = "example";
        Card("GalleryInputs", "GalleryInputsHelp", View.Stack(View.Field("GalleryName", input, 76), View.Field("GalleryStorage", selector, 76), View.Field("GalleryPassword", secret, 76)), 0, 1);
        var states = View.Stack();
        foreach (var item in new (string Key, bool? Value)[] { ("GalleryUnchecked", false), ("GalleryChecked", true), ("GalleryPartial", null) }) {
            var check = new CheckBox { Name = item.Value == null ? "gallery_partial" : item.Value == true ? "gallery_checked" : "gallery_unchecked", Content = L.T(item.Key), IsChecked = item.Value, IsThreeState = true, Margin = new Thickness(0, 6, 0, 6) };
            states.Children.Add(check);
        }
        states.Children.Add(new CheckBox { Content = L.T("GalleryDisabled"), IsEnabled = false, IsChecked = true, Margin = new Thickness(0, 6, 0, 6) });
        Card("GallerySelection", "GallerySelectionHelp", states, 1, 0);
        var table = View.Table(("Rule", "Name", 0), ("State", "State", 82)); table.Name = "gallery_table"; View.Checks(table); table.Height = 132;
        var rows = new ObservableCollection<GalleryRow>(Enumerable.Range(0, 3).Select(i => new GalleryRow(L.T("GallerySampleRow", i + 1), i == 0)));
        table.ItemsSource = rows; var selection = new TableSelection(table, "gallery_select_all", L.T("SelectAll"));
        var summary = View.Text("", 12, "Muted");
        void UpdateSummary() => summary.Text = L.T("GallerySelectedCount", rows.Count(row => row.Selected), rows.Count);
        selection.SelectionChanged += UpdateSummary; UpdateSummary();
        var search = View.Input(); search.Name = "gallery_filter"; search.ToolTip = L.T("Filter"); search.Margin = new Thickness(0, 0, 8, 8);
        search.TextChanged += (_, _) => CollectionViewSource.GetDefaultView(rows).Filter = item => ((GalleryRow)item).Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase);
        var invert = View.Button("InvertSelection", selection.Invert); invert.Name = "gallery_invert";
        Card("GalleryTables", "GalleryTablesHelp", View.Stack(View.Field("Filter", search, 50), table, View.Row(invert, summary)), 1, 1);
        var busy = new StatusBanner(L.T("GalleryBusy"), StatusTone.Busy);
        var simulate = View.Button("GallerySimulate", () => { }); simulate.Name = "gallery_simulate";
        simulate.Click += async (_, _) => { simulate.IsEnabled = false; busy.Show(L.T("GalleryBusy"), StatusTone.Busy); await Task.Delay(1200); busy.Show(L.T("GalleryActionDone"), StatusTone.Success); simulate.IsEnabled = true; };
        Card("GalleryFeedback", "GalleryFeedbackHelp", View.Stack(busy, View.Row(simulate)), 2, 0);
        Card("GalleryValidation", "GalleryValidationHelp", View.Stack(new StatusBanner(L.T("GalleryValidationExample"), StatusTone.Error), new TextBlock { Text = L.T("GalleryKeyboardHelp"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }), 2, 1);
        page.Children.Add(grid);
        page.Children.Add(View.Text(L.T("GalleryFooter"), 12, "Muted"));
        Body.Children.Add(View.FormScroll(page));
    }
    private sealed class GalleryRow(string name, bool selected) : ISelectableRow {
        public string Name => name;
        public string State => L.T("GallerySampleState");
        public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
