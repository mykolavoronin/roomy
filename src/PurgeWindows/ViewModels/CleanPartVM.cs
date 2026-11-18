using System.Windows;
using System.Windows.Media;
using PurgeWindows.Core;
using PurgeWindows.Localization;

namespace PurgeWindows.ViewModels;

/// <summary>
/// One row of the cleaning progress list: a part (one category) being cleaned, mirroring
/// the category rows on the results screen — glyph, title, a plain-language line, and
/// what happened. The row is a thin observable wrapper over the pure
/// <see cref="CleanPartProgress"/> state; <see cref="Refresh"/> is called on the UI thread
/// whenever a progress report touches this part.
/// </summary>
public sealed class CleanPartVM : ObservableObject
{
    // Segoe Fluent Icons "Cancel" (E8BB): no Glyph constant exists for it, and the shared
    // glyph table belongs to the catalog work — keep the literal local to this row.
    const string CancelGlyph = "\uE8BB";

    readonly CleanPartProgress _part;

    public CleanPartVM(CleanPartProgress part) => _part = part;

    public CleanPartProgress Model => _part;
    public string Title => _part.Title;
    public string Glyph => _part.Glyph;
    public CleanPartState State => _part.State;

    public string StateGlyph => State switch
    {
        CleanPartState.Done => G.Check,
        CleanPartState.Cleaning => G.Update,
        CleanPartState.Failed or CleanPartState.Cancelled => CancelGlyph,
        _ => G.ChevronRight,
    };

    public Brush GlyphBrush => Res.Brush(State switch
    {
        CleanPartState.Done => "AccentBrush",
        CleanPartState.Cleaning => "Accent2Brush",
        CleanPartState.Failed => "DangerBrush",
        CleanPartState.Cancelled => "MutedBrush",
        _ => "FaintBrush",
    });

    public Brush TitleBrush => Res.Brush(State == CleanPartState.Queued ? "FaintBrush" : "TextBrush");

    /// <summary>
    /// The plain-language line under the title. While the part waits it explains what the
    /// part is; while it cleans it names the folder being processed; once finished it
    /// names the outcome. The explanation is visible in the row, never hover-only.
    /// </summary>
    public string DetailText => State switch
    {
        CleanPartState.Queued => _part.Description.Length > 0 ? _part.Description : S.PartStatusQueued,
        CleanPartState.Cleaning => _part.CurrentPath.Length > 0 ? _part.CurrentPath : S.PartStatusCleaning,
        CleanPartState.Done => S.PartStatusDone,
        CleanPartState.Failed => S.PartStatusFailed,
        _ => S.PartStatusCancelled,
    };

    public Brush DetailBrush => Res.Brush(State == CleanPartState.Cleaning && _part.CurrentPath.Length > 0
        ? "FaintBrush" : "MutedBrush");

    /// <summary>
    /// The result on the right. Bytes moved to the Recycle Bin are never called "freed":
    /// an all-recycled part says where its bytes went, and a mixed part splits the two.
    /// While a part waits (or has just started) its expected size is shown instead.
    /// </summary>
    public string ResultText
    {
        get
        {
            if (State == CleanPartState.Queued) return Format.Bytes(_part.ExpectedBytes);
            if (_part.BytesDone == 0 && _part.FilesRemoved == 0)
            {
                if (State == CleanPartState.Cleaning) return Format.Bytes(_part.ExpectedBytes);
                if (_part.BytesSkipped > 0) return Loc.Format("PartSkippedLine", Format.Bytes(_part.BytesSkipped));
                return S.Nothing;
            }
            if (_part.BytesFreed == 0 && _part.BytesRecycled > 0)
                return Loc.Format("PartMovedLine",
                    _part.FilesRemoved.ToString("N0"), Loc.Files(_part.FilesRemoved), Format.Bytes(_part.BytesRecycled));
            return Loc.Format("CleanStats",
                _part.FilesRemoved.ToString("N0"), Loc.Files(_part.FilesRemoved), Format.Bytes(_part.BytesFreed));
        }
    }

    /// <summary>Shown only for mixed parts: the ResultText already covers all-recycled parts.</summary>
    public bool HasRecycled => _part.BytesRecycled > 0 && _part.BytesFreed > 0;
    public string RecycledText => Loc.Format("PartRecycledLine", Format.Bytes(_part.BytesRecycled));

    /// <summary>Bytes that could not be removed, made visible instead of silent.</summary>
    public bool HasSkipped => _part.BytesSkipped > 0 && _part.BytesDone > 0 &&
                              State is CleanPartState.Done or CleanPartState.Failed;
    public string SkippedText => Loc.Format("PartSkippedLine", Format.Bytes(_part.BytesSkipped));

    /// <summary>The part's own progress bar, as star-sized columns like the space map rows.</summary>
    public GridLength BarWidth => new(Math.Clamp(_part.Fraction, 0, 1), GridUnitType.Star);
    public GridLength RestWidth => new(1 - Math.Clamp(_part.Fraction, 0, 1), GridUnitType.Star);

    public Brush BarBrush => Res.Brush(State switch
    {
        CleanPartState.Failed => "DangerBrush",
        CleanPartState.Cancelled => "MutedBrush",
        _ => "AccentBrush",
    });

    public void Refresh()
    {
        Raise(nameof(State));
        Raise(nameof(StateGlyph));
        Raise(nameof(GlyphBrush));
        Raise(nameof(TitleBrush));
        Raise(nameof(DetailText));
        Raise(nameof(DetailBrush));
        Raise(nameof(ResultText));
        Raise(nameof(HasRecycled));
        Raise(nameof(RecycledText));
        Raise(nameof(HasSkipped));
        Raise(nameof(SkippedText));
        Raise(nameof(BarWidth));
        Raise(nameof(RestWidth));
        Raise(nameof(BarBrush));
    }
}
