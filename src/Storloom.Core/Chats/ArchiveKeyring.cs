using System.Security.Cryptography;
using System.Text;

namespace Storloom.Core.Chats;

// Password wrapping is separate from archive data encryption. Rewrapping does not rotate
// a compromised master key and does not revoke copies of an older keyring.
internal sealed class ArchiveKeyring:IDisposable {
    private sealed record Configuration(int Version,string Id,int Iterations,string Salt,string Wrapped,string Mode="password");
    private const int Iterations=600000;
    private const string FileName="keyring.json";
    private readonly string root;
    private readonly byte[] master;
    private Configuration configuration;
    private bool disposed;
    internal string Id=>configuration.Id;
    internal ChatVaultKeyMode KeyMode=>configuration.Mode=="direct-key"?ChatVaultKeyMode.DirectKey:ChatVaultKeyMode.Password;
    private ArchiveKeyring(string root,byte[] master,Configuration configuration){this.root=root;this.master=master;this.configuration=configuration;}
    private static string Purpose(string id)=>"storloom/archive-v2/"+id+"/master";
    private static void Password(ReadOnlySpan<char> password){if(password.Length<8||password.Length>65536)throw new ArgumentException("Archive password must contain 8 to 65536 characters.",nameof(password));}
    private static string Root(string path){string root=PathPolicy.Normalize(path);PathPolicy.AssertNoLinks(root);return root;}
    private static FileStream Lease(string root){string path=Path.Combine(root,"writer.lock");if(File.Exists(path))PathPolicy.AssertNoLinks(path);return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
    private static Configuration Wrap(string id,byte[] master,ReadOnlySpan<char> password){
        Password(password);byte[] salt=RandomNumberGenerator.GetBytes(32),wrapping=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32);
        try{return new(1,id,Iterations,Convert.ToBase64String(salt),Convert.ToBase64String(ChatVault.Seal(wrapping,master,Purpose(id))));}finally{CryptographicOperations.ZeroMemory(wrapping);}
    }
    internal static ArchiveKeyring Create(string directory,ReadOnlySpan<char> password,ChatVaultKeyMode mode=ChatVaultKeyMode.Password){
        ChatVault.ValidateCredential(password,mode);string root=Root(directory),path=Path.Combine(root,FileName);
        using var lease=Lease(root);
        if(File.Exists(path))throw new IOException("Archive keyring already exists.");
        byte[] master=mode==ChatVaultKeyMode.DirectKey?Convert.FromHexString(password):RandomNumberGenerator.GetBytes(32);
        try{string id=Guid.NewGuid().ToString("N");var config=mode==ChatVaultKeyMode.DirectKey?new Configuration(2,id,0,"",Convert.ToBase64String(ChatVault.Seal(master,[],Purpose(id))),"direct-key"):Wrap(id,master,password);JsonFiles.Write(path,config);return new(root,master,config);}catch{CryptographicOperations.ZeroMemory(master);throw;}
    }
    private static Configuration Read(string directory){
        string path=Path.Combine(Root(directory),FileName);PathPolicy.AssertNoLinks(path);
        if(new FileInfo(path).Length>4096)throw new InvalidDataException("Invalid archive keyring size.");
        var config=JsonFiles.Read<Configuration>(path);
        bool wrapped=config.Version==1&&config.Mode=="password"&&config.Iterations==Iterations;
        bool direct=config.Version==2&&config.Mode=="direct-key"&&config.Iterations==0&&config.Salt=="";
        if(!Guid.TryParseExact(config.Id,"N",out _)||!wrapped&&!direct)throw new InvalidDataException("Unsupported archive keyring.");
        if(Convert.FromBase64String(config.Salt).Length!=(direct?0:32)||Convert.FromBase64String(config.Wrapped).Length!=(direct?48:80))throw new InvalidDataException("Invalid archive keyring.");
        return config;
    }
    internal static ChatVaultKeyMode ReadKeyMode(string directory)=>Read(directory).Mode=="direct-key"?ChatVaultKeyMode.DirectKey:ChatVaultKeyMode.Password;
    internal static ArchiveKeyring Open(string directory,ReadOnlySpan<char> password){
        string root=Root(directory);var config=Read(root);
        if(config.Mode=="direct-key"){
            ChatVault.ValidateCredential(password,ChatVaultKeyMode.DirectKey);byte[] master=Convert.FromHexString(password);
            try{_ = ChatVault.Unseal(master,Convert.FromBase64String(config.Wrapped),Purpose(config.Id));return new(root,master,config);}catch{CryptographicOperations.ZeroMemory(master);throw;}
        }
        Password(password);
        byte[] salt=Convert.FromBase64String(config.Salt),wrapped=Convert.FromBase64String(config.Wrapped);
        if(salt.Length!=32||wrapped.Length!=80)throw new InvalidDataException("Invalid archive keyring.");
        byte[] wrapping=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32);
        try{return new(root,ChatVault.Unseal(wrapping,wrapped,Purpose(config.Id)),config);}finally{CryptographicOperations.ZeroMemory(wrapping);}
    }
    internal byte[] DomainKey(string domain){ObjectDisposedException.ThrowIf(disposed,this);if(string.IsNullOrEmpty(domain)||domain.Length>128)throw new ArgumentException("Invalid protection domain.",nameof(domain));return HMACSHA256.HashData(master,Encoding.UTF8.GetBytes("storloom/archive-v2/"+Id+"/"+domain));}
    internal byte[] Protect(byte[] plain,string domain){byte[] key=DomainKey(domain);try{return ChatVault.Seal(key,plain,"archive-v2/"+Id+"/"+domain);}finally{CryptographicOperations.ZeroMemory(key);}}
    internal byte[] Unprotect(byte[] cipher,string domain){byte[] key=DomainKey(domain);try{return ChatVault.Unseal(key,cipher,"archive-v2/"+Id+"/"+domain);}finally{CryptographicOperations.ZeroMemory(key);}}
    internal void ChangePassword(ReadOnlySpan<char> password){
        ObjectDisposedException.ThrowIf(disposed,this);if(KeyMode==ChatVaultKeyMode.DirectKey)throw new InvalidOperationException("A direct data key cannot be changed by rewrapping a password.");Password(password);Root(root);string path=Path.Combine(root,FileName);PathPolicy.AssertNoLinks(path);
        using var lease=Lease(root);
        if(JsonFiles.Read<Configuration>(path)!=configuration)throw new IOException("Archive keyring changed since opening.");
        if(NativeFile.ReadStamp(path).Links!=1)throw new IOException("Shared keyring cannot be replaced.");
        var replacement=Wrap(Id,master,password);JsonFiles.Write(path,replacement);configuration=replacement;
    }
    public void Dispose(){if(!disposed){CryptographicOperations.ZeroMemory(master);disposed=true;}}
}
