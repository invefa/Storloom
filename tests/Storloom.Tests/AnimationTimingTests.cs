using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Storloom.Core.Chats;
using Xunit;

namespace Storloom.Tests;

public sealed class AnimationTimingTests {
    private static byte[] Gif()=>Convert.FromBase64String("R0lGODlhAgACAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwECAAAh+QQIBAAAACwAAAAAAgACAAAIBgABCAQQEAAh+QQJEgACACwAAAAAAgACAIEAAP8AAAAAAAAAAAAIBgABCAQQEAA7");
    private static byte[] Container(int secondDelay=100,int thirdDelay=100){
        using var output=new MemoryStream();var header=new byte[17];"wxgf"u8.CopyTo(header);header[4]=17;header[12]=3;output.Write(header);
        void Picture(byte[] bytes){var length=new byte[4];BinaryPrimitives.WriteInt32BigEndian(length,bytes.Length);output.Write(length);output.Write(bytes);}
        void Gap(int delay){var gap=new byte[14];BinaryPrimitives.WriteUInt16BigEndian(gap,(ushort)delay);gap[2]=1;gap[6]=3;output.Write(gap[..10]);}
        Picture([0,0,0,1,0x40,1,12,0,0,1,0x26,1,128,22]);
        Gap(secondDelay);Picture([0,0,0,1,2,1,128,33,0,0,1,0,1,128,44]); // P/B pictures in one partition.
        Gap(thirdDelay);Picture([0,0,0,1,2,1,128,55]);return output.ToArray();
    }
    [Fact]
    public void UniformContainerTimingCountsPicturesRatherThanPartitions(){
        var timeline=WeChatAnimation.UniformTimeline(Container());Assert.NotNull(timeline);Assert.Equal(4,timeline.Value.Frames);Assert.Equal(100,timeline.Value.Delay);
        Assert.DoesNotContain("wxgf",Encoding.ASCII.GetString(timeline.Value.Stream));
        Assert.Null(WeChatAnimation.UniformTimeline(Container(100,200)));
        var truncated=Container()[..^1];Assert.Null(WeChatAnimation.UniformTimeline(truncated));
        var mismatch=Container();mismatch[12]=8;Assert.Null(WeChatAnimation.UniformTimeline(mismatch));
        var unsupported=Container();unsupported[6]=2;Assert.Null(WeChatAnimation.UniformTimeline(unsupported));
    }
    [Fact]
    public void GifRetimingPreservesPicturesDisposalAndLoopAndRejectsFrameLoss(){
        byte[] original=Gif(),retimed=Gif();Assert.True(WeChatAnimation.SetGifDelay(retimed,2,100));
        var changed=Enumerable.Range(0,original.Length).Where(i=>original[i]!=retimed[i]).ToArray();Assert.Equal(2,changed.Length);
        Assert.All(changed,i=>Assert.Equal((byte)10,retimed[i]));
        byte[] invalid=Gif();Assert.False(WeChatAnimation.SetGifDelay(invalid,3,100));Assert.Equal(original,invalid);
        Assert.False(WeChatAnimation.SetGifDelay(original[..^1],2,100));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void RepairsPublishNewAssetsWithoutChangingOriginalMessagesOrCredentials(bool encrypted){
        string root=Path.Combine(AppContext.BaseDirectory,"fixtures","animation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string directory=Path.Combine(root,"archive");const string password="fixture-password";
        try{
            string original="assets/"+Convert.ToHexStringLower(SHA256.HashData(Gif()))+".gif";byte[] replacement=Gif();Assert.True(WeChatAnimation.SetGifDelay(replacement,2,100));
            string target="assets/"+Convert.ToHexStringLower(SHA256.HashData(replacement))+".gif";
            using(var store=ArchiveStore.CreateBundle(directory,encrypted?password:default)){
                using(var writer=store.BeginWrite()){writer.Bytes(original,Gif());writer.Complete(new(new string('a',64),"complete",0,0,0,1,Gif().Length));}
                store.EnableStandalone();string? oldHead=store.Head;string key=Path.Combine(ArchiveStore.ResolveRoot(directory),"keyring.json");byte[]? keyHash=encrypted?SHA256.HashData(File.ReadAllBytes(key)):null;
                Assert.Equal(1,store.RepairAnimationMedia(new Dictionary<string,byte[]>{{original,replacement}}));Assert.NotEqual(oldHead,store.Head);
                byte[] Read(string path){using var input=store.OpenFile(path);using var output=new MemoryStream();input.CopyTo(output);return output.ToArray();}
                Assert.Equal(Gif(),Read(original));Assert.Equal(replacement,Read(target));Assert.Equal(0,store.ImportCursor!.Messages);
                if(encrypted)Assert.Equal(keyHash,SHA256.HashData(File.ReadAllBytes(key)));
                using var index=JsonDocument.Parse(Encoding.UTF8.GetString(store.ReaderIndex())["window.archiveIndex=".Length..].TrimEnd(';'));Assert.Equal(target,index.RootElement.GetProperty("mediaRepairs").GetProperty(original).GetString());
                using(var writer=store.BeginWrite()){writer.Bytes("notes.txt","synthetic"u8.ToArray());writer.Complete(store.ImportCursor!);}
            }
            using var reopened=ArchiveStore.Open(directory,encrypted?password:default);Assert.Contains(target,Encoding.UTF8.GetString(reopened.ReaderIndex()));
        }finally{Directory.Delete(root,true);}
    }
}
