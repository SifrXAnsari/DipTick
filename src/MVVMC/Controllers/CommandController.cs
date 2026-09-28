using System.Reflection;
using DipTick.Models;

namespace DipTick.Controllers;

/// <summary>
/// The command line is the authority: every verb lives here first, and the full-screen
/// interface calls these rather than doing more than they do.
/// </summary>
internal static class CommandController
{
    public static string VersionText =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The verb the line names, or null when it names none; nothing is checked or run.</summary>
    public static string? VerbOf(IReadOnlyList<string> raw)
    {
        var (_, args, _) = RunFlags.Parse(raw);
        return args.Count == 0 ? null : args[0];
    }

    /// <summary>
    /// The command line in a terminal: Ctrl+C is <paramref name="consoleBreak"/>'s, and a verb that
    /// stops cleanly stops at its token.
    /// </summary>
    public static int Run(IReadOnlyList<string> raw, TextWriter output, TextWriter error, ConsoleBreak consoleBreak)
    {
        ArgumentNullException.ThrowIfNull(consoleBreak);
        return Run(raw, output, error, ask: null, windowed: false, consoleBreak, consoleBreak.Token);
    }

    /// <summary>
    /// The command line when no terminal started DipTick (<see cref="ConsoleHost"/>): diptick alone
    /// opens the window, and the window may open, whatever the keyboard's standard input says.
    /// </summary>
    public static int Run(IReadOnlyList<string> raw, TextWriter output, TextWriter error, bool windowed) =>
        Run(raw, output, error, ask: null, windowed, consoleBreak: null, CancellationToken.None);

    /// <summary>
    /// A verb as the command line runs it, for a screen that runs it in its own process: the
    /// verb's questions go to <paramref name="ask"/> (the screen's prompt), its approvals to
    /// <paramref name="approve"/>, which also offers "Allow for this chat", and <paramref name="stop"/>
    /// ends its work (the screen's Esc). Nothing else differs: the same verb, checks and records.
    /// </summary>
    public static int Run(IReadOnlyList<string> raw, TextWriter output, TextWriter error, Func<string, bool?>? ask, Func<string, Models.Harness.Approval?>? approve, CancellationToken stop) =>
        Run(raw, output, error, ask, windowed: false, consoleBreak: null, stop, approve);

    private static int Run(IReadOnlyList<string> raw, TextWriter output, TextWriter error, Func<string, bool?>? ask, bool windowed, ConsoleBreak? consoleBreak, CancellationToken stop, Func<string, Models.Harness.Approval?>? approve = null)
    {
        var (typed, args, errors) = RunFlags.Parse(raw);

        // --help and --version are honoured anywhere on the line, and everything else is ignored
        // with them (GNU Coding Standards, 4.8).
        if (typed.Version)
        {
            output.WriteLine($"diptick {VersionText}");
            return Exit.Ok;
        }

        if (typed.Help || (args.Count > 0 && args[0] == "help"))
        {
            var topic = args.Count == 0 ? null : args[0] == "help" ? (args.Count > 1 ? args[1] : null) : args[0];
            return HelpText.Write(topic, output, error);
        }

        if (errors.Count > 0)
        {
            foreach (var e in errors)
            {
                error.WriteLine($"diptick: {e}");
            }

            return Exit.Usage;
        }

        var config = MasterConfig.Load(MasterConfig.Find(typed.Config));

        // The settings in use for this run decide whether a Google Compute Engine machine's own account may sign in.
        Models.Providers.GoogleSignIn.UseMachineAccount = config.GoogleMachineAccount;
        var (flags, bad) = RunFlags.Resolve(typed, config);
        if (bad.Count > 0)
        {
            foreach (var e in bad)
            {
                error.WriteLine($"diptick: {e}");
            }

            return Exit.Usage;
        }

        if (args.Count == 0)
        {
            // Alone at a keyboard diptick opens the terminal screen, and setup the first time.
            // Started with no console, where Windows would make none for it (Program), the window.
            // Anywhere else it prints help, so a script never opens a screen.
            if (!windowed && (!flags.Interactive || Console.IsInputRedirected || Console.IsOutputRedirected))
            {
                return HelpText.Write(null, output, error);
            }

            args = windowed ? ["window"] : ["tui"];
        }

        var chosen = typed.Config;
        if (chosen is null && Environment.GetEnvironmentVariable(MasterConfig.PathVariable) is { } named && !string.IsNullOrWhiteSpace(named))
        {
            chosen = named;
        }

        var cli = new Cli(flags, config, output, error, chosen) { LiteralFrom = typed.LiteralFrom, Raw = [.. raw], Ask = ask, Approve = approve, Stop = stop, Windowed = windowed, Break = consoleBreak };
        try
        {
            return Dispatch(args, cli);
        }
        catch (InvalidDataException ex)
        {
            return cli.Fail(ex.Message);
        }
    }

    private static int Dispatch(List<string> args, Cli cli) =>
        args[0] switch
        {
            "version" => Version(cli),
            "init" => InitController.Run(args, cli),
            "config" => ConfigController.Run(args, cli),
            "login" => ConfigController.Login(cli),
            "whoami" => ConfigController.WhoAmI(cli),
            "policy" => PolicyController.Policy(args, cli),
            "audit" => PolicyController.Audit(args, cli),
            "doctor" => DoctorController.Run(cli),
            "docs" => DocsController.Run(args, cli),
            "data" => DataController.Run(args, cli),
            "completion" => CompletionController.Run(args, cli),
            "window" or "ui" => UiController.Run(args, cli, terminal: false),
            "tui" => UiController.Run(args, cli, terminal: true),
            "ask" => AskController.Run(args, cli),
            "chat" => ChatController.Run(args, cli),
            "acts" => ActsController.Run(args, cli),
            "seats" => SeatsController.Run(args, cli),
            "key" or "keys" => KeyController.Run(args, cli),
            "models" => ModelsController.Run(args, cli),
            "providers" => ProvidersController.Run(args, cli),
            "tools" => ToolsController.Run(args, cli),
            "research" => ResearchController.Run(args, cli),
            "image" => MediaController.Image(args, cli),
            "video" => MediaController.Video(args, cli),
            "transcribe" => MediaController.Transcribe(args, cli),
            "connectors" => ConnectorsController.Run(args, cli),
            "skills" => SkillsController.Run(args, cli),
            "run" => RunController.Run(args, cli),
            "settings" => SettingsController.Run(args, cli),
            "setup" => SetupController.Run(args, cli),
            _ => cli.Fail($"unknown verb '{args[0]}'. Try 'diptick help'.", Exit.Usage),
        };

    private static int Version(Cli cli)
    {
        if (!cli.WriteJson(new { name = "diptick", version = VersionText }))
        {
            cli.Data($"diptick {VersionText}");
        }

        return Exit.Ok;
    }
}
