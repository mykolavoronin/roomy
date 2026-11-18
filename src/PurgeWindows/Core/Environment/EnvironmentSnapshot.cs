namespace PurgeWindows.Core.Environment;

/// <summary>
/// An immutable snapshot of all 12 probe results plus the session facts gating needs.
/// The mod score is derived from the probe outcomes on access, so a snapshot (including
/// merged copies built with <c>with</c>) always carries the verdict for its own outcomes.
/// </summary>
public sealed record EnvironmentSnapshot(
    ProbeOutcome<OsInfo> Os,
    ProbeOutcome<ComponentPresence> Components,
    ProbeOutcome<ModMarkerEvidence> ModMarkers,
    ProbeOutcome<AntivirusInfo> Antivirus,
    ProbeOutcome<ServiceStates> Services,
    ProbeOutcome<UpdateState> Updates,
    ProbeOutcome<StorageSenseInfo> StorageSense,
    ProbeOutcome<OneDriveInfo> OneDrive,
    ProbeOutcome<ManagedInfo> Managed,
    ProbeOutcome<VirtualizationInfo> Virtualization,
    ProbeOutcome<HardwareContext> Hardware,
    ProbeOutcome<PendingOperations> Pending,
    /// <summary>Whether the process runs elevated. Null when that could not be determined.</summary>
    bool? IsElevated,
    DateTimeOffset DetectedAt)
{
    /// <summary>
    /// The mod score, derived from the probe outcomes. Computed on access so merged snapshots
    /// (built with <c>with</c>) always carry the verdict for their own outcomes.
    /// </summary>
    public ModScoreResult ModScore => ModScoring.Evaluate(Components, ModMarkers, Services, Updates, Virtualization);

    /// <summary>Every probe outcome, in P-01–P-12 order, for disclosure and caching.</summary>
    public IReadOnlyList<(string Id, bool IsKnown, ProbeUnknownReason? Reason, TimeSpan Duration)> ProbeSummaries() =>
    [
        (ProbeIds.Os, Os.IsKnown, Os.UnknownReason, Os.Duration),
        (ProbeIds.Components, Components.IsKnown, Components.UnknownReason, Components.Duration),
        (ProbeIds.ModMarkers, ModMarkers.IsKnown, ModMarkers.UnknownReason, ModMarkers.Duration),
        (ProbeIds.Antivirus, Antivirus.IsKnown, Antivirus.UnknownReason, Antivirus.Duration),
        (ProbeIds.Services, Services.IsKnown, Services.UnknownReason, Services.Duration),
        (ProbeIds.Updates, Updates.IsKnown, Updates.UnknownReason, Updates.Duration),
        (ProbeIds.StorageSense, StorageSense.IsKnown, StorageSense.UnknownReason, StorageSense.Duration),
        (ProbeIds.OneDrive, OneDrive.IsKnown, OneDrive.UnknownReason, OneDrive.Duration),
        (ProbeIds.Managed, Managed.IsKnown, Managed.UnknownReason, Managed.Duration),
        (ProbeIds.Virtualization, Virtualization.IsKnown, Virtualization.UnknownReason, Virtualization.Duration),
        (ProbeIds.Hardware, Hardware.IsKnown, Hardware.UnknownReason, Hardware.Duration),
        (ProbeIds.Pending, Pending.IsKnown, Pending.UnknownReason, Pending.Duration),
    ];

    /// <summary>An all-Unknown snapshot: the safe default before detection runs, and what demo mode uses (no probing at all).</summary>
    public static EnvironmentSnapshot Unknown(DateTimeOffset detectedAt)
    {
        var stamp = detectedAt;
        ProbeOutcome<T> UnknownOutcome<T>() => ProbeOutcome<T>.Unknown(ProbeUnknownReason.NotAvailable, TimeSpan.Zero, stamp);
        return new EnvironmentSnapshot(
            UnknownOutcome<OsInfo>(), UnknownOutcome<ComponentPresence>(), UnknownOutcome<ModMarkerEvidence>(),
            UnknownOutcome<AntivirusInfo>(), UnknownOutcome<ServiceStates>(), UnknownOutcome<UpdateState>(),
            UnknownOutcome<StorageSenseInfo>(), UnknownOutcome<OneDriveInfo>(), UnknownOutcome<ManagedInfo>(),
            UnknownOutcome<VirtualizationInfo>(), UnknownOutcome<HardwareContext>(), UnknownOutcome<PendingOperations>(),
            null, stamp);
    }

    /// <summary>A copy of this snapshot with one probe outcome replaced (used when merging fresh results into cached ones).</summary>
    public EnvironmentSnapshot With(string probeId, object outcome) => probeId switch
    {
        ProbeIds.Os => this with { Os = (ProbeOutcome<OsInfo>)outcome },
        ProbeIds.Components => this with { Components = (ProbeOutcome<ComponentPresence>)outcome },
        ProbeIds.ModMarkers => this with { ModMarkers = (ProbeOutcome<ModMarkerEvidence>)outcome },
        ProbeIds.Antivirus => this with { Antivirus = (ProbeOutcome<AntivirusInfo>)outcome },
        ProbeIds.Services => this with { Services = (ProbeOutcome<ServiceStates>)outcome },
        ProbeIds.Updates => this with { Updates = (ProbeOutcome<UpdateState>)outcome },
        ProbeIds.StorageSense => this with { StorageSense = (ProbeOutcome<StorageSenseInfo>)outcome },
        ProbeIds.OneDrive => this with { OneDrive = (ProbeOutcome<OneDriveInfo>)outcome },
        ProbeIds.Managed => this with { Managed = (ProbeOutcome<ManagedInfo>)outcome },
        ProbeIds.Virtualization => this with { Virtualization = (ProbeOutcome<VirtualizationInfo>)outcome },
        ProbeIds.Hardware => this with { Hardware = (ProbeOutcome<HardwareContext>)outcome },
        ProbeIds.Pending => this with { Pending = (ProbeOutcome<PendingOperations>)outcome },
        _ => this,
    };
}

