using System.Diagnostics;

namespace PurgeWindows.Core;

/// <summary>What the watchdog currently believes about the worker.</summary>
public enum WatchdogState
{
    /// <summary>Heartbeats are arriving on time (or the run just started).</summary>
    Alive,
    /// <summary>No heartbeat for <see cref="CleanWatchdog.WarnAfter"/>: possibly stuck.</summary>
    Silent,
    /// <summary>No heartbeat for <see cref="CleanWatchdog.StuckAfter"/>: stuck.</summary>
    Stuck,
}

/// <summary>
/// Observes worker heartbeats from its own timer thread and records silence (clean log +
/// diagnostic state). It deliberately NEVER kills, cancels, or otherwise touches the worker:
/// a single in-flight shell call cannot be cancelled, and killing mid-batch would leave
/// accounting dishonest. Surfacing ("this step is taking unusually long" + Stop +
/// diagnostics) is a later wave's UI job, built on <see cref="State"/> and the log.
/// One watchdog per run; dispose it when the run ends.
/// </summary>
public sealed class CleanWatchdog : IDisposable
{
    readonly Timer _timer;
    readonly Stopwatch _sincePulse = Stopwatch.StartNew();
    readonly object _gate = new();
    CleanPhase _phase;
    string _lastPath = "";
    WatchdogState _state = WatchdogState.Alive;
    bool _warnLogged, _stuckLogged, _disposed;

    /// <param name="warnAfter">Silence that counts as "may be stuck".</param>
    /// <param name="stuckAfter">Silence that counts as "stuck". Must exceed <paramref name="warnAfter"/>.</param>
    /// <param name="checkInterval">How often the timer re-checks. Defaults to 1 second.</param>
    public CleanWatchdog(TimeSpan warnAfter, TimeSpan stuckAfter, TimeSpan? checkInterval = null)
    {
        WarnAfter = warnAfter;
        StuckAfter = stuckAfter > warnAfter ? stuckAfter : warnAfter + TimeSpan.FromSeconds(1);
        _timer = new Timer(Check, null, checkInterval ?? TimeSpan.FromSeconds(1), checkInterval ?? TimeSpan.FromSeconds(1));
    }

    public TimeSpan WarnAfter { get; }
    public TimeSpan StuckAfter { get; }

    /// <summary>Current belief about the worker. Thread-safe.</summary>
    public WatchdogState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>How long since the last heartbeat. Thread-safe.</summary>
    public TimeSpan Silence
    {
        get { lock (_gate) return _sincePulse.Elapsed; }
    }

    /// <summary>Phase and path of the last heartbeat. Thread-safe.</summary>
    public (CleanPhase Phase, string LastPath) LastKnown
    {
        get { lock (_gate) return (_phase, _lastPath); }
    }

    /// <summary>True once any silence was recorded during this run (even if the worker recovered).</summary>
    public bool WasSilent
    {
        get { lock (_gate) return _warnLogged || _stuckLogged; }
    }

    /// <summary>Called by the worker on every heartbeat/progress event. Cheap and never throws.</summary>
    public void Pulse(CleanPhase phase, string currentPath)
    {
        try
        {
            lock (_gate)
            {
                _phase = phase;
                _lastPath = currentPath;
                _sincePulse.Restart();
                _state = WatchdogState.Alive;
                _warnLogged = false;
                _stuckLogged = false;
            }
        }
        catch { /* a heartbeat must never break the worker */ }
    }

    void Check(object? _)
    {
        try
        {
            CleanPhase phase;
            string path;
            TimeSpan silence;
            bool logWarn, logStuck;
            lock (_gate)
            {
                if (_disposed) return;
                silence = _sincePulse.Elapsed;
                phase = _phase;
                path = _lastPath;
                logWarn = !_warnLogged && silence >= WarnAfter;
                logStuck = !_stuckLogged && silence >= StuckAfter;
                if (logStuck) { _state = WatchdogState.Stuck; _stuckLogged = true; _warnLogged = true; }
                else if (logWarn) { _state = WatchdogState.Silent; _warnLogged = true; }
            }
            // Log outside the lock: file I/O must never hold the pulse path up.
            if (logStuck) CleanLog.WriteWatchdog("STUCK", silence, phase, path);
            else if (logWarn) CleanLog.WriteWatchdog("silent", silence, phase, path);
        }
        catch { /* the watchdog must never throw on its timer thread */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
    }
}
