using System.Diagnostics;
using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// Recycle Bin behavior, including the Phase 0 hang regression tests (C1/C7).
/// These tests used to skip silently on CI ("Windows would wait on a 'delete
/// permanently?' prompt") — that skip hid the hang class instead of fixing it. The
/// shell path now uses no-UI flags with pre-checked recyclability, so the tests run
/// on Windows explicitly: the OS requirement is asserted, never silently skipped,
/// and shell calls must return promptly (a dialog block would hang forever).
/// </summary>
public sealed class RecycleTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "PurgeWindowsTests", Guid.NewGuid().ToString("N"));

    public RecycleTests() => Directory.CreateDirectory(_folder);

    string MakeFile(string name, TimeSpan age)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new byte[4096]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    /// <summary>Explicit OS guard: fails loudly off Windows instead of silently skipping.</summary>
    static void RequireWindows() =>
        Assert.True(OperatingSystem.IsWindows(), "Recycle Bin tests require Windows.");

    static BinCapacity CapacityFor(string folder, bool enabled, long capacityBytes, long usedBytes = 0) =>
        new(Path.GetPathRoot(folder)!, enabled, capacityBytes, usedBytes);

    [Fact]
    public void RecycleMovesTheFileToTheBin()
    {
        RequireWindows();
        var path = MakeFile("old-setup.exe", TimeSpan.FromDays(30));
        var (before, _) = Native.QueryRecycleBin();

        var sw = Stopwatch.StartNew();
        var r = FileOps.Recycle(path, TimeSpan.FromDays(7));
        sw.Stop();

        Assert.Equal(1, r.Files);
        Assert.Equal(4096, r.Freed);
        Assert.False(File.Exists(path));
        var (after, _) = Native.QueryRecycleBin();
        Assert.True(after > before, "the file should now be in the Recycle Bin");
        // A blocking shell dialog would hang indefinitely; 60s is generous for one file.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"recycle took {sw.Elapsed}");
    }

    [Fact]
    public void RecycleSkipsFilesNewerThanTheMinimumAge()
    {
        var path = MakeFile("new-setup.exe", TimeSpan.FromDays(1));
        var r = FileOps.Recycle(path, TimeSpan.FromDays(7));
        Assert.Equal(0, r.Files);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(1, r.Reasons[SkipReason.TooNew]);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RecycleNeverTouchesFolders()
    {
        var r = FileOps.Recycle(_folder, null);
        Assert.Equal(0, r.Files);
        Assert.True(Directory.Exists(_folder));
    }

    [Fact]
    public void CleanerRecyclesBothReviewAndSafeCategories()
    {
        RequireWindows();
        var download = MakeFile("big-download.zip", TimeSpan.FromDays(60));
        var cacheDir = Path.Combine(_folder, "cache");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(Path.Combine(cacheDir, "blob"), new byte[2048]);

        var downloads = new CleanupCategory { Id = "downloads", Title = "Downloads", Description = "", Glyph = "", Tier = SafetyLevel.CheckFirst };
        downloads.Items.Add(new CleanupItem { Name = "zip", Paths = [download], Size = 4096, IsFile = true });
        var caches = new CleanupCategory { Id = "apps", Title = "Caches", Description = "", Glyph = "" };
        caches.Items.Add(new CleanupItem { Name = "cache", Paths = [cacheDir], Size = 2048 });

        // The Check-First category needs an explicit confirmation (Wave 4 tier
        // enforcement); the test stands in for the confirmation dialog.
        var options = new CleanOptions
        {
            CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(["downloads"]),
        };
        var r = Cleaner.Run([new(downloads, downloads.Items), new(caches, caches.Items)], null, options: options);

        // D-21: the Recycle Bin is the default for ALL cleanable items, Safe and
        // Check-First alike. Nothing is deleted for good by hand.
        Assert.Equal(6144, r.Recycled);
        Assert.Equal(0, r.Freed);
        Assert.False(File.Exists(download));
        Assert.Empty(Directory.GetFiles(cacheDir));
        Assert.Equal(2, r.Categories!.Count);
        var dl = r.Categories.Single(c => c.CategoryId == "downloads");
        Assert.Equal(CleanDestination.Recycled, dl.Destination);
        Assert.Equal(4096, dl.Recycled);
        Assert.Equal(CleanDestination.Recycled, r.Categories.Single(c => c.CategoryId == "apps").Destination);
    }

    [Fact]
    public void EverythingToBinRecyclesCacheContents()
    {
        RequireWindows();
        var cacheDir = Path.Combine(_folder, "cache");
        var sub = Path.Combine(cacheDir, "nested");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(cacheDir, "blob"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(sub, "deep"), new byte[1024]);

        var caches = new CleanupCategory { Id = "apps", Title = "Caches", Description = "", Glyph = "" };
        caches.Items.Add(new CleanupItem { Name = "cache", Paths = [cacheDir], Size = 3072 });

        var r = Cleaner.Run([new(caches, caches.Items)], null, RemovalMode.EverythingToBin);

        Assert.Equal(3072, r.Recycled);
        Assert.Equal(0, r.Freed);
        Assert.True(Directory.Exists(cacheDir), "the folder itself stays");
        Assert.Empty(Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories));
        Assert.Equal(CleanDestination.Recycled, r.Categories!.Single().Destination);
    }

    [Fact]
    public void CleaningReportsLiveProgress()
    {
        RequireWindows();
        var cacheDir = Path.Combine(_folder, "cache");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(Path.Combine(cacheDir, "blob"), new byte[2048]);
        var caches = new CleanupCategory { Id = "apps", Title = "Caches", Description = "", Glyph = "" };
        caches.Items.Add(new CleanupItem { Name = "cache", Paths = [cacheDir], Size = 2048 });

        var seen = new List<CleanProgress>();
        var r = Cleaner.Run([new(caches, caches.Items)], new Progress<CleanProgress>(seen.Add));

        Assert.Equal(2048, r.Recycled);
        Assert.Equal(0, r.Freed);
        Assert.NotEmpty(seen);
        var last = seen[^1];
        Assert.Equal(1, last.Fraction);
        Assert.Equal(2048, last.RecycledSoFar);
        Assert.Equal(1, last.FilesSoFar);
        Assert.Contains(seen, p => p.CurrentItem == "cache");
    }

    [Fact]
    public void BothRemovalModesRecycleDownloads()
    {
        RequireWindows();
        // D-21: both remaining removal choices send everything to the Recycle Bin.
        // There is no mode that deletes Downloads for good.
        foreach (var mode in new[] { RemovalMode.ReviewToBin, RemovalMode.EverythingToBin })
        {
            var download = MakeFile($"old-{mode}.msi", TimeSpan.FromDays(60));
            var downloads = new CleanupCategory { Id = "downloads", Title = "Downloads", Description = "", Glyph = "", Tier = SafetyLevel.CheckFirst };
            downloads.Items.Add(new CleanupItem { Name = "msi", Paths = [download], Size = 4096, IsFile = true });

            var options = new CleanOptions
            {
                CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(["downloads"]),
            };
            var r = Cleaner.Run([new(downloads, downloads.Items)], null, mode, options: options);

            Assert.Equal(4096, r.Recycled);
            Assert.Equal(0, r.Freed);
            Assert.False(File.Exists(download));
        }
    }

    // ---------- Phase 0 hang regressions (C1/C7) ----------

    [Fact]
    public void ShellCallReturnsItsOutcomePromptlyWithNoUi()
    {
        RequireWindows();
        var path = MakeFile("shell-result.bin", TimeSpan.FromDays(30));

        var sw = Stopwatch.StartNew();
        var result = Native.SendFilesToRecycleBin([path]);
        sw.Stop();

        Assert.True(result.Succeeded, $"shell call should succeed, got rc={result.ReturnCode} aborted={result.Aborted}");
        Assert.False(File.Exists(path));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"shell call took {sw.Elapsed}");
    }

    [Fact]
    public void RecycleSkipsFilesThatDoNotFitInTheBinWithoutCallingTheShell()
    {
        RequireWindows();
        var path = MakeFile("too-big.bin", TimeSpan.FromDays(30));
        // Zero remaining capacity: the file must be skipped with a recorded reason,
        // never sent to a shell call that could prompt or destroy it.
        var budget = new RecycleBinBudget([CapacityFor(_folder, enabled: true, capacityBytes: 0, usedBytes: 0)]);

        var r = FileOps.Recycle(path, TimeSpan.FromDays(7), budget: budget);

        Assert.Equal(0, r.Files);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(4096, r.SkippedBytes);
        Assert.Equal(1, r.Reasons[SkipReason.TooBigForBin]);
        Assert.True(File.Exists(path), "a skipped file must still be there");
    }

    [Fact]
    public void RecycleContentsSkipsABinDisabledDriveWithoutCallingTheShell()
    {
        RequireWindows();
        var dir = Path.Combine(_folder, "disabled");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[1024]);
        var budget = new RecycleBinBudget([CapacityFor(_folder, enabled: false, capacityBytes: 1024 * 1024)]);

        var r = FileOps.RecycleContents(dir, null, budget: budget);

        Assert.Equal(0, r.Files);
        Assert.Equal(2, r.Skipped);
        Assert.Equal(2, r.Reasons[SkipReason.BinDisabled]);
        Assert.Equal(2, Directory.GetFiles(dir).Length);
    }

    [Fact]
    public void RecycleContentsHonorsABudgetAcrossBatches()
    {
        RequireWindows();
        var dir = Path.Combine(_folder, "budgeted");
        Directory.CreateDirectory(dir);
        // 450 files x 1 KiB in one tree: batches of 200/200/50, so the budget must be
        // spent across batch boundaries, not reset per batch. Skipping is batch-granular:
        // a batch that does not fit is skipped whole, and a later smaller batch may fit.
        for (int i = 0; i < 450; i++) File.WriteAllBytes(Path.Combine(dir, $"f{i}.bin"), new byte[1024]);
        // Room for 300 KiB: batch 1 (200 KiB) recycles, batch 2 (200 KiB) does not fit
        // in the 100 KiB left and is skipped, batch 3 (50 KiB) fits and recycles.
        var budget = new RecycleBinBudget([CapacityFor(_folder, enabled: true, capacityBytes: 300 * 1024)]);

        var sw = Stopwatch.StartNew();
        var r = FileOps.RecycleContents(dir, null, budget: budget);
        sw.Stop();

        Assert.Equal(250, r.Files);
        Assert.Equal(250 * 1024, r.Freed);
        Assert.Equal(200, r.Skipped);
        Assert.Equal(200, r.Reasons[SkipReason.TooBigForBin]);
        Assert.Equal(200, Directory.GetFiles(dir).Length);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(120), $"batched recycle took {sw.Elapsed}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
