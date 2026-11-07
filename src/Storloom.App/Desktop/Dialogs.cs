using System.Diagnostics;
using System.Windows.Documents;
using Storloom.Core;

namespace Storloom.App;

internal sealed class PreferencesWindow : ThemeWindow {
    public AppPreferences Result { get; private set; }
    public PreferencesWindow(AppPreferences preferences, string rulesDirectory, string dataRoot, CleanerSettings settings, bool rulesPage = false)
        : base(L.T("Preferences"), 830, 690) {
        Result = preferences; ShowInTaskbar = false;
        var layout = View.Grid(View.Fill, View.Auto); layout.Margin = new Thickness(24); Body.Children.Add(layout);
        var tabs = new TabControl(); View.Add(layout, tabs, 0);
        var fields = new Grid { Margin = new Thickness(18) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) }); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = View.Fill });
        int row = 0;
        void Field(string key, UIElement element) { fields.RowDefinitions.Add(new RowDefinition { Height = View.Auto });
            var label = View.Text(L.T(key)); label.Margin = new Thickness(0); View.Add(fields, label, row); if (element is FrameworkElement e) e.Margin = new Thickness(0, 6, 0, 6);
            View.Add(fields, element, row++, 1); }
        ComboBox Options(string[] values, int index) { var combo = new ComboBox(); foreach (string value in values) combo.Items.Add(value); combo.SelectedIndex = index; return combo; }
        var language = Options(["English", "简体中文"], preferences.Language == "en" ? 0 : 1); Field("LanguageLabel", language);
        var themes = Options([L.T("ThemeLight"), L.T("ThemeDark")], (int)preferences.Theme); Field("ThemeLabel", themes);
        TextBox FolderField(string key, string initial) {
            var input = View.Input(initial); var panel = new DockPanel(); var browse = View.Button("Browse", () => { string path = View.Folder(this, input.Text); if (path != "") input.Text = path; });
            DockPanel.SetDock(browse, Dock.Right); panel.Children.Add(browse); panel.Children.Add(input); Field(key, panel); return input;
        }
        var backup = FolderField("BackupTo", preferences.BackupRoot); var archive = FolderField("ArchiveRoot", preferences.ArchiveRoot);
        var workers = Options(["1", "2", "4"], preferences.ChatParallelism == 1 ? 0 : preferences.ChatParallelism == 4 ? 2 : 1); Field("ChatWorkers", workers);
        Field("", View.Text(L.T("ChatWorkersHelp"), color: "Muted"));
        var motion = new CheckBox { Content = L.T("ReduceMotion"), IsChecked = preferences.ReduceMotion }; Field("MotionLabel", motion);
        var custom = View.Input(preferences.CustomThemeFile ?? "");
        var customRow = new DockPanel(); var choose = View.Button("Browse", () => {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Theme JSON|*.json" };
            if (picker.ShowDialog(this) == true) custom.Text = picker.FileName;
        }); DockPanel.SetDock(choose, Dock.Right); customRow.Children.Add(choose); customRow.Children.Add(custom); Field("CustomTheme", customRow);
        Field("", View.Text(L.T("CustomThemeHelp"), color: "Muted"));
        tabs.Items.Add(new TabItem { Header = L.T("General"), Content = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var rules = View.Grid(View.Auto, View.Auto, View.Fill, View.Auto); rules.Margin = new Thickness(14);
        View.Add(rules, View.Text(L.T("SettingsHelp"), color: "Muted"), 0);
        string customSets = Path.Combine(dataRoot, "rule-sets");
        var sets = RuleSets.ReadIndex(rulesDirectory, customSets).Sets;
        var enabled = preferences.EnabledRuleSets ?? sets.Where(s => s.DefaultEnabled).Select(s => s.Id).ToArray();
        var choices = sets.Select(set => new RuleSetChoice(set, enabled.Contains(set.Id))).ToList();
        var pending = new Dictionary<string, RuleCatalog>();
        var setList = View.Table(("RuleSetName", "Name", 0), ("RuleSetSource", "Source", 100), ("RuleSetFile", "File", 180));
        View.Checks(setList); setList.Name = "rule_sets"; setList.ItemsSource = choices; View.Add(rules, setList, 2);
        void RefreshSets() { setList.ItemsSource = null; setList.ItemsSource = choices; }
        var setActions = View.Row(View.Button("ImportRuleSet", () => {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Rule catalog JSON|*.json" }; if (picker.ShowDialog(this) != true) return;
            try {
                var catalog = JsonFiles.Read<RuleCatalog>(picker.FileName); _ = new CleanupEngine(RuleSets.Combine([catalog]), settings);
                string id = "custom-" + Guid.NewGuid().ToString("N");
                var info = new RuleSetInfo(id, id + ".json", Path.GetFileNameWithoutExtension(picker.FileName), "", [], [], true) { IsCustom = true };
                pending[id] = catalog; choices.Add(new(info, true)); RefreshSets(); setList.SelectedIndex = choices.Count - 1;
            } catch (Exception ex) { Notice.Show(this, L.T("Error"), ex.Message); }
        }), View.Button("RemoveRuleSet", () => {
            if (setList.SelectedItem is not RuleSetChoice choice || !choice.Info.IsCustom) { Notice.Show(this, L.T("RuleSets"), L.T("RemoveRuleSetHelp")); return; }
            choices.Remove(choice); pending.Remove(choice.Info.Id); RefreshSets();
        })); setActions.Margin = new Thickness(0, 0, 0, 10); View.Add(rules, setActions, 1);
        var local = View.Row();
        foreach (var (key, filename) in new[] { ("LocalRules", "rules.json"), ("LocalSettings", "settings.json") }) local.Children.Add(View.Button(key, () => {
            string path = Path.Combine(dataRoot, filename); if (!File.Exists(path)) { if (filename == "rules.json") JsonFiles.Write(path, new RuleCatalog()); else JsonFiles.Write(path, settings); }
            Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { path } });
        }));
        local.Margin = new Thickness(0, 10, 0, 0); View.Add(rules, local, 3); tabs.Items.Add(new TabItem { Header = L.T("RuleSets"), Content = rules });
        tabs.SelectedIndex = rulesPage ? 1 : 0;
        var actions = View.Row(View.Button("Cancel", () => DialogResult = false), View.Button("Apply", () => {
            try {
                foreach (string path in new[] { backup.Text, archive.Text }) if (!string.IsNullOrWhiteSpace(path)) _ = PathPolicy.Normalize(path);
                string[] selection = choices.Where(c => c.Selected).Select(c => c.Info.Id).ToArray();
                string localRules = Path.Combine(dataRoot, "rules.json");
                var catalog = RuleSets.Combine(choices.Where(c => c.Selected).Select(c => pending.GetValueOrDefault(c.Info.Id) ??
                    JsonFiles.Read<RuleCatalog>(Path.Combine(c.Info.IsCustom ? customSets : rulesDirectory, c.Info.File))),
                    File.Exists(localRules) ? JsonFiles.Read<RuleCatalog>(localRules) : null);
                if (catalog.Rules.Count == 0) throw new InvalidDataException(L.T("NoRuleSets")); _ = new CleanupEngine(catalog, settings);
                if (!string.IsNullOrWhiteSpace(custom.Text)) _ = Theme.ReadCustom(custom.Text);
                Result = preferences with { Language = language.SelectedIndex == 0 ? "en" : "zh-CN", Theme = (AppTheme)themes.SelectedIndex,
                    BackupRoot = backup.Text.Trim(), ArchiveRoot = archive.Text.Trim(), ChatParallelism = int.Parse((string)workers.SelectedItem),
                    ReduceMotion = motion.IsChecked == true, CustomThemeFile = string.IsNullOrWhiteSpace(custom.Text) ? null : Path.GetFullPath(custom.Text), EnabledRuleSets = selection };
                foreach (var choice in choices.Where(c => pending.ContainsKey(c.Info.Id))) JsonFiles.Write(Path.Combine(customSets, choice.Info.File), pending[choice.Info.Id]);
                JsonFiles.Write(Path.Combine(customSets, "sets.json"), new RuleSetIndex(1, choices.Where(c => c.Info.IsCustom).Select(c => c.Info).ToList()));
                DialogResult = true;
            } catch (Exception ex) { Notice.Show(this, L.T("Error"), ex.Message); }
        }, true)); actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 16, 0, 0); View.Add(layout, actions, 1);
    }
    private sealed class RuleSetChoice(RuleSetInfo info, bool selected) {
        public RuleSetInfo Info => info;
        public string Name => info.Names.GetValueOrDefault(L.Language, info.Name);
        public string Source => L.T(info.IsCustom ? "RuleSetImported" : "RuleSetPackaged");
        public string File => info.File;
        public bool Selected { get; set; } = selected;
    }
}

