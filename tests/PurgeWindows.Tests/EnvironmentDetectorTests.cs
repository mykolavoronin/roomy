using PurgeWindows.Core.Environment;

namespace PurgeWindows.Tests;

/// <summary>
/// Detector tests: TTL caching and merging, force refresh, per-probe and total timeouts
/// (via fake slow sources, never sleeps in production paths), and WMI retry/caching.
/// </summary>
public sealed class EnvironmentDetectorTests
{
    static ProbeTimeouts GenerousTimeouts() => new(
        Os: TimeSpan.FromSeconds(5), Components: TimeSpan.FromSeconds(5),
        ModMarkers: TimeSpan.FromSeconds(5), Antivirus: TimeSpan.FromSeconds(5),
        Services: TimeSpan.FromSeconds(5), Updates: TimeSpan.FromSeconds(5),
        StorageSense: TimeSpan.FromSeconds(5), OneDrive: TimeSpan.FromSeconds(5),
        Managed: TimeSpan.FromSeconds(5), Virtualization: TimeSpan.FromSeconds(5),
        Hardware: TimeSpan.FromSeconds(5), Pending: TimeSpan.FromSeconds(5),
        Total: TimeSpan.FromSeconds(30));

    [Fact]
    public async Task DetectAsyncReturnsKnownOutcomesForAPlainMachine()
    {
        var detector = new EnvironmentDetector(FakeEnvironmentDataSource.AllKnownDefaults(),
            GenerousTimeouts(), () => DateTimeOffset.UtcNow);

        var snapshot = await detector.DetectAsync();

        foreach (var summary in snapshot.ProbeSummaries())
            Assert.True(summary.IsKnown, $"probe {summary.Id} should be known");
        Assert.False(snapshot.IsElevated == false);
        Assert.Equal(ModVerdict.Stock, snapshot.ModScore.Verdict);
    }

    [Fact]
    public async Task SecondDetectionReusesCachedResults()
    {
        var fake = FakeEnvironmentDataSource.AllKnownDefaults();
        var wscCalls = 0;
        var baseWsc = fake.WscQuery;
        fake.WscQuery = provider => { wscCalls++; return baseWsc(provider); };
        var ntVersionReads = 0;
        fake.RegistryReadHook = (hive, path, name, view) =>
        {
            if (path == @"SOFTWARE\Microsoft\Windows NT\CurrentVersion") ntVersionReads++;
            return null;
        };
        var detector = new EnvironmentDetector(fake, GenerousTimeouts(), () => DateTimeOffset.UtcNow);

        await detector.DetectAsync();
        var wscAfterFirst = wscCalls;
        var readsAfterFirst = ntVersionReads;
        Assert.True(wscAfterFirst > 0);
        Assert.True(readsAfterFirst > 0);

        await detector.DetectAsync();

        Assert.Equal(wscAfterFirst, wscCalls);
        Assert.Equal(readsAfterFirst, ntVersionReads);
    }

    [Fact]
    public async Task VolatileProbesRefreshAfterFiveMinutesWhileStableOnesDoNot()
    {
        var current = DateTimeOffset.UtcNow;
        var fake = FakeEnvironmentDataSource.AllKnownDefaults();
        var wscCalls = 0;
        var baseWsc = fake.WscQuery;
        fake.WscQuery = provider => { wscCalls++; return baseWsc(provider); };
        var ntVersionReads = 0;
        fake.RegistryReadHook = (hive, path, name, view) =>
        {
            if (path == @"SOFTWARE\Microsoft\Windows NT\CurrentVersion") ntVersionReads++;
            return null;
        };
        var detector = new EnvironmentDetector(fake, GenerousTimeouts(), () => current);

        await detector.DetectAsync();
        var wscAfterFirst = wscCalls;
        var readsAfterFirst = ntVersionReads;

        current = current.AddMinutes(6);
        await detector.DetectAsync();

        Assert.True(wscCalls > wscAfterFirst, "the volatile AV probe should have re-run");
        Assert.Equal(readsAfterFirst, ntVersionReads);
    }

