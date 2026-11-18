using PurgeWindows.Localization;

namespace PurgeWindows.Core;

/// <summary>
/// Why one item could not be cleaned. Ported from Purge's <c>CleanFailureReason</c>
/// (12 cases), adapted to Windows: the macOS-only cases (Full Disk Access, Xcode
/// license, simulator service/tools, move-in-Finder) are replaced by their Windows
/// analogues (Recycle Bin disabled/full, shell failure, owner-tool failure,
/// move-in-Explorer).
/// <para/>
/// <see cref="RawValue"/> is persisted in cleanup history: renaming a value turns
/// old entries into <see cref="Unknown"/> on read, exactly as in Purge. Never rename.
/// </summary>
public enum CleanFailureReason
{
    /// <summary>Needs elevation; the user can retry as administrator.</summary>
    NeedsAdministrator,
    /// <summary>The account is not an administrator; an admin's credentials are needed.</summary>
    NeedsAdministratorAccount,
    /// <summary>The file is in use (locked).</summary>
    InUse,
    /// <summary>Windows protects the file (system, immutable, or access denied).</summary>
    SystemProtected,
    /// <summary>The safety policy refused the path (never-delete or not allowlisted).</summary>
    SafetySkipped,
    /// <summary>Another installed app still uses the file.</summary>
    KeptForOtherApp,
    /// <summary>The Recycle Bin is disabled for the file's drive.</summary>
    BinDisabled,
    /// <summary>The file does not fit in the Recycle Bin's remaining capacity.</summary>
    BinFull,
    /// <summary>The shell call that moves files to the Recycle Bin failed.</summary>
    ShellFailed,
    /// <summary>A dev tool's own clean command failed or timed out.</summary>
    OwnerToolFailed,
    /// <summary>The user must move it in File Explorer; the app cannot do it safely.</summary>
    MoveInExplorer,
    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>One failed item, as recorded in cleanup history.</summary>
public sealed record CleanFailure(string Path, long Size, CleanFailureReason Reason, bool IsUserVisible = true);

public static class CleanFailureReasons
{
    /// <summary>
    /// The persisted raw value. Stable forever: history entries store this string,
    /// and an unknown value on read becomes <see cref="CleanFailureReason.Unknown"/>.
    /// </summary>
    public static string RawValue(CleanFailureReason reason) => reason switch
    {
        CleanFailureReason.NeedsAdministrator => "needsAdministrator",
        CleanFailureReason.NeedsAdministratorAccount => "needsAdministratorAccount",
        CleanFailureReason.InUse => "inUse",
        CleanFailureReason.SystemProtected => "systemProtected",
        CleanFailureReason.SafetySkipped => "safetySkipped",
        CleanFailureReason.KeptForOtherApp => "keptForOtherApp",
        CleanFailureReason.BinDisabled => "binDisabled",
        CleanFailureReason.BinFull => "binFull",
        CleanFailureReason.ShellFailed => "shellFailed",
        CleanFailureReason.OwnerToolFailed => "ownerToolFailed",
        CleanFailureReason.MoveInExplorer => "moveInExplorer",
        _ => "unknown",
    };

    /// <summary>Parses a persisted raw value; anything unrecognized becomes Unknown (never throws).</summary>
    public static CleanFailureReason FromRawValue(string? raw) => raw switch
    {
        "needsAdministrator" => CleanFailureReason.NeedsAdministrator,
        "needsAdministratorAccount" => CleanFailureReason.NeedsAdministratorAccount,
        "inUse" => CleanFailureReason.InUse,
        "systemProtected" => CleanFailureReason.SystemProtected,
        "safetySkipped" => CleanFailureReason.SafetySkipped,
        "keptForOtherApp" => CleanFailureReason.KeptForOtherApp,
        "binDisabled" => CleanFailureReason.BinDisabled,
        "binFull" => CleanFailureReason.BinFull,
        "shellFailed" => CleanFailureReason.ShellFailed,
        "ownerToolFailed" => CleanFailureReason.OwnerToolFailed,
        "moveInExplorer" => CleanFailureReason.MoveInExplorer,
        _ => CleanFailureReason.Unknown,
    };

    /// <summary>The user-facing explanation string key for the reason.</summary>
    public static string ExplanationKey(CleanFailureReason reason) => reason switch
    {
        CleanFailureReason.NeedsAdministrator => "FailNeedsAdmin",
        CleanFailureReason.NeedsAdministratorAccount => "FailNeedsAdminAccount",
        CleanFailureReason.InUse => "FailInUse",
        CleanFailureReason.SystemProtected => "FailSystemProtected",
        CleanFailureReason.SafetySkipped => "FailSafetySkipped",
        CleanFailureReason.KeptForOtherApp => "FailKeptForOtherApp",
        CleanFailureReason.BinDisabled => "FailBinDisabled",
        CleanFailureReason.BinFull => "FailBinFull",
        CleanFailureReason.ShellFailed => "FailShellFailed",
        CleanFailureReason.OwnerToolFailed => "FailOwnerToolFailed",
        CleanFailureReason.MoveInExplorer => "FailMoveInExplorer",
        _ => "FailUnknown",
    };

    /// <summary>The user-facing explanation text.</summary>
    public static string Explanation(CleanFailureReason reason) => Loc.Get(ExplanationKey(reason));

    /// <summary>
    /// Whether the failure offers a retry. Mirrors Purge's rule: retry is offered
    /// for in-use, unknown, the two admin cases, and move-in-Explorer — never for
    /// safety skips, kept-for-other-app, or bin-capacity refusals, where retrying
    /// could not change anything. A retry uses the route that tried first and
    /// never removes more than the first attempt did.
    /// </summary>
    public static bool ShowsRetry(CleanFailureReason reason) => reason is
        CleanFailureReason.InUse or CleanFailureReason.Unknown or
        CleanFailureReason.NeedsAdministrator or CleanFailureReason.NeedsAdministratorAccount or
        CleanFailureReason.MoveInExplorer;

    /// <summary>The retry button label key, or null when no retry is offered.</summary>
    public static string? RetryTitleKey(CleanFailureReason reason) => reason switch
    {
        CleanFailureReason.NeedsAdministrator or CleanFailureReason.NeedsAdministratorAccount => "FailRetryAdmin",
        _ when ShowsRetry(reason) => "FailRetry",
        _ => null,
    };

    /// <summary>Maps the engine's skip vocabulary onto the failure taxonomy.</summary>
    public static CleanFailureReason FromSkipReason(SkipReason reason) => reason switch
    {
        SkipReason.InUse => CleanFailureReason.InUse,
        SkipReason.Protected => CleanFailureReason.SystemProtected,
        SkipReason.PolicyRefused => CleanFailureReason.SafetySkipped,
        SkipReason.LinkRejected => CleanFailureReason.SafetySkipped,
        SkipReason.TooBigForBin => CleanFailureReason.BinFull,
        SkipReason.BinDisabled => CleanFailureReason.BinDisabled,
        SkipReason.ShellError => CleanFailureReason.ShellFailed,
        SkipReason.PathTooLong => CleanFailureReason.Unknown,
        SkipReason.TooNew => CleanFailureReason.Unknown,
        _ => CleanFailureReason.Unknown,
    };
}
