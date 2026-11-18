namespace PurgeWindows.Core;

/// <summary>
/// How safe a catalog entry is to clean, copied from Purge's <c>SafetyLevel</c>
/// (<c>.safe</c> / <c>.medium</c> / <c>.unknown</c>).
/// <para/>
/// There are exactly two cleanable tiers, and everything is visible — there are
/// no modes, no hidden categories, and no mode gates anywhere in this app.
/// <see cref="ReportOnly"/> rows are sizing-only: they are shown, never cleaned.
/// </summary>
public enum SafetyLevel
{
    /// <summary>Safe to Delete. The app rebuilds the data itself; removing it has no
    /// visible consequence. Purge <c>.safe</c>, sort order 0.</summary>
    Safe = 0,

    /// <summary>Not Safe to Delete (Check First). Shown, never hidden, but cleaning
    /// it needs an explicit confirmation (Wave 4 enforces the confirmation; this
    /// wave carries the tier on every entry and enforces the tier rule in the
    /// policy). Purge <c>.medium</c>, sort order 1.</summary>
    CheckFirst = 1,

    /// <summary>Sizing-only. Purge <c>.unknown</c>, sort order 3 — deliberately, no
    /// tier occupies 2, mirroring Purge's sort orders so the most-cautious merge
    /// rule behaves identically.</summary>
    ReportOnly = 3,
}

/// <summary>
/// The evidence the tier rule (§5.0, reinterpreted) requires before anything may
/// be called Safe. Safe iff ALL hold — anything else is Check-First, and
/// ReportOnly rows are read-only.
/// </summary>
public sealed record SafetyEvidence(
    /// <summary>Purge's rule: the path holds nothing but data its app rebuilds by
    /// itself, with no visible consequence. Settings, tools, keys, history,
    /// databases, projects and offline downloads fail this.</summary>
    bool AppRebuildsData,
    /// <summary>Plain file deletion to the Recycle Bin. Permanent removal is never Safe.</summary>
    bool GoesToRecycleBin,
    /// <summary>Per-user scope: nothing belonging to another user, nothing shared.</summary>
    bool PerUserScope,
    /// <summary>No elevation required.</summary>
    bool NoElevation,
    /// <summary>No only-copy or sync risk: nothing that might be the sole copy, and
    /// nothing whose removal would propagate through a sync service.</summary>
    bool NoOnlyCopyOrSyncRisk,
    /// <summary>Not a repair action: clearing it is routine maintenance, not fixing
    /// something broken.</summary>
    bool NotRepairAction);

public static class SafetyLevels
{
    /// <summary>Sort order: Safe 0, CheckFirst 1, ReportOnly 3. Higher is more cautious.</summary>
    public static int SortOrder(SafetyLevel level) => (int)level;

    /// <summary>
    /// The most cautious of two tiers wins (Purge's merge rule: max sort order).
    /// A category holding both Safe and Check-First items is Check-First.
    /// </summary>
    public static SafetyLevel MostCautious(SafetyLevel a, SafetyLevel b) =>
        SortOrder(a) >= SortOrder(b) ? a : b;

    /// <summary>The most cautious tier in a set; Safe when the set is empty.</summary>
    public static SafetyLevel Merge(IEnumerable<SafetyLevel> levels) =>
        levels.Aggregate(SafetyLevel.Safe, MostCautious);

    /// <summary>
    /// True when the tier may appear in scan results at all. All three tiers
    /// surface — ReportOnly as sizing-only rows — because nothing is hidden.
    /// (Purge drops <c>.unknown</c> rows at scan assembly; this port shows them
    /// read-only instead, since a size the user cannot act on is still useful
    /// information about where their space went.)
    /// </summary>
    public static bool CanSurface(SafetyLevel level) => true;

    /// <summary>True when the tier may be cleaned (Safe and Check-First only).</summary>
    public static bool IsCleanable(SafetyLevel level) => level is SafetyLevel.Safe or SafetyLevel.CheckFirst;

    /// <summary>
    /// The shared predicate (Purge's <c>countsAsSafeCleanup</c>): the tier must be
    /// Safe AND every piece of tier-rule evidence must hold. Used by the catalog
    /// audit tests and by the policy; a Safe tier with missing evidence is a bug,
    /// not a judgment call.
    /// </summary>
    public static bool CountsAsSafeCleanup(SafetyLevel level, SafetyEvidence evidence) =>
        level == SafetyLevel.Safe
        && evidence.AppRebuildsData
        && evidence.GoesToRecycleBin
        && evidence.PerUserScope
        && evidence.NoElevation
        && evidence.NoOnlyCopyOrSyncRisk
        && evidence.NotRepairAction;
}
