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
                core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = true;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.WebMessageReceived += (_,args) => {
                    if(args.Source!="https://reader.storloom.invalid/messages.html" && !args.Source.StartsWith("https://reader.storloom.invalid/messages.html#",StringComparison.Ordinal))return;
                    try {
                        if(args.WebMessageAsJson.Length>1024*1024)return;
                        using var message=JsonDocument.Parse(args.WebMessageAsJson);var root=message.RootElement;
                        string? action=root.GetProperty("action").GetString();
                        if(action=="copy-text"){string value=root.GetProperty("text").GetString()??"";if(value.Length>0)Clipboard.SetText(value);core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T("ReaderCopied")}));return;}
                        if(action is not ("save-image" or "save-file"))return;
                        string? path=root.GetProperty("path").GetString();string source=action=="save-image"?ResolveImagePath(directory,path):ResolveResourcePath(directory,path);
                        string name=Path.GetFileName(root.TryGetProperty("name",out var valueName)?valueName.GetString():null)??"image";
                        foreach(char invalid in Path.GetInvalidFileNameChars())name=name.Replace(invalid,'_');
                        string extension=Path.GetExtension(source);if(!name.EndsWith(extension,StringComparison.OrdinalIgnoreCase))name+=extension;
                        var picker=new Microsoft.Win32.SaveFileDialog{Title=L.T(action=="save-image"?"ReaderSaveImage":"ReaderSaveFile"),FileName=name,Filter="File|*"+extension,AddExtension=true,DefaultExt=extension};
                        if(picker.ShowDialog(this)!=true)return;
                        string target=PathPolicy.Normalize(picker.FileName);PathPolicy.AssertNoLinks(target);
                        if(target.Equals(source,StringComparison.OrdinalIgnoreCase))return;
                        if(File.Exists(target)&&NativeFile.ReadStamp(target).Links!=1)throw new IOException("Shared image destination cannot be overwritten.");
                        File.Copy(source,target,true);
                        core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T(action=="save-image"?"ReaderImageSaved":"ReaderFileSaved")}));
                    }catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException){if(!closed)core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T("ReaderSaveFailed")}));}
                };
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
    private static string ResolveResourcePath(string directory,string? relative) {
        if(relative==null||!System.Text.RegularExpressions.Regex.IsMatch(relative,"^assets/[a-f0-9]{64}\\.(pdf|doc|docx|ppt|pptx|xls|xlsx|zip|7z|rar|txt|md|csv)$"))throw new InvalidDataException("Invalid archive resource.");
        string path=TransferService.Child(directory,relative);PathPolicy.AssertNoLinks(path);
        if(!File.Exists(path))throw new FileNotFoundException("Archive resource is missing.");return path;
    }
    internal static string ResolveImagePath(string directory,string? relative) {
        if(relative==null||!System.Text.RegularExpressions.Regex.IsMatch(relative,"^assets/[a-f0-9]{64}\\.(png|jpg|jpeg|gif|webp|bmp)$"))throw new InvalidDataException("Invalid archive image.");
        string path=TransferService.Child(directory,relative);PathPolicy.AssertNoLinks(path);
        if(!File.Exists(path))throw new FileNotFoundException("Archive image is missing.");return path;
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
