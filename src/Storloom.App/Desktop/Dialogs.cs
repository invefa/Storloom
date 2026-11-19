using System.Diagnostics;
using System.Windows.Documents;
using Storloom.Core;

namespace Storloom.App;

internal sealed class PreferencesWindow : ThemeWindow {
    public AppPreferences Result { get; private set; }
    public Func<IProgress<string>,Task>? CheckUpdates { get; init; }
    public Func<bool>? HasPreparedUpdate { get; init; }
    public string UpdateMessage { get; init; } = "";
    public bool UpdateReady { get; init; }
    public bool RestartForUpdate { get; private set; }
    public Action<string,bool>? ShowUpdateStatus { get; private set; }
    public PreferencesWindow(AppPreferences preferences, string rulesDirectory, string dataRoot, CleanerSettings settings, bool rulesPage = false)
        : base(L.T("Preferences"), 830, 560) {
        Result = preferences; var chatPreferences=preferences; ShowInTaskbar = false;
        var layout = View.Grid(View.Fill, View.Auto); layout.Margin = new Thickness(24); Body.Children.Add(layout);
        var tabs = new TabControl(); View.Add(layout, tabs, 0);
        var fields = new Grid { Margin = new Thickness(18) };
        var tasks=new Grid{Margin=new Thickness(18)};bool taskGroup=false;
        void Field(string key, FrameworkElement element) {
            var target=taskGroup?tasks:fields;int row=target.RowDefinitions.Count;
            target.RowDefinitions.Add(new RowDefinition { Height = View.Auto });
            View.Add(target, View.Field(key, element, 142), row);
        }
        ComboBox Options(string[] values, int index) { var combo = new ComboBox(); foreach (string value in values) combo.Items.Add(value); combo.SelectedIndex = index; return combo; }
        var language = Options(["English", "简体中文"], preferences.Language == "en" ? 0 : 1); Field("LanguageLabel", language);
        AppTheme[] themeValues = [AppTheme.Light, AppTheme.Dark, AppTheme.OneDark];
        var themes = Options([L.T("ThemeLight"), L.T("ThemeGrayscaleDark"), L.T("ThemeDark")], Array.IndexOf(themeValues, preferences.Theme)); Field("ThemeLabel", themes);
        TextBox FolderField(string key, string initial) {
            var input = View.Input(initial); var panel = new DockPanel(); var browse = View.Button("Browse", () => { string path = View.Folder(this, input.Text,"preference-"+key); if (path != "") input.Text = path; });
            browse.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(browse, Dock.Right); panel.Children.Add(browse); panel.Children.Add(input); Field(key, panel); return input;
        }
        taskGroup=true;
        var backup = FolderField("BackupTo", preferences.BackupRoot); var archive = FolderField("ArchiveRoot", preferences.ArchiveRoot);
        var working=FolderField("ChatWorkRoot",preferences.ChatWorkRoot);working.ToolTip=L.T("ChatWorkRootHelp");
        Field("Chats", View.Button("ChatSettings",()=>{var dialog=new ChatSettingsWindow(chatPreferences){Owner=this};if(dialog.ShowDialog()==true)chatPreferences=dialog.Result;}));
        var workers = WorkerRecommendation.Selector(preferences.ChatParallelism,preferences.ChatWorkersAutomatic,"backup",()=>backup.Text); Field("ChatWorkers", workers);
        Field("", View.Text(L.T("ChatWorkersHelp"), color: "Muted"));
        taskGroup=false;
        var motion = new CheckBox { Content = L.T("ReduceMotion"), IsChecked = preferences.ReduceMotion }; Field("MotionLabel", motion);
        var closeToTray = new CheckBox { Content = L.T("CloseToTray"), IsChecked = preferences.CloseToTray }; Field("CloseBehavior", closeToTray);
        taskGroup=true;
        var debugLogging = new CheckBox { Content = L.T("DebugLog"), IsChecked = preferences.DebugLogging, ToolTip = L.T("DebugLogHelp") }; Field("LoggingLabel", debugLogging);
        var logAutoShow=new CheckBox{Content=L.T("AutoShowLog"),IsChecked=preferences.LogAutoShow};Field("",logAutoShow);
        taskGroup=false;
        var custom = View.Input(preferences.CustomThemeFile ?? "");
        var customRow = new DockPanel(); var choose = View.Button("Browse", () => {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Theme JSON|*.json" };
            if (PickerHistory.Show(picker,this,"theme-import") == true) custom.Text = picker.FileName;
        }); choose.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(choose, Dock.Right); customRow.Children.Add(choose); customRow.Children.Add(custom); Field("CustomTheme", customRow);
        Field("", View.Text(L.T("CustomThemeHelp"), color: "Muted"));
        tabs.Items.Add(new TabItem { Header = L.T("General"), Content = View.FormScroll(fields) });
        tabs.Items.Add(new TabItem{Header=L.T("TaskPreferences"),Content=View.FormScroll(tasks)});
        var rules = View.Grid(View.Auto, View.Auto, View.Fill, View.Auto); rules.Margin = new Thickness(14);
        View.Add(rules, View.Text(L.T("SettingsHelp"), color: "Muted"), 0);
        string customSets = Path.Combine(dataRoot, "rule-sets");
        var sets = RuleSets.ReadIndex(rulesDirectory, customSets).Sets;
        var enabled = preferences.EnabledRuleSets ?? sets.Where(s => s.DefaultEnabled).Select(s => s.Id).ToArray();
        var choices = sets.Select(set => new RuleSetChoice(set, enabled.Contains(set.Id))).ToList();
        var pending = new Dictionary<string, RuleCatalog>();
        var setList = View.Table(("RuleSetName", "Name", 0), ("RuleSetSource", "Source", 100), ("RuleSetFile", "File", 180));
        View.Checks(setList); setList.Name = "rule_sets";
        _ = new TableSelection(setList, "select_all_rule_sets", L.T("SelectAll"));
        setList.ItemsSource = choices; View.Add(rules, setList, 2);
        void RefreshSets() { setList.ItemsSource = null; setList.ItemsSource = choices; }
        var setActions = View.Row(View.Button("ImportRuleSet", () => {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Rule catalog JSON|*.json" }; if (PickerHistory.Show(picker,this,"rule-catalog-import") != true) return;
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
        tabs.SelectedIndex = rulesPage ? 2 : 0;
        var updates=View.Stack();updates.Margin=new Thickness(18);
        var automaticUpdates=new CheckBox{Name="automatic_updates",Content=L.T("AutomaticUpdates"),IsChecked=preferences.AutomaticUpdates};
        updates.Children.Add(automaticUpdates);updates.Children.Add(View.Text(L.T("AutomaticUpdatesHelp"),color:"Muted"));
        updates.Children.Add(View.Text(L.T("Version")+": "+ProductInfo.Version));
        var updateStatus=View.Text(L.T("UpdateIdle"));updateStatus.Name="update_status";updates.Children.Add(updateStatus);
        var updateProgress=new ProgressBar{Name="update_progress",Height=3,IsIndeterminate=true,Visibility=Visibility.Collapsed,Margin=new Thickness(0,8,0,12)};updates.Children.Add(updateProgress);
        var restart=View.Button("UpdateRestart",()=>{});restart.Name="update_restart";restart.IsEnabled=false;
        ShowUpdateStatus=(message,ready)=>{updateStatus.Text=message;restart.IsEnabled=ready;};
        var check=View.Button("UpdateCheckNow",()=>{});check.Name="update_check";
        check.Click+=async(_,_)=>{if(CheckUpdates==null)return;check.IsEnabled=false;updateProgress.Visibility=Visibility.Visible;
            try {await CheckUpdates(new Progress<string>(message=>updateStatus.Text=message));}
            finally{check.IsEnabled=true;updateProgress.Visibility=Visibility.Collapsed;restart.IsEnabled=HasPreparedUpdate?.Invoke()==true;}};
        var updateActions=View.DialogActions(check,restart);updateActions.Margin=new Thickness(0,16,0,0);updates.Children.Add(updateActions);
        tabs.Items.Add(new TabItem{Header=L.T("Updates"),Content=View.FormScroll(updates)});
        Loaded+=(_,_)=>{if(UpdateMessage!="")updateStatus.Text=UpdateMessage;restart.IsEnabled=UpdateReady;};
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
                Result = chatPreferences with { AutomaticUpdates=automaticUpdates.IsChecked==true, Language = language.SelectedIndex == 0 ? "en" : "zh-CN", Theme = themeValues[themes.SelectedIndex],
                    BackupRoot = backup.Text.Trim(), ArchiveRoot = archive.Text.Trim(), ChatWorkRoot=working.Text.Trim(), ChatParallelism = workers.SelectedValue is int n&&n>0?n:preferences.ChatParallelism, ChatWorkersAutomatic=workers.SelectedValue is 0,
                    LogAutoShow=logAutoShow.IsChecked==true, ReduceMotion = motion.IsChecked == true, CloseToTray = closeToTray.IsChecked == true, DebugLogging = debugLogging.IsChecked == true, CustomThemeFile = string.IsNullOrWhiteSpace(custom.Text) ? null : Path.GetFullPath(custom.Text), EnabledRuleSets = selection };
                foreach (var choice in choices.Where(c => pending.ContainsKey(c.Info.Id))) JsonFiles.Write(Path.Combine(customSets, choice.Info.File), pending[choice.Info.Id]);
                JsonFiles.Write(Path.Combine(customSets, "sets.json"), new RuleSetIndex(1, choices.Where(c => c.Info.IsCustom).Select(c => c.Info).ToList()));
                DialogResult = true;
            } catch (Exception ex) { Notice.Show(this, L.T("Error"), ex.Message); }
        }, true)); actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 16, 0, 0); View.Add(layout, actions, 1);
        restart.Click+=(_,_)=>{RestartForUpdate=true;actions.Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));if(DialogResult!=true)RestartForUpdate=false;};
    }
    private sealed class RuleSetChoice(RuleSetInfo info, bool selected) : ISelectableRow {
        private bool isSelected = selected;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public RuleSetInfo Info => info;
        public string Name => info.Names.GetValueOrDefault(L.Language, info.Name);
        public string Source => L.T(info.IsCustom ? "RuleSetImported" : "RuleSetPackaged");
        public string File => info.File;
        public bool Selected {
            get => isSelected;
            set { if (isSelected == value) return; isSelected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
        }
    }
}

