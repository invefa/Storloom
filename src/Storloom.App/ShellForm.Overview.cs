using Storloom.Core;
using Storloom.Core.Localization;

namespace Storloom.App;

public sealed partial class ShellForm {
    private StorageScan? storageScan;
    private TextBox rootInput = null!;
    private ListView directoryList = null!, largeList = null!, overviewIssues = null!;
    private Label analysisEmpty = null!;

    private void BuildOverview() {
        var layout = Page("overview", "OverviewTitle", "OverviewLead", Ui.Auto(), Ui.Fill());
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 0, 0, 16) };
        bar.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) bar.ColumnStyles.Add(new(SizeType.AutoSize));
        rootInput = Ui.Input(); rootInput.Text = Path.GetPathRoot(Environment.SystemDirectory);
        bar.Controls.Add(rootInput, 0, 0);
        bar.Controls.Add(Button("Browse", () => { string path = Ui.Folder(this, rootInput.Text); if (path != "") rootInput.Text = path; }), 1, 0);
        bar.Controls.Add(AsyncButton("ScanFolder", ScanStorage, true), 2, 0);
        bar.Controls.Add(Button("Back", () => { if (directoryList.Tag is string current) ShowDirectory(Path.GetDirectoryName(current) ?? current); }), 3, 0);
        layout.Controls.Add(bar, 0, 1);
        var sections = new TabControl { Dock = DockStyle.Fill, Margin = Padding.Empty };
        directoryList = Ui.List(("Path", 500), ("Bytes", 110), ("Files", 90), ("Share", 70));
        largeList = Ui.List(("Path", 700), ("Bytes", 120)); overviewIssues = Ui.List(("Path", 550), ("State", 300));
        foreach (var (title, control) in new[] { ("Directories", directoryList), ("LargestFiles", largeList), ("Issues", overviewIssues) }) {
            var page = new TabPage(L.T(title)); page.Controls.Add(control); sections.TabPages.Add(page);
        }
        analysisEmpty = Ui.Text(L.T("NoStorageScan")); analysisEmpty.AutoSize = false; analysisEmpty.Dock = DockStyle.Top;
        analysisEmpty.Height = 76; analysisEmpty.TextAlign = ContentAlignment.MiddleCenter; analysisEmpty.BackColor = Color.White;
        var view = new Panel { Dock = DockStyle.Fill }; view.Controls.Add(sections); view.Controls.Add(analysisEmpty);
        layout.Controls.Add(view, 0, 2);
        directoryList.DoubleClick += (_, _) => {
            if (directoryList.SelectedItems.Count == 1 && directoryList.SelectedItems[0].Tag is StorageEntry entry) ShowDirectory(entry.Path);
        };
        if (storageScan != null) RenderStorage();
    }
    private async Task ScanStorage() {
        string root = rootInput.Text;
        await Run(async token => {
            var progress = Progress(); storageScan = await Task.Run(() => new StorageScanner().Scan(root, progress, token));
            JsonFiles.Write(Path.Combine(dataRoot, "scans", $"storage-{DateTime.Now:yyyyMMdd-HHmmss}.json"), storageScan);
            RenderStorage(); status.Text = L.T("ScanSummary", storageScan.Files, storageScan.Bytes / 1073741824d, storageScan.Issues.Count);
        });
    }
    private void RenderStorage() {
        if (storageScan == null) return;
        analysisEmpty.Visible = false; ShowDirectory(storageScan.Root);
        largeList.BeginUpdate(); overviewIssues.BeginUpdate(); largeList.Items.Clear(); overviewIssues.Items.Clear();
        foreach (var entry in storageScan.LargeFiles) Ui.Row(largeList, entry, entry.Path, Ui.Size(entry.Bytes));
        foreach (var issue in storageScan.Issues) Ui.Row(overviewIssues, issue, issue.Path, issue.Reason);
        largeList.EndUpdate(); overviewIssues.EndUpdate();
    }
    private void ShowDirectory(string path) {
        if (storageScan == null) return;
        var current = storageScan.Directories.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (current == null) return;
        directoryList.Tag = path; rootInput.Text = path; directoryList.BeginUpdate(); directoryList.Items.Clear();
        foreach (var entry in storageScan.Directories.Where(d => Path.GetDirectoryName(d.Path)?.Equals(path, StringComparison.OrdinalIgnoreCase) == true))
            Ui.Row(directoryList, entry, entry.Path, Ui.Size(entry.Bytes), entry.Files.ToString("N0"),
                current.Bytes == 0 ? "0%" : $"{100d * entry.Bytes / current.Bytes:N1}%");
        directoryList.EndUpdate();
    }
}
