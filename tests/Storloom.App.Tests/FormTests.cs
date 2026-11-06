using System.Runtime.ExceptionServices;
using Storloom.App;
using Storloom.Core.Localization;
using Xunit;

namespace Storloom.App.Tests;

public sealed class FormTests {
    [Theory]
    [InlineData(1360, 820, "en")]
    [InlineData(1120, 700, "en")]
    [InlineData(1360, 820, "zh-CN")]
    [InlineData(1120, 700, "zh-CN")]
    public void FormConstructsAndRendersWithCleanupDisabled(int width, int height, string locale) {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                L.SetLanguage(locale);
                using var form = new ShellForm();
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.ClientSize = new Size(width, height);
                form.Show();
                Application.DoEvents();
                form.PerformLayout();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                var buttons = Descendants(form).OfType<Button>().ToList();
                Assert.Contains(buttons, b => b.Text == L.T("Scan") && b.Enabled);
                Assert.Contains(buttons, b => b.Text == L.T("Cancel") && !b.Enabled);
                Assert.All(buttons.Where(b => b.Text == L.T("Execute")), b => Assert.False(b.Enabled));
                Assert.Contains(Descendants(form).OfType<ListView>(), l => l.Items.Count > 0);
                Assert.All(Descendants(form).OfType<Button>(), b => Assert.True(b.Width > 0));
                Assert.True(buttons.First(b => b.Text == L.T("Scan")).Visible);
                DirectoryInfo? root = new(AppContext.BaseDirectory);
                while (root != null && !File.Exists(Path.Combine(root.FullName, "Storloom.sln")))
                    root = root.Parent;
                Assert.NotNull(root);
                string output = Path.Combine(root!.FullName, "artifacts", "test-results");
                Directory.CreateDirectory(output);
                var tabs = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TabControl>().Single();
                for (int i = 0; i < tabs.TabCount; i++) {
                    tabs.SelectedIndex = i; Application.DoEvents(); form.PerformLayout();
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(output, $"window-{locale}-{i}-{width}x{height}.png"));
                }
            } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Form rendering exceeded 30 seconds.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<Control> Descendants(Control parent) {
        foreach (Control child in parent.Controls) {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}
