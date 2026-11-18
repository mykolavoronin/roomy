namespace PurgeWindows.Core;

/// <summary>What the estimator counts: bytes (clean/restore) or items (registry, size-unknown restore).</summary>
public enum EstimateUnits
{
    Bytes,
    Items,
}

/// <summary>What the time line may show. Only <see cref="Known"/> carries a number.</summary>
public enum EstimateState
{
    /// <summary>Rate unknown: the line reads "Estimating time left…".</summary>
    Unknown,
    /// <summary>Rate known: the line reads "About {remaining} left".</summary>
    Known,
    /// <summary>Remaining under 5s: the line reads "Almost done" (no number).</summary>
    AlmostDone,
    /// <summary>Remaining over 15 minutes: the line reads "More than 15 minutes left" (no number).</summary>
    MoreThan15Minutes,
}

/// <summary>One estimator answer: a display state, plus the quantized remaining time when known.</summary>
public sealed record EstimateDisplay(EstimateState State, TimeSpan? Remaining);

/// <summary>
/// Purge's time formatting, reused exactly (<c>TimeTagline.swift</c>): whole-second
/// forms live ("12 seconds", "1m 5s"); the sub-second form is completion-only.
/// Elapsed and remaining share this formatter, so what the user saw live matches
/// what completion reports ("done in 1m 5s").
/// </summary>
public static class TimeTagline
{
    /// <summary>Live form: whole seconds only. Durations under a second read "0 seconds".</summary>
    public static string Format(TimeSpan duration)
    {
        long totalSeconds = Math.Max(0, (long)Math.Floor(duration.TotalSeconds));
        if (totalSeconds < 60)
            return totalSeconds == 1 ? "1 second" : $"{totalSeconds} seconds";
        long minutes = totalSeconds / 60;
        long seconds = totalSeconds % 60;
        return $"{minutes}m {seconds}s";
    }

    /// <summary>
    /// Completion form: under a second, one decimal with a 0.1 floor ("0.4 seconds");
    /// otherwise the live form. Never used for live display.
    /// </summary>
    public static string FormatCompletion(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1))
        {
            double seconds = Math.Max(0.1, Math.Floor(duration.TotalSeconds * 10) / 10);
            return $"{seconds:0.0} seconds";
        }
        return Format(duration);
    }
}

/// <summary>
/// The remaining-time estimator for Class C surfaces (clean, restore, registry
/// clean), running in the view model — never in the engine. The engine reports
/// measured values only; this class turns event streams into an honest "about
/// how much longer", or into no number at all. Implements PORT-PLAN §4.3/§4.4
/// exactly:
/// <list type="bullet">
/// <item>Progress <c>p = attempted / total</c> (attempted, not removed; the
/// engine caps per-item attempted bytes at the scan size).</item>
/// <item>Rate = EWMA over 1-second buckets, α = 0.15, seeded from the run's own
/// overall average. Silent gaps over 10s are excluded from rate history
/// (active time only). Rates are never persisted: every instance estimates
/// from its own run.</item>
/// <item>EWMA vs overall-average divergence over 3× → unknown.</item>
/// <item>Remaining = (effective total − attempted) / rate. Failed/cancelled
/// parts' unprocessed units leave the denominator via
/// <see cref="ExcludeUnprocessed"/>.</item>
/// <item>An estimate appears only when ALL gates hold: elapsed ≥ 5s, ≥ 3
/// one-second buckets with rate &gt; 0, attempted ≥ 1 MiB or p ≥ 5% (items: ≥ 5
/// items or p ≥ 5%), total ≥ 10 MiB (items: ≥ 10 items).</item>
/// <item>Display quantization: &lt; 5s → Almost done; 5–60s whole seconds;
/// 1–5 min nearest 5s; 5–15 min nearest 30s; &gt; 15 min → "More than 15
/// minutes". The displayed value changes at most once per 2s and only when
/// the new quantized value differs by more than one quantum. Monotonicity is
/// NOT enforced: remaining may rise when the run slows.</item>
/// <item>Rate collapse (10s with no units advancing) → unknown, never infinity
/// or a frozen stale number. On resume the EWMA re-seeds from new buckets.</item>
/// </list>
/// The same model runs on items/sec for the item-count variant (registry
/// clean, size-unknown restore) via <see cref="EstimateUnits.Items"/>.
/// This class never formats user-facing copy beyond the duration itself; the
/// view model composes the time line from <see cref="EstimateDisplay"/>.
/// </summary>
public sealed class ProgressEstimator
{
    /// <summary>EWMA weight per 1-second bucket (effective memory ≈ 6–7s).</summary>
    public const double Alpha = 0.15;

