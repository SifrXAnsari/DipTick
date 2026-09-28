using DipTick.Models;
using Terminal.Gui.ViewBase;

namespace DipTick.Views;

/// <summary>
/// Rows of coloured text, drawn exactly as given: every visible part of the screen is one of these
/// (the header, the transcript, the question panel's lines and choices, the command list, the
/// settings screen). It never takes the keyboard; whoever owns it moves <see cref="Top"/>.
/// </summary>
/// <remarks>
/// Drawn by hand rather than with a list control so no colour is derived by the library: the stock
/// controls paint selections and dialogs in their own palette, which gives a 1990s look. Every row
/// is padded to the full width in the background colour.
/// </remarks>
internal sealed class RowsView : View
{
    private IReadOnlyList<IReadOnlyList<Span>> _rows = [];

    public RowsView()
    {
        CanFocus = false;
        SetScheme(Theme.Base);
    }

    /// <summary>The rows. Setting them redraws; with <see cref="FollowTail"/>, the view keeps to the last row.</summary>
    public IReadOnlyList<IReadOnlyList<Span>> Rows
    {
        get => _rows;
        set
        {
            var atEnd = Top >= Math.Max(0, _rows.Count - Viewport.Height);
            _rows = value;
            if (FollowTail && atEnd)
            {
                Top = Math.Max(0, _rows.Count - Math.Max(1, Viewport.Height));
            }

            Top = Math.Clamp(Top, 0, Math.Max(0, _rows.Count - 1));
            SetNeedsDraw();
        }
    }

    /// <summary>The first row shown.</summary>
    public int Top { get; set; }

    /// <summary>Keep to the last row as rows are added, unless the person has scrolled up.</summary>
    public bool FollowTail { get; set; }

    public void ScrollBy(int rows)
    {
        Top = Math.Clamp(Top + rows, 0, Math.Max(0, _rows.Count - Math.Max(1, Viewport.Height)));
        SetNeedsDraw();
    }

    /// <summary>Scrolls so <paramref name="index"/> is on screen.</summary>
    public void Show(int index)
    {
        var height = Math.Max(1, Viewport.Height);
        if (index < Top)
        {
            Top = index;
        }
        else if (index >= Top + height)
        {
            Top = index - height + 1;
        }

        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        for (var y = 0; y < Viewport.Height; y++)
        {
            Move(0, y);
            var used = 0;
            var at = Top + y;
            if (at < _rows.Count)
            {
                foreach (var span in _rows[at])
                {
                    if (used >= width)
                    {
                        break;
                    }

                    // Whatever a span holds is shown, never obeyed by the terminal.
                    var text = SafeText.Line(span.Text);
                    if (text.Length > width - used)
                    {
                        text = text[..Math.Max(0, width - used)];
                    }

                    SetAttribute(span.Look);
                    AddStr(text);
                    used += text.Length;
                }
            }

            if (used < width)
            {
                SetAttribute(Theme.Text);
                AddStr(new string(' ', width - used));
            }
        }

        return true;
    }
}
