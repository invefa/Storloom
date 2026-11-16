using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Storloom.Core.Chats;
namespace Storloom.Core;

[Flags] public enum ConfigurationScope { None=0, Registrations=1, Preferences=2, ProtectedKeys=4 }
public sealed record ConfigurationItem(string Path, ConfigurationScope Scope, byte[] Content, string Sha256);
public sealed record ConfigurationSnapshot(int Version, DateTimeOffset CreatedAt, ConfigurationItem[] Items);
public sealed record ConfigurationChange(string Path, ConfigurationScope Scope, string Action, string? BeforeHash, byte[] Content);
public sealed record ConfigurationRestorePlan(string Root, ConfigurationChange[] Changes);
public sealed record ConfigurationRestoreResult(int Files, string RecoveryDirectory);

// A configuration package is data, never a general-purpose file archive or a job queue.
public static class ConfigurationBackup {
    private sealed record Envelope(int Version,bool Encrypted,byte[] Data,byte[]? Salt=null,byte[]? Nonce=null,byte[]? Tag=null);
    private sealed record KeyReference(string Path,string Account,ChatPlatform Platform);
    private const int MaximumBytes=32*1024*1024,Iterations=600000;
    private static readonly byte[] Aad=Encoding.UTF8.GetBytes("Storloom.Configuration.v1");
    private static readonly string[] Registrations=["chat-archives.json","archive-stores.json","connected-readers.json","chat-backups.json","account-aliases.json"];
    private static readonly HashSet<string> PreferenceFields=new(StringComparer.OrdinalIgnoreCase){"language","theme","downloadQQGroupAvatars","chatParallelism","chatWorkersAutomatic","backupRoot","archiveRoot","chatWorkRoot","chatSearchRoots","chatAutoDiscover","reduceMotion","logHeight","logExpanded","logAutoShow","logAutoCollapse","logWrap","sidebarWidth","sidebarCollapsed","closeToTray","debugLogging"};
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Digest(string value)=>value.Length==64&&value.All(c=>char.IsAsciiHexDigit(c));
    private static ConfigurationScope Category(string path)=>Registrations.Contains(path)?ConfigurationScope.Registrations:
        path=="preferences.json"||path.StartsWith("reader-preferences/",StringComparison.Ordinal)&&path.EndsWith(".json",StringComparison.Ordinal)&&Digest(path[19..^5])?ConfigurationScope.Preferences:
        path=="database-key-library.json"||path.StartsWith("imported-keys/",StringComparison.Ordinal)&&path.EndsWith(".json",StringComparison.Ordinal)&&Digest(path[14..^5])?ConfigurationScope.ProtectedKeys:ConfigurationScope.None;
    private static byte[] Read(string path){PathPolicy.AssertNoLinks(path);if(new FileInfo(path).Length>MaximumBytes)throw new InvalidDataException("Configuration file is too large.");return File.ReadAllBytes(path);}
    private static JsonNode Parse(byte[] bytes)=>JsonNode.Parse(bytes,new JsonNodeOptions(),new JsonDocumentOptions{MaxDepth=32})??throw new InvalidDataException("Empty configuration.");
    private static byte[] Json(JsonNode value)=>JsonSerializer.SerializeToUtf8Bytes(value,JsonFiles.Options);
    private static JsonObject Preferences(byte[] bytes){var input=Parse(bytes).AsObject();return new(input.Where(p=>PreferenceFields.Contains(p.Key)).Select(p=>new KeyValuePair<string,JsonNode?>(p.Key,p.Value?.DeepClone())));}
    private static byte[] ReaderPreferences(byte[] bytes){var input=Parse(bytes).AsObject();var result=new JsonObject();foreach(var name in new[]{"palette","paletteExplicit","dark","updatedAt"})if(input.TryGetPropertyValue(name,out var value))result[name]=value?.DeepClone();if(input["view"] is JsonObject view){var filtered=new JsonObject();foreach(var name in new[]{"autoBottom","autoLast","sidebarCollapsed","minimap","pageSize","sidebarWidth","previewWidth","previewHeight"})if(view.TryGetPropertyValue(name,out var value))filtered[name]=value?.DeepClone();result["view"]=filtered;}return Json(result);}
    private static void ValidateKey(byte[] bytes){var key=JsonSerializer.Deserialize<ChatKeyFile.Envelope>(bytes,JsonFiles.Options)??throw new InvalidDataException("Invalid protected key file.");if(key.Version!=1||!Enum.IsDefined(key.Platform)||key.Salt.Length!=16||key.Nonce.Length!=12||key.Tag.Length!=16||key.Ciphertext.Length is not (16 or 32))throw new InvalidDataException("Only protected key envelopes can be packaged.");}
    public static ConfigurationSnapshot Capture(string root,ConfigurationScope scope,CancellationToken cancellation=default){
        root=PathPolicy.Normalize(root);PathPolicy.AssertNoLinks(root);var items=new List<ConfigurationItem>();
        void Add(string path,byte[] bytes){cancellation.ThrowIfCancellationRequested();if(Category(path)==ConfigurationScope.None)throw new InvalidDataException("Unsupported configuration entry.");items.Add(new(path,Category(path),bytes,Hash(bytes)));}
        if(scope.HasFlag(ConfigurationScope.Registrations))foreach(string name in Registrations)if(File.Exists(Path.Combine(root,name))){
            byte[] bytes=Read(Path.Combine(root,name));if(name=="chat-archives.json"){var projects=JsonSerializer.Deserialize<ChatArchiveProject[]>(bytes,JsonFiles.Options)??throw new InvalidDataException("Invalid projects.");bytes=JsonSerializer.SerializeToUtf8Bytes(projects.Select(p=>p with{TaskFile=null}).ToArray(),JsonFiles.Options);}Add(name,bytes);
        }
        if(scope.HasFlag(ConfigurationScope.Preferences)){
            if(File.Exists(Path.Combine(root,"preferences.json")))Add("preferences.json",Json(Preferences(Read(Path.Combine(root,"preferences.json")))));
            string directory=Path.Combine(root,"reader-preferences");if(Directory.Exists(directory)){PathPolicy.AssertNoLinks(directory);foreach(string path in Directory.EnumerateFiles(directory,"*.json")){string relative="reader-preferences/"+Path.GetFileName(path);if(Category(relative)!=ConfigurationScope.None)Add(relative,ReaderPreferences(Read(path)));}}
        }
        if(scope.HasFlag(ConfigurationScope.ProtectedKeys)&&File.Exists(Path.Combine(root,"database-key-library.json"))){
            var library=JsonSerializer.Deserialize<KeyReference[]>(Read(Path.Combine(root,"database-key-library.json")),JsonFiles.Options)??throw new InvalidDataException("Invalid key library.");var portable=new List<KeyReference>();
            foreach(var entry in library){byte[] bytes=Read(entry.Path);ValidateKey(bytes);string path="imported-keys/"+Hash(bytes)+".json";if(!items.Any(item=>item.Path==path))Add(path,bytes);portable.Add(entry with{Path=path});}Add("database-key-library.json",JsonSerializer.SerializeToUtf8Bytes(portable,JsonFiles.Options));
        }
        var snapshot=new ConfigurationSnapshot(1,DateTimeOffset.UtcNow,items.ToArray());Validate(snapshot);return snapshot;
    }
    private static void Validate(ConfigurationSnapshot snapshot){
        if(snapshot.Version!=1||snapshot.CreatedAt==default||snapshot.Items==null||snapshot.Items.Length>4096)throw new InvalidDataException("Unsupported configuration package.");
        long total=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in snapshot.Items){if(item==null||Category(item.Path)==ConfigurationScope.None||Category(item.Path)!=item.Scope||!seen.Add(item.Path)||item.Content==null||Hash(item.Content)!=item.Sha256)throw new InvalidDataException("Invalid configuration package entry.");total+=item.Content.Length;if(total>MaximumBytes)throw new InvalidDataException("Configuration package is too large.");_=Parse(item.Content);if(item.Path.StartsWith("imported-keys/",StringComparison.Ordinal))ValidateKey(item.Content);
            if(item.Path=="chat-archives.json"){var projects=JsonSerializer.Deserialize<ChatArchiveProject[]>(item.Content,JsonFiles.Options)??throw new InvalidDataException("Invalid projects.");if(projects.Any(p=>p.TaskFile!=null||!Enum.IsDefined(p.Platform)||!Enum.IsDefined(p.Protection)||!Enum.IsDefined(p.ReadingStorage)||!Enum.IsDefined(p.BackupStorage)||string.IsNullOrWhiteSpace(p.Id)||string.IsNullOrWhiteSpace(p.Account)||!Location(p.Source)||p.Reading!=null&&!Location(p.Reading)||p.NativeArchive!=null&&!Location(p.NativeArchive)||p.Manifest!=null&&!Location(p.Manifest)))throw new InvalidDataException("Invalid source registration or executable task state.");}
            if(item.Path=="connected-readers.json"){var paths=JsonSerializer.Deserialize<string[]>(item.Content,JsonFiles.Options)??throw new InvalidDataException("Invalid reader registrations.");if(paths.Any(p=>!Location(p)))throw new InvalidDataException("Invalid reader location.");}
            if(item.Path=="archive-stores.json")foreach(var entry in Parse(item.Content).AsArray())if(entry==null||!Location(entry["directory"]?.GetValue<string>())||entry["complete"]?.GetValueKind() is not JsonValueKind.True and not JsonValueKind.False)throw new InvalidDataException("Invalid compact registration.");
            if(item.Path=="account-aliases.json"){var aliases=Parse(item.Content).AsObject();foreach(string field in new[]{"accounts","sources"}){var values=aliases[field]?.AsObject()??throw new InvalidDataException("Invalid alias configuration.");if(values.Any(p=>p.Value?.GetValue<string>() is not {} text||text.Length>80||text.Any(char.IsControl)))throw new InvalidDataException("Invalid alias configuration.");}}
            if(item.Path=="chat-backups.json"){var backups=JsonSerializer.Deserialize<Dictionary<string,string>>(item.Content,JsonFiles.Options)??throw new InvalidDataException("Invalid backup registrations.");if(backups.Any(p=>!Location(p.Key)||!Location(p.Value)))throw new InvalidDataException("Invalid backup location.");}
        }
        var library=snapshot.Items.SingleOrDefault(i=>i.Path=="database-key-library.json");if(library!=null){var entries=JsonSerializer.Deserialize<KeyReference[]>(library.Content,JsonFiles.Options)??throw new InvalidDataException("Invalid key library.");foreach(var key in entries)if(!Enum.IsDefined(key.Platform)||Category(key.Path)!=ConfigurationScope.ProtectedKeys||!key.Path.StartsWith("imported-keys/",StringComparison.Ordinal)||!snapshot.Items.Any(i=>i.Path==key.Path))throw new InvalidDataException("Missing portable protected key.");}
    }
    private static bool Location(string? value)=>value!=null&&value.Length<=32767&&!value.Any(char.IsControl)&&Path.IsPathFullyQualified(value);
    public static void Save(string file,ConfigurationSnapshot snapshot,ReadOnlySpan<char> password=default,CancellationToken cancellation=default){
        cancellation.ThrowIfCancellationRequested();
        Validate(snapshot);bool encrypted=!password.IsEmpty;if(snapshot.Items.Any(i=>i.Scope==ConfigurationScope.ProtectedKeys)&&!encrypted)throw new InvalidOperationException("Protected keys require an encrypted configuration package.");
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(snapshot,JsonFiles.Options);Envelope envelope;
        try{if(encrypted){if(password.Length<8||password.Length>65536)throw new ArgumentException("Use at least 8 password characters.");byte[] salt=RandomNumberGenerator.GetBytes(32),nonce=RandomNumberGenerator.GetBytes(12),tag=new byte[16],cipher=new byte[plain.Length],key=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32);try{using var aes=new AesGcm(key,16);aes.Encrypt(nonce,plain,cipher,tag,Aad);envelope=new(1,true,cipher,salt,nonce,tag);}finally{CryptographicOperations.ZeroMemory(key);}}else envelope=new(1,false,plain);
            byte[] packet=JsonSerializer.SerializeToUtf8Bytes(envelope,JsonFiles.Options);if(packet.Length>MaximumBytes)throw new InvalidDataException("Configuration package is too large.");
            cancellation.ThrowIfCancellationRequested();string parent=Path.GetDirectoryName(Path.GetFullPath(file))!;PathPolicy.AssertNoLinks(parent);if(File.Exists(file))throw new IOException("Choose a new configuration package file.");string temporary=file+"."+Guid.NewGuid().ToString("N")+".partial";
            try{using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(packet);output.Flush(true);}cancellation.ThrowIfCancellationRequested();File.Move(temporary,file,false);}finally{if(File.Exists(temporary))File.Delete(temporary);}
        }finally{CryptographicOperations.ZeroMemory(plain);}
    }
    private static Envelope ReadEnvelope(string file){var envelope=JsonSerializer.Deserialize<Envelope>(Read(file),JsonFiles.Options)??throw new InvalidDataException("Invalid configuration package.");if(envelope.Version!=1||envelope.Data==null||envelope.Data.Length>MaximumBytes)throw new InvalidDataException("Unsupported configuration package.");return envelope;}
    public static bool IsEncrypted(string file)=>ReadEnvelope(file).Encrypted;
    public static ConfigurationSnapshot Open(string file,ReadOnlySpan<char> password=default){
        var envelope=ReadEnvelope(file);byte[] plain;
        if(envelope.Encrypted){if(envelope.Salt?.Length!=32||envelope.Nonce?.Length!=12||envelope.Tag?.Length!=16)throw new InvalidDataException("Invalid encrypted configuration package.");byte[] key=Rfc2898DeriveBytes.Pbkdf2(password,envelope.Salt,Iterations,HashAlgorithmName.SHA256,32);plain=new byte[envelope.Data.Length];try{using var aes=new AesGcm(key,16);aes.Decrypt(envelope.Nonce,envelope.Data,envelope.Tag,plain,Aad);}catch{CryptographicOperations.ZeroMemory(plain);throw;}finally{CryptographicOperations.ZeroMemory(key);}}
        else{if(envelope.Salt!=null||envelope.Nonce!=null||envelope.Tag!=null)throw new InvalidDataException("Invalid plain configuration package.");plain=envelope.Data;}
        try{var snapshot=JsonSerializer.Deserialize<ConfigurationSnapshot>(plain,JsonFiles.Options)??throw new InvalidDataException("Invalid configuration snapshot.");Validate(snapshot);if(!envelope.Encrypted&&snapshot.Items.Any(i=>i.Scope==ConfigurationScope.ProtectedKeys))throw new InvalidDataException("Key packages must be encrypted.");return snapshot;}finally{CryptographicOperations.ZeroMemory(plain);}
    }
    private static byte[] Merge(string path,byte[] incoming,byte[]? current,string root,bool replace){
        JsonNode next=path=="preferences.json"?Preferences(incoming):path.StartsWith("reader-preferences/",StringComparison.Ordinal)?Parse(ReaderPreferences(incoming)):Parse(incoming);JsonNode? old=current==null?null:Parse(current);
        if(path=="database-key-library.json")foreach(var entry in next.AsArray())entry!["path"]=Path.Combine(root,entry["path"]!.GetValue<string>().Replace('/',Path.DirectorySeparatorChar));
        if(path=="preferences.json"){var combined=old?.AsObject()??new JsonObject();foreach(var pair in next.AsObject())combined[pair.Key]=pair.Value?.DeepClone();return Json(combined);}
        if(path is "chat-archives.json" or "archive-stores.json" or "connected-readers.json" or "database-key-library.json"){
            var result=old?.AsArray()??new JsonArray();string Identity(JsonNode? item)=>path=="connected-readers.json"?item!.GetValue<string>():item![path=="chat-archives.json"?"id":path=="archive-stores.json"?"directory":"path"]!.GetValue<string>();
            var known=result.Select(Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);foreach(var item in next.AsArray()){string id=Identity(item);if(known.Add(id))result.Add(item?.DeepClone());else if(replace&&path is "chat-archives.json" or "archive-stores.json"){int position=Enumerable.Range(0,result.Count).First(i=>Identity(result[i]).Equals(id,StringComparison.OrdinalIgnoreCase));result[position]=item?.DeepClone();}}return Json(result);
        }
        if(path is "chat-backups.json" or "account-aliases.json"){
            var result=old?.AsObject()??new JsonObject();foreach(var pair in next.AsObject()){if(path=="account-aliases.json"&&pair.Value is JsonObject dictionary){var group=result[pair.Key]?.AsObject()??new JsonObject();foreach(var field in dictionary)if(replace||!group.ContainsKey(field.Key))group[field.Key]=field.Value?.DeepClone();if(result[pair.Key]==null)result[pair.Key]=group;}else if(replace||!result.ContainsKey(pair.Key))result[pair.Key]=pair.Value?.DeepClone();}return Json(result);
        }
        if(path.StartsWith("imported-keys/",StringComparison.Ordinal)&&current!=null&&Hash(current)!=Hash(incoming))throw new IOException("A protected key destination already contains different data.");
        if(path.StartsWith("imported-keys/",StringComparison.Ordinal))return current??incoming.ToArray();
        if(path.StartsWith("reader-preferences/",StringComparison.Ordinal))next["updatedAt"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Json(next);
    }
    private static string Target(string root,string relative){if(Category(relative)==ConfigurationScope.None)throw new InvalidDataException("Unexpected restore path.");string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));if(!PathPolicy.Under(path,root))throw new IOException("Restore path escapes configuration directory.");for(string? parent=Path.GetDirectoryName(path);parent!=null&&PathPolicy.Under(parent,root);parent=Path.GetDirectoryName(parent))if(Directory.Exists(parent))PathPolicy.AssertNoLinks(parent);if(File.Exists(path)){PathPolicy.AssertNoLinks(path);if(NativeFile.ReadStamp(path).Links!=1)throw new IOException("Shared configuration cannot be replaced.");}return path;}
    public static ConfigurationRestorePlan Preview(string root,ConfigurationSnapshot snapshot,ConfigurationScope scope,bool replaceRegistrations=false){
        Validate(snapshot);root=PathPolicy.Normalize(root);PathPolicy.AssertNoLinks(root);var changes=new List<ConfigurationChange>();
        foreach(var item in snapshot.Items.Where(i=>scope.HasFlag(i.Scope))){string target=Target(root,item.Path);byte[]? old=File.Exists(target)?Read(target):null;byte[] next=Merge(item.Path,item.Content,old,root,replaceRegistrations);if(old!=null&&old.SequenceEqual(next))continue;changes.Add(new(item.Path,item.Scope,old==null?"add":item.Scope==ConfigurationScope.Preferences?"preferences":replaceRegistrations&&item.Scope==ConfigurationScope.Registrations?"update":"merge",old==null?null:Hash(old),next));}
        return new(root,changes.ToArray());
    }
    public static ConfigurationRestoreResult Restore(ConfigurationRestorePlan plan,CancellationToken cancellation=default){
        string root=PathPolicy.Normalize(plan.Root);PathPolicy.AssertNoLinks(root);var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var before=new Dictionary<string,byte[]?>();
        foreach(var change in plan.Changes){cancellation.ThrowIfCancellationRequested();if(!seen.Add(change.Path)||Category(change.Path)!=change.Scope||change.Content.Length>MaximumBytes)throw new InvalidDataException("Invalid restore plan.");_=Parse(change.Content);string target=Target(root,change.Path);byte[]? bytes=File.Exists(target)?Read(target):null;if((bytes==null?null:Hash(bytes))!=change.BeforeHash)throw new IOException("Configuration changed after preview. Review a new restore plan.");before[target]=bytes;}
        string recovery=Path.Combine(root,"configuration-recovery");if(Directory.Exists(recovery))PathPolicy.AssertNoLinks(recovery);recovery=Path.Combine(recovery,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(recovery);PathPolicy.AssertNoLinks(recovery);
        foreach(var (target,bytes) in before)if(bytes!=null){string backup=Path.Combine(recovery,Path.GetRelativePath(root,target));Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.WriteAllBytes(backup,bytes);}
        JsonFiles.Write(Path.Combine(recovery,"restore-plan.json"),plan.Changes.Select(c=>new{c.Path,c.Action,c.BeforeHash}).ToArray());var written=new List<string>();
        try{foreach(var change in plan.Changes){cancellation.ThrowIfCancellationRequested();string target=Target(root,change.Path);byte[]? current=File.Exists(target)?Read(target):null;if((current==null?null:Hash(current))!=change.BeforeHash)throw new IOException("Configuration changed during restore.");Directory.CreateDirectory(Path.GetDirectoryName(target)!);string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(change.Content);stream.Flush(true);}File.Move(temporary,target,true);written.Add(target);}finally{if(File.Exists(temporary))File.Delete(temporary);}}
            return new(written.Count,recovery);
        }catch{foreach(string target in written.AsEnumerable().Reverse()){Target(root,Path.GetRelativePath(root,target).Replace('\\','/'));if(before[target] is {} bytes)File.WriteAllBytes(target,bytes);else File.Delete(target);}throw;}
    }
}
