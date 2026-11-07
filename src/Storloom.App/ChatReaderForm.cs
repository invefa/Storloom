using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Storloom.Core;
using Storloom.Core.Localization;

namespace Storloom.App;

internal sealed class ChatReaderForm : DialogBase {
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    public ChatReaderForm(string directory, string dataRoot) : base("ChatArchive", new Size(1180, 780)) {
        MaximizeBox = true; Controls.Add(browser);
        Shown += async (_, _) => {
            try {
                directory = PathPolicy.Normalize(directory); PathPolicy.AssertNoLinks(directory);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataRoot, "reader-browser"));
                if (IsDisposed) return;
                await browser.EnsureCoreWebView2Async(environment);
                var core = browser.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false; core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.SetVirtualHostNameToFolderMapping("archive.storloom.invalid", directory, CoreWebView2HostResourceAccessKind.DenyCors);
                bool Allowed(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" &&
                    parsed.Port == 443 && parsed.UserInfo.Length == 0 && parsed.Host == "archive.storloom.invalid";
                core.NavigationStarting += (_, args) => { if (!Allowed(args.Uri)) args.Cancel = true; };
                core.FrameNavigationStarting += (_, args) => args.Cancel = true;
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, args) => { if (!Allowed(args.Request.Uri))
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Local archive only", ""); };
                core.Navigate("https://archive.storloom.invalid/messages.html" + (Ui.Dark ? "#dark" : "#light"));
            } catch (Exception ex) when (!IsDisposed) {
                browser.Visible = false;
                var message = Ui.Text(L.T("ReaderRuntimeMissing") + Environment.NewLine + ex.Message);
                message.Dock = DockStyle.Fill; message.Padding = new Padding(24); Controls.Add(message);
            }
        };
    }
}
