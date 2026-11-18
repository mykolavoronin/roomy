using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace PurgeWindows.Core.Environment;

/// <summary>
/// The production data source: every probe input, read from the live machine. Read-only
/// throughout — registry keys are opened non-writable, services are queried (never controlled),
/// the only command ever run is <c>wsl.exe --list --quiet</c>, and nothing touches the network.
/// WMI goes through <see cref="WmiClient"/> (worker thread, retries, cache); Security Center
/// through <see cref="WscProductSource"/> (enumeration only).
/// </summary>
public sealed class WindowsEnvironmentDataSource : EnvironmentDataSource
{
    private readonly WmiClient _wmi = new();

    public override RegistryRead ReadRegistryValue(RegistryHive hive, string keyPath, string valueName, RegistryView view = RegistryView.Default)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath, writable: false);
            if (key is null) return RegistryRead.Missing();
            var value = key.GetValue(valueName);
            return value is null ? RegistryRead.Missing() : RegistryRead.FoundValue(value);
        }
        catch (UnauthorizedAccessException)
        {
            return RegistryRead.UnreadableValue();
        }
        catch (System.Security.SecurityException)
        {
            return RegistryRead.UnreadableValue();
        }
        catch
        {
            return RegistryRead.UnreadableValue();
        }
    }

    public override RegistryKeyRead ReadRegistryKey(RegistryHive hive, string keyPath, RegistryView view = RegistryView.Default)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath, writable: false);
            if (key is null) return RegistryKeyRead.Missing();
            return RegistryKeyRead.Present(key.GetSubKeyNames(), key.GetValueNames());
        }
        catch (UnauthorizedAccessException)
        {
            return RegistryKeyRead.UnreadableKey();
        }
        catch (System.Security.SecurityException)
        {
            return RegistryKeyRead.UnreadableKey();
        }
        catch
        {
            return RegistryKeyRead.UnreadableKey();
        }
    }

    public override Presence CheckFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) ? Presence.Absent : Presence.Present;
        }
        catch (FileNotFoundException) { return Presence.Absent; }
        catch (DirectoryNotFoundException) { return Presence.Absent; }
        catch { return Presence.Unknown; }
    }

    public override Presence CheckDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) ? Presence.Present : Presence.Absent;
        }
        catch (FileNotFoundException) { return Presence.Absent; }
        catch (DirectoryNotFoundException) { return Presence.Absent; }
        catch { return Presence.Unknown; }
    }

    public override FileSearchResult EnumerateDirectories(string directory, string pattern)
    {
        try
        {
            return FileSearchResult.Ok(Directory.EnumerateDirectories(directory, pattern).ToList());
        }
        catch
        {
            return FileSearchResult.Failed();
        }
    }

    public override FileSearchResult EnumerateFiles(string directory, string pattern, bool recursive)
    {
        try
        {
            return FileSearchResult.Ok(Directory.EnumerateFiles(
                directory, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList());
        }
        catch
        {
            return FileSearchResult.Failed();
        }
    }

    public override string? GetShortcutTarget(string shortcutPath)
    {
        // ShellLink COM, read-only: resolve the target without touching the shortcut.
        object? link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            var shell = Activator.CreateInstance(type);
            if (shell is null) return null;
            link = ComHelper.Call(shell, "CreateShortcut", shortcutPath);
            ComHelper.Release(shell);
            if (link is null) return null;
            return ComHelper.Get(link, "TargetPath") as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            ComHelper.Release(link);
        }
    }

    public override PackageQueryResult QueryPackages()
    {
        try
        {
            // Current user only: FindPackages() with no arguments enumerates the calling user's packages.
            var manager = new PackageManager();
            var families = new List<string>();
            var fullNames = new List<string>();
            foreach (var package in manager.FindPackages())
            {
                families.Add(package.Id.FamilyName);
                fullNames.Add(package.Id.FullName);
            }
            return PackageQueryResult.Ok(families, fullNames);
        }
        catch
        {
            return PackageQueryResult.Failed();
        }
    }

    public override ServiceState QueryService(string serviceName) =>
        EnvironmentNative.QueryServiceState(serviceName);

    public override IReadOnlyList<string>? FindServiceNames(string prefix)
    {
        // Per-user service instances (WpnUserService_*, OneSyncSvc_*) live under the Services key.
        // Subkey names only — no values are read.
        var read = ReadRegistryKey(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services");
        if (read.Unreadable || !read.Exists) return null;
        return read.SubKeyNames.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public override ProcessQueryResult QueryProcesses()
    {
        try
        {
            var names = new List<string>();
            foreach (var process in Process.GetProcesses())
            {
                try { names.Add(process.ProcessName); }
                catch { /* A process that exits mid-enumeration is skipped. */ }
                finally { process.Dispose(); }
            }
            return ProcessQueryResult.Ok(names);
        }
        catch
        {
            return ProcessQueryResult.Failed();
        }
    }

    public override WmiQueryResult QueryWmi(string namespaceName, string query, IReadOnlyList<string> properties) =>
        _wmi.Query(namespaceName, query, properties);

    public override WscQueryResult QueryWscProducts(int provider) =>
        WscProductSource.Query(provider);

    public override CommandResult RunCommand(string fileName, string arguments, TimeSpan timeout)
    {
        // Only one command is ever run: the read-only WSL distro listing. Anything else is refused.
        if (!fileName.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) ||
            !arguments.Equals("--list --quiet", StringComparison.Ordinal))
            return CommandResult.Failed();

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!process.Start()) return CommandResult.Failed();
            // wsl.exe writes UTF-16LE when redirected, so read raw bytes and decode by BOM
            // (UTF-16LE with BOM, UTF-8 with BOM, otherwise UTF-8) instead of trusting one encoding.
            var outputTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, timeout);
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* Already gone. */ }
                return CommandResult.Failed();
            }
            var bytes = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return CommandResult.Ran(process.ExitCode, DecodeCommandOutput(bytes));
        }
        catch
        {
            return CommandResult.Failed();
        }
    }

    static async Task<byte[]> ReadAllBytesAsync(Stream stream, TimeSpan timeout)
    {
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            var deadline = DateTimeOffset.UtcNow + timeout;
            while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (DateTimeOffset.UtcNow > deadline) break;
            }
            return buffer.ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Decodes redirected command output: BOM first, then a UTF-16LE heuristic for
    /// BOM-less output (ASCII text in UTF-16LE has zero high bytes), otherwise UTF-8.</summary>
    internal static string DecodeCommandOutput(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (LooksLikeUtf16Le(bytes))
            return System.Text.Encoding.Unicode.GetString(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    static bool LooksLikeUtf16Le(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0) return false;
        var checkedBytes = Math.Min(bytes.Length / 2, 32);
        var zeroHighBytes = 0;
        for (var i = 0; i < checkedBytes; i++)
        {
            if (bytes[i * 2 + 1] == 0) zeroHighBytes++;
        }
        return zeroHighBytes >= checkedBytes * 4 / 5;
    }

    public override SystemInfo GetSystemInfo()
    {
        try
        {
            var desktops = new List<string>();
            try { desktops.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
            try { desktops.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
            return new SystemInfo(
                Environment.OSVersion.Version,
                RuntimeInformation.OSArchitecture,
                Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"),
                Admin.IsElevated,
                desktops.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch
        {
            return SystemInfo.Unknown();
        }
    }

    public override DriveData GetSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var drive = new DriveInfo(root);
            return drive.IsReady
                ? new DriveData(true, drive.TotalSize, drive.AvailableFreeSpace, drive.DriveFormat)
                : DriveData.Unknown();
        }
        catch
        {
            return DriveData.Unknown();
        }
    }

    public override PowerStatus GetPowerStatus() => EnvironmentNative.GetPowerStatus();

    public override IReadOnlyList<string> GetPathDirectories()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return [];
            return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch
        {
            return [];
        }
    }

    public override void ClearCaches() => _wmi.ClearCache();
}
