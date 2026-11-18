using System.Diagnostics;
using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// Hang-regression tests for the Phase 0 fix (C1–C8): cancellation promptness with exact
/// partial totals, streaming enumeration, recycle pre-check logic, the §3.5 event contract,
/// never-throwing listeners, the watchdog, the bounded clean log, and the owner-tool
/// failure taxonomy. The engine is testable without a UI: every test here runs the engine
/// directly. Shell-dependent tests assert the Windows requirement explicitly.
/// </summary>
public sealed class HangRegressionTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "PurgeWindowsTests", Guid.NewGuid().ToString("N"));

    public HangRegressionTests() => Directory.CreateDirectory(_folder);

    static void RequireWindows() =>
        Assert.True(OperatingSystem.IsWindows(), "This test requires Windows.");

    /// <summary>Builds a nested tree: <paramref name="files"/> files of <paramref name="bytesEach"/>
    /// spread over 10 subdirectories. Returns the root and the total bytes.</summary>
    (string Root, long TotalBytes) MakeTree(string name, int files, int bytesEach)
    {
        var root = Path.Combine(_folder, name);
        var payload = new byte[bytesEach];
        for (int i = 0; i < files; i++)
        {
            var dir = Path.Combine(root, $"sub{i % 10}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"f{i}.bin"), payload);
        }
        return (root, (long)files * bytesEach);
    }

    static int CountFiles(string root) =>
        Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length : 0;

    static CleanupCategory JobCategory(string id, string itemName, string path, long size)
    {
        var cat = new CleanupCategory { Id = id, Title = id, Description = "", Glyph = "" };
        cat.Items.Add(new CleanupItem { Name = itemName, Paths = [path], Size = size });
        return cat;
    }

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

    sealed class SyncEvents : IProgress<CleanEvent>
    {
        public List<CleanEvent> Seen { get; } = [];
        public void Report(CleanEvent value) => Seen.Add(value);
    }

    sealed class ThrowingProgress : IProgress<CleanProgress>
    {
        public void Report(CleanProgress value) => throw new InvalidOperationException("listener broke");
    }

    sealed class ThrowingEvents : IProgress<CleanEvent>
    {
        public void Report(CleanEvent value) => throw new InvalidOperationException("listener broke");
    }

    sealed class ThrowingFileProgress : IProgress<FileOps.FileOpProgress>
    {
        public void Report(FileOps.FileOpProgress value) => throw new InvalidOperationException("listener broke");
    }

    // ---------- C2: cancellation ----------

    [Fact]
    public void CancellationMidRunIsPromptAndPartialTotalsAreExact()
    {
        RequireWindows();
        const int totalFiles = 10_000;
        var (root, totalBytes) = MakeTree("big", totalFiles, 100);
        var cat = JobCategory("a", "big", root, totalBytes);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress
        {
            // Stop as soon as any file is credited: streaming work must already be under way.
            OnReport = p =>
            {
                if (p.FilesSoFar > 0) cts.Cancel();
            },
        };

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<CleanCancelledException>(() =>
            Cleaner.Run([new(cat, cat.Items)], progress, ct: cts.Token));
        sw.Stop();

        var remaining = CountFiles(root);
        var removed = totalFiles - remaining;
        Assert.True(removed > 0, "some files should have been removed before the stop");
        Assert.True(remaining > 0, "the stop must land mid-run, not after everything is gone");
        // Exact partial totals: the partial result must match the disk exactly.
        Assert.Equal(removed, ex.Partial.Files);
        Assert.Equal((long)removed * 100, ex.Partial.Recycled);
        Assert.Equal(0, ex.Partial.Freed);
        Assert.Equal(0, ex.Partial.Skipped);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"stop took {sw.Elapsed}");
    }

    [Fact]
    public void PreCancelledTokenThrowsImmediatelyWithAnEmptyPartial()
    {
        var (root, totalBytes) = MakeTree("small", 10, 100);
        var cat = JobCategory("a", "small", root, totalBytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<CleanCancelledException>(() =>
            Cleaner.Run([new(cat, cat.Items)], null, ct: cts.Token));
        sw.Stop();

        Assert.Equal(0, ex.Partial.Freed);
        Assert.Equal(0, ex.Partial.Recycled);
        Assert.Equal(0, ex.Partial.Files);
        Assert.Equal(0, ex.Partial.Skipped);
        Assert.Single(ex.Partial.Categories!);
        Assert.Equal(10, CountFiles(root));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"pre-cancelled run took {sw.Elapsed}");
    }

    // ---------- C3/C4: streaming enumeration + in-item progress ----------

    [Fact]
    public void StreamingDeleteReportsProgressBeforeTheWholeTreeIsWalked()
    {
        var (root, _) = MakeTree("stream", 5000, 10);
        using var cts = new CancellationTokenSource();
        var seen = new List<FileOps.FileOpProgress>();
        var progress = new SyncFileProgress(fp =>
        {
            seen.Add(fp);
            cts.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() => FileOps.Delete(root, null, cts.Token, progress));

        Assert.NotEmpty(seen);
        // The first report is a heartbeat (liveness before any file is attempted); the
        // first progress report follows after the first file. Either way, reporting
        // starts before enumeration of the whole tree could possibly finish.
        Assert.True(seen[0].IsHeartbeat);
        Assert.Equal(0, seen[0].FilesAttempted);
        Assert.Contains(seen, p => !p.IsHeartbeat && p.FilesAttempted == 1);
        Assert.All(seen, p => Assert.True(p.FilesAttempted <= 64));
        Assert.True(CountFiles(root) > 4900, "cancelling on the first report must leave almost everything");
    }

    sealed class SyncFileProgress(Action<FileOps.FileOpProgress> onReport) : IProgress<FileOps.FileOpProgress>
    {
        public void Report(FileOps.FileOpProgress value) => onReport(value);
    }

    [Fact]
    public void DeleteReportsInsideOneItemAndFinishesWithExactTotals()
    {
        var (root, totalBytes) = MakeTree("inside", 300, 10);
        var seen = new List<FileOps.FileOpProgress>();
        var progress = new SyncFileProgress(seen.Add);

        var r = FileOps.Delete(root, null, default, progress);

        Assert.Equal(300, r.Files);
        Assert.Equal(totalBytes, r.Freed);
        var reports = seen.Where(p => !p.IsHeartbeat).ToList();
        Assert.True(reports.Count > 1, "one item must produce several progress reports, not just one at the end");
        var last = reports[^1];
        Assert.Equal(300, last.FilesAttempted);
        Assert.Equal(300, last.FilesTotal);
        Assert.Equal(totalBytes, last.BytesTotal);
        Assert.True(reports.Take(reports.Count - 1).Any(p => p.FilesAttempted is > 0 and < 300),
            "an intermediate report must show partial progress inside the item");
    }

    [Fact]
    public void DeleteRemovesNowEmptySubfoldersButKeepsTheRoot()
    {
        var (root, _) = MakeTree("tidy", 30, 10);

        var r = FileOps.Delete(root, null);

        Assert.Equal(30, r.Files);
        Assert.True(Directory.Exists(root), "the folder itself stays");
        Assert.Empty(Directory.GetDirectories(root, "*", SearchOption.AllDirectories));
    }

    // ---------- C1/C5 pre-check logic (pure, no shell) ----------

    [Fact]
    public void PreflightFlagsExceedingApproachingAndOkPlans()
    {
        var drive = Path.GetPathRoot(_folder)!;
        var capacities = new List<BinCapacity> { new(drive, true, 500, 0) };

        var exceeds = RecyclePreflight.Evaluate(new Dictionary<string, long> { [drive] = 800 }, capacities);
        Assert.True(exceeds.ExceedsCapacity);
        Assert.True(exceeds.ApproachingCapacity);
        Assert.Equal(500, exceeds.KnownRemainingBytes);

        var approaching = RecyclePreflight.Evaluate(new Dictionary<string, long> { [drive] = 450 }, capacities);
        Assert.False(approaching.ExceedsCapacity);
        Assert.True(approaching.ApproachingCapacity);

        var ok = RecyclePreflight.Evaluate(new Dictionary<string, long> { [drive] = 100 }, capacities);
        Assert.False(ok.ExceedsCapacity);
        Assert.False(ok.ApproachingCapacity);
    }

    [Fact]
    public void PreflightTreatsUnknownCapacityAndDisabledBinsHonestly()
    {
        var drive = Path.GetPathRoot(_folder)!;

        var unknown = RecyclePreflight.Evaluate(
            new Dictionary<string, long> { [drive] = 800 }, []);
        Assert.False(unknown.ExceedsCapacity);
        Assert.False(unknown.ApproachingCapacity);
        Assert.Null(unknown.KnownRemainingBytes);
        Assert.Contains(BinCapacity.Normalize(drive), unknown.UnknownDrives);

        var disabled = RecyclePreflight.Evaluate(
            new Dictionary<string, long> { [drive] = 10 },
            [new BinCapacity(drive, false, 500, 0)]);
        Assert.True(disabled.ExceedsCapacity);
        Assert.Contains(BinCapacity.Normalize(drive), disabled.DisabledDrives);
    }

    [Fact]
    public void BudgetSpendsAcrossReservationsAndNeverOverspends()
    {
        var drive = Path.GetPathRoot(_folder)!;
        var budget = new RecycleBinBudget([new BinCapacity(drive, true, 1000, 0)]);
        var path = Path.Combine(_folder, "x.bin");

        Assert.True(budget.TryReserve(path, 600));
        Assert.Equal(400, budget.RemainingFor(drive));
        Assert.False(budget.TryReserve(path, 500));
        Assert.Equal(400, budget.RemainingFor(drive));
        Assert.Equal(SkipReason.TooBigForBin, budget.RefusalReason(path, 500));
        Assert.True(budget.TryReserve(path, 400));
        Assert.Equal(0, budget.RemainingFor(drive));

        var disabled = new RecycleBinBudget([new BinCapacity(drive, false, 1000, 0)]);
        Assert.False(disabled.TryReserve(path, 1));
        Assert.Equal(SkipReason.BinDisabled, disabled.RefusalReason(path, 1));
        Assert.True(disabled.IsBinDisabled(path));

        var unknown = new RecycleBinBudget([]);
        Assert.True(unknown.TryReserve(path, long.MaxValue));
        Assert.Null(unknown.RefusalReason(path, 1));
    }

    // ---------- §3.5 event contract ----------

    [Fact]
    public void EventContractIsSequencedMonotonicAndComplete()
    {
        RequireWindows();
        var (root, totalBytes) = MakeTree("events", 130, 10);
        var a = JobCategory("a", "events", root, totalBytes);
        var b = JobCategory("b", "missing", Path.Combine(_folder, "nope"), 1000);
        var events = new SyncEvents();
        var options = new CleanOptions { Events = events };

        var r = Cleaner.Run([new(a, a.Items), new(b, b.Items)], null, default, options);

        Assert.Equal(1300, r.Recycled);
        Assert.Equal(0, r.Freed);
        Assert.NotEmpty(events.Seen);
        // Sequenced from 1, contiguous.
        Assert.Equal(Enumerable.Range(1, events.Seen.Count).Select(i => (long)i), events.Seen.Select(e => e.Sequence));
        // Timestamps and elapsed times never go backwards.
        for (int i = 1; i < events.Seen.Count; i++)
        {
            Assert.True(events.Seen[i].Timestamp >= events.Seen[i - 1].Timestamp, "timestamps must not rewind");
            Assert.True(events.Seen[i].Elapsed >= events.Seen[i - 1].Elapsed, "elapsed must not rewind");
            Assert.True(events.Seen[i].Fraction >= events.Seen[i - 1].Fraction - 1e-9, "fraction must not rewind");
            Assert.InRange(events.Seen[i].Fraction, 0, 1);
            Assert.True(events.Seen[i].BytesAttemptedSoFar >= events.Seen[i - 1].BytesAttemptedSoFar, "attempted bytes must not rewind");
            Assert.True(events.Seen[i].BytesRemovedSoFar >= events.Seen[i - 1].BytesRemovedSoFar, "removed bytes must not rewind");
            Assert.True(events.Seen[i].FilesRemoved >= events.Seen[i - 1].FilesRemoved, "removed files must not rewind");
        }
        Assert.Equal(CleanEventKind.RunStarted, events.Seen[0].Kind);
        Assert.Equal(CleanEventKind.RunFinished, events.Seen[^1].Kind);
        Assert.Equal(1, events.Seen[^1].Fraction);
        Assert.Equal(2, events.Seen[0].Parts!.Count);
        Assert.Equal(["a", "b"], events.Seen[0].Parts.Select(p => p.CategoryId).ToList());
        Assert.Equal(2, events.Seen.Count(e => e.Kind == CleanEventKind.PartStarted));
        Assert.Equal(2, events.Seen.Count(e => e.Kind == CleanEventKind.PartFinished));
        Assert.Equal(0, events.Seen.Count(e => e.Kind == CleanEventKind.PartFailed));
        // ETA inputs are present on every event: totals, attempted, per-part, timestamps.
        Assert.All(events.Seen, e =>
        {
            Assert.Equal(totalBytes + 1000, e.TotalBytes);
            Assert.True(e.PartTotalBytes >= 0);
            Assert.True(e.PartBytesAttempted >= 0);
        });
    }

    [Fact]
    public void CancelledRunEndsWithRunCancelledAndPartialTotals()
    {
        RequireWindows();
        var (root, totalBytes) = MakeTree("cancel-events", 5000, 10);
        var cat = JobCategory("a", "cancel-events", root, totalBytes);
        using var cts = new CancellationTokenSource();
        var events = new SyncEvents();
        var progress = new SyncProgress { OnReport = p => { if (p.FilesSoFar > 0) cts.Cancel(); } };
        var options = new CleanOptions { Events = events };

        var ex = Assert.Throws<CleanCancelledException>(() =>
            Cleaner.Run([new(cat, cat.Items)], progress, ct: cts.Token, options: options));

        Assert.Equal(CleanEventKind.RunCancelled, events.Seen[^1].Kind);
        Assert.Equal(ex.Partial.Freed + ex.Partial.Recycled, events.Seen[^1].BytesRemovedSoFar);
        Assert.DoesNotContain(events.Seen, e => e.Kind == CleanEventKind.RunFinished);
    }

    // ---------- Never-throwing listeners ----------

    [Fact]
    public void ThrowingEventAndProgressListenersCannotBreakARun()
    {
        RequireWindows();
        var (root, _) = MakeTree("throwing", 130, 10);
        var cat = JobCategory("a", "throwing", root, 1300);
        var options = new CleanOptions { Events = new ThrowingEvents() };

        var r = Cleaner.Run([new(cat, cat.Items)], new ThrowingProgress(),
            default, options);

        Assert.Equal(1300, r.Recycled);
        Assert.Equal(0, r.Freed);
        Assert.Equal(130, r.Files);
        Assert.Single(r.Categories!);
    }

    [Fact]
    public void ThrowingFileProgressCannotBreakRecycling()
    {
        RequireWindows();
        var dir = Path.Combine(_folder, "throw-recycle");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 70; i++) File.WriteAllBytes(Path.Combine(dir, $"f{i}.tmp"), new byte[10]);

        var r = FileOps.RecycleContents(dir, null, default, new ThrowingFileProgress());

        Assert.Equal(70, r.Files);
        Assert.Equal(700, r.Freed);
        Assert.Empty(Directory.GetFiles(dir));
    }

    // ---------- C8: watchdog + clean log ----------

    [Fact]
    public void WatchdogRecordsSilenceAndRecoversOnTheNextHeartbeat()
    {
        using var watchdog = new CleanWatchdog(
            TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(25));
        watchdog.Pulse(CleanPhase.Deleting, @"C:\temp\file.bin");
        Assert.Equal(WatchdogState.Alive, watchdog.State);

        Thread.Sleep(500);

        Assert.Equal(WatchdogState.Silent, watchdog.State);
        Assert.True(watchdog.WasSilent);
        Assert.Equal(CleanPhase.Deleting, watchdog.LastKnown.Phase);
        Assert.Equal(@"C:\temp\file.bin", watchdog.LastKnown.LastPath);
        Assert.True(watchdog.Silence > TimeSpan.FromMilliseconds(150));

        watchdog.Pulse(CleanPhase.Recycling, @"C:\temp\other.bin");
        Assert.Equal(WatchdogState.Alive, watchdog.State);
    }

    [Fact]
    public void CleanLogStaysBounded()
    {
        var original = CleanLog.FilePath;
        var tempLog = Path.Combine(_folder, "clean.log");
        CleanLog.FilePath = tempLog;
        try
        {
            var bigLine = new string('x', 2048);
            for (int i = 0; i < 300; i++) CleanLog.WriteLine(bigLine);

            Assert.True(File.Exists(tempLog));
            var length = new FileInfo(tempLog).Length;
            Assert.True(length <= CleanLog.MaxBytes + 8192, $"clean log grew to {length} bytes");
            Assert.Contains(bigLine, File.ReadAllText(tempLog));
        }
        finally
        {
            CleanLog.FilePath = original;
        }
    }

    [Fact]
    public void CleanLogWriteEventNeverThrowsForOddInput()
    {
        var original = CleanLog.FilePath;
        CleanLog.FilePath = Path.Combine(_folder, "events.log");
        try
        {
            var ev = new CleanEvent(1, DateTimeOffset.Now, TimeSpan.Zero, CleanEventKind.FileProgress,
                "a", CleanPhase.ShellCall, "", 0, 0, 0, 0, 0, 0, 0, 0, 0,
                Reason: SkipReason.ShellError, ShellReturnCode: -123, ShellAborted: true, Detail: "weird\0detail");
            CleanLog.WriteEvent(ev);
            CleanLog.WriteShellBatch(-1, false, 0, 0, "");
            CleanLog.WriteWatchdog("STUCK", TimeSpan.Zero, CleanPhase.Finishing, "");
            Assert.True(File.Exists(CleanLog.FilePath));
        }
        finally
        {
            CleanLog.FilePath = original;
        }
    }

    // ---------- Owner tools ----------

    [Fact]
    public void OwnerToolClassificationNeverTrustsTheExitCodeAlone()
    {
        Assert.Equal(OwnerToolOutcome.Success, OwnerTools.Classify(0, "", 100, 0));
        Assert.Equal(OwnerToolOutcome.Success, OwnerTools.Classify(0, "", 0, 0));
        Assert.Equal(OwnerToolOutcome.Uncertain, OwnerTools.Classify(0, "", 100, 100));
        Assert.Equal(OwnerToolOutcome.Uncertain, OwnerTools.Classify(0, "error: boom", 100, 0));
        Assert.Equal(OwnerToolOutcome.Uncertain, OwnerTools.Classify(0, "FAILED to remove", 100, 0));
        Assert.Equal(OwnerToolOutcome.Failed, OwnerTools.Classify(1, "", 100, 50));
        Assert.Equal(OwnerToolOutcome.Failed, OwnerTools.Classify(null, "", 0, 0));
    }

    [Fact]
    public void OwnerToolMatchFindsGoAndGradleOnly()
    {
        var go = new CleanupItem
        {
            Name = "Go", Paths = [Path.Combine(_folder, "go-build"), Path.Combine(_folder, "go", "pkg", "mod")],
        };
        var goSpec = OwnerTools.Match(go);
        Assert.NotNull(goSpec);
        Assert.Equal("Go", goSpec.ToolName);
        Assert.Equal(2, goSpec.CleanCommands.Count);
        Assert.Contains(goSpec.CleanCommands, c => c.Arguments == "clean -modcache");
        Assert.Contains(goSpec.CleanCommands, c => c.Arguments == "clean -cache");

        var gradle = new CleanupItem { Name = "Gradle", Paths = [Path.Combine(_folder, ".gradle", "caches")] };
        var gradleSpec = OwnerTools.Match(gradle);
        Assert.NotNull(gradleSpec);
        Assert.Empty(gradleSpec.CleanCommands);
        Assert.NotNull(gradleSpec.PreStep);

        var maven = new CleanupItem { Name = "Maven", Paths = [Path.Combine(_folder, ".m2", "repository")] };
        Assert.Null(OwnerTools.Match(maven));
        var cargo = new CleanupItem { Name = "Cargo", Paths = [Path.Combine(_folder, ".cargo", "registry", "cache")] };
        Assert.Null(OwnerTools.Match(cargo));
        var other = new CleanupItem { Name = "Temp", Paths = [Path.Combine(_folder, "temp")] };
        Assert.Null(OwnerTools.Match(other));
    }

    [Fact]
    public void OwnerToolExecuteReportsNotInstalledQuickly()
    {
        var spec = new OwnerToolSpec("Missing", [new OwnerToolCommand("definitely-not-a-real-tool-xyz.exe", "--clean", "missing")], null);

        var sw = Stopwatch.StartNew();
        var result = OwnerTools.Execute(spec, spec.CleanCommands[0], [], CancellationToken.None, TimeSpan.FromSeconds(30));
        sw.Stop();

        Assert.Equal(OwnerToolOutcome.NotInstalled, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"not-installed took {sw.Elapsed}");
    }

    [Fact]
    public void OwnerToolExecuteHonorsItsTimeout()
    {
        RequireWindows();
        // ping -n 6 takes ~5s; the timeout must kill it far sooner.
        var spec = new OwnerToolSpec("Sleeper",
            [new OwnerToolCommand("cmd.exe", "/c ping -n 6 127.0.0.1 >nul", "sleep")], null);

        var sw = Stopwatch.StartNew();
        var result = OwnerTools.Execute(spec, spec.CleanCommands[0], [], CancellationToken.None, TimeSpan.FromSeconds(1));
        sw.Stop();

        Assert.Equal(OwnerToolOutcome.TimedOut, result.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"timeout took {sw.Elapsed}");
    }

    [Fact]
    public void OwnerToolExecuteHonorsCancellation()
    {
        RequireWindows();
        var spec = new OwnerToolSpec("Sleeper",
            [new OwnerToolCommand("cmd.exe", "/c ping -n 6 127.0.0.1 >nul", "sleep")], null);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var sw = Stopwatch.StartNew();
        var result = OwnerTools.Execute(spec, spec.CleanCommands[0], [], cts.Token, TimeSpan.FromMinutes(5));
        sw.Stop();

        Assert.Equal(OwnerToolOutcome.Cancelled, result.Outcome);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"cancel took {sw.Elapsed}");
    }

    // ---------- P1-5: policy gate + bin default ----------

    /// <summary>Roots for a machine that is not this one, so the real temp folder is
    /// outside the policy's allowlist and every real path is refused.</summary>
    static PolicyRoots ElsewhereRoots() => new(
        @"C:\Users\policy-test-user",
        @"C:\Users\policy-test-user\AppData\Local",
        @"C:\Users\policy-test-user\AppData\Roaming",
        @"C:\ProgramData", @"C:\Windows", @"C:\",
        @"C:\Users\policy-test-user\Downloads",
        @"C:\Users\policy-test-user\Documents",
        @"C:\Users\policy-test-user\Desktop",
        @"C:\Users\policy-test-user\Pictures",
        @"C:\Users\policy-test-user\Videos",
        @"C:\Users\policy-test-user\Music",
        @"C:\Users\policy-test-user\AppData\Local\Temp",
        @"C:\Program Files", @"C:\Program Files (x86)");

    [Fact]
    public void PolicyRefusedPathIsNeverDeletedByTheCleaner()
    {
        RequireWindows();
        var dir = Path.Combine(_folder, "refused");
        Directory.CreateDirectory(dir);
        var keep = Path.Combine(dir, "keep.bin");
        File.WriteAllBytes(keep, new byte[1024]);
        var cat = JobCategory("a", "refused", dir, 1024);
        var options = new CleanOptions { Policy = new DeletionSafetyPolicy(ElsewhereRoots()) };

        var r = Cleaner.Run([new(cat, cat.Items)], null, options: options);

        Assert.Equal(0, r.Files);
        Assert.Equal(0, r.Recycled);
        Assert.Equal(0, r.Freed);
        Assert.True(r.SkipReasons!.TryGetValue(SkipReason.PolicyRefused, out var refused) && refused > 0);
        Assert.True(File.Exists(keep), "a policy-refused file must still be there");
    }

    [Fact]
    public void CleanerDefaultSendsFilesToTheRecycleBin()
    {
        RequireWindows();
        var dir = Path.Combine(_folder, "bin-default");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "gone.bin");
        File.WriteAllBytes(path, new byte[2048]);
        var cat = JobCategory("a", "bin-default", dir, 2048);
        var (before, _) = Native.QueryRecycleBin();

        var r = Cleaner.Run([new(cat, cat.Items)], null);

        Assert.Equal(1, r.Files);
        Assert.Equal(2048, r.Recycled);
        Assert.Equal(0, r.Freed);
        Assert.False(File.Exists(path));
        var (after, _) = Native.QueryRecycleBin();
        Assert.True(after > before, "the file should now be in the Recycle Bin");
    }

    [Fact]
    public void PstFileInsideAllowlistedRootIsSkippedWithPolicyRefused()
    {
        RequireWindows();
        // The temp folder is allowlisted, but .pst files are denied at step 1 even
        // inside an allowlisted root: they may be the only copy of the mail.
        var dir = Path.Combine(_folder, "mail");
        Directory.CreateDirectory(dir);
        var pst = Path.Combine(dir, "archive.pst");
        var other = Path.Combine(dir, "other.bin");
        File.WriteAllBytes(pst, new byte[1024]);
        File.WriteAllBytes(other, new byte[1024]);

        var r = FileOps.RecycleContents(dir, null, policy: DeletionSafetyPolicy.Default);

        Assert.Equal(1, r.Files);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(1, r.Reasons[SkipReason.PolicyRefused]);
        Assert.True(File.Exists(pst), "the .pst file must still be there");
        Assert.False(File.Exists(other));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
