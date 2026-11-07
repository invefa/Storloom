using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Storloom.Core;

namespace Storloom.App;

public sealed class ChatReaderWindow : ThemeWindow {
    public WebView2 Browser { get; } = new();
    private bool closed;
    public ChatReaderWindow(string directory, string dataRoot, bool reduceMotion = false) : base(L.T("ChatArchive"), 1200, 820) {
        Body.Children.Add(Browser); Closed += (_, _) => { closed = true; Browser.Dispose(); };
        Loaded += async (_, _) => {
            try {
                directory = PathPolicy.Normalize(directory); PathPolicy.AssertNoLinks(directory);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataRoot, "reader-browser"));
                if (closed) return; await Browser.EnsureCoreWebView2Async(environment); if (closed) return;
                var core = Browser.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false; core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false;
                core.SetVirtualHostNameToFolderMapping("archive.storloom.invalid", directory, CoreWebView2HostResourceAccessKind.DenyCors);
                bool Allowed(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" && parsed.Port == 443 && parsed.UserInfo.Length == 0 && parsed.Host == "archive.storloom.invalid";
                core.NavigationStarting += (_, args) => { if (!Allowed(args.Uri)) args.Cancel = true; };
                core.FrameNavigationStarting += (_, args) => args.Cancel = true;
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, args) => { if (!Allowed(args.Request.Uri)) args.Response = environment.CreateWebResourceResponse(null, 403, "Local archive only", ""); };
                if (reduceMotion) await core.AddScriptToExecuteOnDocumentCreatedAsync("document.addEventListener('DOMContentLoaded',()=>document.documentElement.classList.add('reduce-motion'));");
                core.Navigate("https://archive.storloom.invalid/messages.html" + (Theme.Current == AppTheme.OneDark ? "#dark" : "#light"));
            } catch (Exception ex) when (!closed) {
                Browser.Visibility = Visibility.Collapsed; Body.Children.Add(View.Text(L.T("ReaderRuntimeMissing") + "\n" + ex.Message));
            }
        };
    }
}
