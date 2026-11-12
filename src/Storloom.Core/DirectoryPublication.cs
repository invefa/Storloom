namespace Storloom.Core;

/// <summary>Bounded retry for transient Windows directory sharing, retaining identity and no-overwrite checks.</summary>
internal static class DirectoryPublication {
    internal static void MoveFile(string source,string destination,bool overwrite=false){
        source=PathPolicy.Normalize(source);destination=PathPolicy.Normalize(destination);
        PathPolicy.AssertNoLinks(source);PathPolicy.AssertNoLinks(Path.GetDirectoryName(destination)!);
        var identity=NativeFile.ReadStamp(source);FileStamp? target=File.Exists(destination)?NativeFile.ReadStamp(destination):null;
        for(int attempt=0;;attempt++){
            PathPolicy.AssertNoLinks(source);PathPolicy.AssertNoLinks(Path.GetDirectoryName(destination)!);
            if(NativeFile.ReadStamp(source)!=identity)throw new IOException("Publication file changed.");
            if(target!=null?(!overwrite||!File.Exists(destination)||NativeFile.ReadStamp(destination)!=target):File.Exists(destination)||Directory.Exists(destination))throw new IOException("Publication destination changed.");
            try{File.Move(source,destination,overwrite);return;}
            catch(Exception ex)when((ex is IOException or UnauthorizedAccessException)&&(ex.HResult&0xffff) is 5 or 32 or 33){
                if(attempt>=7)throw new IOException("Publication file is still in use.",ex);
                Thread.Sleep(Math.Min(200,25<<attempt));
            }
        }
    }
    internal static void Move(string source,string destination,CancellationToken cancellation=default) {
        source=PathPolicy.Normalize(source);destination=PathPolicy.Normalize(destination);
        PathPolicy.AssertNoLinks(source);PathPolicy.AssertNoLinks(Path.GetDirectoryName(destination)!);
        var identity=NativeFile.ReadDirectoryStamp(source);
        for(int attempt=0;;attempt++){
            OperationPause.Check(cancellation);
            PathPolicy.AssertNoLinks(source);PathPolicy.AssertNoLinks(Path.GetDirectoryName(destination)!);
            var current=NativeFile.ReadDirectoryStamp(source);
            if(current.Volume!=identity.Volume||current.FileId!=identity.FileId||current.CreationUtcTicks!=identity.CreationUtcTicks)throw new IOException("Publication source directory changed.");
            if(Directory.Exists(destination)||File.Exists(destination))throw new IOException("Publication destination already exists.");
            try{Directory.Move(source,destination);return;}
            catch(Exception ex)when(attempt<7&&(ex is IOException or UnauthorizedAccessException)&&(ex.HResult&0xffff) is 5 or 32 or 33){
                int delay=Math.Min(200,25<<attempt);
                if(cancellation.WaitHandle.WaitOne(delay))cancellation.ThrowIfCancellationRequested();
            }
        }
    }
}
