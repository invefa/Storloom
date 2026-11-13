using System.Security.Cryptography;
namespace Storloom.Core.Chats;

public sealed partial class ChatVault {
    public static ChatVaultKeyMode ReadKeyMode(string directory){
        var config=ReadConfiguration(Path.Combine(PathPolicy.Normalize(directory),Marker));
        if(config.Version is 1 or 2||config.Version==3&&config.Mode=="password")return ChatVaultKeyMode.Password;
        if(config.Version==3&&config.Mode=="direct-key")return ChatVaultKeyMode.DirectKey;
        throw new InvalidDataException("Unsupported vault format.");
    }
    public static void ValidateCredential(ReadOnlySpan<char> credential,ChatVaultKeyMode mode){
        if(mode==ChatVaultKeyMode.DirectKey){if(credential.Length!=64||credential.ContainsAnyExcept("0123456789abcdefABCDEF"))throw new ArgumentException("Use a 256-bit key encoded as 64 hexadecimal characters.");}
        else if(mode!=ChatVaultKeyMode.Password||credential.Length<8||credential.Length>65536)throw new ArgumentException("Use a password of 8 to 65536 characters.");
    }
    private static string KeyPurpose(string id)=>"storloom/reading-v3/"+id+"/master";
    private static Configuration ReadConfiguration(string path){
        PathPolicy.AssertNoLinks(path);if(new FileInfo(path).Length>4096)throw new InvalidDataException("Invalid vault configuration size.");return JsonFiles.Read<Configuration>(path);
    }
    private static Configuration WrapKey(byte[] master,ReadOnlySpan<char> password){
        ValidateCredential(password,ChatVaultKeyMode.Password);byte[] salt=RandomNumberGenerator.GetBytes(32),wrapping=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32);
        try{string id=Guid.NewGuid().ToString("N");return new(3,Convert.ToBase64String(salt),Iterations,Convert.ToBase64String(Seal(wrapping,master,KeyPurpose(id))),id,"password");}finally{CryptographicOperations.ZeroMemory(wrapping);}
    }
    private static byte[] UnlockKey(Configuration config,ReadOnlySpan<char> credential){
        if(config.Version is 1 or 2){
            if(config.Iterations!=Iterations||config.Wrapped!=null||config.Mode!="legacy")throw new InvalidDataException("Unsupported legacy vault configuration.");
            byte[] salt=Convert.FromBase64String(config.Salt);if(salt.Length!=32)throw new InvalidDataException("Invalid vault salt.");
            return Rfc2898DeriveBytes.Pbkdf2(credential,salt,Iterations,HashAlgorithmName.SHA256,32);
        }
        if(config.Version!=3||!Guid.TryParseExact(config.KeyId,"N",out _))throw new InvalidDataException("Unsupported vault format.");
        if(config.Mode=="direct-key"){
            if(config.Iterations!=0||config.Salt!=""||config.Wrapped!=null)throw new InvalidDataException("Invalid direct-key vault configuration.");
            ValidateCredential(credential,ChatVaultKeyMode.DirectKey);return Convert.FromHexString(credential.ToString());
        }
        if(config.Mode!="password"||config.Iterations!=Iterations||config.Wrapped==null)throw new InvalidDataException("Unsupported key wrapping configuration.");
        ValidateCredential(credential,ChatVaultKeyMode.Password);byte[] wrapSalt=Convert.FromBase64String(config.Salt),wrapped=Convert.FromBase64String(config.Wrapped);
        if(wrapSalt.Length!=32||wrapped.Length!=80)throw new InvalidDataException("Invalid wrapped data key.");
        byte[] wrapping=Rfc2898DeriveBytes.Pbkdf2(credential,wrapSalt,Iterations,HashAlgorithmName.SHA256,32);
        try{return Unseal(wrapping,wrapped,KeyPurpose(config.KeyId!));}finally{CryptographicOperations.ZeroMemory(wrapping);}
    }
    /// <summary>Rewrap only the current vault's data key. Old copies and unlocked sessions remain usable.</summary>
    public void ChangePassword(ReadOnlySpan<char> password){
        ObjectDisposedException.ThrowIf(disposed,this);ValidateCredential(password,ChatVaultKeyMode.Password);
        if(KeyMode==ChatVaultKeyMode.DirectKey)throw new InvalidOperationException("A direct data key cannot be changed by rewrapping a password.");
        PathPolicy.AssertNoLinks(root);using var lease=Lease(root);string marker=Path.Combine(root,Marker);
        if(ReadConfiguration(marker)!=configuration)throw new IOException("Vault credentials changed since opening. Reopen before changing the password.");
        if(NativeFile.ReadStamp(marker).Links!=1)throw new IOException("Shared vault configuration cannot be replaced.");
        var replacement=WrapKey(key,password) with{LegacyVersion=configuration.LegacyVersion??(configuration.Version is 1 or 2?configuration.Version:null)};JsonFiles.Write(marker,replacement);configuration=replacement;
    }
}
