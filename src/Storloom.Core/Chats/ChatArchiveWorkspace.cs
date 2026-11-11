using System.Runtime.Versioning;
namespace Storloom.Core.Chats;
/// <summary>Only EFS-capable Windows volumes may contain decrypted protected workspace files.</summary>
public static class ChatArchiveWorkspace {
    public static void Create(string directory,bool encrypted){
        directory=PathPolicy.Normalize(directory);PathPolicy.AssertNoLinks(Path.GetDirectoryName(directory)!);
        if(Directory.Exists(directory)||File.Exists(directory))throw new IOException("Workspace must be new.");
        Directory.CreateDirectory(directory);
        if(encrypted){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Protected updates require Windows EFS.");Enable(directory);AssertEncrypted(directory);
            string probe=Path.Combine(directory,".efs-check");File.WriteAllText(probe,"EFS workspace verification");AssertEncrypted(probe);File.Delete(probe);}
    }
    [SupportedOSPlatform("windows")]private static void Enable(string directory){try{File.Encrypt(directory);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException){throw new IOException("Windows EFS is unavailable. No protected content was restored to disk. Choose an EFS-capable backup volume.",ex);}}
    public static void AssertEncrypted(string path){PathPolicy.AssertNoLinks(path);if((File.GetAttributes(path)&FileAttributes.Encrypted)==0)throw new IOException("Protected workspace lost EFS encryption; operation stopped.");}
    internal static void PreparePublication(string directory,bool efs,CancellationToken cancellation){
        if(!efs)return;if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        // Called only after every selected original has been authenticated, committed and removed.
        // Public/unselected content is deliberately public under the chosen protection policy.
        foreach(string file in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories)){OperationPause.Check(cancellation);PathPolicy.AssertNoLinks(file);if((File.GetAttributes(file)&FileAttributes.Encrypted)!=0)File.Decrypt(file);}
        foreach(string child in Directory.EnumerateDirectories(directory,"*",SearchOption.AllDirectories).OrderByDescending(p=>p.Length)){PathPolicy.AssertNoLinks(child);if((File.GetAttributes(child)&FileAttributes.Encrypted)!=0)File.Decrypt(child);}
        if((File.GetAttributes(directory)&FileAttributes.Encrypted)!=0)File.Decrypt(directory);
    }
}
