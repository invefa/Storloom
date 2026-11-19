using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Storloom.Core.Updates;

public sealed record UpdateRelease(string Version, string Asset, string Url, string Sha256, long Size);
public sealed record UpdateProgress(string Phase, long Bytes = 0, long Total = 0);
public sealed record UpdatePackage(string Version, string Runtime, Dictionary<string, string> Files);
public sealed record PreparedUpdate(string Directory, string Version);

/// <summary>Only stable Windows x64 releases from the public product repository are accepted.</summary>
public sealed class ReleaseUpdater(HttpClient client, string repository) {
    private string Repository => ValidRepository(repository) ? repository : throw new InvalidDataException("Invalid GitHub repository.");
    private string Latest => "https://api.github.com/repos" + new Uri(Repository).AbsolutePath + "/releases/latest";
    private const long MaxPackage = 2L * 1024 * 1024 * 1024;
    public async Task<UpdateRelease?> CheckAsync(string current, CancellationToken token = default) {
        using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
        request.Headers.UserAgent.ParseAdd("Storloom/" + current);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("Release response is too large.");
        using var buffer = new MemoryStream(); await using var stream = await response.Content.ReadAsStreamAsync(token);
        byte[] chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, token)) > 0) {
            if (buffer.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Release response is too large.");
            buffer.Write(chunk, 0, count);
        }
        string json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        return Parse(json, current, Repository);
    }
    internal static UpdateRelease? Parse(string json, string current, string repository) {
        if (!ValidRepository(repository)) throw new InvalidDataException("Invalid GitHub repository.");
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v')) return null;
        string version = tag[1..];
        if (!ValidVersion(version) || !Version.TryParse(current, out var installed) || Version.Parse(version) <= installed) return null;
        string name = $"Storloom-{version}-win-x64.zip";
        foreach (var asset in root.GetProperty("assets").EnumerateArray()) {
            if (asset.GetProperty("name").GetString() != name) continue;
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            string digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
            long size = asset.GetProperty("size").GetInt64();
            if (url != $"{repository}/releases/download/{tag}/{name}" || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !ValidHash(digest[7..]) || size <= 0 || size > MaxPackage)
                throw new InvalidDataException("The release has no valid Windows x64 download or SHA-256 digest.");
            return new(version, name, url, digest[7..].ToLowerInvariant(), size);
        }
        throw new InvalidDataException("The latest release has no Windows x64 package.");
    }
    public async Task<PreparedUpdate> DownloadAsync(UpdateRelease release, string updatesRoot, IProgress<UpdateProgress>? progress = null, CancellationToken token = default) {
        // Recheck caller-provided metadata too; redirects are handled by HTTPS only in the app's client.
        if (!ValidVersion(release.Version) || !ValidHash(release.Sha256) || release.Asset != $"Storloom-{release.Version}-win-x64.zip" ||
            release.Url != $"{Repository}/releases/download/v{release.Version}/{release.Asset}" || release.Size <= 0 || release.Size > MaxPackage)
            throw new InvalidDataException("Invalid update metadata.");
        Directory.CreateDirectory(updatesRoot);
        string work = Path.Combine(Path.GetFullPath(updatesRoot), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        string zip = Path.Combine(work, "package.zip");
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.Url);
            request.Headers.UserAgent.ParseAdd("Storloom/" + release.Version);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidDataException("Update download must use HTTPS.");
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("Package size mismatch.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true)) {
                byte[] buffer = new byte[131072]; long done = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0) {
                    done += count; if (done > release.Size) throw new InvalidDataException("Package exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token); progress?.Report(new("download", done, release.Size));
                }
                if (done != release.Size) throw new InvalidDataException("Incomplete update download.");
            }
            progress?.Report(new("verify"));
            await using (var stream = File.OpenRead(zip)) {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update SHA-256 mismatch.");
            }
            string package = Extract(zip, work, release.Version, token); File.Delete(zip);
            return new(package, release.Version);
        } catch { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
    }
    internal static string Extract(string zip, string work, string version, CancellationToken token = default) {
        string prefix = $"Storloom-{version}-win-x64/", package = Path.Combine(work, "payload");
        Directory.CreateDirectory(package); long expanded = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 30000) throw new InvalidDataException("Too many package entries.");
        foreach (var entry in archive.Entries) {
            token.ThrowIfCancellationRequested();
            string name = entry.FullName;
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected package root.");
            string relative = name[prefix.Length..]; if (relative == "") continue;
            bool folder = relative.EndsWith('/'); string path = SafePath(package, folder ? relative[..^1] : relative);
            if ((entry.ExternalAttributes & 0x400) != 0 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Package links are not allowed.");
            if (folder) { Directory.CreateDirectory(path); continue; }
            if (!names.Add(relative) || (expanded += entry.Length) > 6L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid package entries or expanded size.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path, false);
        }
        ValidatePackage(package, version); return package;
    }
    public static UpdatePackage ValidatePackage(string directory, string version) {
        string manifest = SafePath(directory, "package-manifest.json");
        if (new FileInfo(manifest).Length > 8 * 1024 * 1024) throw new InvalidDataException("Package manifest is too large.");
        var package = JsonFiles.Read<UpdatePackage>(manifest);
        if (package.Version != version || package.Runtime != "win-x64" || package.Files == null || package.Files.Count is 0 or > 30000 || !package.Files.ContainsKey("Storloom.exe") || !package.Files.ContainsKey("Storloom.dll") || !package.Files.ContainsKey("build-info.json")) throw new InvalidDataException("Invalid package manifest.");
        ValidatePackageFiles(directory, package);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (relative != "package-manifest.json" && !package.Files.Keys.Contains(relative, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Unlisted package file: " + relative);
        }
        return package;
    }
    internal static void ValidatePackageFiles(string directory, UpdatePackage package) {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, expected) in package.Files) {
            string file = SafePath(directory, name);
            if (!names.Add(name) || !ValidHash(expected) || !File.Exists(file) || !Hash(file).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package file verification failed: " + name);
        }
    }
    internal static string SafePath(string root, string relative) {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Split('/').Any(p => string.IsNullOrEmpty(p) || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDevice(p))) throw new InvalidDataException("Unsafe update path.");
        string first = relative.Split('/')[0];
        if (first.Equals("data", StringComparison.OrdinalIgnoreCase) || first.StartsWith('.') || relative.Split('/').Any(p => p.Equals("preferences.json", StringComparison.OrdinalIgnoreCase) || p.Equals("settings.json", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Runtime state is forbidden in updates.");
        string result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes its root.");
        string? ancestor = result;
        while (ancestor != null) { if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked update paths are not allowed."); ancestor = Path.GetDirectoryName(ancestor); }
        return result;
    }
    private static bool IsDevice(string name) { string stem = name.Split('.')[0].ToUpperInvariant(); return stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]); }
    private static bool ValidRepository(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "" &&
        uri.AbsolutePath.Split('/') is ["", var owner, var name] && owner.Length>0 && name.Length>0 && owner.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_') && name.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.');
    internal static bool ValidVersion(string value) => Version.TryParse(value, out var v) && v.Build >= 0 && v.Revision < 0 && value == v.ToString(3);
    internal static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    internal static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
}
