using PurgeWindows.Core;

namespace PurgeWindows.Core.Environment;

/// <summary>Mod verdicts (D-13). Brand attribution is a separate, stricter gate — see <see cref="ModScoreResult.BrandNameable"/>.</summary>
public enum ModVerdict
{
    /// <summary>No meaningful mod evidence (score 0–1) with the foundational probes known.</summary>
    Stock,
    /// <summary>Modified, but without the strong evidence a brand name needs.</summary>
    ModifiedOther,
    /// <summary>Modified with strong evidence (score 4+ including a strong marker). Only this verdict may name a brand.</summary>
    ModifiedLikelyReviOS,
    /// <summary>The foundational probes (P-02/P-03) could not be checked. Never presented as stock.</summary>
    Unknown,
}

/// <summary>One observed mod marker: its id, its disclosure string key, its points, and its strength.</summary>
public sealed record ObservedMarker(string Id, string DisplayKey, int Points, bool IsStrong);

/// <summary>The result of combination scoring over P-02 + P-03 + P-05 + P-06 + P-10 evidence (W5 §5.3).</summary>
public sealed record ModScoreResult(
    int Score,
    int StrongCount,
    int SupportingCount,
    IReadOnlyList<ObservedMarker> ObservedMarkers,
    ModVerdict Verdict,
    bool BrandNameable,
    /// <summary>True when any input probe was Unknown, so the score is a lower bound, not the whole picture.</summary>
    bool PartialEvidence);

/// <summary>A mod verdict after the user's override (D-13: detection is user-visible + overridable).</summary>
public sealed record EffectiveModVerdict(
    ModVerdict Verdict,
    bool BrandNameable,
    bool OverrideActive,
    bool OverrideChangedVerdict);

/// <summary>
/// Resolved mod evidence: every marker as a plain boolean. True means positively observed;
/// false means not observed OR uncheckable — scoring only ever counts positive observations,
/// and <see cref="PartialEvidence"/> records when inputs were missing.
/// </summary>
public sealed record ModEvidence
{
    public bool RevisionTool { get; init; }
    public bool PauseTo2038 { get; init; }
    public bool EdgeBlockCombo { get; init; }
    public bool DefenderStrippedCombo { get; init; }
    public bool AppxDeprovisioned { get; init; }
    public bool EdgeAbsent { get; init; }
    public bool OneDriveAbsent { get; init; }
    public bool WidgetsAbsent { get; init; }
    public bool RecallAbsent { get; init; }
    public bool TeamsAbsent { get; init; }
    public bool PhotosAbsent { get; init; }
    public bool PhoneLinkAbsent { get; init; }
    public bool XboxAbsent { get; init; }
    public bool TelemetryDisabled { get; init; }
    public bool SecurityHealthRemoved { get; init; }
    public bool DataUsageBroken { get; init; }
    public bool DebloatTool { get; init; }
    public bool PartialEvidence { get; init; }
}

/// <summary>
/// Modified-Windows combination scoring (W5 §5.3, D-13/D-14). No single marker identifies a mod:
/// strong markers are worth 2 points, supporting markers 1 point. The disclosure bands are
/// 0–1 (nothing shown), 2–3 (some components missing), 4+ (modified). A brand name appears only
/// when the score reaches 4+ AND a strong marker was observed; anything less says "modified"
/// without a brand. Every marker below traces to W5 §5.3 — no marker was invented, and no
/// NTLite-specific marker is claimed (W5 explicitly claims none).
/// </summary>
public static class ModScoring
{
    // Display string keys for the evidence list. The brand name "ReviOS" appears in exactly one
    // UI string (EnvModLikelyReviOS), used only when BrandNameable is true.
    public const string MarkerRevisionToolKey = "EnvMarkerRevisionTool";
    public const string MarkerPause2038Key = "EnvMarkerPause2038";
    public const string MarkerEdgeBlockKey = "EnvMarkerEdgeBlock";
    public const string MarkerDefenderStrippedKey = "EnvMarkerDefenderStripped";
    public const string MarkerAppxDeprovisionedKey = "EnvMarkerAppxDeprovisioned";
    public const string MarkerEdgeAbsentKey = "EnvMarkerEdgeAbsent";
    public const string MarkerOneDriveAbsentKey = "EnvMarkerOneDriveAbsent";
    public const string MarkerWidgetsAbsentKey = "EnvMarkerWidgetsAbsent";
    public const string MarkerRecallAbsentKey = "EnvMarkerRecallAbsent";
    public const string MarkerTeamsAbsentKey = "EnvMarkerTeamsAbsent";
    public const string MarkerPhotosAbsentKey = "EnvMarkerPhotosAbsent";
    public const string MarkerPhoneLinkAbsentKey = "EnvMarkerPhoneLinkAbsent";
    public const string MarkerXboxAbsentKey = "EnvMarkerXboxAbsent";
    public const string MarkerTelemetryDisabledKey = "EnvMarkerTelemetryDisabled";
    public const string MarkerSecurityHealthKey = "EnvMarkerSecurityHealth";
    public const string MarkerDataUsageKey = "EnvMarkerDataUsage";
    public const string MarkerDebloatToolKey = "EnvMarkerDebloatTool";

