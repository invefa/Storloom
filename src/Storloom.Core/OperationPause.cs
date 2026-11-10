namespace Storloom.Core;

/// <summary>Cooperative pause at bounded engine checkpoints; cancellation remains active while paused.</summary>
public sealed class OperationPause : IDisposable {
    private static readonly AsyncLocal<OperationPause?> ambient = new();
    private readonly ManualResetEventSlim ready = new(true);
    public bool IsPaused => !ready.IsSet;
    public void Pause() => ready.Reset();
    public void Resume() => ready.Set();
    public IDisposable Enter() { var previous=ambient.Value; ambient.Value=this; return new Scope(previous); }
    private sealed class Scope(OperationPause? previous) : IDisposable { public void Dispose() => ambient.Value=previous; }
    public static void Check(CancellationToken cancellation = default) {
        cancellation.ThrowIfCancellationRequested();
        ambient.Value?.ready.Wait(cancellation);
    }
    public void Dispose() { ready.Set(); ready.Dispose(); }
}
