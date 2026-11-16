using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Chats;
using Xunit;
namespace Storloom.Tests;

public sealed class ConfigurationBackupTests:IDisposable {
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","configuration-backup-"+Guid.NewGuid().ToString("N"));
    public ConfigurationBackupTests()=>Directory.CreateDirectory(root);
    private string DirectoryFor(string name){string path=Path.Combine(root,name);Directory.CreateDirectory(path);return path;}
    [Fact]public void CaptureExcludesCredentialsRunningTasksAndMachineExecutables(){
        string source=DirectoryFor("source");var project=ChatArchiveProject.Create("fixture",ChatPlatform.QQ,Path.Combine(root,"client"),root)with{TaskFile=Path.Combine(root,"task.json")};JsonFiles.Write(Path.Combine(source,"chat-archives.json"),new[]{project});
        JsonFiles.Write(Path.Combine(source,"preferences.json"),new{theme="Dark",password="never-package-this",qqExecutable="untrusted.exe",customThemeFile="external.json"});File.WriteAllText(Path.Combine(source,"captured-key.txt"),"private-key-fixture");
        string reader=Path.Combine(source,"reader-preferences");Directory.CreateDirectory(reader);JsonFiles.Write(Path.Combine(reader,new string('a',64)+".json"),new{dark=true,password="never-package-this",view=new{pageSize=500,secret="never-package-this"}});
        var snapshot=ConfigurationBackup.Capture(source,ConfigurationScope.Registrations|ConfigurationScope.Preferences);string text=JsonSerializer.Serialize(snapshot.Items.Select(i=>Encoding.UTF8.GetString(i.Content)));
        Assert.DoesNotContain("never-package-this",text);Assert.DoesNotContain("private-key-fixture",text);Assert.DoesNotContain("untrusted.exe",text);Assert.DoesNotContain("external.json",text);
        var saved=JsonSerializer.Deserialize<ChatArchiveProject[]>(snapshot.Items.Single(i=>i.Path=="chat-archives.json").Content,JsonFiles.Options)!;Assert.Null(saved[0].TaskFile);Assert.Equal(project.Source,saved[0].Source);
        string file=Path.Combine(root,"backup.storloom-config");ConfigurationBackup.Save(file,snapshot);Assert.False(ConfigurationBackup.IsEncrypted(file));Assert.Equal(snapshot.Items.Length,ConfigurationBackup.Open(file).Items.Length);
    }
    [Fact]public void ProtectedKeysRequirePackageEncryptionAndRestoreWithoutChangingTheirOwnPassword(){
        string source=DirectoryFor("source"),target=DirectoryFor("target"),key=Path.Combine(root,"key.json");byte[] secret=Enumerable.Range(0,16).Select(i=>(byte)i).ToArray();ChatKeyFile.Save(key,secret,ChatPlatform.QQ,"key-password");
        JsonFiles.Write(Path.Combine(source,"database-key-library.json"),new[]{new{path=key,account="fixture",platform=ChatPlatform.QQ}});
        Assert.Empty(ConfigurationBackup.Capture(source,ConfigurationScope.Registrations).Items);
        var snapshot=ConfigurationBackup.Capture(source,ConfigurationScope.ProtectedKeys);string file=Path.Combine(root,"keys.storloom-config");Assert.Throws<InvalidOperationException>(()=>ConfigurationBackup.Save(file,snapshot));
        ConfigurationBackup.Save(file,snapshot,"package-password");Assert.True(ConfigurationBackup.IsEncrypted(file));Assert.ThrowsAny<CryptographicException>(()=>ConfigurationBackup.Open(file,"wrong-password"));
        var read=ConfigurationBackup.Open(file,"package-password");var plan=ConfigurationBackup.Preview(target,read,ConfigurationScope.ProtectedKeys);var result=ConfigurationBackup.Restore(plan);Assert.Equal(2,result.Files);
        using var library=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(target,"database-key-library.json")));string imported=library.RootElement[0].GetProperty("path").GetString()!;Assert.StartsWith(target,imported);Assert.Equal(File.ReadAllBytes(key),File.ReadAllBytes(imported));Assert.Equal(secret,ChatKeyFile.Open(imported,ChatPlatform.QQ,"key-password"));
        byte[] bytes=File.ReadAllBytes(file);var envelope=System.Text.Json.Nodes.JsonNode.Parse(bytes)!;string data=envelope["data"]!.GetValue<string>();byte[] cipher=Convert.FromBase64String(data);cipher[0]^=1;envelope["data"]=Convert.ToBase64String(cipher);JsonFiles.Write(file,envelope);Assert.ThrowsAny<CryptographicException>(()=>ConfigurationBackup.Open(file,"package-password"));
    }
    [Fact]public void RestoreMergesByDefaultAllowsReviewedUpdatesAndKeepsRecoveryCopies(){
        string source=DirectoryFor("source"),target=DirectoryFor("target");var project=ChatArchiveProject.Create("fixture",ChatPlatform.QQ,Path.Combine(root,"client"),root)with{Reading=Path.Combine(root,"reader")};JsonFiles.Write(Path.Combine(source,"chat-archives.json"),new[]{project});JsonFiles.Write(Path.Combine(target,"chat-archives.json"),new[]{project with{Reading=null}});
        JsonFiles.Write(Path.Combine(source,"preferences.json"),new{theme="Dark"});JsonFiles.Write(Path.Combine(target,"preferences.json"),new{theme="Light",qqExecutable="keep-local.exe"});
        var snapshot=ConfigurationBackup.Capture(source,ConfigurationScope.Registrations|ConfigurationScope.Preferences);byte[] original=File.ReadAllBytes(Path.Combine(target,"chat-archives.json"));
        var merge=ConfigurationBackup.Preview(target,snapshot,ConfigurationScope.Registrations);ConfigurationBackup.Restore(merge);Assert.Null(JsonFiles.Read<ChatArchiveProject[]>(Path.Combine(target,"chat-archives.json"))[0].Reading);
        var replace=ConfigurationBackup.Preview(target,snapshot,ConfigurationScope.Registrations|ConfigurationScope.Preferences,true);Assert.Contains(replace.Changes,c=>c.Action=="update");var restored=ConfigurationBackup.Restore(replace);
        Assert.Equal(project.Reading,JsonFiles.Read<ChatArchiveProject[]>(Path.Combine(target,"chat-archives.json"))[0].Reading);Assert.Equal(original,File.ReadAllBytes(Path.Combine(restored.RecoveryDirectory,"chat-archives.json")));
        using var prefs=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(target,"preferences.json")));Assert.Equal("Dark",prefs.RootElement.GetProperty("theme").GetString());Assert.Equal("keep-local.exe",prefs.RootElement.GetProperty("qqExecutable").GetString());
    }
    [Fact]public void ChangedConfigurationAndUnsafePackagePathsAreRejectedBeforeRestore(){
        string source=DirectoryFor("source"),target=DirectoryFor("target");JsonFiles.Write(Path.Combine(source,"connected-readers.json"),new[]{Path.Combine(root,"reader")});var snapshot=ConfigurationBackup.Capture(source,ConfigurationScope.Registrations);var plan=ConfigurationBackup.Preview(target,snapshot,ConfigurationScope.Registrations);
        JsonFiles.Write(Path.Combine(target,"connected-readers.json"),new[]{"changed"});Assert.Throws<IOException>(()=>ConfigurationBackup.Restore(plan));Assert.Equal("changed",JsonFiles.Read<string[]>(Path.Combine(target,"connected-readers.json"))[0]);
        var item=snapshot.Items[0];var escaped=snapshot with{Items=[item with{Path="../preferences.json"}]};Assert.Throws<InvalidDataException>(()=>ConfigurationBackup.Preview(target,escaped,ConfigurationScope.Registrations));
        var duplicates=snapshot with{Items=[item,item]};Assert.Throws<InvalidDataException>(()=>ConfigurationBackup.Preview(target,duplicates,ConfigurationScope.Registrations));
        using var stopped=new CancellationTokenSource();stopped.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>ConfigurationBackup.Restore(plan,stopped.Token));
    }
    public void Dispose(){try{Directory.Delete(root,true);}catch(IOException){}}
}
