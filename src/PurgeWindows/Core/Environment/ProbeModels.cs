using System.Runtime.InteropServices;

namespace PurgeWindows.Core.Environment;

/// <summary>Probe identifiers, W5 §5.2 (P-01–P-12). The plan's probe table governs; no other probes exist.</summary>
public static class ProbeIds
{
    public const string Os = "P-01";
    public const string Components = "P-02";
    public const string ModMarkers = "P-03";
    public const string Antivirus = "P-04";
    public const string Services = "P-05";
    public const string Updates = "P-06";
    public const string StorageSense = "P-07";
    public const string OneDrive = "P-08";
    public const string Managed = "P-09";
    public const string Virtualization = "P-10";
    public const string Hardware = "P-11";
    public const string Pending = "P-12";

    public static readonly IReadOnlyList<string> All =
        [Os, Components, ModMarkers, Antivirus, Services, Updates, StorageSense, OneDrive, Managed, Virtualization, Hardware, Pending];
}

/// <summary>Why a probe came back Unknown. Unknown is a first-class result, never a guess.</summary>
public enum ProbeUnknownReason
{
    /// <summary>The probe did not finish within its timeout.</summary>
    TimedOut,
    /// <summary>Windows refused the read (unelevated probes never escalate).</summary>
    AccessDenied,
    /// <summary>The query ran and failed.</summary>
    QueryFailed,
    /// <summary>The API or data source does not exist on this machine.</summary>
    NotAvailable,
}

/// <summary>
/// One probe's outcome: a value, or Unknown with a reason. Every probe returns one of these;
/// a failed probe is Unknown, never a prompt, never a crash, never a guessed value.
/// </summary>
public sealed record ProbeOutcome<T>(T? Value, bool IsKnown, ProbeUnknownReason? UnknownReason, TimeSpan Duration, DateTimeOffset ProbedAt)
{
    public static ProbeOutcome<T> Known(T value, TimeSpan duration, DateTimeOffset probedAt) =>
        new(value, true, null, duration, probedAt);

    public static ProbeOutcome<T> Unknown(ProbeUnknownReason reason, TimeSpan duration, DateTimeOffset probedAt) =>
        new(default, false, reason, duration, probedAt);

    /// <summary>The value when known, null otherwise. Gating only ever acts on known values.</summary>
    public T? KnownValue => IsKnown ? Value : default;
}

/// <summary>
/// Three-state presence. Absence must be proven: a component counts as absent only when every
/// applicable signal says absent. Any signal that could not be checked makes the result Unknown.
/// </summary>
public enum Presence
{
    Present,
    Absent,
    Unknown,
}

public static class PresenceRules
{
    /// <summary>
    /// Combines signals: any Present wins; otherwise any Unknown wins; Absent only when every
    /// signal is Absent. An empty signal set is Unknown (nothing was checked).
    /// </summary>
    public static Presence Combine(params Presence[] signals)
    {
        if (signals.Length == 0) return Presence.Unknown;
        if (signals.Contains(Presence.Present)) return Presence.Present;
        if (signals.Contains(Presence.Unknown)) return Presence.Unknown;
        return Presence.Absent;
    }

    /// <summary>Tri-state OR for nullable booleans: true when any input is true; false only when
    /// every input is false; null otherwise.</summary>
    public static bool? Any(params bool?[] values)
    {
        if (values.Length == 0) return null;
        if (values.Any(v => v == true)) return true;
        if (values.All(v => v == false)) return false;
        return null;
    }
}

/// <summary>Service state from a read-only Service Control Manager query (W5 P-05).</summary>
public enum ServiceState
{
    Running,
    Stopped,
    Disabled,
    NotInstalled,
    Unknown,
}

/// <summary>Windows release channel (W5 P-01). Absence of the WindowsSelfHost key means release.</summary>
public enum OsChannel
{
    Release,
    Preview,
    Unknown,
}

/// <summary>P-01 result: Windows edition, build, channel, and architecture. Null fields were not readable.</summary>
public sealed record OsInfo(
    string? ProductName,
    string? EditionId,
    string? DisplayVersion,
    int? Build,
    int? Ubr,
    string? BuildLabEx,
    Architecture? OsArchitecture,
    OsChannel Channel)
{
    /// <summary>Windows 10 builds run 10240–19045; Windows 11 starts at 22000 (W5 E-24).</summary>
    public bool IsWindows10 => Build is >= 10240 and <= 19045;

    public bool IsWindows11 => Build is >= 22000;

    public bool IsArm64 => OsArchitecture == Architecture.Arm64;

    /// <summary>"26100.1234" when both parts are known, the build alone when only it is, else null.</summary>
    public string? BuildText => Build switch
    {
        null => null,
        var b => Ubr is { } u ? $"{b}.{u}" : b.ToString(),
    };
}

