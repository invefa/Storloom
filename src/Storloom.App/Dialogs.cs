using System.Diagnostics;
using Storloom.Core;
using Storloom.Core.Localization;

namespace Storloom.App;

public sealed record AppPreferences {
    public string Language { get; init; } = L.Language;
    public AppTheme Theme { get; init; }
    public int ChatParallelism { get; init; } = 2;
    public string BackupRoot { get; init; } = "";
    public string ArchiveRoot { get; init; } = "";
    public string[]? EnabledRuleSets { get; init; }
}

internal class DialogBase : Form {
    public DialogBase(string title, Size size) {
        Text = L.T(title); ClientSize = size; MinimumSize = new Size(size.Width - 80, size.Height - 40);
        Font = Ui.BodyFont; BackColor = Ui.Canvas; ForeColor = Ui.Ink; StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false; MaximizeBox = false; Icon = ProductInfo.LoadIcon();
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true; Shown += (_, _) => Ui.ApplyTheme(this);
    }
}

internal sealed class PreferencesForm : DialogBase {
    public AppPreferences Result { get; private set; }
    public PreferencesForm(AppPreferences preferences, string rulesDirectory, string dataRoot, CleanerSettings settings,
        bool rulesPage = false) : base("Preferences", new Size(740, 550)) {
        Result = preferences; Padding = new Padding(18);
        var layout = Ui.Grid(Ui.Fill(), Ui.Auto()); Controls.Add(layout);
        var tabs = new BufferedTabs { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 16) };
        var general = new TabPage(L.T("General")) { Padding = new Padding(18), BackColor = Ui.Surface };
        var fields = new BufferedGrid { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 140)); fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new(SizeType.AutoSize));
        var languages = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        languages.Items.AddRange(["English", "简体中文"]); languages.SelectedIndex = preferences.Language == "en" ? 0 : 1;
        fields.Controls.Add(Ui.FieldLabel(L.T("LanguageLabel")), 0, 0); fields.Controls.Add(languages, 1, 0);
        TextBox Field(string label, string value, int row) {
            var input = Ui.Input(); input.Text = value;
            fields.Controls.Add(Ui.FieldLabel(L.T(label)), 0, row); fields.Controls.Add(input, 1, row);
            var browse = Ui.Button("Browse"); browse.Click += (_, _) => { string p = Ui.Folder(this, input.Text); if (p != "") input.Text = p; };
            fields.Controls.Add(browse, 2, row); return input;
        }
        var backup = Field("BackupTo", preferences.BackupRoot, 1);
        var archive = Field("ArchiveRoot", preferences.ArchiveRoot, 2);
        var themes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210,
            Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        themes.Items.AddRange([L.T("ThemeLight"), "One Dark Pro"]); themes.SelectedIndex = (int)preferences.Theme;
        fields.Controls.Add(Ui.FieldLabel(L.T("ThemeLabel")), 0, 3); fields.Controls.Add(themes, 1, 3);
        var workers = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Anchor = AnchorStyles.Left };
        workers.Items.AddRange(["1", "2", "4"]); workers.SelectedIndex = preferences.ChatParallelism == 1 ? 0 : preferences.ChatParallelism == 4 ? 2 : 1;
        fields.Controls.Add(Ui.FieldLabel(L.T("ChatWorkers")), 0, 4); fields.Controls.Add(workers, 1, 4);
        var workersHelp = Ui.Text(L.T("ChatWorkersHelp")); workersHelp.MaximumSize = new Size(400, 0);
        fields.Controls.Add(workersHelp, 1, 5); fields.SetColumnSpan(workersHelp, 2);
        general.Controls.Add(fields); tabs.TabPages.Add(general);
        var rulePage = new TabPage(L.T("RuleSets")) { Padding = new Padding(18), BackColor = Ui.Surface };
        var ruleLayout = Ui.Grid(Ui.Auto(), Ui.Fill(), Ui.Auto(), Ui.Auto()); rulePage.Controls.Add(ruleLayout);
        ruleLayout.Controls.Add(Ui.Text(L.T("SettingsHelp")), 0, 0);
        var sets = RuleSets.ReadIndex(rulesDirectory).Sets;
        var selected = preferences.EnabledRuleSets ?? sets.Where(s => s.DefaultEnabled).Select(s => s.Id).ToArray();
        var checks = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BorderStyle = BorderStyle.None,
            IntegralHeight = false, Margin = new Padding(0, 12, 0, 12), ItemHeight = 30 };
        foreach (var set in sets) checks.Items.Add(set.Names.GetValueOrDefault(L.Language, set.Name), selected.Contains(set.Id));
        ruleLayout.Controls.Add(checks, 0, 1);
        var setDescription = Ui.Text(""); setDescription.AutoSize = false; setDescription.Height = 48;
        setDescription.Dock = DockStyle.Fill;
        checks.SelectedIndexChanged += (_, _) => {
            if (checks.SelectedIndex >= 0) { var set = sets[checks.SelectedIndex];
                setDescription.Text = set.Descriptions.GetValueOrDefault(L.Language, set.Description); }
        };
        if (checks.Items.Count > 0) checks.SelectedIndex = 0;
        ruleLayout.Controls.Add(setDescription, 0, 2);
        var advanced = Ui.Flow();
        foreach (var entry in new[] { ("LocalRules", "rules.json"), ("LocalSettings", "settings.json") }) {
            var button = Ui.Button(entry.Item1); button.Click += (_, _) => {
                string path = Path.Combine(dataRoot, entry.Item2); Directory.CreateDirectory(dataRoot);
                if (!File.Exists(path)) {
                    if (entry.Item2 == "rules.json") JsonFiles.Write(path, new RuleCatalog()); else JsonFiles.Write(path, settings);
                }
                Process.Start(new ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
            }; advanced.Controls.Add(button);
        }
        ruleLayout.Controls.Add(advanced, 0, 3); tabs.TabPages.Add(rulePage); tabs.SelectedIndex = rulesPage ? 1 : 0;
        layout.Controls.Add(tabs, 0, 0);
        var actions = Ui.Flow(true); var save = Ui.Button("Apply", true); var cancel = Ui.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel; actions.Controls.AddRange([save, cancel]); layout.Controls.Add(actions, 0, 1);
        CancelButton = cancel; AcceptButton = save;
        save.Click += (_, _) => {
            try {
                foreach (string path in new[] { backup.Text, archive.Text }) if (!string.IsNullOrWhiteSpace(path)) _ = PathPolicy.Normalize(path);
                var enabled = checks.CheckedIndices.Cast<int>().Select(i => sets[i].Id).ToArray();
                var catalog = RuleSets.Load(rulesDirectory, enabled, Path.Combine(dataRoot, "rules.json"));
                if (catalog.Rules.Count == 0) throw new InvalidDataException(L.T("NoRuleSets"));
                _ = new CleanupEngine(catalog, settings);
                Result = preferences with { Language = languages.SelectedIndex == 0 ? "en" : "zh-CN", Theme = (AppTheme)themes.SelectedIndex, ChatParallelism = int.Parse(workers.SelectedItem!.ToString()!),
                    BackupRoot = backup.Text.Trim(), ArchiveRoot = archive.Text.Trim(), EnabledRuleSets = enabled };
                DialogResult = DialogResult.OK;
            } catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("Error"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
    }
}

internal sealed record ReviewItem(string Name, string Action, string Amount, string Destination, string Details);
internal sealed class ReviewForm : DialogBase {
    public ReviewForm(List<ReviewItem> items, string warning) : base("ReviewTitle", new Size(920, 600)) {
        Padding = new Padding(20);
        var layout = Ui.Grid(Ui.Auto(), Ui.Fill(), new(SizeType.Absolute, 140), Ui.Auto(), Ui.Auto()); Controls.Add(layout);
        layout.Controls.Add(Ui.Text(L.T("ReviewLead")), 0, 0);
        var list = Ui.List(("Rule", 300), ("Action", 180), ("Bytes", 110));
        foreach (var item in items) Ui.Row(list, item, item.Name, item.Action, item.Amount);
        layout.Controls.Add(list, 0, 1);
        var details = new TextBox { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both,
            BackColor = Ui.Surface, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 12, 0, 12) };
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItems.Count == 1) {
            var item = (ReviewItem)list.SelectedItems[0].Tag!; details.Text = item.Destination + Environment.NewLine + item.Details;
        } };
        layout.Controls.Add(details, 0, 2);
        var notice = Ui.Text(warning); notice.MaximumSize = new Size(840, 0); layout.Controls.Add(notice, 0, 3);
        var actions = Ui.Flow(true); var execute = Ui.Button("ConfirmExecute", true); execute.DialogResult = DialogResult.OK;
        var back = Ui.Button("GoBack"); back.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([execute, back]); layout.Controls.Add(actions, 0, 4); CancelButton = back; ActiveControl = back;
        Shown += (_, _) => { if (list.Items.Count > 0) list.Items[0].Selected = true; };
    }
}

