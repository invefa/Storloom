using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
namespace Storloom.App;
internal static class WorkerRecommendation {
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,Task<bool?>> disks=new(StringComparer.OrdinalIgnoreCase);
    private static Task<bool?> Disk(string? path){
        try{string? root=string.IsNullOrWhiteSpace(path)?null:Path.GetPathRoot(Path.GetFullPath(path));return root==null?Task.FromResult<bool?>(null):disks.GetOrAdd(root,static value=>Task.Run(()=>SeekPenalty(value)));}
        catch(ArgumentException){return Task.FromResult<bool?>(null);}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,uint inputSize,byte[] output,uint outputSize,out uint returned,IntPtr overlapped);
    internal static bool? SeekPenalty(string? path){try{if(string.IsNullOrWhiteSpace(path))return null;string? root=Path.GetPathRoot(Path.GetFullPath(path));if(root==null||root.Length!=3||root[1]!=':')return null;using var volume=CreateFile(@"\\.\"+root[..2],0,3,IntPtr.Zero,3,0,IntPtr.Zero);if(volume.IsInvalid)return null;byte[] query=new byte[12];BitConverter.GetBytes(7).CopyTo(query,0);byte[] result=new byte[16];return DeviceIoControl(volume,0x2D1400,query,12,result,16,out uint received,IntPtr.Zero)&&received>=9?result[8]!=0:null;}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException){return null;}}
    internal static int Choose(string task,bool? seek,int processors,long availableMemory){int desired=seek==null?4:task switch{"backup"=>seek==true?5:4,"encryption"=>seek==true?5:6,"export"=>seek==true?4:6,_=>4};int memoryCap=availableMemory>0&&availableMemory<2L*1024*1024*1024?2:8;return Math.Clamp(desired,1,Math.Min(memoryCap,Math.Clamp(processors,1,8)));}
    internal static int For(string task,string? path){var disk=Disk(path);return Choose(task,disk.IsCompletedSuccessfully?disk.Result:null,Environment.ProcessorCount,GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);}
    internal static ComboBox Selector(int workers,bool automatic,string task,Func<string?> path){var choice=new ComboBox{MinWidth=164,HorizontalAlignment=HorizontalAlignment.Left,SelectedValuePath="Tag",ToolTip=L.T("WorkersAutomaticHelp")};
        void Populate(){choice.Tag=true;int previous=choice.SelectedValue is int value?value:automatic?0:Math.Clamp(workers,1,32);int recommended=For(task,path());choice.Items.Clear();choice.Items.Add(new ComboBoxItem{Content=L.T("WorkersAutomatic",recommended),Tag=0});for(int n=1;n<=32;n++)choice.Items.Add(new ComboBoxItem{Content=n==recommended?L.T("WorkersRecommended",n):n.ToString(),Tag=n});choice.SelectedValue=previous;choice.Tag=false;}
        async void Refresh(object? sender,RoutedEventArgs args){string? current=path();await Disk(current);if(choice.IsLoaded&&string.Equals(current,path(),StringComparison.OrdinalIgnoreCase))Populate();}
        Populate();choice.Loaded+=Refresh;choice.DropDownOpened+=(_,_)=>{Populate();Refresh(choice,new RoutedEventArgs());};return choice;
    }
}
