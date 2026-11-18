using System.Diagnostics;

namespace PurgeWindows.Core;

/// <summary>
/// Why a file was skipped (never attempted) or a batch failed, from a fixed enumeration.
/// Every attempted file is counted exactly once as removed, skipped, or failed, and the
/// reason is recorded — never discarded. This is the §3.5 failure-accounting vocabulary;
/// later waves map these reasons to user-facing copy, they are never shown raw.
/// </summary>
public enum SkipReason
{
    /// <summary>The file is in use (locked) and was skipped, never forced.</summary>
    InUse,
    /// <summary>The file is newer than the item's minimum age.</summary>
    TooNew,
    /// <summary>A protected location refused the operation.</summary>
    Protected,
    /// <summary>The deletion safety policy refused the path (never-delete or not
    /// allowlisted). The clean log carries the policy step that decided it.</summary>
    PolicyRefused,
    /// <summary>The path goes through a junction, symlink, or mount point.</summary>
    LinkRejected,
    /// <summary>The file (or its batch) does not fit in the Recycle Bin's remaining capacity.</summary>
    TooBigForBin,
    /// <summary>The Recycle Bin is disabled for the file's drive.</summary>
    BinDisabled,
    /// <summary>The path is too long for the shell or the trusted-path check.</summary>
    PathTooLong,
    /// <summary>The shell call failed; see the recorded return code.</summary>
    ShellError,
    /// <summary>Anything else. The clean log carries the detail.</summary>
    Other,
}

/// <summary>What the cleaning worker is doing right now. Heartbeats always name a phase.</summary>
public enum CleanPhase
{
    /// <summary>Walking a tree to find files. Streaming; never a silent whole-tree pass.</summary>
    Enumerating,
    /// <summary>Checking each candidate really lives under the checked root.</summary>
    Validating,
    /// <summary>Deleting files directly.</summary>
    Deleting,
    /// <summary>Moving files to the Recycle Bin through the shell.</summary>
    Recycling,
    /// <summary>One shell call is outstanding. This single call cannot be cancelled; the
    /// watchdog observes it from another thread and the UI must say so honestly.</summary>
    ShellCall,
    /// <summary>Removing now-empty subfolders, deepest first.</summary>
    TidyingUp,
    /// <summary>Running a dev tool's own clean command (see <see cref="OwnerTools"/>).</summary>
    OwnerTool,
    /// <summary>Wrapping up a part or the run.</summary>
    Finishing,
}

/// <summary>The sequenced, timestamped events a cleaning run emits (§3.5 contract).</summary>
public enum CleanEventKind
{
    RunStarted,
    PartStarted,
    /// <summary>Liveness only: the worker is alive in a phase, at a path. Never claims progress.</summary>
    Heartbeat,
    /// <summary>Measured advancement: attempted/removed counts and bytes.</summary>
    FileProgress,
    PartFinished,
    PartFailed,
    RunFinished,
    RunCancelled,
    /// <summary>The tree changed mid-run and a total was re-baselined. Fractions never rewind silently.</summary>
    Rebaselined,
}

/// <summary>One part (one category) as announced by <see cref="CleanEventKind.RunStarted"/>.</summary>
public sealed record CleanPartInfo(string CategoryId, string Title, long ExpectedBytes);

/// <summary>
/// One event in a cleaning run. All events carry a monotonic sequence number and both a
/// wall-clock timestamp and a monotonic elapsed time, so stall detection and ETA work can
/// be built on them without guessing. Fractions are measured only and non-decreasing
/// within a run; counters are non-decreasing. The ETA estimator (later wave) reads
/// <see cref="TotalBytes"/>, <see cref="BytesAttemptedSoFar"/>, per-part totals/attempted,
/// and <see cref="Elapsed"/> from these events and performs no prediction itself here.
/// </summary>
public sealed record CleanEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    TimeSpan Elapsed,
    CleanEventKind Kind,
    string CategoryId,
    CleanPhase Phase,
    string CurrentPath,
    /// <summary>Planned total bytes over all executable parts (scan estimates).</summary>
    long TotalBytes,
    /// <summary>Bytes attempted so far across the whole run (removed, skipped, or failed).</summary>
    long BytesAttemptedSoFar,
    /// <summary>Bytes actually removed so far (deleted + recycled).</summary>
    long BytesRemovedSoFar,
    int FilesAttempted,
    int FilesRemoved,
    /// <summary>Planned bytes for this part (scan estimate), 0 when no part is active.</summary>
    long PartTotalBytes,
    /// <summary>Bytes attempted so far in this part.</summary>
    long PartBytesAttempted,
    /// <summary>Overall fraction, 0..1, size-weighted, including in-flight work. The single definition.</summary>
    double Fraction,
    /// <summary>This part's fraction, 0..1. A failed part reports its partial fraction here
    /// while counting as done for <see cref="Fraction"/> — see <see cref="CleanProgressModel"/>.</summary>
    double PartFraction,
    /// <summary>Per-part expected sizes, present only on <see cref="CleanEventKind.RunStarted"/>.</summary>
    IReadOnlyList<CleanPartInfo>? Parts = null,
    /// <summary>Skip/failure reason, when the event is about one.</summary>
    SkipReason? Reason = null,
    /// <summary>Shell return code, when the event reports a shell outcome (C7: never discarded).</summary>
    int? ShellReturnCode = null,
    /// <summary>Whether the shell reported the operation aborted.</summary>
    bool? ShellAborted = null,
    /// <summary>Free-form detail for the clean log (batch size, tool name, error text).</summary>
    string Detail = "");

