using System.Text.Json;
using System.Text.Json.Nodes;
using Storloom.Core;
namespace Storloom.App;
internal static class ReaderPreferences {
    internal static JsonObject Sanitize(JsonElement value){var result=new JsonObject();if(value.ValueKind!=JsonValueKind.Object)throw new JsonException("Invalid preferences.");
        if(value.TryGetProperty("palette",out var palette)&&palette.ValueKind==JsonValueKind.String&&palette.GetString() is "default" or "qq" or "wechat")result["palette"]=palette.GetString();
        if(value.TryGetProperty("dark",out var dark)&&dark.ValueKind is JsonValueKind.True or JsonValueKind.False)result["dark"]=dark.GetBoolean();
        if(value.TryGetProperty("updatedAt",out var time)&&time.ValueKind==JsonValueKind.Number&&time.TryGetInt64(out long stamp)&&stamp>0&&stamp<=DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds())result["updatedAt"]=stamp;
        var view=new JsonObject();if(value.TryGetProperty("view",out var fields)&&fields.ValueKind==JsonValueKind.Object){foreach(var pair in fields.EnumerateObject()){
            if(pair.Name is "autoBottom" or "autoLast" or "sidebarCollapsed"){if(pair.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)view[pair.Name]=pair.Value.GetBoolean();}
            else if(pair.Name=="minimap"){if(pair.Value.ValueKind==JsonValueKind.String&&pair.Value.GetString() is "shown" or "collapsed" or "closed")view[pair.Name]=pair.Value.GetString();}
            else if(pair.Name is "pageSize" or "sidebarWidth" or "previewWidth" or "previewHeight"){if(pair.Value.ValueKind==JsonValueKind.Number&&pair.Value.TryGetDouble(out double number)&&double.IsFinite(number)&&number>=10&&number<=10000)view[pair.Name]=number;}
        }}result["view"]=view;return result;
    }
    internal static JsonObject Load(string file){try{if(!File.Exists(file)||new FileInfo(file).Length>8192)return new();using var json=JsonDocument.Parse(File.ReadAllText(file));return Sanitize(json.RootElement);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){return new();}}
    internal static void Save(string file,JsonElement value){var filtered=Sanitize(value);Directory.CreateDirectory(Path.GetDirectoryName(file)!);JsonFiles.Write(file,filtered);}
}
