using System.Text.Json;
using System.Text.Json.Serialization;

namespace PurgeWindows.Services;

/// <summary>What triggered a recorded clean.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CleanupTrigger
{
    Manual,
    Scheduled,
}

/// <summary>Whether a recorded clean was verified after the fact (post-clean health checks are a later wave).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CleanupVerificationState
{
    /// <summary>No verification ran (or its result is not known).</summary>
    Unknown,
    /// <summary>Post-clean checks ran and the system looked healthy.</summary>
    Verified,
    /// <summary>Post-clean checks found something wrong.</summary>
    Broken,
}

/// <summary>One item a clean moved or deleted, as recorded in history.</summary>
public sealed record HistoryDeletedItem(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    /// <summary>True when the item went to the Recycle Bin (restorable); false when deleted directly.</summary>
    [property: JsonPropertyName("movedToRecycleBin")] bool MovedToRecycleBin);

/// <summary>One item a clean skipped, as recorded in history.</summary>
public sealed record HistorySkippedItem(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason,
    /// <summary>Whether the skip is shown to the user (policy skips are; too-new files are not, by default).</summary>
    [property: JsonPropertyName("isUserVisible")] bool IsUserVisible);

/// <summary>One item a clean failed to remove, as recorded in history.</summary>
public sealed record HistoryFailedItem(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    /// <summary>The persisted <see cref="Core.CleanFailureReason"/> raw value (stable; unknown values read as unknown).</summary>
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>Per-category totals for a recorded clean (no per-item detail is invented when the engine did not supply it).</summary>
public sealed record HistoryCategorySummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("freed")] long Freed,
    [property: JsonPropertyName("recycled")] long Recycled,
    [property: JsonPropertyName("files")] int Files);

/// <summary>
/// One cleanup history entry. The shape mirrors Purge's
/// <c>CleanupHistoryEntry</c>: id shared with the ledger session, newest-first
/// storage, <c>bytesReclaimedOnVolume</c> present only when measured AND at/above
/// the noise floor (null = unknown), per-item detail, and — Windows additions —
/// backup linkage, verification state, and restore eligibility, everything the
/// restore UI (later wave) needs.
/// </summary>
public sealed record CleanupHistoryEntry(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("date")] DateTimeOffset Date,
    [property: JsonPropertyName("trigger")] CleanupTrigger Trigger,
    [property: JsonPropertyName("kind")] string Kind,
    /// <summary>Bytes moved to the Recycle Bin. NEVER presented as freed space.</summary>
    [property: JsonPropertyName("bytesMovedToRecycleBin")] long BytesMovedToRecycleBin,
    /// <summary>Bytes measured reclaimed on the volume (≥ noise floor), or null when unknown.</summary>
    [property: JsonPropertyName("bytesReclaimedOnVolume")] long? BytesReclaimedOnVolume,
    [property: JsonPropertyName("deletedItems")] IReadOnlyList<HistoryDeletedItem> DeletedItems,
    [property: JsonPropertyName("skippedItems")] IReadOnlyList<HistorySkippedItem> SkippedItems,
    [property: JsonPropertyName("failedItems")] IReadOnlyList<HistoryFailedItem> FailedItems,
    [property: JsonPropertyName("categories")] IReadOnlyList<HistoryCategorySummary> Categories,
    /// <summary>Backup this clean can be restored from, when one exists (registry wave; null until then).</summary>
    [property: JsonPropertyName("backupId")] string? BackupId,
    [property: JsonPropertyName("verificationState")] CleanupVerificationState VerificationState,
    /// <summary>True when something about this clean can be put back (Recycle Bin items, backups).</summary>
    [property: JsonPropertyName("restoreEligible")] bool RestoreEligible);

/// <summary>
/// The 100-entry cleanup history (<c>cleanup_history.json</c>, newest-first,
/// pretty-printed, atomic writes), mirroring Purge's <c>CleanupHistoryStore</c>.
/// Decoding is resilient: unreadable entries are skipped, unknown kinds are
/// dropped, and a corrupt file reads as empty without deleting anything.
/// </summary>
public sealed class CleanupHistoryStore
{
    public const int MaxEntries = 100;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PurgeWindows", "cleanup_history.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    readonly string _path;
    readonly object _gate = new();

    public CleanupHistoryStore(string? path = null) => _path = path ?? DefaultPath;

    sealed record Document(
        [property: JsonPropertyName("entries")] List<CleanupHistoryEntry>? Entries);

    /// <summary>All entries, newest first. Never throws: unreadable entries are
    /// skipped individually and a corrupt file reads as empty.</summary>
    public IReadOnlyList<CleanupHistoryEntry> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return [];
                using var doc = JsonDocument.Parse(File.ReadAllText(_path));
                var entries = new List<CleanupHistoryEntry>();
                if (!doc.RootElement.TryGetProperty("entries", out var array) ||
                    array.ValueKind != JsonValueKind.Array) return [];
                foreach (var element in array.EnumerateArray())
                {
                    try
                    {
                        var entry = element.Deserialize<CleanupHistoryEntry>(JsonOptions);
                        // Resilient decoding: drop entries that fail validation
                        // instead of failing the whole file. Unknown kinds are
                        // dropped (Purge rule: unknown kinds decode to nil).
                        if (entry is not null && Valid(entry)) entries.Add(entry);
                    }
                    catch (JsonException)
                    {
                        // One unreadable entry never takes down the history.
                    }
                }
                return entries;
            }
            catch
            {
                return [];
            }
        }
    }

    static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal) { "cleanup" };

    static bool Valid(CleanupHistoryEntry e) =>
        e.Id != Guid.Empty && KnownKinds.Contains(e.Kind) &&
        e.BytesMovedToRecycleBin >= 0 &&
        (e.BytesReclaimedOnVolume is null or >= 0);

    /// <summary>Records one clean: newest-first, capped at 100, atomic write.</summary>
    public void Record(CleanupHistoryEntry entry)
    {
        lock (_gate)
        {
            var entries = Load().ToList();
            entries.RemoveAll(e => e.Id == entry.Id);
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries) entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            Write(entries);
        }
    }

    /// <summary>Removes all saved cleanup records. The ledger is cleared separately (see <see cref="CleanupLedgerStore"/>).</summary>
    public void Clear()
    {
        lock (_gate) Write([]);
    }

    void Write(IReadOnlyList<CleanupHistoryEntry> entries)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(dir);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Document(entries.ToList()), JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
