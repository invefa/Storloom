using System.Security.Cryptography;
namespace Storloom.Core.Chats;

public sealed partial class ChatVault {
    internal (long Files,long Bytes,bool Complete) VerifyFiles(IProgress<ScanProgress>? progress,CancellationToken cancellation){
        ObjectDisposedException.ThrowIf(disposed,this);long bytes=0,files=0;bool complete=true;
        foreach(var entry in entries){
            OperationPause.Check(cancellation);using var input=OpenRead(entry.Path);string hash=Convert.ToHexString(TransferService.Hash(input,cancellation,count=>bytes+=count));
            if(committed.TryGetValue(entry.Id,out var expected)){if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash),Convert.FromHexString(expected)))throw new InvalidDataException("Protected file digest mismatch.");}else complete=false;
            files++;progress?.Report(new("archive-verify",(int)Math.Min(int.MaxValue,files),bytes,TotalFiles:entries.Length,Detail:entry.Path));
        }
        if(UnplannedFiles().Any())complete=false;return(files,bytes,complete);
    }
}
