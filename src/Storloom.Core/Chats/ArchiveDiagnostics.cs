using System.Security.Cryptography;
using System.Text.Json;
namespace Storloom.Core.Chats;

public sealed record ArchiveConversationOverview(string Conversation,long Messages,long? FirstTimestamp,long? LastTimestamp);
public sealed record ArchiveOverview(string Directory,string Id,string? Head,string Kind,ChatPlatform? Platform,string? Account,
    long? Messages,long? Resources,long? ResourceBytes,long? MissingMedia,long? UnsupportedMessages,bool IndexOnly,ArchiveConversationOverview[] Conversations) {
    public long? FirstTimestamp=>Conversations.Where(c=>c.FirstTimestamp.HasValue).Select(c=>c.FirstTimestamp).Min();
    public long? LastTimestamp=>Conversations.Where(c=>c.LastTimestamp.HasValue).Select(c=>c.LastTimestamp).Max();
}
public sealed record ArchiveConversationDifference(string Conversation,long LeftMessages,long RightMessages,long? LeftFirst,long? LeftLast,long? RightFirst,long? RightLast);
public sealed record ArchiveComparison(ArchiveOverview Left,ArchiveOverview Right,bool DifferentAccount,ArchiveConversationDifference[] Conversations);
public sealed record ArchiveIntegrityReport(int Version,string Directory,string? ArchiveId,string? Snapshot,string Scope,string Outcome,
    DateTimeOffset StartedAt,DateTimeOffset FinishedAt,long CheckedFiles,long CheckedBytes,long? MissingMedia,string? Detail=null);

internal sealed partial class ArchiveStore {
    internal ArchiveOverview Overview(){
        using var lease=Lease(readOnly:true);var conversations=query.OverviewConversations();var files=FileInventory().Where(f=>f.Path.StartsWith("assets/",StringComparison.Ordinal)).ToArray();
        var reference=ArchiveMetadata.Inspect(root);long? missing=null,unsupported=null;
        if(FileInventory().Any(f=>f.Path=="export-report.json")){using var input=OpenFile("export-report.json");var report=JsonSerializer.Deserialize<ChatExportResult>(input,JsonFiles.Options)??throw new InvalidDataException("Invalid export report.");missing=report.UnresolvedMedia;unsupported=report.UnsupportedMessages;}
        var native=reference.Metadata.Kind=="native"?NativeHistory().FirstOrDefault():null;
        return new(reference.Directory,Id,Head,reference.Metadata.Kind,reference.Metadata.Platform,reference.Metadata.Account,reference.Metadata.Kind=="native"?null:conversations.Sum(c=>c.Messages),native?.Files??files.LongLength,native?.Bytes??files.Sum(f=>f.Length),missing,unsupported,true,conversations);
    }
}

