using System.Diagnostics;
using System.Security.Cryptography;
using Storloom.Core;
using Storloom.Core.Chats;
using Storloom.Core.Localization;

namespace Storloom.App;

public sealed partial class ShellForm : Form {
    private readonly string dataRoot;
    private readonly string rulesDirectory = Path.Combine(AppContext.BaseDirectory, "rules");
    private AppPreferences preferences = new();
    private CleanupEngine engine = null!;
    private CleanupPlan? cleanupPlan;
    private CancellationTokenSource? operation;
    private readonly Dictionary<string, Panel> pages = [];
    private readonly Dictionary<string, Button> navigation = [];
    private Panel pageHost = null!;
    private ToolStripStatusLabel status = null!;
    private ToolStripProgressBar activity = null!;
    private OperationView operationView = null!;
    private ToolStripButton cancel = null!;
    private ToolStripMenuItem fileMenu = null!, toolsMenu = null!;
    private string currentPage = "cleanup";
    private byte[]? capturedKey;
    private readonly Dictionary<string, string> verifiedBackups = new(StringComparer.OrdinalIgnoreCase);

    public ShellForm(string? dataDirectory = null) {
        dataRoot = Path.GetFullPath(dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data"));
        string prefs = Path.Combine(dataRoot, "preferences.json");
        if (File.Exists(prefs)) preferences = JsonFiles.Read<AppPreferences>(prefs);
        string backups = Path.Combine(dataRoot, "chat-backups.json");
        if (File.Exists(backups)) foreach (var pair in JsonFiles.Read<Dictionary<string, string>>(backups))
            verifiedBackups[pair.Key] = pair.Value;
        L.SetLanguage(preferences.Language); Ui.SetTheme(preferences.Theme); ReloadEngine(); DoubleBuffered = true;
        Text = "Storloom"; ClientSize = new Size(1300, 820); MinimumSize = new Size(1080, 720);
        Font = Ui.BodyFont; BackColor = Ui.Canvas; ForeColor = Ui.Ink; Icon = ProductInfo.LoadIcon();
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        BuildUi();
        FormClosing += (_, e) => {
            if (operation != null) { operation.Cancel(); e.Cancel = true; status.Text = L.T("Busy"); }
            else SetCapturedKey(null);
        };
    }

    private void BuildUi() {
        SuspendLayout();
        foreach (Control control in Controls.Cast<Control>().ToArray()) control.Dispose();
        Controls.Clear(); pages.Clear(); navigation.Clear();
        var menu = new MenuStrip { BackColor = Ui.Surface, Padding = new Padding(12, 3, 0, 3) };
        fileMenu = new ToolStripMenuItem(L.T("FileMenu"));
        void Item(ToolStripMenuItem parent, string key, Action action, Keys shortcut = Keys.None) {
            var item = new ToolStripMenuItem(L.T(key)) { ShortcutKeys = shortcut };
            item.Click += (_, _) => { try { action(); } catch (Exception ex) { Error(ex); } }; parent.DropDownItems.Add(item);
        }
        Item(fileMenu, "ImportRules", ImportRules); Item(fileMenu, "ReloadRules", () => { ReloadEngine(); RenderRules(); });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        Item(fileMenu, "SavePlan", SaveCleanupPlan); Item(fileMenu, "LoadPlan", LoadCleanupPlan);
        Item(fileMenu, "OpenChatBackup", OpenChatBackup);
        Item(fileMenu, "OpenReaderArchive", () => {
            using var picker = new OpenFileDialog { Filter = "Storloom HTML|messages.html" };
            if (picker.ShowDialog(this) == DialogResult.OK) {
                using var reader = new ChatReaderForm(Path.GetDirectoryName(picker.FileName)!, dataRoot); reader.ShowDialog(this);
            }
        });
        fileMenu.DropDownItems.Add(new ToolStripSeparator()); Item(fileMenu, "OpenData", () => OpenFolder(dataRoot));
        Item(fileMenu, "Exit", Close);
        toolsMenu = new ToolStripMenuItem(L.T("ToolsMenu")); Item(toolsMenu, "Preferences", () => ShowPreferences());
        var help = new ToolStripMenuItem(L.T("HelpMenu")); Item(help, "QuickStart", ShowHelp, Keys.F1);
        Item(help, "OpenAiGuide", ShowAiGuide); help.DropDownItems.Add(new ToolStripSeparator());
        Item(help, "About", () => { using var dialog = new AboutForm(); dialog.ShowDialog(this); });
        menu.Items.AddRange([fileMenu, toolsMenu, help]); MainMenuStrip = menu;
        var strip = new StatusStrip { BackColor = Ui.Surface, SizingGrip = true };
        status = new ToolStripStatusLabel(L.T("CatalogSummary", engine.Catalog.Rules.Count)) {
            Spring = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Muted };
        activity = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, Visible = false, Width = 120 };
        cancel = new ToolStripButton(L.T("Cancel")) { Visible = false, Enabled = false };
        cancel.Click += (_, _) => operation?.Cancel(); strip.Items.AddRange([status, activity, cancel]);
        var root = new BufferedGrid { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            BackColor = Ui.Canvas, Padding = Padding.Empty, Margin = Padding.Empty };
        root.ColumnStyles.Add(new(SizeType.Absolute, 158)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        var sidebar = new BufferedFlow { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            Name = "sidebar", WrapContents = false, Padding = new Padding(12, 20, 10, 0), BackColor = Ui.Sidebar };
        foreach (var (id, key) in new[] { ("overview", "NavOverview"), ("cleanup", "NavCleanup"), ("chats", "NavChats") }) {
            var button = Ui.Button(key); button.Name = "nav-" + id;
            button.AutoSize = false; button.Size = new Size(134, 44); button.Margin = new Padding(0, 0, 0, 8);
            button.Padding = new Padding(8, 0, 0, 0); button.TextAlign = ContentAlignment.MiddleLeft;
            button.FlatAppearance.BorderSize = 0; button.Click += (_, _) => Navigate(id);
            navigation[id] = button; sidebar.Controls.Add(button);
        }
        pageHost = new BufferedPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 16) };
        root.Controls.Add(sidebar, 0, 0); root.Controls.Add(pageHost, 1, 0);
        operationView = new OperationView { Visible = false };
        Controls.Add(root); Controls.Add(operationView); Controls.Add(strip); Controls.Add(menu);
        BuildOverview(); BuildRules(); BuildChats(); Navigate(currentPage);
        ResumeLayout(true);
        Ui.ApplyTheme(this); Navigate(currentPage);
    }

    private void Navigate(string id) {
        currentPage = id;
        foreach (var pair in pages) pair.Value.Visible = pair.Key == id;
        foreach (var pair in navigation) {
            pair.Value.BackColor = pair.Key == id ? Ui.Surface : Ui.Sidebar;
            pair.Value.ForeColor = pair.Key == id ? Ui.Accent : Ui.Ink;
        }
        pages[id].BringToFront();
    }
    private TableLayoutPanel Page(string id, string title, string lead, params RowStyle[] rows) {
        var panel = new BufferedPanel { Dock = DockStyle.Fill, Visible = false, Margin = Padding.Empty };
        var layout = Ui.Grid([Ui.Auto(), .. rows]);
        var header = Ui.Grid(Ui.Auto(), Ui.Auto()); header.AutoSize = true; header.Dock = DockStyle.Top;
        header.Margin = new Padding(0, 0, 0, 16); header.Controls.Add(Ui.Text(L.T(title), true), 0, 0);
        header.Controls.Add(Ui.Text(L.T(lead)), 0, 1); layout.Controls.Add(header, 0, 0);
        panel.Controls.Add(layout); pages[id] = panel; pageHost.Controls.Add(panel); return layout;
    }
    private Button Button(string key, Action action, bool primary = false) {
        var button = Ui.Button(key, primary); button.Click += (_, _) => { try { action(); } catch (Exception ex) { Error(ex); } };
        return button;
    }
    private Button AsyncButton(string key, Func<Task> action, bool primary = false) {
        var button = Ui.Button(key, primary); button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Error(ex); } };
        return button;
    }
    private void Error(Exception ex) {
        try { JsonFiles.Write(Path.Combine(dataRoot, "errors", $"error-{Guid.NewGuid():N}.json"),
            new { time = DateTimeOffset.UtcNow, error = ex.ToString() }); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        status.Text = ex.Message;
        MessageBox.Show(this, ex.Message, L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    private bool Confirm(string text) => MessageBox.Show(this, text, L.T("Confirm"), MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    private IProgress<ScanProgress> Progress() => new ThrottledProgress(p => {
        if (operation != null && !IsDisposed) operationView.Report(p);
    });
    private async Task Run(Func<CancellationToken, Task> work) {
        if (operation != null) return;
        using var source = new CancellationTokenSource(); operation = source;
        var disabled = Walk(pageHost).Where(c => c is System.Windows.Forms.Button or ListView or ComboBox or CheckBox).Select(c => (Control: c, c.Enabled)).ToArray();
        foreach (var entry in disabled) entry.Control.Enabled = false;
        fileMenu.Enabled = false; toolsMenu.Enabled = false; operationView.Start();
        foreach (var button in navigation.Values) button.Enabled = false;
        cancel.Visible = cancel.Enabled = true; activity.Visible = false;
        bool completed = false; status.Text = L.T("OperationStarting");
        try { await work(source.Token); completed = !source.IsCancellationRequested; }
        catch (OperationCanceledException) { status.Text = L.T("Cancelled"); }
        catch (Exception ex) { Error(ex); }
        finally {
            operation = null; foreach (var entry in disabled) if (!entry.Control.IsDisposed) entry.Control.Enabled = entry.Enabled;
            fileMenu.Enabled = toolsMenu.Enabled = true; operationView.Finish(status.Text ?? L.T("Complete"), completed);
            foreach (var button in navigation.Values) button.Enabled = true;
            cancel.Visible = cancel.Enabled = false; activity.Visible = false;
        }
    }
    private static IEnumerable<Control> Walk(Control parent) {
        foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Walk(child)) yield return nested; }
    }
    private static void OpenFolder(string path) {
        Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void ReloadEngine() {
        string settings = Path.Combine(dataRoot, "settings.json");
        var configuration = File.Exists(settings) ? JsonFiles.Read<CleanerSettings>(settings) : new();
        engine = new CleanupEngine(RuleSets.Load(rulesDirectory, preferences.EnabledRuleSets, Path.Combine(dataRoot, "rules.json")), configuration);
        cleanupPlan = null; excluded.Clear();
    }
    private void ShowPreferences(bool ruleSets = false) {
        using var dialog = new PreferencesForm(preferences, rulesDirectory, dataRoot, engine.Settings, ruleSets);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        preferences = dialog.Result; JsonFiles.Write(Path.Combine(dataRoot, "preferences.json"), preferences);
        SetCapturedKey(null); L.SetLanguage(preferences.Language); Ui.SetTheme(preferences.Theme); Font = Ui.BodyFont; ReloadEngine(); BuildUi();
        status.Text = L.T("SettingsSaved");
    }
    private void ShowHelp() {
        int topic = currentPage == "chats" ? 2 : currentPage == "overview" ? 5 :
            SelectedRule()?.Action == CleanupAction.Command ? 3 :
            SelectedRule() is CleanupRule rule && treatments.GetValueOrDefault(rule.Id) != Treatment.Rule ? 1 : 0;
        using var dialog = new HelpForm(topic); dialog.ShowDialog(this);
    }
    private void ShowAiGuide() {
        string path = Path.Combine(AppContext.BaseDirectory, "docs", "ai-rules.md");
        using var dialog = new HelpForm(document: File.Exists(path) ? File.ReadAllText(path) : L.T("RulesGuide")); dialog.ShowDialog(this);
    }
    private void ImportRules() {
        using var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var catalog = JsonFiles.Read<RuleCatalog>(dialog.FileName); _ = new CleanupEngine(catalog, engine.Settings);
        // Imports merge by ID and keep previously customized local entries.
        string path = Path.Combine(dataRoot, "rules.json");
        var merged = File.Exists(path) ? JsonFiles.Read<RuleCatalog>(path).Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase) : [];
        foreach (var rule in catalog.Rules) merged[rule.Id] = rule;
        JsonFiles.Write(path, new RuleCatalog { Rules = merged.Values.ToList() }); ReloadEngine(); RenderRules(); Navigate("cleanup");
        status.Text = L.T("Imported");
    }
    private void SaveCleanupPlan() {
        if (cleanupPlan == null) throw new InvalidOperationException(L.T("ScanFirst"));
        using var dialog = new SaveFileDialog { Filter = "JSON|*.cleanup-plan.json" };
        if (dialog.ShowDialog(this) == DialogResult.OK) JsonFiles.Write(dialog.FileName,
            cleanupPlan with { Files = SelectedFiles(), Commands = cleanupPlan.Commands.Where(c => SelectedRules().Contains(c.RuleId)).ToList() });
    }
    private void LoadCleanupPlan() {
        using var dialog = new OpenFileDialog { Filter = "JSON|*.cleanup-plan.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        cleanupPlan = JsonFiles.Read<CleanupPlan>(dialog.FileName); excluded.Clear(); RenderRules(); Navigate("cleanup");
    }
    private void SetCapturedKey(byte[]? key) {
        if (capturedKey != null) CryptographicOperations.ZeroMemory(capturedKey); capturedKey = key;
    }
}
