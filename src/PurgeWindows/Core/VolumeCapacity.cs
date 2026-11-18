using Microsoft.Win32;

namespace PurgeWindows.Core;

/// <summary>
/// What is known about one drive's Recycle Bin: whether it is enabled, how big it is, and
/// how much it already holds. Any field may be unknown (null) when Windows would not say —
/// unknown is a first-class outcome, never a guess, and the engine treats it as "no
/// enforcement" while the no-UI shell flags + return codes remain the guard.
/// <para/>
/// Moved unchanged from the Phase 0 <c>RecycleBinCapacity</c> helper, which this file
/// supersedes (Phase 1): per-volume free-space + bin accounting live here now.
/// </summary>
public sealed record BinCapacity(
    string DriveRoot,
    /// <summary>False when the bin is disabled for this drive ("don't move files to the bin").
    /// Null when Windows would not say.</summary>
    bool? Enabled,
    /// <summary>Bin capacity in bytes. Null when unknown.</summary>
    long? CapacityBytes,
    /// <summary>Bytes currently in the bin for this drive (0 when the query failed).</summary>
    long UsedBytes)
{
    /// <summary>Remaining capacity, when capacity is known. Never negative.</summary>
    public long? RemainingBytes => CapacityBytes is { } cap ? Math.Max(0, cap - UsedBytes) : null;

    public static BinCapacity Unknown(string driveRoot) => new(Normalize(driveRoot), null, null, 0);

    internal static string Normalize(string driveRoot)
    {
        try
        {
            var root = Path.GetPathRoot(driveRoot);
            return string.IsNullOrEmpty(root) ? driveRoot : root.ToUpperInvariant();
        }
        catch { return driveRoot; }
    }
}

/// <summary>Outcome of the pre-flight capacity check, computed purely from planned bytes + capacities.</summary>
public sealed record RecyclePreflight(
    long PlannedBytes,
    long? KnownRemainingBytes,
    bool ExceedsCapacity,
    bool ApproachingCapacity,
    IReadOnlyList<string> UnknownDrives,
    IReadOnlyList<string> DisabledDrives)
{
    /// <summary>Pure evaluation: no shell, no registry, fully unit-testable.</summary>
    public static RecyclePreflight Evaluate(
        IReadOnlyDictionary<string, long> plannedByDrive,
        IReadOnlyList<BinCapacity> capacities,
        double warnAtFraction = 0.8)
    {
        var byDrive = new Dictionary<string, BinCapacity>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in capacities) byDrive.TryAdd(BinCapacity.Normalize(c.DriveRoot), c);
        long planned = 0, knownRemaining = 0;
        bool exceeds = false, approaching = false;
        var unknown = new List<string>();
        var disabled = new List<string>();
        foreach (var (drive, bytes) in plannedByDrive)
        {
            var root = BinCapacity.Normalize(drive);
            planned += Math.Max(0, bytes);
            if (!byDrive.TryGetValue(root, out var cap) || (cap.Enabled is null && cap.CapacityBytes is null))
            {
                unknown.Add(root);
                continue;
            }
            if (cap.Enabled == false)
            {
                disabled.Add(root);
                exceeds = true; // nothing can be recycled on a bin-disabled drive
                continue;
            }
            if (cap.RemainingBytes is not { } remaining) { unknown.Add(root); continue; }
            knownRemaining += remaining;
            if (bytes > remaining) { exceeds = true; approaching = true; }
            else if (bytes >= warnAtFraction * remaining) approaching = true;
        }
        return new RecyclePreflight(planned, unknown.Count == plannedByDrive.Count ? null : knownRemaining,
            exceeds, approaching, unknown, disabled);
    }
}

/// <summary>
/// A per-drive byte budget, snapshotted from the pre-flight check and spent as batches are
/// recycled. Worker-thread only (the cleaning worker is the sole spender). Unknown
/// capacity never blocks: <see cref="TryReserve"/> returns true and the shell's no-UI
/// flags + return codes are the remaining guard.
/// </summary>
public sealed class RecycleBinBudget
{
    readonly Dictionary<string, BinCapacity> _byDrive;
    readonly Dictionary<string, long> _remaining;