internal sealed record ReviewItem(string Name, string Action, string Amount, string Destination, string Details) {
    public long? Bytes { get; init; }
}
internal sealed class ReviewWindow : ThemeWindow {
    public ReviewWindow(List<ReviewItem> items, string warning) : base(L.T("ReviewTitle"), 1000, 680) {
        var grid = View.Grid(View.Auto, View.Fill, new GridLength(160), View.Auto, View.Auto); grid.Margin = new Thickness(24); Body.Children.Add(grid);
        View.Add(grid, View.Text(L.T("ReviewLead")), 0);
        var list = View.Table(("Rule", "Name", 0), ("Action", "Action", 150), ("Bytes", "Amount", 120)); list.ItemsSource = items; View.Add(grid, list, 1);
        var details = View.Input(readOnly: true); details.Height = double.NaN; details.AcceptsReturn = true; details.TextWrapping = TextWrapping.Wrap; details.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        details.VerticalAlignment = VerticalAlignment.Stretch; details.VerticalContentAlignment = VerticalAlignment.Top; details.Margin = new Thickness(0, 12, 0, 12); View.Add(grid, details, 2);
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ReviewItem item) details.Text = item.Destination + "\n" + item.Details; };
        if (items.Count > 0) list.SelectedIndex = 0; View.Add(grid, View.Text(warning), 3);
        var back = View.Button("GoBack", () => DialogResult = false); back.IsCancel = true;
        var actions = View.Row(back, View.Button("ConfirmExecute", () => DialogResult = true, true)); actions.HorizontalAlignment = HorizontalAlignment.Right; View.Add(grid, actions, 4);
        Loaded += (_, _) => back.Focus();
    }
}

