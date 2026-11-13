namespace Storloom.Core.Chats;

internal sealed record ArchiveObject(string Id,byte[] Bytes){[System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]public int? DeclaredLength{get;init;}[System.Text.Json.Serialization.JsonIgnore]internal int Length=>DeclaredLength??Bytes.Length;}
internal sealed record ArchiveFilePart(string Version,int Ordinal,string Object);
internal sealed record ArchiveFileEntry(string Path,string Version,long Length,int Chunks,bool Complete,FileStamp? SourceStamp=null,string? Sha256=null);
internal sealed record ArchiveImportCursor(string Snapshot,string Stage,int File,long Offset,long Messages,long Files,long Bytes,string? SourceIdentity=null);
internal sealed record ArchiveDelta(int Version,ArchiveQueryCache.Message[] Messages,ArchiveObject[] Objects,
    ArchiveFilePart[] Parts,ArchiveFileEntry[] Files,ArchiveImportCursor? Cursor=null);
