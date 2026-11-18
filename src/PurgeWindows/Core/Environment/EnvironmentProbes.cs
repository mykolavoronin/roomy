using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PurgeWindows.Core.Environment;

/// <summary>
/// The 12 environment probes (W5 §5.2). Every probe is read-only, runs off the UI thread
/// (the runner owns threading and timeouts), and returns a <see cref="ProbeOutcome{T}"/>:
/// a value, or Unknown with a reason. A probe is Known when it could check its signals;
/// individual fields inside a known result may still be null (that one reading failed).
/// Unknown never hides a category — gating acts only on positive evidence.
///
/// Method deviations from the W5 table, each deliberate and documented where it occurs:
/// Edge presence is judged by the executable alone (the EdgeUpdate Clients key cannot tell
/// Edge apart from WebView2); update-in-progress is judged by setup processes running
/// (wuauserv runs constantly, so its Running state alone means nothing); three S4/S5-adjacent
/// readings the plan lists without a verifiable source are never asserted (see P-03).
/// </summary>
public static class EnvironmentProbes
{
    // ------------------------------------------------------------------ shared helpers

    static string ProgramFiles => Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
    static string ProgramFilesX86 => Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string SystemRoot => Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

    static int? RegistryInt(object? value) => value switch
    {
        null => null,
        int i => i,
        long l => (int)l,
        uint u => (int)u,
        string s => int.TryParse(s.Trim(), out var parsed) ? parsed : null,
        _ => null,
    };

    static string? RegistryString(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => value.ToString(),
    };

    /// <summary>Parses a pause-expiry value: an ISO date string, or a Windows FileTime integer.</summary>
    internal static DateTimeOffset? ParsePauseExpiry(object? value) => value switch
    {
        null => null,
        string s when DateTimeOffset.TryParse(s, out var parsed) => parsed,
        long fileTime => TryFileTime(fileTime),
        int fileTime32 => TryFileTime(fileTime32),
        _ => null,
    };

    static DateTimeOffset? TryFileTime(long fileTime)
    {
        try { return DateTimeOffset.FromFileTime(fileTime); }
        catch { return null; }
    }

    /// <summary>
    /// Computes pause state from parsed end-times. No values at all means not paused; values
    /// present but unparseable means unknown; otherwise the latest end-time decides.
    /// </summary>
    internal static (DateTimeOffset? Expiry, bool? IsPaused) ComputePauseState(
        IReadOnlyList<DateTimeOffset?> endTimes, bool valuesPresent, DateTimeOffset now)
    {
        var parsed = endTimes.Where(t => t is not null).Select(t => t!.Value).ToList();
        if (parsed.Count == 0) return (null, valuesPresent ? null : false);
        var expiry = parsed.Max();
        return (expiry, expiry > now);
    }

