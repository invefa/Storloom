using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using Storloom.Core;
using Storloom.Core.Updates;

namespace Storloom.App;

public sealed partial class ShellWindow {
    private sealed record UpdateSchedule(DateTimeOffset NextCheck);
    private CancellationTokenSource? updateCancellation;
    private DispatcherTimer? updateTimer;
    private PreparedUpdate? preparedUpdate;
    private bool updateChecking, restartForUpdate;
    private string updateMessage = "";
    private Action<string,bool>? updateStatusChanged;
    private string UpdatesRoot => Path.Combine(dataRoot, "updates");
    private void StartUpdates() {
        if(updateTimer!=null)return;
        try {
            string ready = Path.Combine(UpdatesRoot, "ready.json");
            if (File.Exists(ready)) {
                var pending = JsonFiles.Read<PreparedUpdate>(ready);
                if (Version.TryParse(pending.Version, out var next) && next > Version.Parse(ProductInfo.Version) && Directory.Exists(pending.Directory)) preparedUpdate = pending;
                else File.Delete(ready);
            }
            string resultFile = Path.Combine(UpdatesRoot, "result.json");
            if (File.Exists(resultFile)) {
                var result = JsonFiles.Read<UpdateInstallResult>(resultFile);
                updateMessage = L.T(result.Success ? "UpdateInstalled" : "UpdateFailed", result.Success ? result.Version : result.Detail);
                operationView.AddLog(updateMessage); if (!result.Success) status.Text = updateMessage;
            }
            if (preparedUpdate != null) updateMessage = L.T("UpdateReady", preparedUpdate.Version);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { updateMessage = L.T("UpdateFailed", ex.Message); operationView.AddLog(updateMessage); }
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        updateTimer.Tick += async (_, _) => await CheckForUpdates(false);
        updateTimer.Start();
        _=Task.Run(()=>{try{UpdateInstaller.PruneOldTransactions(dataRoot,AppContext.BaseDirectory,ProductInfo.Version,preparedUpdate?.Directory);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}});
        _ = CheckForUpdates(false);
    }
    private async Task CheckForUpdates(bool manual, IProgress<string>? feedback = null) {
        void Say(string message) { updateMessage = message; feedback?.Report(message); updateStatusChanged?.Invoke(message,preparedUpdate!=null); }
        if (updateChecking) { feedback?.Report(string.IsNullOrEmpty(updateMessage) ? L.T("UpdateChecking") : updateMessage); return; }
        if (!manual && (!preferences.AutomaticUpdates || windowClosed || preparedUpdate != null)) return;
        if (preparedUpdate != null) { Say(L.T("UpdateReady", preparedUpdate.Version)); return; }
        try {
            string schedule = Path.Combine(UpdatesRoot, "schedule.json");
            if (!manual && File.Exists(schedule)) {var next=JsonFiles.Read<UpdateSchedule>(schedule).NextCheck;if(next>DateTimeOffset.UtcNow&&next<=DateTimeOffset.UtcNow.AddDays(1))return;}
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* A corrupt schedule must not disable updates. */ }
        updateChecking = true; updateCancellation = new CancellationTokenSource(); var token = updateCancellation.Token;
        try {
            Say(L.T("UpdateChecking"));
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var updater = new ReleaseUpdater(client,ProductInfo.Repository);
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); checkTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var release = await updater.CheckAsync(ProductInfo.Version, checkTimeout.Token);
            if (release == null) { Say(L.T("UpdateLatest", ProductInfo.Version)); JsonFiles.Write(Path.Combine(UpdatesRoot, "schedule.json"), new UpdateSchedule(DateTimeOffset.UtcNow.AddDays(1))); return; }
            Say(L.T("UpdateDownloading", release.Version, "0%"));
            long lastPercent = -1;
            var progress = new Progress<UpdateProgress>(p => {
                if (windowClosed || token.IsCancellationRequested) return;
                long percent = p.Total > 0 ? p.Bytes * 100 / p.Total : -1;
                if (percent == lastPercent && p.Phase == "download") return; lastPercent = percent;
                Say(p.Phase == "download" ? L.T("UpdateDownloading", release.Version, percent + "%") : L.T("UpdateVerifying"));
            });
            // Hashing and decompression never run on the window dispatcher.
            using var downloadTimeout=CancellationTokenSource.CreateLinkedTokenSource(token);downloadTimeout.CancelAfter(TimeSpan.FromMinutes(30));
            var prepared = await Task.Run(() => updater.DownloadAsync(release, UpdatesRoot, progress, downloadTimeout.Token), token);
            token.ThrowIfCancellationRequested();
            JsonFiles.Write(Path.Combine(UpdatesRoot, "ready.json"), prepared); preparedUpdate = prepared;
            Say(L.T("UpdateReady", prepared.Version)); operationView.AddLog(updateMessage);
            if (operation == null) status.Text = updateMessage;
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { if (!windowClosed) Say(L.T("UpdateCancelled")); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or OperationCanceledException or ArgumentException) {
            if (!windowClosed) { Say(L.T("UpdateFailed", ex is OperationCanceledException ? L.T("UpdateTimeout") : ex.Message)); operationView.AddLog(updateMessage); }
            try { JsonFiles.Write(Path.Combine(UpdatesRoot, "schedule.json"), new UpdateSchedule(DateTimeOffset.UtcNow.AddHours(1))); } catch (Exception save) when (save is IOException or UnauthorizedAccessException) { }
        } finally { updateChecking = false; updateCancellation?.Dispose(); updateCancellation = null; }
    }
    private void LaunchUpdateOnExit() {
        updateTimer?.Stop(); updateCancellation?.Cancel();
        if (preparedUpdate == null || !preferences.AutomaticUpdates && !restartForUpdate) return;
        try {
            string installation = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            // Revalidate persisted staging before executing its updater, including after an offline restart.
            UpdateInstaller.ValidateStaging(preparedUpdate,dataRoot);
            using var process = Process.GetCurrentProcess();
            var plan = new UpdateInstallPlan(installation, dataRoot, preparedUpdate, process.Id, process.StartTime.ToUniversalTime().Ticks,
                ReleaseUpdater.Hash(Path.Combine(installation, "build-info.json")), restartForUpdate);
            string planFile = UpdateInstaller.PlanFile(preparedUpdate.Directory); JsonFiles.Write(planFile, plan);
            Process.Start(new ProcessStartInfo(Path.Combine(preparedUpdate.Directory, "Storloom.exe")) { UseShellExecute = false, WorkingDirectory = preparedUpdate.Directory, CreateNoWindow = true, ArgumentList = { "--apply-update", planFile } });
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) {
            try { JsonFiles.Write(Path.Combine(UpdatesRoot, "result.json"), new UpdateInstallResult(preparedUpdate.Version, false, ex.Message, DateTimeOffset.UtcNow)); } catch (Exception save) when (save is IOException or UnauthorizedAccessException) { }
        }
    }
}
