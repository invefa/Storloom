using Microsoft.Win32;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Storloom.Core;
namespace Storloom.App;
internal static class PickerHistory {
    private static readonly DependencyProperty RootProperty=DependencyProperty.RegisterAttached("Root",typeof(string),typeof(PickerHistory));
    internal static void Attach(Window window,string root)=>window.SetValue(RootProperty,root);
    private static string? Root(Window? owner){while(owner!=null){if(owner.GetValue(RootProperty) is string root)return root;owner=owner.Owner;}return null;}
    internal static Guid Identity(string purpose)=>new(SHA256.HashData(Encoding.UTF8.GetBytes("Storloom.picker."+purpose)).AsSpan(0,16));
    private static string File(Window owner)=>Path.Combine(Root(owner)??Path.Combine(AppContext.BaseDirectory,"data"),"picker-directories.json");
    private static Dictionary<string,string> Read(Window owner){try{return System.IO.File.Exists(File(owner))?JsonFiles.Read<Dictionary<string,string>>(File(owner)):[];}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){return [];}}
    private static void Remember(Window owner,string purpose,string? directory){if(string.IsNullOrEmpty(directory))return;try{var values=Read(owner);values[purpose]=directory;JsonFiles.Write(File(owner),values);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}}
    internal static bool? Show(FileDialog dialog,Window owner,string purpose){dialog.ClientGuid=Identity(purpose);var values=Read(owner);if(values.TryGetValue(purpose,out var saved)&&Directory.Exists(saved))dialog.InitialDirectory=saved;bool? result=dialog.ShowDialog(owner);if(result==true)Remember(owner,purpose,Path.GetDirectoryName(dialog.FileName));return result;}
    internal static string Folder(Window owner,string? initial,string purpose){var dialog=new OpenFolderDialog{ClientGuid=Identity(purpose)};var values=Read(owner);if(values.TryGetValue(purpose,out var saved)&&Directory.Exists(saved))dialog.InitialDirectory=saved;else if(Directory.Exists(initial))dialog.InitialDirectory=initial;bool? result=dialog.ShowDialog(owner);if(result==true)Remember(owner,purpose,dialog.FolderName);return result==true?dialog.FolderName:"";}
}
