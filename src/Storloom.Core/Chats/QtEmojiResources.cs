using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Storloom.Core.Chats;

// Read Qt's compiled resource tables as data; never load or execute a client DLL.
internal static class QtEmojiResources {
    internal static Dictionary<string, byte[]> Read(string path) {
        PathPolicy.AssertNoLinks(path);
        if (new FileInfo(path).Length > 512L * 1024 * 1024) return [];
        byte[] data = File.ReadAllBytes(path);
        using var pe = new PEReader(new MemoryStream(data, false));
        int Raw(int rva) { foreach (var s in pe.PEHeaders.SectionHeaders) if (rva >= s.VirtualAddress && rva - s.VirtualAddress < s.SizeOfRawData) return checked(s.PointerToRawData + rva - s.VirtualAddress); return -1; }
        int Rva(int raw) { foreach (var s in pe.PEHeaders.SectionHeaders) if (raw >= s.PointerToRawData && raw - s.PointerToRawData < s.SizeOfRawData) return checked(s.VirtualAddress + raw - s.PointerToRawData); return -1; }
        int Target(int at) => Raw(checked(Rva(at) + 7 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 3, 4))));
        int marker = data.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("newemoji-config.xml"));
        if (marker < 0) return [];
        for (int at = 0; at < data.Length - 64; at++) {
            if (data[at]!=0x4c || data[at+1]!=0x8d || data[at+2]!=0x05) continue;
            int names = Target(at); if (names < marker - 100000 || names > marker) continue;
            int tree = -1, blobs = -1;
            for (int p = Math.Max(0, at - 14); p <= at + 21; p++) {
                if (data[p]==0x48 && data[p+1]==0x8d && data[p+2]==0x15) tree = Target(p);
                if (data[p]==0x4c && data[p+1]==0x8d && data[p+2]==0x0d) blobs = Target(p);
            }
            if (tree < 0 || blobs < 0) continue;
            try { var result = Decode(data, tree, names, blobs); if (result.ContainsKey("newemoji-config.xml")) {
                int aliases=data.AsSpan().IndexOf(Encoding.UTF8.GetBytes("/微笑|/撇嘴|"));
                if(aliases>=0){int end=data.AsSpan(aliases,Math.Min(16384,data.Length-aliases)).IndexOf((byte)0);if(end>0)result["base-aliases.txt"]=data.AsSpan(aliases,end).ToArray();}
                return result;
            } }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IOException) { }
        }
        return [];
    }
    internal static Dictionary<string, byte[]> Decode(byte[] data, int tree, int names, int blobs) {
        int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at,2));
        int U32(int at) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at,4)));
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase); var visited = new HashSet<int>(); long total = 0, expandedTotal = 0;
        void Visit(int node, string parent, int depth) {
            if (depth > 12 || !visited.Add(node) || visited.Count > 4096) throw new InvalidDataException("Invalid Qt resource tree.");
            int at = checked(tree + node * 22), pos = checked(names + U32(at)), length = U16(pos);
            if (length > 256) throw new InvalidDataException("Invalid Qt resource name.");
            string name = Encoding.BigEndianUnicode.GetString(data, pos + 6, checked(length * 2));
            string full = parent + "/" + name; int flags = U16(at + 4);
            if ((flags & 2) != 0) { int count = U32(at+6), first = U32(at+10); if (count > 4096) throw new InvalidDataException("Invalid Qt resource count."); for (int i=0;i<count;i++) Visit(checked(first+i),full,depth+1); return; }
            if (!full.Contains("/emoji/",StringComparison.Ordinal) || name != Path.GetFileName(name) || !(name.EndsWith(".png",StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml",StringComparison.OrdinalIgnoreCase))) return;
            int offset = checked(blobs+U32(at+10)), size = U32(offset); if (size > 4*1024*1024 || (total += size) > 32*1024*1024) throw new InvalidDataException("Qt emoji resources exceed limit.");
            byte[] bytes = data.AsSpan(offset+4,size).ToArray();
            if ((flags & 1) != 0) { if (U32(offset+4)>4*1024*1024) throw new InvalidDataException("Compressed resource exceeds limit."); using var packed=new ZLibStream(new MemoryStream(bytes,4,bytes.Length-4),CompressionMode.Decompress); using var plain=new MemoryStream(); byte[] buffer=new byte[8192];int read;while((read=packed.Read(buffer))>0){if(plain.Length+read>4*1024*1024)throw new InvalidDataException("Compressed resource exceeds limit.");plain.Write(buffer,0,read);}bytes=plain.ToArray(); }
            else if ((flags & 4) != 0) return; // Qt builds using other compression keep their textual fallback.
            if((expandedTotal+=bytes.Length)>32*1024*1024)throw new InvalidDataException("Expanded emoji resources exceed limit.");
            result.TryAdd(name,bytes);
        }
        Visit(0,"",0);return result;
    }
}
