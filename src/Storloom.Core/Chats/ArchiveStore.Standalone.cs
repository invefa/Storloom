using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Storloom.Core.Localization;
namespace Storloom.Core.Chats;

internal sealed partial class ArchiveStore {
    internal const string StandaloneEntry="index.html";
    private static string EntryDirectory(string directory)=>Path.GetFileName(directory)==DataDirectory?Path.GetDirectoryName(directory)!:directory;
    internal bool StandaloneRequested{get;set;}
    internal bool HasStandalone=>File.Exists(Path.Combine(root,"browser","roots.json"));
    private sealed record BrowserRoot(string Head,string File);
    private sealed record BrowserRoots(int Version,BrowserRoot[] Roots);
    private sealed record BrowserSnapshot(int Version,string ArchiveId,string Head,string Query,JsonElement Index);
    private static string ReaderResource(string name){var assembly=typeof(ArchiveStore).Assembly;string suffix=".Chats.Reader."+name.Replace('/','.');string resource=assembly.GetManifestResourceNames().Single(n=>n.EndsWith(suffix,StringComparison.Ordinal));using var input=assembly.GetManifestResourceStream(resource)!;using var text=new StreamReader(input);return text.ReadToEnd().Replace("\r\n","\n");}
    internal string EnableStandalone(IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        if(formatVersion!=3||IsNative(root))throw new InvalidOperationException(L.T("StandaloneUnsupportedArchive"));
        using var lease=Lease();lock(readGate){if(Head==null)throw new InvalidOperationException("Publish a reading snapshot before enabling browser reading.");query.EnableBrowserStatistics(progress,cancellation);PublishBrowser(Head,query,OpenFile,progress,cancellation);WriteBrowserRuntime(cancellation);return Path.Combine(EntryDirectory(root),StandaloneEntry);}
    }
    private void PublishBrowser(string head,ArchiveQueryCache projection,Func<string,Stream> open,IProgress<ScanProgress>? progress,CancellationToken cancellation){
        OperationPause.Check(cancellation);projection.EnableBrowserStatistics(progress,cancellation);projection.Checkpoint();
        string relative=Path.GetRelativePath(root,projection.DatabasePath).Replace('\\','/');
        if(relative!="query/query.db"&&!System.Text.RegularExpressions.Regex.IsMatch(relative,"^query/stage-[a-f0-9]{32}/query[.]db$"))throw new IOException("Browser index must be inside its archive.");
        byte[] raw=ReaderIndex(projection,open);const string prefix="window.archiveIndex=";using var index=JsonDocument.Parse(Encoding.UTF8.GetString(raw)[prefix.Length..].TrimEnd(';'));
        var state=new BrowserSnapshot(1,Id,head,relative,index.RootElement.Clone());byte[] json=JsonSerializer.SerializeToUtf8Bytes(state,JsonFiles.Options),packed=ArchivePackCodec.Compress(json),encoded=ProtectRecord(packed,"browser/snapshot");
        string directory=Path.Combine(root,"browser");Directory.CreateDirectory(directory);PathPolicy.AssertNoLinks(directory);
        try{PublishBrowserBytes(directory,head+".snapshot",encoded,cancellation);var roots=new List<BrowserRoot>{new(head,head+".snapshot")};string mapping=Path.Combine(directory,"roots.json");
            if(File.Exists(mapping)){PathPolicy.AssertNoLinks(mapping);if(new FileInfo(mapping).Length>16384)throw new InvalidDataException("Invalid browser root map.");var previous=JsonFiles.Read<BrowserRoots>(mapping);if(previous.Version!=1)throw new InvalidDataException("Unsupported browser root map.");roots.AddRange(previous.Roots.Where(r=>r.Head!=head));}
            PublishBrowserBytes(directory,"roots.json",JsonSerializer.SerializeToUtf8Bytes(new BrowserRoots(1,roots.Take(16).ToArray()),JsonFiles.Options),cancellation);progress?.Report(new("browser-publish",1,encoded.Length,encoded.Length,1));
        }finally{CryptographicOperations.ZeroMemory(raw);CryptographicOperations.ZeroMemory(json);CryptographicOperations.ZeroMemory(packed);CryptographicOperations.ZeroMemory(encoded);}
    }
    private static void PublishBrowserBytes(string directory,string name,byte[] bytes,CancellationToken cancellation){
        OperationPause.Check(cancellation);string target=TransferService.Child(directory,name);PathPolicy.AssertNoLinks(directory);if(File.Exists(target)){PathPolicy.AssertNoLinks(target);if(NativeFile.ReadStamp(target).Links!=1)throw new IOException("Shared browser metadata.");if(new FileInfo(target).Length==bytes.Length){using var existing=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing),SHA256.HashData(bytes)))return;}}
        string temporary=Path.Combine(directory,"browser-"+Guid.NewGuid().ToString("N")+".tmp");
        try{using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}OperationPause.Check(cancellation);File.Move(temporary,target,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private void CopyBrowserSnapshot(string destination,string? snapshot,IProgress<ScanProgress>? progress,CancellationToken cancellation){
        if(snapshot==null||!HasStandalone)return;
        string browser=Path.Combine(root,"browser"),statePath=Path.Combine(browser,snapshot+".snapshot");if(!File.Exists(statePath))return;
        PathPolicy.AssertNoLinks(statePath);if(new FileInfo(statePath).Length>ArchiveCommitLog.MaximumPayloadBytes)throw new InvalidDataException("Browser snapshot too large.");byte[] cipher=File.ReadAllBytes(statePath),packed=UnprotectRecord(cipher,"browser/snapshot"),plain=ArchivePackCodec.Expand(packed);
        try{var state=JsonSerializer.Deserialize<BrowserSnapshot>(plain,JsonFiles.Options)??throw new InvalidDataException("Invalid browser snapshot.");if(state.ArchiveId!=Id||state.Head!=snapshot||!System.Text.RegularExpressions.Regex.IsMatch(state.Query,"^query/(?:stage-[a-f0-9]{32}/)?query[.]db$"))throw new InvalidDataException("Browser snapshot identity mismatch.");
            string source=TransferService.Child(root,state.Query);if(!File.Exists(source))return;PathPolicy.AssertNoLinks(source);if(snapshot==Head)query.Checkpoint();
            string target=TransferService.Child(destination,state.Query);Directory.CreateDirectory(Path.GetDirectoryName(target)!);PathPolicy.AssertNoLinks(Path.GetDirectoryName(target)!);
            long bytes=0,total=new FileInfo(source).Length*2;ChatWorkspaceCopy.File(()=>new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete),target,target+".copy",cancellation,n=>{bytes+=n;progress?.Report(new("archive-query-publish",0,bytes,total,1));});
            var projections=new List<Projection>{new(snapshot,state.Query[..^"/query.db".Length])};string mapping=Path.Combine(destination,"projection.json");if(File.Exists(mapping)){PathPolicy.AssertNoLinks(mapping);projections.AddRange(JsonFiles.Read<Projection[]>(mapping).Where(p=>p.Head!=snapshot));}JsonFiles.Write(mapping,projections.Where(p=>p.Directory!="query").Take(16).ToArray());
            string targetBrowser=Path.Combine(destination,"browser");Directory.CreateDirectory(targetBrowser);PathPolicy.AssertNoLinks(targetBrowser);PublishBrowserBytes(targetBrowser,snapshot+".snapshot",cipher,cancellation);
            var roots=new List<BrowserRoot>{new(snapshot,snapshot+".snapshot")};string rootsPath=Path.Combine(targetBrowser,"roots.json");if(File.Exists(rootsPath)){PathPolicy.AssertNoLinks(rootsPath);roots.AddRange(JsonFiles.Read<BrowserRoots>(rootsPath).Roots.Where(r=>r.Head!=snapshot));}PublishBrowserBytes(targetBrowser,"roots.json",JsonSerializer.SerializeToUtf8Bytes(new BrowserRoots(1,roots.Take(16).ToArray()),JsonFiles.Options),cancellation);
            string entry=Path.Combine(EntryDirectory(root),StandaloneEntry);PathPolicy.AssertNoLinks(entry);if(new FileInfo(entry).Length>16*1024*1024)throw new InvalidDataException("Browser runtime too large.");PublishBrowserBytes(EntryDirectory(destination),StandaloneEntry,File.ReadAllBytes(entry),cancellation);
        }finally{CryptographicOperations.ZeroMemory(cipher);CryptographicOperations.ZeroMemory(packed);CryptographicOperations.ZeroMemory(plain);}
    }
    internal static void RefreshStandaloneRuntime(string directory,CancellationToken cancellation=default){
        string root=ResolveRoot(directory),marker=Path.Combine(root,"archive.json");PathPolicy.AssertNoLinks(marker);if(new FileInfo(marker).Length>4096)throw new InvalidDataException("Invalid archive marker.");var config=JsonFiles.Read<Configuration>(marker);
        if(config.Version!=3||config.Kind!="reading"||!Guid.TryParseExact(config.Id,"N",out _)||!File.Exists(Path.Combine(root,"browser","roots.json")))throw new InvalidOperationException("Prepare an independent HTML snapshot before refreshing its entry.");
        string lockPath=Path.Combine(root,"operation.lock");if(File.Exists(lockPath))PathPolicy.AssertNoLinks(lockPath);using var lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);WriteBrowserRuntime(root,config.Id,cancellation);
    }
    private void WriteBrowserRuntime(CancellationToken cancellation)=>WriteBrowserRuntime(root,Id,cancellation);
    private static void WriteBrowserRuntime(string root,string id,CancellationToken cancellation){
        string brotli=ReaderResource("Standalone/Vendor/brotli.js").Replace("export default init;","return {init,DecompressStream};").Replace("export ","").Replace("import.meta.url","''");
        string worker=ReaderResource("Standalone/Vendor/sqlite3.js")+"\nconst Brotli=(()=>{"+brotli+"})();\n"+ReaderResource("Standalone/worker.js").Replace("__SQLITE_WASM__",ReaderResource("Standalone/Vendor/sqlite3.wasm.base64")).Replace("__BROTLI_WASM__",ReaderResource("Standalone/Vendor/brotli.wasm.base64"));
        var catalogs=new Dictionary<string,Dictionary<string,string>>();var assembly=typeof(L).Assembly;
        foreach(string language in new[]{"en","zh-CN"}){using var input=assembly.GetManifestResourceStream("Storloom.Core.Localization.Locales."+language+".json")!;var all=JsonSerializer.Deserialize<Dictionary<string,string>>(input)!;catalogs[language]=all.Where(p=>p.Key.StartsWith("Standalone",StringComparison.Ordinal)||p.Key.StartsWith("Reader",StringComparison.Ordinal)||p.Key=="ChatArchive").ToDictionary(p=>p.Key,p=>p.Value);}
        string template=ReaderResource("messages.html"),body=template[(template.IndexOf("<body>",StringComparison.Ordinal)+6)..template.LastIndexOf("</body>",StringComparison.Ordinal)];
        static string Hash(string text)=>Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        string readerScript="function startStandaloneReader(){\n"+ReaderResource("reader.js")+"\n}",readerHash=Hash(readerScript);
        string readerDocument="<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'sha256-"+readerHash+"'; style-src 'unsafe-inline'; img-src blob: data:; media-src blob:; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'\"><style>"+ReaderResource("reader.css")+"</style></head><body>"+body+"<script>"+readerScript+"</script></body></html>";
        string source="const standaloneEntryId="+JsonSerializer.Serialize(id)+";\nconst standaloneWorkerSource='"+Convert.ToBase64String(Encoding.UTF8.GetBytes(worker))+"';\nconst standaloneReaderDocument='"+Convert.ToBase64String(Encoding.UTF8.GetBytes(readerDocument))+"';\nwindow.readerCatalogs="+JsonSerializer.Serialize(catalogs)+";\n"+ReaderResource("Standalone/bootstrap.js");
        string html="""
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'sha256-__HASH__' 'sha256-__READER_HASH__' 'wasm-unsafe-eval'; worker-src blob:; frame-src 'self'; style-src 'unsafe-inline'; img-src blob: data:; media-src blob:; connect-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'">
<title>Storloom</title><style>__STYLE__</style></head><body>
<section id="standalone-start"><h1 id="standalone-title"></h1><p id="standalone-help"></p>
<div class="standalone-actions"><button id="standalone-resume" hidden></button><button id="standalone-directory"></button><button id="standalone-compatible"></button><button id="standalone-forget" hidden></button><input id="standalone-files" type="file" webkitdirectory multiple hidden></div>
<label class="standalone-remember"><input id="standalone-remember" type="checkbox" checked><span id="standalone-remember-label"></span></label><p id="standalone-memory-note" role="status" hidden></p>
<form id="standalone-unlock" hidden><label id="standalone-secret-label" for="standalone-secret"></label><input id="standalone-secret" type="password" autocomplete="off"><button id="standalone-open" type="submit"></button></form>
<p id="standalone-status" role="status"></p><details><summary id="standalone-license-label"></summary><pre class="standalone-licenses">__NOTICES__</pre></details></section>
<iframe id="standalone-reader" title="Storloom" allowfullscreen hidden></iframe>
<div id="standalone-toolbar" hidden><button id="standalone-refresh"></button><button id="standalone-lock"></button><span id="standalone-activity" role="status"></span></div>
<script>__SCRIPT__</script></body></html>
""";
        html=html.Replace("__HASH__",Hash(source)).Replace("__READER_HASH__",readerHash).Replace("__STYLE__",ReaderResource("Standalone/style.css")).Replace("__NOTICES__",WebUtility.HtmlEncode(ReaderResource("Standalone/Vendor/NOTICE.txt"))).Replace("__SCRIPT__",source);
        PublishBrowserBytes(EntryDirectory(root),StandaloneEntry,Encoding.UTF8.GetBytes(html),cancellation);
    }
}
