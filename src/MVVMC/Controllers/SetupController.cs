using DipTick.Models;
using DipTick.Models.Chat;
using DipTick.Models.Harness;
using DipTick.Models.Keys;
using DipTick.ViewModels;
using DipTick.Views;

namespace DipTick.Controllers;

/// <summary>
/// <c>diptick setup [--full]</c>: setup in the plain console.
/// The chat screen runs the same flow in dialogs.
/// </summary>
internal static class SetupController
{
    public static int Run(IReadOnlyList<string> args, Cli cli)
    {
        var full = false;
        foreach (var arg in args.Skip(1))
        {
            switch (arg)
            {
                case "--full":
                    full = true;
                    break;
                case "--quick":
                    full = false;
                    break;
                default:
                    return cli.Fail($"setup does not take '{arg}'. Usage: diptick setup [--full]", Exit.Usage);
            }
        }

        var keyboard = !Console.IsInputRedirected;
        if (keyboard && !cli.Flags.Interactive)
        {
            return cli.Fail("setup asks questions, and --no-input forbids asking. Pipe the answers and pass --yes.", Exit.Usage);
        }

        if (cli.ChosenConfig is { } named && named.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return cli.Fail("setup saves settings, and --config none names no file.", Exit.Usage);
        }

        var target = cli.Config.Path ?? (cli.ChosenConfig is { } chosen ? Path.GetFullPath(chosen) : MasterConfig.UserConfigPath);

        // The same door as the chat screen: with a password set, the settings do not change without it.
        if (cli.Config.UserPasswordHash.Length > 0)
        {
            var password = ConsoleInput.ReadSecret($"Password for {cli.Who}: ", cli.Err);
            if (password is null || !PasswordHash.Verify(password, cli.Config.UserPasswordHash))
            {
                return cli.Fail("wrong password.");
            }
        }

        cli.Err.WriteLine(cli.Config.MainProvider.Length > 0
            ? SafeText.Line($"DipTick is set up: {Engines.DisplayName(cli.Config.MainProvider, cli.Config.MainEndpoint)} {cli.Config.MainModel} for {cli.Who}. Nothing changes until you save.")
            : "Welcome to DipTick. A few questions, and a model answers you.");

        var context = new SetupContext(
            cli.Config,
            target,
            cli.Flags,
            WindowsKeyStore.Instance,
            Environment.GetEnvironmentVariable,
            ModelLister.Instance,
            permissions => new Gate(permissions, cli.Flags, AuditLog.ForThisUser(), cli.Who, cli.Err, question => keyboard ? ConsoleInput.Ask(question, cli.Err) : null));
        var result = new SetupFlow(context, new ConsoleAsker(Console.In, cli.Err, keyboard)).RunWithAgents(full);
        switch (result.Outcome)
        {
            case SetupOutcome.Saved:
                cli.Note(result.Message);
                break;
            case SetupOutcome.DryRun:
                cli.Note(result.Message);
                return Exit.Ok;
            case SetupOutcome.Stopped:
                cli.Note(result.Message);
                return Exit.NoYes;
            case SetupOutcome.NoYes:
                return cli.Fail(result.Message, Exit.NoYes);
            default:
                return cli.Fail(result.Message);
        }

        if (!keyboard || Console.IsOutputRedirected)
        {
            return Exit.Ok;
        }

        cli.Err.Write("Open the chat now? [Y/n] ");
        var answer = Console.ReadLine()?.Trim().ToUpperInvariant();
        if (answer is not ("" or "Y" or "YES"))
        {
            cli.Note("'diptick' opens it whenever you like.");
            return Exit.Ok;
        }

        // The terminal screen, in this terminal. The password was asked for at the start of setup
        // (or has just been set): not asked again.
        return UiController.Run(["tui"], new Cli(cli.Flags, MasterConfig.Load(target), cli.Out, cli.Err, cli.ChosenConfig), terminal: true, passwordChecked: true);
    }

}