    public static readonly IReadOnlyList<string> AllMarkerDisplayKeys =
    [
        MarkerRevisionToolKey, MarkerPause2038Key, MarkerEdgeBlockKey, MarkerDefenderStrippedKey,
        MarkerAppxDeprovisionedKey, MarkerEdgeAbsentKey, MarkerOneDriveAbsentKey, MarkerWidgetsAbsentKey,
        MarkerRecallAbsentKey, MarkerTeamsAbsentKey, MarkerPhotosAbsentKey, MarkerPhoneLinkAbsentKey,
        MarkerXboxAbsentKey, MarkerTelemetryDisabledKey, MarkerSecurityHealthKey, MarkerDataUsageKey,
        MarkerDebloatToolKey,
    ];

    /// <summary>
    /// Assembles resolved evidence from the probe outcomes. Unknown outcomes contribute nothing:
    /// their markers stay false and <see cref="ModEvidence.PartialEvidence"/> is set. Absence
    /// markers fire only on proven absence (<see cref="Presence.Absent"/>), never on Unknown.
    /// </summary>
    public static ModEvidence AssembleEvidence(
        ComponentPresence? components,
        ModMarkerEvidence? markers,
        ServiceStates? services,
        UpdateState? updates,
        VirtualizationInfo? virtualization)
    {
        var partial = components is null || markers is null || services is null || updates is null || virtualization is null;

        // S4's "Defender absent at the WinSxS level" half: P-02 reports Defender Absent only when
        // the service AND the binaries are absent — stronger than merely disabled, which is
        // exactly the distinction W5 §5.3 draws.
        var defenderStripped = components?.Defender == Presence.Absent
            && markers?.SmartScreenEnabled == false
            && virtualization?.VbsEnabled == false
            && markers?.CredentialGuardEnabled == false;

        // Telemetry bullet (W5 §5.3): DiagTrack removed/disabled AND error reporting (WerSvc)
        // removed/disabled. Merely stopped does not count — a stopped-but-enabled service is normal.
        var telemetryDisabled = IsRemovedOrDisabled(services?.Get("DiagTrack"))
            && IsRemovedOrDisabled(services?.Get("WerSvc"));

        return new ModEvidence
        {
            RevisionTool = markers?.RevisionToolFound == true || markers?.AmeWizardFound == true,
            PauseTo2038 = updates?.PauseEnds2038 == true,
            EdgeBlockCombo = markers?.EdgeBlockPolicyEnabled == true
                && components?.Edge == Presence.Absent
                && components?.WebView2 == Presence.Present,
            DefenderStrippedCombo = defenderStripped,
            AppxDeprovisioned = markers?.DeprovisionedCount is >= 50,
            EdgeAbsent = components?.Edge == Presence.Absent,
            OneDriveAbsent = components?.OneDrive == Presence.Absent,
            WidgetsAbsent = components?.Widgets == Presence.Absent,
            RecallAbsent = components?.RecallCopilot == Presence.Absent,
            TeamsAbsent = components?.Teams == Presence.Absent,
            PhotosAbsent = components?.Photos == Presence.Absent,
            PhoneLinkAbsent = components?.PhoneLink == Presence.Absent,
            XboxAbsent = components?.Xbox == Presence.Absent,
            TelemetryDisabled = telemetryDisabled,
            // Both halves are required, as W5 §5.3 writes them with "+". Probes never assert the
            // tray-icon half (no plan-specified source), so this marker stays unobserved in practice.
            SecurityHealthRemoved = markers?.SecurityHealthStartupPresent == false
                && markers?.SecurityHealthTrayHidden == true,
            DataUsageBroken = markers?.DataUsageBroken == true,
            DebloatTool = markers?.DebloatToolFound == true,
            PartialEvidence = partial,
        };
    }

    static bool IsRemovedOrDisabled(ServiceState? state) =>
        state is ServiceState.Disabled or ServiceState.NotInstalled;

