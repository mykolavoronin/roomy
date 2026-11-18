using Roomy.Core;

namespace Roomy.Tests;

public sealed class WindowsFeaturesTests
{
    [Fact]
    public void PowerCfgArgsUseHibernationOnAndOff()
    {
        Assert.Equal("/h on", WindowsFeatures.PowerCfgArgs(true));
        Assert.Equal("/h off", WindowsFeatures.PowerCfgArgs(false));
    }

    [Fact]
    public void ReadingTheHibernationSettingNeverThrows()
    {
        // The value depends on the machine; the read itself must always be safe.
        _ = WindowsFeatures.HibernationEnabled();
    }

    [Fact]
    public void HiberfilSizeIsNeverNegative()
    {
        Assert.True(WindowsFeatures.HiberfilSize() >= 0);
    }

    [Fact]
    public void DeliveryOptimizationCacheLivesUnderWindows()
    {
        Assert.StartsWith(Env.Windows, WindowsFeatures.DeliveryOptimizationCache, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HiberfilPathIsOnTheSystemDriveRoot()
    {
        Assert.Equal(Path.Combine(Env.SystemRoot, "hiberfil.sys"), WindowsFeatures.HiberfilPath);
    }
}
