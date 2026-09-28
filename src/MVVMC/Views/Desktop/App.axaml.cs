using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using DipTick.ViewModels;

namespace DipTick.Views.Desktop;

/// <summary>
/// The window's application. It opens the windows of the <see cref="AppModel"/> the command line
/// built, one for each the app opens, and ends when the last closes: nothing outlives
/// <c>diptick ui</c>. When no terminal started DipTick and a password is set, the password window
/// comes first.
/// </summary>
internal sealed partial class App : Application
{
    /// <summary>What the windows show, handed over by <see cref="Desktop.Run"/> before Avalonia starts.</summary>
    public static AppModel? Tabs { get; set; }

    /// <summary>How the first tab starts, when the last run's windows do not come back.</summary>
    public static TabStart? First { get; set; }

    /// <summary>Whether the windows and tabs left open at the last quit come back.</summary>
    public static bool Restore { get; set; }

    /// <summary>The theme to open in, from Settings, Appearance.</summary>
    public static string Theme { get; set; } = "dark";

    /// <summary>Who the password window asks.</summary>
    public static string Who { get; set; } = Environment.UserName;

    /// <summary>The password hash to ask for before the chat opens, or null to open it at once.</summary>
    public static string? PasswordHash { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Tabs is { } app && First is { } first)
        {
            // The theme before the first window, so the password window wears it too.
            RequestedThemeVariant = Theme switch
            {
                "light" => ThemeVariant.Light,
                "system" => ThemeVariant.Default,
                _ => ThemeVariant.Dark,
            };

            // Every window the app opens gets its own; the run ends when the last one closes.
            void Open()
            {
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                app.WindowOpened += window => new MainWindow(app, window).Show();
                app.Start(first, Restore);
                desktop.MainWindow = desktop.Windows.OfType<MainWindow>().FirstOrDefault();
            }

            if (PasswordHash is { Length: > 0 } hash)
            {
                // While the password is asked, only DipTick ends the run, and it says how it ended.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var decided = false;
                var gate = new PasswordWindow(Who, hash);
                gate.Passed += () =>
                {
                    // The chat first, then the gate goes: the chat's windows end the run from here on.
                    decided = true;
                    Open();
                    gate.Close();
                };
                gate.Failed += () =>
                {
                    decided = true;
                    desktop.Shutdown(Desktop.WrongPassword);
                };
                gate.Closed += (_, _) =>
                {
                    if (!decided)
                    {
                        desktop.Shutdown(Desktop.Left);
                    }
                };
                desktop.MainWindow = gate;
            }
            else
            {
                Open();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Starts the windows and holds the terminal until the last closes.</summary>
internal static class Desktop
{
    /// <summary>The window's exit when the password was wrong every time it was tried.</summary>
    public const int WrongPassword = 3;

    /// <summary>The window's exit when the person left at the password, and nothing opened.</summary>
    public const int Left = 4;

    /// <param name="app">DipTick's windows and tabs.</param>
    /// <param name="first">How the first tab starts, when the last run's windows do not come back.</param>
    /// <param name="restore">Whether the windows and tabs left open at the last quit come back.</param>
    /// <param name="look">Settings, Appearance, Theme, and who the password window asks.</param>
    /// <param name="passwordHash">A password to ask for in a window before the chat opens, or null.</param>
    public static int Run(AppModel app, TabStart first, bool restore, (string Theme, string Who) look, string? passwordHash)
    {
        App.Tabs = app;
        App.First = first;
        App.Restore = restore;
        App.Theme = look.Theme;
        App.Who = look.Who;
        App.PasswordHash = passwordHash;

        // The backends are named rather than detected: DipTick is Windows-only, and
        // UsePlatformDetect lives in Avalonia.Desktop, which brings the X11 backend and its advisory.
        // Avalonia 12 split text shaping out of Skia, so HarfBuzz is named too.
        return AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .StartWithClassicDesktopLifetime([]);
    }
}