    [Fact]
    public async Task StableProbesRefreshAfterSixtyMinutes()
    {
        var current = DateTimeOffset.UtcNow;
        var fake = FakeEnvironmentDataSource.AllKnownDefaults();
        var ntVersionReads = 0;
        fake.RegistryReadHook = (hive, path, name, view) =>
        {
            if (path == @"SOFTWARE\Microsoft\Windows NT\CurrentVersion") ntVersionReads++;
            return null;
        };
        var detector = new EnvironmentDetector(fake, GenerousTimeouts(), () => current);

        await detector.DetectAsync();
        var readsAfterFirst = ntVersionReads;

        current = current.AddMinutes(61);
        await detector.DetectAsync();

        Assert.True(ntVersionReads > readsAfterFirst, "the stable OS probe should have re-run");
    }

    [Fact]
    public async Task ForceRefreshRerunsEveryProbeAndClearsCaches()
    {
        var fake = FakeEnvironmentDataSource.AllKnownDefaults();
        var wscCalls = 0;
        var baseWsc = fake.WscQuery;
        fake.WscQuery = provider => { wscCalls++; return baseWsc(provider); };
        var detector = new EnvironmentDetector(fake, GenerousTimeouts(), () => DateTimeOffset.UtcNow);

        await detector.DetectAsync();
        var wscAfterFirst = wscCalls;

        await detector.DetectAsync(forceRefresh: true);

        Assert.True(wscCalls > wscAfterFirst);
        Assert.Equal(1, fake.ClearCachesCalls);
    }

    [Fact]
    public async Task UnknownOutcomesAreNeverCached()
    {
        var fake = FakeEnvironmentDataSource.AllKnownDefaults();
        fake.WscQuery = _ => WscQueryResult.Failed();
        fake.WmiQuery = (ns, query, _) => ns.Contains("SecurityCenter2") || query.Contains("MSFT_MpComputerStatus")
            ? WmiQueryResult.Failed()
            : WmiQueryResult.Ok([]);
        var wscCalls = 0;
        var baseWsc = fake.WscQuery;
        fake.WscQuery = provider => { wscCalls++; return baseWsc(provider); };
        var detector = new EnvironmentDetector(fake, GenerousTimeouts(), () => DateTimeOffset.UtcNow);

        var first = await detector.DetectAsync();
        Assert.False(first.Antivirus.IsKnown);
        var wscAfterFirst = wscCalls;

        var second = await detector.DetectAsync();

        Assert.False(second.Antivirus.IsKnown);
        Assert.True(wscCalls > wscAfterFirst, "an Unknown probe must re-run on the next detection");
    }

    [Fact]
    public async Task WmiFailureYieldsUnknownAntivirusButKnownOs()
    {
        var fake = FakeEnvironmentDataSource.AllKnownDefaults();
        fake.WmiQuery = (_, _, _) => WmiQueryResult.Failed();
        fake.WscQuery = _ => WscQueryResult.Failed();
        var detector = new EnvironmentDetector(fake, GenerousTimeouts(), () => DateTimeOffset.UtcNow);

        var snapshot = await detector.DetectAsync();

        Assert.False(snapshot.Antivirus.IsKnown);
        Assert.Equal(ProbeUnknownReason.QueryFailed, snapshot.Antivirus.UnknownReason);
        Assert.True(snapshot.Os.IsKnown);
    }

