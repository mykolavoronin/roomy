using PurgeWindows.Localization;

namespace PurgeWindows.Core.Environment;

/// <summary>One probe's disclosure row: what was checked, what was found, and why when unknown.</summary>
public sealed record EvidenceRow(string ProbeId, string Title, string Detail, bool IsKnown, string DurationText);

/// <summary>One gating decision's disclosure row: the entry, the outcome, and the reason.</summary>
public sealed record GatingDecisionRow(string Key, string DisplayName, string OutcomeText, string ReasonText, string? RuleId);

/// <summary>
/// The pure text builders behind the "Your setup" disclosure: summary lines, per-probe
/// evidence rows, and gating-decision rows. Kept free of WPF so the copy rules — especially
/// the D-13 brand gating in <see cref="ModVerdictText"/> — are unit-testable. Copy is
/// factual and neutral: mod and antivirus states are never presented as problems.
/// </summary>
public static class EnvironmentDisclosure
{
    public static IReadOnlyList<string> BuildSummaryLines(EnvironmentSnapshot snapshot, GatingResult gating) =>
    [
        OsSummaryText(snapshot.Os),
        ModVerdictText(snapshot.ModScore, gating.EffectiveVerdict),
        AntivirusSummaryText(snapshot.Antivirus),
        PrivilegeText(snapshot.IsElevated),
    ];

    public static string OsSummaryText(ProbeOutcome<OsInfo> outcome)
    {
        var os = outcome.KnownValue;
        if (os is null) return UnknownText(outcome.UnknownReason);
        if (os.ProductName is { } product && os.DisplayVersion is { } version && os.BuildText is { } build)
            return Loc.Format("EnvOsFormat", product, version, build);
        if (os.ProductName is not null && os.BuildText is not null)
            return Loc.Format("EnvOsFormatNoVersion", os.ProductName, os.BuildText);
        if (os.BuildText is not null)
            return Loc.Format("EnvOsBuildOnly", os.BuildText);
        return Loc.Get("EnvOsUnknown");
    }

    /// <summary>
    /// The mod verdict line. The brand wording (EnvModLikelyReviOS) is used ONLY when the
    /// effective verdict is ModifiedLikelyReviOS — an override can never produce it, and a
    /// score of 2–3 without brand evidence says components are missing, not that the build
    /// is modified.
    /// </summary>
    public static string ModVerdictText(ModScoreResult score, EffectiveModVerdict effective)
    {
        var text = effective.Verdict switch
        {
            ModVerdict.Stock => Loc.Get("EnvModStock"),
            ModVerdict.ModifiedLikelyReviOS => Loc.Get("EnvModLikelyReviOS"),
            ModVerdict.ModifiedOther => effective.OverrideActive || score.Score >= 4
                ? Loc.Get("EnvModModifiedOther")
                : Loc.Get("EnvModComponentsMissing"),
            _ => Loc.Get("EnvModUnknown"),
        };
        if (effective.OverrideActive)
            text += " " + Loc.Get("EnvOverrideActiveNote");
        return text;
    }

    public static string AntivirusSummaryText(ProbeOutcome<AntivirusInfo> outcome)
    {
        var av = outcome.KnownValue;
        if (av is null) return UnknownText(outcome.UnknownReason);
        if (av.NoProductsRegistered) return Loc.Get("EnvAvNoneRegistered");
        return string.Join(", ", av.Products.Select(p =>
            Loc.Format("EnvAvProductFormat", p.Name, AvStateText(p.State))));
    }

    public static string AvStateText(AvProductState state) => state switch
    {
        AvProductState.Enabled => Loc.Get("EnvAvStateEnabled"),
        AvProductState.DisabledOrSnoozed => Loc.Get("EnvAvStateDisabled"),
        AvProductState.OutOfDate => Loc.Get("EnvAvStateOutOfDate"),
        _ => Loc.Get("EnvAvStateUnknown"),
    };

    public static string PrivilegeText(bool? isElevated) => isElevated switch
    {
        true => Loc.Get("EnvPrivilegeAdmin"),
        false => Loc.Get("EnvPrivilegeStandard"),
        null => Loc.Get("EnvValueUnknown"),
    };

