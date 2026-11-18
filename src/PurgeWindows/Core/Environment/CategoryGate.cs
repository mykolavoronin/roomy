using PurgeWindows.Localization;

namespace PurgeWindows.Core.Environment;

/// <summary>What the gate does with one catalog entry. Subtractive only: the gate can only
/// hide, downgrade, or disable — never re-enable, restore, or fight the user's setup.</summary>
public enum GateOutcome
{
    /// <summary>Offered at its catalog tier (possibly with an advisory note).</summary>
    Offered,
    /// <summary>Hidden from scan results, with a reason. Only on positive evidence.</summary>
    Hidden,
    /// <summary>Safe tier lowered to Check-First, with a reason.</summary>
    Downgraded,
    /// <summary>Shown with no action (sizing only), with a reason. Used for managed machines (E-19).</summary>
    Disabled,
}

/// <summary>One gated offer: the catalog entry plus the gate's decision and its human-readable reason.</summary>
public sealed record GatedOffer(
    CatalogEntry Entry,
    GateOutcome Outcome,
    SafetyLevel EffectiveTier,
    string? ReasonKey,
    IReadOnlyList<object?> ReasonArgs,
    string? AdvisoryKey,
    IReadOnlyList<object?> AdvisoryArgs,
    bool? DefaultSelectedOverride,
    string? RuleId)
{
    public string? ReasonText => ReasonKey is null ? null : Loc.Format(ReasonKey, ReasonArgs.ToArray());
    public string? AdvisoryText => AdvisoryKey is null ? null : Loc.Format(AdvisoryKey, AdvisoryArgs.ToArray());
}

