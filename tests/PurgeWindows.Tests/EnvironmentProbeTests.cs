using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PurgeWindows.Core.Environment;

namespace PurgeWindows.Tests;

/// <summary>
/// Probe tests against canned fake sources — never the live machine. Each test sets up
/// exactly the signals its scenario needs and asserts the parsed outcome.
/// </summary>
public sealed class EnvironmentProbeTests
{
    const string NtVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string UxSettingsKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    static readonly CancellationToken None = CancellationToken.None;

    // -------------------------------------------------------------- P-01

    [Fact]
    public void OsProbeReadsEditionBuildAndChannel()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetValue(RegistryHive.LocalMachine, NtVersionKey, "ProductName", "Windows 11 Pro");
        fake.SetValue(RegistryHive.LocalMachine, NtVersionKey, "EditionID", "Professional");
        fake.SetValue(RegistryHive.LocalMachine, NtVersionKey, "DisplayVersion", "24H2");
        fake.SetValue(RegistryHive.LocalMachine, NtVersionKey, "CurrentBuild", "26100");
        fake.SetValue(RegistryHive.LocalMachine, NtVersionKey, "UBR", 1234);
        fake.SetValue(RegistryHive.LocalMachine, NtVersionKey, "BuildLabEx", "26100.1.amd64fre");
        fake.SetKeyMissing(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WindowsSelfHost");
        fake.SystemInfoValue = new SystemInfo(null, Architecture.X64, "AMD64", false, []);

        var outcome = EnvironmentProbes.ProbeOs(fake, None);

        Assert.True(outcome.IsKnown);
        var os = Assert.NotNull(outcome.KnownValue);
        Assert.Equal("Windows 11 Pro", os.ProductName);
        Assert.Equal("Professional", os.EditionId);
        Assert.Equal("24H2", os.DisplayVersion);
        Assert.Equal(26100, os.Build);
        Assert.Equal(1234, os.Ubr);
        Assert.Equal("26100.1234", os.BuildText);
        Assert.True(os.IsWindows11);
        Assert.False(os.IsWindows10);
        Assert.False(os.IsArm64);
        Assert.Equal(OsChannel.Release, os.Channel);
        Assert.Equal(Architecture.X64, os.OsArchitecture);
    }

    [Fact]
    public void OsProbeFallsBackToOsVersionWhenRegistryFails()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SystemInfoValue = new SystemInfo(new Version(10, 0, 19045), Architecture.X64, "AMD64", false, []);

        var outcome = EnvironmentProbes.ProbeOs(fake, None);

