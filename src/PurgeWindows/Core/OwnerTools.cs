using System.Diagnostics;
using System.Text;

namespace PurgeWindows.Core;

/// <summary>What happened when a dev tool's own clean command ran. Never trust the exit code alone.</summary>
public enum OwnerToolOutcome
{
    /// <summary>Exit 0, no error markers in the output, and the cache measurably shrank (or was already empty).</summary>
    Success,
    /// <summary>The tool is not installed (executable not found). Hand-deletion is the fallback.</summary>
    NotInstalled,
    /// <summary>The command ran past its timeout and was killed. Hand-deletion cleans the remainder.</summary>
    TimedOut,
    /// <summary>The user stopped the run; the process was killed.</summary>
    Cancelled,
    /// <summary>Non-zero exit code.</summary>
    Failed,
    /// <summary>Exit 0, but the output contains error markers or nothing was removed:
    /// the command claims success the evidence does not support. Hand-deletion is the fallback.</summary>
    Uncertain,
}

/// <summary>One owner-tool command: a fixed executable + fixed arguments. No user input ever reaches a command line.</summary>
public sealed record OwnerToolCommand(string ExeName, string Arguments, string Label);

/// <summary>
/// What a dev tool offers for cleaning its own cache. A tool may offer clean commands,
/// a non-destructive pre-step, or nothing at all — <c>null</c> from
/// <see cref="Match"/> means "no owner command exists; hand-delete behind the fixed engine".
/// </summary>
public sealed record OwnerToolSpec(
    string ToolName,
    IReadOnlyList<OwnerToolCommand> CleanCommands,
    OwnerToolCommand? PreStep);

/// <summary>Result of running one owner-tool command, with the evidence behind the outcome.</summary>
public sealed record OwnerToolResult(
    string ToolName, string Label, OwnerToolOutcome Outcome, int? ExitCode,
    string OutputTail, long BytesBefore, long BytesAfter, TimeSpan Duration);

/// <summary>
/// Prefers each dev tool's own clean command where one exists, mirroring Purge's
/// owner-tool exceptions — with hand-deletion only as a fallback behind the fixed engine.
/// <para/>
/// Honest inventory (Windows, 2026): Go is the only dev cache here with real owner clean
/// commands (<c>go clean -cache</c> for the build cache, <c>go clean -modcache</c> for the
/// read-only module cache and its lock). Gradle has no cache-clean command — deleting
/// <c>~\.gradle\caches</c> by hand is the documented approach once daemons are stopped, so
/// Gradle offers only the non-destructive <c>gradle --stop</c> pre-step. Maven has no global
/// local-repository clean command (<c>mvn dependency:purge-local-repository</c> needs a
/// project POM), and Cargo has no built-in cache clean (<c>cargo clean</c> is per-project;
/// <c>cargo-cache</c> is a third-party subcommand) — both hand-delete. Inventing
/// <c>mvn</c>/<c>cargo</c> invocations here would be unsafe; if a future wave verifies a
/// real owner command for either, add it to <see cref="Match"/>.
/// <para/>
/// Owner commands delete for good — they never recycle. The cleaner therefore runs them
/// only for parts that delete permanently anyway; recycled parts run the pre-step at most.
/// Every command runs with a timeout, bounded output capture, cancellation-linked kill,
/// and a failure taxonomy that checks the exit code, the output, AND the measured
/// before/after size. Execution is synchronous polling on the worker thread (never a
/// blocking wait on a UI thread — there is no UI thread here).
/// </summary>
public static class OwnerTools
{
    const int MaxOutputChars = 16 * 1024;
    const int OutputTailChars = 4096;

    static readonly string[] ErrorMarkers =
        ["error:", "failed", "fatal", "access is denied", "permission denied"];

