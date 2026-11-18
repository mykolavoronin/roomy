using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PurgeWindows.Core;

/// <summary>
/// Handle-based file system checks that can't be fooled by junctions or symlinks.
/// A path is only trusted if Windows says the file really lives at that path, and deletes go
/// through the same open handle that was checked, so swapping in a junction afterwards has no effect.
/// <para/>
/// WINDOWS-ONLY TODO (P1-1 benchmark): measure the per-file validation cost on a
/// 100,000-file tree before changing anything further here. Exact procedure, on
/// Windows: (1) build a tree of 100,000 files across 1,000 folders, 10 KiB each,
/// under a temp folder; (2) time <c>FileOps.RecycleContents</c> over it with a
/// counting progress sink and no UI, from a Release build; (3) time
/// <c>FileOps.Delete</c> over an identical fresh tree; (4) record wall time,
/// files/second, and the <see cref="TrustedPathFast"/> vs <see cref="TrustedPath"/>
/// split by timing a run that calls <see cref="TrustedPath"/> directly.
/// Report the numbers in the commit message; never claim them in a comment here.
/// </summary>
internal static class SafeFs
{
    const uint DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100;
    const uint SHARE_ALL = 0x1 | 0x2 | 0x4;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    const int FileDispositionInfo = 4, FileDispositionInfoEx = 21;
    const uint FILE_DISPOSITION_FLAG_DELETE = 0x1, FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2,
        FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref uint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, uint size);

    /// <summary>Opens the entry itself (never a link's target) for checking and deleting.</summary>
    static SafeFileHandle Open(string path, bool forDelete) => CreateFileW(@"\\?\" + path,
        FILE_READ_ATTRIBUTES | (forDelete ? DELETE | FILE_WRITE_ATTRIBUTES : 0), SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

    /// <summary>Where Windows says this handle really points, as a normal path.</summary>
    static string? FinalPath(SafeFileHandle h)
    {
        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0) return null;
        if (n > sb.Capacity)
        {
            sb = new StringBuilder((int)n + 1);
            n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (n == 0 || n > sb.Capacity) return null;
        }
        var s = sb.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// <summary>The full, long-name form of a path (expands 8.3 names like PC-USE~1).</summary>
    static string? LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        var sb = new StringBuilder(1024);
        uint n = GetLongPathNameW(@"\\?\" + full, sb, (uint)sb.Capacity);
        if (n == 0 || n > sb.Capacity) return null;
        var s = sb.ToString();
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    static string Trim(string p) => p.Length > 3 ? p.TrimEnd('\\') : p;

    /// <summary>
    /// The real location of <paramref name="path"/>, or null if any part of the path goes through
    /// a junction, symlink or mount point (or it doesn't exist). The entry itself must not be a link either.
    /// </summary>
    public static string? TrustedPath(string path)
    {
        try
        {
            var expected = LongPath(path);
            if (expected is null) return null;
            using var h = Open(expected, forDelete: false);
            if (h.IsInvalid) return null;
            var real = FinalPath(h);
            if (real is null || !string.Equals(Trim(real), Trim(expected), StringComparison.OrdinalIgnoreCase)) return null;
            return (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0 ? null : Trim(real);
        }
        catch { return null; }
    }

    /// <summary>
    /// P1-1 per-file fast path, for paths produced by enumeration under an
    /// already-trusted root. Enumeration yields long-form paths, so the
    /// <c>GetLongPathNameW</c> call <see cref="TrustedPath"/> makes per file is
    /// skipped — saving one syscall per file — unless any segment could be an
    /// 8.3 short name (any '~' in the path), in which case the full check runs.
    /// Junction safety is NOT weakened: the handle is still opened with
    /// OPEN_REPARSE_POINT, the final path is still compared, and reparse entries
    /// are still rejected. Only callers holding enumeration-produced paths may
    /// use this; arbitrary user input always goes through <see cref="TrustedPath"/>.
    /// </summary>
    public static string? TrustedPathFast(string path)
    {
        if (path.IndexOf('~') >= 0) return TrustedPath(path);
        try
        {
            var expected = Path.GetFullPath(path);
            using var h = Open(expected, forDelete: false);
            if (h.IsInvalid) return null;
            var real = FinalPath(h);
            if (real is null || !string.Equals(Trim(real), Trim(expected), StringComparison.OrdinalIgnoreCase)) return null;
            return (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0 ? null : Trim(real);
        }
        catch { return null; }
    }

    /// <summary>True when <paramref name="real"/> is <paramref name="root"/> or somewhere inside it.</summary>
    public static bool IsInside(string real, string root) =>
        string.Equals(real, root, StringComparison.OrdinalIgnoreCase) ||
        real.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes one file or empty folder, but only if it really lives inside <paramref name="trustedRoot"/>.
    /// The check and the delete use the same handle, so the path can't be redirected in between.
    /// A link is deleted itself, never its target. Read-only files are deleted too.
    /// </summary>
    public static bool Delete(string path, string trustedRoot)
    {
        try
        {
            using var h = Open(Path.GetFullPath(path), forDelete: true);
            if (h.IsInvalid) return false;
            var real = FinalPath(h);
            if (real is null || !IsInside(Trim(real), trustedRoot)) return false;

            uint flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
            if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref flags, sizeof(uint))) return true;

            // Older file systems (FAT, some network drives) only know the classic call.
            byte delete = 1;
            return SetFileInformationByHandle(h, FileDispositionInfo, ref delete, 1);
        }
        catch { return false; }
    }
}
