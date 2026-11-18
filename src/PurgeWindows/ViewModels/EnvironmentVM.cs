using PurgeWindows.Core;
using PurgeWindows.Core.Environment;
using PurgeWindows.Localization;

namespace PurgeWindows.ViewModels;

/// <summary>
/// The "Your setup" disclosure data: setup summary lines, the per-probe evidence list
/// (Unknowns included, with reasons), the gating decisions with reasons, and the Re-check
/// command. The visual panel is the shell wave's job; this view-model only provides data.
/// Copy is factual and neutral — mod and antivirus states are never presented as problems.
/// The text builders live in <see cref="EnvironmentDisclosure"/> (WPF-free, unit-tested).
/// All members must be touched from the UI thread: detection runs on worker threads, but
/// <see cref="StartDetectionAsync"/> and <see cref="ReCheckAsync"/> marshal results back.
/// </summary>
public sealed class EnvironmentVM : ObservableObject
{
    readonly IEnvironmentDetector _detector;
    readonly PreferencesStore? _store;
    readonly CategoryCatalog _catalog;
    bool _hasSnapshot;

    public EnvironmentVM(IEnvironmentDetector detector, PreferencesStore? store, CategoryCatalog catalog)
    {
        _detector = detector;
        _store = store;
        _catalog = catalog;
        _snapshot = EnvironmentSnapshot.Unknown(DateTimeOffset.UtcNow);
        _currentGating = GatingResult.Empty(catalog.Entries);
        _modOverride = store?.Load().ModOverride ?? ModVerdictOverride.None;
        ReCheckCommand = new RelayCommand(() => _ = ReCheckAsync(), () => !IsDetecting);
    }

    /// <summary>Raised after a detection completes or the override changes — anything gating
    /// depends on changed, so scan results must be re-gated.</summary>
    public event EventHandler? SnapshotChanged;

    public RelayCommand ReCheckCommand { get; }

