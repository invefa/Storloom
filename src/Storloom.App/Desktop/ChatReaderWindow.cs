using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Storloom.Core;
using Storloom.Core.Chats;
using System.Text;
using System.Text.Json;

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
                bool Allowed(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" && parsed.Port == 443 && parsed.UserInfo.Length == 0 && parsed.Host is "archive.storloom.invalid" or "reader.storloom.invalid";
                core.NavigationStarting += (_, args) => { if (!Allowed(args.Uri)) args.Cancel = true; };
                core.FrameNavigationStarting += (_, args) => args.Cancel = true;
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                var assets = ReaderAssets();
                core.WebResourceRequested += (_, args) => {
                    if (!Allowed(args.Request.Uri)) args.Response = environment.CreateWebResourceResponse(null, 403, "Local archive only", "");
                    else if (new Uri(args.Request.Uri).Host == "reader.storloom.invalid") {
                        if (assets.TryGetValue(new Uri(args.Request.Uri).AbsolutePath, out var asset))
                            args.Response = environment.CreateWebResourceResponse(new MemoryStream(asset.Bytes, false), 200, "OK", "Content-Type: " + asset.Type + "; charset=utf-8\r\nCache-Control: no-store");
                        else args.Response = environment.CreateWebResourceResponse(null, 404, "Unknown reader asset", "");
                    }
                };
                if (reduceMotion) await core.AddScriptToExecuteOnDocumentCreatedAsync("document.addEventListener('DOMContentLoaded',()=>document.documentElement.classList.add('reduce-motion'));");
                core.Navigate("https://reader.storloom.invalid/messages.html" + (Theme.Current == AppTheme.OneDark ? "#dark" : "#light"));
            } catch (Exception ex) when (!closed) {
                Browser.Visibility = Visibility.Collapsed; Body.Children.Add(View.Text(L.T("ReaderRuntimeMissing") + "\n" + ex.Message));
            }
        };
    }
    private static Dictionary<string, (byte[] Bytes, string Type)> ReaderAssets() {
        var assembly = typeof(ChatMessage).Assembly;
        var catalogs = new Dictionary<string, Dictionary<string, string>>();
        foreach (string language in new[] { "en", "zh-CN" }) {
            using var input = assembly.GetManifestResourceStream("Storloom.Core.Localization.Locales." + language + ".json")!;
            catalogs[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(input)!.Where(p => p.Key.StartsWith("Reader", StringComparison.Ordinal))
                .ToDictionary(p => p.Key, p => p.Value);
        }
        var assets = new Dictionary<string, (byte[], string)>();
        foreach (var (name, mime) in new[] { ("messages.html", "text/html"), ("reader.css", "text/css"), ("reader.js", "text/javascript") }) {
            using var input = assembly.GetManifestResourceStream("Storloom.Core.Chats.Reader." + name)!;
            using var reader = new StreamReader(input); string text = reader.ReadToEnd();
            if (name == "reader.js") text = "window.archiveContentBase='https://archive.storloom.invalid/';window.readerCatalogs=" + JsonSerializer.Serialize(catalogs) + ";\n" + text;
            if (name == "messages.html") text = text.Replace("reader.css\"", "reader.css?v=" + ProductInfo.Version + "\"", StringComparison.Ordinal)
                .Replace("reader.js\"", "reader.js?v=" + ProductInfo.Version + "\"", StringComparison.Ordinal)
                .Replace("src=\"archive-index.js\"", "src=\"https://archive.storloom.invalid/archive-index.js\"", StringComparison.Ordinal)
                .Replace("script-src 'self'", "script-src 'self' https://archive.storloom.invalid", StringComparison.Ordinal)
                .Replace("img-src 'self'", "img-src 'self' https://archive.storloom.invalid", StringComparison.Ordinal)
                .Replace("media-src 'self'", "media-src 'self' https://archive.storloom.invalid", StringComparison.Ordinal);
            assets["/" + name] = (Encoding.UTF8.GetBytes(text), mime);
        }
        return assets;
    }
}