/// <summary>P-02 result: presence of the built-in components W5 §5.2 lists, plus Teams and Photos,
/// which the mod scoring in W5 §5.3 needs. Absent means proven absent; Unknown means uncheckable.</summary>
public sealed record ComponentPresence(
    Presence Defender,
    Presence Edge,
    Presence WebView2,
    Presence Store,
    Presence OneDrive,
    Presence Widgets,
    Presence Xbox,
    Presence Search,
    Presence PhoneLink,
    Presence RecallCopilot,
    Presence Teams,
    Presence Photos)
{
    public Presence[] AllStates() =>
        [Defender, Edge, WebView2, Store, OneDrive, Widgets, Xbox, Search, PhoneLink, RecallCopilot, Teams, Photos];
}

/// <summary>
/// P-03 result: the raw mod-marker readings. Every field is nullable: null means that reading
/// could not be checked, which is never treated as evidence either way. Scoring lives in
/// <see cref="ModScoring"/>; this record only carries what the probes observed.
/// </summary>
public sealed record ModMarkerEvidence(
    /// <summary>Revision Tool found: revitool.exe discoverable, or a Revision Tool uninstall record (W5 §5.3).</summary>
    bool? RevisionToolFound,
    /// <summary>AME Wizard install record found (W5 §5.3; the .apbx association alone never counts).</summary>
    bool? AmeWizardFound,
    /// <summary>DoNotUpdateToEdgeWithChromium policy enabled (W5 §5.3).</summary>
    bool? EdgeBlockPolicyEnabled,
    /// <summary>SmartScreen enabled. Missing key means the Windows default (on); null means unreadable.</summary>
    bool? SmartScreenEnabled,
    /// <summary>Credential Guard enabled (LsaCfgFlags). A missing value means never configured (off).</summary>
    bool? CredentialGuardEnabled,
    /// <summary>SecurityHealth present in the Run startup key.</summary>
    bool? SecurityHealthStartupPresent,
    /// <summary>Windows Security tray icon hidden. No plan-specified source exists, so probes never assert this.</summary>
    bool? SecurityHealthTrayHidden,
    /// <summary>A debloat tool's uninstall record found (O&amp;O ShutUp10, W10Privacy, Chris Titus Tech utility).</summary>
    bool? DebloatToolFound,
    /// <summary>Count of deprovisioned AppX packages. No verified stock baseline exists, so probes never assert this.</summary>
    int? DeprovisionedCount,
    /// <summary>Data-usage monitoring broken. UNVERIFIED as a detector (W5 §5.3), so probes never assert this.</summary>
    bool? DataUsageBroken);

/// <summary>Coarse antivirus product state (W5 §5.2b). Four words only; the bitmask cannot support more.</summary>
public enum AvProductState
{
    Enabled,
    DisabledOrSnoozed,
    OutOfDate,
    Unknown,
}

/// <summary>Definition/signature freshness, as reported. Unknown when the source did not say.</summary>
public enum AvSignatureStatus
{
    UpToDate,
    OutOfDate,
    Unknown,
}

/// <summary>One registered security product, as reported by Windows Security Center.</summary>
public sealed record AvProduct(
    string Name,
    AvProductState State,
    AvSignatureStatus SignatureStatus,
    bool IsDefender,
    string? ExePath);

/// <summary>Which Security Center source produced the antivirus result (WSC primary, WMI fallback).</summary>
public enum AvSource
{
    Wsc,
    Wmi,
}

