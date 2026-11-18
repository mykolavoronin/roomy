using PurgeWindows.Core;

namespace PurgeWindows.Tests;

public sealed class CatalogTests
{
    [Fact]
    public void EveryScanStepResolvesToACatalogCategory()
    {
        foreach (var step in Scanner.Steps)
        {
            var cat = Catalog.Create(step.Id);
            Assert.Equal(step.Id, cat.Id);
            Assert.NotEmpty(cat.Title);
            Assert.NotEmpty(cat.Description);
        }
    }

    [Fact]
    public void ReportOnlyCategoriesNeverGoToTheRecycleBin()
    {
        var cat = Catalog.Create("virtual");
        Assert.Equal(CleanAction.ReportOnly, cat.Action);
        foreach (var mode in Enum.GetValues<RemovalMode>())
            Assert.False(RemovalPolicy.GoesToBin(mode, cat));
    }

    [Fact]
    public void CleanerSkipsReportOnlyJobsWithoutTouchingAnything()
    {
        var cat = Catalog.Create("virtual");
        cat.Items.Add(new CleanupItem { Name = "VM", Paths = [Path.GetTempPath()], Size = 1234 });
        var r = Cleaner.Run([new CleanJob(cat, cat.Items)], progress: null);
        Assert.Equal(0, r.Freed);
        Assert.Equal(0, r.Files);
        Assert.Empty(r.Categories ?? []);
    }

    [Fact]
    public void NewCategoriesHaveTheExpectedSafetyTiers()
    {
        Assert.Equal(Safety.Safe, Catalog.Create("thumbnails").Safety);
        Assert.Equal(Safety.Safe, Catalog.Create("avd").Safety);
        Assert.Equal(Safety.Review, Catalog.Create("devcaches").Safety);
        Assert.Equal(Safety.Review, Catalog.Create("virtual").Safety);
    }
}