internal sealed class HelpForm : DialogBase {
    public HelpForm(int topic = 0, string? document = null) : base("QuickStart", new Size(850, 620)) {
        Padding = new Padding(18); var layout = Ui.Grid(Ui.Fill(), Ui.Auto()); Controls.Add(layout);
        var tabs = new BufferedTabs { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
        var entries = new[] { ("HelpCleanupTitle", "HelpCleanupBody"), ("HelpArchiveTitle", "HelpArchiveBody"),
            ("HelpChatTitle", "HelpChatBody"), ("HelpCommandsTitle", "HelpCommandsBody"), ("HelpTroubleshootingTitle", "HelpTroubleshootingBody"),
            ("NavOverview", "HelpStorageBody") };
        foreach (var (title, body) in entries) {
            var page = new TabPage(L.T(title)) { Padding = new Padding(16) };
            page.Controls.Add(FormatHelp(L.T(title), L.T(body))); tabs.TabPages.Add(page);
        }
        if (document != null) {
            var page = new TabPage(L.T("OpenAiGuide")) { Padding = new Padding(16) };
            page.Controls.Add(FormatHelp(L.T("OpenAiGuide"), document)); tabs.TabPages.Add(page); topic = tabs.TabCount - 1;
        }
        tabs.SelectedIndex = Math.Clamp(topic, 0, tabs.TabCount - 1); layout.Controls.Add(tabs, 0, 0);
        var footer = Ui.Flow(true); var close = Ui.Button("Close"); close.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(close); layout.Controls.Add(footer, 0, 1); CancelButton = close;
    }
    private static RichTextBox FormatHelp(string title, string body) {
        var text = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill,
            BackColor = Ui.Surface, ForeColor = Ui.Ink, Font = new Font(Ui.BodyFont.FontFamily, 11f), DetectUrls = false };
        text.SelectionFont = new Font(Ui.BodyFont.FontFamily, 17f); text.SelectionColor = Ui.Ink;
        text.AppendText(title + "\n\n");
        foreach (string paragraph in body.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)) {
            foreach (string line in paragraph.Split('\n')) {
                bool step = System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\.") || line.StartsWith('#');
                text.SelectionFont = new Font(Ui.BodyFont.FontFamily, step ? 11f : 10.5f, step ? FontStyle.Bold : FontStyle.Regular);
                text.SelectionColor = Ui.Ink; text.SelectionIndent = step ? 0 : 10;
                text.AppendText(line.Trim().TrimStart('#').TrimStart() + "\n");
            }
            text.AppendText("\n");
        }
        text.Select(0, 0); return text;
    }
}

