using System.Runtime.InteropServices;
using Microsoft.Win32;
using PurgeWindows.Core;
using PurgeWindows.Core.Environment;

namespace PurgeWindows.Tests;

/// <summary>
/// A fake machine for probe tests: every data-source method is a hook the test configures.
/// Defaults report "unavailable" (the base behavior), so a test only sets up what its
/// scenario needs. Nothing here touches the live machine.
/// </summary>
public sealed class FakeEnvironmentDataSource : EnvironmentDataSource
{
    public Dictionary<(RegistryHive Hive, string Path, string Name, RegistryView View), RegistryRead> RegistryValues { get; } = new();
    public Dictionary<(RegistryHive Hive, string Path, RegistryView View), RegistryKeyRead> RegistryKeys { get; } = new();
    public Func<string, Presence> FileCheck { get; set; } = _ => Presence.Unknown;
    public Func<string, Presence> DirectoryCheck { get; set; } = _ => Presence.Unknown;
    public Func<string, string, FileSearchResult> DirectoryEnumeration { get; set; } = (_, _) => FileSearchResult.Failed();
    public Func<string, string, bool, FileSearchResult> FileEnumeration { get; set; } = (_, _, _) => FileSearchResult.Failed();
    public Func<string, string?> ShortcutTarget { get; set; } = _ => null;
    public PackageQueryResult Packages { get; set; } = PackageQueryResult.Failed();
    public Dictionary<string, ServiceState> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IReadOnlyList<string>?> ServiceNameSearches { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ProcessQueryResult Processes { get; set; } = ProcessQueryResult.Failed();
    public Func<string, string, IReadOnlyList<string>, WmiQueryResult> WmiQuery { get; set; } = (_, _, _) => WmiQueryResult.Failed();
    public Func<int, WscQueryResult> WscQuery { get; set; } = _ => WscQueryResult.Failed();
    public Func<string, string, TimeSpan, CommandResult> CommandRun { get; set; } = (_, _, _) => CommandResult.Failed();
    public SystemInfo SystemInfoValue { get; set; } = SystemInfo.Unknown();
    public DriveData DriveValue { get; set; } = DriveData.Unknown();
    public PowerStatus PowerValue { get; set; } = PowerStatus.Unknown();
    public IReadOnlyList<string> PathDirectoriesValue { get; set; } = [];
    public RegistryRead RegistryValueDefault { get; set; } = RegistryRead.UnreadableValue();
    public RegistryKeyRead RegistryKeyDefault { get; set; } = RegistryKeyRead.UnreadableKey();
    /// <summary>Optional hook consulted before the dictionaries (lets tests count/observe reads).</summary>
    public Func<RegistryHive, string, string, RegistryView, RegistryRead?>? RegistryReadHook { get; set; }
    /// <summary>Optional hook consulted before the service dictionary (lets tests block service queries).</summary>
    public Func<string, ServiceState>? ServiceHook { get; set; }
    public int ClearCachesCalls { get; private set; }
    public List<string> WmiQueriesSeen { get; } = [];

    public void SetValue(RegistryHive hive, string path, string name, object? value, RegistryView view = RegistryView.Default) =>
        RegistryValues[(hive, path, name, view)] = RegistryRead.FoundValue(value);

    public void SetValueMissing(RegistryHive hive, string path, string name, RegistryView view = RegistryView.Default) =>
        RegistryValues[(hive, path, name, view)] = RegistryRead.Missing();

    public void SetKey(RegistryHive hive, string path, IReadOnlyList<string> subKeys, IReadOnlyList<string> valueNames, RegistryView view = RegistryView.Default) =>
        RegistryKeys[(hive, path, view)] = RegistryKeyRead.Present(subKeys, valueNames);

    public void SetKeyMissing(RegistryHive hive, string path, RegistryView view = RegistryView.Default) =>
        RegistryKeys[(hive, path, view)] = RegistryKeyRead.Missing();

    /// <summary>Defaults describing a plain, unmodified machine: every probe comes back Known, verdict Stock.</summary>
    public static FakeEnvironmentDataSource AllKnownDefaults()
    {
        var fake = new FakeEnvironmentDataSource
        {
            FileCheck = path => path.EndsWith("MsMpEng.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("OneDrive.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("SearchIndexer.exe", StringComparison.OrdinalIgnoreCase)
                ? Presence.Present : Presence.Absent,
            DirectoryCheck = _ => Presence.Absent,
            DirectoryEnumeration = (directory, _) => FileSearchResult.Ok([directory + @"\120.0.0.0"]),
            FileEnumeration = (_, _, _) => FileSearchResult.Ok([]),
            Packages = PackageQueryResult.Ok(
                ["Microsoft.WindowsStore_8wekyb3d8bbwe",
                 "MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy",
                 "Microsoft.GamingServices_8wekyb3d8bbwe",
                 "Microsoft.XboxApp_8wekyb3d8bbwe",
                 "Microsoft.YourPhone_8wekyb3d8bbwe",
                 "Microsoft.Windows.Photos_8wekyb3d8bbwe",
                 "MSTeams_8wekyb3d8bbwe",
                 "Microsoft.Copilot_8wekyb3d8bbwe",
                 "Microsoft.Windows.SecHealthUI_cw5n1h2txyewy"],
                []),
            Processes = ProcessQueryResult.Ok([]),
            WmiQuery = (_, _, _) => WmiQueryResult.Ok([]),
            WscQuery = _ => WscQueryResult.Ok([]),
            SystemInfoValue = new SystemInfo(new Version(10, 0, 26100), Architecture.X64, "AMD64", false, []),
            DriveValue = new DriveData(true, 1000, 500, "NTFS"),
            PowerValue = new PowerStatus(false, true),
            RegistryValueDefault = RegistryRead.Missing(),
            RegistryKeyDefault = RegistryKeyRead.Missing(),
        };
        foreach (var name in EnvironmentProbes.ProbedServices) fake.Services[name] = ServiceState.Stopped;
        fake.Services["vmms"] = ServiceState.Stopped;
        // Sense is not installed on this plain machine, so Defender for Endpoint reads as absent.
        fake.Services["Sense"] = ServiceState.NotInstalled;
        fake.Services["IntuneManagementExtension"] = ServiceState.NotInstalled;
        return fake;
    }

    public override RegistryRead ReadRegistryValue(RegistryHive hive, string keyPath, string valueName, RegistryView view = RegistryView.Default) =>
        RegistryReadHook?.Invoke(hive, keyPath, valueName, view)
        ?? (RegistryValues.TryGetValue((hive, keyPath, valueName, view), out var read) ? read : RegistryValueDefault);

    public override RegistryKeyRead ReadRegistryKey(RegistryHive hive, string keyPath, RegistryView view = RegistryView.Default) =>
        RegistryKeys.TryGetValue((hive, keyPath, view), out var read) ? read : RegistryKeyDefault;

    public override Presence CheckFile(string path) => FileCheck(path);
    public override Presence CheckDirectory(string path) => DirectoryCheck(path);
    public override FileSearchResult EnumerateDirectories(string directory, string pattern) => DirectoryEnumeration(directory, pattern);
    public override FileSearchResult EnumerateFiles(string directory, string pattern, bool recursive) => FileEnumeration(directory, pattern, recursive);
    public override string? GetShortcutTarget(string shortcutPath) => ShortcutTarget(shortcutPath);
    public override PackageQueryResult QueryPackages() => Packages;
    public override ServiceState QueryService(string serviceName) =>
        ServiceHook?.Invoke(serviceName)
        ?? (Services.TryGetValue(serviceName, out var state) ? state : ServiceState.Unknown);
    public override IReadOnlyList<string>? FindServiceNames(string prefix) =>
        ServiceNameSearches.TryGetValue(prefix, out var names) ? names : null;
    public override ProcessQueryResult QueryProcesses() => Processes;

    public override WmiQueryResult QueryWmi(string namespaceName, string query, IReadOnlyList<string> properties)
    {
        WmiQueriesSeen.Add(query);
        return WmiQuery(namespaceName, query, properties);
    }

    public override WscQueryResult QueryWscProducts(int provider) => WscQuery(provider);
    public override CommandResult RunCommand(string fileName, string arguments, TimeSpan timeout) => CommandRun(fileName, arguments, timeout);
    public override SystemInfo GetSystemInfo() => SystemInfoValue;
    public override DriveData GetSystemDrive() => DriveValue;
    public override PowerStatus GetPowerStatus() => PowerValue;
    public override IReadOnlyList<string> GetPathDirectories() => PathDirectoriesValue;
    public override void ClearCaches() => ClearCachesCalls++;
}

/// <summary>Builds snapshots for gate/scoring tests: named outcomes are Known, the rest Unknown.</summary>
public static class SnapshotBuilder
{
    static readonly DateTimeOffset Stamp = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public static ProbeOutcome<T> Known<T>(T value) => ProbeOutcome<T>.Known(value, TimeSpan.FromMilliseconds(5), Stamp);
    public static ProbeOutcome<T> Unknown<T>(ProbeUnknownReason reason = ProbeUnknownReason.NotAvailable) =>
        ProbeOutcome<T>.Unknown(reason, TimeSpan.Zero, Stamp);

    public static EnvironmentSnapshot Build(
        OsInfo? os = null,
        ComponentPresence? components = null,
        ModMarkerEvidence? markers = null,
        AntivirusInfo? antivirus = null,
        ServiceStates? services = null,
        UpdateState? updates = null,
        StorageSenseInfo? storageSense = null,
        OneDriveInfo? oneDrive = null,
        ManagedInfo? managed = null,
        VirtualizationInfo? virtualization = null,
        HardwareContext? hardware = null,
        PendingOperations? pending = null,
        bool? isElevated = false) =>
        new(
            os is null ? Unknown<OsInfo>() : Known(os),
            components is null ? Unknown<ComponentPresence>() : Known(components),
            markers is null ? Unknown<ModMarkerEvidence>() : Known(markers),
            antivirus is null ? Unknown<AntivirusInfo>() : Known(antivirus),
            services is null ? Unknown<ServiceStates>() : Known(services),
            updates is null ? Unknown<UpdateState>() : Known(updates),
            storageSense is null ? Unknown<StorageSenseInfo>() : Known(storageSense),
            oneDrive is null ? Unknown<OneDriveInfo>() : Known(oneDrive),
            managed is null ? Unknown<ManagedInfo>() : Known(managed),
            virtualization is null ? Unknown<VirtualizationInfo>() : Known(virtualization),
            hardware is null ? Unknown<HardwareContext>() : Known(hardware),
            pending is null ? Unknown<PendingOperations>() : Known(pending),
            isElevated,
            Stamp);

    public static ComponentPresence AllComponents(Presence presence) => new(
        presence, presence, presence, presence, presence, presence,
        presence, presence, presence, presence, presence, presence);

    public static ModMarkerEvidence NoMarkers() => new(
        false, false, false, true, false, true, null, false, null, null);

    public static ServiceStates AllServices(ServiceState state, params string[] names) =>
        new(names.ToDictionary(n => n, _ => state, StringComparer.OrdinalIgnoreCase));

    public static AntivirusInfo NoAv(AvSource source = AvSource.Wsc) =>
        new([], [], [], source, true, null, null);

    public static AntivirusInfo WithProducts(params AvProduct[] products) =>
        new(products, [], [], AvSource.Wsc, true, null, null);

    public static IReadOnlyDictionary<string, object?> WmiRow(params (string Name, object? Value)[] values)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values) row[name] = value;
        return row;
    }
}

/// <summary>Builds small catalogs for gate tests, including entries for keys the real catalog does not have yet.</summary>
public static class CatalogBuilder
{
    public static CatalogEntry Entry(string key, string kind = "item", string? scannerId = null, SafetyLevel tier = SafetyLevel.CheckFirst) =>
        new(key, key, tier switch
        {
            SafetyLevel.Safe => "safe",
            SafetyLevel.CheckFirst => "medium",
            _ => "unknown",
        }, tier, "test", [], kind, "recycle", [], "verified", "2026-11-18", scannerId);

    public static CategoryCatalog WithEntries(params CatalogEntry[] entries) =>
        new(entries, Loaded: true);

    /// <summary>The real catalog plus entries for the future keys the gate references.</summary>
    public static CategoryCatalog RealPlusFuture()
    {
        var real = CategoryCatalog.Load();
        var entries = real.Entries.ToList();
        foreach (var key in new[] { "edge-cache", "webview2-cache", "onedrive-cache", "onedrive-dehydration", "widgets-cache", "xbox-cache" })
            entries.Add(Entry(key));
        return new CategoryCatalog(entries, Loaded: true);
    }
}
