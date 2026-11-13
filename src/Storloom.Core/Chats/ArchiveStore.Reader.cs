using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Storloom.Core.Localization;
namespace Storloom.Core.Chats;

internal sealed partial class ArchiveStore {
    private readonly object readGate=new();
    internal string DirectoryPath=>root;
    internal bool Protected=>keys!=null;
    internal void ChangePassword(ReadOnlySpan<char> password){using var lease=Lease();if(keys==null)throw new InvalidOperationException("This archive is not encrypted.");keys.ChangePassword(password);}
    internal static bool SnapshotReads(string directory)=>File.Exists(Path.Combine(directory,"projection.json"));
    internal static bool Exists(string directory)=>File.Exists(Path.Combine(directory,"archive.json"));
    internal static bool NeedsPassword(string directory){string marker=Path.Combine(directory,"archive.json");PathPolicy.AssertNoLinks(marker);if(new FileInfo(marker).Length>4096)throw new InvalidDataException("Invalid archive marker.");return JsonFiles.Read<Configuration>(marker).Protected;}
    private Dictionary<string,string>? readerConversations;
    internal byte[] ReaderIndex(){lock(readGate){
        JsonObject metadata;
        try{using var input=OpenFile("archive-index.js");using var text=new StreamReader(input);string raw=text.ReadToEnd();if(!raw.StartsWith("window.archiveIndex=",StringComparison.Ordinal))throw new InvalidDataException("Invalid reader metadata.");metadata=JsonNode.Parse(raw["window.archiveIndex=".Length..].Trim().TrimEnd(';'))!.AsObject();}
        catch(FileNotFoundException){metadata=new(){["language"]=L.Language,["labels"]=JsonSerializer.SerializeToNode(L.Keys.Where(k=>k.StartsWith("Reader",StringComparison.Ordinal)||k=="ChatArchive").ToDictionary(k=>k,k=>L.T(k)))};}
        readerConversations=new(StringComparer.Ordinal);var chats=new JsonArray();var native=metadata["conversationKeys"] as JsonObject??new();metadata["conversationKeys"]=native;
        foreach(var (key,count) in query.Conversations()){
            string conversation=key[(key.IndexOf('/')+1)..],id=JsonFiles.Hash(conversation)[..24].ToLowerInvariant();if(readerConversations.ContainsKey(id))id=JsonFiles.Hash(key)[..24].ToLowerInvariant();readerConversations[id]=key;
            var sample=query.Page(key,1).FirstOrDefault();string name=conversation;
            if(sample!=null){using var doc=JsonDocument.Parse(sample.Json);if(doc.RootElement.TryGetProperty("ConversationName",out var value)&&value.ValueKind==JsonValueKind.String)name=value.GetString()??name;}
            chats.Add(new JsonObject{["id"]=id,["name"]=name,["count"]=count,["pages"]=(count+299)/300});native[id]=conversation;
        }
        metadata["conversations"]=chats;metadata["preferenceScope"]=Id;metadata["nativeStore"]=true;metadata["sourceIdentity"]=ImportCursor?.SourceIdentity;
        return Encoding.UTF8.GetBytes("window.archiveIndex="+metadata.ToJsonString()+";");
    }}
    internal byte[] ReaderPage(string conversation,int page){lock(readGate){
        if(readerConversations==null)_=ReaderIndex();if(!readerConversations!.TryGetValue(conversation,out string? key))throw new FileNotFoundException("Unknown conversation.");
        var rows=query.PageAt(key,page);return Encoding.UTF8.GetBytes("window.receivePage("+JsonSerializer.Serialize(conversation)+","+page+",["+string.Join(',',rows.Select(r=>r.Json))+"]);");
    }}
    private sealed record SearchCursor(long Time,string Id);
    internal byte[] ReaderReference(string conversation,string reference){lock(readGate){
        if(readerConversations==null)_=ReaderIndex();if(reference.Length>1024||!readerConversations!.TryGetValue(conversation,out var key))throw new InvalidDataException("Invalid reference request.");
        var message=query.Reference(key,reference);if(message==null)return "null"u8.ToArray();using var raw=JsonDocument.Parse(message.Json);return JsonSerializer.SerializeToUtf8Bytes(new{page=query.Rank(message)/300,id=raw.RootElement.TryGetProperty("Id",out var id)?id.GetString():null});
    }}
    internal byte[] ReaderSearch(string text,string? cursor=null,bool reverse=false){lock(readGate){
        if(readerConversations==null)_=ReaderIndex();var ids=readerConversations!.ToDictionary(p=>p.Value,p=>p.Key,StringComparer.Ordinal);
        if(text.Length>4096||cursor?.Length>1024)throw new InvalidDataException("Invalid search request.");
        var after=cursor==null?null:JsonSerializer.Deserialize<SearchCursor>(Convert.FromBase64String(cursor));
        var found=query.Search(text,101,after?.Time,after?.Id,reverse);var selected=found.Take(100).ToArray();
        var rows=selected.Select(m=>new{message=JsonSerializer.Deserialize<JsonElement>(m.Json),conversation=ids[m.Conversation],page=query.Rank(m)/300}).ToArray();
        string? next=found.Count>100?Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new SearchCursor(selected[^1].Timestamp,selected[^1].Id))):null;
        return JsonSerializer.SerializeToUtf8Bytes(new{rows,next});
    }}
    internal static string SearchableText(ChatMessage message){var identity=message.Identity;string name=new[]{identity?.GroupCard,identity?.FriendRemark,identity?.Nickname,identity?.RecordedName,message.SenderName,message.Sender}.FirstOrDefault(n=>!string.IsNullOrEmpty(n))??"";
        var parts=message.Parts.Length>0?message.Parts:MessagePresentation.Parse(message);return string.Join(' ',new[]{message.DisplayText??message.Text,name}.Concat(parts.SelectMany(p=>new[]{p.Text,p.Title,p.Detail,p.ReferenceText})).Where(s=>!string.IsNullOrEmpty(s)));
    }
    internal IReadOnlyList<ArchiveFileEntry> FileInventory(){lock(readGate)return query.Files();}
    private IEnumerable<ArchiveQueryCache.Message> ReaderRows(string conversation){
        long time=long.MinValue;string id="";
        while(true){IReadOnlyList<ArchiveQueryCache.Message> rows;lock(readGate)rows=query.Page(conversation,1000,time,id);if(rows.Count==0)yield break;foreach(var row in rows)yield return row;time=rows[^1].Timestamp;id=rows[^1].Id;}
    }
    internal byte[] ReaderReferences(string conversation){
        lock(readGate){if(readerConversations==null)_=ReaderIndex();if(!readerConversations!.TryGetValue(conversation,out var key))throw new FileNotFoundException();using var output=new MemoryStream();WriteReferences(output,conversation,key);return output.ToArray();}
    }
    private void WriteReferences(Stream output,string conversation,string key,CancellationToken cancellation=default){
        using var writer=new StreamWriter(output,new UTF8Encoding(false),65536,true);writer.Write("window.receiveReferences("+JsonSerializer.Serialize(conversation)+",{");long position=0;bool first=true;
        foreach(var row in ReaderRows(key)){
            OperationPause.Check(cancellation);using var doc=JsonDocument.Parse(row.Json);var message=doc.RootElement;string? id=message.TryGetProperty("Id",out var value)?value.GetString():null;
            void Reference(string? reference){if(string.IsNullOrEmpty(reference)||reference is "0" or "seq:0")return;if(!first)writer.Write(',');first=false;writer.Write(JsonSerializer.Serialize(reference)+":"+JsonSerializer.Serialize(new{page=position/300,id}));}
            if(message.TryGetProperty("ServerId",out value))Reference(value.GetString());if(message.TryGetProperty("Sequence",out value)&&value.ValueKind!=JsonValueKind.Null)Reference("seq:"+value.GetString());position++;
        }writer.Write("});");
    }
}