    /// <summary>
    /// Matches an item to its owning tool by the cache paths the scanner reports. Path-based
    /// matching is a Phase 0 bridge: Wave 3's catalog rework should carry explicit tool
    /// metadata per item instead. Returns null when no owner command or pre-step exists.
    /// </summary>
    public static OwnerToolSpec? Match(CleanupItem item)
    {
        bool Has(params string[] tails) => item.Paths.Any(p =>
            tails.Any(t => p.EndsWith(t, StringComparison.OrdinalIgnoreCase)));

        if (Has(Path.Combine("go", "pkg", "mod"), "go-build"))
        {
            var commands = new List<OwnerToolCommand>();
            if (Has("go-build")) commands.Add(new OwnerToolCommand("go.exe", "clean -cache", "go clean -cache"));
            if (Has(Path.Combine("go", "pkg", "mod"))) commands.Add(new OwnerToolCommand("go.exe", "clean -modcache", "go clean -modcache"));
            if (commands.Count > 0) return new OwnerToolSpec("Go", commands, null);
        }
        if (Has(Path.Combine(".gradle", "caches"), Path.Combine(".gradle", "wrapper", "dists")))
            return new OwnerToolSpec("Gradle", [], new OwnerToolCommand("gradle", "--stop", "gradle --stop"));
        return null; // Maven, Cargo: no owner command; hand-delete behind the fixed engine.
    }

