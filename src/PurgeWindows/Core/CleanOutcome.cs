namespace PurgeWindows.Core;

/// <summary>
/// What a cleaning run amounted to, overall. Ported from Purge's <c>CleanOutcome</c>
/// (5 cases) with the same <c>resolve</c> logic: the outcome is derived from what
/// was removed and what failed, never from what was planned.
/// </summary>
public enum CleanOutcome
{
    /// <summary>Everything attempted was removed.</summary>
    Success,
    /// <summary>Something was removed and something failed.</summary>
    Partial,
    /// <summary>Nothing was removed and something failed.</summary>
    Failed,
    /// <summary>Nothing was removed; every failure was a safety skip.</summary>
    SkippedOnly,
    /// <summary>Nothing was removed and nothing failed (nothing left to clean).</summary>
    NothingLeft,
}

public static class CleanOutcomes
{
    /// <summary>
    /// Resolves the outcome from the run's results, mirroring Purge's
    /// <c>CleanOutcome.resolve</c>: no failures → success when something was removed,
    /// else nothing-left; removed-with-failures → partial; nothing removed with all
    /// failures safety-skipped → skipped-only; otherwise failed.
    /// </summary>
    public static CleanOutcome Resolve(int removedCount, IReadOnlyList<CleanFailure> failedItems)
    {
        if (failedItems.Count == 0)
            return removedCount > 0 ? CleanOutcome.Success : CleanOutcome.NothingLeft;
        if (removedCount > 0)
            return CleanOutcome.Partial;
        if (failedItems.All(f => f.Reason == CleanFailureReason.SafetySkipped))
            return CleanOutcome.SkippedOnly;
        return CleanOutcome.Failed;
    }

    /// <summary>
    /// Only success and partial runs show a reclaimed-space figure. A failed or
    /// skipped-only run must never present a number as if space was freed.
    /// </summary>
    public static bool ShowsFigure(CleanOutcome outcome) =>
        outcome is CleanOutcome.Success or CleanOutcome.Partial;
}
