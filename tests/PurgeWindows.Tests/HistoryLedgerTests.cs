using System.Text.Json;
using PurgeWindows.Core;
using PurgeWindows.Services;

namespace PurgeWindows.Tests;

/// <summary>
/// Cleanup history (100-entry JSON) and the append-only ledger (yearly JSONL):
/// round-trips, caps, resilient decoding, baseline survival, reconciliation, and
/// the field name that must never say "trash" on Windows.
/// </summary>
public sealed class HistoryLedgerTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "PurgeWindowsTests", Guid.NewGuid().ToString("N"));

    public HistoryLedgerTests() => Directory.CreateDirectory(_folder);

    static CleanupHistoryEntry Entry(Guid id, long recycled, DateTimeOffset? date = null) => new(
        id, date ?? DateTimeOffset.Now, CleanupTrigger.Manual, "cleanup",
        recycled, null, [], [], [],
        [new HistoryCategorySummary("temp", "Temporary files", 0, recycled, 3)],
        null, CleanupVerificationState.Unknown, recycled > 0);

    static LedgerSession Session(Guid id, long recycled, long removed, DateTimeOffset? date = null) => new(
        id, date ?? new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero),
        CleanupTrigger.Manual, LedgerSource.Manual, recycled, removed, null, 0, 0, false,
        [new LedgerItem(@"~\AppData\Local\Temp\a.bin", recycled, true)]);

    [Fact]
    public void HistoryRoundTripsNewestFirst()
    {
        var store = new CleanupHistoryStore(Path.Combine(_folder, "history.json"));
        var first = Entry(Guid.NewGuid(), 100);
        var second = Entry(Guid.NewGuid(), 200);
        store.Record(first);
        store.Record(second);

        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(second.Id, loaded[0].Id);
        Assert.Equal(first.Id, loaded[1].Id);
        Assert.Equal(200, loaded[0].BytesMovedToRecycleBin);
        Assert.Null(loaded[0].BytesReclaimedOnVolume);
    }

    [Fact]
    public void HistoryCapsAt100Entries()
    {
        var store = new CleanupHistoryStore(Path.Combine(_folder, "history.json"));
        for (int i = 0; i < 101; i++) store.Record(Entry(Guid.NewGuid(), i));
        var loaded = store.Load();
        Assert.Equal(CleanupHistoryStore.MaxEntries, loaded.Count);
        Assert.Equal(100, loaded[0].BytesMovedToRecycleBin);
    }

    [Fact]
    public void HistorySkipsCorruptEntriesAndDropsUnknownKinds()
    {
        var path = Path.Combine(_folder, "history.json");
        var good = Entry(Guid.NewGuid(), 100);
        var unknownKind = good with { Id = Guid.NewGuid(), Kind = "mystery" };
        var doc = "{\"entries\":[" + JsonSerializer.Serialize(good) + ",{\"bogus\":true},"
            + JsonSerializer.Serialize(unknownKind) + "]}";
        File.WriteAllText(path, doc);

        var loaded = new CleanupHistoryStore(path).Load();
        Assert.Single(loaded);
        Assert.Equal(good.Id, loaded[0].Id);
    }

    [Fact]
    public void HistoryFileNamesTheRecycleBinField()
    {
        var path = Path.Combine(_folder, "history.json");
        new CleanupHistoryStore(path).Record(Entry(Guid.NewGuid(), 100));
        var text = File.ReadAllText(path);
        Assert.Contains("bytesMovedToRecycleBin", text);
        Assert.DoesNotContain("movedToTrash", text);
    }

    [Fact]
    public void LedgerRoundTripsAndSkipsTornLines()
    {
        var store = new CleanupLedgerStore(Path.Combine(_folder, "ledger"));
        var session = Session(Guid.NewGuid(), 500, 0);
        store.Append(session);
        // A torn line from a crashed write, glued into the same year file.
        File.AppendAllText(Path.Combine(_folder, "ledger", "2026.jsonl"), "{\"id\":\"torn\",broken\n");

        var read = store.ReadYear(2026);
        Assert.Single(read);
        Assert.Equal(session.Id, read[0].Id);
        Assert.Equal(500, read[0].BytesMovedToRecycleBin);
    }

    [Fact]
    public void LedgerAppendHandlesAMissingTrailingNewline()
    {
        var dir = Path.Combine(_folder, "ledger");
        Directory.CreateDirectory(dir);
        var first = Session(Guid.NewGuid(), 100, 0);
        // A crashed write: a complete line with no trailing newline.
        File.WriteAllText(Path.Combine(dir, "2026.jsonl"), JsonSerializer.Serialize(first));
        new CleanupLedgerStore(dir).Append(Session(Guid.NewGuid(), 200, 0));

        Assert.Equal(2, new CleanupLedgerStore(dir).ReadYear(2026).Count);
    }

    [Fact]
    public void LedgerSkipsSessionsThatRemovedNothing()
    {
        var store = new CleanupLedgerStore(Path.Combine(_folder, "ledger"));
        store.Append(Session(Guid.NewGuid(), 0, 0));
        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public void LedgerClearKeepsLifetimeTotals()
    {
        var store = new CleanupLedgerStore(Path.Combine(_folder, "ledger"));
        store.Append(Session(Guid.NewGuid(), 100, 50));
        store.Append(Session(Guid.NewGuid(), 200, 0, new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero)));

        store.Clear();

        Assert.Empty(store.ReadAll());
        Assert.Equal((300, 50), store.LifetimeTotals());
    }

    [Fact]
    public void LedgerReconcileImportsMissingHistoryEntriesById()
    {
        var store = new CleanupLedgerStore(Path.Combine(_folder, "ledger"));
        var known = Guid.NewGuid();
        store.Append(Session(known, 100, 0));
        var missing = Entry(Guid.NewGuid(), 300);
        var alreadyThere = Entry(known, 100);

        var imported = store.Reconcile([missing, alreadyThere]);

        Assert.Equal(1, imported);
        var sessions = store.ReadAll();
        Assert.Equal(2, sessions.Count);
        var importedSession = sessions.Single(s => s.Id == missing.Id);
        Assert.Equal(LedgerSource.Imported, importedSession.Source);
        Assert.True(importedSession.ImportedFromHistory);
        // Reconciling again imports nothing: the match is by id.
        Assert.Equal(0, store.Reconcile([missing, alreadyThere]));
    }

    [Fact]
    public void ToHomeRelativeMakesProfilePathsRelative()
    {
        Assert.True(OperatingSystem.IsWindows(), "Profile-relative paths use Windows separators.");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal("~", CleanupLedgerStore.ToHomeRelative(profile));
        Assert.Equal(@"~\" + "Documents" + @"\" + "x.txt",
            CleanupLedgerStore.ToHomeRelative(Path.Combine(profile, "Documents", "x.txt")));
        var outside = Path.Combine(Path.GetPathRoot(profile)!, "elsewhere", "x.txt");
        Assert.Equal(outside, CleanupLedgerStore.ToHomeRelative(outside));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
