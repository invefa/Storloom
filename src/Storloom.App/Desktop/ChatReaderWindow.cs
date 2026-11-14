using System.Security.Cryptography;
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
    internal string LoadingStage { get; private set; }="created";
    internal Func<string?>? VaultPasswordProvider { get; set; }
    private ChatVault? vault;
    private ArchiveStore? store;
    private FileStream? readerLease;
    private readonly SemaphoreSlim readWork=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private readonly Dictionary<string,BufferedResource> resourceCache=new(StringComparer.Ordinal);
    private long resourceCacheBytes;
    internal Action<string>? ResourceReadObserver {get;set;}
    private async Task<T> ReadAsync<T>(Func<T> work){
        await readWork.WaitAsync(lifetime.Token);
        try{return await Task.Run(()=>{lifetime.Token.ThrowIfCancellationRequested();return work();});}
        finally{readWork.Release();}
    }
    internal string? LastClipboardError { get; private set; }
    internal Action<System.Windows.Media.Imaging.BitmapSource> ImageClipboardWriter { get; set; } = Clipboard.SetImage;
    public ChatReaderWindow(string directory, string dataRoot, bool reduceMotion = false,string? archiveScope = null) : base(L.T("ChatArchive"), 1200, 820) {
        WindowGeometry.Attach(this,dataRoot,"reader",480,360);PickerHistory.Attach(this,dataRoot);
        var loading=View.Stack(View.Text(L.T("ReaderOpening")),new System.Windows.Controls.ProgressBar{IsIndeterminate=true,Height=4,Margin=new Thickness(0,14,0,0)});loading.VerticalAlignment=VerticalAlignment.Center;loading.HorizontalAlignment=HorizontalAlignment.Center;
        var surface=new System.Windows.Controls.Grid();surface.Children.Add(Browser);surface.Children.Add(loading);Body.Children.Add(surface);
        Closed += (_, _) => { closed = true;lifetime.Cancel();Browser.Dispose();_ = Task.Run(async()=>{await readWork.WaitAsync();try{foreach(var resource in resourceCache.Values)CryptographicOperations.ZeroMemory(resource.Bytes);resourceCache.Clear();readerLease?.Dispose();vault?.Dispose();store?.Dispose();}finally{readWork.Release();}}); };
        Loaded += async (_, _) => {
            try {
                LoadingStage="opening archive";directory = PathPolicy.Normalize(directory); PathPolicy.AssertNoLinks(directory);
                if(File.Exists(Path.Combine(directory,".storloom-export.json")))throw new IOException(L.T("StoreFinishExport"));
                bool packed=ArchiveStore.Exists(directory);
                if(!packed||!ArchiveStore.SnapshotReads(directory)){
                string lockPath=Path.Combine(packed?ArchiveStore.ResolveRoot(directory):directory,packed?"operation.lock":".export.lock");
                if(!File.Exists(lockPath)){using var create=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read);}
                PathPolicy.AssertNoLinks(lockPath);readerLease=new FileStream(lockPath,FileMode.Open,FileAccess.Read,FileShare.Read);}
                if(ChatVault.Exists(directory)||packed&&ArchiveStore.NeedsPassword(directory)){
                    string? secret;
                    if(VaultPasswordProvider!=null){secret=VaultPasswordProvider();VaultPasswordProvider=null;}
                    else{var password=new VaultPasswordWindow(false,false){Owner=this};secret=password.ShowDialog()==true?password.Password:null;password.ClearSecret();}
                    if(secret==null){Close();return;}
                    try{if(packed)store=await Task.Run(()=>ArchiveStore.Open(directory,secret.AsSpan()));else vault=await Task.Run(()=>ChatVault.Open(directory,secret.AsSpan()));}finally{secret=null;}
                    if(closed){vault?.Dispose();store?.Dispose();return;}
                    bool complete=store!=null||(await Task.Run(()=>vault!.Status)).Complete;
                    Title=L.T("ChatArchive")+" — "+L.T(complete?"VaultReaderProtected":"VaultReaderPartial");Caption.Title=Title;
                }
                else if(packed)store=await Task.Run(()=>ArchiveStore.Open(directory));
                if(closed){store?.Dispose();return;}
                if(store!=null&&store.ImportCursor?.Stage!="complete")throw new IOException(L.T("StoreFinishMigration"));
                LoadingStage="starting browser environment";var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataRoot, "reader-browser"));
                if (closed) return;
                LoadingStage="initializing browser";if(vault!=null||store?.Protected==true){var options=environment.CreateCoreWebView2ControllerOptions();options.IsInPrivateModeEnabled=true;options.ProfileName="ProtectedReader";await Browser.EnsureCoreWebView2Async(environment,options);}
                else await Browser.EnsureCoreWebView2Async(environment); if (closed) return;
                LoadingStage="configuring browser";var core = Browser.CoreWebView2;
                string preferenceScope=store?.Id??(archiveScope==null?JsonFiles.Hash(directory.ToUpperInvariant()):JsonFiles.Hash(new{archive=archiveScope}));
                string preferenceFile=Path.Combine(dataRoot,"reader-preferences",preferenceScope+".json");
                await core.AddScriptToExecuteOnDocumentCreatedAsync("window.readerPreferenceScope="+JsonSerializer.Serialize(preferenceScope)+";window.readerHostPreferences="+ReaderPreferences.Load(preferenceFile).ToJsonString()+";window.readerDefaults="+JsonSerializer.Serialize(new{dark=Theme.IsDark})+";");
                core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = true;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.WebMessageReceived += async (_,args) => {
                    if(args.Source!="https://reader.storloom.invalid/messages.html" && !args.Source.StartsWith("https://reader.storloom.invalid/messages.html#",StringComparison.Ordinal))return;
                    bool copyingImage=false;
                    try {
                        if(args.WebMessageAsJson.Length>34*1024*1024)return;
                        using var message=JsonDocument.Parse(args.WebMessageAsJson);var root=message.RootElement;
                        string? action=root.GetProperty("action").GetString();
                        if(action=="store-reference"&&store!=null){int requestId=root.GetProperty("requestId").GetInt32();try{string conversation=root.GetProperty("conversation").GetString()??"",reference=root.GetProperty("reference").GetString()??"";byte[] target=await ReadAsync(()=>store.ReaderReference(conversation,reference,root.TryGetProperty("metadataOnly",out var metadata)&&metadata.ValueKind==JsonValueKind.True));if(!closed)core.PostWebMessageAsJson("{\"action\":\"store-reference-result\",\"requestId\":"+requestId+",\"result\":"+Encoding.UTF8.GetString(target)+"}");}catch{if(!closed)core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="store-reference-result",requestId,error=true}));}return;}
                        if(action=="store-search"&&store!=null){
                            int searchId=root.GetProperty("requestId").GetInt32();
                            try{string query=root.GetProperty("text").GetString()??"";string? cursor=root.TryGetProperty("cursor",out var c)&&c.ValueKind==JsonValueKind.String?c.GetString():null;bool reverse=root.TryGetProperty("reverse",out var r)&&r.GetBoolean();
                                byte[] result=await ReadAsync(()=>store.ReaderSearch(query,cursor,reverse));if(!closed)core.PostWebMessageAsJson("{\"action\":\"store-search-result\",\"requestId\":"+searchId+",\"result\":"+Encoding.UTF8.GetString(result)+"}");
                            }catch{if(!closed)core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="store-search-result",requestId=searchId,error=true}));}return;
                        }
                        if(action!="clipboard-image"&&args.WebMessageAsJson.Length>1024*1024)return;
                        if(action=="reader-preferences"){if(args.WebMessageAsJson.Length<=8192)ReaderPreferences.Save(preferenceFile,root.GetProperty("preferences"));return;}
                        if(action=="copy-text"){string value=root.GetProperty("text").GetString()??"";if(value.Length>0)await WriteClipboardAsync(()=>Clipboard.SetText(value));core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T("ReaderCopied")}));return;}
                        if(action=="clipboard-image"){copyingImage=true;var bitmap=DecodeClipboardImage(root.GetProperty("data").GetString());await WriteClipboardAsync(()=>ImageClipboardWriter(bitmap));core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T("ReaderCopied")}));return;}
                        if(action is not ("save-image" or "save-file"))return;
                        string? path=root.GetProperty("path").GetString();string source=vault!=null||store!=null?ValidateVaultResource(path,action=="save-image"):action=="save-image"?ResolveImagePath(directory,path):ResolveResourcePath(directory,path);
                        string name=Path.GetFileName(root.TryGetProperty("name",out var valueName)?valueName.GetString():null)??"image";
                        foreach(char invalid in Path.GetInvalidFileNameChars())name=name.Replace(invalid,'_');
                        string extension=Path.GetExtension(source);if(!name.EndsWith(extension,StringComparison.OrdinalIgnoreCase))name+=extension;
                        var picker=new Microsoft.Win32.SaveFileDialog{Title=L.T(action=="save-image"?"ReaderSaveImage":"ReaderSaveFile"),FileName=name,Filter="File|*"+extension,AddExtension=true,DefaultExt=extension};
                        if(PickerHistory.Show(picker,this,action=="save-image"?"reader-save-image":"reader-save-file")!=true)return;
                        if(vault==null&&store==null)await ReadAsync(()=>{SaveResourceFile(source,picker.FileName);return true;});
                        else await ReadAsync(()=>{using var input=store!=null?store.OpenFile(source):vault!.OpenRead(source);SaveDecryptedResource(input,picker.FileName);return true;});
                        core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T(action=="save-image"?"ReaderImageSaved":"ReaderFileSaved")}));
                    }catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or NotSupportedException or FormatException or OverflowException or CryptographicException){if(copyingImage)LastClipboardError=ex.GetType().Name+":"+ex.HResult.ToString("X");if(!closed)core.PostWebMessageAsJson(JsonSerializer.Serialize(new{action="reader-status",text=L.T(copyingImage?"ReaderCopyImageFailed":"ReaderSaveFailed")}));}
                };
                core.Settings.AreDefaultScriptDialogsEnabled = false; core.Settings.AreDevToolsEnabled = false; core.Settings.IsStatusBarEnabled = false;
                if(vault==null&&store==null)core.SetVirtualHostNameToFolderMapping("archive.storloom.invalid", directory, CoreWebView2HostResourceAccessKind.DenyCors);
                bool Allowed(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == "https" && parsed.Port == 443 && parsed.UserInfo.Length == 0 && parsed.Host is "archive.storloom.invalid" or "reader.storloom.invalid";
                core.NavigationStarting += (_, args) => { if (!Allowed(args.Uri)) args.Cancel = true; };
                core.FrameNavigationStarting += (_, args) => args.Cancel = true;
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, args) => args.Cancel = true;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                var assets = ReaderAssets();
                core.WebResourceRequested += async (_, args) => {
                    if (!Allowed(args.Request.Uri)) args.Response = environment.CreateWebResourceResponse(null, 403, "Local archive only", "");
                    else if((vault!=null||store!=null)&&new Uri(args.Request.Uri).Host=="archive.storloom.invalid") {
                        using var deferral=args.GetDeferral();
                        try{string relative=Uri.UnescapeDataString(new Uri(args.Request.Uri).AbsolutePath.TrimStart('/'));
                            string? range=args.Request.Headers.Contains("Range")?args.Request.Headers.GetHeader("Range"):null;
                            var resource=await ReadAsync(()=>{
                            ResourceReadObserver?.Invoke(relative);
                            string cacheKey=relative+"\n"+range;if(resourceCache.TryGetValue(cacheKey,out var cached))return cached;
                            Stream input;
                            if(store!=null&&relative=="archive-index.js")input=new MemoryStream(store.ReaderIndex(),false);
                            else if(store!=null&&System.Text.RegularExpressions.Regex.Match(relative,"^conversations/([a-f0-9]{24})-refs\\.js$") is{Success:true} reference)input=new MemoryStream(store.ReaderReferences(reference.Groups[1].Value),false);
                            else if(store!=null&&System.Text.RegularExpressions.Regex.Match(relative,"^conversations/([a-f0-9]{24})-([0-9]+)\\.js$") is{Success:true} match)input=new MemoryStream(store.ReaderPage(match.Groups[1].Value,int.Parse(match.Groups[2].Value,System.Globalization.CultureInfo.InvariantCulture)),false);
                            else input=store!=null?store.OpenFile(relative):vault!.OpenRead(relative);
                            var value=BufferResource(input,ContentType(relative),range);if(value.Bytes.Length<=512*1024){if(resourceCacheBytes+value.Bytes.Length>16*1024*1024){resourceCache.Clear();resourceCacheBytes=0;}resourceCache[cacheKey]=value;resourceCacheBytes+=value.Bytes.Length;}return value;});
                            if(!closed)args.Response=environment.CreateWebResourceResponse(new MemoryStream(resource.Bytes,false),resource.Status,resource.Status==200?"OK":"Partial Content",resource.Headers);
                        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or OverflowException or JsonException or OperationCanceledException){if(!closed)args.Response=environment.CreateWebResourceResponse(null,404,"Protected resource unavailable","Cache-Control: no-store");}
                    }
                    else if (new Uri(args.Request.Uri).Host == "reader.storloom.invalid") {
                        string relative=new Uri(args.Request.Uri).AbsolutePath;
                        if(relative.StartsWith("/image-copy/",StringComparison.Ordinal)) {
                            using var deferral=args.GetDeferral();
                            try {var resource=await ReadAsync(()=>{string path=vault!=null||store!=null?ValidateVaultResource("assets/"+relative[12..],true):ResolveImagePath(directory,"assets/"+relative[12..]);using var input=store!=null?store.OpenFile(path):vault!=null?vault.OpenRead(path):File.OpenRead(path);if(input.Length>32*1024*1024)throw new InvalidDataException("Image exceeds clipboard source limit.");return BufferResource(input,ContentType(path),null);});
                                if(!closed)args.Response=environment.CreateWebResourceResponse(new MemoryStream(resource.Bytes,false),resource.Status,"OK",resource.Headers);
                            }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException or OperationCanceledException){if(!closed)args.Response=environment.CreateWebResourceResponse(null,404,"Image unavailable","");}
                        }
                        else if (assets.TryGetValue(relative, out var asset))
                            args.Response = environment.CreateWebResourceResponse(new MemoryStream(asset.Bytes, false), 200, "OK", "Content-Type: " + asset.Type + "; charset=utf-8\r\nCache-Control: no-store");
                        else args.Response = environment.CreateWebResourceResponse(null, 404, "Unknown reader asset", "");
                    }
                };
                if (reduceMotion) await core.AddScriptToExecuteOnDocumentCreatedAsync("document.addEventListener('DOMContentLoaded',()=>document.documentElement.classList.add('reduce-motion'));");
                core.NavigationCompleted+=(_,_)=>loading.Visibility=Visibility.Collapsed;
                LoadingStage="loading page";core.Navigate("https://reader.storloom.invalid/messages.html" + (Theme.IsDark ? "#dark" : "#light"));
            } catch (Exception ex) when (!closed) {
                loading.Visibility=Visibility.Collapsed;readerLease?.Dispose();vault?.Dispose();store?.Dispose();Browser.Visibility = Visibility.Collapsed; Body.Children.Add(View.Text(L.T(ChatVault.Exists(directory)||ArchiveStore.Exists(directory)?"VaultOpenFailed":"ReaderRuntimeMissing")+"\n"+ex.Message));
            }
        };
    }
    private static string ValidateVaultResource(string? path,bool image) {
        string formats=image?"png|jpg|jpeg|gif|webp|bmp":"pdf|doc|docx|ppt|pptx|xls|xlsx|zip|7z|rar|txt|md|csv";
        if(path==null||!System.Text.RegularExpressions.Regex.IsMatch(path,"^assets/[a-f0-9]{64}\\.("+formats+")$"))throw new InvalidDataException("Invalid protected resource.");return path;
    }
    private static string ContentType(string path)=>Path.GetExtension(path).ToLowerInvariant() switch{
        ".js"=>"application/javascript", ".css"=>"text/css", ".html"=>"text/html", ".json"=>"application/json",
        ".jpg" or ".jpeg"=>"image/jpeg", ".png"=>"image/png", ".gif"=>"image/gif", ".webp"=>"image/webp", ".bmp"=>"image/bmp",
        ".mp4"=>"video/mp4", ".webm"=>"video/webm", ".wav"=>"audio/wav", ".mp3"=>"audio/mpeg", ".ogg"=>"audio/ogg", _=>"application/octet-stream"};
    internal sealed record BufferedResource(byte[] Bytes,int Status,string Headers);
    internal static BufferedResource BufferResource(Stream input,string type,string? range){
        using(input){long length=input.Length,start=0,end=length-1;bool partial=range!=null;
            string headers="Content-Type: "+type+"\r\nCache-Control: no-store\r\nAccept-Ranges: bytes";
            if(range!=null){var match=System.Text.RegularExpressions.Regex.Match(range,"^bytes=([0-9]+)-([0-9]*)$");if(!match.Success||!long.TryParse(match.Groups[1].Value,out start)||start>=length)return new([],416,"Content-Range: bytes */"+length);if(match.Groups[2].Value.Length>0){if(!long.TryParse(match.Groups[2].Value,out long requested)||requested<start)return new([],416,"Content-Range: bytes */"+length);end=Math.Min(end,requested);}}
            bool media=type.StartsWith("video/",StringComparison.Ordinal)||type.StartsWith("audio/",StringComparison.Ordinal);long limit=media?4*1024*1024:64*1024*1024;
            if(end-start+1>limit){if(!media)throw new IOException("Reader resource exceeds the inline limit.");end=start+limit-1;partial=true;}
            if(start!=0)input.Position=start;byte[] bytes=new byte[checked((int)Math.Max(0,end-start+1))];input.ReadExactly(bytes);
            if(partial)headers+="\r\nContent-Range: bytes "+start+"-"+end+"/"+length;
            return new(bytes,partial?206:200,headers+"\r\nContent-Length: "+bytes.Length);
        }
    }
    private static void SaveDecryptedResource(Stream input,string target){target=PathPolicy.Normalize(target);PathPolicy.AssertNoLinks(Path.GetDirectoryName(target)!);if(File.Exists(target)){PathPolicy.AssertNoLinks(target);if(NativeFile.ReadStamp(target).Links!=1)throw new IOException("Shared destination cannot be overwritten.");}using var output=new FileStream(target,FileMode.Create,FileAccess.Write,FileShare.None);input.CopyTo(output);output.Flush(true);}
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
    private async Task WriteClipboardAsync(Action write) {
        for (int attempt = 0; ; attempt++) {
            if (closed) throw new InvalidOperationException("The reading window was closed.");
            try { write(); return; }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 3) { await Task.Delay(60); }
        }
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