    public static string UnknownText(ProbeUnknownReason? reason) =>
        Loc.Format("EnvProbeUnknownWithReason", ReasonText(reason));

    public static string ReasonText(ProbeUnknownReason? reason) => reason switch
    {
        ProbeUnknownReason.TimedOut => Loc.Get("EnvProbeReasonTimedOut"),
        ProbeUnknownReason.AccessDenied => Loc.Get("EnvProbeReasonAccessDenied"),
        ProbeUnknownReason.QueryFailed => Loc.Get("EnvProbeReasonQueryFailed"),
        ProbeUnknownReason.NotAvailable => Loc.Get("EnvProbeReasonNotAvailable"),
        _ => Loc.Get("EnvValueUnknown"),
    };

    public static string DurationText(TimeSpan duration) =>
        Loc.Format("EnvProbeChecked", (int)Math.Round(duration.TotalMilliseconds));

    public static IReadOnlyList<EvidenceRow> BuildEvidence(EnvironmentSnapshot snapshot)
    {
        var rows = new List<EvidenceRow>();
        void Add<T>(string probeId, string titleKey, ProbeOutcome<T> outcome, Func<T, string> detail)
        {
            var value = outcome.KnownValue;
            rows.Add(new EvidenceRow(probeId, Loc.Get(titleKey),
                value is null ? UnknownText(outcome.UnknownReason) : detail(value),
                outcome.IsKnown,
                outcome.IsKnown ? DurationText(outcome.Duration) : ""));
        }

        Add(ProbeIds.Os, "EnvProbeOs", snapshot.Os, _ => OsSummaryText(snapshot.Os));
        Add(ProbeIds.Components, "EnvProbeComponents", snapshot.Components, ComponentsDetail);
        Add(ProbeIds.ModMarkers, "EnvProbeModMarkers", snapshot.ModMarkers, _ =>
            Loc.Format("EnvMarkersObserved", snapshot.ModScore.ObservedMarkers.Count));
        Add(ProbeIds.Antivirus, "EnvProbeAntivirus", snapshot.Antivirus, _ => AntivirusSummaryText(snapshot.Antivirus));
        Add(ProbeIds.Services, "EnvProbeServices", snapshot.Services, ServicesDetail);
        Add(ProbeIds.Updates, "EnvProbeUpdates", snapshot.Updates, UpdatesDetail);
        Add(ProbeIds.StorageSense, "EnvProbeStorageSense", snapshot.StorageSense, sense => sense.State switch
        {
            StorageSenseState.On => Loc.Get("EnvStorageSenseOn"),
            StorageSenseState.Off => Loc.Get("EnvStorageSenseOff"),
            _ => Loc.Get("EnvValueUnknown"),
        });
        Add(ProbeIds.OneDrive, "EnvProbeOneDrive", snapshot.OneDrive, drive =>
            Loc.Format("EnvOneDriveSummary",
                drive.ProviderCount?.ToString() ?? "?",
                drive.FilesOnDemand switch
                {
                    true => Loc.Get("EnvValueOn"),
                    false => Loc.Get("EnvValueOff"),
                    null => Loc.Get("EnvValueUnknown"),
                }));
        Add(ProbeIds.Managed, "EnvProbeManaged", snapshot.Managed, managed =>
            managed.DomainJoined is null && managed.MdmEnrolled is null
                && managed.DefenderForEndpoint is null && managed.IntuneExtension is null
                ? Loc.Get("EnvValueUnknown")
                : managed.IsManaged ? Loc.Get("EnvManagedYes") : Loc.Get("EnvManagedNo"));
        Add(ProbeIds.Virtualization, "EnvProbeVirtualization", snapshot.Virtualization, virt =>
            Loc.Format("EnvVirtSummary",
                virt.HyperV switch
                {
                    HyperVState.Enabled => Loc.Get("EnvValueEnabled"),
                    HyperVState.Absent => Loc.Get("EnvValueAbsent"),
                    _ => Loc.Get("EnvValueUnknown"),
                },
                virt.Wsl switch
                {
                    WslState.Present => Loc.Get("EnvValuePresent"),
                    WslState.Absent => Loc.Get("EnvValueAbsent"),
                    _ => Loc.Get("EnvValueUnknown"),
                },
                virt.VbsEnabled switch
                {
                    true => Loc.Get("EnvValueOn"),
                    false => Loc.Get("EnvValueOff"),
                    null => Loc.Get("EnvValueUnknown"),
                }));
        Add(ProbeIds.Hardware, "EnvProbeHardware", snapshot.Hardware, HardwareDetail);
        Add(ProbeIds.Pending, "EnvProbePending", snapshot.Pending, pending =>
            pending.PendingReboot is null && pending.InstallerRunning is null && pending.BackupRunning is null
                ? Loc.Get("EnvValueUnknown")
                : pending.PendingReboot == true || pending.InstallerRunning == true || pending.BackupRunning == true
                    ? Loc.Get("EnvPendingSomething")
                    : Loc.Get("EnvPendingNone"));
        return rows;
    }

