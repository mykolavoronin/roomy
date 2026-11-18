using PurgeWindows.Localization;
namespace PurgeWindows.Core;

public sealed record CleanJob(CleanupCategory Category, IReadOnlyList<CleanupItem> Items);

/// <summary>Where one category's files ended up.</summary>
public enum CleanDestination
{
    /// <summary>Deleted directly. The space is freed right away.</summary>
    Deleted,
    /// <summary>Moved to the Recycle Bin. The space is freed once the bin is emptied.</summary>
    Recycled,
    /// <summary>The Recycle Bin itself was emptied.</summary>
    BinEmptied,
}

/// <summary>What one category contributed to a cleanup, for the done screen.</summary>
public sealed record CategoryCleanResult(string CategoryId, string Title, long Freed, long Recycled, long Skipped, int Files, CleanDestination Destination);

/// <summary>Live progress during a cleanup. Every value is measured, never estimated.</summary>
public sealed record CleanProgress(double Fraction, string Step, string CurrentItem, long FreedSoFar, long RecycledSoFar, int FilesSoFar);

/// <param name="Recycled">Bytes moved to the Recycle Bin. They are not counted in <paramref name="Freed"/>.</param>
public sealed record CleanResult(long Freed, long Skipped, int Files, long Recycled = 0, IReadOnlyList<CategoryCleanResult>? Categories = null);

public static class Cleaner
{
    /// <param name="mode">Where cleaned files go. By default, "your call" items go to the Recycle Bin.</param>
    public static CleanResult Run(IReadOnlyList<CleanJob> jobs, IProgress<CleanProgress>? progress,
        RemovalMode mode = RemovalMode.ReviewToBin, CancellationToken ct = default)
    {
        long total = Math.Max(1, jobs.Sum(j => j.Items.Sum(i => i.Size)));
        long done = 0, freed = 0, skipped = 0, recycled = 0;
        int files = 0;
        var perCategory = new List<CategoryCleanResult>();

        // Empty the bin before recycling anything, so this cleanup never wipes the files it just recycled.
        if (mode != RemovalMode.EverythingPermanent)
            jobs = jobs.OrderBy(j => j.Category.Action == CleanAction.EmptyRecycleBin ? 0 : 1).ToList();

        foreach (var job in jobs)
        {
            var step = Loc.Format("CleaningCategory", job.Category.Title.ToLowerInvariant());
            progress?.Report(new CleanProgress((double)done / total, step, "", freed, recycled, files));
            long jobFreed = 0, jobRecycled = 0, jobSkipped = 0;
            int jobFiles = 0;

            if (job.Category.Action == CleanAction.EmptyRecycleBin)
            {
                long size = job.Items.Sum(i => i.Size);
                var (before, beforeItems) = Native.QueryRecycleBin();
                Native.SHEmptyRecycleBin(IntPtr.Zero, null,
                    Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
                var (after, afterItems) = Native.QueryRecycleBin();
                long got = Math.Max(0, before - after);
                int gotFiles = (int)Math.Max(0, beforeItems - afterItems);
                freed += got;
                files += gotFiles;
                jobFreed = got;
                jobFiles = gotFiles;
                jobSkipped = Math.Max(0, size - got);
                skipped += jobSkipped;
                done += size;
                perCategory.Add(new CategoryCleanResult(job.Category.Id, job.Category.Title,
                    jobFreed, 0, jobSkipped, jobFiles, CleanDestination.BinEmptied));
                progress?.Report(new CleanProgress((double)done / total, step, "", freed, recycled, files));
                continue;
            }

            if (job.Category.Action != CleanAction.Delete) continue;
            bool recycle = RemovalPolicy.GoesToBin(mode, job.Category);

            foreach (var item in job.Items)
            {
                ct.ThrowIfCancellationRequested();
                long got = 0;
                foreach (var p in item.Paths)
                {
                    try
                    {
                        if (recycle)
                        {
                            var r = FileOps.RecycleContents(p, item.MinAge, ct);
                            recycled += r.Freed;
                            jobRecycled += r.Freed;
                            files += r.Files;
                            jobFiles += r.Files;
                            got += r.Freed;
                        }
                        else
                        {
                            var r = FileOps.Delete(p, item.MinAge, ct);
                            freed += r.Freed;
                            jobFreed += r.Freed;
                            got += r.Freed;
                            files += r.Files;
                            jobFiles += r.Files;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* protected or vanished: skip */ }
                }
                long itemSkipped = Math.Max(0, item.Size - got);
                skipped += itemSkipped;
                jobSkipped += itemSkipped;
                done += item.Size;
                progress?.Report(new CleanProgress((double)done / total, step, item.Name, freed, recycled, files));
            }
            perCategory.Add(new CategoryCleanResult(job.Category.Id, job.Category.Title,
                jobFreed, jobRecycled, jobSkipped, jobFiles,
                recycle ? CleanDestination.Recycled : CleanDestination.Deleted));
        }

        progress?.Report(new CleanProgress(1, S.AllDone, "", freed, recycled, files));
        return new CleanResult(freed, skipped, files, recycled, perCategory);
    }
}
