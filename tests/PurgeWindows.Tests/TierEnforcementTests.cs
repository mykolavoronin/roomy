using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// Tier enforcement at the engine level, with the UI bypassed: the cleaner
/// refuses Check-First jobs without an explicit confirmation, the Safe-only
/// rule rejects anything non-Safe, and owner-tool consent only ever flows
/// from the confirmation path. Tests use paths that do not exist, so nothing
/// is deleted; refusal happens before the first event is emitted.
/// </summary>
public sealed class TierEnforcementTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "PurgeWindowsTests", Guid.NewGuid().ToString("N"));

    public TierEnforcementTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    string Missing(string name) => Path.Combine(_folder, name);

    static CleanupCategory Category(string id, SafetyLevel tier, CleanAction action = CleanAction.Delete)
    {
        var cat = new CleanupCategory { Id = id, Title = id, Description = "", Glyph = "", Tier = tier, Action = action };
        return cat;
    }

    CleanJob Job(string id, SafetyLevel tier)
    {
        var cat = Category(id, tier);
        cat.Items.Add(new CleanupItem { Name = "item", Paths = [Missing(id)], Size = 1024 });
        return new CleanJob(cat, cat.Items);
    }

    sealed class SyncEvents : IProgress<CleanEvent>
    {
        public List<CleanEvent> Seen { get; } = [];
        public void Report(CleanEvent value) => Seen.Add(value);
    }

    [Fact]
    public void RefusesCheckFirstWithoutConfirmation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Cleaner.Run([Job("downloads", SafetyLevel.CheckFirst)], null));

        Assert.Contains("downloads", ex.Message);
    }

    [Fact]
    public void RefusalHappensBeforeAnyEventOrWork()
    {
        var events = new SyncEvents();
        var options = new CleanOptions { Events = events };

        Assert.Throws<InvalidOperationException>(() =>
            Cleaner.Run([Job("downloads", SafetyLevel.CheckFirst)], null, options: options));

        Assert.Empty(events.Seen);
    }

    [Fact]
    public void RefusesCheckFirstWhenConfirmationCoversAnotherCategory()
    {
        var options = new CleanOptions
        {
            CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(["other"]),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Cleaner.Run([Job("downloads", SafetyLevel.CheckFirst)], null, options: options));

        Assert.Contains("downloads", ex.Message);
    }

    [Fact]
    public void RefusalListsEveryUncoveredCategory()
    {
        var options = new CleanOptions
        {
            CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(["downloads"]),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Cleaner.Run([
                Job("downloads", SafetyLevel.CheckFirst),
                Job("devcaches", SafetyLevel.CheckFirst),
            ], null, options: options));

        Assert.Contains("devcaches", ex.Message);
        Assert.DoesNotContain("downloads", ex.Message);
    }

    [Fact]
    public void AllowsCheckFirstWithCoveringConfirmation()
    {
        var options = new CleanOptions
        {
            CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(["downloads"]),
        };

        var r = Cleaner.Run([Job("downloads", SafetyLevel.CheckFirst)], null, options: options);

        Assert.Equal(0, r.Files);
    }

    [Fact]
    public void SafeJobsNeedNoConfirmation()
    {
        var r = Cleaner.Run([Job("temp", SafetyLevel.Safe)], null);

        Assert.Equal(0, r.Files);
    }

    [Fact]
    public void SafeTierOnlyRejectsCheckFirstEvenWithConfirmation()
    {
        // The safe-clean rule: a confirmation-free Safe flow rejects anything
        // non-Safe, whatever else the options say.
        var options = new CleanOptions
        {
            SafeTierOnly = true,
            CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(["downloads"]),
        };

        Assert.Throws<InvalidOperationException>(() =>
            Cleaner.Run([Job("downloads", SafetyLevel.CheckFirst)], null, options: options));
    }

    [Fact]
    public void SafeTierOnlyAcceptsSafeJobs()
    {
        var options = new CleanOptions { SafeTierOnly = true };

        var r = Cleaner.Run([Job("temp", SafetyLevel.Safe)], null, options: options);

        Assert.Equal(0, r.Files);
    }

    [Fact]
    public void NonExecutableJobsNeedNoConfirmation()
    {
        // ReportOnly jobs never reach the engine's cleaning loop.
        var cat = Category("virtual", SafetyLevel.ReportOnly, CleanAction.ReportOnly);
        cat.Items.Add(new CleanupItem { Name = "vm", Paths = [Missing("vm")], Size = 1024 });

        var r = Cleaner.Run([new CleanJob(cat, cat.Items)], null);

        Assert.Equal(0, r.Files);
    }

    [Fact]
    public void OwnerToolConsentWithoutConfirmationIsRefused()
    {
        var options = new CleanOptions { OwnerToolConsent = true };

        Assert.Throws<InvalidOperationException>(() =>
            Cleaner.Run([Job("temp", SafetyLevel.Safe)], null, options: options));
    }

    [Fact]
    public void OwnerToolConsentWithoutOwnerConsentInConfirmationIsRefused()
    {
        var options = new CleanOptions
        {
            OwnerToolConsent = true,
            CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(
                ["downloads"], ownerToolsConsented: false),
        };

        Assert.Throws<InvalidOperationException>(() =>
            Cleaner.Run([Job("downloads", SafetyLevel.CheckFirst)], null, options: options));
    }

    [Fact]
    public void OwnerToolAllowedOnlyForCoveredCheckFirstJobs()
    {
        var full = new CleanOptions
        {
            OwnerToolConsent = true,
            CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(
                ["downloads"], ownerToolsConsented: true),
        };
        var checkFirst = Job("downloads", SafetyLevel.CheckFirst);
        var safe = Job("temp", SafetyLevel.Safe);

        // Consent never applies to Safe items — permanent removal is never Safe.
        Assert.False(CleanOptions.OwnerToolAllowed(safe, full));
        Assert.True(CleanOptions.OwnerToolAllowed(checkFirst, full));
        Assert.False(CleanOptions.OwnerToolAllowed(checkFirst, null));

        var noFlag = new CleanOptions
        {
            CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(
                ["downloads"], ownerToolsConsented: true),
        };
        Assert.False(CleanOptions.OwnerToolAllowed(checkFirst, noFlag));

        var uncovered = new CleanOptions
        {
            OwnerToolConsent = true,
            CheckFirstConfirmation = CheckFirstConfirmation.FromConfirmationDialog(
                ["other"], ownerToolsConsented: true),
        };
        Assert.False(CleanOptions.OwnerToolAllowed(checkFirst, uncovered));
    }

    [Fact]
    public void ConfirmationCoversOnlyWhatItNames()
    {
        var c = CheckFirstConfirmation.FromConfirmationDialog(["a", "b"]);

        Assert.True(c.Covers("a"));
        Assert.True(c.Covers("b"));
        Assert.False(c.Covers("c"));
        Assert.False(c.OwnerToolsConsented);
    }
}
