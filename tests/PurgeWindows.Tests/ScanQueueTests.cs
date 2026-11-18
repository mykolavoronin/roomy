using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// The scan queue (strictly one step at a time, failing steps don't stop the scan,
/// cooperative cancellation) and the 140 ms progress coalescer.
/// </summary>
public sealed class ScanQueueTests
{
    static CleanupCategory Cat(string id) =>
        new() { Id = id, Title = id, Description = "", Glyph = "" };

    static ScanStep Step(string id, Func<ScanContext, CleanupCategory?>? run = null) =>
        new(id, id, run ?? (_ => Cat(id)));

    sealed class SyncProgress : IProgress<ScanProgressEvent>
    {
        public List<ScanProgressEvent> Seen { get; } = [];
        public void Report(ScanProgressEvent value) => Seen.Add(value);
    }

    [Fact]
    public async Task StepsRunInOrderOneAtATime()
    {
        var order = new List<string>();
        int concurrent = 0, maxConcurrent = 0;
        ScanStep Make(string id) => Step(id, _ =>
        {
            Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, Volatile.Read(ref concurrent));
            Thread.Sleep(20);
            Interlocked.Decrement(ref concurrent);
            lock (order) order.Add(id);
            return Cat(id);
        });

        var results = await ScanQueue.RunAsync(
            [Make("a"), Make("b"), Make("c")], new ScanContext());

        Assert.Equal(["a", "b", "c"], order);
        Assert.Equal(1, maxConcurrent);
        Assert.Equal(["a", "b", "c"], results.Select(c => c.Id).ToList());
    }

    [Fact]
    public async Task FailingStepReportsFailedAndQueueCarriesOn()
    {
        var progress = new SyncProgress();
        var steps = new[]
        {
            Step("a"),
            Step("b", _ => throw new InvalidOperationException("boom")),
            Step("c"),
        };

        var results = await ScanQueue.RunAsync(steps, new ScanContext(), progress);

        Assert.Equal(["a", "c"], results.Select(c => c.Id).ToList());
        var failed = Assert.Single(progress.Seen.Where(e => e.State == ScanStepState.Failed));
        Assert.Equal("b", failed.StepId);
        Assert.Contains("boom", failed.Detail);
        Assert.Equal(2, progress.Seen.Count(e => e.State == ScanStepState.Done));
    }

    [Fact]
    public async Task CancellationBetweenStepsStopsTheQueue()
    {
        using var cts = new CancellationTokenSource();
        var ran = new List<string>();
        ScanStep Make(string id) => Step(id, _ => { lock (ran) ran.Add(id); return Cat(id); });
        var progress = new SyncProgress();

        var results = await ScanQueue.RunAsync(
            [Make("a"), Make("b"), Make("c")],
            new ScanContext { Token = cts.Token },
            progress, cts.Token,
            onStepCompleted: (_, _) => { cts.Cancel(); return Task.CompletedTask; });

        Assert.Single(results);
        Assert.Equal(["a"], ran);
        Assert.Contains(progress.Seen, e => e.State == ScanStepState.Cancelled);
    }

    [Fact]
    public async Task DoneEventsCarryTheCategory()
    {
        var progress = new SyncProgress();
        await ScanQueue.RunAsync([Step("a")], new ScanContext(), progress);
        var done = Assert.Single(progress.Seen.Where(e => e.State == ScanStepState.Done));
        Assert.Equal("a", done.Category!.Id);
        Assert.Equal(1, done.Fraction);
    }

    [Fact]
    public void FractionCountsBoundaryStatesAsDone()
    {
        ScanProgressEvent Ev(int i, ScanStepState s) =>
            new(i, 2, "x", "x", s, "", 0, 0, TimeSpan.Zero);
        Assert.Equal(0, Ev(0, ScanStepState.Active).Fraction);
        Assert.Equal(0.5, Ev(0, ScanStepState.Done).Fraction);
        Assert.Equal(0.5, Ev(0, ScanStepState.Failed).Fraction);
        Assert.Equal(1, Ev(1, ScanStepState.Done).Fraction);
    }

    // ---------- coalescer ----------

    static ScanProgressEvent Active(string path = "") =>
        new(0, 1, "x", "x", ScanStepState.Active, path, 0, 0, TimeSpan.Zero);

    static ScanProgressEvent Done() =>
        new(0, 1, "x", "x", ScanStepState.Done, "", 0, 0, TimeSpan.Zero);

    [Fact]
    public void CoalescerThrottlesActivesAndKeepsTheLatest()
    {
        var sink = new SyncProgress();
        using var coalescer = new ScanCoalescer(sink, TimeSpan.FromHours(1));
        coalescer.Report(Active("one"));
        coalescer.Report(Active("two"));
        coalescer.Report(Active("three"));

        // Only the first Active forwarded; the latest is pending.
        Assert.Single(sink.Seen);
        Assert.Equal("one", sink.Seen[0].CurrentPath);

        coalescer.Flush();
        Assert.Equal(2, sink.Seen.Count);
        Assert.Equal("three", sink.Seen[1].CurrentPath);
    }

    [Fact]
    public void CoalescerForwardsBoundaryEventsImmediately()
    {
        var sink = new SyncProgress();
        using var coalescer = new ScanCoalescer(sink, TimeSpan.FromHours(1));
        coalescer.Report(Active("one"));
        coalescer.Report(Active("two"));
        coalescer.Report(Done());

        Assert.Equal(2, sink.Seen.Count);
        Assert.Equal(ScanStepState.Done, sink.Seen[1].State);
        // The pending Active was dropped by the boundary event, not forwarded after it.
        coalescer.Flush();
        Assert.Equal(2, sink.Seen.Count);
    }

    [Fact]
    public void CoalescerWithZeroIntervalForwardsEverything()
    {
        var sink = new SyncProgress();
        using var coalescer = new ScanCoalescer(sink, TimeSpan.Zero);
        coalescer.Report(Active("one"));
        coalescer.Report(Active("two"));
        Assert.Equal(2, sink.Seen.Count);
    }

    [Fact]
    public void CoalescerNeverThrowsIntoTheEngine()
    {
        var throwing = new ThrowingProgress();
        using var coalescer = new ScanCoalescer(throwing);
        coalescer.Report(Active());
        coalescer.Report(Done());
        coalescer.Flush();
    }

    sealed class ThrowingProgress : IProgress<ScanProgressEvent>
    {
        public void Report(ScanProgressEvent value) => throw new InvalidOperationException("listener broke");
    }
}
