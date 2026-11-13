using System.Security.Cryptography;
using System.Text.Json;

namespace Storloom.Core.Chats;

internal sealed record ArchiveVerificationResult(string? Head,int Commits,long MessageRevisions,long ResourceChunks,long Bytes);
internal sealed partial class ArchiveStore {
    // Explicit, read-only full verification. Normal startup and updates do not run this scan.
    internal ArchiveVerificationResult Verify(IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        using var lease=Lease(readOnly:true);string? head=Head;var entries=log.CommitsSince(null,head).ToArray();
        long total=entries.Sum(e=>e.Entry.Bytes),bytes=0,messages=0,objects=0;int done=0;
        progress?.Report(new("archive-verify",0,0,total,entries.Length));
        foreach(var entry in entries){
            OperationPause.Check(cancellation);var stored=log.Read(entry.Id);ArchiveDelta? batch=null;
            try{batch=Decode(stored.Payload);query.Validate(batch);messages+=batch.Messages.Length;foreach(var item in batch.Objects){OperationPause.Check(cancellation);if(ObjectId(item.Bytes)!=item.Id)throw new InvalidDataException("Resource identity verification failed.");objects++;}}
            finally{if(batch?.Objects!=null)foreach(var item in batch.Objects)CryptographicOperations.ZeroMemory(item.Bytes);}
            bytes+=entry.Entry.Bytes;done++;progress?.Report(new("archive-verify",done,bytes,total,entries.Length));
        }
        return new(head,done,messages,objects,bytes);
    }
}
