namespace FolderBackuper.Features.Backups;

public sealed record BackupProgressSnapshot(
    Guid RunId, RunPhase Phase, long FilesProcessed, long DirectoriesProcessed,
    long BytesProcessed, long TotalFiles, long TotalDirectories, long TotalBytes,
    string? CurrentRelativePath, long SourceBytes, long ArchiveBytes, long TransferBytes,
    double ThroughputBytesPerSecond, TimeSpan Elapsed, TimeSpan? EstimatedRemaining,
    bool CancellationAvailable);

public sealed class RollingThroughput
{
    private readonly TimeProvider clock;
    private readonly TimeSpan window;
    private readonly Queue<(long Bytes, long Tick)> samples = new();
    private long lastBytes;
    private long lastTick;
    private const int MaximumSamples = 64;
    public RollingThroughput(TimeProvider? clock = null, TimeSpan? window = null) { this.clock = clock ?? TimeProvider.System; this.window = window ?? TimeSpan.FromSeconds(5); }
    public double Add(long bytes)
    {
        var now = clock.GetTimestamp();
        if (samples.Count > 0 && now < lastTick || bytes < lastBytes) samples.Clear();
        lastBytes = bytes;
        lastTick = now;
        samples.Enqueue((bytes, now));
        while (samples.Count > 1 && (clock.GetElapsedTime(samples.Peek().Tick, now) > window || samples.Count > MaximumSamples)) samples.Dequeue();
        var first = samples.Peek(); var elapsed = clock.GetElapsedTime(first.Tick, now).TotalSeconds;
        return elapsed <= 0 || bytes < first.Bytes ? 0 : (bytes - first.Bytes) / elapsed;
    }
}

public sealed class BackupProgressRegistry
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan interval;
    private readonly Dictionary<Guid, ProgressState> states = new();
    public BackupProgressRegistry(TimeProvider? clock = null, TimeSpan? minimumInterval = null) { this.clock = clock ?? TimeProvider.System; interval = minimumInterval ?? TimeSpan.FromMilliseconds(250); }
    public BackupProgressSnapshot? Current(Guid runId) { lock (gate) return states.TryGetValue(runId, out var state) ? state.Current : null; }
    public IDisposable Subscribe(Guid runId, Action<BackupProgressSnapshot> handler)
    {
        lock (gate) { GetState(runId).Changed += handler; }
        return new Subscription(this, runId, handler);
    }
    public bool Publish(BackupProgressSnapshot snapshot, bool force = false)
    {
        Action<BackupProgressSnapshot>? notify;
        lock (gate)
        {
            var state = GetState(snapshot.RunId);
            var now = clock.GetTimestamp();
            var phaseChanged = state.Current is null || state.Current.Phase != snapshot.Phase;
            if (!force && !phaseChanged && state.LastPublish is long last && clock.GetElapsedTime(last, now) < interval) { state.Current = snapshot; return false; }
            state.Current = snapshot; state.LastPublish = now; notify = state.Changed;
        }
        notify?.Invoke(snapshot); return true;
    }
    private ProgressState GetState(Guid runId) => states.TryGetValue(runId, out var state) ? state : states[runId] = new ProgressState();
    private void Unsubscribe(Guid runId, Action<BackupProgressSnapshot> handler) { lock (gate) if (states.TryGetValue(runId, out var state)) state.Changed -= handler; }
    private sealed class ProgressState { public BackupProgressSnapshot? Current; public long? LastPublish; public Action<BackupProgressSnapshot>? Changed; }
    private sealed class Subscription(BackupProgressRegistry owner, Guid runId, Action<BackupProgressSnapshot> handler) : IDisposable { public void Dispose() => owner.Unsubscribe(runId, handler); }
}
