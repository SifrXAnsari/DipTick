using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.ViewModels;
using DipTick.Views;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick tui [--session &lt;id|last|new&gt;]</c> (and <c>diptick</c> alone at a keyboard): the
/// terminal screen. <c>diptick window [--session &lt;id|last|new&gt;] [--private] [--wait]</c>, also
/// <c>diptick ui</c>: the windows. At a terminal the window starts as a program of its own and the
/// command returns at once (<see cref="WindowStart"/>); with <c>--wait</c>, or started with no
/// terminal, the windows run in this process until the last closes, and the windows and tabs left
/// open at the last quit come back unless a session or a private tab is asked for. With no model set
/// up, setup opens inside either screen, once: after that, from Settings, You, Run setup again
/// (<see cref="UiState"/>). <c>--session</c> opens a kept session; the default is a new one.
/// </summary>
internal static class UiController
{
    public static int Run(IReadOnlyList<string> args, Cli cli, bool terminal) => Run(args, cli, terminal, passwordChecked: false);

    /// <param name="passwordChecked">True when the password was asked for just before (by setup), so it is not asked twice.</param>
    public static int Run(IReadOnlyList<string> args, Cli cli, bool terminal, bool passwordChecked)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(cli);
        var verb = terminal ? "tui" : args.Count > 0 ? args[0] : "window";

        // The terminal's screen needs a console. Started from Explorer it makes its own (Program),
        // so this is only Windows refusing to make one.
        if (terminal && cli.Windowed)
        {
            return cli.Fail("'tui' is the screen inside a terminal, and Windows gave it no console to draw in. Run it from Windows Terminal, or open the window: diptick window.");
        }

        // Started from Explorer, the window is interactive whatever standard input says: there is none.
        if (!cli.Flags.Interactive && !cli.Windowed)
        {
            return cli.Fail($"'{verb}' is interactive by nature; it cannot run with --no-interactive.", Exit.Usage);
        }

        if (terminal && (Console.IsInputRedirected || Console.IsOutputRedirected))
        {
            return cli.Fail("'tui' needs a terminal on both ends. For scripts, use the other verbs.");
        }

        string? session = null;
        var isPrivate = false;
        var wait = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--private")
            {
                isPrivate = true;
            }
            else if (!terminal && args[i] == "--wait")
            {
                wait = true;
            }
            else if (args[i] == "--session" && i + 1 < args.Count)
            {
                session = args[++i];
            }
            else if (args[i].StartsWith("--session=", StringComparison.Ordinal))
            {
                session = args[i]["--session=".Length..];
            }
            else
            {
                return cli.Fail($"usage: diptick {verb} [--session <id|last|new>] [--private]{(terminal ? string.Empty : " [--wait]")}", Exit.Usage);
            }
        }

        // At a terminal the window opens as a program of its own, and the terminal is free at once;
        // that program asks for the password, in the window. With --wait, or started with no
        // terminal, the window runs here until it closes.
        if (!terminal && !wait && !cli.Windowed)
        {
            if (WindowStart.Start(cli.Raw.Count > 0 ? cli.Raw : args) is { } why)
            {
                return cli.Fail(why);
            }

            cli.Note("The window is opening; this terminal is free.");
            return Exit.Ok;
        }

        var config = cli.Config;

        // With a password set, nobody gets the screen without it. Checked here, before anything
        // is drawn, so the terminal is still an ordinary terminal while the password is typed.
        // Started from Explorer there is no terminal to type it in: the window asks for it first.
        var askInWindow = false;
        if (!passwordChecked && config.UserPasswordHash.Length > 0)
        {
            if (cli.Windowed && !terminal)
            {
                askInWindow = true;
            }
            else
            {
                var password = ConsoleInput.ReadSecret($"Password for {cli.Who}: ", cli.Err);
                if (password is null || !PasswordHash.Verify(password, config.UserPasswordHash))
                {
                    return cli.Fail("wrong password.");
                }
            }
        }

        // Where setup and /settings save: the file in use, else the one --config names, else the
        // per-user file. --config none names no file, so nothing can be saved.
        var target = cli.ChosenConfig is { } chosen && chosen.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : config.Path ?? (cli.ChosenConfig is { } named ? Path.GetFullPath(named) : MasterConfig.UserConfigPath);
        // The screen runs the verbs for everything it keeps (chat say, list, show, title, export),
        // so it holds no store of its own. What it remembers for the person is not a setting.
        var remembered = UiState.Load(Models.Harness.AuditLog.DataFolder());

        // One book of unsent drafts for every tab, so a chat's draft follows it wherever it opens.
        var drafts = new DraftBook(remembered.Drafts);

        // Each tab's chat, made alike: the first may open setup, once; a private one says who sees what.
        ChatViewModel Make(TabStart start)
        {
            var model = new ChatViewModel(config, cli.Flags, Engines.Create(config))
            {
                SettingsPath = target,
                SetupOnStart = start.First && !config.AnyAgent && target is not null && !remembered.SetupShown,
                OpenOnStart = start.Session,
                UiState = remembered,
                Drafts = drafts,
            };
            if (start.Private)
            {
                model.NewChat(isPrivate: true);
            }

            return model;
        }

        var first = new TabStart(session is null || session.Equals("new", StringComparison.OrdinalIgnoreCase) ? null : session, isPrivate, First: true);

        // Ctrl+C in the terminal that started it is a quit like Esc three times: everything stops,
        // the local models are let go, and the screen closes.
        cli.StopsCleanly();
        if (terminal)
        {
            // One chat at a time: it takes the last run's new chat's draft, unless it is private.
            var model = Make(first);
            if (!model.IsPrivate)
            {
                model.SeedDraft(null, drafts.TakeNewChat());
            }

            // /window and F2 open the window as a program of its own, on the chat on screen when it is kept.
            model.OpenWindow = kept => WindowStart.Start(WindowArguments(cli, kept));
            using var onBreak = cli.Stop.Register(() => _ = Task.Run(() => model.QuitAsync(ask: false)));
            ChatView.Run(model);
            return Exit.Ok;
        }

        var app = new AppModel(Make, remembered);
        using var onBreakWindow = cli.Stop.Register(() => _ = Task.Run(() => app.QuitAsync(ask: false)));

        // A console made for DipTick alone (older Windows, started from Explorer) closes now, before the window opens.
        if (cli.Windowed)
        {
            ConsoleHost.Detach();
        }

        return Views.Desktop.Desktop.Run(app, first, restore: session is null && !isPrivate, (config.AppearanceTheme, cli.Who), askInWindow ? config.UserPasswordHash : null) switch
        {
            0 => Exit.Ok,
            Views.Desktop.Desktop.WrongPassword => cli.Fail($"wrong password, {Views.Desktop.PasswordWindow.Tries.ToString(System.Globalization.CultureInfo.InvariantCulture)} times; nothing opened."),

            // The person left at the password: their choice, and nothing to tell them.
            _ => Exit.Failed,
        };
    }

    /// <summary>
    /// What the terminal screen starts the window with: the settings file this run uses, and the
    /// chat on screen when it is kept.
    /// </summary>
    internal static List<string> WindowArguments(Cli cli, string? session)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var arguments = new List<string>();
        if (cli.ChosenConfig is { } config)
        {
            arguments.Add("--config");
            arguments.Add(config);
        }

        arguments.Add("window");
        if (session is not null)
        {
            arguments.Add("--session");
            arguments.Add(session);
        }

        return arguments;
    }
}
