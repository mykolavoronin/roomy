using PurgeWindows.Localization;
namespace PurgeWindows.Core;

// Safety tiers live in SafetyLevel.cs (SafetyLevel: Safe / CheckFirst / ReportOnly).

/// <summary>Where cleaned files go. Every cleanable item goes to the Recycle Bin (D-21):
/// both values behave identically today and are kept for settings compatibility.
/// Permanent removal is never a setting; it only ever happens through an explicit
/// per-item confirmation (Wave 4) or an owner tool run with consent.</summary>
public enum RemovalMode
{
    /// <summary>Everything that can be recycled goes to the Recycle Bin.</summary>
    ReviewToBin,
    /// <summary>Everything that can be recycled goes to the Recycle Bin.</summary>
    EverythingToBin,
}

public static class RemovalPolicy
{
    /// <summary>True when this category's files go to the Recycle Bin under <paramref name="mode"/>.
    /// Always true for Delete categories: the Recycle Bin is the default (and only)
    /// destination for cleaned files. ReportOnly/Steam/Settings categories never
    /// reach the engine.</summary>
    public static bool GoesToBin(RemovalMode mode, CleanupCategory c) => c.Action == CleanAction.Delete;
}

public enum CleanAction
{
    /// <summary>Delete files (for folders: delete the contents, keep the folder).</summary>
    Delete,
    /// <summary>Hand off to Steam's own uninstaller.</summary>
    SteamUninstall,
    /// <summary>Only Windows can remove it safely, so we open Settings.</summary>
    OpenSettings,
    /// <summary>Sizes only. Purge for Windows never touches these files; the owning tool must do it.</summary>
    ReportOnly,
}

public sealed class CleanupItem
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public string Path => Paths.Count > 0 ? Paths[0] : "";
    public long Size { get; set; }
    public int FileCount { get; set; }
    public bool IsFile { get; init; }
    public string? Detail { get; init; }
    /// <summary>Only files older than this are measured and deleted.</summary>
    public TimeSpan? MinAge { get; init; }
    public string? AppId { get; init; }
    /// <summary>True when the paths were resolved by asking the owning tool where its
    /// cache lives (npm, Go) and validated to stay inside the user profile. The
    /// safety policy trusts such paths via <c>EvaluateToolResolved</c>; paths the
    /// scanner hardcoded must be on the allowlist instead.</summary>
    public bool ToolResolved { get; init; }
}

public sealed class CleanupCategory
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public SafetyLevel Tier { get; init; } = SafetyLevel.Safe;
    public CleanAction Action { get; init; } = CleanAction.Delete;
    public bool SelectedByDefault { get; init; }
    public bool ShowItems { get; init; } = true;
    public bool Blocked { get; set; }
    public string? Note { get; set; }
    public List<CleanupItem> Items { get; } = [];
    public long Size => Items.Sum(i => i.Size);
}

/// <summary>Segoe Fluent Icons / MDL2 code points.</summary>
public static class G
{
    public const string Delete = "";
    public const string Clock = "";
    public const string Globe = "";
    public const string Apps = "";
    public const string Color = "";
    public const string Bug = "";
    public const string Update = "";
    public const string Package = "";
    public const string Download = "";
    public const string Game = "";
    public const string History = "";
    public const string Folder = "";
    public const string Document = "";
    public const string Shield = "";
    public const string Check = "";
    public const string ChevronDown = "";
    public const string ChevronUp = "";
    public const string ChevronRight = "";
}

