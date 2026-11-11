using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Storloom.Core.Chats;

public sealed record ChatVaultProgress(int ProtectedFiles,int TotalFiles,long ProtectedBytes,long TotalBytes,bool Complete){public bool SelectionComplete{get;init;}public int SelectedFiles{get;init;}}

/// <summary>Password-protected, resumable encryption of a completed derived reading copy.</summary>
public sealed partial class ChatVault : IDisposable {
    public const string Marker="vault.storloom.json";
    private const int Chunk=1024*1024, HeaderSize=52, Iterations=600000;
    private static readonly byte[] Magic="SLVAULT1"u8.ToArray();
    private sealed record Configuration(int Version,string Salt,int Iterations);
    private sealed record Entry(string Path,string Id,FileStamp Stamp,bool Selected=true,string? PublicFile=null,FileStamp? PublicStamp=null);
    private sealed record Commit(string Id,string Sha256);
    private readonly string root,store;
    private readonly byte[] key;
    private Entry[] entries;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> committed=new(StringComparer.Ordinal);
    private Dictionary<string,Entry> paths;
    private readonly object gate=new();
    private bool disposed;
    public static bool Exists(string directory)=>File.Exists(Path.Combine(directory,Marker));
    private ChatVault(string directory,byte[] secret,Entry[] plan){root=directory;store=Path.Combine(root,".vault");key=secret;entries=plan;paths=entries.ToDictionary(e=>e.Path,StringComparer.Ordinal);}
    public static ChatVault Create(string directory,ReadOnlySpan<char> password,CancellationToken cancellation=default,IProgress<ScanProgress>? progress=null,ChatProtectionSelection? selection=null) => CreateCore(directory,password,cancellation,progress,selection,null,false);
    internal static ChatVault CreateUpdated(string directory,ReadOnlySpan<char> password,ChatVault previous,CancellationToken cancellation,IProgress<ScanProgress>? progress,ChatProtectionSelection? selection,bool reuseProtectedMedia=false) => CreateCore(directory,password,cancellation,progress,selection,previous,reuseProtectedMedia);
    private static ChatVault CreateCore(string directory,ReadOnlySpan<char> password,CancellationToken cancellation,IProgress<ScanProgress>? progress,ChatProtectionSelection? selection,ChatVault? previous,bool reuseProtectedMedia) {
        if(password.Length<8)throw new ArgumentException("Use a password of at least 8 characters.");
        directory=PathPolicy.Normalize(directory);PathPolicy.AssertNoLinks(directory);
        if(Exists(directory)||Directory.Exists(Path.Combine(directory,".vault")))throw new IOException("An encryption task already exists.");
        var report=JsonFiles.Read<ChatExportResult>(Path.Combine(directory,"export-report.json"));
        if(report.CanResume||File.Exists(Path.Combine(directory,".reading-update-pending.json")))throw new IOException("Finish the export or update before encrypting.");
        using var lease=Lease(directory);
        var plan=new List<Entry>();var clock=System.Diagnostics.Stopwatch.StartNew();long plannedBytes=0;double lastReport=0;
        progress?.Report(new("chat-encryption-plan",0,0));
        foreach(string file in EnumeratePlainFiles(directory)) {
            OperationPause.Check(cancellation);var stamp=NativeFile.ReadStamp(file);
            if(stamp.Links!=1)throw new IOException("Shared files cannot be encrypted in place.");
            string relative=Path.GetRelativePath(directory,file).Replace('\\','/');
            if(relative==".export.lock")continue;
            if(relative.Split('/').SkipLast(1).Any(p=>p.StartsWith(".work-",StringComparison.Ordinal)||p.StartsWith(".reading-update-",StringComparison.Ordinal)))throw new IOException("An unfinished task remains in this reading copy.");
            plan.Add(new(relative,Guid.NewGuid().ToString("N"),stamp));plannedBytes+=stamp.Bytes;
            if(clock.Elapsed.TotalSeconds-lastReport>=.5){progress?.Report(new("chat-encryption-plan",plan.Count,plannedBytes));lastReport=clock.Elapsed.TotalSeconds;}
        }
        progress?.Report(new("chat-encryption-derive",plan.Count,plannedBytes));
        byte[] salt=previous==null?RandomNumberGenerator.GetBytes(32):Convert.FromBase64String(JsonFiles.Read<Configuration>(Path.Combine(previous.root,Marker)).Salt),secret=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32);
        var vault=new ChatVault(directory,secret,plan.ToArray());
        try {
            progress?.Report(new("chat-encryption-save-plan",plan.Count,plannedBytes));
            Directory.CreateDirectory(vault.store);
            if(selection!=null){
                var selected=ChatProtectionPlanner.Build(directory,selection,progress,cancellation);
                vault.entries=vault.entries.Select(entry=>{
                    bool protect=entry.Path.StartsWith("assets/",StringComparison.Ordinal)?selected.Media.Contains(entry.Path):
                        System.Text.RegularExpressions.Regex.IsMatch(entry.Path,"^conversations/[a-f0-9]{24}-[0-9]+\\.js$")?selected.Pages.Contains(entry.Path):true;
                    string? publicFile=selected.PublicPages.GetValueOrDefault(entry.Path);
                    return entry with{Selected=protect,PublicFile=publicFile,PublicStamp=publicFile==null?null:NativeFile.ReadStamp(Path.Combine(vault.store,publicFile))};
                }).ToArray();vault.paths=vault.entries.ToDictionary(e=>e.Path,StringComparer.Ordinal);
            }
            if(previous!=null){vault.ReuseCiphertext(previous,progress,cancellation);if(reuseProtectedMedia)vault.ImportProtectedMedia(previous,progress,cancellation);}
            vault.WriteProtection(new(true,selection));
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(vault.entries);
            if(bytes.Length>256*1024*1024)throw new InvalidDataException("Too many files for this vault format.");
            try{WriteEnvelope(Path.Combine(vault.store,"plan.enc"),secret,bytes,"plan");}finally{CryptographicOperations.ZeroMemory(bytes);}
            JsonFiles.Write(Path.Combine(directory,Marker),new Configuration(selection==null?1:2,Convert.ToBase64String(salt),Iterations));
            return vault;
        }catch{
            vault.Dispose();
            // No originals are removed during preparation. Remove only this failed attempt's
            // newly created metadata/stages so cancellation or an empty filter can be retried.
            if(!Exists(directory)&&Directory.Exists(vault.store)){
                PathPolicy.AssertNoLinks(vault.store);
                var staged=Directory.GetFiles(vault.store);
                if(staged.All(p=>Path.GetFileName(p) is "plan.enc" or "commits.enc" or "protection.enc"||System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p),"^[a-f0-9]{32}\\.(public|enc)$"))){foreach(string file in staged){PathPolicy.AssertNoLinks(file);File.Delete(file);}Directory.Delete(vault.store);}
            }
            throw;
        }
    }
    public static ChatVault Open(string directory,ReadOnlySpan<char> password) {
        directory=PathPolicy.Normalize(directory);PathPolicy.AssertNoLinks(directory);
        string marker=Path.Combine(directory,Marker);PathPolicy.AssertNoLinks(marker);
        var config=JsonFiles.Read<Configuration>(marker);
        if(config.Version is not (1 or 2)||config.Iterations!=Iterations)throw new InvalidDataException("Unsupported vault format.");
        byte[] salt=Convert.FromBase64String(config.Salt);if(salt.Length!=32)throw new InvalidDataException("Invalid vault salt.");
        byte[] secret=Rfc2898DeriveBytes.Pbkdf2(password,salt,config.Iterations,HashAlgorithmName.SHA256,32);
        try {
            string store=Path.Combine(directory,".vault");PathPolicy.AssertNoLinks(store);
            byte[] bytes=ReadEnvelope(Path.Combine(store,"plan.enc"),secret,"plan");Entry[] plan;
            try{plan=JsonSerializer.Deserialize<Entry[]>(bytes)??throw new InvalidDataException("Invalid encryption plan.");}finally{CryptographicOperations.ZeroMemory(bytes);}
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var entry in plan){_=TransferService.Child(directory,entry.Path);if(!System.Text.RegularExpressions.Regex.IsMatch(entry.Id,"^[a-f0-9]{32}$")||!ids.Add(entry.Id)||entry.Stamp.Bytes<0)throw new InvalidDataException("Invalid vault entry.");}
            foreach(var entry in plan)if(entry.PublicFile!=null&&(!System.Text.RegularExpressions.Regex.IsMatch(entry.PublicFile,"^[a-f0-9]{32}\\.public$")||entry.PublicStamp==null))throw new InvalidDataException("Invalid public page entry.");
            var vault=new ChatVault(directory,secret,plan);vault.LoadCommits();return vault;
        }catch{CryptographicOperations.ZeroMemory(secret);throw;}
    }
    private static IEnumerable<string> EnumeratePlainFiles(string directory,string? excluded=null){
        var pending=new Stack<string>();pending.Push(directory);
        while(pending.TryPop(out string? folder)){
            PathPolicy.AssertNoLinks(folder);
            foreach(var item in new DirectoryInfo(folder).EnumerateFileSystemInfos()){
                if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked paths cannot be encrypted.");
                if((item.Attributes&FileAttributes.Directory)!=0){if(!item.FullName.Equals(excluded,StringComparison.OrdinalIgnoreCase))pending.Push(item.FullName);}else yield return item.FullName;
            }
        }
    }
    private static FileStream Lease(string root){string path=Path.Combine(root,".export.lock");if(File.Exists(path))PathPolicy.AssertNoLinks(path);return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
    private void LoadCommits() {
        string log=Path.Combine(store,"commits.enc");if(!File.Exists(log))return;PathPolicy.AssertNoLinks(log);
        using var input=File.OpenRead(log);using var reader=new BinaryReader(input);var ids=entries.Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
        while(input.Position<input.Length){if(input.Length-input.Position<4)break;int size=reader.ReadInt32();if(size<48||size>1024*1024)throw new InvalidDataException("Invalid encryption journal.");if(input.Length-input.Position<size)break;
            byte[] record=reader.ReadBytes(size),plain=Unseal(key,record,"commit");
            try{var commit=JsonSerializer.Deserialize<Commit>(plain)??throw new InvalidDataException("Invalid encryption commit.");if(!ids.Contains(commit.Id)||!System.Text.RegularExpressions.Regex.IsMatch(commit.Sha256,"^[A-F0-9]{64}$"))throw new InvalidDataException("Invalid encryption commit.");committed[commit.Id]=commit.Sha256;validLogLength=input.Position;}finally{CryptographicOperations.ZeroMemory(plain);}
        }
        // Only a final incomplete append is ignored; complete records must authenticate.
    }
    private long validLogLength;
    public ChatVaultProgress Status=>InspectStatus();
    private ChatVaultProgress InspectStatus(IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default){
        lock(gate){long done=0,total=0;int count=0,selected=0,selectedDone=0,scanned=0;
            foreach(var entry in entries){OperationPause.Check(cancellation);bool ready=Protected(entry);total+=entry.Stamp.Bytes;if(ready){count++;done+=entry.Stamp.Bytes;}if(entry.Selected){selected++;if(ready)selectedDone++;}
                if(++scanned%1024==0)progress?.Report(new("chat-encryption-check",scanned,0,TotalFiles:entries.Length));
            }
            var extras=UnplannedFiles().ToArray();return new(count,entries.Length+extras.Length,done,total+extras.Sum(p=>new FileInfo(p).Length),count==entries.Length&&extras.Length==0&&entries.All(e=>e.PublicFile==null)){SelectionComplete=extras.Length==0&&selectedDone==selected,SelectedFiles=selected};
        }
    }
    private bool Protected(Entry e)=>committed.ContainsKey(e.Id)&&File.Exists(Cipher(e))&&
        (e.PublicStamp==null?!File.Exists(Plain(e)):File.Exists(Plain(e))&&PublicMatches(e,Plain(e)));
    private IEnumerable<string> UnplannedFiles(){
        foreach(string path in EnumeratePlainFiles(root,store)){
            string relative=Path.GetRelativePath(root,path).Replace('\\','/');
            if(relative!=Marker&&relative!=".export.lock"&&!paths.ContainsKey(relative))yield return path;
        }
    }
    private string Plain(Entry entry)=>TransferService.Child(root,entry.Path);
    private string Cipher(Entry entry)=>Path.Combine(store,entry.Id+".enc");
    public ChatVaultProgress Encrypt(bool includeMedia=true,int workers=2,IProgress<ScanProgress>? progress=null,CancellationToken cancellation=default) {
        ObjectDisposedException.ThrowIf(disposed,this);if(workers is <1 or >8)throw new ArgumentOutOfRangeException(nameof(workers));
        using var lease=Lease(root);
        progress?.Report(new("chat-encryption-check",0,0));
        if(UnplannedFiles().Any())throw new IOException("Unplanned files were added after encryption started. They remain plaintext and must be retained separately before this plan can resume.");
        string log=Path.Combine(store,"commits.enc");if(File.Exists(log)){PathPolicy.AssertNoLinks(log);using var tail=new FileStream(log,FileMode.Open,FileAccess.Write,FileShare.Read);tail.SetLength(validLogLength);}
        WriteProtection(ReadProtection() with{IncludeMedia=includeMedia});
        var selected=entries.Where(e=>e.Selected&&(includeMedia||!e.Path.StartsWith("assets/",StringComparison.Ordinal))).ToArray();
        var initial=InspectStatus(progress,cancellation);long done=initial.ProtectedBytes,total=initial.TotalBytes;int files=initial.ProtectedFiles;
        Parallel.ForEach(selected,new ParallelOptions{MaxDegreeOfParallelism=workers,CancellationToken=cancellation},entry=>{
            OperationPause.Check(cancellation);string source=Plain(entry),cipher=Cipher(entry);
            if(committed.TryGetValue(entry.Id,out var expected)){
                if(!File.Exists(cipher))throw new IOException("A committed encrypted file is missing.");
                if(entry.PublicStamp!=null&&File.Exists(source)&&PublicMatches(entry,source))return;
                if(!File.Exists(source)){PublishPublic(entry);return;}
                using(var verify=OpenProtected(entry)){if(Convert.ToHexString(TransferService.Hash(verify,cancellation))!=expected)throw new IOException("Encrypted file changed before plaintext removal.");}
                RemovePlain(entry);PublishPublic(entry);return;
            }
            PathPolicy.AssertNoLinks(source);if(NativeFile.ReadStamp(source)!=entry.Stamp)throw new IOException("Reading copy changed before encryption.");
            using var original=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
            string digest=Protect(entry,original,cipher,cancellation,bytes=>{long value=Interlocked.Add(ref done,bytes);progress?.Report(new("chat-encrypt",Volatile.Read(ref files),value,total,entries.Length));});
            original.Dispose();
            if(NativeFile.ReadStamp(source)!=entry.Stamp)throw new IOException("Reading copy changed while encrypting.");
            using(var verify=OpenProtected(entry)){if(Convert.ToHexString(TransferService.Hash(verify,cancellation))!=digest)throw new CryptographicException("Encrypted file verification failed.");}
            OperationPause.Check(cancellation);
            lock(gate){byte[] plain=JsonSerializer.SerializeToUtf8Bytes(new Commit(entry.Id,digest)),record=Seal(key,plain,"commit");CryptographicOperations.ZeroMemory(plain);
                using var journal=new FileStream(log,FileMode.Append,FileAccess.Write,FileShare.Read);using var writer=new BinaryWriter(journal);writer.Write(record.Length);writer.Write(record);writer.Flush();journal.Flush(true);validLogLength=journal.Length;committed[entry.Id]=digest;}
            RemovePlain(entry);PublishPublic(entry);Interlocked.Increment(ref files);
        });
        return InspectStatus(progress,cancellation);
    }
    private void RemovePlain(Entry entry){string path=Plain(entry);if(!File.Exists(path))return;PathPolicy.AssertNoLinks(path);if(NativeFile.ReadStamp(path)!=entry.Stamp)throw new IOException("Plaintext changed; retained both versions.");NativeFile.DeleteVerified(new("chat-vault",root,path,entry.Stamp));}
    private static bool PublicMatches(Entry entry,string path){
        if(entry.PublicStamp==null)return false;var stamp=NativeFile.ReadStamp(path);
        // NTFS file tunnelling can restore the destination's creation time on rename.
        // Identity, volume, link count, write time and size must still match the staged file.
        return stamp with{CreationUtcTicks=entry.PublicStamp.CreationUtcTicks}==entry.PublicStamp;
    }
    private void PublishPublic(Entry entry){
        if(entry.PublicFile==null)return;string stage=Path.Combine(store,entry.PublicFile),target=Plain(entry);
        if(File.Exists(target)){if(PublicMatches(entry,target))return;throw new IOException("Public page changed; both versions retained.");}
        PathPolicy.AssertNoLinks(stage);if(!PublicMatches(entry,stage))throw new IOException("Public page changed before publication.");
        File.Move(stage,target);
    }
    private string Protect(Entry entry,FileStream input,string target,CancellationToken cancellation,Action<int> advanced) {
        byte[] header=new byte[HeaderSize];bool exists=File.Exists(target);
        if(exists)PathPolicy.AssertNoLinks(target);
        using var output=new FileStream(target,exists?FileMode.Open:FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);
        if(exists&&output.Length<HeaderSize){output.SetLength(0);exists=false;}
        if(exists){output.ReadExactly(header);ValidateHeader(header,entry.Stamp.Bytes);}else{Magic.CopyTo(header,0);BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8),Chunk);BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12),entry.Stamp.Bytes);RandomNumberGenerator.Fill(header.AsSpan(20));output.Write(header);output.Flush(true);}
        byte[] fileKey=HMACSHA256.HashData(key,header.AsSpan(20));byte[] buffer=new byte[Chunk],recovered=new byte[Chunk],cipher=new byte[Chunk],tag=new byte[16],nonce=new byte[12];
        using var aes=new AesGcm(fileKey,16);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long chunks=Math.Max(1,(entry.Stamp.Bytes+Chunk-1)/Chunk);
        try{for(long n=0;n<chunks;n++){
            OperationPause.Check(cancellation);int count=(int)Math.Min(Chunk,entry.Stamp.Bytes-input.Position);input.ReadExactly(buffer.AsSpan(0,count));hash.AppendData(buffer,0,count);
            byte[] aad=Aad(header,entry.Path,n);long start=output.Position;
            if(output.Length-start>=count+28){output.ReadExactly(nonce);output.ReadExactly(cipher.AsSpan(0,count));output.ReadExactly(tag);try{aes.Decrypt(nonce,cipher.AsSpan(0,count),tag,recovered.AsSpan(0,count),aad);if(!CryptographicOperations.FixedTimeEquals(recovered.AsSpan(0,count),buffer.AsSpan(0,count)))throw new IOException("Interrupted encryption source differs.");}finally{CryptographicOperations.ZeroMemory(recovered);}}
            else{output.SetLength(start);RandomNumberGenerator.Fill(nonce);aes.Encrypt(nonce,buffer.AsSpan(0,count),cipher.AsSpan(0,count),tag,aad);output.Write(nonce);output.Write(cipher.AsSpan(0,count));output.Write(tag);output.Flush(true);}
            advanced(count);
        }if(output.Position!=output.Length)throw new InvalidDataException("Unexpected encrypted file tail.");return Convert.ToHexString(hash.GetHashAndReset());}
        finally{CryptographicOperations.ZeroMemory(fileKey);CryptographicOperations.ZeroMemory(buffer);}
    }
    public Stream OpenRead(string relative) {
        ObjectDisposedException.ThrowIf(disposed,this);relative=relative.Replace('\\','/');if(!paths.TryGetValue(relative,out var entry))throw new FileNotFoundException("Unknown archive resource.");
        lock(gate){if(committed.ContainsKey(entry.Id))return OpenProtected(entry);}
        string source=Plain(entry);PathPolicy.AssertNoLinks(source);if(NativeFile.ReadStamp(source)!=entry.Stamp)throw new IOException("Unencrypted archive resource changed.");return new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
    }
    private Stream OpenProtected(Entry entry)=>new ProtectedStream(Cipher(entry),entry.Path,entry.Stamp.Bytes,key);
    private static byte[] Aad(byte[] header,string path,long index){byte[] name=Encoding.UTF8.GetBytes(path);var aad=new byte[header.Length+name.Length+8];header.CopyTo(aad,0);name.CopyTo(aad,header.Length);BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(aad.Length-8),index);return aad;}
    private static void ValidateHeader(byte[] header,long length){if(!header.AsSpan(0,8).SequenceEqual(Magic)||BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8))!=Chunk||BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12))!=length)throw new InvalidDataException("Invalid encrypted file header.");}
    private static byte[] Seal(byte[] key,ReadOnlySpan<byte> plain,string purpose){byte[] record=new byte[48+plain.Length];RandomNumberGenerator.Fill(record.AsSpan(0,32));byte[] derived=HMACSHA256.HashData(key,record.AsSpan(0,32));try{using var aes=new AesGcm(derived,16);aes.Encrypt(new byte[12],plain,record.AsSpan(48,plain.Length),record.AsSpan(32,16),Encoding.UTF8.GetBytes(purpose));return record;}finally{CryptographicOperations.ZeroMemory(derived);}}
    private static byte[] Unseal(byte[] key,byte[] record,string purpose){if(record.Length<48)throw new InvalidDataException("Invalid encrypted metadata.");int size=record.Length-48;byte[] plain=new byte[size],derived=HMACSHA256.HashData(key,record.AsSpan(0,32));try{using var aes=new AesGcm(derived,16);aes.Decrypt(new byte[12],record.AsSpan(48,size),record.AsSpan(32,16),plain,Encoding.UTF8.GetBytes(purpose));return plain;}catch{CryptographicOperations.ZeroMemory(plain);throw;}finally{CryptographicOperations.ZeroMemory(derived);}}
    private static void WriteEnvelope(string path,byte[] key,byte[] plain,string purpose){using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(Seal(key,plain,purpose));output.Flush(true);}
    private static byte[] ReadEnvelope(string path,byte[] key,string purpose){PathPolicy.AssertNoLinks(path);if(new FileInfo(path).Length>256*1024*1024)throw new InvalidDataException("Encrypted metadata is too large.");return Unseal(key,File.ReadAllBytes(path),purpose);}
    public void Dispose(){if(disposed)return;disposed=true;CryptographicOperations.ZeroMemory(key);}

    private sealed class ProtectedStream : Stream {
        private readonly FileStream input;private readonly byte[] header=new byte[HeaderSize],secret;private readonly string path;private readonly AesGcm aes;
        private readonly byte[] cache=new byte[Chunk],cipher=new byte[Chunk],nonce=new byte[12],tag=new byte[16];private long cached=-1,position;private readonly long length;
        public ProtectedStream(string file,string path,long length,byte[] key){PathPolicy.AssertNoLinks(file);this.path=path;this.length=length;input=new(file,FileMode.Open,FileAccess.Read,FileShare.Read);try{input.ReadExactly(header);ValidateHeader(header,length);if(input.Length!=HeaderSize+length+Math.Max(1,(length+Chunk-1)/Chunk)*28)throw new InvalidDataException("Truncated encrypted file.");secret=HMACSHA256.HashData(key,header.AsSpan(20));aes=new(secret,16);if(length==0){byte[] nonce=new byte[12],tag=new byte[16];input.ReadExactly(nonce);input.ReadExactly(tag);aes.Decrypt(nonce,Array.Empty<byte>(),tag,Span<byte>.Empty,Aad(header,path,0));}}catch{input.Dispose();throw;}}
        public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
        public override int Read(Span<byte> buffer){if(position>=length)return 0;int total=0;while(buffer.Length>0&&position<length){long n=position/Chunk;int size=(int)Math.Min(Chunk,length-n*Chunk);if(cached!=n){input.Position=HeaderSize+n*(Chunk+28L);input.ReadExactly(nonce);input.ReadExactly(cipher.AsSpan(0,size));input.ReadExactly(tag);aes.Decrypt(nonce,cipher.AsSpan(0,size),tag,cache.AsSpan(0,size),Aad(header,path,n));cached=n;}int offset=(int)(position%Chunk),take=Math.Min(buffer.Length,size-offset);cache.AsSpan(offset,take).CopyTo(buffer);buffer=buffer[take..];position+=take;total+=take;}return total;}
        public override long Seek(long offset,SeekOrigin origin){long next=origin switch{SeekOrigin.Begin=>offset,SeekOrigin.Current=>checked(position+offset),SeekOrigin.End=>checked(length+offset),_=>throw new ArgumentOutOfRangeException(nameof(origin))};if(next<0)throw new IOException("Invalid encrypted stream position.");return position=next;}
        public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>length;public override long Position{get=>position;set=>Seek(value,SeekOrigin.Begin);}public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing){input.Dispose();aes.Dispose();CryptographicOperations.ZeroMemory(secret);CryptographicOperations.ZeroMemory(cache);}base.Dispose(disposing);}
    }
}