    public static string ComponentsDetail(ComponentPresence components)
    {
        var states = components.AllStates();
        return Loc.Format("EnvComponentsSummary",
            states.Count(s => s == Presence.Present),
            states.Count(s => s == Presence.Absent),
            states.Count(s => s == Presence.Unknown));
    }

    public static string ServicesDetail(ServiceStates services)
    {
        var states = services.States.Values.ToList();
        return Loc.Format("EnvServicesSummary",
            states.Count(s => s == ServiceState.Running),
            states.Count(s => s is ServiceState.Stopped or ServiceState.Disabled or ServiceState.NotInstalled),
            states.Count(s => s == ServiceState.Unknown));
    }

    public static string UpdatesDetail(UpdateState updates)
    {
        if (updates.UpdateInProgress == true || updates.PendingReboot == true)
            return Loc.Get("EnvUpdateSummaryBusy");
        if (updates.IsPaused == true && updates.PauseExpiry is { } expiry)
            return Loc.Format("EnvUpdateSummaryPaused", expiry.ToLocalTime().ToString("d"));
        if (updates.IsPaused == true)
            return Loc.Get("EnvUpdateSummaryPausedUnknown");
        if (updates.IsPaused is null)
            return Loc.Get("EnvUpdateSummaryUnknown");
        return Loc.Get("EnvUpdateSummaryOk");
    }

    public static string HardwareDetail(HardwareContext hardware)
    {
        var detail = Loc.Format("EnvHardwareSummary",
            hardware.IsLaptop switch
            {
                true => Loc.Get("EnvHardwareLaptop"),
                false => Loc.Get("EnvHardwareDesktop"),
                null => Loc.Get("EnvHardwareUnknown"),
            },
            hardware.TotalRamBytes is { } ram ? Format.Bytes(ram) : "?",
            hardware.SystemDriveMedia switch
            {
                DriveMedia.Ssd => "SSD",
                DriveMedia.Hdd => "HDD",
                _ => Loc.Get("EnvValueUnknown"),
            });
        if (hardware.IsLowDiskSpace)
            detail += " " + Loc.Get("EnvLowDiskNote");
        return detail;
    }

    public static IReadOnlyList<GatingDecisionRow> BuildGatingDecisions(GatingResult gating) =>
        gating.Offers
            .Where(o => o.Outcome != GateOutcome.Offered || o.AdvisoryKey is not null || o.DefaultSelectedOverride is not null)
            .Select(o => new GatingDecisionRow(
                o.Entry.Key,
                o.Entry.DisplayName,
                o.Outcome switch
                {
                    GateOutcome.Hidden => Loc.Get("EnvGateOutcomeHidden"),
                    GateOutcome.Disabled => Loc.Get("EnvGateOutcomeDisabled"),
                    GateOutcome.Downgraded => Loc.Get("EnvGateOutcomeDowngraded"),
                    _ => Loc.Get("EnvGateOutcomeOffered"),
                },
                o.ReasonText ?? o.AdvisoryText ?? "",
                o.RuleId))
            .ToList();
}