    public RecycleBinBudget(IEnumerable<BinCapacity> capacities)
    {
        _byDrive = new Dictionary<string, BinCapacity>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in capacities) _byDrive.TryAdd(BinCapacity.Normalize(c.DriveRoot), c);
        _remaining = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _byDrive.Values)
            if (c.RemainingBytes is { } r) _remaining[BinCapacity.Normalize(c.DriveRoot)] = r;
    }

    /// <summary>Remaining budget for a drive, or null when unknown.</summary>
    public long? RemainingFor(string driveRoot) =>
        _remaining.TryGetValue(BinCapacity.Normalize(driveRoot), out var r) ? r : null;

    /// <summary>True when the bin is known-disabled for the drive of <paramref name="path"/>.</summary>
    public bool IsBinDisabled(string path) =>
        _byDrive.TryGetValue(BinCapacity.Normalize(Path.GetPathRoot(path) ?? path), out var c) && c.Enabled == false;

    /// <summary>
    /// Reserves <paramref name="bytes"/> on the drive holding <paramref name="path"/>.
    /// Returns false — without spending anything — when the bin is disabled or the known
    /// remaining capacity cannot hold the bytes. Unknown capacity always reserves.
    /// </summary>
    public bool TryReserve(string path, long bytes)
    {
        var root = BinCapacity.Normalize(Path.GetPathRoot(path) ?? path);
        if (_byDrive.TryGetValue(root, out var c) && c.Enabled == false) return false;
        if (!_remaining.TryGetValue(root, out var remaining)) return true;
        if (bytes > remaining) return false;
        _remaining[root] = remaining - bytes;
        return true;
    }

    /// <summary>The reason a reservation for <paramref name="path"/> would fail, or null if it would succeed.</summary>
    public SkipReason? RefusalReason(string path, long bytes)
    {
        var root = BinCapacity.Normalize(Path.GetPathRoot(path) ?? path);
        if (_byDrive.TryGetValue(root, out var c) && c.Enabled == false) return SkipReason.BinDisabled;
        if (_remaining.TryGetValue(root, out var remaining) && bytes > remaining) return SkipReason.TooBigForBin;
        return null;
    }
}

/// <summary>Free-space snapshot for one volume. Unknown fields stay null, never guessed.</summary>
public sealed record VolumeFreeSpace(string DriveRoot, long? TotalBytes, long? FreeBytes)
{
    public static VolumeFreeSpace Unknown(string driveRoot) => new(BinCapacity.Normalize(driveRoot), null, null);
}

/// <summary>
/// Per-drive Recycle Bin capacity + free-space reading, and the pre-flight check built
/// on them. This supersedes the Phase 0 <c>RecycleBinCapacity</c> helper (removed):
/// the bin types moved here unchanged, and free-space + settled-gain measurement
/// joined them, so "bytes moved to the Recycle Bin" and "bytes actually reclaimed on
/// the volume" are never confused.
/// <para/>
/// OPEN QUESTION (Windows validation): <c>MaxCapacity</c> in the BitBucket registry
/// key is believed to be megabytes, but the unit is UNVERIFIED. Until it is checked
/// against real bin sizes on Windows, the pre-flight warning stays advisory and the
/// budget enforces only what it could read. A capacity larger than the drive itself
/// is treated as unknown rather than trusted.
/// </summary>
public static class VolumeCapacity
{
    /// <summary>
    /// Noise floor for reclaimed-space claims (Purge's <c>VolumeCapacityReader</c>):
    /// only measured volume deltas at or above 64 MiB count as reclaimed. Smaller
    /// deltas are background filesystem noise, and bytes moved to the Recycle Bin
    /// are NEVER presented as freed.
    /// </summary>
    public const long NoiseFloorBytes = 64L * 1024 * 1024;

    /// <summary>Reads one drive's bin state. Never throws; unknown fields stay null.</summary>
    public static BinCapacity Query(string driveRoot)
    {
        var root = BinCapacity.Normalize(driveRoot);
        long used = 0;
        try { (used, _) = Native.QueryRecycleBin(root); } catch { }
        try
        {
            var (enabled, capacity) = ReadSettings(root);
            return new BinCapacity(root, enabled, capacity, used);
        }
        catch { return new BinCapacity(root, null, null, used); }
    }

    /// <summary>Reads every listed drive's bin state. Never throws.</summary>
    public static IReadOnlyList<BinCapacity> QueryAll(IEnumerable<string> driveRoots)
    {
        var list = new List<BinCapacity>();
        foreach (var root in driveRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { list.Add(Query(root)); } catch { list.Add(BinCapacity.Unknown(root)); }
        }
        return list;
    }

    /// <summary>Reads one drive's free space. Never throws; unknown fields stay null.</summary>
    public static VolumeFreeSpace QueryFreeSpace(string driveRoot)
    {
        var root = BinCapacity.Normalize(driveRoot);
        try
        {
            var di = new DriveInfo(root);
            return di.IsReady ? new VolumeFreeSpace(root, di.TotalSize, di.AvailableFreeSpace) : VolumeFreeSpace.Unknown(root);
        }
        catch { return VolumeFreeSpace.Unknown(root); }
    }

