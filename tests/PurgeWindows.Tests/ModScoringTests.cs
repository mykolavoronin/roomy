using PurgeWindows.Core;
using PurgeWindows.Core.Environment;

namespace PurgeWindows.Tests;

/// <summary>
/// Combination-scoring tests (W5 §5.3, D-13): no single marker names a brand, strong
/// combinations do, and missing evidence yields honest Stock/Unknown verdicts.
/// </summary>
public sealed class ModScoringTests
{
    static ModEvidence Evidence(
        bool revisionTool = false, bool pause2038 = false, bool edgeBlock = false,
        bool defenderStripped = false, bool appx = false,
        bool edgeAbsent = false, bool oneDriveAbsent = false, bool widgetsAbsent = false,
        bool recallAbsent = false, bool teamsAbsent = false, bool photosAbsent = false,
        bool phoneLinkAbsent = false, bool xboxAbsent = false,
        bool telemetry = false, bool securityHealth = false, bool dataUsage = false,
        bool debloat = false, bool partial = false) => new()
        {
            RevisionTool = revisionTool,
            PauseTo2038 = pause2038,
            EdgeBlockCombo = edgeBlock,
            DefenderStrippedCombo = defenderStripped,
            AppxDeprovisioned = appx,
            EdgeAbsent = edgeAbsent,
            OneDriveAbsent = oneDriveAbsent,
            WidgetsAbsent = widgetsAbsent,
            RecallAbsent = recallAbsent,
            TeamsAbsent = teamsAbsent,
            PhotosAbsent = photosAbsent,
            PhoneLinkAbsent = phoneLinkAbsent,
            XboxAbsent = xboxAbsent,
            TelemetryDisabled = telemetry,
            SecurityHealthRemoved = securityHealth,
            DataUsageBroken = dataUsage,
            DebloatTool = debloat,
            PartialEvidence = partial,
        };

    [Fact]
    public void NoMarkersYieldsStock()
    {
        var result = ModScoring.Score(Evidence(), foundationalProbesKnown: true);
        Assert.Equal(0, result.Score);
        Assert.Equal(ModVerdict.Stock, result.Verdict);
        Assert.False(result.BrandNameable);
        Assert.Empty(result.ObservedMarkers);
    }

    [Fact]
    public void SingleWeakMarkerNeverNamesABrand()
    {
        var result = ModScoring.Score(Evidence(edgeAbsent: true), foundationalProbesKnown: true);
        Assert.Equal(1, result.Score);
        Assert.Equal(ModVerdict.Stock, result.Verdict);
        Assert.False(result.BrandNameable);
    }

    [Fact]
    public void TwoWeakMarkersYieldGenericModifiedWithoutABrand()
    {
        var result = ModScoring.Score(Evidence(edgeAbsent: true, oneDriveAbsent: true), foundationalProbesKnown: true);
        Assert.Equal(2, result.Score);
        Assert.Equal(ModVerdict.ModifiedOther, result.Verdict);
        Assert.False(result.BrandNameable);
    }

    [Fact]
    public void FourWeakMarkersStillDoNotNameABrand()
    {
        var result = ModScoring.Score(Evidence(
            edgeAbsent: true, oneDriveAbsent: true, widgetsAbsent: true, xboxAbsent: true),
            foundationalProbesKnown: true);
        Assert.Equal(4, result.Score);
        Assert.Equal(0, result.StrongCount);
        Assert.Equal(ModVerdict.ModifiedOther, result.Verdict);
        Assert.False(result.BrandNameable);
    }

    [Fact]
    public void SingleStrongMarkerAloneDoesNotNameABrand()
    {
        var result = ModScoring.Score(Evidence(revisionTool: true), foundationalProbesKnown: true);
        Assert.Equal(2, result.Score);
        Assert.Equal(1, result.StrongCount);
        Assert.Equal(ModVerdict.ModifiedOther, result.Verdict);
        Assert.False(result.BrandNameable);
    }

    [Fact]
    public void StrongMarkerPlusTwoWeakOnesNamesTheBrand()
    {
        var result = ModScoring.Score(Evidence(
            revisionTool: true, edgeAbsent: true, oneDriveAbsent: true),
            foundationalProbesKnown: true);
        Assert.Equal(4, result.Score);
        Assert.Equal(1, result.StrongCount);
        Assert.True(result.BrandNameable);
        Assert.Equal(ModVerdict.ModifiedLikelyReviOS, result.Verdict);
    }

    [Fact]
    public void TwoStrongMarkersNameTheBrand()
    {
        var result = ModScoring.Score(Evidence(revisionTool: true, pause2038: true),
            foundationalProbesKnown: true);
        Assert.Equal(4, result.Score);
        Assert.True(result.BrandNameable);
        Assert.Equal(ModVerdict.ModifiedLikelyReviOS, result.Verdict);
    }

    [Fact]
    public void UnknownFoundationalProbesYieldUnknownEvenWithMarkers()
    {
        var result = ModScoring.Score(Evidence(revisionTool: true, pause2038: true),
            foundationalProbesKnown: false);
        Assert.Equal(ModVerdict.Unknown, result.Verdict);
        Assert.False(result.BrandNameable);
        // The score is still computed honestly; only the verdict refuses to guess.
        Assert.Equal(4, result.Score);
    }