public static class ArchiveDiagnostics {
    private sealed class Forward(Action<ScanProgress> action):IProgress<ScanProgress>{public void Report(ScanProgress value)=>action(value);}
    public static string OriginalSnapshot(string manifest){PathPolicy.AssertNoLinks(manifest);using var file=File.OpenRead(manifest);return Convert.ToHexStringLower(SHA256.HashData(file));}
    public static ArchiveIntegrityReport VerifyOriginal(string manifest,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        var start=DateTimeOffset.UtcNow;string directory=manifest;string? snapshot=null;long files=0,bytes=0;
        ArchiveIntegrityReport Result(string outcome,string? detail=null)=>new(1,directory,null,snapshot,"original-receipts",outcome,start,DateTimeOffset.UtcNow,files,bytes,null,detail);
        try{cancellation.ThrowIfCancellationRequested();snapshot=OriginalSnapshot(manifest);var journal=JsonFiles.Read<TransferJournal>(manifest);directory=journal.Plan.Destination;
            ChatExporter.VerifyBackup(manifest,new Forward(p=>{files=p.Files;bytes=p.Bytes;progress?.Report(p);}),cancellation);files=journal.Receipts.Count;bytes=journal.Receipts.Sum(r=>r.Bytes);return Result("verified");
        }catch(OperationCanceledException){return Result("cancelled");}catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException){return Result("failed","read-or-format-failed");}
    }
    public static ArchiveOverview Overview(string path,ReadOnlySpan<char> secret=default,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        var reference=ArchiveMetadata.Inspect(path);if(ArchiveStore.Exists(reference.Directory)){using var store=ArchiveStore.Open(reference.Directory,secret,progress,cancellation);return store.Overview();}
        using var vault=ChatVault.Exists(reference.Directory)?ChatVault.Open(reference.Directory,secret):null;
        Stream Open(string name)=>vault==null?File.OpenRead(TransferService.Child(reference.Directory,name)):vault.OpenRead(name);
        using var indexStream=Open("archive-index.js");if(indexStream.Length>16*1024*1024)throw new InvalidDataException("Reader index is too large.");using var reader=new StreamReader(indexStream);string text=reader.ReadToEnd();const string prefix="window.archiveIndex=";
        if(!text.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("Invalid reader index.");using var json=JsonDocument.Parse(text[prefix.Length..].Trim().TrimEnd(';'));
        var conversations=json.RootElement.GetProperty("conversations").EnumerateArray().Select(c=>{string id=c.GetProperty("id").GetString()!;string key=json.RootElement.TryGetProperty("conversationKeys",out var mapping)&&mapping.TryGetProperty(id,out var native)?(reference.Metadata.Platform?.ToString()??"unknown")+"/"+native.GetString():id;return new ArchiveConversationOverview(key,c.GetProperty("count").GetInt64(),null,null);}).ToArray();
        ChatExportResult? report=null;try{using var input=Open("export-report.json");report=JsonSerializer.Deserialize<ChatExportResult>(input,JsonFiles.Options);}catch(FileNotFoundException){}
        cancellation.ThrowIfCancellationRequested();return new(reference.Directory,reference.Metadata.Id,null,"reading",reference.Metadata.Platform,reference.Metadata.Account,conversations.Sum(c=>c.Messages),report?.MediaFiles,null,report?.UnresolvedMedia,report?.UnsupportedMessages,true,conversations);
    }
    public static ArchiveComparison Compare(ArchiveOverview left,ArchiveOverview right){
        var a=left.Conversations.ToDictionary(c=>c.Conversation,StringComparer.Ordinal);var b=right.Conversations.ToDictionary(c=>c.Conversation,StringComparer.Ordinal);
        var rows=a.Keys.Union(b.Keys,StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(key=>{var l=a.GetValueOrDefault(key);var r=b.GetValueOrDefault(key);return new ArchiveConversationDifference(key,l?.Messages??0,r?.Messages??0,l?.FirstTimestamp,l?.LastTimestamp,r?.FirstTimestamp,r?.LastTimestamp);}).ToArray();
        bool different=left.Kind!=right.Kind||left.Platform!=null&&right.Platform!=null&&left.Platform!=right.Platform||left.Account!=null&&right.Account!=null&&left.Account!=right.Account;
        return new(left,right,different,rows);
    }
    public static string Snapshot(string path){var reference=ArchiveMetadata.Inspect(path);string head=Path.Combine(reference.DataDirectory,"HEAD");if(File.Exists(head)){PathPolicy.AssertNoLinks(head);if(new FileInfo(head).Length>4096)throw new InvalidDataException("Invalid archive head.");return File.ReadAllText(head).Trim();}return JsonFiles.Hash(new{reference.Metadata.Id,Marker=File.GetLastWriteTimeUtc(Path.Combine(reference.Directory,ChatVault.Marker))});}
    public static ArchiveIntegrityReport Verify(string path,ReadOnlySpan<char> secret=default,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        DateTimeOffset start=DateTimeOffset.UtcNow;ArchiveReference? reference=null;string? snapshot=null;long files=0,bytes=0;bool opened=false;string scope="unreadable";long? missing=null;
        ArchiveIntegrityReport Result(string outcome,string? detail=null)=>new(1,reference?.Directory??Path.GetFullPath(path),reference?.Metadata.Id,snapshot,scope,outcome,start,DateTimeOffset.UtcNow,files,bytes,missing,detail);
        try{
            cancellation.ThrowIfCancellationRequested();reference=ArchiveMetadata.Inspect(path);snapshot=Snapshot(reference.Directory);
            if(ArchiveStore.Exists(reference.Directory)){
                scope="committed-records";using var store=ArchiveStore.Open(reference.Directory,secret,progress,cancellation);opened=true;
                var result=store.Verify(new Forward(p=>{if(p.RuleId=="archive-verify"){files=p.Files;bytes=p.Bytes;}progress?.Report(p);}),cancellation);snapshot=result.Head;files=result.Commits;bytes=result.Bytes;var overview=store.Overview();missing=overview.MissingMedia;
                bool incomplete=store.FileInventory().Any(f=>!f.Complete);return Result(incomplete||missing>0||overview.UnsupportedMessages>0?"partial":"verified",incomplete?"incomplete-resources":missing>0?"missing-media":overview.UnsupportedMessages>0?"unsupported-messages":null);
            }
            if(ChatVault.Exists(reference.Directory)){
                scope="authenticated-folder-files";using var vault=ChatVault.Open(reference.Directory,secret);opened=true;var result=vault.VerifyFiles(progress,cancellation);files=result.Files;bytes=result.Bytes;
                long? unsupported=null;if(vault.Files.Contains("export-report.json")){using var input=vault.OpenRead("export-report.json");var report=JsonSerializer.Deserialize<ChatExportResult>(input,JsonFiles.Options);missing=report?.UnresolvedMedia;unsupported=report?.UnsupportedMessages;}
                return Result(result.Complete&&missing is not >0&&unsupported is not >0?"verified":"partial",!result.Complete?"unprotected-or-unplanned-files":missing>0?"missing-media":unsupported>0?"unsupported-messages":null);
            }
            scope="folder-readability";opened=true;var pending=new Stack<string>();pending.Push(reference.Directory);
            while(pending.TryPop(out string? directory))foreach(string file in Directory.EnumerateFileSystemEntries(directory)){
                cancellation.ThrowIfCancellationRequested();PathPolicy.AssertNoLinks(file);if(Directory.Exists(file)){pending.Push(file);continue;}using var input=File.OpenRead(file);_=TransferService.Hash(input,cancellation,count=>bytes+=count);files++;progress?.Report(new("archive-verify",(int)Math.Min(int.MaxValue,files),bytes,Detail:Path.GetRelativePath(reference.Directory,file)));
            }
            return Result("partial","no-authenticated-baseline");
        }catch(OperationCanceledException){return Result("cancelled");}
        catch(CryptographicException){return Result(opened?"failed":"locked",opened?"authentication-failed":"unlock-failed");}
        catch(Exception ex)when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or ArgumentException){return Result("failed","read-or-format-failed");}
    }
}
