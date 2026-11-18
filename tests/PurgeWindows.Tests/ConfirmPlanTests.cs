using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// The confirmation-flow plan: Check-First-first sections, confirm disabled
/// at zero lines, the Check-First acknowledgement gate, and the engine
/// confirmation built from the plan. Pure logic — the dialog renders this
/// plan and can never produce a confirmation it would not allow.
/// </summary>
public sealed class ConfirmPlanTests
{
    static CleanJob Job(string id, SafetyLevel tier, long size,
        CleanAction action = CleanAction.Delete)
    {
        var cat = new CleanupCategory { Id = id, Title = id, Description = "", Glyph = "", Tier = tier, Action = action };
        cat.Items.Add(new CleanupItem { Name = "item", Paths = ["C:\\missing"], Size = size });
        return new CleanJob(cat, cat.Items);
    }

    [Fact]
    public void CheckFirstSectionLeadsAndSafeFollows()
    {
        var plan = TieredConfirmPlan.FromJobs([
            Job("temp", SafetyLevel.Safe, 900),
            Job("downloads", SafetyLevel.CheckFirst, 100),
            Job("browser", SafetyLevel.Safe, 500),
        ]);

        Assert.Equal(["downloads"], plan.CheckFirstLines.Select(l => l.CategoryId));
        Assert.Equal(["temp", "browser"], plan.SafeLines.Select(l => l.CategoryId));
        Assert.Equal(3, plan.TotalLines);
    }

    [Fact]
    public void LargestFirstWithinEachSection()
    {
        var plan = TieredConfirmPlan.FromJobs([
            Job("a", SafetyLevel.CheckFirst, 100),
            Job("b", SafetyLevel.CheckFirst, 500),
            Job("c", SafetyLevel.CheckFirst, 300),
        ]);

        Assert.Equal(["b", "c", "a"], plan.CheckFirstLines.Select(l => l.CategoryId));
    }

    [Fact]
    public void EmptyPlanCannotConfirm()
    {
        var plan = TieredConfirmPlan.FromJobs([]);

        Assert.Equal(0, plan.TotalLines);
        Assert.False(plan.CanConfirm(false));
        Assert.False(plan.CanConfirm(true));
        Assert.False(plan.RequiresAcknowledgement);
    }

    [Fact]
    public void SafeOnlyPlanNeedsNoAcknowledgement()
    {
        var plan = TieredConfirmPlan.FromJobs([Job("temp", SafetyLevel.Safe, 100)]);

        Assert.False(plan.RequiresAcknowledgement);
        Assert.True(plan.CanConfirm(false));
        Assert.Null(plan.BuildConfirmation(false));
    }

    [Fact]
    public void CheckFirstPlanRequiresAcknowledgement()
    {
        var plan = TieredConfirmPlan.FromJobs([Job("downloads", SafetyLevel.CheckFirst, 100)]);

        Assert.True(plan.RequiresAcknowledgement);
        Assert.False(plan.CanConfirm(false));
        Assert.True(plan.CanConfirm(true));
    }

    [Fact]
    public void BuildConfirmationThrowsWithoutAcknowledgement()
    {
        var plan = TieredConfirmPlan.FromJobs([Job("downloads", SafetyLevel.CheckFirst, 100)]);

        Assert.Throws<InvalidOperationException>(() => plan.BuildConfirmation(false));
    }

    [Fact]
    public void BuildConfirmationCoversExactlyTheCheckFirstCategories()
    {
        var plan = TieredConfirmPlan.FromJobs([
            Job("temp", SafetyLevel.Safe, 100),
            Job("downloads", SafetyLevel.CheckFirst, 100),
            Job("devcaches", SafetyLevel.CheckFirst, 100),
        ]);

        var confirmation = plan.BuildConfirmation(true);

        Assert.NotNull(confirmation);
        Assert.True(confirmation?.Covers("downloads"));
        Assert.True(confirmation?.Covers("devcaches"));
        Assert.False(confirmation?.Covers("temp"));
        Assert.True(confirmation?.OwnerToolsConsented);
    }

    [Fact]
    public void NonDeleteJobsNeverAppear()
    {
        var plan = TieredConfirmPlan.FromJobs([
            Job("games", SafetyLevel.CheckFirst, 100, CleanAction.SteamUninstall),
            Job("virtual", SafetyLevel.ReportOnly, 100, CleanAction.ReportOnly),
        ]);

        Assert.Equal(0, plan.TotalLines);
        Assert.False(plan.CanConfirm(true));
    }

    [Fact]
    public void JobsWithoutItemsNeverAppear()
    {
        var cat = new CleanupCategory { Id = "temp", Title = "temp", Description = "", Glyph = "" };
        var plan = TieredConfirmPlan.FromJobs([new CleanJob(cat, [])]);

        Assert.Equal(0, plan.TotalLines);
    }

    [Fact]
    public void NonSafeTiersTakeTheCautiousSection()
    {
        var plan = TieredConfirmPlan.FromJobs([
            Job("x", SafetyLevel.ReportOnly, 100),
        ]);

        Assert.Single(plan.CheckFirstLines);
        Assert.Empty(plan.SafeLines);
        Assert.True(plan.RequiresAcknowledgement);
    }
}
