namespace Storloom.Core.Chats;

// Metadata only. Never add candidate bytes, key strings, message payloads or IPC responses here.
public sealed record ChatDiagnostic(string Stage, long Bytes = 0, int Processes = 0,
    int Regions = 0, int Candidates = 0, double ElapsedSeconds = 0) {
    public static bool Enabled(IProgress<ChatDiagnostic>? output)=>output is IChatDiagnosticSwitch toggle?toggle.Enabled:output!=null;
}
public interface IChatDiagnosticSwitch : IProgress<ChatDiagnostic> { bool Enabled { get; } }
