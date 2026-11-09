using System.Security.Cryptography;

namespace Storloom.Core.Chats;

internal static class ChatAvatars {
    internal static Dictionary<string,string> Read(SqliteReader reader,string destination,CancellationToken cancellation=default) {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        var tables=reader.Query("SELECT name FROM sqlite_master WHERE type='table'",cancellation).Select(r=>r[0]?.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var (table,id,blob) in new[]{("head_image","username","image_buffer"),("ContactHeadImg1","usrName","smallHeadBuf")}) {
            if(!tables.Contains(table))continue;
            var columns=reader.Query("PRAGMA table_info("+SqliteReader.Identifier(table)+")",cancellation).Select(r=>r[1]?.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(!columns.Contains(id)||!columns.Contains(blob))continue;
            foreach(var row in reader.Query("SELECT "+SqliteReader.Identifier(id)+","+SqliteReader.Identifier(blob)+" FROM "+SqliteReader.Identifier(table),cancellation)) {
                if(row[0] is not string identity||identity.Length==0||row[1] is not byte[] bytes||bytes.Length>8*1024*1024)continue;
                string? extension=WeChatImages.Format(bytes);if(extension is not (".png" or ".jpg" or ".gif" or ".webp" or ".bmp"))continue;
                string name=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()+extension;
                string assets=Path.Combine(destination,"assets");Directory.CreateDirectory(assets);PathPolicy.AssertNoLinks(assets);string target=Path.Combine(assets,name);if(File.Exists(target))PathPolicy.AssertNoLinks(target);
                if(!File.Exists(target)){using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write);output.Write(bytes);}
                else {using var input=File.OpenRead(target);if(!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(bytes)))throw new InvalidDataException("Avatar asset conflicts with existing content.");}
                result[identity]="assets/"+name;
            }
        }
        return result;
    }
}
