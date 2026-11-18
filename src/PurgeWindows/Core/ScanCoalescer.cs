namespace PurgeWindows.Core;

/// <summary>
/// Coalesces rapid scan-progress events (Purge's <c>ScanCoalesce</c>, 140 ms):
/// Active updates forward at most once per interval, keeping the latest event;
/// boundary events (Done/Failed/Cancelled) always forward immediately, and
/// <see cref="Flush"/> forwards anything pending. Delivery never throws into the
/// engine. The caller owns flushing at the end of a run.
/// </summary>
public sealed class ScanCoalescer : IProgress<ScanProgressEvent>, IDisposable
{
    /// <summary>Purge's coalesce interval: 140 ms.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(140);

    readonly IProgress<ScanProgressEvent> _sink;
    readonly TimeSpan _interval;
    readonly object _gate = new();
    readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    ScanProgressEvent? _pending;
    long _lastForwardMs = -10_000;
    bool _disposed;

    public ScanCoalescer(IProgress<ScanProgressEvent> sink, TimeSpan? interval = null)
    {
        _sink = sink;
        _interval = interval ?? DefaultInterval;
    }

    public void Report(ScanProgressEvent value)
    {
        ScanProgressEvent? forward = null;
        lock (_gate)
        {
            if (_disposed) return;
            bool boundary = value.State is not ScanStepState.Active;
            long now = _watch.ElapsedMilliseconds;
            if (boundary || now - _lastForwardMs >= _interval.TotalMilliseconds)
            {
                _pending = null;
                _lastForwardMs = now;
                forward = value;
            }
            else
            {
                _pending = value;
            }
        }
        if (forward is not null) Forward(forward);
    }

    /// <summary>Forwards the latest pending event, if any.</summary>
    public void Flush()
    {
        ScanProgressEvent? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            if (pending is not null) _lastForwardMs = _watch.ElapsedMilliseconds;
        }
        if (pending is not null) Forward(pending);
    }

    void Forward(ScanProgressEvent value)
    {
        try { _sink.Report(value); } catch { /* a listener must never break the scan */ }
    }

    public void Dispose()
    {
        Flush();
        lock (_gate) { _disposed = true; _pending = null; }
    }
}
