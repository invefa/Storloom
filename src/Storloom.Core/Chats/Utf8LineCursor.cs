using System.Buffers;
using System.Text;
namespace Storloom.Core.Chats;

/// <summary>UTF-8 line reader whose checkpoint is the exact consumed byte offset, not a buffered stream position.</summary>
internal sealed class Utf8LineCursor:IDisposable {
    private readonly Stream input;
    private readonly byte[] buffer=new byte[64*1024];
    private int position,length;
    private readonly ArrayBufferWriter<byte> line=new();
    private static readonly UTF8Encoding encoding=new(false,true);
    public long Offset{get;private set;}
    public Utf8LineCursor(string path,long offset=0):this(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,64*1024,FileOptions.SequentialScan),offset){}
    internal Utf8LineCursor(Stream source,long offset=0){input=source;if(!source.CanSeek||offset<0||offset>input.Length){input.Dispose();throw new InvalidDataException("Invalid transcript checkpoint.");}input.Position=Offset=offset;}
    public string? ReadLine(CancellationToken cancellation){
        line.Clear();
        long start=Offset;
        while(true){
            OperationPause.Check(cancellation);
            if(position==length){length=input.Read(buffer);position=0;if(length==0)return line.WrittenCount==0?null:Decode(start);}
            int newline=Array.IndexOf(buffer,(byte)'\n',position,length-position);
            int end=newline<0?length:newline;
            if((long)line.WrittenCount+end-position>64*1024*1024)throw new InvalidDataException("Transcript line exceeds the supported limit.");
            line.Write(buffer.AsSpan(position,end-position));Offset+=end-position;position=end;
            if(newline>=0){position++;Offset++;return Decode(start);}
        }
    }
    private string Decode(long start){var bytes=line.WrittenSpan;if(bytes.Length>0&&bytes[^1]=='\r')bytes=bytes[..^1];if(start==0&&bytes.StartsWith("\uFEFF"u8))bytes=bytes[3..];return encoding.GetString(bytes);}
    public void Dispose()=>input.Dispose();
}
