using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PurgeWindows.Core.Environment;

/// <summary>
/// One registry value read. <see cref="Found"/> distinguishes "the value exists" from "the key
/// opened but the value is absent" (meaningful: no policy set) and <see cref="Unreadable"/>
/// (the key could not be opened at all — access denied or worse — which is never evidence).
/// </summary>
public sealed record RegistryRead(bool Found, object? Value, bool Unreadable)
{
    public static RegistryRead FoundValue(object? value) => new(true, value, false);
    public static RegistryRead Missing() => new(false, null, false);
    public static RegistryRead UnreadableValue() => new(false, null, true);
}

/// <summary>One registry key read: existence plus subkey/value names. Names only — values under
/// sensitive keys (enrollment data) are never read, only counted.</summary>
public sealed record RegistryKeyRead(bool Exists, bool Unreadable, IReadOnlyList<string> SubKeyNames, IReadOnlyList<string> ValueNames)
{
    public static RegistryKeyRead Present(IReadOnlyList<string> subKeys, IReadOnlyList<string> values) => new(true, false, subKeys, values);
    public static RegistryKeyRead Missing() => new(false, false, [], []);
    public static RegistryKeyRead UnreadableKey() => new(false, true, [], []);
}

/// <summary>A directory/file enumeration: success plus matches. Failure is distinct from "no matches".</summary>
public sealed record FileSearchResult(bool Succeeded, IReadOnlyList<string> Matches)
{
    public static FileSearchResult Ok(IReadOnlyList<string> matches) => new(true, matches);
    public static FileSearchResult Failed() => new(false, []);
}

/// <summary>Installed AppX/MSIX packages for the current user only (never other users' packages).</summary>
public sealed record PackageQueryResult(bool Succeeded, IReadOnlyList<string> FamilyNames, IReadOnlyList<string> FullNames)
{
    public static PackageQueryResult Ok(IReadOnlyList<string> families, IReadOnlyList<string> fullNames) => new(true, families, fullNames);
    public static PackageQueryResult Failed() => new(false, [], []);
}

/// <summary>Running process names (no arguments, no owners — names only, for activity signals).</summary>
public sealed record ProcessQueryResult(bool Succeeded, IReadOnlyList<string> ProcessNames)
{
    public static ProcessQueryResult Ok(IReadOnlyList<string> names) => new(true, names);
    public static ProcessQueryResult Failed() => new(false, []);
}

/// <summary>One WMI query's rows: property name → value (null when the property was absent).</summary>
public sealed record WmiQueryResult(bool Succeeded, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)
{
    public static WmiQueryResult Ok(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) => new(true, rows);
    public static WmiQueryResult Failed() => new(false, []);
}

/// <summary>One Windows Security Center product row (raw state/signature integers; decoding lives in <see cref="AntivirusState"/>).</summary>
public sealed record WscProductRow(string Name, int State, int SignatureStatus);

/// <summary>A Windows Security Center enumeration: success plus products. Empty + success means "none registered".</summary>
public sealed record WscQueryResult(bool Succeeded, IReadOnlyList<WscProductRow> Products)
{
    public static WscQueryResult Ok(IReadOnlyList<WscProductRow> products) => new(true, products);
    public static WscQueryResult Failed() => new(false, []);
}

/// <summary>A read-only command's outcome. Only fixed, read-only commands are ever run (wsl --list).</summary>
public sealed record CommandResult(bool Started, int ExitCode, string Stdout)
{
    public static CommandResult Ran(int exitCode, string stdout) => new(true, exitCode, stdout);
    public static CommandResult Failed() => new(false, -1, "");
}

/// <summary>Core system facts: OS version, architecture, elevation, and folder locations probes need.</summary>
public sealed record SystemInfo(
    Version? OsVersion,
    Architecture? OsArchitecture,
    string? ProcessorArchitectureEnv,
    bool? IsElevated,
    IReadOnlyList<string> DesktopFolders)
{
    public static SystemInfo Unknown() => new(null, null, null, null, []);
}

/// <summary>System-drive capacity facts (for the low-disk rule, W5 E-26).</summary>
public sealed record DriveData(bool IsReady, long TotalBytes, long FreeBytes, string? DriveFormat)
{
    public static DriveData Unknown() => new(false, 0, 0, null);

    public double? FreeFraction => IsReady && TotalBytes > 0 ? (double)FreeBytes / TotalBytes : null;
}

