using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Storloom.Core.Chats;

namespace Storloom.App;

internal sealed record CaptureRequest(ChatLocation Location, string Executable, bool Debug=false);
internal sealed record CaptureResponse(string? Key, string? Error, ChatDiagnostic? Diagnostic = null);

// The inherited native debugger expects process exit to end its fresh debuggee tree.
// Keep that lifecycle out of the long-lived GUI and exchange keys only over pipes.
internal static class CaptureWorker {
    public static int Run() {
        try {
            var request = JsonSerializer.Deserialize<CaptureRequest>(Console.ReadLine() ?? "") ??
                throw new InvalidDataException("Missing capture request.");
            var diagnostics = new WorkerDiagnostics(request.Debug,value => Console.WriteLine(JsonSerializer.Serialize(new CaptureResponse(null, null, value))));
            _=Task.Run(()=>{while(Console.ReadLine() is string command)diagnostics.Enabled=command=="debug:on";});
            byte[] key = request.Location.Platform == ChatPlatform.QQ ? QQKeyCapture.Capture(request.Location, request.Executable, diagnostics: diagnostics) : WeChatKeyCapture.Capture(request.Location, request.Executable, diagnostics: diagnostics);
            try { Console.WriteLine(JsonSerializer.Serialize(new CaptureResponse(Convert.ToHexString(key), null))); }
            finally { CryptographicOperations.ZeroMemory(key); }
            return 0;
        } catch (Exception ex) {
            Console.WriteLine(JsonSerializer.Serialize(new CaptureResponse(null, ex.Message))); return 1;
        }
    }

    public static async Task<byte[]> Capture(ChatLocation location, string executable, CancellationToken cancellation, IProgress<ChatDiagnostic>? diagnostics = null) {
        string application = Path.Combine(AppContext.BaseDirectory, "Storloom.exe");
        var start = new ProcessStartInfo(application) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--capture-worker");
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the capture worker.");
        using var controls=new CancellationTokenSource();Task controlTask=Task.CompletedTask;
        try {
            bool initial=ChatDiagnostic.Enabled(diagnostics);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new CaptureRequest(location, executable,initial)));
            controlTask=Task.Run(async()=>{bool previous=initial;try{while(!controls.IsCancellationRequested){await Task.Delay(250,controls.Token);bool value=ChatDiagnostic.Enabled(diagnostics);if(value!=previous){await process.StandardInput.WriteLineAsync(value?"debug:on":"debug:off");previous=value;}}}catch(Exception ex)when(ex is OperationCanceledException or IOException or ObjectDisposedException){}});
            var errors = process.StandardError.ReadToEndAsync(cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            CaptureResponse? response = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is string line) {
                var frame = JsonSerializer.Deserialize<CaptureResponse>(line) ?? throw new InvalidDataException("Invalid capture worker frame.");
                if (frame.Diagnostic != null) { if(ChatDiagnostic.Enabled(diagnostics))diagnostics!.Report(frame.Diagnostic); continue; }
                response = frame; break;
            }
            await process.WaitForExitAsync(timeout.Token);
            if (response == null) throw new InvalidDataException("Capture worker returned no response.");
            _ = await errors; // Never echo arbitrary native diagnostics or key material.
            if (process.ExitCode != 0 || response.Key == null)
                throw new InvalidOperationException(response.Error ?? "Capture did not authenticate a key.");
            return Convert.FromHexString(response.Key);
        } finally {
            controls.Cancel();await controlTask;
            // Exiting the debugger worker also ends only the child clients it created.
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }
    private sealed class WorkerDiagnostics(bool enabled,Action<ChatDiagnostic> report) : IChatDiagnosticSwitch {
        private volatile bool active=enabled;
        public bool Enabled{get=>active;set=>active=value;}
        public void Report(ChatDiagnostic value){if(active)report(value);}
    }
}
