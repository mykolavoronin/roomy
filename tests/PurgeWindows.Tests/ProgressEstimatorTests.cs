using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// The remaining-time estimator (§4.3/§4.4), driven by synthetic event streams:
/// unknown→known transitions, minimum-samples gates, quantization boundaries,
/// hysteresis, rate collapse, rebaselining, the item-count variant, and the
/// 3× divergence guard. All times are run-elapsed values supplied directly, so
/// the tests are deterministic with no clock involved.
/// </summary>
public sealed class ProgressEstimatorTests
{
    const long MB = 1024L * 1024;

    /// <summary>Feeds a steady stream: <paramref name="perSecond"/> units every second, 0..lastSecond.</summary>
    static ProgressEstimator SteadyFeed(long total, long perSecond, int lastSecond,
        EstimateUnits units = EstimateUnits.Bytes)
    {
        var e = new ProgressEstimator(total, units);
        for (int s = 0; s <= lastSecond; s++)
            e.Observe(TimeSpan.FromSeconds(s), perSecond * s);
        return e;
    }

    [Fact]
    public void UnknownBeforeMinimumElapsed()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 3);

        var display = e.Evaluate(TimeSpan.FromSeconds(3));

        Assert.Equal(EstimateState.Unknown, display.State);
    }

    [Fact]
    public void UnknownWithFewerThanThreePositiveBuckets()
    {
        // One observation after 5s of silence: a single burst bucket, however
        // big, must not set the estimate.
        var e = new ProgressEstimator(600 * MB);
        e.Observe(TimeSpan.Zero, 0);
        e.Observe(TimeSpan.FromSeconds(5), 50 * MB);

        Assert.Equal(EstimateState.Unknown, e.Evaluate(TimeSpan.FromSeconds(5)).State);
        Assert.Equal(1, e.PositiveBuckets);
    }

    [Fact]
    public void UnknownWhenTotalBelowTenMegabytes()
    {
        var e = SteadyFeed(5 * MB, 1 * MB, 5);

        Assert.Equal(EstimateState.Unknown, e.Evaluate(TimeSpan.FromSeconds(5)).State);
    }

    [Fact]
    public void UnknownWhenProgressBelowOneMegabyteAndFivePercent()
    {
        // 500 KB attempted of 600 MB: neither progress gate holds.
        var e = SteadyFeed(600 * MB, 100 * 1024, 5);

        Assert.Equal(EstimateState.Unknown, e.Evaluate(TimeSpan.FromSeconds(5)).State);
    }

    [Fact]
    public void FivePercentProgressSatisfiesTheProgressGate()
    {
        // 550 KiB of 10 MiB is ~5.4%: the fraction gate holds without 1 MiB attempted.
        var e = SteadyFeed(10 * MB, 110 * 1024, 5);

        var display = e.Evaluate(TimeSpan.FromSeconds(5));

        Assert.Equal(EstimateState.Known, display.State);
        // (10 MiB − 550 KiB) / 110 KiB/s = 88.1s → 88s.
        Assert.Equal(TimeSpan.FromSeconds(88), display.Remaining);
    }

    [Fact]
    public void SteadyRunBecomesKnownWithTheRightRemainingTime()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);

        var display = e.Evaluate(TimeSpan.FromSeconds(5));

        // (600 − 50) MB at 10 MB/s = 55s.
        Assert.Equal(EstimateState.Known, display.State);
        Assert.Equal(TimeSpan.FromSeconds(55), display.Remaining);
    }

    [Fact]
    public void QuantizationBands()
    {
        Assert.Equal(59, ProgressEstimator.Quantize(59.4));
        Assert.Equal(60, ProgressEstimator.Quantize(59.6));
        Assert.Equal(60, ProgressEstimator.Quantize(62));
        Assert.Equal(65, ProgressEstimator.Quantize(63));
        Assert.Equal(65, ProgressEstimator.Quantize(62.5));
        Assert.Equal(300, ProgressEstimator.Quantize(299));
        Assert.Equal(300, ProgressEstimator.Quantize(314));
        Assert.Equal(330, ProgressEstimator.Quantize(315));
        Assert.Equal(900, ProgressEstimator.Quantize(900));
    }

    [Fact]
    public void AlmostDoneBelowFiveSeconds()
    {
        var e = SteadyFeed(200 * MB, 10 * MB, 16);

        // (200 − 160) MB at 10 MB/s = 4s of raw remaining: no number.
        Assert.Equal(EstimateState.AlmostDone, e.Evaluate(TimeSpan.FromSeconds(16)).State);
    }

    [Fact]
    public void FiveSecondsRemainingStillShowsANumber()
    {
        var e = SteadyFeed(200 * MB, 10 * MB, 15);

        var display = e.Evaluate(TimeSpan.FromSeconds(15));

        Assert.Equal(EstimateState.Known, display.State);
        Assert.Equal(TimeSpan.FromSeconds(5), display.Remaining);
    }

    [Fact]
    public void MoreThanFifteenMinutesShowsNoNumber()
    {
        var e = SteadyFeed(6000 * MB, 1 * MB, 5);

        Assert.Equal(EstimateState.MoreThan15Minutes, e.Evaluate(TimeSpan.FromSeconds(5)).State);
    }

    [Fact]
    public void FifteenMinutesShowsANumber()
    {
        // 900s raw remaining, quantized to the 30s band: "About 15m 0s left".
        var e = SteadyFeed(905 * MB, 1 * MB, 5);

        var display = e.Evaluate(TimeSpan.FromSeconds(5));

        Assert.Equal(EstimateState.Known, display.State);
        Assert.Equal(TimeSpan.FromSeconds(900), display.Remaining);
    }

    [Fact]
    public void DisplayedValueHoldsForTwoSeconds()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);
        Assert.Equal(TimeSpan.FromSeconds(55), e.Evaluate(TimeSpan.FromSeconds(5)).Remaining);
        e.Observe(TimeSpan.FromSeconds(6), 60 * MB);

        // Raw remaining is now 54s, but the display may change at most 1×/2s.
        Assert.Equal(TimeSpan.FromSeconds(55), e.Evaluate(TimeSpan.FromSeconds(6)).Remaining);
    }

    [Fact]
    public void DisplayedValueUpdatesAfterTwoSecondsWhenChangeExceedsOneQuantum()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);
        Assert.Equal(TimeSpan.FromSeconds(55), e.Evaluate(TimeSpan.FromSeconds(5)).Remaining);
        e.Observe(TimeSpan.FromSeconds(6), 60 * MB);
        e.Observe(TimeSpan.FromSeconds(7), 70 * MB);

        // Raw 53s: 2s elapsed and the change (2s) exceeds one 1s quantum.
        Assert.Equal(TimeSpan.FromSeconds(53), e.Evaluate(TimeSpan.FromSeconds(7)).Remaining);
    }

    [Fact]
    public void DisplayedValueHoldsWhenChangeIsAtMostOneQuantum()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);
        Assert.Equal(TimeSpan.FromSeconds(55), e.Evaluate(TimeSpan.FromSeconds(5)).Remaining);
        // Gentle slowdown to 9 MB/s: raw remaining falls by ~1s per step.
        e.Observe(TimeSpan.FromSeconds(6), 59 * MB);
        e.Observe(TimeSpan.FromSeconds(7), 68 * MB);
        e.Observe(TimeSpan.FromSeconds(8), 77 * MB);

        // Raw is 54s at t=8 — a 1-quantum change from the displayed 55s — so
        // the display holds even though 3s have passed.
        Assert.Equal(TimeSpan.FromSeconds(55), e.Evaluate(TimeSpan.FromSeconds(8)).Remaining);
    }

    [Fact]
    public void RemainingMayRiseWhenTheRunSlowsDown()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);
        Assert.Equal(TimeSpan.FromSeconds(55), e.Evaluate(TimeSpan.FromSeconds(5)).Remaining);
        // Halve the rate: the honest answer is a LONGER remaining time.
        e.Observe(TimeSpan.FromSeconds(6), 55 * MB);
        e.Observe(TimeSpan.FromSeconds(7), 60 * MB);

        var display = e.Evaluate(TimeSpan.FromSeconds(7));

        Assert.Equal(EstimateState.Known, display.State);
        Assert.True(display.Remaining > TimeSpan.FromSeconds(55),
            $"remaining should rise, got {display.Remaining}");
    }

    [Fact]
    public void RateCollapseReturnsToUnknownAfterTenSecondsWithoutProgress()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);
        Assert.Equal(EstimateState.Known, e.Evaluate(TimeSpan.FromSeconds(5)).State);
        for (int s = 6; s <= 14; s++) e.Observe(TimeSpan.FromSeconds(s), 50 * MB);
        Assert.Equal(EstimateState.Known, e.Evaluate(TimeSpan.FromSeconds(14)).State);

        e.Observe(TimeSpan.FromSeconds(15), 50 * MB);

        // 10s with no bytes advancing: no infinity, no frozen stale number.
        Assert.Equal(EstimateState.Unknown, e.Evaluate(TimeSpan.FromSeconds(15)).State);
    }

    [Fact]
    public void EstimateReturnsWhenBytesResumeAfterCollapse()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);
        for (int s = 6; s <= 15; s++) e.Observe(TimeSpan.FromSeconds(s), 50 * MB);
        Assert.Equal(EstimateState.Unknown, e.Evaluate(TimeSpan.FromSeconds(15)).State);
        for (int s = 16; s <= 19; s++) e.Observe(TimeSpan.FromSeconds(s), 50 * MB + (s - 15) * 10 * MB);

        Assert.Equal(EstimateState.Known, e.Evaluate(TimeSpan.FromSeconds(19)).State);
    }

    [Fact]
    public void SilentGapOverTenSecondsIsExcludedFromRateHistory()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);
        // A 15s silent jump: the gap leaves active time and the EWMA resets.
        e.Observe(TimeSpan.FromSeconds(20), 50 * MB);
        Assert.Equal(EstimateState.Unknown, e.Evaluate(TimeSpan.FromSeconds(20)).State);
        e.Observe(TimeSpan.FromSeconds(21), 60 * MB);
        e.Observe(TimeSpan.FromSeconds(22), 70 * MB);
        e.Observe(TimeSpan.FromSeconds(23), 80 * MB);

        var display = e.Evaluate(TimeSpan.FromSeconds(23));

        Assert.Equal(EstimateState.Known, display.State);
        Assert.Equal(TimeSpan.FromSeconds(52), display.Remaining);
        // Active time is 8s (5 + 3), not 23s: the gap was excluded.
        Assert.Equal(10 * MB, e.OverallRate!.Value, 0);
        Assert.Equal(3, e.PositiveBuckets);
    }

    [Fact]
    public void FailedPartRemainderLeavesTheDenominator()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);

        // A part fails with 300 MB unprocessed: remaining is (300 − 50) / 10.
        e.ExcludeUnprocessed(300 * MB);

        var display = e.Evaluate(TimeSpan.FromSeconds(5));
        Assert.Equal(EstimateState.Known, display.State);
        Assert.Equal(TimeSpan.FromSeconds(25), display.Remaining);
        Assert.Equal(300 * MB, e.EffectiveTotal);
    }

    [Fact]
    public void ExcludingEverythingReadsAsAlmostDone()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);
        e.ExcludeUnprocessed(600 * MB);

        Assert.Equal(EstimateState.AlmostDone, e.Evaluate(TimeSpan.FromSeconds(5)).State);
    }

    [Fact]
    public void ItemCountVariantUsesTheSameModel()
    {
        var e = SteadyFeed(100, 2, 5, EstimateUnits.Items);

        var display = e.Evaluate(TimeSpan.FromSeconds(5));

        // (100 − 10) items at 2 items/s = 45s.
        Assert.Equal(EstimateState.Known, display.State);
        Assert.Equal(TimeSpan.FromSeconds(45), display.Remaining);
    }

    [Fact]
    public void ItemCountVariantSuppressesTinyRuns()
    {
        // 8 items total: below the 10-item gate — a registry clean shows
        // elapsed-only, by the rule, not by a special case.
        var e = SteadyFeed(8, 1, 5, EstimateUnits.Items);

        Assert.Equal(EstimateState.Unknown, e.Evaluate(TimeSpan.FromSeconds(5)).State);
    }

    [Fact]
    public void DivergenceOverThreeTimesReturnsToUnknown()
    {
        var e = new ProgressEstimator(20000 * MB);
        // 20s of trickle, then one enormous bucket: EWMA and overall disagree.
        for (int s = 0; s <= 20; s++) e.Observe(TimeSpan.FromSeconds(s), 1L * MB * s);
        e.Observe(TimeSpan.FromSeconds(21), 1020 * MB);

        Assert.Equal(EstimateState.Unknown, e.Evaluate(TimeSpan.FromSeconds(21)).State);
    }

    [Fact]
    public void EstimateReturnsWhenEstimatorsReconverge()
    {
        var e = new ProgressEstimator(20000 * MB);
        for (int s = 0; s <= 20; s++) e.Observe(TimeSpan.FromSeconds(s), 1L * MB * s);
        e.Observe(TimeSpan.FromSeconds(21), 1020 * MB);
        Assert.Equal(EstimateState.Unknown, e.Evaluate(TimeSpan.FromSeconds(21)).State);
        for (int s = 22; s <= 25; s++) e.Observe(TimeSpan.FromSeconds(s), (20 + (s - 20) * 1000L) * MB);

        Assert.Equal(EstimateState.Known, e.Evaluate(TimeSpan.FromSeconds(25)).State);
    }

    [Fact]
    public void OutOfOrderObservationsAreIgnored()
    {
        var e = SteadyFeed(600 * MB, 10 * MB, 5);
        e.Observe(TimeSpan.FromSeconds(3), 999 * MB);
        e.Observe(TimeSpan.FromSeconds(6), 10 * MB); // rewound attempted value

        var display = e.Evaluate(TimeSpan.FromSeconds(6));

        Assert.Equal(EstimateState.Known, display.State);
    }
}

/// <summary>Purge's TimeTagline forms, reused exactly.</summary>
public sealed class TimeTaglineTests
{
    [Theory]
    [InlineData(0, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(2, "2 seconds")]
    [InlineData(12, "12 seconds")]
    [InlineData(59, "59 seconds")]
    [InlineData(60, "1m 0s")]
    [InlineData(65, "1m 5s")]
    [InlineData(750, "12m 30s")]
    [InlineData(3600, "60m 0s")]
    public void LiveFormsAreWholeSeconds(int seconds, string expected) =>
        Assert.Equal(expected, TimeTagline.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void CompletionFormUsesTenthsBelowOneSecond()
    {
        Assert.Equal("0.4 seconds", TimeTagline.FormatCompletion(TimeSpan.FromMilliseconds(400)));
        Assert.Equal("0.1 seconds", TimeTagline.FormatCompletion(TimeSpan.FromMilliseconds(50)));
        Assert.Equal("1 second", TimeTagline.FormatCompletion(TimeSpan.FromMilliseconds(1500)));
        Assert.Equal("1m 5s", TimeTagline.FormatCompletion(TimeSpan.FromSeconds(65)));
    }
}