internal sealed class AboutForm : DialogBase {
    public AboutForm() : base("About", new Size(530, 320)) {
        Padding = new Padding(26); var layout = Ui.Grid(Ui.Auto(), Ui.Auto(), Ui.Auto(), Ui.Auto(), Ui.Fill(), Ui.Auto()); Controls.Add(layout);
        var title = Ui.Flow(); using var icon = ProductInfo.LoadIcon(128);
        if (icon != null) title.Controls.Add(new PictureBox { Image = icon.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(52, 52), Margin = new Padding(0, 0, 16, 12) });
        title.Controls.Add(Ui.Text("Storloom", true)); layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(Ui.Text(L.T("Version") + ": " + ProductInfo.Version), 0, 1);
        layout.Controls.Add(Ui.Text(L.T("Developer") + ": " + ProductInfo.Developer), 0, 2);
        var link = new LinkLabel { Text = ProductInfo.Repository, AutoSize = true, LinkColor = Ui.Accent, Margin = new Padding(0, 8, 0, 12) };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(ProductInfo.Repository) { UseShellExecute = true });
        layout.Controls.Add(link, 0, 3); layout.Controls.Add(Ui.Text(L.T("LicenseLabel")), 0, 4);
        var footer = Ui.Flow(true); var close = Ui.Button("Close"); close.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(close); layout.Controls.Add(footer, 0, 5); CancelButton = close;
    }
}
