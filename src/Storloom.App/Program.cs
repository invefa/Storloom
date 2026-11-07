namespace Storloom.App;

internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        if (args.SequenceEqual(new[] { "--capture-worker" })) return CaptureWorker.Run();
        ApplicationConfiguration.Initialize();
        string? dataDirectory = args.Length == 2 && args[0] == "--data-dir" ? args[1] : null;
        if (args.Length > 0 && dataDirectory == null) {
            MessageBox.Show("Usage: Storloom.exe [--data-dir DIRECTORY]", "Storloom"); return 2;
        }
        try { Application.Run(new ShellForm(dataDirectory)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Storloom", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        return 0;
    }
}
