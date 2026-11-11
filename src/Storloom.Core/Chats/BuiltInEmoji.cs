using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.Versioning;

namespace Storloom.Core.Chats;

// Assets belong to the locally installed client or backup. None are bundled with Storloom.
public sealed class BuiltInEmoji {
    private sealed record Resource(Func<byte[]> Read, string Extension);
    private readonly Dictionary<string,Resource> resources=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> exported=new(StringComparer.Ordinal);
    private readonly string destination;
    private Regex? wechatTokens;
    private bool tokensReady;
    public IReadOnlyDictionary<string,string> Exported => exported;
    public int CreatedFiles { get; private set; }
    public BuiltInEmoji(string source,string destination,bool includeInstalled=false) {
        this.destination=destination;
        foreach(string relative in new[]{"nt_qq/nt_data/Emoji/BaseEmojiSyastems/EmojiSystermResource","nt_data/Emoji/BaseEmojiSyastems/EmojiSystermResource","Emoji/BaseEmojiSyastems/EmojiSystermResource","nt_qq/nt_data/Emoji/emoji-resource/sysface_res"}) AddQQFolder(Path.Combine(source,relative));
        if(!includeInstalled || !OperatingSystem.IsWindows())return;
        foreach(string root in InstallRoots("QQ")) {
            foreach(string version in new[]{root}.Concat(Directory.Exists(Path.Combine(root,"versions"))?Directory.EnumerateDirectories(Path.Combine(root,"versions")).OrderDescending().Take(3):[]))
                AddQQFolder(Path.Combine(version,"resources/app/resource/default-emojis"));
        }
        foreach(string root in InstallRoots("WeChat").Concat(InstallRoots("Weixin"))) {
            foreach(string version in new[]{root}.Concat(Directory.EnumerateDirectories(root).OrderDescending().Take(4))) {
                string dll=Path.Combine(version,"Weixin.dll");if(!File.Exists(dll))continue;
                try { AddWeChat(QtEmojiResources.Read(dll)); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException or OverflowException or XmlException) { }
                break;
            }
        }
    }
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> InstallRoots(string product) {
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})foreach(var hive in new[]{RegistryHive.LocalMachine,RegistryHive.CurrentUser}) {
            using var registry=RegistryKey.OpenBaseKey(hive,view);
            using var entry=registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\"+product);
            if(entry?.GetValue("InstallLocation") is string path && Directory.Exists(path)) roots.Add(path);
            if(entry?.GetValue("InstallLocation") is string old && product=="WeChat" && Path.GetDirectoryName(old) is string parentRoot && Directory.Exists(Path.Combine(parentRoot,"Weixin")))roots.Add(Path.Combine(parentRoot,"Weixin"));
            if(entry?.GetValue("UninstallString") is string command){int end=command.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);if(end>=0){string? parent=Path.GetDirectoryName(command[..(end+4)].Trim('"'));if(parent!=null && Directory.Exists(parent))roots.Add(parent);}}
        }
        return roots;
    }
    private void AddQQFolder(string directory) {
        if(!Directory.Exists(directory))return;
        try {
            PathPolicy.AssertNoLinks(directory);
            var files=new List<string>();var pending=new Stack<(string Path,int Depth)>();pending.Push((directory,0));int visited=0;
            while(pending.TryPop(out var item) && visited++<4096){PathPolicy.AssertNoLinks(item.Path);files.AddRange(Directory.EnumerateFiles(item.Path,"*.png").Take(2000));if(item.Depth<3)foreach(string child in Directory.EnumerateDirectories(item.Path))if((File.GetAttributes(child)&FileAttributes.ReparsePoint)==0)pending.Push((child,item.Depth+1));}
            foreach(string file in files.OrderBy(f=>f.Contains("apng",StringComparison.OrdinalIgnoreCase)?0:1)) {
                string id=Path.GetFileNameWithoutExtension(file);if(!Regex.IsMatch(id,"^[0-9]{1,6}$"))continue;
                resources.TryAdd("QQ:"+id,new(()=>ReadImage(file),".png"));
            }
            string config=Path.Combine(directory,"default_config.json");if(!File.Exists(config)||new FileInfo(config).Length>4*1024*1024)return;
            PathPolicy.AssertNoLinks(config);using var json=JsonDocument.Parse(File.ReadAllText(config));
            void Aliases(JsonElement node) {
                if(node.ValueKind==JsonValueKind.Array){foreach(var child in node.EnumerateArray())Aliases(child);}
                if(node.ValueKind!=JsonValueKind.Object)return;
                if(node.TryGetProperty("emojiId",out var id)&&node.TryGetProperty("describe",out var description)&&description.ValueKind==JsonValueKind.String && resources.TryGetValue("QQ:"+id.GetString(),out var resource)) {
                    AddAliases("QQ",description.GetString()??"",resource);
                }
                foreach(var child in node.EnumerateObject())if(child.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)Aliases(child.Value);
            }
            Aliases(json.RootElement);
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){ }
    }
    private void AddAliases(string platform,string value,Resource resource) {
        string name=value.Trim().TrimStart('/').Trim('[',']');if(name.Length==0)return;
        foreach(string alias in new[]{value,name,"/"+name,"["+name+"]"})resources.TryAdd(platform+":"+alias,resource);
        tokensReady=false;
    }
    internal void AddWeChat(Dictionary<string,byte[]> files) {
        if(files.TryGetValue("base-aliases.txt",out var aliases)) {
            string[] names=Encoding.UTF8.GetString(aliases).Split('|');for(int i=0;i<Math.Min(99,names.Length);i++) {
                // The legacy alias table follows the old 99-face order. Resource numbers
                // also include inserted faces and gestures; they are not alias positions.
                int ordinal=i+1,resource=ordinal+(ordinal>=87?4:ordinal>=31?2:ordinal>=22?1:0);
                string file="expression_"+resource+".png";if(files.TryGetValue(file,out var bytes)) AddAliases("WeChat",names[i],new(()=>bytes,".png"));
            }
        }
        if(!files.TryGetValue("newemoji-config.xml",out var xml))return;
        using var reader=XmlReader.Create(new MemoryStream(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1024*1024});
        foreach(var item in XDocument.Load(reader).Descendants("emoji")) {
            string? file=item.Element("fileName")?.Value;if(file==null || !files.TryGetValue(file,out var bytes))continue;
            foreach(var alias in item.Elements().Where(e=>e.Name.LocalName is "key" or "cn-value" or "qq-value" or "en-value" or "tw-value" or "th-value"))
                AddAliases("WeChat",alias.Value,new(()=>bytes,".png"));
        }
    }
    private static byte[] ReadImage(string path){PathPolicy.AssertNoLinks(path);if(new FileInfo(path).Length>4*1024*1024)throw new InvalidDataException("Emoji exceeds limit.");return File.ReadAllBytes(path);}
    public string? Use(string key) {
        if(exported.TryGetValue(key,out string? existing))return existing;
        if(!resources.TryGetValue(key,out var resource))return null;
        try {
            byte[] bytes=resource.Read();if(!bytes.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10}))return null;
            string folder=Path.Combine(destination,"assets");Directory.CreateDirectory(folder);PathPolicy.AssertNoLinks(folder);
            string name=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()+resource.Extension,path=Path.Combine(folder,name);
            if(!File.Exists(path)){using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write);output.Write(bytes);CreatedFiles++;}
            else {PathPolicy.AssertNoLinks(path);if(!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))throw new InvalidDataException("Emoji asset changed.");}
            return exported[key]="assets/"+name;
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return null;}
    }
    public void UseParts(string platform,IEnumerable<MessagePart> parts) {
        foreach(var part in parts){
            if(part.Inline || part.EmojiId!=null){Use(platform+":"+(part.EmojiId??part.Title.TrimStart('#')));if(part.Title!="")Use(platform+":"+part.Title);}
            if(platform=="WeChat" && (part.Text.Contains('[')||part.Text.Contains('/'))) {
                if(!tokensReady){var aliases=resources.Keys.Where(k=>k.StartsWith("WeChat:[",StringComparison.Ordinal)||k.StartsWith("WeChat:/",StringComparison.Ordinal)).Select(k=>k[7..]).OrderByDescending(k=>k.Length).Select(Regex.Escape).ToArray();wechatTokens=aliases.Length==0?null:new Regex(string.Join('|',aliases),RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));tokensReady=true;}
                if(wechatTokens!=null)foreach(Match match in wechatTokens.Matches(part.Text))Use("WeChat:"+match.Value);
            }
            if(part.Forwarded.Length>0)UseParts(platform,part.Forwarded.SelectMany(f=>f.Parts));
        }
    }
    internal MessagePart[] NormalizeParts(string platform,MessagePart[] parts) {
        if(!parts.Any(p=>p.Forwarded.Length>0||p.Kind=="sticker"&&(p.MediaKeys?.Length??0)==0&&resources.ContainsKey(platform+":"+p.Title)))return parts;
        return parts.Select(p=> {
        if(p.Kind=="sticker" && (p.MediaKeys?.Length??0)==0 && resources.ContainsKey(platform+":"+p.Title))p=p with{Inline=true};
        return p.Forwarded.Length==0?p:p with{Forwarded=p.Forwarded.Select(f=>f with{Parts=NormalizeParts(platform,f.Parts)}).ToArray()};
        }).ToArray();
    }
    public void ExportAll(){foreach(string key in resources.Keys)Use(key);}
    internal void Restore(Dictionary<string,string>? previous){if(previous!=null)foreach(var pair in previous)exported[pair.Key]=pair.Value;}
}
