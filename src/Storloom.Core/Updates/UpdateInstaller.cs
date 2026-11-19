using System.Diagnostics;

namespace Storloom.Core.Updates;

public sealed record UpdateInstallPlan(string InstallRoot, string DataRoot, PreparedUpdate Update, int ParentId, long ParentStarted, string PreviousBuildHash, bool Restart);
public sealed record UpdateInstallResult(string Version, bool Success, string Detail, DateTimeOffset Time);

/// <summary>Runs from the staged package, after the original process has saved its state and exited.</summary>
public static class UpdateInstaller {
    public static string PlanFile(string payload) => Path.Combine(Path.GetDirectoryName(payload)!, "install.json");
    public static void ValidateStaging(PreparedUpdate update,string dataRoot) {
        string source=Path.GetFullPath(update.Directory),transaction=Path.GetDirectoryName(source)??"";
        if(Path.GetFileName(source)!="payload" || !Guid.TryParseExact(Path.GetFileName(transaction),"N",out _) ||
            !string.Equals(Path.GetDirectoryName(transaction),Path.Combine(Path.GetFullPath(dataRoot),"updates"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid update staging location.");
        ReleaseUpdater.ValidatePackage(source,update.Version);
    }
    /// <summary>Keep the two newest completed transactions; ignore any directory not created by this updater.</summary>
    public static void PruneOldTransactions(string dataRoot, string installRoot, string currentVersion, string? pending = null) {
        string updates = Path.Combine(Path.GetFullPath(dataRoot), "updates");
        if (!Directory.Exists(updates) || !Version.TryParse(currentVersion, out var installed)) return;
        var completed = new List<string>();
        foreach (string directory in Directory.EnumerateDirectories(updates).OrderByDescending(Directory.GetCreationTimeUtc)) {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || Path.Combine(directory,"payload").Equals(pending,StringComparison.OrdinalIgnoreCase)) continue;
            string planFile = Path.Combine(directory,"install.json");
            if (!File.Exists(planFile)) continue;
            try {
                var plan = JsonFiles.Read<UpdateInstallPlan>(planFile);
                if (!Path.GetFullPath(plan.DataRoot).Equals(Path.GetFullPath(dataRoot),StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetFullPath(plan.InstallRoot).Equals(Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase) ||
                    !plan.Update.Directory.Equals(Path.Combine(directory,"payload"),StringComparison.OrdinalIgnoreCase) || !Version.TryParse(plan.Update.Version,out var version) || version>installed) continue;
                completed.Add(directory);
            } catch (Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { }
        }
        foreach (string directory in completed.Skip(2)) {
            try {
                // Resolve and check the exact managed transaction before recursive deletion, and reject links at every level.
                string resolved=Path.GetFullPath(directory);
                if(!resolved.StartsWith(updates+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
                ReleaseUpdater.SafePath(directory,"install.json");
                var inspect=new Stack<string>();inspect.Push(directory);
                while(inspect.Count>0)foreach(string entry in Directory.EnumerateFileSystemEntries(inspect.Pop())) {
                    var attributes=File.GetAttributes(entry);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked transaction retained.");if((attributes&FileAttributes.Directory)!=0)inspect.Push(entry);
                }
                Directory.Delete(resolved,true);
            } catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){ }
        }
    }
    public static void Apply(UpdateInstallPlan plan) {
        string source = Path.GetFullPath(plan.Update.Directory), target = Path.GetFullPath(plan.InstallRoot), data = Path.GetFullPath(plan.DataRoot);
        string expectedParent = Path.Combine(data, "updates");
        if (!source.StartsWith(Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(source) != "payload" ||
            source.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !data.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid update staging location.");
        if (target.Equals(source, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(target, "Storloom.exe"))) throw new InvalidDataException("Invalid update destination.");
        ValidateStaging(plan.Update, data);
        var package = JsonFiles.Read<UpdatePackage>(Path.Combine(source,"package-manifest.json"));
        // A second update or a manually replaced installation must not be overwritten.
        if (!ReleaseUpdater.Hash(ReleaseUpdater.SafePath(target, "build-info.json")).Equals(plan.PreviousBuildHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("The installed build changed after the update was prepared.");
        string backup = Path.Combine(Path.GetDirectoryName(source)!, "rollback"); Directory.CreateDirectory(backup);
        var paths = package.Files.Keys.Append("package-manifest.json").ToArray();
        foreach (string relative in paths) {
            string destination = ReleaseUpdater.SafePath(target, relative);
            if (destination.Equals(data, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(data.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Update overlaps the user data directory.");
        }
        // Back up every replaced file before the first mutation. New and unlisted user files are retained.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in paths) {
            string destination = ReleaseUpdater.SafePath(target, relative);
            if (!File.Exists(destination)) continue;
            string saved = ReleaseUpdater.SafePath(backup, relative); Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(destination, saved, false); existing.Add(relative);
        }
        var written = new List<string>();
        try {
            foreach (string relative in paths) {
                string destination = ReleaseUpdater.SafePath(target, relative), incoming = ReleaseUpdater.SafePath(source, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                ReplaceFile(incoming, destination); written.Add(relative);
            }
            ReleaseUpdater.ValidatePackageFiles(target, package);
        } catch (Exception error) {
            var failures = new List<string>();
            foreach (string relative in written.AsEnumerable().Reverse()) {
                try { string destination = ReleaseUpdater.SafePath(target, relative); if (existing.Contains(relative)) ReplaceFile(ReleaseUpdater.SafePath(backup, relative), destination); else File.Delete(destination); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(relative + ": " + ex.Message); }
            }
            if (failures.Count > 0) throw new IOException("Update failed. Some files could not be restored; the previous files are in " + backup + ". " + string.Join("; ", failures), error);
            throw new IOException("Update failed; replaced files were restored. " + error.Message, error);
        }
    }
    private static void ReplaceFile(string source, string destination) {
        string temporary = destination + ".update-" + Guid.NewGuid().ToString("N");
        try { File.Copy(source, temporary, false); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static int Run(string planFile, string executableDirectory) {
        UpdateInstallPlan? plan = null;
        try {
            plan = JsonFiles.Read<UpdateInstallPlan>(planFile);
            if (!Path.GetFullPath(plan.Update.Directory).Equals(Path.GetFullPath(executableDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(planFile).Equals(PlanFile(plan.Update.Directory), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The updater must run from its staged package.");
            try {
                using var parent = Process.GetProcessById(plan.ParentId);
                if (parent.StartTime.ToUniversalTime().Ticks == plan.ParentStarted && !parent.WaitForExit(120000)) throw new IOException("The application did not exit; update was not installed.");
            } catch (ArgumentException) { /* Original process has already exited. */ }
            // Other running copies hold a shared installation lease; never replace their files.
            using (var lease = new FileStream(Path.Combine(plan.InstallRoot, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) Apply(plan);
            JsonFiles.Write(Path.Combine(plan.DataRoot, "updates", "result.json"), new UpdateInstallResult(plan.Update.Version, true, "Update installed.", DateTimeOffset.UtcNow));
            File.Delete(Path.Combine(plan.DataRoot, "updates", "ready.json"));
            if (plan.Restart) Process.Start(new ProcessStartInfo(Path.Combine(plan.InstallRoot, "Storloom.exe")) { UseShellExecute = false, WorkingDirectory = plan.InstallRoot, ArgumentList = { "--data-dir", plan.DataRoot } });
            return 0;
        } catch (Exception ex) {
            if (plan != null) {
                try {
                    JsonFiles.Write(Path.Combine(plan.DataRoot, "updates", "result.json"), new UpdateInstallResult(plan.Update.Version, false, ex.Message, DateTimeOffset.UtcNow));
                    // Do not silently retry a damaged or locked update at every exit.
                    File.Delete(Path.Combine(plan.DataRoot, "updates", "ready.json"));
                } catch (Exception save) when (save is IOException or UnauthorizedAccessException) { }
            }
            return 1;
        }
    }
}
