using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DipTick.Views.Desktop;

/// <summary>
/// The window's brushes, read from the theme in force: one place to ask,
/// so code that builds controls uses the same colours the styles do, in dark and in light.
/// </summary>
internal static class Palette
{
    public static IBrush Ground => Brush("GroundBrush");

    public static IBrush Panel => Brush("PanelBrush");

    public static IBrush Raised => Brush("RaisedBrush");

    public static IBrush Hairline => Brush("HairlineBrush");

    public static IBrush Ink => Brush("InkBrush");

    public static IBrush Dim => Brush("DimBrush");

    public static IBrush Accent => Brush("AccentBrush");

    public static IBrush AccentSoft => Brush("AccentSoftBrush");

    public static IBrush Warning => Brush("WarningBrush");

    public static IBrush Error => Brush("ErrorBrush");

    public static IBrush Plasma => Brush("PlasmaBrush");

    public static IBrush Code => Brush("CodeBrush");

    public static IBrush Selection => Brush("SelectionBrush");

    public static FontFamily Mono => Resource("MonoFont") as FontFamily ?? new FontFamily("Consolas");

    public static Markdown.Look Look => new(Ink, Dim, Code, Selection, Mono, Accent);

    /// <summary>
    /// An agent's colour (<c>&lt;id&gt;.color</c>): cyan, magenta and blue have their own shade in each
    /// theme; green is the accent, which the model that answers you wears; white, or anything else, is the ink.
    /// </summary>
    public static IBrush Seat(string color) => color switch
    {
        "cyan" => Brush("SeatCyanBrush"),
        "magenta" => Brush("SeatMagentaBrush"),
        "blue" => Brush("SeatBlueBrush"),
        "green" => Accent,
        _ => Ink,
    };

    private static IBrush Brush(string key) => Resource(key) as IBrush ?? Brushes.Magenta;

    /// <summary>A resource of the theme in force: the brushes sit in theme dictionaries, so the variant must be named.</summary>
    private static object? Resource(string key)
    {
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) ? value : null;
    }
}
