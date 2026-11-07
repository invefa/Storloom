namespace Storloom.App;

internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        if (args.SequenceEqual(new[] { "--capture-worker" })) return CaptureWorker.Run();
        string? dataDirectory = args.Length == 2 && args[0] == "--data-dir" ? args[1] : null;
        if (args.Length > 0 && dataDirectory == null) {
            MessageBox.Show("Usage: Storloom.exe [--data-dir DIRECTORY]", "Storloom"); return 2;
        }
        var application = new System.Windows.Application();
        try { return application.Run(new ShellWindow(dataDirectory)); }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Storloom"); return 1; }
    }
}
