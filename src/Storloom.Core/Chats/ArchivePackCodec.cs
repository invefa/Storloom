using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
namespace Storloom.Core.Chats;

// Binary payloads with independently authenticated records. Metadata is small and
// contains the byte ranges required for a single-resource read, never Base64 data.
internal static class ArchivePackCodec {
    private static ReadOnlySpan<byte> Magic=>"SLPACK03"u8;
    internal sealed record Location(string Id,int Length,int Offset,int Stored);
    internal sealed record ObjectDirectory(Location[] Objects,long Start);
    private sealed record Metadata(ArchiveDelta Delta,Location[] Objects);
    internal static bool IsBinary(ReadOnlySpan<byte> bytes)=>bytes.StartsWith(Magic);
    internal static byte[] Compress(byte[] plain){
        if(plain.Length>ArchiveCommitLog.MaximumPayloadBytes)throw new InvalidDataException("Archive record too large.");
        byte[] compressed=new byte[BrotliEncoder.GetMaxCompressedLength(plain.Length)];
        bool compress=true;if(plain.Length>=2048){Span<bool> symbols=stackalloc bool[256];symbols.Clear();int distinct=0;foreach(byte value in plain.AsSpan(plain.Length/2,1024))if(!symbols[value]){symbols[value]=true;distinct++;}compress=distinct<230;}
        int size=0;bool use=compress&&plain.Length>=256&&BrotliEncoder.TryCompress(plain,compressed,out size,quality:1,window:20)&&size+32<plain.Length;
        byte[] result=new byte[5+(use?size:plain.Length)];result[0]=use?(byte)1:(byte)0;BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1),plain.Length);(use?compressed.AsSpan(0,size):plain.AsSpan()).CopyTo(result.AsSpan(5));CryptographicOperations.ZeroMemory(compressed);return result;
    }
    internal static byte[] Expand(byte[] encoded){
        if(encoded.Length<5)throw new InvalidDataException("Truncated archive record.");int size=BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(1));
        if(size<0||size>ArchiveCommitLog.MaximumPayloadBytes)throw new InvalidDataException("Invalid archive record size.");byte[] plain=new byte[size];
        try{if(encoded[0]==0){if(encoded.Length!=size+5)throw new InvalidDataException("Invalid archive record length.");encoded.AsSpan(5).CopyTo(plain);}else if(encoded[0]!=1||!BrotliDecoder.TryDecompress(encoded.AsSpan(5),plain,out int count)||count!=size)throw new InvalidDataException("Invalid compressed archive record.");return plain;}catch{CryptographicOperations.ZeroMemory(plain);throw;}
    }
    private static byte[] EncodeRecord(byte[] plain,string purpose,Func<byte[],string,byte[]> protect){byte[] packed=Compress(plain);try{return protect(packed,purpose);}finally{CryptographicOperations.ZeroMemory(packed);}}
    private static byte[] DecodeRecord(byte[] encoded,string purpose,Func<byte[],string,byte[]> unprotect){byte[] packed=unprotect(encoded,purpose);try{return Expand(packed);}finally{CryptographicOperations.ZeroMemory(packed);}}
    internal static byte[] Encode(ArchiveDelta delta,Func<byte[],string,byte[]> protect,int workers){
        byte[][] records=new byte[delta.Objects.Length][];var locations=new Location[records.Length];
        try{Parallel.For(0,records.Length,new ParallelOptions{MaxDegreeOfParallelism=Math.Clamp(workers,1,8)},i=>records[i]=EncodeRecord(delta.Objects[i].Bytes,"record/"+delta.Objects[i].Id,protect));int offset=0;
            for(int i=0;i<records.Length;i++){locations[i]=new(delta.Objects[i].Id,delta.Objects[i].Bytes.Length,offset,records[i].Length);offset=checked(offset+records[i].Length);}
            byte[] json=JsonSerializer.SerializeToUtf8Bytes(new Metadata(delta with{Objects=[]},locations));byte[] metadata;try{metadata=EncodeRecord(json,"pack/metadata",protect);}finally{CryptographicOperations.ZeroMemory(json);}
            try{if((long)16+metadata.Length+offset>ArchiveCommitLog.MaximumPayloadBytes)throw new InvalidDataException("Archive pack exceeds its size bound.");using var output=new MemoryStream(16+metadata.Length+offset);output.Write(Magic);Span<byte> header=stackalloc byte[8];BinaryPrimitives.WriteInt32LittleEndian(header,metadata.Length);BinaryPrimitives.WriteInt32LittleEndian(header[4..],records.Length);output.Write(header);output.Write(metadata);foreach(var record in records)output.Write(record);return output.ToArray();}finally{CryptographicOperations.ZeroMemory(metadata);}
        }finally{foreach(var record in records)if(record!=null)CryptographicOperations.ZeroMemory(record);}
    }
    private static (Metadata Data,long Start) Header(Stream input,Func<byte[],string,byte[]> unprotect){
        Span<byte> header=stackalloc byte[16];input.ReadExactly(header);if(!IsBinary(header))throw new InvalidDataException("Unsupported binary archive pack.");int length=BinaryPrimitives.ReadInt32LittleEndian(header[8..]),count=BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if(length<5||length>ArchiveCommitLog.MaximumPayloadBytes||count<0||count>65536||length>input.Length-16)throw new InvalidDataException("Invalid archive pack directory.");byte[] encoded=new byte[length];input.ReadExactly(encoded);byte[] json=DecodeRecord(encoded,"pack/metadata",unprotect);
        try{var metadata=JsonSerializer.Deserialize<Metadata>(json)??throw new InvalidDataException("Missing archive metadata.");if(metadata.Objects.Length!=count||metadata.Delta.Objects.Length!=0)throw new InvalidDataException("Invalid archive object directory.");long end=0;foreach(var item in metadata.Objects){if(!ArchiveQueryCache.ValidId(item.Id)||item.Length is <1 or >4*1024*1024||item.Offset!=end||item.Stored<5)throw new InvalidDataException("Invalid archive object location.");end=checked(end+item.Stored);}if(16L+length+end!=input.Length)throw new InvalidDataException("Archive pack length mismatch.");return(metadata,16L+length);}finally{CryptographicOperations.ZeroMemory(json);CryptographicOperations.ZeroMemory(encoded);}
    }
    private static byte[] Object(Stream input,Location item,long start,Func<byte[],string,byte[]> unprotect){input.Position=checked(start+item.Offset);byte[] encoded=new byte[item.Stored];input.ReadExactly(encoded);try{byte[] plain=DecodeRecord(encoded,"record/"+item.Id,unprotect);if(plain.Length!=item.Length){CryptographicOperations.ZeroMemory(plain);throw new InvalidDataException("Archive object size mismatch.");}return plain;}finally{CryptographicOperations.ZeroMemory(encoded);}}
    internal static ArchiveDelta DecodeProjection(Stream input,Func<byte[],string,byte[]> unprotect){var (metadata,_)=Header(input,unprotect);return metadata.Delta with{Objects=metadata.Objects.Select(item=>new ArchiveObject(item.Id,[]){DeclaredLength=item.Length}).ToArray()};}
    internal static ArchiveDelta Decode(byte[] bytes,Func<byte[],string,byte[]> unprotect){using var input=new MemoryStream(bytes,false);var (metadata,start)=Header(input,unprotect);var objects=new List<ArchiveObject>();try{foreach(var item in metadata.Objects)objects.Add(new(item.Id,Object(input,item,start,unprotect)));return metadata.Delta with{Objects=objects.ToArray()};}catch{foreach(var item in objects)CryptographicOperations.ZeroMemory(item.Bytes);throw;}}
    internal static ObjectDirectory ReadDirectory(Stream input,Func<byte[],string,byte[]> unprotect){var (metadata,start)=Header(input,unprotect);return new(metadata.Objects,start);}
    internal static byte[] ReadObject(Stream input,int ordinal,string id,long length,Func<byte[],string,byte[]> unprotect,ObjectDirectory? directory=null){directory??=ReadDirectory(input,unprotect);if(ordinal<0||ordinal>=directory.Objects.Length)throw new InvalidDataException("Invalid object ordinal.");var item=directory.Objects[ordinal];if(item.Id!=id||item.Length!=length)throw new InvalidDataException("Archive object locator mismatch.");return Object(input,item,directory.Start,unprotect);}
}
