using Storloom.Core;

namespace Storloom.App;

internal static class WindowGeometry {
    private sealed record Bounds(double Width,double Height,bool Maximized);
    public static void Attach(Window window,string dataRoot,string key,double minimumWidth,double minimumHeight){
        window.MinWidth=minimumWidth;window.MinHeight=minimumHeight;
        string file=Path.Combine(dataRoot,"window-"+key+".json");
        try{if(File.Exists(file)){
            var value=JsonFiles.Read<Bounds>(file);var work=SystemParameters.WorkArea;
            if(double.IsFinite(value.Width)&&double.IsFinite(value.Height)){
                window.Width=Math.Clamp(value.Width,Math.Min(minimumWidth,work.Width),Math.Max(minimumWidth,work.Width));
                window.Height=Math.Clamp(value.Height,Math.Min(minimumHeight,work.Height),Math.Max(minimumHeight,work.Height));
                if(value.Maximized)window.WindowState=WindowState.Maximized;
            }
        }}catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException){ }
        window.Closed+=(_,_)=>{
            var bounds=window.WindowState==WindowState.Normal?new Rect(window.Left,window.Top,window.ActualWidth,window.ActualHeight):window.RestoreBounds;
            if(!double.IsFinite(bounds.Width)||!double.IsFinite(bounds.Height)||bounds.Width<minimumWidth||bounds.Height<minimumHeight)return;
            try{Directory.CreateDirectory(dataRoot);JsonFiles.Write(file,new Bounds(bounds.Width,bounds.Height,window.WindowState==WindowState.Maximized));}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){ }
        };
    }
}