/// <summary>Per-probe timeouts (W5 §5.2) plus the overall detection budget (W5 §5.4: 15 seconds).</summary>
public sealed record ProbeTimeouts(
    TimeSpan Os,
    TimeSpan Components,
    TimeSpan ModMarkers,
    TimeSpan Antivirus,
    TimeSpan Services,
    TimeSpan Updates,
    TimeSpan StorageSense,
    TimeSpan OneDrive,
    TimeSpan Managed,
    TimeSpan Virtualization,
    TimeSpan Hardware,
    TimeSpan Pending,
    TimeSpan Total)
{
    /// <summary>
    /// The plan's timeouts. "Instant" probes (P-01, P-07) get a 2-second cap so a hung registry
    /// read can never stall detection; the W3 total of 10s is superseded by W5 §5.4's 15s.
    /// </summary>
    public static ProbeTimeouts Default { get; } = new(
        Os: TimeSpan.FromSeconds(2),
        Components: TimeSpan.FromSeconds(5),
        ModMarkers: TimeSpan.FromSeconds(3),
        Antivirus: TimeSpan.FromSeconds(3),
        Services: TimeSpan.FromSeconds(2),
        Updates: TimeSpan.FromSeconds(2),
        StorageSense: TimeSpan.FromSeconds(2),
        OneDrive: TimeSpan.FromSeconds(2),
        Managed: TimeSpan.FromSeconds(2),
        Virtualization: TimeSpan.FromSeconds(10),
        Hardware: TimeSpan.FromSeconds(2),
        Pending: TimeSpan.FromSeconds(2),
        Total: TimeSpan.FromSeconds(15));
}

/// <summary>Runs the 12 probes and returns a snapshot. Detection never blocks startup and never elevates.</summary>
public interface IEnvironmentDetector
{
    /// <summary>
    /// Detects the environment, reusing cached probe results inside their TTL. With
    /// <paramref name="forceRefresh"/>, every probe runs fresh. Safe to call concurrently:
    /// overlapping calls are serialised and share one run's results.
    /// </summary>
    Task<EnvironmentSnapshot> DetectAsync(bool forceRefresh = false, CancellationToken ct = default);
}

/// <summary>
/// The production detector: runs probes concurrently on worker threads with per-probe
/// timeouts and a 15-second overall budget, caches results in session memory with the W5
/// TTLs (5 minutes for volatile AV/service/update state, 60 minutes for stable OS/build
/// facts), and merges fresh results into the cached snapshot. A probe that times out or
/// throws is Unknown — never a retry loop, never a block.
/// </summary>
public sealed class EnvironmentDetector : IEnvironmentDetector
{
    private readonly EnvironmentDataSource _source;
    private readonly ProbeTimeouts _timeouts;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EnvironmentSnapshot? _cached;

    /// <summary>Volatile probes (W5 §5.4): AV, services, updates — plus Storage Sense, OneDrive, and pending ops, which change just as fast.</summary>
    public static readonly TimeSpan VolatileTtl = TimeSpan.FromMinutes(5);