/// <summary>Power facts: on battery, and whether a battery exists at all (W5 P-11).</summary>
public sealed record PowerStatus(bool? OnBattery, bool? BatteryPresent)
{
    public static PowerStatus Unknown() => new(null, null);
}

/// <summary>
/// The single seam between the environment probes and the machine. Every method is read-only:
/// registry reads, file existence, package/service/process enumeration, WMI/WSC queries, one
/// fixed read-only command, and system facts. Nothing here writes, elevates, or touches the
/// network. The default implementations report "unavailable" so test fakes only override
/// what their scenario needs; production uses <see cref="WindowsEnvironmentDataSource"/>.
/// </summary>
public abstract class EnvironmentDataSource
{
    /// <summary>Reads one registry value. Never opens a key writable; never creates anything.</summary>
    public virtual RegistryRead ReadRegistryValue(RegistryHive hive, string keyPath, string valueName, RegistryView view = RegistryView.Default) =>
        RegistryRead.UnreadableValue();

    /// <summary>Reads one registry key's existence and child names. Never opens a key writable.</summary>
    public virtual RegistryKeyRead ReadRegistryKey(RegistryHive hive, string keyPath, RegistryView view = RegistryView.Default) =>
        RegistryKeyRead.UnreadableKey();

    /// <summary>Whether a file exists. Unknown when the check itself could not run (e.g. access denied).</summary>
    public virtual Presence CheckFile(string path) => Presence.Unknown;

    /// <summary>Whether a directory exists. Unknown when the check itself could not run.</summary>
    public virtual Presence CheckDirectory(string path) => Presence.Unknown;

    /// <summary>Directories under <paramref name="directory"/> matching a wildcard pattern.</summary>
    public virtual FileSearchResult EnumerateDirectories(string directory, string pattern) => FileSearchResult.Failed();

    /// <summary>Files under <paramref name="directory"/> matching a wildcard pattern.</summary>
    public virtual FileSearchResult EnumerateFiles(string directory, string pattern, bool recursive) => FileSearchResult.Failed();

    /// <summary>A shortcut's target path, or null when it cannot be read. The shortcut is never modified.</summary>
    public virtual string? GetShortcutTarget(string shortcutPath) => null;

    /// <summary>AppX/MSIX packages installed for the current user.</summary>
    public virtual PackageQueryResult QueryPackages() => PackageQueryResult.Failed();

    /// <summary>One service's state from the Service Control Manager (read-only query).</summary>
    public virtual ServiceState QueryService(string serviceName) => ServiceState.Unknown;

    /// <summary>Service names starting with <paramref name="prefix"/> (per-user service instances). Null when unlistable.</summary>
    public virtual IReadOnlyList<string>? FindServiceNames(string prefix) => null;

    /// <summary>Names of running processes.</summary>
    public virtual ProcessQueryResult QueryProcesses() => ProcessQueryResult.Failed();

    /// <summary>One WMI query. Production goes through <see cref="WmiClient"/> (worker thread, retries, cache).</summary>
    public virtual WmiQueryResult QueryWmi(string namespaceName, string query, IReadOnlyList<string> properties) => WmiQueryResult.Failed();

    /// <summary>One Windows Security Center enumeration for a provider (antivirus/firewall). Enumeration only — never registration.</summary>
    public virtual WscQueryResult QueryWscProducts(int provider) => WscQueryResult.Failed();

    /// <summary>Runs a fixed read-only command and captures its output. Production allows only wsl.exe.</summary>
    public virtual CommandResult RunCommand(string fileName, string arguments, TimeSpan timeout) => CommandResult.Failed();

    /// <summary>OS version, architecture, elevation, and desktop folders.</summary>
    public virtual SystemInfo GetSystemInfo() => SystemInfo.Unknown();

    /// <summary>System-drive capacity and filesystem.</summary>
    public virtual DriveData GetSystemDrive() => DriveData.Unknown();

    /// <summary>AC/battery state.</summary>
    public virtual PowerStatus GetPowerStatus() => PowerStatus.Unknown();

    /// <summary>Directories on PATH, for executable discovery.</summary>
    public virtual IReadOnlyList<string> GetPathDirectories() => [];

    /// <summary>Drops any cached query results (a forced re-check starts fresh).</summary>
    public virtual void ClearCaches() { }
}
