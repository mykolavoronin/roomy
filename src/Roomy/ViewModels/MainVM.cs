using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Roomy.Core;
using Roomy.Localization;

namespace Roomy.ViewModels;

public enum AppState { Home, Scanning, Results, Cleaning, Done }

public sealed class MainVM : ObservableObject
{
    bool _busy;
    long _demoFreed;

    public MainVM(bool demo)
    {
        IsDemo = demo;
        IsAdmin = demo || Admin.IsElevated;
        ScanCommand = new RelayCommand(() => _ = ScanAsync());
        CleanCommand = new RelayCommand(() => _ = CleanAsync(), () => SelectedTotal > 0);
        RestartAdminCommand = new RelayCommand(RestartAsAdmin);
        ShareCommand = new RelayCommand(() => ShareRequested?.Invoke(this, EventArgs.Empty));
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        HomeCommand = new RelayCommand(() => { RefreshDrives(); State = AppState.Home; });
        ConfirmYesCommand = new RelayCommand(() => CloseConfirm(true));
        ConfirmNoCommand = new RelayCommand(() => CloseConfirm(false));
        SpaceMap = new SpaceMapVM(this);
        RefreshDrives();
        RefreshRemoval();
    }

    public bool IsDemo { get; }
    public bool IsAdmin { get; }
    public bool ShowAdminBadge => Admin.IsElevated && !IsDemo;
    public SpaceMapVM SpaceMap { get; }
    public event EventHandler? ShareRequested;
    public event EventHandler? SettingsRequested;
    public RelayCommand SettingsCommand { get; }

    public RelayCommand ScanCommand { get; }
    public RelayCommand CleanCommand { get; }
    public RelayCommand RestartAdminCommand { get; }
    public RelayCommand ShareCommand { get; }
    public RelayCommand HomeCommand { get; }
    public RelayCommand ConfirmYesCommand { get; }
    public RelayCommand ConfirmNoCommand { get; }

    AppState _state = AppState.Home;
    public AppState State { get => _state; private set => Set(ref _state, value); }

    // ---------- removal: where cleaned files go ----------

    public RemovalMode RemovalSetting { get; private set; }

    void SetRemoval(RemovalMode mode)
    {
        RemovalSetting = mode;
        foreach (var c in Categories) c.RefreshDestination();
        Raise(nameof(SelectedBinTotal));
        Raise(nameof(SelectedDeleteTotal));
        Raise(nameof(HasSelectionSplit));
        Raise(nameof(SelectionSplitText));
    }

    /// <summary>Re-reads the removal choice from settings (the demo always uses the default).</summary>
    public void RefreshRemoval() => SetRemoval(IsDemo
        ? RemovalMode.ReviewToBin
        : new PreferencesStore(PreferencesStore.DefaultPath).Load().Removal);

    // ---------- drives ----------

    public ObservableCollection<DriveVM> Drives { get; } = [];
    public DriveVM? SystemDrive { get; private set; }
    public IEnumerable<DriveVM> OtherDrives => Drives.Where(d => d != SystemDrive);
    public bool HasOtherDrives => OtherDrives.Any();