    /// <summary>Stable probes (W5 §5.4): OS, components, mod markers, managed, hardware — plus virtualization, which rarely changes mid-session.</summary>
    public static readonly TimeSpan StableTtl = TimeSpan.FromMinutes(60);

    public EnvironmentDetector(EnvironmentDataSource source) : this(source, ProbeTimeouts.Default, () => DateTimeOffset.UtcNow) { }

    internal EnvironmentDetector(EnvironmentDataSource source, ProbeTimeouts timeouts, Func<DateTimeOffset> clock)
    {
        _source = source;
        _timeouts = timeouts;
        _clock = clock;
    }

    public static TimeSpan TtlFor(string probeId) => probeId switch
    {
        ProbeIds.Antivirus or ProbeIds.Services or ProbeIds.Updates or ProbeIds.StorageSense
            or ProbeIds.OneDrive or ProbeIds.Pending => VolatileTtl,
        _ => StableTtl,
    };

    public async Task<EnvironmentSnapshot> DetectAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock();
            if (forceRefresh) _source.ClearCaches();

            var previous = _cached;
            var needsRun = forceRefresh || previous is null;
            bool IsStale(string probeId, DateTimeOffset probedAt) =>
                needsRun || now - probedAt >= TtlFor(probeId);

            var runners = new Dictionary<string, Func<CancellationToken, Task<object>>>
            {
                [ProbeIds.Os] = async token => (object)await RunProbe(token, _timeouts.Os,
                    t => Task.Run(() => EnvironmentProbes.ProbeOs(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.Components] = async token => (object)await RunProbe(token, _timeouts.Components,
                    t => Task.Run(() => EnvironmentProbes.ProbeComponents(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.ModMarkers] = async token => (object)await RunProbe(token, _timeouts.ModMarkers,
                    t => Task.Run(() => EnvironmentProbes.ProbeModMarkers(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.Antivirus] = async token => (object)await RunProbe(token, _timeouts.Antivirus,
                    t => Task.Run(() => EnvironmentProbes.ProbeAntivirus(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.Services] = async token => (object)await RunProbe(token, _timeouts.Services,
                    t => Task.Run(() => EnvironmentProbes.ProbeServices(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.Updates] = async token => (object)await RunProbe(token, _timeouts.Updates,
                    t => Task.Run(() => EnvironmentProbes.ProbeUpdates(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.StorageSense] = async token => (object)await RunProbe(token, _timeouts.StorageSense,
                    t => Task.Run(() => EnvironmentProbes.ProbeStorageSense(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.OneDrive] = async token => (object)await RunProbe(token, _timeouts.OneDrive,
                    t => Task.Run(() => EnvironmentProbes.ProbeOneDrive(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.Managed] = async token => (object)await RunProbe(token, _timeouts.Managed,
                    t => Task.Run(() => EnvironmentProbes.ProbeManaged(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.Virtualization] = async token => (object)await RunProbe(token, _timeouts.Virtualization,
                    t => Task.Run(() => EnvironmentProbes.ProbeVirtualization(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.Hardware] = async token => (object)await RunProbe(token, _timeouts.Hardware,
                    t => Task.Run(() => EnvironmentProbes.ProbeHardware(_source, t), t))\.ConfigureAwait(false),
                [ProbeIds.Pending] = async token => (object)await RunProbe(token, _timeouts.Pending,
                    t => Task.Run(() => EnvironmentProbes.ProbePending(_source, t), t))\.ConfigureAwait(false),
            };

            // Fresh results for stale probes; cached outcomes for the rest. Unknown outcomes
            // are never cached as fresh: an Unknown probe re-runs on the next detection.
            var baseSnapshot = previous ?? EnvironmentSnapshot.Unknown(now);
            var snapshot = baseSnapshot;
            var running = new List<(string Id, Task<object> Task)>();
            foreach (var probeId in ProbeIds.All)
            {
                var existing = ExistingOutcome(baseSnapshot, probeId);
                var stale = existing is null || !existing.IsKnown || IsStale(probeId, existing.ProbedAt);
                if (stale) running.Add((probeId, runners[probeId](ct)));
            }

            if (running.Count > 0)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(_timeouts.Total);
                var all = Task.WhenAll(running.Select(r => r.Task));
                try { await all.WaitAsync(budget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* The budget expired; finished probes merge, the rest stay Unknown. */ }

                foreach (var (probeId, task) in running)
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                        snapshot = snapshot.With(probeId, task.Result);
                    else
                        snapshot = snapshot.With(probeId, TimedOutOutcome(probeId, now));
                }
                // Elevation is a session fact: refresh it whenever any probe runs.
                snapshot = snapshot with { IsElevated = ReadElevation(), DetectedAt = now };
            }

            _cached = snapshot;
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    bool? ReadElevation()
    {
        try { return _source.GetSystemInfo().IsElevated; }
        catch { return null; }
    }

    /// <summary>Runs one probe with its timeout. A probe that never returns is abandoned and reported TimedOut.</summary>
    static async Task<ProbeOutcome<T>> RunProbe<T>(
        CancellationToken outer, TimeSpan timeout, Func<CancellationToken, Task<ProbeOutcome<T>>> run)
    {
        var started = DateTimeOffset.UtcNow;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var probeTask = run(timeoutSource.Token);
            var completed = await Task.WhenAny(probeTask, Task.Delay(timeout + TimeSpan.FromMilliseconds(50), outer)).ConfigureAwait(false);
            if (completed != probeTask)
                return ProbeOutcome<T>.Unknown(ProbeUnknownReason.TimedOut, timeout, started);
            return await probeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<T>.Unknown(ProbeUnknownReason.TimedOut, DateTimeOffset.UtcNow - started, started);
        }
        catch
        {
            return ProbeOutcome<T>.Unknown(ProbeUnknownReason.QueryFailed, DateTimeOffset.UtcNow - started, started);
        }
    }

    static (bool IsKnown, DateTimeOffset ProbedAt)? ExistingOutcome(EnvironmentSnapshot snapshot, string probeId) =>
        probeId switch
        {
            ProbeIds.Os => (snapshot.Os.IsKnown, snapshot.Os.ProbedAt),
            ProbeIds.Components => (snapshot.Components.IsKnown, snapshot.Components.ProbedAt),
            ProbeIds.ModMarkers => (snapshot.ModMarkers.IsKnown, snapshot.ModMarkers.ProbedAt),
            ProbeIds.Antivirus => (snapshot.Antivirus.IsKnown, snapshot.Antivirus.ProbedAt),
            ProbeIds.Services => (snapshot.Services.IsKnown, snapshot.Services.ProbedAt),
            ProbeIds.Updates => (snapshot.Updates.IsKnown, snapshot.Updates.ProbedAt),
            ProbeIds.StorageSense => (snapshot.StorageSense.IsKnown, snapshot.StorageSense.ProbedAt),
            ProbeIds.OneDrive => (snapshot.OneDrive.IsKnown, snapshot.OneDrive.ProbedAt),
            ProbeIds.Managed => (snapshot.Managed.IsKnown, snapshot.Managed.ProbedAt),
            ProbeIds.Virtualization => (snapshot.Virtualization.IsKnown, snapshot.Virtualization.ProbedAt),
            ProbeIds.Hardware => (snapshot.Hardware.IsKnown, snapshot.Hardware.ProbedAt),
            ProbeIds.Pending => (snapshot.Pending.IsKnown, snapshot.Pending.ProbedAt),
            _ => null,
        };

    static object TimedOutOutcome(string probeId, DateTimeOffset now) => probeId switch
    {
        ProbeIds.Os => ProbeOutcome<OsInfo>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.Components => ProbeOutcome<ComponentPresence>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.ModMarkers => ProbeOutcome<ModMarkerEvidence>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.Antivirus => ProbeOutcome<AntivirusInfo>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.Services => ProbeOutcome<ServiceStates>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.Updates => ProbeOutcome<UpdateState>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.StorageSense => ProbeOutcome<StorageSenseInfo>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.OneDrive => ProbeOutcome<OneDriveInfo>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.Managed => ProbeOutcome<ManagedInfo>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.Virtualization => ProbeOutcome<VirtualizationInfo>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.Hardware => ProbeOutcome<HardwareContext>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        ProbeIds.Pending => ProbeOutcome<PendingOperations>.Unknown(ProbeUnknownReason.TimedOut, TimeSpan.Zero, now),
        _ => throw new ArgumentOutOfRangeException(nameof(probeId)),
    };
}

/// <summary>
/// A detector that never probes: returns the all-Unknown snapshot. Used for demo mode, where
/// no real machine state may be read, and as the safe fallback when detection is disabled.
/// </summary>
public sealed class UnknownEnvironmentDetector : IEnvironmentDetector
{
    public Task<EnvironmentSnapshot> DetectAsync(bool forceRefresh = false, CancellationToken ct = default) =>
        Task.FromResult(EnvironmentSnapshot.Unknown(DateTimeOffset.UtcNow));
}
