using Storloom.Core.Localization;

namespace Storloom.App;

internal sealed record CleanupItemResult(string Name, string Action, long? Before, long? After, long? Freed, string State);
internal sealed class CleanupResultsForm : DialogBase {
    public CleanupResultsForm(List<CleanupItemResult> results) : base("CleanupResults", new Size(1020, 540)) {
        Padding = new Padding(20);
        var grid = Ui.Grid(Ui.Auto(), Ui.Fill(), Ui.Auto(), Ui.Auto()); Controls.Add(grid);
        grid.Controls.Add(Ui.Text(L.T("FreedTotal", Ui.Size(results.Sum(r => r.Freed ?? 0))), true), 0, 0);
        var list = Ui.List(("Rule", 270), ("Action", 130), ("BeforeSize", 110), ("AfterSize", 110), ("FreedSize", 110), ("State", 110));
        foreach (var result in results) Ui.Row(list, result, result.Name, result.Action,
            result.Before is long before ? Ui.Size(before) : "-", result.After is long after ? Ui.Size(after) : "-",
            result.Freed is long freed ? Ui.Size(freed) : L.T("SizeNotScanned"), result.State);
        grid.Controls.Add(list, 0, 1); var notice = Ui.Text(L.T("FreedNotice")); notice.MaximumSize = new Size(960, 0);
        notice.Margin = new Padding(0, 12, 0, 12); grid.Controls.Add(notice, 0, 2);
        var footer = Ui.Flow(true); var close = Ui.Button("Close"); close.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(close); grid.Controls.Add(footer, 0, 3); CancelButton = close;
    }
}
