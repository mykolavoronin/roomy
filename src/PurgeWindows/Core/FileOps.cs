using System.IO.Enumeration;

namespace PurgeWindows.Core;

/// <summary>Measuring and deleting, with guard rails.</summary>
public static class FileOps
{
    public readonly record struct DeleteResult(long Freed, int Files, int Failed);

    static EnumerationOptions Deep() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Never follow junctions/symlinks: they can point anywhere.
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
    /// </summary>
    public static DeleteResult Delete(string path, TimeSpan? minAge, CancellationToken ct = default)
    {
        var cutoff = Cutoff(minAge);
        if (SafeFs.TrustedPath(path) is not { } root) return default;
        Guard(path);
        Guard(root);

        if (File.Exists(root))
        {
            var fi = new FileInfo(root);
            if (fi.LastWriteTimeUtc > cutoff) return default;
            long len = fi.Length;
            return SafeFs.Delete(root, root) ? new(len, 1, 0) : new(0, 0, 1);
        }

        if (!Directory.Exists(root)) return default;

        List<(string, long, DateTime)> files;
        try
        {
            files = new FileSystemEnumerable<(string, long, DateTime)>(root,
                (ref FileSystemEntry x) => (x.ToFullPath(), x.Length, x.LastWriteTimeUtc.UtcDateTime), Deep())
            {
                ShouldIncludePredicate = (ref FileSystemEntry x) => !x.IsDirectory,
            }.ToList();
        }
        catch { return default; }

        long freed = 0;
        int ok = 0, failed = 0;
        foreach (var (p, len, mod) in files)
        {
            ct.ThrowIfCancellationRequested();
            if (mod > cutoff) continue;
            if (SafeFs.Delete(p, root)) { freed += len; ok++; }
            else failed++;
        }

        // Tidy up now-empty subfolders, deepest first. Never removes the root.
        try
        {
            var dirs = new FileSystemEnumerable<string>(root, (ref FileSystemEntry x) => x.ToFullPath(), Deep())
            {
                ShouldIncludePredicate = (ref FileSystemEntry x) => x.IsDirectory,
            }.ToList();
            foreach (var d in dirs.OrderByDescending(d => d.Count(ch => ch == '\\')))
            {
                SafeFs.Delete(d, root); // only succeeds if the folder is empty
            }
        }
        catch { }

        return new(freed, ok, failed);
    }

    /// <summary>Sends a single file to the Recycle Bin, with the same guards as <see cref="Delete"/>.</summary>
    public static DeleteResult Recycle(string path, TimeSpan? minAge)
    {
        if (SafeFs.TrustedPath(path) is not { } real || !File.Exists(real)) return default;
        Guard(path);
        Guard(real);
        var fi = new FileInfo(real);
        if (fi.LastWriteTimeUtc > Cutoff(minAge)) return default;
        long len = fi.Length;
        return Native.SendToRecycleBin(real) && !File.Exists(real) ? new(len, 1, 0) : new(0, 0, 1);
    }

    /// <summary>
    /// Sends a file, or the contents of a folder (the folder itself stays), to the Recycle Bin.
    /// Same guards as <see cref="Delete"/>: nothing is reached through a junction or symlink,
    /// protected locations are refused, and files that are in use are skipped, never forced.
    /// Files go to the bin in batches through one shell call each; a file only counts as
    /// recycled once it is actually gone. Now-empty subfolders are tidied up afterwards.
    /// </summary>
    public static DeleteResult RecycleContents(string path, TimeSpan? minAge, CancellationToken ct = default)
    {
        var cutoff = Cutoff(minAge);
        if (SafeFs.TrustedPath(path) is not { } root) return default;
        Guard(path);
        Guard(root);

        if (File.Exists(root)) return Recycle(root, minAge);
        if (!Directory.Exists(root)) return default;

        List<(string, long, DateTime)> files;
        try
        {
            files = new FileSystemEnumerable<(string, long, DateTime)>(root,
                (ref FileSystemEntry x) => (x.ToFullPath(), x.Length, x.LastWriteTimeUtc.UtcDateTime), Deep())
            {
                ShouldIncludePredicate = (ref FileSystemEntry x) => !x.IsDirectory,
            }.ToList();
        }
        catch { return default; }

        // Only files that really live under the checked root, and are old enough, go to the bin.
        var candidates = new List<(string Path, long Len)>();
        foreach (var (p, len, mod) in files)
        {
            ct.ThrowIfCancellationRequested();
            if (mod > cutoff) continue;
            var real = SafeFs.TrustedPath(p);
            if (real is null || !SafeFs.IsInside(real, root)) continue;
            candidates.Add((real, len));
        }

        long freed = 0;
        int ok = 0, failed = 0;
        const int batch = 200;
        for (int i = 0; i < candidates.Count; i += batch)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = candidates.Skip(i).Take(batch).ToList();
            Native.SendFilesToRecycleBin(chunk.Select(c => c.Path).ToList());
            foreach (var (p, len) in chunk)
            {
                if (!File.Exists(p)) { freed += len; ok++; }
                else failed++;
            }
        }

        // Tidy up now-empty subfolders, deepest first. Never removes the root.
        try
        {
            var dirs = new FileSystemEnumerable<string>(root, (ref FileSystemEntry x) => x.ToFullPath(), Deep())
            {
                ShouldIncludePredicate = (ref FileSystemEntry x) => x.IsDirectory,
            }.ToList();
            foreach (var d in dirs.OrderByDescending(d => d.Count(ch => ch == '\\')))
            {
                SafeFs.Delete(d, root); // only succeeds if the folder is empty
            }
        }
        catch { }

        return new(freed, ok, failed);
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
