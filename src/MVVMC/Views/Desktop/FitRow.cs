using Avalonia;
using Avalonia.Controls;

namespace DipTick.Views.Desktop;

/// <summary>
/// A row against its right end that shows as many of its children as fit whole, in their order. A
/// child that does not fit is not drawn, never cut in half and never drawn over what stands to the
/// left of the row: the F-key bar's keys and the chat header's words keep their places however much
/// else there is. What gives way first is the first child, the least
/// pressing, or with <see cref="DropFromEnd"/> the last.
/// </summary>
internal sealed class FitRow : Panel
{
    private readonly List<bool> _fits = [];
    private double _spacing = 12;
    private bool _dropFromEnd;
    private bool _alignLeft;

    public FitRow() => ClipToBounds = true;

    public double Spacing
    {
        get => _spacing;
        set
        {
            _spacing = value;
            InvalidateMeasure();
        }
    }

    /// <summary>True when the last children give way first; else the first ones do.</summary>
    public bool DropFromEnd
    {
        get => _dropFromEnd;
        set
        {
            _dropFromEnd = value;
            InvalidateMeasure();
        }
    }

    /// <summary>True for a row against its left end; else against its right end.</summary>
    public bool AlignLeft
    {
        get => _alignLeft;
        set
        {
            _alignLeft = value;
            InvalidateArrange();
        }
    }

    /// <summary>How many children fit and are drawn.</summary>
    public int Shown => _fits.Count(f => f);

    /// <summary>Whether the child at <paramref name="index"/> fits and is drawn.</summary>
    public bool IsShown(int index) => index >= 0 && index < _fits.Count && _fits[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        _fits.Clear();
        _fits.AddRange(Enumerable.Repeat(false, Children.Count));
        var width = 0.0;
        var height = 0.0;
        for (var n = 0; n < Children.Count; n++)
        {
            // The ones kept longest are counted first.
            var i = DropFromEnd ? n : Children.Count - 1 - n;
            var child = Children[i];
            child.Measure(Size.Infinity);
            var needed = child.DesiredSize.Width + (width > 0 ? Spacing : 0);
            if (width + needed > availableSize.Width)
            {
                // Measured still, so their sizes are known, but none past the first that does not fit is drawn.
                for (var rest = n + 1; rest < Children.Count; rest++)
                {
                    Children[DropFromEnd ? rest : Children.Count - 1 - rest].Measure(Size.Infinity);
                }

                break;
            }

            _fits[i] = true;
            width += needed;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Those that fit, in their order, against the row's end; the rest outside the row, which clips them.
        var shownWidth = Children.Where((_, i) => IsShown(i)).Sum(c => c.DesiredSize.Width) + (Math.Max(0, Shown - 1) * Spacing);
        var x = AlignLeft ? 0 : finalSize.Width - shownWidth;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            var size = child.DesiredSize;
            if (!IsShown(i))
            {
                child.Arrange(new Rect(-size.Width - finalSize.Width, 0, size.Width, size.Height));
                continue;
            }

            child.Arrange(new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width + Spacing;
        }

        return finalSize;
    }
}