/// <summary>The fixed copy for every category, shared by the real scanner and demo mode.</summary>
public static class Catalog
{
    public static CleanupCategory Create(string id) => id switch
    {
        "recycle" => new CleanupCategory
        {
            Id = id, Title = S.CatRecycle, Glyph = G.Delete, Tier = SafetyLevel.ReportOnly,
            Description = S.CatRecycleDesc,
            // Review/open-only since the Phase 0 hang fix: Purge never empties the Trash,
            // and the bin-emptying shell call was an uninterruptible silent phase (C5). The bin is
            // measured and shown; emptying it is Windows' job.
            Action = CleanAction.ReportOnly, SelectedByDefault = false, ShowItems = false,
        },
        "temp" => new CleanupCategory
        {
            Id = id, Title = S.CatTemp, Glyph = G.Clock,
            Description = S.CatTempDesc,
            SelectedByDefault = true,
        },
        "browser" => new CleanupCategory
        {
            Id = id, Title = S.CatBrowser, Glyph = G.Globe,
            Description = S.CatBrowserDesc,
            SelectedByDefault = true,
        },
        "apps" => new CleanupCategory
        {
            Id = id, Title = S.CatApps, Glyph = G.Apps,
            Description = S.CatAppsDesc,
            SelectedByDefault = true,
        },
        "shaders" => new CleanupCategory
        {
            Id = id, Title = S.CatShaders, Glyph = G.Color,
            Description = S.CatShadersDesc,
            SelectedByDefault = false,
        },
        "crash" => new CleanupCategory
        {
            Id = id, Title = S.CatCrash, Glyph = G.Bug,
            Description = S.CatCrashDesc,
            SelectedByDefault = true,
        },
        "windows" => new CleanupCategory
        {
            Id = id, Title = S.CatWindows, Glyph = G.Update, Tier = SafetyLevel.CheckFirst,
            Description = S.CatWindowsDesc,
            SelectedByDefault = true,
        },
        "installers" => new CleanupCategory
        {
            Id = id, Title = S.CatInstallers, Glyph = G.Package, Tier = SafetyLevel.CheckFirst,
            Description = S.CatInstallersDesc,
        },
        "downloads" => new CleanupCategory
        {
            Id = id, Title = S.CatDownloads, Glyph = G.Download, Tier = SafetyLevel.CheckFirst,
            Description = S.CatDownloadsDesc,
        },
        "games" => new CleanupCategory
        {
            Id = id, Title = S.CatGames, Glyph = G.Game, Tier = SafetyLevel.CheckFirst,
            Description = S.CatGamesDesc,
            Action = CleanAction.SteamUninstall,
        },
        "winold" => new CleanupCategory
        {
            Id = id, Title = S.CatWinOld, Glyph = G.History, Tier = SafetyLevel.CheckFirst,
            Description = S.CatWinOldDesc,
            Action = CleanAction.OpenSettings, ShowItems = false,
        },
        "thumbnails" => new CleanupCategory
        {
            Id = id, Title = S.CatThumbnails, Glyph = G.Document,
            Description = S.CatThumbnailsDesc,
            SelectedByDefault = true,
        },
        "avd" => new CleanupCategory
        {
            Id = id, Title = S.CatAvd, Glyph = G.Apps,
            Description = S.CatAvdDesc,
            SelectedByDefault = false,
        },
        "devcaches" => new CleanupCategory
        {
            Id = id, Title = S.CatDevCaches, Glyph = G.Package, Tier = SafetyLevel.CheckFirst,
            Description = S.CatDevCachesDesc,
        },
        "virtual" => new CleanupCategory
        {
            Id = id, Title = S.CatVirtual, Glyph = G.Folder, Tier = SafetyLevel.ReportOnly,
            Description = S.CatVirtualDesc,
            Action = CleanAction.ReportOnly,
        },
        "diagnostics" => new CleanupCategory
        {
            Id = id, Title = S.CatDiagnostics, Glyph = G.Shield, Tier = SafetyLevel.ReportOnly,
            Description = S.CatDiagnosticsDesc,
            Action = CleanAction.ReportOnly,
        },
        "winlogs" => new CleanupCategory
        {
            Id = id, Title = S.CatWinLogs, Glyph = G.History, Tier = SafetyLevel.CheckFirst,
            Description = S.CatWinLogsDesc,
        },
        "outlook" => new CleanupCategory
        {
            Id = id, Title = S.CatOutlook, Glyph = G.Document, Tier = SafetyLevel.CheckFirst,
            Description = S.CatOutlookDesc,
        },
        "messaging" => new CleanupCategory
        {
            Id = id, Title = S.CatMessaging, Glyph = G.Apps, Tier = SafetyLevel.CheckFirst,
            Description = S.CatMessagingDesc,
        },
        "captures" => new CleanupCategory
        {
            Id = id, Title = S.CatCaptures, Glyph = G.Game, Tier = SafetyLevel.CheckFirst,
            Description = S.CatCapturesDesc,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, S.UnknownCategory),
    };
}
