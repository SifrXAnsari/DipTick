using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DipTick.Models;

namespace DipTick.Views.Desktop;

/// <summary>
/// The password, asked before the window opens when no terminal started DipTick: from
/// Explorer there is no console to type it in. The same check as the terminal's: nobody gets the
/// screen without it. Three tries, each checked off the window's thread (Argon2id takes its time);
/// Esc or ✕ leaves, and nothing opens.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Every control made here is added to the window, which owns it and disposes it with itself; CA2000 does not follow ownership through Children.Add.")]
internal sealed class PasswordWindow : Window
{
    /// <summary>How many times the password may be typed before DipTick gives up.</summary>
    public const int Tries = 3;

    private readonly string _hash;
    private readonly TextBox _field = new() { PasswordChar = '●', RevealPassword = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _open = new() { Content = "Open", Classes = { "primary" }, IsDefault = true };
    private readonly TextBlock _said = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MinHeight = 18 };
    private int _tried;
    private bool _checking;

    public PasswordWindow(string who, string hash)
    {
        _hash = hash;
        Title = "DipTick";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Ground;

        var column = new StackPanel { Spacing = 12, Margin = new Thickness(24, 20) };
        column.Children.Add(new TextBlock { Text = "DipTick", FontSize = 19, FontWeight = FontWeight.SemiBold });
        column.Children.Add(new TextBlock { Text = $"Password for {who}", Foreground = Palette.Dim });
        column.Children.Add(_field);
        column.Children.Add(_said);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var leave = new Button { Content = "Leave", IsCancel = true };
        leave.Click += (_, _) => Close();
        buttons.Children.Add(leave);
        buttons.Children.Add(_open);
        column.Children.Add(buttons);
        Content = column;

        _open.Click += (_, _) => Check();
        Opened += (_, _) => _field.Focus();
    }

    /// <summary>Raised on the window's thread when the password is right: the chat opens.</summary>
    public event Action? Passed;

    /// <summary>Raised on the window's thread after the last wrong try: nothing opens.</summary>
    public event Action? Failed;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Checks what was typed, off the window's thread; the field is cleared whatever the answer.</summary>
    private void Check()
    {
        if (_checking)
        {
            return;
        }

        var typed = _field.Text ?? string.Empty;
        _field.Text = string.Empty;
        if (typed.Length == 0)
        {
            _said.Text = "Type the password first.";
            _said.Foreground = Palette.Warning;
            return;
        }

        _checking = true;
        _open.IsEnabled = false;
        _said.Text = "Checking…";
        _said.Foreground = Palette.Dim;
        _ = Task.Run(() => PasswordHash.Verify(typed, _hash)).ContinueWith(
            done => Dispatcher.UIThread.Post(() => Checked(done.IsCompletedSuccessfully && done.Result)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void Checked(bool right)
    {
        _checking = false;
        _open.IsEnabled = true;
        if (right)
        {
            Passed?.Invoke();
            return;
        }

        _tried++;
        if (_tried >= Tries)
        {
            Failed?.Invoke();
            return;
        }

        var left = Tries - _tried;
        _said.Text = left == 1 ? "That is not the password. One try left." : $"That is not the password. {left.ToString(CultureInfo.InvariantCulture)} tries left.";
        _said.Foreground = Palette.Error;
        _field.Focus();
    }
}
