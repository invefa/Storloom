using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Storloom.Core.Chats;

internal sealed partial class ArchiveStore {
    private const string MediaRepairFile="media-repairs.json";
    private static bool RepairAsset(string path)=>Regex.IsMatch(path,"^assets/[a-f0-9]{64}\\.gif$",RegexOptions.CultureInvariant);
    private Dictionary<string,string> MediaRepairs(Func<string,Stream>? open=null){
        try{using var input=(open??OpenFile)(MediaRepairFile);if(input.Length>1024*1024)throw new InvalidDataException("Media repair index exceeds limit.");var result=JsonSerializer.Deserialize<Dictionary<string,string>>(input)??[];
            if(result.Count>4096||result.Any(p=>!RepairAsset(p.Key)||!RepairAsset(p.Value)))throw new InvalidDataException("Invalid media repair index.");return result;
        }catch(FileNotFoundException){return [];}
    }
    private byte[] WithMediaRepairs(byte[] bytes,Func<string,Stream>? open=null){var repairs=MediaRepairs(open);if(repairs.Count==0)return bytes;
        string raw=Encoding.UTF8.GetString(bytes);var metadata=JsonNode.Parse(raw["window.archiveIndex=".Length..].Trim().TrimEnd(';'))!.AsObject();metadata["mediaRepairs"]=JsonSerializer.SerializeToNode(repairs);
        return Encoding.UTF8.GetBytes("window.archiveIndex="+metadata.ToJsonString()+";");
    }
    // Publish replacements in a new snapshot. The original objects and their
    // content-addressed names remain intact; message records need no rewrite.
    internal int RepairAnimationMedia(IReadOnlyDictionary<string,byte[]> replacements,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        if(replacements.Count==0)return 0;
        if(replacements.Count>4096||replacements.Any(p=>!RepairAsset(p.Key)||p.Value.Length>32*1024*1024||WeChatImages.Format(p.Value)!=".gif"))throw new InvalidDataException("Invalid animation replacement.");
        var aliases=MediaRepairs();foreach(var item in replacements){using var original=OpenFile(item.Key);}
        using var writer=BeginWrite(cancellation,"animation-timing-repair",progress);int count=0;
        foreach(var item in replacements){OperationPause.Check(cancellation);string path="assets/"+Convert.ToHexStringLower(SHA256.HashData(item.Value))+".gif";writer.Bytes(path,item.Value);aliases[item.Key]=path;count++;progress?.Report(new("chat-media",count,0,TotalFiles:replacements.Count));}
        if(aliases.Count>4096)throw new InvalidDataException("Media repair index exceeds limit.");
        writer.Bytes(MediaRepairFile,JsonSerializer.SerializeToUtf8Bytes(aliases));writer.Complete(ImportCursor??new(JsonFiles.Hash(new{Id,Head}).ToLowerInvariant(),"complete",0,0,0,0,0));return count;
    }
}