    /// <summary>
    /// Scores resolved evidence. The verdict is Unknown when the foundational probes (P-02/P-03)
    /// were Unknown — scoring without component data would invent a verdict. Otherwise: 0–1 is
    /// Stock, 2+ is Modified, and a brand name needs 4+ including a strong marker.
    /// </summary>
    public static ModScoreResult Score(ModEvidence evidence, bool foundationalProbesKnown)
    {
        var observed = new List<ObservedMarker>();
        void Strong(string id, string key, bool fired)
        {
            if (fired) observed.Add(new ObservedMarker(id, key, 2, true));
        }
        void Supporting(string id, string key, bool fired)
        {
            if (fired) observed.Add(new ObservedMarker(id, key, 1, false));
        }

        Strong("S1", MarkerRevisionToolKey, evidence.RevisionTool);
        Strong("S2", MarkerPause2038Key, evidence.PauseTo2038);
        Strong("S3", MarkerEdgeBlockKey, evidence.EdgeBlockCombo);
        Strong("S4", MarkerDefenderStrippedKey, evidence.DefenderStrippedCombo);
        Strong("S5", MarkerAppxDeprovisionedKey, evidence.AppxDeprovisioned);
        Supporting("A1", MarkerEdgeAbsentKey, evidence.EdgeAbsent);
        Supporting("A2", MarkerOneDriveAbsentKey, evidence.OneDriveAbsent);
        Supporting("A3", MarkerWidgetsAbsentKey, evidence.WidgetsAbsent);
        Supporting("A4", MarkerRecallAbsentKey, evidence.RecallAbsent);
        Supporting("A5", MarkerTeamsAbsentKey, evidence.TeamsAbsent);
        Supporting("A6", MarkerPhotosAbsentKey, evidence.PhotosAbsent);
        Supporting("A7", MarkerPhoneLinkAbsentKey, evidence.PhoneLinkAbsent);
        Supporting("A8", MarkerXboxAbsentKey, evidence.XboxAbsent);
        Supporting("A9", MarkerTelemetryDisabledKey, evidence.TelemetryDisabled);
        Supporting("A10", MarkerSecurityHealthKey, evidence.SecurityHealthRemoved);
        Supporting("A11", MarkerDataUsageKey, evidence.DataUsageBroken);
        Supporting("A12", MarkerDebloatToolKey, evidence.DebloatTool);

        var score = observed.Sum(m => m.Points);
        var strongCount = observed.Count(m => m.IsStrong);
        var brandNameable = foundationalProbesKnown && score >= 4 && strongCount >= 1;
        var verdict = !foundationalProbesKnown ? ModVerdict.Unknown
            : brandNameable ? ModVerdict.ModifiedLikelyReviOS
            : score >= 2 ? ModVerdict.ModifiedOther
            : ModVerdict.Stock;

        return new ModScoreResult(score, strongCount, observed.Count - strongCount, observed,
            verdict, brandNameable, evidence.PartialEvidence);
    }

    /// <summary>Scores a snapshot's probe outcomes, extracting known values and flagging partial input.</summary>
    public static ModScoreResult Evaluate(
        ProbeOutcome<ComponentPresence> components,
        ProbeOutcome<ModMarkerEvidence> markers,
        ProbeOutcome<ServiceStates> services,
        ProbeOutcome<UpdateState> updates,
        ProbeOutcome<VirtualizationInfo> virtualization)
    {
        var evidence = AssembleEvidence(
            components.KnownValue, markers.KnownValue, services.KnownValue,
            updates.KnownValue, virtualization.KnownValue);
        return Score(evidence, components.IsKnown && markers.IsKnown);
    }

    /// <summary>
    /// Applies the user's mod override (D-13). An override can force Stock or generic Modified,
    /// but it can never create brand evidence: forcing Modified yields ModifiedOther, never the
    /// brand verdict, and forcing Stock drops brand attribution entirely.
    /// </summary>
    public static EffectiveModVerdict ApplyOverride(ModScoreResult score, ModVerdictOverride modOverride) =>
        modOverride switch
        {
            ModVerdictOverride.TreatAsStock => new EffectiveModVerdict(
                ModVerdict.Stock, false, true, score.Verdict != ModVerdict.Stock),
            ModVerdictOverride.TreatAsModified => new EffectiveModVerdict(
                ModVerdict.ModifiedOther, false, true, score.Verdict != ModVerdict.ModifiedOther),
            _ => new EffectiveModVerdict(score.Verdict, score.BrandNameable, false, false),
        };
}
