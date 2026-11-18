using System.Text;

namespace PurgeWindows.Core;

/// <summary>
/// Bounded structured log of cleaning runs, at %LOCALAPPDATA%\PurgeWindows\clean.log, so a
/// freeze-and-kill leaves a last-known phase, path, and batch. Same discipline as
/// <see cref="CrashLog"/>: the file stays on this PC, nothing is sent anywhere, writing
/// never throws and never blocks the worker for long (one small append per event, with
/// rotation that keeps the newest half). One line per event, sequenced and timestamped.
/// </summary>
public static class CleanLog
{
    internal const long MaxBytes = 256 * 1024;
    static readonly object Gate = new();

    public static string FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PurgeWindows", "clean.log");

    /// <summary>Appends one sequenced clean event. Never throws.</summary>
    public static void WriteEvent(CleanEvent ev)
    {
        var sb = new StringBuilder()
            .Append('#').Append(ev.Sequence)
            .Append(" [").Append(ev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" +")
            .Append(ev.Elapsed.TotalSeconds.ToString("0.000")).Append("s] ")
            .Append(ev.Kind);
        if (ev.CategoryId.Length > 0) sb.Append(' ').Append(ev.CategoryId);
        sb.Append(" phase=").Append(ev.Phase);
        if (ev.CurrentPath.Length > 0) sb.Append(" path=").Append(ev.CurrentPath);
        sb.Append(" attempted=").Append(ev.BytesAttemptedSoFar).Append('/').Append(ev.TotalBytes)
            .Append(" removed=").Append(ev.BytesRemovedSoFar)
            .Append(" files=").Append(ev.FilesRemoved).Append('/').Append(ev.FilesAttempted)
            .Append(" frac=").Append(ev.Fraction.ToString("0.000"));
        if (ev.Reason is { } reason) sb.Append(" reason=").Append(reason);
        if (ev.ShellReturnCode is { } code) sb.Append(" shell=").Append(code);
        if (ev.ShellAborted == true) sb.Append(" shellAborted");
        if (ev.Detail.Length > 0) sb.Append(" :: ").Append(ev.Detail);
        Append(sb.ToString());
    }

    /// <summary>Appends one shell-batch outcome (C7: return codes are recorded, never discarded).</summary>
    public static void WriteShellBatch(int returnCode, bool aborted, int files, long bytes, string firstPath)
    {
        Append($"shell batch rc={returnCode} aborted={aborted} files={files} bytes={bytes} first={firstPath}");
    }

    /// <summary>Appends one watchdog silence record (C8).</summary>
    public static void WriteWatchdog(string level, TimeSpan silence, CleanPhase phase, string lastPath)
    {
        Append($"watchdog {level} silence={silence.TotalSeconds:0.0}s phase={phase} lastPath={lastPath}");
    }

    /// <summary>Appends one free-form line (owner-tool outcomes, pre-flight results). Never throws.</summary>
    public static void WriteLine(string line) => Append(line);

    static void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    // Keep the newest half so the file never grows without limit.
                    var old = File.ReadAllText(FilePath);
                    File.WriteAllText(FilePath, old[(old.Length / 2)..]);
                }
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
            }
        }
        catch { /* logging must never make cleaning worse */ }
    }
}
