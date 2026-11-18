namespace PurgeWindows.Core;

/// <summary>Stall levels, uniform across every surface (§4.5).</summary>
public enum StallLevel
{
    /// <summary>Progress is fresh. No stall line.</summary>
    Normal,
    /// <summary>Progress stale ≥ 10s but activity fresh: alive, working on
    /// something hard. Reassurance only, never escalates while activity stays fresh.</summary>
    Slow,
    /// <summary>Both clocks stale ≥ 10s: silence, measured ("No update for {n} seconds.").</summary>
    Silent,
    /// <summary>Both clocks stale ≥ 30s: "It may be stuck…" (amber).</summary>
    Stuck,
    /// <summary>Both clocks stale ≥ 120s: "Still no response…" (danger).</summary>
    Dead,
}

/// <summary>
/// Stall thresholds (PORT-PLAN D-28 proposed defaults, unvalidated on real
/// hardware — keep them named constants, easily tunable, identical on every
/// surface so behaviour is learnable).
/// </summary>
public static class StallThresholds
{
    /// <summary>Progress/activity stale this long → Slow/Silent.</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(10);

    /// <summary>Both clocks stale this long → Stuck.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(30);

    /// <summary>Both clocks stale this long → Dead.</summary>
    public static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(120);

    /// <summary>Stopping taking longer than this shows only "Still stopping…".</summary>
    public static readonly TimeSpan StoppingReassuranceAfter = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The two-clock stall watchdog (§4.5), surface-agnostic: one instance per
/// running surface. The activity clock resets on ANY event (including
/// heartbeats); the progress clock resets only on progress evidence
/// (advancing counters, part/step/check transitions, path movement — what
/// counts is the surface's decision, made by the caller choosing which method
/// to call). Both clocks are evaluated on the 1s UI tick via
/// <see cref="Evaluate"/>. Times are run-elapsed values supplied by the caller
/// (engine event stamps on events, the run stopwatch on ticks), so the
/// detector is deterministic and unit-testable with no clock of its own.
/// <para/>
/// Copy selection lives in the view model: this class reports levels only, so
/// Stop-less surfaces can render factual-only copy and surfaces with Stop can
/// offer it, from the same levels.
/// </summary>
public sealed class StallDetector
{
    TimeSpan _lastActivity;
    TimeSpan _lastProgress;
    bool _started;

    /// <summary>True while the run is stopping: levels are suppressed (the user
    /// already acted; a stuck warning plus a Stop button would be absurd).</summary>
    public bool Suppressed { get; set; }

    /// <summary>True once <see cref="Start"/> has been called for this run.</summary>
    public bool IsStarted => _started;

    /// <summary>Starts both clocks at run elapsed <paramref name="now"/>.</summary>
    public void Start(TimeSpan now)
    {
        _lastActivity = now;
        _lastProgress = now;
        _started = true;
        Suppressed = false;
    }

    /// <summary>Resets both clocks (run ended, or a new run starts on this instance).</summary>
    public void Reset()
    {
        _started = false;
        Suppressed = false;
    }

    /// <summary>
    /// Any event arrived, including a heartbeat: the engine is saying
    /// something. Resets the activity clock only — a heartbeat must never be
    /// mistaken for progress.
    /// </summary>
    public void ObserveActivity(TimeSpan now)
    {
        if (!_started) return;
        if (now > _lastActivity) _lastActivity = now;
    }

    /// <summary>
    /// Progress evidence arrived: counters advanced, or a part/step/check
    /// transitioned. Resets both clocks (progress implies activity).
    /// </summary>
    public void ObserveProgress(TimeSpan now)
    {
        if (!_started) return;
        if (now > _lastActivity) _lastActivity = now;
        if (now > _lastProgress) _lastProgress = now;
    }

    /// <summary>
    /// A unit (part/check) finished, failed, or was cancelled: it ends its own
    /// clocks. The next unit starts fresh; a failed unit never triggers stall
    /// copy and is never named as the stuck item.
    /// </summary>
    public void EndUnit(TimeSpan now)
    {
        if (!_started) return;
        if (now > _lastActivity) _lastActivity = now;
        if (now > _lastProgress) _lastProgress = now;
    }

    /// <summary>How long the activity clock has been stale at <paramref name="now"/>.</summary>
    public TimeSpan ActivityStaleFor(TimeSpan now) =>
        _started ? now - _lastActivity : TimeSpan.Zero;

    /// <summary>How long the progress clock has been stale at <paramref name="now"/>.</summary>
    public TimeSpan ProgressStaleFor(TimeSpan now) =>
        _started ? now - _lastProgress : TimeSpan.Zero;

    /// <summary>
    /// The stall level at run elapsed <paramref name="now"/>. Normal while
    /// stopping is suppressed, and before <see cref="Start"/>. Any qualifying
    /// event returns the surface to Normal immediately — no dwell, no
    /// "recovered" message.
    /// </summary>
    public StallLevel Evaluate(TimeSpan now)
    {
        if (!_started || Suppressed) return StallLevel.Normal;
        var progressStale = now - _lastProgress;
        var activityStale = now - _lastActivity;
        if (progressStale < StallThresholds.SlowAfter) return StallLevel.Normal;
        if (activityStale < StallThresholds.SlowAfter) return StallLevel.Slow;
        if (activityStale < StallThresholds.StuckAfter) return StallLevel.Silent;
        if (activityStale < StallThresholds.DeadAfter) return StallLevel.Stuck;
        return StallLevel.Dead;
    }
}
