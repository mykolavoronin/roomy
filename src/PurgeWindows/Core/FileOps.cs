using System.Diagnostics;
using System.IO.Enumeration;

namespace PurgeWindows.Core;

/// <summary>Measuring and deleting, with guard rails.</summary>
public static class FileOps
{
    /// <summary>
    /// What one delete/recycle operation did. <see cref="Failed"/> counts files that were
    /// attempted but are still there; <see cref="Skipped"/> counts files never attempted,
    /// broken down by <see cref="SkipReasons"/>. Shell outcomes are recorded, never
    /// discarded (<see cref="LastShellReturnCode"/>, <see cref="ShellAborted"/>).
    /// </summary>
    public readonly record struct DeleteResult(
        long Freed, int Files, int Failed,
        int Skipped = 0, long SkippedBytes = 0,
        IReadOnlyDictionary<SkipReason, int>? SkipReasons = null,
        int? LastShellReturnCode = null, bool ShellAborted = false)
    {
        static readonly IReadOnlyDictionary<SkipReason, int> Empty =
            new Dictionary<SkipReason, int>();
        public IReadOnlyDictionary<SkipReason, int> Reasons => SkipReasons ?? Empty;
    }

    /// <summary>
    /// Live progress inside one delete/recycle operation. <see cref="FilesAttempted"/> and
    /// <see cref="BytesAttempted"/> count every eligible file tried (removed or not), so a
    /// progress bar driven by them always reaches the end; <see cref="FilesRemoved"/> and
    /// <see cref="BytesRemoved"/> count only files that are actually gone, so totals built
    /// from them never claim more than happened.
    /// <para/>
    /// Enumeration streams, so totals are unknown while work is in flight:
    /// <see cref="FilesTotal"/>/<see cref="BytesTotal"/> are 0 (unknown) until the final
    /// report, which carries the exact totals. Callers map attempted bytes onto the scan
    /// estimate for the progress bar; they never treat 0 as "done".
    /// <see cref="IsHeartbeat"/> reports are liveness only — same counters, no new progress —
    /// emitted at least every 250 ms in every phase, including enumeration and tidy-up.
    /// </summary>
    public readonly record struct FileOpProgress(
        string CurrentPath, int FilesAttempted, int FilesTotal,
        long BytesAttempted, long BytesTotal, int FilesRemoved, long BytesRemoved,
        CleanPhase Phase = CleanPhase.Deleting, bool IsHeartbeat = false,
        int FilesSkipped = 0, long BytesSkipped = 0,
        int? ShellReturnCode = null, bool ShellAborted = false,
        IReadOnlyDictionary<SkipReason, int>? SkipReasons = null);

    /// <summary>Reports progress without ever letting a listener break the file operation.</summary>
    static void Tell(IProgress<FileOpProgress>? progress, FileOpProgress value)
    {
        try { progress?.Report(value); }
        catch { }
    }

    /// <summary>Emits at most one heartbeat per 250 ms (the first call always emits).</summary>
    sealed class HeartbeatGate
    {
        readonly Stopwatch _sw = Stopwatch.StartNew();
        long _lastMs = -1000;
        public bool TryBeat()
        {
            var now = _sw.ElapsedMilliseconds;
            if (now - _lastMs < 250) return false;
            _lastMs = now;
            return true;
        }
    }

    static EnumerationOptions Deep() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Never follow junctions/symlinks: they can point anywhere.
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    static readonly EnumerationOptions Flat = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    static DateTime Cutoff(TimeSpan? minAge) => minAge is { } a ? DateTime.UtcNow - a : DateTime.MaxValue;

