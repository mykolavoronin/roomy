using PurgeWindows.Core;
using PurgeWindows.Core.Environment;
using PurgeWindows.Localization;

namespace PurgeWindows.Tests;

/// <summary>
/// Gating tests (W5 §5.5, E-01–E-26): positive evidence hides/downgrades/disables, Unknown
/// keeps categories offered, overrides apply and are recorded. The real catalog is used
/// wherever the entry exists; a hand-built catalog covers future keys.
/// </summary>
public sealed class CategoryGateTests
{
    static CategoryCatalog RealCatalog()
    {
        var catalog = CategoryCatalog.Load();
        Assert.True(catalog.Loaded);
        return catalog;
    }

    static GatingResult Gate(EnvironmentSnapshot snapshot, CategoryCatalog? catalog = null,
        ModVerdictOverride modOverride = ModVerdictOverride.None) =>
        CategoryGate.Gate(catalog ?? RealCatalog(), snapshot, modOverride);

    static ComponentPresence Components(
        Presence defender = Presence.Present, Presence edge = Presence.Present,
        Presence webView2 = Presence.Present, Presence store = Presence.Present,
        Presence oneDrive = Presence.Present, Presence widgets = Presence.Present,
        Presence xbox = Presence.Present) => new(
        defender, edge, webView2, store, oneDrive, widgets, xbox,
        Presence.Present, Presence.Present, Presence.Present, Presence.Present, Presence.Present);

    static EnvironmentSnapshot AllUnknown() => SnapshotBuilder.Build();

    // -------------------------------------------------------------- E-01–E-05 (Defender / AV)

    [Fact]
    public void E01DefenderStrippedHidesDefenderHistory()
    {
        var gating = Gate(SnapshotBuilder.Build(components: Components(defender: Presence.Absent)));
        var offer = Assert.NotNull(gating.OfferFor("defender-history"));
        Assert.Equal(GateOutcome.Hidden, offer.Outcome);
        Assert.Equal("E-01", offer.RuleId);
        Assert.Equal("EnvGateDefenderStripped", offer.ReasonKey);
    }

    [Fact]
    public void E01UnknownComponentsKeepDefenderHistoryOffered()
    {
        var gating = Gate(AllUnknown());
        var offer = Assert.NotNull(gating.OfferFor("defender-history"));
        Assert.Equal(GateOutcome.Offered, offer.Outcome);
        Assert.Null(offer.ReasonKey);
    }

    [Fact]
    public void E02ThirdPartyActiveHidesDefenderHistoryAndNamesTheAv()
    {
        var av = SnapshotBuilder.WithProducts(
            new AvProduct("Windows Defender", AvProductState.DisabledOrSnoozed, AvSignatureStatus.Unknown, true, null),
            new AvProduct("Malwarebytes", AvProductState.Enabled, AvSignatureStatus.UpToDate, false, null));
        var gating = Gate(SnapshotBuilder.Build(components: Components(), antivirus: av));
        var offer = Assert.NotNull(gating.OfferFor("defender-history"));
        Assert.Equal(GateOutcome.Hidden, offer.Outcome);
        Assert.Equal("E-02", offer.RuleId);
        Assert.Equal("EnvGateDefenderDisabled", offer.ReasonKey);
        Assert.Equal("Malwarebytes", Assert.Single(offer.ReasonArgs));
    }

    [Fact]
    public void E02DoesNotFireWhenDefenderIsEnabled()
    {
        var av = SnapshotBuilder.WithProducts(
            new AvProduct("Windows Defender", AvProductState.Enabled, AvSignatureStatus.UpToDate, true, null),
            new AvProduct("Malwarebytes", AvProductState.Enabled, AvSignatureStatus.UpToDate, false, null));
        var gating = Gate(SnapshotBuilder.Build(components: Components(), antivirus: av));
        var offer = Assert.NotNull(gating.OfferFor("defender-history"));
        Assert.Equal(GateOutcome.Offered, offer.Outcome);
        // E-04 advises instead: a third-party AV is active.
        Assert.Equal("EnvGateThirdPartyAv", offer.AdvisoryKey);
    }

