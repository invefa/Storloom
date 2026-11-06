namespace Storloom.App;

internal static class Program {
    [STAThread]
    private static int Main(string[] args) {
        if (args.SequenceEqual(new[] { "--capture-worker" })) return CaptureWorker.Run();
        ApplicationConfiguration.Initialize();
        Application.Run(new ShellForm());
        return 0;
    }
}
