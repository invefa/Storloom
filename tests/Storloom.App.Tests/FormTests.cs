using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Storloom.App;
using Storloom.Core;
using Storloom.Core.Chats;
using Storloom.Core.Localization;
using Xunit;

namespace Storloom.App.Tests;

public sealed class FormTests {
    private static readonly Lazy<Dispatcher> Ui = new(() => {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() => {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ready.SetResult(app.Dispatcher); app.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return ready.Task.GetAwaiter().GetResult();
    });
    private static void OnUi(Action action) => Ui.Value.Invoke(action);
    private static void Pump() {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static T Wait<T>(Task<T> task) {
        DateTime until = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
        Assert.True(task.IsCompleted); return task.GetAwaiter().GetResult();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i); yield return child;
            foreach(var nested in Descendants(child)) yield return nested;
        }
    }
    private static string Fixture() {
        string path=Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path;
    }
    private static void Capture(FrameworkElement window,string name) {
        Pump(); window.UpdateLayout();
        var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); image.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        DirectoryInfo? repo=new(AppContext.BaseDirectory); while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
        string output=Path.Combine(repo!.FullName,"artifacts/ui-review/wpf");Directory.CreateDirectory(output);
        using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);
    }
    [Theory]
    [InlineData("en",AppTheme.Light)]
    [InlineData("zh-CN",AppTheme.Light)]
    [InlineData("en",AppTheme.OneDark)]
    [InlineData("zh-CN",AppTheme.OneDark)]
    public void NativePagesResizeAndShareTheTitleBarPalette(string locale,AppTheme theme) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Language=locale,Theme=theme,ReduceMotion=true});
        var window=new ShellWindow(fixture){ShowInTaskbar=false};
        try {
            window.Show();Pump();
            Assert.Equal(3,Descendants(window).OfType<ListBox>().Single(l=>l.Name=="navigation").Items.Count);
            Assert.False(Descendants(window).OfType<Button>().Single(b=>b.Name=="review_cleanup").IsEnabled);
            Assert.NotNull(window.Icon);
            var title=Descendants(window).OfType<Wpf.Ui.Controls.TitleBar>().Single();
            Assert.Equal(((SolidColorBrush)Application.Current.Resources["Surface"]).Color,((SolidColorBrush)title.Background).Color);
            foreach(string page in new[]{"overview","cleanup","chats"}) {
                window.Navigate(page);Pump();Capture(window,locale+"-"+theme+"-"+page);
            }
            window.Navigate("cleanup");
            foreach(var size in new[]{(1080d,720d),(2560d,1440d),(1300d,860d)}) {
                window.Width=size.Item1;window.Height=size.Item2;Pump();window.UpdateLayout();
                var table=Descendants(window).OfType<DataGrid>().Single(d=>d.Name=="cleanup_rules");
                Assert.True(table.ActualHeight>100);
                var first=table.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
                Assert.NotNull(first);Assert.InRange(first!.TranslatePoint(new Point(0,0),table).Y,20,70);
            }
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void LogStopsFollowingOnManualInputAndPreservesSelection(AppTheme theme) => OnUi(() => {
        Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});
        var panel=new OperationPanel();var window=new ThemeWindow("Isolated log fixture",1000,500){ShowInTaskbar=false};window.Body.Children.Add(panel);
        try {
            window.Show();Pump();panel.Start();
            for(int i=0;i<150;i++)panel.AddLog("fixture log "+i);
            Pump();Assert.True(panel.Follow);Assert.True(panel.Log.VerticalOffset>0);
            panel.Log.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=UIElement.PreviewMouseWheelEvent});
            Assert.False(panel.Follow);panel.Log.ScrollToVerticalOffset(0);panel.Log.Select(15,12);Pump();
            string selected=panel.Log.SelectedText;
            panel.AddLog("appended while selecting");Pump();
            Assert.Equal(selected,panel.Log.SelectedText);Assert.InRange(panel.Log.VerticalOffset,0,1);
            panel.ToggleFollowing();Pump();Assert.True(panel.Follow);Assert.True(panel.Log.VerticalOffset>0);
            panel.ToggleExpanded();Assert.False(panel.Expanded);panel.ToggleExpanded();Assert.True(panel.Expanded);
            Capture(window,"log-"+theme);
        } finally {window.Close();}
    });
    [Theory]
    [InlineData("en",AppTheme.Light)]
    [InlineData("zh-CN",AppTheme.OneDark)]
    public void PreferencesHelpReviewAndResultsUseNativeThemedWindows(string locale,AppTheme theme) => OnUi(() => {
        string fixture=Fixture();L.SetLanguage(locale);Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});
        var assembly=typeof(ShellWindow).Assembly;
        try {
            foreach(var entry in new[]{("PreferencesWindow",new object?[]{new AppPreferences{Language=locale,Theme=theme},Path.Combine(AppContext.BaseDirectory,"rules"),fixture,new CleanerSettings(),false}),
                ("HelpWindow",new object?[]{2,null}),("AboutWindow",Array.Empty<object?>())}) {
                var window=(Window)Activator.CreateInstance(assembly.GetType("Storloom.App."+entry.Item1)!,entry.Item2)!;
                try {window.ShowInTaskbar=false;window.Show();Pump();Capture(window,locale+"-"+theme+"-"+entry.Item1);
                    Assert.Single(Descendants(window).OfType<Wpf.Ui.Controls.TitleBar>());
                } finally {window.Close();}
            }
        } finally {Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData("en",false)]
    [InlineData("zh-CN",true)]
    public void VerifiedBackupCanExportAfterOriginalAccountIsRemoved(string locale,bool removeSource) => OnUi(() => {
        string fixture=Fixture(), source=Path.Combine(fixture,"account"), data=Path.Combine(fixture,"data");Directory.CreateDirectory(source);
        Database(Path.Combine(source,"nt_msg.db"),"CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'isolated wizard fixture');");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        long size=new FileInfo(Path.Combine(source,"nt_msg.db")).Length;if(removeSource)Directory.Delete(source,true);
        JsonFiles.Write(Path.Combine(data,"preferences.json"),new AppPreferences{Language=locale,ReduceMotion=true});
        var window=new ShellWindow(data){ShowInTaskbar=false};
        try {
            window.Show();Pump();
            var type=typeof(ShellWindow);
            type.GetField("activeChat",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,new ChatLocation(ChatPlatform.QQ,source,"fixture",Path.Combine(source,"nt_msg.db"),size));
            type.GetField("activeManifest",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,backup.Manifest);
            type.GetField("chatStep",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,2);
            type.GetMethod("RenderChatStage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);window.Navigate("chats");
            var task=(Task)type.GetMethod("ExportChat",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
            Wait(AsResult(task));
            string target=Directory.GetDirectories(fixture,"raw-reading-*").Single();
            var report=JsonFiles.Read<ChatExportResult>(Path.Combine(target,"export-report.json"));Assert.True(report.Complete);Assert.Equal(1,report.Messages);
            Assert.Equal(!removeSource,Directory.Exists(source));Capture(window,locale+"-wizard-complete");
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    private static async Task<bool> AsResult(Task task){await task;return true;}
    [Theory]
    [InlineData("en",AppTheme.Light)]
    [InlineData("zh-CN",AppTheme.OneDark)]
    public void RuleSetLibraryDistinguishesImportsWithoutSavingOnClose(string locale,AppTheme theme) => OnUi(() => {
        string fixture=Fixture(),custom=Path.Combine(fixture,"rule-sets");
        JsonFiles.Write(Path.Combine(custom,"sets.json"),new RuleSetIndex(1,[new("custom-fixture","fixture.json","Imported fixture","",[],[],true)]));
        JsonFiles.Write(Path.Combine(custom,"fixture.json"),new RuleCatalog{Rules=[new(){Id="fixture-cache",Name="Fixture",Roots=[Path.Combine(fixture,"cache")]}]});
        string before=File.ReadAllText(Path.Combine(custom,"sets.json"));L.SetLanguage(locale);Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});
        var window=(Window)Activator.CreateInstance(typeof(ShellWindow).Assembly.GetType("Storloom.App.PreferencesWindow")!,new object?[]{new AppPreferences{Theme=theme,Language=locale},Path.Combine(AppContext.BaseDirectory,"rules"),fixture,new CleanerSettings(),true})!;
        try {
            window.ShowInTaskbar=false;window.Show();Pump();
            var table=Descendants(window).OfType<DataGrid>().Single(d=>d.Name=="rule_sets");Assert.Equal(8,table.Items.Count);
            var imported=table.Items.Cast<object>().Single(row=>(string)row.GetType().GetProperty("Name")!.GetValue(row)! =="Imported fixture");
            Assert.Equal(L.T("RuleSetImported"),imported.GetType().GetProperty("Source")!.GetValue(imported));
            imported.GetType().GetProperty("Selected")!.SetValue(imported,false);Capture(window,"rule-library-"+theme);
        } finally {window.Close();}
        Assert.Equal(before,File.ReadAllText(Path.Combine(custom,"sets.json")));Assert.False(File.Exists(Path.Combine(fixture,"preferences.json")));Directory.Delete(fixture,true);
    });
    [Fact]
    public void ScanActionBecomesAnEnabledCancelButtonAndEditorsKeepStableBackgrounds() => OnUi(() => {
        string fixture=Fixture();string source=Path.Combine(fixture,"scan");Directory.CreateDirectory(source);
        for(int i=0;i<1000;i++)File.WriteAllText(Path.Combine(source,i+".txt"),"isolated scan fixture");
        JsonFiles.Write(Path.Combine(fixture,"data/preferences.json"),new AppPreferences{Language="en",ReduceMotion=false});
        var window=new ShellWindow(Path.Combine(fixture,"data")){ShowInTaskbar=false};
        try {
            window.Show();window.Navigate("overview");Pump();
            var input=(TextBox)typeof(ShellWindow).GetField("rootInput",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;input.Text=source;
            input.Focus();Pump();input.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
            var frame=(Border)input.Template.FindName("frame",input);Assert.False(((SolidColorBrush)frame.Background).HasAnimatedProperties);Assert.Equal(1,input.SelectionOpacity);
            var scan=Descendants(window).OfType<Button>().Single(b=>b.IsVisible&&Equals(b.Content,L.T("ScanFolder")));
            scan.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Assert.Equal(L.T("Cancel"),scan.Content);Assert.True(scan.IsEnabled);
            Capture(window,"inline-cancel");scan.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            DateTime until=DateTime.UtcNow.AddSeconds(15);
            while(typeof(ShellWindow).GetField("operation",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!=null&&DateTime.UtcNow<until){Pump();Thread.Sleep(5);}
            Assert.Null(typeof(ShellWindow).GetField("operation",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window));Assert.Equal(L.T("ScanFolder"),scan.Content);
            Assert.Equal(1000,Directory.GetFiles(source).Length);
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void ExplorerTablesSortNumbersWithoutSelectionOverflowAndSidebarCanCollapse(AppTheme theme) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Theme=theme,Language="zh-CN",ReduceMotion=true});
        var window=new ShellWindow(fixture){ShowInTaskbar=false};
        try {
            window.Show();window.Navigate("overview");Pump();
            var entries=Enumerable.Range(0,80).Select(i=>new StorageEntry(Path.Combine(fixture,"folder-"+i),i==0?1048576:i==1?950:2048+i,1,1)).ToList();
            var scan=new StorageScan(fixture,DateTimeOffset.UtcNow,[new(fixture,entries.Sum(e=>e.Bytes),80,0),..entries],[],[],80,entries.Sum(e=>e.Bytes),false);
            typeof(ShellWindow).GetField("storageScan",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,scan);
            typeof(ShellWindow).GetMethod("RenderStorage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);Pump();window.UpdateLayout();
            var table=Descendants(window).OfType<DataGrid>().First(d=>d.IsVisible);
            long Bytes(object value)=>(long)value.GetType().GetProperty("Bytes")!.GetValue(value)!;
            Assert.Equal(1048576,Bytes(table.Items[0]));
            var sizeColumn=table.Columns.Single(c=>c.SortMemberPath=="Bytes");
            var header=Descendants(table).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h=>h.Column==sizeColumn);
            Assert.Equal(Visibility.Visible,((System.Windows.Shapes.Path)header.Template.FindName("sort",header)).Visibility);
            typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(header,null);Pump();
            Assert.Equal(950,Bytes(table.Items[0]));Assert.Equal(System.ComponentModel.ListSortDirection.Ascending,sizeColumn.SortDirection);
            var scroll=Descendants(table).OfType<ScrollViewer>().Single(s=>s.Name=="DG_ScrollViewer");
            Assert.InRange(scroll.ScrollableWidth,0,1);
            table.SelectedIndex=0;Pump();Assert.InRange(scroll.ScrollableWidth,0,1);
            var first=(DataGridRow)table.ItemContainerGenerator.ContainerFromIndex(0)!;Assert.InRange(first.ActualHeight,25,27);
            var vertical=Descendants(table).OfType<System.Windows.Controls.Primitives.ScrollBar>().Single(s=>s.Name=="PART_VerticalScrollBar");
            Assert.InRange(vertical.TranslatePoint(new Point(0,0),table).Y,28,30);
            table.ScrollIntoView(table.Items[^1]);Pump();Assert.True(scroll.VerticalOffset>0);
            var toggle=Descendants(window).OfType<Button>().Single(b=>b.Name=="toggle_sidebar");
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Pump();
            Assert.All(Descendants(window).OfType<TextBlock>().Where(t=>t.Name=="nav_label"),t=>Assert.False(t.IsVisible));
            Capture(window,"dense-collapsed-"+theme);
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Pump();
            Assert.All(Descendants(window).OfType<TextBlock>().Where(t=>t.Name=="nav_label"),t=>Assert.True(t.IsVisible));
            Capture(window,"dense-populated-"+theme);
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Fact]
    public void DiscoveryReportsNoResultsInThePageAndLogsUseEnglish() => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Language="zh-CN",ReduceMotion=true});
        var window=new ShellWindow(fixture){ShowInTaskbar=false};
        try {
            window.Show();window.Navigate("chats");Pump();
            var task=(Task)typeof(ShellWindow).GetMethod("DiscoverChats",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[fixture])!;
            Wait(AsResult(task));Pump();
            var notice=Descendants(window).OfType<TextBlock>().Single(t=>t.Name=="account_discovery_notice");Assert.Equal(L.T("DiscoveryEmpty"),notice.Text);
            var log=Descendants(window).OfType<OperationPanel>().Single().Log;
            Assert.Contains("0 account(s) found",log.Text);Assert.Contains("Operation completed",log.Text);Assert.DoesNotContain(L.T("OperationStarting"),log.Text);
            Assert.False(ControlFeedback.GetEnabled(log));Capture(window,"discovery-empty");
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void CompactChromePopupsAndResizeTargetsRemainConsistent(AppTheme theme) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Theme=theme,Language="zh-CN",ReduceMotion=false});
        var window=new ShellWindow(fixture){ShowInTaskbar=false};
        try {
            window.Show();Pump();window.UpdateLayout();
            var nodes=Descendants(window).ToArray();var caption=nodes.OfType<Wpf.Ui.Controls.TitleBar>().Single();
            Assert.Equal(((SolidColorBrush)Theme.Brush("Ink")).Color,((SolidColorBrush)caption.ButtonsForeground).Color);
            var menu=nodes.OfType<Menu>().Single();
            Assert.InRange(menu.TranslatePoint(new Point(0,0),caption).Y,0,caption.ActualHeight-menu.ActualHeight);
            var item=(MenuItem)menu.Items[0];item.IsSubmenuOpen=true;Pump();
            var popup=(System.Windows.Controls.Primitives.Popup)item.Template.FindName("PART_Popup",item);
            Assert.Equal(System.Windows.Controls.Primitives.PopupAnimation.None,popup.PopupAnimation);
            Assert.Equal(((SolidColorBrush)Theme.Brush("Surface")).Color,((SolidColorBrush)((Border)popup.Child).Background).Color);
            Capture((FrameworkElement)popup.Child,"menu-"+theme);
            item.IsSubmenuOpen=false;window.Navigate("overview");Pump();
            nodes=Descendants(window).ToArray();var input=nodes.OfType<TextBox>().Single(t=>t.Text==Path.GetPathRoot(Environment.SystemDirectory));
            var browse=nodes.OfType<Button>().First(b=>Equals(b.Content,L.T("Browse"))&&b.IsVisible);
            Assert.Equal(32,input.ActualHeight);Assert.Equal(input.ActualHeight,browse.ActualHeight);
            Assert.InRange(Math.Abs(input.TranslatePoint(new Point(0,input.ActualHeight/2),window).Y-browse.TranslatePoint(new Point(0,browse.ActualHeight/2),window).Y),0,.5);
            var tab=nodes.OfType<TabControl>().First(t=>t.IsVisible);var selected=(TabItem)tab.SelectedItem;
            var header=(Border)selected.Template.FindName("head",selected);
            Assert.Equal(((SolidColorBrush)tab.Background).Color,((SolidColorBrush)header.Background).Color);
            Assert.Equal(new Thickness(0),tab.BorderThickness);Assert.Equal(3,header.CornerRadius.TopLeft);
            var splitter=nodes.OfType<GridSplitter>().Single(s=>s.Name=="sidebar_splitter");Assert.InRange(splitter.ActualWidth,9,11);
            var columns=((Grid)splitter.Parent).ColumnDefinitions;double original=columns[0].ActualWidth;
            splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),Environment.TickCount,Key.Right){RoutedEvent=Keyboard.KeyDownEvent});
            Pump();window.UpdateLayout();Assert.True(columns[0].ActualWidth>original);
            window.Navigate("cleanup");Pump();
            foreach(var scrollbar in Descendants(window).OfType<System.Windows.Controls.Primitives.ScrollBar>().Where(s=>s.IsVisible)) {
                Assert.Equal(((SolidColorBrush)Theme.Brush("Surface")).Color,((SolidColorBrush)scrollbar.Background).Color);
                Assert.True(scrollbar.ActualWidth==12||scrollbar.ActualHeight==12);
            }
            var button=Descendants(window).OfType<Button>().Single(b=>b.Name=="scan_cleanup");
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
            var frame=(Border)button.Template.FindName("frame",button);
            if(SystemParameters.ClientAreaAnimation)Assert.True(((SolidColorBrush)frame.Background).HasAnimatedProperties);
            Assert.False(button.HasAnimatedProperties);Assert.Equal(1,button.Opacity);
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});
            Capture(window,"compact-"+theme);
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void CleanupRequiresReviewAndRetainsAnUncheckedFixtureFile(AppTheme theme) => OnUi(() => {
        string fixture=Fixture(),cache=Path.Combine(fixture,"cache"),data=Path.Combine(fixture,"data");Directory.CreateDirectory(cache);
        string keep=Path.Combine(cache,"keep.tmp"),remove=Path.Combine(cache,"remove.tmp");File.WriteAllText(keep,"keep");File.WriteAllText(remove,"remove");
        JsonFiles.Write(Path.Combine(data,"preferences.json"),new AppPreferences{Language="en",Theme=theme,EnabledRuleSets=[],ReduceMotion=true});
        JsonFiles.Write(Path.Combine(data,"rules.json"),new RuleCatalog{Rules=[new CleanupRule{Id="fixture",Name="Fixture cleanup",Roots=[cache],MinimumAgeHours=0}]});
        var window=new ShellWindow(data){ShowInTaskbar=false};
        try {
            window.Show();Pump();var type=typeof(ShellWindow);
            Task Invoke(string method)=>(Task)type.GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
            Wait(AsResult(Invoke("ScanRules")));
            var rules=Descendants(window).OfType<DataGrid>().Single(g=>g.Name=="cleanup_rules");
            rules.Items[0].GetType().GetProperty("Selected")!.SetValue(rules.Items[0],true);
            var files=Descendants(window).OfType<DataGrid>().Single(g=>g.Name=="cleanup_files");
            Assert.Equal(2,files.Items.Count);
            foreach(var item in files.Items)if(Equals(item.GetType().GetProperty("Path")!.GetValue(item),keep))item.GetType().GetProperty("Selected")!.SetValue(item,false);
            bool approve=false;int reviews=0;
            var dismiss=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};
            dismiss.Tick+=(_,_)=>{
                foreach(var dialog in Application.Current.Windows.Cast<Window>().Where(w=>w!=window).ToArray()) {
                    if(dialog.GetType().Name=="ReviewWindow") {reviews++;Assert.True(File.Exists(remove));Capture(dialog,"review-"+theme);dialog.DialogResult=approve;}
                    else if(dialog.GetType().Name=="CleanupResultsWindow"){Capture(dialog,"results-"+theme);dialog.Close();}
                }
            };
            dismiss.Start();
            try {
                Wait(AsResult(Invoke("ExecuteRules")));Assert.Equal(1,reviews);Assert.True(File.Exists(remove));Assert.True(File.Exists(keep));
                approve=true;Wait(AsResult(Invoke("ExecuteRules")));Assert.Equal(2,reviews);Assert.False(File.Exists(remove));Assert.True(File.Exists(keep));
            } finally {dismiss.Stop();}
        } finally {window.Close();Directory.Delete(fixture,true);}
    });

    [Fact]
    public void CustomPaletteIsValidatedAndMotionCanBeDisabled() => OnUi(() => {
        string fixture=Fixture(),path=Path.Combine(fixture,"theme.json");
        try {
            JsonFiles.Write(path,new Dictionary<string,string>{{"Accent","#444444"},{"Surface","#fefefe"}});
            Theme.Apply(new AppPreferences{CustomThemeFile=path,ReduceMotion=true});
            Assert.Equal(Color.FromRgb(0x44,0x44,0x44),((SolidColorBrush)Application.Current.Resources["Accent"]).Color);
            var element=new Grid();Theme.Enter(element);Assert.False(element.HasAnimatedProperties);
            JsonFiles.Write(path,new Dictionary<string,string>{{"Unknown","#123456"}});Assert.Throws<InvalidDataException>(()=>Theme.ReadCustom(path));
            JsonFiles.Write(path,new Dictionary<string,string>{{"Accent","not-a-color"}});Assert.Throws<InvalidDataException>(()=>Theme.ReadCustom(path));
            Theme.Apply(new AppPreferences{ReduceMotion=false});Theme.Enter(element);
            Assert.False(element.HasAnimatedProperties);Assert.Equal(1,element.Opacity);
        } finally {Theme.Apply(new AppPreferences{ReduceMotion=true});Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void ReaderShowsCardsAndCollapsedSourceWithoutExternalAccess(AppTheme theme) => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});L.SetLanguage("en");
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);"+
            "INSERT INTO MSG VALUES(49,1700000000,'fixture','<msg><appmsg><title>Reply text</title><type>57</type><refermsg><displayname>Quoted friend</displayname><content>Quoted text</content></refermsg></appmsg></msg>'),"+
            "(3,1700000001,'fixture','<msg><img md5=\"0123456789abcdef0123456789abcdef\"/></msg>'),"+
            "(49,1700000002,'fixture','<msg><appmsg><type>2000</type><wcpayinfo><feedesc>12.34</feedesc></wcpayinfo></appmsg></msg>'),"+
            "(49,1700000003,'fixture','<msg><appmsg><type>987654</type><title>Unparsed card</title></appmsg></msg>');");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        var export=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(fixture,"reading"),ChatPlatform.WeChat);
        var window=new ChatReaderWindow(export.Directory,Path.Combine(fixture,"data"),true){ShowInTaskbar=false};
        try {
            window.Show();DateTime until=DateTime.UtcNow.AddSeconds(30);
            while(window.Browser.CoreWebView2==null&&DateTime.UtcNow<until){Pump();Thread.Sleep(10);}
            Assert.NotNull(window.Browser.CoreWebView2);var core=window.Browser.CoreWebView2;
            string Query(string script)=>Wait(core.ExecuteScriptAsync(script));
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.message').length")!="4"){Pump();Thread.Sleep(10);}
            Assert.Equal("4",Query("document.querySelectorAll('.message').length"));
            Assert.Contains("Quoted friend",Query("document.querySelector('blockquote').textContent"));
            Assert.Equal("1",Query("document.querySelectorAll('.kind-transfer').length"));
            Assert.Equal("0",Query("document.querySelectorAll('details[open]').length"));
            Assert.Equal("0",Query("document.querySelectorAll('pre').length"));
            Assert.Equal("1",Query("document.querySelectorAll('.kind-image .placeholder').length"));
            Assert.False(core.Settings.AreHostObjectsAllowed);Assert.False(core.Settings.IsWebMessageEnabled);
            Query("document.querySelector('details').open=true;true");Pump();
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('pre').length")=="0"){Pump();Thread.Sleep(10);}
            Assert.Contains("<msg>",System.Text.Json.JsonSerializer.Deserialize<string>(Query("document.querySelector('pre').textContent"))!);
            using var stream=new MemoryStream();Wait(AsResult(core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream)));
            DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
            File.WriteAllBytes(Path.Combine(repo!.FullName,"artifacts/ui-review/wpf/reader-"+theme+".png"),stream.ToArray());
        } finally {window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    private static void Database(string path,string sql) {
        Assert.Equal(0,sqlite3_open_v2(path,out nint db,6,0));try{Assert.Equal(0,sqlite3_exec(db,sql,0,0,0));}finally{sqlite3_close_v2(db);}
    }
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_exec(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint callback,nint state,nint error);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_close_v2(nint db);
}
