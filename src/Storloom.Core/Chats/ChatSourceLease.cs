namespace Storloom.Core.Chats;

/// <summary>A closed client's database files stay read-locked for one independent reading task.</summary>
internal sealed class ChatSourceLease : IDisposable {
    internal string Root { get; }
    internal string IdentityRoot {get;}
    internal Dictionary<string,string> Hashes { get; }=new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileStream> handles=[];
    private ChatSourceLease(string root,string? identityRoot){Root=root;IdentityRoot=PathPolicy.Normalize(identityRoot??root);}
    internal static ChatSourceLease Open(string root,ChatPlatform platform,CancellationToken cancellation,bool checkRunningClients=true,IProgress<ScanProgress>? progress=null,string? identityRoot=null){
        root=PathPolicy.Normalize(root);PathPolicy.AssertNoLinks(root);
        var names=platform==ChatPlatform.QQ?new[]{"QQ"}:new[]{"Weixin","WeChat"};
        if(checkRunningClients&&CleanupEngine.RunningProcesses(new CleanupRule{Processes=names}).Length>0)throw new IOException("Close the chat client before reading its databases.");
        var result=new ChatSourceLease(root,identityRoot);
        try{
            var files=new List<string>();
            var queue=new Queue<(string Path,int Depth)>();queue.Enqueue((root,0));
            while(queue.TryDequeue(out var item)){
                OperationPause.Check(cancellation);PathPolicy.AssertNoLinks(item.Path);
                foreach(var entry in new DirectoryInfo(item.Path).EnumerateFileSystemInfos()){
                    if((entry.Attributes&FileAttributes.ReparsePoint)!=0)continue;
                    if(entry is DirectoryInfo){if(item.Depth<6&&!(platform==ChatPlatform.QQ&&entry.Name.Equals("QQGuild",StringComparison.OrdinalIgnoreCase))&&!new[]{"attach","FileStorage","image","video","pic","cache","nt_data","MsgAttach","FileRecv","head_image","emoji"}.Contains(entry.Name,StringComparer.OrdinalIgnoreCase))queue.Enqueue((entry.FullName,item.Depth+1));}
                    else if(entry.Name.EndsWith(".db",StringComparison.OrdinalIgnoreCase)||entry.Name.EndsWith(".db-wal",StringComparison.OrdinalIgnoreCase)||entry.Name.EndsWith(".db-shm",StringComparison.OrdinalIgnoreCase)){
                        files.Add(entry.FullName);
                    }
                }
            }
            if(!ChatExporter.FindDatabases(root,platform).Any())throw new InvalidDataException("No supported source database found.");
            foreach(string file in files){OperationPause.Check(cancellation);PathPolicy.AssertNoLinks(file);result.handles.Add(new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read));}
            long total=result.handles.Sum(h=>h.Length),processed=0;int done=0;
            progress?.Report(new("chat-source-lock",0,0,total,files.Count));
            for(int index=0;index<files.Count;index++){
                OperationPause.Check(cancellation);var input=result.handles[index];string relative=Path.GetRelativePath(root,files[index]);
                result.Hashes[relative]=Convert.ToHexString(TransferService.Hash(input,cancellation,count=>{processed+=count;progress?.Report(new("chat-source-lock",done,processed,total,files.Count,relative));}));input.Position=0;done++;
            }
            progress?.Report(new("chat-source-lock",done,processed,total,files.Count));
            return result;
        }catch{result.Dispose();throw;}
    }
    public void Dispose(){foreach(var stream in handles)stream.Dispose();handles.Clear();}
}
