using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DipTick.Views.Desktop;

/// <summary>What one F-key does on a screen: its label on the bar, what runs, and a line saying more.</summary>
internal sealed record FKey(string Label, Action Run, string Tip);

/// <summary>
/// The F-key bar across the top of the window, above everything: F1 to F10, each a key and a
/// button, and what each does on the screen on show. Midnight Commander's key bar is the model, its
/// labels clickable and its set changed with the screen. A key with nothing to do on a screen shows
/// its number, dimmed, with no label; nothing moves, so a key's place is learned once. The right end
/// says what runs across DipTick.
/// </summary>
internal sealed class FKeyBar : Grid
{
    public const int Count = 10;

    private readonly Button[] _buttons = new Button[Count];
    private readonly TextBlock[] _labels = new TextBlock[Count];
    private readonly FKey?[] _set = new FKey?[Count];
    private readonly StackPanel _keys = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _warning = new() { IsVisible = false, Classes = { "warning" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Margin = new Thickness(10, 0) };

    public FKeyBar()
    {
        // The keys first and whole, where they always are; what runs takes the room that is left.
        ColumnDefinitions = new ColumnDefinitions("Auto,*");
        for (var i = 0; i < Count; i++)
        {
            var index = i;

            // Colours from the styles (TextBlock, TextBlock.accent), which follow the theme as it changes.
            var number = new TextBlock { Text = $"F{(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}", Classes = { "accent" }, FontFamily = Palette.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            face.Children.Add(number);
            face.Children.Add(label);
            var button = new Button { Classes = { "fkey" }, Content = face };
            button.Click += (_, _) => Press(index + 1);
            _buttons[i] = button;
            _labels[i] = label;
            _keys.Children.Add(button);
        }

        Children.Add(_keys);
        Children.Add(_warning);
        SetColumn(Activity, 1);
        Children.Add(Activity);
    }

    /// <summary>
    /// The right end of the bar: what runs across DipTick and what waits for the person, the most
    /// pressing last, so that when room is short what is dropped is what matters least.
    /// </summary>
    public FitRow Activity { get; } = new() { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };

    /// <summary>The keys for the screen on show. A null is a key with nothing to do here: dimmed, never hidden.</summary>
    public void Show(IReadOnlyList<FKey?> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        for (var i = 0; i < Count; i++)
        {
            var key = i < keys.Count ? keys[i] : null;
            _set[i] = key;
            _labels[i].Text = key?.Label ?? string.Empty;
            _labels[i].IsVisible = key is not null;
            _buttons[i].IsEnabled = key is not null;
            var number = $"F{(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            ToolTip.SetTip(_buttons[i], key is null ? $"{number}: nothing to do on this screen" : $"{number}: {key.Tip}");
        }
    }

    /// <summary>Runs F<paramref name="number"/>'s job on this screen; false when it has none here.</summary>
    public bool Press(int number)
    {
        if (number < 1 || number > Count || _set[number - 1] is not { } key)
        {
            return false;
        }

        key.Run();
        return true;
    }

    /// <summary>A line in place of the keys (Esc's warning before it quits), or null to bring the keys back.</summary>
    public void Warn(string? text)
    {
        _warning.Text = text ?? string.Empty;
        _warning.IsVisible = text is not null;
        _keys.IsVisible = text is null;
    }
}
