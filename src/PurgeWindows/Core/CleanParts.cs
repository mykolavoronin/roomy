namespace PurgeWindows.Core;

/// <summary>Where one part (one category being cleaned) is in its life cycle.</summary>
public enum CleanPartState
{
    /// <summary>Not started yet. The part is listed so the user can see what is coming.</summary>
    Queued,
    /// <summary>Being cleaned right now.</summary>
    Cleaning,
    /// <summary>Finished. Some bytes may still have been skipped (in use); see <see cref="CleanPartProgress.BytesSkipped"/>.</summary>
    Done,
    /// <summary>Something went wrong with this part. The rest of the cleanup still ran.</summary>
    Failed,
    /// <summary>The user stopped the cleanup before or during this part. Nothing more was attempted.</summary>
    Cancelled,
}

/// <summary>Live state of one part. Plain data: no UI, no threading, safe to unit-test.</summary>
public sealed class CleanPartProgress
{
    public required string CategoryId { get; init; }
    public required string Title { get; init; }
    /// <summary>Plain-language explanation of what this part is, shown while the part waits.</summary>
    public string Description { get; init; } = "";
    /// <summary>The category's glyph, so part rows look like the category rows they came from.</summary>
    public string Glyph { get; init; } = "";
    /// <summary>Bytes the scan found for this part. The weight this part carries in overall progress.</summary>
    public long ExpectedBytes { get; init; }
    public CleanPartState State { get; set; } = CleanPartState.Queued;
    /// <summary>How far through this part the cleanup is, 0..1. Reaches 1 when the part finishes.
    /// A failed part keeps its partial fraction here (the row shows how far it got) while
    /// counting as done for overall progress — see <see cref="CleanProgressModel"/>.</summary>
    public double Fraction { get; set; }
    /// <summary>The folder or file being processed right now. Empty when the part is not active.</summary>
    public string CurrentPath { get; set; } = "";
    /// <summary>The item (folder) within the part being processed right now.</summary>
    public string CurrentItem { get; set; } = "";
    public int FilesRemoved { get; set; }
    /// <summary>Bytes deleted for good in this part.</summary>
    public long BytesFreed { get; set; }
    /// <summary>Bytes moved to the Recycle Bin in this part. Not counted in <see cref="BytesFreed"/>.</summary>
    public long BytesRecycled { get; set; }
    /// <summary>Bytes that could not be removed (in use, protected, or vanished) in finished items.</summary>
    public long BytesSkipped { get; set; }
    /// <summary>Everything this part removed, whether deleted or recycled.</summary>
    public long BytesDone => BytesFreed + BytesRecycled;
}

/// <summary>
/// The state machine behind the per-part cleaning list: which part is active, how far each
/// part is, and the totals. Pure logic — the view model mirrors it into observable rows.
/// All updates are idempotent and monotonic, so duplicate or slightly out-of-order progress
/// reports from the worker thread can never move a part backwards or resurrect a finished one.
/// <para/>
/// Failed-part fraction rule (defined once, used everywhere): a failed part counts as DONE
/// for overall progress — the run moved past it — and reports its PARTIAL fraction for its
/// own row. <see cref="Cleaner"/> applies the same rule when it advances its byte counter
/// past a failed part. A cancelled part keeps its partial contribution in both places,
/// because the run stopped instead of moving past it.
/// </summary>
public sealed class CleanProgressModel
{
    readonly List<CleanPartProgress> _parts;
    readonly Dictionary<string, CleanPartProgress> _byId;

    public CleanProgressModel(IEnumerable<(string CategoryId, string Title, string Description, string Glyph, long ExpectedBytes)> parts)
    {
        _parts = parts.Select(p => new CleanPartProgress
        {
            CategoryId = p.CategoryId,
            Title = p.Title,
            Description = p.Description,
            Glyph = p.Glyph,
            ExpectedBytes = Math.Max(0, p.ExpectedBytes),
        }).ToList();
        _byId = _parts.ToDictionary(p => p.CategoryId, StringComparer.Ordinal);
    }

    public IReadOnlyList<CleanPartProgress> Parts => _parts;
    public int PartCount => _parts.Count;
    public int PartsDone => _parts.Count(p => p.State == CleanPartState.Done);
    public CleanPartProgress? Find(string categoryId) =>
        categoryId.Length == 0 ? null : _byId.GetValueOrDefault(categoryId);

    /// <summary>Queued -> Cleaning. A no-op for parts that already started or finished.</summary>
    public void StartPart(string categoryId)
    {
        if (Find(categoryId) is { State: CleanPartState.Queued } part)
            part.State = CleanPartState.Cleaning;
    }

