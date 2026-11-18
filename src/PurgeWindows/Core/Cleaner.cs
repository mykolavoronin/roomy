using System.Diagnostics;
using PurgeWindows.Localization;
namespace PurgeWindows.Core;

public sealed record CleanJob(CleanupCategory Category, IReadOnlyList<CleanupItem> Items);

/// <summary>Where one category's files ended up.</summary>
public enum CleanDestination
{
    /// <summary>Deleted directly. The space is freed right away.</summary>
    Deleted,
    /// <summary>Moved to the Recycle Bin. The space is freed once the bin is emptied.</summary>
    Recycled,
}

/// <summary>What one category contributed to a cleanup, for the done screen.</summary>
public sealed record CategoryCleanResult(string CategoryId, string Title, long Freed, long Recycled, long Skipped, int Files, CleanDestination Destination);

/// <summary>
/// Live progress during a cleanup. Every value is measured, never estimated.
/// The first six values are the overall totals; the rest describe the part (category)
/// being cleaned right now, so the UI can show a live per-part list. All part values
/// default to "no part context", which keeps older callers and the final report working.
/// <para/>
/// There is exactly ONE overall-fraction definition: size-weighted over executable parts,
/// including in-flight attempted bytes, non-decreasing, clamped to [0,1]. The UI drives
/// its bar from <see cref="Fraction"/>, never from a second computation.
/// </summary>
public sealed record CleanProgress(
    double Fraction, string Step, string CurrentItem, long FreedSoFar, long RecycledSoFar, int FilesSoFar,
    string CategoryId = "", int PartIndex = -1, int PartCount = 0, double PartFraction = 0,
    string CurrentPath = "", int PartFilesSoFar = 0, long PartFreedSoFar = 0, long PartRecycledSoFar = 0,
    long PartSkippedSoFar = 0, bool PartDone = false, bool PartFailed = false, bool Cancelled = false);

/// <param name="Recycled">Bytes moved to the Recycle Bin. They are not counted in <paramref name="Freed"/>.</param>
/// <param name="SkipReasons">Skipped-file counts by fixed reason, aggregated over the run.</param>
/// <param name="Failed">Files attempted but still there (in use, locked, shell failures).</param>
public sealed record CleanResult(long Freed, long Skipped, int Files, long Recycled = 0,
    IReadOnlyList<CategoryCleanResult>? Categories = null,
    IReadOnlyDictionary<SkipReason, int>? SkipReasons = null,
    int Failed = 0)
{
    /// <summary>
    /// The run's outcome, resolved from counts the way <see cref="CleanOutcomes.Resolve"/>
    /// resolves from a failure list: removed files with no failures → Success;
    /// removed with failures → Partial; nothing removed with failures → Failed;
    /// nothing removed, nothing failed, but policy refusals → SkippedOnly;
    /// otherwise NothingLeft. Skips for other reasons (too-new files, capacity)
    /// never downgrade a run that removed files.
    /// </summary>
    public CleanOutcome Outcome
    {
        get
        {
            int safetySkipped = SkipReasons is not null &&
                SkipReasons.TryGetValue(SkipReason.PolicyRefused, out var n) ? n : 0;
            if (Files > 0) return Failed == 0 ? CleanOutcome.Success : CleanOutcome.Partial;
            if (Failed > 0) return CleanOutcome.Failed;
            return safetySkipped > 0 ? CleanOutcome.SkippedOnly : CleanOutcome.NothingLeft;
        }
    }
}

/// <summary>
/// Thrown when a cleanup is cancelled. It is still an <see cref="OperationCanceledException"/>,
/// so existing callers catch it as before — but it also carries everything that was already
/// cleaned, so the UI can show a coherent partial finish instead of throwing the work away.
/// Partial totals are exact: the engine reports on the cancellation exit path, not only on
/// the reporting cadence, and bytes never attempted are neither freed nor skipped.
/// </summary>
public sealed class CleanCancelledException : OperationCanceledException
{
    public CleanCancelledException(CleanResult partial, CancellationToken ct) : base(ct) => Partial = partial;
    public CleanResult Partial { get; }
}