    /// <summary>
    /// Resolves an executable name to a full path: PATH search first, then well-known install
    /// locations. Returns null when the tool is not installed. Never uses the shell.
    /// </summary>
    public static string? ResolveExe(string exeName)
    {
        try
        {
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in pathDirs)
            {
                foreach (var candidate in Candidates(dir, exeName))
                    if (File.Exists(candidate)) return candidate;
            }
            if (string.Equals(exeName, "go.exe", StringComparison.OrdinalIgnoreCase))
            {
                var known = Path.Combine(Env.ProgramFiles, "Go", "bin", "go.exe");
                if (File.Exists(known)) return known;
            }
        }
        catch { }
        return null;
    }

    static IEnumerable<string> Candidates(string dir, string exeName)
    {
        yield return Path.Combine(dir, exeName);
        // Gradle ships as gradle.bat on Windows; it runs via cmd.exe (see Execute).
        if (!exeName.Contains('.')) yield return Path.Combine(dir, exeName + ".bat");
    }

    /// <summary>
    /// Pure outcome classification from exit code + output + measured sizes. Never trusts
    /// the exit code alone: exit 0 with error markers or with nothing removed is Uncertain.
    /// </summary>
    public static OwnerToolOutcome Classify(int? exitCode, string output, long bytesBefore, long bytesAfter)
    {
        if (exitCode is null) return OwnerToolOutcome.Failed;
        if (exitCode != 0) return OwnerToolOutcome.Failed;
        if (ErrorMarkers.Any(m => output.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return OwnerToolOutcome.Uncertain;
        if (bytesBefore > 0 && bytesAfter >= bytesBefore) return OwnerToolOutcome.Uncertain;
        return OwnerToolOutcome.Success;
    }

    /// <summary>
    /// Runs one owner command synchronously on the worker thread with a timeout, bounded
    /// output capture, heartbeat reports, and cancellation-linked kill. Measures the given
    /// paths before and after (streaming, cancellable) so the outcome rests on evidence.
    /// Cancellation returns a <see cref="OwnerToolOutcome.Cancelled"/> result (after killing
    /// the process and re-measuring) instead of throwing, so the caller can credit exactly
    /// what the tool removed before it was killed and then stop the run itself. The only
    /// cancellation exception that escapes is from the before-measure, when cancellation
    /// lands before the command starts.
    /// </summary>
    public static OwnerToolResult Execute(
        OwnerToolSpec spec, OwnerToolCommand command, IReadOnlyList<string> paths,
        CancellationToken ct, TimeSpan timeout, Action<CleanPhase, string>? heartbeat = null)
    {
        var sw = Stopwatch.StartNew();
        long before = MeasureAll(paths, ct);
        heartbeat?.Invoke(CleanPhase.OwnerTool, command.Label);
        var exePath = ResolveExe(command.ExeName);
        if (exePath is null)
            return new OwnerToolResult(spec.ToolName, command.Label, OwnerToolOutcome.NotInstalled,
                null, "", before, before, sw.Elapsed);

        var output = new StringBuilder();
        var outputLock = new object();
        void AppendOutput(string? line)
        {
            if (line is null) return;
            lock (outputLock)
            {
                if (output.Length < MaxOutputChars) output.AppendLine(line);
            }
        }

        Process? process;
        try
        {
            var psi = StartInfo(exePath, command.Arguments);
            process = Process.Start(psi);
            if (process is null)
                return new OwnerToolResult(spec.ToolName, command.Label, OwnerToolOutcome.Failed,
                    null, "process did not start", before, before, sw.Elapsed);
        }
        catch (Exception ex)
        {
            return new OwnerToolResult(spec.ToolName, command.Label, OwnerToolOutcome.Failed,
                null, ex.Message, before, before, sw.Elapsed);
        }

        using (process)
        {
            process.OutputDataReceived += (_, e) => AppendOutput(e.Data);
            process.ErrorDataReceived += (_, e) => AppendOutput(e.Data);
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch { /* output capture is best-effort */ }

            bool timedOut = false, cancelled = false;
            while (!process.WaitForExit(100))
            {
                heartbeat?.Invoke(CleanPhase.OwnerTool, command.Label);
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                if (sw.Elapsed > timeout) { timedOut = true; break; }
            }
            if (cancelled || timedOut)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(5000); } catch { }
            }
            // Drain whatever output arrived; never block long on a dead process.
            try { process.WaitForExit(2000); } catch { }

            string captured;
            lock (outputLock) captured = output.ToString();
            var tail = captured.Length > OutputTailChars ? captured[^OutputTailChars..] : captured;

            if (cancelled)
            {
                // Re-measure without the cancelled token so partial totals stay exact: the
                // tool may have removed part of the cache before it was killed. The caller
                // credits the difference and stops the run; nothing is thrown here.
                long afterCancel = MeasureAll(paths, CancellationToken.None);
                CleanLog.WriteLine($"owner-tool {spec.ToolName} {command.Label}: cancelled after {sw.Elapsed.TotalSeconds:0.0}s, {before} -> {afterCancel} bytes");
                return new OwnerToolResult(spec.ToolName, command.Label, OwnerToolOutcome.Cancelled,
                    null, tail, before, afterCancel, sw.Elapsed);
            }
            if (timedOut)
            {
                long afterTimeout = MeasureAll(paths, CancellationToken.None);
                var timeoutResult = new OwnerToolResult(spec.ToolName, command.Label, OwnerToolOutcome.TimedOut,
                    null, tail, before, afterTimeout, sw.Elapsed);
                CleanLog.WriteLine($"owner-tool {spec.ToolName} {command.Label}: timed out after {timeout.TotalSeconds:0}s, {before} -> {afterTimeout} bytes");
                return timeoutResult;
            }

            int? exitCode = null;
            try { exitCode = process.ExitCode; } catch { }
            // Re-measure without the token: the command already finished, and its removed
            // bytes must be credited even if cancellation landed in this instant — the
            // caller honors cancellation the moment Execute returns.
            long after = MeasureAll(paths, CancellationToken.None);
            var outcome = Classify(exitCode, captured, before, after);
            CleanLog.WriteLine($"owner-tool {spec.ToolName} {command.Label}: {outcome} exit={exitCode} {before} -> {after} bytes in {sw.Elapsed.TotalSeconds:0.0}s");
            return new OwnerToolResult(spec.ToolName, command.Label, outcome, exitCode, tail, before, after, sw.Elapsed);
        }
    }

    /// <summary>
    /// Runs a tool's non-destructive pre-step (Gradle daemon stop) best-effort: short timeout,
    /// failures ignored, cancellation honored (rethrows so the run stops promptly).
    /// </summary>
    public static void RunPreStep(OwnerToolSpec spec, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (spec.PreStep is not { } pre) return;
        try
        {
            // No paths to measure: the pre-step removes nothing, so evidence is exit/output only.
            var result = Execute(spec, pre, [], ct, timeout ?? TimeSpan.FromSeconds(30));
            if (result.Outcome == OwnerToolOutcome.Cancelled) ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort: hand-deletion proceeds regardless */ }
    }

    static ProcessStartInfo StartInfo(string exePath, string arguments)
    {
        // .bat shims (gradle) cannot start directly with UseShellExecute=false; run them
        // through cmd.exe with a fixed command line (no user input anywhere in it).
        if (exePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return new ProcessStartInfo("cmd.exe", $"/c \"{exePath}\" {arguments}")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
        return new ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Env.UserProfile,
        };
    }

    static long MeasureAll(IReadOnlyList<string> paths, CancellationToken ct)
    {
        long total = 0;
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            try { total += FileOps.Measure(p, null, ct).Bytes; } catch (OperationCanceledException) { throw; } catch { }
        }
        return total;
    }
}
