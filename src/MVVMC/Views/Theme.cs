using DipTick.ViewModels;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace DipTick.Views;

/// <summary>One piece of a drawn line: its text and how it looks.</summary>
internal sealed record Span(string Text, Attribute Look);

/// <summary>
/// DipTick's look: a near-black background, one accent (green), cream text, dim hints, a warning
/// colour for approvals and an error colour. Nothing else: no gray panels, no shadows, no bracketed
/// buttons. True colour; Terminal.Gui falls back to the nearest of 16 where a console cannot show it.
/// </summary>
internal static class Theme
{
    public static readonly Color Background = new(0x10, 0x10, 0x14);
    public static readonly Color TextColor = new(0xE8, 0xE6, 0xE3);
    public static readonly Color DimColor = new(0x8A, 0x8D, 0x96);
    public static readonly Color AccentColor = new(0x3D, 0xDC, 0x97);
    public static readonly Color WarningColor = new(0xFF, 0xB0, 0x20);
    public static readonly Color ErrorColor = new(0xFF, 0x6B, 0x6B);

    public static Attribute Text { get; } = new(TextColor, Background);

    public static Attribute Dim { get; } = new(DimColor, Background);

    public static Attribute Accent { get; } = new(AccentColor, Background);

    public static Attribute Warning { get; } = new(WarningColor, Background);

    public static Attribute Error { get; } = new(ErrorColor, Background);

    private static readonly Attribute Cyan = new(new Color(0x5C, 0xD3, 0xE6), Background);
    private static readonly Attribute Magenta = new(new Color(0xF2, 0x8B, 0xD9), Background);
    private static readonly Attribute Blue = new(new Color(0x8F, 0xA8, 0xFF), Background);

    /// <summary>
    /// An agent's colour (<c>&lt;id&gt;.color</c>), the shades the window's dark theme draws, each 7:1 or
    /// better on this background; green is the accent, and white, or anything else, the text colour.
    /// </summary>
    public static Attribute Seat(string color) => color switch
    {
        "cyan" => Cyan,
        "magenta" => Magenta,
        "blue" => Blue,
        "green" => Accent,
        _ => Text,
    };

    /// <summary>Everything on the dark background in the text colour, focused or not: the screen's base.</summary>
    public static Scheme Base { get; } = Uniform(Text);

    /// <summary>For borders and titles that should recede.</summary>
    public static Scheme Faint { get; } = Uniform(Dim);

    /// <summary>For the border and title of the panel that has the person's attention.</summary>
    public static Scheme Lit { get; } = Uniform(Accent);

    /// <summary>For an approval's border and title.</summary>
    public static Scheme Alert { get; } = Uniform(Warning);

    /// <summary>A scheme with every role in one attribute, so nothing is derived into a colour DipTick did not choose.</summary>
    public static Scheme Uniform(Attribute look) => new(look)
    {
        Focus = look,
        HotNormal = look,
        HotFocus = look,
        Active = look,
        HotActive = look,
        Highlight = look,
        Editable = look,
        ReadOnly = look,
        Disabled = look,
    };

    /// <summary>The terminal's width, or a safe guess when there is no console to ask.</summary>
    public static int ScreenWidth()
    {
        try
        {
            return Math.Max(40, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 100;
        }
    }

    /// <summary>The terminal's height, or a safe guess.</summary>
    public static int ScreenHeight()
    {
        try
        {
            return Math.Max(15, Console.WindowHeight);
        }
        catch (IOException)
        {
            return 30;
        }
    }

    /// <summary>Words wrapped to <paramref name="width"/>; a line break in the text starts a new line.</summary>
    public static List<string> Wrap(string text, int width) => [.. ConsoleAsker.Wrap(text ?? string.Empty, Math.Max(10, width))];

    /// <summary>A row of one span.</summary>
    public static IReadOnlyList<Span> Row(string text, Attribute look) => [new Span(text, look)];
}
