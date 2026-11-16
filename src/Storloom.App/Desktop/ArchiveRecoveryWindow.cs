using System.ComponentModel;
using Storloom.Core.Chats;
namespace Storloom.App;

internal sealed class ArchiveRecoveryWindow : ThemeWindow {
    private sealed class Row(ArchiveReference reference) : ISelectableRow {
        private bool selected = true;
        public ArchiveReference Reference { get; } = reference;
        public string Account => Reference.Metadata.Account ?? L.T("ArchiveIdentityUnassigned");
        public string Client => Reference.Metadata.Platform?.ToString() ?? L.T("ArchiveOriginUnknown");
        public string Path => Reference.Directory;
        public bool Selected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public ArchiveReference[] SelectedArchives { get; private set; } = [];
    public ArchiveRecoveryWindow(ArchiveDiscoveryResult result) : base(L.T("RecoverArchiveLibrary"), 1040, 680) {
        var layout = View.Grid(View.Auto, View.Fill, View.Auto, View.Auto); layout.Margin = new Thickness(24); Body.Children.Add(layout);
        View.Add(layout, View.Text(L.T(result.Complete ? "ArchiveRecoveryReview" : "ArchiveRecoveryPartial", result.Archives.Length, result.Issues.Length), color:"Muted"), 0);
        var rows = result.Archives.Select(reference => new Row(reference)).ToArray();
        var table = View.Table(("Account", "Account", 160), ("ArchiveIdentityPlatform", "Client", 90), ("Path", "Path", 0)); View.Checks(table);
        table.Name = "archive_recovery_candidates"; table.ItemsSource = rows;
        var selection = new TableSelection(table, "archive_recovery_select_all"); View.Add(layout, table, 1);
        var issues = new Expander { Header = L.T("ArchiveRecoverySkipped", result.Issues.Length), Margin = new Thickness(0,8,0,8),
            Content = new TextBox { Text = string.Join(Environment.NewLine, result.Issues.Select(issue => L.T("ArchiveRecoveryIssue" + issue.Reason) + ": " + issue.Path)), IsReadOnly = true,
                MaxHeight = 120, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        View.Add(layout, issues, 2);
        var apply = View.Button("ArchiveRecoveryApply", () => { SelectedArchives = rows.Where(row => row.Selected).Select(row => row.Reference).ToArray(); DialogResult = true; }, true);
        apply.Name = "archive_recovery_apply"; apply.IsEnabled = rows.Length > 0;
        selection.SelectionChanged += () => apply.IsEnabled = rows.Any(row => row.Selected);
        var cancel = View.Button("Cancel", () => DialogResult = false); cancel.IsCancel = true;
        var buttons = View.Row(View.Button("InvertSelection", selection.Invert), cancel, apply); buttons.HorizontalAlignment = HorizontalAlignment.Right; View.Add(layout, buttons, 3);
    }
}
