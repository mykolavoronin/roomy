using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// The deletion safety policy: evaluation order, allowlist/never-touch sets, and
/// re-check semantics. All paths use fake roots for a machine that is not this one,
/// so no real machine layout is involved — but Windows path semantics are required,
/// so the whole class asserts the OS up front.
/// </summary>
public sealed class SafetyPolicyTests
{
    public SafetyPolicyTests() =>
        Assert.True(OperatingSystem.IsWindows(), "Policy tests require Windows path semantics.");

    static PolicyRoots Roots() => new(
        @"C:\Users\you",
        @"C:\Users\you\AppData\Local",
        @"C:\Users\you\AppData\Roaming",
        @"C:\ProgramData", @"C:\Windows", @"C:\",
        @"C:\Users\you\Downloads",
        @"C:\Users\you\Documents",
        @"C:\Users\you\Desktop",
        @"C:\Users\you\Pictures",
        @"C:\Users\you\Videos",
        @"C:\Users\you\Music",
        @"C:\Users\you\AppData\Local\Temp",
        @"C:\Program Files", @"C:\Program Files (x86)");

    static DeletionSafetyPolicy Policy() => new(Roots());

    [Fact]
    public void AllowlistBeatsNeverDeletePrefix()
    {
        // SoftwareDistribution is never-touch, but its Download subfolder is the one
        // allowlisted exception — and the allowlist is checked first.
        var allowed = Policy().Evaluate(@"C:\Windows\SoftwareDistribution\Download\update.cab");
        Assert.Equal(PolicyVerdict.Allow, allowed.Verdict);
        Assert.Equal(5, allowed.Step);

        var blocked = Policy().Evaluate(@"C:\Windows\SoftwareDistribution\DataStore\datastore.edb");
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, blocked.Verdict);
        Assert.Equal(7, blocked.Step);
    }

    [Fact]
    public void ProtectedSetsComeFirst()
    {
        var p = Policy();
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"C:\Users\you").Verdict);
        Assert.Equal(1, p.Evaluate(@"C:\Users\you").Step);
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"C:\Windows\System32\drivers\etc\hosts").Verdict);
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"C:\").Verdict);
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"C:\Users\you\AppData\Local\Microsoft\Outlook\mail.pst").Verdict);
        Assert.Equal(1, p.Evaluate(@"C:\Users\you\AppData\Local\Microsoft\Outlook\mail.pst").Step);
    }

    [Fact]
    public void OstFilesAreAllowedWherePstFilesAreNot()
    {
        // The Outlook folder is allowlisted; .ost files rebuild from the server,
        // while .pst files may be the only copy of the mail.
        var allowed = Policy().Evaluate(@"C:\Users\you\AppData\Local\Microsoft\Outlook\mailbox.ost");
        Assert.Equal(PolicyVerdict.Allow, allowed.Verdict);
        Assert.Equal(5, allowed.Step);
    }

    [Fact]
    public void TempAndDownloadsAreAllowed()
    {
        var p = Policy();
        Assert.Equal(PolicyVerdict.Allow, p.Evaluate(@"C:\Users\you\AppData\Local\Temp\setup123.tmp").Verdict);
        Assert.Equal(PolicyVerdict.Allow, p.Evaluate(@"C:\Users\you\Downloads\old-installer.exe").Verdict);
    }

    [Fact]
    public void UnknownPathsAreBlocked()
    {
        var d = Policy().Evaluate(@"C:\Users\you\Documents\report.docx");
        Assert.Equal(PolicyVerdict.BlockedNotWhitelisted, d.Verdict);
        Assert.Equal(12, d.Step);
    }

    [Fact]
    public void UncRelativeAndEmptyPathsAreBlocked()
    {
        var p = Policy();
        Assert.Equal(PolicyVerdict.BlockedNotWhitelisted, p.Evaluate(@"\\server\share\file.bin").Verdict);
        Assert.Equal(PolicyVerdict.BlockedNotWhitelisted, p.Evaluate(@"relative\path\file.bin").Verdict);
        Assert.Equal(PolicyVerdict.BlockedNotWhitelisted, p.Evaluate("").Verdict);
    }

    [Fact]
    public void FolderNameAndProjectRulesRunAfterTheGuards()
    {
        var p = Policy();
        // A project artifact inside the profile is allowed by the late rules.
        var artifact = p.Evaluate(@"C:\Users\you\projects\app\node_modules");
        Assert.Equal(PolicyVerdict.Allow, artifact.Verdict);
        Assert.Equal(10, artifact.Step);
        // But the same late rules never rescue a never-delete location.
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"C:\Windows\WinSxS\Cache").Verdict);
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"C:\ProgramData\Package Cache\Cache").Verdict);
        // And version-control state is protected even inside a project.
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"C:\Users\you\projects\app\.git\config").Verdict);
    }

    [Fact]
    public void PagingFilesAtDriveRootsAreNeverDelete()
    {
        var p = Policy();
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"C:\pagefile.sys").Verdict);
        Assert.Equal(8, p.Evaluate(@"C:\pagefile.sys").Step);
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"C:\hiberfil.sys").Verdict);
        Assert.Equal(PolicyVerdict.BlockedNeverDelete, p.Evaluate(@"D:\System Volume Information\file").Verdict);
    }

    [Fact]
    public void ExclusionsAreReCheckedLiveAndNeverMemoized()
    {
        var p = Policy();
        const string path = @"C:\Users\you\AppData\Local\Temp\work.bin";
        Assert.Equal(PolicyVerdict.Allow, p.Evaluate(path, LiveContext.Empty).Verdict);

        var excluded = p.Evaluate(path, new LiveContext([@"C:\Users\you\AppData\Local\Temp"]));
        Assert.Equal(PolicyVerdict.BlockedNotWhitelisted, excluded.Verdict);
        Assert.Equal(0, excluded.Step);

        // Removing the exclusion restores the allow verdict: nothing was memoized.
        Assert.Equal(PolicyVerdict.Allow, p.Evaluate(path, LiveContext.Empty).Verdict);
    }

    [Fact]
    public void AllowsContainedFastPath()
    {
        var p = Policy();
        const string root = @"C:\Users\you\AppData\Local\Temp";
        Assert.True(p.AllowsContained(root + @"\sub\file.bin", root));
        Assert.False(p.AllowsContained(@"C:\Users\you\Documents\file.bin", root));
        // A .pst inside an allowed root is still refused.
        Assert.False(p.AllowsContained(root + @"\mail.pst", root));
        // Escaping the root with ".." is refused.
        Assert.False(p.AllowsContained(root + @"\..\Roaming\evil.bin", root));
    }

    [Fact]
    public void EvaluateToolResolved()
    {
        var p = Policy();
        Assert.Equal(PolicyVerdict.Allow,
            p.EvaluateToolResolved(@"C:\Users\you\tools\npm-cache\_cacache").Verdict);
        Assert.Equal(PolicyVerdict.BlockedNotWhitelisted,
            p.EvaluateToolResolved(@"D:\caches\npm-cache").Verdict);
        Assert.Equal(PolicyVerdict.BlockedNeverDelete,
            p.EvaluateToolResolved(@"C:\Windows\System32\cache").Verdict);
        Assert.Equal(PolicyVerdict.BlockedNotWhitelisted,
            p.EvaluateToolResolved(@"\\server\share\cache").Verdict);
    }

    [Fact]
    public void AllowRootsFactoryAllowsExtraRoots()
    {
        // A folder under the profile that no allowlist rule covers: the default
        // policy blocks it, and only the extra root allows it.
        var folder = Path.Combine(Env.UserProfile, "PurgeWindowsPolicyTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "file.bin");
            Assert.Equal(PolicyVerdict.BlockedNotWhitelisted, DeletionSafetyPolicy.Default.Evaluate(file).Verdict);
            var p = DeletionSafetyPolicy.AllowRoots(folder);
            Assert.Equal(PolicyVerdict.Allow, p.Evaluate(file).Verdict);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
