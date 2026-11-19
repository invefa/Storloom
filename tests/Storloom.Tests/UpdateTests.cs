using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Storloom.Core;
using Storloom.Core.Updates;
using Xunit;

namespace Storloom.Tests;

public sealed class UpdateTests : IDisposable {
    private const string Repository = "https://github.com/storloom-tests/Storloom";
    private readonly string root=Path.Combine(AppContext.BaseDirectory,"fixtures","update-"+Guid.NewGuid().ToString("N"));
    public UpdateTests()=>Directory.CreateDirectory(root);
    public void Dispose()=>Directory.Delete(root,true);
    private static string Metadata(string version="9.0.0",bool preview=false,string? url=null,string? digest=null)=>JsonSerializer.Serialize(new {
        draft=false,prerelease=preview,tag_name="v"+version,assets=new[]{new{name=$"Storloom-{version}-win-x64.zip",size=123,
        browser_download_url=url??$"{Repository}/releases/download/v{version}/Storloom-{version}-win-x64.zip",digest=digest??"sha256:"+new string('a',64)}}});
    [Fact]public void OnlyNewStableVersionsAreEligible(){
        Assert.Null(ReleaseUpdater.Parse(Metadata(),"9.0.0",Repository));Assert.Null(ReleaseUpdater.Parse(Metadata(),"10.0.0",Repository));
        Assert.Null(ReleaseUpdater.Parse(Metadata(preview:true),"1.0.0",Repository));Assert.Null(ReleaseUpdater.Parse(Metadata("9.0.0-beta"),"1.0.0",Repository));
        Assert.Equal("9.0.0",ReleaseUpdater.Parse(Metadata(),"1.0.0",Repository)!.Version);
    }
    [Theory][InlineData("https://example.invalid/package.zip",null)][InlineData(null,"sha256:bad")][InlineData(null,"md5:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ForeignDownloadsAndMissingChecksumsAreRejected(string? url,string? digest)=>Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.Parse(Metadata(url:url,digest:digest),"1.0.0",Repository));
    [Fact]public async Task MissingReleaseIsHandledWithoutDownloading(){
        using var client=new HttpClient(new Handler(_=>new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new ReleaseUpdater(client,Repository).CheckAsync("1.0.0"));
    }
    private string Package(string directory,string version="9.0.0") {
        Directory.CreateDirectory(directory);var files=new Dictionary<string,string>();
        foreach(var (name,text) in new[]{("Storloom.exe","new-executable"),("Storloom.dll","new-library"),("build-info.json","{\"version\":\""+version+"\"}"),("docs/guide.md","new help")}){
            string path=Path.Combine(directory,name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);files.Add(name,ReleaseUpdater.Hash(path));
        }
        JsonFiles.Write(Path.Combine(directory,"package-manifest.json"),new UpdatePackage(version,"win-x64",files));return directory;
    }
    private byte[] Zip(string directory){string zip=Path.Combine(root,Guid.NewGuid()+".zip");ZipFile.CreateFromDirectory(directory,zip);return File.ReadAllBytes(zip);}
    [Fact]public async Task VerifiedDownloadExtractsAndReportsProgress(){
        string container=Path.Combine(root,"container");Package(Path.Combine(container,"Storloom-9.0.0-win-x64"));byte[] bytes=Zip(container);
        using var client=new HttpClient(new Handler(r=>new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=r,Content=new ByteArrayContent(bytes)}));
        var progress=new List<UpdateProgress>();var result=await new ReleaseUpdater(client,Repository).DownloadAsync(Release(bytes),Path.Combine(root,"updates"),new ProgressSink(progress.Add));
        Assert.Equal("9.0.0",result.Version);Assert.True(File.Exists(Path.Combine(result.Directory,"Storloom.exe")));Assert.Contains(progress,p=>p.Phase=="download"&&p.Bytes==bytes.Length);Assert.Contains(progress,p=>p.Phase=="verify");
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task CorruptAndTruncatedDownloadsLeaveNoPreparedPackage(bool truncated){
        byte[] bytes=Encoding.UTF8.GetBytes("broken download");var release=Release(bytes);if(truncated)release=release with{Size=bytes.Length+1};else release=release with{Sha256=new string('a',64)};
        using var client=new HttpClient(new Handler(r=>new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=r,Content=new ByteArrayContent(bytes)}));string updates=Path.Combine(root,"updates");
        await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseUpdater(client,Repository).DownloadAsync(release,updates));Assert.Empty(Directory.GetFileSystemEntries(updates));
    }
    [Fact]public async Task CancelledDownloadIsNotMarkedReady(){
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();using var client=new HttpClient(new Handler(_=>throw new Exception("should not download")));
        string updates=Path.Combine(root,"updates");await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ReleaseUpdater(client,Repository).DownloadAsync(Release([1,2,3]),updates,token:cancellation.Token));Assert.Empty(Directory.GetFileSystemEntries(updates));
    }
    [Theory][InlineData("../escape")][InlineData("data/preferences.json")][InlineData("docs/../../escape")][InlineData("docs/CON.txt")][InlineData("docs/a:stream")][InlineData("docs/ending.")][InlineData("docs\\escape")]
    public void UnsafePackagePathsAreRejected(string name)=>Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.SafePath(root,name));
    [Fact]public void ZipTraversalCannotWriteOutsideStaging(){
        string zip=Path.Combine(root,"bad.zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){using var writer=new StreamWriter(archive.CreateEntry("Storloom-9.0.0-win-x64/../escape").Open());writer.Write("escape");}
        Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.Extract(zip,Path.Combine(root,"staging"),"9.0.0"));Assert.False(File.Exists(Path.Combine(root,"escape")));
    }
    [Fact]public void TamperedAndUnlistedStagedFilesAreRejected(){
        string payload=Package(Path.Combine(root,"payload"));File.AppendAllText(Path.Combine(payload,"Storloom.dll"),"tamper");Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ValidatePackage(payload,"9.0.0"));
        File.WriteAllText(Path.Combine(payload,"Storloom.dll"),"new-library");File.WriteAllText(Path.Combine(payload,"unlisted.exe"),"unexpected");Assert.Throws<InvalidDataException>(()=>ReleaseUpdater.ValidatePackage(payload,"9.0.0"));
    }
    private UpdateInstallPlan Plan(){
        string install=Path.Combine(root,"installed"),data=Path.Combine(install,"data");Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(install,"Storloom.exe"),"old-executable");File.WriteAllText(Path.Combine(install,"Storloom.dll"),"old-library");File.WriteAllText(Path.Combine(install,"build-info.json"),"old-build");
        File.WriteAllText(Path.Combine(data,"preferences.json"),"private preferences");File.WriteAllText(Path.Combine(install,"user-note.txt"),"keep user file");
        string payload=Package(Path.Combine(data,"updates",Guid.NewGuid().ToString("N"),"payload"));
        return new(install,data,new(payload,"9.0.0"),int.MaxValue,0,ReleaseUpdater.Hash(Path.Combine(install,"build-info.json")),false);
    }
    [Fact]public void UpdatePreservesUserDataAndBacksUpReplacedFiles(){
        var plan=Plan();UpdateInstaller.Apply(plan);Assert.Equal("new-executable",File.ReadAllText(Path.Combine(plan.InstallRoot,"Storloom.exe")));
        Assert.Equal("private preferences",File.ReadAllText(Path.Combine(plan.DataRoot,"preferences.json")));Assert.Equal("keep user file",File.ReadAllText(Path.Combine(plan.InstallRoot,"user-note.txt")));
        Assert.Equal("old-executable",File.ReadAllText(Path.Combine(Path.GetDirectoryName(plan.Update.Directory)!,"rollback","Storloom.exe")));
    }
    [Fact]public void LockedDestinationRollsBackEarlierReplacements(){
        var plan=Plan();using var locked=new FileStream(Path.Combine(plan.InstallRoot,"Storloom.dll"),FileMode.Open,FileAccess.Read,FileShare.Read);
        var error=Assert.Throws<IOException>(()=>UpdateInstaller.Apply(plan));Assert.Contains("restored",error.Message);Assert.Equal("old-executable",File.ReadAllText(Path.Combine(plan.InstallRoot,"Storloom.exe")));Assert.Equal("old-build",File.ReadAllText(Path.Combine(plan.InstallRoot,"build-info.json")));
    }
    [Fact]public void AnotherBuildInstalledBeforeExitIsNeverOverwritten(){
        var plan=Plan();File.WriteAllText(Path.Combine(plan.InstallRoot,"build-info.json"),"another build");Assert.Throws<IOException>(()=>UpdateInstaller.Apply(plan));Assert.Equal("old-executable",File.ReadAllText(Path.Combine(plan.InstallRoot,"Storloom.exe")));
    }
    [Fact]public void WorkerReportsFailureWhenAnotherWindowStillHoldsTheInstallation(){
        var plan=Plan();string planFile=UpdateInstaller.PlanFile(plan.Update.Directory);JsonFiles.Write(planFile,plan);JsonFiles.Write(Path.Combine(plan.DataRoot,"updates","ready.json"),plan.Update);
        using var lease=new FileStream(Path.Combine(plan.InstallRoot,".update.lock"),FileMode.OpenOrCreate,FileAccess.Read,FileShare.Read);
        Assert.Equal(1,UpdateInstaller.Run(planFile,plan.Update.Directory));var result=JsonFiles.Read<UpdateInstallResult>(Path.Combine(plan.DataRoot,"updates","result.json"));Assert.False(result.Success);Assert.False(File.Exists(Path.Combine(plan.DataRoot,"updates","ready.json")));Assert.Equal("old-executable",File.ReadAllText(Path.Combine(plan.InstallRoot,"Storloom.exe")));
    }
    [Fact]public void WorkerInstallsAndClearsPendingState(){
        var plan=Plan();string planFile=UpdateInstaller.PlanFile(plan.Update.Directory);JsonFiles.Write(planFile,plan);JsonFiles.Write(Path.Combine(plan.DataRoot,"updates","ready.json"),plan.Update);
        Assert.Equal(0,UpdateInstaller.Run(planFile,plan.Update.Directory));Assert.True(JsonFiles.Read<UpdateInstallResult>(Path.Combine(plan.DataRoot,"updates","result.json")).Success);Assert.False(File.Exists(Path.Combine(plan.DataRoot,"updates","ready.json")));
    }
    [Fact]public void PersistedStagingOutsideTheManagedDirectoryIsRejected(){
        string directory=Package(Path.Combine(root,"payload"));Assert.Throws<InvalidDataException>(()=>UpdateInstaller.ValidateStaging(new(directory,"9.0.0"),Path.Combine(root,"data")));
    }
    [Fact]public void PersistedStagingIsReverifiedBeforeLaunchingItsExecutable(){
        var plan=Plan();File.AppendAllText(Path.Combine(plan.Update.Directory,"Storloom.exe"),"damage");Assert.Throws<InvalidDataException>(()=>UpdateInstaller.ValidateStaging(plan.Update,plan.DataRoot));
    }
    [Fact]public void OldTransactionPruningRetainsRecentPendingAndUnmanagedFiles(){
        UpdateInstallPlan? latest=null;
        for(int i=0;i<5;i++){latest=Plan();JsonFiles.Write(UpdateInstaller.PlanFile(latest.Update.Directory),latest);}
        var pending=Plan();string unmanaged=Path.Combine(pending.DataRoot,"updates","user-notes");Directory.CreateDirectory(unmanaged);File.WriteAllText(Path.Combine(unmanaged,"keep.txt"),"keep");
        UpdateInstaller.PruneOldTransactions(pending.DataRoot,pending.InstallRoot,"9.0.0",pending.Update.Directory);
        Assert.True(Directory.Exists(pending.Update.Directory));Assert.True(File.Exists(Path.Combine(unmanaged,"keep.txt")));Assert.Equal(4,Directory.GetDirectories(Path.Combine(pending.DataRoot,"updates")).Length);
    }
    private static UpdateRelease Release(byte[] bytes)=>new("9.0.0","Storloom-9.0.0-win-x64.zip",Repository+"/releases/download/v9.0.0/Storloom-9.0.0-win-x64.zip",Convert.ToHexString(SHA256.HashData(bytes)),bytes.Length);
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(respond(request));}}
    private sealed class ProgressSink(Action<UpdateProgress> receive):IProgress<UpdateProgress>{public void Report(UpdateProgress value)=>receive(value);}
}
