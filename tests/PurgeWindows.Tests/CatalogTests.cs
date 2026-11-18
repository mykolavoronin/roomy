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
    public void ScanStepIdsAreUnique()
    {
        var ids = Scanner.Steps.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ReportOnlyCategoriesNeverGoToTheRecycleBin()
    {
        foreach (var id in new[] { "recycle", "virtual", "diagnostics" })
        {
            var cat = Catalog.Create(id);
            Assert.Equal(CleanAction.ReportOnly, cat.Action);
            foreach (var mode in Enum.GetValues<RemovalMode>())
                Assert.False(RemovalPolicy.GoesToBin(mode, cat));
        }
    }

    [Fact]
    public void CleanerSkipsReportOnlyJobsWithoutTouchingAnything()
    {
        var cat = Catalog.Create("virtual");
        cat.Items.Add(new CleanupItem { Name = "VM", Paths = [Path.GetTempPath()], Size = 1234 });
        var r = Cleaner.Run([new CleanJob(cat, cat.Items)], progress: null);
        Assert.Equal(0, r.Freed);
        Assert.Equal(0, r.Recycled);
        Assert.Equal(0, r.Files);
        Assert.Empty(r.Categories ?? []);
    }

    [Fact]
    public void CleanerSkipsDiagnosticsJobsWithoutTouchingAnything()
    {
        var cat = Catalog.Create("diagnostics");
        Assert.Equal(CleanAction.ReportOnly, cat.Action);
        Assert.Equal(SafetyLevel.ReportOnly, cat.Tier);
        cat.Items.Add(new CleanupItem { Name = "logs", Paths = [Path.GetTempPath()], Size = 1234 });
        var r = Cleaner.Run([new CleanJob(cat, cat.Items)], progress: null);
        Assert.Equal(0, r.Freed);
        Assert.Equal(0, r.Recycled);
        Assert.Equal(0, r.Files);
        Assert.Empty(r.Categories ?? []);
    }

    [Fact]
    public void NewCategoriesHaveTheExpectedSafetyTiers()
    {
        Assert.Equal(SafetyLevel.Safe, Catalog.Create("thumbnails").Tier);
        Assert.Equal(SafetyLevel.Safe, Catalog.Create("avd").Tier);
        Assert.Equal(SafetyLevel.CheckFirst, Catalog.Create("devcaches").Tier);
        Assert.Equal(SafetyLevel.ReportOnly, Catalog.Create("virtual").Tier);
    }

    [Fact]
    public void Round3CategoriesHaveTheExpectedSafetyTiers()
    {
        Assert.Equal(SafetyLevel.ReportOnly, Catalog.Create("diagnostics").Tier);
        Assert.Equal(SafetyLevel.CheckFirst, Catalog.Create("winlogs").Tier);
        Assert.Equal(SafetyLevel.CheckFirst, Catalog.Create("outlook").Tier);
        Assert.Equal(SafetyLevel.CheckFirst, Catalog.Create("messaging").Tier);
        Assert.Equal(SafetyLevel.CheckFirst, Catalog.Create("captures").Tier);
    }

    // ---------- embedded JSON catalog ----------

    [Fact]
    public void EmbeddedCatalogLoads()
    {
        var catalog = CategoryCatalog.Load();
        Assert.True(catalog.Loaded);
        Assert.NotEmpty(catalog.Entries);
    }

    [Fact]
    public void CatalogAuditIsClean()
    {
        var catalog = CategoryCatalog.Load();
        var issues = catalog.Audit(Scanner.Steps.Select(s => s.Id).ToList());
        Assert.Empty(issues);
    }

    [Fact]
    public void CatalogHasNoDuplicateKeys()
    {
        var keys = CategoryCatalog.Load().Entries.Select(e => e.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CatalogTierRuleIsEnforced()
    {
        // Safe ⟹ Recycle Bin removal + no elevation/repair/only-copy/sync preconditions.
        // (The audit checks the same rule; this test pins the rule itself.)
        foreach (var e in CategoryCatalog.Load().Entries.Where(e => e.Tier == SafetyLevel.Safe))
        {
            Assert.Equal("recycle", e.Removal);
            Assert.DoesNotContain(e.Preconditions, p => p is "elevation" or "repair" or "only-copy" or "sync");
        }
    }

    [Fact]
    public void CatalogTiersAgreeWithHardcodedCategoryTiers()
    {
        var catalog = CategoryCatalog.Load();
        foreach (var step in Scanner.Steps)
        {
            var entry = catalog.ForScanner(step.Id);
            Assert.NotNull(entry);
            Assert.Equal(Catalog.Create(step.Id).Tier, entry.Tier);
        }
    }

    [Fact]
    public void MergedTierEqualsCategoryTierForEveryStep()
    {
        var resolver = new ExplanationResolver(CategoryCatalog.Load());
        foreach (var step in Scanner.Steps)
            Assert.Equal(Catalog.Create(step.Id).Tier, resolver.MergedTier(step.Id));
    }

    [Fact]
    public void CatalogEntriesHaveExplanationsAndNoModeField()
    {
        // No mode field exists in the schema: the record has no such property, so
        // this pins the content side — every entry explains itself in plain language.
        foreach (var e in CategoryCatalog.Load().Entries)
        {
            Assert.NotEmpty(e.DisplayName);
            Assert.NotEmpty(e.Explanation);
            Assert.DoesNotContain("faster", e.Explanation, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("speed up", e.Explanation, StringComparison.OrdinalIgnoreCase);
        }
    }
}
