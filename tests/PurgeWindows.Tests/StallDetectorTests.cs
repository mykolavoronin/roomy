using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// The two-clock stall watchdog (§4.5): threshold crossings, slow vs silent vs
/// stuck vs dead, heartbeat semantics (alive-but-slow never escalates),
/// clock resets, unit endings, and stopping suppression. Times are run-elapsed
/// values supplied directly — no clock, no UI.
/// </summary>
public sealed class StallDetectorTests
{
    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    static StallDetector Started()
    {
        var d = new StallDetector();
        d.Start(S(0));
        return d;
    }

    [Fact]
    public void NormalBeforeStartAndWhileProgressIsFresh()
    {
        var d = new StallDetector();
        Assert.Equal(StallLevel.Normal, d.Evaluate(S(100)));

        d = Started();
        d.ObserveProgress(S(5));
        Assert.Equal(StallLevel.Normal, d.Evaluate(S(9)));
    }

    [Fact]
    public void SilentAtTenSecondsWithoutAnyEvent()
    {
        var d = Started();

        Assert.Equal(StallLevel.Normal, d.Evaluate(S(9.9)));
        Assert.Equal(StallLevel.Silent, d.Evaluate(S(10)));
    }

    [Fact]
    public void SlowWhileHeartbeatsArriveButNothingAdvances()
    {
        var d = Started();
        d.ObserveProgress(S(0));
        d.ObserveActivity(S(9));

        // Progress stale 10s, activity fresh: alive, working on something hard.
        Assert.Equal(StallLevel.Slow, d.Evaluate(S(10)));
    }

    [Fact]
    public void SlowNeverEscalatesWhileActivityStaysFresh()
    {
        var d = Started();
        d.ObserveProgress(S(0));
        for (int s = 5; s <= 300; s += 5) d.ObserveActivity(S(s));

        Assert.Equal(StallLevel.Slow, d.Evaluate(S(300)));
    }

    [Fact]
    public void StuckAtThirtySecondsOfSilence()
    {
        var d = Started();

        Assert.Equal(StallLevel.Silent, d.Evaluate(S(29.9)));
        Assert.Equal(StallLevel.Stuck, d.Evaluate(S(30)));
    }

    [Fact]
    public void DeadAtTwoMinutesOfSilence()
    {
        var d = Started();

        Assert.Equal(StallLevel.Stuck, d.Evaluate(S(119.9)));
        Assert.Equal(StallLevel.Dead, d.Evaluate(S(120)));
    }

    [Fact]
    public void ProgressReturnsToNormalImmediately()
    {
        var d = Started();
        Assert.Equal(StallLevel.Stuck, d.Evaluate(S(30)));

        d.ObserveProgress(S(30));

        Assert.Equal(StallLevel.Normal, d.Evaluate(S(30)));
    }

    [Fact]
    public void HeartbeatAloneMovesStuckBackToSlowNotNormal()
    {
        var d = Started();
        Assert.Equal(StallLevel.Stuck, d.Evaluate(S(30)));

        // A heartbeat proves aliveness, not progress: Slow, not Normal.
        d.ObserveActivity(S(30));

        Assert.Equal(StallLevel.Slow, d.Evaluate(S(30)));
    }

    [Fact]
    public void EndUnitStartsFreshClocksForTheNextUnit()
    {
        var d = Started();
        Assert.Equal(StallLevel.Silent, d.Evaluate(S(25)));

        // A failed part ends its own clocks: no stall copy names it.
        d.EndUnit(S(25));

        Assert.Equal(StallLevel.Normal, d.Evaluate(S(25)));
        Assert.Equal(StallLevel.Silent, d.Evaluate(S(35)));
    }

    [Fact]
    public void SuppressedWhileStopping()
    {
        var d = Started();
        d.Suppressed = true;

        Assert.Equal(StallLevel.Normal, d.Evaluate(S(200)));
    }

    [Fact]
    public void ResetClearsTheRun()
    {
        var d = Started();
        d.Reset();

        Assert.Equal(StallLevel.Normal, d.Evaluate(S(200)));
        Assert.False(d.IsStarted);
    }

    [Fact]
    public void StaleDurationsMeasureTheSilence()
    {
        var d = Started();
        d.ObserveProgress(S(0));
        d.ObserveActivity(S(20));

        Assert.Equal(S(10), d.ProgressStaleFor(S(10)));
        Assert.Equal(S(10), d.ActivityStaleFor(S(30)));
    }

    [Fact]
    public void OutOfOrderObservationsNeverRewindTheClocks()
    {
        var d = Started();
        d.ObserveProgress(S(20));
        d.ObserveProgress(S(5));

        Assert.Equal(StallLevel.Normal, d.Evaluate(S(25)));
    }
}
