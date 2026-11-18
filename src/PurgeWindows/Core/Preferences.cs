using System.Text.Json;
using PurgeWindows.Localization;

namespace PurgeWindows.Core;

public sealed record Preferences
{
    public bool RunInBackground { get; init; }
    public bool LowSpaceAlerts { get; init; }
    public bool WeeklyChecks { get; init; }
    public int LowSpaceGB { get; init; } = 10;
    public int CheckEveryMinutes { get; init; } = 30;
    /// <summary>Where cleaned files go. The default sends "your call" items to the Recycle Bin.</summary>
    public RemovalMode Removal { get; init; }
    public DateTimeOffset? SnoozedUntil { get; init; }
    public DateTimeOffset? LastNotification { get; init; }
    public DateTimeOffset? LastWeeklyCheck { get; init; }

    public Preferences Normalize() => this with
    {
        LowSpaceGB = Math.Clamp(LowSpaceGB, 1, 500),
        CheckEveryMinutes = CheckEveryMinutes == 60 ? 60 : 30,
        Removal = Removal is RemovalMode.ReviewToBin or RemovalMode.EverythingToBin or RemovalMode.EverythingPermanent
            ? Removal : RemovalMode.ReviewToBin,
    };
}

public sealed class PreferencesStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PurgeWindows", "settings.json");

    // HISTORICAL: the pre-rename app (Roomy) stored its settings here. First-run
    // migration copies them forward; the old folder is never deleted or modified.
    static string LegacyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roomy", "settings.json");

    public string? LoadError { get; private set; }

    public Preferences Load()
    {
        MigrateLegacySettings();
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new()).Normalize()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = S.CantReadSettings;
            return new();
        }
    }

    /// <summary>On first run, copy settings from the pre-rename folder if the new one has none.</summary>
    void MigrateLegacySettings()
    {
        // Only the default location migrates: tests and tools pass their own paths.
        if (!string.Equals(path, DefaultPath, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (File.Exists(path) || !File.Exists(LegacyPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.Copy(LegacyPath, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the defaults; the old file stays untouched for a later run.
        }
    }

    public void Save(Preferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        LoadError = null;
    }
}

public static class ReminderPolicy
{
    public static bool CanNotify(Preferences p, DateTimeOffset now) => p.RunInBackground &&
        (p.SnoozedUntil is null || now >= p.SnoozedUntil) &&
        (p.LastNotification is null || now - p.LastNotification >= TimeSpan.FromDays(1));

    public static bool IsLowSpace(Preferences p, long freeBytes) => p.LowSpaceAlerts &&
        freeBytes >= 0 && freeBytes < p.LowSpaceGB * (1L << 30);

    public static bool WeeklyCheckDue(Preferences p, DateTimeOffset now) => p.WeeklyChecks &&
        CanNotify(p, now) && (p.LastWeeklyCheck is null || now - p.LastWeeklyCheck >= TimeSpan.FromDays(7));
}
