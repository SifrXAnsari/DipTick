using System.Globalization;
using DipTick.Models;
using Terminal.Gui.ViewBase;

namespace DipTick.Views;

/// <summary>What one F-key does on the terminal screen: its label, and what runs.</summary>
internal sealed record TermKey(string Label, Action Run);

/// <summary>Where one key sits on the bar: its first column, and the columns its label may use.</summary>
internal readonly record struct KeySlot(int Start, int Room);

/// <summary>
/// The terminal screen's F-key bar: F1 to F10 on the bottom row, where Midnight Commander keeps its
/// bar, each doing what the window's does. Each key keeps one place on every screen, sized for the
/// longest label it has on any of them, so a key's place is learned once. A key with nothing to do
/// on a screen shows its number, dim, with no label; a click on a key runs it as the key does. It
/// can say one line instead of its keys (Esc's warning).
/// </summary>
internal sealed class TermKeyBar : View
{
    public const int Count = 10;

    /// <summary>Columns between two keys when the terminal is wide enough; one when it is not.</summary>
    private const int Gap = 2;

    private readonly TermKey?[] _keys = new TermKey?[Count];
    private int[] _widest = new int[Count];
    private string? _warning;

    public TermKeyBar()
    {
        CanFocus = false;
        SetScheme(Theme.Base);
        MouseEvent += (_, mouse) =>
        {
            if (mouse.IsSingleClicked && _warning is null && mouse.Position is { } at && KeyAt(Slots(_widest, Viewport.Width), at.X) is { } number)
            {
                mouse.Handled = Press(number);
            }
        };
    }

    /// <summary>What the bar says in place of its keys, or null.</summary>
    public string? Warning => _warning;

    /// <summary>
    /// Every screen's keys, once: each key's place is sized for the longest label it has on any of
    /// them, so no key moves when the screen changes.
    /// </summary>
    public void Measure(IEnumerable<IReadOnlyList<TermKey?>> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        _widest = Widest(screens);
        SetNeedsDraw();
    }

    /// <summary>The screen's keys; a null is a key with nothing to do here.</summary>
    public void Show(IReadOnlyList<TermKey?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        for (var i = 0; i < Count; i++)
        {
            _keys[i] = i < keys.Count ? keys[i] : null;
        }

        SetNeedsDraw();
    }

    /// <summary>Runs F<paramref name="number"/>'s job on this screen; false when it has none here.</summary>
    public bool Press(int number)
    {
        if (number < 1 || number > Count || _keys[number - 1] is not { } key)
        {
            return false;
        }

        key.Run();
        return true;
    }

    /// <summary>A line in place of the keys, or null for the keys again.</summary>
    public void Warn(string? text)
    {
        _warning = text;
        SetNeedsDraw();
    }

    /// <summary>Each key's longest label over <paramref name="screens"/>, F1 first; 0 for a key no screen gives a job.</summary>
    internal static int[] Widest(IEnumerable<IReadOnlyList<TermKey?>> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var widest = new int[Count];
        foreach (var keys in screens)
        {
            for (var i = 0; i < Count && i < keys.Count; i++)
            {
                widest[i] = Math.Max(widest[i], keys[i]?.Label.Length ?? 0);
            }
        }

        return widest;
    }

    /// <summary>
    /// Where each key sits in <paramref name="width"/> columns. Every label gets its full length
    /// when the row holds them with two columns between keys, then with one; a narrower terminal
    /// takes a column from the longest label, then the next, until the row fits.
    /// </summary>
    internal static KeySlot[] Slots(IReadOnlyList<int> widest, int width)
    {
        ArgumentNullException.ThrowIfNull(widest);
        var rooms = new int[Count];
        for (var i = 0; i < Count; i++)
        {
            rooms[i] = i < widest.Count ? Math.Max(0, widest[i]) : 0;
        }

        int Used(int gap)
        {
            var used = gap * (Count - 1);
            for (var i = 0; i < Count; i++)
            {
                used += Number(i + 1).Length + (rooms[i] > 0 ? 1 + rooms[i] : 0);
            }

            return used;
        }

        var gap = Used(Gap) <= width ? Gap : 1;

        // A label keeps at least one letter and its ellipsis while any is longer.
        while (Used(gap) > width && rooms.Max() > 2)
        {
            rooms[Array.IndexOf(rooms, rooms.Max())]--;
        }

        var slots = new KeySlot[Count];
        var at = 0;
        for (var i = 0; i < Count; i++)
        {
            slots[i] = new KeySlot(at, rooms[i]);
            at += Number(i + 1).Length + (rooms[i] > 0 ? 1 + rooms[i] : 0) + gap;
        }

        return slots;
    }

    /// <summary>The key whose place holds column <paramref name="x"/>: each reaches to the next one's start, the last to the end.</summary>
    internal static int? KeyAt(IReadOnlyList<KeySlot> slots, int x)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (x < 0 || slots.Count == 0)
        {
            return null;
        }

        for (var i = slots.Count - 1; i >= 0; i--)
        {
            if (x >= slots[i].Start)
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>The bar as spans: each key's number, then its label cut to its room with an ellipsis, then the gap to the next.</summary>
    internal static IReadOnlyList<Span> Spans(IReadOnlyList<TermKey?> keys, IReadOnlyList<int> widest, int width)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var slots = Slots(widest, width);
        var spans = new List<Span>();
        var used = 0;
        for (var i = 0; i < Count; i++)
        {
            if (slots[i].Start > used)
            {
                spans.Add(new Span(new string(' ', slots[i].Start - used), Theme.Text));
                used = slots[i].Start;
            }

            var label = i < keys.Count && keys[i] is { } key ? key.Label : string.Empty;
            var number = Number(i + 1);
            spans.Add(new Span(number, label.Length == 0 ? Theme.Dim : Theme.Accent));
            used += number.Length;
            if (slots[i].Room > 0)
            {
                var room = slots[i].Room;
                var shown = label.Length <= room ? label : label[..(room - 1)].TrimEnd() + "…";
                spans.Add(new Span(" " + shown.PadRight(room), Theme.Text));
                used += 1 + room;
            }
        }

        return spans;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        Move(0, 0);
        var spans = _warning is { } warning ? [new Span(" " + warning, Theme.Warning)] : Spans(_keys, _widest, width);
        var used = 0;
        foreach (var span in spans)
        {
            if (used >= width)
            {
                break;
            }

            var text = SafeText.Line(span.Text);
            if (text.Length > width - used)
            {
                text = text[..Math.Max(0, width - used)];
            }

            SetAttribute(span.Look);
            AddStr(text);
            used += text.Length;
        }

        if (used < width)
        {
            SetAttribute(Theme.Text);
            AddStr(new string(' ', width - used));
        }

        return true;
    }

    private static string Number(int number) => "F" + number.ToString(CultureInfo.InvariantCulture);
}
