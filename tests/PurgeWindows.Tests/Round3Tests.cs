using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// Round 3 path-resolution rules: a cache location reported by a tool (npm, Go)
/// is only trusted when it is an absolute path inside the user's profile.
/// These tests touch no real files and run no tools.
/// Carried over from Round 3 verbatim, apart from the namespace and the explicit
/// Windows guard (the rules rely on Windows path semantics).
/// </summary>
public sealed class Round3Tests
{
    public Round3Tests() =>
        Assert.True(OperatingSystem.IsWindows(), "Tool-path rules require Windows path semantics.");

    const string Profile = @"C:\Users\you";

    [Theory]
    [InlineData(@"C:\Users\you\AppData\Local\npm-cache", true)]
    [InlineData(@"C:\Users\you\go\pkg\mod", true)]
    [InlineData(@"C:\Users\you", true)]
    [InlineData(@"c:\users\YOU\AppData\Local\npm-cache", true)]
    [InlineData(@"C:\Users\you\cache\", true)]
    [InlineData(@"C:\Users\you\..\you2\cache", false)]
    [InlineData(@"C:\Users\you2\cache", false)]
    [InlineData(@"D:\npm-cache", false)]
    [InlineData(@"\\server\share\cache", false)]
    [InlineData(@"AppData\Local\npm-cache", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ToolPathsMustStayInsideTheUserProfile(string? candidate, bool expected)
    {
        Assert.Equal(expected, Scanner.IsInsideProfile(candidate, Profile));
    }

    [Fact]
    public void ToolOutputLinesSkipsBlanksAndTrims()
    {
        Assert.Equal(new[] { @"C:\cache", @"D:\other" },
            Scanner.ToolOutputLines("  C:\\cache\r\n\r\nD:\\other\n"));
        Assert.Empty(Scanner.ToolOutputLines(null));
        Assert.Empty(Scanner.ToolOutputLines("  \n "));
        Assert.Empty(Scanner.ToolOutputLines(""));
    }

    [Fact]
    public void TrustedToolPathRejectsOutsidePathsAndNormalisesInsideOnes()
    {
        Assert.Null(Scanner.TrustedToolPath(@"D:\cache", Profile));
        Assert.Null(Scanner.TrustedToolPath(null, Profile));
        Assert.Null(Scanner.TrustedToolPath("", Profile));
        Assert.Equal(@"C:\Users\you\cache", Scanner.TrustedToolPath(@"C:\Users\you\cache", Profile));
        Assert.Equal(@"C:\Users\you\cache",
            Scanner.TrustedToolPath(@"C:\Users\you\sub\..\cache", Profile));
    }
}
