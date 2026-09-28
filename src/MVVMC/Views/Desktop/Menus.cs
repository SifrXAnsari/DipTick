using Avalonia.Controls;
using Avalonia.Input;

namespace DipTick.Views.Desktop;

/// <summary>
/// The window's own right-click menus: built here rather than taken
/// from the theme, so what they offer, and when an item is on, is DipTick's to decide. The theme's
/// Copy follows a <c>CanCopy</c> that Avalonia gets wrong when a selection ends below the text's
/// last character (github.com/AvaloniaUI/Avalonia/issues/21622).
/// </summary>
internal static class Menus
{
    /// <summary>A menu item that runs <paramref name="act"/> on one click, with its key shown when it has one.</summary>
    public static MenuItem Item(string header, Action act, string? gesture = null)
    {
        ArgumentNullException.ThrowIfNull(act);
        var item = new MenuItem { Header = header };
        if (gesture is not null)
        {
            item.InputGesture = KeyGesture.Parse(gesture);
        }

        item.Click += (_, _) => act();
        return item;
    }

    /// <summary>A menu of <paramref name="items"/>, in order; null entries are left out.</summary>
    public static ContextMenu Of(params Control?[] items)
    {
        var menu = new ContextMenu();
        foreach (var item in items)
        {
            if (item is not null)
            {
                menu.Items.Add(item);
            }
        }

        return menu;
    }

    public static Separator Line() => new();
}
