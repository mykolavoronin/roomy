using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// Volume capacity: the 64 MiB noise floor, per-drive planning, settled-gain
/// polling with a fake reader, and fail-soft capacity reads.
/// </summary>
public sealed class VolumeCapacityTests
{
    static void RequireWindows() =>
        Assert.True(OperatingSystem.IsWindows(), "Drive-letter planning requires Windows.");

    [Fact]
    public void NoiseFloorFiltersSmallAndNegativeDeltas()
    {
        long floor = VolumeCapacity.NoiseFloorBytes;
        Assert.Equal(64L * 1024 * 1024, floor);
        Assert.Null(VolumeCapacity.ReclaimedBytes(1000, 1000 + floor - 1));
        Assert.Equal(floor, VolumeCapacity.ReclaimedBytes(1000, 1000 + floor));
        Assert.Equal(floor + 5, VolumeCapacity.ReclaimedBytes(1000, 1000 + floor + 5));
        Assert.Null(VolumeCapacity.ReclaimedBytes(1000, 1000));
        Assert.Null(VolumeCapacity.ReclaimedBytes(1000, 500));
    }

    [Fact]
    public void PlanSplitsItemsAcrossDrivesAndSkipsReportOnlyJobs()
    {
        RequireWindows();
        var delete = new CleanupCategory { Id = "temp", Title = "Temp", Description = "", Glyph = "" };
        delete.Items.Add(new CleanupItem { Name = "a", Paths = [@"C:\a", @"D:\b"], Size = 101 });
        var report = Catalog.Create("virtual");
        report.Items.Add(new CleanupItem { Name = "vm", Paths = [@"C:\vm"], Size = 9999 });

        var planned = VolumeCapacity.Plan([new CleanJob(delete, delete.Items), new CleanJob(report, report.Items)]);

        // The remainder byte goes to the first drive.
        Assert.Equal(51, planned[@"C:\"]);
        Assert.Equal(50, planned[@"D:\"]);
        Assert.Equal(2, planned.Count);
    }

    [Fact]
    public async Task SettledGainPollingReturnsTheGainWhenItSettles()
    {
        long free = 1000;
        long? Reader(string _) => free += 10L * 1024 * 1024;

        var gain = await VolumeCapacity.MeasureSettledGainAsync(
            @"C:\", 1000, Reader, attempts: 10, interval: TimeSpan.FromMilliseconds(1));

        Assert.Equal(70L * 1024 * 1024, gain);
    }

    [Fact]
    public async Task SettledGainPollingReturnsNullWhenItNeverReachesTheFloor()
    {
        var gain = await VolumeCapacity.MeasureSettledGainAsync(
            @"C:\", 1000, _ => 1000 + 1024, attempts: 3, interval: TimeSpan.FromMilliseconds(1));
        Assert.Null(gain);
    }

    [Fact]
    public async Task SettledGainPollingTreatsUnreadableVolumesAsUnknown()
    {
        Assert.Null(await VolumeCapacity.MeasureSettledGainAsync(
            @"C:\", 1000, _ => null, attempts: 3, interval: TimeSpan.FromMilliseconds(1)));
        Assert.Null(await VolumeCapacity.MeasureSettledGainAsync(
            @"C:\", 1000, _ => throw new InvalidOperationException("boom"),
            attempts: 3, interval: TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task SettledGainPollingHonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await VolumeCapacity.MeasureSettledGainAsync(
            @"C:\", 1000, _ => 1000 + VolumeCapacity.NoiseFloorBytes,
            attempts: 10, interval: TimeSpan.FromMilliseconds(1), ct: cts.Token));
    }

    [Fact]
    public void CapacityReadsNeverThrow()
    {
        var all = VolumeCapacity.QueryAll([@"C:\", @"Z:\", ""]);
        Assert.Equal(3, all.Count);
        var free = VolumeCapacity.QueryFreeSpace(@"Z:\definitely-not-a-drive");
        Assert.Null(free.FreeBytes);
    }
}
