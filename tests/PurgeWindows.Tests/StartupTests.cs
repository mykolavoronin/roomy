using PurgeWindows.Services;

namespace PurgeWindows.Tests;

public sealed class StartupTests
{
    [Theory]
    [InlineData(@"C:\Program Files\PurgeWindows\PurgeWindows.exe")]
    [InlineData(@"D:\My downloads\PurgeWindows-portable.exe")]
    public void StartupQuotesInstallerAndPortablePaths(string path) =>
        Assert.Equal($"\"{path}\" --background", StartupRegistration.StartupCommand(path));

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData("PurgeWindows.exe")]
    [InlineData(@"C:\PurgeWindows\PurgeWindows.dll")]
    public void StartupRejectsFrameworkHostsAndIncompletePaths(string path) =>
        Assert.Throws<InvalidOperationException>(() => StartupRegistration.StartupCommand(path));

    [Fact]
    public void StartupRejectsCommandsBeyondWindowsRunLimit() =>
        Assert.Throws<InvalidOperationException>(() => StartupRegistration.StartupCommand(@"C:\" + new string('x', 250) + @"\PurgeWindows.exe"));
}
