using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// The per-part progress state machine, totals aggregation and overall-progress
/// computation, plus the cleaner's part reporting and cancellation contract. The model
/// tests are pure logic; the cleaner tests use paths that do not exist, so nothing is
/// deleted — except one test that deletes files it created itself in a temp folder,
/// mirroring the existing junction/recycle tests, to prove a failing progress listener
/// can never break a delete.
/// </summary>
public sealed class CleanProgressTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "PurgeWindowsTests", Guid.NewGuid().ToString("N"));

    public CleanProgressTests() => Directory.CreateDirectory(_folder);

    static CleanProgressModel TwoParts(long aSize = 300, long bSize = 100) => new([
        ("a", "Caches", "", "", aSize),
        ("b", "Temp", "", "", bSize),
    ]);

    static CleanupCategory JobCategory(string id, params (string Name, string Path, long Size)[] items)
    {
        var cat = new CleanupCategory { Id = id, Title = id, Description = "", Glyph = "" };
        foreach (var (name, path, size) in items)
            cat.Items.Add(new CleanupItem { Name = name, Paths = [path], Size = size });
        return cat;
    }

    string Missing(string name) => Path.Combine(_folder, name);

    /// <summary>Delivers reports synchronously, so cancellation from a report is deterministic.</summary>
    sealed class SyncProgress : IProgress<CleanProgress>
    {
        public List<CleanProgress> Seen { get; } = [];
        public Action<CleanProgress>? OnReport;
        public void Report(CleanProgress value)
        {
            Seen.Add(value);
            OnReport?.Invoke(value);
        }
    }

    [Fact]
    public void PartsMoveFromQueuedToCleaningToDone()
    {
        var model = TwoParts();

        Assert.All(model.Parts, p => Assert.Equal(CleanPartState.Queued, p.State));
        Assert.Equal(0, model.PartsDone);

        model.StartPart("a");
        Assert.Equal(CleanPartState.Cleaning, model.Find("a")!.State);

        model.FinishPart("a", 3, 300, 0, 0);
        Assert.Equal(CleanPartState.Done, model.Find("a")!.State);
        Assert.Equal(1, model.Find("a")!.Fraction);
        Assert.Equal(1, model.PartsDone);
    }

    [Fact]
    public void OverallFractionIsWeightedByScannedSize()
    {
        var model = TwoParts(aSize: 300, bSize: 100);
        model.StartPart("a");
        model.FinishPart("a", 3, 300, 0, 0);
        model.StartPart("b");
        model.UpdatePart("b", 0.5, "", "", 0, 0, 0);

        // (300 * 1 + 100 * 0.5) / 400 = 0.875: the big part moves the bar further.
        Assert.Equal(0.875, model.OverallFraction, 9);
    }

    [Fact]
    public void OverallFractionFallsBackToEqualWeightsWhenSizesAreUnknown()
    {
        var model = TwoParts(aSize: 0, bSize: 0);
        model.StartPart("a");
        model.FinishPart("a", 0, 0, 0, 0);

        Assert.Equal(0.5, model.OverallFraction, 9);
    }

    [Fact]
    public void OverallFractionIsZeroWithNoParts()
    {
        var model = new CleanProgressModel([]);
        Assert.Equal(0, model.OverallFraction);
        Assert.Equal(0, model.PartCount);
    }

    [Fact]
    public void UpdatesNeverMoveAPartBackwardsOrResurrectIt()
    {
        var model = TwoParts();

        // Updates to a queued part are ignored.
        model.UpdatePart("a", 0.5, "C:\\x", "item", 1, 10, 0);
        Assert.Equal(0, model.Find("a")!.Fraction);

        model.StartPart("a");
        model.UpdatePart("a", 0.8, "C:\\x", "item", 2, 20, 0);
        // A stale report cannot rewind the part.
        model.UpdatePart("a", 0.3, "C:\\y", "item", 1, 10, 0);
        Assert.Equal(0.8, model.Find("a")!.Fraction);
        Assert.Equal(2, model.Find("a")!.FilesRemoved);

        model.FinishPart("a", 2, 20, 0, 0);
        // Finished parts ignore further updates and cannot restart.
        model.UpdatePart("a", 0.1, "", "", 0, 0, 0);
        model.StartPart("a");
        Assert.Equal(CleanPartState.Done, model.Find("a")!.State);
        Assert.Equal(1, model.Find("a")!.Fraction);
    }

    [Fact]
    public void CancelRemainingKeepsFinishedPartsAndCancelsTheRest()
    {
        var model = TwoParts();
        model.StartPart("a");
        model.FinishPart("a", 3, 300, 0, 0);
        model.StartPart("b");
        model.UpdatePart("b", 0.5, "C:\\temp", "item", 1, 40, 0);

        model.CancelRemaining();

        Assert.Equal(CleanPartState.Done, model.Find("a")!.State);
        var b = model.Find("b")!;
        Assert.Equal(CleanPartState.Cancelled, b.State);
        Assert.Equal(1, b.FilesRemoved);
        Assert.Equal(40, b.BytesFreed);
        Assert.Equal("", b.CurrentPath);
        // The cancelled part keeps its partial contribution; nothing is double-counted.
        Assert.Equal(0.875, model.OverallFraction, 9);
    }

    [Fact]
    public void TotalsAggregateAcrossPartsWithoutMixingBinMovesIntoFreed()
    {
        var model = TwoParts();
        model.StartPart("a");
        model.FinishPart("a", 3, 200, 100, 10);
        model.StartPart("b");
        model.FinishPart("b", 1, 50, 0, 5);

        var (files, freed, recycled, skipped) = model.Totals;
        Assert.Equal(4, files);
        Assert.Equal(250, freed);
        Assert.Equal(100, recycled);
        Assert.Equal(15, skipped);
    }

    [Fact]
    public void FailPartKeepsPartialTotalsAndLeavesOtherPartsAlone()
    {
        var model = TwoParts();
        model.StartPart("a");
        model.UpdatePart("a", 0.4, "", "", 2, 120, 0);
        model.FailPart("a", 2, 120, 0, 30);

        Assert.Equal(CleanPartState.Failed, model.Find("a")!.State);
        Assert.Equal(120, model.Find("a")!.BytesFreed);
        Assert.Equal(30, model.Find("a")!.BytesSkipped);
        Assert.Equal(CleanPartState.Queued, model.Find("b")!.State);
    }

    [Fact]
    public void FailedPartCountsAsDoneForOverallProgressButKeepsItsPartialRowFraction()
    {
        var model = TwoParts(aSize: 300, bSize: 100);
        model.StartPart("a");
        model.UpdatePart("a", 0.4, "", "", 2, 120, 0);
        model.FailPart("a", 2, 120, 0, 30);
        model.StartPart("b");
        model.UpdatePart("b", 0.5, "", "", 0, 0, 0);

        // The row shows how far the failed part got; overall counts it as done because
        // the run moved past it: (300 * 1 + 100 * 0.5) / 400 = 0.875.
        Assert.Equal(0.4, model.Find("a")!.Fraction, 9);
        Assert.Equal(0.875, model.OverallFraction, 9);
    }

    [Fact]
    public void CleanerReportsPartContextForEveryJob()
    {
        var a = JobCategory("a", ("cache", Missing("cache-a"), 1000));
        var b = JobCategory("b", ("temp", Missing("temp-b"), 2000));
        var progress = new SyncProgress();

        var r = Cleaner.Run([new(a, a.Items), new(b, b.Items)], progress, RemovalMode.EverythingPermanent);

        Assert.Equal(0, r.Freed);
        Assert.Equal(3000, r.Skipped);
        Assert.Equal(2, r.Categories!.Count);
        var done = progress.Seen.Where(p => p.PartDone).ToList();
        Assert.Equal(2, done.Count);
        Assert.All(done, p => Assert.Equal(2, p.PartCount));
        Assert.Equal(["a", "b"], done.Select(p => p.CategoryId).ToList());
        Assert.Contains(progress.Seen, p => p.CurrentItem == "cache" && p.CategoryId == "a");
        Assert.Equal(1, progress.Seen[^1].Fraction);
    }

    [Fact]
    public void CleanerGivesReportOnlyJobsNoPartAndNoResult()
    {
        var vm = Catalog.Create("virtual");
        vm.Items.Add(new CleanupItem { Name = "VM", Paths = [Missing("vm")], Size = 1234 });
        var a = JobCategory("a", ("cache", Missing("cache-a"), 1000));
        var progress = new SyncProgress();

        var r = Cleaner.Run([new(vm, vm.Items), new(a, a.Items)], progress, RemovalMode.EverythingPermanent);

        Assert.Single(r.Categories!);
        Assert.DoesNotContain(progress.Seen, p => p.CategoryId == "virtual");
        Assert.All(progress.Seen.Where(p => p.CategoryId.Length > 0), p => Assert.Equal(1, p.PartCount));
    }

    [Fact]
    public void CleanerGivesTheReviewOnlyBinCategoryNoPartAndNoResult()
    {
        var bin = Catalog.Create("recycle");
        Assert.Equal(CleanAction.ReportOnly, bin.Action);
        bin.Items.Add(new CleanupItem { Name = "Recycle Bin", Paths = ["shell:RecycleBinFolder"], Size = 1234 });
        var progress = new SyncProgress();

        var r = Cleaner.Run([new(bin, bin.Items)], progress, RemovalMode.EverythingPermanent);

        Assert.Empty(r.Categories ?? []);
        Assert.Equal(0, r.Freed);
        Assert.DoesNotContain(progress.Seen, p => p.CategoryId == "recycle");
    }

    [Fact]
    public void CancellingMidRunThrowsWithThePartialResult()
    {
        var a = JobCategory("a", ("one", Missing("a1"), 1000), ("two", Missing("a2"), 1000));
        var b = JobCategory("b", ("three", Missing("b1"), 1000), ("four", Missing("b2"), 1000));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress
        {
            // Cancel as soon as the second part starts: its items must never be attempted.
            OnReport = p =>
            {
                if (p.CategoryId == "b" && !p.PartDone && p.CurrentItem.Length == 0 && p.PartFraction == 0)
                    cts.Cancel();
            },
        };

        var ex = Assert.Throws<CleanCancelledException>(() =>
            Cleaner.Run([new(a, a.Items), new(b, b.Items)], progress, RemovalMode.EverythingPermanent, cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.Equal(2, ex.Partial.Categories!.Count);
        // The first part finished (its missing files count as skipped); the second part
        // never started, so its bytes are neither freed nor skipped.
        Assert.Equal(2000, ex.Partial.Skipped);
        Assert.Equal(0, ex.Partial.Freed);
        Assert.Contains(progress.Seen, p => p is { Cancelled: true, CategoryId: "b" });
    }

    [Fact]
    public void AThrowingProgressListenerCannotFailPartsOrBreakTheRun()
    {
        // Contract change from Round 3: progress delivery never throws into the engine,
        // so a broken listener must NOT mark parts failed. (Round 3's test expected the
        // opposite because delivery could throw there.)
        var a = JobCategory("a", ("cache", Missing("cache-a"), 1000));
        var b = JobCategory("b", ("temp", Missing("temp-b"), 1000));
        var progress = new SyncProgress
        {
            OnReport = p =>
            {
                if (p.PartDone) throw new InvalidOperationException("listener broke");
            },
        };

        var r = Cleaner.Run([new(a, a.Items), new(b, b.Items)], progress, RemovalMode.EverythingPermanent);

        Assert.Equal(2, r.Categories!.Count);
        Assert.Equal(2, progress.Seen.Count(p => p.PartDone));
        Assert.DoesNotContain(progress.Seen, p => p.PartFailed);
    }

    [Fact]
    public void AThrowingProgressListenerCannotBreakDeleting()
    {
        var dir = Path.Combine(_folder, "doomed");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 70; i++) File.WriteAllBytes(Path.Combine(dir, $"f{i}.tmp"), new byte[10]);

        var r = FileOps.Delete(dir, null, default, new ThrowingFileProgress());

        Assert.Equal(70, r.Files);
        Assert.Equal(700, r.Freed);
        Assert.Empty(Directory.GetFiles(dir));
        Assert.True(Directory.Exists(dir), "the folder itself stays");
    }

    [Fact]
    public void CleanerRecordsSkipReasonsForTooNewFiles()
    {
        var dir = Path.Combine(_folder, "fresh");
        Directory.CreateDirectory(dir);
        var fresh = Path.Combine(dir, "new.tmp");
        File.WriteAllBytes(fresh, new byte[512]);
        var cat = JobCategory("a", ("fresh", dir, 512));
        cat.Items[0] = new CleanupItem
        {
            Name = "fresh", Paths = [dir], Size = 512, MinAge = TimeSpan.FromDays(7),
        };

        var r = Cleaner.Run([new(cat, cat.Items)], null, RemovalMode.EverythingPermanent);

        Assert.Equal(0, r.Freed);
        Assert.Equal(1, r.SkipReasons![SkipReason.TooNew]);
        Assert.True(File.Exists(fresh));
    }

    sealed class ThrowingFileProgress : IProgress<FileOps.FileOpProgress>
    {
        public void Report(FileOps.FileOpProgress value) => throw new InvalidOperationException("listener broke");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
