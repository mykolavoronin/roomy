namespace PurgeWindows.Core;

/// <summary>
/// Owns one at most open confirmation dialog (P1-6). A second request while one is
/// open resolves the first waiter with <c>false</c> (the safe answer: nothing
/// proceeds on a stale dialog) instead of orphaning it — a waiter must never hang
/// forever because a newer dialog replaced its own. Single-threaded by design: the
/// UI thread is the only caller, as with the dialog itself.
/// </summary>
public sealed class ConfirmGate
{
    TaskCompletionSource<bool>? _open;

    /// <summary>True while a confirmation is waiting for an answer.</summary>
    public bool IsOpen => _open is not null;

    /// <summary>
    /// Opens a confirmation and returns its answer task. If one is already open,
    /// the old waiter is resolved with <c>false</c> first (P1-6 reentrancy guard).
    /// </summary>
    public Task<bool> Request()
    {
        var previous = _open;
        var next = new TaskCompletionSource<bool>();
        _open = next;
        previous?.TrySetResult(false);
        return next.Task;
    }

    /// <summary>Answers the open confirmation, if any. Extra answers are ignored.</summary>
    public void Resolve(bool result)
    {
        var open = _open;
        _open = null;
        open?.TrySetResult(result);
    }
}