    [Fact]
    public void E03DefenderSoleActiveAdvisesRetention()
    {
        var av = SnapshotBuilder.WithProducts(
            new AvProduct("Windows Defender", AvProductState.Enabled, AvSignatureStatus.UpToDate, true, null));
        var gating = Gate(SnapshotBuilder.Build(components: Components(), antivirus: av));
        var offer = Assert.NotNull(gating.OfferFor("defender-history"));
        Assert.Equal(GateOutcome.Offered, offer.Outcome);
        Assert.Equal("EnvGateDefenderRetention", offer.AdvisoryKey);
        Assert.Equal("E-03", offer.RuleId);
    }

    [Fact]
    public void E04ThirdPartyActiveAdvisesBeforeAvAdjacentRows()
    {
        var av = SnapshotBuilder.WithProducts(
            new AvProduct("Malwarebytes", AvProductState.Enabled, AvSignatureStatus.UpToDate, false, null));
        var gating = Gate(SnapshotBuilder.Build(components: Components(defender: Presence.Unknown), antivirus: av));
        var offer = Assert.NotNull(gating.OfferFor("defender-history"));
        Assert.Equal(GateOutcome.Offered, offer.Outcome);
        Assert.Equal("EnvGateThirdPartyAv", offer.AdvisoryKey);
        Assert.Equal("Malwarebytes", Assert.Single(offer.AdvisoryArgs));
    }

    [Fact]
    public void E04UnknownAvAdvisesWithoutAlarm()
    {
        var gating = Gate(AllUnknown());
        var offer = Assert.NotNull(gating.OfferFor("defender-history"));
        Assert.Equal(GateOutcome.Offered, offer.Outcome);
        Assert.Equal("EnvGateAvUnknown", offer.AdvisoryKey);
    }

    [Fact]
    public void E05NoAvRegisteredHasNoGateEffectAndNoNag()
    {
        var gating = Gate(SnapshotBuilder.Build(antivirus: SnapshotBuilder.NoAv()));
        var offer = Assert.NotNull(gating.OfferFor("defender-history"));
        Assert.Equal(GateOutcome.Offered, offer.Outcome);
        Assert.Null(offer.AdvisoryKey);
        Assert.Null(offer.ReasonKey);
    }

    // -------------------------------------------------------------- E-06–E-11 (components)

