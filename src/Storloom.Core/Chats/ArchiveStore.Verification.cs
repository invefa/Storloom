using System.Security.Cryptography;
using System.Text.Json;

namespace Storloom.Core.Chats;

internal sealed record ArchiveVerificationResult(string? Head,int Commits,long MessageRevisions,long ResourceChunks,long Bytes);
internal sealed partial class ArchiveStore {
    // Explicit, read-only full verification. Normal startup and updates do not run this scan.
    internal ArchiveVerificationResult Verify(IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        using var lease=Lease(readOnly:true);string? head=log.Head;var entries=log.CommitsSince(null,head).ToArray();
        long total=entries.Sum(e=>e.Entry.Bytes),bytes=0,messages=0,objects=0;int done=0;
        progress?.Report(new("archive-verify",0,0,total,entries.Length));
        foreach(var entry in entries){
            OperationPause.Check(cancellation);var stored=log.Read(entry.Id);byte[] plain=Decode(stored.Payload);ArchiveDelta? batch=null;
            try{
                using var document=JsonDocument.Parse(plain);
                if(document.RootElement.ValueKind==JsonValueKind.Array){var rows=JsonSerializer.Deserialize<ArchiveQueryCache.Message[]>(plain)??throw new InvalidDataException("Invalid message pack.");query.Validate(new(1,rows,[],[],[]));messages+=rows.Length;}
                else{
                    batch=JsonSerializer.Deserialize<ArchiveDelta>(plain)??throw new InvalidDataException("Invalid archive pack.");query.Validate(batch);messages+=batch.Messages.Length;
                    foreach(var item in batch.Objects){OperationPause.Check(cancellation);if(ObjectId(item.Bytes)!=item.Id)throw new InvalidDataException("Resource identity verification failed.");objects++;}
                }
            }finally{CryptographicOperations.ZeroMemory(plain);if(batch?.Objects!=null)foreach(var item in batch.Objects)if(item?.Bytes!=null)CryptographicOperations.ZeroMemory(item.Bytes);}
            bytes+=entry.Entry.Bytes;done++;progress?.Report(new("archive-verify",done,bytes,total,entries.Length));
        }
        return new(head,done,messages,objects,bytes);
    }
}
