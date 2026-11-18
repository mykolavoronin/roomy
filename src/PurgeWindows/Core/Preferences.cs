using System.Text.Json;
using PurgeWindows.Localization;

namespace PurgeWindows.Core;

/// <summary>
/// The mod-verdict override (D-13): the user's explicit, reversible choice to treat their
/// Windows as stock or as modified, regardless of what detection found. An override can
/// never create brand evidence: forcing "modified" yields a generic modified verdict.
/// Serialized as a number in settings.json; values are stable — do not renumber.
/// </summary>
public enum ModVerdictOverride
{
    /// <summary>No override: use the detected verdict.</summary>
    None = 0,
    /// <summary>Treat this Windows as stock, whatever detection found.</summary>
    TreatAsStock = 1,
    /// <summary>Treat this Windows as modified (generic — never a brand name).</summary>
    TreatAsModified = 2,
}

public sealed record Preferences
{
    public bool RunInBackground { get; init; }
    public bool LowSpaceAlerts { get; init; }
    public bool WeeklyChecks { get; init; }
    public int LowSpaceGB { get; init; } = 10;
    public int CheckEveryMinutes { get; init; } = 30;
    /// <summary>Where cleaned files go. Every cleanable item goes to the Recycle Bin (D-21).</summary>
    public RemovalMode Removal { get; init; }
    /// <summary>Whether cleans are recorded in cleanup history + the ledger. Default on.</summary>
    public bool RecordCleanupHistory { get; init; } = true;
    /// <summary>After how many days without changes a developer project counts as stale (0 = show all).
    /// Allowed values: 0, 30, 90, 180, 365, 730. Default 180.</summary>
    public int DevToolsStalenessDays { get; init; } = 180;
    /// <summary>The user's mod-verdict override (D-13). Default: no override, use detection.</summary>
    public ModVerdictOverride ModOverride { get; init; }
    public DateTimeOffset? SnoozedUntil { get; init; }
    public DateTimeOffset? LastNotification { get; init; }
    public DateTimeOffset? LastWeeklyCheck { get; init; }

    public Preferences Normalize() => this with
    {
        LowSpaceGB = Math.Clamp(LowSpaceGB, 1, 500),
        CheckEveryMinutes = CheckEveryMinutes == 60 ? 60 : 30,
        // RemovalMode.EverythingPermanent (numeric 2) was removed with D-21: a
        // settings file that still holds it falls back to the default, which now
        // also sends everything to the Recycle Bin.
        Removal = Removal is RemovalMode.ReviewToBin or RemovalMode.EverythingToBin
            ? Removal : RemovalMode.ReviewToBin,
        DevToolsStalenessDays = DevToolsStalenessDays is 0 or 30 or 90 or 180 or 365 or 730
            ? DevToolsStalenessDays : 180,
        ModOverride = ModOverride is ModVerdictOverride.None or ModVerdictOverride.TreatAsStock or ModVerdictOverride.TreatAsModified
            ? ModOverride : ModVerdictOverride.None,
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