/// <summary>P-04 result: registered antivirus/firewall/antispyware products plus Defender detail.</summary>
public sealed record AntivirusInfo(
    IReadOnlyList<AvProduct> Products,
    IReadOnlyList<AvProduct> FirewallProducts,
    IReadOnlyList<AvProduct> AntiSpywareProducts,
    AvSource Source,
    bool FirewallChecked,
    bool? DefenderRealTimeEnabled,
    bool? TamperProtectionEnabled)
{
    /// <summary>True when the query succeeded and no antivirus product is registered. This is
    /// reported as exactly that — never as "no antivirus installed" (D-13).</summary>
    public bool NoProductsRegistered => Products.Count == 0;

    public AvProduct? DefenderProduct => Products.FirstOrDefault(p => p.IsDefender);

    public bool HasThirdPartyActive => Products.Any(p => !p.IsDefender && p.State == AvProductState.Enabled);

    public IReadOnlyList<string> ActiveThirdPartyNames =>
        Products.Where(p => !p.IsDefender && p.State == AvProductState.Enabled).Select(p => p.Name).ToList();

    /// <summary>True when Defender is the only enabled antivirus (W5 E-03).</summary>
    public bool DefenderSoleActive =>
        DefenderProduct?.State == AvProductState.Enabled && !HasThirdPartyActive;
}

/// <summary>P-05 result: states of the relevant service subset (W5 P-05). Missing names read as Unknown.</summary>
public sealed record ServiceStates(IReadOnlyDictionary<string, ServiceState> States)
{
    public ServiceState Get(string name) =>
        States.TryGetValue(name, out var state) ? state : ServiceState.Unknown;
}

/// <summary>P-06 result: Windows Update pause state, pending reboot, and update-in-progress signals.</summary>
public sealed record UpdateState(
    DateTimeOffset? PauseExpiry,
    bool? IsPaused,
    bool PauseValuesPresent,
    bool? PendingReboot,
    bool? UpdateInProgress)
{
    /// <summary>True when the pause expiry falls in 2038 — the far-future pause W5 §5.3 names (ReviOS default).</summary>
    public bool PauseEnds2038 => PauseExpiry?.Year == 2038;
}

/// <summary>Storage Sense state (W5 P-07).</summary>
public enum StorageSenseState
{
    On,
    Off,
    Unknown,
}

/// <summary>P-07 result.</summary>
public sealed record StorageSenseInfo(StorageSenseState State);

/// <summary>
/// P-08 result: OneDrive sync providers and Files On-Demand. <see cref="HasPlaceholders"/> is
/// always null in this build: counting placeholders needs a file scan, which no probe does.
/// </summary>
public sealed record OneDriveInfo(
    int? ProviderCount,
    Presence OneDrivePresent,
    bool? FilesOnDemand,
    bool? HasPlaceholders);

/// <summary>P-09 result: domain, MDM, Defender for Endpoint, and Intune signals. Null means uncheckable.</summary>
public sealed record ManagedInfo(
    bool? DomainJoined,
    bool? MdmEnrolled,
    bool? DefenderForEndpoint,
    bool? IntuneExtension)
{
    /// <summary>True only on positive evidence of management (W5 E-19). Unknown never counts as managed.</summary>
    public bool IsManaged => DomainJoined == true || MdmEnrolled == true || DefenderForEndpoint == true || IntuneExtension == true;
}

/// <summary>Hyper-V state (W5 P-10).</summary>
public enum HyperVState
{
    Enabled,
    Absent,
    Unknown,
}

/// <summary>WSL state (W5 P-10).</summary>
public enum WslState
{
    Present,
    Absent,
    Unknown,
}

/// <summary>P-10 result: Hyper-V, WSL, and virtualization-based security.</summary>
public sealed record VirtualizationInfo(
    HyperVState HyperV,
    WslState Wsl,
    IReadOnlyList<string> DistroNames,
    bool? VbsEnabled,
    bool? HvciEnabled);

/// <summary>Physical disk media type, for copy only (the app never defragments).</summary>
public enum DriveMedia
{
    Ssd,
    Hdd,
    Unknown,
}

/// <summary>P-11 result: hardware context, used only for honest copy (W5 P-11).</summary>
public sealed record HardwareContext(
    IReadOnlyList<int> ChassisTypes,
    bool? IsLaptop,
    long? TotalRamBytes,
    DriveMedia SystemDriveMedia,
    bool? BatteryPresent,
    bool? IsOnBattery,
    double? SystemDriveFreeFraction,
    string? SystemDriveFileSystem)
{
    /// <summary>True only when the system drive is positively known to be under 10% free (W5 E-26).</summary>
    public bool IsLowDiskSpace => SystemDriveFreeFraction is { } fraction && fraction < 0.10;
}

/// <summary>P-12 result: pending operations and locks, best-effort, never exhaustive.</summary>
public sealed record PendingOperations(
    bool? PendingReboot,
    bool? InstallerRunning,
    bool? BackupRunning);
