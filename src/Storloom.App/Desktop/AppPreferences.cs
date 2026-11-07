namespace Storloom.App;

public enum AppTheme { Light, OneDark }
public sealed record AppPreferences {
    public string Language { get; init; } = L.Language;
    public AppTheme Theme { get; init; }
    public int ChatParallelism { get; init; } = 2;
    public string BackupRoot { get; init; } = "";
    public string ArchiveRoot { get; init; } = "";
    public string[]? EnabledRuleSets { get; init; }
    public bool ReduceMotion { get; init; }
    public string? CustomThemeFile { get; init; }
    public double LogHeight { get; init; } = 200;
    public double SidebarWidth { get; init; } = 136;
    public bool SidebarCollapsed { get; init; }
}