    [Fact]
    public void EvaluateYieldsUnknownWhenComponentsOrMarkersAreUnknown()
    {
        var unknownComponents = SnapshotBuilder.Build(markers: SnapshotBuilder.NoMarkers());
        Assert.Equal(ModVerdict.Unknown, unknownComponents.ModScore.Verdict);

        var unknownMarkers = SnapshotBuilder.Build(components: SnapshotBuilder.AllComponents(Presence.Present));
        Assert.Equal(ModVerdict.Unknown, unknownMarkers.ModScore.Verdict);
    }

    [Fact]
    public void AssembleEvidenceFlagsPartialInput()
    {
        var evidence = ModScoring.AssembleEvidence(
            SnapshotBuilder.AllComponents(Presence.Present), SnapshotBuilder.NoMarkers(),
            services: null, updates: null, virtualization: null);
        Assert.True(evidence.PartialEvidence);
    }

    [Fact]
    public void AssembleEvidenceRequiresTheFullEdgeBlockCombination()
    {
        var markers = SnapshotBuilder.NoMarkers() with { EdgeBlockPolicyEnabled = true };
        var combo = ModScoring.AssembleEvidence(
            new ComponentPresence(Presence.Present, Presence.Absent, Presence.Present, Presence.Present,
                Presence.Present, Presence.Present, Presence.Present, Presence.Present, Presence.Present,
                Presence.Present, Presence.Present, Presence.Present),
            markers, SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc"),
            new UpdateState(null, false, false, false, false),
            new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], true, true));
        Assert.True(combo.EdgeBlockCombo);

        // WebView2 absent breaks the combination (the policy alone is not the marker).
        var noWebView2 = ModScoring.AssembleEvidence(
            new ComponentPresence(Presence.Present, Presence.Absent, Presence.Absent, Presence.Present,
                Presence.Present, Presence.Present, Presence.Present, Presence.Present, Presence.Present,
                Presence.Present, Presence.Present, Presence.Present),
            markers, SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc"),
            new UpdateState(null, false, false, false, false),
            new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], true, true));
        Assert.False(noWebView2.EdgeBlockCombo);
    }

    [Fact]
    public void AssembleEvidenceRequiresTheFullDefenderStrippedCombination()
    {
        var markers = SnapshotBuilder.NoMarkers() with { SmartScreenEnabled = false, CredentialGuardEnabled = false };
        var components = new ComponentPresence(Presence.Absent, Presence.Present, Presence.Present, Presence.Present,
            Presence.Present, Presence.Present, Presence.Present, Presence.Present, Presence.Present,
            Presence.Present, Presence.Present, Presence.Present);
        var services = SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc");
        var updates = new UpdateState(null, false, false, false, false);

        var stripped = ModScoring.AssembleEvidence(components, markers, services, updates,
            new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], false, false));
        Assert.True(stripped.DefenderStrippedCombo);

        // VBS still on breaks the combination.
        var vbsOn = ModScoring.AssembleEvidence(components, markers, services, updates,
            new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], true, false));
        Assert.False(vbsOn.DefenderStrippedCombo);
    }

    [Fact]
    public void AssembleEvidenceCountsOnlyRemovedOrDisabledTelemetryServices()
    {
        var components = SnapshotBuilder.AllComponents(Presence.Present);
        var markers = SnapshotBuilder.NoMarkers();
        var updates = new UpdateState(null, false, false, false, false);
        var virt = new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], null, null);

        var removed = ModScoring.AssembleEvidence(components, markers,
            SnapshotBuilder.AllServices(ServiceState.Disabled, "DiagTrack", "WerSvc"), updates, virt);
        Assert.True(removed.TelemetryDisabled);

        // Merely stopped is normal and does not count.
        var stopped = ModScoring.AssembleEvidence(components, markers,
            SnapshotBuilder.AllServices(ServiceState.Stopped, "DiagTrack", "WerSvc"), updates, virt);
        Assert.False(stopped.TelemetryDisabled);

        var unknown = ModScoring.AssembleEvidence(components, markers,
            SnapshotBuilder.AllServices(ServiceState.Unknown, "DiagTrack", "WerSvc"), updates, virt);
        Assert.False(unknown.TelemetryDisabled);
    }

    [Fact]
    public void AssembleEvidenceNeedsFiftyDeprovisionedPackages()
    {
        var components = SnapshotBuilder.AllComponents(Presence.Present);
        var services = SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc");
        var updates = new UpdateState(null, false, false, false, false);
        var virt = new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], null, null);

        Assert.True(ModScoring.AssembleEvidence(components,
            SnapshotBuilder.NoMarkers() with { DeprovisionedCount = 50 }, services, updates, virt).AppxDeprovisioned);
        Assert.False(ModScoring.AssembleEvidence(components,
            SnapshotBuilder.NoMarkers() with { DeprovisionedCount = 49 }, services, updates, virt).AppxDeprovisioned);
        Assert.False(ModScoring.AssembleEvidence(components,
            SnapshotBuilder.NoMarkers() with { DeprovisionedCount = null }, services, updates, virt).AppxDeprovisioned);
    }

    [Fact]
    public void AssembleEvidenceNeedsBothSecurityHealthHalves()
    {
        var components = SnapshotBuilder.AllComponents(Presence.Present);
        var services = SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc");
        var updates = new UpdateState(null, false, false, false, false);
        var virt = new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], null, null);

        var both = ModScoring.AssembleEvidence(components,
            SnapshotBuilder.NoMarkers() with { SecurityHealthStartupPresent = false, SecurityHealthTrayHidden = true },
            services, updates, virt);
        Assert.True(both.SecurityHealthRemoved);

        // The tray-icon half is never asserted by probes, so the marker stays unobserved in practice.
        var startupOnly = ModScoring.AssembleEvidence(components,
            SnapshotBuilder.NoMarkers() with { SecurityHealthStartupPresent = false, SecurityHealthTrayHidden = null },
            services, updates, virt);
        Assert.False(startupOnly.SecurityHealthRemoved);
    }

    [Fact]
    public void ScoreListsObservedMarkersWithPointsAndStrength()
    {
        var result = ModScoring.Score(Evidence(pause2038: true, edgeAbsent: true), foundationalProbesKnown: true);
        Assert.Equal(3, result.Score);
        Assert.Contains(result.ObservedMarkers, m => m.Id == "S2" && m.Points == 2 && m.IsStrong);
        Assert.Contains(result.ObservedMarkers, m => m.Id == "A1" && m.Points == 1 && !m.IsStrong);
    }

    [Fact]
    public void AllMarkerDisplayKeysAreListed()
    {
        Assert.Equal(17, ModScoring.AllMarkerDisplayKeys.Count);
        Assert.DoesNotContain(ModScoring.AllMarkerDisplayKeys, k => k.Contains("Atlas"));
    }

    // -------------------------------------------------------------- overrides

    [Fact]
    public void OverrideToStockDropsTheBrandAndIsRecorded()
    {
        var score = ModScoring.Score(Evidence(revisionTool: true, pause2038: true), foundationalProbesKnown: true);
        var effective = ModScoring.ApplyOverride(score, ModVerdictOverride.TreatAsStock);
        Assert.Equal(ModVerdict.Stock, effective.Verdict);
        Assert.False(effective.BrandNameable);
        Assert.True(effective.OverrideActive);
        Assert.True(effective.OverrideChangedVerdict);
    }

    [Fact]
    public void OverrideToModifiedNeverCreatesBrandEvidence()
    {
        var score = ModScoring.Score(Evidence(), foundationalProbesKnown: true);
        var effective = ModScoring.ApplyOverride(score, ModVerdictOverride.TreatAsModified);
        Assert.Equal(ModVerdict.ModifiedOther, effective.Verdict);
        Assert.False(effective.BrandNameable);
        Assert.True(effective.OverrideActive);
        Assert.True(effective.OverrideChangedVerdict);
    }

    [Fact]
    public void OverrideThatMatchesTheVerdictIsRecordedButNotAChange()
    {
        var score = ModScoring.Score(Evidence(edgeAbsent: true, oneDriveAbsent: true), foundationalProbesKnown: true);
        var effective = ModScoring.ApplyOverride(score, ModVerdictOverride.TreatAsModified);
        Assert.Equal(ModVerdict.ModifiedOther, effective.Verdict);
        Assert.True(effective.OverrideActive);
        Assert.False(effective.OverrideChangedVerdict);
    }

    [Fact]
    public void NoOverrideLeavesTheDetectedVerdictUntouched()
    {
        var score = ModScoring.Score(Evidence(revisionTool: true, pause2038: true), foundationalProbesKnown: true);
        var effective = ModScoring.ApplyOverride(score, ModVerdictOverride.None);
        Assert.Equal(ModVerdict.ModifiedLikelyReviOS, effective.Verdict);
        Assert.True(effective.BrandNameable);
        Assert.False(effective.OverrideActive);
        Assert.False(effective.OverrideChangedVerdict);
    }

    [Fact]
    public void EvaluateScoresARealisticModifiedSnapshot()
    {
        var snapshot = SnapshotBuilder.Build(
            components: new ComponentPresence(Presence.Present, Presence.Absent, Presence.Present, Presence.Present,
                Presence.Absent, Presence.Present, Presence.Present, Presence.Present, Presence.Present,
                Presence.Present, Presence.Present, Presence.Present),
            markers: SnapshotBuilder.NoMarkers() with { EdgeBlockPolicyEnabled = true },
            services: SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc"),
            updates: new UpdateState(null, false, false, false, false),
            virtualization: new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], true, true));

        // S3 (2) + Edge absent (1) + OneDrive absent (1) = 4, with a strong marker: brand verdict.
        Assert.Equal(4, snapshot.ModScore.Score);
        Assert.Equal(ModVerdict.ModifiedLikelyReviOS, snapshot.ModScore.Verdict);
        Assert.True(snapshot.ModScore.BrandNameable);
    }
}