    [Fact]
    public void E06EdgeRemovedHidesEdgeRows()
    {
        var gating = Gate(SnapshotBuilder.Build(components: Components(edge: Presence.Absent)),
            CatalogBuilder.RealPlusFuture());
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("edge-cache")).Outcome);
        Assert.Equal("E-06", gating.OfferFor("edge-cache")!.RuleId);
    }

    [Fact]
    public void E07WebView2AbsentHidesWebView2RowsButNotTeamsCache()
    {
        var gating = Gate(SnapshotBuilder.Build(components: Components(webView2: Presence.Absent)),
            CatalogBuilder.RealPlusFuture());
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("webview2-cache")).Outcome);
        // teams-cache holds mixed content and is deliberately never hidden by the gate.
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(gating.OfferFor("teams-cache")).Outcome);
    }

    [Fact]
    public void E08StoreRemovedHidesStoreCache()
    {
        var gating = Gate(SnapshotBuilder.Build(components: Components(store: Presence.Absent)));
        var offer = Assert.NotNull(gating.OfferFor("store-cache"));
        Assert.Equal(GateOutcome.Hidden, offer.Outcome);
        Assert.Equal("E-08", offer.RuleId);
        Assert.Equal("Microsoft Store", Assert.Single(offer.ReasonArgs));
    }

    [Fact]
    public void E09OneDriveRemovedHidesOneDriveRows()
    {
        var gating = Gate(SnapshotBuilder.Build(components: Components(oneDrive: Presence.Absent)),
            CatalogBuilder.RealPlusFuture());
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("onedrive-cache")).Outcome);
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("onedrive-dehydration")).Outcome);
    }

    [Fact]
    public void E10WidgetsRemovedHidesWidgetsRow()
    {
        var gating = Gate(SnapshotBuilder.Build(components: Components(widgets: Presence.Absent)),
            CatalogBuilder.RealPlusFuture());
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("widgets-cache")).Outcome);
    }

    [Fact]
    public void E11XboxRemovedHidesXboxRowButNotCaptures()
    {
        var gating = Gate(SnapshotBuilder.Build(components: Components(xbox: Presence.Absent)),
            CatalogBuilder.RealPlusFuture());
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("xbox-cache")).Outcome);
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(gating.OfferFor("game-captures")).Outcome);
    }

    [Fact]
    public void FutureKeysHaveNoOfferUntilTheirEntryLands()
    {
        var gating = Gate(SnapshotBuilder.Build(components: Components(
            edge: Presence.Absent, webView2: Presence.Absent, oneDrive: Presence.Absent,
            widgets: Presence.Absent, xbox: Presence.Absent)));
        Assert.Null(gating.OfferFor("edge-cache"));
        Assert.Null(gating.OfferFor("webview2-cache"));
        Assert.Null(gating.OfferFor("onedrive-cache"));
        Assert.Null(gating.OfferFor("onedrive-dehydration"));
        Assert.Null(gating.OfferFor("widgets-cache"));
        Assert.Null(gating.OfferFor("xbox-cache"));
    }

    // -------------------------------------------------------------- E-12–E-14 (mod / updates)

    [Fact]
    public void E12ModifiedVerdictSetsTheGlobalRespectFlag()
    {
        var modified = SnapshotBuilder.Build(
            components: Components(edge: Presence.Absent, oneDrive: Presence.Absent),
            markers: SnapshotBuilder.NoMarkers(),
            services: SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc"),
            updates: new UpdateState(null, false, false, false, false),
            virtualization: new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], null, null));
        Assert.Equal(ModVerdict.ModifiedOther, modified.ModScore.Verdict);
        Assert.True(Gate(modified).ModRespectApplies);

        var stock = SnapshotBuilder.Build(
            components: Components(),
            markers: SnapshotBuilder.NoMarkers(),
            services: SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc"),
            updates: new UpdateState(null, false, false, false, false),
            virtualization: new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], null, null));
        Assert.Equal(ModVerdict.Stock, stock.ModScore.Verdict);
        Assert.False(Gate(stock).ModRespectApplies);
    }

    [Fact]
    public void E13PausedUpdatesHideUpdateSensitiveCategories()
    {
        var gating = Gate(SnapshotBuilder.Build(
            updates: new UpdateState(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), true, true, false, false)));
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("windows")).Outcome);
        Assert.Equal("E-13", gating.OfferFor("windows")!.RuleId);
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("winold")).Outcome);
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("windows-update-download")).Outcome);
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("bits-jobs")).Outcome);
        // E-13 does not cover the log categories; E-14 does.
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(gating.OfferFor("winlogs")).Outcome);
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(gating.OfferFor("diagnostics")).Outcome);
    }

    [Fact]
    public void E14UpdateBusyHidesServicingCategoriesAndWinsOverE13()
    {
        var gating = Gate(SnapshotBuilder.Build(
            updates: new UpdateState(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), true, true, true, true)));
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("windows")).Outcome);
        Assert.Equal("E-14", gating.OfferFor("windows")!.RuleId);
        Assert.Equal("EnvGateUpdateBusy", gating.OfferFor("windows")!.ReasonKey);
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("winlogs")).Outcome);
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("diagnostics")).Outcome);
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("cbs-logs")).Outcome);
    }

    [Fact]
    public void E14PendingRebootFromP12HidesServicingCategories()
    {
        var gating = Gate(SnapshotBuilder.Build(
            pending: new PendingOperations(true, false, false)));
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("windows")).Outcome);
        Assert.Equal("E-14", gating.OfferFor("windows")!.RuleId);
    }

    [Fact]
    public void E14InstallerRunningHidesServicingCategories()
    {
        var gating = Gate(SnapshotBuilder.Build(
            pending: new PendingOperations(false, true, false)));
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("winlogs")).Outcome);
    }

    [Fact]
    public void E13AndE14DoNotFireOnUnknownUpdateState()
    {
        var gating = Gate(AllUnknown());
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(gating.OfferFor("windows")).Outcome);
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(gating.OfferFor("winlogs")).Outcome);
    }

    // -------------------------------------------------------------- E-15–E-19

    [Fact]
    public void E15StorageSenseOnUnselectsTempAndRecycleWithAnAdvisory()
    {
        var gating = Gate(SnapshotBuilder.Build(storageSense: new StorageSenseInfo(StorageSenseState.On)));
        var temp = Assert.NotNull(gating.OfferFor("temp"));
        Assert.Equal(GateOutcome.Offered, temp.Outcome);
        Assert.False(temp.DefaultSelectedOverride == false);
        Assert.Equal("EnvGateStorageSense", temp.AdvisoryKey);
        var recycle = Assert.NotNull(gating.OfferFor("recycle"));
        Assert.False(recycle.DefaultSelectedOverride == false);
        Assert.Equal("EnvGateStorageSense", recycle.AdvisoryKey);
    }

    [Fact]
    public void E16StorageSenseOffLeavesNormalOffersWithNoNag()
    {
        var gating = Gate(SnapshotBuilder.Build(storageSense: new StorageSenseInfo(StorageSenseState.Off)));
        var temp = Assert.NotNull(gating.OfferFor("temp"));
        Assert.Null(temp.DefaultSelectedOverride);
        Assert.Null(temp.AdvisoryKey);
    }

    [Fact]
    public void E17PlaceholdersAdvisoryFiresOnlyWithPositivePlaceholderEvidence()
    {
        var catalog = CatalogBuilder.RealPlusFuture();
        var withPlaceholders = Gate(SnapshotBuilder.Build(
            oneDrive: new OneDriveInfo(1, Presence.Present, true, true)), catalog);
        Assert.Equal("EnvGatePlaceholders",
            Assert.NotNull(withPlaceholders.OfferFor("onedrive-dehydration")).AdvisoryKey);

        // Placeholders unknown (what the probe always reports): no advisory.
        var unknown = Gate(SnapshotBuilder.Build(
            oneDrive: new OneDriveInfo(1, Presence.Present, true, null)), catalog);
        Assert.Null(Assert.NotNull(unknown.OfferFor("onedrive-dehydration")).AdvisoryKey);
    }

    [Fact]
    public void E18OnDemandOffHidesDehydrationRows()
    {
        var catalog = CatalogBuilder.RealPlusFuture();
        var gating = Gate(SnapshotBuilder.Build(
            oneDrive: new OneDriveInfo(1, Presence.Present, false, null)), catalog);
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("onedrive-dehydration")).Outcome);

        var unknown = Gate(SnapshotBuilder.Build(
            oneDrive: new OneDriveInfo(1, Presence.Present, null, null)), catalog);
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(unknown.OfferFor("onedrive-dehydration")).Outcome);
    }

    [Fact]
    public void E19ManagedMachinesShowRowsWithNoAction()
    {
        var gating = Gate(SnapshotBuilder.Build(
            managed: new ManagedInfo(true, false, false, false)));
        foreach (var key in new[] { "firewall-log", "branchcache", "event-logs", "defender-history", "registry-cleaning" })
        {
            var offer = Assert.NotNull(gating.OfferFor(key));
            Assert.Equal(GateOutcome.Disabled, offer.Outcome);
            Assert.Equal("E-19", offer.RuleId);
            Assert.Equal("EnvGateManaged", offer.ReasonKey);
        }
    }

    [Fact]
    public void E19DoesNotFireWhenNotManaged()
    {
        var gating = Gate(SnapshotBuilder.Build(
            managed: new ManagedInfo(false, false, false, false)));
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(gating.OfferFor("firewall-log")).Outcome);
    }

    [Fact]
    public void HiddenBeatsDisabledForTheSameEntry()
    {
        var gating = Gate(SnapshotBuilder.Build(
            components: Components(defender: Presence.Absent),
            managed: new ManagedInfo(true, false, false, false)));
        var offer = Assert.NotNull(gating.OfferFor("defender-history"));
        Assert.Equal(GateOutcome.Hidden, offer.Outcome);
        Assert.Equal("E-01", offer.RuleId);
    }

    // -------------------------------------------------------------- E-20–E-26

    [Fact]
    public void E20HyperVAbsentHidesHyperVRows()
    {
        var gating = Gate(SnapshotBuilder.Build(
            virtualization: new VirtualizationInfo(HyperVState.Absent, WslState.Present, ["Ubuntu"], null, null)));
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("hyperv-optimize")).Outcome);
    }

    [Fact]
    public void E21WslAbsentHidesWslRows()
    {
        var gating = Gate(SnapshotBuilder.Build(
            virtualization: new VirtualizationInfo(HyperVState.Enabled, WslState.Absent, [], null, null)));
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("orphaned-wsl")).Outcome);
        Assert.Equal(GateOutcome.Hidden, Assert.NotNull(gating.OfferFor("wsl-compact")).Outcome);
    }

    [Fact]
    public void E20AndE21DoNotFireOnUnknownVirtualization()
    {
        var gating = Gate(AllUnknown());
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(gating.OfferFor("hyperv-optimize")).Outcome);
        Assert.Equal(GateOutcome.Offered, Assert.NotNull(gating.OfferFor("orphaned-wsl")).Outcome);
    }

    [Fact]
    public void E22VbsDisabledHasNoCategoryEffect()
    {
        var gating = Gate(SnapshotBuilder.Build(
            virtualization: new VirtualizationInfo(HyperVState.Enabled, WslState.Present, [], false, false)));
        Assert.DoesNotContain(gating.Offers, o => o.Outcome != GateOutcome.Offered && o.AdvisoryKey is null);
        Assert.Empty(gating.Offers.Where(o => o.Outcome != GateOutcome.Offered));
    }

    [Fact]
    public void E23LaptopWithBatteryAdvisesOnHibernation()
    {
        var gating = Gate(SnapshotBuilder.Build(
            hardware: new HardwareContext([10], true, null, DriveMedia.Unknown, true, false, 0.5, "NTFS")));
        var offer = Assert.NotNull(gating.OfferFor("hibernation"));
        Assert.Equal(GateOutcome.Offered, offer.Outcome);
        Assert.Equal("EnvGateHibernationLaptop", offer.AdvisoryKey);

        var desktop = Gate(SnapshotBuilder.Build(
            hardware: new HardwareContext([3], false, null, DriveMedia.Unknown, false, false, 0.5, "NTFS")));
        Assert.Null(Assert.NotNull(desktop.OfferFor("hibernation")).AdvisoryKey);
    }

    [Fact]
    public void E24Windows10HasNoGateEffect()
    {
        var gating = Gate(SnapshotBuilder.Build(
            os: new OsInfo("Windows 10 Pro", "Professional", "22H2", 19045, 1, null, System.Runtime.InteropServices.Architecture.X64, OsChannel.Release)));
        Assert.Empty(gating.Offers.Where(o => o.Outcome != GateOutcome.Offered));
    }

    [Fact]
    public void E25Arm64HasNoGateEffect()
    {
        var gating = Gate(SnapshotBuilder.Build(
            os: new OsInfo("Windows 11 Pro", "Professional", "24H2", 26100, 1, null, System.Runtime.InteropServices.Architecture.Arm64, OsChannel.Release)));
        Assert.Empty(gating.Offers.Where(o => o.Outcome != GateOutcome.Offered));
    }

    [Fact]
    public void E26LowDiskSetsTheGlobalFlagWithoutChangingOffers()
    {
        var low = Gate(SnapshotBuilder.Build(
            hardware: new HardwareContext([], null, null, DriveMedia.Unknown, null, null, 0.05, "NTFS")));
        Assert.True(low.LowDiskApplies);
        Assert.Empty(low.Offers.Where(o => o.Outcome != GateOutcome.Offered));

        var roomy = Gate(SnapshotBuilder.Build(
            hardware: new HardwareContext([], null, null, DriveMedia.Unknown, null, null, 0.5, "NTFS")));
        Assert.False(roomy.LowDiskApplies);
    }

    // -------------------------------------------------------------- override + empty gating

    [Fact]
    public void OverrideAppliesAndIsRecordedInTheGatingOutput()
    {
        var modified = SnapshotBuilder.Build(
            components: Components(edge: Presence.Absent, oneDrive: Presence.Absent),
            markers: SnapshotBuilder.NoMarkers(),
            services: SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc"),
            updates: new UpdateState(null, false, false, false, false),
            virtualization: new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], null, null));
        var gating = Gate(modified, modOverride: ModVerdictOverride.TreatAsStock);
        Assert.Equal(ModVerdict.Stock, gating.EffectiveVerdict.Verdict);
        Assert.True(gating.OverrideActive);
        Assert.True(gating.OverrideChangedVerdict);
        Assert.False(gating.ModRespectApplies);

        var stock = SnapshotBuilder.Build(
            components: Components(),
            markers: SnapshotBuilder.NoMarkers(),
            services: SnapshotBuilder.AllServices(ServiceState.Running, "DiagTrack", "WerSvc"),
            updates: new UpdateState(null, false, false, false, false),
            virtualization: new VirtualizationInfo(HyperVState.Unknown, WslState.Unknown, [], null, null));
        var forced = Gate(stock, modOverride: ModVerdictOverride.TreatAsModified);
        Assert.Equal(ModVerdict.ModifiedOther, forced.EffectiveVerdict.Verdict);
        Assert.False(forced.EffectiveVerdict.BrandNameable);
        Assert.True(forced.ModRespectApplies);
    }

    [Fact]
    public void EmptyGatingOffersEverything()
    {
        var gating = GatingResult.Empty(RealCatalog().Entries);
        Assert.All(gating.Offers, o => Assert.Equal(GateOutcome.Offered, o.Outcome));
        Assert.False(gating.ModRespectApplies);
        Assert.False(gating.LowDiskApplies);
    }

    // -------------------------------------------------------------- ApplyToScan mechanics

    static CleanupCategory ScanCategory(string id, SafetyLevel tier = SafetyLevel.Safe,
        CleanAction action = CleanAction.Delete, bool selectedByDefault = true, string? note = null)
    {
        var category = new CleanupCategory
        {
            Id = id, Title = id, Description = id, Glyph = "x",
            Tier = tier, Action = action, SelectedByDefault = selectedByDefault, Note = note,
        };
        category.Items.Add(new CleanupItem { Name = "item", Paths = [@"C:\x"], Size = 100 });
        return category;
    }

    [Fact]
    public void ApplyToScanDropsHiddenCategoriesWithoutMutatingTheInput()
    {
        var windows = ScanCategory("windows", SafetyLevel.CheckFirst);
        var temp = ScanCategory("temp");
        var input = new List<CleanupCategory> { windows, temp };
        var gating = Gate(SnapshotBuilder.Build(
            updates: new UpdateState(null, true, true, false, false)));

        var gated = CategoryGate.ApplyToScan(input, gating);

        Assert.DoesNotContain(gated, c => c.Id == "windows");
        Assert.Contains(gated, c => c.Id == "temp");
        Assert.Equal(2, input.Count);
    }

    [Fact]
    public void ApplyToScanDisablesByCloningToReportOnlyWithTheReason()
    {
        var entry = CatalogBuilder.Entry("temp", "category", "temp", SafetyLevel.Safe);
        var offer = new GatedOffer(entry, GateOutcome.Disabled, SafetyLevel.Safe,
            "EnvGateManaged", [], null, [], null, "E-19");
        var gating = new GatingResult([offer],
            new Dictionary<string, GatedOffer>(StringComparer.OrdinalIgnoreCase) { ["temp"] = offer },
            new EffectiveModVerdict(ModVerdict.Stock, false, false, false), false, false);
        var temp = ScanCategory("temp");

        var gated = CategoryGate.ApplyToScan([temp], gating);

        var clone = Assert.Single(gated);
        Assert.NotSame(temp, clone);
        Assert.Equal(CleanAction.ReportOnly, clone.Action);
        Assert.Equal(SafetyLevel.Safe, clone.Tier);
        Assert.NotNull(clone.Note);
        Assert.Contains("organisation", clone.Note!);
        Assert.Single(clone.Items);
        // The input is untouched.
        Assert.Equal(CleanAction.Delete, temp.Action);
        Assert.Null(temp.Note);
    }

    [Fact]
    public void ApplyToScanDowngradesSafeToCheckFirstByCloning()
    {
        var entry = CatalogBuilder.Entry("temp", "category", "temp", SafetyLevel.Safe);
        var offer = new GatedOffer(entry, GateOutcome.Downgraded, SafetyLevel.CheckFirst,
            "EnvGateManaged", [], null, [], null, "E-99");
        var gating = new GatingResult([offer],
            new Dictionary<string, GatedOffer>(StringComparer.OrdinalIgnoreCase) { ["temp"] = offer },
            new EffectiveModVerdict(ModVerdict.Stock, false, false, false), false, false);
        var temp = ScanCategory("temp");

        var gated = CategoryGate.ApplyToScan([temp], gating);

        var clone = Assert.Single(gated);
        Assert.Equal(SafetyLevel.CheckFirst, clone.Tier);
        Assert.Equal(SafetyLevel.Safe, temp.Tier);
        Assert.NotNull(clone.Note);
    }

    [Fact]
    public void ApplyToScanAppendsAdvisoriesAndAppliesDefaultSelectionOverrides()
    {
        var gating = Gate(SnapshotBuilder.Build(storageSense: new StorageSenseInfo(StorageSenseState.On)));
        var temp = ScanCategory("temp", note: "scanner note");

        var gated = CategoryGate.ApplyToScan([temp], gating);

        var clone = Assert.Single(gated);
        Assert.NotSame(temp, clone);
        Assert.False(clone.SelectedByDefault);
        Assert.True(temp.SelectedByDefault);
        Assert.Equal("scanner note\nStorage Sense already handles this.", clone.Note!);
    }

    [Fact]
    public void ApplyToScanPassesUnaffectedCategoriesThroughByReference()
    {
        var gating = Gate(AllUnknown());
        var temp = ScanCategory("temp");
        // The all-Unknown snapshot advises on defender-history only; temp is unaffected.
        // (E-04's unknown-AV advisory targets defender-history, not temp.)
        var gated = CategoryGate.ApplyToScan([temp], gating);
        Assert.Same(temp, Assert.Single(gated));
    }

    // -------------------------------------------------------------- string keys resolve

    [Fact]
    public void EveryNewStringKeyResolvesToText()
    {
        var keys = new[]
        {
            "EnvOsFormat", "EnvOsFormatNoVersion", "EnvOsBuildOnly", "EnvOsUnknown",
            "EnvModStock", "EnvModComponentsMissing", "EnvModModifiedOther", "EnvModLikelyReviOS",
            "EnvModUnknown", "EnvOverrideActiveNote",
            "EnvAvNoneRegistered", "EnvAvStateEnabled", "EnvAvStateDisabled", "EnvAvStateOutOfDate",
            "EnvAvStateUnknown", "EnvAvProductFormat",
            "EnvPrivilegeAdmin", "EnvPrivilegeStandard",
            "EnvValueUnknown", "EnvValueOn", "EnvValueOff", "EnvValueEnabled", "EnvValuePresent", "EnvValueAbsent",
            "EnvWin10Notice",
            "EnvProbeOs", "EnvProbeComponents", "EnvProbeModMarkers", "EnvProbeAntivirus", "EnvProbeServices",
            "EnvProbeUpdates", "EnvProbeStorageSense", "EnvProbeOneDrive", "EnvProbeManaged",
            "EnvProbeVirtualization", "EnvProbeHardware", "EnvProbePending",
            "EnvProbeReasonTimedOut", "EnvProbeReasonAccessDenied", "EnvProbeReasonQueryFailed",
            "EnvProbeReasonNotAvailable", "EnvProbeUnknownWithReason",
            "EnvComponentsSummary", "EnvMarkersObserved", "EnvServicesSummary",
            "EnvUpdateSummaryOk", "EnvUpdateSummaryUnknown", "EnvUpdateSummaryPaused", "EnvUpdateSummaryPausedUnknown", "EnvUpdateSummaryBusy",
            "EnvHardwareSummary", "EnvHardwareLaptop", "EnvHardwareDesktop", "EnvHardwareUnknown",
            "EnvPendingNone", "EnvPendingSomething", "EnvMarkerEvidenceTitle", "EnvProbeChecked",
            "EnvStorageSenseOn", "EnvStorageSenseOff", "EnvOneDriveSummary",
            "EnvManagedYes", "EnvManagedNo", "EnvVirtSummary", "EnvLowDiskNote",
            "EnvGateDefenderStripped", "EnvGateDefenderDisabled", "EnvGateDefenderRetention",
            "EnvGateThirdPartyAv", "EnvGateAvUnknown", "EnvGateComponentAbsent",
            "EnvGateUpdatesPaused", "EnvGateUpdateBusy", "EnvGateOnDemandOff",
            "EnvGateHyperVAbsent", "EnvGateWslAbsent", "EnvGateManaged", "EnvGateStorageSense",
            "EnvGatePlaceholders", "EnvGateHibernationLaptop",
            "EnvGateOutcomeHidden", "EnvGateOutcomeDisabled", "EnvGateOutcomeDowngraded", "EnvGateOutcomeOffered",
            "EnvOverrideNone", "EnvOverrideStock", "EnvOverrideModified",
            "EnvYourSetupTitle", "EnvRecheck", "EnvDetecting",
        };
        foreach (var key in keys)
            Assert.NotEqual(key, Loc.Get(key));
        foreach (var key in ModScoring.AllMarkerDisplayKeys)
            Assert.NotEqual(key, Loc.Get(key));
    }

    [Fact]
    public void FormatKeysAcceptTheirCallSiteArgumentCounts()
    {
        Assert.Contains("AV-Name", Loc.Format("EnvGateDefenderDisabled", "AV-Name"));
        Assert.Contains("AV-Name", Loc.Format("EnvGateThirdPartyAv", "AV-Name"));
        Assert.Contains("Edge", Loc.Format("EnvGateComponentAbsent", "Edge"));
        Assert.Contains("2038", Loc.Format("EnvUpdateSummaryPaused", "2038"));
        Assert.Contains("2", Loc.Format("EnvMarkersObserved", 2));
        Assert.Contains("7", Loc.Format("EnvProbeChecked", 7));
        Assert.Contains("reason", Loc.Format("EnvProbeUnknownWithReason", "reason"));
        Assert.Contains("Defender", Loc.Format("EnvAvProductFormat", "Defender", "on"));
        Assert.Contains("26100", Loc.Format("EnvOsFormat", "Windows 11", "24H2", "26100"));
        Assert.Contains("26100", Loc.Format("EnvOsFormatNoVersion", "Windows 11", "26100"));
        Assert.Contains("26100", Loc.Format("EnvOsBuildOnly", "26100"));
        Assert.Contains("1", Loc.Format("EnvComponentsSummary", 1, 2, 3));
        Assert.Contains("2", Loc.Format("EnvServicesSummary", 1, 2, 3));
        Assert.Contains("Laptop", Loc.Format("EnvHardwareSummary", "Laptop", "16 GB", "SSD"));
        Assert.Contains("2", Loc.Format("EnvOneDriveSummary", "2", "On"));
        Assert.Contains("WSL", Loc.Format("EnvVirtSummary", "Enabled", "WSL", "On"));
    }
}
