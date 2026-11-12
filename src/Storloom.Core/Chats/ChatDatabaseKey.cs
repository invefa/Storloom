using System.Security.Cryptography;
using System.Text;

namespace Storloom.Core.Chats;

/// <summary>Text interchange for explicitly imported or exported database keys.</summary>
public static class ChatDatabaseKey {
    public static byte[] Parse(string text, ChatPlatform platform) {
        // Printable spaces can be part of a key. Strip file line endings, never key spaces.
        text=text.TrimEnd('\r','\n');
        if(platform==ChatPlatform.QQ && text.Length is 16 or 32 && text.All(c=>c is >= ' ' and <= '~'))
            return Encoding.ASCII.GetBytes(text);
        if(platform==ChatPlatform.WeChat && text.Length==64 && text.All(Uri.IsHexDigit))
            return Convert.FromHexString(text);
        throw new FormatException("Invalid database key format.");
    }
    public static string Format(ReadOnlySpan<byte> key, ChatPlatform platform)=>
        platform==ChatPlatform.QQ?Encoding.ASCII.GetString(key):Convert.ToHexString(key);
    public static bool Verify(string database, ReadOnlySpan<byte> key, ChatPlatform platform) {
        using var input=new FileStream(database,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        byte[] prefix=new byte[Math.Min(input.Length,5120)];input.ReadExactly(prefix);
        try{return ChatCipher.Detect(prefix,key,platform)!=null;}
        finally{CryptographicOperations.ZeroMemory(prefix);}
    }
}
