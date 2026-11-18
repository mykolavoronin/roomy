using PurgeWindows.Core;
using PurgeWindows.Core.Environment;
using PurgeWindows.Localization;

namespace PurgeWindows.Tests;

/// <summary>
/// Disclosure-text tests: the D-13 brand gating in the verdict copy, the evidence rows,
/// and the gating-decision rows. Assertions are written to hold whether strings resolve
/// from the catalogue or fall back to keys (key resolution itself is covered separately).
/// </summary>
public sealed class EnvironmentDisclosureTests
{
    static ModScoreResult ScoreFor(ModVerdict verdict, int score) => verdict switch
    {
        ModVerdict.ModifiedLikelyReviOS => ModScoring.Score(new ModEvidence
        {
            RevisionTool = true,
            PauseTo2038 = true,
        }, foundationalProbesKnown: true),
        ModVerdict.ModifiedOther => score >= 4
            ? ModScoring.Score(new ModEvidence
            {
                EdgeAbsent = true,
                OneDriveAbsent = true,
                WidgetsAbsent = true,
                XboxAbsent = true,
            }, foundationalProbesKnown: true)
            : ModScoring.Score(new ModEvidence { EdgeAbsent = true, OneDriveAbsent = true },
                foundationalProbesKnown: true),
        ModVerdict.Unknown => ModScoring.Score(new ModEvidence(), foundationalProbesKnown: false),
        _ => ModScoring.Score(new ModEvidence(), foundationalProbesKnown: true),
    };

    static EffectiveModVerdict NoOverride(ModScoreResult score) =>
        ModScoring.ApplyOverride(score, ModVerdictOverride.None);

    [Fact]
    public void BrandVerdictTextNamesReviOS()
    {
        var score = ScoreFor(ModVerdict.ModifiedLikelyReviOS, 4);
        Assert.Equal(ModVerdict.ModifiedLikelyReviOS, score.Verdict);
        Assert.Contains("ReviOS", EnvironmentDisclosure.ModVerdictText(score, NoOverride(score)));
    }

    [Fact]
    public void GenericModifiedTextNeverNamesABrand()
    {
        var score = ScoreFor(ModVerdict.ModifiedOther, 4);
        Assert.Equal(ModVerdict.ModifiedOther, score.Verdict);
        Assert.DoesNotContain("ReviOS", EnvironmentDisclosure.ModVerdictText(score, NoOverride(score)));
    }

