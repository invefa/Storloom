using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Storloom.Core.Chats;

namespace Storloom.App;

internal sealed record CaptureRequest(ChatLocation Location, string Executable);
internal sealed record CaptureResponse(string? Key, string? Error);

// The inherited native debugger expects process exit to end its fresh debuggee tree.
// Keep that lifecycle out of the long-lived GUI and exchange keys only over pipes.
internal static class CaptureWorker {
    public static int Run() {
        try {
            var request = JsonSerializer.Deserialize<CaptureRequest>(Console.ReadLine() ?? "") ??
                throw new InvalidDataException("Missing capture request.");
            byte[] key = WeChatKeyCapture.Capture(request.Location, request.Executable);
            try { Console.WriteLine(JsonSerializer.Serialize(new CaptureResponse(Convert.ToHexString(key), null))); }
            finally { CryptographicOperations.ZeroMemory(key); }
            return 0;
        } catch (Exception ex) {
            Console.WriteLine(JsonSerializer.Serialize(new CaptureResponse(null, ex.Message))); return 1;
        }
    }

    public static async Task<byte[]> Capture(ChatLocation location, string executable, CancellationToken cancellation) {
        string application = Path.Combine(AppContext.BaseDirectory, "Storloom.exe");
        var start = new ProcessStartInfo(application) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--capture-worker");
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the capture worker.");
        try {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new CaptureRequest(location, executable)));
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellation);
            var errors = process.StandardError.ReadToEndAsync(cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
            var response = JsonSerializer.Deserialize<CaptureResponse>(await output) ??
                throw new InvalidDataException("Capture worker returned no response.");
            _ = await errors; // Never echo arbitrary native diagnostics or key material.
            if (process.ExitCode != 0 || response.Key == null)
                throw new InvalidOperationException(response.Error ?? "Capture did not authenticate a key.");
            return Convert.FromHexString(response.Key);
        } finally {
            // Exiting the debugger worker also ends only the child clients it created.
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }
}
