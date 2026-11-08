using System.Buffers.Binary;
using System.Text;
using Storloom.Core.Chats;
using Xunit;

namespace Storloom.Tests;

public sealed class QQKeyCaptureTests {
    private static byte[] Image() {
        byte[] image = new byte[0x800];
        void W16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), value);
        void W32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);
        W16(0,0x5a4d);W32(0x3c,0x80);W32(0x80,0x4550);W16(0x84,0x8664);W16(0x86,3);W16(0x94,0xf0);W16(0x96,0x2022);
        int opt=0x98;W16(opt,0x20b);W32(opt+32,0x1000);W32(opt+36,0x200);W32(opt+56,0x4000);W32(opt+60,0x200);W32(opt+108,16);
        W32(opt+112+3*8,0x3000);W32(opt+116+3*8,12);
        void Section(int index,string name,uint rva,uint raw) {
            int offset=0x188+index*40;Encoding.ASCII.GetBytes(name).CopyTo(image,offset);W32(offset+8,0x200);W32(offset+12,rva);W32(offset+16,0x200);W32(offset+20,raw);
        }
        Section(0,".text",0x1000,0x200);Section(1,".rdata",0x2000,0x400);Section(2,".pdata",0x3000,0x600);
        image.AsSpan(0x200,0x200).Fill(0x90);image[0x210]=0x48;image[0x211]=0x8d;image[0x212]=0x15;W32(0x213,0x2000-0x1017);
        "nt_sqlite3_key_v2: db=%p zDb=%s"u8.CopyTo(image.AsSpan(0x400));W32(0x600,0x1000);W32(0x604,0x1080);W32(0x608,0x3080);
        return image;
    }
    [Fact]
    public void ResolvesTheRuntimeFunctionFromItsStringReference() {
        byte[] image=Image();var function=QQKeyLocator.Find(image);
        Assert.Equal(0x1000u,function.Rva);Assert.Equal(image.AsSpan(0x200,16).ToArray(),function.Prefix);
    }
    [Theory]
    [InlineData("marker")]
    [InlineData("duplicate-marker")]
    [InlineData("reference")]
    [InlineData("function")]
    [InlineData("ambiguous-functions")]
    [InlineData("architecture")]
    [InlineData("truncated")]
    public void RejectsMissingAmbiguousAndMalformedLayouts(string damage) {
        byte[] image=Image();
        switch(damage) {
            case "marker":image[0x400]=0;break;
            case "duplicate-marker":"nt_sqlite3_key_v2: db=%p zDb=%s"u8.CopyTo(image.AsSpan(0x440));break;
            case "reference":image[0x210]=0;break;
            case "function":BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x600),0x1100);break;
            case "ambiguous-functions":
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x98+116+3*8),24);
                image.AsSpan(0x600,12).CopyTo(image.AsSpan(0x60c));break;
            case "architecture":image[0x84]=0;break;
            case "truncated":Array.Resize(ref image,0x420);break;
        }
        Assert.Throws<InvalidDataException>(()=>QQKeyLocator.Find(image));
    }
    [Fact]
    public void InspectionNeverLaunchesAnUnsignedExecutable() {
        string directory=Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try {string executable=Path.Combine(directory,"QQ.exe");File.WriteAllText(executable,"Unsigned fixture");Assert.Throws<System.Security.Cryptography.CryptographicException>(()=>QQKeyCapture.Inspect(executable));}
        finally {Directory.Delete(directory,true);}
    }
}
