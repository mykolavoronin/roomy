using PurgeWindows.Core;

namespace PurgeWindows.Tests;

public sealed class PreferencesTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "PurgeWindowsTests", Guid.NewGuid().ToString("N"));
    string SettingsPath => Path.Combine(_folder, "settings.json");

    [Fact]
    public void MissingSettingsUseTheRecycleBinDefaultWithoutCreatingFiles()
    {
        var p = new PreferencesStore(SettingsPath).Load();
        Assert.False(p.RunInBackground);
        Assert.Equal(RemovalMode.ReviewToBin, p.Removal);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void RemovalChoiceSurvivesRestart()
    {
        new PreferencesStore(SettingsPath).Save(new() { Removal = RemovalMode.EverythingPermanent });
        Assert.Equal(RemovalMode.EverythingPermanent, new PreferencesStore(SettingsPath).Load().Removal);
    }

    [Fact]
    public void OlderSettingsFilesUseTheRecycleBinDefault()
    {
        Directory.CreateDirectory(_folder);
        // Written by an older version: the old RecycleDownloads switch is ignored.
        File.WriteAllText(SettingsPath, "{\"RunInBackground\":true,\"RecycleDownloads\":true}");
        Assert.Equal(RemovalMode.ReviewToBin, new PreferencesStore(SettingsPath).Load().Removal);
    }

    [Fact]
    public void UnknownRemovalValuesFallBackToTheDefault()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "{\"Removal\":99}");
        Assert.Equal(RemovalMode.ReviewToBin, new PreferencesStore(SettingsPath).Load().Removal);
    }

    [Fact]
    public void CooldownAndSnoozeSurviveRestart()
    {
        var p = new Preferences
        {
            RunInBackground = true, LowSpaceAlerts = true, WeeklyChecks = true,
            LowSpaceGB = 25, CheckEveryMinutes = 60,
            LastNotification = DateTimeOffset.UtcNow, SnoozedUntil = DateTimeOffset.UtcNow.AddDays(1),
            LastWeeklyCheck = DateTimeOffset.UtcNow.AddDays(-3),
        };
        new PreferencesStore(SettingsPath).Save(p);
        var loaded = new PreferencesStore(SettingsPath).Load();
        Assert.Equal(p, loaded);
        Assert.False(ReminderPolicy.CanNotify(loaded, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void InvalidJsonFailsClosedAndKeepsOriginalForRecovery()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "{broken");
        var store = new PreferencesStore(SettingsPath);
        Assert.False(store.Load().RunInBackground);
        Assert.NotNull(store.LoadError);
        Assert.Equal("{broken", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void SavingReplacesSettingsAndLeavesNoTemporaryFiles()
    {
        var store = new PreferencesStore(SettingsPath);
        store.Save(new() { RunInBackground = true });
        store.Save(new() { RunInBackground = false });
        Assert.False(store.Load().RunInBackground);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void OutOfRangeSettingsAreNormalizedOnLoad()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "{\"LowSpaceGB\":2147483647,\"CheckEveryMinutes\":0}");
        var p = new PreferencesStore(SettingsPath).Load();
        Assert.Equal(500, p.LowSpaceGB);
        Assert.Equal(30, p.CheckEveryMinutes);
    }

    public void Dispose()
    {
        // Only remove files this test created. Never recurse through links in a temp folder.
        if (!Directory.Exists(_folder)) return;
        foreach (var file in Directory.GetFiles(_folder)) File.Delete(file);
        Directory.Delete(_folder);
    }
}
