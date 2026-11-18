namespace PurgeWindows.Core;

/// <summary>
/// Resolves catalog explanations and tiers for categories and items. Returns null
/// when the catalog has no entry (or failed to load) so callers fall back to the
/// hardcoded copy — a missing explanation must never hide a category or crash a scan.
/// </summary>
public sealed class ExplanationResolver
{
    readonly CategoryCatalog _catalog;

    public ExplanationResolver(CategoryCatalog catalog) => _catalog = catalog;

    public static ExplanationResolver Default { get; } = new(CategoryCatalog.Instance);

    /// <summary>The plain-language explanation for a catalog key, or null when unknown.</summary>
    public string? ExplanationFor(string key) => _catalog.Get(key)?.Explanation;

    /// <summary>The catalog entry for a scan step's category, or null when unknown.</summary>
    public CatalogEntry? CategoryFor(string scannerId) => _catalog.ForScanner(scannerId);

    /// <summary>The catalog tier for a scan step's category, or null when unknown.</summary>
    public SafetyLevel? TierFor(string scannerId) => _catalog.ForScanner(scannerId)?.Tier;

    /// <summary>The explanation for a scan step's category, or null when unknown.</summary>
    public string? CategoryExplanation(string scannerId) => _catalog.ForScanner(scannerId)?.Explanation;

    /// <summary>
    /// The most cautious tier among a category and its item entries (Purge's merge
    /// rule). Used by the catalog audit: the hardcoded category tier in
    /// <see cref="Catalog.Create"/> must equal this merge.
    /// </summary>
    public SafetyLevel MergedTier(string scannerId)
    {
        var entries = _catalog.ForScannerItems(scannerId);
        return entries.Count == 0 ? SafetyLevel.Safe : SafetyLevels.Merge(entries.Select(e => e.Tier));
    }
}