/// <summary>
/// Options for a cleaning run. Everything here is optional: <see cref="Cleaner.Run"/>
/// behaves safely with <c>null</c> (no owner window, unknown bin capacity, owner tools on,
/// watchdog on). Tests construct options directly to inject budgets and event sinks.
/// </summary>
public sealed class CleanOptions
{
    /// <summary>Owner window for shell calls. Zero means "resolve the main window handle".</summary>
    public IntPtr OwnerWindowHandle { get; init; }

    /// <summary>Sequenced event sink (ETA/stalled UI in later waves). Delivery never throws
    /// into the engine and never blocks it: reports go through a never-throwing forwarder.</summary>
    public IProgress<CleanEvent>? Events { get; init; }

    /// <summary>Per-drive Recycle Bin budget from the pre-flight check. Null means unknown:
    /// no batch is skipped for capacity, and the no-UI shell flags + return codes are the guard.</summary>
    public RecycleBinBudget? Budget { get; init; }

    /// <summary>Run dev tools' own clean commands where available (Go only, today) before
    /// hand-deletion. Owner commands delete for good, so they are a consented
    /// permanent action: they run only for Check-First items when
    /// <see cref="OwnerToolConsent"/> is set (Wave 4 sets it from the confirmation
    /// dialog). Without consent, only the non-destructive pre-step runs. Default on.</summary>
    public bool UseOwnerTools { get; init; } = true;

    /// <summary>
    /// Explicit consent to run owner-tool clean commands (permanent deletion) for
    /// Check-First items in this run. Default false: without it, owner commands
    /// never run and every file goes to the Recycle Bin. It is honoured ONLY
    /// alongside a <see cref="CheckFirstConfirmation"/> that covers the category
    /// and itself consents to owner tools — setting this flag without such a
    /// confirmation makes <see cref="ValidateRun"/> refuse the run. The
    /// confirmation dialog path (MainVM) is the only caller that sets it, and
    /// only together with the confirmation it just obtained. Consent never
    /// applies to Safe items — permanent removal is never Safe.
    /// </summary>
    public bool OwnerToolConsent { get; init; }

    /// <summary>
    /// Proof that the user explicitly confirmed the Check-First categories in
    /// this run, obtained from the confirmation dialog. Null means "no
    /// Check-First confirmation": the engine then refuses every Check-First job
    /// (see <see cref="ValidateRun"/>). Never defaulted, never set from
    /// scan/selection code — only the confirmation dialog path constructs one,
    /// via <see cref="CheckFirstConfirmation.FromConfirmationDialog"/>.
    /// </summary>
    public CheckFirstConfirmation? CheckFirstConfirmation { get; init; }

    /// <summary>
    /// Safe-tier-only rule: when true, the run may contain Safe jobs only and
    /// any non-Safe executable job refuses the run. This is the rule for
    /// confirmation-free flows — Purge's "Clean Safe Items" (§9.3: no sheet,
    /// the selection rule IS the confirmation) and scheduled cleans, which are
    /// Safe-tier-only always. The UI sets it on the no-dialog path; the engine
    /// enforces it in <see cref="ValidateRun"/> with the UI bypassed.
    /// </summary>
    public bool SafeTierOnly { get; init; }

