using System.Buffers;
using System.Security.Cryptography;
namespace Storloom.Core.Chats;

/// <summary>Reconstructible staging copies. Verify before rename; never alter the source.</summary>
internal static class ChatWorkspaceCopy {
    internal static void File(Func<Stream> open, string target, string temporary, CancellationToken cancellation, Action<int> advanced) {
        PathPolicy.AssertNoLinks(System.IO.Path.GetDirectoryName(target)!);
        if(System.IO.File.Exists(target)) {
            PathPolicy.AssertNoLinks(target);
            if(NativeFile.ReadStamp(target).Links!=1)throw new IOException("Shared workspace file.");
            using var expected=open();using var existing=System.IO.File.OpenRead(target);
            if(expected.Length!=existing.Length||!TransferService.Hash(expected,cancellation,advanced).AsSpan().SequenceEqual(TransferService.Hash(existing,cancellation,advanced)))throw new IOException("Workspace file differs from its source.");
            return;
        }
        if(System.IO.File.Exists(temporary)){PathPolicy.AssertNoLinks(temporary);if(NativeFile.ReadStamp(temporary).Links!=1)throw new IOException("Shared workspace partial file.");}
        using var input=open();
        byte[] buffer=ArrayPool<byte>.Shared.Rent((int)Math.Clamp(input.Length,4096,1024*1024));
        byte[] digest;
        try {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)) {
                int count;while((count=input.Read(buffer,0,buffer.Length))>0){OperationPause.Check(cancellation);output.Write(buffer,0,count);hash.AppendData(buffer,0,count);advanced(count);}
                // No authoritative copy is removed. Interrupted staging is checked again on resume.
                output.Flush();
            }
            digest=hash.GetHashAndReset();
        } finally {ArrayPool<byte>.Shared.Return(buffer,clearArray:true);}
        using(var written=System.IO.File.OpenRead(temporary))if(!digest.AsSpan().SequenceEqual(TransferService.Hash(written,cancellation,advanced)))throw new IOException("Workspace copy verification failed.");
        OperationPause.Check(cancellation);System.IO.File.Move(temporary,target,false);
    }
}
