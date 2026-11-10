namespace Storloom.App;

public enum AppTheme { Light = 0, OneDark = 1, Dark = 2 }
public sealed record AppPreferences {
    public string Language { get; init; } = L.Language;
    public AppTheme Theme { get; init; }
    public bool DownloadQQGroupAvatars { get; init; }
    public int ChatParallelism { get; init; } = 2;
    public string BackupRoot { get; init; } = "";
    public string ArchiveRoot { get; init; } = "";
    public string[]? EnabledRuleSets { get; init; }
    public bool ReduceMotion { get; init; }
    public string? CustomThemeFile { get; init; }
    public double LogHeight { get; init; } = 160;
    public bool LogExpanded{get;init;}
    public bool LogAutoShow{get;init;}=true;
    public bool LogWrap{get;init;}
    public double SidebarWidth { get; init; } = 136;
    public bool SidebarCollapsed { get; init; }
    public bool CloseToTray { get; init; }
    public bool DebugLogging { get; init; }
}