    [Fact]
    public async Task SlowProbeYieldsUnknownWithinItsTimeout()
    {
        var release = new ManualResetEventSlim(false);
        try
        {
            var fake = FakeEnvironmentDataSource.AllKnownDefaults();
            fake.WscQuery = _ =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return WscQueryResult.Failed();
            };
            var timeouts = GenerousTimeouts() with
            {
                Antivirus = TimeSpan.FromMilliseconds(50),
                Total = TimeSpan.FromSeconds(10),
            };
            var detector = new EnvironmentDetector(fake, timeouts, () => DateTimeOffset.UtcNow);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = await detector.DetectAsync();
            watch.Stop();

            Assert.False(snapshot.Antivirus.IsKnown);
            Assert.Equal(ProbeUnknownReason.TimedOut, snapshot.Antivirus.UnknownReason);
            Assert.True(snapshot.Os.IsKnown);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5),
                $"detection took {watch.Elapsed}; a slow probe must not stall it");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task TotalBudgetAbandonsAllSlowProbes()
    {
        var release = new ManualResetEventSlim(false);
        try
        {
            var fake = FakeEnvironmentDataSource.AllKnownDefaults();
            void Block() => release.Wait(TimeSpan.FromSeconds(30));
            fake.WscQuery = _ => { Block(); return WscQueryResult.Failed(); };
            fake.WmiQuery = (_, _, _) => { Block(); return WmiQueryResult.Failed(); };
            fake.RegistryReadHook = (_, _, _, _) => { Block(); return RegistryRead.UnreadableValue(); };
            fake.ServiceHook = _ => { Block(); return ServiceState.Unknown; };
            fake.FileCheck = _ => { Block(); return Presence.Unknown; };
            fake.DirectoryEnumeration = (_, _) => { Block(); return FileSearchResult.Failed(); };
            fake.FileEnumeration = (_, _, _) => { Block(); return FileSearchResult.Failed(); };
            fake.Processes = ProcessQueryResult.Failed();
            var timeouts = GenerousTimeouts() with { Total = TimeSpan.FromMilliseconds(200) };
            var detector = new EnvironmentDetector(fake, timeouts, () => DateTimeOffset.UtcNow);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = await detector.DetectAsync();
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5),
                $"detection took {watch.Elapsed}; the total budget must bound it");
            foreach (var summary in snapshot.ProbeSummaries())
            {
                Assert.False(summary.IsKnown, $"probe {summary.Id} should be unknown");
                Assert.Equal(ProbeUnknownReason.TimedOut, summary.Reason);
            }
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task UnknownDetectorReturnsAllUnknownWithoutProbing()
    {
        var detector = new UnknownEnvironmentDetector();
        var snapshot = await detector.DetectAsync();
        foreach (var summary in snapshot.ProbeSummaries())
        {
            Assert.False(summary.IsKnown);
            Assert.Equal(ProbeUnknownReason.NotAvailable, summary.Reason);
        }
        Assert.Equal(ModVerdict.Unknown, snapshot.ModScore.Verdict);
    }

    // -------------------------------------------------------------- WMI client

    [Fact]
    public void WmiClientRetriesTwiceThenSucceeds()
    {
        var calls = 0;
        var client = new WmiClient(
            (_, _, _) => ++calls < 3
                ? WmiQueryResult.Failed()
                : WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("Name", "x"))]),
            () => DateTimeOffset.UtcNow);

        var result = client.Query("root\\CIMV2", "SELECT Name FROM X", ["Name"]);

        Assert.True(result.Succeeded);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void WmiClientGivesUpAfterTwoRetriesAndDoesNotCacheFailures()
    {
        var calls = 0;
        var client = new WmiClient((_, _, _) =>
        {
            calls++;
            return WmiQueryResult.Failed();
        }, () => DateTimeOffset.UtcNow);

        Assert.False(client.Query("root\\CIMV2", "SELECT Name FROM X", ["Name"]).Succeeded);
        Assert.Equal(3, calls);
        Assert.False(client.Query("root\\CIMV2", "SELECT Name FROM X", ["Name"]).Succeeded);
        Assert.Equal(6, calls);
    }

    [Fact]
    public void WmiClientTreatsThrowingExecutorsAsFailures()
    {
        var client = new WmiClient((_, _, _) => throw new InvalidOperationException("boom"),
            () => DateTimeOffset.UtcNow);
        Assert.False(client.Query("root\\CIMV2", "SELECT Name FROM X", ["Name"]).Succeeded);
    }

    [Fact]
    public void WmiClientCachesSuccessesForFiveMinutes()
    {
        var current = DateTimeOffset.UtcNow;
        var calls = 0;
        var client = new WmiClient((_, _, _) =>
        {
            calls++;
            return WmiQueryResult.Ok([]);
        }, () => current);

        client.Query("root\\CIMV2", "SELECT Name FROM X", ["Name"]);
        client.Query("root\\CIMV2", "SELECT Name FROM X", ["Name"]);
        Assert.Equal(1, calls);

        current = current.AddMinutes(6);
        client.Query("root\\CIMV2", "SELECT Name FROM X", ["Name"]);
        Assert.Equal(2, calls);

        client.ClearCache();
        client.Query("root\\CIMV2", "SELECT Name FROM X", ["Name"]);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void WmiClientCachesPerQuery()
    {
        var calls = 0;
        var client = new WmiClient((_, _, _) =>
        {
            calls++;
            return WmiQueryResult.Ok([]);
        }, () => DateTimeOffset.UtcNow);

        client.Query("root\\CIMV2", "SELECT A FROM X", ["A"]);
        client.Query("root\\CIMV2", "SELECT B FROM X", ["B"]);
        Assert.Equal(2, calls);
    }
}
