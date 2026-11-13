using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Storloom.Core.Chats;

// Rebuildable projection, never the recovery authority.
// Protected archives must pass a binary query key; no plaintext fallback is allowed.
internal sealed class ArchiveQueryCache:IDisposable {
    internal sealed record Message(string Id,string Conversation,long Timestamp,string Text,string Json);
    private readonly ArchiveSqlite index;
    // Fresh projections can reject missing IDs without a random B-tree read per
    // message. False positives only cause a normal lookup; rollback is harmless.
    private readonly System.Collections.BitArray? freshIds;
    internal int Workers{get;set;}=2;
    private static (int A,int B) IdBits(string id){var hash=System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id));return ((int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash)&0x3ffffff),(int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(4))&0x3ffffff));}
    private bool MayContain(string id){if(freshIds==null)return true;var (a,b)=IdBits(id);return freshIds[a]&&freshIds[b];}
    private void Remember(string id){if(freshIds==null)return;var (a,b)=IdBits(id);freshIds[a]=freshIds[b]=true;}
    internal string DatabasePath{get;}
    internal void EnableWriteAhead()=>index.EnableWriteAhead();
    internal void Checkpoint()=>index.Checkpoint();
    internal ArchiveQueryCache(string directory,bool protectedArchive,byte[]? key=null){
        PathPolicy.AssertNoLinks(directory);if(protectedArchive&&key==null)throw new IOException("Protected archive query cache requires an encryption key.");
        string path=Path.Combine(directory,"query.db");DatabasePath=path;if(File.Exists(path))PathPolicy.AssertNoLinks(path);
        index=new ArchiveSqlite(path,key);
        try{
            index.Execute("CREATE TABLE IF NOT EXISTS projection(k TEXT PRIMARY KEY,v TEXT NOT NULL) WITHOUT ROWID");
            index.Execute("CREATE TABLE IF NOT EXISTS current_messages(id TEXT NOT NULL UNIQUE,conversation TEXT NOT NULL,time INTEGER NOT NULL,text TEXT NOT NULL,body TEXT NOT NULL)");
            index.Execute("CREATE INDEX IF NOT EXISTS timeline ON current_messages(conversation,time,id)");
            index.Execute("CREATE TABLE IF NOT EXISTS message_references(conversation TEXT NOT NULL,reference TEXT NOT NULL,message_id TEXT NOT NULL,PRIMARY KEY(conversation,reference,message_id)) WITHOUT ROWID");
            index.Execute("CREATE INDEX IF NOT EXISTS reference_message ON message_references(message_id)");
            index.Execute("CREATE TABLE IF NOT EXISTS archive_objects(id TEXT PRIMARY KEY,commit_id TEXT NOT NULL,ordinal INTEGER NOT NULL,bytes INTEGER NOT NULL) WITHOUT ROWID");
            index.Execute("CREATE TABLE IF NOT EXISTS source_assets(stamp TEXT PRIMARY KEY,path TEXT NOT NULL) WITHOUT ROWID");
            index.Execute("CREATE TABLE IF NOT EXISTS archive_files(path TEXT PRIMARY KEY,body TEXT NOT NULL) WITHOUT ROWID");
            index.Execute("CREATE TABLE IF NOT EXISTS archive_file_parts(version TEXT NOT NULL,ordinal INTEGER NOT NULL,object_id TEXT NOT NULL,PRIMARY KEY(version,ordinal)) WITHOUT ROWID");
            // Caller-generated Unicode tokens allow single-character and mixed-script queries.
            // The index stores postings only; display data remains in current_messages.
            index.Execute("CREATE VIRTUAL TABLE IF NOT EXISTS message_search USING fts5(tokens,content='',detail=none,columnsize=0)");
            index.Execute("CREATE TABLE IF NOT EXISTS browser_conversations(conversation TEXT PRIMARY KEY,count INTEGER NOT NULL,name TEXT NOT NULL) WITHOUT ROWID");
            if(index.Query("SELECT 1 FROM current_messages LIMIT 1").Count==0)freshIds=new(1<<26);
        }catch{index.Dispose();throw;}
    }
    internal bool BrowserStatisticsReady=>index.Query("SELECT v FROM projection WHERE k='browser-statistics'").Count!=0;
    internal string? BrowserConversationName(string key)=>BrowserStatisticsReady?index.Query("SELECT name FROM browser_conversations WHERE conversation=?",key).SingleOrDefault()?[0]:null;
    private static string DisplayName(Message message){using var document=JsonDocument.Parse(message.Json);return document.RootElement.TryGetProperty("ConversationName",out var name)&&name.ValueKind==JsonValueKind.String?name.GetString()??"":"";}
    internal void EnableBrowserStatistics(IProgress<ScanProgress>? progress,CancellationToken cancellation){
        if(BrowserStatisticsReady)return;using var interrupt=index.InterruptOnCancellation(cancellation);progress?.Report(new("browser-statistics",0,0));index.Execute("BEGIN");
        try{index.Execute("DELETE FROM browser_conversations");index.Execute("INSERT INTO browser_conversations SELECT conversation,count(*),'' FROM current_messages GROUP BY conversation");var conversations=index.Query("SELECT conversation FROM browser_conversations ORDER BY conversation");int done=0;
            foreach(var row in conversations){OperationPause.Check(cancellation);var sample=Page(row[0],1).First();index.Execute("UPDATE browser_conversations SET name=? WHERE conversation=?",DisplayName(sample),row[0]);progress?.Report(new("browser-statistics",++done,0,TotalFiles:conversations.Count));}
            index.Execute("INSERT OR REPLACE INTO projection VALUES('browser-statistics','1')");index.Execute("COMMIT");
        }catch{try{index.Execute("ROLLBACK");}catch(Microsoft.Data.Sqlite.SqliteException){}cancellation.ThrowIfCancellationRequested();throw;}
    }
    internal string? Applied=>index.Query("SELECT v FROM projection WHERE k='applied'").SingleOrDefault()?[0];
    internal ArchiveImportCursor? Cursor=>index.Query("SELECT v FROM projection WHERE k='import'").Select(r=>JsonSerializer.Deserialize<ArchiveImportCursor>(r[0])).SingleOrDefault();
    internal string? AssetFor(FileStamp stamp)=>index.Query("SELECT path FROM source_assets WHERE stamp=?",JsonFiles.Hash(stamp)).SingleOrDefault()?[0];
    internal long MessageCount=>long.Parse(index.Query("SELECT count(*) FROM current_messages").Single()[0],CultureInfo.InvariantCulture);
    internal int AssetCount=>int.Parse(index.Query("SELECT count(*) FROM archive_files WHERE path>='assets/' AND path<'assets0'").Single()[0],CultureInfo.InvariantCulture);
    internal bool HasObject(string id)=>index.Query("SELECT 1 FROM archive_objects WHERE id=?",id).Count!=0;
    internal Message? Find(string id)=>!MayContain(id)?null:index.Query("SELECT body FROM current_messages WHERE id=?",id).Select(r=>Read(r[0])).SingleOrDefault();
    internal IReadOnlyList<(string Key,long Count)> Conversations()=>index.Query(BrowserStatisticsReady?"SELECT conversation,count FROM browser_conversations ORDER BY conversation":"SELECT conversation,count(*) FROM current_messages GROUP BY conversation ORDER BY conversation").Select(r=>(r[0],long.Parse(r[1],CultureInfo.InvariantCulture))).ToArray();
    internal IReadOnlyList<Message> PageAt(string conversation,int page,int size=300){
        if(page<0||size is <1 or >10000)throw new ArgumentOutOfRangeException(nameof(page));
        return index.Query("SELECT body FROM current_messages WHERE conversation=? ORDER BY time,id LIMIT CAST(? AS INTEGER) OFFSET CAST(? AS INTEGER)",conversation,size.ToString(CultureInfo.InvariantCulture),checked((long)page*size).ToString(CultureInfo.InvariantCulture)).Select(r=>Read(r[0])).ToArray();
    }
    internal long Rank(Message message)=>long.Parse(index.Query("SELECT count(*) FROM current_messages WHERE conversation=? AND (time,id)<(CAST(? AS INTEGER),?)",message.Conversation,message.Timestamp.ToString(CultureInfo.InvariantCulture),message.Id).Single()[0],CultureInfo.InvariantCulture);
    internal Message? Reference(string conversation,string reference)=>index.Query("SELECT m.body FROM message_references r JOIN current_messages m ON m.id=r.message_id WHERE r.conversation=? AND r.reference=? ORDER BY m.time DESC,m.id DESC LIMIT 1",conversation,reference).Select(r=>Read(r[0])).FirstOrDefault();
    internal IReadOnlyList<ArchiveFileEntry> Files()=>index.Query("SELECT body FROM archive_files ORDER BY path").Select(r=>JsonSerializer.Deserialize<ArchiveFileEntry>(r[0])!).ToArray();
    internal IReadOnlyList<ArchiveFileEntry> NativeSnapshotFiles()=>index.Query("SELECT body FROM archive_files WHERE path>='native-snapshots/' AND path<'native-snapshots0' ORDER BY path").Select(r=>JsonSerializer.Deserialize<ArchiveFileEntry>(r[0])!).ToArray();
    internal ArchiveFileEntry? FileEntry(string path)=>index.Query("SELECT body FROM archive_files WHERE path=?",path).Select(r=>JsonSerializer.Deserialize<ArchiveFileEntry>(r[0])).SingleOrDefault();
    internal (string Commit,int Ordinal,long Bytes) Locate(string id){var row=index.Query("SELECT commit_id,ordinal,bytes FROM archive_objects WHERE id=?",id).SingleOrDefault()??throw new FileNotFoundException("Missing archive object.");return(row[0],int.Parse(row[1],CultureInfo.InvariantCulture),long.Parse(row[2],CultureInfo.InvariantCulture));}
    internal IReadOnlyList<string> FileParts(string version)=>index.Query("SELECT object_id FROM archive_file_parts WHERE version=? ORDER BY ordinal",version).Select(r=>r[0]).ToArray();
    private static string Normalize(string text)=>text.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static string Token(ReadOnlySpan<Rune> runes){var text=new StringBuilder("u");foreach(var rune in runes)text.Append(rune.Value.ToString("x",CultureInfo.InvariantCulture)).Append('x');return text.ToString();}
    private static string Tokens(string text){
        var runes=Normalize(text).EnumerateRunes().ToArray();var tokens=new HashSet<string>(StringComparer.Ordinal);
        // Unigrams support one-character searches; trigrams narrow longer substring searches.
        for(int n=0;n<runes.Length;n++){tokens.Add(Token(runes.AsSpan(n,1)));if(n+3<=runes.Length)tokens.Add(Token(runes.AsSpan(n,3)));}
        return string.Join(' ',tokens.Order(StringComparer.Ordinal));
    }
    private static Message Read(string text){if(!text.StartsWith('!'))return JsonSerializer.Deserialize<Message>(text)??throw new InvalidDataException("Invalid archive message delta.");byte[] plain=ArchivePackCodec.Expand(Convert.FromBase64String(text[1..]));try{return JsonSerializer.Deserialize<Message>(plain)??throw new InvalidDataException("Invalid compressed query row.");}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);}}
    private static byte[] Body(Message message){byte[] plain=JsonSerializer.SerializeToUtf8Bytes(message);try{return ArchivePackCodec.Compress(plain);}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);}}
    internal void Validate(ArchiveDelta batch){
        if(batch.Version!=1||batch.Messages==null||batch.Objects==null||batch.Parts==null||batch.Files==null)throw new InvalidDataException("Unsupported archive delta.");
        foreach(var message in batch.Messages){if(message==null||string.IsNullOrEmpty(message.Id)||message.Conversation==null||message.Text==null||message.Json==null)throw new InvalidDataException("Invalid archive message.");_ = Normalize(message.Text);using var raw=JsonDocument.Parse(message.Json);}
        foreach(var item in batch.Objects)if(item==null||!ValidId(item.Id)||item.Bytes==null||item.Length is <1 or >4*1024*1024)throw new InvalidDataException("Invalid resource chunk.");
        var known=batch.Objects.Select(o=>o.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var part in batch.Parts)if(part==null||!ValidId(part.Version)||!ValidId(part.Object)||part.Ordinal<0||!known.Contains(part.Object)&&!HasObject(part.Object))throw new InvalidDataException("Missing resource chunk reference.");
        foreach(var file in batch.Files){ValidatePath(file.Path);if(!ValidId(file.Version)||file.Length<0||file.Chunks<0)throw new InvalidDataException("Invalid archive file.");}
        if(batch.Cursor is{} cursor&&(!ValidId(cursor.Snapshot)||cursor.Stage is not ("messages" or "files" or "complete")||cursor.File<0||cursor.Offset<0||cursor.Messages<0||cursor.Files<0||cursor.Bytes<0))throw new InvalidDataException("Invalid migration checkpoint.");
    }
    internal int Replay(ArchiveCommitLog log,string? snapshot,Func<byte[],byte[]> decode,CancellationToken cancellation=default,IProgress<ScanProgress>? progress=null)=>Replay(log,snapshot,bytes=>{byte[] plain=decode(bytes);try{using var doc=JsonDocument.Parse(plain);return doc.RootElement.ValueKind==JsonValueKind.Array?new ArchiveDelta(1,JsonSerializer.Deserialize<Message[]>(plain)!,[],[],[]):JsonSerializer.Deserialize<ArchiveDelta>(plain)??throw new InvalidDataException("Invalid archive delta.");}finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);}},cancellation,progress);
    internal int Replay(ArchiveCommitLog log,string? snapshot,Func<byte[],ArchiveDelta> decode,CancellationToken cancellation=default,IProgress<ScanProgress>? progress=null,Func<Stream,ArchiveDelta>? projection=null){
        OperationPause.Check(cancellation);int applied=0;long bytes=0;
        var commits=log.CommitsSince(Applied,snapshot).ToArray();long total=commits.Sum(c=>c.Entry.Bytes);
        progress?.Report(new("archive-query-replay",0,0,total,commits.Length));
        foreach(var commit in commits){
            OperationPause.Check(cancellation);using var indexed=projection==null?null:log.OpenProjection(commit.Id);
            OperationPause.Check(cancellation);ArchiveDelta? batch=null;
            try{
                batch=indexed==null?decode(log.Read(commit.Id).Payload):projection!(indexed);
                Validate(batch);
                var prepared=new (string Normalized,string Tokens,byte[] Body)[batch.Messages.Length];
                Parallel.For(0,prepared.Length,new ParallelOptions{MaxDegreeOfParallelism=Math.Clamp(Workers,1,32),CancellationToken=cancellation},n=>{var message=batch.Messages[n];prepared[n]=(Normalize(message.Text),Tokens(message.Text),Body(message));});
                bool browserStatistics=BrowserStatisticsReady;index.Execute("BEGIN");
                try{
                    for(int messageIndex=0;messageIndex<batch.Messages.Length;messageIndex++){
                        var message=batch.Messages[messageIndex];var ready=prepared[messageIndex];
                        OperationPause.Check(cancellation);
                        if(string.IsNullOrEmpty(message.Id)||message.Text==null||message.Json==null||message.Conversation==null)throw new InvalidDataException("Invalid archive message.");
                        var previous=MayContain(message.Id)?index.Query("SELECT rowid,body FROM current_messages WHERE id=?",message.Id).SingleOrDefault():null;
                        string row;
                        if(previous!=null){
                            index.Execute("INSERT INTO message_search(message_search,rowid,tokens) VALUES('delete',?,?)",previous[0],Tokens(Read(previous[1]).Text));
                            index.ExecuteValues("UPDATE current_messages SET conversation=?,time=CAST(? AS INTEGER),text=?,body=? WHERE id=?",message.Conversation,message.Timestamp.ToString(CultureInfo.InvariantCulture),ready.Normalized,ready.Body,message.Id);row=previous[0];
                            index.Execute("DELETE FROM message_references WHERE message_id=?",message.Id);
                        }else{index.ExecuteValues("INSERT INTO current_messages VALUES(?,?,CAST(? AS INTEGER),?,?)",message.Id,message.Conversation,message.Timestamp.ToString(CultureInfo.InvariantCulture),ready.Normalized,ready.Body);row=index.LastInsertRowId.ToString(CultureInfo.InvariantCulture);Remember(message.Id);}
                        index.Execute("INSERT INTO message_search(rowid,tokens) VALUES(?,?)",row,ready.Tokens);
                        if(browserStatistics){var old=previous==null?null:Read(previous[1]);if(old!=null&&old.Conversation!=message.Conversation){index.Execute("UPDATE browser_conversations SET count=count-1 WHERE conversation=?",old.Conversation);index.Execute("DELETE FROM browser_conversations WHERE count<=0");}
                            index.Execute("INSERT INTO browser_conversations VALUES(?,CAST(? AS INTEGER),?) ON CONFLICT(conversation) DO UPDATE SET count=count+excluded.count,name=CASE WHEN excluded.name<>'' THEN excluded.name ELSE name END",message.Conversation,old==null||old.Conversation!=message.Conversation?"1":"0",DisplayName(message));}
                        using var raw=JsonDocument.Parse(message.Json);
                        if(raw.RootElement.ValueKind==JsonValueKind.Object){
                            void Reference(string property,string prefix){if(raw.RootElement.TryGetProperty(property,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is{Length:>0} reference&&reference!="0")index.Execute("INSERT OR IGNORE INTO message_references VALUES(?,?,?)",message.Conversation,prefix+reference,message.Id);}
                            Reference("ServerId","");Reference("Sequence","seq:");
                        }
                    }
                    for(int n=0;n<batch.Objects.Length;n++){
                        var item=batch.Objects[n];if(!ValidId(item.Id)||item.Bytes==null||item.Length is <1 or >4*1024*1024)throw new InvalidDataException("Invalid resource chunk.");
                        index.Execute("INSERT OR IGNORE INTO archive_objects VALUES(?,?,CAST(? AS INTEGER),CAST(? AS INTEGER))",item.Id,commit.Id,n.ToString(CultureInfo.InvariantCulture),item.Length.ToString(CultureInfo.InvariantCulture));
                    }
                    foreach(var part in batch.Parts){
                        if(!ValidId(part.Version)||part.Ordinal<0||!HasObject(part.Object))throw new InvalidDataException("Missing resource chunk reference.");
                        index.Execute("INSERT OR REPLACE INTO archive_file_parts VALUES(?,CAST(? AS INTEGER),?)",part.Version,part.Ordinal.ToString(CultureInfo.InvariantCulture),part.Object);
                    }
                    foreach(var file in batch.Files){
                        ValidatePath(file.Path);if(!ValidId(file.Version)||file.Length<0||file.Chunks<0)throw new InvalidDataException("Invalid archive file.");
                        index.Execute("INSERT OR REPLACE INTO archive_files VALUES(?,?)",file.Path,JsonSerializer.Serialize(file));if(file.SourceStamp!=null&&file.Path.StartsWith("assets/",StringComparison.Ordinal))index.Execute("INSERT OR REPLACE INTO source_assets VALUES(?,?)",JsonFiles.Hash(file.SourceStamp),file.Path);
                    }
                    if(batch.Cursor!=null){
                        var cursor=batch.Cursor;if(!ValidId(cursor.Snapshot)||cursor.Stage is not ("messages" or "files" or "complete")||cursor.File<0||cursor.Offset<0||cursor.Messages<0||cursor.Files<0||cursor.Bytes<0)throw new InvalidDataException("Invalid migration checkpoint.");
                        index.Execute("INSERT OR REPLACE INTO projection VALUES('import',?)",JsonSerializer.Serialize(cursor));
                    }
                    index.Execute("INSERT OR REPLACE INTO projection VALUES('applied',?)",commit.Id);index.Execute("COMMIT");applied++;
                }catch{index.Execute("ROLLBACK");throw;}
                bytes+=commit.Entry.Bytes;progress?.Report(new("archive-query-replay",applied,bytes,total,commits.Length));
            }finally{if(batch?.Objects!=null)foreach(var item in batch.Objects)if(item?.Bytes!=null)System.Security.Cryptography.CryptographicOperations.ZeroMemory(item.Bytes);}
        }
        return applied;
    }
    internal static bool ValidId(string id)=>id is{Length:64}&&id.All(c=>c is >= 'a' and <= 'f' or >= '0' and <= '9');
    internal static void ValidatePath(string path){if(string.IsNullOrWhiteSpace(path)||path.Contains('\\')||path.Contains(':')||path.StartsWith('/')||path.Split('/').Any(p=>p is "" or "." or ".."))throw new InvalidDataException("Invalid archive-relative file path.");}
    internal IReadOnlyList<Message> Page(string conversation,int limit,long afterTime=long.MinValue,string afterId=""){
        if(limit is <1 or >10000)throw new ArgumentOutOfRangeException(nameof(limit));
        return index.Query("SELECT body FROM current_messages WHERE conversation=? AND (time,id)>(CAST(? AS INTEGER),?) ORDER BY time,id LIMIT CAST(? AS INTEGER)",conversation,afterTime.ToString(CultureInfo.InvariantCulture),afterId,limit.ToString(CultureInfo.InvariantCulture)).Select(r=>Read(r[0])).ToArray();
    }
    internal IReadOnlyList<Message> Search(string text,int limit=100,long? afterTime=null,string? afterId=null,bool reverse=false){
        if(limit is <1 or >10000)throw new ArgumentOutOfRangeException(nameof(limit));if(string.IsNullOrEmpty(text))return [];
        string normalized=Normalize(text);var runes=normalized.EnumerateRunes().ToArray();string term=Token(runes.AsSpan(0,Math.Min(3,runes.Length)));
        // Two-character terms use the first unigram; exact substring verification removes false positives.
        if(runes.Length==2)term=Token(runes.AsSpan(0,1));
        if(runes.Length>3){int middle=(runes.Length-3)/2;term+=" AND "+Token(runes.AsSpan(middle,3))+" AND "+Token(runes.AsSpan(runes.Length-3,3));}
        string order=reverse?" DESC":" ASC",comparison=reverse?"<":">";
        string sql="SELECT m.body FROM message_search JOIN current_messages m ON m.rowid=message_search.rowid WHERE message_search MATCH ? AND instr(m.text,?)>0";
        var values=new List<string>{term,normalized};
        if(afterTime!=null){sql+=" AND (m.time,m.id)"+comparison+"(CAST(? AS INTEGER),?)";values.Add(afterTime.Value.ToString(CultureInfo.InvariantCulture));values.Add(afterId??"");}
        sql+=" ORDER BY m.time"+order+",m.id"+order+" LIMIT CAST(? AS INTEGER)";values.Add(limit.ToString(CultureInfo.InvariantCulture));
        return index.Query(sql,values.ToArray()).Select(r=>Read(r[0])).ToArray();
    }
    public void Dispose()=>index.Dispose();
}