    public void RefreshDrives()
    {
        var found = new List<(string Root, string Label, long Total, long Free)>();
        if (IsDemo)
        {
            foreach (var d in DemoData.Drives)
                found.Add((d.Root, d.Label, d.Total, d.Root == DemoData.Drives[0].Root ? d.Free + _demoFreed : d.Free));
        }
        else
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                try
                {
                    if (di.DriveType == DriveType.Fixed && di.IsReady)
                        found.Add((di.Name, di.VolumeLabel, di.TotalSize, di.TotalFreeSpace));
                }
                catch { }
            }
        }

        foreach (var f in found)
        {
            var existing = Drives.FirstOrDefault(d => string.Equals(d.Root, f.Root, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Update(f.Total, f.Free);
            else Drives.Add(new DriveVM(f.Root, f.Label, f.Total, f.Free));
        }

        SystemDrive ??= (IsDemo ? Drives.FirstOrDefault() : null)
                        ?? Drives.FirstOrDefault(d => string.Equals(d.Root, Env.SystemRoot, StringComparison.OrdinalIgnoreCase))
                        ?? Drives.FirstOrDefault();
        Raise(nameof(SystemDrive));
        Raise(nameof(OtherDrives));
        Raise(nameof(HasOtherDrives));
    }

    // ---------- scanning ----------

    public ObservableCollection<ScanStepVM> ScanSteps { get; } = [];

    string _scanStep = "";
    public string ScanStep { get => _scanStep; private set => Set(ref _scanStep, value); }

    long _scanFound;
    public long ScanFound { get => _scanFound; private set => Set(ref _scanFound, value); }

    int _scanItems;
    public int ScanItemsFound { get => _scanItems; private set => Set(ref _scanItems, value); }

    int _scanDone;
    public int ScanStepsDone { get => _scanDone; private set => Set(ref _scanDone, value); }

    public double ScanFraction => Scanner.Steps.Count == 0 ? 0 : (double)ScanStepsDone / Scanner.Steps.Count;
    public string ScanTotalsText => Loc.Format("ScanTotalsLine",
        ScanItemsFound.ToString("N0"), ScanStepsDone, Scanner.Steps.Count);

    void RaiseScanTotals()
    {
        Raise(nameof(ScanFraction));
        Raise(nameof(ScanTotalsText));
    }

    async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            ScanSteps.Clear();
            foreach (var s in Scanner.Steps) ScanSteps.Add(new ScanStepVM(s.Title));
            ScanFound = 0;
            ScanItemsFound = 0;
            ScanStepsDone = 0;
            RaiseScanTotals();
            IsCleanupTab = true;
            State = AppState.Scanning;

            var ctx = new ScanContext { IsAdmin = IsAdmin };
            var results = new List<CleanupCategory>();
            for (int i = 0; i < Scanner.Steps.Count; i++)
            {
                var step = Scanner.Steps[i];
                var row = ScanSteps[i];
                row.State = StepState.Active;
                ScanStep = Loc.Format("CheckingStep", step.Title);

                var sw = Stopwatch.StartNew();
                var cat = IsDemo ? DemoData.Category(step.Id) : await Task.Run(() => SafeRun(step, ctx));
                // A tiny pause so each row visibly ticks off; real work usually takes longer anyway.
                int min = IsDemo ? 240 : 90;
                if (sw.ElapsedMilliseconds < min) await Task.Delay(min - (int)sw.ElapsedMilliseconds);

                row.Finish(cat);
                ScanStepsDone = i + 1;
                if (cat != null)
                {
                    results.Add(cat);
                    ScanItemsFound += cat.Items.Count;
                    if (cat.Safety == Safety.Safe && !cat.Blocked) ScanFound += cat.Size;
                }
                RaiseScanTotals();
            }

            BuildCategories(results);
            await Task.Delay(400);
            State = AppState.Results;
        }
        catch (Exception ex)
        {
            ShowToast(S.ScanProblem + ex.Message);
            State = AppState.Home;
        }
        finally
        {
            _busy = false;
        }
    }

    static CleanupCategory? SafeRun(ScanStep s, ScanContext c)
    {
        try { return s.Run(c); }
        catch { return null; }
    }

    // ---------- results ----------

    public ObservableCollection<CategoryVM> Categories { get; } = [];

    void BuildCategories(List<CleanupCategory> results)
    {
        Categories.Clear();
        var shown = results.Where(c => c.Blocked || c.Size > 0).ToList();
        var ordered = shown.Where(c => c.Safety == Safety.Safe && !c.Blocked).OrderByDescending(c => c.Size)
            .Concat(shown.Where(c => c.Blocked))
            .Concat(shown.Where(c => c.Safety == Safety.Review && !c.Blocked).OrderByDescending(c => c.Size));
        foreach (var c in ordered) Categories.Add(new CategoryVM(c, this));

        Raise(nameof(SafeTotal));
        Raise(nameof(ReviewTotal));
        Raise(nameof(ReviewSummary));
        Raise(nameof(ShowAdminBanner));
        Raise(nameof(NothingFound));
        RefreshRemoval();
        Recalc();
    }

    public long SafeTotal => Categories.Where(c => c.IsSafe && c.CanSelect).Sum(c => c.Size);
    public long ReviewTotal => Categories.Where(c => !c.IsSafe && !c.IsBlocked).Sum(c => c.Size);
    public bool NothingFound => Categories.Count == 0;
    public string ReviewSummary => ReviewTotal > 0
        ? Loc.Format("PlusMore", Format.Bytes(ReviewTotal))
        : S.EverythingFound;
    public bool ShowAdminBanner => !IsAdmin && Categories.Any(c => c.IsBlocked);

    public long SelectedTotal => Categories.Sum(c => c.SelectedSize);
    public string CleanButtonText => SelectedTotal > 0 ? Loc.Format("CleanUpAmount", Format.Bytes(SelectedTotal)) : S.PickSomething;

    /// <summary>How much of the selection goes to the Recycle Bin vs being deleted for good.</summary>
    public long SelectedBinTotal => Categories
        .Where(c => c.CanSelect && c.Model.Action == CleanAction.Delete && RemovalPolicy.GoesToBin(RemovalSetting, c.Model))
        .Sum(c => c.SelectedSize);
    public long SelectedDeleteTotal => Categories
        .Where(c => c.CanSelect && c.Model.Action == CleanAction.Delete && !RemovalPolicy.GoesToBin(RemovalSetting, c.Model))
        .Sum(c => c.SelectedSize);
    public bool HasSelectionSplit => SelectedBinTotal > 0;
    public string SelectionSplitText => SelectedDeleteTotal > 0
        ? Loc.Format("SelectionSplitBoth", Format.Bytes(SelectedBinTotal), Format.Bytes(SelectedDeleteTotal))
        : Loc.Format("SelectionSplitBin", Format.Bytes(SelectedBinTotal));

    internal void Recalc()
    {
        Raise(nameof(SelectedTotal));
        Raise(nameof(CleanButtonText));
        Raise(nameof(SelectedBinTotal));
        Raise(nameof(SelectedDeleteTotal));
        Raise(nameof(HasSelectionSplit));
        Raise(nameof(SelectionSplitText));
        CleanCommand.RaiseCanExecuteChanged();
    }

    bool _spaceTab;
    public bool IsCleanupTab
    {
        get => !_spaceTab;
        set
        {
            if (!value || !_spaceTab) return;
            _spaceTab = false;
            Raise(nameof(IsCleanupTab));
            Raise(nameof(IsSpaceTab));
        }
    }
    public bool IsSpaceTab
    {
        get => _spaceTab;
        set
        {
            if (!value || _spaceTab) return;
            _spaceTab = true;
            Raise(nameof(IsCleanupTab));
            Raise(nameof(IsSpaceTab));
            SpaceMap.EnsureScanned();
        }
    }

    // ---------- cleaning ----------

    double _cleanProgress;
    public double CleanProgress { get => _cleanProgress; private set => Set(ref _cleanProgress, value); }

    string _cleanStep = "";
    public string CleanStep { get => _cleanStep; private set => Set(ref _cleanStep, value); }

    string _cleanItem = "";
    public string CleanCurrentItem { get => _cleanItem; private set => Set(ref _cleanItem, value); }

    int _cleanFiles;
    public int CleanFilesSoFar { get => _cleanFiles; private set => Set(ref _cleanFiles, value); }

    long _cleanFreed, _cleanRecycled;
    public long CleanFreedSoFar { get => _cleanFreed; private set => Set(ref _cleanFreed, value); }
    public long CleanRecycledSoFar { get => _cleanRecycled; private set => Set(ref _cleanRecycled, value); }

    public string CleanStatsText => Loc.Format("CleanStats",
        CleanFilesSoFar.ToString("N0"), Loc.Files(CleanFilesSoFar), Format.Bytes(CleanFreedSoFar));
    public bool HasCleanRecycled => CleanRecycledSoFar > 0;
    public string CleanRecycledText => Loc.Format("RecycledSoFar", Format.Bytes(CleanRecycledSoFar));

    void RaiseCleanStats()
    {
        Raise(nameof(CleanStatsText));
        Raise(nameof(HasCleanRecycled));
        Raise(nameof(CleanRecycledText));
    }

    long _freed;
    public long Freed { get => _freed; private set => Set(ref _freed, value); }

    int _filesRemoved;
    public int FilesRemoved { get => _filesRemoved; private set => Set(ref _filesRemoved, value); }
    public string FilesRemovedText => Loc.Format("FilesRemovedTotal",
        FilesRemoved.ToString("N0"), Loc.Files(FilesRemoved));

    public ObservableCollection<DoneCategoryVM> DoneCategories { get; } = [];
    public bool HasDoneBreakdown => DoneCategories.Count > 0;
    public bool HasDoneSplit => Freed > 0 && Recycled > 0;
    public string DoneSplitText => Loc.Format("DoneSplit", Format.Bytes(Freed), Format.Bytes(Recycled));

    long _skipped;
    public long Skipped { get => _skipped; private set => Set(ref _skipped, value); }
    public bool HasSkipped => Skipped > 100L * 1024 * 1024;
    public string SkippedText => Loc.Format("InUseSkippedMessage", Format.Bytes(Skipped));

    long _recycled;
    public long Recycled { get => _recycled; private set => Set(ref _recycled, value); }
    public bool HasRecycled => Recycled > 0;
    public string RecycledText => Loc.Format("RecycledMessage", Format.Bytes(Recycled));

    double _before, _after;
    public double BeforeFraction { get => _before; private set => Set(ref _before, value); }
    public double AfterFraction { get => _after; private set => Set(ref _after, value); }

    string _beforeText = "", _afterText = "", _doneSubtitle = "";
    public string BeforeText { get => _beforeText; private set => Set(ref _beforeText, value); }
    public string AfterText { get => _afterText; private set => Set(ref _afterText, value); }
    public string DoneSubtitle { get => _doneSubtitle; private set => Set(ref _doneSubtitle, value); }

    Brush? _beforeBrush;
    public Brush? BeforeBrush { get => _beforeBrush; private set => Set(ref _beforeBrush, value); }

    async Task CleanAsync()
    {
        if (_busy) return;
        var jobs = Categories.Where(c => c.CanSelect)
            .Select(c => new CleanJob(c.Model, c.SelectedItems()))
            .Where(j => j.Items.Count > 0)
            .ToList();
        if (jobs.Count == 0) return;

        // Read fresh so the choice also applies after "Restart as admin".
        var mode = IsDemo ? RemovalMode.ReviewToBin : new PreferencesStore(PreferencesStore.DefaultPath).Load().Removal;
        SetRemoval(mode);

        // Confirm whenever "your call" files are involved, or when everything goes to the
        // Recycle Bin (the space is only freed once the bin is emptied). The dialog lists
        // every category with where it goes, so there are no surprises.
        var lines = jobs.Select(j => ConfirmLineFor(j, mode)).ToList();
        bool anyReview = jobs.Any(j => j.Category.Safety == Safety.Review);
        if (anyReview || mode == RemovalMode.EverythingToBin)
        {
            bool allBin = lines.All(l => l.Destination == ConfirmDestination.Bin);
            bool allDeleted = lines.All(l => l.Destination == ConfirmDestination.Deleted);
            var (title, body, yes) = (allBin, allDeleted) switch
            {
                (true, _) => (S.MoveToBinTitle, S.MoveToBinWarning, S.MoveToBin),
                (_, true) => (S.DeleteTheseForGood, S.DeleteWarning, S.DeletePermanently),
                _ => (S.ConfirmCleanTitle, S.ConfirmMixedBody, S.CleanUp),
            };
            if (!await ConfirmAsync(title, body, yes, lines)) return;
        }

        _busy = true;
        try
        {
            var drive = SystemDrive;
            BeforeFraction = drive?.UsedFraction ?? 0;
            BeforeBrush = drive?.BarBrush;
            BeforeText = drive is null ? "" : Loc.Format("FreeBytes", Format.Bytes(drive.Free));
            CleanProgress = 0;
            CleanStep = S.GettingStarted;
            CleanCurrentItem = "";
            CleanFilesSoFar = 0;
            CleanFreedSoFar = 0;
            CleanRecycledSoFar = 0;
            RaiseCleanStats();
            State = AppState.Cleaning;

            var progress = new Progress<CleanProgress>(p =>
            {
                CleanProgress = p.Fraction;
                CleanStep = p.Step;
                CleanCurrentItem = p.CurrentItem;
                CleanFilesSoFar = p.FilesSoFar;
                CleanFreedSoFar = p.FreedSoFar;
                CleanRecycledSoFar = p.RecycledSoFar;
                RaiseCleanStats();
            });

            CleanResult r;
            if (IsDemo)
            {
                r = await DemoClean(jobs, mode, progress);
                _demoFreed += r.Freed;
            }
            else
            {
                r = await Task.Run(() => Cleaner.Run(jobs, progress, mode));
                await Task.Delay(500);
            }

            Freed = r.Freed;
            FilesRemoved = r.Files;
            Skipped = r.Skipped;
            Recycled = r.Recycled;
            DoneCategories.Clear();
            foreach (var c in (r.Categories ?? []).Where(c => c.Freed + c.Recycled > 0).OrderByDescending(c => c.Freed + c.Recycled))
                DoneCategories.Add(new DoneCategoryVM(c));
            RefreshDrives();
            AfterFraction = drive?.UsedFraction ?? 0;
            AfterText = drive is null ? "" : Loc.Format("FreeBytes", Format.Bytes(drive.Free));
            DoneSubtitle = drive is null ? "" : Loc.Format("DriveNowFree", drive.Letter, Format.Bytes(drive.Free));
            Raise(nameof(FilesRemovedText));
            Raise(nameof(HasDoneBreakdown));
            Raise(nameof(HasDoneSplit));
            Raise(nameof(DoneSplitText));
            Raise(nameof(HasSkipped));
            Raise(nameof(SkippedText));
            Raise(nameof(HasRecycled));
            Raise(nameof(RecycledText));
            State = AppState.Done;
        }
        catch (Exception ex)
        {
            ShowToast(S.CleaningStopped + ex.Message);
            State = AppState.Results;
        }
        finally
        {
            _busy = false;
        }
    }

    static ConfirmLine ConfirmLineFor(CleanJob job, RemovalMode mode)
    {
        var dest = job.Category.Action == CleanAction.EmptyRecycleBin ? ConfirmDestination.BinEmptied
            : RemovalPolicy.GoesToBin(mode, job.Category) ? ConfirmDestination.Bin : ConfirmDestination.Deleted;
        return new ConfirmLine(job.Category.Title, Format.Bytes(job.Items.Sum(i => i.Size)), dest);
    }

    async Task<CleanResult> DemoClean(List<CleanJob> jobs, RemovalMode mode, IProgress<CleanProgress> progress)
    {
        long total = Math.Max(1, jobs.Sum(j => j.Items.Sum(i => i.Size)));
        long done = 0, freed = 0, recycled = 0;
        int files = 0;
        var perCategory = new List<CategoryCleanResult>();
        foreach (var j in jobs)
        {
            var step = Loc.Format("CleaningCategory", j.Category.Title.ToLowerInvariant());
            progress.Report(new CleanProgress((double)done / total, step, "", freed, recycled, files));
            bool binJob = j.Category.Action == CleanAction.EmptyRecycleBin;
            bool recycle = RemovalPolicy.GoesToBin(mode, j.Category);
            long jobFreed = 0, jobRecycled = 0;
            int jobFiles = 0;
            foreach (var item in j.Items)
            {
                if (binJob || !recycle) { jobFreed += item.Size; freed += item.Size; }
                else { jobRecycled += item.Size; recycled += item.Size; }
                files += item.FileCount;
                jobFiles += item.FileCount;
                done += item.Size;
                progress.Report(new CleanProgress((double)done / total, step, item.Name, freed, recycled, files));
                await Task.Delay(110);
            }
            var dest = binJob ? CleanDestination.BinEmptied
                : recycle ? CleanDestination.Recycled : CleanDestination.Deleted;
            perCategory.Add(new CategoryCleanResult(j.Category.Id, j.Category.Title, jobFreed, jobRecycled, 0, jobFiles, dest));
            await Task.Delay(120);
        }
        await Task.Delay(300);
        progress.Report(new CleanProgress(1, S.AllDone, "", freed, recycled, files));
        return new CleanResult(freed, 0, files, recycled, perCategory);
    }

    void RestartAsAdmin()
    {
        if (IsDemo) { ShowToast(S.AdminButtonHint); return; }
        if (Admin.RestartElevated("--scan")) Application.Current.Shutdown();
        else ShowToast(S.NoAdminWorries);
    }

    // ---------- confirm dialog ----------

    TaskCompletionSource<bool>? _confirm;

    bool _confirmVisible;
    public bool ConfirmVisible { get => _confirmVisible; private set => Set(ref _confirmVisible, value); }

    string _confirmTitle = "", _confirmBody = "", _confirmYes = "";
    public string ConfirmTitle { get => _confirmTitle; private set => Set(ref _confirmTitle, value); }
    public string ConfirmBody { get => _confirmBody; private set => Set(ref _confirmBody, value); }
    public string ConfirmYesText { get => _confirmYes; private set => Set(ref _confirmYes, value); }

    public ObservableCollection<ConfirmLine> ConfirmLines { get; } = [];
    public bool HasConfirmLines => ConfirmLines.Count > 0;

    Task<bool> ConfirmAsync(string title, string body, string yes, IReadOnlyList<ConfirmLine> lines)
    {
        ConfirmTitle = title;
        ConfirmBody = body;
        ConfirmYesText = yes;
        ConfirmLines.Clear();
        foreach (var l in lines) ConfirmLines.Add(l);
        Raise(nameof(HasConfirmLines));
        _confirm = new TaskCompletionSource<bool>();
        ConfirmVisible = true;
        return _confirm.Task;
    }

    void CloseConfirm(bool result)
    {
        ConfirmVisible = false;
        _confirm?.TrySetResult(result);
        _confirm = null;
    }

    // ---------- toast ----------

    string _toast = "";
    public string ToastText { get => _toast; private set => Set(ref _toast, value); }

    bool _toastVisible;
    public bool ToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }

    int _toastId;
    public async void ShowToast(string text)
    {
        ToastText = text;
        var id = ++_toastId;
        ToastVisible = true;
        await Task.Delay(3400);
        if (id == _toastId) ToastVisible = false;
    }

    // ---------- used by the screenshot runner ----------

    internal void ExpandOnly(string id)
    {
        foreach (var c in Categories) c.IsExpanded = c.Model.Id == id;
    }
}