    [Fact]
    public void FewMarkersTextSaysComponentsAreMissing()
    {
        var score = ScoreFor(ModVerdict.ModifiedOther, 2);
        var text = EnvironmentDisclosure.ModVerdictText(score, NoOverride(score));
        Assert.Contains("missing", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReviOS", text);
    }

    [Fact]
    public void StockAndUnknownTextsNeverNamesABrand()
    {
        var stock = ScoreFor(ModVerdict.Stock, 0);
        Assert.DoesNotContain("ReviOS", EnvironmentDisclosure.ModVerdictText(stock, NoOverride(stock)));
        var unknown = ScoreFor(ModVerdict.Unknown, 0);
        Assert.DoesNotContain("ReviOS", EnvironmentDisclosure.ModVerdictText(unknown, NoOverride(unknown)));
    }

    [Fact]
    public void OverrideTextNotesTheOverrideAndNeverNamesABrand()
    {
        var stock = ScoreFor(ModVerdict.Stock, 0);
        var forced = ModScoring.ApplyOverride(stock, ModVerdictOverride.TreatAsModified);
        var text = EnvironmentDisclosure.ModVerdictText(stock, forced);
        Assert.Contains("override", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReviOS", text);

        var brand = ScoreFor(ModVerdict.ModifiedLikelyReviOS, 4);
        var forcedStock = ModScoring.ApplyOverride(brand, ModVerdictOverride.TreatAsStock);
        var stockText = EnvironmentDisclosure.ModVerdictText(brand, forcedStock);
        Assert.Contains("override", stockText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReviOS", stockText);
    }

    [Fact]
    public void AvStateTextsAreDistinct()
    {
        var texts = new[]
        {
            EnvironmentDisclosure.AvStateText(AvProductState.Enabled),
            EnvironmentDisclosure.AvStateText(AvProductState.DisabledOrSnoozed),
            EnvironmentDisclosure.AvStateText(AvProductState.OutOfDate),
            EnvironmentDisclosure.AvStateText(AvProductState.Unknown),
        };
        Assert.Equal(4, texts.Distinct().Count());
        Assert.All(texts, t => Assert.NotEmpty(t));
    }

    [Fact]
    public void ReasonTextsAreDistinctAndNullMeansUnknown()
    {
        var texts = new[]
        {
            EnvironmentDisclosure.ReasonText(ProbeUnknownReason.TimedOut),
            EnvironmentDisclosure.ReasonText(ProbeUnknownReason.AccessDenied),
            EnvironmentDisclosure.ReasonText(ProbeUnknownReason.QueryFailed),
            EnvironmentDisclosure.ReasonText(ProbeUnknownReason.NotAvailable),
        };
        Assert.Equal(4, texts.Distinct().Count());
        Assert.Equal(Loc.Get("EnvValueUnknown"), EnvironmentDisclosure.ReasonText(null));
        Assert.Contains(EnvironmentDisclosure.ReasonText(ProbeUnknownReason.TimedOut),
            EnvironmentDisclosure.UnknownText(ProbeUnknownReason.TimedOut));
    }

    [Fact]
    public void BuildEvidenceListsAllTwelveProbesInOrder()
    {
        var rows = EnvironmentDisclosure.BuildEvidence(SnapshotBuilder.Build());
        Assert.Equal(ProbeIds.All, rows.Select(r => r.ProbeId).ToList());
        Assert.All(rows, r =>
        {
            Assert.False(r.IsKnown);
            Assert.NotEmpty(r.Title);
            Assert.Contains("Unknown", r.Detail);
            Assert.Empty(r.DurationText);
        });
    }

    [Fact]
    public void BuildEvidenceShowsKnownDetailsWithDurations()
    {
        var snapshot = SnapshotBuilder.Build(
            os: new OsInfo("Windows 11 Pro", "Professional", "24H2", 26100, 1, null,
                System.Runtime.InteropServices.Architecture.X64, OsChannel.Release),
            antivirus: SnapshotBuilder.NoAv());
        var rows = EnvironmentDisclosure.BuildEvidence(snapshot);
        var osRow = rows[0];
        Assert.True(osRow.IsKnown);
        Assert.Contains("Windows 11 Pro", osRow.Detail);
        Assert.Contains("26100", osRow.Detail);
        Assert.NotEmpty(osRow.DurationText);
        var avRow = rows[3];
        Assert.True(avRow.IsKnown);
        Assert.Equal(Loc.Get("EnvAvNoneRegistered"), avRow.Detail);
    }

    [Fact]
    public void BuildSummaryLinesHasFourLinesInOrder()
    {
        var snapshot = SnapshotBuilder.Build(
            os: new OsInfo("Windows 11 Pro", "Professional", "24H2", 26100, 1, null,
                System.Runtime.InteropServices.Architecture.X64, OsChannel.Release),
            antivirus: SnapshotBuilder.NoAv(),
            isElevated: true);
        var gating = CategoryGate.Gate(CategoryCatalog.Load(), snapshot, ModVerdictOverride.None);
        var lines = EnvironmentDisclosure.BuildSummaryLines(snapshot, gating);
        Assert.Equal(4, lines.Count);
        Assert.Contains("Windows 11 Pro", lines[0]);
        Assert.Equal(Loc.Get("EnvPrivilegeAdmin"), lines[3]);
    }

    [Fact]
    public void OsSummaryTextDegradesGracefully()
    {
        var full = SnapshotBuilder.Known(new OsInfo("Windows 11 Pro", "Professional", "24H2", 26100, 1234, null,
            System.Runtime.InteropServices.Architecture.X64, OsChannel.Release));
        Assert.Contains("24H2", EnvironmentDisclosure.OsSummaryText(full));

        var noVersion = SnapshotBuilder.Known(new OsInfo("Windows 11 Pro", "Professional", null, 26100, null, null,
            System.Runtime.InteropServices.Architecture.X64, OsChannel.Release));
        Assert.Contains("Windows 11 Pro", EnvironmentDisclosure.OsSummaryText(noVersion));

        var buildOnly = SnapshotBuilder.Known(new OsInfo(null, null, null, 26100, null, null, null, OsChannel.Unknown));
        Assert.Contains("26100", EnvironmentDisclosure.OsSummaryText(buildOnly));

        var empty = SnapshotBuilder.Known(new OsInfo(null, null, null, null, null, null, null, OsChannel.Unknown));
        Assert.Equal(Loc.Get("EnvOsUnknown"), EnvironmentDisclosure.OsSummaryText(empty));
    }

    [Fact]
    public void UpdatesDetailSelectsBusyPausedAndOk()
    {
        Assert.Equal(Loc.Get("EnvUpdateSummaryBusy"), EnvironmentDisclosure.UpdatesDetail(
            new UpdateState(null, false, false, true, false)));
        Assert.Equal(Loc.Get("EnvUpdateSummaryBusy"), EnvironmentDisclosure.UpdatesDetail(
            new UpdateState(null, false, false, false, true)));
        Assert.Contains("2038", EnvironmentDisclosure.UpdatesDetail(
            new UpdateState(new DateTimeOffset(2038, 1, 19, 0, 0, 0, TimeSpan.Zero), true, true, false, false)));
        Assert.Equal(Loc.Get("EnvUpdateSummaryPausedUnknown"), EnvironmentDisclosure.UpdatesDetail(
            new UpdateState(null, true, true, false, false)));
        Assert.Equal(Loc.Get("EnvUpdateSummaryOk"), EnvironmentDisclosure.UpdatesDetail(
            new UpdateState(null, false, false, false, false)));
        Assert.Equal(Loc.Get("EnvUpdateSummaryUnknown"), EnvironmentDisclosure.UpdatesDetail(
            new UpdateState(null, null, false, false, false)));
    }

    [Fact]
    public void HardwareDetailAppendsTheLowDiskNote()
    {
        var low = EnvironmentDisclosure.HardwareDetail(
            new HardwareContext([10], true, 17179869184, DriveMedia.Ssd, true, false, 0.05, "NTFS"));
        Assert.Contains(Loc.Get("EnvLowDiskNote"), low);
        Assert.Contains("SSD", low);

        var roomy = EnvironmentDisclosure.HardwareDetail(
            new HardwareContext([3], false, 17179869184, DriveMedia.Hdd, false, false, 0.5, "NTFS"));
        Assert.DoesNotContain(Loc.Get("EnvLowDiskNote"), roomy);
    }

    [Fact]
    public void BuildGatingDecisionsShowsOnlyDecisionsThatChangedSomething()
    {
        var catalog = CategoryCatalog.Load();
        Assert.True(catalog.Loaded);
        var snapshot = SnapshotBuilder.Build(
            updates: new UpdateState(null, true, true, false, false));
        var gating = CategoryGate.Gate(catalog, snapshot, ModVerdictOverride.None);
        var decisions = EnvironmentDisclosure.BuildGatingDecisions(gating);

        var windows = Assert.Single(decisions, d => d.Key == "windows");
        Assert.Equal("E-13", windows.RuleId);
        Assert.NotEmpty(windows.ReasonText);
        Assert.DoesNotContain(decisions, d => d.Key == "temp");
    }
}