internal sealed record CleanupItemResult(string Name, string Action, long? Before, long? After, long? Freed, string State);
internal sealed class CleanupResultsWindow : ThemeWindow {
    public CleanupResultsWindow(List<CleanupItemResult> results) : base(L.T("CleanupResults"), 1080, 600) {
        var grid = View.Grid(View.Auto, View.Fill, View.Auto, View.Auto); grid.Margin = new Thickness(24); Body.Children.Add(grid);
        View.Add(grid, View.Text(L.T("FreedTotal", View.Size(results.Sum(r => r.Freed ?? 0))), 23), 0);
        var list = View.Table(("Rule", "Name", 0), ("Action", "Action", 120), ("BeforeSize", "Before", 110), ("AfterSize", "After", 110), ("FreedSize", "Freed", 110), ("State", "State", 120));
        list.ItemsSource = results.Select(r => new { r.Name, r.Action, Before = r.Before is long before ? View.Size(before) : "-", After = r.After is long after ? View.Size(after) : "-",
            Freed = r.Freed is long freed ? View.Size(freed) : L.T("SizeNotScanned"), BeforeBytes = r.Before, AfterBytes = r.After, FreedBytes = r.Freed, r.State }).ToArray(); View.Add(grid, list, 1);
        View.Add(grid, View.Text(L.T("FreedNotice"), color: "Muted"), 2); var close = View.Button("Close", Close); close.HorizontalAlignment = HorizontalAlignment.Right; View.Add(grid, close, 3);
    }
}

