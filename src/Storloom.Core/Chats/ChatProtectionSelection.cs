using System.Text.Json.Nodes;

namespace Storloom.Core.Chats;

public sealed record ChatProtectionSelection(string[]? Conversations=null,string[]? Kinds=null,
    string Content="",string Sender="",string[]? MessageIds=null,bool Strict=false);

internal static class ChatProtectionPlanner {
    internal sealed record Selection(HashSet<string> Pages,HashSet<string> Media,Dictionary<string,string> PublicPages);
    internal static Selection Build(string root,ChatProtectionSelection scope,IProgress<ScanProgress>? progress,CancellationToken cancellation){
        string indexPath=Path.Combine(root,"archive-index.js");PathPolicy.AssertNoLinks(indexPath);
        var index=ReadObject(indexPath,"window.archiveIndex=");
        string stage=Path.Combine(root,".vault");
        var selectedPages=new HashSet<string>(StringComparer.Ordinal);var media=new HashSet<string>(StringComparer.Ordinal);
        var publicPages=new Dictionary<string,string>(StringComparer.Ordinal);var affected=new HashSet<string>(StringComparer.Ordinal);
        int matched=0,scanned=0;
        var pages=Directory.EnumerateFiles(Path.Combine(root,"conversations"),"*.js").Where(p=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p),"^[a-f0-9]{24}-[0-9]+\\.js$"));
        foreach(string file in pages){
            OperationPause.Check(cancellation);var (head,items,tail)=ReadPage(file);string id=Path.GetFileName(file)[..24];
            if(scope.Conversations is {Length:>0}&&!scope.Conversations.Contains(id,StringComparer.Ordinal))continue;
            var selected=items.Where(n=>n!=null&&Matches(n!,scope)).ToArray();scanned++;
            if(selected.Length>0){affected.Add(id);matched+=selected.Length;selectedPages.Add(Relative(root,file));
                foreach(var node in scope.Strict?selected:items.Where(n=>n!=null).ToArray())CollectMedia(node!,index,media);
            }
            if(scanned%20==0)progress?.Report(new("chat-encryption-select",scanned,0,Detail:matched.ToString()));
        }
        if(matched==0)throw new InvalidOperationException("No messages match the protection selection.");
        // Originals, JSONL and all other metadata are protected. Public pages never retain raw
        // payloads or quoted previews from selected conversations, which could repeat selected text.
        foreach(string file in Directory.EnumerateFiles(Path.Combine(root,"conversations"),"*.js")){
            OperationPause.Check(cancellation);string name=Path.GetFileName(file);
            if(name.Length<24||!affected.Contains(name[..24])||!System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-f0-9]{24}-[0-9]+\\.js$"))continue;
            var (head,items,tail)=ReadPage(file);bool hasQuote=items.Any(n=>n?["Parts"]?.AsArray().Any(p=>p?["Kind"]?.GetValue<string>()=="reply")==true);
            string relative=Relative(root,file);
            if(!scope.Strict){if(hasQuote){selectedPages.Add(relative);foreach(var node in items)if(node!=null)CollectMedia(node,index,media);}continue;}
            var visible=new JsonArray();foreach(var node in items){if(node==null||Matches(node,scope))continue;
                var safe=node.DeepClone();safe["RawBase64"]=null;safe["ExtraRawBase64"]=null;safe["SourceText"]=null;safe["ReactionsRawBase64"]=null;
                SanitizeQuotes(safe["Parts"]);safe["Text"]=string.Concat(safe["Parts"]?.AsArray().Select(p=>p?["Text"]?.GetValue<string>()??"")??[]);safe["DisplayText"]=null;
                visible.Add(safe);
            }
            // Stage every page in the affected conversation so its raw payload cannot leak quotes.
            selectedPages.Add(relative);string publicFile=Guid.NewGuid().ToString("N")+".public";
            using(var output=new FileStream(Path.Combine(stage,publicFile),FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                using var writer=new StreamWriter(output,leaveOpen:true);writer.Write(head);writer.Write(visible.ToJsonString());writer.Write(tail);writer.Flush();output.Flush(true);
            }
            publicPages.Add(relative,publicFile);
        }
        progress?.Report(new("chat-encryption-select",scanned,0,Detail:matched.ToString()));
        return new(selectedPages,media,publicPages);
    }
    private static void SanitizeQuotes(JsonNode? parts){if(parts is not JsonArray list)return;foreach(var part in list){if(part is not JsonObject obj)continue;
        if(obj["Kind"]?.GetValue<string>()=="reply"){obj["ReferenceText"]="[Protected quote]";obj["ReferenceSender"]=null;obj["ReferenceTimestamp"]=null;obj["Title"]="";obj["Detail"]="";}
        if(obj["Forwarded"] is JsonArray forwards)foreach(var entry in forwards){if(entry is JsonObject item){item["RawBase64"]=null;item["SourceText"]=null;SanitizeQuotes(item["Parts"]);}}
    }}
    private static bool Matches(JsonNode node,ChatProtectionSelection scope){
        if(scope.Kinds is {Length:>0}&&!(node["Parts"]?.AsArray().Any(p=>scope.Kinds.Contains(p?["Kind"]?.GetValue<string>()??"",StringComparer.Ordinal))??false))return false;
        if(scope.MessageIds is {Length:>0}&&!new[]{"Id","ServerId","Sequence"}.Any(k=>scope.MessageIds.Contains(node[k]?.GetValue<string>()??"",StringComparer.Ordinal)))return false;
        if(scope.Sender.Length>0&&!new[]{"Sender","SenderName"}.Any(k=>(node[k]?.GetValue<string>()??"").Contains(scope.Sender,StringComparison.OrdinalIgnoreCase)))return false;
        if(scope.Content.Length>0&&!new[]{"Text","DisplayText"}.Select(k=>node[k]?.GetValue<string>()??"").Concat(node["Parts"]?.AsArray().SelectMany(p=>new[]{"Text","Title","Detail","ReferenceText"}.Select(k=>p?[k]?.GetValue<string>()??""))??[]).Any(t=>t.Contains(scope.Content,StringComparison.OrdinalIgnoreCase)))return false;
        return true;
    }
    private static void CollectMedia(JsonNode node,JsonObject index,HashSet<string> paths){
        if(node["Media"] is JsonArray entries)foreach(var item in entries)Add(item);
        if(index["resourceMedia"] is JsonObject resources){Add(resources["message:"+(node["Id"]?.GetValue<string>()??"")]);
            foreach(var key in node["LocalMediaKeys"]?.AsArray()??[])if(key!=null)Add(resources[key.GetValue<string>()]);
            Parts(node["Parts"]);
            void Parts(JsonNode? parts){if(parts is not JsonArray list)return;foreach(var part in list){foreach(var key in part?["MediaKeys"]?.AsArray()??[])if(key!=null)Add(resources[key.GetValue<string>()]);foreach(var entry in part?["Forwarded"]?.AsArray()??[])Parts(entry?["Parts"]);}}
        }
        void Add(JsonNode? item){if(item?["Path"]?.GetValue<string>() is string path&&path.StartsWith("assets/",StringComparison.Ordinal))paths.Add(path);}
    }
    private static string Relative(string root,string path)=>Path.GetRelativePath(root,path).Replace('\\','/');
    private static JsonObject ReadObject(string file,string prefix){string text=ReadText(file);if(!text.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("Unsupported reader index.");return JsonNode.Parse(text[prefix.Length..].TrimEnd().TrimEnd(';'))?.AsObject()??throw new InvalidDataException("Invalid reader index.");}
    private static (string Head,JsonArray Items,string Tail) ReadPage(string file){string text=ReadText(file);int start=text.IndexOf('['),end=text.LastIndexOf(']');if(!text.StartsWith("window.receivePage(",StringComparison.Ordinal)||start<0||end<start)throw new InvalidDataException("Unsupported message page.");return(text[..start],JsonNode.Parse(text[start..(end+1)])!.AsArray(),text[(end+1)..]);}
    private static string ReadText(string file){PathPolicy.AssertNoLinks(file);if(new FileInfo(file).Length>64*1024*1024)throw new InvalidDataException("Reader page is too large for selective protection.");return File.ReadAllText(file);}
}
