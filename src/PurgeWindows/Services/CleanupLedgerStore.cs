using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PurgeWindows.Services;

/// <summary>Where a ledger session came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LedgerSource
{
    Manual,
    Scheduled,
    Retry,
    /// <summary>Imported from cleanup history during launch reconciliation.</summary>
    Imported,
}

/// <summary>One cleaned item in a ledger session. Paths are profile-relative when possible.</summary>
public sealed record LedgerItem(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("movedToRecycleBin")] bool MovedToRecycleBin);

/// <summary>
/// One ledger session (one clean), mirroring Purge's ledger schema: id shared with
/// the history entry, trigger, source, moved/removed/reclaimed bytes, safety-skip
/// and failure counts, import flag, and per-item detail with profile-relative paths.
/// </summary>
public sealed record LedgerSession(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("date")] DateTimeOffset Date,
    [property: JsonPropertyName("trigger")] CleanupTrigger Trigger,
    [property: JsonPropertyName("source")] LedgerSource Source,
    [property: JsonPropertyName("bytesMovedToRecycleBin")] long BytesMovedToRecycleBin,
    [property: JsonPropertyName("bytesRemovedDirectly")] long BytesRemovedDirectly,
    [property: JsonPropertyName("bytesReclaimedOnVolume")] long? BytesReclaimedOnVolume,
    [property: JsonPropertyName("skippedForSafetyCount")] int SkippedForSafetyCount,
    [property: JsonPropertyName("failedCount")] int FailedCount,
    [property: JsonPropertyName("importedFromHistory")] bool ImportedFromHistory,
    [property: JsonPropertyName("items")] IReadOnlyList<LedgerItem> Items);

