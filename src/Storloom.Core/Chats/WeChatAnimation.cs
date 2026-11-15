using System.Diagnostics;
using System.Buffers.Binary;

namespace Storloom.Core.Chats;

// WXGF carries an HEVC stream. Conversion uses an optional local FFmpeg installation;
// no executable or client resource is downloaded or bundled by this resolver.
internal static class WeChatAnimation {
    // Version-zero animated WXGF stores a duration and a length-delimited mask
    // before each subsequent HEVC picture. Only accept a complete, uniform
    // timeline here; the initial picture's timing is not independently described.
    // Do not infer a frame rate from FFmpeg's raw HEVC default (usually 25 fps).
    internal static (byte[] Stream, int Frames, int Delay)? UniformTimeline(byte[] data) {
        if(data.Length<17||data.Length>32*1024*1024||!data.AsSpan().StartsWith("wxgf"u8)||data[5]!=0||data[6]!=0)return null;
        int first=-1;
        for(int p=Math.Max(8,(int)data[4]);p+6<data.Length;p++)
            if(data.AsSpan(p,6).SequenceEqual("\0\0\0\u0001@\u0001"u8)&&p>=4){first=p;break;}
        if(first<0)return null;
        using var stream=new MemoryStream();int at=first,frames=0,delay=0;
        while(true){
            int size=BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(at-4,4));
            if(size<6||size>data.Length-at||++frames>1000)return null;
            if(!data.AsSpan(at,4).SequenceEqual("\0\0\0\u0001"u8))return null;
            stream.Write(data,at,size);int end=at+size;
            if(end==data.Length)break;
            if(data.Length-end<11)return null;
            int nextDelay=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(end,2));
            if(nextDelay<20||nextDelay>60000||delay!=0&&delay!=nextDelay||data[end+2]>1)return null;
            delay=nextDelay;at=-1;
            // Mask compression/palettes have several encodings. Locate the next
            // length-delimited picture, then validate its endpoint as well as
            // its NAL header; never treat start codes inside a picture as frames.
            for(int p=end+11;p+6<=data.Length;p++){
                if(!data.AsSpan(p,4).SequenceEqual("\0\0\0\u0001"u8)||(data[p+5]&7)==0||(data[p+4]>>1)>32)continue;
                int length=BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p-4,4));if(length<6||length>data.Length-p)continue;
                int next=p+length;if(next!=data.Length&&(data.Length-next<11||BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(next,2))!=delay||data[next+2]>1))continue;
                at=p;break;
            }
            if(at<0)return null;
        }
        byte[] payload=stream.ToArray();int pictures=0;
        for(int p=0;p+6<payload.Length;p++){
            int prefix=payload.AsSpan(p,4).SequenceEqual("\0\0\0\u0001"u8)?4:payload.AsSpan(p,3).SequenceEqual("\0\0\u0001"u8)?3:0;
            if(prefix==0)continue;int nal=p+prefix;
            if(nal+2<payload.Length&&(payload[nal]>>1)<=31&&(payload[nal+1]&7)!=0&&(payload[nal+2]&128)!=0)pictures++;
            p+=prefix-1;
        }
        // A partition may contain reordered P/B pictures. Counting partitions
        // would drop/double their durations; validate against the container count.
        int declared=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(11,2))+1;
        return frames>1&&delay>0&&pictures==declared?(payload,pictures,delay):null;
    }

    internal static bool SetGifDelay(byte[] gif,int expectedFrames,int milliseconds) {
        if(gif.Length<13||!(gif.AsSpan().StartsWith("GIF89a"u8)||gif.AsSpan().StartsWith("GIF87a"u8)))return false;
        int p=13+((gif[10]&128)!=0?3*(1<<((gif[10]&7)+1)):0),frames=0;var delays=new List<int>();bool control=false;
        bool Blocks(){while(p<gif.Length){int size=gif[p++];if(size==0)return true;if(size>gif.Length-p)return false;p+=size;}return false;}
        while(p<gif.Length){int kind=gif[p++];if(kind==0x3b){if(frames!=expectedFrames||delays.Count!=frames)return false;ushort value=checked((ushort)Math.Clamp((milliseconds+5)/10,1,ushort.MaxValue));foreach(int offset in delays)BinaryPrimitives.WriteUInt16LittleEndian(gif.AsSpan(offset,2),value);return true;}
            if(kind==0x21){if(p>=gif.Length)return false;int label=gif[p++];if(label==0xf9){if(p+6>gif.Length||gif[p]!=4||gif[p+5]!=0||control)return false;delays.Add(p+2);control=true;p+=6;}else if(!Blocks())return false;}
            else if(kind==0x2c){if(p+9>gif.Length||!control)return false;int flags=gif[p+8];p+=9;if((flags&128)!=0)p+=3*(1<<((flags&7)+1));if(p>=gif.Length)return false;p++;if(!Blocks())return false;frames++;control=false;}
            else return false;
        }
        return false;
    }

    public static byte[]? Convert(byte[] data, CancellationToken cancellation) {
        string? executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            .Select(p => Path.Combine(p.Trim('"'), "ffmpeg.exe")).FirstOrDefault(File.Exists);
        if (executable == null || data.Length > 32 * 1024 * 1024) return null;
        int offset = -1;
        for (int i = 4; i < Math.Min(data.Length - 6, 8192); i++)
            if (data.AsSpan(i, 6).SequenceEqual(new byte[] { 0, 0, 0, 1, 0x40, 1 })) { offset = i; break; }
        if (offset < 0) return null;
        var timeline=UniformTimeline(data);
        string directory = Path.Combine(Path.GetTempPath(), "Storloom-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string input = Path.Combine(directory, "input.hevc"), output = Path.Combine(directory, "output.gif");
            File.WriteAllBytes(input, timeline?.Stream??data[offset..]);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-f", "hevc", "-i", input,
                "-vf", (timeline==null?"fps=15,":"")+"scale=w='min(1024,iw)':h='min(1024,ih)':force_original_aspect_ratio=decrease", "-fps_mode", "passthrough", "-fs", "33554433", "-loop", "0", output }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process == null) return null;
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length > 32 * 1024 * 1024) return null;
                byte[] result = File.ReadAllBytes(output);
                if(timeline is{} timing&&!SetGifDelay(result,timing.Frames,timing.Delay))return null;
                return WeChatImages.Format(result) == ".gif" ? result : null;
            } catch (OperationCanceledException) {
                if (!process.HasExited) process.Kill(true);
                process.WaitForExit();
                OperationPause.Check(cancellation);
                return null;
            }
        } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return null; }
        finally {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