    /// <summary>Parses <c>wsl.exe --list --quiet</c> output into distro names, tolerating stray NULs and blank lines.</summary>
    internal static IReadOnlyList<string> ParseWslOutput(string stdout) =>
        stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Replace("\0", "", StringComparison.Ordinal).Trim())
            .Where(line => line.Length > 0)
            .ToList();

    /// <summary>
    /// Decodes Win32_DeviceGuard. VirtualizationBasedSecurityStatus 2 means running (0/1 mean
    /// off); CodeIntegrityPolicyEnforcementStatus 1 means enforced (0 means off). Anything
    /// else is Unknown. These value mappings need live verification on Windows.
    /// </summary>
    internal static (bool? Vbs, bool? Hvci) DecodeDeviceGuard(int? vbsStatus, int? hvciStatus) =>
        (vbsStatus switch { 2 => true, 0 or 1 => false, _ => null },
         hvciStatus switch { 1 => true, 0 => false, _ => null });

    static readonly HashSet<int> LaptopChassisTypes = [8, 9, 10, 11, 12, 14, 30, 31, 32];

    /// <summary>True when any chassis type is a portable form factor; false when types are known and none is; null when unknown.</summary>
    internal static bool? IsLaptopChassis(IReadOnlyList<int> chassisTypes)
    {
        if (chassisTypes.Count == 0) return null;
        return chassisTypes.Any(t => LaptopChassisTypes.Contains(t));
    }

    /// <summary>MSFT_PhysicalDisk MediaType: 4 is SSD, 3 is HDD. Only meaningful for a single-disk machine (the caller enforces that).</summary>
    internal static DriveMedia MediaTypeToDriveMedia(int? mediaType) => mediaType switch
    {
        4 => DriveMedia.Ssd,
        3 => DriveMedia.Hdd,
        _ => DriveMedia.Unknown,
    };

    internal static bool MatchesToolName(string? displayName, string? publisher, params string[] needles) =>
        needles.Any(needle =>
            (displayName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (publisher?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));

    static bool ProcessNameMatches(IReadOnlyList<string> names, params string[] wanted) =>
        names.Any(name => wanted.Contains(name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Searches versioned install roots (e.g. Edge's per-version folders) for an executable.
    /// At most 16 subdirectories are checked, newest names first. Present when found; Absent
    /// when the enumeration worked and nothing matched; Unknown when the enumeration failed.
    /// </summary>
    static Presence FindVersionedExe(EnvironmentDataSource source, string versionsRoot, string fileName, ref bool anySignal)
    {
        var enumeration = source.EnumerateDirectories(versionsRoot, "*");
        if (!enumeration.Succeeded) return Presence.Unknown;
        anySignal = true;
        var anyUnknown = false;
        var subdirs = enumeration.Matches
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Take(16);
        foreach (var subdir in subdirs)
        {
            var check = source.CheckFile(Path.Combine(subdir, fileName));
            if (check == Presence.Present) return Presence.Present;
            if (check == Presence.Unknown) anyUnknown = true;
        }
        return anyUnknown ? Presence.Unknown : Presence.Absent;
    }

    static Presence CheckFileTracked(EnvironmentDataSource source, string path, ref bool anySignal)
    {
        var result = source.CheckFile(path);
        if (result != Presence.Unknown) anySignal = true;
        return result;
    }

    static Presence CheckDirectoryTracked(EnvironmentDataSource source, string path, ref bool anySignal)
    {
        var result = source.CheckDirectory(path);
        if (result != Presence.Unknown) anySignal = true;
        return result;
    }

    static RegistryRead ReadValueTracked(EnvironmentDataSource source, RegistryHive hive, string keyPath, string valueName, ref bool anySignal, RegistryView view = RegistryView.Default)
    {
        var result = source.ReadRegistryValue(hive, keyPath, valueName, view);
        if (!result.Unreadable) anySignal = true;
        return result;
    }

    static RegistryKeyRead ReadKeyTracked(EnvironmentDataSource source, RegistryHive hive, string keyPath, ref bool anySignal, RegistryView view = RegistryView.Default)
    {
        var result = source.ReadRegistryKey(hive, keyPath, view);
        if (!result.Unreadable) anySignal = true;
        return result;
    }

    /// <summary>Tri-state OR over "is this reboot signal set": true wins, then null, then false.</summary>
    static bool? PendingRebootSignal(EnvironmentDataSource source, ref bool anySignal)
    {
        var cbs = ReadKeyTracked(source, RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", ref anySignal);
        var wu = ReadKeyTracked(source, RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", ref anySignal);
        var rename = ReadValueTracked(source, RegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations", ref anySignal);
        return PresenceRules.Any(
            cbs.Unreadable ? null : cbs.Exists,
            wu.Unreadable ? null : wu.Exists,
            rename.Unreadable ? null : rename.Found && RenameOperationsPending(rename.Value));
    }

    /// <summary>True when the pending-rename value actually lists operations: an existing but
    /// empty value means nothing is pending. An unrecognized shape stays conservative.</summary>
    static bool RenameOperationsPending(object? value) => value switch
    {
        string[] lines => lines.Any(line => !string.IsNullOrWhiteSpace(line)),
        string text => !string.IsNullOrWhiteSpace(text),
        _ => true,
    };

    // ------------------------------------------------------------------ P-01 OS

    public static ProbeOutcome<OsInfo> ProbeOs(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            var anySignal = false;
            var productName = ReadValueTracked(source, RegistryHive.LocalMachine, key, "ProductName", ref anySignal);
            var editionId = ReadValueTracked(source, RegistryHive.LocalMachine, key, "EditionID", ref anySignal);
            var displayVersion = ReadValueTracked(source, RegistryHive.LocalMachine, key, "DisplayVersion", ref anySignal);
            var currentBuild = ReadValueTracked(source, RegistryHive.LocalMachine, key, "CurrentBuild", ref anySignal);
            var ubr = ReadValueTracked(source, RegistryHive.LocalMachine, key, "UBR", ref anySignal);
            var buildLabEx = ReadValueTracked(source, RegistryHive.LocalMachine, key, "BuildLabEx", ref anySignal);
            var selfHost = ReadKeyTracked(source, RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WindowsSelfHost", ref anySignal);

            var systemInfo = source.GetSystemInfo();
            var build = ParseBuildNumber(RegistryString(currentBuild.Value));
            var ubrValue = RegistryInt(ubr.Value);
            // Fall back to Environment.OSVersion when the registry build is unreadable: the
            // version is still honest OS data, just from the other source.
            if (build is null && systemInfo.OsVersion is { } version)
            {
                build = version.Build;
                anySignal = true;
            }

            if (!anySignal && build is null)
                return ProbeOutcome<OsInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);

            var info = new OsInfo(
                RegistryString(productName.Value),
                RegistryString(editionId.Value),
                RegistryString(displayVersion.Value),
                build,
                ubrValue,
                RegistryString(buildLabEx.Value),
                ParseArchitecture(systemInfo.ProcessorArchitectureEnv, systemInfo.OsArchitecture),
                selfHost.Unreadable ? OsChannel.Unknown : selfHost.Exists ? OsChannel.Preview : OsChannel.Release);
            return ProbeOutcome<OsInfo>.Known(info, sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<OsInfo>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<OsInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    internal static int? ParseBuildNumber(string? text) =>
        text is not null && int.TryParse(text.Trim(), out var build) ? build : null;

    /// <summary>PROCESSOR_ARCHITECTURE wins when it parses; otherwise the runtime's OS architecture.</summary>
    internal static Architecture? ParseArchitecture(string? processorArchitectureEnv, Architecture? osArchitecture) =>
        processorArchitectureEnv?.Trim().ToUpperInvariant() switch
        {
            "ARM64" => Architecture.Arm64,
            "AMD64" => Architecture.X64,
            "X86" => Architecture.X86,
            _ => osArchitecture,
        };

    // ------------------------------------------------------------------ P-02 components

    public static ProbeOutcome<ComponentPresence> ProbeComponents(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var anySignal = false;

            var packages = source.QueryPackages();
            if (packages.Succeeded) anySignal = true;
            Presence PackagePresence(params string[] familyPrefixes)
            {
                if (!packages.Succeeded) return Presence.Unknown;
                return packages.FamilyNames.Any(f => familyPrefixes.Any(p =>
                    f.StartsWith(p, StringComparison.OrdinalIgnoreCase))) ? Presence.Present : Presence.Absent;
            }
            // A package signal that can only prove presence, never absence (the package is the
            // UI for the component, not the component itself).
            Presence PackagePresentOnly(params string[] familyPrefixes) =>
                PackagePresence(familyPrefixes) == Presence.Present ? Presence.Present : Presence.Unknown;

            // Defender: service + binaries + Security Health package (present-only). The WMI
            // SecurityCenter2 half of the W5 table is P-04's job; P-02 must not depend on it.
            var winDefend = source.QueryService("WinDefend");
            if (winDefend != ServiceState.Unknown) anySignal = true;
            var defenderService = winDefend switch
            {
                ServiceState.NotInstalled => Presence.Absent,
                ServiceState.Unknown => Presence.Unknown,
                _ => Presence.Present,
            };
            var defenderExe = PresenceRules.Combine(
                FindVersionedExe(source, Path.Combine(ProgramFiles, @"Windows Defender\Platform"), "MsMpEng.exe", ref anySignal),
                CheckFileTracked(source, Path.Combine(ProgramFiles, @"Windows Defender\MsMpEng.exe"), ref anySignal));
            // The package signal participates only when it proves presence: its absence must
            // neither prove nor veto Defender's absence (it is the UI, not the component).
            var defenderSignals = new List<Presence> { defenderService, defenderExe };
            var secHealthPackage = PackagePresentOnly("Microsoft.Windows.SecHealthUI");
            if (secHealthPackage == Presence.Present) defenderSignals.Add(secHealthPackage);
            var defender = PresenceRules.Combine([.. defenderSignals]);

            // Edge: executable only. The EdgeUpdate Clients key cannot tell Edge apart from
            // WebView2 (both register clients there), so it is not used as an Edge signal.
            var edge = PresenceRules.Combine(
                FindVersionedExe(source, Path.Combine(ProgramFilesX86, @"Microsoft\Edge\Application"), "msedge.exe", ref anySignal),
                FindVersionedExe(source, Path.Combine(ProgramFiles, @"Microsoft\Edge\Application"), "msedge.exe", ref anySignal),
                FindVersionedExe(source, Path.Combine(LocalAppData, @"Microsoft\Edge\Application"), "msedge.exe", ref anySignal));

            // WebView2: the versioned runtime executable plus the runtime folder itself.
            var webView2 = PresenceRules.Combine(
                FindVersionedExe(source, Path.Combine(ProgramFilesX86, @"Microsoft\EdgeWebView\Application"), "msedgewebview2.exe", ref anySignal),
                FindVersionedExe(source, Path.Combine(ProgramFiles, @"Microsoft\EdgeWebView\Application"), "msedgewebview2.exe", ref anySignal),
                FindVersionedExe(source, Path.Combine(LocalAppData, @"Microsoft\EdgeWebView\Application"), "msedgewebview2.exe", ref anySignal),
                CheckDirectoryTracked(source, Path.Combine(ProgramFilesX86, @"Microsoft\EdgeWebView\Application"), ref anySignal),
                CheckDirectoryTracked(source, Path.Combine(ProgramFiles, @"Microsoft\EdgeWebView\Application"), ref anySignal));

            var store = PackagePresence("Microsoft.WindowsStore");

            var oneDriveKey = ReadKeyTracked(source, RegistryHive.CurrentUser, @"Software\SyncEngines\Providers\OneDrive", ref anySignal);
            var oneDriveRegistry = oneDriveKey.Unreadable ? Presence.Unknown
                : oneDriveKey.SubKeyNames.Count > 0 ? Presence.Present : Presence.Absent;
            var oneDrive = PresenceRules.Combine(
                CheckFileTracked(source, Path.Combine(ProgramFiles, @"Microsoft OneDrive\OneDrive.exe"), ref anySignal),
                CheckFileTracked(source, Path.Combine(LocalAppData, @"Microsoft\OneDrive\OneDrive.exe"), ref anySignal),
                oneDriveRegistry);

            var widgets = PackagePresence("MicrosoftWindows.Client.WebExperience");
            var xbox = PresenceRules.Combine(
                PackagePresence("Microsoft.GamingServices"),
                PackagePresence("Microsoft.XboxApp"));

            var searchService = source.QueryService("WSearch");
            if (searchService != ServiceState.Unknown) anySignal = true;
            var search = PresenceRules.Combine(
                searchService switch
                {
                    ServiceState.NotInstalled => Presence.Absent,
                    ServiceState.Unknown => Presence.Unknown,
                    _ => Presence.Present,
                },
                CheckFileTracked(source, Path.Combine(SystemRoot, @"System32\SearchIndexer.exe"), ref anySignal));

            var phoneLink = PackagePresence("Microsoft.YourPhone");
            var recallCopilot = PackagePresence("Microsoft.Copilot", "Microsoft.Windows.Copilot", "Microsoft.Windows.AI");
            var teams = PackagePresence("Microsoft.Teams", "MSTeams");
            var photos = PackagePresence("Microsoft.Windows.Photos");

            if (!anySignal)
                return ProbeOutcome<ComponentPresence>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);

            return ProbeOutcome<ComponentPresence>.Known(new ComponentPresence(
                defender, edge, webView2, store, oneDrive, widgets, xbox, search,
                phoneLink, recallCopilot, teams, photos), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<ComponentPresence>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<ComponentPresence>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    // ------------------------------------------------------------------ P-03 mod markers

    public static ProbeOutcome<ModMarkerEvidence> ProbeModMarkers(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var anySignal = false;

            var (revisionTool, ameWizard, debloatTool) = ScanModToolEvidence(source, ref anySignal, ct);

            var edgeBlock = ReadValueTracked(source, RegistryHive.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\EdgeUpdate", "DoNotUpdateToEdgeWithChromium", ref anySignal);
            // A missing policy value means the policy was never set (not enabled); only an
            // unreadable key is unknown.
            bool? edgeBlockEnabled = edgeBlock.Unreadable ? null : RegistryInt(edgeBlock.Value) == 1;

            var smartScreen = ReadValueTracked(source, RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled", ref anySignal);
            bool? smartScreenEnabled = smartScreen.Unreadable ? null
                : !smartScreen.Found ? true // Missing key: the Windows default is on.
                : RegistryString(smartScreen.Value) switch
                {
                    "Off" => false,
                    _ => true,
                };

            var lsa = ReadValueTracked(source, RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags", ref anySignal);
            bool? credentialGuard = lsa.Unreadable ? null
                : !lsa.Found ? false // Never configured means off.
                : RegistryInt(lsa.Value) is 1 or 2;

            var runKey = ReadKeyTracked(source, RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ref anySignal);
            bool? securityHealthStartup = runKey.Unreadable ? null
                : runKey.ValueNames.Contains("SecurityHealth", StringComparer.OrdinalIgnoreCase);

            if (!anySignal)
                return ProbeOutcome<ModMarkerEvidence>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);

            return ProbeOutcome<ModMarkerEvidence>.Known(new ModMarkerEvidence(
                revisionTool,
                ameWizard,
                edgeBlockEnabled,
                smartScreenEnabled,
                credentialGuard,
                securityHealthStartup,
                // No plan-specified source exists for the tray-icon state, the deprovisioned
                // count has no verified stock baseline, and the data-usage detector is
                // UNVERIFIED (W5 §5.3) — these are never asserted, so the markers that need
                // them stay unobserved rather than guessed.
                SecurityHealthTrayHidden: null,
                DebloatToolFound: debloatTool,
                DeprovisionedCount: null,
                DataUsageBroken: null), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<ModMarkerEvidence>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<ModMarkerEvidence>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    /// <summary>
    /// Revision Tool / AME Wizard / debloat-tool evidence: file discovery (Revision folders,
    /// PATH, desktop shortcuts) plus uninstall records. Returns nulls when nothing could be
    /// checked at all; otherwise false means "checked, not found".
    /// </summary>
    static (bool? RevisionTool, bool? AmeWizard, bool? DebloatTool) ScanModToolEvidence(
        EnvironmentDataSource source, ref bool anySignal, CancellationToken ct)
    {
        var checkedAny = false;
        var revisionFound = false;

        // revitool.exe in %ProgramFiles%\Revision* / %ProgramFiles(x86)%\Revision*.
        foreach (var root in new[] { ProgramFiles, ProgramFilesX86 })
        {
            ct.ThrowIfCancellationRequested();
            var dirs = source.EnumerateDirectories(root, "Revision*");
            if (!dirs.Succeeded) continue;
            anySignal = true;
            checkedAny = true;
            foreach (var dir in dirs.Matches)
            {
                if (CheckFileTracked(source, Path.Combine(dir, "revitool.exe"), ref anySignal) == Presence.Present)
                    revisionFound = true;
            }
        }

        // revitool.exe on PATH.
        foreach (var dir in source.GetPathDirectories())
        {
            var check = CheckFileTracked(source, Path.Combine(dir, "revitool.exe"), ref anySignal);
            if (check == Presence.Present)
            {
                revisionFound = true;
                checkedAny = true;
            }
            else if (check != Presence.Unknown)
            {
                checkedAny = true;
            }
        }

        // Desktop shortcuts pointing at revitool.exe.
        foreach (var desktop in source.GetSystemInfo().DesktopFolders)
        {
            ct.ThrowIfCancellationRequested();
            var links = source.EnumerateFiles(desktop, "*.lnk", recursive: false);
            if (!links.Succeeded) continue;
            anySignal = true;
            checkedAny = true;
            foreach (var link in links.Matches)
            {
                var target = source.GetShortcutTarget(link);
                if (target?.EndsWith("revitool.exe", StringComparison.OrdinalIgnoreCase) == true)
                    revisionFound = true;
            }
        }

        // Uninstall records (64-bit view, 32-bit view, current user).
        var (uninstallRevision, uninstallAme, uninstallDebloat, uninstallReadable) =
            ScanUninstallRecords(source, ref anySignal, ct);
        if (uninstallRevision == true) revisionFound = true;

        var revisionTool = revisionFound ? true : checkedAny || uninstallReadable ? false : (bool?)null;
        bool? ameWizard = uninstallAme == true ? true : uninstallReadable ? false : null;
        bool? debloatTool = uninstallDebloat == true ? true : uninstallReadable ? false : null;
        return (revisionTool, ameWizard, debloatTool);
    }

    static (bool? RevisionTool, bool? AmeWizard, bool? DebloatTool, bool Readable) ScanUninstallRecords(
        EnvironmentDataSource source, ref bool anySignal, CancellationToken ct)
    {
        const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var readable = false;
        var revision = false;
        var ame = false;
        var debloat = false;
        var scanned = 0;
        const int maxSubkeys = 1000;

        var views = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        };
        foreach (var (hive, view) in views)
        {
            ct.ThrowIfCancellationRequested();
            var key = ReadKeyTracked(source, hive, uninstallKey, ref anySignal, view);
            if (key.Unreadable || !key.Exists) continue;
            readable = true;
            foreach (var subkey in key.SubKeyNames)
            {
                if (scanned++ >= maxSubkeys) break;
                var subPath = uninstallKey + "\\" + subkey;
                var displayName = ReadValueTracked(source, hive, subPath, "DisplayName", ref anySignal, view);
                var publisher = ReadValueTracked(source, hive, subPath, "Publisher", ref anySignal, view);
                var name = RegistryString(displayName.Value);
                var pub = RegistryString(publisher.Value);
                if (MatchesToolName(name, pub, "Revision Tool")) revision = true;
                if (MatchesToolName(name, pub, "AME Wizard")) ame = true;
                if (MatchesToolName(name, pub, "ShutUp10", "W10Privacy", "Chris Titus")) debloat = true;
            }
        }
        return (revision ? true : readable ? false : null,
                ame ? true : readable ? false : null,
                debloat ? true : readable ? false : null,
                readable);
    }

    // ------------------------------------------------------------------ P-04 antivirus

    public static ProbeOutcome<AntivirusInfo> ProbeAntivirus(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();

            // Primary: Windows Security Center enumeration. A successful empty result means
            // "none registered" — it is a known result, never a reason to fall back.
            var wscAv = source.QueryWscProducts(WscProductSource.ProviderAntivirus);
            IReadOnlyList<AvProduct> products;
            IReadOnlyList<AvProduct> firewallProducts;
            IReadOnlyList<AvProduct> antiSpywareProducts;
            AvSource avSource;
            bool firewallChecked;

            if (wscAv.Succeeded)
            {
                avSource = AvSource.Wsc;
                products = MapWscRows(wscAv.Products);
                var wscFw = source.QueryWscProducts(WscProductSource.ProviderFirewall);
                firewallChecked = wscFw.Succeeded;
                firewallProducts = wscFw.Succeeded ? MapWscRows(wscFw.Products) : [];
                var wscSpy = source.QueryWscProducts(WscProductSource.ProviderAntiSpyware);
                antiSpywareProducts = wscSpy.Succeeded ? MapWscRows(wscSpy.Products) : [];
            }
            else
            {
                // Fallback: WMI root\SecurityCenter2.
                var wmiAv = source.QueryWmi("root\\SecurityCenter2",
                    "SELECT displayName, productState, pathToSignedProductExe FROM AntiVirusProduct",
                    ["displayName", "productState", "pathToSignedProductExe"]);
                if (!wmiAv.Succeeded)
                    return ProbeOutcome<AntivirusInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);

                avSource = AvSource.Wmi;
                products = MapWmiRows(wmiAv.Rows);
                var wmiFw = source.QueryWmi("root\\SecurityCenter2",
                    "SELECT displayName, productState, pathToSignedProductExe FROM FirewallProduct",
                    ["displayName", "productState", "pathToSignedProductExe"]);
                firewallChecked = wmiFw.Succeeded;
                firewallProducts = wmiFw.Succeeded ? MapWmiRows(wmiFw.Rows) : [];
                var wmiSpy = source.QueryWmi("root\\SecurityCenter2",
                    "SELECT displayName, productState, pathToSignedProductExe FROM AntiSpywareProduct",
                    ["displayName", "productState", "pathToSignedProductExe"]);
                antiSpywareProducts = wmiSpy.Succeeded ? MapWmiRows(wmiSpy.Rows) : [];
            }

            // Defender detail, best-effort: never affects whether the probe is Known.
            bool? realTime = null;
            foreach (var ns in new[] { @"root\Microsoft\ProtectionManagement", @"root\Microsoft\Windows\Defender" })
            {
                var status = source.QueryWmi(ns,
                    "SELECT RealTimeProtectionEnabled FROM MSFT_MpComputerStatus",
                    ["RealTimeProtectionEnabled"]);
                if (status.Succeeded && status.Rows.Count > 0)
                {
                    realTime = WmiValue.AsBool(RowValue(status.Rows[0], "RealTimeProtectionEnabled"));
                    break;
                }
            }
            // Tamper Protection: registry value, admin-gated — unreadable means unknown, never a prompt.
            var tamper = source.ReadRegistryValue(RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Windows Defender\Features", "TamperProtection");
            bool? tamperEnabled = tamper.Unreadable || !tamper.Found ? null : RegistryInt(tamper.Value) != 0;

            return ProbeOutcome<AntivirusInfo>.Known(new AntivirusInfo(
                products, firewallProducts, antiSpywareProducts, avSource, firewallChecked,
                realTime, tamperEnabled), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<AntivirusInfo>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<AntivirusInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    static IReadOnlyList<AvProduct> MapWscRows(IReadOnlyList<WscProductRow> rows) =>
        rows.Select(row =>
        {
            var (state, signature) = AntivirusState.FromWsc(row.State, row.SignatureStatus);
            return new AvProduct(row.Name, state, signature,
                AntivirusState.IsDefenderProduct(row.Name, null), null);
        }).ToList();

    static IReadOnlyList<AvProduct> MapWmiRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        rows.Select(row =>
        {
            var name = WmiValue.AsString(RowValue(row, "displayName")) ?? "";
            var exe = WmiValue.AsString(RowValue(row, "pathToSignedProductExe"));
            var state = WmiValue.AsUInt(RowValue(row, "productState")) is { } productState
                ? AntivirusState.DecodeProductState(productState)
                : AvProductState.Unknown;
            return new AvProduct(name, state, AvSignatureStatus.Unknown,
                AntivirusState.IsDefenderProduct(name, exe), exe);
        }).ToList();

    static object? RowValue(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) ? value : null;

    // ------------------------------------------------------------------ P-05 services

    /// <summary>The P-05 service subset (W5), plus DiagTrack and WerSvc, which mod scoring needs.</summary>
    public static readonly IReadOnlyList<string> ProbedServices =
    [
        "wuauserv", "BITS", "DoSvc", "WinDefend", "Sense", "Spooler", "FontCache", "WSearch",
        "SysMain", "DiagTrack", "WerSvc",
    ];

    public static ProbeOutcome<ServiceStates> ProbeServices(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var states = new Dictionary<string, ServiceState>(StringComparer.OrdinalIgnoreCase);
            var anySignal = false;

            foreach (var name in ProbedServices)
            {
                var state = source.QueryService(name);
                if (state != ServiceState.Unknown) anySignal = true;
                states[name] = state;
            }
            // Per-user service instances (WpnUserService_*, OneSyncSvc_*).
            foreach (var prefix in new[] { "WpnUserService_", "OneSyncSvc_" })
            {
                var instances = source.FindServiceNames(prefix);
                if (instances is null) continue;
                anySignal = true;
                foreach (var instance in instances.Take(16))
                {
                    var state = source.QueryService(instance);
                    if (state != ServiceState.Unknown) anySignal = true;
                    states[instance] = state;
                }
            }

            if (!anySignal)
                return ProbeOutcome<ServiceStates>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
            return ProbeOutcome<ServiceStates>.Known(new ServiceStates(states), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<ServiceStates>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<ServiceStates>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    // ------------------------------------------------------------------ P-06 updates

    public static ProbeOutcome<UpdateState> ProbeUpdates(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var anySignal = false;
            const string uxKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

            // Pause value names vary by build; read defensively and parse what parses.
            var endTimes = new List<DateTimeOffset?>();
            var valuesPresent = false;
            foreach (var valueName in new[]
                     {
                         "PauseUpdatesExpiryTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime",
                         "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime", "PauseUpdatesStartTime",
                     })
            {
                var read = ReadValueTracked(source, RegistryHive.LocalMachine, uxKey, valueName, ref anySignal);
                if (read.Found)
                {
                    valuesPresent = true;
                    if (valueName.EndsWith("EndTime", StringComparison.Ordinal) ||
                        valueName.EndsWith("ExpiryTime", StringComparison.Ordinal))
                        endTimes.Add(ParsePauseExpiry(read.Value));
                }
            }
            var (pauseExpiry, isPaused) = ComputePauseState(endTimes, valuesPresent, DateTimeOffset.UtcNow);

            var pendingReboot = PendingRebootSignal(source, ref anySignal);

            // In progress: a setup process is running. wuauserv runs constantly, so its state
            // alone cannot mean an update is in progress; the setup processes are the signal.
            var processes = source.QueryProcesses();
            bool? inProgress = processes.Succeeded
                ? ProcessNameMatches(processes.ProcessNames, "SetupHost", "SetupPrep", "TiWorker")
                : null;
            if (processes.Succeeded) anySignal = true;

            if (!anySignal)
                return ProbeOutcome<UpdateState>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
            return ProbeOutcome<UpdateState>.Known(new UpdateState(
                pauseExpiry, isPaused, valuesPresent, pendingReboot, inProgress), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<UpdateState>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<UpdateState>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    // ------------------------------------------------------------------ P-07 Storage Sense

    public static ProbeOutcome<StorageSenseInfo> ProbeStorageSense(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var anySignal = false;
            const string policyKey = @"SOFTWARE\Policies\Microsoft\Windows\StorageSense";

            StorageSenseState? policyState = null;
            foreach (var valueName in new[] { "AllowStorageSenseGlobal", "ConfigStorageSenseGlobal" })
            {
                var read = ReadValueTracked(source, RegistryHive.LocalMachine, policyKey, valueName, ref anySignal);
                if (read.Found && RegistryInt(read.Value) is { } number)
                    policyState = number == 1 ? StorageSenseState.On : StorageSenseState.Off;
            }

            // The per-user "01" value is an undocumented hint, not a documented setting: it
            // only ever decides when no policy says anything.
            var hint = ReadValueTracked(source, RegistryHive.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", ref anySignal);
            StorageSenseState? hintState = hint.Found && RegistryInt(hint.Value) is { } hintNumber
                ? hintNumber == 1 ? StorageSenseState.On : StorageSenseState.Off
                : null;

            if (!anySignal)
                return ProbeOutcome<StorageSenseInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
            return ProbeOutcome<StorageSenseInfo>.Known(
                new StorageSenseInfo(policyState ?? hintState ?? StorageSenseState.Unknown), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<StorageSenseInfo>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<StorageSenseInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    // ------------------------------------------------------------------ P-08 OneDrive

    public static ProbeOutcome<OneDriveInfo> ProbeOneDrive(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var anySignal = false;

            var providers = ReadKeyTracked(source, RegistryHive.CurrentUser,
                @"Software\SyncEngines\Providers\OneDrive", ref anySignal);
            int? providerCount = providers.Unreadable ? null : providers.SubKeyNames.Count;

            var present = PresenceRules.Combine(
                CheckFileTracked(source, Path.Combine(ProgramFiles, @"Microsoft OneDrive\OneDrive.exe"), ref anySignal),
                CheckFileTracked(source, Path.Combine(LocalAppData, @"Microsoft\OneDrive\OneDrive.exe"), ref anySignal),
                providers.Unreadable ? Presence.Unknown
                    : providers.SubKeyNames.Count > 0 ? Presence.Present : Presence.Absent);

            // Files On-Demand only from the policy value; anything else would be a guess.
            var fod = ReadValueTracked(source, RegistryHive.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\OneDrive", "FilesOnDemandEnabled", ref anySignal);
            bool? filesOnDemand = fod.Unreadable || !fod.Found ? null : RegistryInt(fod.Value) == 1;

            if (!anySignal)
                return ProbeOutcome<OneDriveInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
            return ProbeOutcome<OneDriveInfo>.Known(new OneDriveInfo(
                providerCount, present, filesOnDemand,
                // Placeholder counting needs a file scan, which no probe does.
                HasPlaceholders: null), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<OneDriveInfo>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<OneDriveInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    // ------------------------------------------------------------------ P-09 managed

    public static ProbeOutcome<ManagedInfo> ProbeManaged(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var anySignal = false;

            bool? domainJoined = null;
            var computerSystem = source.QueryWmi("root\\CIMV2",
                "SELECT PartOfDomain, DomainRole FROM Win32_ComputerSystem",
                ["PartOfDomain", "DomainRole"]);
            if (computerSystem.Succeeded)
            {
                anySignal = true;
                if (computerSystem.Rows.Count > 0)
                {
                    var row = computerSystem.Rows[0];
                    domainJoined = WmiValue.AsBool(RowValue(row, "PartOfDomain"))
                        ?? (WmiValue.AsInt(RowValue(row, "DomainRole")) switch
                        {
                            1 or 3 or 4 or 5 => true,
                            0 or 2 => false,
                            _ => null,
                        });
                }
            }

            // MDM: subkey presence only. Enrollment values are secrets and are never read.
            var enrollments = ReadKeyTracked(source, RegistryHive.LocalMachine,
                @"SOFTWARE\Microsoft\Enrollments", ref anySignal);
            bool? mdmEnrolled = enrollments.Unreadable ? null : enrollments.SubKeyNames.Count > 0;

            var sense = source.QueryService("Sense");
            if (sense != ServiceState.Unknown) anySignal = true;
            bool? sensePresent = sense switch
            {
                ServiceState.NotInstalled => false,
                ServiceState.Unknown => null,
                _ => true,
            };
            var msSenseExe = CheckFileTracked(source,
                Path.Combine(ProgramFiles, @"Windows Defender Advanced Threat Protection\MsSense.exe"), ref anySignal);
            bool? defenderForEndpoint = PresenceRules.Any(
                sensePresent, msSenseExe == Presence.Present ? true : msSenseExe == Presence.Absent ? false : null);

            var intune = source.QueryService("IntuneManagementExtension");
            if (intune != ServiceState.Unknown) anySignal = true;
            bool? intuneExtension = intune switch
            {
                ServiceState.NotInstalled => false,
                ServiceState.Unknown => null,
                _ => true,
            };

            if (!anySignal)
                return ProbeOutcome<ManagedInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
            return ProbeOutcome<ManagedInfo>.Known(new ManagedInfo(
                domainJoined, mdmEnrolled, defenderForEndpoint, intuneExtension), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<ManagedInfo>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<ManagedInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    // ------------------------------------------------------------------ P-10 virtualization

    public static ProbeOutcome<VirtualizationInfo> ProbeVirtualization(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var anySignal = false;

            int? hyperVInstallState = null;
            var optionalFeature = source.QueryWmi("root\\CIMV2",
                "SELECT Name, InstallState FROM Win32_OptionalFeature WHERE Name = 'Microsoft-Hyper-V-All'",
                ["Name", "InstallState"]);
            if (optionalFeature.Succeeded)
            {
                anySignal = true;
                if (optionalFeature.Rows.Count > 0)
                    hyperVInstallState = WmiValue.AsInt(RowValue(optionalFeature.Rows[0], "InstallState"));
            }
            var vmms = source.QueryService("vmms");
            if (vmms != ServiceState.Unknown) anySignal = true;
            var hyperV = hyperVInstallState == 1 || vmms == ServiceState.Running ? HyperVState.Enabled
                : hyperVInstallState is not null && hyperVInstallState != 1 && vmms == ServiceState.NotInstalled
                    ? HyperVState.Absent
                    : HyperVState.Unknown;

            // WSL: the executable, the distro listing, and the Lxss registry, in that order.
            var wslExe = CheckFileTracked(source, Path.Combine(SystemRoot, @"System32\wsl.exe"), ref anySignal);
            var lxss = ReadKeyTracked(source, RegistryHive.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss", ref anySignal);
            var lxssDistros = lxss.Unreadable ? null : lxss.SubKeyNames.Count > 0;

            IReadOnlyList<string> distros = [];
            WslState wsl;
            if (wslExe == Presence.Absent && lxssDistros != true)
            {
                wsl = WslState.Absent;
            }
            else
            {
                var listing = source.RunCommand("wsl.exe", "--list --quiet", TimeSpan.FromSeconds(8));
                if (listing.Started && listing.ExitCode == 0)
                {
                    anySignal = true;
                    distros = ParseWslOutput(listing.Stdout);
                    wsl = distros.Count > 0 ? WslState.Present
                        : lxssDistros == true ? WslState.Present
                        : lxss.Unreadable ? WslState.Unknown
                        : WslState.Absent;
                }
                else if (lxssDistros == true)
                {
                    wsl = WslState.Present;
                }
                else if (wslExe == Presence.Absent)
                {
                    wsl = WslState.Absent;
                }
                else
                {
                    wsl = WslState.Unknown;
                }
            }

            bool? vbs = null;
            bool? hvci = null;
            var deviceGuard = source.QueryWmi("root\\CIMV2",
                "SELECT VirtualizationBasedSecurityStatus, CodeIntegrityPolicyEnforcementStatus FROM Win32_DeviceGuard",
                ["VirtualizationBasedSecurityStatus", "CodeIntegrityPolicyEnforcementStatus"]);
            if (deviceGuard.Succeeded)
            {
                anySignal = true;
                if (deviceGuard.Rows.Count > 0)
                {
                    var row = deviceGuard.Rows[0];
                    (vbs, hvci) = DecodeDeviceGuard(
                        WmiValue.AsInt(RowValue(row, "VirtualizationBasedSecurityStatus")),
                        WmiValue.AsInt(RowValue(row, "CodeIntegrityPolicyEnforcementStatus")));
                }
            }

            if (!anySignal)
                return ProbeOutcome<VirtualizationInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
            return ProbeOutcome<VirtualizationInfo>.Known(new VirtualizationInfo(
                hyperV, wsl, distros, vbs, hvci), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<VirtualizationInfo>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<VirtualizationInfo>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    // ------------------------------------------------------------------ P-11 hardware

    public static ProbeOutcome<HardwareContext> ProbeHardware(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var anySignal = false;

            var chassisTypes = new List<int>();
            var enclosure = source.QueryWmi("root\\CIMV2",
                "SELECT ChassisTypes FROM Win32_SystemEnclosure", ["ChassisTypes"]);
            if (enclosure.Succeeded)
            {
                anySignal = true;
                foreach (var row in enclosure.Rows)
                    chassisTypes.AddRange(WmiValue.AsIntList(RowValue(row, "ChassisTypes")));
            }

            long? totalRam = null;
            var computerSystem = source.QueryWmi("root\\CIMV2",
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", ["TotalPhysicalMemory"]);
            if (computerSystem.Succeeded)
            {
                anySignal = true;
                if (computerSystem.Rows.Count > 0)
                    totalRam = WmiValue.AsLong(RowValue(computerSystem.Rows[0], "TotalPhysicalMemory"));
            }

            // Media type only when exactly one physical disk exists; with several disks, no
            // single "the disk type" is honest.
            var media = DriveMedia.Unknown;
            var disks = source.QueryWmi(@"root\Microsoft\Windows\Storage",
                "SELECT MediaType FROM MSFT_PhysicalDisk", ["MediaType"]);
            if (disks.Succeeded)
            {
                anySignal = true;
                if (disks.Rows.Count == 1)
                    media = MediaTypeToDriveMedia(WmiValue.AsInt(RowValue(disks.Rows[0], "MediaType")));
            }

            bool? batteryFromWmi = null;
            var battery = source.QueryWmi("root\\CIMV2", "SELECT BatteryStatus FROM Win32_Battery", ["BatteryStatus"]);
            if (battery.Succeeded)
            {
                anySignal = true;
                batteryFromWmi = battery.Rows.Count > 0;
            }

            var power = source.GetPowerStatus();
            if (power.OnBattery is not null || power.BatteryPresent is not null) anySignal = true;

            var drive = source.GetSystemDrive();
            if (drive.IsReady) anySignal = true;

            if (!anySignal)
                return ProbeOutcome<HardwareContext>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
            return ProbeOutcome<HardwareContext>.Known(new HardwareContext(
                chassisTypes,
                IsLaptopChassis(chassisTypes),
                totalRam,
                media,
                PresenceRules.Any(batteryFromWmi, power.BatteryPresent),
                power.OnBattery,
                drive.FreeFraction,
                drive.DriveFormat), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<HardwareContext>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<HardwareContext>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }

    // ------------------------------------------------------------------ P-12 pending

    public static ProbeOutcome<PendingOperations> ProbePending(EnvironmentDataSource source, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var probedAt = DateTimeOffset.UtcNow;
        try
        {
            ct.ThrowIfCancellationRequested();
            var anySignal = false;

            var pendingReboot = PendingRebootSignal(source, ref anySignal);

            var processes = source.QueryProcesses();
            bool? installerRunning = null;
            bool? backupRunning = null;
            if (processes.Succeeded)
            {
                anySignal = true;
                installerRunning = ProcessNameMatches(processes.ProcessNames, "msiexec", "setup");
                backupRunning = ProcessNameMatches(processes.ProcessNames, "wbengine");
            }

            if (!anySignal)
                return ProbeOutcome<PendingOperations>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
            return ProbeOutcome<PendingOperations>.Known(new PendingOperations(
                pendingReboot, installerRunning, backupRunning), sw.Elapsed, probedAt);
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome<PendingOperations>.Unknown(ProbeUnknownReason.TimedOut, sw.Elapsed, probedAt);
        }
        catch
        {
            return ProbeOutcome<PendingOperations>.Unknown(ProbeUnknownReason.QueryFailed, sw.Elapsed, probedAt);
        }
    }
}
