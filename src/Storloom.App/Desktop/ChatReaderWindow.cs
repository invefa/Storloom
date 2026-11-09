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
                    bool copyingImage=false;
                    try {
                        if(args.WebMessageAsJson.Length>34*1024*1024)return;
                        using var message=JsonDocument.Parse(args.WebMessageAsJson);var root=message.RootElement;
                        string? action=root.GetProperty("action").GetString();
                        if(action!="clipboard-image"&&args.WebMessageAsJson.Length>1024*1024)return;
                        if(action=="copy-text"){string value=root.GetProperty("text").GetString()??"";if(value.Length>0)Clipboard.SetText(value);core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T("ReaderCopied")}));return;}
                        if(action=="clipboard-image"){copyingImage=true;Clipboard.SetImage(DecodeClipboardImage(root.GetProperty("data").GetString()));core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T("ReaderCopied")}));return;}
                        if(action is not ("save-image" or "save-file"))return;
                        string? path=root.GetProperty("path").GetString();string source=action=="save-image"?ResolveImagePath(directory,path):ResolveResourcePath(directory,path);
                        string name=Path.GetFileName(root.TryGetProperty("name",out var valueName)?valueName.GetString():null)??"image";
                        foreach(char invalid in Path.GetInvalidFileNameChars())name=name.Replace(invalid,'_');
                        string extension=Path.GetExtension(source);if(!name.EndsWith(extension,StringComparison.OrdinalIgnoreCase))name+=extension;
                        var picker=new Microsoft.Win32.SaveFileDialog{Title=L.T(action=="save-image"?"ReaderSaveImage":"ReaderSaveFile"),FileName=name,Filter="File|*"+extension,AddExtension=true,DefaultExt=extension};
                        if(picker.ShowDialog(this)!=true)return;
                        SaveResourceFile(source,picker.FileName);
                        core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T(action=="save-image"?"ReaderImageSaved":"ReaderFileSaved")}));
                    }catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or NotSupportedException or FormatException or OverflowException){if(!closed)core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T(copyingImage?"ReaderCopyImageFailed":"ReaderSaveFailed")}));}
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
                        string relative=new Uri(args.Request.Uri).AbsolutePath;
                        if(relative.StartsWith("/image-copy/",StringComparison.Ordinal)) {
                            try {string path=ResolveImagePath(directory,"assets/"+relative[12..]);if(new FileInfo(path).Length>32*1024*1024)throw new InvalidDataException("Image exceeds clipboard source limit.");string mime=Path.GetExtension(path).ToLowerInvariant() switch {".jpg" or ".jpeg"=>"image/jpeg",".gif"=>"image/gif",".webp"=>"image/webp",".bmp"=>"image/bmp",_=>"image/png"};
                                args.Response=environment.CreateWebResourceResponse(File.OpenRead(path),200,"OK","Content-Type: "+mime+"\r\nCache-Control: no-store");
                            }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){args.Response=environment.CreateWebResourceResponse(null,404,"Image unavailable","");}
                        }
                        else if (assets.TryGetValue(relative, out var asset))
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
    internal static void SaveResourceFile(string source,string target) {
        source=PathPolicy.Normalize(source);target=PathPolicy.Normalize(target);PathPolicy.AssertNoLinks(source);
        if(target.Equals(source,StringComparison.OrdinalIgnoreCase))return;
        PathPolicy.AssertNoLinks(Path.GetDirectoryName(target)!);
        if(File.Exists(target)) {
            PathPolicy.AssertNoLinks(target);
            if(NativeFile.ReadStamp(target).Links!=1)throw new IOException("Shared resource destination cannot be overwritten.");
        }
        File.Copy(source,target,true);
    }
    internal static System.Windows.Media.Imaging.BitmapSource DecodeClipboardImage(string? encoded) {
        if(encoded==null||encoded.Length>32*1024*1024)throw new InvalidDataException("Invalid clipboard image size.");
        byte[] bytes;try{bytes=Convert.FromBase64String(encoded);}catch(FormatException){throw new InvalidDataException("Invalid clipboard image.");}
        if(bytes.Length<33||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||!bytes.AsSpan(12,4).SequenceEqual("IHDR"u8))throw new InvalidDataException("Expected a PNG image.");
        uint width=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4)),height=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4));
        if(width==0||height==0||(ulong)width*height>32_000_000)throw new InvalidDataException("Clipboard image dimensions are too large.");
        using var input=new MemoryStream(bytes,false);var decoder=new System.Windows.Media.Imaging.PngBitmapDecoder(input,System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var image=decoder.Frames[0];image.Freeze();return image;
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
            if (name == "reader.js") text = "window.readerImageCopyBase='https://reader.storloom.invalid/image-copy/';window.archiveContentBase='https://archive.storloom.invalid/';window.readerCatalogs=" + JsonSerializer.Serialize(catalogs) + ";\n" + text;
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
