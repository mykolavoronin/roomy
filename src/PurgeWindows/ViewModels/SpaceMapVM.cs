using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PurgeWindows.Core;
using PurgeWindows.Localization;

namespace PurgeWindows.ViewModels;

public sealed class SpaceMapVM : ObservableObject
{
    readonly MainVM _main;
    SpaceNode? _root, _current;
    DriveVM? _drive;
    long _unseen;
    CancellationTokenSource? _scanCts;

    public SpaceMapVM(MainVM main)
    {
        _main = main;
        ScanCommand = new RelayCommand(() => _ = ScanAsync());
        SelectDriveCommand = new RelayCommand<DriveVM>(d =>
        {
            if (d is null || IsScanning) return;
            Select(d);
            _ = ScanAsync();
        });
        UpCommand = new RelayCommand(() => { if (_current?.Parent is { } p) Navigate(p); });
    }

    public ObservableCollection<DriveVM> Drives => _main.Drives;
    public ObservableCollection<SpaceRowVM> Rows { get; } = [];
    public ObservableCollection<CrumbVM> Crumbs { get; } = [];
    public ObservableCollection<BigFileVM> BigFiles { get; } = [];

    bool _scanning;
    public bool IsScanning
    {
        get => _scanning;
        private set { if (Set(ref _scanning, value)) Raise(nameof(ShowEmpty)); }
    }

    bool _hasData;
    public bool HasData
    {
        get => _hasData;
        private set
        {
            if (!Set(ref _hasData, value)) return;
            Raise(nameof(ShowEmpty));
            Raise(nameof(ScanButtonText));
        }
    }

    public bool ShowEmpty => !IsScanning && !HasData;
    public string ScanButtonText => HasData ? S.Rescan : S.Scan;

    string _progress = "";
    public string ProgressText { get => _progress; private set => Set(ref _progress, value); }

    string _stall = "";
    /// <summary>Factual-only stall line (no Stop exists on this surface).</summary>
    public string StallText { get => _stall; private set => Set(ref _stall, value); }

    Brush _stallBrush = Res.Brush("PurgeTextSecondaryBrush");
    public Brush StallBrush { get => _stallBrush; private set => Set(ref _stallBrush, value); }

    /// <summary>Live-region announcement, updated only when the stall LEVEL changes.</summary>
    string _stallAnnouncement = "";
    public string StallAnnouncement { get => _stallAnnouncement; private set => Set(ref _stallAnnouncement, value); }

    /// <summary>
    /// Measuring progress runs on polled snapshots: the scanner exposes no
    /// current folder and no heartbeat (A7), so counter movement is both the
    /// activity and the progress evidence — a frozen counter reads as silence,
    /// honestly, and Slow never fires here. The scanner wave replaces the
    /// polled snapshots with real events carrying CurrentPath.
    /// </summary>
    readonly StallDetector _stallDetector = new();
    StallLevel _stallLevel;
    readonly Stopwatch _measureWatch = new();
    long _measureSequence;
    long _lastFiles, _lastBytes;

    string _currentSize = "";
    public string CurrentSizeText { get => _currentSize; private set => Set(ref _currentSize, value); }

    public bool CanGoUp => _current?.Parent != null;

    public ICommand ScanCommand { get; }
    public ICommand SelectDriveCommand { get; }
    public ICommand UpCommand { get; }

    void Select(DriveVM d)
    {
        foreach (var x in Drives) x.IsSpaceSelected = x == d;
        _drive = d;
    }

    public void EnsureScanned()
    {
        if (!HasData && !IsScanning) _ = ScanAsync();
    }

    /// <summary>Stops a running space scan (a new scan, a cleanup, or leaving the tab).
    /// The scan's token is wired through to the scanner, so the walk stops promptly
    /// instead of running to completion in the background.</summary>
    public void CancelScan()
    {
        try { _scanCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the scan already finished; nothing to stop */ }
    }