/// <summary>Where one category's files go, as shown in the pre-clean confirmation.</summary>
public enum ConfirmDestination { Deleted, Bin, BinEmptied }

public sealed record ConfirmLine(string Title, string SizeText, ConfirmDestination Destination)
{
    public string DestinationText => Destination switch
    {
        ConfirmDestination.Bin => S.CatRecycle,
        ConfirmDestination.BinEmptied => S.BinEmptied,
        _ => S.DeletedForGood,
    };
    public Brush DestinationBg => Res.Brush(Destination switch
    {
        ConfirmDestination.Bin => "WarnSoftBrush",
        ConfirmDestination.BinEmptied => "MutedSoftBrush",
        _ => "DangerSoftBrush",
    });
    public Brush DestinationFg => Res.Brush(Destination switch
    {
        ConfirmDestination.Bin => "WarnBrush",
        ConfirmDestination.BinEmptied => "MutedBrush",
        _ => "DangerBrush",
    });
}

/// <summary>One row of the done screen's per-category breakdown.</summary>
public sealed class DoneCategoryVM(CategoryCleanResult result)
{
    public string Title => result.Title;
    public string AmountText => Format.Bytes(result.Freed + result.Recycled);
    public bool HasFiles => result.Files > 0;
    public string FilesText => $"{result.Files:N0} {Loc.Files(result.Files)}";
    public string DestinationText => result.Destination switch
    {
        CleanDestination.Recycled => S.CatRecycle,
        CleanDestination.BinEmptied => S.BinEmptied,
        _ => S.DeletedForGood,
    };
    public Brush DestinationBg => Res.Brush(result.Destination switch
    {
        CleanDestination.Recycled => "WarnSoftBrush",
        CleanDestination.BinEmptied => "MutedSoftBrush",
        _ => "DangerSoftBrush",
    });
    public Brush DestinationFg => Res.Brush(result.Destination switch
    {
        CleanDestination.Recycled => "WarnBrush",
        CleanDestination.BinEmptied => "MutedBrush",
        _ => "DangerBrush",
    });
}
