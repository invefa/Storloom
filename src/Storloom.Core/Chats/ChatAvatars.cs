using System.Security.Cryptography;

namespace Storloom.Core.Chats;

internal static class ChatAvatars {
    internal static Dictionary<string,string> DownloadQQUsers(IEnumerable<string> users,string destination,IProgress<ScanProgress>? progress,CancellationToken cancellation,int workers=4){
        string[] ids=users.Where(id=>System.Text.RegularExpressions.Regex.IsMatch(id,"^[1-9][0-9]{4,14}$")).Distinct(StringComparer.Ordinal).ToArray();var result=new System.Collections.Concurrent.ConcurrentDictionary<string,string>(StringComparer.Ordinal);int done=0;
        using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(8)};
        Parallel.ForEach(ids,new ParallelOptions{MaxDegreeOfParallelism=Math.Clamp(workers,1,8),CancellationToken=cancellation},id=>{
            try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(8));using var response=client.GetAsync("https://q1.qlogo.cn/g?b=qq&nk="+id+"&s=100",HttpCompletionOption.ResponseHeadersRead,timeout.Token).GetAwaiter().GetResult();response.EnsureSuccessStatusCode();using var input=response.Content.ReadAsStream(timeout.Token);using var bytes=new MemoryStream();byte[] buffer=new byte[16384];int count;while((count=input.ReadAsync(buffer.AsMemory(),timeout.Token).AsTask().GetAwaiter().GetResult())>0){OperationPause.Check(cancellation);if(bytes.Length+count>8*1024*1024)throw new InvalidDataException("Avatar exceeds size limit.");bytes.Write(buffer,0,count);}byte[] image=bytes.ToArray();string? extension=WeChatImages.Format(image);if(extension is not (".png" or ".jpg" or ".gif" or ".webp" or ".bmp"))return;string assets=Path.Combine(destination,"assets");Directory.CreateDirectory(assets);PathPolicy.AssertNoLinks(assets);string name=Convert.ToHexStringLower(SHA256.HashData(image))+extension;lock(result){string path=Path.Combine(assets,name);if(File.Exists(path)){PathPolicy.AssertNoLinks(path);using var existing=File.OpenRead(path);if(!SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(image)))throw new IOException("Avatar content mismatch.");}else{using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write);output.Write(image);}result[id]="assets/"+name;}}
            catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException){OperationPause.Check(cancellation);}
            finally{progress?.Report(new("chat-avatars",Interlocked.Increment(ref done),0,TotalFiles:ids.Length));}
        });return new(result,StringComparer.Ordinal);
    }
    internal static Dictionary<string,string> DownloadQQGroups(IEnumerable<string> groups,string destination,IProgress<ScanProgress>? progress,CancellationToken cancellation,out int failed,int workers=4) {
        string[] ids=groups.Where(g=>System.Text.RegularExpressions.Regex.IsMatch(g,"^group:[1-9][0-9]{4,14}$")).Distinct(StringComparer.Ordinal).ToArray();
        var result=new System.Collections.Concurrent.ConcurrentDictionary<string,string>(StringComparer.Ordinal);int done=0;
        using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
        Parallel.ForEach(ids,new ParallelOptions{MaxDegreeOfParallelism=Math.Clamp(workers,1,32),CancellationToken=cancellation},id=>{
            try {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(10));
                string number=id[6..];using var request=new HttpRequestMessage(HttpMethod.Get,$"https://p.qlogo.cn/gh/{number}/{number}/640");
                using var response=client.Send(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);response.EnsureSuccessStatusCode();
                using var input=response.Content.ReadAsStream(timeout.Token);using var bytes=new MemoryStream();byte[] buffer=new byte[32*1024];int count;
                while((count=input.ReadAsync(buffer.AsMemory(),timeout.Token).AsTask().GetAwaiter().GetResult())>0){if(bytes.Length+count>8*1024*1024)throw new InvalidDataException("Avatar exceeds size limit.");bytes.Write(buffer,0,count);}
                byte[] image=bytes.ToArray();string? extension=WeChatImages.Format(image);if(extension is not (".png" or ".jpg" or ".gif" or ".webp" or ".bmp"))throw new InvalidDataException("Unsupported group avatar.");
                string assets=Path.Combine(destination,"assets");Directory.CreateDirectory(assets);PathPolicy.AssertNoLinks(assets);
                string filename=Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant()+extension,target=Path.Combine(assets,filename);
                lock(result){
                    if(File.Exists(target)){PathPolicy.AssertNoLinks(target);if(!File.ReadAllBytes(target).AsSpan().SequenceEqual(image))throw new InvalidDataException("Avatar asset changed.");}
                    else {using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write);output.Write(image);}
                    result[id]="assets/"+filename;
                }
            } catch(Exception ex)when(ex is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException){OperationPause.Check(cancellation);}
            progress?.Report(new("chat-avatars",Interlocked.Increment(ref done),0,TotalFiles:ids.Length));
        });
        failed=ids.Length-result.Count;return new(result,StringComparer.Ordinal);
    }
    internal static Dictionary<string,string> Read(SqliteReader reader,string destination,CancellationToken cancellation=default) {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        var tables=reader.Query("SELECT name FROM sqlite_master WHERE type='table'",cancellation).Select(r=>r[0]?.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var (table,id,blob) in new[]{("head_image","username","image_buffer"),("ContactHeadImg1","usrName","smallHeadBuf")}) {
            if(!tables.Contains(table))continue;
            var columns=reader.Query("PRAGMA table_info("+SqliteReader.Identifier(table)+")",cancellation).Select(r=>r[1]?.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? imageColumn=new[]{blob,"smallHeadBuf","bigHeadBuf","image_buffer","small_head_buf"}.FirstOrDefault(columns.Contains);if(!columns.Contains(id)||imageColumn==null)continue;
            foreach(var row in reader.Query("SELECT "+SqliteReader.Identifier(id)+","+SqliteReader.Identifier(imageColumn)+" FROM "+SqliteReader.Identifier(table),cancellation)) {
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
