using System.Diagnostics;

namespace Storloom.Core.Chats;

// WXGF carries an HEVC stream. Conversion uses an optional local FFmpeg installation;
// no executable or client resource is downloaded or bundled by this resolver.
internal static class WeChatAnimation {
    public static byte[]? Convert(byte[] data, CancellationToken cancellation) {
        string? executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            .Select(p => Path.Combine(p.Trim('"'), "ffmpeg.exe")).FirstOrDefault(File.Exists);
        if (executable == null || data.Length > 32 * 1024 * 1024) return null;
        int offset = -1;
        for (int i = 4; i < Math.Min(data.Length - 6, 8192); i++)
            if (data.AsSpan(i, 6).SequenceEqual(new byte[] { 0, 0, 0, 1, 0x40, 1 })) { offset = i; break; }
        if (offset < 0) return null;
        string directory = Path.Combine(Path.GetTempPath(), "Storloom-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            string input = Path.Combine(directory, "input.hevc"), output = Path.Combine(directory, "output.gif");
            File.WriteAllBytes(input, data[offset..]);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-f", "hevc", "-i", input,
                "-vf", "fps=15,scale=w='min(1024,iw)':h='min(1024,ih)':force_original_aspect_ratio=decrease", "-fs", "33554433", "-loop", "0", output }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process == null) return null;
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length > 32 * 1024 * 1024) return null;
                byte[] result = File.ReadAllBytes(output);
                return WeChatImages.Format(result) == ".gif" ? result : null;
            } catch (OperationCanceledException) {
                if (!process.HasExited) process.Kill(true);
                process.WaitForExit();
                cancellation.ThrowIfCancellationRequested();
                return null;
            }
        } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return null; }
        finally {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
