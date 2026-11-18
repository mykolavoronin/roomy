using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PurgeWindows.Core;

/// <summary>
/// One catalog entry, adapted from Purge's <c>explanations.json</c> schema
/// (key/display_name/tag/explanation/aliases/kind — NO paths) plus Windows
/// additions: tier, removal method, preconditions, verified status/date, and the
/// scan step that produces it (null for groundwork entries with no scanner yet).
/// <para/>
/// There is deliberately NO mode field: there are no modes, and nothing is hidden.
/// </summary>
public sealed record CatalogEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("tier")] SafetyLevel Tier,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("removal")] string Removal,
    [property: JsonPropertyName("preconditions")] IReadOnlyList<string> Preconditions,
    [property: JsonPropertyName("verified")] string Verified,
    [property: JsonPropertyName("verified_date")] string VerifiedDate,
    [property: JsonPropertyName("scanner_id")] string? ScannerId)
{
    /// <summary>True when the entry is cleanable in this build: a scanner produces
    /// it, it is verified or secondary, and its removal route exists.</summary>
    public bool IsCleanableNow =>
        ScannerId is not null &&
        Verified is "verified" or "secondary" &&
        Removal is "recycle" or "owner-tool" or "uninstall" or "settings-handoff" &&
        Tier is SafetyLevel.Safe or SafetyLevel.CheckFirst;
}

/// <summary>
/// The category catalog: loads <c>Resources/explanations.windows.json</c> from the
/// embedded resource and exposes it read-only. Loading is fail-soft — a missing or
/// unreadable resource yields an empty catalog (callers fall back to the hardcoded
/// category copy in <see cref="Catalog"/>), never a crash. The JSON is the
/// authoritative source for tiers and explanations; <see cref="Catalog.Create"/>
/// mirrors it structurally, and the catalog tests assert the two agree.
/// </summary>
public sealed class CategoryCatalog
{
    public const string ResourceName = "PurgeWindows.Resources.explanations.windows.json";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static CategoryCatalog? _instance;
    public static CategoryCatalog Instance => _instance ??= Load();

    public static CategoryCatalog Load(Assembly? assembly = null)
    {
        try
        {
            using var stream = (assembly ?? typeof(CategoryCatalog).Assembly)
                .GetManifestResourceStream(ResourceName);
            if (stream is null) return new CategoryCatalog([], Loaded: false);
            using var reader = new StreamReader(stream);
            var doc = JsonSerializer.Deserialize<CatalogDocument>(reader.ReadToEnd(), JsonOptions);
            return new CategoryCatalog(doc?.Entries ?? [], Loaded: true);
        }
        catch
        {
            return new CategoryCatalog([], Loaded: false);
        }
    }

    sealed record CatalogDocument(
        [property: JsonPropertyName("entries")] List<CatalogEntry> Entries);

    public CategoryCatalog(IReadOnlyList<CatalogEntry> entries, bool Loaded)
    {
        Entries = entries;
        this.Loaded = Loaded;
    }

    public IReadOnlyList<CatalogEntry> Entries { get; }
    public bool Loaded { get; }

    public CatalogEntry? Get(string key) =>
        Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

    public CatalogEntry? ForScanner(string scannerId) =>
        Entries.FirstOrDefault(e => e.Kind == "category" &&
            string.Equals(e.ScannerId, scannerId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<CatalogEntry> ForScannerItems(string scannerId) =>
        Entries.Where(e => string.Equals(e.ScannerId, scannerId, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Audits the catalog the way Purge's <c>AllowlistAuditTests</c> audit theirs:
    /// no duplicate keys, required fields present, tier/tag agreement, the tier
    /// rule enforced (Safe ⟹ Recycle Bin removal + no elevation/repair/only-copy/
    /// sync preconditions), and every scan step covered by a category entry.
    /// Returns human-readable issues; empty means clean.
    /// </summary>
    public List<string> Audit(IReadOnlyList<string>? scannerIds = null)
    {
        var issues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries)
        {
            if (!seen.Add(e.Key)) issues.Add($"duplicate key: {e.Key}");
            if (e.DisplayName.Length == 0) issues.Add($"{e.Key}: empty display_name");
            if (e.Explanation.Length == 0) issues.Add($"{e.Key}: empty explanation");
            var expectedTag = e.Tier switch
            {
                SafetyLevel.Safe => "safe",
                SafetyLevel.CheckFirst => "medium",
                SafetyLevel.ReportOnly => "unknown",
                _ => "?",
            };
            if (e.Tag != expectedTag) issues.Add($"{e.Key}: tag {e.Tag} disagrees with tier {e.Tier}");
            if (e.Tier == SafetyLevel.Safe)
            {
                if (e.Removal != "recycle") issues.Add($"{e.Key}: Safe tier with removal {e.Removal} (must be recycle)");
                foreach (var p in e.Preconditions)
                    if (p is "elevation" or "repair" or "only-copy" or "sync")
                        issues.Add($"{e.Key}: Safe tier with blocking precondition {p}");
            }
            if (e.Tier == SafetyLevel.ReportOnly && e.Removal != "report-only")
                issues.Add($"{e.Key}: ReportOnly tier with removal {e.Removal}");
            if (e.Verified is not ("verified" or "secondary" or "gated" or "detection-only"))
                issues.Add($"{e.Key}: unknown verified status {e.Verified}");
        }
        if (scannerIds is not null)
        {
            foreach (var id in scannerIds)
                if (ForScanner(id) is null) issues.Add($"scan step {id} has no category entry");
        }
        return issues;
    }
}
