using System.Text;
using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;

namespace Storloom.Tests;

public sealed class ChatProtectionTests : IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","selection-"+Guid.NewGuid().ToString("N"));
    private const string Id="111111111111111111111111",Other="222222222222222222222222";
    public ChatProtectionTests(){Directory.CreateDirectory(Path.Combine(root,"conversations"));JsonFiles.Write(Path.Combine(root,"export-report.json"),new ChatExportResult(root,3,1,[],true));
        File.WriteAllText(Path.Combine(root,"archive-index.js"),"window.archiveIndex={\"conversations\":[]};");
    }
    private string Page(string id,params ChatMessage[] messages){string path=Path.Combine(root,"conversations",id+"-0.js");File.WriteAllText(path,"window.receivePage(\""+id+"\",0,"+JsonSerializer.Serialize(messages)+");");return path;}
    private static ChatMessage Message(string id,string text)=>new("QQ","group:fixture","fixture-sender",1700000000,1,text){Id=id,Parts=[new("text",text)]};
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ScopedProtectionHidesSelectedContentAndRetainsFullAuthenticatedOriginal(bool strict){
        var selected=Message("selected","private-selection-marker") with{RawBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes("private-selection-marker"))};
        var visible=Message("visible","public-text") with{Parts=[new("reply","public-text",ReferenceText:"private-selection-marker")],SourceText="private-selection-marker"};
        string page=Page(Id,selected,visible),other=Page(Other,Message("other","other-public-text"));byte[] original=File.ReadAllBytes(page);
        File.WriteAllText(Path.Combine(root,"messages.jsonl"),"private-selection-marker");
        using(var vault=ChatVault.Create(root,"eight123",selection:new([Id],MessageIds:["selected"],Strict:strict))){
            var result=vault.Encrypt(workers:4);Assert.False(result.Complete);Assert.True(File.Exists(other));Assert.False(File.Exists(Path.Combine(root,"messages.jsonl")));
            using var source=vault.OpenRead("conversations/"+Id+"-0.js");using var bytes=new MemoryStream();source.CopyTo(bytes);Assert.Equal(original,bytes.ToArray());
            if(strict){string publicText=File.ReadAllText(page);Assert.Contains("public-text",publicText);Assert.DoesNotContain("private-selection-marker",publicText);Assert.DoesNotContain(selected.RawBase64!,publicText);}
            else Assert.False(File.Exists(page));
        }
        using var resumed=ChatVault.Open(root,"eight123");resumed.Encrypt();Assert.Contains("other-public-text",File.ReadAllText(other));
    }
    [Fact]
    public void StrictPublicPagePublicationRecoversAfterInterruption(){
        string page=Page(Id,Message("private","private-text"),Message("public","public-text"));string stage;
        using(var vault=ChatVault.Create(root,"eight123",selection:new([Id],MessageIds:["private"],Strict:true))){stage=Directory.GetFiles(Path.Combine(root,".vault"),"*.public").Single();var status=vault.Encrypt();Assert.False(status.Complete);Assert.True(status.SelectionComplete);}
        File.Move(page,stage); // Simulate the durable commit with publication still pending.
        using var resumed=ChatVault.Open(root,"eight123");resumed.Encrypt();Assert.Contains("public-text",File.ReadAllText(page));Assert.DoesNotContain("private-text",File.ReadAllText(page));
    }
    [Fact]
    public void EmptySelectionDoesNotAlterOriginalsOrBlockRetry(){
        string page=Page(Id,Message("public","public-text"));string before=File.ReadAllText(page);
        Assert.Throws<InvalidOperationException>(()=>ChatVault.Create(root,"eight123",selection:new(Content:"absent")));
        Assert.Equal(before,File.ReadAllText(page));Assert.False(ChatVault.Exists(root));Assert.False(Directory.Exists(Path.Combine(root,".vault")));
        using var retry=ChatVault.Create(root,"eight123");Assert.True(retry.Encrypt().Complete);
    }
    [Theory]
    [InlineData(" 12345678901234 ",ChatPlatform.QQ)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",ChatPlatform.WeChat)]
    public void KeyInterchangePreservesKeySpacesAndAcceptsFileLineEndings(string value,ChatPlatform platform){
        byte[] key=ChatDatabaseKey.Parse(value+"\r\n",platform);Assert.Equal(value,ChatDatabaseKey.Format(key,platform));
        Assert.Throws<FormatException>(()=>ChatDatabaseKey.Parse("invalid",platform));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