    public static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    public static (long Bytes, int Files) Measure(string path, TimeSpan? minAge, CancellationToken ct = default)
    {
        long bytes = 0;
        int files = 0;
        var cutoff = Cutoff(minAge);
        try
        {
            // Only count what really lives at this path: nothing reached through a junction or symlink.
            if (SafeFs.TrustedPath(path) is not { } root) return (0, 0);
            if (File.Exists(root))
            {
                var fi = new FileInfo(root);
                return fi.LastWriteTimeUtc <= cutoff ? (fi.Length, 1) : (0, 0);
            }
            if (!Directory.Exists(root)) return (0, 0);

            var e = new FileSystemEnumerable<(long Len, DateTime Mod)>(root,
                (ref FileSystemEntry x) => (x.Length, x.LastWriteTimeUtc.UtcDateTime), Deep())
            {
                ShouldIncludePredicate = (ref FileSystemEntry x) => !x.IsDirectory,
            };
            foreach (var (len, mod) in e)
            {
                ct.ThrowIfCancellationRequested();
                if (mod <= cutoff) { bytes += len; files++; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* keep whatever we counted */ }
        return (bytes, files);
    }

    /// <summary>
    /// Deletes a file, or the contents of a folder (the folder itself stays).
    /// Files that are in use are skipped, never forced. Nothing is reached through a junction or
    /// symlink, wherever it sits in the path, and every delete is checked against the file's real location.
    /// Enumeration streams: files are deleted as they are found, with a heartbeat at least
    /// every 250 ms and a cancellation check per file — no whole-tree materialization, no
    /// silent phase. On cancellation the exact partial snapshot is reported before the
    /// exception propagates, so callers can credit it precisely.
    /// </summary>
    /// <param name="progress">Live progress + heartbeats. Best-effort; can never change what gets deleted.</param>
    public static DeleteResult Delete(string path, TimeSpan? minAge, CancellationToken ct = default,
        IProgress<FileOpProgress>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = Cutoff(minAge);
        if (SafeFs.TrustedPath(path) is not { } root) return default;
        Guard(path);
        Guard(root);

        if (File.Exists(root))
        {
            var fi = new FileInfo(root);
            long len = fi.Length;
            if (fi.LastWriteTimeUtc > cutoff)
                return new(0, 0, 0, Skipped: 1, SkippedBytes: len,
                    SkipReasons: new Dictionary<SkipReason, int> { [SkipReason.TooNew] = 1 });
            bool gone = SafeFs.Delete(root, root);
            Tell(progress, new FileOpProgress(root, 1, 1, len, len, gone ? 1 : 0, gone ? len : 0, CleanPhase.Deleting));
            return gone ? new(len, 1, 0) : new(0, 0, 1);
        }

        if (!Directory.Exists(root)) return default;

        var gate = new HeartbeatGate();
        var reasons = new Dictionary<SkipReason, int>();
        void Count(SkipReason r) => reasons[r] = reasons.TryGetValue(r, out var n) ? n + 1 : 1;

        long freed = 0;
        int ok = 0, failed = 0, attempted = 0, skipped = 0;
        long attemptedBytes = 0, skippedBytes = 0;
        string current = root;

        FileOpProgress Snap(CleanPhase phase, bool heartbeat, bool final) => new(
            current, attempted, final ? attempted : 0, attemptedBytes, final ? attemptedBytes : 0,
            ok, freed, phase, heartbeat, skipped, skippedBytes,
            SkipReasons: reasons.Count > 0 ? new Dictionary<SkipReason, int>(reasons) : null);

        try
        {
            var e = new FileSystemEnumerable<(string Path, long Len, DateTime Mod)>(root,
                (ref FileSystemEntry x) => (x.ToFullPath(), x.Length, x.LastWriteTimeUtc.UtcDateTime), Deep())
            {
                ShouldIncludePredicate = (ref FileSystemEntry x) => !x.IsDirectory,
            };
            foreach (var (p, len, mod) in e)
            {
                ct.ThrowIfCancellationRequested();
                current = p;
                if (gate.TryBeat()) Tell(progress, Snap(CleanPhase.Deleting, heartbeat: true, final: false));
                if (mod > cutoff) { skipped++; skippedBytes += len; Count(SkipReason.TooNew); continue; }
                attempted++;
                attemptedBytes += len;
                if (SafeFs.Delete(p, root)) { freed += len; ok++; }
                else failed++;
                if (attempted == 1 || attempted % 64 == 0)
                    Tell(progress, Snap(CleanPhase.Deleting, heartbeat: false, final: false));
            }
        }
        catch (OperationCanceledException)
        {
            // Exact partial snapshot on the cancellation exit path — not just on cadence.
            Tell(progress, Snap(CleanPhase.Deleting, heartbeat: false, final: true));
            throw;
        }
        catch
        {
            // Enumeration broke partway (a directory vanished mid-walk, ...). Keep the exact
            // partial counts and tidy up, instead of discarding work already done.
        }

        Tell(progress, Snap(CleanPhase.Deleting, heartbeat: false, final: true));
        TidyUp(root, root, ct, d =>
        {
            current = d;
            // Enumeration is done, so totals are known even in these heartbeats.
            if (gate.TryBeat()) Tell(progress, Snap(CleanPhase.TidyingUp, heartbeat: true, final: true));
        }, depth: 0);

        return new(freed, ok, failed, skipped, skippedBytes, reasons);
    }

    /// <summary>Sends a single file to the Recycle Bin, with the same guards as <see cref="Delete"/>.</summary>
    /// <param name="budget">When given, a file that cannot fit in the remaining bin capacity
    /// is skipped with a recorded reason instead of reaching the shell.</param>
    /// <param name="ownerHwnd">Owner window for the shell call; zero resolves the main window.</param>
    public static DeleteResult Recycle(string path, TimeSpan? minAge, CancellationToken ct = default,
        RecycleBinBudget? budget = null, IntPtr ownerHwnd = default)
    {
        ct.ThrowIfCancellationRequested();
        if (SafeFs.TrustedPath(path) is not { } real || !File.Exists(real)) return default;
        Guard(path);
        Guard(real);
        var fi = new FileInfo(real);
        long len = fi.Length;
        if (fi.LastWriteTimeUtc > Cutoff(minAge))
            return new(0, 0, 0, Skipped: 1, SkippedBytes: len,
                SkipReasons: new Dictionary<SkipReason, int> { [SkipReason.TooNew] = 1 });
        if (budget?.RefusalReason(real, len) is { } refusal)
            return new(0, 0, 0, Skipped: 1, SkippedBytes: len,
                SkipReasons: new Dictionary<SkipReason, int> { [refusal] = 1 });
        budget?.TryReserve(real, len);
        var result = Native.SendToRecycleBin(real, ownerHwnd);
        CleanLog.WriteShellBatch(result.ReturnCode, result.Aborted, 1, len, real);
        bool gone = !File.Exists(real);
        return gone
            ? new(len, 1, 0, LastShellReturnCode: result.ReturnCode, ShellAborted: result.Aborted)
            : new(0, 0, 1, LastShellReturnCode: result.ReturnCode, ShellAborted: result.Aborted);
    }

    /// <summary>
    /// Sends a file, or the contents of a folder (the folder itself stays), to the Recycle Bin.
    /// Same guards as <see cref="Delete"/>: nothing is reached through a junction or symlink,
    /// protected locations are refused, and files that are in use are skipped, never forced.
    /// Files go to the bin in capped batches (200 files / 256 MiB) through one no-UI shell
    /// call each; a file only counts as recycled once it is actually gone. Enumeration
    /// streams in bounded batches — there is no whole-tree candidate list and no O(n²)
    /// slicing. Batches that cannot fit in the remaining bin capacity are skipped with a
    /// recorded reason, never sent to the shell. Now-empty subfolders are tidied up afterwards.
    /// </summary>
    /// <param name="progress">Live progress + heartbeats, per batch and at least every 250 ms.</param>
    /// <param name="budget">Pre-flight bin budget; null means capacity unknown (no enforcement).</param>
    /// <param name="ownerHwnd">Owner window for shell calls; zero resolves the main window.</param>
    public static DeleteResult RecycleContents(string path, TimeSpan? minAge, CancellationToken ct = default,
        IProgress<FileOpProgress>? progress = null, RecycleBinBudget? budget = null, IntPtr ownerHwnd = default)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = Cutoff(minAge);
        if (SafeFs.TrustedPath(path) is not { } root) return default;
        Guard(path);
        Guard(root);

        if (File.Exists(root))
        {
            long len = 0;
            try { len = new FileInfo(root).Length; } catch { }
            var single = Recycle(root, minAge, ct, budget, ownerHwnd);
            int tried = single.Files + single.Failed;
            if (tried + single.Skipped > 0)
                Tell(progress, new FileOpProgress(root, tried, tried, tried > 0 ? len : 0, tried > 0 ? len : 0,
                    single.Files, single.Freed,
                    CleanPhase.Recycling, FilesSkipped: single.Skipped, BytesSkipped: single.SkippedBytes,
                    ShellReturnCode: single.LastShellReturnCode, ShellAborted: single.ShellAborted,
                    SkipReasons: single.SkipReasons));
            return single;
        }
        if (!Directory.Exists(root)) return default;

        const int MaxBatchFiles = 200;
        const long MaxBatchBytes = 256L * 1024 * 1024;

        var gate = new HeartbeatGate();
        var reasons = new Dictionary<SkipReason, int>();
        void Count(SkipReason r, int n = 1) => reasons[r] = reasons.TryGetValue(r, out var c) ? c + n : n;

        long freed = 0;
        int ok = 0, failed = 0, attempted = 0, skipped = 0;
        long attemptedBytes = 0, skippedBytes = 0;
        int? lastShellCode = null;
        bool shellAborted = false;
        string current = root;

        FileOpProgress Snap(CleanPhase phase, bool heartbeat, bool final) => new(
            current, attempted, final ? attempted : 0, attemptedBytes, final ? attemptedBytes : 0,
            ok, freed, phase, heartbeat, skipped, skippedBytes, lastShellCode, shellAborted,
            SkipReasons: reasons.Count > 0 ? new Dictionary<SkipReason, int>(reasons) : null);

        // One bounded batch, built incrementally as enumeration streams. Flushing walks it
        // by index — there is no candidate list to re-slice, so the old O(n²) Skip/Take
        // batching is gone by construction.
        var batch = new List<(string Path, long Len)>();
        long batchBytes = 0;

        void FlushBatch()
        {
            if (batch.Count == 0) return;
            ct.ThrowIfCancellationRequested();
            var first = batch[0].Path;
            if (budget?.RefusalReason(first, batchBytes) is { } refusal)
            {
                skipped += batch.Count;
                skippedBytes += batchBytes;
                Count(refusal, batch.Count);
                current = batch[^1].Path;
                Tell(progress, Snap(CleanPhase.Recycling, heartbeat: false, final: false));
                batch.Clear();
                batchBytes = 0;
                return;
            }
            budget?.TryReserve(first, batchBytes);
            current = first;
            if (gate.TryBeat()) Tell(progress, Snap(CleanPhase.ShellCall, heartbeat: true, final: false));
            var paths = new List<string>(batch.Count);
            for (int i = 0; i < batch.Count; i++) paths.Add(batch[i].Path);
            var result = Native.SendFilesToRecycleBin(paths, ownerHwnd);
            lastShellCode = result.ReturnCode;
            shellAborted |= result.Aborted;
            CleanLog.WriteShellBatch(result.ReturnCode, result.Aborted, batch.Count, batchBytes, first);
            for (int i = 0; i < batch.Count; i++)
            {
                var (p, len) = batch[i];
                attempted++;
                attemptedBytes += len;
                if (!File.Exists(p)) { freed += len; ok++; }
                else failed++;
            }
            current = batch[^1].Path;
            Tell(progress, Snap(CleanPhase.Recycling, heartbeat: false, final: false));
            batch.Clear();
            batchBytes = 0;
        }

        try
        {
            var e = new FileSystemEnumerable<(string Path, long Len, DateTime Mod)>(root,
                (ref FileSystemEntry x) => (x.ToFullPath(), x.Length, x.LastWriteTimeUtc.UtcDateTime), Deep())
            {
                ShouldIncludePredicate = (ref FileSystemEntry x) => !x.IsDirectory,
            };
            foreach (var (p, len, mod) in e)
            {
                ct.ThrowIfCancellationRequested();
                current = p;
                if (gate.TryBeat()) Tell(progress, Snap(CleanPhase.Validating, heartbeat: true, final: false));
                if (mod > cutoff) { skipped++; skippedBytes += len; Count(SkipReason.TooNew); continue; }
                // Only files that really live under the checked root go to the bin.
                var real = SafeFs.TrustedPath(p);
                if (real is null || !SafeFs.IsInside(real, root))
                {
                    skipped++;
                    skippedBytes += len;
                    // The trusted-path check fails closed for both link traversal and
                    // over-long paths; only the length is observable here, so over-long
                    // paths get their own reason and everything else is link-rejected.
                    Count(p.Length > 1024 ? SkipReason.PathTooLong : SkipReason.LinkRejected);
                    continue;
                }
                batch.Add((real, len));
                batchBytes += len;
                if (batch.Count >= MaxBatchFiles || batchBytes >= MaxBatchBytes) FlushBatch();
            }
            FlushBatch();
        }
        catch (OperationCanceledException)
        {
            // The pending batch was never attempted: it stays uncounted, and the exact
            // partial snapshot is reported before the exception propagates.
            Tell(progress, Snap(CleanPhase.Recycling, heartbeat: false, final: true));
            throw;
        }
        catch
        {
            // Enumeration broke partway. Flush the validated batch, keep exact partials.
            try { FlushBatch(); } catch (OperationCanceledException) { throw; } catch { }
        }

        Tell(progress, Snap(CleanPhase.Recycling, heartbeat: false, final: true));
        TidyUp(root, root, ct, d =>
        {
            current = d;
            // Enumeration is done, so totals are known even in these heartbeats.
            if (gate.TryBeat()) Tell(progress, Snap(CleanPhase.TidyingUp, heartbeat: true, final: true));
        }, depth: 0);

        return new(freed, ok, failed, skipped, skippedBytes, reasons, lastShellCode, shellAborted);
    }

    /// <summary>
    /// Removes now-empty subfolders, deepest first, without ever materializing the tree:
    /// a recursive post-order walk (one directory enumerated at a time), with a cancellation
    /// check and a heartbeat callback per directory. The root itself is never removed.
    /// Depth is capped so a pathological tree cannot overflow the stack.
    /// </summary>
    static void TidyUp(string dir, string root, CancellationToken ct, Action<string> beat, int depth)
    {
        if (depth > 200) return;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", Flat))
            {
                ct.ThrowIfCancellationRequested();
                beat(sub);
                TidyUp(sub, root, ct, beat, depth + 1);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* a vanished or inaccessible folder just stays */ }
        if (!string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
            SafeFs.Delete(dir, root); // only succeeds if the folder is empty
    }

    static readonly string[] Protected = new[]
    {
        Env.SystemRoot, Env.Windows, Path.Combine(Env.Windows, "System32"), Env.UserProfile,
        Path.GetDirectoryName(Env.UserProfile) ?? "", Env.ProgramFiles, Env.ProgramFilesX86,
        Env.ProgramData, Env.Local, Env.Roaming, Env.Downloads,
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
    }.Where(s => s.Length > 0).Select(Norm).ToArray();

    static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\'); }
        catch { return p.TrimEnd('\\'); }
    }

    /// <summary>Last line of defence: never wipe a drive root or a top-level personal/system folder.</summary>
    static void Guard(string path)
    {
        var n = Norm(path);
        if (n.Length <= 3 || Protected.Any(p => string.Equals(p, n, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Refusing to clean a protected location: {path}");
    }
}
