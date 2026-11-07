using Storloom.Core;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private StorageScan? storageScan;
    private TextBox rootInput = null!;
    private DataGrid directoryList = null!, largeList = null!, overviewIssues = null!;
    private string? currentDirectory;
    private void BuildOverview() {
        var layout = Page("overview", "OverviewTitle", "OverviewLead", View.Auto, View.Fill);
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var actions = View.Row(Button("Browse", () => { string path = View.Folder(this, rootInput.Text); if (path != "") rootInput.Text = path; }),
            AsyncButton("ScanFolder", ScanStorage, true), Button("Back", () => { if (currentDirectory != null) ShowDirectory(Path.GetDirectoryName(currentDirectory) ?? currentDirectory); }));
        DockPanel.SetDock(actions, Dock.Right); bar.Children.Add(actions); rootInput = View.Input(Path.GetPathRoot(Environment.SystemDirectory) ?? "");
        rootInput.Margin = new Thickness(0, 0, 10, 0); bar.Children.Add(rootInput); View.Add(layout, bar, 1);
        directoryList = View.Table(("Path", "Path", 0), ("Bytes", "Size", 110), ("Files", "Files", 90), ("Share", "Share", 80));
        largeList = View.Table(("Path", "Path", 0), ("Bytes", "Size", 120)); overviewIssues = View.Table(("Path", "Path", 0), ("State", "Reason", 300));
        var tabs = new TabControl(); foreach (var (key, content) in new[] { ("Directories", directoryList), ("LargestFiles", largeList), ("Issues", overviewIssues) })
            tabs.Items.Add(new TabItem { Header = L.T(key), Content = content });
        View.Add(layout, tabs, 2);
        directoryList.MouseDoubleClick += (_, _) => { if (directoryList.SelectedItem is StorageRow entry) ShowDirectory(entry.Path); };
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
        if (storageScan == null) return; ShowDirectory(storageScan.Root);
        largeList.ItemsSource = storageScan.LargeFiles.Select(e => new StorageRow(e.Path, View.Size(e.Bytes), e.Files, "")).ToArray();
        overviewIssues.ItemsSource = storageScan.Issues;
    }
    private void ShowDirectory(string path) {
        if (storageScan == null) return;
        var current = storageScan.Directories.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase)); if (current == null) return;
        currentDirectory = path; rootInput.Text = path;
        directoryList.ItemsSource = storageScan.Directories.Where(d => Path.GetDirectoryName(d.Path)?.Equals(path, StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => new StorageRow(e.Path, View.Size(e.Bytes), e.Files, current.Bytes == 0 ? "0%" : $"{100d * e.Bytes / current.Bytes:N1}%")).ToArray();
    }
    private sealed record StorageRow(string Path, string Size, long Files, string Share);
}
