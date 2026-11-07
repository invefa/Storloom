using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Runtime.InteropServices;
using Storloom.Core.Chats;
using Storloom.App;
using Storloom.Core;
using Storloom.Core.Localization;
using Xunit;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Storloom.App.Tests;

public sealed class FormTests {
    [Theory]
    [InlineData(1300, 820, "en", AppTheme.Light)]
    [InlineData(1080, 720, "en", AppTheme.Light)]
    [InlineData(1300, 820, "zh-CN", AppTheme.Light)]
    [InlineData(1080, 720, "zh-CN", AppTheme.Light)]
    [InlineData(1300, 820, "en", AppTheme.OneDark)]
    [InlineData(1080, 720, "zh-CN", AppTheme.OneDark)]
    public void GuidedPagesRenderWithReviewDisabledAndNoStandaloneTransfer(int width, int height, string locale, AppTheme theme) {
        OnSta(() => {
            Application.EnableVisualStyles();
            L.SetLanguage(locale);
            string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            JsonFiles.Write(Path.Combine(fixture, "preferences.json"), new AppPreferences { Language = locale, Theme = theme });
            try {
                using var form = new ShellForm(fixture);
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000); form.ClientSize = new Size(width, height);
                form.Show(); Application.DoEvents(); form.PerformLayout();
                var controls = Descendants(form).ToList();
                Assert.Contains(controls.OfType<Button>(), b => b.Name == "scan-cleanup" && b.Enabled);
                Assert.Contains(controls.OfType<Button>(), b => b.Name == "review-cleanup" && !b.Enabled);
                Assert.DoesNotContain(controls.OfType<Label>(), l => l.Text == "Storloom");
                Assert.DoesNotContain(controls.OfType<ComboBox>(), c => c.Items.Contains("English"));
                var nav = controls.OfType<Button>().Where(b => b.Name.StartsWith("nav-")).ToArray();
                Assert.Equal(3, nav.Length);
                Assert.NotNull(form.Icon);
                Assert.Contains(form.Controls.OfType<MenuStrip>().Single().Items.OfType<ToolStripMenuItem>(), i => i.Text == L.T("HelpMenu"));
                foreach (var button in nav) {
                    button.PerformClick(); Application.DoEvents(); form.PerformLayout();
                    Capture(form, $"{locale}-{button.Name}-{width}x{height}-{theme}");
                }
            } finally { Directory.Delete(fixture, true); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void ResizingPreservesViewportWithoutBlankSpaceAboveTheFirstVisibleRow(AppTheme theme) {
        OnSta(() => {
            string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
            JsonFiles.Write(Path.Combine(fixture, "preferences.json"), new AppPreferences { Language = "zh-CN", Theme = theme });
            try {
                using var form = new ShellForm(fixture) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
                form.Show(); Application.DoEvents();
                var list = Descendants(form).OfType<ListView>().Single(c => c.Name == "cleanup-rules");
                for (int i = 0; i < 80; i++) list.Items.Add(new ListViewItem(new[] { "isolated resize fixture " + i, "-", "-" }));
                foreach (var size in new[] { new Size(1080,720), new Size(2560,1440), new Size(1300,820), new Size(1920,1080) }) {
                    list.Items[^1].EnsureVisible(); form.ClientSize = size; Application.DoEvents(); form.PerformLayout(); Application.DoEvents();
                    Assert.NotNull(list.TopItem); Assert.InRange(list.TopItem!.Bounds.Top, 0, 45);
                    Assert.True(list.TopItem.Bounds.Bottom < list.ClientSize.Height);
                }
                Capture(form, "resize-1920x1080-" + theme);
            } finally { Directory.Delete(fixture, true); }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.OneDark)]
    public void EmbeddedReaderLoadsNamedConversationsLocalImagesAndPagesWithoutHostAccess(AppTheme theme) {
        OnSta(() => {
            string fixture = Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N")), source = Path.Combine(fixture,"account");
            Directory.CreateDirectory(source);
            void Database(string name, string sql) {
                Assert.Equal(0,sqlite3_open_v2(Path.Combine(source,name),out nint db,6,0));
                try { Assert.Equal(0,sqlite3_exec(db,sql,0,0,0)); } finally { sqlite3_close_v2(db); }
            }
            Database("nt_msg.db", "CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");" +
                "WITH RECURSIVE seq(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM seq WHERE x<301) INSERT INTO c2c_msg_table SELECT 1,2,1700000000+x,CASE WHEN x=1 THEN '[attachment] fixture.png' ELSE 'Isolated reader fixture' END FROM seq;");
            Database("contact.db", "CREATE TABLE contact(username,nick_name,remark); INSERT INTO contact VALUES('1','Fixture contact','Named fixture'),('2','Fixture sender','');");
            using(var bitmap = new Bitmap(12,12)) { using var graphics=Graphics.FromImage(bitmap); graphics.Clear(Color.Teal); bitmap.Save(Path.Combine(source,"fixture.png")); }
            var service = new TransferService(); var backup = service.Execute(service.Plan(source,Path.Combine(fixture,"raw")),Path.Combine(fixture,"journals"));
            var exported = new ChatExporter().ExportVerifiedBackup(backup.Manifest,Path.Combine(fixture,"reading"),ChatPlatform.QQ);
            Assert.True(exported.Complete); Assert.Equal(1,exported.MediaFiles);
            try {
                var themeUi=typeof(ShellForm).Assembly.GetType("Storloom.App.Ui")!; themeUi.GetMethod("SetTheme")!.Invoke(null,[theme]);
                using var form=(Form)Activator.CreateInstance(typeof(ShellForm).Assembly.GetType("Storloom.App.ChatReaderForm")!,exported.Directory,Path.Combine(fixture,"data"))!;
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();
                var browser=Descendants(form).OfType<WebView2>().Single(); var deadline=DateTime.UtcNow.AddSeconds(25);
                while(browser.CoreWebView2==null && DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
                Assert.NotNull(browser.CoreWebView2); var core=browser.CoreWebView2;
                string Query(string script) => Pump(core.ExecuteScriptAsync(script));
                while(DateTime.UtcNow<deadline && Query("document.querySelectorAll('.message').length")!="300"){Application.DoEvents();Thread.Sleep(10);}
                Assert.Equal("300",Query("document.querySelectorAll('.message').length"));
                Assert.Contains("Named fixture",Query("document.getElementById('chat-title').textContent"));
                Assert.False(core.Settings.AreHostObjectsAllowed); Assert.False(core.Settings.IsWebMessageEnabled);
                while(DateTime.UtcNow<deadline && Query("document.querySelector('img')?.naturalWidth||0")=="0"){Application.DoEvents();Thread.Sleep(10);}
                Assert.Equal("12",Query("document.querySelector('img').naturalWidth"));
                using(var image=new MemoryStream()) { Pump(core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image));
                    DirectoryInfo? repo=new(AppContext.BaseDirectory);while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"Storloom.sln")))repo=repo.Parent;
                    File.WriteAllBytes(Path.Combine(repo!.FullName,"artifacts/ui-review/reader-browser-"+theme+".png"),image.ToArray()); }
                Query("document.getElementById('next').click();true");
                while(DateTime.UtcNow<deadline && Query("document.querySelectorAll('.message').length")!="1"){Application.DoEvents();Thread.Sleep(10);}
                Assert.Equal("1",Query("document.querySelectorAll('.message').length"));
            } finally {
                // WebView2 may finish releasing its isolated cache after the control closes.
                try { Directory.Delete(fixture,true); } catch(IOException) { }
            }
        });
    }
    private static T Pump<T>(Task<T> task) {
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(!task.IsCompleted && DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(5);}
        Assert.True(task.IsCompleted);return task.GetAwaiter().GetResult();
    }
    private static void Pump(Task task) => Pump(AsResult(task));
    private static async Task<bool> AsResult(Task task){await task;return true;}

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void PreferencesHelpAndAboutRenderWithPublicMetadata(string locale) {
        OnSta(() => {
            Application.EnableVisualStyles(); L.SetLanguage(locale);
            var assembly = typeof(ShellForm).Assembly;
            string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtures);
            try {
                foreach (var entry in new[] { ("PreferencesForm", new object[] { new AppPreferences(),
                    Path.Combine(AppContext.BaseDirectory, "rules"), fixtures, new CleanerSettings(), false }),
                    ("HelpForm", new object[] { 2, null! }), ("AboutForm", Array.Empty<object>()) }) {
                    using var dialog = (Form)Activator.CreateInstance(assembly.GetType("Storloom.App." + entry.Item1)!, entry.Item2)!;
                    dialog.ShowInTaskbar = false; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-30000, -30000);
                    dialog.Show(); Application.DoEvents(); dialog.PerformLayout(); Capture(dialog, locale + "-" + entry.Item1);
                    if (entry.Item1 == "PreferencesForm") {
                        var languageLabel = Descendants(dialog).OfType<Label>().Single(l => l.Text == L.T("LanguageLabel"));
                        var languageCombo = Descendants(dialog).OfType<ComboBox>().Single(c => c.Items.Contains("English"));
                        Assert.Same(languageLabel.Parent, languageCombo.Parent);
                        Assert.InRange(Math.Abs(languageLabel.Top + languageLabel.Height / 2 - languageCombo.Top - languageCombo.Height / 2), 0, 2);
                        var fields = (TableLayoutPanel)languageLabel.Parent!;
                        foreach (int row in new[] { 1, 2 }) {
                            var field = fields.GetControlFromPosition(1, row)!; var browse = fields.GetControlFromPosition(2, row)!;
                            var label = fields.GetControlFromPosition(0, row)!;
                            Assert.InRange(Math.Abs(field.Top + field.Height / 2 - browse.Top - browse.Height / 2), 0, 2);
                            Assert.InRange(Math.Abs(field.Top + field.Height / 2 - label.Top - label.Height / 2), 0, 2);
                        }
                        Descendants(dialog).OfType<TabControl>().Single().SelectedIndex = 1; Application.DoEvents();
                        Capture(dialog, locale + "-PreferencesRules");
                    }
                    if (entry.Item1 == "AboutForm") {
                        Assert.Contains(Descendants(dialog).OfType<Label>(), c => c.Text.StartsWith(L.T("Developer") + ": "));
                        Assert.Contains(Descendants(dialog).OfType<LinkLabel>(), c => c.Text.StartsWith("https://github.com/"));
                    }
                }
                var itemType = assembly.GetType("Storloom.App.ReviewItem")!;
                var reviewItems = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
                reviewItems.Add(Activator.CreateInstance(itemType, L.T("ArchiveMove"), L.T("Move"), "12 MB",
                    Path.Combine(fixtures, "archive"), Path.Combine(fixtures, "cache", "preview.bin"))!);
                using var review = (Form)Activator.CreateInstance(assembly.GetType("Storloom.App.ReviewForm")!,
                    reviewItems, L.T("DeleteNotice"))!;
                review.ShowInTaskbar = false; review.StartPosition = FormStartPosition.Manual; review.Location = new Point(-30000, -30000);
                review.Show(); Application.DoEvents(); Capture(review, locale + "-ReviewForm");
                Assert.Null(review.AcceptButton);
                Assert.Equal(L.T("GoBack"), ((Button)review.CancelButton!).Text);
                Assert.Contains(Descendants(review).OfType<TextBox>(), t => t.ReadOnly && t.Text.Contains("preview.bin"));
                var resultType = assembly.GetType("Storloom.App.CleanupItemResult")!;
                var results = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(resultType))!;
                results.Add(Activator.CreateInstance(resultType, "Isolated native cache", L.T("Command"), (long?)4096, (long?)1024, (long?)3072, L.T("Complete"))!);
                using var resultDialog = (Form)Activator.CreateInstance(assembly.GetType("Storloom.App.CleanupResultsForm")!, results)!;
                resultDialog.ShowInTaskbar = false; resultDialog.StartPosition = FormStartPosition.Manual; resultDialog.Location = new Point(-30000, -30000);
                resultDialog.Show(); Application.DoEvents(); Capture(resultDialog, locale + "-CleanupResultsForm");
            } finally { Directory.Delete(fixtures, true); }
        });
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("zh-CN", false)]
    [InlineData("en", true)]
    [InlineData("zh-CN", true)]
    public void ChatWizardUsesVerifiedBackupAndCompletesReadableExport(string locale, bool sourceRemoved) {
        OnSta(() => {
            Application.EnableVisualStyles(); L.SetLanguage(locale);
            string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(fixture, "account"), data = Path.Combine(fixture, "data");
            Directory.CreateDirectory(source); Directory.CreateDirectory(data);
            string database = Path.Combine(source, "nt_msg.db");
            Assert.Equal(0, sqlite3_open_v2(database, out nint db, 6, 0));
            try { Assert.Equal(0, sqlite3_exec(db,
                "CREATE TABLE c2c_msg_table(\"40030\",\"40033\",\"40050\",\"40800\");" +
                "INSERT INTO c2c_msg_table VALUES(1,2,1700000000,'isolated wizard fixture');",0,0,0)); }
            finally { sqlite3_close_v2(db); }
            var transfer = new TransferService(); var backup = transfer.Execute(transfer.Plan(source, Path.Combine(fixture,"backup")), Path.Combine(fixture,"journals"));
            long databaseBytes = new FileInfo(database).Length;
            if (sourceRemoved) Directory.Delete(source, true);
            JsonFiles.Write(Path.Combine(data,"preferences.json"), new AppPreferences { Language = locale });
            JsonFiles.Write(Path.Combine(data,"chat-backups.json"), new Dictionary<string,string> { [source] = backup.Manifest });
            try {
                using var form = new ShellForm(data) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000,-30000) };
                form.Show(); Application.DoEvents();
                Button Find(string name) => Descendants(form).OfType<Button>().Single(b => b.Name == name);
                Button TextButton(string key) => Descendants(form).OfType<Button>().Single(b => b.Text == L.T(key));
                Find("nav-chats").PerformClick();
                // Bind isolated account metadata to the view; no production discovery or chat data is accessed.
                typeof(ShellForm).GetField("locations", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form,
                    new List<ChatLocation> { new(ChatPlatform.QQ,source,"fixture-account",database,databaseBytes) });
                typeof(ShellForm).GetMethod("RenderChatStage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form,null);
                var accounts = Descendants(form).OfType<ListView>().Single(l => l.Name == "chat-accounts");
                accounts.Items[0].Selected = true; Application.DoEvents(); Find("chat-next").PerformClick(); Application.DoEvents();
                Capture(form, locale + "-chat-backup-step");
                TextButton("UsePreviousBackup").PerformClick(); Application.DoEvents(); Capture(form,locale + "-chat-export-step");
                Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);
                Assert.True(Find("chat-export").Enabled);
                Find("chat-export").Focus(); Application.DoEvents();
                bool clicked = false; Find("chat-export").Click += (_, _) => clicked = true;
                Find("chat-export").PerformClick(); Assert.True(clicked, "Export button did not receive a click");
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (Descendants(form).OfType<Button>().All(b => b.Text != L.T("OpenReport")) && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                Capture(form,locale + "-after-export-attempt");
                string state = string.Join(" | ", form.Controls.OfType<StatusStrip>().Single().Items.OfType<ToolStripStatusLabel>().Select(i => i.Text));
                if (Directory.Exists(Path.Combine(data,"errors"))) state += string.Join("\n",Directory.GetFiles(Path.Combine(data,"errors")).Select(File.ReadAllText));
                Assert.True(Descendants(form).OfType<Button>().Any(b => b.Text == L.T("OpenReport")), state);
                Capture(form,locale + "-chat-complete-step");
                string export = Directory.GetDirectories(fixture,"backup-reading-*").Single();
                var result = JsonFiles.Read<ChatExportResult>(Path.Combine(export,"export-report.json"));
                Assert.True(result.Complete); Assert.Equal(1,result.Messages); Assert.Equal(!sourceRemoved,File.Exists(database));
            } finally { Directory.Delete(fixture,true); }
        });
    }
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,nint callback,nint state,nint error);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(nint db);

    private static void Capture(Form form, string name) {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Storloom.sln"))) root = root.Parent;
        Assert.NotNull(root); string output = Path.Combine(root!.FullName, "artifacts", "ui-review");
        Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name + ".png"));
    }
    private static void OnSta(Action action) {
        Exception? failure = null;
        var thread = new Thread(() => {
            Application.EnableVisualStyles();
            using var pump = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000), Size = new Size(100, 100) };
            pump.Shown += (_, _) => pump.BeginInvoke((Action)(() => {
                try { action(); } catch (Exception ex) { failure = ex; }
                finally { pump.Close(); }
            }));
            Application.Run(pump);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "UI rendering exceeded 40 seconds.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static IEnumerable<Control> Descendants(Control parent) {
        foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
    }
}