internal sealed class HelpWindow : ThemeWindow {
    public HelpWindow(int topic = 0, string? document = null) : base(L.T("QuickStart"), 920, 700) {
        var grid = View.Grid(View.Fill, View.Auto); grid.Margin = new Thickness(22); Body.Children.Add(grid);
        var tabs = new TabControl();
        foreach (var (title, body) in new[] { ("HelpCleanupTitle", "HelpCleanupBody"), ("HelpArchiveTitle", "HelpArchiveBody"), ("HelpChatTitle", "HelpChatBody"),
            ("HelpCommandsTitle", "HelpCommandsBody"), ("HelpTroubleshootingTitle", "HelpTroubleshootingBody"), ("NavOverview", "HelpStorageBody") })
            tabs.Items.Add(new TabItem { Header = L.T(title), Content = Document(L.T(title), L.T(body)) });
        if (document != null) { tabs.Items.Add(new TabItem { Header = L.T("OpenAiGuide"), Content = Document(L.T("OpenAiGuide"), document) }); topic = tabs.Items.Count - 1; }
        tabs.SelectedIndex = Math.Clamp(topic, 0, tabs.Items.Count - 1); View.Add(grid, tabs, 0);
        var close = View.Button("Close", Close); close.Margin = new Thickness(0, 14, 0, 0); close.HorizontalAlignment = HorizontalAlignment.Right; View.Add(grid, close, 1);
    }
    private static FlowDocumentScrollViewer Document(string title, string text) {
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontSize = 14, PagePadding = new Thickness(24), LineHeight = 24 };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Ink"); document.SetResourceReference(FlowDocument.BackgroundProperty, "Surface");
        document.Blocks.Add(new Paragraph(new Run(title)) { FontSize = 25, Margin = new Thickness(0, 0, 0, 22) });
        bool code = false;
        foreach (string line in text.Replace("\r", "").Split('\n')) {
            if (line.StartsWith("```")) { code = !code; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            bool heading = line.StartsWith('#') || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\. ");
            var paragraph = new Paragraph(new Run(line.TrimStart('#').Trim())) { Margin = new Thickness(0, heading ? 12 : 0, 0, 10), FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
            if (code) paragraph.FontFamily = new FontFamily("Consolas"); document.Blocks.Add(paragraph);
        }
        return new FlowDocumentScrollViewer { Document = document, IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}

internal sealed class AboutWindow : ThemeWindow {
    public AboutWindow() : base(L.T("About"), 650, 460) {
        var layout = View.Grid(View.Auto, View.Fill, View.Auto); layout.Margin = new Thickness(24); Body.Children.Add(layout);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        var icon = new Image { Source = ProductInfo.LoadIcon(128), Width = 64, Height = 64, Margin = new Thickness(0, 0, 18, 0) };
        DockPanel.SetDock(icon, Dock.Left); header.Children.Add(icon);
        header.Children.Add(View.Stack(View.Text("Storloom", 25), View.Text(L.T("AboutDescription"), color: "Muted"))); View.Add(layout, header, 0);
        var details = View.Stack(View.Text(L.T("Version") + ": " + ProductInfo.Version), View.Text(L.T("Developer") + ": " + ProductInfo.Developer), View.Text(L.T("LicenseLabel")));
        var links = new TextBlock { Margin = new Thickness(0, 12, 0, 12) };
        void Link(string label, string url) {
            var link = new Hyperlink(new Run(label)) { NavigateUri = new Uri(url) }; link.SetResourceReference(TextElement.ForegroundProperty, "Ink");
            link.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
            links.Inlines.Add(link);
        }
        Link("GitHub", ProductInfo.Repository); links.Inlines.Add(new Run("    ")); Link(L.T("ReleaseHistory"), ProductInfo.Repository + "/releases");
        links.Inlines.Add(new Run("    ")); Link(L.T("ReportIssue"), ProductInfo.Repository + "/issues"); details.Children.Add(links);
        details.Children.Add(View.Text(L.T("AboutComponents"), color: "Muted")); View.Add(layout, details, 1);
        var close = View.Button("Close", Close); close.IsCancel = true; close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0); View.Add(layout, close, 2);
    }
}
