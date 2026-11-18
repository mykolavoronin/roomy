using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using PurgeWindows.Core;
using PurgeWindows.Localization;
using PurgeWindows.Services;

namespace PurgeWindows.ViewModels;

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
        CancelCleanCommand = new RelayCommand(CancelClean);
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
    public RelayCommand CancelCleanCommand { get; }

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

    CancellationTokenSource? _scanCts;

    /// <summary>Asks the running scan to stop between steps (engine support; the
    /// scan-Stop button itself is a later wave). A stopped scan returns Home.</summary>
    public void StopScan() => _scanCts?.Cancel();

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
        SpaceMap.CancelScan();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
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

            // The demo reuses the same queue with steps that return demo data,
            // so the scan view behaves identically in both modes.
            var steps = IsDemo
                ? Scanner.Steps.Select(s => new ScanStep(s.Id, s.Title, _ => DemoData.Category(s.Id))).ToList()
                : Scanner.Steps;
            var ctx = new ScanContext { IsAdmin = IsAdmin, Token = _scanCts.Token };
            // Progress<T> marshals events back to the UI thread; the coalescer
            // keeps Active updates to one per 140 ms while boundary events stay
            // immediate. The queue is the single progress/cancel contract for
            // scanning, mirroring the cleaning event contract (P1-7).
            using var coalescer = new ScanCoalescer(new Progress<ScanProgressEvent>(OnScanEvent));

            // A tiny pause so each row visibly ticks off; real work usually takes longer anyway.
            var pace = Stopwatch.StartNew();
            async Task PaceStep(ScanStep _, CleanupCategory? _)
            {
                int min = IsDemo ? 240 : 90;
                var left = min - (int)pace.ElapsedMilliseconds;
                pace.Restart();
                if (left > 0) await Task.Delay(left);
            }

            var results = await ScanQueue.RunAsync(steps, ctx, coalescer, _scanCts.Token, PaceStep);
            coalescer.Flush();
            if (_scanCts.Token.IsCancellationRequested)
            {
                State = AppState.Home;
                return;
            }

            BuildCategories(results.ToList());
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

    void OnScanEvent(ScanProgressEvent e)
    {
        if (e.StepIndex < 0 || e.StepIndex >= ScanSteps.Count) return;
        var row = ScanSteps[e.StepIndex];
        switch (e.State)
        {
            case ScanStepState.Active:
                row.State = StepState.Active;
                ScanStep = Loc.Format("CheckingStep", e.StepTitle);
                break;
            case ScanStepState.Done:
                row.Finish(e.Category);
                ScanStepsDone = e.StepIndex + 1;
                if (e.Category is { } cat)
                {
                    ScanItemsFound += cat.Items.Count;
                    if (cat.Tier == SafetyLevel.Safe && !cat.Blocked) ScanFound += cat.Size;
                }
                RaiseScanTotals();
                break;
            case ScanStepState.Failed:
                // A failed step reports nothing and the scan carries on.
                row.Finish(null);
                ScanStepsDone = e.StepIndex + 1;
                RaiseScanTotals();
                break;
            case ScanStepState.Cancelled:
                break;
        }
    }

    // ---------- results ----------

    public ObservableCollection<CategoryVM> Categories { get; } = [];

    void BuildCategories(List<CleanupCategory> results)
    {
        Categories.Clear();
        var shown = results.Where(c => c.Blocked || c.Size > 0).ToList();
        var ordered = shown.Where(c => c.Tier == SafetyLevel.Safe && !c.Blocked).OrderByDescending(c => c.Size)
            .Concat(shown.Where(c => c.Blocked))
            .Concat(shown.Where(c => c.Tier == SafetyLevel.CheckFirst && !c.Blocked).OrderByDescending(c => c.Size))
            .Concat(shown.Where(c => c.Tier == SafetyLevel.ReportOnly && !c.Blocked).OrderByDescending(c => c.Size));
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

    // ---------- per-part cleaning progress ----------

    CleanProgressModel? _cleanModel;
    readonly Dictionary<string, CleanPartVM> _cleanRows = new(StringComparer.Ordinal);
    CancellationTokenSource? _cleanCts;
    bool _cancellingClean;
    TaskCompletionSource<bool>? _cleanFinished;

    /// <summary>Completes when the current cleanup finishes (or immediately when none runs).
    /// The window-close path awaits it with a bound instead of trapping the user.</summary>
    public Task CleanCompletion => _cleanFinished?.Task ?? Task.CompletedTask;

    public bool IsCleaning => State == AppState.Cleaning;

    /// <summary>The parts being cleaned, in the order they run, with live states and totals.</summary>
    public ObservableCollection<CleanPartVM> CleanParts { get; } = [];
    public bool HasCleanParts => CleanParts.Count > 0;

    string _cleanPath = "";
    /// <summary>The folder or file being processed right now.</summary>
    public string CleanCurrentPath
    {
        get => _cleanPath;
        private set { if (Set(ref _cleanPath, value)) Raise(nameof(HasCleanCurrentPath)); }
    }
    public bool HasCleanCurrentPath => CleanCurrentPath.Length > 0;

    public string CleanPartsText => _cleanModel is null ? ""
        : Loc.Format("PartsProgressLine", _cleanModel.PartsDone, _cleanModel.PartCount);

    public bool CanCancelClean => _cleanCts is not null && !_cancellingClean && State == AppState.Cleaning;
    public string CancelButtonText => _cancellingClean ? S.StoppingCleaning : S.StopCleaning;

    void RaiseCancelState()
    {
        Raise(nameof(CanCancelClean));
        Raise(nameof(CancelButtonText));
    }

    /// <summary>Stops after the current file operation / batch. Finished parts stay done;
    /// partial totals are exact; nothing half-removed is claimed.</summary>
    void CancelClean()
    {
        if (_cleanCts is null || _cancellingClean) return;
        _cancellingClean = true;
        CleanStep = S.StoppingCleaning;
        RaiseCancelState();
        _cleanCts.Cancel();
    }

    /// <summary>Requests a stop from outside the cleaning view (window close / quit).</summary>
    public void RequestCleanStop() => CancelClean();

    /// <summary>Applies one progress report to the part list. Runs on the UI thread.
    /// The overall bar follows the engine's single fraction definition, never a second
    /// computation; the model drives the per-part rows only.</summary>
    void ApplyCleanPartReport(CleanProgress p)
    {
        CleanProgress = p.Fraction;
        if (_cleanModel is null) return;
        if (p.CategoryId.Length > 0 && _cleanRows.TryGetValue(p.CategoryId, out var row))
        {
            if (p.PartFailed)
                _cleanModel.FailPart(p.CategoryId, p.PartFilesSoFar, p.PartFreedSoFar, p.PartRecycledSoFar, p.PartSkippedSoFar);
            else if (p.PartDone)
                _cleanModel.FinishPart(p.CategoryId, p.PartFilesSoFar, p.PartFreedSoFar, p.PartRecycledSoFar, p.PartSkippedSoFar);
            else
            {
                // Live and cancelled reports alike: the cancelled report carries the
                // part's final partial totals, which the row must show before the
                // finish step marks it Cancelled.
                _cleanModel.StartPart(p.CategoryId);
                _cleanModel.UpdatePart(p.CategoryId, p.PartFraction, p.CurrentPath, p.CurrentItem,
                    p.PartFilesSoFar, p.PartFreedSoFar, p.PartRecycledSoFar);
            }
            row.Refresh();
        }
        Raise(nameof(CleanPartsText));
    }

    void RefreshCleanRows()
    {
        foreach (var row in CleanParts) row.Refresh();
        Raise(nameof(CleanPartsText));
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

    bool _wasCancelled;
    /// <summary>True when the user stopped the cleanup early; the done screen says so plainly.</summary>
    public bool WasCleanCancelled { get => _wasCancelled; private set => Set(ref _wasCancelled, value); }

    /// <summary>Parts that failed outright. Named on the done screen instead of hidden.</summary>
    public bool HasFailedParts => _cleanModel?.Parts.Any(p => p.State == CleanPartState.Failed) == true;
    public string FailedPartsText => _cleanModel is null ? "" : Loc.Format("FailedPartsLine",
        string.Join(", ", _cleanModel.Parts.Where(p => p.State == CleanPartState.Failed).Select(p => p.Title)));

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
        // Only Delete jobs clean anything. The Recycle Bin category is review-only now
        // (its emptying action was removed with the hang fix); filtering here too keeps a
        // stale selection from ever reaching the engine as a silent no-op.
        var jobs = Categories.Where(c => c.CanSelect && c.Model.Action == CleanAction.Delete)
            .Select(c => new CleanJob(c.Model, c.SelectedItems()))
            .Where(j => j.Items.Count > 0)
            .ToList();
        if (jobs.Count == 0) return;
        SpaceMap.CancelScan();

        // Read fresh so the choice also applies after "Restart as admin".
        var mode = IsDemo ? RemovalMode.ReviewToBin : new PreferencesStore(PreferencesStore.DefaultPath).Load().Removal;
        SetRemoval(mode);

        // Pre-flight: when planned recycle bytes approach or exceed the remaining bin
        // capacity, the user decides BEFORE the run starts. Files that cannot be recycled
        // are skipped with a recorded reason — never a shell dialog, never silent
        // permanent deletion. The budget snapshot is enforced by the engine per batch.
        RecycleBinBudget? budget = null;
        string preflightNote = "";
        if (!IsDemo)
        {
            var check = await CheckRecyclePreflightAsync(jobs);
            if (check is { } c)
            {
                budget = c.Budget;
                if (c.Preflight.ExceedsCapacity)
                {
                    var goOn = await ConfirmAsync(S.BinCapacityTitle,
                        Loc.Format("BinCapacityBody", Format.Bytes(c.Preflight.PlannedBytes),
                            Format.Bytes(c.Preflight.KnownRemainingBytes ?? 0)),
                        S.BinCapacityContinue, []);
                    if (!goOn) return;
                }
                else if (c.Preflight.ApproachingCapacity)
                {
                    preflightNote = Loc.Format("BinCapacityApproachingLine",
                        Format.Bytes(c.Preflight.KnownRemainingBytes ?? 0), Format.Bytes(c.Preflight.PlannedBytes));
                }
            }
        }

        // Confirm whenever "your call" files are involved, or when everything goes to the
        // Recycle Bin (the space is only freed once the bin is emptied). The dialog lists
        // every category with where it goes, so there are no surprises.
        var lines = jobs.Select(j => ConfirmLineFor(j, mode)).ToList();
        bool anyReview = jobs.Any(j => j.Category.Tier == SafetyLevel.CheckFirst);
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
            if (preflightNote.Length > 0) body += "\n\n" + preflightNote;
            if (!await ConfirmAsync(title, body, yes, lines)) return;
        }

        _busy = true;
        _cleanCts = new CancellationTokenSource();
        _cancellingClean = false;
        _cleanFinished = new TaskCompletionSource<bool>();
        var ct = _cleanCts.Token;
        try
        {
            var drive = SystemDrive;
            BeforeFraction = drive?.UsedFraction ?? 0;
            BeforeBrush = drive?.BarBrush;
            BeforeText = drive is null ? "" : Loc.Format("FreeBytes", Format.Bytes(drive.Free));

            // The parts list is built before cleaning starts, in the order the cleaner
            // will run them, so every part is visible as queued before the first starts.
            _cleanModel = new CleanProgressModel(jobs.Select(j =>
                (j.Category.Id, j.Category.Title, j.Category.Description, j.Category.Glyph,
                    j.Items.Sum(i => i.Size))));
            CleanParts.Clear();
            _cleanRows.Clear();
            foreach (var part in _cleanModel.Parts)
            {
                var row = new CleanPartVM(part);
                CleanParts.Add(row);
                _cleanRows[part.CategoryId] = row;
            }
            Raise(nameof(HasCleanParts));
            Raise(nameof(CleanPartsText));

            CleanProgress = 0;
            CleanStep = S.GettingStarted;
            CleanCurrentItem = "";
            CleanCurrentPath = "";
            CleanFilesSoFar = 0;
            CleanFreedSoFar = 0;
            CleanRecycledSoFar = 0;
            RaiseCleanStats();
            State = AppState.Cleaning;
            RaiseCancelState();

            // Progress<CleanProgress> captures the UI thread here, so every update below runs
            // on it: the worker thread never touches the view models directly.
            var progress = new Progress<CleanProgress>(p =>
            {
                if (!_cancellingClean) CleanStep = p.Step;
                CleanCurrentItem = p.CurrentItem;
                if (p.CurrentPath.Length > 0) CleanCurrentPath = p.CurrentPath;
                if (p.PartDone || p.PartFailed || p.Cancelled) CleanCurrentPath = "";
                CleanFilesSoFar = p.FilesSoFar;
                CleanFreedSoFar = p.FreedSoFar;
                CleanRecycledSoFar = p.RecycledSoFar;
                RaiseCleanStats();
                ApplyCleanPartReport(p);
            });

            var cleanOptions = new CleanOptions { Budget = budget };
            CleanResult r;
            bool cancelled = false;
            try
            {
                if (IsDemo)
                {
                    r = await DemoClean(jobs, mode, progress, ct);
                    _demoFreed += r.Freed;
                }
                else
                {
                    // The token goes to Cleaner.Run, not to Task.Run: the engine converts
                    // cancellation into a CleanCancelledException carrying exact partial
                    // totals, instead of a bare TaskCanceledException with nothing in it.
                    r = await Task.Run(() => Cleaner.Run(jobs, progress, mode, ct, cleanOptions));
                    await Task.Delay(500);
                }
            }
            catch (CleanCancelledException ex)
            {
                // Stopping keeps the exact partial result: the done screen shows what was
                // actually cleaned, and says plainly that the run stopped early.
                r = ex.Partial;
                cancelled = true;
            }

            FinishClean(r, drive, cancelled);
        }
        catch (Exception ex)
        {
            ShowToast(S.CleaningStopped + ex.Message);
            State = AppState.Results;
        }
        finally
        {
            _busy = false;
            _cleanCts?.Dispose();
            _cleanCts = null;
            _cleanFinished?.TrySetResult(true);
            RaiseCancelState();
        }
    }

    void FinishClean(CleanResult r, DriveVM? drive, bool cancelled)
    {
        if (cancelled && _cleanModel != null)
        {
            _cleanModel.CancelRemaining();
            RefreshCleanRows();
        }
        WasCleanCancelled = cancelled;
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
        Raise(nameof(HasFailedParts));
        Raise(nameof(FailedPartsText));
        State = AppState.Done;
        RaiseCancelState();
        RecordHistory(r);
    }

    /// <summary>
    /// Records a real clean in the cleanup history (never demo runs, never when
    /// the user turned history off). Recording must never break the done screen,
    /// so every failure is swallowed. Per-item detail stays empty: the engine
    /// does not supply it yet, and history must not invent paths. The reclaimed
    /// figure stays unknown until settled-gain measurement is wired in.
    /// </summary>
    void RecordHistory(CleanResult r)
    {
        if (IsDemo) return;
        try
        {
            if (!new PreferencesStore(PreferencesStore.DefaultPath).Load().RecordCleanupHistory) return;
            var entry = new CleanupHistoryEntry(
                Guid.NewGuid(), DateTimeOffset.Now, CleanupTrigger.Manual, "cleanup",
                r.Recycled, null, [], [], [],
                (r.Categories ?? []).Select(c =>
                    new HistoryCategorySummary(c.CategoryId, c.Title, c.Freed, c.Recycled, c.Files)).ToList(),
                null, CleanupVerificationState.Unknown, r.Recycled > 0);
            new CleanupHistoryStore().Record(entry);
        }
        catch { /* history is best-effort; the done screen already happened */ }
    }

    /// <summary>
    /// Pre-flight Recycle Bin capacity check: planned recycle bytes per drive vs the bin
    /// capacity remaining on each drive. Capacity reads run off the UI thread. Returns null
    /// when nothing in the selection goes to the bin. Unknown capacity is reported as
    /// unknown, never guessed.
    /// </summary>
    static async Task<(RecyclePreflight Preflight, RecycleBinBudget Budget)?> CheckRecyclePreflightAsync(
        List<CleanJob> jobs)
    {
        var planned = VolumeCapacity.Plan(jobs);
        if (planned.Count == 0) return null;
        var capacities = await Task.Run(() => VolumeCapacity.QueryAll(planned.Keys));
        var preflight = RecyclePreflight.Evaluate(planned, capacities);
        CleanLog.WriteLine($"pre-flight planned={preflight.PlannedBytes} knownRemaining={preflight.KnownRemainingBytes} " +
            $"exceeds={preflight.ExceedsCapacity} approaching={preflight.ApproachingCapacity} " +
            $"unknown=[{string.Join(",", preflight.UnknownDrives)}] disabled=[{string.Join(",", preflight.DisabledDrives)}]");
        return (preflight, new RecycleBinBudget(capacities));
    }

    static ConfirmLine ConfirmLineFor(CleanJob job, RemovalMode mode)
    {
        var dest = RemovalPolicy.GoesToBin(mode, job.Category) ? ConfirmDestination.Bin : ConfirmDestination.Deleted;
        return new ConfirmLine(job.Category.Title, Format.Bytes(job.Items.Sum(i => i.Size)), dest);
    }

    async Task<CleanResult> DemoClean(List<CleanJob> jobs, RemovalMode mode, IProgress<CleanProgress> progress, CancellationToken ct)
    {
        long total = Math.Max(1, jobs.Sum(j => j.Items.Sum(i => i.Size)));
        long done = 0, freed = 0, recycled = 0;
        int files = 0;
        var perCategory = new List<CategoryCleanResult>();
        CleanJob? currentJob = null;
        bool currentRecycle = false;
        long jobFreed = 0, jobRecycled = 0;
        int jobFiles = 0;
        try
        {
            for (int partIndex = 0; partIndex < jobs.Count; partIndex++)
            {
                var j = jobs[partIndex];
                ct.ThrowIfCancellationRequested();
                currentJob = j;
                jobFreed = 0;
                jobRecycled = 0;
                jobFiles = 0;
                var step = Loc.Format("CleaningCategory", j.Category.Title.ToLowerInvariant());
                progress.Report(new CleanProgress((double)done / total, step, "", freed, recycled, files,
                    CategoryId: j.Category.Id, PartIndex: partIndex, PartCount: jobs.Count));
                currentRecycle = RemovalPolicy.GoesToBin(mode, j.Category);
                long jobTotal = Math.Max(1, j.Items.Sum(i => i.Size));
                long jobDoneBytes = 0;
                foreach (var item in j.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!currentRecycle) { jobFreed += item.Size; freed += item.Size; }
                    else { jobRecycled += item.Size; recycled += item.Size; }
                    files += item.FileCount;
                    jobFiles += item.FileCount;
                    done += item.Size;
                    jobDoneBytes += item.Size;
                    progress.Report(new CleanProgress((double)done / total, step, item.Name, freed, recycled, files,
                        CategoryId: j.Category.Id, PartIndex: partIndex, PartCount: jobs.Count,
                        PartFraction: (double)jobDoneBytes / jobTotal, CurrentPath: item.Path,
                        PartFilesSoFar: jobFiles, PartFreedSoFar: jobFreed, PartRecycledSoFar: jobRecycled));
                    await Task.Delay(110, ct);
                }
                var dest = currentRecycle ? CleanDestination.Recycled : CleanDestination.Deleted;
                perCategory.Add(new CategoryCleanResult(j.Category.Id, j.Category.Title, jobFreed, jobRecycled, 0, jobFiles, dest));
                progress.Report(new CleanProgress((double)done / total, step, "", freed, recycled, files,
                    CategoryId: j.Category.Id, PartIndex: partIndex, PartCount: jobs.Count,
                    PartFraction: 1, PartFilesSoFar: jobFiles, PartFreedSoFar: jobFreed,
                    PartRecycledSoFar: jobRecycled, PartDone: true));
                currentJob = null;
                await Task.Delay(120, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Same contract as the real cleaner: stopping keeps the partial result.
            if (currentJob != null)
            {
                var dest = currentRecycle ? CleanDestination.Recycled : CleanDestination.Deleted;
                perCategory.Add(new CategoryCleanResult(currentJob.Category.Id, currentJob.Category.Title,
                    jobFreed, jobRecycled, 0, jobFiles, dest));
                progress.Report(new CleanProgress((double)done / total, "", "", freed, recycled, files,
                    CategoryId: currentJob.Category.Id, PartFilesSoFar: jobFiles,
                    PartFreedSoFar: jobFreed, PartRecycledSoFar: jobRecycled, Cancelled: true));
            }
            throw new CleanCancelledException(new CleanResult(freed, 0, files, recycled, perCategory), ct);
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

    /// <summary>One open confirmation at a time; a second request answers the
    /// first with "no" instead of orphaning its waiter (P1-6).</summary>
    readonly ConfirmGate _confirmGate = new();

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
        ConfirmVisible = true;
        return _confirmGate.Request();
    }

    void CloseConfirm(bool result)
    {
        ConfirmVisible = false;
        _confirmGate.Resolve(result);
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
public enum ConfirmDestination { Deleted, Bin }

public sealed record ConfirmLine(string Title, string SizeText, ConfirmDestination Destination)
{
    public string DestinationText => Destination switch
    {
        ConfirmDestination.Bin => S.CatRecycle,
        _ => S.DeletedForGood,
    };
    public Brush DestinationBg => Res.Brush(Destination switch
    {
        ConfirmDestination.Bin => "WarnSoftBrush",
        _ => "DangerSoftBrush",
    });
    public Brush DestinationFg => Res.Brush(Destination switch
    {
        ConfirmDestination.Bin => "WarnBrush",
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
        _ => S.DeletedForGood,
    };
    public Brush DestinationBg => Res.Brush(result.Destination switch
    {
        CleanDestination.Recycled => "WarnSoftBrush",
        _ => "DangerSoftBrush",
    });
    public Brush DestinationFg => Res.Brush(result.Destination switch
    {
        CleanDestination.Recycled => "WarnBrush",
        _ => "DangerBrush",
    });
}