    async Task ScanAsync()
    {
        if (IsScanning) return;
        var drive = _drive ?? _main.SystemDrive;
        if (drive is null) return;
        Select(drive);
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        IsScanning = true;
        HasData = false;
        ProgressText = S.Starting;
        StallText = "";
        StallAnnouncement = "";
        _stallLevel = StallLevel.Normal;
        _lastFiles = 0;
        _lastBytes = 0;
        _measureSequence = 0;
        _stallDetector.Start(TimeSpan.Zero);
        _measureWatch.Restart();
        try
        {
            IReadOnlyList<BigFile> big;
            SpaceNode root;
            if (_main.IsDemo)
            {
                for (int i = 1; i <= 12; i++)
                {
                    OnMeasureEvent(MeasureSnapshot(i * 41_337, (long)(i * 18.9 * (1L << 30))));
                    await Task.Delay(110, ct);
                }
                root = DemoData.SpaceTree();
                big = DemoData.BigFiles();
            }
            else
            {
                var scanner = new SpaceScanner();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                timer.Tick += (_, _) => OnMeasureEvent(MeasureSnapshot(scanner.Files, scanner.Bytes));
                timer.Start();
                try { root = await Task.Run(() => scanner.Scan(drive.Root, ct)); }
                finally { timer.Stop(); }
                big = scanner.BigFiles;
            }

            root.Name = drive.Title;
            _root = root;
            _unseen = Math.Max(0, drive.Used - root.Size);
            BigFiles.Clear();
            foreach (var b in big) BigFiles.Add(new BigFileVM(b));
            Navigate(root);
            HasData = true;
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose: no toast, no stale partial data. HasData stays false.
        }
        catch (Exception ex)
        {
            _main.ShowToast(S.CantMapDrive + ex.Message);
        }
        finally
        {
            IsScanning = false;
            _measureWatch.Stop();
            _stallDetector.Reset();
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    /// <summary>
    /// Builds one measuring snapshot from polled counters. CurrentPath stays
    /// empty: the scanner does not report the folder being walked yet (A7),
    /// and an unknown folder is left unknown, never guessed.
    /// </summary>
    MeasureProgressEvent MeasureSnapshot(long files, long bytes) => new(
        ++_measureSequence, DateTimeOffset.Now, _measureWatch.Elapsed,
        ProgressOperation.SpaceMeasure, "Measuring", Heartbeat: false,
        FilesCounted: files, BytesCounted: bytes, CurrentPath: "");

    /// <summary>
    /// Applies one measuring snapshot: the progress line (counts + elapsed, no
    /// ETA — totals are unknown until the walk ends) and the stall line.
    /// Counter movement is the only progress evidence available.
    /// </summary>
    void OnMeasureEvent(MeasureProgressEvent e)
    {
        ProgressText = Loc.Format("SpaceProgressLine",
            e.FilesCounted.ToString("N0"), Format.Bytes(e.BytesCounted),
            TimeTagline.Format(e.Elapsed));
        if (e.FilesCounted != _lastFiles || e.BytesCounted != _lastBytes)
        {
            _lastFiles = e.FilesCounted;
            _lastBytes = e.BytesCounted;
            _stallDetector.ObserveProgress(e.Elapsed);
        }
        var level = _stallDetector.Evaluate(e.Elapsed);
        StallText = level switch
        {
            StallLevel.Slow => S.StallSlowMeasure,
            StallLevel.Silent => Loc.Format("StallSilent",
                (int)_stallDetector.ProgressStaleFor(e.Elapsed).TotalSeconds),
            StallLevel.Stuck => S.StallStuckMeasure,
            StallLevel.Dead => Loc.Format("StallDead",
                TimeTagline.Format(_stallDetector.ActivityStaleFor(e.Elapsed))),
            _ => "",
        };
        StallBrush = Res.Brush(level switch
        {
            StallLevel.Stuck => "PurgeStatusCheckTextBrush",
            StallLevel.Dead => "PurgeStatusDangerTextBrush",
            _ => "PurgeTextSecondaryBrush",
        });
        if (level != _stallLevel)
        {
            _stallLevel = level;
            if (level != StallLevel.Normal) StallAnnouncement = StallText;
        }
    }

    void Navigate(SpaceNode node)
    {
        _current = node;
        bool atRoot = node.Parent is null;
        long denominator = atRoot ? node.Size + _unseen : node.Size;

        var rows = new List<SpaceRowVM>();
        foreach (var child in node.Children.Where(c => c.Size > 0).Take(80))
        {
            var target = child;
            rows.Add(SpaceRowVM.Folder(target, denominator, () => Navigate(target)));
        }
        if (node.FileBytes > 0)
        {
            var label = atRoot ? S.SystemFiles : Loc.Format("FilesInFolder", node.FileCount.ToString("N0"));
            rows.Add(SpaceRowVM.Files(label, node.FileBytes, denominator, node.FullPath));
        }
        if (atRoot && _unseen > 1L << 30)
            rows.Add(SpaceRowVM.Locked(S.LockedSystemFiles, _unseen, denominator));

        rows.Sort((a, b) => b.Size.CompareTo(a.Size));
        long max = rows.Count > 0 ? rows[0].Size : 1;
        Rows.Clear();
        foreach (var r in rows)
        {
            r.SetScale(max);
            Rows.Add(r);
        }

        var chain = new List<SpaceNode>();
        for (var n = node; n != null; n = n.Parent) chain.Insert(0, n);
        Crumbs.Clear();
        for (int i = 0; i < chain.Count; i++)
        {
            var target = chain[i];
            Crumbs.Add(new CrumbVM(target.Name, i == 0, new RelayCommand(() => Navigate(target))));
        }

        CurrentSizeText = Format.Bytes(node.Size);
        Raise(nameof(CanGoUp));
    }
}

public sealed class SpaceRowVM : ObservableObject
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public long ParentSize { get; init; }
    public string Glyph { get; init; } = G.Folder;
    public Brush GlyphBrush { get; init; } = Res.Brush("MutedBrush");
    public Brush BarBrush { get; init; } = Res.Brush("AccentGradient");
    public ICommand? OpenCommand { get; init; }
    public ICommand? RevealCommand { get; init; }
    public bool CanReveal => RevealCommand != null;
    public bool CanOpen => OpenCommand != null;
    public string SizeText => Format.Bytes(Size);
    public string PercentText => ParentSize > 0 ? $"{Size * 100.0 / ParentSize:0}%" : "";

    double _fraction;
    public GridLength BarWidth => new(Math.Max(_fraction, 0.006), GridUnitType.Star);
    public GridLength RestWidth => new(Math.Max(1 - _fraction, 0.0001), GridUnitType.Star);
    public void SetScale(long max) => _fraction = max > 0 ? (double)Size / max : 0;

    public static SpaceRowVM Folder(SpaceNode n, long parentSize, Action open) => new()
    {
        Name = n.Name, Size = n.Size, ParentSize = parentSize,
        Glyph = G.Folder, GlyphBrush = Res.Brush("Accent2Brush"),
        OpenCommand = n.Children.Count > 0 ? new RelayCommand(open) : null,
        RevealCommand = new RelayCommand(() => Shell.Reveal(n.FullPath)),
    };

    public static SpaceRowVM Files(string label, long size, long parentSize, string folder) => new()
    {
        Name = label, Size = size, ParentSize = parentSize,
        Glyph = G.Document, BarBrush = Res.Brush("MutedBrush"),
        RevealCommand = new RelayCommand(() => Shell.Reveal(folder)),
    };

    public static SpaceRowVM Locked(string label, long size, long parentSize) => new()
    {
        Name = label, Size = size, ParentSize = parentSize,
        Glyph = G.Shield, GlyphBrush = Res.Brush("FaintBrush"), BarBrush = Res.Brush("FaintBrush"),
    };
}

public sealed record CrumbVM(string Name, bool IsFirst, ICommand Command)
{
    public bool ShowSeparator => !IsFirst;
}

public sealed class BigFileVM(BigFile file)
{
    public string Name { get; } = Path.GetFileName(file.FullPath);
    public string Folder { get; } = Path.GetDirectoryName(file.FullPath) ?? "";
    public string SizeText { get; } = Format.Bytes(file.Size);
    public ICommand RevealCommand { get; } = new RelayCommand(() => Shell.Reveal(file.FullPath));
}