    /// <summary>EWMA/overall divergence ratio beyond which the rate is unknown.</summary>
    public const double DivergenceRatio = 3.0;

    /// <summary>Minimum elapsed before an estimate may appear.</summary>
    public static readonly TimeSpan MinElapsed = TimeSpan.FromSeconds(5);

    /// <summary>Minimum one-second buckets with rate &gt; 0 before an estimate may appear.</summary>
    public const int MinPositiveBuckets = 3;

    /// <summary>Minimum attempted bytes (byte mode) for the progress gate.</summary>
    public const long MinProgressBytes = 1L * 1024 * 1024;

    /// <summary>Minimum total bytes (byte mode) for an estimate to matter.</summary>
    public const long MinTotalBytes = 10L * 1024 * 1024;

    /// <summary>Minimum attempted items (item mode) for the progress gate.</summary>
    public const int MinProgressItems = 5;

    /// <summary>Minimum total items (item mode): the §13.6 "10 MB equivalent".
    /// Registry cleans with fewer items show elapsed-only, by the rule.</summary>
    public const int MinTotalItems = 10;

    /// <summary>Progress fraction that satisfies the progress gate on its own.</summary>
    public const double MinProgressFraction = 0.05;

    /// <summary>Silent gaps longer than this are excluded from rate history.</summary>
    public static readonly TimeSpan SilentGapExclusion = TimeSpan.FromSeconds(10);

    /// <summary>No units advancing for this long collapses the rate to unknown.</summary>
    public static readonly TimeSpan RateCollapseAfter = TimeSpan.FromSeconds(10);

    /// <summary>Minimum time between displayed remaining-value changes.</summary>
    public static readonly TimeSpan DisplayHold = TimeSpan.FromSeconds(2);

    /// <summary>Remaining above this reads "More than 15 minutes left", with no number.</summary>
    public static readonly TimeSpan LongRemainingCap = TimeSpan.FromMinutes(15);

    /// <summary>Remaining below this reads "Almost done", with no number.</summary>
    public static readonly TimeSpan AlmostDoneBelow = TimeSpan.FromSeconds(5);

    readonly long _totalUnits;
    readonly EstimateUnits _units;

    long _attempted;
    long _excluded;
    TimeSpan _lastElapsed;
    bool _hasObservation;
    TimeSpan _activeElapsed;
    TimeSpan? _lastProgressElapsed;

    // Bucket state, keyed by whole active seconds.
    long _openBucketUnits;
    long _openBucketIndex = -1;
    double? _ewma;
    int _positiveBuckets;

    // Hysteresis: the displayed value and when it last changed.
    double? _heldSeconds;
    TimeSpan _heldSince;

    public ProgressEstimator(long totalUnits, EstimateUnits units = EstimateUnits.Bytes)
    {
        _totalUnits = Math.Max(0, totalUnits);
        _units = units;
    }

    /// <summary>Units still expected, after failed/cancelled remainders were excluded.</summary>
    public long EffectiveTotal => Math.Max(0, _totalUnits - _excluded);