    /// <summary>
    /// Planned recycle bytes per drive for a set of clean jobs: every Delete job's
    /// items go to the Recycle Bin (D-21), split across the drives their paths sit
    /// on. Pure and unit-testable; the async pre-flight wrapper lives in MainVM.
    /// </summary>
    public static Dictionary<string, long> Plan(IReadOnlyList<CleanJob> jobs)
    {
        var planned = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (job.Category.Action != CleanAction.Delete) continue;
            foreach (var item in job.Items)
            {
                var drives = item.Paths
                    .Select(p => Path.GetPathRoot(p))
                    .Where(r => !string.IsNullOrEmpty(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (drives.Count == 0) continue;
                long perDrive = item.Size / drives.Count;
                for (int i = 0; i < drives.Count; i++)
                {
                    var root = drives[i]!;
                    long share = perDrive + (i == 0 ? item.Size % drives.Count : 0);
                    planned[root] = planned.TryGetValue(root, out var n) ? n + share : share;
                }
            }
        }
        return planned;
    }

    /// <summary>
    /// The reclaimed-space claim for a measured free-space delta: the delta itself
    /// when it reaches the noise floor, null (unknown) otherwise. Negative deltas
    /// (the volume filled up during the clean) also read as unknown, never as a
    /// negative claim.
    /// </summary>
    public static long? ReclaimedBytes(long freeBefore, long freeAfter)
    {
        long delta = freeAfter - freeBefore;
        return delta >= NoiseFloorBytes ? delta : null;
    }

    /// <summary>
    /// Polls a volume's free space until it settles, then reports the reclaimed
    /// bytes via <see cref="ReclaimedBytes"/> (Purge's settled-gain polling: 10
    /// attempts, 1 second apart). The reader is injected so tests can fake the
    /// volume; production passes <see cref="QueryFreeSpace"/>. Returns null when
    /// the gain never reaches the noise floor, the volume goes unreadable, or
    /// cancellation arrives — unknown is always an honest answer.
    /// </summary>
    public static async Task<long?> MeasureSettledGainAsync(
        string driveRoot, long freeBefore, Func<string, long?> readFree,
        int attempts = 10, TimeSpan? interval = null, CancellationToken ct = default)
    {
        long? last = null;
        for (int i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { last = readFree(driveRoot); } catch { return null; }
            if (last is null) return null;
            if (ReclaimedBytes(freeBefore, last.Value) is { } gain) return gain;
            if (i + 1 < attempts)
            {
                try { await Task.Delay(interval ?? TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { return null; }
            }
        }
        return null;
    }

    /// <summary>
    /// Per-volume bin settings live under HKCU\...\Explorer\BitBucket\Volume\{volume-guid},
    /// with MaxCapacity (believed to be megabytes — see the class doc) and NukeOnDelete
    /// (1 = the bin is disabled for that volume). The volume GUID is resolved from the
    /// drive root; anything unreadable yields unknown. A capacity larger than the
    /// drive itself is treated as unknown rather than trusted.
    /// </summary>
    static (bool? Enabled, long? CapacityBytes) ReadSettings(string driveRoot)
    {
        var volumeName = Native.TryGetVolumeName(driveRoot);
        if (volumeName is null) return (null, null);
        var guid = ExtractGuid(volumeName);
        if (guid is null) return (null, null);
        try
        {
            using var volumes = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume");
            if (volumes is null) return (null, null);
            string? match = null;
            foreach (var name in volumes.GetSubKeyNames())
            {
                if (string.Equals(name, guid, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, guid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase))
                { match = name; break; }
            }
            if (match is null) return (null, null);
            using var key = volumes.OpenSubKey(match);
            if (key is null) return (null, null);
            bool? enabled = key.GetValue("NukeOnDelete") is int nuke ? nuke == 0 : null;
            long? capacity = null;
            if (key.GetValue("MaxCapacity") is int mb && mb > 0)
            {
                long bytes = (long)mb * 1024 * 1024;
                capacity = bytes <= DriveTotalBytes(driveRoot) ? bytes : null;
            }
            return (enabled, capacity);
        }
        catch { return (null, null); }
    }

    static string? ExtractGuid(string volumeName)
    {
        var start = volumeName.IndexOf('{');
        var end = volumeName.IndexOf('}');
        return start >= 0 && end > start ? volumeName[start..(end + 1)] : null;
    }

    static long DriveTotalBytes(string driveRoot)
    {
        try
        {
            var di = new DriveInfo(driveRoot);
            return di.IsReady ? di.TotalSize : long.MaxValue;
        }
        catch { return long.MaxValue; }
    }
}
