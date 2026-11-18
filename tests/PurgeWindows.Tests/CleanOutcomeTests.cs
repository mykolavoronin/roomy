using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// The failure/outcome taxonomies: resolve rules, figure display, stable raw values,
/// skip-reason mapping, and the retry rule.
/// </summary>
public sealed class CleanOutcomeTests
{
    static CleanFailure Fail(CleanFailureReason reason) => new(@"C:\x\file.bin", 100, reason);

    [Fact]
    public void ResolveCoversAllFiveCases()
    {
        Assert.Equal(CleanOutcome.Success, CleanOutcomes.Resolve(3, []));
        Assert.Equal(CleanOutcome.NothingLeft, CleanOutcomes.Resolve(0, []));
        Assert.Equal(CleanOutcome.Partial, CleanOutcomes.Resolve(2, [Fail(CleanFailureReason.InUse)]));
        Assert.Equal(CleanOutcome.Failed, CleanOutcomes.Resolve(0, [Fail(CleanFailureReason.InUse)]));
        Assert.Equal(CleanOutcome.SkippedOnly,
            CleanOutcomes.Resolve(0, [Fail(CleanFailureReason.SafetySkipped), Fail(CleanFailureReason.SafetySkipped)]));
        // One non-safety failure keeps it Failed even with safety skips present.
        Assert.Equal(CleanOutcome.Failed,
            CleanOutcomes.Resolve(0, [Fail(CleanFailureReason.SafetySkipped), Fail(CleanFailureReason.InUse)]));
    }

    [Fact]
    public void OnlySuccessAndPartialShowAFigure()
    {
        Assert.True(CleanOutcomes.ShowsFigure(CleanOutcome.Success));
        Assert.True(CleanOutcomes.ShowsFigure(CleanOutcome.Partial));
        Assert.False(CleanOutcomes.ShowsFigure(CleanOutcome.Failed));
        Assert.False(CleanOutcomes.ShowsFigure(CleanOutcome.SkippedOnly));
        Assert.False(CleanOutcomes.ShowsFigure(CleanOutcome.NothingLeft));
    }

    [Fact]
    public void RawValuesRoundTripAndUnknownReadsAsUnknown()
    {
        foreach (var reason in Enum.GetValues<CleanFailureReason>())
            Assert.Equal(reason, CleanFailureReasons.FromRawValue(CleanFailureReasons.RawValue(reason)));
        Assert.Equal(CleanFailureReason.Unknown, CleanFailureReasons.FromRawValue("no-such-reason"));
        Assert.Equal(CleanFailureReason.Unknown, CleanFailureReasons.FromRawValue(null));
        Assert.Equal(CleanFailureReason.Unknown, CleanFailureReasons.FromRawValue(""));
    }

    [Fact]
    public void FromSkipReasonMapsPolicyRefusalsToSafetySkips()
    {
        Assert.Equal(CleanFailureReason.SafetySkipped, CleanFailureReasons.FromSkipReason(SkipReason.PolicyRefused));
        Assert.Equal(CleanFailureReason.InUse, CleanFailureReasons.FromSkipReason(SkipReason.InUse));
        Assert.Equal(CleanFailureReason.SystemProtected, CleanFailureReasons.FromSkipReason(SkipReason.Protected));
    }

    [Fact]
    public void ShowsRetryFollowsThePurgeRule()
    {
        Assert.True(CleanFailureReasons.ShowsRetry(CleanFailureReason.InUse));
        Assert.True(CleanFailureReasons.ShowsRetry(CleanFailureReason.Unknown));
        Assert.True(CleanFailureReasons.ShowsRetry(CleanFailureReason.NeedsAdministrator));
        Assert.True(CleanFailureReasons.ShowsRetry(CleanFailureReason.NeedsAdministratorAccount));
        Assert.True(CleanFailureReasons.ShowsRetry(CleanFailureReason.MoveInExplorer));
        Assert.False(CleanFailureReasons.ShowsRetry(CleanFailureReason.SafetySkipped));
        Assert.False(CleanFailureReasons.ShowsRetry(CleanFailureReason.KeptForOtherApp));
        Assert.False(CleanFailureReasons.ShowsRetry(CleanFailureReason.BinFull));
        Assert.False(CleanFailureReasons.ShowsRetry(CleanFailureReason.BinDisabled));
        Assert.False(CleanFailureReasons.ShowsRetry(CleanFailureReason.SystemProtected));
    }

    [Fact]
    public void RetryTitlesAreAdminAware()
    {
        Assert.Equal("FailRetryAdmin", CleanFailureReasons.RetryTitleKey(CleanFailureReason.NeedsAdministrator));
        Assert.Equal("FailRetryAdmin", CleanFailureReasons.RetryTitleKey(CleanFailureReason.NeedsAdministratorAccount));
        Assert.Equal("FailRetry", CleanFailureReasons.RetryTitleKey(CleanFailureReason.InUse));
        Assert.Null(CleanFailureReasons.RetryTitleKey(CleanFailureReason.SafetySkipped));
    }

    [Fact]
    public void EveryReasonHasAnExplanationKey()
    {
        foreach (var reason in Enum.GetValues<CleanFailureReason>())
            Assert.NotEmpty(CleanFailureReasons.ExplanationKey(reason));
    }
}