    /// <summary>Progress fraction on the effective total, clamped to [0, 1].</summary>
    public double ProgressFraction
    {
        get
        {
            long total = EffectiveTotal;
            if (total <= 0) return _attempted > 0 ? 1 : 0;
            return Math.Clamp((double)_attempted / total, 0, 1);
        }
    }

    internal double? EwmaRate => _ewma;
    internal int PositiveBuckets => _positiveBuckets;
    internal TimeSpan ActiveElapsed => _activeElapsed;

    internal double? OverallRate
    {
        get
        {
            if (_activeElapsed <= TimeSpan.Zero) return null;
            return _attempted / _activeElapsed.TotalSeconds;
        }
    }

    /// <summary>
    /// Records one observation: <paramref name="elapsed"/> is run elapsed (the
    /// engine event's stamp) and <paramref name="attempted"/> the cumulative
    /// units attempted so far. Observations are monotonic: out-of-order or
    /// rewound values are ignored, never applied.
    /// </summary>
    public void Observe(TimeSpan elapsed, long attempted)
    {
        attempted = Math.Max(0, attempted);
        if (_hasObservation && elapsed < _lastElapsed) return;
        long delta = _hasObservation ? Math.Max(0, attempted - _attempted) : attempted;
        TimeSpan dt = _hasObservation ? elapsed - _lastElapsed : TimeSpan.Zero;

        if (!_hasObservation)
        {
            _hasObservation = true;
            _lastElapsed = elapsed;
            _attempted = attempted;
            _openBucketIndex = 0;
            if (attempted > 0) _lastProgressElapsed = elapsed;
            _openBucketUnits += attempted;
            return;
        }

        _lastElapsed = elapsed;
        _attempted = Math.Max(_attempted, attempted);
        if (delta > 0) _lastProgressElapsed = elapsed;

        // A silent gap over 10s is excluded from rate history: active time does
        // not advance, the EWMA and bucket counts reset, and the estimator
        // re-seeds from new buckets when units resume. Bytes that arrive with
        // the resume join the next open bucket; the 3-bucket gate and the
        // divergence guard absorb that one bucket's distortion.
        if (dt > SilentGapExclusion && delta == 0)
        {
            _ewma = null;
            _positiveBuckets = 0;
            _openBucketUnits = 0;
            return;
        }
        if (dt > SilentGapExclusion)
        {
            _ewma = null;
            _positiveBuckets = 0;
            _openBucketUnits = 0;
            _openBucketUnits += delta;
            return;
        }

        _activeElapsed += dt;
        _openBucketUnits += delta;
        CloseBucketsUpTo((long)Math.Floor(_activeElapsed.TotalSeconds));
    }

    /// <summary>
    /// Removes unprocessed units from the denominator: a failed or cancelled
    /// part's remainder will never be attempted, so it must not inflate the
    /// remaining time. Never moves the numerator.
    /// </summary>
    public void ExcludeUnprocessed(long units)
    {
        if (units > 0) _excluded += units;
    }

