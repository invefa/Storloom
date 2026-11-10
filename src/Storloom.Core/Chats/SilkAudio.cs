using System.Diagnostics;
using System.Text;

namespace Storloom.Core.Chats;

internal static class SilkAudio {
    public static byte[]? Decode(byte[] data, CancellationToken cancellation) {
        if (data.AsSpan().StartsWith("RIFF"u8) && data.Length >= 12 && data.AsSpan(8).StartsWith("WAVE"u8)) return data;
        if (!(data.AsSpan().StartsWith("#!SILK_V3"u8) || data.Length > 1 && data[0] == 2 && data.AsSpan(1).StartsWith("#!SILK_V3"u8)) || data.Length > 16 * 1024 * 1024) return null;
        string executable = Path.Combine(AppContext.BaseDirectory, "storloom-silk.exe");
        if (!File.Exists(executable)) return null;
        string directory = Path.Combine(Path.GetTempPath(), "Storloom-voice-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try {
            string input = Path.Combine(directory, "voice.silk"), output = Path.Combine(directory, "voice.pcm"); File.WriteAllBytes(input, data);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { input, output, "-Fs_API", "24000", "-quiet" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start); if (process == null) return null;
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null); var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            try {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                if (process.ExitCode != 0 || !File.Exists(output)) return null;
                long size = new FileInfo(output).Length; if (size == 0 || size > 64 * 1024 * 1024 || size % 2 != 0) return null;
                byte[] pcm = File.ReadAllBytes(output);
                using var wave = new MemoryStream(); using var writer = new BinaryWriter(wave, Encoding.ASCII, true);
                writer.Write("RIFF"u8); writer.Write(pcm.Length + 36); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(24000); writer.Write(48000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
                return wave.ToArray();
            } catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); process.WaitForExit(); OperationPause.Check(cancellation); return null; }
        } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { return null; }
        finally { foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path); Directory.Delete(directory); }
    }
}
