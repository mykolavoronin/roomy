using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PurgeWindows.Core;

/// <summary>
/// Outcome of one shell file operation. The return code and the aborted flag are first-class
/// data (C7): callers record them in skip/failure reasons and the clean log, never discard
/// them. <see cref="Succeeded"/> is true only when the shell reported success and did not abort.
/// </summary>
public sealed record ShellOperationResult(int ReturnCode, bool Aborted)
{
    public bool Succeeded => ReturnCode == 0 && !Aborted;
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint bufferLength);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    const uint FO_DELETE = 3;
    // No-UI flags only. The nuke-warning flag is deliberately ABSENT: it partially
    // overrides FOF_NOCONFIRMATION and makes Windows show a modal "delete permanently?"
    // dialog when a file cannot be recycled — an ownerless dialog that blocked the
    // cleaning worker forever (C1). Files that cannot be recycled must never reach
    // this call: the pre-flight check and RecycleBinBudget skip them with a recorded
    // reason before any shell call.
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
        FOF_NOCONFIRMMKDIR = 0x200, FOF_NOERRORUI = 0x400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>
    /// Moves one file to the Recycle Bin. Never shows UI and never deletes permanently:
    /// a file that cannot be recycled fails (see the return code) instead of prompting.
    /// The caller decides what a failure means and records it; nothing is retried as a
    /// permanent delete.
    /// </summary>
    public static ShellOperationResult SendToRecycleBin(string path, IntPtr ownerHwnd = default) =>
        SendFilesToRecycleBin([path], ownerHwnd);

    /// <summary>
    /// Moves several files to the Recycle Bin in one shell call, with no UI of any kind.
    /// The call is synchronous and cannot be cancelled while outstanding; callers keep
    /// batches capped (count + bytes) and let the watchdog observe from another thread.
    /// <para/>
    /// FOLLOW-UP (not implemented here): <c>IFileOperation</c> on a dedicated STA thread
    /// with a message pump, a progress sink, and cancellation would give per-item progress
    /// and true cancellation of the shell operation itself. It was not implemented in this
    /// wave because the COM interop (IFileOperation, IShellItem, IFileOperationProgressSink
    /// GUIDs and vtable layouts) cannot be verified without compiling and running on
    /// Windows, and wrong COM interop is worse than this hardened SHFileOperation path:
    /// SHFileOperation from MTA pool threads is the documented pattern, and with no-UI
    /// flags + an owner window + pre-checked recyclability + honored return codes, the
    /// blocking-dialog class is eliminated. A later wave should implement IFileOperation
    /// with a Windows compile + the P1-5 fixture suite as its gate.
    /// </summary>
    public static ShellOperationResult SendFilesToRecycleBin(IReadOnlyList<string> paths, IntPtr ownerHwnd = default)
    {
        if (paths.Count == 0) return new ShellOperationResult(0, false);
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = ownerHwnd != IntPtr.Zero ? ownerHwnd : ShellOwnerHandle(),
                wFunc = FO_DELETE,
                pFrom = string.Join('\0', paths) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR,
            };
            var rc = SHFileOperation(ref op);
            return new ShellOperationResult(rc, op.fAnyOperationsAborted);
        }
        catch
        {
            // The shell call itself failed (e.g. no shell on this system). -1 is not a
            // real shell return code; it marks "the call never ran" for the clean log.
            return new ShellOperationResult(-1, false);
        }
    }

    /// <summary>
    /// The main window's handle, so any shell UI that could ever appear has an owner.
    /// Resolved lazily from the process (cleaning only ever runs with a window showing);
    /// IntPtr.Zero when there is no main window (tests, headless runs).
    /// </summary>
    static IntPtr ShellOwnerHandle()
    {
        try { return Process.GetCurrentProcess().MainWindowHandle; }
        catch { return IntPtr.Zero; }
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Size and item count of the Recycle Bin across all drives.</summary>
    public static (long Size, long Items) QueryRecycleBin() => QueryRecycleBin(null);

    /// <summary>Size and item count of the Recycle Bin for one drive root (e.g. "C:\").</summary>
    public static (long Size, long Items) QueryRecycleBin(string? rootPath)
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(rootPath, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// The volume name (e.g. <c>\\?\Volume{...}\</c>) for a drive root, or null when it
    /// cannot be resolved. Used to find the drive's Recycle Bin settings in the registry.
    /// </summary>
    public static string? TryGetVolumeName(string driveRoot)
    {
        try
        {
            var sb = new StringBuilder(64);
            if (GetVolumeNameForVolumeMountPointW(driveRoot, sb, (uint)sb.Capacity))
            {
                var name = sb.ToString();
                return name.Length > 0 ? name : null;
            }
        }
        catch { }
        return null;
    }
}