    /// <summary>
    /// Answers the display question for run elapsed <paramref name="now"/>:
    /// unknown, known (with the quantized remaining time), almost done, or
    /// more than 15 minutes. Call on the 1s UI tick and on progress events;
    /// hysteresis is applied here, so calling often is safe.
    /// </summary>
    public EstimateDisplay Evaluate(TimeSpan now)
    {
        if (_totalUnits <= 0 || !_hasObservation)
            return Unknown();

        long remaining = Math.Max(0, EffectiveTotal - _attempted);
        if (remaining == 0)
            return AlmostDone();

        // Rate collapse: 10s with no units advancing → unknown, immediately.
        // No infinity, no frozen stale number, no extrapolation from zero.
        if (_lastProgressElapsed is { } last && now - last >= RateCollapseAfter)
            return Unknown();
        if (_lastProgressElapsed is null && now >= RateCollapseAfter)
            return Unknown();

        // Minimum-samples gates: ALL must hold.
        if (now < MinElapsed) return Unknown();
        if (_positiveBuckets < MinPositiveBuckets) return Unknown();
        if (_units == EstimateUnits.Bytes)
        {
            if (_totalUnits < MinTotalBytes) return Unknown();
            if (_attempted < MinProgressBytes && ProgressFraction < MinProgressFraction) return Unknown();
        }
        else
        {
            if (_totalUnits < MinTotalItems) return Unknown();
            if (_attempted < MinProgressItems && ProgressFraction < MinProgressFraction) return Unknown();
        }

        double? ewma = _ewma;
        double? overall = OverallRate;
        if (ewma is null or <= 0 || overall is null or <= 0) return Unknown();

        // Divergence guard: when two honest estimators disagree by more than
        // 3×, the honest display is no number.
        double ratio = Math.Max(ewma.Value, overall.Value) / Math.Min(ewma.Value, overall.Value);
        if (ratio > DivergenceRatio) return Unknown();

        double rawSeconds = remaining / ewma.Value;
        if (rawSeconds < AlmostDoneBelow.TotalSeconds) return AlmostDone();
        if (rawSeconds > LongRemainingCap.TotalSeconds) return Long();

        double quantized = Quantize(rawSeconds);
        return KnownWithHysteresis(quantized, now);
    }

    EstimateDisplay Unknown()
    {
        _heldSeconds = null;
        return new EstimateDisplay(EstimateState.Unknown, null);
    }

    EstimateDisplay AlmostDone()
    {
        _heldSeconds = null;
        return new EstimateDisplay(EstimateState.AlmostDone, null);
    }

    EstimateDisplay Long()
    {
        _heldSeconds = null;
        return new EstimateDisplay(EstimateState.MoreThan15Minutes, null);
    }

    EstimateDisplay KnownWithHysteresis(double quantizedSeconds, TimeSpan now)
    {
        if (_heldSeconds is null)
        {
            _heldSeconds = quantizedSeconds;
            _heldSince = now;
        }
        else if (now - _heldSince >= DisplayHold &&
                 Math.Abs(quantizedSeconds - _heldSeconds.Value) > QuantumFor(quantizedSeconds))
        {
            _heldSeconds = quantizedSeconds;
            _heldSince = now;
        }
        return new EstimateDisplay(EstimateState.Known, TimeSpan.FromSeconds(_heldSeconds.Value));
    }

    /// <summary>Closes every whole bucket before <paramref name="currentBucketIndex"/>.</summary>
    void CloseBucketsUpTo(long currentBucketIndex)
    {
        // The open bucket belongs to _openBucketIndex; buckets strictly before
        // the current one are complete. Intermediate buckets with no units are
        // real zero-rate buckets (a slow phase, not a gap) and decay the EWMA.
        while (_openBucketIndex < currentBucketIndex)
        {
            CloseBucket(_openBucketUnits);
            _openBucketUnits = 0;
            _openBucketIndex++;
        }
    }

    void CloseBucket(long units)
    {
        double rate = units; // units per 1-second bucket
        if (rate > 0)
        {
            _positiveBuckets++;
            if (_ewma is null)
            {
                // Seeded from the run's own overall average, never a constant
                // or a previous run.
                _ewma = OverallRate ?? rate;
            }
            else
            {
                _ewma = Alpha * rate + (1 - Alpha) * _ewma.Value;
            }
        }
        else if (_ewma is not null)
        {
            _ewma = Alpha * 0 + (1 - Alpha) * _ewma.Value;
        }
    }

    /// <summary>Quantizes a raw remaining-seconds value to its display band.</summary>
    internal static double Quantize(double rawSeconds)
    {
        double quantum = QuantumFor(rawSeconds);
        return Math.Round(rawSeconds / quantum, MidpointRounding.AwayFromZero) * quantum;
    }

    /// <summary>The quantum for a remaining-seconds value's band.</summary>
    internal static double QuantumFor(double seconds) =>
        seconds < 60 ? 1 : seconds < 300 ? 5 : 30;
}
