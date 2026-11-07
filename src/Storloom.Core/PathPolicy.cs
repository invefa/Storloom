namespace Storloom.Core;

public sealed class PathPolicy {
    private readonly CleanerSettings settings;
    private readonly string[] forbidden;
    private readonly string[] broadRoots;
    private readonly string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public PathPolicy(CleanerSettings settings) {
        this.settings = settings;
        if (settings.PlanLifetimeHours is < 1 or > 168 || settings.MaximumCandidates < 1 ||
            settings.AllowedDrives.Length == 0 || settings.CommandTimeoutSeconds is < 1 or > 3600)
            throw new InvalidDataException("Invalid settings.");
        forbidden = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        }.Concat(settings.ProtectedRoots).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Normalize).ToArray();
        broadRoots = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData")
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).ToArray();
    }

    public static string Normalize(string path) {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("A local absolute path is required.");
        path = path.Replace('/', '\\');
        if (path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.AsSpan(2).Contains(':') || path.StartsWith(@"\\") ||
            path.Split('\\').Any(s => s.EndsWith(' ') || s.EndsWith('.'))) {
            throw new InvalidDataException($"A canonical local absolute path is required: {path}");
        }
        string full = Path.GetFullPath(path);
        return full.Length == 3 ? full : full.TrimEnd('\\');
    }

    public static bool Under(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    public static void AssertNoLinks(string path) {
        for (string? current = Normalize(path); current != null;
            current = Path.GetDirectoryName(current)) {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse point skipped: {current}");
        }
    }

    public string ValidateRoot(string root) {
        root = Normalize(root);
        if (root.Length <= 3 || root[3..].Split('\\').Length < 2 ||
            broadRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) {
            throw new InvalidDataException($"Root is too broad: {root}");
        }
        ValidateFile(root);
        return root;
    }

    public void ValidateFile(string path) {
        path = Normalize(path);
        string drive = Path.GetPathRoot(path)!;
        if (!settings.AllowedDrives.Any(d => string.Equals(d.TrimEnd('\\', ':'),
            drive.TrimEnd('\\', ':'), StringComparison.OrdinalIgnoreCase))) {
            throw new InvalidDataException($"Drive is not enabled for cleanup: {drive}");
        }
        if (forbidden.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || Under(path, p)) ||
            path.Split('\\').Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))) {
            throw new InvalidDataException($"Protected path: {path}");
        }
        if (Under(path, windows) || path.Equals(windows, StringComparison.OrdinalIgnoreCase)) {
            string[] allowed = [Path.Combine(windows, "Temp"), Path.Combine(windows, "Minidump")];
            if (!allowed.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || Under(path, p)))
                throw new InvalidDataException($"Protected Windows path: {path}");
        }
    }
}