        Assert.True(outcome.IsKnown);
        Assert.Equal(19045, outcome.KnownValue!.Build);
        Assert.True(outcome.KnownValue.IsWindows10);
        Assert.Equal(OsChannel.Unknown, outcome.KnownValue.Channel);
    }

    [Fact]
    public void OsProbeIsUnknownWhenNothingIsReadable()
    {
        var outcome = EnvironmentProbes.ProbeOs(new FakeEnvironmentDataSource(), None);
        Assert.False(outcome.IsKnown);
        Assert.Equal(ProbeUnknownReason.QueryFailed, outcome.UnknownReason);
    }

    [Theory]
    [InlineData("26100", 26100)]
    [InlineData(" 19045 ", 19045)]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void ParseBuildNumberHandlesGoodAndBadInput(string? text, int? expected) =>
        Assert.Equal(expected, EnvironmentProbes.ParseBuildNumber(text));

    [Theory]
    [InlineData("ARM64", Architecture.X64, Architecture.Arm64)]
    [InlineData("AMD64", Architecture.Arm64, Architecture.X64)]
    [InlineData("amd64", null, Architecture.X64)]
    [InlineData("x86", null, Architecture.X86)]
    [InlineData("mips", Architecture.X64, Architecture.X64)]
    [InlineData(null, Architecture.Arm64, Architecture.Arm64)]
    [InlineData(null, null, null)]
    public void ParseArchitecturePrefersTheEnvironmentVariable(string? env, Architecture? osArch, Architecture? expected) =>
        Assert.Equal(expected, EnvironmentProbes.ParseArchitecture(env, osArch));

    [Fact]
    public void CancelledProbeReportsTimedOut()
    {
        var outcome = EnvironmentProbes.ProbeOs(new FakeEnvironmentDataSource(), new CancellationToken(canceled: true));
        Assert.False(outcome.IsKnown);
        Assert.Equal(ProbeUnknownReason.TimedOut, outcome.UnknownReason);
    }

    // -------------------------------------------------------------- P-02

    static FakeEnvironmentDataSource StockComponentsFake()
    {
        var fake = new FakeEnvironmentDataSource
        {
            Packages = PackageQueryResult.Ok(
                ["Microsoft.WindowsStore_8wekyb3d8bbwe",
                 "MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy",
                 "Microsoft.GamingServices_8wekyb3d8bbwe",
                 "Microsoft.XboxApp_8wekyb3d8bbwe",
                 "Microsoft.YourPhone_8wekyb3d8bbwe",
                 "Microsoft.Windows.Photos_8wekyb3d8bbwe",
                 "MSTeams_8wekyb3d8bbwe",
                 "Microsoft.Windows.SecHealthUI_cw5n1h2txyewy"],
                []),
            FileCheck = path => path.EndsWith("MsMpEng.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("OneDrive.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("SearchIndexer.exe", StringComparison.OrdinalIgnoreCase)
                ? Presence.Present : Presence.Absent,
            DirectoryEnumeration = (directory, _) => FileSearchResult.Ok([directory + @"\120.0.0.0"]),
        };
        fake.Services["WinDefend"] = ServiceState.Running;
        fake.Services["WSearch"] = ServiceState.Running;
        fake.SetKey(RegistryHive.CurrentUser, @"Software\SyncEngines\Providers\OneDrive", ["{guid}"], []);
        return fake;
    }

    [Fact]
    public void ComponentsProbeFindsAStockMachine()
    {
        var outcome = EnvironmentProbes.ProbeComponents(StockComponentsFake(), None);

        Assert.True(outcome.IsKnown);
        var c = Assert.NotNull(outcome.KnownValue);
        Assert.Equal(Presence.Present, c.Defender);
        Assert.Equal(Presence.Present, c.Edge);
        Assert.Equal(Presence.Present, c.WebView2);
        Assert.Equal(Presence.Present, c.Store);
        Assert.Equal(Presence.Present, c.OneDrive);
        Assert.Equal(Presence.Present, c.Widgets);
        Assert.Equal(Presence.Present, c.Xbox);
        Assert.Equal(Presence.Present, c.Search);
        Assert.Equal(Presence.Present, c.PhoneLink);
        Assert.Equal(Presence.Present, c.Teams);
        Assert.Equal(Presence.Present, c.Photos);
        // No Copilot package was listed, so Recall/Copilot is proven absent.
        Assert.Equal(Presence.Absent, c.RecallCopilot);
    }

    [Fact]
    public void ComponentsProbeDistinguishesEdgeAbsentFromWebView2Present()
    {
        var fake = new FakeEnvironmentDataSource
        {
            DirectoryEnumeration = (directory, _) => directory.Contains("EdgeWebView", StringComparison.OrdinalIgnoreCase)
                ? FileSearchResult.Ok([directory + @"\120.0.0.0"])
                : FileSearchResult.Ok([]),
            FileCheck = path => path.EndsWith("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)
                ? Presence.Present : Presence.Absent,
        };

        var outcome = EnvironmentProbes.ProbeComponents(fake, None);

        Assert.True(outcome.IsKnown);
        Assert.Equal(Presence.Absent, outcome.KnownValue!.Edge);
        Assert.Equal(Presence.Present, outcome.KnownValue.WebView2);
    }

    [Fact]
    public void ComponentsProbeReportsDefenderAbsentOnlyWhenEverySignalSaysSo()
    {
        var fake = new FakeEnvironmentDataSource
        {
            Packages = PackageQueryResult.Ok([], []),
            FileCheck = _ => Presence.Absent,
            DirectoryEnumeration = (_, _) => FileSearchResult.Ok([]),
        };
        fake.Services["WinDefend"] = ServiceState.NotInstalled;

        var outcome = EnvironmentProbes.ProbeComponents(fake, None);

        Assert.True(outcome.IsKnown);
        Assert.Equal(Presence.Absent, outcome.KnownValue!.Defender);
    }

    [Fact]
    public void ComponentsProbeReportsDefenderUnknownWhenTheServiceCannotBeChecked()
    {
        var fake = new FakeEnvironmentDataSource
        {
            Packages = PackageQueryResult.Ok([], []),
            FileCheck = _ => Presence.Absent,
            DirectoryEnumeration = (_, _) => FileSearchResult.Ok([]),
        };
        // WinDefend is left Unknown: the service could not be checked, so absence
        // cannot be claimed even though the binaries and the package are missing.

        var outcome = EnvironmentProbes.ProbeComponents(fake, None);

        Assert.True(outcome.IsKnown);
        Assert.Equal(Presence.Unknown, outcome.KnownValue!.Defender);
    }

    [Fact]
    public void ComponentsProbeIsUnknownWhenNothingCanBeChecked()
    {
        var outcome = EnvironmentProbes.ProbeComponents(new FakeEnvironmentDataSource(), None);
        Assert.False(outcome.IsKnown);
    }

    // -------------------------------------------------------------- P-03

    [Fact]
    public void ModProbeFindsRevisionToolInUninstallRecords()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetKey(RegistryHive.LocalMachine, UninstallKey, ["RevisionTool"], [], RegistryView.Registry64);
        fake.SetValue(RegistryHive.LocalMachine, UninstallKey + @"\RevisionTool", "DisplayName", "Revision Tool", RegistryView.Registry64);

        var outcome = EnvironmentProbes.ProbeModMarkers(fake, None);

        Assert.True(outcome.IsKnown);
        var markers = Assert.NotNull(outcome.KnownValue);
        Assert.True(markers.RevisionToolFound == true);
        Assert.False(markers.AmeWizardFound == false);
        Assert.False(markers.DebloatToolFound == false);
        Assert.Null(markers.SecurityHealthTrayHidden);
        Assert.Null(markers.DeprovisionedCount);
        Assert.Null(markers.DataUsageBroken);
    }

    [Fact]
    public void ModProbeFindsAmeWizardInUninstallRecords()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetKey(RegistryHive.LocalMachine, UninstallKey, ["AmeWizard"], [], RegistryView.Registry64);
        fake.SetValue(RegistryHive.LocalMachine, UninstallKey + @"\AmeWizard", "DisplayName", "AME Wizard", RegistryView.Registry64);

        var outcome = EnvironmentProbes.ProbeModMarkers(fake, None);

        Assert.True(outcome.KnownValue!.AmeWizardFound == true);
        Assert.False(outcome.KnownValue.RevisionToolFound == false);
    }

    [Fact]
    public void ModProbeFindsDebloatToolsByDisplayNameAndPublisher()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetKey(RegistryHive.LocalMachine, UninstallKey, ["ShutUp", "Titus"], [], RegistryView.Registry64);
        fake.SetValue(RegistryHive.LocalMachine, UninstallKey + @"\ShutUp", "DisplayName", "O&O ShutUp10", RegistryView.Registry64);
        fake.SetValue(RegistryHive.LocalMachine, UninstallKey + @"\Titus", "DisplayName", "Windows Utility", RegistryView.Registry64);
        fake.SetValue(RegistryHive.LocalMachine, UninstallKey + @"\Titus", "Publisher", "Chris Titus Tech", RegistryView.Registry64);

        var outcome = EnvironmentProbes.ProbeModMarkers(fake, None);

        Assert.True(outcome.KnownValue!.DebloatToolFound == true);
    }

    [Fact]
    public void ModProbeFindsRevisionToolOnPath()
    {
        var fake = new FakeEnvironmentDataSource
        {
            PathDirectoriesValue = [@"C:\Tools"],
            FileCheck = path => path.EndsWith("revitool.exe", StringComparison.OrdinalIgnoreCase)
                ? Presence.Present : Presence.Absent,
        };

        var outcome = EnvironmentProbes.ProbeModMarkers(fake, None);

        Assert.True(outcome.KnownValue!.RevisionToolFound == true);
        // Uninstall records were unreadable, so those readings stay null.
        Assert.Null(outcome.KnownValue.AmeWizardFound);
        Assert.Null(outcome.KnownValue.DebloatToolFound);
    }

    [Fact]
    public void ModProbeFindsRevisionToolViaDesktopShortcut()
    {
        var fake = new FakeEnvironmentDataSource
        {
            SystemInfoValue = new SystemInfo(null, null, null, false, [@"C:\Users\Test\Desktop"]),
            FileEnumeration = (directory, _, _) => FileSearchResult.Ok([directory + @"\Revision Tool.lnk"]),
            ShortcutTarget = _ => @"C:\Program Files\Revision\revitool.exe",
        };

        var outcome = EnvironmentProbes.ProbeModMarkers(fake, None);

        Assert.True(outcome.KnownValue!.RevisionToolFound == true);
    }

    [Fact]
    public void ModProbeReadsPolicyAndStartupSignals()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\EdgeUpdate", "DoNotUpdateToEdgeWithChromium", 1);
        fake.SetValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled", "Off");
        fake.SetValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags", 2);
        fake.SetKey(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", [], ["SecurityHealth"]);

        var outcome = EnvironmentProbes.ProbeModMarkers(fake, None);

        Assert.True(outcome.IsKnown);
        var markers = Assert.NotNull(outcome.KnownValue);
        Assert.True(markers.EdgeBlockPolicyEnabled == true);
        Assert.False(markers.SmartScreenEnabled == false);
        Assert.True(markers.CredentialGuardEnabled == true);
        Assert.True(markers.SecurityHealthStartupPresent == true);
    }

    [Fact]
    public void ModProbeTreatsMissingSmartScreenKeyAsOnAndMissingLsaAsOff()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetValueMissing(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled");
        fake.SetValueMissing(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags");
        fake.SetKeyMissing(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");

        var outcome = EnvironmentProbes.ProbeModMarkers(fake, None);

        Assert.True(outcome.KnownValue!.SmartScreenEnabled == true);
        Assert.False(outcome.KnownValue.CredentialGuardEnabled == false);
        Assert.False(outcome.KnownValue.SecurityHealthStartupPresent == false);
    }

    [Fact]
    public void ModProbeIsUnknownWhenNothingCanBeChecked()
    {
        var outcome = EnvironmentProbes.ProbeModMarkers(new FakeEnvironmentDataSource(), None);
        Assert.False(outcome.IsKnown);
    }

    // -------------------------------------------------------------- P-04

    [Theory]
    [InlineData(397568u, AvProductState.Enabled)]
    [InlineData(397312u, AvProductState.Enabled)]
    [InlineData(393216u, AvProductState.Enabled)]
    [InlineData(262144u, AvProductState.DisabledOrSnoozed)]
    [InlineData(327680u, AvProductState.DisabledOrSnoozed)]
    [InlineData(0u, AvProductState.Unknown)]
    [InlineData(65536u, AvProductState.Unknown)]
    public void DecodeProductStateUsesOnlyTheDocumentedRanges(uint productState, AvProductState expected) =>
        Assert.Equal(expected, AntivirusState.DecodeProductState(productState));

    [Theory]
    [InlineData(0, 1, AvProductState.Enabled, AvSignatureStatus.UpToDate)]
    [InlineData(0, 0, AvProductState.OutOfDate, AvSignatureStatus.OutOfDate)]
    [InlineData(0, 99, AvProductState.Enabled, AvSignatureStatus.Unknown)]
    [InlineData(1, 1, AvProductState.DisabledOrSnoozed, AvSignatureStatus.UpToDate)]
    [InlineData(2, 1, AvProductState.DisabledOrSnoozed, AvSignatureStatus.UpToDate)]
    [InlineData(3, 1, AvProductState.DisabledOrSnoozed, AvSignatureStatus.UpToDate)]
    [InlineData(9, 1, AvProductState.Unknown, AvSignatureStatus.UpToDate)]
    public void FromWscMapsStateAndSignatures(int state, int signature, AvProductState expectedState, AvSignatureStatus expectedSignature)
    {
        var (product, sig) = AntivirusState.FromWsc(state, signature);
        Assert.Equal(expectedState, product);
        Assert.Equal(expectedSignature, sig);
    }

    [Theory]
    [InlineData("Windows Defender", null, true)]
    [InlineData("Something", @"C:\Program Files\Windows Defender\MsMpEng.exe", true)]
    [InlineData("Malwarebytes", null, false)]
    [InlineData(null, null, false)]
    public void IsDefenderProductChecksExeThenName(string? name, string? exe, bool expected) =>
        Assert.Equal(expected, AntivirusState.IsDefenderProduct(name, exe));

    [Fact]
    public void AntivirusProbePrefersWscAndDoesNotFallBackOnSuccess()
    {
        var fake = new FakeEnvironmentDataSource
        {
            WscQuery = provider => provider == WscProductSource.ProviderAntivirus
                ? WscQueryResult.Ok([new WscProductRow("Windows Defender", 0, 1), new WscProductRow("Malwarebytes", 0, 1)])
                : WscQueryResult.Failed(),
            WmiQuery = (_, _, _) => WmiQueryResult.Failed(),
        };

        var outcome = EnvironmentProbes.ProbeAntivirus(fake, None);

        Assert.True(outcome.IsKnown);
        var av = Assert.NotNull(outcome.KnownValue);
        Assert.Equal(AvSource.Wsc, av.Source);
        Assert.Equal(2, av.Products.Count);
        Assert.True(av.Products[0].IsDefender);
        Assert.Equal(AvProductState.Enabled, av.Products[0].State);
        Assert.Equal(AvSignatureStatus.UpToDate, av.Products[0].SignatureStatus);
        Assert.False(av.Products[1].IsDefender);
        Assert.True(av.HasThirdPartyActive);
        Assert.False(av.DefenderSoleActive);
        Assert.False(av.FirewallChecked);
        Assert.DoesNotContain(fake.WmiQueriesSeen, q => q.Contains("AntiVirusProduct"));
    }

    [Fact]
    public void AntivirusProbeTreatsEmptyWscAsNoneRegisteredNotFailure()
    {
        var fake = new FakeEnvironmentDataSource { WscQuery = _ => WscQueryResult.Ok([]) };

        var outcome = EnvironmentProbes.ProbeAntivirus(fake, None);

        Assert.True(outcome.IsKnown);
        Assert.True(outcome.KnownValue!.NoProductsRegistered);
        Assert.DoesNotContain(fake.WmiQueriesSeen, q => q.Contains("AntiVirusProduct"));
    }

    [Fact]
    public void AntivirusProbeFallsBackToWmiWhenWscFails()
    {
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, query, _) => query.Contains("AntiVirusProduct")
                ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(
                    ("displayName", "Windows Defender"),
                    ("productState", 397568u),
                    ("pathToSignedProductExe", @"C:\Program Files\Windows Defender\MsMpEng.exe"))])
                : query.Contains("FirewallProduct")
                    ? WmiQueryResult.Ok([])
                    : query.Contains("MSFT_MpComputerStatus")
                        ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("RealTimeProtectionEnabled", true))])
                        : WmiQueryResult.Failed(),
        };
        fake.SetValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows Defender\Features", "TamperProtection", 1);

        var outcome = EnvironmentProbes.ProbeAntivirus(fake, None);

        Assert.True(outcome.IsKnown);
        var av = Assert.NotNull(outcome.KnownValue);
        Assert.Equal(AvSource.Wmi, av.Source);
        var defender = Assert.Single(av.Products);
        Assert.True(defender.IsDefender);
        Assert.Equal(AvProductState.Enabled, defender.State);
        Assert.Equal(AvSignatureStatus.Unknown, defender.SignatureStatus);
        Assert.True(av.FirewallChecked);
        Assert.True(av.DefenderSoleActive);
        Assert.True(av.DefenderRealTimeEnabled == true);
        Assert.True(av.TamperProtectionEnabled == true);
    }

    [Fact]
    public void AntivirusProbeIsUnknownWhenBothSourcesFail()
    {
        var outcome = EnvironmentProbes.ProbeAntivirus(new FakeEnvironmentDataSource(), None);
        Assert.False(outcome.IsKnown);
        Assert.Equal(ProbeUnknownReason.QueryFailed, outcome.UnknownReason);
    }

    // -------------------------------------------------------------- P-05

    [Fact]
    public void ServicesProbeReadsTheSubsetAndPerUserInstances()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.Services["wuauserv"] = ServiceState.Running;
        fake.Services["BITS"] = ServiceState.Stopped;
        fake.Services["DoSvc"] = ServiceState.Disabled;
        fake.Services["WinDefend"] = ServiceState.NotInstalled;
        fake.ServiceNameSearches["WpnUserService_"] = ["WpnUserService_123"];
        fake.Services["WpnUserService_123"] = ServiceState.Running;

        var outcome = EnvironmentProbes.ProbeServices(fake, None);

        Assert.True(outcome.IsKnown);
        var services = Assert.NotNull(outcome.KnownValue);
        Assert.Equal(ServiceState.Running, services.Get("wuauserv"));
        Assert.Equal(ServiceState.Stopped, services.Get("BITS"));
        Assert.Equal(ServiceState.Disabled, services.Get("DoSvc"));
        Assert.Equal(ServiceState.NotInstalled, services.Get("WinDefend"));
        Assert.Equal(ServiceState.Running, services.Get("WpnUserService_123"));
        Assert.Equal(ServiceState.Unknown, services.Get("SomethingElse"));
    }

    [Fact]
    public void ServicesProbeIsUnknownWhenNothingCanBeChecked()
    {
        var outcome = EnvironmentProbes.ProbeServices(new FakeEnvironmentDataSource(), None);
        Assert.False(outcome.IsKnown);
    }

    // -------------------------------------------------------------- P-06

    [Fact]
    public void UpdatesProbeDetectsAPauseTo2038()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetValue(RegistryHive.LocalMachine, UxSettingsKey, "PauseUpdatesExpiryTime", "2038-01-19T00:00:00Z");
        fake.SetKeyMissing(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        fake.SetKeyMissing(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        fake.SetValueMissing(RegistryHive.LocalMachine, SessionManagerKey, "PendingFileRenameOperations");
        fake.Processes = ProcessQueryResult.Ok([]);

        var outcome = EnvironmentProbes.ProbeUpdates(fake, None);

        Assert.True(outcome.IsKnown);
        var updates = Assert.NotNull(outcome.KnownValue);
        Assert.True(updates.IsPaused == true);
        Assert.True(updates.PauseValuesPresent);
        Assert.Equal(2038, updates.PauseExpiry!.Value.Year);
        Assert.True(updates.PauseEnds2038);
        Assert.False(updates.UpdateInProgress == false);
        Assert.False(updates.PendingReboot == false);
    }

    [Fact]
    public void UpdatesProbeParsesFileTimePauseValues()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetValue(RegistryHive.LocalMachine, UxSettingsKey, "PauseUpdatesExpiryTime",
            new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero).ToFileTime());
        fake.Processes = ProcessQueryResult.Ok([]);

        var outcome = EnvironmentProbes.ProbeUpdates(fake, None);

        Assert.True(outcome.KnownValue!.IsPaused == true);
        Assert.Equal(2030, outcome.KnownValue.PauseExpiry!.Value.Year);
        Assert.False(outcome.KnownValue.PauseEnds2038);
    }

    [Fact]
    public void UpdatesProbeTreatsExpiredPausesAsNotPausedAndGarbageAsUnknown()
    {
        var expired = new FakeEnvironmentDataSource();
        expired.SetValue(RegistryHive.LocalMachine, UxSettingsKey, "PauseUpdatesExpiryTime", "2020-01-01T00:00:00Z");
        expired.Processes = ProcessQueryResult.Ok([]);
        Assert.False(EnvironmentProbes.ProbeUpdates(expired, None).KnownValue!.IsPaused == false);

        var garbage = new FakeEnvironmentDataSource();
        garbage.SetValue(RegistryHive.LocalMachine, UxSettingsKey, "PauseUpdatesExpiryTime", "not-a-date");
        garbage.Processes = ProcessQueryResult.Ok([]);
        var parsed = EnvironmentProbes.ProbeUpdates(garbage, None).KnownValue!;
        Assert.Null(parsed.IsPaused);
        Assert.Null(parsed.PauseExpiry);
        Assert.True(parsed.PauseValuesPresent);
    }

    [Fact]
    public void UpdatesProbeReadsPendingRebootAndInProgressSignals()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetKey(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", [], []);
        fake.Processes = ProcessQueryResult.Ok(["explorer", "TiWorker"]);

        var outcome = EnvironmentProbes.ProbeUpdates(fake, None);

        Assert.True(outcome.KnownValue!.PendingReboot == true);
        Assert.True(outcome.KnownValue.UpdateInProgress == true);
        Assert.False(outcome.KnownValue.IsPaused == false);
        Assert.False(outcome.KnownValue.PauseValuesPresent);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, null)]
    public void ComputePauseStateHandlesMissingAndUnparseableValues(bool valuesPresent, bool? expectedPaused)
    {
        var (_, isPaused) = EnvironmentProbes.ComputePauseState([], valuesPresent, DateTimeOffset.UtcNow);
        Assert.Equal(expectedPaused, isPaused);
    }

    [Fact]
    public void ComputePauseStateUsesTheLatestEndTime()
    {
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var (expiry, isPaused) = EnvironmentProbes.ComputePauseState(
            [now.AddDays(-1), now.AddDays(7)], valuesPresent: true, now);
        Assert.Equal(now.AddDays(7), expiry);
        Assert.True(isPaused == true);
    }

    // -------------------------------------------------------------- P-07

    [Fact]
    public void StorageSenseProbePrefersPolicyOverThePerUserHint()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\StorageSense", "AllowStorageSenseGlobal", 0);
        fake.SetValue(RegistryHive.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1);

        var outcome = EnvironmentProbes.ProbeStorageSense(fake, None);

        Assert.True(outcome.IsKnown);
        Assert.Equal(StorageSenseState.Off, outcome.KnownValue!.State);
    }

    [Fact]
    public void StorageSenseProbeUsesTheHintWhenNoPolicyExists()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetValue(RegistryHive.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1);

        Assert.Equal(StorageSenseState.On,
            EnvironmentProbes.ProbeStorageSense(fake, None).KnownValue!.State);
    }

    [Fact]
    public void StorageSenseProbeIsUnknownWhenNothingCanBeChecked()
    {
        Assert.False(EnvironmentProbes.ProbeStorageSense(new FakeEnvironmentDataSource(), None).IsKnown);
    }

    // -------------------------------------------------------------- P-08

    [Fact]
    public void OneDriveProbeCountsProvidersAndReadsFilesOnDemand()
    {
        var fake = new FakeEnvironmentDataSource
        {
            FileCheck = path => path.EndsWith("OneDrive.exe", StringComparison.OrdinalIgnoreCase)
                ? Presence.Present : Presence.Absent,
        };
        fake.SetKey(RegistryHive.CurrentUser, @"Software\SyncEngines\Providers\OneDrive", ["p1", "p2"], []);
        fake.SetValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\OneDrive", "FilesOnDemandEnabled", 1);

        var outcome = EnvironmentProbes.ProbeOneDrive(fake, None);

        Assert.True(outcome.IsKnown);
        var drive = Assert.NotNull(outcome.KnownValue);
        Assert.Equal(2, drive.ProviderCount);
        Assert.Equal(Presence.Present, drive.OneDrivePresent);
        Assert.True(drive.FilesOnDemand == true);
        Assert.Null(drive.HasPlaceholders);
    }

    [Fact]
    public void OneDriveProbeLeavesFilesOnDemandUnknownWithoutThePolicy()
    {
        var fake = new FakeEnvironmentDataSource();
        fake.SetKeyMissing(RegistryHive.CurrentUser, @"Software\SyncEngines\Providers\OneDrive");

        var outcome = EnvironmentProbes.ProbeOneDrive(fake, None);

        Assert.True(outcome.IsKnown);
        Assert.Equal(0, outcome.KnownValue!.ProviderCount);
        Assert.Null(outcome.KnownValue.FilesOnDemand);
    }

    // -------------------------------------------------------------- P-09

    [Fact]
    public void ManagedProbeReadsDomainMdmAndEndpointSignals()
    {
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, query, _) => query.Contains("Win32_ComputerSystem")
                ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("PartOfDomain", true), ("DomainRole", 1))])
                : WmiQueryResult.Failed(),
        };
        fake.SetKey(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Enrollments", ["abc"], []);
        fake.Services["Sense"] = ServiceState.Running;
        fake.Services["IntuneManagementExtension"] = ServiceState.NotInstalled;

        var outcome = EnvironmentProbes.ProbeManaged(fake, None);

        Assert.True(outcome.IsKnown);
        var managed = Assert.NotNull(outcome.KnownValue);
        Assert.True(managed.DomainJoined == true);
        Assert.True(managed.MdmEnrolled == true);
        Assert.True(managed.DefenderForEndpoint == true);
        Assert.False(managed.IntuneExtension == false);
        Assert.True(managed.IsManaged);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(9, null)]
    public void ManagedProbeInfersDomainMembershipFromDomainRole(int role, bool? expected)
    {
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, _, _) => WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("DomainRole", role))]),
        };

        var outcome = EnvironmentProbes.ProbeManaged(fake, None);

        Assert.Equal(expected, outcome.KnownValue!.DomainJoined);
    }

    [Fact]
    public void ManagedProbeIsNotManagedWhenEverySignalSaysNo()
    {
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, _, _) => WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("PartOfDomain", false), ("DomainRole", 0))]),
            FileCheck = _ => Presence.Absent,
        };
        fake.SetKeyMissing(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Enrollments");
        fake.Services["Sense"] = ServiceState.NotInstalled;
        fake.Services["IntuneManagementExtension"] = ServiceState.NotInstalled;

        var managed = EnvironmentProbes.ProbeManaged(fake, None).KnownValue!;
        Assert.False(managed.IsManaged);
    }

    // -------------------------------------------------------------- P-10

    [Theory]
    [InlineData(2, 1, true, true)]
    [InlineData(0, 0, false, false)]
    [InlineData(1, 0, false, false)]
    [InlineData(9, 9, null, null)]
    [InlineData(null, null, null, null)]
    public void DecodeDeviceGuardMapsOnlyKnownValues(int? vbs, int? hvci, bool? expectedVbs, bool? expectedHvci)
    {
        var (vbsEnabled, hvciEnabled) = EnvironmentProbes.DecodeDeviceGuard(vbs, hvci);
        Assert.Equal(expectedVbs, vbsEnabled);
        Assert.Equal(expectedHvci, hvciEnabled);
    }

    [Theory]
    [InlineData("Ubuntu\r\nDebian\r\n", new[] { "Ubuntu", "Debian" })]
    [InlineData("", new string[] { })]
    [InlineData("Ubuntu\0\n\nDebian\n", new[] { "Ubuntu", "Debian" })]
    [InlineData("  Ubuntu  \n", new[] { "Ubuntu" })]
    public void ParseWslOutputSplitsAndCleansLines(string stdout, string[] expected) =>
        Assert.Equal(expected, EnvironmentProbes.ParseWslOutput(stdout));

    [Fact]
    public void DecodeCommandOutputHandlesUtf16AndUtf8()
    {
        Assert.Equal("Ubuntu", WindowsEnvironmentDataSource.DecodeCommandOutput(
            Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Ubuntu")).ToArray()));
        Assert.Equal("Ubuntu", WindowsEnvironmentDataSource.DecodeCommandOutput(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Ubuntu")).ToArray()));
        Assert.Equal("Ubuntu", WindowsEnvironmentDataSource.DecodeCommandOutput(Encoding.UTF8.GetBytes("Ubuntu")));
        Assert.Equal("Ubuntu", WindowsEnvironmentDataSource.DecodeCommandOutput(Encoding.Unicode.GetBytes("Ubuntu")));
    }

    [Fact]
    public void VirtualizationProbeReadsHyperVAndWsl()
    {
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, query, _) => query.Contains("Win32_OptionalFeature")
                ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("InstallState", 1))])
                : query.Contains("Win32_DeviceGuard")
                    ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(
                        ("VirtualizationBasedSecurityStatus", 2),
                        ("CodeIntegrityPolicyEnforcementStatus", 1))])
                    : WmiQueryResult.Failed(),
            FileCheck = path => path.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase)
                ? Presence.Present : Presence.Absent,
            CommandRun = (_, _, _) => CommandResult.Ran(0, "Ubuntu\r\nDebian\r\n"),
        };
        fake.Services["vmms"] = ServiceState.Stopped;
        fake.SetKey(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss", ["{guid}"], []);

        var outcome = EnvironmentProbes.ProbeVirtualization(fake, None);

        Assert.True(outcome.IsKnown);
        var virt = Assert.NotNull(outcome.KnownValue);
        Assert.Equal(HyperVState.Enabled, virt.HyperV);
        Assert.Equal(WslState.Present, virt.Wsl);
        Assert.Equal(["Ubuntu", "Debian"], virt.DistroNames);
        Assert.True(virt.VbsEnabled == true);
        Assert.True(virt.HvciEnabled == true);
    }

    [Fact]
    public void VirtualizationProbeReportsHyperVAbsentOnlyOnJointEvidence()
    {
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, query, _) => query.Contains("Win32_OptionalFeature")
                ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("InstallState", 2))])
                : WmiQueryResult.Failed(),
            FileCheck = _ => Presence.Absent,
        };
        fake.Services["vmms"] = ServiceState.NotInstalled;
        fake.SetKeyMissing(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss");

        var virt = EnvironmentProbes.ProbeVirtualization(fake, None).KnownValue!;
        Assert.Equal(HyperVState.Absent, virt.HyperV);
        Assert.Equal(WslState.Absent, virt.Wsl);
    }

    [Fact]
    public void VirtualizationProbeDoesNotRunWslWhenTheExeIsAbsent()
    {
        var commandsRun = 0;
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, _, _) => WmiQueryResult.Ok([]),
            FileCheck = _ => Presence.Absent,
            CommandRun = (_, _, _) => { commandsRun++; return CommandResult.Ran(0, ""); },
        };
        fake.SetKeyMissing(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss");

        var virt = EnvironmentProbes.ProbeVirtualization(fake, None).KnownValue!;
        Assert.Equal(WslState.Absent, virt.Wsl);
        Assert.Equal(0, commandsRun);
    }

    [Fact]
    public void VirtualizationProbeFallsBackToLxssWhenTheWslCommandFails()
    {
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, _, _) => WmiQueryResult.Ok([]),
            FileCheck = path => path.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase)
                ? Presence.Present : Presence.Absent,
            CommandRun = (_, _, _) => CommandResult.Failed(),
        };
        fake.SetKey(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss", ["{guid}"], []);

        Assert.Equal(WslState.Present,
            EnvironmentProbes.ProbeVirtualization(fake, None).KnownValue!.Wsl);
    }

    // -------------------------------------------------------------- P-11

    [Fact]
    public void HardwareProbeReadsChassisMemoryDiskAndBattery()
    {
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, query, _) => query.Contains("Win32_SystemEnclosure")
                ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("ChassisTypes", new object[] { 10 }))])
                : query.Contains("Win32_ComputerSystem")
                    ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("TotalPhysicalMemory", 17179869184L))])
                    : query.Contains("MSFT_PhysicalDisk")
                        ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("MediaType", 4))])
                        : query.Contains("Win32_Battery")
                            ? WmiQueryResult.Ok([SnapshotBuilder.WmiRow(("BatteryStatus", 1))])
                            : WmiQueryResult.Failed(),
            DriveValue = new DriveData(true, 1000, 50, "NTFS"),
            PowerValue = new PowerStatus(true, true),
        };

        var outcome = EnvironmentProbes.ProbeHardware(fake, None);

        Assert.True(outcome.IsKnown);
        var hardware = Assert.NotNull(outcome.KnownValue);
        Assert.Equal([10], hardware.ChassisTypes);
        Assert.True(hardware.IsLaptop == true);
        Assert.Equal(17179869184L, hardware.TotalRamBytes);
        Assert.Equal(DriveMedia.Ssd, hardware.SystemDriveMedia);
        Assert.True(hardware.BatteryPresent == true);
        Assert.True(hardware.IsOnBattery == true);
        Assert.True(hardware.IsLowDiskSpace);
        Assert.Equal("NTFS", hardware.SystemDriveFileSystem);
    }

    [Fact]
    public void HardwareProbeLeavesDiskTypeUnknownWithSeveralDisks()
    {
        var fake = new FakeEnvironmentDataSource
        {
            WmiQuery = (_, query, _) => query.Contains("MSFT_PhysicalDisk")
                ? WmiQueryResult.Ok([
                    SnapshotBuilder.WmiRow(("MediaType", 4)),
                    SnapshotBuilder.WmiRow(("MediaType", 3))])
                : WmiQueryResult.Ok([]),
        };

        var hardware = EnvironmentProbes.ProbeHardware(fake, None).KnownValue!;
        Assert.Equal(DriveMedia.Unknown, hardware.SystemDriveMedia);
        Assert.Null(hardware.IsLaptop);
        Assert.False(hardware.IsLowDiskSpace);
    }

    [Theory]
    [InlineData(4, DriveMedia.Ssd)]
    [InlineData(3, DriveMedia.Hdd)]
    [InlineData(0, DriveMedia.Unknown)]
    [InlineData(null, DriveMedia.Unknown)]
    public void MediaTypeMapping(int? mediaType, DriveMedia expected) =>
        Assert.Equal(expected, EnvironmentProbes.MediaTypeToDriveMedia(mediaType));

    // -------------------------------------------------------------- P-12

    [Fact]
    public void PendingProbeReadsInstallersBackupsAndReboots()
    {
        var fake = new FakeEnvironmentDataSource
        {
            Processes = ProcessQueryResult.Ok(["explorer", "msiexec", "wbengine"]),
        };
        fake.SetValue(RegistryHive.LocalMachine, SessionManagerKey, "PendingFileRenameOperations", new[] { "a", "b" });

        var outcome = EnvironmentProbes.ProbePending(fake, None);

        Assert.True(outcome.IsKnown);
        var pending = Assert.NotNull(outcome.KnownValue);
        Assert.True(pending.InstallerRunning == true);
        Assert.True(pending.BackupRunning == true);
        Assert.True(pending.PendingReboot == true);
    }

    [Fact]
    public void PendingProbeIgnoresAnEmptyPendingRenameValue()
    {
        var fake = new FakeEnvironmentDataSource { Processes = ProcessQueryResult.Ok(["explorer"]) };
        fake.SetKeyMissing(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        fake.SetKeyMissing(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        fake.SetValue(RegistryHive.LocalMachine, SessionManagerKey, "PendingFileRenameOperations", Array.Empty<string>());

        var pending = EnvironmentProbes.ProbePending(fake, None).KnownValue!;
        Assert.False(pending.PendingReboot == false);
    }

    [Fact]
    public void PendingProbeReportsNothingWhenIdle()
    {
        var fake = new FakeEnvironmentDataSource { Processes = ProcessQueryResult.Ok(["explorer"]) };
        fake.SetKeyMissing(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        fake.SetKeyMissing(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        fake.SetValueMissing(RegistryHive.LocalMachine, SessionManagerKey, "PendingFileRenameOperations");

        var pending = EnvironmentProbes.ProbePending(fake, None).KnownValue!;
        Assert.False(pending.InstallerRunning == false);
        Assert.False(pending.BackupRunning == false);
        Assert.False(pending.PendingReboot == false);
    }
}