    EnvironmentSnapshot _snapshot;
    public EnvironmentSnapshot Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }

    /// <summary>True once a detection run has completed (demo mode completes immediately with Unknowns).</summary>
    public bool HasSnapshot { get => _hasSnapshot; private set => Set(ref _hasSnapshot, value); }

    bool _detecting;
    public bool IsDetecting
    {
        get => _detecting;
        private set
        {
            if (Set(ref _detecting, value)) ReCheckCommand.RaiseCanExecuteChanged();
        }
    }

    GatingResult _currentGating;
    /// <summary>The gating for the current snapshot + override. Empty (everything offered) until the first detection.</summary>
    public GatingResult CurrentGating { get => _currentGating; private set => Set(ref _currentGating, value); }

    ModVerdictOverride _modOverride;
    /// <summary>
    /// The user's mod-verdict override (D-13): explicit, reversible, persisted like other
    /// preferences (in-memory only when no store was given, i.e. demo mode). Changing it
    /// re-gates immediately and records the override in the gating output.
    /// </summary>
    public ModVerdictOverride ModOverride
    {
        get => _modOverride;
        set
        {
            if (_modOverride == value) return;
            _modOverride = value;
            PersistOverride(value);
            RefreshFromSnapshot();
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            Raise(nameof(ModOverride));
        }
    }

    /// <summary>The override choices with display labels, for the shell wave to bind.</summary>
    public IReadOnlyList<(ModVerdictOverride Value, string Label)> ModOverrideOptions { get; } =
    [
        (ModVerdictOverride.None, Loc.Get("EnvOverrideNone")),
        (ModVerdictOverride.TreatAsStock, Loc.Get("EnvOverrideStock")),
        (ModVerdictOverride.TreatAsModified, Loc.Get("EnvOverrideModified")),
    ];

    void PersistOverride(ModVerdictOverride value)
    {
        if (_store is null) return;
        try
        {
            var preferences = _store.Load();
            _store.Save(preferences with { ModOverride = value });
        }
        catch { /* A failed save keeps the in-memory override; detection never breaks on settings. */ }
    }

    IReadOnlyList<string> _summaryLines = [];
    /// <summary>Setup summary: OS, mod status, antivirus status, privilege — in that order.</summary>
    public IReadOnlyList<string> SummaryLines { get => _summaryLines; private set => Set(ref _summaryLines, value); }

    /// <summary>The mod verdict line on its own, for prominent display. Brand-gated: the ReviOS
    /// wording appears only when the verdict is ModifiedLikelyReviOS.</summary>
    public string ModStatusText => EnvironmentDisclosure.ModVerdictText(Snapshot.ModScore, CurrentGating.EffectiveVerdict);

    IReadOnlyList<EvidenceRow> _evidence = [];
    public IReadOnlyList<EvidenceRow> Evidence { get => _evidence; private set => Set(ref _evidence, value); }

    IReadOnlyList<string> _observedMarkers = [];
    /// <summary>The observed mod markers, as display strings. Empty when none were observed.</summary>
    public IReadOnlyList<string> ObservedMarkers { get => _observedMarkers; private set => Set(ref _observedMarkers, value); }

    public string MarkersTitle => Loc.Get("EnvMarkerEvidenceTitle");

    IReadOnlyList<GatingDecisionRow> _gatingDecisions = [];
    /// <summary>Only decisions that changed something: hidden, disabled, downgraded, advised, or unselected.</summary>
    public IReadOnlyList<GatingDecisionRow> GatingDecisions { get => _gatingDecisions; private set => Set(ref _gatingDecisions, value); }

    public bool ModRespectApplies => CurrentGating.ModRespectApplies;
    public bool LowDiskApplies => CurrentGating.LowDiskApplies;
    public bool OverrideActive => CurrentGating.OverrideActive;

    /// <summary>A neutral out-of-support line on Windows 10 (E-24), or null. Never a nag.</summary>
    public string? Win10Notice => Snapshot.Os.KnownValue?.IsWindows10 == true ? Loc.Get("EnvWin10Notice") : null;

    public string YourSetupTitle => Loc.Get("EnvYourSetupTitle");
    public string RecheckText => Loc.Get("EnvRecheck");
    public string DetectingText => Loc.Get("EnvDetecting");

    /// <summary>Runs the first detection (cached results reused when fresh). Never throws.</summary>
    public async Task StartDetectionAsync() => await DetectAsync(forceRefresh: false).ConfigureAwait(true);

    /// <summary>Re-runs every probe fresh, ignoring caches. Never throws.</summary>
    public async Task ReCheckAsync() => await DetectAsync(forceRefresh: true).ConfigureAwait(true);

    async Task DetectAsync(bool forceRefresh)
    {
        if (IsDetecting) return;
        IsDetecting = true;
        try
        {
            Snapshot = await _detector.DetectAsync(forceRefresh).ConfigureAwait(true);
            HasSnapshot = true;
            RefreshFromSnapshot();
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Detection must never break the app: keep the previous snapshot and stay usable.
        }
        finally
        {
            IsDetecting = false;
        }
    }

    void RefreshFromSnapshot()
    {
        CurrentGating = CategoryGate.Gate(_catalog, Snapshot, ModOverride);
        SummaryLines = EnvironmentDisclosure.BuildSummaryLines(Snapshot, CurrentGating);
        Evidence = EnvironmentDisclosure.BuildEvidence(Snapshot);
        ObservedMarkers = Snapshot.ModScore.ObservedMarkers.Select(m => Loc.Get(m.DisplayKey)).ToList();
        GatingDecisions = EnvironmentDisclosure.BuildGatingDecisions(CurrentGating);
        Raise(nameof(ModStatusText));
        Raise(nameof(ModRespectApplies));
        Raise(nameof(LowDiskApplies));
        Raise(nameof(OverrideActive));
        Raise(nameof(Win10Notice));
    }
}
