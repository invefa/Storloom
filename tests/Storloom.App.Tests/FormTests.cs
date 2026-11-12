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
            AppContext.SetSwitch("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", false);
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
    [InlineData("zh-CN",AppTheme.Dark)]
    public void NativePagesResizeAndShareTheTitleBarPalette(string locale,AppTheme theme) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Language=locale,Theme=theme,ReduceMotion=true});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
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
        var panel=new OperationPanel();panel.ToggleExpanded();var window=new ThemeWindow("Isolated log fixture",1000,500){ShowInTaskbar=false};window.Body.Children.Add(panel);
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
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:data){ShowInTaskbar=false};
        try {
            window.Show();Pump();
            var type=typeof(ShellWindow);
            type.GetField("activeChat",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,new ChatLocation(ChatPlatform.QQ,source,"fixture",Path.Combine(source,"nt_msg.db"),size));
            type.GetField("activeManifest",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,backup.Manifest);
            type.GetField("chatStep",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,2);
            type.GetMethod("RenderChatStage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);window.Navigate("chats");
            var confirm=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};confirm.Tick+=(_,_)=>{foreach(var dialog in Application.Current.Windows.OfType<Window>().Where(w=>w.Owner==window).ToArray()){var accept=Descendants(dialog).OfType<Button>().FirstOrDefault(b=>Equals(b.Content,L.T("ConfirmExecute")));if(accept!=null)accept.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));}};confirm.Start();
            var task=(Task)type.GetMethod("ExportChat",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
            Wait(AsResult(task));
            confirm.Stop();string target=JsonFiles.Read<ChatArchiveProject[]>(Path.Combine(data,"chat-archives.json")).Single().Reading!;Assert.True(PathPolicy.Under(target,fixture));
            var report=JsonFiles.Read<ChatExportResult>(Path.Combine(target,"export-report.json"));Assert.True(report.Complete);Assert.Equal(1,report.Messages);
            Assert.Equal(!removeSource,Directory.Exists(source));Capture(window,locale+"-wizard-complete");
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    private static async Task<bool> AsResult(Task task){await task;return true;}
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void NativeHorizontalWheelTargetsTheHoveredPaneAndPausesLogFollowing(AppTheme theme) => OnUi(() => {
        Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});var window=new ThemeWindow("Horizontal wheel fixture",1000,600){ShowInTaskbar=false};
        var layout=new Grid();layout.ColumnDefinitions.Add(new ColumnDefinition());layout.ColumnDefinitions.Add(new ColumnDefinition());window.Body.Children.Add(layout);
        var table=new DataGrid{IsReadOnly=true,AutoGenerateColumns=false};table.Columns.Add(new DataGridTextColumn{Header="Fixture path",Binding=new System.Windows.Data.Binding("Name"),Width=1600});
        table.ItemsSource=Enumerable.Range(0,100).Select(i=>new{Name="Isolated row "+i}).ToArray();layout.Children.Add(table);
        var operation=new OperationPanel();operation.ToggleExpanded();Grid.SetColumn(operation,1);layout.Children.Add(operation);
        try {
            window.Show();Pump();operation.Start();operation.AddLog(new string('x',500));Pump();window.UpdateLayout();
            var scroll=Descendants(table).OfType<ScrollViewer>().Single(s=>s.Name=="DG_ScrollViewer");var handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;
            void Wheel(FrameworkElement target,int delta) {
                var point=target.PointToScreen(new Point(60,80));long coordinates=(ushort)(short)point.X|((long)(ushort)(short)point.Y<<16);
                SendMessageW(handle,0x020e,(nint)((long)(ushort)(short)delta<<16),(nint)coordinates);Pump();window.UpdateLayout();
            }
            Assert.True(scroll.ScrollableWidth>0);Wheel(table,120);Assert.True(scroll.HorizontalOffset>0);double right=scroll.HorizontalOffset;
            Wheel(table,-60);Assert.InRange(scroll.HorizontalOffset,0,right-0.1);Assert.Equal(0,scroll.VerticalOffset);double retained=scroll.HorizontalOffset;
            Assert.True(operation.Follow);Wheel(operation.Log,120);Assert.True(operation.Log.HorizontalOffset>0);Assert.False(operation.Follow);Assert.Equal(retained,scroll.HorizontalOffset);
            Wheel(table,-1200);Assert.InRange(scroll.HorizontalOffset,0,0.1);Capture(window,"horizontal-wheel-"+theme);
        } finally {window.Close();}
    });
    [DllImport("user32.dll")]private static extern int GetWindowLongW(nint hwnd,int index);
    [DllImport("user32.dll")]private static extern nint SendMessageW(nint hwnd,uint message,nint wParam,nint lParam);
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void BusyNavigationKeepsItsPaletteAndStatusHasOneVisibleOwner(AppTheme theme) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Theme=theme,ReduceMotion=true,Language="en"});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};var completion=new TaskCompletionSource();Task? task=null;
        try {
            window.Show();Pump();var type=typeof(ShellWindow);var action=Descendants(window).OfType<Button>().Single(b=>b.Name=="scan_cleanup");
            type.GetField("operationButton",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,action);
            task=(Task)type.GetMethod("Run",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,new object[]{new Func<CancellationToken,Task>(_=>completion.Task)})!;
            Pump();window.UpdateLayout();var navigation=Descendants(window).OfType<ListBox>().Single(l=>l.Name=="navigation");Assert.False(navigation.IsEnabled);
            var sample=navigation.TranslatePoint(new Point(navigation.ActualWidth/2,navigation.ActualHeight-20),window);
            var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            byte[] pixel=new byte[4];bitmap.CopyPixels(new Int32Rect((int)sample.X,(int)sample.Y,1,1),pixel,4,0);var sidebar=((SolidColorBrush)Theme.Brush("Sidebar")).Color;
            Assert.Equal(sidebar.B,pixel[0]);Assert.Equal(sidebar.G,pixel[1]);Assert.Equal(sidebar.R,pixel[2]);
            Assert.Single(Descendants(window).OfType<TextBlock>().Where(t=>t.IsVisible&&t.Text==L.T("OperationStarting")));Capture(window,"busy-palette-"+theme);
            completion.SetResult();Wait(AsResult(task));Pump();
            Assert.Single(Descendants(window).OfType<TextBlock>().Where(t=>t.IsVisible&&t.Text==L.T("OperationStarting")));Assert.True(navigation.IsEnabled);
            var panel=Descendants(window).OfType<OperationPanel>().Single();panel.ToggleExpanded();Pump();
            Assert.Single(Descendants(window).OfType<TextBlock>().Where(t=>t.IsVisible&&t.Text==L.T("OperationStarting")));Capture(window,"single-status-"+theme);
        } finally {completion.TrySetResult();if(task!=null)Wait(AsResult(task));window.Close();Directory.Delete(fixture,true);}
    });
    [Fact]
    public void SidebarTransitionCanReverseAndTransparentFeedbackDoesNotFlashWhite() => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Theme=AppTheme.OneDark,ReduceMotion=false});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try {
            window.Show();Pump();var toggle=Descendants(window).OfType<Button>().Single(b=>b.Name=="toggle_sidebar");
            var frame=(Border)toggle.Template.FindName("frame",toggle);var background=((SolidColorBrush)Theme.Brush("Sidebar")).Color;
            toggle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
            for(int i=0;i<12;i++) {
                Pump();Thread.Sleep(10);var color=((SolidColorBrush)frame.Background).Color;
                double Composite(byte value,byte under)=>value*color.A/255d+under*(255-color.A)/255d;
                Assert.InRange(Composite(color.R,background.R),background.R-1,background.R+35);
                Assert.InRange(Composite(color.G,background.G),background.G-1,background.G+35);
                Assert.InRange(Composite(color.B,background.B),background.B-1,background.B+35);
            }
            toggle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});
            var divider=Descendants(window).OfType<GridSplitter>().Single(s=>s.Name=="sidebar_splitter");var column=((Grid)divider.Parent).ColumnDefinitions[0];double expanded=column.ActualWidth;
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Pump();
            if(SystemParameters.ClientAreaAnimation) {Assert.True(column.HasAnimatedProperties);Thread.Sleep(35);Pump();window.UpdateLayout();Assert.InRange(column.ActualWidth,44,expanded);}
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            for(int i=0;i<25;i++){Thread.Sleep(10);Pump();}window.UpdateLayout();
            Assert.InRange(column.ActualWidth,expanded-1,expanded+1);Assert.True(divider.IsEnabled);
            Assert.All(Descendants(window).OfType<TextBlock>().Where(t=>t.Name=="nav_label"),t=>Assert.True(t.IsVisible));Capture(window,"sidebar-animation-restored");
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void HeaderSizingDoubleClickTooltipsAndMenuSeparatorsWorkTogether(AppTheme theme) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Theme=theme,Language="zh-CN",ReduceMotion=true,SidebarCollapsed=true});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try {
            window.Show();Pump();
            var table=Descendants(window).OfType<DataGrid>().Single(d=>d.Name=="cleanup_rules");var row=(DataGridRow)table.ItemContainerGenerator.ContainerFromIndex(0)!;
            var cell=Descendants(row).OfType<DataGridCell>().Single(c=>c.Column==table.Columns[1]);
            bool Selected()=>(bool)row.Item.GetType().GetProperty("Selected")!.GetValue(row.Item)!;
            void DoubleClick(object source)=>table.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.PreviewMouseDoubleClickEvent,Source=source});
            Assert.False(Selected());DoubleClick(cell);Assert.True(Selected(),"row double-click should check");Assert.True(Descendants(row).OfType<CheckBox>().Single().IsChecked,"checkbox should update");
            DoubleClick(cell);Assert.False(Selected());
            var check=Descendants(row).OfType<CheckBox>().Single();check.IsChecked=true;DoubleClick(check);Assert.True(Selected(),"checkbox double-click should not toggle twice");
            Assert.Equal(row.Item.GetType().GetProperty("Name")!.GetValue(row.Item),cell.ToolTip);
            var header=Descendants(table).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h=>h.Column==table.Columns[1]);
            var original=header.Column.Width;var menu=header.ContextMenu;Assert.NotNull(menu);menu.PlacementTarget=header;menu.IsOpen=true;Pump();Capture(menu,"header-context-"+theme);
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();Assert.True(header.Column.Width.IsAuto,"fit column should set Auto");
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();Assert.All(table.Columns.Where(c=>c.CanUserResize),c=>Assert.True(c.Width.IsAuto));Assert.Equal(28,table.Columns[0].ActualWidth);
            ((MenuItem)menu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();Assert.Equal(original.UnitType,header.Column.Width.UnitType);menu.IsOpen=false;
            var navigation=Descendants(window).OfType<ListBox>().Single(l=>l.Name=="navigation");var item=(ListBoxItem)navigation.SelectedItem;var sidebar=(Grid)navigation.Parent;
            double left=item.TranslatePoint(new Point(0,0),sidebar).X,right=sidebar.ActualWidth-item.TranslatePoint(new Point(item.ActualWidth,0),sidebar).X;
            Assert.InRange(Math.Abs(left-right),0,1);Assert.Equal(new Thickness(0),((Border)item.Template.FindName("item",item)).BorderThickness);
            Assert.Equal(new Thickness(0),Descendants(window).OfType<Button>().Single(b=>b.Name=="toggle_sidebar").BorderThickness);
            var file=(MenuItem)Descendants(window).OfType<Menu>().Single().Items[0];file.IsSubmenuOpen=true;Pump();
            var popup=(System.Windows.Controls.Primitives.Popup)file.Template.FindName("PART_Popup",file);var separator=file.Items.OfType<Separator>().First();separator.ApplyTemplate();
            Assert.Contains(Descendants(separator).OfType<Border>(),b=>b.Background is SolidColorBrush brush&&brush.Color==((SolidColorBrush)Theme.Brush("Line")).Color);
            Capture((FrameworkElement)popup.Child,"menu-separators-"+theme);file.IsSubmenuOpen=false;
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void PublicReadmeScreenshotsUseOnlySyntheticDisplayData(string locale) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Language=locale,Theme=AppTheme.Light,ReduceMotion=true});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try {
            window.Show();Pump();var type=typeof(ShellWindow);
            var entries=new[]{new StorageEntry(@"C:\Example\Applications",12884901888,12400,1),new StorageEntry(@"C:\Example\Projects",7516192768,8200,1),new StorageEntry(@"C:\Example\Media",5368709120,320,1),new StorageEntry(@"C:\Example\Archives",3221225472,120,1),new StorageEntry(@"C:\Example\Cache",1073741824,4200,1)};
            var scan=new StorageScan(@"C:\Example",DateTimeOffset.UtcNow,[new(@"C:\Example",entries.Sum(e=>e.Bytes),entries.Sum(e=>e.Files),0),..entries],[],[],entries.Sum(e=>e.Files),entries.Sum(e=>e.Bytes),false);
            type.GetField("storageScan",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,scan);type.GetMethod("RenderStorage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
            window.Navigate("overview");Pump();((TextBlock)type.GetField("status",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Text=L.T("ScanSummary",scan.Files,scan.Bytes/1073741824d,0);Capture(window,"public-"+locale+"-storage");
            Theme.Apply(new AppPreferences{Theme=AppTheme.OneDark,ReduceMotion=true});
            var catalog=new RuleCatalog{Rules=Enumerable.Range(0,14).Select(i=>new CleanupRule{Id="example-cache-"+i,Name="Application cache "+(i+1),Names=new(){{"zh-CN","应用缓存 "+(i+1)}},Description="Example data for the screenshot. Review matched files before cleanup.",Descriptions=new(){{"zh-CN","截图使用示例数据, 处理前请核对匹配文件"}},Roots=[@"C:\Example\Cache"]}).ToList()};
            type.GetField("engine",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,new CleanupEngine(catalog));
            var plan=new CleanupPlan{Computer="EXAMPLE-PC",UserProfile=@"C:\Example\Profile",Files=Enumerable.Range(0,60).Select(i=>new Candidate("example-cache-"+(i%14),@"C:\Example\Cache",@"C:\Example\Cache\session-"+(i+1).ToString("D3")+".cache",new FileStamp(1048576L*(i+1),0,0,0,(ulong)i,1))).ToList()};
            type.GetField("cleanupPlan",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,plan);type.GetMethod("RenderRules",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
            window.Navigate("cleanup");Pump();((TextBlock)type.GetField("status",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!).Text=L.T("CatalogSummary",14);var table=Descendants(window).OfType<DataGrid>().Single(d=>d.Name=="cleanup_rules");
            foreach(var item in table.Items.Cast<object>().Take(3))item.GetType().GetProperty("Selected")!.SetValue(item,true);
            Capture(window,"public-"+locale+"-cleanup");
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void SelectedEditorGlyphsStayVisibleAndScrollbarThumbReachesBothEnds(AppTheme theme) => OnUi(() => {
        Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});
        var window=new ThemeWindow("Selection fixture",900,500){ShowInTaskbar=false};
        var input=new TextBox{Text="Selected text remains readable 0123456789",Width=480,Height=32,Margin=new Thickness(20),VerticalAlignment=VerticalAlignment.Top};
        window.Body.Children.Add(input);
        try {
            window.Show();window.Activate();input.Focus();input.SelectAll();Pump();window.UpdateLayout();
            Assert.Equal(((SolidColorBrush)Theme.Brush("SelectedText")).Color,((SolidColorBrush)input.SelectionTextBrush).Color);
            var bitmap=new RenderTargetBitmap(900,500,96,96,PixelFormats.Pbgra32);bitmap.Render(window);byte[] pixels=new byte[900*500*4];bitmap.CopyPixels(pixels,900*4,0);var origin=input.TranslatePoint(new Point(0,0),window);
            var ink=((SolidColorBrush)input.SelectionTextBrush).Color;int glyphPixels=0;
            for(int y=6;y<26;y++)for(int x=10;x<460;x++) {int offset=(((int)origin.Y+y)*900+(int)origin.X+x)*4;if(Math.Abs(pixels[offset]-ink.B)<25&&Math.Abs(pixels[offset+1]-ink.G)<25&&Math.Abs(pixels[offset+2]-ink.R)<25)glyphPixels++;}
            Capture(window,"selection-readable-"+theme);Assert.True(glyphPixels>100,$"Expected readable selected glyphs, found {glyphPixels} ink pixels.");
            window.Body.Children.Clear();
            var table=new DataGrid{IsReadOnly=true,AutoGenerateColumns=false};table.Columns.Add(new DataGridTextColumn{Header="Fixture",Binding=new System.Windows.Data.Binding("Name"),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
            table.ItemsSource=Enumerable.Range(0,150).Select(i=>new{Name="Row "+i}).ToArray();window.Body.Children.Add(table);Pump();window.UpdateLayout();
            var scroll=Descendants(table).OfType<ScrollViewer>().Single(s=>s.Name=="DG_ScrollViewer");scroll.ScrollToTop();Pump();
            var bar=Descendants(table).OfType<System.Windows.Controls.Primitives.ScrollBar>().Single(s=>s.Name=="PART_VerticalScrollBar");
            var track=(System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track",bar);
            Assert.InRange(track.Thumb.TranslatePoint(new Point(0,0),bar).Y,0,1);
            Assert.InRange(track.DecreaseRepeatButton.ActualHeight,0,1);
            scroll.ScrollToEnd();Pump();window.UpdateLayout();
            Assert.InRange(Math.Abs(track.Thumb.TranslatePoint(new Point(0,track.Thumb.ActualHeight),bar).Y-bar.ActualHeight),0,1);
            Capture(window,"scrollbar-endpoints-"+theme);
        } finally {window.Close();}
    });
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
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:Path.Combine(fixture,"data")){ShowInTaskbar=false};
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
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
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
    public void DiscoveryReportsNoResultsInThePageAndNormalLogsUseTheSelectedLanguage() => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Language="zh-CN",ReduceMotion=true,ChatAutoDiscover=false});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try {
            window.Show();window.Navigate("chats");Pump();Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,L.T("ArchiveCreate"))).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Pump();
            var task=(Task)typeof(ShellWindow).GetMethod("DiscoverChats",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[fixture])!;
            Wait(AsResult(task));Pump();
            var notice=Descendants(window).OfType<TextBlock>().Single(t=>t.Name=="account_discovery_notice");Assert.Equal(L.T("DiscoveryEmpty"),notice.Text);
            var log=Descendants(window).OfType<OperationPanel>().Single().Log;
            Assert.Contains(L.T("LogAccountsDone",0),log.Text);Assert.Contains(L.T("OperationCompletedLog"),log.Text);Assert.Contains(L.T("OperationStarting"),log.Text);
            Assert.False(ControlFeedback.GetEnabled(log));Capture(window,"discovery-empty");
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void CompactChromePopupsAndResizeTargetsRemainConsistent(AppTheme theme) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Theme=theme,Language="zh-CN",ReduceMotion=false});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
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
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:data){ShowInTaskbar=false};
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
            Assert.Equal("0",Query("document.querySelectorAll('details.source[open]').length"));
            Assert.Equal("0",Query("document.querySelectorAll('pre').length"));
            Assert.Equal("1",Query("document.querySelectorAll('.kind-image .placeholder').length"));
            Assert.False(core.Settings.AreHostObjectsAllowed);Assert.True(core.Settings.IsWebMessageEnabled);Assert.False(core.Settings.AreDefaultContextMenusEnabled);
            Query("document.querySelector('details.source').open=true;true");Pump();
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('pre').length")=="0"){Pump();Thread.Sleep(10);}
            Assert.Contains("<msg>",System.Text.Json.JsonSerializer.Deserialize<string>(Query("document.querySelector('pre').textContent"))!);
            using var stream=new MemoryStream();Wait(AsResult(core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream)));
            DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
            File.WriteAllBytes(Path.Combine(repo!.FullName,"artifacts/ui-review/wpf/reader-"+theme+".png"),stream.ToArray());
        } finally {window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void LongLogsKeepBothScrollbarsInsideTheViewportAndSplittersStayBounded(AppTheme theme) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Theme=theme,ReduceMotion=true});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};var completion=new TaskCompletionSource();Task? task=null;
        try {
            window.Show();Pump();task=(Task)typeof(ShellWindow).GetMethod("Run",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,new object[]{new Func<CancellationToken,Task>(_=>completion.Task)})!;
            var panel=Descendants(window).OfType<OperationPanel>().Single();for(int i=0;i<150;i++)panel.AddLog(new string('x',1200)+i);Pump();window.UpdateLayout();
            var viewer=Descendants(panel.Log).OfType<ScrollViewer>().Single(v=>v.Name=="PART_ContentHost");
            var vertical=Descendants(viewer).OfType<System.Windows.Controls.Primitives.ScrollBar>().Single(b=>b.Orientation==Orientation.Vertical);
            var horizontal=Descendants(viewer).OfType<System.Windows.Controls.Primitives.ScrollBar>().Single(b=>b.Orientation==Orientation.Horizontal);
            foreach(double offset in new[]{viewer.ScrollableHeight,0d}) {
                viewer.ScrollToVerticalOffset(offset);Pump();window.UpdateLayout();
                Assert.True(vertical.IsVisible&&horizontal.IsVisible);var v=vertical.TranslatePoint(new Point(),viewer);var h=horizontal.TranslatePoint(new Point(),viewer);
                Assert.InRange(v.Y+vertical.ActualHeight,0,h.Y+0.1);Assert.InRange(h.X+horizontal.ActualWidth,0,v.X+0.1);
            }
            panel.PauseFollowing();panel.Log.Text=string.Join('\n',Enumerable.Range(0,4000).Select(i=>"Fixture line "+i+new string('x',60)))+"\n"+new string('x',6000);Pump();window.UpdateLayout();
            var vt=Descendants(vertical).OfType<System.Windows.Controls.Primitives.Track>().Single();var ht=Descendants(horizontal).OfType<System.Windows.Controls.Primitives.Track>().Single();
            foreach(double fraction in new[]{0d,.5,1d}) {
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight*fraction);viewer.ScrollToHorizontalOffset(viewer.ScrollableWidth*fraction);Pump();window.UpdateLayout();
                var v=vt.Thumb.TranslatePoint(new Point(),vt);var h=ht.Thumb.TranslatePoint(new Point(),ht);
                Assert.InRange(v.Y,-.1,vt.ActualHeight);Assert.InRange(v.Y+vt.Thumb.ActualHeight,0,vt.ActualHeight+.1);
                Assert.InRange(h.X,-.1,ht.ActualWidth);Assert.InRange(h.X+ht.Thumb.ActualWidth,0,ht.ActualWidth+.1);
                Assert.True(vt.Thumb.ActualHeight>=23.9);Assert.True(ht.Thumb.ActualWidth>=23.9);
            }
            var logRow=(RowDefinition)typeof(ShellWindow).GetField("logRow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            logRow.Height=new GridLength(5000);Pump();window.UpdateLayout();
            Assert.True(panel.TranslatePoint(new Point(0,panel.ActualHeight),window).Y<=window.ActualHeight+.1,$"panelBottom={panel.TranslatePoint(new Point(0,panel.ActualHeight),window).Y}; window={window.ActualHeight}; body={window.Body.ActualHeight}; log={logRow.ActualHeight}; max={logRow.MaxHeight}; top={panel.TranslatePoint(new Point(),window).Y}");
            window.Height=650;Pump();window.UpdateLayout();Assert.InRange(logRow.ActualHeight,40,window.Body.ActualHeight-260);
            Capture(window,"bounded-log-"+theme);
        } finally {completion.TrySetResult();if(task!=null)Wait(AsResult(task));window.Close();Directory.Delete(fixture,true);}
    });
    [Fact]
    public void SidebarIconsRetainTheirVerticalPositionsThroughBothTransitions() => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Theme=AppTheme.OneDark});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try {
            window.Show();Pump();var navigation=Descendants(window).OfType<ListBox>().Single(l=>l.Name=="navigation");
            var icons=Descendants(navigation).OfType<System.Windows.Shapes.Path>().ToArray();var y=icons.Select(i=>i.TranslatePoint(new Point(),window).Y).ToArray();
            var toggle=Descendants(window).OfType<Button>().Single(b=>b.Name=="toggle_sidebar");
            for(int direction=0;direction<2;direction++) {
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                for(int frame=0;frame<20;frame++){Thread.Sleep(10);Pump();window.UpdateLayout();for(int i=0;i<icons.Length;i++)Assert.InRange(icons[i].TranslatePoint(new Point(),window).Y,y[i]-.1,y[i]+.1);}
            }
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingBusyWindowRequiresConsentAndTrayKeepsTheTaskAlive(bool background) => OnUi(() => {
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{CloseToTray=background,ReduceMotion=true});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};var completion=new TaskCompletionSource();Task? task=null;CancellationToken token=default;
        try {
            window.Show();Pump();task=(Task)typeof(ShellWindow).GetMethod("Run",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,new object[]{new Func<CancellationToken,Task>(ct=>{token=ct;ct.Register(()=>completion.TrySetCanceled(ct));return completion.Task;})})!;
            void Answer(bool yes) { _=Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
                var dialog=Application.Current.Windows.Cast<Window>().Single(w=>w.Owner==window);Assert.Contains(background?L.T("ConfirmBackgroundTask"):L.T("ConfirmExitTask"),Descendants(dialog).OfType<TextBlock>().Select(t=>t.Text));dialog.DialogResult=yes;
            }));window.Close();Pump(); }
            Answer(false);Assert.True(window.IsVisible);Assert.False(token.IsCancellationRequested);
            Answer(true);
            if(background) {
                Assert.False(window.IsVisible);Assert.False(token.IsCancellationRequested);
                typeof(ShellWindow).GetMethod("RestoreFromTray",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);Assert.True(window.IsVisible);
                completion.TrySetResult();Wait(AsResult(task));typeof(ShellWindow).GetMethod("RequestExit",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
            } else {Assert.True(token.IsCancellationRequested);Wait(AsResult(task));Pump();Assert.False(window.IsVisible);}
        } finally {
            completion.TrySetResult();if(task!=null)Wait(AsResult(task));if(window.IsVisible)typeof(ShellWindow).GetMethod("RequestExit",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);Directory.Delete(fixture,true);
        }
    });
    [Fact]
    public void RefreshedReaderShowsInlineMentionsNamesDecodedSourceAndUnknownTimes() => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);L.SetLanguage("en");
        byte[] V(ulong n){var bytes=new List<byte>();do{byte b=(byte)(n&127);n>>=7;bytes.Add((byte)(b|(n>0?128:0)));}while(n>0);return bytes.ToArray();}
        byte[] B(int field,byte[] value)=>[..V((ulong)(field*8+2)),..V((ulong)value.Length),..value];
        byte[] Part(string value)=>B(40800,[..V(45002*8),1,..B(45101,System.Text.Encoding.UTF8.GetBytes(value))]);
        byte[] mixed=[..Part("@Alex"),..Part(" hello")],system=B(40800,[..V(45002*8),8]);
        Database(Path.Combine(source,"nt_msg.db"),"CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\",\"40011\");INSERT INTO c2c_msg_table VALUES(1,2,1700000000,X'"+Convert.ToHexString(mixed)+"',1);INSERT INTO c2c_msg_table VALUES(1,0,0,X'"+Convert.ToHexString(system)+"',5);");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        var export=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(fixture,"reading"),ChatPlatform.QQ);
        ChatReaderRepair.Refresh(export.Directory,new Dictionary<string,string>{{"1","Fixture friend"},{"2","Fixture sender"}});
        var window=new ChatReaderWindow(export.Directory,Path.Combine(fixture,"data"),true){ShowInTaskbar=false};
        try {
            window.Show();DateTime until=DateTime.UtcNow.AddSeconds(30);while(window.Browser.CoreWebView2==null&&DateTime.UtcNow<until){Pump();Thread.Sleep(10);}
            Assert.True(window.Browser.CoreWebView2!=null,string.Join(" | ",window.Body.Children.OfType<TextBlock>().Select(t=>t.Text)));var core=window.Browser.CoreWebView2!;string Query(string script)=>Wait(core.ExecuteScriptAsync(script));
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.message').length")!="2"){Pump();Thread.Sleep(10);}
            Assert.Contains("Fixture friend",Query("document.querySelector('#chat-title').textContent"));
            Assert.Contains("Fixture sender",Query("document.querySelector('.message:not(.system) .timestamp').textContent"));
            Assert.Equal("true",Query("(()=>{let p=document.querySelectorAll('.message:not(.system) .message-text');return Math.abs(p[0].getBoundingClientRect().top-p[1].getBoundingClientRect().top)<1})()"));
            Assert.DoesNotContain("1970",Query("document.querySelector('#messages').textContent"));
            Assert.Contains("Time unavailable",Query("document.querySelector('.system').textContent"));
            Query("document.querySelector('.message:not(.system) .source').open=true;true");
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.source-bubble').length")!="1"){Pump();Thread.Sleep(10);}
            Assert.Contains("45101",Query("document.querySelector('.source-bubble').textContent"));
            Assert.Contains("@Alex",Query("document.querySelector('.source-bubble').textContent"));
        } finally {window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    [Fact]
    public void ReaderFiltersJumpPagesAndKeepFacesInlineAndResolvedPicturesBare() => OnUi(() => {
        Theme.Apply(new AppPreferences{Theme=AppTheme.OneDark,ReduceMotion=true});
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);L.SetLanguage("en");
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);"+string.Join("",Enumerable.Range(0,301).Select(i=>$"INSERT INTO MSG VALUES(1,{1700000000+i},'fixture','{(i==300?"Unique last-page fixture":"Fixture text") }');")));
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        var export=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(fixture,"reading"),ChatPlatform.WeChat);
        byte[] png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGP4/x8AAwAB//wl3FEAAAAASUVORK5CYII=");string image="assets/"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant()+".png";Directory.CreateDirectory(Path.Combine(export.Directory,"assets"));File.WriteAllBytes(Path.Combine(export.Directory,image),png);
        var window=new ChatReaderWindow(export.Directory,Path.Combine(fixture,"data"),true){ShowInTaskbar=false};
        try {
            window.Show();DateTime until=DateTime.UtcNow.AddSeconds(30);while(window.Browser.CoreWebView2==null&&DateTime.UtcNow<until){Pump();Thread.Sleep(10);}Assert.True(window.Browser.CoreWebView2!=null,string.Join(" | ",window.Body.Children.OfType<TextBlock>().Select(t=>t.Text)));var core=window.Browser.CoreWebView2!;string Query(string s)=>Wait(core.ExecuteScriptAsync(s));
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.message').length")!="300"){Pump();Thread.Sleep(10);}
            Assert.Equal("300",Query("document.querySelectorAll('.message').length"));
            Assert.Equal("true",Query("document.querySelector('#filter-panel').contains(document.querySelector('#message-search'))"));
            Query("document.querySelector('#sidebar-toggle').click();true");Assert.Equal("true",Query("document.body.classList.contains('sidebar-collapsed')"));
            Query("document.querySelector('#sidebar-toggle').click();true");Assert.Equal("false",Query("document.body.classList.contains('sidebar-collapsed')"));
            Assert.Equal("true",Query("!!document.querySelector('#page-bottom')&&!!document.querySelector('#auto-last')&&!!document.querySelector('#auto-bottom')"));
            Assert.Equal("true",Query("['page-bottom','auto-last','auto-bottom'].every(id=>document.querySelector('#'+id).closest('.page-footer'))"));
            Query("document.querySelector('#last').click();true");while(DateTime.UtcNow<until&&Query("document.querySelector('#page').textContent")!="\"2 / 2\""){Pump();Thread.Sleep(10);}Assert.Contains("2 / 2",Query("document.querySelector('#page').textContent"));
            Query("document.querySelector('#first').click();true");while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.message').length")!="300"){Pump();Thread.Sleep(10);}
            Query("document.querySelector('#message-search').value='Unique';document.querySelector('#message-search').dispatchEvent(new Event('input'));document.querySelector('#find-page').click();true");
            while(DateTime.UtcNow<until&&!Query("document.querySelector('#messages').textContent").Contains("Unique last-page fixture")){Pump();Thread.Sleep(10);}Assert.Contains("Unique last-page fixture",Query("document.querySelector('#messages').textContent"));
            Query("document.querySelector('.show-context').click();true");Assert.Equal("false",Query("document.querySelector('#back-results').hidden"));Assert.Equal("1",Query("document.querySelectorAll('.referenced').length"));
            Query("document.querySelector('#back-results').click();true");Assert.Equal("true",Query("document.querySelector('#back-results').hidden"));Assert.Equal("\"Unique\"",Query("document.querySelector('#message-search').value"));
            Query("document.querySelector('#reset-filter').click();true");
            string items=System.Text.Json.JsonSerializer.Serialize(new[]{new ChatMessage("QQ","direct:fixture","fixture",1700000000,2,""){Parts=[new("text","Hi "),new("sticker",Title:"#75"){Inline=true,EmojiId="75"},new("text"," friend")]},new ChatMessage("QQ","direct:fixture","fixture",1700000001,2,""){Parts=[new("image",Title:"internal-name.png",MediaKeys:["FIXTURE"]),new("image",MediaKeys:["duplicate"])],Media=[new(image,"image","internal-name.png"){Key="fixture"},new(image.Replace(".png",".gif"),"image","internal-name.gif"){Key="duplicate"}]}});
            Query("window.receivePage(window.archiveIndex.conversations[0].id,1,"+items+");true");
            Assert.Equal("1",Query("document.querySelectorAll('.kind-face').length"));Assert.Equal("true",Query("(()=>{const p=document.querySelectorAll('.message:first-child .message-text,.message:first-child .kind-face');return Math.abs(p[0].getBoundingClientRect().top-p[1].getBoundingClientRect().top)<8})()"));
            Assert.Equal("1",Query("document.querySelectorAll('.visual-only').length"));Assert.Equal("0",Query("document.querySelectorAll('.visual-only .kind-title,.visual-only strong').length"));
            Assert.Equal("1",Query("document.querySelectorAll('img.previewable').length"));
            Query("document.querySelector('#kind-filter').value='image';document.querySelector('#kind-filter').dispatchEvent(new Event('input'));true");Assert.Equal("1",Query("document.querySelectorAll('.message').length"));
            Query("document.querySelector('img.previewable').click();true");Assert.Equal("true",Query("document.querySelector('#image-preview').open"));
            Assert.Equal("both",Query("getComputedStyle(document.querySelector('#image-preview')).resize").Trim('"'));
            Assert.Equal("true",Query("document.querySelector('#image-preview').getBoundingClientRect().width<=780"));
            Assert.Equal("true",Query("document.querySelector('#image-previous').parentElement===document.querySelector('#image-next').parentElement"));
            Query("(()=>{const p=document.querySelector('#image-preview'),box=p.getBoundingClientRect();p.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,clientX:box.right-2,clientY:box.bottom-2}));p.style.width='620px';p.style.height='440px';const resized=p.getBoundingClientRect();p.dispatchEvent(new MouseEvent('click',{bubbles:true,clientX:resized.right-2,clientY:resized.bottom-2}));})()");
            Assert.Equal("true",Query("document.querySelector('#image-preview').open"));Assert.Equal("620",Query("document.querySelector('#image-preview').getBoundingClientRect().width"));
            Query("document.querySelector('#image-stage').dispatchEvent(new WheelEvent('wheel',{deltaY:-200,clientX:200,clientY:200,cancelable:true}));true");Assert.DoesNotContain("100%",Query("document.querySelector('#zoom-value').textContent"));
            Query("document.querySelector('#zoom-original').click();true");Assert.Contains("100%",Query("document.querySelector('#zoom-value').textContent"));
            Query("document.querySelector('#rotate-right').click();true");Assert.Contains("rotate(90deg)",Query("document.querySelector('#preview-image').style.transform"));
            Query("document.querySelector('#rotate-left').click();true");Assert.Contains("rotate(0deg)",Query("document.querySelector('#preview-image').style.transform"));
            BitmapSource? copiedBitmap=null;
            typeof(ChatReaderWindow).GetProperty("ImageClipboardWriter",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,new Action<BitmapSource>(bitmap=>copiedBitmap=bitmap));
            Query("document.querySelector('#image-copy').click();true");var copiedUntil=DateTime.UtcNow.AddSeconds(5);while(DateTime.UtcNow<copiedUntil&&!Query("document.querySelector('#action-status').textContent").Contains("Copied")){Pump();Thread.Sleep(10);}
            Assert.Contains("Copied",Query("document.querySelector('#action-status').textContent"));Assert.NotNull(copiedBitmap);Assert.True(copiedBitmap!.IsFrozen);Assert.Equal(1,copiedBitmap.PixelWidth);Assert.Equal(1,copiedBitmap.PixelHeight);
            Query("document.querySelector('#preview-image').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:100,clientY:100}));true");Assert.Equal("false",Query("document.querySelector('#reader-menu').hidden"));Assert.Contains("Save image",Query("document.querySelector('#reader-menu').textContent"));
            Query("document.querySelector('#image-close').click();true");Assert.Equal("false",Query("document.querySelector('#image-preview').open"));
            Query("document.querySelector('#reset-filter').click();true");
            byte[] variantPng;
            using(var stream=new MemoryStream()){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,255,255,0,255,0,255},8)));encoder.Save(stream);variantPng=stream.ToArray();}
            string variant="assets/"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(variantPng)).ToLowerInvariant()+".png";File.WriteAllBytes(Path.Combine(export.Directory,variant),variantPng);
            byte[] wave=Convert.FromHexString("524946462600000057415645666D74201000000001000100401F0000803E00000200100064617461020000000000");
            string voice="assets/"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wave)).ToLowerInvariant()+".wav";File.WriteAllBytes(Path.Combine(export.Directory,voice),wave);
            const string logical="0123456789abcdef0123456789abcdef";
            string mediaItems=System.Text.Json.JsonSerializer.Serialize(new[]{
                new ChatMessage("QQ","direct:fixture","fixture",1700000000,2,""){Identity=new("fixture","direct:fixture",Nickname:"Fixture nickname",FriendRemark:"Fixture remark",GroupCard:"Fixture card",Avatar:image),Parts=[new("image",MediaKeys:[logical])],Media=[new(image,"image",logical+".jpg"){Key=logical},new(variant,"image",logical+".png"){Key=logical}]},
                new ChatMessage("QQ","direct:fixture","fixture",1700000001,2,""){Parts=[new("audio",Title:"internal-voice.amr",MediaKeys:["voice"])],Media=[new(voice,"audio","internal-voice.amr"){Key="voice"}]}
            });
            Query("window.receivePage(window.archiveIndex.conversations[0].id,1,"+mediaItems+");true");
            Assert.Equal("1",Query("document.querySelectorAll('img.previewable').length"));Assert.Equal("1",Query("document.querySelectorAll('.image-source-switch').length"));
            Assert.Equal("true",Query("(()=>{const a=document.querySelector('.message-tools .source').getBoundingClientRect(),b=document.querySelector('.image-source-tools').getBoundingClientRect();return b.left-a.right<35})()"));
            Query("document.querySelector('.avatar[data-profile]').click();true");Assert.Equal("true",Query("document.querySelector('.identity-details').open"));
            Assert.Contains("Fixture nickname",Query("document.querySelector('.identity-panel').textContent"));Assert.Contains("Fixture remark",Query("document.querySelector('.identity-panel').textContent"));
            Query("document.body.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true}));true");Assert.Equal("false",Query("document.querySelector('.identity-details').open"));
            Query("document.querySelector('.avatar[data-profile]').click();document.querySelector('.identity-portrait').click();true");Assert.Equal("true",Query("document.querySelector('#image-preview').open"));
            Query("document.querySelector('#image-close').click();document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));true");Assert.Equal("false",Query("document.querySelector('.identity-details').open"));

            Query("document.querySelector('.image-source-switch').click();true");Assert.Contains(variant,Query("document.querySelector('img.previewable').dataset.asset"));
            Query("document.querySelector('.image-source-all').click();true");Assert.Equal("2",Query("document.querySelectorAll('img.previewable').length"));
            Query("document.querySelector('.image-source-all').click();true");Assert.Equal("1",Query("document.querySelectorAll('img.previewable').length"));
            Assert.Equal("1",Query("document.querySelectorAll('.audio-only audio').length"));Assert.Equal("0",Query("document.querySelectorAll('.audio-only .kind-title,.audio-only strong').length"));
            Assert.Equal("true",Query("document.querySelector('.audio-only').getBoundingClientRect().width<330"));
            Query("document.querySelector('.audio-only').closest('.content').querySelector('.source').open=true;true");Pump();
            Assert.Equal("true",Query("(()=>{const c=document.querySelector('.audio-only').closest('.content');return c.querySelector('.source').getBoundingClientRect().width>c.querySelector('.bubble').getBoundingClientRect().width+100})()"));

            Query("document.querySelector('#reset-filter').click();document.querySelector('#filter-panel').open=true;true");Pump();
            using var screenshot=new MemoryStream();Wait(AsResult(core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,screenshot)));
            DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
            File.WriteAllBytes(Path.Combine(repo!.FullName,"artifacts/ui-review/wpf/reader-inline-faces-filters.png"),screenshot.ToArray());
        } finally {window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    [Fact]
    public void ReaderSearchScopesKeepBoundedResultsContextAndCompactSelectableControls() => OnUi(() => {
        Theme.Apply(new AppPreferences{Theme=AppTheme.OneDark,ReduceMotion=true});
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);L.SetLanguage("en");
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);"+string.Join("",Enumerable.Range(0,201).Select(i=>$"INSERT INTO MSG VALUES(1,{1700000000+i},'alpha','needle {i}');"))+"INSERT INTO MSG VALUES(1,1700000300,'beta','needle beta'),(1,1700000301,'beta','needle beta two');");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        var export=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(fixture,"reading"),ChatPlatform.WeChat);
        var window=new ChatReaderWindow(export.Directory,Path.Combine(fixture,"data"),true){ShowInTaskbar=false};
        try {
            window.Show();DateTime until=DateTime.UtcNow.AddSeconds(30);while(window.Browser.CoreWebView2==null&&DateTime.UtcNow<until){Pump();Thread.Sleep(10);}Assert.True(window.Browser.CoreWebView2!=null,string.Join(" | ",window.Body.Children.OfType<TextBlock>().Select(t=>t.Text)));var core=window.Browser.CoreWebView2!;string Query(string q)=>Wait(core.ExecuteScriptAsync(q));
            void Until(string q,string expected){var limit=DateTime.UtcNow.AddSeconds(20);while(DateTime.UtcNow<limit&&Query(q)!=expected){Pump();Thread.Sleep(10);}Assert.Equal(expected,Query(q));}
            Until("document.querySelectorAll('.message').length","201");
            Assert.Equal("false",Query("document.querySelector('#filter-panel').open"));
            Assert.Equal("0",Query("document.querySelector('#filter-panel').getBoundingClientRect().height"));
            Query("document.querySelector('#filter-toggle').click();true");
            Assert.Equal("true",Query("document.querySelector('#filter-panel').getBoundingClientRect().height>0"));
            Query("document.querySelector('#filter-toggle').click();true");
            Assert.Equal("0",Query("document.querySelector('#filter-panel').getBoundingClientRect().height"));
            Assert.Equal("true",Query("Math.abs(document.querySelector('main').getBoundingClientRect().right-document.querySelector('#filter-toggle').getBoundingClientRect().right-12)<2"));
            Assert.Equal("true",Query("(()=>{const b=document.querySelector('#sidebar-toggle').getBoundingClientRect(),t=document.querySelector('#chat-title').getBoundingClientRect(),m=document.querySelector('#chat-meta').getBoundingClientRect();return Math.abs(b.top+b.height/2-t.top-t.height/2)<5&&Math.abs(t.bottom-m.bottom)<6&&document.querySelector('.chat-header').getBoundingClientRect().height<60})()"));
            Assert.Equal("true",Query("Math.abs(document.querySelector('.pagination').getBoundingClientRect().left-document.querySelector('main').getBoundingClientRect().left-12)<2"));
            Query("document.querySelector('#filter-toggle').click();document.querySelector('#message-search').value='selectable content';true");
            window.Activate();window.Browser.Focus();Pump();Query("document.querySelector('#message-search').focus();true");
            using(var rect=System.Text.Json.JsonDocument.Parse(Query("(()=>{const r=document.querySelector('#message-search').getBoundingClientRect();return {x:r.left+10,y:r.top+r.height/2}})()"))){double x=rect.RootElement.GetProperty("x").GetDouble(),y=rect.RootElement.GetProperty("y").GetDouble();
                string Mouse(string type,double px,int buttons)=>System.Text.Json.JsonSerializer.Serialize(new{type,x=px,y,button="left",buttons,clickCount=1});
                Wait(core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",Mouse("mousePressed",x,1)));
                for(int i=1;i<=8;i++)Wait(core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",Mouse("mouseMoved",x+12*i,1)));
                Wait(core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",Mouse("mouseReleased",x+96,0)));
            }
            Assert.Equal("true",Query("(()=>{const i=document.querySelector('#message-search');return i.selectionEnd>i.selectionStart})()"));
            Query("document.querySelector('#sender-filter').value='selectable sender';document.querySelector('#sender-filter').focus();document.querySelector('#sender-filter').select();true");
            Assert.Equal("true",Query("document.querySelector('#sender-filter').selectionEnd>document.querySelector('#sender-filter').selectionStart"));
            Assert.Equal("true",Query("(()=>{const a=document.querySelector('.filter-actions').getBoundingClientRect(),p=document.querySelector('#filter-panel').getBoundingClientRect();return p.right-a.right<15})()"));
            Query("document.querySelector('#reset-filter').click();document.querySelector('#search-scope').value='archive';document.querySelector('#search-scope').dispatchEvent(new Event('change'));document.querySelector('#message-search').value='needle';document.querySelector('#message-search').dispatchEvent(new Event('input'));document.querySelector('#find-page').click();true");
            Until("document.querySelectorAll('.result-location').length","100");Assert.Contains("Entire archive",Query("document.querySelector('#chat-title').textContent"));
            Query("document.querySelector('#next').click();true");Until("document.querySelector('#page').textContent","\"Results - batch 2\"");
            string first=Query("document.querySelector('.message').id");
            Query("document.querySelector('.show-context').click();true");Until("document.querySelectorAll('.message').length","201");Assert.Equal("1",Query("document.querySelectorAll('.referenced').length"));
            Query("document.querySelector('#back-results').click();true");Until("document.querySelectorAll('.result-location').length","100");Assert.Equal(first,Query("document.querySelector('.message').id"));
            Query("document.querySelector('#next').click();true");Until("document.querySelectorAll('.result-location').length","3");
            Assert.Contains("beta",Query("document.querySelector('#messages').textContent"));
            Query("document.querySelectorAll('.show-context')[1].click();true");Until("document.querySelectorAll('.message').length","2");
            Query("document.querySelector('#back-results').click();true");Until("document.querySelectorAll('.result-location').length","3");
            Query("document.querySelector('#first').click();true");Until("document.querySelectorAll('.result-location').length","100");
            Query("document.querySelector('#filter-toggle').click();document.querySelector('#message-search').value='absent';document.querySelector('#message-search').dispatchEvent(new Event('input'));document.querySelector('#find-page').click();document.querySelector('#stop-search').click();true");
            Assert.Equal("false",Query("document.querySelector('#find-page').disabled"));Assert.Contains("stopped",Query("document.querySelector('#filter-status').textContent"));
            Query("document.querySelector('#reset-filter').click();true");Until("document.querySelectorAll('.message').length","201");
            Query("document.querySelector('#search-scope').value='conversation';document.querySelector('#search-scope').dispatchEvent(new Event('change'));document.querySelector('#page-size').value='100';document.querySelector('#page-size').dispatchEvent(new Event('change'));true");Until("document.querySelectorAll('.message').length","100");
            Assert.Equal("\"1 / 3\"",Query("document.querySelector('#page').textContent"));
            Query("document.querySelector('#next').click();true");Until("document.querySelector('#page').textContent","\"2 / 3\"");
            Query("document.querySelector('#find-previous').click();true");Until("document.querySelector('#page').textContent","\"1 / 3\"");
            Query("document.querySelector('#minimap-toggle').click();true");Until("document.querySelector('#minimap-canvas').width>0","true");
            Assert.Equal("true",Query("document.querySelector('#messages').getBoundingClientRect().right<=document.querySelector('#minimap').getBoundingClientRect().left+1"));
            Query("document.querySelector('#minimap-collapse').click();true");Assert.Equal("true",Query("document.querySelector('#minimap').classList.contains('collapsed')&&document.querySelector('#minimap-canvas').width>0"));
            Assert.Equal("true",Query("document.querySelector('#messages').getBoundingClientRect().right<=document.querySelector('#minimap').getBoundingClientRect().left+1"));
            Query("document.querySelector('#page-size').value='125';document.querySelector('#page-size').dispatchEvent(new Event('change'));true");Until("document.querySelectorAll('.message').length","125");
            Query("document.querySelector('#page-size').value='500';document.querySelector('#page-size-toggle').click();true");
            Assert.Equal("6",Query("document.querySelectorAll('#page-sizes button').length"));
            Assert.Equal("true",Query("!document.querySelector('#page-sizes').hidden&&document.querySelector('#page-sizes').getBoundingClientRect().bottom<document.querySelector('#page-size').getBoundingClientRect().top"));
            Assert.Equal("true",Query("document.querySelector('#page-size').getBoundingClientRect().width>=88"));
            Query("document.querySelector('#page-sizes button:nth-child(2)').click();true");Until("document.querySelectorAll('.message').length","201");
            Assert.Equal("true",Query("document.querySelector('#page-sizes').hidden"));
            Query("document.querySelector('#palette').value='qq';document.querySelector('#palette').dispatchEvent(new Event('change'));if(document.body.classList.contains('dark'))document.querySelector('#theme').click();true");
            Until("document.querySelector('#palette').value","\"qq\"");
            Pump();Query("location.reload();true");Until("document.querySelectorAll('.message').length","201");
            Assert.Equal("\"qq\"",Query("document.querySelector('#palette').value"));Assert.Equal("false",Query("document.body.classList.contains('dark')"));

            Query("document.querySelector('#minimap-close').click();true");Assert.Equal("true",Query("document.querySelector('#minimap').hidden&&document.querySelector('#minimap-canvas').width===0"));
            Query("document.querySelector('#chat-title').click();true");Assert.Equal("true",Query("document.querySelector('#conversation-details').open"));Assert.Contains("Archived senders",Query("document.querySelector('#conversation-details-body').textContent"));
            Query("document.querySelector('#conversation-details-close').click();document.querySelector('#page-size').value='300';document.querySelector('#page-size').dispatchEvent(new Event('change'));true");Until("document.querySelectorAll('.message').length","201");
            window.Width=500;Pump();Until("innerWidth<640","true");Query("if(!document.querySelector('#filter-panel').open)document.querySelector('#filter-toggle').click();true");Pump();
            Assert.Equal("true",Query("document.querySelector('.header-actions').getBoundingClientRect().right<=document.querySelector('main').getBoundingClientRect().right+1"));
            Assert.Equal("true",Query("document.querySelector('#filter-panel').getBoundingClientRect().top>=document.querySelector('.chat-header').getBoundingClientRect().bottom"));
            window.Width=1200;Pump();
            using var screenshot=new MemoryStream();Wait(AsResult(core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,screenshot)));
            DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
            File.WriteAllBytes(Path.Combine(repo!.FullName,"artifacts/ui-review/wpf/reader-compact-search.png"),screenshot.ToArray());
        }finally{window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    [Theory]
    [InlineData(AppTheme.Light,"en")] [InlineData(AppTheme.Dark,"en")] [InlineData(AppTheme.OneDark,"en")] [InlineData(AppTheme.OneDark,"zh-CN")]
    public void AccountArchiveHomeExposesReadingUpdateAndProtectionWithoutSetupSteps(AppTheme theme,string locale)=>OnUi(()=>{
        string fixture=Fixture(),source=Path.Combine(fixture,"account"),data=Path.Combine(fixture,"data");Directory.CreateDirectory(source);Directory.CreateDirectory(data);
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','managed archive fixture');");
        var project=new ChatArchiveProject(ChatArchiveProject.Identity(ChatPlatform.WeChat,source),"Fixture account",ChatPlatform.WeChat,source,Path.Combine(fixture,"backups"));
        project=ChatArchiveWorkflow.Update(project,Path.Combine(data,"task.json"),null,null,2,_=>{},checkRunningClients:false);
        project=project with{BackedUpAt=DateTimeOffset.FromUnixTimeSeconds(1700000000),ReadableAt=DateTimeOffset.FromUnixTimeSeconds(1700000000)};JsonFiles.Write(Path.Combine(data,"chat-archives.json"),new[]{project});JsonFiles.Write(Path.Combine(data,"preferences.json"),new AppPreferences{Theme=theme,Language=locale,ReduceMotion=true});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:data){ShowInTaskbar=false};try{window.Show();window.Navigate("chats");Pump();
            var table=Descendants(window).OfType<DataGrid>().Single(t=>t.Name=="managed_archives");Assert.Single(table.Items.Cast<object>());
            foreach(string name in new[]{"archive_open","archive_update","archive_backup"})Assert.True(Descendants(window).OfType<Button>().Single(b=>b.Name==name).IsEnabled);
            var materials=Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,L.T("AccountMaterials"))).ContextMenu!;
            foreach(string name in new[]{"archive_protection","archive_verify_now"})Assert.True(materials.Items.OfType<MenuItem>().Single(i=>i.Name==name).IsEnabled);
            Assert.DoesNotContain(Descendants(window).OfType<DataGrid>(),t=>t.Name=="chat_accounts"&&t.IsVisible);
            Capture(window,"archive-home-"+theme);
            if(theme==AppTheme.OneDark){var detail=Descendants(window).OfType<TextBlock>().First(t=>t.Text.Contains(project.Source,StringComparison.Ordinal));detail.Text=L.T("ArchiveDetail",@"D:\Example\Account",@"E:\Example\Archives\Reading")+"\n"+L.T("ArchiveRawProtectionNotice");Capture(window,"public-"+locale+"-archives");}
            var prepare=(Task)typeof(ShellWindow).GetMethod("PrepareArchiveUpdate",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,new object?[]{project,null})!;Wait(AsResult(prepare));Pump();
            var verification=Descendants(window).OfType<CheckBox>().Single(c=>c.Name=="archive_verify_before");Assert.True(verification.IsChecked);
            verification.IsChecked=false;verification.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(Assert.Single(JsonFiles.Read<ChatArchiveProject[]>(Path.Combine(data,"chat-archives.json"))).VerifyBackupBeforeReading);
            var selected=(ChatArchiveProject)typeof(ShellWindow).GetMethod("SelectedArchive",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,null)!;Assert.False(selected.VerifyBackupBeforeReading);
            string pendingFile=Path.Combine(data,"archive-tasks",project.Id+".json");Directory.CreateDirectory(Path.GetDirectoryName(pendingFile)!);
            JsonFiles.Write(pendingFile,new ChatArchiveTask(1,project.Id,"fixture-work","fixture-generation",project.Reading,project.Manifest,"backup",false));
            typeof(ShellWindow).GetMethod("SaveArchive",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,new object[]{selected with{TaskFile=pendingFile}});
            verification.IsChecked=false;verification.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Assert.False(JsonFiles.Read<ChatArchiveTask>(pendingFile).VerifyBackupBeforeReading);
            verification.IsChecked=true;verification.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Assert.True(JsonFiles.Read<ChatArchiveTask>(pendingFile).VerifyBackupBeforeReading);
            typeof(ShellWindow).GetMethod("SaveArchive",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,new object[]{selected});
            Capture(window,"archive-update-options-"+theme);
            Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,L.T("ArchiveCreate"))).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Pump();Assert.True(Descendants(window).OfType<DataGrid>().Single(t=>t.Name=="chat_accounts").IsVisible);
        }finally{window.Close();Directory.Delete(fixture,true);}
    });
    [Fact]
    public void ArchiveCatalogAdoptsVerifiedBackupWithoutMovingOriginalData()=>OnUi(()=>{
        string fixture=Fixture(),source=Path.Combine(fixture,"account"),data=Path.Combine(fixture,"data");Directory.CreateDirectory(source);Directory.CreateDirectory(data);
        Database(Path.Combine(source,"nt_msg.db"),"CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'catalog import fixture');");
        var transfer=new TransferService();var backup=transfer.Execute(transfer.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));string reading=Path.Combine(fixture,"raw-reading-legacy");new ChatExporter().ExportVerifiedBackup(backup.Manifest,reading,ChatPlatform.QQ);
        var stamp=NativeFile.ReadStamp(Path.Combine(reading,"messages.jsonl"));JsonFiles.Write(Path.Combine(data,"chat-backups.json"),new Dictionary<string,string>{{source,backup.Manifest}});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:data){ShowInTaskbar=false};try{window.Show();Pump();var project=Assert.Single(JsonFiles.Read<ChatArchiveProject[]>(Path.Combine(data,"chat-archives.json")));Assert.Equal(ChatPlatform.QQ,project.Platform);Assert.Equal(reading,project.Reading);Assert.True(PathPolicy.Under(project.BackupRoot,fixture));Assert.Equal(stamp,NativeFile.ReadStamp(Path.Combine(reading,"messages.jsonl")));}
        finally{window.Close();Directory.Delete(fixture,true);}
    });
    [Fact]
    public void ClipboardImagesRequireBoundedPngData() => OnUi(() => {
        string Encode(uint width=2,uint height=1){var bitmap=BitmapSource.Create(2,1,96,96,PixelFormats.Bgra32,null,new byte[]{0,0,255,255,0,255,0,255},8);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=new MemoryStream();encoder.Save(output);byte[] data=output.ToArray();System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16,4),width);System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20,4),height);return Convert.ToBase64String(data);}
        BitmapSource Decode(string? encoded){try{return (BitmapSource)typeof(ChatReaderWindow).GetMethod("DecodeClipboardImage",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[encoded])!;}catch(TargetInvocationException ex){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();throw;}}
        var image=Decode(Encode());Assert.Equal(2,image.PixelWidth);Assert.Equal(1,image.PixelHeight);Assert.True(image.IsFrozen);
        Assert.Throws<InvalidDataException>(()=>Decode(null));Assert.Throws<InvalidDataException>(()=>Decode("not base64"));Assert.Throws<InvalidDataException>(()=>Decode(Convert.ToBase64String(new byte[40])));Assert.Throws<InvalidDataException>(()=>Decode(Encode(uint.MaxValue,2)));
    });
    [Fact]
    public void ReaderCanSaveImagesAndFilesToNewDestinations() {
        string fixture=Fixture();try{
            string source=Path.Combine(fixture,"original.png"),target=Path.Combine(fixture,"saved.png");byte[] bytes=[137,80,78,71,13,10,26,10];File.WriteAllBytes(source,bytes);
            var save=typeof(ChatReaderWindow).GetMethod("SaveResourceFile",BindingFlags.Static|BindingFlags.NonPublic)!;
            save.Invoke(null,[source,target]);Assert.Equal(bytes,File.ReadAllBytes(target));Assert.Equal(bytes,File.ReadAllBytes(source));
            File.WriteAllText(target,"old fixture");save.Invoke(null,[source,target]);Assert.Equal(bytes,File.ReadAllBytes(target));
            save.Invoke(null,[source,source]);Assert.Equal(bytes,File.ReadAllBytes(source));
        }finally{Directory.Delete(fixture,true);}
    }
    [Fact]
    public void ReaderImageExportAcceptsOnlyExistingHashedLocalImages() {
        string fixture=Fixture();try {
            string Resolve(string relative){try{return (string)typeof(ChatReaderWindow).GetMethod("ResolveImagePath",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[fixture,relative])!;}catch(TargetInvocationException ex){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();throw;}}
            Directory.CreateDirectory(Path.Combine(fixture,"assets"));string name=new string('a',64)+".png",path=Path.Combine(fixture,"assets",name);File.WriteAllBytes(path,[1,2,3]);
            Assert.Equal(path,Resolve("assets/"+name));
            foreach(string relative in new[]{"../private.png","assets/../../private.png","https://example.invalid/image.png","assets/"+new string('a',64)+".exe"})Assert.Throws<InvalidDataException>(()=>Resolve(relative));
            Assert.Throws<FileNotFoundException>(()=>Resolve("assets/"+new string('b',64)+".png"));
        }finally{Directory.Delete(fixture,true);}
    }

    [Fact]
    public void ReaderJumpsAcrossPagesAndKeepsSystemAndSourceContentOutsideOrdinaryBubbles() => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);Theme.Apply(new AppPreferences{Theme=AppTheme.OneDark,ReduceMotion=true});L.SetLanguage("en");
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent,MsgSvrID);"+
            "INSERT INTO MSG VALUES(10000,1700000000,'fixture','Alex invited Sam',100);"+
            "INSERT INTO MSG VALUES(1,1700000001,'fixture','Original target',101);"+
            "WITH RECURSIVE seq(x) AS(VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<299) INSERT INTO MSG SELECT CASE WHEN x=250 THEN 49 ELSE 1 END,1700000001+x,'fixture',CASE WHEN x=250 THEN '<msg><appmsg><type>57</type><title>Same-page reply</title><refermsg><svrid>101</svrid><content>Original target</content><type>1</type></refermsg></appmsg></msg>' ELSE 'Filler text' END,101+x FROM seq;"+
            "INSERT INTO MSG VALUES(49,1700000401,'fixture','<msg><appmsg><type>57</type><title>Cross-page reply</title><refermsg><svrid>101</svrid><displayname>Alex</displayname><content>Original target</content><type>1</type></refermsg></appmsg></msg>',900);"+
            "INSERT INTO MSG VALUES(49,1700000402,'fixture','<msg><appmsg><type>19</type><recorditem>&lt;recordinfo&gt;&lt;datalist&gt;&lt;dataitem datatype=\"1\"&gt;&lt;sourcename&gt;Sam&lt;/sourcename&gt;&lt;datadesc&gt;Forwarded fixture&lt;/datadesc&gt;&lt;/dataitem&gt;&lt;/datalist&gt;&lt;/recordinfo&gt;</recorditem></appmsg></msg>',901);");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        var export=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(fixture,"reading"),ChatPlatform.WeChat);
        var window=new ChatReaderWindow(export.Directory,Path.Combine(fixture,"data"),true){ShowInTaskbar=false};
        try {
            window.Show();DateTime until=DateTime.UtcNow.AddSeconds(30);while(window.Browser.CoreWebView2==null&&DateTime.UtcNow<until){Pump();Thread.Sleep(10);}
            Assert.True(window.Browser.CoreWebView2!=null,string.Join(" | ",window.Body.Children.OfType<TextBlock>().Select(t=>t.Text)));var core=window.Browser.CoreWebView2!;string Query(string script)=>Wait(core.ExecuteScriptAsync(script));
            void Until(string expression,string expected){while(DateTime.UtcNow<until&&Query(expression)!=expected){Pump();Thread.Sleep(10);}Assert.Equal(expected,Query(expression));}
            Until("document.querySelectorAll('.message.system').length","1");
            Query("document.querySelector('.system .source').open=true;true");Pump();
            Assert.Equal("true",Query("(()=>{const a=document.querySelector('.system'),b=a.querySelector('.source-bubble');const x=a.getBoundingClientRect(),y=b.getBoundingClientRect();return Math.abs((x.left+x.right-y.left-y.right)/2)<2})()"));
            Query("document.querySelector('.system .source').open=false;true");Assert.Equal("0",Query("document.querySelectorAll('.system .avatar').length"));
            Query("window.readerOriginalScrollIntoView=Element.prototype.scrollIntoView;Element.prototype.scrollIntoView=function(options){window.readerJumpStart=document.querySelector('#messages').scrollTop;window.readerJumpAnchorTop=window.readerOrigin.getBoundingClientRect().top;window.readerJumpBehavior=options.behavior;return window.readerOriginalScrollIntoView.call(this,options);};window.readerOrigin=document.querySelector('blockquote').closest('.message');document.querySelector('#messages').scrollTop=window.readerOrigin.offsetTop-100;window.readerOriginTop=window.readerOrigin.getBoundingClientRect().top;true");
            double start=double.Parse(Query("document.querySelector('#messages').scrollTop"),System.Globalization.CultureInfo.InvariantCulture);Assert.True(start>1000);
            Query("document.querySelector('blockquote').click();true");Until("document.querySelectorAll('.referenced').length","1");
            Assert.True(double.Parse(Query("window.readerJumpStart"),System.Globalization.CultureInfo.InvariantCulture)>1000);Assert.Equal(Query("window.readerOriginTop"),Query("window.readerJumpAnchorTop"));Assert.Equal("true",Query("window.readerOrigin.isConnected"));
            Query("document.querySelector('#back-results').click();true");
            Assert.Equal("0",Query("document.querySelectorAll('.bubble .source').length"));Query("document.getElementById('next').click();true");
            Until("document.querySelectorAll('.forwarded').length","1");Assert.Contains("Forwarded fixture",Query("document.querySelector('.forwarded').textContent"));
            Query("document.querySelector('blockquote').click();true");Until("document.querySelectorAll('.referenced').length","1");
            Assert.Contains("Original target",Query("document.querySelector('.referenced .bubble').textContent"));Assert.Contains("1 / 2",Query("document.getElementById('page').textContent"));
            Assert.Equal("\"instant\"",Query("window.readerJumpBehavior"));Query("Element.prototype.scrollIntoView=window.readerOriginalScrollIntoView;true");
            Assert.Equal("0",Query("document.querySelectorAll('pre').length"));Query("document.querySelector('.referenced .source').open=true;true");
            Until("document.querySelectorAll('.source-bubble').length","1");Assert.Equal("0",Query("document.querySelectorAll('.bubble details').length"));
            using var stream=new MemoryStream();Wait(AsResult(core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream)));
            DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
            File.WriteAllBytes(Path.Combine(repo!.FullName,"artifacts/ui-review/wpf/reader-reference-dark.png"),stream.ToArray());
        } finally {window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void ReaderPalettesSupportBothBrightnessModesAndRememberTheScheme(string locale) => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);L.SetLanguage(locale);Theme.Apply(new AppPreferences{Theme=AppTheme.Light,ReduceMotion=true});
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent,IsSender);"+
            "INSERT INTO MSG VALUES(1,1700000000,'Alex','A compact conversation preview.',0),(1,1700000001,'Alex','This is a reply from me.',1),(10000,1700000002,'Alex','Alex invited Sam',0),"+
            "(3,1700000003,'Alex','<msg><img md5=\"0123456789abcdef0123456789abcdef\"/></msg>',0);");
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(new RenderTargetBitmap(8,8,96,96,PixelFormats.Pbgra32)));
        using(var file=File.Create(Path.Combine(source,"0123456789abcdef0123456789abcdef.png")))png.Save(file);
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        var export=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(fixture,"reading"),ChatPlatform.WeChat);
        // Existing archives keep their files. The host must supply the current UI and missing translated labels.
        File.WriteAllText(Path.Combine(export.Directory,"messages.html"),"<!doctype html><title>Older archive fixture</title>");
        File.WriteAllText(Path.Combine(export.Directory,"reader.js"),"throw new Error('Older asset must remain untouched');");
        File.WriteAllText(Path.Combine(export.Directory,"reader.css"),"body{background:white}");
        string indexFile=Path.Combine(export.Directory,"archive-index.js");string oldIndex=File.ReadAllText(indexFile);
        oldIndex=System.Text.RegularExpressions.Regex.Replace(oldIndex,"\\\"ReaderPalette[^\\\"]*\\\":\\\"[^\\\"]*\\\",?","");File.WriteAllText(indexFile,oldIndex);
        var window=new ChatReaderWindow(export.Directory,Path.Combine(fixture,"data"),true){ShowInTaskbar=false};
        try {
            window.Show();DateTime until=DateTime.UtcNow.AddSeconds(30);while(window.Browser.CoreWebView2==null&&DateTime.UtcNow<until){Pump();Thread.Sleep(10);}
            Assert.True(window.Browser.CoreWebView2!=null,string.Join(" | ",window.Body.Children.OfType<TextBlock>().Select(t=>t.Text)));var core=window.Browser.CoreWebView2!;string Query(string script)=>Wait(core.ExecuteScriptAsync(script));
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.message').length")!="4"){Pump();Thread.Sleep(10);}
            Assert.Equal("4",Query("document.querySelectorAll('.message').length"));
            while(DateTime.UtcNow<until&&Query("document.querySelector('.bubble img')?.naturalWidth")!="8"){Pump();Thread.Sleep(10);}
            Assert.Equal("8",Query("document.querySelector('.bubble img')?.naturalWidth"));
            Assert.Equal(locale=="en"?"WeChat":"微信",System.Text.Json.JsonSerializer.Deserialize<string>(Query("document.querySelector('option[value=wechat]').textContent")));
            Assert.Equal("\"6px 10px\"",Query("getComputedStyle(document.querySelector('.bubble')).padding"));
            Query("(()=>{const bubble=document.querySelector('.self .bubble');for(const cls of ['kind-title','card-detail','placeholder']){const text=document.createElement('div');text.className=cls;text.textContent='Unreadable-message palette fixture';bubble.append(text);}})()");
            foreach(var entry in new[]{("default","rgb(247, 247, 245)","rgb(35, 39, 46)"),("wechat","rgb(249, 249, 249)","rgb(30, 30, 30)"),("qq","rgb(244, 244, 244)","rgb(32, 32, 32)")}) {
                Query("document.getElementById('palette').value='"+entry.Item1+"';document.getElementById('palette').dispatchEvent(new Event('change'));true");
                foreach(bool dark in new[]{false,true}) {
                    Query("document.body.classList.toggle('dark',"+(dark?"true":"false")+");true");
                    Assert.Equal(System.Text.Json.JsonSerializer.Serialize(dark?entry.Item3:entry.Item2),Query("getComputedStyle(document.body).backgroundColor"));
                    Assert.Equal("true",Query("document.querySelector('aside header').scrollWidth<=document.querySelector('aside header').clientWidth"));
                    string contrastFailures=Query("(()=>{const rgb=s=>(s.match(/[0-9.]+/g)||[]).map(Number),lum=c=>{const v=c.slice(0,3).map(x=>{x/=255;return x<=.04045?x/12.92:((x+.055)/1.055)**2.4});return .2126*v[0]+.7152*v[1]+.0722*v[2]},fail=[];for(const el of document.querySelectorAll('.timestamp,#notice,nav small,.source summary,input:not([type=checkbox]),select,.bubble,.kind-title,.card-detail,.placeholder,.avatar,#filter-label')){let node=el,bg;while(node){bg=rgb(getComputedStyle(node).backgroundColor);if(bg.length===3||bg[3]>0)break;node=node.parentElement;}if(!node)continue;const a=lum(rgb(getComputedStyle(el).color)),b=lum(bg),ratio=(Math.max(a,b)+.05)/(Math.min(a,b)+.05);if(ratio<4.5)fail.push({element:el.className||el.id,ratio});}return fail;})()");
                    Assert.True(contrastFailures=="[]",entry.Item1+" dark="+dark+": "+contrastFailures);
                    Wait(AsResult(Task.Delay(150)));using var stream=new MemoryStream();Wait(AsResult(core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream)));
                    DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
                    File.WriteAllBytes(Path.Combine(repo!.FullName,"artifacts/ui-review/wpf/reader-"+entry.Item1+"-"+(dark?"dark":"light")+"-"+locale+".png"),stream.ToArray());
                }
            }
            Assert.Equal("\"qq\"",Query("JSON.parse(localStorage.getItem('storloom.reader.'+window.readerPreferenceScope)).palette"));Query("location.reload();true");
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.message').length")!="4"){Pump();Thread.Sleep(10);}
            Assert.Equal("\"qq\"",Query("document.getElementById('palette').value"));
            Assert.Contains("Older archive fixture",File.ReadAllText(Path.Combine(export.Directory,"messages.html")));
            Assert.Contains("Older asset must remain untouched",File.ReadAllText(Path.Combine(export.Directory,"reader.js")));
        } finally {window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    [Theory]
    [InlineData("en",AppTheme.Light)]
    [InlineData("zh-CN",AppTheme.OneDark)]
    public void NoticesSizeToContentAndDoNotShowResizeButtons(string locale,AppTheme theme) => OnUi(() => {
        L.SetLanguage(locale);Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});var owner=new ThemeWindow("Notice fixture",1000,720){ShowInTaskbar=false};
        try {
            owner.Show();Pump();double shortHeight=0;
            foreach(bool longText in new[]{false,true}) {
                _=Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
                    var dialog=Application.Current.Windows.Cast<Window>().Single(w=>w.Owner==owner);dialog.UpdateLayout();
                    Assert.InRange(dialog.ActualWidth,340,520);Assert.InRange(dialog.ActualHeight,130,owner.ActualHeight*.9+.1);
                    var caption=Descendants(dialog).OfType<Wpf.Ui.Controls.TitleBar>().Single();Assert.False(caption.ShowMaximize);Assert.False(caption.ShowMinimize);
                    if(!longText){shortHeight=dialog.ActualHeight;Assert.True(shortHeight<280);}else Assert.True(dialog.ActualHeight>shortHeight);
                    var footer=Descendants(dialog).OfType<Border>().Single(b=>b.Child is WrapPanel);
                    Assert.Equal(((SolidColorBrush)Application.Current.Resources["Canvas"]).Color,((SolidColorBrush)footer.Background).Color);
                    Assert.Equal(((SolidColorBrush)Application.Current.Resources["Surface"]).Color,((SolidColorBrush)caption.Background).Color);
                    Capture((FrameworkElement)dialog,"compact-notice-"+locale+"-"+longText);dialog.DialogResult=false;
                }));
                string message=longText?string.Join("\n",Enumerable.Repeat(L.T("ConfirmBackgroundTask"),12)):L.T("ConfirmBackgroundTask");
                typeof(ShellWindow).Assembly.GetType("Storloom.App.Notice")!.GetMethod("Show")!.Invoke(null,new object[]{owner,L.T("Confirm"),message,true});
            }
        } finally {owner.Close();}
    });
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void ReaderKeepsBubbleWidthsIndependentAndUsesNativeQuoteLayoutsAndBoundedForwards(string locale) => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);L.SetLanguage(locale);Theme.Apply(new AppPreferences{Theme=AppTheme.OneDark,ReduceMotion=true});
        const string hash="0123456789abcdef0123456789abcdef";
        string forwarded="<msg><appmsg><type>19</type><title>Forwarded chat</title><recorditem><![CDATA[<recordinfo><datalist>"+
            string.Join("",Enumerable.Range(1,8).Select(i=>i==7?$"<dataitem datatype=\"2\"><sourcename>Sam</sourcename><fullmd5>{hash}</fullmd5></dataitem>":$"<dataitem datatype=\"1\"><sourcename>Sam</sourcename><datadesc>Forward {i}</datadesc></dataitem>"))+"</datalist></recordinfo>]]></recorditem></appmsg></msg>";
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent,IsSender);"+
            "INSERT INTO MSG VALUES(1,1700000000,'fixture@chatroom','wxid_fixture_member:"+"\nGoing to make tea',0),(1,1700000001,'fixture@chatroom','1',1),"+
            "(49,1700000002,'fixture@chatroom','<msg><appmsg><type>57</type><title>123</title><refermsg><displayname>Sam</displayname><content>2</content><type>1</type></refermsg></appmsg></msg>',1),"+
            $"(49,1700000003,'fixture@chatroom','{forwarded}',0),(3,1700000004,'fixture@chatroom','<msg><img md5=\"{hash}\"/></msg>',0);");
        Database(Path.Combine(source,"contact.db"),"CREATE TABLE contact(username,nick_name,remark);INSERT INTO contact VALUES('wxid_fixture_member','Sam','Sam');");
        var png=new PngBitmapEncoder();var drawing=new DrawingVisual();using(var context=drawing.RenderOpen()){context.DrawRectangle(Brushes.LightSlateGray,null,new Rect(0,0,1200,600));context.DrawRectangle(Brushes.SteelBlue,null,new Rect(0,0,600,600));}
        var bitmap=new RenderTargetBitmap(1200,600,96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(source,hash+".png")))png.Save(file);
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        var export=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(fixture,"reading"),ChatPlatform.WeChat);
        var page=Directory.GetFiles(Path.Combine(export.Directory,"conversations"),"*-0.js").Single();string script=File.ReadAllText(page);
        var items=System.Text.Json.JsonSerializer.Deserialize<ChatMessage[]>(script[script.IndexOf('[')..(script.LastIndexOf(']')+1)])!;
        Assert.Equal("Sam",items[0].SenderName);Assert.Equal("Going to make tea",items[0].Parts[0].Text);
        items[0]=items[0] with{DisplayText=null,Parts=[new("text",items[0].Text)]};items[1]=items[1] with{RawBase64=new string('A',12000)};
        File.WriteAllText(page,"window.receivePage("+System.Text.Json.JsonSerializer.Serialize(Path.GetFileNameWithoutExtension(page)[..^2])+",0,"+System.Text.Json.JsonSerializer.Serialize(items)+");");
        var window=new ChatReaderWindow(export.Directory,Path.Combine(fixture,"data"),true){ShowInTaskbar=false};
        try {
            window.Show();DateTime until=DateTime.UtcNow.AddSeconds(30);while(window.Browser.CoreWebView2==null&&DateTime.UtcNow<until){Pump();Thread.Sleep(10);}var core=window.Browser.CoreWebView2!;
            string Query(string js)=>Wait(core.ExecuteScriptAsync(js));
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.message').length")!="5"){Pump();Thread.Sleep(10);}Assert.Equal("5",Query("document.querySelectorAll('.message').length"));
            Assert.DoesNotContain("wxid_",Query("document.querySelector('.message-text').textContent"));
            Query("window.short=document.querySelectorAll('.message')[1];window.shortBubble=short.querySelector('.bubble');window.originalWidth=shortBubble.getBoundingClientRect().width;window.originalX=shortBubble.getBoundingClientRect().x;true");
            Assert.True(Query("originalWidth<60")=="true","Short bubble width: "+Query("originalWidth"));Query("short.querySelector('.source').open=true;true");Pump();
            Assert.True(Query("Math.abs(shortBubble.getBoundingClientRect().width-originalWidth)<0.1&&Math.abs(shortBubble.getBoundingClientRect().x-originalX)<0.1")=="true","Expanded source moved the message: "+Query("[originalWidth,shortBubble.getBoundingClientRect().width,originalX,shortBubble.getBoundingClientRect().x]"));
            Assert.True(Query("document.querySelector('nav button').getBoundingClientRect().height<56")=="true","Conversation row height: "+Query("document.querySelector('nav button').getBoundingClientRect().height"));
            Assert.Equal("5",Query("document.querySelectorAll('.forwarded').length"));Assert.Equal("0",Query("document.querySelectorAll('.kind-forward img').length"));
            Query("document.querySelector('.forward-more').click();true");Assert.Equal("8",Query("document.querySelectorAll('.forwarded').length"));Assert.Equal("1",Query("document.querySelectorAll('.kind-forward img').length"));
            Query("document.querySelector('.forward-more').click();true");Assert.Equal("5",Query("Array.from(document.querySelectorAll('.forwarded')).filter(e=>e.getClientRects().length).length"));
            Query("short.querySelector('.source').open=false;true");
            foreach(string palette in new[]{"wechat","qq"})foreach(bool dark in new[]{false,true}) {
                Query("document.getElementById('palette').value='"+palette+"';document.getElementById('palette').dispatchEvent(new Event('change'));document.body.classList.toggle('dark',"+(dark?"true":"false")+");true");
                Assert.Equal("\"0px\"",Query("getComputedStyle(shortBubble).borderTopWidth"));
                Assert.Equal(palette=="qq"?"\"50%\"":"\"5px\"",Query("getComputedStyle(document.querySelector('.avatar')).borderTopLeftRadius"));
                Assert.Equal(palette=="wechat"?"1":"0",Query("document.querySelectorAll('.reference-zone>blockquote').length"));Assert.Equal(palette=="qq"?"1":"0",Query("document.querySelectorAll('.bubble blockquote').length"));
                Wait(AsResult(Task.Delay(100)));using var stream=new MemoryStream();Wait(AsResult(core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream)));
                DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
                File.WriteAllBytes(Path.Combine(repo!.FullName,"artifacts/ui-review/wpf/reader-layout-"+palette+"-"+dark+"-"+locale+".png"),stream.ToArray());
            }
            Query("document.querySelectorAll('.message')[4].scrollIntoView();true");
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.message')[4].querySelector('img').naturalWidth")!="1200"){Pump();Thread.Sleep(10);}
            Assert.True(Query("(()=>{let b=document.querySelectorAll('.message')[4].querySelector('.bubble'),i=b.querySelector('img');return b.getBoundingClientRect().width-i.getBoundingClientRect().width<=24})()")=="true","Image bubble has unused width: "+Query("(()=>{let b=document.querySelectorAll('.message')[4].querySelector('.bubble'),i=b.querySelector('img');return [b.getBoundingClientRect().width,i.getBoundingClientRect().width]})()"));
        } finally {window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    [Theory]
    [InlineData("en",AppTheme.Light)]
    [InlineData("zh-CN",AppTheme.OneDark)]
    public void BackupAndFinishedPagesLabelTheirPathsOnTheLeftWithConsistentSpacing(string locale,AppTheme theme) => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account"),data=Path.Combine(fixture,"data");Directory.CreateDirectory(source);
        Database(Path.Combine(source,"nt_msg.db"),"CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'Fixture message');");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        var export=new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(fixture,"reading"),ChatPlatform.QQ);
        JsonFiles.Write(Path.Combine(data,"preferences.json"),new AppPreferences{Language=locale,Theme=theme,ReduceMotion=true,BackupRoot=Path.Combine(fixture,"backup-destination")});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:data){ShowInTaskbar=false};
        try {
            window.Show();Pump();var type=typeof(ShellWindow);void Set(string key,object value)=>type.GetField(key,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,value);
            Set("activeChat",new ChatLocation(ChatPlatform.QQ,source,"Fixture account",Path.Combine(source,"nt_msg.db"),new FileInfo(Path.Combine(source,"nt_msg.db")).Length));
            Set("activeManifest",backup.Manifest);Set("readingDirectory",export.Directory);Set("exportReport",Path.Combine(export.Directory,"export-report.json"));
            window.Navigate("chats");
            foreach(int step in new[]{6,3}) {
                Set("chatStep",step);type.GetMethod("RenderChatStage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);Pump();window.UpdateLayout();
                var labels=Descendants(window).OfType<Label>().Where(l=>l.IsVisible&&!Equals(l.Content,L.T("ChatWorkers"))).ToArray();Assert.Equal(2,labels.Length);
                Assert.Equal(step==6?new[]{L.T("AccountFolderLabel"),L.T("BackupTo")}:new[]{L.T("RawBackupLabel"),L.T("ReadingCopyLabel")},labels.Select(l=>l.Content));
                foreach(var label in labels) {
                    var target=(FrameworkElement)label.Target;var lp=label.TranslatePoint(new Point(),window);var cp=target.TranslatePoint(new Point(),window);
                    Assert.True(lp.X+label.ActualWidth<=cp.X);Assert.InRange(Math.Abs(lp.Y+label.ActualHeight/2-cp.Y-target.ActualHeight/2),0,1);
                }
                Assert.InRange(labels[1].TranslatePoint(new Point(),window).Y-labels[0].TranslatePoint(new Point(),window).Y,38,44);
                Capture(window,"chat-fields-"+step+"-"+locale);
            }
        } finally {window.Close();Directory.Delete(fixture,true);}
    });
    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    [InlineData(AppTheme.Dark)]
    public void ManualDatabaseKeyUsesTheSharedEditorPaletteAndLeftLabel(AppTheme theme) => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account"),data=Path.Combine(fixture,"data");Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source,"nt_msg.db"),new byte[5120]);
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        JsonFiles.Write(Path.Combine(data,"preferences.json"),new AppPreferences{Language="zh-CN",Theme=theme,ReduceMotion=true});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:data){ShowInTaskbar=false};
        try {
            window.Show();Pump();var type=typeof(ShellWindow);void Set(string key,object value)=>type.GetField(key,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,value);
            Set("activeChat",new ChatLocation(ChatPlatform.QQ,source,"Fixture account",Path.Combine(source,"nt_msg.db"),5120));Set("activeManifest",backup.Manifest);Set("chatStep",2);
            window.Navigate("chats");type.GetMethod("RenderChatStage",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);Pump();
            Assert.Contains(Descendants(window).OfType<Button>(),b=>Equals(b.Content,L.T("ReadQQLogin")));
            Descendants(window).OfType<CheckBox>().Single(c=>Equals(c.Content,L.T("ManualKey"))).IsChecked=true;Pump();
            var secret=Descendants(window).OfType<PasswordBox>().Single();var label=Descendants(window).OfType<Label>().Single(l=>l.Target==secret);
            Assert.Equal(L.T("DatabaseKeyLabel"),label.Content);Assert.True(label.TranslatePoint(new Point(),window).X+label.ActualWidth<=secret.TranslatePoint(new Point(),window).X);
            var plain=Descendants(window).OfType<TextBox>().First(t=>t.IsVisible);Assert.Same(plain.Template,secret.Template);
            secret.Password="fixture-key-1234!";
            foreach(bool focus in new[]{true,false}) {
                if(focus)secret.Focus();else plain.Focus();Pump();
                var frame=Descendants(secret).OfType<Border>().Single(b=>b.Name=="frame");
                Assert.Equal(((SolidColorBrush)Application.Current.Resources["Surface"]).Color,((SolidColorBrush)frame.Background).Color);
                Assert.Equal(((SolidColorBrush)Application.Current.Resources["Ink"]).Color,((SolidColorBrush)secret.Foreground).Color);
                Capture(window,"manual-key-"+theme+"-"+focus);
            }
            Assert.Contains(Descendants(window).OfType<TextBlock>(),t=>t.Text==L.T("DatabaseKeyEntryHelp"));
            var help=Descendants(window).OfType<Expander>().Single(e=>Equals(e.Header,L.T("ExportHelpTitle")));help.IsExpanded=true;Pump();
            Assert.InRange(Descendants(help).OfType<System.Windows.Controls.Primitives.ToggleButton>().First().ActualHeight,10,26);
            var textInput=Descendants(window).OfType<TextBox>().First(t=>t.IsVisible&&t.IsReadOnly&&t.Text.Length>0);var glyph=textInput.GetRectFromCharacterIndex(0);Assert.True(glyph.Left>=6);Assert.InRange(Math.Abs(glyph.Top+glyph.Height/2-textInput.ActualHeight/2),0,2);
            help.BringIntoView();Pump();Capture(window,"export-compact-help-"+theme);
            foreach(double width in new[]{760d,1100d}){window.Width=width;Pump();window.UpdateLayout();
                var viewport=Descendants(window).OfType<ScrollViewer>().First(v=>v.Content is StackPanel panel&&Descendants(panel).OfType<PasswordBox>().Any());
                var bounds=viewport.TransformToAncestor(window).TransformBounds(new Rect(0,0,viewport.ActualWidth,viewport.ActualHeight));
                foreach(var control in Descendants(viewport).OfType<Control>().Where(c=>c.IsVisible&&c is TextBox or PasswordBox or Button)){
                    var box=control.TransformToAncestor(window).TransformBounds(new Rect(0,0,control.ActualWidth,control.ActualHeight));Assert.True(box.Right<=bounds.Right+1);
                }
            }
        } finally {window.Close();Directory.Delete(fixture,true);}
    });

    [Fact]
    public void PreparedBackupViewsDoNotReopenTheManifestOrDatabaseOnTheUiThread() => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account"),raw=Path.Combine(fixture,"raw");Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source,"nt_msg.db"),new byte[5120]);
        var service=new TransferService();var backup=service.Execute(service.Plan(source,raw),Path.Combine(fixture,"journals"));
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:Path.Combine(fixture,"data")){ShowInTaskbar=false};
        try {
            window.Show();var type=typeof(ShellWindow);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            type.GetField("activeChat",flags)!.SetValue(window,new ChatLocation(ChatPlatform.QQ,source,"Fixture account",Path.Combine(source,"nt_msg.db"),5120));
            var load=(Task)type.GetMethod("UseChatBackup",flags)!.Invoke(window,new object[]{backup.Manifest})!;
            bool dispatched=false;window.Dispatcher.BeginInvoke(new Action(()=>dispatched=true));Pump();Assert.True(dispatched);Wait(AsResult(load));
            File.Move(backup.Manifest,backup.Manifest+".unavailable");
            using var held=new FileStream(Path.Combine(raw,"nt_msg.db"),FileMode.Open,FileAccess.Read,FileShare.None);
            type.GetMethod("RenderChatStage",flags)!.Invoke(window,null);Pump();
            Assert.Contains(Descendants(window).OfType<Button>(),b=>Equals(b.Content,L.T("ArchiveStart")));
        } finally {window.Close();Directory.Delete(fixture,true);}
    });

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void OperationProgressRemainsVisibleAndShowsTheCurrentPhaseFraction(AppTheme theme) => OnUi(() => {
        Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});
        var panel=new OperationPanel();var window=new ThemeWindow("Progress fixture",900,420){ShowInTaskbar=false};window.Body.Children.Add(panel);
        try {
            window.Show();panel.Start();panel.Report(new("chat-reader",75,0,TotalFiles:100));Pump();window.UpdateLayout();
            var bar=Descendants(panel).OfType<ProgressBar>().Single();
            Assert.True(bar.IsVisible);Assert.InRange(bar.ActualHeight,1,3);Assert.Equal(.75,bar.Value,3);
            var track=(FrameworkElement)bar.Template.FindName("PART_Track",bar);var indicator=(FrameworkElement)bar.Template.FindName("PART_Indicator",bar);
            Assert.InRange(indicator.ActualWidth/track.ActualWidth,.74,.76);
            panel.Report(new("archive-workspace",3,4096,8192,6));Assert.Contains("3",panel.StatusText.Text);Assert.Contains("6",panel.StatusText.Text);Assert.Contains(" / ",panel.StatusText.Text);
            panel.Report(new("chat-resume",0,0));Pump();Assert.True(bar.IsVisible);Assert.True(bar.IsIndeterminate);
            panel.ToggleExpanded();panel.Height=56;Pump();window.UpdateLayout();
            Assert.InRange(bar.TranslatePoint(new Point(0,bar.ActualHeight),panel).Y,0,panel.ActualHeight);
            panel.Finish("Fixture complete",true);panel.Start();Pump();Assert.True(bar.IsVisible);Assert.True(bar.IsIndeterminate);Assert.Equal(0,bar.Value);
        } finally {window.Close();}
    });

    [Fact]
    public void LogHeightIsSavedOnDragAndRestoredAcrossReopenAndTaskStart()=>OnUi(()=>{
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{ReduceMotion=true,LogHeight=160});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try{
            window.Show();Pump();var panel=Descendants(window).OfType<OperationPanel>().Single();panel.ToggleExpanded();Pump();
            var row=(RowDefinition)typeof(ShellWindow).GetField("logRow",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            row.Height=new GridLength(205);Pump();
            var divider=Descendants(window).OfType<GridSplitter>().Single(s=>s.Name=="log_splitter");
            divider.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0,0,false){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragCompletedEvent});Pump();
            Assert.InRange(JsonFiles.Read<AppPreferences>(Path.Combine(fixture,"preferences.json")).LogHeight,204,206);
            panel.ToggleExpanded();Pump();panel.ToggleExpanded();Pump();Assert.True(row.ActualHeight>=204&&row.ActualHeight<=206,"Log check 1: "+row.ActualHeight+" / remembered: "+JsonFiles.Read<AppPreferences>(Path.Combine(fixture,"preferences.json")).LogHeight);
            Func<CancellationToken,Task> work=_=>Task.CompletedTask;
            var task=(Task)typeof(ShellWindow).GetMethod("Run",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,new object[]{work})!;Wait(AsResult(task));Pump();Assert.True(row.ActualHeight>=204&&row.ActualHeight<=206,"Log check 2: "+row.ActualHeight+" / remembered: "+JsonFiles.Read<AppPreferences>(Path.Combine(fixture,"preferences.json")).LogHeight);
            window.Height+=100;Pump();Assert.True(row.ActualHeight>=204&&row.ActualHeight<=206,"Log check 3: "+row.ActualHeight+" / remembered: "+JsonFiles.Read<AppPreferences>(Path.Combine(fixture,"preferences.json")).LogHeight);
            window.Close();window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};window.Show();Pump();
            row=(RowDefinition)typeof(ShellWindow).GetField("logRow",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;Assert.True(row.ActualHeight>=204&&row.ActualHeight<=206,"Log check 4: "+row.ActualHeight+" / remembered: "+JsonFiles.Read<AppPreferences>(Path.Combine(fixture,"preferences.json")).LogHeight);
        }finally{window.Close();Directory.Delete(fixture,true);}
    });

    [Fact]
    public void FailedReadingExportShowsErrorsAndDoesNotOfferAnEmptyReader() => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source,"nt_msg.db"),new byte[5120]);
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        string reading=Path.Combine(fixture,"reading");Directory.CreateDirectory(reading);
        string report=Path.Combine(reading,"export-report.json");
        JsonFiles.Write(report,new ChatExportResult(reading,0,0,[new("chat-export","nt_msg.db","Fixture page authentication failed")],false));
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:Path.Combine(fixture,"data")){ShowInTaskbar=false};
        try {
            window.Show();var type=typeof(ShellWindow);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            void Set(string name,object value)=>type.GetField(name,flags)!.SetValue(window,value);
            Set("activeChat",new ChatLocation(ChatPlatform.QQ,source,"Fixture account",Path.Combine(source,"nt_msg.db"),5120));
            Set("activeManifest",backup.Manifest);Set("readingDirectory",reading);Set("exportReport",report);Set("chatStep",3);
            window.Navigate("chats");type.GetMethod("RenderChatStage",flags)!.Invoke(window,null);Pump();
            Assert.Contains(Descendants(window).OfType<TextBlock>(),t=>t.Text==L.T("ReadingFailedTitle"));
            Assert.Contains(Descendants(window).OfType<TextBlock>(),t=>t.Text=="Fixture page authentication failed");
            Assert.DoesNotContain(Descendants(window).OfType<Button>(),b=>b.IsVisible&&Equals(b.Content,L.T("OpenReading")));
            var run=(Task)type.GetMethod("Run",flags)!.Invoke(window,new object[]{(Func<CancellationToken,Task>)(_=>{Set("operationIncomplete",true);return Task.CompletedTask;})})!;
            Wait(AsResult(run));
            string log=Descendants(window).OfType<OperationPanel>().Single().Log.Text;
            Assert.Contains("Operation stopped.",log);Assert.DoesNotContain("Operation completed.",log);
        } finally {window.Close();Directory.Delete(fixture,true);}
    });

    [Fact]
    public void ArchivedQqKeyValidationCorrectsAStaleGuildSelection() => OnUi(() => {
        string fixture=Fixture(),source=Path.Combine(fixture,"account");
        string guild=Path.Combine(source,"QQGuild","nt_db","nt_msg.db"),main=Path.Combine(source,"nt_qq","nt_db","nt_msg.db");
        Directory.CreateDirectory(Path.GetDirectoryName(guild)!);Directory.CreateDirectory(Path.GetDirectoryName(main)!);
        File.WriteAllBytes(guild,new byte[5120]);File.WriteAllBytes(main,new byte[5120]);
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:Path.Combine(fixture,"data")){ShowInTaskbar=false};
        try {
            var type=typeof(ShellWindow);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            type.GetField("activeChat",flags)!.SetValue(window,new ChatLocation(ChatPlatform.QQ,source,"Fixture account",guild,5120));
            type.GetField("activeManifest",flags)!.SetValue(window,backup.Manifest);
            var archived=(ChatLocation)type.GetMethod("ArchiveAccount",flags)!.Invoke(window,null)!;
            Assert.Equal(Path.Combine(backup.Destination,"nt_qq","nt_db","nt_msg.db"),archived.Database);
            Assert.Equal(backup.Destination,archived.Directory);
        } finally {window.Close();Directory.Delete(fixture,true);}
    });

    [Fact]
    public void OperationButtonRemainsCancellableAfterItsInputWasCleared() => OnUi(() => {
        string fixture=Fixture();var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try {
            window.Show();Pump();var type=typeof(ShellWindow);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            Button? button=null;
            Func<Task> action=()=>{
                button!.IsEnabled=false;
                return (Task)type.GetMethod("Run",flags)!.Invoke(window,new object[]{(Func<CancellationToken,Task>)(token=>Task.Delay(Timeout.Infinite,token))})!;
            };
            button=(Button)type.GetMethod("AsyncButton",flags)!.Invoke(window,new object[]{"ExportReading",action,true})!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Assert.True(button.IsEnabled);Assert.Equal(L.T("Cancel"),button.Content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DateTime until=DateTime.UtcNow.AddSeconds(3);while(type.GetField("operation",flags)!.GetValue(window)!=null&&DateTime.UtcNow<until){Pump();Thread.Sleep(5);}
            Assert.Null(type.GetField("operation",flags)!.GetValue(window));Assert.False(button.IsEnabled);
        } finally {window.Close();Directory.Delete(fixture,true);}
    });

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void DiagnosticLogsAreOptInAndExportMoreThanTheVisibleTail(string locale) => OnUi(() => {
        L.SetLanguage(locale);Theme.Apply(new AppPreferences{ReduceMotion=true});string fixture=Fixture();
        var panel=new OperationPanel();var window=new ThemeWindow("Log fixture",1040,500){ShowInTaskbar=false};window.Body.Children.Add(panel);
        try {
            window.Show();Pump();panel.Start();panel.AddLog("first-fixture-entry");
            panel.AddDiagnostic(new("key.passive.exhausted",Candidates:10));
            Assert.DoesNotContain("key.passive.exhausted",panel.Log.Text);
            for(int i=0;i<900;i++)panel.AddLog("fixture-tail-"+i+new string('x',450));
            Assert.DoesNotContain("first-fixture-entry",panel.Log.Text);
            panel.DebugToggle.IsChecked=true;panel.AddDiagnostic(new("key.passive.scan",Bytes:123456,Processes:3,Regions:45,Candidates:6,ElapsedSeconds:7.5));
            panel.AddDiagnostic(new("candidate=must-never-appear"));panel.AddException(new InvalidOperationException("private-input-must-never-appear"));
            panel.ToggleExpanded();Assert.True(Descendants(panel).OfType<Button>().Single(b=>b.Name=="export_log").IsVisible);
            string path=Path.Combine(fixture,"session.log");panel.ExportLog(path);string text=File.ReadAllText(path);
            Assert.Contains("Storloom ",text);Assert.Contains("first-fixture-entry",text);Assert.Contains("bytes: 123,456",text);Assert.Contains("candidates: 6",text);Assert.All(text.Split('\n').Where(line=>line.Contains("[DEBUG]")),line=>Assert.All(line,c=>Assert.InRange((int)c,0,127)));
            Assert.Contains("System.InvalidOperationException",text);Assert.DoesNotContain("must-never-appear",text);Assert.DoesNotContain("key.passive.exhausted",text);
            panel.DebugToggle.IsChecked=false;panel.Start();panel.ExportLog(path);Assert.Contains("first-fixture-entry",File.ReadAllText(path));Assert.Contains(L.T("DebugDisabled"),File.ReadAllText(path));
            Capture(window,"diagnostic-log-"+locale);
        } finally {window.Close();Directory.Delete(fixture,true);}
    });

    [Fact]
    public void DebugLoggingChoicePersistsAcrossShellRestarts() => OnUi(() => {
        string fixture=Fixture();var first=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        first.Show();Pump();Descendants(first).OfType<OperationPanel>().Single().DebugToggle.IsChecked=true;first.Close();
        Assert.True(JsonFiles.Read<AppPreferences>(Path.Combine(fixture,"preferences.json")).DebugLogging);
        var second=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try {second.Show();Pump();Assert.True(Descendants(second).OfType<OperationPanel>().Single().DebugToggle.IsChecked);}
        finally {second.Close();Directory.Delete(fixture,true);}
    });

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    [InlineData(AppTheme.Dark)]
    public void NativePaletteKeepsTextControlsAndPressedPrimaryActionsReadable(AppTheme theme) => OnUi(() => {
        Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});
        Color ColorOf(string key)=>((SolidColorBrush)Theme.Brush(key)).Color;
        foreach(string background in new[]{"Canvas","Surface","Sidebar","Selection","Hover"})
            foreach(string foreground in new[]{"Ink","Muted","AccentText"})
                Assert.True(Contrast(ColorOf(foreground),ColorOf(background))>=4.5,$"{theme} {foreground}/{background}");
        foreach(var pair in new[]{("OnAccent","Accent"),("SelectedText","TextSelection"),("Danger","Surface"),("Danger","Canvas")})
            Assert.True(Contrast(ColorOf(pair.Item1),ColorOf(pair.Item2))>=4.5,$"{theme} {pair}");
        // Idle outlines follow the subtle dividers; focus remains visibly accented.
        Assert.Equal(ColorOf("Line"),ColorOf("ControlBorder"));
        if(theme==AppTheme.OneDark)Assert.InRange(Contrast(ColorOf("Line"),ColorOf("Surface")),1.1,1.3);
        Assert.True(Contrast(ColorOf("AccentText"),ColorOf("Surface"))>=3);
        var button=new Button{Style=(Style)Application.Current.Resources["PrimaryButton"],Content="Primary fixture"};
        var window=new ThemeWindow("Palette fixture",600,350){ShowInTaskbar=false};window.Body.Children.Add(button);
        try {
            window.Show();Pump();var pressed=(DependencyProperty)typeof(ControlFeedback).GetField("PressedProperty",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
            var frameBefore=(Border)button.Template.FindName("frame",button);
            Assert.Equal(0,((SolidColorBrush)frameBefore.BorderBrush).Color.A);
            button.Focus();Pump();
            Assert.Equal(0,((SolidColorBrush)frameBefore.BorderBrush).Color.A);
            Assert.NotNull(button.FocusVisualStyle);
            button.SetValue(pressed,true);ControlFeedback.Refresh(button);
            var frame=(Border)button.Template.FindName("frame",button);
            Assert.True(Contrast(((SolidColorBrush)button.Foreground).Color,((SolidColorBrush)frame.Background).Color)>=4.5);
        }finally{window.Close();Theme.Apply(new AppPreferences{ReduceMotion=true});}
    });
    [Theory]
    [InlineData(AppTheme.Light,0)]
    [InlineData(AppTheme.Dark,1)]
    [InlineData(AppTheme.OneDark,2)]
    public void ThemeChoicesKeepGrayscaleDarkBeforeOneDarkAndRoundTrip(AppTheme theme,int selected) => OnUi(() => {
        string fixture=Fixture();L.SetLanguage("zh-CN");Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});
        var type=typeof(ShellWindow).Assembly.GetType("Storloom.App.PreferencesWindow")!;
        var window=(Window)Activator.CreateInstance(type,new object?[]{new AppPreferences{Theme=theme},Path.Combine(AppContext.BaseDirectory,"rules"),fixture,new CleanerSettings(),false})!;
        try {
            window.ShowInTaskbar=false;window.Show();Pump();
            var combo=Descendants(window).OfType<ComboBox>().Single(c=>c.Items.Count==3 && Equals(c.Items[2],"OneDark"));
            Assert.Equal(new[]{"浅色","深色","OneDark"},combo.Items.Cast<string>());
            Assert.Equal(selected,combo.SelectedIndex);
            string path=Path.Combine(fixture,"preferences.json");JsonFiles.Write(path,new AppPreferences{Theme=theme});
            Assert.Equal(theme,JsonFiles.Read<AppPreferences>(path).Theme);
            Assert.Equal(theme != AppTheme.Light,Theme.IsDark);
            if(theme==AppTheme.Dark)
                foreach(string key in new[]{"Canvas","Surface","Sidebar","Ink","Muted","Line","Accent","OnAccent","Selection","Hover","ControlBorder"}) {
                    var color=((SolidColorBrush)Theme.Brush(key)).Color;Assert.Equal(color.R,color.G);Assert.Equal(color.G,color.B);
                }
        }finally{window.Close();Directory.Delete(fixture,true);Theme.Apply(new AppPreferences{ReduceMotion=true});}
    });
    [Fact]
    public void PersistentLogBarRespectsAutoOpenAndWrapAndKeepsIconsCentered() => OnUi(()=>{
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Theme=AppTheme.OneDark,ReduceMotion=true,LogAutoShow=false});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};var finish=new TaskCompletionSource();Task? task=null;
        try{window.Show();Pump();var panel=Descendants(window).OfType<OperationPanel>().Single();
            Assert.True(panel.IsVisible);Assert.False(panel.Expanded);Assert.True(panel.Follow);Assert.InRange(panel.ActualHeight,31,34);
            task=(Task)typeof(ShellWindow).GetMethod("RunPausable",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[new Func<CancellationToken,Task>(_=>finish.Task)])!;Pump();Assert.False(panel.Expanded);
            panel.WrapToggle.IsChecked=true;Assert.Equal(TextWrapping.Wrap,panel.Log.TextWrapping);Assert.Equal(ScrollBarVisibility.Disabled,panel.Log.HorizontalScrollBarVisibility);
            panel.ToggleExpanded();Pump();Assert.True(panel.Expanded);Assert.True(panel.ActualHeight>80);
            foreach(var button in new[]{panel.PauseButton,panel.CancelButton}){var icon=Assert.IsType<System.Windows.Shapes.Path>(button.Content);var bounds=icon.RenderedGeometry.Bounds;var center=icon.TranslatePoint(new Point(bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2),button);Assert.InRange(Math.Abs(center.X-button.ActualWidth/2),0,1.5);Assert.InRange(Math.Abs(center.Y-button.ActualHeight/2),0,1.5);}
            foreach(var control in Descendants(panel).OfType<Control>().Where(c=>new[]{"pause_operation","cancel_operation","debug_log","wrap_log","follow_log","auto_show_log","auto_collapse_log","export_log","toggle_log"}.Contains(c.Name))){
                double top=control.TranslatePoint(new Point(0,0),panel).Y;Assert.InRange(control.ActualWidth,23.5,24.5);Assert.InRange(control.ActualHeight,23.5,24.5);Assert.Equal(new Thickness(2,0,2,0),control.Margin);Assert.InRange(top,3.5,4.5);Assert.InRange(32-top-control.ActualHeight,3.5,4.5);
            }
            Capture(window,"compact-persistent-log");panel.ToggleExpanded();Pump();Assert.InRange(panel.ActualHeight,31,34);finish.SetResult();Wait(AsResult(task));
        }finally{finish.TrySetResult();if(task!=null)Wait(AsResult(task));window.Close();Directory.Delete(fixture,true);}
    });
    [Fact]
    public void SuccessfulTasksAutoCollapseWithoutLosingLogsHeightOrSavedChoice()=>OnUi(()=>{
        string fixture=Fixture();JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{ReduceMotion=true,LogHeight=205});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try{
            window.Show();Pump();var panel=Descendants(window).OfType<OperationPanel>().Single();Assert.False(panel.AutoCollapseToggle.IsChecked);
            panel.AutoCollapseToggle.IsChecked=true;Assert.True(JsonFiles.Read<AppPreferences>(Path.Combine(fixture,"preferences.json")).LogAutoCollapse);
            Func<CancellationToken,Task> work=_=>{Assert.True(panel.Expanded);panel.AddLog("fixture retained task details");return Task.CompletedTask;};
            var task=(Task)typeof(ShellWindow).GetMethod("Run",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,new object[]{work})!;Wait(AsResult(task));Pump();
            Assert.False(panel.Expanded);Assert.Contains("fixture retained task details",panel.Log.Text);Assert.Equal(205,JsonFiles.Read<AppPreferences>(Path.Combine(fixture,"preferences.json")).LogHeight);
            panel.ToggleExpanded();Pump();Assert.InRange(panel.ActualHeight,204,206);panel.Start();panel.Finish("Fixture failure or cancellation",false);Assert.True(panel.Expanded);
            panel.AutoCollapseToggle.IsChecked=false;panel.Start();panel.Finish("Fixture success with auto collapse off",true);Assert.True(panel.Expanded);
            panel.AutoCollapseToggle.IsChecked=true;window.Close();window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};window.Show();Pump();
            Assert.True(Descendants(window).OfType<OperationPanel>().Single().AutoCollapseToggle.IsChecked);
        }finally{window.Close();Directory.Delete(fixture,true);}
    });
    [Theory][InlineData(AppTheme.Light)][InlineData(AppTheme.Dark)][InlineData(AppTheme.OneDark)]
    public void SelectedLogTogglesHaveDistinctReadableHover(AppTheme theme)=>OnUi(()=>{
        Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});var panel=new OperationPanel();var window=new ThemeWindow("Selected hover fixture",900,400){ShowInTaskbar=false};window.Body.Children.Add(panel);
        try{
            window.Show();Pump();var toggle=panel.FollowToggle;var frame=(Border)toggle.Template.FindName("frame",toggle);Color idle=((SolidColorBrush)frame.Background).Color;
            var mouseKey=(DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            toggle.SetValue(mouseKey,true);Pump();Color hovered=((SolidColorBrush)frame.Background).Color;
            Assert.NotEqual(idle,hovered);Assert.Equal(((SolidColorBrush)Theme.Brush("SelectionHover")).Color,hovered);Assert.True(toggle.IsChecked);
            Assert.True(Contrast(((SolidColorBrush)toggle.Foreground).Color,hovered)>=4.5);
            Capture(window,"selected-log-hover-"+theme);toggle.ClearValue(mouseKey);Pump();Assert.Equal(idle,((SolidColorBrush)frame.Background).Color);
        }finally{window.Close();Theme.Apply(new AppPreferences{ReduceMotion=true});}
    });

    [Fact]
    public void KeyCustodyDialogIsFixedAndUsesASeparatedActionRow() => OnUi(()=>{
        string fixture=Fixture();var owner=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try{owner.Show();typeof(ShellWindow).GetField("activeChat",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,new ChatLocation(ChatPlatform.QQ,fixture,"fixture",Path.Combine(fixture,"db"),0));typeof(ShellWindow).GetMethod("SetCapturedKey",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,[System.Text.Encoding.ASCII.GetBytes("synthetic-key-123")]);
            owner.Dispatcher.BeginInvoke(new Action(()=>{var dialog=Application.Current.Windows.Cast<Window>().Single(w=>w.Owner==owner);Assert.Equal(ResizeMode.NoResize,dialog.ResizeMode);Assert.Equal(0,GetWindowLongW(new System.Windows.Interop.WindowInteropHelper(dialog).Handle,-16)&0x40000);Assert.Equal(new Thickness(0),System.Windows.Shell.WindowChrome.GetWindowChrome(dialog)!.ResizeBorderThickness);var actions=Descendants(dialog).OfType<Button>().Where(b=>Equals(b.Content,L.T("KeyCopy"))||Equals(b.Content,L.T("ProtectedKeySave"))||Equals(b.Content,L.T("Close"))).ToArray();Assert.Equal(3,actions.Length);Capture(dialog,"key-custody-compact");dialog.Close();}));
            typeof(ShellWindow).GetMethod("ManageChatKey",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(owner,null);
        }finally{owner.Close();Directory.Delete(fixture,true);}
    });

    [Fact]
    public void MainWindowRemembersNarrowSizeWithoutChangingExistingSettings() => OnUi(()=>{
        string fixture=Fixture();var first=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        first.Show();first.Width=740;first.Height=560;Pump();first.Close();
        var second=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try{second.Show();Pump();Assert.Equal(740,second.Width);Assert.Equal(560,second.Height);Assert.True(second.MinWidth<=640);}
        finally{second.Close();Directory.Delete(fixture,true);}
    });
    [Fact]
    public void ProtectionPasswordDialogFitsContentAndAcceptsEightCharacters() => OnUi(()=>{
        L.SetLanguage("en");Theme.Apply(new AppPreferences{ReduceMotion=true});
        var type=typeof(ShellWindow).Assembly.GetType("Storloom.App.VaultPasswordWindow")!;
        var window=(Window)Activator.CreateInstance(type,new object?[]{true,true,null})!;window.ShowInTaskbar=false;
        window.Loaded+=(_,_)=>window.Dispatcher.BeginInvoke(new Action(()=>{
            var passwords=Descendants(window).OfType<PasswordBox>().ToArray();Assert.Equal(2,passwords.Length);
            foreach(var password in passwords)password.Password="eight123";
            var accept=Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,L.T("VaultEncrypt")));
            var position=accept.TransformToAncestor(window).Transform(new Point(0,accept.ActualHeight));
            Assert.True(window.ActualHeight-position.Y<60,"Unexpected unused bottom space");
            accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        Assert.True(window.ShowDialog());
        Assert.Equal("eight123",type.GetProperty("Password",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window));
        type.GetMethod("ClearSecret",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
    });

    [Fact]
    public void ProtectedReaderDecryptsContentInPrivateProfileWithoutPlaintextExtraction() => OnUi(()=>{
        string fixture=Fixture(),source=Path.Combine(fixture,"account"),reading=Path.Combine(fixture,"reading");Directory.CreateDirectory(source);
        Theme.Apply(new AppPreferences{ReduceMotion=true});L.SetLanguage("en");
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);INSERT INTO MSG VALUES(1,1700000000,'fixture','Synthetic protected message');");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
        new ChatExporter().ExportVerifiedBackup(backup.Manifest,reading,ChatPlatform.WeChat);
        byte[] original=File.ReadAllBytes(Path.Combine(reading,"messages.jsonl"));
        using(var vault=ChatVault.Create(reading,"synthetic-protected-reader")){Assert.True(vault.Encrypt().Complete);}
        Assert.False(File.Exists(Path.Combine(reading,"messages.jsonl")));Assert.False(File.Exists(Path.Combine(reading,"archive-index.js")));
        var window=new ChatReaderWindow(reading,Path.Combine(fixture,"data"),true){ShowInTaskbar=false};
        typeof(ChatReaderWindow).GetProperty("VaultPasswordProvider",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,(Func<string?>)(()=>"synthetic-protected-reader"));
        try{
            window.Show();DateTime until=DateTime.UtcNow.AddSeconds(30);
            while(window.Browser.CoreWebView2==null&&DateTime.UtcNow<until){Pump();Thread.Sleep(10);}
            Assert.True(window.Browser.CoreWebView2!=null,string.Join(" | ",window.Body.Children.OfType<TextBlock>().Select(t=>t.Text)));
            var core=window.Browser.CoreWebView2!;string Query(string script)=>Wait(core.ExecuteScriptAsync(script));
            while(DateTime.UtcNow<until&&Query("document.querySelectorAll('.message').length")!="1"){Pump();Thread.Sleep(10);}
            Assert.Contains("Synthetic protected message",Query("document.querySelector('#messages').textContent"));Assert.True(core.Profile.IsInPrivateModeEnabled);
            using var vault=ChatVault.Open(reading,"synthetic-protected-reader");Assert.ThrowsAny<IOException>(()=>vault.Encrypt());
            var request=core.Environment.CreateWebResourceRequest("https://archive.storloom.invalid/messages.jsonl","GET",null,"Range: bytes=2-6");
            var response=(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceResponse)typeof(ChatReaderWindow).GetMethod("VaultResponse",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[core.Environment,request,vault.OpenRead("messages.jsonl"),"application/octet-stream"])!;
            Assert.Equal(206,response.StatusCode);using(var bytes=new MemoryStream()){response.Content.CopyTo(bytes);response.Content.Dispose();Assert.Equal(original[2..7],bytes.ToArray());}
            Assert.False(File.Exists(Path.Combine(reading,"archive-index.js")));Assert.False(File.Exists(Path.Combine(reading,"messages.jsonl")));
        }finally{window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    [Theory][InlineData(false)][InlineData(true)]
    public void PackedArchiveReaderSearchesAndExportsCurrentMessagesWithoutExtractingPrivateData(bool encrypted)=>OnUi(()=>{
        Theme.Apply(new AppPreferences{Theme=AppTheme.OneDark,ReduceMotion=true});L.SetLanguage("en");string fixture=Fixture(),source=Path.Combine(fixture,"account"),reading=Path.Combine(fixture,"reading"),packed=Path.Combine(fixture,"packed");Directory.CreateDirectory(source);
        Database(Path.Combine(source,"message_0.db"),"CREATE TABLE MSG(Type,CreateTime,StrTalker,StrContent);"+string.Join("",Enumerable.Range(0,215).Select(i=>$"INSERT INTO MSG VALUES(1,{1700000000+i},'alpha','indexed needle {i}');"))+"INSERT INTO MSG VALUES(1,1700000300,'beta','indexed needle beta');");
        var service=new TransferService();var backup=service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));new ChatExporter().ExportVerifiedBackup(backup.Manifest,reading,ChatPlatform.WeChat);
        using(var store=ArchiveStore.Create(packed,encrypted?"fixture-reader-password":default))store.ImportReader(reading);
        var window=new ChatReaderWindow(packed,Path.Combine(fixture,"data"),true){ShowInTaskbar=false};typeof(ChatReaderWindow).GetProperty("VaultPasswordProvider",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,(Func<string?>)(()=>"fixture-reader-password"));
        try{
            window.Show();DateTime until=DateTime.UtcNow.AddSeconds(30);while(window.Browser.CoreWebView2==null&&DateTime.UtcNow<until){Pump();Thread.Sleep(10);}Assert.True(window.Browser.CoreWebView2!=null,"Stage: "+typeof(ChatReaderWindow).GetProperty("LoadingStage",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)+" | "+string.Join(" | ",window.Body.Children.OfType<TextBlock>().Select(t=>t.Text)));var core=window.Browser.CoreWebView2!;string Query(string code)=>Wait(core.ExecuteScriptAsync(code));
            void Until(string script,string expected){var limit=DateTime.UtcNow.AddSeconds(20);while(DateTime.UtcNow<limit&&Query(script)!=expected){Pump();Thread.Sleep(10);}Assert.Equal(expected,Query(script));}
            Until("document.querySelectorAll('.message').length","215");Assert.Contains("indexed needle 0",Query("document.querySelector('#messages').textContent"));Assert.Equal(encrypted,core.Profile.IsInPrivateModeEnabled);
            Query("document.querySelector('#search-scope').value='archive';document.querySelector('#message-search').value='indexed needle';document.querySelector('#message-search').dispatchEvent(new Event('input'));document.querySelector('#find-page').click();true");
            Until("document.querySelectorAll('.message').length","100");Until("document.querySelector('#stop-search').hidden","true");Assert.Contains("indexed needle 0",Query("document.querySelector('#messages').textContent"));
            Query("document.querySelector('#next').click();true");Until("document.querySelector('#messages').textContent.includes('indexed needle 100')","true");Until("document.querySelector('#stop-search').hidden","true");
            Query("document.querySelector('#next').click();true");Until("document.querySelectorAll('.message').length","16");Until("document.querySelector('#stop-search').hidden","true");Assert.Contains("indexed needle beta",Query("document.querySelector('#messages').textContent"));
            using var store=ArchiveStore.Open(packed,encrypted?"fixture-reader-password":default);Assert.ThrowsAny<IOException>(()=>store.ImportReader(reading));
            string exported=Path.Combine(fixture,"exported");store.ExportReader(exported);Assert.Equal(216,File.ReadLines(Path.Combine(exported,"messages.jsonl")).Count());Assert.False(File.Exists(Path.Combine(packed,"messages.jsonl")));
            if(encrypted){using var png=new MemoryStream();Wait(AsResult(core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,png)));File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory,"packed-reader.png"),png.ToArray());}
        }finally{window.Close();Pump();try{Directory.Delete(fixture,true);}catch(IOException){}}
    });
    [Theory][InlineData("en")][InlineData("zh-CN")]
    public void ArchiveLibraryAndExportOptionsFitCompactWindows(string language)=>OnUi(()=>{
        string fixture=Fixture(),data=Path.Combine(fixture,"data");JsonFiles.Write(Path.Combine(data,"preferences.json"),new AppPreferences{Language=language,Theme=AppTheme.OneDark,ReduceMotion=true});var window=new ShellWindow(automaticDiscovery:false,dataDirectory:data){ShowInTaskbar=false,Width=850,Height=650};
        try{
            window.Show();window.Navigate("chats");Pump();var nav=Descendants(window).OfType<Button>().Single(b=>Equals(b.Content,L.T("ArchiveTools"))).ContextMenu!.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,L.T("StoreMaintenance")));nav.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump();window.UpdateLayout();Assert.Single(Descendants(window).OfType<DataGrid>().Where(g=>g.Name=="storage_archives"));Assert.All(Descendants(window).OfType<Button>().Where(b=>b.Name is "store_open" or "store_backup" or "store_update"),b=>Assert.False(b.IsEnabled));Capture(window,"archive-library-"+language);
            foreach(string name in new[]{"StoreMigrationWindow","StoreExportWindow"}){
                var type=typeof(ShellWindow).Assembly.GetType("Storloom.App."+name)!;var dialog=(Window)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,name=="StoreMigrationWindow"?[data,null,null]:[data,"Fixture archive"],null)!;
                try{dialog.ShowInTaskbar=false;dialog.Show();Pump();dialog.UpdateLayout();Assert.Equal(ResizeMode.NoResize,dialog.ResizeMode);foreach(var button in Descendants(dialog).OfType<Button>().Where(b=>b.Name is "store_migrate_start" or "store_export_start")){var point=button.TranslatePoint(new Point(),dialog);Assert.InRange(point.Y+button.ActualHeight,0,dialog.ActualHeight);Assert.InRange(point.X+button.ActualWidth,0,dialog.ActualWidth);}Assert.DoesNotContain(Descendants(dialog).OfType<TextBlock>(),t=>t.Text.StartsWith("Store",StringComparison.Ordinal));Capture(dialog,name+"-"+language);}finally{dialog.Close();Pump();}
            }
        }finally{window.Close();Pump();Directory.Delete(fixture,true);}
    });
    private static double Contrast(Color foreground,Color background) {
        static double Luminance(Color c) {static double Linear(byte channel){double x=channel/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}return .2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);}
        double a=Luminance(foreground),b=Luminance(background);return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
    }
    private static void Database(string path,string sql) {
        Assert.Equal(0,sqlite3_open_v2(path,out nint db,6,0));try{Assert.Equal(0,sqlite3_exec(db,sql,0,0,0));}finally{sqlite3_close_v2(db);}
    }
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string path,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_exec(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint callback,nint state,nint error);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int sqlite3_close_v2(nint db);
    [Theory][InlineData(AppTheme.Light)][InlineData(AppTheme.Dark)][InlineData(AppTheme.OneDark)]
    public void ChatLocationAndProtectedKeyDialogsKeepActionsVisibleAndEditorsAligned(AppTheme theme) => OnUi(()=>{
        string fixture=Fixture();L.SetLanguage("zh-CN");Theme.Apply(new AppPreferences{Theme=theme,ReduceMotion=true});
        string qq=Path.Combine(fixture,"QQ.exe"),wechat=Path.Combine(fixture,"Weixin.exe");File.WriteAllText(qq,"fixture");File.WriteAllText(wechat,"fixture");
        foreach(string name in new[]{"ChatSettingsWindow","KeyPasswordWindow"}){
            var type=typeof(ShellWindow).Assembly.GetType("Storloom.App."+name)!;
            object[] args=name=="ChatSettingsWindow"?[new AppPreferences{QQExecutable=qq,WeChatExecutable=wechat}]:[true];
            var dialog=(Window)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,args,null)!;
            try{
                dialog.ShowInTaskbar=false;dialog.Show();Pump();dialog.UpdateLayout();
                foreach(var editor in Descendants(dialog).OfType<Control>().Where(c=>c is PasswordBox || c is TextBox input&&!input.AcceptsReturn))Assert.Equal(32,editor.ActualHeight);
                foreach(var button in Descendants(dialog).OfType<Button>().Where(b=>b.IsVisible&&Equals(b.Content,L.T(name=="ChatSettingsWindow"?"Apply":"ProtectedKeySave")))){
                    var point=button.TranslatePoint(new Point(),dialog);Assert.InRange(point.Y+button.ActualHeight,0,dialog.ActualHeight);Assert.InRange(point.X+button.ActualWidth,0,dialog.ActualWidth);
                }
                if(name=="ChatSettingsWindow"){dialog.Width=480;dialog.Height=380;Pump();dialog.UpdateLayout();Assert.All(Descendants(dialog).OfType<Button>().Where(b=>Equals(b.Content,L.T("Apply"))),b=>Assert.True(b.TranslatePoint(new Point(),dialog).Y+b.ActualHeight<=dialog.ActualHeight));}
                else{var inputs=Descendants(dialog).OfType<PasswordBox>().ToArray();Assert.Equal(2,inputs.Length);Assert.True(dialog.ActualHeight<350);}
                if(dialog.IsVisible)Capture(dialog,name+"-"+theme);
            }finally{dialog.Close();}
        }
        Directory.Delete(fixture,true);
    });
    [Fact]
    public void DirectReadingSetupHasNoManifestDependencyOrBackupVerificationControl() => OnUi(()=>{
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);File.WriteAllBytes(Path.Combine(source,"nt_msg.db"),new byte[5120]);
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:Path.Combine(fixture,"data")){ShowInTaskbar=false};
        try{
            window.Show();var type=typeof(ShellWindow);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            type.GetField("activeChat",flags)!.SetValue(window,new ChatLocation(ChatPlatform.QQ,source,"fixture",Path.Combine(source,"nt_msg.db"),5120));
            type.GetField("directChatSource",flags)!.SetValue(window,true);type.GetField("chatStep",flags)!.SetValue(window,2);window.Navigate("chats");type.GetMethod("RenderChatStage",flags)!.Invoke(window,null);Pump();
            Assert.Contains(Descendants(window).OfType<Button>(),b=>b.IsVisible&&Equals(b.Content,L.T("ExportDirect")));
            Assert.DoesNotContain(Descendants(window).OfType<CheckBox>(),c=>c.IsVisible&&Equals(c.Content,L.T("VerifyBackupBeforeExport")));
            Assert.Contains(Descendants(window).OfType<Button>(),b=>b.IsVisible&&Equals(b.Content,L.T("KeyLibrary")));
            Capture(window,"direct-reading-setup");
        }finally{window.Close();Directory.Delete(fixture,true);}
    });

    [Theory][InlineData("en")][InlineData("zh-CN")]
    public void AccountSizeMeasurementSortsBySizeKeepsSelectionAndCanRefreshOnlyOneAccount(string locale) => OnUi(()=>{
        string fixture=Fixture(),small=Path.Combine(fixture,"small"),large=Path.Combine(fixture,"large");Directory.CreateDirectory(small);Directory.CreateDirectory(large);
        File.WriteAllText(Path.Combine(small,"nt_msg.db"),"123");File.WriteAllText(Path.Combine(large,"nt_msg.db"),"123456789");File.WriteAllText(Path.Combine(large,"attachment.bin"),"abcdef");
        JsonFiles.Write(Path.Combine(fixture,"preferences.json"),new AppPreferences{Language=locale,Theme=AppTheme.OneDark,ReduceMotion=true});
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};
        try{
            window.Show();const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;var type=typeof(ShellWindow);
            var locations=new List<ChatLocation>{new(ChatPlatform.QQ,small,"small",Path.Combine(small,"nt_msg.db"),3),new(ChatPlatform.QQ,large,"large",Path.Combine(large,"nt_msg.db"),9)};
            type.GetField("locations",flags)!.SetValue(window,locations);type.GetField("chatStep",flags)!.SetValue(window,0);window.Navigate("chats");type.GetMethod("RenderChatStage",flags)!.Invoke(window,null);Pump();
            var table=Descendants(window).OfType<DataGrid>().Single(t=>t.Name=="chat_accounts");table.SelectedIndex=0;
            var measure=type.GetMethod("MeasureAccountSizes",flags)!;Wait(AsResult((Task)measure.Invoke(window,[false])!));Pump();
            string Account(object row)=>row.GetType().GetProperty("Account")!.GetValue(row)!.ToString()!;
            Assert.Equal("large",Account(table.Items[0]));Assert.Equal("small",Account(table.SelectedItem));
            Assert.Equal(3,locations[0].AccountBytes);Assert.Equal(15,locations[1].AccountBytes);Assert.Equal(2,locations[1].AccountFiles);Assert.All(locations,l=>Assert.True(l.SizeComplete));Assert.All(locations,l=>Assert.NotNull(l.SizeScannedAt));
            var previous=locations[1].SizeScannedAt;File.WriteAllText(Path.Combine(small,"nt_msg.db"),"1234567");
            Wait(AsResult((Task)measure.Invoke(window,[true])!));Pump();Assert.Equal(7,locations[0].AccountBytes);Assert.Equal(previous,locations[1].SizeScannedAt);
            Assert.True(Descendants(window).OfType<Button>().Single(b=>b.Name=="chat_next").IsEnabled);Assert.True(Descendants(window).OfType<Button>().Single(b=>b.Name=="measure_selected_account").IsEnabled);
            window.Width=760;Pump();Capture(window,"account-size-measurement-"+locale);
        }finally{window.Close();Directory.Delete(fixture,true);}
    });

    [Theory][InlineData(AppTheme.Light,"en")][InlineData(AppTheme.Dark,"zh-CN")][InlineData(AppTheme.OneDark,"zh-CN")]
    public void AccountTaskPagesFitWithoutFormScrollingAndPreferencesAreGrouped(AppTheme theme,string locale)=>OnUi(()=>{
        string fixture=Fixture(),source=Path.Combine(fixture,"account");Directory.CreateDirectory(source);File.WriteAllBytes(Path.Combine(source,"nt_msg.db"),new byte[5120]);
        var prefs=new AppPreferences{Theme=theme,Language=locale,ReduceMotion=true,LogAutoShow=false,BackupRoot=Path.Combine(fixture,"backups"),ArchiveRoot=Path.Combine(fixture,"readers")};JsonFiles.Write(Path.Combine(fixture,"preferences.json"),prefs);
        var window=new ShellWindow(automaticDiscovery:false,dataDirectory:fixture){ShowInTaskbar=false};var type=typeof(ShellWindow);const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var account=new ChatLocation(ChatPlatform.QQ,source,"Fixture account",Path.Combine(source,"nt_msg.db"),5120);
        try{
            window.Show();window.Navigate("chats");type.GetField("activeChat",flags)!.SetValue(window,account);
            var project=new ChatArchiveProject(ChatArchiveProject.Identity(account.Platform,account.Directory),account.Account,account.Platform,account.Directory,Path.Combine(fixture,"account-backups")){ReadingRoot=Path.Combine(fixture,"account-readers")};type.GetMethod("SaveArchive",flags)!.Invoke(window,[project]);
            foreach(var size in new[]{(950d,760d),(760d,640d)}){
                window.Width=size.Item1;window.Height=size.Item2;
                foreach(int step in new[]{1,6,4}){
                    if(step==4)Wait(AsResult((Task)type.GetMethod("PrepareIndependentReading",flags)!.Invoke(window,[account])!));
                    else{type.GetField("chatStep",flags)!.SetValue(window,step);type.GetMethod("RenderChatStage",flags)!.Invoke(window,null);}Pump();window.UpdateLayout();
                    if(step==6)Assert.Equal(project.BackupRoot,((TextBox)type.GetField("backupInput",flags)!.GetValue(window)!).Text);
                    if(step==4)Assert.Equal(project.ReadingRoot,((TextBox)type.GetField("readingRootInput",flags)!.GetValue(window)!).Text);
                    foreach(var scroll in Descendants(window).OfType<ScrollViewer>().Where(v=>v.IsVisible&&v.Content is StackPanel panel&&Descendants(panel).OfType<Control>().Any(c=>c is TextBox or PasswordBox))){
                        Assert.InRange(scroll.ScrollableHeight,0,1);var bounds=scroll.TransformToAncestor(window).TransformBounds(new Rect(0,0,scroll.ActualWidth,scroll.ActualHeight));
                        foreach(var control in Descendants(scroll).OfType<Control>().Where(c=>c.IsVisible&&c is TextBox or PasswordBox or Button)){var box=control.TransformToAncestor(window).TransformBounds(new Rect(0,0,control.ActualWidth,control.ActualHeight));Assert.True(box.Right<=bounds.Right+1);Assert.True(box.Bottom<=bounds.Bottom+1);}
                    }
                    Capture(window,"account-task-"+step+"-"+theme+"-"+(int)size.Item1);
                }
                foreach(string page in new[]{"overview","cleanup","chats"}){window.Navigate(page);Pump();Assert.All(Descendants(window).OfType<Button>().Where(b=>b.IsVisible),b=>Assert.True(b.TranslatePoint(new Point(b.ActualWidth,b.ActualHeight),window).X<=window.ActualWidth+1));}
            }
            var dialog=(Window)Activator.CreateInstance(type.Assembly.GetType("Storloom.App.PreferencesWindow")!,[prefs,Path.Combine(AppContext.BaseDirectory,"rules"),fixture,new CleanerSettings(),false])!;
            try{dialog.ShowInTaskbar=false;dialog.Show();Pump();var tabs=Descendants(dialog).OfType<TabControl>().Single();Assert.Equal(3,tabs.Items.Count);for(int tab=0;tab<2;tab++){tabs.SelectedIndex=tab;Pump();Assert.All(Descendants(dialog).OfType<ScrollViewer>().Where(v=>v.IsVisible&&v.Content is Grid),v=>Assert.InRange(v.ScrollableHeight,0,1));Capture(dialog,"grouped-preferences-"+tab+"-"+theme);}}finally{dialog.Close();}
        }finally{window.Close();Directory.Delete(fixture,true);}
    });

}