/// <summary>The gate's full output for one catalog + snapshot pair.</summary>
public sealed record GatingResult(
    IReadOnlyList<GatedOffer> Offers,
    IReadOnlyDictionary<string, GatedOffer> ScannerOffers,
    EffectiveModVerdict EffectiveVerdict,
    bool ModRespectApplies,
    bool LowDiskApplies)
{
    public bool OverrideActive => EffectiveVerdict.OverrideActive;
    public bool OverrideChangedVerdict => EffectiveVerdict.OverrideChangedVerdict;

    /// <summary>The offer for one catalog key, or null when the catalog has no such entry.</summary>
    public GatedOffer? OfferFor(string key) =>
        Offers.FirstOrDefault(o => string.Equals(o.Entry.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The offer for one scan step id, or null when no category entry covers it.</summary>
    public GatedOffer? ScannerOffer(string scannerId) =>
        ScannerOffers.TryGetValue(scannerId, out var offer) ? offer : null;

    /// <summary>An empty gating: everything offered, no snapshot yet. Used before the first detection completes.</summary>
    public static GatingResult Empty(IReadOnlyList<CatalogEntry> entries)
    {
        var offers = entries.Select(e => new GatedOffer(e, GateOutcome.Offered, e.Tier,
            null, [], null, [], null, null)).ToList();
        var scannerOffers = offers
            .Where(o => o.Entry.Kind == "category" && o.Entry.ScannerId is not null)
            .ToDictionary(o => o.Entry.ScannerId!, StringComparer.OrdinalIgnoreCase);
        var verdict = new EffectiveModVerdict(ModVerdict.Unknown, false, false, false);
        return new GatingResult(offers, scannerOffers, verdict, false, false);
    }
}

/// <summary>
/// Runtime category gating (W5 §5.5, rules E-01–E-26). The catalog stays the single source
/// of category truth; this gate filters and downgrades at scan-assembly time only.
///
/// Hard rules: gating requires POSITIVE EVIDENCE — an Unknown probe never hides, downgrades,
/// or disables anything. Every decision carries a string-keyed reason the disclosure panel
/// can show. Precedence is Hidden &gt; Disabled &gt; Downgraded &gt; Offered-with-advisory,
/// and for shared keys the update-busy rule (E-14) is evaluated before the update-paused
/// rule (E-13) so its reason wins.
///
/// No E-row in this build produces Downgraded: the outcome exists, <see cref="ApplyToScan"/>
/// implements it, and a mechanism test covers it, but no current rule downgrades a tier.
/// Keys marked "future" below have no catalog entry in this build; the rule materializes
/// when the entry lands.
/// </summary>
public static class CategoryGate
{
    static int Rank(GateOutcome outcome) => outcome switch
    {
        GateOutcome.Hidden => 3,
        GateOutcome.Disabled => 2,
        GateOutcome.Downgraded => 1,
        _ => 0,
    };

    sealed class Decision
    {
        public GateOutcome Outcome = GateOutcome.Offered;
        public string? RuleId;
        public string? ReasonKey;
        public IReadOnlyList<object?> ReasonArgs = [];
        public string? AdvisoryKey;
        public IReadOnlyList<object?> AdvisoryArgs = [];
        public bool? DefaultSelectedOverride;
    }

    public static GatingResult Gate(CategoryCatalog catalog, EnvironmentSnapshot snapshot, ModVerdictOverride modOverride)
    {
        var components = snapshot.Components.KnownValue;
        var antivirus = snapshot.Antivirus.KnownValue;
        var updates = snapshot.Updates.KnownValue;
        var storageSense = snapshot.StorageSense.KnownValue;
        var oneDrive = snapshot.OneDrive.KnownValue;
        var managed = snapshot.Managed.KnownValue;
        var virt = snapshot.Virtualization.KnownValue;
        var hardware = snapshot.Hardware.KnownValue;
        var pending = snapshot.Pending.KnownValue;

        var effective = ModScoring.ApplyOverride(snapshot.ModScore, modOverride);
        var decisions = catalog.Entries.ToDictionary(
            e => e.Key, _ => new Decision(), StringComparer.OrdinalIgnoreCase);

        void Hide(string key, string ruleId, string reasonKey, params object?[] args)
        {
            if (!decisions.TryGetValue(key, out var decision)) return;
            // Hidden outranks everything, but the first hiding rule keeps its reason
            // (E-14 is evaluated before E-13 so the busy reason wins for shared keys).
            if (decision.Outcome == GateOutcome.Hidden) return;
            decision.Outcome = GateOutcome.Hidden;
            decision.RuleId = ruleId;
            decision.ReasonKey = reasonKey;
            decision.ReasonArgs = args;
        }

        void HideMany(IEnumerable<string> keys, string ruleId, string reasonKey, params object?[] args)
        {
            foreach (var key in keys) Hide(key, ruleId, reasonKey, args);
        }

        void Disable(string key, string ruleId, string reasonKey, params object?[] args)
        {
            if (!decisions.TryGetValue(key, out var decision)) return;
            if (Rank(decision.Outcome) >= Rank(GateOutcome.Disabled)) return;
            decision.Outcome = GateOutcome.Disabled;
            decision.RuleId = ruleId;
            decision.ReasonKey = reasonKey;
            decision.ReasonArgs = args;
        }

        void Advise(string key, string ruleId, string advisoryKey, params object?[] args)
        {
            if (!decisions.TryGetValue(key, out var decision)) return;
            if (decision.AdvisoryKey is not null) return;
            decision.AdvisoryKey = advisoryKey;
            decision.AdvisoryArgs = args;
            decision.RuleId ??= ruleId;
        }

        // E-14 before E-13: when updates are both paused and busy, the busy reason wins.
        // E-14: update in progress or a reboot/installer pending — hide servicing categories until done.
        var updateBusy = updates?.UpdateInProgress == true
            || updates?.PendingReboot == true
            || pending?.PendingReboot == true
            || pending?.InstallerRunning == true;
        if (updateBusy)
        {
            HideMany(["windows", "winold", "windows-update-download", "delivery-optimization", "bits-jobs",
                "setup-logs-extended", "esd-staging", "panther-logs",
                "winlogs", "diagnostics", "windows-temp", "system-crash-dumps", "cbs-logs"],
                "E-14", "EnvGateUpdateBusy");
        }

        // E-13: updates paused — hide update-sensitive categories, never unpause.
        if (updates?.IsPaused == true)
        {
            HideMany(["windows", "winold", "windows-update-download", "delivery-optimization", "bits-jobs",
                "setup-logs-extended", "esd-staging", "panther-logs"],
                "E-13", "EnvGateUpdatesPaused");
        }

        // E-01: Defender stripped at the component level — hide Defender history, never re-enable.
        if (components?.Defender == Presence.Absent)
            Hide("defender-history", "E-01", "EnvGateDefenderStripped");

        // E-02: Defender present but another AV is active and Defender is not enabled — hide Defender history.
        var thirdPartyName = antivirus?.ActiveThirdPartyNames.FirstOrDefault();
        if (components?.Defender == Presence.Present && antivirus?.HasThirdPartyActive == true
            && antivirus.DefenderProduct?.State != AvProductState.Enabled && thirdPartyName is not null)
            Hide("defender-history", "E-02", "EnvGateDefenderDisabled", thirdPartyName);

        // E-06: Edge removed — hide Edge rows. WebView2 rows stay when WebView2 is present.
        if (components?.Edge == Presence.Absent)
            Hide("edge-cache", "E-06", "EnvGateComponentAbsent", "Microsoft Edge"); // Future key: no entry in this build.
        // E-07: WebView2 absent — hide EBWebView rows. (teams-cache is deliberately NOT hidden: mixed content.)
        if (components?.WebView2 == Presence.Absent)
            Hide("webview2-cache", "E-07", "EnvGateComponentAbsent", "WebView2"); // Future key: no entry in this build.
        // E-08: Store removed — hide Store cache.
        if (components?.Store == Presence.Absent)
            Hide("store-cache", "E-08", "EnvGateComponentAbsent", "Microsoft Store");
        // E-09: OneDrive removed — hide OneDrive rows.
        if (components?.OneDrive == Presence.Absent)
            HideMany(["onedrive-cache", "onedrive-dehydration"], "E-09", "EnvGateComponentAbsent", "OneDrive"); // Future keys.
        // E-10: Widgets removed — hide the Widgets row.
        if (components?.Widgets == Presence.Absent)
            Hide("widgets-cache", "E-10", "EnvGateComponentAbsent", "Widgets"); // Future key: no entry in this build.
        // E-11: Xbox removed — hide the Xbox row. Captures-folder rows stay when the folder exists.
        if (components?.Xbox == Presence.Absent)
            Hide("xbox-cache", "E-11", "EnvGateComponentAbsent", "Xbox"); // Future key: no entry in this build.

        // E-18: OneDrive present but Files On-Demand off — hide dehydration rows.
        if (oneDrive?.OneDrivePresent == Presence.Present && oneDrive?.FilesOnDemand == false)
            Hide("onedrive-dehydration", "E-18", "EnvGateOnDemandOff"); // Future key: no entry in this build.

        // E-20: Hyper-V absent — hide the Hyper-V optimization row.
        if (virt?.HyperV == HyperVState.Absent)
            Hide("hyperv-optimize", "E-20", "EnvGateHyperVAbsent");
        // E-21: WSL absent or no distros — hide the WSL rows.
        if (virt?.Wsl == WslState.Absent)
            HideMany(["orphaned-wsl", "wsl-compact"], "E-21", "EnvGateWslAbsent");

        // E-19: domain/managed — show as managed by the organisation, with no action. Never change policy.
        if (managed?.IsManaged == true)
        {
            foreach (var key in new[] { "firewall-log", "branchcache", "event-logs", "defender-history", "registry-cleaning" })
                Disable(key, "E-19", "EnvGateManaged");
        }

        // E-03: Defender is the sole active AV — offer Defender history with a retention warning.
        if (antivirus?.DefenderSoleActive == true)
            Advise("defender-history", "E-03", "EnvGateDefenderRetention");
        // E-04: a third-party AV is active — warn before AV-adjacent rows, never touch AV data.
        if (antivirus?.HasThirdPartyActive == true && thirdPartyName is not null)
            Advise("defender-history", "E-04", "EnvGateThirdPartyAv", thirdPartyName);
        // E-04 (unknown): the AV probe failed — say state is unknown, without alarm.
        if (!snapshot.Antivirus.IsKnown)
            Advise("defender-history", "E-04", "EnvGateAvUnknown");
        // E-05: no AV registered — disclosure only, no gate effect, no nag.
        // E-12: mod respect is global (ModRespectApplies), not per-category.

        // E-15: Storage Sense already handles Temp and the Recycle Bin — unselect, never change settings.
        if (storageSense?.State == StorageSenseState.On)
        {
            foreach (var key in new[] { "temp", "recycle" })
            {
                if (!decisions.TryGetValue(key, out var decision)) continue;
                decision.DefaultSelectedOverride = false;
                Advise(key, "E-15", "EnvGateStorageSense");
            }
            Advise("onedrive-dehydration", "E-15", "EnvGateStorageSense"); // Future key.
        }
        // E-16: Storage Sense off — normal offers, no nag.

        // E-17: Files On-Demand on with placeholders — warn that re-downloading needs internet.
        // Never fires in this build: the probe reports placeholders as unknown (no file scan).
        if (oneDrive?.FilesOnDemand == true && oneDrive?.HasPlaceholders == true)
            Advise("onedrive-dehydration", "E-17", "EnvGatePlaceholders"); // Future key.
        // E-22: VBS/HVCI disabled — record only, no category changes.
        // E-23: laptop on battery — hibernation copy emphasises the consequence.
        if (hardware?.IsLaptop == true && hardware?.BatteryPresent == true)
            Advise("hibernation", "E-23", "EnvGateHibernationLaptop");
        // E-24: Windows 10 — a neutral out-of-support line in the disclosure, never a nag. No gate effect.
        // E-25: ARM64 — no x64-only owner tools exist in this build. No gate effect.
        // E-26: low disk — a global flag (LowDiskApplies); sorting is already largest-first.

        var offers = catalog.Entries.Select(entry =>
        {
            var decision = decisions[entry.Key];
            var effectiveTier = decision.Outcome == GateOutcome.Downgraded && entry.Tier == SafetyLevel.Safe
                ? SafetyLevel.CheckFirst
                : entry.Tier;
            return new GatedOffer(entry, decision.Outcome, effectiveTier,
                decision.ReasonKey, decision.ReasonArgs,
                decision.AdvisoryKey, decision.AdvisoryArgs,
                decision.DefaultSelectedOverride, decision.RuleId);
        }).ToList();

        var scannerOffers = offers
            .Where(o => o.Entry.Kind == "category" && o.Entry.ScannerId is not null)
            .ToDictionary(o => o.Entry.ScannerId!, StringComparer.OrdinalIgnoreCase);

        var modRespectApplies = effective.Verdict is ModVerdict.ModifiedOther or ModVerdict.ModifiedLikelyReviOS;
        var lowDiskApplies = hardware?.IsLowDiskSpace == true;
        return new GatingResult(offers, scannerOffers, effective, modRespectApplies, lowDiskApplies);
    }

    /// <summary>
    /// Applies gating to scan results. Hidden categories are dropped; disabled ones are cloned
    /// to sizing-only with the reason appended; downgraded ones are cloned to Check-First;
    /// advisories and default-selection overrides are applied to clones. Inputs are never
    /// mutated: unaffected categories pass through by reference, affected ones are clones.
    /// </summary>
    public static IReadOnlyList<CleanupCategory> ApplyToScan(
        IReadOnlyList<CleanupCategory> results, GatingResult gating)
    {
        var gated = new List<CleanupCategory>();
        foreach (var category in results)
        {
            var offer = gating.ScannerOffer(category.Id);
            if (offer is null || (offer.Outcome == GateOutcome.Offered
                && offer.AdvisoryKey is null && offer.DefaultSelectedOverride is null))
            {
                gated.Add(category);
                continue;
            }
            if (offer.Outcome == GateOutcome.Hidden) continue;

            var clone = new CleanupCategory
            {
                Id = category.Id,
                Title = category.Title,
                Description = category.Description,
                Glyph = category.Glyph,
                Tier = offer.Outcome == GateOutcome.Downgraded && category.Tier == SafetyLevel.Safe
                    ? SafetyLevel.CheckFirst
                    : category.Tier,
                Action = offer.Outcome == GateOutcome.Disabled ? CleanAction.ReportOnly : category.Action,
                SelectedByDefault = offer.DefaultSelectedOverride ?? category.SelectedByDefault,
                ShowItems = category.ShowItems,
                Blocked = category.Blocked,
                Note = AppendNote(category.Note, offer.Outcome == GateOutcome.Offered
                    ? offer.AdvisoryText
                    : offer.ReasonText),
            };
            clone.Items.AddRange(category.Items);
            gated.Add(clone);
        }
        return gated;
    }

    static string? AppendNote(string? existing, string? addition)
    {
        if (string.IsNullOrEmpty(addition)) return existing;
        if (string.IsNullOrEmpty(existing)) return addition;
        return existing + "\n" + addition;
    }
}
