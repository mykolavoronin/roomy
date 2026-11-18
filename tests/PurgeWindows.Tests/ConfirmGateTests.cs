using PurgeWindows.Core;

namespace PurgeWindows.Tests;

/// <summary>
/// The confirm-dialog reentrancy guard (P1-6): one open confirmation at a time, a
/// second request answers the first with "no" instead of orphaning its waiter.
/// </summary>
public sealed class ConfirmGateTests
{
    [Fact]
    public void ResolveDeliversTheAnswer()
    {
        var gate = new ConfirmGate();
        var task = gate.Request();
        Assert.True(gate.IsOpen);
        Assert.False(task.IsCompleted);

        gate.Resolve(true);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.True(task.Result);
        Assert.False(gate.IsOpen);
    }

    [Fact]
    public void SecondRequestResolvesTheFirstWithFalse()
    {
        var gate = new ConfirmGate();
        var first = gate.Request();
        var second = gate.Request();

        // The first waiter is answered with the safe answer, never orphaned.
        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(first.Result);
        Assert.False(second.IsCompleted);
        Assert.True(gate.IsOpen);

        gate.Resolve(true);
        Assert.True(second.Result);
    }

    [Fact]
    public void DoubleResolveIsSafe()
    {
        var gate = new ConfirmGate();
        var task = gate.Request();
        gate.Resolve(true);
        gate.Resolve(false);
        Assert.True(task.Result);
        Assert.False(gate.IsOpen);
    }

    [Fact]
    public void ResolveWithNothingOpenIsSafe()
    {
        var gate = new ConfirmGate();
        gate.Resolve(true);
        Assert.False(gate.IsOpen);
    }
}