    /// <summary>
    /// Enforces the tier rules before any cleaning starts. Throws
    /// <see cref="InvalidOperationException"/> — before the first event is
    /// emitted and before anything is touched — when: a Safe-tier-only run
    /// contains a non-Safe job; a Check-First job is not covered by the run's
    /// confirmation; or owner-tool consent is set without a confirmation that
    /// consents to owner tools. Safe jobs need no confirmation. Only executable
    /// jobs are checked: ReportOnly/Steam/Settings jobs never clean and need
    /// no confirmation.
    /// </summary>
    internal static void ValidateRun(IReadOnlyList<CleanJob> executable, CleanOptions? options)
    {
        var confirmation = options?.CheckFirstConfirmation;
        if (options?.SafeTierOnly == true)
        {
            var nonSafe = executable
                .Where(j => j.Category.Tier != SafetyLevel.Safe)
                .Select(j => j.Category.Id)
                .ToList();
            if (nonSafe.Count > 0)
                throw new InvalidOperationException(
                    "Safe-only clean refused: non-Safe categories present: " +
                    string.Join(", ", nonSafe) + ".");
        }
        var uncovered = executable
            .Where(j => j.Category.Tier == SafetyLevel.CheckFirst &&
                        confirmation?.Covers(j.Category.Id) != true)
            .Select(j => j.Category.Id)
            .ToList();
        if (uncovered.Count > 0)
            throw new InvalidOperationException(
                "Check-First categories refused without an explicit confirmation: " +
                string.Join(", ", uncovered) + ".");
        if (options?.OwnerToolConsent == true && confirmation?.OwnerToolsConsented != true)
            throw new InvalidOperationException(
                "Owner-tool consent refused without a Check-First confirmation " +
                "that consents to owner tools.");
    }

    /// <summary>
    /// Whether an owner tool's clean commands may run for <paramref name="job"/>:
    /// the run carries owner-tool consent AND a confirmation covering this
    /// category that itself consents to owner tools AND the category is
    /// Check-First. Safe items never qualify, whatever the flags say.
    /// </summary>
    internal static bool OwnerToolAllowed(CleanJob job, CleanOptions? options) =>
        options?.OwnerToolConsent == true
        && options.CheckFirstConfirmation is { OwnerToolsConsented: true } confirmation
        && confirmation.Covers(job.Category.Id)
        && job.Category.Tier == SafetyLevel.CheckFirst;

    /// <summary>
    /// The deletion safety policy gating this run. Null means the default policy:
    /// every item path is evaluated before cleaning, and every file is re-checked
    /// at delete time. Tests pass a policy built for their temp folder.
    /// </summary>
    public DeletionSafetyPolicy? Policy { get; init; }

    /// <summary>How long one owner-tool command may run before it is killed. Default 10 minutes:
    /// <c>go clean -modcache</c> on a huge module cache is legitimately slow.</summary>
    public TimeSpan OwnerToolTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Observe worker heartbeats and record silence. Default on. The watchdog never
    /// kills the worker; it only records (log + diagnostic state) for later surfacing.</summary>
    public bool EnableWatchdog { get; init; } = true;