internal sealed record ReviewItem(string Name, string Action, string Amount, string Destination, string Details) {
    public long? Bytes { get; init; }
}
internal sealed class ReviewWindow : ThemeWindow {
    private sealed record Split(double Ratio);
    public ReviewWindow(List<ReviewItem> items, string warning,string? dataRoot=null) : base(L.T("ReviewTitle"), 1000, 680) {
        MinWidth=640;MinHeight=480;double ratio=.55;string? settings=dataRoot==null?null:Path.Combine(dataRoot,"window-review-split.json");
        try{if(settings!=null&&File.Exists(settings)){double saved=JsonFiles.Read<Split>(settings).Ratio;if(double.IsFinite(saved))ratio=Math.Clamp(saved,.15,.85);}}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){}
        if(dataRoot!=null)WindowGeometry.Attach(this,dataRoot,"review",640,480);
        var grid = View.Grid(View.Auto,new GridLength(ratio,GridUnitType.Star),new GridLength(12),new GridLength(1-ratio,GridUnitType.Star),View.Auto,View.Auto); grid.Margin = new Thickness(24); Body.Children.Add(grid);
        grid.RowDefinitions[1].MinHeight=90;grid.RowDefinitions[3].MinHeight=100;
        View.Add(grid, View.Text(L.T("ReviewLead")), 0);
        var list = View.Table(("Rule", "Name", 0), ("Action", "Action", 150), ("Bytes", "Amount", 120)); list.ItemsSource = items; View.Add(grid, list, 1);
        var divider=new GridSplitter{Name="review_splitter",Height=12,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=Brushes.Transparent,Cursor=System.Windows.Input.Cursors.SizeNS};
        var template=new ControlTemplate(typeof(GridSplitter));var hit=new FrameworkElementFactory(typeof(Grid));hit.SetValue(Panel.BackgroundProperty,Brushes.Transparent);var line=new FrameworkElementFactory(typeof(Border));line.SetValue(FrameworkElement.HeightProperty,1d);line.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);line.SetResourceReference(Border.BackgroundProperty,"Line");hit.AppendChild(line);template.VisualTree=hit;divider.Template=template;View.Add(grid,divider,2);
        divider.DragCompleted+=(_,_)=>{double total=grid.RowDefinitions[1].ActualHeight+grid.RowDefinitions[3].ActualHeight;if(total<=0)return;double share=Math.Clamp(grid.RowDefinitions[1].ActualHeight/total,.15,.85);grid.RowDefinitions[1].Height=new GridLength(share,GridUnitType.Star);grid.RowDefinitions[3].Height=new GridLength(1-share,GridUnitType.Star);if(settings!=null)try{JsonFiles.Write(settings,new Split(share));}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}};
        var details = View.Input(readOnly: true); details.Name="review_details";details.Height = double.NaN; details.AcceptsReturn = true; details.TextWrapping = TextWrapping.Wrap; details.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        details.VerticalAlignment = VerticalAlignment.Stretch; details.VerticalContentAlignment = VerticalAlignment.Top; details.Margin = new Thickness(0,0,0,12); View.Add(grid, details, 3);
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ReviewItem item) details.Text = item.Destination + "\n" + item.Details; };
        if (items.Count > 0) list.SelectedIndex = 0; View.Add(grid, View.Text(warning), 4);
        var back = View.Button("GoBack", () => DialogResult = false); back.IsCancel = true;
        var actions = View.Row(back, View.Button("ConfirmExecute", () => DialogResult = true, true)); actions.HorizontalAlignment = HorizontalAlignment.Right; View.Add(grid, actions, 5);
        Loaded += (_, _) => back.Focus();
    }
}

internal sealed class AboutWindow : ThemeWindow {
    public AboutWindow() : base(L.T("About"), 530, 350) {
        MinWidth = 490; MinHeight = 330; ResizeMode = ResizeMode.NoResize; Caption.ShowMaximize = false; Caption.ShowMinimize = false;
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
