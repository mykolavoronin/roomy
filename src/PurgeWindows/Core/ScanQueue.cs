namespace PurgeWindows.Core;

/// <summary>What one scan step is doing, as reported by <see cref="ScanProgressEvent"/>.</summary>
public enum ScanStepState
{
    Queued,
    Active,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// One scan-progress event (§4.6 <c>ScanProgressEvent</c> shape, adapted: there is
/// NO Basic/Advanced scope field — there are no modes). Steps run strictly one at
/// a time; events are the single progress/cancel contract for scanning, mirroring
/// the cleaning event contract.
/// </summary>
public sealed record ScanProgressEvent(
    int StepIndex,
    int StepCount,
    string StepId,
    string StepTitle,
    ScanStepState State,
    string CurrentPath,
    int ItemsFound,
    long BytesFound,
    TimeSpan Elapsed,
    /// <summary>The step's result, present only on Done events.</summary>
    CleanupCategory? Category = null,
    /// <summary>Failure detail, present only on Failed events.</summary>
    string Detail = "")
{
    /// <summary>Overall fraction by steps completed (0..1). Boundary states count as done.</summary>
    public double Fraction => StepCount == 0 ? 0 :
        Math.Clamp((StepIndex + (State is ScanStepState.Done or ScanStepState.Failed or ScanStepState.Cancelled ? 1 : 0)) / (double)StepCount, 0, 1);
}

/// <summary>
/// Runs scan steps strictly one at a time, in order, emitting
/// <see cref="ScanProgressEvent"/>s. A failing step reports Failed and the queue
/// carries on — one bad step must never take down the whole scan. Cancellation is
/// cooperative: the token is checked between steps and passed into every step via
/// the <see cref="ScanContext"/>, and the in-flight step's own checks stop it.
/// <para/>
/// This replaces the sequential step loop that used to live in MainVM: path
/// knowledge stays in <see cref="Scanner"/>, orchestration lives here.
/// </summary>
public sealed class ScanQueue
{
    /// <summary>
    /// Runs <paramref name="steps"/> in order. Returns the categories produced
    /// (null results — steps that found nothing worth showing — are dropped).
    /// <paramref name="onStepCompleted"/> runs after each step on the caller's
    /// context (MainVM uses it for per-row pacing); it is optional and its
    /// failures are swallowed like any other step-boundary failure.
    /// </summary>
    public static async Task<IReadOnlyList<CleanupCategory>> RunAsync(
        IReadOnlyList<ScanStep> steps,
        ScanContext context,
        IProgress<ScanProgressEvent>? progress = null,
        CancellationToken ct = default,
        Func<ScanStep, CleanupCategory?, Task>? onStepCompleted = null)
    {
        var results = new List<CleanupCategory>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int itemsFound = 0;
        long bytesFound = 0;

        void Emit(int i, ScanStep step, ScanStepState state, string path = "",
            CleanupCategory? category = null, string detail = "")
        {
            try
            {
                progress?.Report(new ScanProgressEvent(i, steps.Count, step.Id, step.Title, state, path,
                    itemsFound, bytesFound, watch.Elapsed, category, detail));
            }
            catch { /* a listener must never break the scan */ }
        }

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (ct.IsCancellationRequested)
            {
                Emit(i, step, ScanStepState.Cancelled);
                break;
            }
            Emit(i, step, ScanStepState.Active);
            CleanupCategory? cat = null;
            string failure = "";
            try
            {
                // Steps do blocking filesystem work; they run off the caller's
                // thread. The token goes to the step (via the context), not to
                // Task.Run, so a cancelled scan reports Cancelled, not a bare
                // TaskCanceledException with nothing in it.
                cat = await Task.Run(() => step.Run(context), ct);
            }
            catch (OperationCanceledException)
            {
                Emit(i, step, ScanStepState.Cancelled);
                break;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
            }

            if (failure.Length > 0)
            {
                Emit(i, step, ScanStepState.Failed, detail: failure);
            }
            else
            {
                if (cat is not null)
                {
                    results.Add(cat);
                    itemsFound += cat.Items.Count;
                    bytesFound += cat.Size;
                }
                Emit(i, step, ScanStepState.Done, category: cat);
            }

            if (onStepCompleted is not null)
            {
                try { await onStepCompleted(step, cat); }
                catch { /* pacing must never break the scan */ }
            }
        }
        return results;
    }
}
