using Microsoft.Win32;
using System.Diagnostics;

namespace Storloom.Core.Chats;

/// <summary>Bounded local hints only. Never crawl whole disks to discover installations.</summary>
public static class ChatPaths {
    public static string[] DataRoots() {
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path) { if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)) parents.Add(Environment.ExpandEnvironmentVariables(path)); }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(profile); Add(Path.Combine(profile, "Documents"));
        if (OperatingSystem.IsWindows()) {
            try {
                using var folders = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                Add(folders?.GetValue("Personal") as string);
                foreach (string name in new[] { "WeChat", "Weixin", "QQ", "QQNT" }) {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\" + name);
                    foreach (string value in new[] { "FileSavePath", "UserDataPath", "DataPath" }) if (key?.GetValue(value) is string path && Path.IsPathFullyQualified(path)) { Add(path); explicitRoots.Add(path); }
                }
            } catch (System.Security.SecurityException) { }
        }
        // Profiles can reside on a different volume from the running Windows profile.
        foreach (var drive in DriveInfo.GetDrives()) {
            try { if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                Add(drive.RootDirectory.FullName);
                Add(Path.Combine(drive.RootDirectory.FullName, "Users", Environment.UserName, "Documents"));
            } catch (IOException) { }
        }
        var roots = new HashSet<string>(explicitRoots, StringComparer.OrdinalIgnoreCase);
        foreach (string parent in parents) {
            if (new[] { "Tencent Files", "WeChat Files", "xwechat_files" }.Contains(Path.GetFileName(parent), StringComparer.OrdinalIgnoreCase)) roots.Add(parent);
            foreach (string name in new[] { "Tencent Files", "WeChat Files", "xwechat_files" }) roots.Add(Path.Combine(parent, name));
        }
        return roots.Where(Directory.Exists).ToArray();
    }
    public static string? Executable(ChatPlatform platform, string? manual = null) {
        string[] names = platform == ChatPlatform.QQ ? ["QQ.exe"] : ["Weixin.exe", "WeChat.exe"];
        bool Valid(string? path) => path != null && File.Exists(path) && names.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        if (Valid(manual)) return Path.GetFullPath(manual!);
        foreach (string name in names) foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name))) {
            using (process) { try { string? path = process.MainModule?.FileName; if (Valid(path)) return path; } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { } }
        }
        if (OperatingSystem.IsWindows()) foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine }) foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
            try {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                foreach (string name in names) {
                    using var app = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                    string? path = (app?.GetValue("") as string)?.Trim('"'); if (Valid(path)) return path;
                }
                foreach (string client in platform == ChatPlatform.QQ ? new[] { "QQ", "QQNT" } : new[] { "WeChat", "Weixin" }) {
                    using var key = registry.OpenSubKey(@"Software\Tencent\" + client);
                    foreach (string value in new[] { "InstallPath", "InstallLocation", "Install" }) if (key?.GetValue(value) is string directory)
                        foreach (string name in names) foreach (string relative in new[] { name, Path.Combine("Bin", name) }) { string path = Path.Combine(directory, relative); if (Valid(path)) return path; }
                }
            } catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        foreach (string parent in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            foreach (string client in new[] { "QQ", "QQNT", "WeChat", "Weixin" }) foreach (string name in names) {
                string path = Path.Combine(parent, "Tencent", client, name); if (Valid(path)) return path;
            }
        return null;
    }
}