public static class Cleaner
{
    /// <summary>How often in-flight (within one folder) progress may reach the UI: fast enough
    /// to feel live, slow enough that the UI thread is never flooded. Part and item
    /// boundaries always report immediately, whatever the throttle says.</summary>
    const long ForwardIntervalMs = 120;

    /// <summary>
    /// Cleans the given jobs. Every Delete job's files go to the Recycle Bin
    /// (D-21: the bin is the default for ALL cleanable items); the only bytes
    /// counted as freed are those an owner tool removed with explicit consent.
    /// Every item path is evaluated against the deletion safety policy before
    /// cleaning, and every file is re-checked at delete time.
    /// </summary>
    /// <param name="mode">Retained for compatibility; all Delete jobs go to the
    /// Recycle Bin regardless of the mode (D-21).</param>
    /// <param name="options">Run options (owner window, event sink, bin budget, owner tools, watchdog, policy).
    /// Null behaves safely: unknown bin capacity, owner tools on without consent, watchdog on, default policy.</param>
    public static CleanResult Run(IReadOnlyList<CleanJob> jobs, IProgress<CleanProgress>? progress,
        RemovalMode mode = RemovalMode.ReviewToBin, CancellationToken ct = default,
        CleanOptions? options = null)
    {
        // Only Delete jobs actually clean anything. ReportOnly, Steam, Settings, and the
        // (now review-only) Recycle Bin jobs never reach the engine from the UI; if a
        // caller sends one anyway it stays silent and untouched, takes up no part in the
        // progress list, and contributes nothing to the total — exactly as before.
        var executable = jobs.Where(j => j.Category.Action == CleanAction.Delete).ToList();
        // Tier enforcement, before anything is touched or reported: Check-First
        // jobs need a confirmation covering them, Safe-tier-only runs refuse
        // anything non-Safe, and owner-tool consent without a confirmation
        // refuses the run. Throws InvalidOperationException; the UI cannot
        // bypass it because it runs here, in the engine.
        CleanOptions.ValidateRun(executable, options);
        var policy = options?.Policy ?? DeletionSafetyPolicy.Default;
        long total = Math.Max(1, executable.Sum(j => j.Items.Sum(i => i.Size)));
        long done = 0, freed = 0, skipped = 0, recycled = 0;
        int files = 0, failed = 0;
        long attemptedTotal = 0;
        int filesAttemptedTotal = 0;
        var perCategory = new List<CategoryCleanResult>();
        var reasons = new Dictionary<SkipReason, int>();
        void CountReasons(IReadOnlyDictionary<SkipReason, int> r)
        {
            foreach (var (reason, n) in r) reasons[reason] = reasons.TryGetValue(reason, out var c) ? c + n : n;
        }

        bool useWatchdog = options?.EnableWatchdog ?? true;
        CleanWatchdog? watchdog = useWatchdog
            ? new CleanWatchdog(options?.WatchdogWarnAfter ?? TimeSpan.FromSeconds(5),
                options?.WatchdogStuckAfter ?? TimeSpan.FromSeconds(15))
            : null;
        using var sink = new CleanEventSink(options, watchdog);
        var watch = Stopwatch.StartNew();
        long lastForwardMs = -ForwardIntervalMs;
        double lastFraction = 0;

        // Every UI report goes through here: never-throwing (a broken listener must not
        // fail a part) and monotonic (a stale report can never rewind the bar). The
        // in-flight extras let live totals advance inside a path: the extras describe
        // work the current path has done but the run has not accounted yet, and they
        // drop back to zero the moment the path completes and its result is credited,
        // so totals stay continuous and exact.
        void EmitProgress(double fraction, string step, string currentItem,
            string categoryId = "", int partIndex = -1, int partCount = 0, double partFraction = 0,
            string currentPath = "", int partFiles = 0, long partFreed = 0, long partRecycled = 0,
            long partSkipped = 0, bool partDone = false, bool partFailed = false, bool cancelled = false,
            int filesExtra = 0, long freedExtra = 0, long recycledExtra = 0)
        {
            lastFraction = Math.Clamp(Math.Max(lastFraction, fraction), 0, 1);
            try
            {
                progress?.Report(new CleanProgress(lastFraction, step, currentItem,
                    freed + freedExtra, recycled + recycledExtra, files + filesExtra,
                    CategoryId: categoryId, PartIndex: partIndex, PartCount: partCount,
                    PartFraction: Math.Clamp(partFraction, 0, 1), CurrentPath: currentPath,
                    PartFilesSoFar: partFiles, PartFreedSoFar: partFreed, PartRecycledSoFar: partRecycled,
                    PartSkippedSoFar: partSkipped, PartDone: partDone, PartFailed: partFailed, Cancelled: cancelled));
            }
            catch { /* a listener must never break the engine */ }
        }

        sink.Emit(CleanEventKind.RunStarted, "", CleanPhase.Finishing, "", total, 0, 0, 0, 0, 0, 0, 0, 0,
            parts: executable.Select(j => new CleanPartInfo(j.Category.Id, j.Category.Title, j.Items.Sum(i => i.Size))).ToList());

        for (int partIndex = 0; partIndex < executable.Count; partIndex++)
        {
            var job = executable[partIndex];
            var step = Loc.Format("CleaningCategory", job.Category.Title.ToLowerInvariant());
            long jobTotal = Math.Max(1, job.Items.Sum(i => i.Size));
            long jobDoneBytes = 0;
            long jobFreed = 0, jobRecycled = 0, jobSkipped = 0;
            int jobFiles = 0;
            long partAttempted = 0;
            // The latest in-flight report from the path being processed right now. Reset after
            // every path finishes, so it only ever describes work that has not been counted yet:
            // on cancellation it is the sole record of the interrupted path's partial progress.
            // FileOps reports an exact snapshot on its cancellation exit path, so this is exact.
            FileOps.FileOpProgress inFlight = default;
            // Bytes attempted in the item being processed right now (capped at its scan
            // size), as last reported by the callback. Boundary events emitted after an
            // in-flight report must not rewind the fraction below what was already
            // reported, so they take the max of the throttled UI fraction and this.
            long currentItemAttempted = 0;
            // True once this job's result is recorded, so a failure while reporting the finish
            // can never record the same part twice.
            bool recorded = false;

            sink.Emit(CleanEventKind.PartStarted, job.Category.Id, CleanPhase.Finishing, "", total,
                attemptedTotal, freed + recycled, filesAttemptedTotal, files, jobTotal, 0, lastFraction, 0);
            EmitProgress((double)done / total, step, "", job.Category.Id, partIndex, executable.Count);

            try
            {
                foreach (var item in job.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    inFlight = default;
                    currentItemAttempted = 0;
                    long got = 0;
                    long completedPathsAttempted = 0;
                    bool itemRebaselined = false;

                    // Owner tools first, where they exist: the tool's own clean command is
                    // vendor-correct for its cache. Owner commands delete for good, so
                    // they are a consented permanent action: they run ONLY for
                    // Check-First items when the run carries explicit consent AND a
                    // confirmation covering this category that consents to owner
                    // tools (both set from the confirmation dialog, and only there).
                    // Without that, the non-destructive pre-step runs at most, and
                    // hand-deletion recycles whatever remains, behind the fixed engine.
                    var tool = (options?.UseOwnerTools ?? true) ? OwnerTools.Match(item) : null;
                    if (tool != null)
                    {
                        bool ownerConsent = CleanOptions.OwnerToolAllowed(job, options);
                        if (ownerConsent && tool.CleanCommands.Count > 0)
                        {
                            // Heartbeats during the owner phase: the tool reports no
                            // incremental progress, so liveness + the command label are
                            // what keep the run from looking frozen. The fraction does not
                            // advance until the tool's result is measured — measured only.
                            void OwnerHeartbeat(string label)
                            {
                                sink.Emit(CleanEventKind.Heartbeat, job.Category.Id, CleanPhase.OwnerTool, label,
                                    total, attemptedTotal, freed + recycled, filesAttemptedTotal, files,
                                    jobTotal, partAttempted, (double)done / total, (double)jobDoneBytes / jobTotal);
                                if (watch.ElapsedMilliseconds - lastForwardMs < ForwardIntervalMs) return;
                                lastForwardMs = watch.ElapsedMilliseconds;
                                EmitProgress((double)done / total, step, item.Name,
                                    job.Category.Id, partIndex, executable.Count, (double)jobDoneBytes / jobTotal,
                                    label, jobFiles, jobFreed, jobRecycled, jobSkipped);
                            }
                            foreach (var cmd in tool.CleanCommands)
                            {
                                ct.ThrowIfCancellationRequested();
                                var result = OwnerTools.Execute(tool, cmd, item.Paths, ct,
                                    options?.OwnerToolTimeout ?? TimeSpan.FromMinutes(10),
                                    (_, label) => OwnerHeartbeat(label));
                                long diff = Math.Max(0, result.BytesBefore - result.BytesAfter);
                                freed += diff;
                                jobFreed += diff;
                                got += diff;
                                // Accumulate immediately, per command: event counters must
                                // never go backwards across a multi-command tool.
                                attemptedTotal += diff;
                                partAttempted += diff;
                                completedPathsAttempted += diff;
                                sink.Emit(CleanEventKind.FileProgress, job.Category.Id, CleanPhase.OwnerTool, cmd.Label,
                                    total, attemptedTotal, freed + recycled, filesAttemptedTotal, files,
                                    jobTotal, partAttempted, lastFraction, (double)jobDoneBytes / jobTotal,
                                    detail: $"owner-tool {result.ToolName} {result.Label}: {result.Outcome} exit={result.ExitCode}");
                                if (result.Outcome == OwnerToolOutcome.Cancelled) ct.ThrowIfCancellationRequested();
                            }
                            // Only removed bytes count as covered (accumulated per command
                            // above): if the tool removed nothing, hand-deletion still has
                            // the whole item to attempt, and the fraction must not jump
                            // ahead of it.
                        }
                        else if (tool.PreStep != null)
                        {
                            OwnerTools.RunPreStep(tool, ct);
                        }
                    }

                    // One item can span several folders; the callback below turns per-folder
                    // progress into per-part progress, throttled so the UI stays responsive.
                    // Events (heartbeat vs progress) are emitted for every report; UI
                    // forwarding is throttled, with boundaries always immediate.
                    var fileProgress = new FileOpProgressSink((fp, currentItem) =>
                    {
                        inFlight = fp;
                        long attemptedInItem = Math.Min(item.Size, completedPathsAttempted + fp.BytesAttempted);
                        currentItemAttempted = attemptedInItem;
                        double partFraction = (double)(jobDoneBytes + attemptedInItem) / jobTotal;
                        if (!itemRebaselined && completedPathsAttempted + fp.BytesAttempted > item.Size)
                        {
                            itemRebaselined = true;
                            sink.Emit(CleanEventKind.Rebaselined, job.Category.Id, fp.Phase, fp.CurrentPath,
                                total, attemptedTotal + fp.BytesAttempted, freed + recycled + fp.BytesRemoved,
                                filesAttemptedTotal + fp.FilesAttempted, files + fp.FilesRemoved,
                                jobTotal, partAttempted + fp.BytesAttempted,
                                (double)(done + attemptedInItem) / total, partFraction,
                                detail: "tree grew past the scan estimate; fraction clamped, not rewound");
                        }
                        sink.Emit(fp.IsHeartbeat ? CleanEventKind.Heartbeat : CleanEventKind.FileProgress,
                            job.Category.Id, fp.Phase, fp.CurrentPath,
                            total, attemptedTotal + fp.BytesAttempted, freed + recycled + fp.BytesRemoved,
                            filesAttemptedTotal + fp.FilesAttempted, files + fp.FilesRemoved,
                            jobTotal, partAttempted + fp.BytesAttempted,
                            (double)(done + attemptedInItem) / total, partFraction,
                            shellReturnCode: fp.ShellReturnCode, shellAborted: fp.ShellAborted ? true : null);
                        if (watch.ElapsedMilliseconds - lastForwardMs < ForwardIntervalMs) return;
                        lastForwardMs = watch.ElapsedMilliseconds;
                        EmitProgress((double)(done + attemptedInItem) / total, step, currentItem,
                            job.Category.Id, partIndex, executable.Count, partFraction, fp.CurrentPath,
                            jobFiles + fp.FilesRemoved,
                            jobFreed,
                            jobRecycled + fp.BytesRemoved,
                            jobSkipped,
                            filesExtra: fp.FilesRemoved,
                            freedExtra: 0,
                            recycledExtra: fp.BytesRemoved);
                    }, item.Name);

                    foreach (var p in item.Paths)
                    {
                        // Policy gate, before EVERY delete: the path is evaluated
                        // fresh, right now — never memoized. Tool-resolved paths
                        // (npm, Go) take the tool-resolved evaluation. A refused
                        // path is skipped with a recorded reason, never touched;
                        // FileOps re-checks every file at delete time as well.
                        var decision = item.ToolResolved
                            ? policy.EvaluateToolResolved(p)
                            : policy.Evaluate(p);
                        if (decision.Verdict != PolicyVerdict.Allow)
                        {
                            CountReasons(new Dictionary<SkipReason, int> { [SkipReason.PolicyRefused] = 1 });
                            CleanLog.WriteLine($"path skipped by policy {job.Category.Id}/{item.Name}: {p}: " +
                                $"{decision.Verdict} (step {decision.Step}, {decision.Matched})");
                            continue;
                        }
                        try
                        {
                            var r = FileOps.RecycleContents(p, item.MinAge, ct, fileProgress,
                                options?.Budget, options?.OwnerWindowHandle ?? default, policy);
                            recycled += r.Freed;
                            jobRecycled += r.Freed;
                            files += r.Files;
                            jobFiles += r.Files;
                            failed += r.Failed;
                            got += r.Freed;
                            CountReasons(r.Reasons);
                            if (r.LastShellReturnCode is { } code && (code != 0 || r.ShellAborted))
                                sink.Emit(CleanEventKind.FileProgress, job.Category.Id, CleanPhase.Recycling, p,
                                    total, attemptedTotal, freed + recycled, filesAttemptedTotal, files,
                                    jobTotal, partAttempted,
                                    Math.Max(lastFraction, (double)(done + currentItemAttempted) / total),
                                    Math.Clamp((double)(jobDoneBytes + currentItemAttempted) / jobTotal, 0, 1),
                                    reason: SkipReason.ShellError, shellReturnCode: code,
                                    shellAborted: r.ShellAborted, detail: "shell batch outcome recorded");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            // Protected or vanished path: skip it, record it, carry on.
                            CleanLog.WriteLine($"path skipped {job.Category.Id}/{item.Name}: {p}: {ex.GetType().Name}: {ex.Message}");
                        }
                        // This path is fully counted now (or was skipped wholesale); whatever the
                        // callback last saw for it counts towards the item's attempted bytes, and
                        // the in-flight record clears so a later cancellation cannot count it twice.
                        completedPathsAttempted += inFlight.BytesAttempted;
                        attemptedTotal += inFlight.BytesAttempted;
                        partAttempted += inFlight.BytesAttempted;
                        filesAttemptedTotal += inFlight.FilesAttempted;
                        inFlight = default;
                    }
                    long itemSkipped = Math.Max(0, item.Size - got);
                    skipped += itemSkipped;
                    jobSkipped += itemSkipped;
                    done += item.Size;
                    jobDoneBytes += item.Size;
                    EmitProgress((double)done / total, step, item.Name,
                        job.Category.Id, partIndex, executable.Count, (double)jobDoneBytes / jobTotal,
                        partFiles: jobFiles, partFreed: jobFreed, partRecycled: jobRecycled, partSkipped: jobSkipped);
                }
                perCategory.Add(new CategoryCleanResult(job.Category.Id, job.Category.Title,
                    jobFreed, jobRecycled, jobSkipped, jobFiles,
                    CleanDestination.Recycled));
                recorded = true;
                sink.Emit(CleanEventKind.PartFinished, job.Category.Id, CleanPhase.Finishing, "", total,
                    attemptedTotal, freed + recycled, filesAttemptedTotal, files, jobTotal, partAttempted,
                    lastFraction, 1);
                EmitProgress((double)done / total, step, "",
                    job.Category.Id, partIndex, executable.Count, 1,
                    partFiles: jobFiles, partFreed: jobFreed, partRecycled: jobRecycled,
                    partSkipped: jobSkipped, partDone: true);
            }
            catch (OperationCanceledException)
            {
                // Credit the interrupted path's partial work (only it is uncounted; see above),
                // then stop with everything done so far intact. Bytes never attempted are not
                // "skipped": the part is cancelled, and the rest of the run never started.
                if (inFlight.FilesRemoved > 0)
                {
                    files += inFlight.FilesRemoved;
                    jobFiles += inFlight.FilesRemoved;
                    recycled += inFlight.BytesRemoved; jobRecycled += inFlight.BytesRemoved;
                }
                attemptedTotal += inFlight.BytesAttempted;
                partAttempted += inFlight.BytesAttempted;
                filesAttemptedTotal += inFlight.FilesAttempted;
                if (inFlight.SkipReasons is { } cancelledReasons) CountReasons(cancelledReasons);
                if (!recorded)
                    perCategory.Add(new CategoryCleanResult(job.Category.Id, job.Category.Title,
                        jobFreed, jobRecycled, jobSkipped, jobFiles,
                        CleanDestination.Recycled));
                sink.Emit(CleanEventKind.RunCancelled, job.Category.Id, CleanPhase.Finishing, "", total,
                    attemptedTotal, freed + recycled, filesAttemptedTotal, files, jobTotal, partAttempted,
                    Math.Max(lastFraction, (double)(done + currentItemAttempted) / total),
                    Math.Clamp((double)(jobDoneBytes + currentItemAttempted) / jobTotal, 0, 1));
                EmitProgress((double)done / total, step, "",
                    job.Category.Id, partIndex, executable.Count,
                    partFiles: jobFiles, partFreed: jobFreed, partRecycled: jobRecycled,
                    partSkipped: jobSkipped, cancelled: true);
                throw new CleanCancelledException(new CleanResult(freed, skipped, files, recycled, perCategory, reasons, failed), ct);
            }
            catch (Exception ex)
            {
                // One bad part must not take down the whole cleanup: mark it failed, with
                // whatever it managed, and carry on with the next part. The finish screen
                // names the failed parts instead of hiding them behind a generic error.
                // Failed parts count as done for overall progress (same rule as
                // CleanProgressModel); the part fraction stays partial.
                done += Math.Max(0, jobTotal - jobDoneBytes);
                if (inFlight.SkipReasons is { } failedReasons) CountReasons(failedReasons);
                if (!recorded)
                    perCategory.Add(new CategoryCleanResult(job.Category.Id, job.Category.Title,
                        jobFreed, jobRecycled, jobSkipped, jobFiles,
                        CleanDestination.Recycled));
                CleanLog.WriteLine($"part failed {job.Category.Id}: {ex.GetType().Name}: {ex.Message}");
                sink.Emit(CleanEventKind.PartFailed, job.Category.Id, CleanPhase.Finishing, "", total,
                    attemptedTotal, freed + recycled, filesAttemptedTotal, files, jobTotal, partAttempted,
                    (double)done / total,
                    Math.Clamp((double)(jobDoneBytes + currentItemAttempted) / jobTotal, 0, 1),
                    detail: ex.GetType().Name);
                EmitProgress((double)done / total, step, "",
                    job.Category.Id, partIndex, executable.Count, (double)jobDoneBytes / jobTotal,
                    partFiles: jobFiles, partFreed: jobFreed, partRecycled: jobRecycled,
                    partSkipped: jobSkipped, partFailed: true);
            }
        }

        sink.Emit(CleanEventKind.RunFinished, "", CleanPhase.Finishing, "", total,
            attemptedTotal, freed + recycled, filesAttemptedTotal, files, 0, 0, 1, 0,
            detail: string.Join(", ", reasons.Select(kv => $"{kv.Key}={kv.Value}")));
        EmitProgress(1, S.AllDone, "");
        return new CleanResult(freed, skipped, files, recycled, perCategory, reasons, failed);
    }

    /// <summary>Turns a file-operation progress callback into a plain <see cref="IProgress{T}"/>.</summary>
    sealed class FileOpProgressSink(Action<FileOps.FileOpProgress, string> onReport, string itemName)
        : IProgress<FileOps.FileOpProgress>
    {
        public void Report(FileOps.FileOpProgress value) => onReport(value, itemName);
    }
}