/// <summary>
/// The append-only cleanup ledger (<c>ledger/&lt;year&gt;.jsonl</c>, one JSON line per
/// clean, one file per calendar year), mirroring Purge's <c>CleanupLedgerStore</c>:
/// never trimmed, never rewritten, torn lines skipped on read, appends handle a
/// missing trailing newline from a crashed write by starting on a fresh line.
/// Only cleans that deleted at least one item are recorded; retries are recorded
/// with source <see cref="LedgerSource.Retry"/>.
/// <para/>
/// <c>ledger/baseline.json</c> holds the lifetime total that survives a clear
/// (Purge rule: clearing history also clears the ledger, but the lifetime total
/// counter is kept).
/// </summary>
public sealed class CleanupLedgerStore
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PurgeWindows", "ledger");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly string _directory;
    readonly object _gate = new();

    public CleanupLedgerStore(string? directory = null) => _directory = directory ?? DefaultDirectory;

    sealed record Baseline(
        [property: JsonPropertyName("totalBytesMovedToRecycleBin")] long TotalBytesMovedToRecycleBin,
        [property: JsonPropertyName("totalBytesRemovedDirectly")] long TotalBytesRemovedDirectly,
        [property: JsonPropertyName("firstSeenAt")] DateTimeOffset? FirstSeenAt);

    string YearFile(int year) => Path.Combine(_directory, year + ".jsonl");
    string BaselineFile => Path.Combine(_directory, "baseline.json");

    /// <summary>Appends one session. Sessions that removed nothing are not recorded.</summary>
    public void Append(LedgerSession session)
    {
        if (session.BytesMovedToRecycleBin <= 0 && session.BytesRemovedDirectly <= 0) return;
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var file = YearFile(session.Date.Year);
            var line = JsonSerializer.Serialize(session, JsonOptions);
            // A crashed write may have left the file without a trailing newline;
            // start on a fresh line so the new session never glues onto a torn one.
            bool needsNewline = false;
            if (File.Exists(file))
            {
                try
                {
                    using var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (read.Length > 0)
                    {
                        read.Seek(-1, SeekOrigin.End);
                        needsNewline = read.ReadByte() != (byte)'\n';
                    }
                }
                catch { needsNewline = false; }
            }
            File.AppendAllText(file, (needsNewline ? "\n" : "") + line + "\n");
        }
    }

    /// <summary>Reads one year's sessions, oldest first. Torn lines are skipped, never fatal.</summary>
    public IReadOnlyList<LedgerSession> ReadYear(int year)
    {
        lock (_gate)
        {
            var file = YearFile(year);
            if (!File.Exists(file)) return [];
            var sessions = new List<LedgerSession>();
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var session = JsonSerializer.Deserialize<LedgerSession>(line, JsonOptions);
                    if (session is not null && session.Id != Guid.Empty) sessions.Add(session);
                }
                catch (JsonException)
                {
                    // A torn line from a crash is skipped on read.
                }
            }
            return sessions;
        }
    }

    /// <summary>Reads all years, oldest first.</summary>
    public IReadOnlyList<LedgerSession> ReadAll()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return [];
            return Directory.EnumerateFiles(_directory, "*.jsonl")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .SelectMany(f =>
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    return int.TryParse(name, out var year) ? ReadYear(year) : [];
                })
                .ToList();
        }
    }

    /// <summary>
    /// Lifetime totals: the baseline (kept across clears) plus every recorded
    /// session. Bytes moved to the Recycle Bin and bytes removed directly are
    /// always reported separately, never summed into a "freed" figure.
    /// </summary>
    public (long MovedToRecycleBin, long RemovedDirectly) LifetimeTotals()
    {
        lock (_gate)
        {
            long moved = 0, removed = 0;
            try
            {
                if (File.Exists(BaselineFile))
                {
                    var baseline = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(BaselineFile), JsonOptions);
                    if (baseline is not null)
                    {
                        moved += baseline.TotalBytesMovedToRecycleBin;
                        removed += baseline.TotalBytesRemovedDirectly;
                    }
                }
            }
            catch { }
            foreach (var s in ReadAll())
            {
                moved += s.BytesMovedToRecycleBin;
                removed += s.BytesRemovedDirectly;
            }
            return (moved, removed);
        }
    }

    /// <summary>
    /// Clears the ledger, keeping the lifetime total in <c>baseline.json</c>
    /// (Purge rule: clearing history also clears the ledger, but the lifetime
    /// total counter is kept).
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            var (moved, removed) = LifetimeTotals();
            DateTimeOffset? firstSeen = null;
            try
            {
                if (File.Exists(BaselineFile))
                    firstSeen = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(BaselineFile), JsonOptions)?.FirstSeenAt;
            }
            catch { }
            firstSeen ??= ReadAll().Select(s => (DateTimeOffset?)s.Date).Min();
            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            Directory.CreateDirectory(_directory);
            File.WriteAllText(BaselineFile, JsonSerializer.Serialize(
                new Baseline(moved, removed, firstSeen), JsonOptions));
        }
    }

    /// <summary>
    /// Launch reconciliation (Purge rule): imports history entries the ledger
    /// lacks, matched by id. Returns how many sessions were imported.
    /// </summary>
    public int Reconcile(IReadOnlyList<CleanupHistoryEntry> history)
    {
        lock (_gate)
        {
            var known = new HashSet<Guid>(ReadAll().Select(s => s.Id));
            int imported = 0;
            foreach (var entry in history)
            {
                if (known.Contains(entry.Id)) continue;
                if (entry.BytesMovedToRecycleBin <= 0) continue;
                var session = new LedgerSession(entry.Id, entry.Date, entry.Trigger, LedgerSource.Imported,
                    entry.BytesMovedToRecycleBin, 0, entry.BytesReclaimedOnVolume, 0, entry.FailedItems.Count, true,
                    entry.DeletedItems.Select(i => new LedgerItem(ToHomeRelative(i.Path), i.Size, i.MovedToRecycleBin)).ToList());
                Append(session);
                known.Add(entry.Id);
                imported++;
            }
            return imported;
        }
    }

    /// <summary>
    /// Makes a path profile-relative (<c>~\...</c>) when it lives under the user
    /// profile, so ledger entries stay readable if the profile moves. Absolute
    /// paths outside the profile are kept as-is.
    /// </summary>
    public static string ToHomeRelative(string path)
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length == 0) return path;
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(profile);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return "~";
            var prefix = root.EndsWith('\\') ? root : root + "\\";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return "~\\" + full[prefix.Length..];
            return path;
        }
        catch { return path; }
    }
}