    /// <summary>
    /// Live update for the active part. Ignored unless the part is Cleaning, and the fraction
    /// only ever moves forward, so a stale report can never rewind the bar.
    /// </summary>
    public void UpdatePart(string categoryId, double fraction, string currentPath, string currentItem,
        int filesRemoved, long bytesFreed, long bytesRecycled)
    {
        if (Find(categoryId) is not { State: CleanPartState.Cleaning } part) return;
        part.Fraction = Math.Clamp(Math.Max(part.Fraction, fraction), 0, 1);
        if (currentPath.Length > 0) part.CurrentPath = currentPath;
        if (currentItem.Length > 0) part.CurrentItem = currentItem;
        part.FilesRemoved = Math.Max(part.FilesRemoved, filesRemoved);
        part.BytesFreed = Math.Max(part.BytesFreed, bytesFreed);
        part.BytesRecycled = Math.Max(part.BytesRecycled, bytesRecycled);
    }

    /// <summary>Cleaning -> Done, with the part's final totals. The path clears: nothing is active.</summary>
    public void FinishPart(string categoryId, int filesRemoved, long bytesFreed, long bytesRecycled, long bytesSkipped)
    {
        if (Find(categoryId) is not { } part) return;
        if (part.State is CleanPartState.Done or CleanPartState.Failed or CleanPartState.Cancelled) return;
        part.State = CleanPartState.Done;
        part.Fraction = 1;
        part.CurrentPath = "";
        part.CurrentItem = "";
        part.FilesRemoved = Math.Max(part.FilesRemoved, filesRemoved);
        part.BytesFreed = Math.Max(part.BytesFreed, bytesFreed);
        part.BytesRecycled = Math.Max(part.BytesRecycled, bytesRecycled);
        part.BytesSkipped = Math.Max(part.BytesSkipped, bytesSkipped);
    }

    /// <summary>Cleaning -> Failed, keeping whatever the part managed before it failed.
    /// The partial fraction is kept for the row; overall progress counts the part as done.</summary>
    public void FailPart(string categoryId, int filesRemoved, long bytesFreed, long bytesRecycled, long bytesSkipped)
    {
        if (Find(categoryId) is not { } part) return;
        if (part.State is CleanPartState.Done or CleanPartState.Failed or CleanPartState.Cancelled) return;
        part.State = CleanPartState.Failed;
        part.CurrentPath = "";
        part.CurrentItem = "";
        part.FilesRemoved = Math.Max(part.FilesRemoved, filesRemoved);
        part.BytesFreed = Math.Max(part.BytesFreed, bytesFreed);
        part.BytesRecycled = Math.Max(part.BytesRecycled, bytesRecycled);
        part.BytesSkipped = Math.Max(part.BytesSkipped, bytesSkipped);
    }

    /// <summary>
    /// Stops everything that has not finished: the active part keeps its partial totals and
    /// becomes Cancelled, and every queued part becomes Cancelled without being attempted.
    /// Finished parts (Done or Failed) are untouched.
    /// </summary>
    public void CancelRemaining()
    {
        foreach (var part in _parts)
        {
            if (part.State is CleanPartState.Queued or CleanPartState.Cleaning)
            {
                part.State = CleanPartState.Cancelled;
                part.CurrentPath = "";
                part.CurrentItem = "";
            }
        }
    }

    /// <summary>
    /// Overall progress, 0..1, driven by completed parts. Parts are weighted by the bytes the
    /// scan found, so a 6 GB part moves the bar much further than a 6 MB one; the active part
    /// contributes its own fraction. Failed parts count as done (the run moved past them);
    /// cancelled parts keep their partial contribution. When no part has a known size
    /// (all zero), every part weighs the same and the bar is simply parts done / total parts.
    /// Never indeterminate: with no parts at all the result is 0.
    /// </summary>
    public double OverallFraction
    {
        get
        {
            if (_parts.Count == 0) return 0;
            long total = _parts.Sum(p => p.ExpectedBytes);
            if (total > 0)
                return Math.Clamp(_parts.Sum(p => p.ExpectedBytes * EffectiveFraction(p)) / (double)total, 0, 1);
            return Math.Clamp(_parts.Sum(EffectiveFraction) / _parts.Count, 0, 1);
        }
    }

    static double EffectiveFraction(CleanPartProgress p) => p.State switch
    {
        CleanPartState.Done => 1,
        CleanPartState.Failed => 1,
        CleanPartState.Queued => 0,
        _ => Math.Clamp(p.Fraction, 0, 1),
    };

    /// <summary>Totals across all parts. Only what actually happened is counted.</summary>
    public (int Files, long Freed, long Recycled, long Skipped) Totals => (
        _parts.Sum(p => p.FilesRemoved),
        _parts.Sum(p => p.BytesFreed),
        _parts.Sum(p => p.BytesRecycled),
        _parts.Sum(p => p.BytesSkipped));
}