    /// <summary>Silence that counts as "worker may be stuck". Default 5 seconds.</summary>
    public TimeSpan WatchdogWarnAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Silence that counts as "worker stuck". Default 15 seconds.</summary>
    public TimeSpan WatchdogStuckAfter { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Proof that the user explicitly confirmed the Check-First categories in one
/// cleaning run. The ONLY sanctioned construction path is
/// <see cref="FromConfirmationDialog"/>, called from the confirmation dialog
/// flow after the user acknowledged the Check-First section; it is never
/// defaulted and never constructed from scan/selection code. The engine
/// (<see cref="CleanOptions.ValidateRun"/>) refuses every Check-First job
/// whose category id is not covered. The record is single-run: it is created
/// for one dialog answer and carried on that run's <see cref="CleanOptions"/>.
/// </summary>
public sealed class CheckFirstConfirmation
{
    readonly HashSet<string> _ids;

    CheckFirstConfirmation(IEnumerable<string> categoryIds, bool ownerToolsConsented, DateTimeOffset confirmedAt)
    {
        CategoryIds = categoryIds.ToList();
        _ids = new HashSet<string>(CategoryIds, StringComparer.Ordinal);
        OwnerToolsConsented = ownerToolsConsented;
        ConfirmedAt = confirmedAt;
    }

    /// <summary>Creates a confirmation for the categories the dialog just confirmed.</summary>
    public static CheckFirstConfirmation FromConfirmationDialog(
        IEnumerable<string> categoryIds, bool ownerToolsConsented = false) =>
        new(categoryIds, ownerToolsConsented, DateTimeOffset.Now);

    /// <summary>Category ids this confirmation covers, in dialog order.</summary>
    public IReadOnlyList<string> CategoryIds { get; }

    /// <summary>When the dialog answer was given.</summary>
    public DateTimeOffset ConfirmedAt { get; }

    /// <summary>
    /// Whether the confirmation also consents to owner-tool clean commands
    /// (permanent deletion) for the covered Check-First categories. Set only
    /// from the dialog path, together with the acknowledgement.
    /// </summary>
    public bool OwnerToolsConsented { get; }

    /// <summary>True when this confirmation covers <paramref name="categoryId"/>.</summary>
    public bool Covers(string categoryId) => _ids.Contains(categoryId);
}

/// <summary>One line of a tiered confirmation plan: plain data, no UI.</summary>
public sealed record ConfirmPlanLine(string CategoryId, string Title, string SizeText, SafetyLevel Tier);

/// <summary>
/// The pure confirmation-flow plan for one cleaning run: which jobs the dialog
/// shows, split into sections, whether the Check-First acknowledgement is
/// required, and whether confirming is allowed. The view model renders the
/// sections and asks this plan for the engine confirmation — the dialog can
/// never produce a confirmation the plan would not allow. Section order is
/// Check-First first, then Safe (Purge's confirm-sheet grouping, EXTRACTION
/// §9.2: Check First leads, each section headed by the safety name); within a
/// section, largest first.
/// </summary>
public sealed class TieredConfirmPlan
{
    public TieredConfirmPlan(
        IReadOnlyList<ConfirmPlanLine> checkFirstLines, IReadOnlyList<ConfirmPlanLine> safeLines)
    {
        CheckFirstLines = checkFirstLines;
        SafeLines = safeLines;
    }

    /// <summary>Check-First lines, shown first. Empty when the run has none.</summary>
    public IReadOnlyList<ConfirmPlanLine> CheckFirstLines { get; }

    /// <summary>Safe lines, shown after the Check-First section.</summary>
    public IReadOnlyList<ConfirmPlanLine> SafeLines { get; }

    /// <summary>Lines across both sections.</summary>
    public int TotalLines => CheckFirstLines.Count + SafeLines.Count;

    /// <summary>True when the dialog must require the Check-First acknowledgement.</summary>
    public bool RequiresAcknowledgement => CheckFirstLines.Count > 0;

    /// <summary>
    /// Whether confirming is allowed: something must be selected (Purge §9.1:
    /// no sheet offers "Move 0 to Trash"), and the Check-First acknowledgement
    /// must be given when required.
    /// </summary>
    public bool CanConfirm(bool acknowledged) =>
        TotalLines > 0 && (!RequiresAcknowledgement || acknowledged);

    /// <summary>
    /// Builds the engine confirmation for this plan. Returns null when the run
    /// has no Check-First lines (Safe runs need no confirmation); throws when
    /// the acknowledgement is required but was not given. The confirmation
    /// covers exactly the plan's Check-First categories and consents to owner
    /// tools for them — the acknowledgement is the consent.
    /// </summary>
    public CheckFirstConfirmation? BuildConfirmation(bool acknowledged)
    {
        if (CheckFirstLines.Count == 0) return null;
        if (!acknowledged)
            throw new InvalidOperationException(
                "Check-First confirmation requires the acknowledgement.");
        return CheckFirstConfirmation.FromConfirmationDialog(
            CheckFirstLines.Select(l => l.CategoryId), ownerToolsConsented: true);
    }

    /// <summary>
    /// Builds a plan from executable (Delete) jobs with selected items.
    /// Non-cleaning jobs never appear: they are not confirmed because they are
    /// never cleaned.
    /// </summary>
    public static TieredConfirmPlan FromJobs(IEnumerable<CleanJob> jobs)
    {
        var sized = jobs
            .Where(j => j.Category.Action == CleanAction.Delete && j.Items.Count > 0)
            .Select(j => (Line: new ConfirmPlanLine(j.Category.Id, j.Category.Title,
                Format.Bytes(j.Items.Sum(i => i.Size)), j.Category.Tier),
                Size: j.Items.Sum(i => i.Size)))
            .OrderByDescending(x => x.Size)
            .Select(x => x.Line)
            .ToList();
        // Anything not Safe needs the acknowledgement: the Check-First section
        // is the cautious home for any tier that is not proven Safe.
        return new TieredConfirmPlan(
            sized.Where(l => l.Tier != SafetyLevel.Safe).ToList(),
            sized.Where(l => l.Tier == SafetyLevel.Safe).ToList());
    }
}

/// <summary>
/// Which operation a progress event belongs to (§4.6 common fields). Every
/// event shape in the progress contract names its operation from this one
/// enumeration, so later waves (registry, orphan, program detection, restore,
/// health, environment) share the vocabulary instead of inventing their own.
/// </summary>
public enum ProgressOperation
{
    Clean,
    Restore,
    RegistryScan,
    RegistryClean,
    Scan,
    SpaceMeasure,
    OrphanScan,
    ProgramDetect,
    HealthCheck,
    EnvProbe,
}

/// <summary>
/// One space-map measuring snapshot (§4.6 <c>MeasureProgressEvent</c>): the
/// common fields (sequence, engine timestamp, elapsed, operation, phase,
/// heartbeat) plus files/bytes counted and the folder being walked.
/// <see cref="CurrentPath"/> is empty until the scanner wave exposes the
/// folder being walked (ambiguity A7): empty means unknown, never guessed.
/// Today the view model synthesizes these snapshots from the scanner's polled
/// counters; the scanner wave replaces the source without changing the shape.
/// </summary>
public sealed record MeasureProgressEvent(
    long Sequence,
    DateTimeOffset TimestampUtc,
    TimeSpan Elapsed,
    ProgressOperation Operation,
    string Phase,
    bool Heartbeat,
    long FilesCounted,
    long BytesCounted,
    string CurrentPath);

/// <summary>
/// Sequences and timestamps clean events, forwards them to listeners without ever throwing
/// or blocking the engine, writes the selected ones to the bounded clean log, and pulses
/// the watchdog. One sink per run, used only from the worker thread.
/// </summary>
internal sealed class CleanEventSink : IDisposable
{
    readonly IProgress<CleanEvent>? _events;
    readonly CleanWatchdog? _watchdog;
    readonly Stopwatch _watch = Stopwatch.StartNew();
    readonly bool _logEvents;
    long _sequence;
    long _lastLoggedProgressMs = -10_000;

    public CleanEventSink(CleanOptions? options, CleanWatchdog? watchdog, bool logEvents = true)
    {
        _events = options?.Events;
        _watchdog = watchdog;
        _logEvents = logEvents;
    }

    public TimeSpan Elapsed => _watch.Elapsed;

    /// <summary>Emits one event. Never throws, never blocks: a broken listener cannot stall cleaning.</summary>
    public void Emit(CleanEventKind kind, string categoryId, CleanPhase phase, string currentPath,
        long totalBytes, long bytesAttempted, long bytesRemoved, int filesAttempted, int filesRemoved,
        long partTotalBytes, long partBytesAttempted, double fraction, double partFraction,
        IReadOnlyList<CleanPartInfo>? parts = null, SkipReason? reason = null,
        int? shellReturnCode = null, bool? shellAborted = null, string detail = "")
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var ev = new CleanEvent(sequence, DateTimeOffset.Now, _watch.Elapsed, kind, categoryId, phase,
            currentPath, totalBytes, bytesAttempted, bytesRemoved, filesAttempted, filesRemoved,
            partTotalBytes, partBytesAttempted, Math.Clamp(fraction, 0, 1), Math.Clamp(partFraction, 0, 1),
            parts, reason, shellReturnCode, shellAborted, detail);
        try { _events?.Report(ev); } catch { /* a listener must never break the engine */ }
        _watchdog?.Pulse(phase, currentPath);
        if (_logEvents) Log(ev);
    }

    /// <summary>Records liveness without a full event (used between FileOpProgress reports).</summary>
    public void Pulse(CleanPhase phase, string currentPath) => _watchdog?.Pulse(phase, currentPath);

    void Log(CleanEvent ev)
    {
        // FileProgress is high-volume: log it at most every 2 seconds so a freeze-and-kill
        // still leaves a last-known phase no more than ~2s stale, without flooding the log.
        // Boundaries, failures, cancellations, and re-baselines always log immediately.
        if (ev.Kind is CleanEventKind.FileProgress or CleanEventKind.Heartbeat)
        {
            if (ev.Elapsed.TotalMilliseconds - _lastLoggedProgressMs < 2000) return;
            _lastLoggedProgressMs = (long)ev.Elapsed.TotalMilliseconds;
        }
        CleanLog.WriteEvent(ev);
    }

    public void Dispose() => _watchdog?.Dispose();
}
