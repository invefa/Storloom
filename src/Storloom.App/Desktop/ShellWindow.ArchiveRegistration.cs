using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    private static ArchiveReference? InspectRegisteredReader(string? path) {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
        try { return ArchiveMetadata.Inspect(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }
    private static string RegisteredReaderPath(string path) => InspectRegisteredReader(path)?.Directory ?? path;
    private async Task ConnectArchive() {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = L.T("ConnectArchiveFile"),
            Filter = "Storloom archive|archive.storloom.json;archive.json;index.html;messages.html;vault.storloom.json;*.storloom-*.json|JSON|*.json" };
        if (PickerHistory.Show(picker, this, "archive-connect") == true) await RegisterArchiveFile(picker.FileName);
    }
    private async Task RegisterArchiveFile(string path, bool askForIdentity = true) {
        ArchiveReference reference;
        try { reference = ArchiveMetadata.Inspect(path); }
        catch (InvalidDataException) when (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path) is not (ArchiveMetadata.FileName or "archive.json" or "vault.storloom.json")) {
            await OpenChatBackupFrom(path); return;
        }
        if (reference.Metadata.Kind == "native") { await ConnectCompactNative(reference.Directory); chatStep = -1; RenderChatStage(); return; }
        if (askForIdentity && reference.Metadata.Account == null) {
            var identity = new ArchiveIdentityWindow(reference) { Owner = this }; if (identity.ShowDialog() != true) return;
            ArchiveMetadata.Write(reference.Directory, identity.Platform, identity.Account);
        } else if (!reference.HasMetadata) ArchiveMetadata.Write(reference.Directory);
        RegisterReadingReference(ArchiveMetadata.Inspect(reference.Directory));
        chatStep = -1; RenderChatStage(); Navigate("chats");
    }
    private void RegisterReadingReference(ArchiveReference reference) {
        if (reference.Metadata.Kind != "reading") throw new InvalidDataException("Select a reading archive.");
        string directory = reference.Directory;
        var project = archiveProjects.Values.FirstOrDefault(p => p.Reading != null && RegisteredReaderPath(p.Reading).Equals(directory, StringComparison.OrdinalIgnoreCase));
        if (project != null) { selectedArchiveId = project.Id; return; }
        if (ArchiveStore.Exists(directory)) {
            LoadStores(); var previous = stores.FirstOrDefault(s => RegisteredReaderPath(s.Directory).Equals(directory, StringComparison.OrdinalIgnoreCase));
            stores.RemoveAll(s => RegisteredReaderPath(s.Directory).Equals(directory, StringComparison.OrdinalIgnoreCase));
            SaveStore(new(reference.Metadata.Id, directory, previous?.Source ?? "", File.Exists(Path.Combine(reference.DataDirectory, "HEAD"))) { AccountId = previous?.AccountId });
        }
        var entries = ReadConnectedReaders().Where(p => !RegisteredReaderPath(p).Equals(directory, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!ArchiveStore.Exists(directory)) entries.Add(directory);
        JsonFiles.Write(Path.Combine(dataRoot, "connected-readers.json"), entries); selectedArchiveId = directory;
    }
    private List<string> ReadConnectedReaders() {
        string path = Path.Combine(dataRoot, "connected-readers.json"); return File.Exists(path) ? JsonFiles.Read<string[]>(path).ToList() : [];
    }
    private void AssociateArchiveIdentity() {
        if (selectedHomeSource is not { Project: null, Reader: { } path }) return;
        var reference = ArchiveMetadata.Inspect(path); var dialog = new ArchiveIdentityWindow(reference) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        ArchiveMetadata.Write(reference.Directory, dialog.Platform, dialog.Account, replaceIdentity:true); selectedArchiveId = selectedHomeSource.Id; RenderChatStage();
    }
    private void ForgetArchiveGroup(HomeAccountRow row) {
        if (row.Sources == null || !Confirm(L.T("ForgetArchiveConfirm", row.DisplayAccount, row.Sources.Count))) return;
        ForgetArchiveRegistrations(row.Sources); chatStep = -1; RenderChatStage();
    }
    private void ForgetArchiveRegistrations(IEnumerable<HomeAccountRow> sources) {
        var items = sources.ToArray();
        if (items.Any(item => item.Project != null && PendingState(item.Project) != null)) throw new InvalidOperationException(L.T("ForgetArchivePending"));
        var ids = items.Where(item => item.Project != null).Select(item => item.Project!.Id).ToHashSet(StringComparer.Ordinal);
        var paths = items.Where(item => item.Reader != null).Select(item => RegisteredReaderPath(item.Reader!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids) {
            var project = archiveProjects[id]; verifiedBackups.Remove(project.Source); archiveProjects.Remove(id);
        }
        LoadStores(); stores.RemoveAll(store => store.AccountId != null && ids.Contains(store.AccountId) || paths.Contains(RegisteredReaderPath(store.Directory)));
        JsonFiles.Write(ArchiveCatalogPath, archiveProjects.Values.ToArray()); JsonFiles.Write(StoreCatalog, stores);
        JsonFiles.Write(Path.Combine(dataRoot, "connected-readers.json"), ReadConnectedReaders().Where(path => !paths.Contains(RegisteredReaderPath(path))).ToArray());
        JsonFiles.Write(Path.Combine(dataRoot, "chat-backups.json"), verifiedBackups); selectedArchiveId = null;
    }
}
