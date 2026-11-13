using Storloom.Core;
using Storloom.Core.Chats;
namespace Storloom.App;

public sealed partial class ShellWindow {
    // Drafts remain in this window only. Credentials are never written to settings.
    private sealed record AccessDraft(string? ReadingRoot,string? Password,string? Confirmation,string? DatabaseKey,ChatArchiveUpdateRequest? Request);
    private readonly Dictionary<string,AccessDraft> accessDrafts=new(StringComparer.Ordinal);
    private string? accessDraftOwner,rawDraftOwner;
    private readonly Dictionary<string,int> materialTabsBySource=new(StringComparer.Ordinal);
    private void CaptureAccessDraft(){
        if(accessDraftOwner==null)return;
        accessDrafts[accessDraftOwner]=new(readingRootInput?.Text,archivePassword?.Password,archivePasswordConfirmation?.Password,keyInput?.Password,pendingArchive?.Id==accessDraftOwner?pendingArchiveRequest:null);
        if(readingRootInput!=null&&archiveProjects.TryGetValue(accessDraftOwner,out var project)){var selected=selectedArchiveId;SaveArchive(project with{ReadingRoot=readingRootInput.Text});selectedArchiveId=selected;}
        accessDraftOwner=null;
    }
    private FrameworkElement WorkDirectoryField(ChatArchiveProject project){
        var saved=PendingState(project);var input=View.Input(saved?.Work??preferences.ChatWorkRoot,saved!=null);input.Name="archive_work_root";input.ToolTip=L.T("ChatWorkRootHelp");
        var browse=Button("Browse",()=>{string path=View.Folder(this,input.Text,"chat-work-root");if(path!="")input.Text=path;});browse.IsEnabled=saved==null;browse.Margin=new Thickness(8,0,0,0);var row=new DockPanel();DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);row.Children.Add(input);
        input.TextChanged+=(_,_)=>{if(saved==null)preferences=preferences with{ChatWorkRoot=input.Text.Trim()};};
        return View.Field("ChatWorkRoot",row);
    }
    private static bool ValidFolderText(string? value){try{return !string.IsNullOrWhiteSpace(value)&&Path.IsPathFullyQualified(value)&&PathPolicy.Normalize(value).Length>0;}catch(Exception ex)when(ex is ArgumentException or IOException or NotSupportedException){return false;}}
}
